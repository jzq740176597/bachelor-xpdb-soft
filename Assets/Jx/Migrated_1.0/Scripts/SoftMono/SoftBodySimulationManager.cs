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
		[Header("SDF Collision")]
		public ComputeShader SdfCollisionCS;   // SdfCollision.compute
		public ComputeShader SoftSoftCollisionCS; // SoftSoftCollision.compute  — ComputeBounds, ClearSoftSoftDelta, DetectSoftSoft
		public ComputeShader SoftSoftApplyCS;     // SoftSoftApply.compute      — ApplySoftSoftDelta
		public ComputeShader DeformCS;

		// ── Inspector: Simulation ─────────────────────────────────────────────
		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		[Header("Collision")]
		[Range(1, 8)]
		[Tooltip("How many times to re-run detect+solve per substep after elastic constraints. " +
				 "Higher values prevent penetration at the cost of GPU time. 2-3 is usually enough.")]
		public int CollisionIterations = 3;
		[Tooltip("Project elastic constraint endpoints outside colliders before computing stretch.")]
		public bool UseExclusionStretch = true;
		[Range(1, 4)]
		[Tooltip("Stretch+Volume exclusion iterations per substep. More = stronger contact resistance.")]
		public int ExclusionIterations = 1;

		[Tooltip("Scale for auto-computed particle radius. 1.0 = exact gap coverage. " +
				"Lower = less visible margin but may allow very small colliders to slip through.")]
		[Range(0.1f, 2f)] public float ParticleRadiusScale = 0.8f;

		[Header("Contact Skin")]
		[Tooltip("Minimum contact skin width in metres. Default 0.005 (5 mm). " +
				 "Auto-skin can go no smaller than this.")]
		public float ContactSkinMin = 0.005f;
		[Tooltip("Maximum contact skin width in metres. Default 0.10 (10 cm). " +
				 "Auto-skin can grow no larger than this.")]
		public float ContactSkinMax = 0.10f;
		[Tooltip("Skin = clamp(softBodyMaxExtent * this fraction, Min, Max). " +
				 "0.25 works well for most body sizes.")]
		[Range(0.05f, 0.5f)] public float ContactSkinFraction = 0.25f;

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

		// ── Attachment callbacks ───────────────────────────────────────────────
		// Called every substep after Presolve, before constraints.
		// Signature: (body, positionsBuffer, deltaBuffer, subDt)
		// Attachment writes corrections directly into positionsBuffer.predict
		// for the specific body it owns — no full GetData/SetData round-trip.
		readonly List<System.Action<SoftBodyComponent, ComputeBuffer, ComputeBuffer, float>> _attachmentCallbacks = new();

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
		int _kPresolve, _kPostsolve, _kStretch, _kVolume, _kClampDelta, _kClearDelta;
		int _kHardProject, _kStretchExclusion, _kVolumeExclusion;
		int _kWriteCollisionState, _kClearCollisionState;
		int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

		// Per-body GPU bounds buffer + CPU readback (4 uints: centroid_fp×3, count, maxDistSq, pad×3)
		// Allocated once per body in AddBody, freed in RemoveBody.
		// ComputeBounds writes to this; manager reads it to get live circumsphere.
		readonly Dictionary<int, ComputeBuffer> _bodyBoundsBuffers = new();
		// Per-body _Delta StructuredBuffer<float3> (particleCount × 12 bytes).
		// Written by Presolve (zero), StretchConstraint, VolumeConstraint;
		// read by Postsolve. Replaces the old RWByteAddressBuffer DeltaBytesBuffer.
		readonly Dictionary<int, ComputeBuffer> _bodyDeltaBuffers = new();
		readonly int[] _boundsReadback = new int[8]; // scratch, reused every substep

		float _timeAccum, _fixedDT, _subDT;
		// Unified contact skin — set each frame from the bodies' bounding extents.
		// GPU uniform _ContactSkin; replaces the old per-shape #define constants.
		float _contactSkin = 0.04f;
		float _particleRadius = 0.05f;

		#region Sdf_Collider
		readonly List<XpbdSdfCollider> _sdfColliders = new();

		// Flat SDF data buffer — all grids packed, indexed by SdfDataOffset per shape.
		ComputeBuffer _sdfDataBuffer;
		// Per-SDF-shape descriptor buffer — one SdfShapeDescriptorCPU per shape.
		ComputeBuffer _sdfShapesBuffer;
		// Per-SDF-shape world-to-local matrix buffer.
		ComputeBuffer _sdfTransformBuffer;

		int _kSdfDetect;
		int _sdfTotalCells;

		// CPU scratch arrays
		SdfShapeDescriptorCPU[] _cpuSdfShapes = Array.Empty<SdfShapeDescriptorCPU>();
		Matrix4x4[] _cpuSdfTransforms = Array.Empty<Matrix4x4>();

		// Add SDF to Public API region:
		public void RegisterSdfCollider(XpbdSdfCollider src)
		{
			if (!_sdfColliders.Contains(src))
				_sdfColliders.Add(src);
		}

		public void UnregisterSdfCollider(XpbdSdfCollider src)
		{
			_sdfColliders.Remove(src);
		}
		void RebuildSdfBuffers()
		{
			int count = _sdfColliders.Count;
			if (count == 0)
				return;

			// Accumulate total SDF cell count and assign data offsets.
			int totalCells = 0;
			for (int i = 0; i < count; i++)
			{
				var src = _sdfColliders[i];
				if (src.SdfAsset == null || !src.SdfAsset.IsBaked)
					continue;
				src.SdfDataOffset = totalCells;
				totalCells += src.SdfAsset.SdfGrid.Length;
			}
			_sdfTotalCells = totalCells;

			// Reallocate if layout changed.
			bool needRebuild = _sdfDataBuffer == null
							|| _sdfDataBuffer.count != Mathf.Max(totalCells, 1)
							|| _sdfShapesBuffer == null
							|| _sdfShapesBuffer.count != count;

			if (needRebuild)
			{
				ReleaseSdfBuffers();
				// stride = sizeof(SdfShapeDescriptorCPU) = 128 bytes
				_sdfShapesBuffer = new ComputeBuffer(count, 128);
				_sdfTransformBuffer = new ComputeBuffer(count, 64); // Matrix4x4 = 64 bytes
				_sdfDataBuffer = new ComputeBuffer(Mathf.Max(totalCells, 1), sizeof(float));
			}

			// Fill CPU arrays.
			if (_cpuSdfShapes.Length < count)
				_cpuSdfShapes = new SdfShapeDescriptorCPU[count];
			if (_cpuSdfTransforms.Length < count)
				_cpuSdfTransforms = new Matrix4x4[count];

			int cellIdx = 0;
			// Temporary float array for SDF data — only reallocate if total changed.
			var sdfFlat = new float[Mathf.Max(totalCells, 1)];

			for (int i = 0; i < count; i++)
			{
				var src = _sdfColliders[i];
				src.RefreshDescriptor(_subDT);
				var desc = src.Descriptor;

				_cpuSdfShapes[i] = desc;
				_cpuSdfTransforms[i] = src.transform.worldToLocalMatrix;

				if (src.SdfAsset != null && src.SdfAsset.IsBaked)
				{
					var grid = src.SdfAsset.SdfGrid;
					System.Array.Copy(grid, 0, sdfFlat, cellIdx, grid.Length);
					cellIdx += grid.Length;
				}
			}

			_sdfShapesBuffer.SetData(_cpuSdfShapes, 0, 0, count);
			_sdfTransformBuffer.SetData(_cpuSdfTransforms, 0, 0, count);
			if (totalCells > 0)
				_sdfDataBuffer.SetData(sdfFlat, 0, 0, totalCells);
		}

		// ── STEP 7: UploadSdfTransformsForSubstep ────────────────────────────────────
		// Called every substep to update world-to-local matrices (moving SDF colliders).

		void UploadSdfTransformsForSubstep()
		{
			int count = _sdfColliders.Count;
			for (int i = 0; i < count; i++)
			{
				_cpuSdfTransforms[i] = _sdfColliders[i].transform.worldToLocalMatrix;
				_sdfColliders[i].RefreshDescriptor(_subDT);
				_cpuSdfShapes[i] = _sdfColliders[i].Descriptor;
			}
			_sdfShapesBuffer.SetData(_cpuSdfShapes, 0, 0, count);
			_sdfTransformBuffer.SetData(_cpuSdfTransforms, 0, 0, count);
		}

		// ── STEP 8: DispatchSdfDetect ─────────────────────────────────────────────────
		// Called in substep loop in same slot as DispatchDetectShapes, for each body.

		void DispatchSdfDetect(SoftBodyGPUState body)
		{
			if (_sdfColliders.Count == 0 || SdfCollisionCS == null)
				return;

			int count = _sdfColliders.Count;
			SdfCollisionCS.SetInt("_SdfShapeCount", count);
			SdfCollisionCS.SetInt("_SdfParticleCount", body.ParticleCount);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_SdfShapes", _sdfShapesBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_SdfTransforms", _sdfTransformBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_SdfData", _sdfDataBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_Particles", body.ParticleBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_Positions", body.PositionsBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_ColSize", body.ColSizeBuffer);
			SdfCollisionCS.SetBuffer(_kSdfDetect, "_ColConstraints", body.ColConstraintBuffer);

			int threads = (count * body.ParticleCount + 31) / 32;
			SdfCollisionCS.Dispatch(_kSdfDetect, threads, 1, 1);
		}

		// ── STEP 9: ReleaseSdfBuffers ─────────────────────────────────────────────────

		void ReleaseSdfBuffers()
		{
			_sdfDataBuffer?.Release();
			_sdfDataBuffer = null;
			_sdfShapesBuffer?.Release();
			_sdfShapesBuffer = null;
			_sdfTransformBuffer?.Release();
			_sdfTransformBuffer = null;
		}
		#endregion //Sdf_Collider
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
			ReleaseSdfBuffers(); // [3/26/2026 jzq]
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

			bool hasRigid = _colliders.Count > 0 && CollisionShapesCS;
			bool hasSoftSoft = _softSoftPairs.Count > 0 && SoftSoftCollisionCS && SoftSoftApplyCS;
			// [3/26/2026 jzq]
			bool hasSdf = _sdfColliders.Count > 0 && SdfCollisionCS;
			if (hasRigid)
			{
				// Snapshot BEFORE RebuildCollisionBuffers so frameStartPos captures
				// the position from the END of the previous frame — not the current one.
				// RebuildCollisionBuffers calls RefreshDescriptor which updates _prevPos
				// to the current transform.position; if we snapshot after that, both
				// frameStartPos and the lerp target are the same and no sweep occurs.
				foreach (var col in _colliders)
					col.SnapshotStartOfFrame();

				// Recompute adaptive contact skin from the largest registered body.
				UpdateContactSkin();
				RebuildCollisionBuffers(_fixedDT);

				if (_dynSlotCount > 0)
					DispatchClearImpulse();
			}
			// [3/26/2026 jzq]
			if (hasSdf)
				RebuildSdfBuffers();
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
					if (!body)
						continue;
					DispatchPresolve(body);

					// Attachment corrections — runs every substep immediately after
					// this body's Presolve so predict is current. Attachment writes
					// into PositionsBuffer directly (indexed by particle), before
					// constraints read it. GetData stalls the GPU here which is
					// acceptable: small groups only, and correctness requires sync.
					if (_attachmentCallbacks.Count > 0)
					{
						int id = body.GetInstanceID();
						_bodyDeltaBuffers.TryGetValue(id, out var deltaBuffer);
						foreach (var cb in _attachmentCallbacks)
							cb(body, body.State.PositionsBuffer, deltaBuffer, _subDT);
					}
				}

				// Advance colliders to substep-end position.
				if (hasRigid)
				{
					float t = (s + 1f) / SubSteps;
					uint dynSlot = 0;
					foreach (var col in _colliders)
					{
						uint slot = col.Type == XpbdColliderSource.ColType.Dynamic ? dynSlot++ : 0u;
						col.RefreshDescriptorAtFraction(t, _subDT, slot);
					}
					UploadShapesForSubstep();
				}
				if (hasSdf)
					UploadSdfTransformsForSubstep();  /// once per substep, outside body loop
				// Collision pass 1: CCD — before elastic constraints.
				// Detect+solve to push particles out, then WriteCollisionState so
				// ClampDelta can suppress inward elastic deltas in Stretch/Volume.
				if (hasRigid || hasSdf)
				{
					foreach (var body in _bodies)
					{
						if (!body)
							continue;
						DispatchClearCollisionState(body.State);
						ResetColSize(body.State);
						if (hasSdf)
							DispatchSdfDetect(body.State);
						if (hasRigid)
							DispatchDetectShapes(body.State);
						// SolveCollisions consumes _ColConstraints written by BOTH
						// SdfDetect and DetectShapes — must run whenever either fires.
						DispatchShapesSolve(body);
						DispatchWriteCollisionState(body.State);
					}
				}

				// Elastic constraints + Postsolve.
				_softSoftDetectedThisStep.Clear();
				foreach (var body in _bodies)
				{
					if (!body)
						continue;
					DispatchConstraintsAndPostsolve(body);
					if (hasSoftSoft)
						DispatchComputeBounds(body);

				}

				// Collision post-elastic iterations: re-run detect+solve N times.
				// Each iteration catches particles that elastic constraints pushed
				// back inside. CollisionIterations=3 is usually sufficient.
				for (int ci = 0; ci < CollisionIterations; ci++)
				{
					foreach (var body in _bodies)
					{
						if (!body)
							continue;
						if (hasRigid || hasSdf)
						{
							ResetColSize(body.State);
							if (hasSdf)
								DispatchSdfDetect(body.State);
							if (hasRigid)
								DispatchDetectShapes(body.State);
							DispatchShapesSolve(body);
						}
						if (hasSoftSoft && ci == CollisionIterations - 1)
							DispatchSoftSoftPairs(body.State);
					}
				}

				// HardProject: absolute final guarantee — analytic projection of every
				// particle outside every shape. Runs after all elastic constraints.
				// Nothing runs after this in the substep so it cannot be undone.
				if (hasRigid)
				{
					foreach (var body in _bodies)
					{
						if (!body)
							continue;
						DispatchHardProject(body.State);
					}
				}
			}

			foreach (var body in _bodies)
			{
				if (!body)
					continue;
				DispatchDeform(body);
			}

			if (hasRigid && _dynSlotCount > 0)
				ApplyDynamicImpulses();

		}

		// ── Public API ────────────────────────────────────────────────────────

		/// <summary>
		/// Duration of one XPBD substep in seconds.
		/// Equals (1/FixedTimeStepFPS) / SubSteps.
		/// </summary>
		public float SubDT => _subDT;

		/// <summary>
		/// Register a per-substep attachment callback.
		/// Called after each body's Presolve, before constraints.
		/// Arguments: (body, positionsBuffer, deltaBuffer, subDt).
		/// Only called for the body the attachment owns.
		/// </summary>
		public void RegisterAttachment(System.Action<SoftBodyComponent, ComputeBuffer, ComputeBuffer, float> cb)
		{
			if (!_attachmentCallbacks.Contains(cb))
				_attachmentCallbacks.Add(cb);
		}

		public void UnregisterAttachment(System.Action<SoftBodyComponent, ComputeBuffer, ComputeBuffer, float> cb)
		{
			_attachmentCallbacks.Remove(cb);
		}

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
			// Allocate _Delta buffer: StructuredBuffer<float3>, one entry per particle.
			// stride = 3 × sizeof(float) = 12 bytes, type Default (not Raw).
			if (body.State != null && body.State.ParticleCount > 0)
			{
				var delta = new ComputeBuffer(body.State.ParticleCount, 3 * sizeof(float));
				delta.SetData(new float[body.State.ParticleCount * 3]);
				_bodyDeltaBuffers[body.GetInstanceID()] = delta;
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
			if (_bodyDeltaBuffers.TryGetValue(id, out var delta))
			{
				delta?.Release();
				_bodyDeltaBuffers.Remove(id);
			}
		}

		// Compute bounding radius and particle radius from the tetrahedral mesh asset.
		//
		// ParticleRadius is derived from actual mesh edge lengths — not from a
		// spherical approximation. For each particle we compute the mean of all
		// connected edge rest lengths (its Voronoi cell radius in the mesh).
		// We then take the median across all particles — this gives the typical
		// local inter-particle gap size, which is exactly what a collider must
		// be larger than to avoid slipping between particles.
		//
		// This fully utilises the tetrahedral mesh topology and adapts
		// automatically to non-uniform meshes (dense regions, coarse regions).
		void ComputeRestBoundingRadius(SoftBodyComponent body)
		{
			var state = body.State;
			if (state == null || state.ParticleCount == 0)
				return;

			var asset = body.TetMeshAsset;

			// ── Bounding radius from GPU readback (still needed for soft-soft) ──
			int floatsPerParticle = 8;
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

			// ── Particle radius from mesh edge topology ───────────────────────
			if (asset != null && asset.Edges != null && asset.Edges.Length > 0)
			{
				// Accumulate sum of connected edge rest lengths per particle.
				var edgeLenSum = new float[state.ParticleCount];
				var edgeCount = new int[state.ParticleCount];
				foreach (var e in asset.Edges)
				{
					edgeLenSum[e.IndexA] += e.RestLen;
					edgeLenSum[e.IndexB] += e.RestLen;
					edgeCount[e.IndexA]++;
					edgeCount[e.IndexB]++;
				}

				// Per-particle mean edge length = local Voronoi cell radius.
				// Half of this is the radius of the sphere centred at the particle
				// that just touches its nearest neighbor.
				var perParticleR = new float[state.ParticleCount];
				int validCount = 0;
				for (int i = 0; i < state.ParticleCount; i++)
				{
					if (edgeCount[i] == 0)
						continue;
					// Half mean edge length = particle sphere radius so spheres just touch
					perParticleR[validCount++] = (edgeLenSum[i] / edgeCount[i]) * 0.5f;
				}

				// Median gives the typical gap — robust against outlier long boundary edges.
				System.Array.Sort(perParticleR, 0, validCount);
				float medianR = perParticleR[validCount / 2];

				body.ParticleRadius = medianR * ParticleRadiusScale;
			}
			else
			{
				// Fallback: spherical approximation if no edge data.
				float sa = 4f * Mathf.PI * body.BoundingRadius * body.BoundingRadius;
				body.ParticleRadius = Mathf.Sqrt(sa / state.ParticleCount / Mathf.PI) * ParticleRadiusScale;
			}
		}

		public void RegisterCollider(XpbdColliderSource src)
		{
			if (!_colliders.Contains(src))
				_colliders.Add(src);

		}

		public void UnregisterCollider(XpbdColliderSource src)
		{
			_colliders.Remove(src);
		}


		// ── Soft-Soft pair API ────────────────────────────────────────────────

		/// <summary>
		/// Register an explicit soft-soft collision pair.
		/// Order of A/B doesn't matter; the pair is keyed canonically.
		/// </summary>
		public void AddSoftSoftPair(SoftBodyComponent a, SoftBodyComponent b)
		{
			var key = MakePairKey(a, b);
			if (!_softSoftPairs.ContainsKey(key))
			{
				_softSoftPairs[key] = null; // buffers allocated lazily
			}
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
			cs.SetFloat("_ColDeltaTime", _subDT);
			cs.SetFloat("_ContactSkin", _contactSkin);
			cs.SetFloat("_ParticleRadius", _particleRadius);
		}

		// Recompute the adaptive contact skin each frame.
		// Uses the largest bounding radius among all registered soft bodies,
		// scaled by ContactSkinFraction and clamped to [ContactSkinMin, ContactSkinMax].
		// This ensures a 1 cm soft body gets a small skin and a 1 m body gets a
		// proportionally larger one, without manual tuning.
		void UpdateContactSkin()
		{
			float maxExtent = 0f;
			float maxPR = 0f;
			foreach (var body in _bodies)
			{
				if (body != null && body.State != null)
				{
					maxExtent = Mathf.Max(maxExtent, body.BoundingRadius);
					maxPR = Mathf.Max(maxPR, body.ParticleRadius);
				}
			}
			if (maxExtent < 1e-4f)
			{
				_contactSkin = ContactSkinMin;
				_particleRadius = ContactSkinMin;
				return;
			}
			// _particleRadius: must be >= smallest grip radius so even tiny colliders
			// pin enough particles to form a rigid contact patch.
			// Physics: contact stiffness scales with patch area ~4π(R_grip+r_p)².
			// If r_p < R_grip, patch is too small and elastic forces overpower it.
			float minGripR = float.MaxValue;
			foreach (var col in _colliders)
			{
				if (col.Shape == XpbdColliderSource.ShapeType.Sphere)
					minGripR = Mathf.Min(minGripR, col.Descriptor.param0);
			}
			if (minGripR == float.MaxValue)
				minGripR = 0f;
			_particleRadius = Mathf.Max(maxPR, Mathf.Max(minGripR, ContactSkinMin));
			// _contactSkin: speculative detection. NOT inflated by particle radius.
			_contactSkin = Mathf.Clamp(
				maxExtent * ContactSkinFraction, ContactSkinMin, ContactSkinMax);
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
		{
			body.ColSizeBuffer.SetData(new uint[] { 0 });
		}

		void DispatchClearImpulse()
		{
			CollisionShapesCS.SetBuffer(_kClearImpulse, "_ImpulseBytes", _impulseBuffer);
			CollisionShapesCS.Dispatch(_kClearImpulse, Ceil(_dynSlotCount), 1, 1);
		}

		// Re-upload just the shapes and velocity buffers after per-substep interpolation.
		// Does NOT reallocate buffers — only SetData on existing ones.
		void UploadShapesForSubstep()
		{
			if (_shapesBuffer == null)
				return;

			int count = _colliders.Count;
			for (int i = 0; i < count; i++)
				_cpuShapes[i] = _colliders[i].Descriptor;

			for (int i = 0; i < count; i++)
				_cpuVels[i] = _colliders[i].SurfaceVelocity;

			_shapesBuffer.SetData(_cpuShapes, 0, 0, count);
			_shapeVelBuffer.SetData(_cpuVels, 0, 0, count);
			// Re-upload face planes for any convex shapes that moved
			int fpIdx = 0;
			for (int i = 0; i < count; i++)
			{
				var col = _colliders[i];
				if (col.Shape == XpbdColliderSource.ShapeType.Convex && col.FacePlanes != null)
				{
					Array.Copy(col.FacePlanes, 0, _cpuFacePlanes, fpIdx, col.FacePlanes.Length);
					fpIdx += col.FacePlanes.Length;
				}
			}
			if (_totalFacePlanes > 0)
				_meshFacePlanesBuffer.SetData(_cpuFacePlanes, 0, 0, _totalFacePlanes);

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

		void DispatchShapesSolve(SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			var cs   = CollisionShapesCS;
			cs.SetFloat("_ColDeltaTime", _subDT);
			cs.SetFloat("_ContactSkin", _contactSkin);
			cs.SetFloat("_ParticleRadius", _particleRadius);
			cs.SetFloat("_CollisionDeltaWeight", (float) SubSteps);
			cs.SetInt("_ParticleCount", body.ParticleCount);

			// _impulseBuffer is only allocated by RebuildCollisionBuffers when
			// rigid colliders are present. In SDF-only mode it is null, so we
			// allocate a minimal 1-slot dummy so the kernel binding doesn't crash.
			// Dynamic impulse feedback is a no-op in this case (dynSlotCount = 0).
			if (_impulseBuffer == null)
				_impulseBuffer = new ComputeBuffer(IMPULSE_STRIDE / 4, sizeof(uint), ComputeBufferType.Raw);

			cs.SetBuffer(_kShapesSolve, "_ImpulseBytes",   _impulseBuffer);
			cs.SetBuffer(_kShapesSolve, "_Particles",      body.ParticleBuffer);
			cs.SetBuffer(_kShapesSolve, "_Positions",      body.PositionsBuffer);
			cs.SetBuffer(_kShapesSolve, "_ColSize",        body.ColSizeBuffer);
			cs.SetBuffer(_kShapesSolve, "_ColConstraints", body.ColConstraintBuffer);
			cs.SetBuffer(_kShapesSolve, "_CollisionState", body.CollisionStateBuffer);
			if (_bodyDeltaBuffers.TryGetValue(bodyCmp.GetInstanceID(), out var deltaBuffer))
				cs.SetBuffer(_kShapesSolve, "_DeltaRaw", deltaBuffer);
			cs.Dispatch(_kShapesSolve, Ceil(MAX_COLLISION_CONSTRAINTS), 1, 1);
		}

		void DispatchHardProject(SoftBodyGPUState body)
		{
			if (_shapesBuffer == null)
				return;
			var cs = CollisionShapesCS;
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetFloat("_ContactSkin", _contactSkin);
			cs.SetFloat("_ParticleRadius", _particleRadius);
			cs.SetBuffer(_kHardProject, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kHardProject, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kHardProject, "_Shapes", _shapesBuffer);
			cs.SetBuffer(_kHardProject, "_ShapeVelocities", _shapeVelBuffer);
			// [3/26/2026 jzq]
			cs.SetBuffer(_kHardProject, "_MeshFacePlanes", _meshFacePlanesBuffer);
			cs.Dispatch(_kHardProject, Ceil(body.ParticleCount), 1, 1);
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
			if (!_bodyBoundsBuffers.TryGetValue(id, out var buf) || buf == null)
				return;


			// Zero the accumulator
			buf.SetData(new uint[8]);

			var cs = SoftSoftCollisionCS;
			cs.SetInt("_CountA", body.State.ParticleCount);
			cs.SetBuffer(_kComputeBounds, "_PositionsA", body.State.PositionsBuffer);
			cs.SetBuffer(_kComputeBounds, "_BoundsBuf", buf);
			cs.Dispatch(_kComputeBounds, Ceil(body.State.ParticleCount), 1, 1);
		}

		// Reads the bounds buffer (GPU→CPU sync) and returns centroid + circumsphere radius.
		// This is a synchronous readback — cheap because the buffer is only 32 bytes.
		// Returns false if no bounds buffer exists for this body.
		bool ReadLiveBounds(SoftBodyComponent body, out Vector3 centroid, out float radius)
		{
			centroid = Vector3.zero;
			radius = body.BoundingRadius; // fallback to rest-pose

			int id = body.GetInstanceID();
			if (!_bodyBoundsBuffers.TryGetValue(id, out var buf) || buf == null)
				return false;


			buf.GetData(_boundsReadback);

			int count = _boundsReadback[3];
			if (count == 0)
				return false;


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
				float AutoRadius(SoftBodyComponent body)
				{
					return AutoRadiusMul * Mathf.Sqrt(
						4f * Mathf.PI * body.BoundingRadius * body.BoundingRadius
						/ Mathf.Max(body.State.ParticleCount, 1));
				}
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
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA", bufs.DeltaA);
				applyCs.Dispatch(_kApplySoftSoftDelta, Ceil(nA), 1, 1);

				applyCs.SetInt("_CountA", nB);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_ParticlesA", b.State.ParticleBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_PositionsA", b.State.PositionsBuffer);
				applyCs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA", bufs.DeltaB);
				applyCs.Dispatch(_kApplySoftSoftDelta, Ceil(nB), 1, 1);
			}
		}

		// ── Elastic integration (Presolve + Stretch + Volume + Postsolve) ─────
		// Collision is NOT here — it runs after ALL bodies integrate (see Update).
		void DispatchPresolve(SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			var cs = SoftBodySimCS;
			cs.SetFloat("_DeltaTime", _subDT);
			cs.SetFloat("_DistanceCompliance", EdgeCompliance);
			cs.SetFloat("_VolumeCompliance", VolumeCompliance);
			cs.SetFloat("_ContactSkin", _contactSkin);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetInt("_EdgeCount", body.EdgeCount);
			cs.SetInt("_TetCount", body.TetCount);

			BindSimBuffers(cs, _kPresolve, bodyCmp);
			cs.Dispatch(_kPresolve, Ceil(body.ParticleCount), 1, 1);
		}

		void DispatchClearCollisionState(SoftBodyGPUState body)
		{
			var cs = CollisionShapesCS;
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kClearCollisionState, "_CollisionState", body.CollisionStateBuffer);
			cs.Dispatch(_kClearCollisionState, Ceil(body.ParticleCount), 1, 1);
		}

		void DispatchWriteCollisionState(SoftBodyGPUState body)
		{
			var cs = CollisionShapesCS;
			cs.SetBuffer(_kWriteCollisionState, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kWriteCollisionState, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kWriteCollisionState, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kWriteCollisionState, "_ColConstraints", body.ColConstraintBuffer);
			cs.SetBuffer(_kWriteCollisionState, "_CollisionState", body.CollisionStateBuffer);
			cs.Dispatch(_kWriteCollisionState, Ceil(MAX_COLLISION_CONSTRAINTS), 1, 1);
		}

		void DispatchClampDelta(SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			var cs = SoftBodySimCS;
			cs.SetInt("_ParticleCount", body.ParticleCount);
			BindSimBuffers(cs, _kClampDelta, bodyCmp);
			cs.Dispatch(_kClampDelta, Ceil(body.ParticleCount), 1, 1);
		}

		void DispatchConstraintsAndPostsolve(SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			var cs = SoftBodySimCS;

			int stretchKernel = UseExclusionStretch ? _kStretchExclusion : _kStretch;
			int volumeKernel = UseExclusionStretch ? _kVolumeExclusion : _kVolume;
			int excIter = UseExclusionStretch ? ExclusionIterations : 1;
			for (int ei = 0; ei < excIter; ei++)
			{
				// Clear delta before each extra iteration — Presolve already cleared
				// it for the first pass. Without this, corrections double each pass.
				if (ei > 0)
				{
					BindSimBuffers(cs, _kClearDelta, bodyCmp);
					cs.Dispatch(_kClearDelta, Ceil(body.ParticleCount), 1, 1);
				}
				BindSimBuffers(cs, stretchKernel, bodyCmp);
				cs.Dispatch(stretchKernel, Ceil(body.EdgeCount), 1, 1);

				BindSimBuffers(cs, volumeKernel, bodyCmp);
				cs.Dispatch(volumeKernel, Ceil(body.TetCount), 1, 1);

				// Apply this iteration via Postsolve, then restore predict for next pass.
				// Only the last iteration feeds into the final Postsolve below.
			}

			// ClampDelta: zero elastic delta components pointing into the collider.
			// Gives collision hard priority over Stretch/Volume constraints.
			BindSimBuffers(cs, _kClampDelta, bodyCmp);
			cs.Dispatch(_kClampDelta, Ceil(body.ParticleCount), 1, 1);

			BindSimBuffers(cs, _kPostsolve, bodyCmp);
			cs.Dispatch(_kPostsolve, Ceil(body.ParticleCount), 1, 1);
		}

		void BindSimBuffers(ComputeShader cs, int kernel, SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			cs.SetBuffer(kernel, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(kernel, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(kernel, "_Edges", body.EdgeBuffer);
			cs.SetBuffer(kernel, "_Tetrahedrals", body.TetBuffer);
			// Bind _Delta and _DeltaRaw: both point to the same ComputeBuffer.
			// _Delta (StructuredBuffer<float3>) used by Presolve/Postsolve for clear/read.
			// _DeltaRaw (RWByteAddressBuffer) used by constraint kernels for CAS atomic adds.
			if (_bodyDeltaBuffers.TryGetValue(bodyCmp.GetInstanceID(), out var deltaBuffer))
			{
				cs.SetBuffer(kernel, "_Delta", deltaBuffer);
				cs.SetBuffer(kernel, "_DeltaRaw", deltaBuffer);
			}
			if (ReferenceEquals(cs, SoftBodySimCS))
				cs.SetBuffer(kernel, "_CollisionState", bodyCmp.State.CollisionStateBuffer);
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
				cs.SetBuffer(deformKernel, "_OrigIndices", body.OrigIndicesBuffer);

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
			{
				if (b && b.GetInstanceID() == instanceID)
					return b;
			}
			return null;
		}

		static int Ceil(int n)
		{
			return (n + GROUP_SIZE - 1) / GROUP_SIZE;
		}

		static int CeilN(int n, int g)
		{
			return (n + g - 1) / g;
		}


		// ── Kernel ID cache ───────────────────────────────────────────────────

		void CacheKernelIDs()
		{
			_kPresolve = SoftBodySimCS.FindKernel("Presolve");
			_kPostsolve = SoftBodySimCS.FindKernel("Postsolve");
			_kStretch = SoftBodySimCS.FindKernel("StretchConstraint");
			_kVolume = SoftBodySimCS.FindKernel("VolumeConstraint");
			_kClearDelta = SoftBodySimCS.FindKernel("ClearDelta");
			_kClampDelta = SoftBodySimCS.FindKernel("ClampDelta");
			_kStretchExclusion = SoftBodySimCS.FindKernel("StretchConstraintWithExclusion");
			_kVolumeExclusion = SoftBodySimCS.FindKernel("VolumeConstraintWithExclusion");
			_kWriteCollisionState = CollisionShapesCS.FindKernel("WriteCollisionState");
			_kClearCollisionState = CollisionShapesCS.FindKernel("ClearCollisionState");
			_kHardProject = CollisionShapesCS.FindKernel("HardProjectParticles");

			_kClearImpulse = CollisionShapesCS.FindKernel("ClearImpulseAccum");
			_kDetectShapes = CollisionShapesCS.FindKernel("DetectShapes");
			_kShapesSolve = CollisionShapesCS.FindKernel("SolveCollisions");

			if (SoftSoftCollisionCS)
			{
				_kComputeBounds = SoftSoftCollisionCS.FindKernel("ComputeBounds");
				_kClearSoftSoftDelta = SoftSoftCollisionCS.FindKernel("ClearSoftSoftDelta");
				_kDetectSoftSoft = SoftSoftCollisionCS.FindKernel("DetectSoftSoft");
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
			// [3/26/2026 jzq]
			if (SdfCollisionCS)
				_kSdfDetect = SdfCollisionCS.FindKernel("SdfDetect");
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
