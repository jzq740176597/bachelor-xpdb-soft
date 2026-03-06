// SoftBodySimulationManager.cs
// Main simulation manager — orchestrates all compute dispatches each frame.
// Directly mirrors the Renderer::computePhysics / detectCollisions /
// deformMesh sequence from Renderer.cpp, plus the fixed-timestep loop
// with configurable substeps.
//
// Attach to an empty GameObject in the scene.
// Assign all ComputeShader assets and the collision mesh in the Inspector.

// FIXES vs original:
//   FIX 1: _DeltaInt  → _DeltaBytes  (RWByteAddressBuffer, true float atomics)
//          _NormalInt → _NormalBytes  (same)
//   FIX 2: Lines 278-285 — removed per-frame Vector3[] allocation.
//          GetData now writes into body.ReadbackPos / body.ReadbackNrm
//          which are allocated once in SoftBodyGPUState.Init.
//   FIX 3: ClearIntBuffer CPU call removed — Presolve kernel zeroes its own
//          particle's delta slots in-shader (one thread per particle, no race).
//          NormalBytes cleared via body.ClearNormalBytes() which reuses
//          the pre-allocated _zeroNormalBytes byte array.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	[DisallowMultipleComponent]
	public class SoftBodySimulationManager : MonoBehaviour
	{
		// ── Constants (matching original Renderer.h) ──────────────────────────
		public const int MAX_COLLISION_CONSTRAINTS = 10000;
		const int GROUP_SIZE = 32;

		// ── Inspector: Compute shaders ────────────────────────────────────────
		[Header("Compute Shaders")]
		public ComputeShader SoftBodySimCS;   // SoftBodySim.compute
		public ComputeShader CollisionCS;     // Collision.compute
		public ComputeShader DeformCS;        // Deform.compute

		// ── Inspector: Collision geometry ────────────────────────────────────
		[Header("Collision Mesh (Floor / Static)")]
		public Mesh CollisionMesh;

		// ── Inspector: Simulation parameters (matching ImGui sliders) ─────────
		[Header("Simulation")]
		[Range(10, 240)] public int FixedTimeStepFPS = 60;
		[Range(1, 25)] public int SubSteps = 20;
		[Range(0f, 1f)] public float EdgeCompliance = 0.01f;
		[Range(0f, 1f)] public float VolumeCompliance = 0.0f;

		// ── Inspector: Rendering ──────────────────────────────────────────────
		//[Header("Rendering")]
		//public Material SoftBodyMaterial;       // SoftBodyPBR.shader instance
		//public Light DirectionalLight;       // used to compute light matrix
		//public float ShadowOrthoSize = 15f;
		//public float ShadowLightDist = 15f;

		// ── Private state ─────────────────────────────────────────────────────
		readonly List</*SoftBodyGPUState*/SoftBodyComponent> _bodies = new();

		// Collision geometry buffers (static — uploaded once)
		ComputeBuffer _colPositionsBuffer;
		ComputeBuffer _colIndicesBuffer;
		int _colTriCount;

		// Kernel IDs — cached at startup
		int _kPresolve, _kPostsolve, _kStretch, _kVolume;
		int _kDetect, _kSolve;
		int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

		// Fixed-timestep accumulator (matches Timer::passedFixedDT)
		float _timeAccum, _fixedDT, _subDT;

		// ── MaterialPropertyBlock per body ────────────────────────────────────
		MaterialPropertyBlock _mpb;

		// ─────────────────────────────────────────────────────────────────────
		void Awake()
		{
			CacheKernelIDs();
			UploadCollisionMesh();
			_mpb = new MaterialPropertyBlock();
		}

		void OnDestroy()
		{
			foreach (var b in _bodies)
			{
				Destroy(b);
				//b.Dispose();
			}
			//_bodies.Clear();
			_colPositionsBuffer?.Release();
			_colIndicesBuffer?.Release();
		}

		// ─────────────────────────────────────────────────────────────────────
		void Update()
		{
			// Recalculate fixed dt in case Inspector values changed
			_fixedDT = 1f / FixedTimeStepFPS;
			_subDT = _fixedDT / SubSteps;

			_timeAccum += Time.deltaTime;

			// Update light matrix on shader each frame (matches renderImGui lightDir update)
			//UpdateLightMatrix();

			if (_timeAccum >= _fixedDT)
			{
				_timeAccum -= _fixedDT;

				// ── Collision detection (once per fixed step, before substeps) ──
				foreach (var body in _bodies)
				{
					if (!body/*.Active*/)
						continue;
					ResetColSize(body.State);
					DispatchDetectCollisions(body.State);
				}

				// ── Physics substeps ───────────────────────────────────────────
				foreach (var body in _bodies)
				{
					if (!body/*.Active*/)
						continue;
					for (int s = 0; s < SubSteps; s++)
						DispatchPhysicsSubstep(body.State);

					// ── Mesh deformation (once per fixed step) ─────────────────
					DispatchDeform(body);
				}
			}

			// ── Draw bodies every render frame ────────────────────────────────
			//DrawAllBodies();
		}

		// ─────────────────────────────────────────────────────────────────────
		// Public API
		// ─────────────────────────────────────────────────────────────────────

		/// <summary>Register a new soft body for simulation.</summary>
		public void AddBody(/*SoftBodyGPUState*/SoftBodyComponent body)
		{
			_bodies.Add(body);
		}

		/// <summary>Remove and dispose a soft body.</summary>
		public void RemoveBody(/*SoftBodyGPUState*/SoftBodyComponent body)
		{
			//body.Dispose();
			_bodies.Remove(body);
		}

		// ─────────────────────────────────────────────────────────────────────
		// Dispatch: Collision Detection
		// Mirrors Renderer::detectCollisions()
		// ─────────────────────────────────────────────────────────────────────
		void ResetColSize(SoftBodyGPUState body)
			=> body.ColSizeBuffer.SetData(new uint[] { 0 });

		void DispatchDetectCollisions(SoftBodyGPUState body)
		{
			var cs = CollisionCS;
			cs.SetFloat("_ColDeltaTime", _fixedDT);
			cs.SetInt("_TriCount", _colTriCount);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetInt("_EdgeCount", body.EdgeCount);
			cs.SetInt("_TetCount", body.TetCount);

			cs.SetBuffer(_kDetect, "_ColPositions", _colPositionsBuffer);
			cs.SetBuffer(_kDetect, "_TriIndices", _colIndicesBuffer);
			cs.SetBuffer(_kDetect, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(_kDetect, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(_kDetect, "_ColSize", body.ColSizeBuffer);
			cs.SetBuffer(_kDetect, "_ColConstraints", body.ColConstraintBuffer);
			//cs.SetBuffer(_kDetect, "_DeltaInt",        body.DeltaIntBuffer);

			cs.Dispatch(_kDetect, Ceil(_colTriCount), 1, 1);
			// NOTE: Original dispatches over triCount; inner loop walks particles.
			// Kept identical to preserve collision correctness.
		}

		// ─────────────────────────────────────────────────────────────────────
		// Dispatch: Physics Substep
		// Mirrors Renderer::computePhysics() — one substep iteration.
		// Dispatch order: Presolve → ColConstraint → Stretch → Volume → Postsolve
		// Barriers: Unity inserts UAV hazard barriers automatically between
		//           Dispatch calls on ComputeBuffers (DX11/DX12 behavior).
		// ─────────────────────────────────────────────────────────────────────
		void DispatchPhysicsSubstep(SoftBodyGPUState body)
		{
			var cs = SoftBodySimCS;

			// Shared per-call constants
			cs.SetFloat("_DeltaTime", _subDT);
			cs.SetFloat("_DistanceCompliance", EdgeCompliance);
			cs.SetFloat("_VolumeCompliance", VolumeCompliance);
			cs.SetInt("_ParticleCount", body.ParticleCount);
			cs.SetInt("_EdgeCount", body.EdgeCount);
			cs.SetInt("_TetCount", body.TetCount);

			// FIX 3: no CPU ClearIntBuffer call.
			// Presolve kernel zeroes DeltaBytes for its own particle in-shader.
			// (One thread per particle → no race on the Store calls.)

			// ── Presolve ──────────────────────────────────────────────────────
			BindPhysicsBuffers(cs, _kPresolve, body);
			cs.Dispatch(_kPresolve, Ceil(body.ParticleCount), 1, 1);

			// ── Collision constraint solve ─────────────────────────────────────
			var colCS = CollisionCS;
			colCS.SetFloat("_ColDeltaTime", _fixedDT);
			colCS.SetInt("_TriCount", _colTriCount);
			colCS.SetInt("_ParticleCount", body.ParticleCount);
			colCS.SetBuffer(_kSolve, "_Particles", body.ParticleBuffer);
			colCS.SetBuffer(_kSolve, "_Positions", body.PositionsBuffer);
			colCS.SetBuffer(_kSolve, "_ColSize", body.ColSizeBuffer);
			colCS.SetBuffer(_kSolve, "_ColConstraints", body.ColConstraintBuffer);
			colCS.SetBuffer(_kSolve, "_DeltaBytes", body.DeltaBytesBuffer); // FIX 1
			colCS.Dispatch(_kSolve, Ceil(MAX_COLLISION_CONSTRAINTS), 1, 1);

			// ── Stretch constraint ─────────────────────────────────────────────
			BindPhysicsBuffers(cs, _kStretch, body);
			cs.Dispatch(_kStretch, Ceil(body.EdgeCount), 1, 1);

			// ── Volume constraint ──────────────────────────────────────────────
			BindPhysicsBuffers(cs, _kVolume, body);
			cs.Dispatch(_kVolume, Ceil(body.TetCount), 1, 1);

			// ── Postsolve ─────────────────────────────────────────────────────
			BindPhysicsBuffers(cs, _kPostsolve, body);
			cs.Dispatch(_kPostsolve, Ceil(body.ParticleCount), 1, 1);
		}

		void BindPhysicsBuffers(ComputeShader cs, int kernel, SoftBodyGPUState body)
		{
			cs.SetBuffer(kernel, "_Particles", body.ParticleBuffer);
			cs.SetBuffer(kernel, "_Positions", body.PositionsBuffer);
			cs.SetBuffer(kernel, "_Edges", body.EdgeBuffer);
			cs.SetBuffer(kernel, "_Tetrahedrals", body.TetBuffer);
			cs.SetBuffer(kernel, "_DeltaBytes", body.DeltaBytesBuffer); // FIX 1
		}

		// ─────────────────────────────────────────────────────────────────────
		// Dispatch: Mesh Deformation
		// Mirrors Renderer::deformMesh()
		// ─────────────────────────────────────────────────────────────────────
		void DispatchDeform(/*SoftBodyGPUState*/SoftBodyComponent bodyCmp)
		{
			var body = bodyCmp.State;
			//
			var cs = DeformCS;
			cs.SetInt("_VertexCount", body.VertexCount);
			cs.SetInt("_IndexCount", body.IndexCount);

			// FIX 2+3: no per-frame alloc, no ClearIntBuffer
			body.ClearNormalBytes(); // reuses pre-allocated _zeroNormalBytes

			// ── Deform (position update) ──────────────────────────────────────
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

			// ── Recalculate normals (per face) ────────────────────────────────
			cs.SetBuffer(_kRecalcNormals, "_VertexPositions", body.VertexPositionsBuffer);
			cs.SetBuffer(_kRecalcNormals, "_VertexNormals", body.VertexNormalsBuffer);
			cs.SetBuffer(_kRecalcNormals, "_Indices", body.MeshIndicesBuffer);
			cs.SetBuffer(_kRecalcNormals, "_NormalBytes", body.NormalBytesBuffer); // FIX 1
			cs.Dispatch(_kRecalcNormals, Ceil(body.IndexCount / 3), 1, 1);

			// ── Normalize normals ─────────────────────────────────────────────
			cs.SetBuffer(_kNormalizeNormals, "_VertexNormals", body.VertexNormalsBuffer);
			cs.SetBuffer(_kNormalizeNormals, "_NormalBytes", body.NormalBytesBuffer); // FIX 1
			cs.Dispatch(_kNormalizeNormals, Ceil(body.VertexCount), 1, 1);

			// ── Pull vertex/normal data back to Unity Mesh ────────────────────
			// Use AsyncGPUReadback for zero-stall readback (1-frame latency is fine)
			// For best performance upgrade to GraphicsBuffer + Mesh.SetVertexBufferData
			// FIX 2: GetData into pre-allocated arrays — zero heap allocation
			body.VertexPositionsBuffer.GetData(body.ReadbackPos);
			body.VertexNormalsBuffer.GetData(body.ReadbackNrm);
			body.RenderMesh.vertices = body.ReadbackPos;
			body.RenderMesh.normals = body.ReadbackNrm;
			body.RenderMesh.RecalculateBounds();
			// [3/7/2026 jzq]
			bodyCmp.InternalOnDeformed();
		}

		// ─────────────────────────────────────────────────────────────────────
		// Rendering
		// Graphics.DrawMesh replaces vkCmdDrawIndexed; MaterialPropertyBlock
		// replaces push constants (tint, roughness, metallic).
		// ─────────────────────────────────────────────────────────────────────
		//void DrawAllBodies()
		//{
		//	if (SoftBodyMaterial == null)
		//		return;

		//	foreach (var body in _bodies)
		//	{
		//		if (!body.Active || body.RenderMesh == null)
		//			continue;

		//		_mpb.SetColor("_Tint", body.Tint);
		//		_mpb.SetFloat("_Roughness", body.Roughness);
		//		_mpb.SetFloat("_Metallic", body.Metallic);

		//		Graphics.DrawMesh(
		//			body.RenderMesh,
		//			Matrix4x4.identity,
		//			SoftBodyMaterial,
		//			0,          // layer
		//			null,       // camera (null = all cameras)
		//			0,          // submesh
		//			_mpb
		//		);
		//	}
		//}

		// ─────────────────────────────────────────────────────────────────────
		// Light matrix update (matches renderImGui light matrix calculation)
		// ─────────────────────────────────────────────────────────────────────
		//void UpdateLightMatrix()
		//{
		//	if (DirectionalLight == null /*|| SoftBodyMaterial == null*/)
		//		return;

		//	Vector3 lightDir = DirectionalLight.transform.forward;
		//	Matrix4x4 lightProj = Matrix4x4.Ortho(
		//		-ShadowOrthoSize, ShadowOrthoSize,
		//		-ShadowOrthoSize, ShadowOrthoSize,
		//		0.1f, 100f);

		//	Matrix4x4 lightView = Matrix4x4.LookAt(
		//		-lightDir * ShadowLightDist,
		//		Vector3.zero,
		//		Vector3.up);

		//	//SoftBodyMaterial.SetMatrix("_LightMatrix", lightProj * lightView);
		//	//SoftBodyMaterial.SetVector("_LightDir", new Vector4(lightDir.x, lightDir.y, lightDir.z, 0));
		//}

		// ─────────────────────────────────────────────────────────────────────
		// Collision mesh upload (replaces createResources() floor setup)
		// ─────────────────────────────────────────────────────────────────────
		void UploadCollisionMesh()
		{
			if (CollisionMesh == null)
			{
				Debug.LogWarning("[XPBD] No collision mesh assigned — collisions disabled.");
				return;
			}

			Vector3[] verts = CollisionMesh.vertices;
			int[] tris = CollisionMesh.triangles;
			_colTriCount = tris.Length / 3;

			// float3 positions (stride = 12)
			_colPositionsBuffer = new ComputeBuffer(verts.Length, 3 * sizeof(float));
			_colPositionsBuffer.SetData(verts);

			_colIndicesBuffer = new ComputeBuffer(tris.Length, sizeof(uint));
			_colIndicesBuffer.SetData(Array.ConvertAll(tris, x => (uint) x));
		}

		// ─────────────────────────────────────────────────────────────────────
		// Kernel ID caching
		// ─────────────────────────────────────────────────────────────────────
		void CacheKernelIDs()
		{
			_kPresolve = SoftBodySimCS.FindKernel("Presolve");
			_kPostsolve = SoftBodySimCS.FindKernel("Postsolve");
			_kStretch = SoftBodySimCS.FindKernel("StretchConstraint");
			_kVolume = SoftBodySimCS.FindKernel("VolumeConstraint");

			_kDetect = CollisionCS.FindKernel("DetectCollisions");
			_kSolve = CollisionCS.FindKernel("SolveCollisions");

			_kDirectDeform = DeformCS.FindKernel("DirectDeform");
			_kTetDeform = DeformCS.FindKernel("TetDeform");
			_kRecalcNormals = DeformCS.FindKernel("RecalcNormals");
			_kNormalizeNormals = DeformCS.FindKernel("NormalizeNormals");
		}

		// ─────────────────────────────────────────────────────────────────────
		// Utility
		// ─────────────────────────────────────────────────────────────────────
		static int Ceil(int count) => (count + GROUP_SIZE - 1) / GROUP_SIZE;
	}
}
