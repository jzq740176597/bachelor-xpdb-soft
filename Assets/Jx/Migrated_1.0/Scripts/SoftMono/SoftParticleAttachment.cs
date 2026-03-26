// SoftParticleAttachment.cs
//
// Pins or drives a named ParticleGroup of a soft body to a Transform target.
// Analogous to ObiParticleAttachment in the Obi Softbody plugin.
//
// ATTACHMENT TYPES:
//
//   Static   — particles are snapped to their rest-offset positions every substep
//               and the saved position anchor is also updated, so Postsolve derives
//               zero velocity.  The soft body cannot pull the target.
//
//   Dynamic  — particles are driven toward the target via XPBD positional correction
//               every substep. If AttachTarget has a non-kinematic Rigidbody, the
//               equal-and-opposite reaction force is fed back for two-way coupling.
//
// COORDINATE SPACE:
//   Particles live in world space. RestOffsets are stored in AttachTarget's local
//   space so the group follows the target's rotation and translation correctly.

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

		[Tooltip("Static: hard-pin (zero velocity). " +
				 "Dynamic: XPBD spring (two-way coupling with non-kinematic Rigidbody).")]
		public AttachmentType Type = AttachmentType.Static;

		[Header("Dynamic settings (ignored for Static)")]
		[Tooltip("XPBD compliance: 0 = rigid snap, higher = softer spring.\n" +
				 "Formula: corrScale = 1 / (1 + Compliance / subDt²).\n" +
				 "At subDt ≈ 0.00083 s (60 Hz / 20 substeps):\n" +
				 "  1e-5 ≈ very stiff,  1e-3 ≈ medium,  0.1 ≈ very soft.")]
		public float Compliance = 1e-5f;

		// ── Inspector: Debug Gizmos ───────────────────────────────────────────
		[Header("Debug Gizmos (Dynamic only)")]
		public bool  ShowGizmos          = true;
		public float GizmoParticleRadius = 0.04f;
		public Color GizmoParticleColor  = new Color(0.2f, 0.8f, 1f, 0.85f);
		public Color GizmoRestColor      = Color.green;
		public Color GizmoStretchColor   = Color.red;
		public Color GizmoCompressColor  = Color.cyan;

		[Tooltip("Strain at which edge/link colour fully saturates. E.g. 0.10 = ±10%.")]
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

		// Cached particle indices from the group.
		int[] _indices;

		// GPU readback scratch buffers — allocated once, reused every substep.
		// _posRaw  : PositionsBuffer — GPUPbdPositions[] — 8 floats/particle
		//            layout: predict.x/y/z, _pad0, delta.x/y/z, _pad1
		// _partRaw : ParticleBuffer  — Particle[]        — 8 floats/particle
		//            layout: position.x/y/z, _pad0, velocity.x/y/z, invMass
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

		// ── SimStep — called every substep by Manager after this body's Presolve ──
		//
		// Buffer state at entry (Presolve just ran):
		//   PositionsBuffer[i].predict  = old_predict + vel*dt   ← current predicted pos
		//   ParticleBuffer [i].position = old_predict            ← saved anchor for Postsolve
		//
		// We write corrected predict into PositionsBuffer.
		// Postsolve derives: velocity = (corrected_predict - saved_anchor) / dt
		// No explicit velocity injection — Postsolve handles it automatically.
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

			bool dirtyPos  = false;
			bool dirtyPart = false;

			float dt = Mathf.Max(subDt, 1e-6f);

			// Cache Rigidbody lookup outside loop — TryGetComponent every particle is wasteful.
			Rigidbody targetRb = null;
			bool hasReaction = Type == AttachmentType.Dynamic
				&& AttachTarget.TryGetComponent(out targetRb)
				&& !targetRb.isKinematic;  // kinematic bodies ignore AddForce entirely

			for (int g = 0; g < _indices.Length; g++)
			{
				int idx = _indices[g];
				if (idx < 0 || idx >= n)
					continue;

				Vector3 targetWorld = AttachTarget.TransformPoint(
					g < _restOffsets.Length ? _restOffsets[g] : Vector3.zero);

				int     o       = idx * fpp;
				Vector3 predict = new Vector3(_posRaw[o], _posRaw[o + 1], _posRaw[o + 2]);
				Vector3 delta   = targetWorld - predict;

				if (delta.sqrMagnitude < 1e-10f)
					continue;

				if (Type == AttachmentType.Static)
				{
					// Snap predict to target every substep.
					// Also update the saved anchor so Postsolve derives ~zero velocity.
					_posRaw[o]     = targetWorld.x;
					_posRaw[o + 1] = targetWorld.y;
					_posRaw[o + 2] = targetWorld.z;
					dirtyPos = true;

					if (!dirtyPart)
					{
						state.ParticleBuffer.GetData(_partRaw);
						dirtyPart = true;
					}
					_partRaw[o]     = targetWorld.x;
					_partRaw[o + 1] = targetWorld.y;
					_partRaw[o + 2] = targetWorld.z;
				}
				else // Dynamic
				{
					// XPBD positional correction into predict.
					// corrScale = 1 / (1 + Compliance / dt²)
					// Compliance = 0   → corrScale = 1   → full snap this substep
					// Compliance = 1e-5 → very stiff at subDt ≈ 0.00083 s
					float corrScale = 1f / (1f + Compliance / (dt * dt));
					Vector3 corr    = delta * corrScale;

					_posRaw[o]     = predict.x + corr.x;
					_posRaw[o + 1] = predict.y + corr.y;
					_posRaw[o + 2] = predict.z + corr.z;
					dirtyPos = true;

					// Two-way coupling: feed reaction into target Rigidbody.
					// Skipped for kinematic bodies (they ignore AddForce).
					if (hasReaction)
					{
						if (!dirtyPart)
						{
							state.ParticleBuffer.GetData(_partRaw);
							dirtyPart = true;
						}
						float invMass = _partRaw[idx * fpp + 7];
						if (invMass > 1e-6f)
						{
							float   mass     = 1f / invMass;
							Vector3 reaction = -corr * mass / dt;
							targetRb.AddForceAtPosition(reaction, predict, ForceMode.Force);
						}
					}
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
				Vector3 wp = ParticlePos(idx, fpp, hasLive, particles);
				Gizmos.DrawSphere(wp, GizmoParticleRadius);
			}

			// ── Intra-group spring links (strain coloured) ────────────────────
			if (edges != null && edges.Length > 0)
			{
				var inGroup = new HashSet<int>(indices);
				foreach (var e in edges)
				{
					int a = (int)e.IndexA;
					int b = (int)e.IndexB;
					if (!inGroup.Contains(a) || !inGroup.Contains(b))
						continue;
					if (a >= particles.Length || b >= particles.Length)
						continue;

					Vector3 wpA = ParticlePos(a, fpp, hasLive, particles);
					Vector3 wpB = ParticlePos(b, fpp, hasLive, particles);

					float strain = e.RestLen > 1e-6f
						? (Vector3.Distance(wpA, wpB) - e.RestLen) / e.RestLen
						: 0f;

					Gizmos.color = EvalStrainColor(strain);
					Gizmos.DrawLine(wpA, wpB);

					UnityEditor.Handles.Label(
						(wpA + wpB) * 0.5f,
						$"{strain * 100f:+0.#;-0.#;0}%",
						UnityEditor.EditorStyles.miniLabel);
				}
			}

			// ── Particle → anchor links (strain coloured by stretch to target) ─
			// Each particle has a rest-offset anchor on AttachTarget.
			// The link colour shows how far the particle is from its anchor:
			//   green = at rest distance, red = stretched, cyan = compressed.
			// A small diamond is drawn AT the anchor point on the target.
			bool hasOffsets = _restOffsets != null && _restOffsets.Length == indices.Length;

			for (int g = 0; g < indices.Length; g++)
			{
				int idx = indices[g];
				if (idx < 0 || idx >= particles.Length)
					continue;

				Vector3 wp = ParticlePos(idx, fpp, hasLive, particles);

				// Anchor = rest-offset position on the target (not the target origin).
				Vector3 anchor = hasOffsets
					? AttachTarget.TransformPoint(_restOffsets[g])
					: AttachTarget.position;

				// Strain relative to rest (rest distance = 0 at attachment time).
				// Positive = particle has moved away from anchor (stretched).
				float linkDist   = Vector3.Distance(wp, anchor);
				float linkStrain = linkDist > 1e-4f ? linkDist : 0f; // always ≥ 0
				// Map: 0 = green, >0 = red (never compressed, only stretched or at rest).
				float t = Mathf.Clamp01(linkStrain / Mathf.Max(StrainSaturate, 1e-5f));
				Color linkColor = Color.Lerp(GizmoRestColor, GizmoStretchColor, t);

				Gizmos.color = linkColor;
				Gizmos.DrawLine(wp, anchor);

				// Small diamond at anchor to show where the particle SHOULD be.
				float r = GizmoParticleRadius * 0.7f;
				Gizmos.color = new Color(linkColor.r, linkColor.g, linkColor.b, 1f);
				Gizmos.DrawWireSphere(anchor, r);

				// Distance label at midpoint of link.
				UnityEditor.Handles.Label(
					(wp + anchor) * 0.5f,
					$"{linkDist * 100f:0.#} cm",
					UnityEditor.EditorStyles.miniLabel);
			}

			// Target origin marker.
			Gizmos.color = new Color(1f, 0.85f, 0.1f, 0.9f);
			Gizmos.DrawWireSphere(AttachTarget.position, GizmoParticleRadius * 1.5f);
		}

		// World-space particle position: live GPU data if available, else rest-pose.
		Vector3 ParticlePos(int idx, int fpp, bool hasLive, ParticleData[] particles)
		{
			if (hasLive)
				return new Vector3(_posRaw[idx * fpp], _posRaw[idx * fpp + 1], _posRaw[idx * fpp + 2]);
			return _softBody.transform.TransformPoint(particles[idx].Position);
		}

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
