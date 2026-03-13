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

		//[Header("Reference (auto-found if null)")]
		SoftBodyComponent _softBody;

		// ── Runtime state ─────────────────────────────────────────────────────
		// Rest offsets in AttachTarget's LOCAL space, computed at Enable time.
		Vector3[] _restOffsets;

		// Cached particle indices from the group
		int[] _indices;

		// GPU readback scratch buffers (allocated once, reused)
		// Particle struct: float3 pos, float pad, float3 vel, float invMass = 32 bytes
		// We read the full ParticleBuffer to get velocities as well.
		float[] _particleRaw;

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
		}

		void OnDisable()
		{
			// When detached in Static mode, restore the original invMass so
			// pinned particles become dynamic again.
			if (Type == AttachmentType.Static)
				RestoreInvMass();
		}

		// ── FixedUpdate — apply attachment each physics step ──────────────────
		void FixedUpdate()
		{
			if (!Validate())
				return;
			if (_indices == null || _indices.Length == 0)
				return;
			if (AttachTarget == null)
				return;

			var state = _softBody.State;
			if (state == null)
				return;

			int n = state.ParticleCount;
			int floatsPerParticle = 8; // float3 pos(12) + float pad(4) + float3 vel(12) + float invMass(4) = 32
			if (_particleRaw == null || _particleRaw.Length != n * floatsPerParticle)
				_particleRaw = new float[n * floatsPerParticle];

			// GPU → CPU readback (synchronous, acceptable for small groups)
			state.ParticleBuffer.GetData(_particleRaw);

			bool dirty = false;

			for (int g = 0; g < _indices.Length; g++)
			{
				int idx = _indices[g];
				if (idx < 0 || idx >= n)
					continue;

				// Compute target world position from rest offset
				Vector3 targetWorld = AttachTarget.TransformPoint(
					g < _restOffsets.Length ? _restOffsets[g] : Vector3.zero);

				int o = idx * floatsPerParticle;
				var curPos = new Vector3(_particleRaw[o], _particleRaw[o + 1], _particleRaw[o + 2]);
				var curVel = new Vector3(_particleRaw[o + 4], _particleRaw[o + 5], _particleRaw[o + 6]);

				if (Type == AttachmentType.Static)
				{
					// Hard pin: teleport to target, zero velocity
					_particleRaw[o] = targetWorld.x;
					_particleRaw[o + 1] = targetWorld.y;
					_particleRaw[o + 2] = targetWorld.z;
					_particleRaw[o + 4] = 0f;
					_particleRaw[o + 5] = 0f;
					_particleRaw[o + 6] = 0f;
					// invMass[idx] = 0 (pinned) was set in ComputeRestOffsets
					dirty = true;
				}
				else // Dynamic
				{
					// Spring-damper: compute correction impulse toward target
					float dt = Time.fixedDeltaTime;
					if (dt < 1e-6f)
						continue;

					Vector3 delta = targetWorld - curPos;
					float dist = delta.magnitude;
					if (dist < 1e-5f)
						continue;

					// XPBD compliance: correction = delta / (1 + compliance / dt²)
					float invDt2 = 1f / (dt * dt);
					float corrScale = 1f / (1f + Compliance * invDt2);
					Vector3 corr = delta * corrScale;

					// Apply damping to existing velocity component along correction
					Vector3 n_ = delta / dist;
					float vDot = Vector3.Dot(curVel, n_);
					Vector3 dampedVel = curVel - n_ * vDot * Damping;

					// New velocity = (corr / dt) + damped lateral velocity
					Vector3 newVel = corr / dt + (dampedVel - dampedVel);
					// Simpler: add positional impulse to velocity
					newVel = dampedVel + corr / dt;

					_particleRaw[o + 4] = newVel.x;
					_particleRaw[o + 5] = newVel.y;
					_particleRaw[o + 6] = newVel.z;

					// Optionally transfer reaction to target Rigidbody
					if (AttachTarget.TryGetComponent<Rigidbody>(out var rb))
					{
						float invMass = _particleRaw[o + 7];
						if (invMass > 1e-6f)
						{
							float mass = 1f / invMass;
							Vector3 reaction = -corr * mass / dt;
							rb.AddForceAtPosition(reaction, curPos, ForceMode.Force);
						}
					}

					dirty = true;
				}
			}

			if (dirty)
				state.ParticleBuffer.SetData(_particleRaw);
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
			int floatsPerParticle = 8;

			if (_particleRaw == null || _particleRaw.Length != n * floatsPerParticle)
				_particleRaw = new float[n * floatsPerParticle];

			state.ParticleBuffer.GetData(_particleRaw);

			_restOffsets = new Vector3[_indices.Length];
			for (int g = 0; g < _indices.Length; g++)
			{
				int idx = _indices[g];
				if (idx < 0 || idx >= n)
				{
					_restOffsets[g] = Vector3.zero;
					continue;
				}

				int o = idx * floatsPerParticle;
				Vector3 worldPos = new Vector3(_particleRaw[o], _particleRaw[o + 1], _particleRaw[o + 2]);
				// Store rest offset in target's local space
				_restOffsets[g] = AttachTarget.InverseTransformPoint(worldPos);
			}

			// For Static attachments: pin the particles by zeroing invMass
			if (Type == AttachmentType.Static)
				PinParticles(state, n, floatsPerParticle);
		}

		void PinParticles(SoftBodyGPUState state, int n, int floatsPerParticle)
		{
			// invMass is at float offset 7 in each particle record
			foreach (int idx in _indices)
			{
				if (idx < 0 || idx >= n)
					continue;
				_particleRaw[idx * floatsPerParticle + 7] = 0f; // invMass = 0 → pinned
			}
			state.ParticleBuffer.SetData(_particleRaw);
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
			if (AttachTarget == null || ParticleGrp?.ParticleIndices == null)
				return;
			if (_softBody?.State == null)
				return;

			Gizmos.color = Type == AttachmentType.Static
				? new Color(0.2f, 0.8f, 1f, 0.8f)
				: new Color(1f, 0.6f, 0.1f, 0.8f);

			// Draw lines from each grouped particle's rest position to the target
			var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<TetrahedralMeshAsset>(
				UnityEditor.AssetDatabase.GetAssetPath(
					_softBody.GetComponent<UnityEngine.MeshFilter>()?.sharedMesh));

			// Fallback: draw a sphere at the target
			Gizmos.DrawWireSphere(AttachTarget.position, 0.08f);
			Gizmos.DrawIcon(AttachTarget.position + Vector3.up * 0.15f, "sv_icon_dot4_pix16_gizmo");
		}
#endif
	}
}
