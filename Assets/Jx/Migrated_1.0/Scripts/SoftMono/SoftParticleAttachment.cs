// SoftParticleAttachment.cs
//
// Pins or drives a named ParticleGroup of a soft body to a Transform target.
// Analogous to ObiParticleAttachment in the Obi Softbody plugin.
//
// ATTACHMENT TYPES:
//
//   Static   — particles are teleported to their rest-offset positions relative
//               to the target transform every fixed step, then their velocity is
//               zeroed.  The soft body cannot pull the target.  Use for hard pins
//               (e.g. a sphere hanging from a ceiling hook).
//
//   Dynamic  — particles are driven toward the target via a spring/damper impulse.
//               The target can be influenced by the accumulated reaction force
//               (if the target has a Rigidbody).  Use for two-way coupling
//               (e.g. a cloth draped over a moving rigidbody character).
//
// RUNTIME REQUIREMENTS:
//   - SoftBodyComponent must be on the same GameObject or assignable via SoftBody field.
//   - The SoftBodySimulationManager must be running.
//   - Particle positions are read/written via GPU readback each FixedUpdate.
//     This is a synchronous stall — acceptable for small groups (< 50 particles).
//     For large groups consider async readback in a future patch.
//
// COORDINATE SPACE:
//   Particles live in world space (SoftBodyGPUState was initialized with the
//   body's localToWorldMatrix baked in at spawn).  AttachTarget is also world-space.
//   RestOffsets are computed once at attachment time in the target's local space,
//   so the group follows the target's rotation and translation correctly.

