// SoftBodyGPUState.cs
// Owns and manages all GPU ComputeBuffers for one soft body instance.
// This replaces the Vulkan SoftBody struct (VkBuffer allocations +
// descriptor set writes) from Renderer.cpp / Softbody.h.
//
// Lifecycle: Create → bind to compute shaders each frame → Dispose.
// SoftBodySimulationManager.cs is responsible for calling Dispose.
//
// FIXES vs original:
//
//   FIX 1 — NaN positions (root cause of line 278-285 error):
//     Old: DeltaIntBuffer  = ComputeBuffer(count*3, int)
//          AtomicAddDelta  = InterlockedAdd(_DeltaInt, asint(v.x))
//     asint() reinterprets the float bit-pattern as an integer.
//     Adding two integer bit-patterns ≠ adding the floats.
//       asint(0.3f) + asint(0.3f) decoded back = 1.49e37  (should be 0.6)
//       asint(+1.5f) + asint(-0.5f) decoded back = -1.28e38  (should be 1.0)
//     Every shared particle gets garbage on first substep → NaN floods out.
//     Fix: RWByteAddressBuffer + InterlockedAddFloat — true float atomics,
//     available DX12 SM6.0+.  The project already requires DX12 for wave
//     intrinsics, so this costs nothing.
//     Buffer renamed: DeltaIntBuffer  → DeltaBytesBuffer
//                     NormalIntBuffer → NormalBytesBuffer
//
//   FIX 2 — per-frame heap allocation at line 278-279:
//     Old: var positions = new Vector3[body.VertexCount];   // every fixed step!
//          var normals   = new Vector3[body.VertexCount];
//     Fix: _readbackPos / _readbackNrm allocated ONCE in Init, reused every frame.
//          Exposed as ReadbackPos / ReadbackNrm so Manager can call GetData into them.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	public sealed class SoftBodyGPUState : IDisposable
	{
		// ── Physics buffers ─────────────────────────────────────────────────
		public readonly ComputeBuffer ParticleBuffer;    // GPUParticle[]
		public readonly ComputeBuffer PositionsBuffer;   // GPUPbdPositions[]
		public readonly ComputeBuffer EdgeBuffer;        // GPUEdge[]
		public readonly ComputeBuffer TetBuffer;         // GPUTetrahedral[]

		// FIX 1: Raw byte buffer → RWByteAddressBuffer in HLSL → InterlockedAddFloat
		// Size bytes = ParticleCount * 3 floats * 4 bytes
		public readonly ComputeBuffer DeltaBytesBuffer;

		// ── Deform buffers ───────────────────────────────────────────────────
		// Either direct index map (same-res) or barycentric skinning (tet deform)
		public readonly ComputeBuffer OrigIndicesBuffer; // uint[]          — DirectDeform path
		public readonly ComputeBuffer SkinningBuffer;    // GPUSkinningInfo[] — TetDeform path
		public readonly bool UseTetDeformation;

		// Render mesh vertex/normal streams — written by Deform kernels
		public readonly ComputeBuffer VertexPositionsBuffer; // float3[]
		public readonly ComputeBuffer VertexNormalsBuffer;   // float3[]
		public readonly ComputeBuffer MeshIndicesBuffer;     // uint[] (face index buffer for RecalcNormals)
															 // FIX 1: Same fix for normal accumulation
															 // Size bytes = VertexCount * 3 floats * 4 bytes
		public readonly ComputeBuffer NormalBytesBuffer;

		// ── Collision buffers ────────────────────────────────────────────────
		public readonly ComputeBuffer ColSizeBuffer;       // uint[1]  — atomic counter
		public readonly ComputeBuffer ColConstraintBuffer; // GPUColConstraint[MAX_CONSTRAINTS]

		// ── Counts ───────────────────────────────────────────────────────────
		public readonly int ParticleCount;
		public readonly int EdgeCount;
		public readonly int TetCount;
		public readonly int VertexCount;
		public readonly int IndexCount;   // triangle index count (= faceCount * 3)

		// ── Render material (per-body tint / roughness / metallic) ───────────
		//public Color Tint = new Color(1f, 0.15f, 0.05f, 1f);
		//public float Roughness = 0.5f;
		//public float Metallic = 0.0f;

		// Used by SoftBodySimulationManager to hold the Unity Mesh
		public readonly Mesh RenderMesh;
		//public bool Active
		//{
		//	get; private set;
		//}

		// FIX 2: Pre-allocated readback arrays — allocated ONCE in Init, reused every frame.
		// Manager calls: body.VertexPositionsBuffer.GetData(body.ReadbackPos);
		public readonly Vector3[] ReadbackPos;
		public readonly Vector3[] ReadbackNrm;

		// Pre-allocated zero bytes for clearing NormalBytesBuffer — no per-frame alloc.
		readonly byte[] _zeroNormalBytes;
		// ── Initialize ───────────────────────────────────────────────────────
		/// <summary>
		/// Allocate all GPU buffers and upload initial data.
		/// Called once by SoftBodySimulationManager when spawning a body.
		/// </summary>
		//public SoftBodyGPUState(
		//	GPUParticle[] particles,
		//	GPUEdge[] edges,
		//	GPUTetrahedral[] tets,
		//	//>
		//	//Vector3[] initialVertexPositions,
		//	//Vector2[] uvs,
		//	//int[] triangleIndices,
		//	//<
		//	// Deform path A — same-res direct index map
		//	uint[] origIndices,
		//	// Deform path B — tetrahedral barycentric skinning
		//	GPUSkinningInfo[] skinning,
		//	bool useTetDeformation,
		//	Mesh renderMesh
		//	)
		#region Static
		sealed class TetMeshGpuInfo
		{
			public TetMeshGpuInfo(TetrahedralMeshAsset tetAsset)
			{
				particles = new GPUParticle[tetAsset.Particles.Length];
				for (int i = 0; i < tetAsset.Particles.Length; i++)
				{
					particles[i].position = tetAsset.Particles[i].Position;
					particles[i].velocity = Vector3.zero;
					particles[i].invMass = tetAsset.Particles[i].InvMass;
				}
				edges = new GPUEdge[tetAsset.Edges.Length];
				for (int i = 0; i < edges.Length; i++)
				{
					var edge = tetAsset.Edges[i];
					edges[i].indexA = edge.IndexA;
					edges[i].indexB = edge.IndexB;
					edges[i].restLen = edge.RestLen;
				}
				tets = new GPUTetrahedral[tetAsset.Tetrahedrals.Length];

				for (int i = 0; i < tets.Length; i++)
				{
					var t = tetAsset.Tetrahedrals[i];
					tets[i].i0 = t.I0;
					tets[i].i1 = t.I1;
					tets[i].i2 = t.I2;
					tets[i].i3 = t.I3;
					tets[i].restVolume = t.RestVolume;
				}
				if (tetAsset.UseTetDeformation)
				{
					skinnings = new GPUSkinningInfo[tetAsset.Skinning.Length];
					for (int i = 0; i < skinnings.Length; i++)
					{
						var skinning = tetAsset.Skinning[i];
						skinnings[i].weights = skinning.Weights;
						skinnings[i].tetIndex = skinning.TetIndex;
					}
				}
				else
					skinnings = null;
			}
			public readonly GPUParticle[] particles;
			public readonly GPUEdge[] edges;
			public readonly GPUTetrahedral[] tets;
			public readonly GPUSkinningInfo[] skinnings;
		}
		readonly static Dictionary<TetrahedralMeshAsset, TetMeshGpuInfo> tetMeshGpuInfoMap_s = new Dictionary<TetrahedralMeshAsset, TetMeshGpuInfo>();
		#endregion
		public SoftBodyGPUState(TetrahedralMeshAsset tetMeshAsset, Matrix4x4 mat, Mesh renderMesh)
		{
			//var renderMesh = tetMeshAsset.RenderMesh; //CAN'T use asset-version
			var triangleIndices = renderMesh.triangles;
			var initialVertexPositions = renderMesh.vertices;
			//
			if (!tetMeshGpuInfoMap_s.TryGetValue(tetMeshAsset, out var tetMeshGpuInfo))
				tetMeshGpuInfoMap_s[tetMeshAsset] = tetMeshGpuInfo = new TetMeshGpuInfo(tetMeshAsset);
			//
			ParticleCount = tetMeshGpuInfo.particles.Length;
			EdgeCount = tetMeshGpuInfo.edges.Length;
			TetCount = tetMeshGpuInfo.tets.Length;
			VertexCount = initialVertexPositions.Length;
			IndexCount = triangleIndices.Length;
			UseTetDeformation = tetMeshAsset.UseTetDeformation;
			RenderMesh = renderMesh;
			//Active = true;
			//
			GPUParticle[] particles = null;
			if (!mat.isIdentity) //deep-clone & transform
			{
				particles = new GPUParticle[ParticleCount];
				for (int i = 0; i < particles.Length; i++)
				{
					var p = particles[i];
					tetMeshGpuInfo.particles[i].CpyTo(ref p);
					//post
					p.position = mat.MultiplyPoint3x4(p.position);
					particles[i] = p; //^
				}
			}
			else
				particles = tetMeshGpuInfo.particles;
			// Physics
			ParticleBuffer = Upload(particles, GPUStrides.Particle);
			EdgeBuffer = Upload(tetMeshGpuInfo.edges, GPUStrides.Edge);
			TetBuffer = Upload(tetMeshGpuInfo.tets, GPUStrides.Tetrahedral);
			// PbdPositions: predict = initial position, delta = 0
			var pbdPos = new GPUPbdPositions[ParticleCount];
			for (int i = 0; i < ParticleCount; i++)
				pbdPos[i].predict = particles[i].position;
			PositionsBuffer = Upload(pbdPos, GPUStrides.PbdPositions);
			{
				//Null particles [3/6/2026 jzq]
				particles = null;
			}
			// FIX 1: ComputeBufferType.Raw → RWByteAddressBuffer in shader
			// Count = number of 4-byte dwords.  ParticleCount*3 floats = ParticleCount*3 dwords.
			DeltaBytesBuffer = new ComputeBuffer(
				ParticleCount * 3, 4, ComputeBufferType.Raw);
			DeltaBytesBuffer.SetData(new int[ParticleCount * 3]); // zero on creation

			// Deform
			if (!UseTetDeformation)
				OrigIndicesBuffer = Upload(tetMeshAsset.OrigIndices, sizeof(uint));
			else
				SkinningBuffer = Upload(tetMeshGpuInfo.skinnings, GPUStrides.SkinningInfo);

			// Vertex / normal streams (RWStructuredBuffer<float3>)
			// stride = 12 bytes (float3), use stride override
			VertexPositionsBuffer = new ComputeBuffer(VertexCount, 3 * sizeof(float));
			VertexNormalsBuffer = new ComputeBuffer(VertexCount, 3 * sizeof(float));
			VertexPositionsBuffer.SetData(initialVertexPositions);

			MeshIndicesBuffer = Upload(
				Array.ConvertAll(triangleIndices, x => (uint) x), sizeof(uint));

			// FIX 1: Raw byte buffer for normals (same reason)
			NormalBytesBuffer = new ComputeBuffer(
				VertexCount * 3, 4, ComputeBufferType.Raw);
			// Pre-allocated zero array — reused by ClearNormalBytes() every fixed step
			_zeroNormalBytes = new byte[VertexCount * 3 * 4];
			NormalBytesBuffer.SetData(_zeroNormalBytes);

			// ── Collision buffers ─────────────────────────────────────────────
			ColSizeBuffer = new ComputeBuffer(1, sizeof(uint));
			ColSizeBuffer.SetData(new uint[] { 0 });

			ColConstraintBuffer = new ComputeBuffer(
				SoftBodySimulationManager.MAX_COLLISION_CONSTRAINTS,
				GPUStrides.ColConstraint);
			// FIX 2: allocate readback arrays once
			ReadbackPos = new Vector3[VertexCount];
			ReadbackNrm = new Vector3[VertexCount];
		}

		// Called once per fixed step before RecalcNormals dispatch.
		// Uses the pre-allocated _zeroNormalBytes — no heap allocation.
		public void ClearNormalBytes()
			=> NormalBytesBuffer.SetData(_zeroNormalBytes);

		// ── Helpers ───────────────────────────────────────────────────────────
		static ComputeBuffer Upload<T>(T[] data, int stride) where T : struct
		{
			var b = new ComputeBuffer(data.Length, stride);
			b.SetData(data);
			return b;
		}


		// ── IDisposable ───────────────────────────────────────────────────────
		public void Dispose()
		{
			//Active = false;
			SafeRelease(ParticleBuffer);
			SafeRelease(PositionsBuffer);
			SafeRelease(EdgeBuffer);
			SafeRelease(TetBuffer);
			SafeRelease(DeltaBytesBuffer);
			SafeRelease(OrigIndicesBuffer);
			SafeRelease(SkinningBuffer);
			SafeRelease(VertexPositionsBuffer);
			SafeRelease(VertexNormalsBuffer);
			SafeRelease(MeshIndicesBuffer);
			SafeRelease(NormalBytesBuffer);
			SafeRelease(ColSizeBuffer);
			SafeRelease(ColConstraintBuffer);
		}

		static void SafeRelease(ComputeBuffer buf)
		{
			if (buf != null && buf.IsValid())
				buf.Release();
		}
	}
}
