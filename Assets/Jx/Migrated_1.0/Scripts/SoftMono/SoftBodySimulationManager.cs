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
		const int IMPULSE_STRIDE = 32; // bytes — must match Collision_Shapes.compute

		// ── Inspector: Compute shaders ────────────────────────────────────────
		[Header("Compute Shaders")]
		public ComputeShader SoftBodySimCS;
		public ComputeShader CollisionFloorCS;   // Collision_Floor.compute
		public ComputeShader CollisionShapesCS;  // Collision_Shapes.compute (optional)
		public ComputeShader DeformCS;

		// ── Inspector: Static floor ───────────────────────────────────────────
		[Header("Static Floor Collision Mesh")]
		public Mesh CollisionMesh;

		// ── Inspector: Simulation ─────────────────────────────────────────────
		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		// ── Bodies ────────────────────────────────────────────────────────────
		readonly List<SoftBodyComponent> _bodies = new();

		// ── Phase 1: static floor ─────────────────────────────────────────────
		ComputeBuffer _colPositionsBuffer;
		ComputeBuffer _colIndicesBuffer;
		int _colTriCount;

		// ── Phase 2: primitive colliders (XpbdColliderSource) ─────────────────
		readonly List<XpbdColliderSource> _colliders = new();
		ComputeBuffer _shapesBuffer;
		ComputeBuffer _shapeVelBuffer;
		ComputeBuffer _impulseBuffer;
		int _shapeCount;
		int _dynSlotCount;
		ShapeDescriptorCPU[] _cpuShapes = Array.Empty<ShapeDescriptorCPU>();
		Vector3[] _cpuVels = Array.Empty<Vector3>();
		byte[] _cpuImpulseReadback = Array.Empty<byte>();

		// ── Kernel IDs — Floor ────────────────────────────────────────────────
		int _kFloorDetect, _kFloorSolve;

		// ── Kernel IDs — Shapes ───────────────────────────────────────────────
		int _kClearImpulse, _kDetectShapes, _kShapesSolve;

		// ── Kernel IDs — Sim / Deform ─────────────────────────────────────────
		int _kPresolve, _kPostsolve, _kStretch, _kVolume;
		int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

		float _timeAccum, _fixedDT, _subDT;
		//MaterialPropertyBlock _mpb;

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
			UploadCollisionMesh();
		}

		void OnDestroy()
		{
			foreach (var b in _bodies)
				Destroy(b);

			_colPositionsBuffer?.Release();
			_colIndicesBuffer?.Release();
			ReleaseShapeBuffers();
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

			bool hasFloor = _colTriCount > 0;
			bool hasShapes = _colliders.Count > 0 && CollisionShapesCS != null;

			// ── Phase 2: refresh descriptors + rebuild GPU buffers ─────────
			if (hasShapes)
				RebuildShapeBuffers(_fixedDT);

			// ── Clear dynamic impulse slots before detection ───────────────
			if (hasShapes && _dynSlotCount > 0)
				DispatchClearImpulse();

			// ── Collision detection (once per fixed step) ──────────────────
			foreach (var body in _bodies)
			{
				if (!body)
					continue;
				var st = body.State;
				ResetColSize(st);
				if (hasFloor)
					DispatchFloorDetect(st);
				if (hasShapes)
					DispatchShapesDetect(st);
			}

			// ── Physics substeps ───────────────────────────────────────────
			foreach (var body in _bodies)
			{
				if (!body)
					continue;
				for (int s = 0; s < SubSteps; s++)
					DispatchSubstep(body.State, hasFloor, hasShapes);
				DispatchDeform(body);
			}

			// ── Apply rigidbody impulses ───────────────────────────────────
			if (hasShapes && _dynSlotCount > 0)
				ApplyDynamicImpulses();
		}

		// ─────────────────────────────────────────────────────────────────────
		// Public API
		// ─────────────────────────────────────────────────────────────────────

		public void AddBody(SoftBodyComponent body) => _bodies.Add(body);
		public void RemoveBody(SoftBodyComponent body) => _bodies.Remove(body);

		public void RegisterCollider(XpbdColliderSource src)
		{
			if (!_colliders.Contains(src))
				_colliders.Add(src);
		}

		public void UnregisterCollider(XpbdColliderSource src)
			=> _colliders.Remove(src);

		// ─────────────────────────────────────────────────────────────────────
		// Phase 1 — Floor collision
		// ─────────────────────────────────────────────────────────────────────

		void ResetColSize(SoftBodyGPUState body)
			=> body.ColSizeBuffer.SetData(new uint[] { 0 });

		void DispatchFloorDetect(SoftBodyGPUState body)
		{
			var cs = CollisionFloorCS;
			cs.SetFloat("_ColDeltaTime", _fixedDT);
			cs.SetInt("_TriCount", _colTriCount);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kFloorDetect, "_ColPositions", _colPositionsBuffer);
			cs.SetBuffer(_kFloorDetect, "_TriIndices", _colIndicesBuffer);
			cs.SetBuffer(_kFloorDetect, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kFloorDetect, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kFloorDetect, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kFloorDetect, "_ColConstraints", body.ColConstraintBuffer);
			cs.Dispatch(_kFloorDetect, Ceil(_colTriCount), 1, 1);
		}

		void DispatchFloorSolve(SoftBodyGPUState body)
		{
			var cs = CollisionFloorCS;
			cs.SetFloat("_ColDeltaTime", _fixedDT);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kFloorSolve, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kFloorSolve, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kFloorSolve, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kFloorSolve, "_ColConstraints", body.ColConstraintBuffer);
			cs.Dispatch(_kFloorSolve, Ceil(MAX_COLLISION_CONSTRAINTS), 1, 1);
		}

		// ─────────────────────────────────────────────────────────────────────
		// Phase 2 — Primitive shape collision
		// ─────────────────────────────────────────────────────────────────────

		void RebuildShapeBuffers(float dt)
		{
			int count = _colliders.Count;
			_shapeCount = count;
			_dynSlotCount = 0;
			foreach (var c in _colliders)
				if (c.Type == XpbdColliderSource.ColType.Dynamic)
					_dynSlotCount++;

			if (_cpuShapes.Length < count)
				_cpuShapes = new ShapeDescriptorCPU[count];
			if (_cpuVels.Length < count)
				_cpuVels = new Vector3[count];

			uint dynSlot = 0;
			for (int i = 0; i < count; i++)
			{
				var col = _colliders[i];
				uint slot = col.Type == XpbdColliderSource.ColType.Dynamic ? dynSlot++ : 0u;
				col.RefreshDescriptor(dt, slot);
				_cpuShapes[i] = col.Descriptor;
				_cpuVels[i] = col.SurfaceVelocity;
			}

			if (_shapesBuffer == null || _shapesBuffer.count != count)
			{
				ReleaseShapeBuffers();
				_shapesBuffer = new ComputeBuffer(count, 64);
				_shapeVelBuffer = new ComputeBuffer(count, 3 * sizeof(float));
				int iBytes = Mathf.Max(1, _dynSlotCount) * IMPULSE_STRIDE;
				_impulseBuffer = new ComputeBuffer(iBytes / 4, sizeof(uint),
													ComputeBufferType.Raw);
				_cpuImpulseReadback = new byte[iBytes];
			}

			_shapesBuffer.SetData(_cpuShapes, 0, 0, count);
			_shapeVelBuffer.SetData(_cpuVels, 0, 0, count);

			var cs = CollisionShapesCS;
			cs.SetInt("_ShapeCount", _shapeCount);
			cs.SetInt("_DynSlotCount", _dynSlotCount);
			cs.SetFloat("_ColDeltaTime", _fixedDT);
		}

		void ReleaseShapeBuffers()
		{
			_shapesBuffer?.Release();
			_shapesBuffer = null;
			_shapeVelBuffer?.Release();
			_shapeVelBuffer = null;
			_impulseBuffer?.Release();
			_impulseBuffer = null;
		}

		void DispatchClearImpulse()
		{
			var cs = CollisionShapesCS;
			cs.SetBuffer(_kClearImpulse, "_ImpulseBytes", _impulseBuffer);
			cs.Dispatch(_kClearImpulse, Ceil(_dynSlotCount), 1, 1);
		}

		void DispatchShapesDetect(SoftBodyGPUState body)
		{
			var cs = CollisionShapesCS;
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetBuffer(_kDetectShapes, "_Shapes", _shapesBuffer);
			cs.SetBuffer(_kDetectShapes, "_ShapeVelocities", _shapeVelBuffer);
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

		// ─────────────────────────────────────────────────────────────────────
		// Physics substep
		// ─────────────────────────────────────────────────────────────────────

		void DispatchSubstep(SoftBodyGPUState body, bool hasFloor, bool hasShapes)
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

			// Each active phase runs its own SolveCollisions kernel.
			// Order: floor first (static, no velocity field), then shapes
			// (may carry surface velocity for kinematic/dynamic).
			if (hasFloor)
				DispatchFloorSolve(body);
			if (hasShapes)
				DispatchShapesSolve(body);

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

		// ─────────────────────────────────────────────────────────────────────
		// Mesh deformation
		// ─────────────────────────────────────────────────────────────────────

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

		// ─────────────────────────────────────────────────────────────────────
		// Static floor upload
		// ─────────────────────────────────────────────────────────────────────

		void UploadCollisionMesh()
		{
			if (CollisionMesh == null)
			{
				Debug.LogWarning("[XPBD] No CollisionMesh — static floor disabled.");
				return;
			}
			var verts = CollisionMesh.vertices;
			var tris = CollisionMesh.triangles;
			_colTriCount = tris.Length / 3;

			_colPositionsBuffer = new ComputeBuffer(verts.Length, 3 * sizeof(float));
			_colPositionsBuffer.SetData(verts);
			_colIndicesBuffer = new ComputeBuffer(tris.Length, sizeof(uint));
			_colIndicesBuffer.SetData(Array.ConvertAll(tris, x => (uint) x));
		}

		// ─────────────────────────────────────────────────────────────────────
		// Kernel ID cache
		// ─────────────────────────────────────────────────────────────────────

		void CacheKernelIDs()
		{
			_kPresolve = SoftBodySimCS.FindKernel("Presolve");
			_kPostsolve = SoftBodySimCS.FindKernel("Postsolve");
			_kStretch = SoftBodySimCS.FindKernel("StretchConstraint");
			_kVolume = SoftBodySimCS.FindKernel("VolumeConstraint");

			_kFloorDetect = CollisionFloorCS.FindKernel("DetectCollisions");
			_kFloorSolve = CollisionFloorCS.FindKernel("SolveCollisions");

			if (CollisionShapesCS != null)
			{
				_kClearImpulse = CollisionShapesCS.FindKernel("ClearImpulseAccum");
				_kDetectShapes = CollisionShapesCS.FindKernel("DetectShapes");
				_kShapesSolve = CollisionShapesCS.FindKernel("SolveCollisions");
			}

			_kDirectDeform = DeformCS.FindKernel("DirectDeform");
			_kTetDeform = DeformCS.FindKernel("TetDeform");
			_kRecalcNormals = DeformCS.FindKernel("RecalcNormals");
			_kNormalizeNormals = DeformCS.FindKernel("NormalizeNormals");
		}

		static int Ceil(int n) => (n + GROUP_SIZE - 1) / GROUP_SIZE;
	}
}
