// SoftBodySimulationManager.cs
// Main simulation manager — orchestrates all compute dispatches each frame.
//
// COLLISION — two independent compute shaders, each only declaring the buffers
// they need (Unity validates every file-scope buffer on every kernel dispatch,
// so mixing Phase-1 and Phase-2 buffers in one file forces dummy bindings):
//
//   CollisionFloorCS   (Collision_Floor.compute)
//     DetectCollisions — Möller–Trumbore triangle soup (static floor mesh)
//     SolveCollisions  — direct position projection, no ImpulseBytes
//
//   CollisionShapesCS  (Collision_Shapes.compute)
//     ClearImpulseAccum — zeros per-dynamic-slot accumulators
//     DetectShapes      — analytic Sphere / OBB / Capsule
//     SolveCollisions   — mass-weighted projection + Newton-3rd impulse readback
//
// Both detection kernels append into the SAME _ColConstraints / _ColSize buffers
// on each soft body, so the corresponding SolveCollisions sees all contacts from
// both phases in one dispatch.
//
// Test path: assign only CollisionFloorCS + CollisionMesh — Phase 2 is skipped
// entirely unless at least one XpbdColliderSource is registered.


// Central XPBD simulation manager. All collision goes through XpbdColliderSource
// (Sphere / OBB / Capsule / Cylinder / Convex). No static floor mesh.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	[DefaultExecutionOrder(-100)] //For Instance init [3/11/2026 jzq]
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
		const int IMPULSE_STRIDE = 32; // bytes, must match Collision_Shapes.compute

		// ── Inspector ─────────────────────────────────────────────────────────
		[Header("Compute Shaders")]
		public ComputeShader SoftBodySimCS;
		public ComputeShader CollisionShapesCS;
		public ComputeShader DeformCS;

		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		// ── Bodies ────────────────────────────────────────────────────────────
		readonly List<SoftBodyComponent> _bodies = new();
		readonly List<XpbdColliderSource> _colliders = new();

		// ── Collision GPU buffers ─────────────────────────────────────────────
		ComputeBuffer _shapesBuffer;       // ShapeDescriptor[]  (64 b each)
		ComputeBuffer _shapeVelBuffer;     // float3[]
		ComputeBuffer _meshFacePlanesBuffer; // float4[]  — all convex meshes packed
		ComputeBuffer _impulseBuffer;      // RWByteAddressBuffer

		int _shapeCount;
		int _dynSlotCount;
		int _totalFacePlanes;

		// CPU staging — grown lazily, never shrunk
		ShapeDescriptorCPU[] _cpuShapes = Array.Empty<ShapeDescriptorCPU>();
		Vector3[] _cpuVels = Array.Empty<Vector3>();
		Vector4[] _cpuFacePlanes = Array.Empty<Vector4>();
		byte[] _cpuImpulseReadback = Array.Empty<byte>();

		// ── Kernel IDs ────────────────────────────────────────────────────────
		int _kClearImpulse, _kDetectShapes, _kShapesSolve;
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

			bool hasCollision = _colliders.Count > 0 && CollisionShapesCS;

			if (hasCollision)
			{
				RebuildCollisionBuffers(_fixedDT);
				if (_dynSlotCount > 0)
					DispatchClearImpulse();
			}

			foreach (var body in _bodies)
			{
				if (!body)
					continue;
				for (int s = 0; s < SubSteps; s++)
					DispatchSubstep(body.State, hasCollision);
				DispatchDeform(body);
			}

			if (hasCollision && _dynSlotCount > 0)
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

		// ── Collision buffer management ───────────────────────────────────────

		void RebuildCollisionBuffers(float dt)
		{
			int count = _colliders.Count;
			_shapeCount = count;
			_dynSlotCount = 0;

			// First pass: assign dynamic slots, set FacePlanesOffset for convex shapes,
			// call RefreshDescriptor so FacePlanes[] is current world-space.
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

			// Grow CPU arrays
			if (_cpuShapes.Length < count)
				_cpuShapes = new ShapeDescriptorCPU[count];
			if (_cpuVels.Length < count)
				_cpuVels = new Vector3[count];
			if (_cpuFacePlanes.Length < _totalFacePlanes)
				_cpuFacePlanes = new Vector4[Mathf.Max(_totalFacePlanes, 1)];

			// Second pass: fill CPU arrays
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

			// Reallocate GPU buffers when counts change
			bool needRebuild = _shapesBuffer == null
							|| _shapesBuffer.count != count
							|| _meshFacePlanesBuffer == null
							|| _meshFacePlanesBuffer.count != Mathf.Max(_totalFacePlanes, 1);

			if (needRebuild)
			{
				ReleaseCollisionBuffers();
				_shapesBuffer = new ComputeBuffer(count, 64);
				_shapeVelBuffer = new ComputeBuffer(count, 3 * sizeof(float));
				_meshFacePlanesBuffer = new ComputeBuffer(Mathf.Max(_totalFacePlanes, 1),
														   4 * sizeof(float));
				int iBytes = Mathf.Max(1, _dynSlotCount) * IMPULSE_STRIDE;
				_impulseBuffer = new ComputeBuffer(iBytes / 4, sizeof(uint),
														   ComputeBufferType.Raw);
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

		// ── Collision dispatches ──────────────────────────────────────────────

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

		// ── Physics substep ───────────────────────────────────────────────────

		void DispatchSubstep(SoftBodyGPUState body, bool hasCollision)
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

			// Detect every substep with fresh predicted positions so contact
			// points are never stale. Reusing stale constraints across substeps
			// causes Postsolve to produce a large oscillating velocity
			// (velocity = (corrected_predict - old_position) / subDT) that
			// resonates with the constraint correction each substep → explosion.
			if (hasCollision)
			{
				ResetColSize(body);
				DispatchDetectShapes(body);
				DispatchShapesSolve(body);
			}

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

			_kDirectDeform = DeformCS.FindKernel("DirectDeform");
			_kTetDeform = DeformCS.FindKernel("TetDeform");
			_kRecalcNormals = DeformCS.FindKernel("RecalcNormals");
			_kNormalizeNormals = DeformCS.FindKernel("NormalizeNormals");
		}

		static int Ceil(int n) => (n + GROUP_SIZE - 1) / GROUP_SIZE;
	}
}
