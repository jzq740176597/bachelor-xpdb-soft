// SoftBodySimulationManager.cs
// Central XPBD simulation manager.
//
// ── Rigid-body collision ─────────────────────────────────────────────────────
// All non-soft colliders register via XpbdColliderSource (Sphere/OBB/Capsule/
// Convex MeshCollider). OBB: param1<0 → CCD segment-vs-face (thin floors);
// param1≥0 → interior point-in-box (thick shapes).
//
// ── Soft-body vs Soft-body collision ────────────────────────────────────────
// Default: zero soft-soft collision (zero cost).
// Opt-in via two complementary APIs (may use both simultaneously):
//
//   1. Explicit pair:
//        manager.AddSoftSoftPair(bodyA, bodyB)
//        manager.RemoveSoftSoftPair(bodyA, bodyB)
//      No layer setup needed. Precise per-pair control.
//
//   2. Layer / Mask (like Unity physics layers, 0–31):
//        bodyA.CollisionLayer = 1;  bodyA.CollisionMask = 1<<2;
//        bodyB.CollisionLayer = 2;  bodyB.CollisionMask = 1<<1;
//      Two bodies collide iff A.Mask has B's layer AND B.Mask has A's layer.
//      CollisionMask = 0 → body never collides via layer (only via explicit pairs).
//
// Both mechanisms resolve to the same GPU dispatch (SoftSoftCollision.compute).
// Particle radius controls the contact threshold; set SoftSoftParticleRadius on
// each SoftBodyComponent, or use the global fallback SoftSoftDefaultRadius here.
//
// [DefaultExecutionOrder(-100)] ensures Awake runs before XpbdColliderSource
// so Instance is set before any collider tries to register.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	[DefaultExecutionOrder(-100)]
	[DisallowMultipleComponent]
	public class SoftBodySimulationManager : MonoBehaviour
	{
		// ── Singleton ─────────────────────────────────────────────────────────
		public static SoftBodySimulationManager Instance
		{
			get; private set;
		}

		// ── Constants ─────────────────────────────────────────────────────────
		public const int MAX_COLLISION_CONSTRAINTS = 10000;
		const int GROUP_SIZE = 32;
		const int IMPULSE_STRIDE = 32; // bytes — must match Collision_Shapes.compute

		// ── Inspector: Compute Shaders ────────────────────────────────────────
		[Header("Compute Shaders")]
		public ComputeShader SoftBodySimCS;
		public ComputeShader CollisionShapesCS;
		public ComputeShader SoftSoftCollisionCS; // SoftSoftCollision.compute  — ComputeBounds, ClearSoftSoftDelta, DetectSoftSoft
		public ComputeShader SoftSoftApplyCS;     // SoftSoftApply.compute      — ApplySoftSoftDelta
		public ComputeShader DeformCS;

		// ── Inspector: Simulation ─────────────────────────────────────────────
		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		// ── Inspector: Soft-Soft Collision ────────────────────────────────────
		[Header("Soft-Soft Collision")]
		[Tooltip("Multiplier for the auto-computed contact radius (default 1.5). " +
				 "ColRadius = AutoRadiusMul * sqrt(4π * R² / N) where R is the body's " +
				 "bounding radius and N is its particle count. Override per-body via " +
				 "SoftBodyComponent.SoftSoftParticleRadius (set > 0 to bypass auto).")]
		public float AutoRadiusMul = 1.5f;

		// ── Bodies and rigid colliders ─────────────────────────────────────────
		readonly List<SoftBodyComponent> _bodies = new();
		readonly List<XpbdColliderSource> _colliders = new();

		// ── Soft-soft pair registry ───────────────────────────────────────────
		// Canonical key: (lower instanceID, higher instanceID) so (A,B)==(B,A).
		// Value is null until first EnsureSoftSoftPairBuffers allocates it.
		readonly Dictionary<(int, int), SoftSoftPairBuffers> _softSoftPairs = new();
		// Tracks which pairs have already run Clear+Detect this substep.
		// First body to reach a pair does Clear+Detect+Apply-both; second skips.
		readonly HashSet<(int, int)> _softSoftDetectedThisStep = new();

		// ── Rigid-collision GPU buffers ───────────────────────────────────────
		ComputeBuffer _shapesBuffer;
		ComputeBuffer _shapeVelBuffer;
		ComputeBuffer _meshFacePlanesBuffer;
		ComputeBuffer _impulseBuffer;

		int _shapeCount;
		int _dynSlotCount;
		int _totalFacePlanes;

		ShapeDescriptorCPU[] _cpuShapes = Array.Empty<ShapeDescriptorCPU>();
		Vector3[] _cpuVels = Array.Empty<Vector3>();
		Vector4[] _cpuFacePlanes = Array.Empty<Vector4>();
		byte[] _cpuImpulseReadback = Array.Empty<byte>();

		// ── Kernel IDs ────────────────────────────────────────────────────────
		int _kClearImpulse, _kDetectShapes, _kShapesSolve;
		int _kComputeBounds, _kClearSoftSoftDelta, _kDetectSoftSoft, _kApplySoftSoftDelta;
		int _kPresolve, _kPostsolve, _kStretch, _kVolume;
		int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

		// Per-body GPU bounds buffer + CPU readback (4 uints: centroid_fp×3, count, maxDistSq, pad×3)
		// Allocated once per body in AddBody, freed in RemoveBody.
		// ComputeBounds writes to this; manager reads it to get live circumsphere.
		readonly Dictionary<int, ComputeBuffer> _bodyBoundsBuffers = new();
		readonly int[] _boundsReadback = new int[8]; // scratch, reused every substep

		float _timeAccum, _fixedDT, _subDT;

		// ─────────────────────────────────────────────────────────────────────
		void Awake()
		{
			if (Instance != null && Instance != this)
			{
				Debug.LogWarning("[XPBD] Duplicate SoftBodySimulationManager destroyed.");
				Destroy(this);
				return;
			}
			Instance = this;
			CacheKernelIDs();
		}

		void OnDestroy()
		{
			foreach (var b in _bodies)
				Destroy(b);
			ReleaseCollisionBuffers();
			ReleaseSoftSoftBuffers();
			SoftBodyGPUState.ClearAssetCache_S();
			if (Instance == this)
				Instance = null;
		}

		// ─────────────────────────────────────────────────────────────────────
		void Update()
		{
			_fixedDT = 1f / FixedTimeStepFPS;
			_subDT = _fixedDT / SubSteps;
			_timeAccum += Time.deltaTime;
			if (_timeAccum < _fixedDT)
				return;
			_timeAccum -= _fixedDT;

			bool hasRigid    = _colliders.Count > 0 && CollisionShapesCS;
			bool hasSoftSoft = _softSoftPairs.Count > 0 && SoftSoftCollisionCS && SoftSoftApplyCS;

			if (hasRigid)
			{
				RebuildCollisionBuffers(_fixedDT);
				if (_dynSlotCount > 0)
					DispatchClearImpulse();
			}

			if (hasSoftSoft)
				EnsureSoftSoftPairBuffers();

			// Substep-outer / bodies-inner with two phases:
			//   Phase 1: ALL bodies integrate (Presolve→Stretch→Volume→Postsolve)
			//   Phase 2: ALL bodies run collision
			// Required for soft-soft: both bodies must be at the same substep
			// position when detect runs. Also preserves the floor fix — for each
			// body, Postsolve always completes before its collision runs.
			for (int s = 0; s < SubSteps; s++)
			{
				foreach (var body in _bodies)
				{
					if (!body) continue;
					DispatchIntegrate(body.State);
					// Recompute live circumsphere after Postsolve so it reflects
					// the current deformed shape. Used by broad-phase in Phase 2.
					if (hasSoftSoft) DispatchComputeBounds(body);
				}

				_softSoftDetectedThisStep.Clear();
				foreach (var body in _bodies)
				{
					if (!body) continue;
					if (hasRigid)
					{
						ResetColSize(body.State);
						DispatchDetectShapes(body.State);
						DispatchShapesSolve(body.State);
					}
					if (hasSoftSoft)
						DispatchSoftSoftPairs(body.State);
				}
			}

			foreach (var body in _bodies)
			{
				if (!body) continue;
				DispatchDeform(body);
			}

			if (hasRigid && _dynSlotCount > 0)
				ApplyDynamicImpulses();
		}

		// ── Public API ────────────────────────────────────────────────────────

		public void AddBody(SoftBodyComponent body)
		{
			_bodies.Add(body);
			// Rest-pose bounding radius — drives the ColRadius auto-formula only.
			// Live circumsphere is re-computed each substep via ComputeBounds kernel.
			ComputeRestBoundingRadius(body);
			// Allocate per-body GPU bounds buffer (8 × uint = 32 bytes, Raw).
			// [0..2] centroid fixed-point sum, [3] count, [4] maxDistSq (float CAS), [5..7] pad
			if (SoftSoftCollisionCS && body.State != null)
			{
				var buf = new ComputeBuffer(8, sizeof(uint), ComputeBufferType.Raw);
				buf.SetData(new uint[8]);
				_bodyBoundsBuffers[body.GetInstanceID()] = buf;
			}
		}

		public void RemoveBody(SoftBodyComponent body)
		{
			_bodies.Remove(body);
			int id = body.GetInstanceID();
			if (_bodyBoundsBuffers.TryGetValue(id, out var buf))
			{
				buf?.Release();
				_bodyBoundsBuffers.Remove(id);
			}
		}

		// One-time GPU readback at spawn to get rest-pose circumsphere radius.
		// Used only for the ColRadius auto-formula (not live collision detection).
		void ComputeRestBoundingRadius(SoftBodyComponent body)
		{
			var state = body.State;
			if (state == null || state.ParticleCount == 0) return;

			int floatsPerParticle = 8; // float3 pos + float pad + float3 vel + float invMass = 32 bytes
			var raw = new float[state.ParticleCount * floatsPerParticle];
			state.ParticleBuffer.GetData(raw);

			var centroid = Vector3.zero;
			for (int i = 0; i < state.ParticleCount; i++)
			{
				int o = i * floatsPerParticle;
				centroid += new Vector3(raw[o], raw[o + 1], raw[o + 2]);
			}
			centroid /= state.ParticleCount;

			float maxDist = 0f;
			for (int i = 0; i < state.ParticleCount; i++)
			{
				int o = i * floatsPerParticle;
				maxDist = Mathf.Max(maxDist,
					Vector3.Distance(new Vector3(raw[o], raw[o + 1], raw[o + 2]), centroid));
			}
			body.BoundingRadius = Mathf.Max(maxDist, 0.01f);
		}

		public void RegisterCollider(XpbdColliderSource src)
		{
			if (!_colliders.Contains(src))
				_colliders.Add(src);
		}

		public void UnregisterCollider(XpbdColliderSource src)
			=> _colliders.Remove(src);

		// ── Soft-Soft pair API ────────────────────────────────────────────────

		/// <summary>
		/// Register an explicit soft-soft collision pair.
		/// Order of A/B doesn't matter; the pair is keyed canonically.
		/// </summary>
		public void AddSoftSoftPair(SoftBodyComponent a, SoftBodyComponent b)
		{
			var key = MakePairKey(a, b);
			if (!_softSoftPairs.ContainsKey(key))
				_softSoftPairs[key] = null; // buffers allocated lazily
		}

		/// <summary>Remove a previously registered explicit pair.</summary>
		public void RemoveSoftSoftPair(SoftBodyComponent a, SoftBodyComponent b)
		{
			var key = MakePairKey(a, b);
			if (_softSoftPairs.TryGetValue(key, out var bufs))
			{
				bufs?.Release();
				_softSoftPairs.Remove(key);
			}
		}

		/// <summary>
		/// Scan all registered bodies and auto-register / auto-remove soft-soft
		/// pairs according to their SoftCollisionLayer / SoftCollisionMask bitmasks.
		/// Two bodies collide iff each one's mask includes the other's layer.
		/// SoftCollisionMask = 0 means "no layer-based collision" for that body.
		/// Call this after adding bodies or changing their layer/mask values.
		/// </summary>
		public void RebuildLayerPairs()
		{
			for (int i = 0; i < _bodies.Count; i++)
			{
				var a = _bodies[i];
				if (!a || a.SoftCollisionMask == 0)
					continue;
				for (int j = i + 1; j < _bodies.Count; j++)
				{
					var b = _bodies[j];
					if (!b || b.SoftCollisionMask == 0)
						continue;
					bool aSeesB = (a.SoftCollisionMask & (1 << b.SoftCollisionLayer)) != 0;
					bool bSeesA = (b.SoftCollisionMask & (1 << a.SoftCollisionLayer)) != 0;
					if (aSeesB && bSeesA)
						AddSoftSoftPair(a, b);
					else
						RemoveSoftSoftPair(a, b);
				}
			}
		}

		// ── Rigid-collision buffer management ────────────────────────────────

		void RebuildCollisionBuffers(float dt)
		{
			int count = _colliders.Count;
			_shapeCount = count;
			_dynSlotCount = 0;

			uint dynSlot = 0;
			uint facePlaneOff = 0;

			for (int i = 0; i < count; i++)
			{
				var col = _colliders[i];
				uint slot = col.Type == XpbdColliderSource.ColType.Dynamic ? dynSlot++ : 0u;
				if (col.Type == XpbdColliderSource.ColType.Dynamic)
					_dynSlotCount++;

				if (col.Shape == XpbdColliderSource.ShapeType.Convex)
					col.FacePlanesOffset = facePlaneOff;

				col.RefreshDescriptor(dt, slot);

				if (col.Shape == XpbdColliderSource.ShapeType.Convex && col.FacePlanes != null)
					facePlaneOff += (uint) col.FacePlanes.Length;
			}
			_totalFacePlanes = (int) facePlaneOff;

			if (_cpuShapes.Length < count)
				_cpuShapes = new ShapeDescriptorCPU[count];
			if (_cpuVels.Length < count)
				_cpuVels = new Vector3[count];
			if (_cpuFacePlanes.Length < _totalFacePlanes)
				_cpuFacePlanes = new Vector4[Mathf.Max(_totalFacePlanes, 1)];

			int fpIdx = 0;
			for (int i = 0; i < count; i++)
			{
				var col = _colliders[i];
				_cpuShapes[i] = col.Descriptor;
				_cpuVels[i] = col.SurfaceVelocity;
				if (col.Shape == XpbdColliderSource.ShapeType.Convex && col.FacePlanes != null)
				{
					Array.Copy(col.FacePlanes, 0, _cpuFacePlanes, fpIdx, col.FacePlanes.Length);
					fpIdx += col.FacePlanes.Length;
				}
			}

			bool needRebuild = _shapesBuffer == null
							|| _shapesBuffer.count != count
							|| _meshFacePlanesBuffer == null
							|| _meshFacePlanesBuffer.count != Mathf.Max(_totalFacePlanes, 1);

			if (needRebuild)
			{
				ReleaseCollisionBuffers();
				_shapesBuffer = new ComputeBuffer(count, 64);
				_shapeVelBuffer = new ComputeBuffer(count, 3 * sizeof(float));
				_meshFacePlanesBuffer = new ComputeBuffer(Mathf.Max(_totalFacePlanes, 1), 4 * sizeof(float));
				int iBytes = Mathf.Max(1, _dynSlotCount) * IMPULSE_STRIDE;
				_impulseBuffer = new ComputeBuffer(iBytes / 4, sizeof(uint), ComputeBufferType.Raw);
				_cpuImpulseReadback = new byte[iBytes];
			}

			_shapesBuffer.SetData(_cpuShapes, 0, 0, count);
			_shapeVelBuffer.SetData(_cpuVels, 0, 0, count);
			if (_totalFacePlanes > 0)
				_meshFacePlanesBuffer.SetData(_cpuFacePlanes, 0, 0, _totalFacePlanes);

			var cs = CollisionShapesCS;
			cs.SetInt("_ShapeCount", _shapeCount);
			cs.SetInt("_DynSlotCount", _dynSlotCount);
			cs.SetFloat("_ColDeltaTime", _fixedDT);
		}

		void ReleaseCollisionBuffers()
		{
			_shapesBuffer?.Release();
			_shapesBuffer = null;
			_shapeVelBuffer?.Release();
			_shapeVelBuffer = null;
			_meshFacePlanesBuffer?.Release();
			_meshFacePlanesBuffer = null;
			_impulseBuffer?.Release();
			_impulseBuffer = null;
		}

		// ── Rigid-collision dispatches ────────────────────────────────────────

		void ResetColSize(SoftBodyGPUState body)
			=> body.ColSizeBuffer.SetData(new uint[] { 0 });

		void DispatchClearImpulse()
		{
			CollisionShapesCS.SetBuffer(_kClearImpulse, "_ImpulseBytes", _impulseBuffer);
			CollisionShapesCS.Dispatch(_kClearImpulse, Ceil(_dynSlotCount), 1, 1);
		}

		void DispatchDetectShapes(SoftBodyGPUState body)
		{
			var cs = CollisionShapesCS;
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kDetectShapes, "_Shapes", _shapesBuffer);
			cs.SetBuffer(_kDetectShapes, "_ShapeVelocities", _shapeVelBuffer);
			cs.SetBuffer(_kDetectShapes, "_MeshFacePlanes", _meshFacePlanesBuffer);
			cs.SetBuffer(_kDetectShapes, "_ImpulseBytes", _impulseBuffer);
			cs.SetBuffer(_kDetectShapes, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kDetectShapes, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kDetectShapes, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kDetectShapes, "_ColConstraints", body.ColConstraintBuffer);
			cs.Dispatch(_kDetectShapes, Ceil(_shapeCount * body.ParticleCount), 1, 1);
		}

		void DispatchShapesSolve(SoftBodyGPUState body)
		{
			var cs = CollisionShapesCS;
			cs.SetFloat("_ColDeltaTime", _fixedDT);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kShapesSolve, "_ImpulseBytes", _impulseBuffer);
			cs.SetBuffer(_kShapesSolve, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kShapesSolve, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kShapesSolve, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kShapesSolve, "_ColConstraints", body.ColConstraintBuffer);
			cs.Dispatch(_kShapesSolve, Ceil(MAX_COLLISION_CONSTRAINTS), 1, 1);
		}

		void ApplyDynamicImpulses()
		{
			int wordCount = _dynSlotCount * IMPULSE_STRIDE / 4;
			_impulseBuffer.GetData(_cpuImpulseReadback, 0, 0, wordCount);

			uint slot = 0;
			foreach (var col in _colliders)
			{
				if (col.Type != XpbdColliderSource.ColType.Dynamic)
					continue;
				if (!col.Body)
				{
					slot++;
					continue;
				}

				int b = (int) (slot * IMPULSE_STRIDE);
				float ix = BitConverter.ToSingle(_cpuImpulseReadback, b + 0);
				float iy = BitConverter.ToSingle(_cpuImpulseReadback, b + 4);
				float iz = BitConverter.ToSingle(_cpuImpulseReadback, b + 8);
				uint cnt = BitConverter.ToUInt32(_cpuImpulseReadback, b + 12);
				float cx = BitConverter.ToSingle(_cpuImpulseReadback, b + 16);
				float cy = BitConverter.ToSingle(_cpuImpulseReadback, b + 20);
				float cz = BitConverter.ToSingle(_cpuImpulseReadback, b + 24);

				if (cnt > 0 && !float.IsNaN(ix))
				{
					var impulse = new Vector3(ix, iy, iz);
					var centre = new Vector3(cx, cy, cz) / cnt;
					col.Body.AddForceAtPosition(impulse, centre, ForceMode.Impulse);
				}
				slot++;
			}
		}

		// ── Soft-Soft collision buffer management ─────────────────────────────

		// Called once per fixed step: allocates / reallocates buffers for any
		// pair whose particle counts changed (body re-initialized etc.).
		void EnsureSoftSoftPairBuffers()
		{
			foreach (var key in new List<(int, int)>(_softSoftPairs.Keys))
			{
				var a = FindBody(key.Item1);
				var b = FindBody(key.Item2);
				if (a == null || b == null)
					continue;

				var bufs = _softSoftPairs[key];
				int nA = a.State.ParticleCount;
				int nB = b.State.ParticleCount;

				if (bufs == null || bufs.CountA != nA || bufs.CountB != nB)
				{
					bufs?.Release();
					_softSoftPairs[key] = new SoftSoftPairBuffers(nA, nB);
				}
			}
		}

		void ReleaseSoftSoftBuffers()
		{
			foreach (var bufs in _softSoftPairs.Values)
				bufs?.Release();
			_softSoftPairs.Clear();
		}

		// ── Live circumsphere (broad phase) ──────────────────────────────────

		// Resets the bounds buffer to zero, then dispatches ComputeBounds which
		// accumulates centroid (fixed-point) and max-dist-sq for all particles.
		void DispatchComputeBounds(SoftBodyComponent body)
		{
			int id = body.GetInstanceID();
			if (!_bodyBoundsBuffers.TryGetValue(id, out var buf) || buf == null) return;

			// Zero the accumulator
			buf.SetData(new uint[8]);

			var cs = SoftSoftCollisionCS;
			cs.SetInt("_CountA", body.State.ParticleCount);
			cs.SetBuffer(_kComputeBounds, "_PositionsA", body.State.PositionsBuffer);
			cs.SetBuffer(_kComputeBounds, "_BoundsBuf",  buf);
			cs.Dispatch(_kComputeBounds, Ceil(body.State.ParticleCount), 1, 1);
		}

		// Reads the bounds buffer (GPU→CPU sync) and returns centroid + circumsphere radius.
		// This is a synchronous readback — cheap because the buffer is only 32 bytes.
		// Returns false if no bounds buffer exists for this body.
		bool ReadLiveBounds(SoftBodyComponent body, out Vector3 centroid, out float radius)
		{
			centroid = Vector3.zero;
			radius   = body.BoundingRadius; // fallback to rest-pose

			int id = body.GetInstanceID();
			if (!_bodyBoundsBuffers.TryGetValue(id, out var buf) || buf == null)
				return false;

			buf.GetData(_boundsReadback);

			int count = _boundsReadback[3];
			if (count == 0) return false;

			const float FP_SCALE = 1000f;
			centroid = new Vector3(
				_boundsReadback[0] / (FP_SCALE * count),
				_boundsReadback[1] / (FP_SCALE * count),
				_boundsReadback[2] / (FP_SCALE * count));

			// _boundsReadback[4] stores asuint(maxDistSq from origin).
			// The true circumsphere radius from centroid is <= sqrt(maxDistSq) + |centroid|.
			// We use sqrt(maxDistSq) as a conservative (slightly inflated) bound — safe.
			float maxDistSqFromOrigin = System.BitConverter.Int32BitsToSingle(_boundsReadback[4]);
			radius = Mathf.Sqrt(Mathf.Max(maxDistSqFromOrigin, 0f));
			return true;
		}

		// ── Soft-Soft collision dispatches ────────────────────────────────────

		// Called in Phase 2 of each substep, after all bodies have integrated.
		// _softSoftDetectedThisStep cleared once per substep in Update.
		// First body to reach pair (A,B): Clear+Detect+Apply-both → mark done.
		// Second body to reach pair (A,B): already in set → skip entirely.
		void DispatchSoftSoftPairs(SoftBodyGPUState bodyState)
		{
			var cs = SoftSoftCollisionCS;

			foreach (var kvp in _softSoftPairs)
			{
				var bufs = kvp.Value;
				if (bufs == null)
					continue;

				var a = FindBody(kvp.Key.Item1);
				var b = FindBody(kvp.Key.Item2);
				if (a == null || b == null)
					continue;

				if (a.State != bodyState && b.State != bodyState)
					continue;

				var pairKey = kvp.Key;
				if (_softSoftDetectedThisStep.Contains(pairKey))
					continue;
				_softSoftDetectedThisStep.Add(pairKey);

				// ColRadius auto-formula: AutoRadiusMul * sqrt(4π * R² / N)
				// For unit sphere (R=1, N=313): ~0.30m. Manual override via SoftSoftParticleRadius > 0.
				float AutoRadius(SoftBodyComponent body) =>
					AutoRadiusMul * Mathf.Sqrt(4f * Mathf.PI * body.BoundingRadius * body.BoundingRadius
					                           / Mathf.Max(body.State.ParticleCount, 1));
				float rA = a.SoftSoftParticleRadius > 0 ? a.SoftSoftParticleRadius : AutoRadius(a);
				float rB = b.SoftSoftParticleRadius > 0 ? b.SoftSoftParticleRadius : AutoRadius(b);
				float radius = Mathf.Max(rA, rB);

				// ── Broad phase: live circumsphere overlap ─────────────────────
				// ReadLiveBounds returns the circumsphere computed by DispatchComputeBounds
				// this substep — reflects the current deformed shape, not rest pose.
				// If (centreA-centreB distance) > (radA + radB + colRadius), bodies
				// can't possibly have any particle pairs within colRadius → skip N×M.
				if (ReadLiveBounds(a, out var centA, out var radA) &&
				    ReadLiveBounds(b, out var centB, out var radB))
				{
					if (Vector3.Distance(centA, centB) > radA + radB + radius)
						continue;
				}

				int nA = a.State.ParticleCount;
				int nB = b.State.ParticleCount;

				// ── Clear ─────────────────────────────────────────────────────
				cs.SetInt("_CountA", nA);
				cs.SetBuffer(_kClearSoftSoftDelta, "_DeltaBufA", bufs.DeltaA);
				cs.Dispatch(_kClearSoftSoftDelta, Ceil(nA), 1, 1);

				cs.SetInt("_CountA", nB);
				cs.SetBuffer(_kClearSoftSoftDelta, "_DeltaBufA", bufs.DeltaB);
				cs.Dispatch(_kClearSoftSoftDelta, Ceil(nB), 1, 1);

				// ── Detect ────────────────────────────────────────────────────
				cs.SetInt("_CountA", nA);
				cs.SetInt("_CountB", nB);
				cs.SetFloat("_ColRadius", radius);
				cs.SetBuffer(_kDetectSoftSoft, "_ParticlesA", a.State.ParticleBuffer);
				cs.SetBuffer(_kDetectSoftSoft, "_PositionsA", a.State.PositionsBuffer);
				cs.SetBuffer(_kDetectSoftSoft, "_DeltaBufA", bufs.DeltaA);
				cs.SetBuffer(_kDetectSoftSoft, "_ParticlesB", b.State.ParticleBuffer);
				cs.SetBuffer(_kDetectSoftSoft, "_PositionsB", b.State.PositionsBuffer);
				cs.SetBuffer(_kDetectSoftSoft, "_DeltaBufB", bufs.DeltaB);
				cs.Dispatch(_kDetectSoftSoft, CeilN(nA, 8), CeilN(nB, 8), 1);

				// ── Apply both sides ──────────────────────────────────────────
				// Uses SoftSoftApplyCS (separate file from SoftSoftCollisionCS).
				// Must bind _ParticlesA — ApplySoftSoftDelta writes velocity.
				var applyCs = SoftSoftApplyCS;
				applyCs.SetInt("_CountA", nA);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_ParticlesA", a.State.ParticleBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_PositionsA", a.State.PositionsBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA",  bufs.DeltaA);
				applyCs.Dispatch(_kApplySoftSoftDelta, Ceil(nA), 1, 1);

				applyCs.SetInt("_CountA", nB);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_ParticlesA", b.State.ParticleBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_PositionsA", b.State.PositionsBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA",  bufs.DeltaB);
				applyCs.Dispatch(_kApplySoftSoftDelta, Ceil(nB), 1, 1);
			}
		}

		// ── Elastic integration (Presolve + Stretch + Volume + Postsolve) ─────
		// Collision is NOT here — it runs after ALL bodies integrate (see Update).
		void DispatchIntegrate(SoftBodyGPUState body)
		{
			var cs = SoftBodySimCS;
			cs.SetFloat("_DeltaTime", _subDT);
			cs.SetFloat("_DistanceCompliance", EdgeCompliance);
			cs.SetFloat("_VolumeCompliance", VolumeCompliance);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetInt("_EdgeCount", body.EdgeCount);
			cs.SetInt("_TetCount", body.TetCount);

			BindSimBuffers(cs, _kPresolve, body);
			cs.Dispatch(_kPresolve, Ceil(body.ParticleCount), 1, 1);

			BindSimBuffers(cs, _kStretch, body);
			cs.Dispatch(_kStretch, Ceil(body.EdgeCount), 1, 1);

			BindSimBuffers(cs, _kVolume, body);
			cs.Dispatch(_kVolume, Ceil(body.TetCount), 1, 1);

			BindSimBuffers(cs, _kPostsolve, body);
			cs.Dispatch(_kPostsolve, Ceil(body.ParticleCount), 1, 1);
		}

		void BindSimBuffers(ComputeShader cs, int kernel, SoftBodyGPUState body)
		{
			cs.SetBuffer(kernel, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(kernel, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(kernel, "_Edges", body.EdgeBuffer);
			cs.SetBuffer(kernel, "_Tetrahedrals", body.TetBuffer);
			cs.SetBuffer(kernel, "_DeltaBytes", body.DeltaBytesBuffer);
		}

		// ── Mesh deformation ──────────────────────────────────────────────────

		void DispatchDeform(SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			var cs = DeformCS;

			cs.SetInt("_VertexCount", body.VertexCount);
			cs.SetInt("_IndexCount", body.IndexCount);

			body.ClearNormalBytes();

			int deformKernel = body.UseTetDeformation ? _kTetDeform : _kDirectDeform;
			cs.SetBuffer(deformKernel, "_VertexPositions", body.VertexPositionsBuffer);
			cs.SetBuffer(deformKernel, "_VertexNormals", body.VertexNormalsBuffer);
			cs.SetBuffer(deformKernel, "_Positions", body.PositionsBuffer);
			if (body.UseTetDeformation)
			{
				cs.SetBuffer(deformKernel, "_Skinning", body.SkinningBuffer);
				cs.SetBuffer(deformKernel, "_Tetrahedrals", body.TetBuffer);
			}
			else
			{
				cs.SetBuffer(deformKernel, "_OrigIndices", body.OrigIndicesBuffer);
			}
			cs.Dispatch(deformKernel, Ceil(body.VertexCount), 1, 1);

			cs.SetBuffer(_kRecalcNormals, "_VertexPositions", body.VertexPositionsBuffer);
			cs.SetBuffer(_kRecalcNormals, "_VertexNormals", body.VertexNormalsBuffer);
			cs.SetBuffer(_kRecalcNormals, "_Indices", body.MeshIndicesBuffer);
			cs.SetBuffer(_kRecalcNormals, "_NormalBytes", body.NormalBytesBuffer);
			cs.Dispatch(_kRecalcNormals, Ceil(body.IndexCount / 3), 1, 1);

			cs.SetBuffer(_kNormalizeNormals, "_VertexNormals", body.VertexNormalsBuffer);
			cs.SetBuffer(_kNormalizeNormals, "_NormalBytes", body.NormalBytesBuffer);
			cs.Dispatch(_kNormalizeNormals, Ceil(body.VertexCount), 1, 1);

			body.VertexPositionsBuffer.GetData(body.ReadbackPos);
			body.VertexNormalsBuffer.GetData(body.ReadbackNrm);
			body.RenderMesh.vertices = body.ReadbackPos;
			body.RenderMesh.normals = body.ReadbackNrm;
			body.RenderMesh.RecalculateBounds();

			bodyCmp.InternalOnDeformed();
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		static (int, int) MakePairKey(SoftBodyComponent a, SoftBodyComponent b)
		{
			int ia = a.GetInstanceID(), ib = b.GetInstanceID();
			return ia < ib ? (ia, ib) : (ib, ia);
		}

		SoftBodyComponent FindBody(int instanceID)
		{
			foreach (var b in _bodies)
				if (b && b.GetInstanceID() == instanceID)
					return b;
			return null;
		}

		static int Ceil(int n) => (n + GROUP_SIZE - 1) / GROUP_SIZE;
		static int CeilN(int n, int g) => (n + g - 1) / g;

		// ── Kernel ID cache ───────────────────────────────────────────────────

		void CacheKernelIDs()
		{
			_kPresolve = SoftBodySimCS.FindKernel("Presolve");
			_kPostsolve = SoftBodySimCS.FindKernel("Postsolve");
			_kStretch = SoftBodySimCS.FindKernel("StretchConstraint");
			_kVolume = SoftBodySimCS.FindKernel("VolumeConstraint");

			_kClearImpulse = CollisionShapesCS.FindKernel("ClearImpulseAccum");
			_kDetectShapes = CollisionShapesCS.FindKernel("DetectShapes");
			_kShapesSolve = CollisionShapesCS.FindKernel("SolveCollisions");

			if (SoftSoftCollisionCS)
			{
				_kComputeBounds      = SoftSoftCollisionCS.FindKernel("ComputeBounds");
				_kClearSoftSoftDelta = SoftSoftCollisionCS.FindKernel("ClearSoftSoftDelta");
				_kDetectSoftSoft     = SoftSoftCollisionCS.FindKernel("DetectSoftSoft");
			}
			// ApplySoftSoftDelta lives in its own file (SoftSoftApply.compute).
			// See SoftSoftCollision.compute header: split-file rule prevents Unity
			// from demanding _ParticlesA/B be bound for kernels that don't use them.
			if (SoftSoftApplyCS)
				_kApplySoftSoftDelta = SoftSoftApplyCS.FindKernel("ApplySoftSoftDelta");

			_kDirectDeform = DeformCS.FindKernel("DirectDeform");
			_kTetDeform = DeformCS.FindKernel("TetDeform");
			_kRecalcNormals = DeformCS.FindKernel("RecalcNormals");
			_kNormalizeNormals = DeformCS.FindKernel("NormalizeNormals");
		}
	}

	// ── Per-pair GPU buffers for soft-soft collision ───────────────────────────
	// Owned by the manager. One instance per registered (A,B) pair.
	// DeltaA / DeltaB: RWByteAddressBuffer, 12 bytes per particle (3 × float).
	class SoftSoftPairBuffers
	{
		public readonly int CountA;
		public readonly int CountB;
		public readonly ComputeBuffer DeltaA; // 12 b/particle = 3 uint words
		public readonly ComputeBuffer DeltaB;

		public SoftSoftPairBuffers(int countA, int countB)
		{
			CountA = countA;
			CountB = countB;
			DeltaA = new ComputeBuffer(countA * 3, sizeof(uint), ComputeBufferType.Raw);
			DeltaB = new ComputeBuffer(countB * 3, sizeof(uint), ComputeBufferType.Raw);
		}

		public void Release()
		{
			DeltaA?.Release();
			DeltaB?.Release();
		}
	}
}
