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
		public ComputeShader SoftSoftCollisionCS; // SoftSoftCollision.compute
		public ComputeShader DeformCS;

		// ── Inspector: Simulation ─────────────────────────────────────────────
		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		// ── Inspector: Soft-Soft Collision ────────────────────────────────────
		[Header("Soft-Soft Collision")]
		[Tooltip("Fallback contact-distance threshold used when a body does not " +
				 "set its own SoftSoftParticleRadius. Roughly equal to the mean " +
				 "spacing between physics particles.")]
		public float SoftSoftDefaultRadius = 0.05f;

		// ── Bodies and rigid colliders ─────────────────────────────────────────
		readonly List<SoftBodyComponent> _bodies = new();
		readonly List<XpbdColliderSource> _colliders = new();

		// ── Soft-soft pair registry ───────────────────────────────────────────
		// Canonical key: (lower instanceID, higher instanceID) so (A,B)==(B,A).
		// Value is null until first EnsureSoftSoftPairBuffers allocates it.
		readonly Dictionary<(int, int), SoftSoftPairBuffers> _softSoftPairs = new();
		// Tracks which pairs have already run Clear+Detect in the current substep.
		// Prevents the second body in a pair from re-clearing and re-detecting,
		// which would erase the corrections accumulated for the first body's Apply.
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
		int _kClearSoftSoftDelta, _kDetectSoftSoft, _kApplySoftSoftDelta;
		int _kPresolve, _kPostsolve, _kStretch, _kVolume;
		int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

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

			bool hasRigid = _colliders.Count > 0 && CollisionShapesCS;
			bool hasSoftSoft = _softSoftPairs.Count > 0 && SoftSoftCollisionCS;

			if (hasRigid)
			{
				RebuildCollisionBuffers(_fixedDT);
				if (_dynSlotCount > 0)
					DispatchClearImpulse();
			}

			if (hasSoftSoft)
				EnsureSoftSoftPairBuffers();

			for (int s = 0; s < SubSteps; s++)
			{
				// Reset per-substep detect set so Clear+Detect runs exactly once per pair.
				_softSoftDetectedThisStep.Clear();
				foreach (var body in _bodies)
				{
					if (!body)
						continue;
					DispatchSubstep(body.State, hasRigid, hasSoftSoft);
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

		public void AddBody(SoftBodyComponent body) => _bodies.Add(body);
		public void RemoveBody(SoftBodyComponent body) => _bodies.Remove(body);

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
		/// pairs according to their CollisionLayer / CollisionMask bitmasks.
		/// Two bodies collide iff each one's mask includes the other's layer.
		/// CollisionMask = 0 means "no layer-based collision" for that body.
		/// Call this after adding bodies or changing their layer/mask values.
		/// </summary>
		public void RebuildLayerPairs()
		{
			for (int i = 0; i < _bodies.Count; i++)
			{
				var a = _bodies[i];
				if (!a || a.CollisionMask == 0)
					continue;
				for (int j = i + 1; j < _bodies.Count; j++)
				{
					var b = _bodies[j];
					if (!b || b.CollisionMask == 0)
						continue;
					bool aSeesB = (a.CollisionMask & (1 << b.CollisionLayer)) != 0;
					bool bSeesA = (b.CollisionMask & (1 << a.CollisionLayer)) != 0;
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

		// ── Soft-Soft collision dispatches ────────────────────────────────────

		// Called inside DispatchSubstep for the current body.
		// Iterates all registered pairs that involve this body and runs
		//   Clear → Detect → Apply
		// for that pair's side.
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

				bool isA = a.State == bodyState;
				bool isB = b.State == bodyState;
				if (!isA && !isB)
					continue;

				// Contact radius = max of the two bodies' per-particle radii
				// (or the global default if a body leaves theirs at 0).
				float radius = Mathf.Max(
					a.SoftSoftParticleRadius > 0 ? a.SoftSoftParticleRadius : SoftSoftDefaultRadius,
					b.SoftSoftParticleRadius > 0 ? b.SoftSoftParticleRadius : SoftSoftDefaultRadius);

				int nA = a.State.ParticleCount;
				int nB = b.State.ParticleCount;

				// ── Clear + Detect (once per pair per substep) ──────────────────
				// _softSoftDetectedThisStep is cleared once per substep in Update.
				// The first body to reach this pair runs Clear+Detect.
				// The second body skips straight to Apply, reading the shared delta buffer.
				var pairKey = kvp.Key;
				if (!_softSoftDetectedThisStep.Contains(pairKey))
				{
					_softSoftDetectedThisStep.Add(pairKey);

					// Clear A's delta buffer
					cs.SetInt("_CountA", nA);
					cs.SetBuffer(_kClearSoftSoftDelta, "_DeltaBufA", bufs.DeltaA);
					cs.Dispatch(_kClearSoftSoftDelta, Ceil(nA), 1, 1);
					// Clear B's delta buffer
					cs.SetInt("_CountA", nB);
					cs.SetBuffer(_kClearSoftSoftDelta, "_DeltaBufA", bufs.DeltaB);
					cs.Dispatch(_kClearSoftSoftDelta, Ceil(nB), 1, 1);

					// Detect: 2D dispatch over CountA x CountB particles
					cs.SetInt  ("_CountA",    nA);
					cs.SetInt  ("_CountB",    nB);
					cs.SetFloat("_ColRadius", radius);
					cs.SetBuffer(_kDetectSoftSoft, "_ParticlesA", a.State.ParticleBuffer);
					cs.SetBuffer(_kDetectSoftSoft, "_PositionsA", a.State.PositionsBuffer);
					cs.SetBuffer(_kDetectSoftSoft, "_DeltaBufA",  bufs.DeltaA);
					cs.SetBuffer(_kDetectSoftSoft, "_ParticlesB", b.State.ParticleBuffer);
					cs.SetBuffer(_kDetectSoftSoft, "_PositionsB", b.State.PositionsBuffer);
					cs.SetBuffer(_kDetectSoftSoft, "_DeltaBufB",  bufs.DeltaB);
					cs.Dispatch(_kDetectSoftSoft, CeilN(nA, 8), CeilN(nB, 8), 1);
				}

				// ── Apply this body's side ────────────────────────────────────
				// ApplySoftSoftDelta reuses _DeltaBufA + _PositionsA + _CountA.
				if (isA)
				{
					cs.SetInt("_CountA", nA);
					cs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA", bufs.DeltaA);
					cs.SetBuffer(_kApplySoftSoftDelta, "_PositionsA", a.State.PositionsBuffer);
					cs.Dispatch(_kApplySoftSoftDelta, Ceil(nA), 1, 1);
				}
				if (isB)
				{
					cs.SetInt("_CountA", nB);
					cs.SetBuffer(_kApplySoftSoftDelta, "_DeltaBufA", bufs.DeltaB);
					cs.SetBuffer(_kApplySoftSoftDelta, "_PositionsA", b.State.PositionsBuffer);
					cs.Dispatch(_kApplySoftSoftDelta, Ceil(nB), 1, 1);
				}
			}
		}

		// ── Physics substep ───────────────────────────────────────────────────

		void DispatchSubstep(SoftBodyGPUState body, bool hasRigid, bool hasSoftSoft)
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

			// Detect every substep with fresh predicted positions.
			// (Stale constraints cause resonant velocity oscillation → explosion.)
			if (hasRigid)
			{
				ResetColSize(body);
				DispatchDetectShapes(body);
				DispatchShapesSolve(body);
			}

			// Soft-soft: detect + apply for all pairs involving this body.
			if (hasSoftSoft)
				DispatchSoftSoftPairs(body);

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
				_kClearSoftSoftDelta = SoftSoftCollisionCS.FindKernel("ClearSoftSoftDelta");
				_kDetectSoftSoft = SoftSoftCollisionCS.FindKernel("DetectSoftSoft");
				_kApplySoftSoftDelta = SoftSoftCollisionCS.FindKernel("ApplySoftSoftDelta");
			}

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