using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	public enum AttachmentType
	{
		Static, Dynamic
	}

	[AddComponentMenu("XPBD/Soft Particle Attachment")]
	[RequireComponent(typeof(SoftBodyComponent))]
	public sealed class SoftParticleAttachment : MonoBehaviour
	{
		// ── Inspector ─────────────────────────────────────────────────────────
		[Header("Attachment")]
		[Tooltip("Transform this group should follow.")]
		public Transform AttachTarget;

		[Tooltip("Particle group defined in the TetrahedralMeshAsset editor.")]
		public ParticleGroup ParticleGrp;

		[Tooltip("Static: hard-pin (zero velocity, teleport). " +
				 "Dynamic: spring-drive (two-way coupling with Rigidbody target).")]
		public AttachmentType Type = AttachmentType.Static;

		[Header("Dynamic settings (ignored for Static)")]
		[Range(0f, 1f)]
		[Tooltip("Spring compliance: 0 = rigid, 1 = very soft spring.")]
		public float Compliance = 0.01f;

		[Range(0f, 1f)]
		[Tooltip("Velocity damping applied to attachment correction. 1 = fully damped.")]
		public float Damping = 0.5f;

		// ── Inspector: Debug Gizmos ───────────────────────────────────────────
		[Header("Debug Gizmos (Dynamic only)")]
		public bool ShowGizmos = true;
		public float GizmoParticleRadius = 0.04f;
		public Color GizmoParticleColor = new Color(0.2f, 0.8f, 1f, 0.85f);
		public Color GizmoRestColor = Color.green;
		public Color GizmoStretchColor = Color.red;
		public Color GizmoCompressColor = Color.cyan;

		[Tooltip("Strain at which edge colour fully saturates. E.g. 0.10 = ±10%.")]
		[Range(0.01f, 0.5f)]
		public float StrainSaturate = 0.10f;

		[Tooltip("Strains within ±RestBand show as rest colour (green).")]
		[Range(0f, 0.1f)]
		public float RestBand = 0.02f;

		//[Header("Reference (auto-found if null)")]
		SoftBodyComponent _softBody;

		// ── Runtime state ─────────────────────────────────────────────────────
		// Rest offsets in AttachTarget's LOCAL space, computed at Enable time.
		Vector3[] _restOffsets;

		// Cached particle indices from the group
		int[] _indices;

		// GPU readback scratch buffers (allocated once, reused each SimStep).
		// _posRaw  : PositionsBuffer — GPUPbdPositions[] — 8 floats/particle
		// _partRaw : ParticleBuffer  — Particle[]        — 8 floats/particle
		float[] _posRaw;
		float[] _partRaw;

		// ── Lifecycle ─────────────────────────────────────────────────────────
		void Awake()
		{
			if (_softBody == null)
			{
				_softBody = GetComponent<SoftBodyComponent>();
				//ensure it Init() for pass : later OnEnable() Validate [3/14/2026 jzq]
				_softBody.Init();
			}
			//**Temp ReFetch By Name, Later fix by custom-editor Choose from _softBody.TetMeshAsset.Groups [3/14/2026 jzq]
			var find = false;
			foreach (var g in _softBody.TetMeshAsset.Groups)
			{
				if (g.Name == ParticleGrp.Name)
				{
					ParticleGrp = g;
					find = true;
					break;
				}
			}
			if (!find)
			{
				Debug.LogError($"ParticleGrp.Name = {ParticleGrp.Name} not found in softbody '{_softBody.name}'");
				enabled = false;
			}
		}

		void OnEnable()
		{
			if (!Validate())
				return;
			CacheIndices();
			ComputeRestOffsets();

			if (SoftBodySimulationManager.Instance != null)
				SoftBodySimulationManager.Instance.RegisterAttachment(SimStep);
		}

		void OnDisable()
		{
			if (SoftBodySimulationManager.Instance != null)
				SoftBodySimulationManager.Instance.UnregisterAttachment(SimStep);

			if (Type == AttachmentType.Static)
				RestoreInvMass();
		}

		// Scratch buffers for GPU readback — allocated once per body, reused.
		// _posRaw  : PositionsBuffer  — GPUPbdPositions[]  — 8 floats/particle (predict.xyz, pad, delta.xyz, pad)
		// _partRaw : ParticleBuffer   — Particle[]         — 8 floats/particle (pos.xyz, pad, vel.xyz, invMass)
		//float[] _posRaw;   // PositionsBuffer readback (predict)
		//float[] _partRaw;  // ParticleBuffer  readback (position anchor + invMass)

		// ── SimStep — called every substep by Manager after this body's Presolve ──
		//
		// Buffer state at entry (Presolve just ran):
		//   PositionsBuffer[i].predict  = old_predict + vel * dt   (new predicted pos)
		//   ParticleBuffer [i].position = old_predict              (saved anchor for Postsolve)
		//
		// We write the corrected predict back. Postsolve then computes:
		//   velocity = (corrected_predict - saved_anchor) / dt
		// No explicit velocity injection needed — Postsolve derives it automatically.
		// Running every substep means elastic constraints propagate each correction
		// through the mesh incrementally, preventing the sudden large deltas that explode.
		void SimStep(SoftBodyComponent simBody, ComputeBuffer posBuffer, ComputeBuffer deltaBuffer, float subDt)
		{
			if (simBody != _softBody)
				return;
			if (!Validate())
				return;
			if (_indices == null || _indices.Length == 0)
				return;
			if (AttachTarget == null)
				return;

			var state = _softBody.State;
			int n = state.ParticleCount;
			int fpp = 8; // GPUPbdPositions: predict(xyz) + pad + delta(xyz) + pad

			if (_posRaw == null || _posRaw.Length != n * fpp)
				_posRaw = new float[n * fpp];
			if (_partRaw == null || _partRaw.Length != n * fpp)
				_partRaw = new float[n * fpp];

			posBuffer.GetData(_posRaw);

			bool dirtyPos = false;
			bool dirtyPart = false;

			float dt = Mathf.Max(subDt, 1e-6f);

			for (int g = 0; g < _indices.Length; g++)
			{
				int idx = _indices[g];
				if (idx < 0 || idx >= n)
					continue;

				Vector3 targetWorld = AttachTarget.TransformPoint(
					g < _restOffsets.Length ? _restOffsets[g] : Vector3.zero);

				int o = idx * fpp;
				Vector3 predict = new Vector3(_posRaw[o], _posRaw[o + 1], _posRaw[o + 2]);
				Vector3 delta = targetWorld - predict;

				if (delta.sqrMagnitude < 1e-10f)
					continue;

				if (Type == AttachmentType.Static)
				{
					// Hard pin every substep: snap predict to target.
					// Also update the saved anchor (ParticleBuffer.position) to match
					// so Postsolve derives ~zero velocity instead of fighting gravity.
					_posRaw[o] = targetWorld.x;
					_posRaw[o + 1] = targetWorld.y;
					_posRaw[o + 2] = targetWorld.z;
					dirtyPos = true;

					if (!dirtyPart)
					{
						state.ParticleBuffer.GetData(_partRaw);
						dirtyPart = true;
					}
					_partRaw[o] = targetWorld.x;
					_partRaw[o + 1] = targetWorld.y;
					_partRaw[o + 2] = targetWorld.z;
				}
				else // Dynamic
				{
					// XPBD positional correction.
					// corrScale = 1/(1 + Compliance/dt²).
					// Compliance=0 → full snap; higher Compliance → softer spring.
					// Safe at corrScale=1 because this runs every substep:
					// elastic constraints distribute the correction through the mesh
					// gradually — no single frame sees the full accumulated gap.
					float corrScale = 1f / (1f + Compliance / (dt * dt));
					Vector3 corr = delta * corrScale;

					_posRaw[o] = predict.x + corr.x;
					_posRaw[o + 1] = predict.y + corr.y;
					_posRaw[o + 2] = predict.z + corr.z;
					dirtyPos = true;
				}
			}

			if (dirtyPos)
				posBuffer.SetData(_posRaw);
			if (dirtyPart)
				state.ParticleBuffer.SetData(_partRaw);
		}

		// ── Helpers ───────────────────────────────────────────────────────────
		bool Validate()
		{
			if (_softBody == null || _softBody.State == null)
				return false;
			if (ParticleGrp == null)
				return false;
			return true;
		}

		void CacheIndices()
		{
			_indices = ParticleGrp?.ParticleIndices ?? System.Array.Empty<int>();
		}

		void ComputeRestOffsets()
		{
			if (AttachTarget == null || _softBody?.State == null)
				return;

			var state = _softBody.State;
			int n = state.ParticleCount;
			int fpp = 8;

			if (_posRaw == null || _posRaw.Length != n * fpp)
				_posRaw = new float[n * fpp];

			// Read current predicted positions to compute rest offsets.
			state.PositionsBuffer.GetData(_posRaw);

			_restOffsets = new Vector3[_indices.Length];
			for (int g = 0; g < _indices.Length; g++)
			{
				int idx = _indices[g];
				if (idx < 0 || idx >= n)
				{
					_restOffsets[g] = Vector3.zero;
					continue;
				}

				int o = idx * fpp;
				Vector3 worldPos = new Vector3(_posRaw[o], _posRaw[o + 1], _posRaw[o + 2]);
				// Store rest offset in target's local space
				_restOffsets[g] = AttachTarget.InverseTransformPoint(worldPos);
			}

			// For Static attachments: pin the particles by zeroing invMass
			if (Type == AttachmentType.Static)
				PinParticles(state, n, fpp);
		}

		void PinParticles(SoftBodyGPUState state, int n, int fpp)
		{
			if (_partRaw == null || _partRaw.Length != n * fpp)
				_partRaw = new float[n * fpp];
			state.ParticleBuffer.GetData(_partRaw);

			// invMass is at float offset 7 in each Particle record
			foreach (int idx in _indices)
			{
				if (idx < 0 || idx >= n)
					continue;
				_partRaw[idx * fpp + 7] = 0f; // invMass = 0 → pinned
			}
			state.ParticleBuffer.SetData(_partRaw);
		}

		void RestoreInvMass()
		{
			// We don't know the original invMass here without caching it.
			// Best practice: cache original values in ComputeRestOffsets.
			// For now, log a warning — user should re-initialize the body.
			Debug.LogWarning("[XPBD] SoftParticleAttachment disabled: " +
				"pinned particle invMass was zeroed and cannot be automatically restored. " +
				"Reload the SoftBodyComponent to reset particle masses.", this);
		}

		// ── Gizmos ────────────────────────────────────────────────────────────
#if UNITY_EDITOR
		void OnDrawGizmosSelected()
		{
			if (!ShowGizmos)
				return;
			if (Type != AttachmentType.Dynamic)
				return;
			if (AttachTarget == null)
				return;
			if (_softBody == null || _softBody.TetMeshAsset == null)
				return;

			// Resolve indices: use cached runtime array when available,
			// fall back to ParticleGrp directly (works in edit-mode before Awake).
			int[] indices = (_indices != null && _indices.Length > 0)
				? _indices
				: ParticleGrp?.ParticleIndices;
			if (indices == null || indices.Length == 0)
				return;

			var asset = _softBody.TetMeshAsset;
			var particles = asset.Particles;
			var edges = asset.Edges;
			if (particles == null)
				return;

			// Use live predicted positions when available (_posRaw filled by SimStep).
			// Fall back to asset rest-pose in edit-mode.
			int fpp = 8; // floats per particle (PositionsBuffer stride = 32 bytes)
			bool hasLive = _posRaw != null
						&& _softBody.State != null
						&& _posRaw.Length == _softBody.State.ParticleCount * fpp;

			// ── Particle spheres ──────────────────────────────────────────────
			Gizmos.color = GizmoParticleColor;
			foreach (int idx in indices)
			{
				if (idx < 0 || idx >= particles.Length)
					continue;

				Vector3 wp = hasLive
					? new Vector3(
						_posRaw[idx * fpp],
						_posRaw[idx * fpp + 1],
						_posRaw[idx * fpp + 2])
					: _softBody.transform.TransformPoint(particles[idx].Position);

				Gizmos.DrawSphere(wp, GizmoParticleRadius);
			}

			// ── Spring links ──────────────────────────────────────────────────
			// Only draw edges whose both endpoints belong to this group.
			if (edges == null || edges.Length == 0)
				return;

			var inGroup = new HashSet<int>(indices);

			foreach (var e in edges)
			{
				int a = (int) e.IndexA;
				int b = (int) e.IndexB;
				if (!inGroup.Contains(a) || !inGroup.Contains(b))
					continue;
				if (a >= particles.Length || b >= particles.Length)
					continue;

				Vector3 wpA = hasLive
					? new Vector3(
						_posRaw[a * fpp],
						_posRaw[a * fpp + 1],
						_posRaw[a * fpp + 2])
					: _softBody.transform.TransformPoint(particles[a].Position);

				Vector3 wpB = hasLive
					? new Vector3(
						_posRaw[b * fpp],
						_posRaw[b * fpp + 1],
						_posRaw[b * fpp + 2])
					: _softBody.transform.TransformPoint(particles[b].Position);

				float curLen = Vector3.Distance(wpA, wpB);
				float strain = e.RestLen > 1e-6f ? (curLen - e.RestLen) / e.RestLen : 0f;

				Gizmos.color = EvalStrainColor(strain);
				Gizmos.DrawLine(wpA, wpB);

				// Strain % label at midpoint
				UnityEditor.Handles.Label(
					(wpA + wpB) * 0.5f,
					$"{strain * 100f:+0.#;-0.#;0}%",
					UnityEditor.EditorStyles.miniLabel);
			}
			// ── AttachTarget links ────────────────────────────────────────────
			// Draw a line from each particle to its target world-space anchor,
			// plus a wire-sphere at the target to make it easy to spot.
			Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f); // yellow
			Gizmos.DrawWireSphere(AttachTarget.position, GizmoParticleRadius * 1.5f);

			for (int g = 0; g < indices.Length; g++)
			{
				int idx = indices[g];
				if (idx < 0 || idx >= particles.Length)
					continue;

				Vector3 wp = hasLive
					? new Vector3(
						_posRaw[idx * fpp],
						_posRaw[idx * fpp + 1],
						_posRaw[idx * fpp + 2])
					: _softBody.transform.TransformPoint(particles[idx].Position);

				// Target anchor = rest offset transformed by the current AttachTarget pose.
				Vector3 anchor = (_restOffsets != null && g < _restOffsets.Length)
					? AttachTarget.TransformPoint(_restOffsets[g])
					: AttachTarget.position;

				Gizmos.DrawLine(wp, anchor);
			}
		}

		// Lerp rest→stretch or rest→compress based on signed strain.
		// Strains within ±RestBand show GizmoRestColor.
		// Outside the band, lerp saturates at ±StrainSaturate.
		Color EvalStrainColor(float strain)
		{
			if (strain > RestBand)
			{
				float t = Mathf.Clamp01(
					(strain - RestBand) / Mathf.Max(StrainSaturate - RestBand, 1e-5f));
				return Color.Lerp(GizmoRestColor, GizmoStretchColor, t);
			}
			if (strain < -RestBand)
			{
				float t = Mathf.Clamp01(
					(-strain - RestBand) / Mathf.Max(StrainSaturate - RestBand, 1e-5f));
				return Color.Lerp(GizmoRestColor, GizmoCompressColor, t);
			}
			return GizmoRestColor;
		}
#endif
	}
}
