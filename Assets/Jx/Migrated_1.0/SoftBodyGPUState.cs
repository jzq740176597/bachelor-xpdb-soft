// SoftBodyGPUState.cs
// Owns and manages all GPU ComputeBuffers for one soft body instance.
// This replaces the Vulkan SoftBody struct (VkBuffer allocations +
// descriptor set writes) from Renderer.cpp / Softbody.h.
//
// Lifecycle: Create → bind to compute shaders each frame → Dispose.
// SoftBodySimulationManager.cs is responsible for calling Dispose.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace XPBD
{
    public class SoftBodyGPUState : IDisposable
    {
        // ── Physics buffers ─────────────────────────────────────────────────
        public ComputeBuffer ParticleBuffer;    // GPUParticle[]
        public ComputeBuffer PositionsBuffer;   // GPUPbdPositions[]
        public ComputeBuffer EdgeBuffer;        // GPUEdge[]
        public ComputeBuffer TetBuffer;         // GPUTetrahedral[]

        // Delta accumulation — int bit-cast (replaces atomic float)
        // size = particleCount * 3 ints
        public ComputeBuffer DeltaIntBuffer;

        // ── Deform buffers ───────────────────────────────────────────────────
        // Either direct index map (same-res) or barycentric skinning (tet deform)
        public ComputeBuffer OrigIndicesBuffer; // uint[]          — DirectDeform path
        public ComputeBuffer SkinningBuffer;    // GPUSkinningInfo[] — TetDeform path
        public bool          UseTetDeformation;

        // Render mesh vertex/normal streams — written by Deform kernels
        public ComputeBuffer VertexPositionsBuffer; // float3[]
        public ComputeBuffer VertexNormalsBuffer;   // float3[]
        public ComputeBuffer MeshIndicesBuffer;     // uint[] (face index buffer for RecalcNormals)
        public ComputeBuffer NormalIntBuffer;       // int[]  size = vertexCount * 3

        // ── Collision buffers ────────────────────────────────────────────────
        public ComputeBuffer ColSizeBuffer;       // uint[1]  — atomic counter
        public ComputeBuffer ColConstraintBuffer; // GPUColConstraint[MAX_CONSTRAINTS]

        // ── Counts ───────────────────────────────────────────────────────────
        public int ParticleCount;
        public int EdgeCount;
        public int TetCount;
        public int VertexCount;
        public int IndexCount;   // triangle index count (= faceCount * 3)

        // ── Render material (per-body tint / roughness / metallic) ───────────
        public Color   Tint      = new Color(1f, 0.15f, 0.05f, 1f);
        public float   Roughness = 0.5f;
        public float   Metallic  = 0.0f;

        // Used by SoftBodySimulationManager to hold the Unity Mesh
        public Mesh     RenderMesh;
        public bool     Active;

        // ── Initialize ───────────────────────────────────────────────────────
        /// <summary>
        /// Allocate all GPU buffers and upload initial data.
        /// Called once by SoftBodySimulationManager when spawning a body.
        /// </summary>
        public void Init(
            GPUParticle[]      particles,
            GPUEdge[]          edges,
            GPUTetrahedral[]   tets,
            Vector3[]          initialVertexPositions,
            Vector2[]          uvs,
            int[]              triangleIndices,
            // Deform path A — same-res direct index map
            uint[]             origIndices,
            // Deform path B — tetrahedral barycentric skinning
            GPUSkinningInfo[]  skinning,
            bool               useTetDeformation,
            Mesh               renderMesh,
            Color              tint)
        {
            ParticleCount = particles.Length;
            EdgeCount     = edges.Length;
            TetCount      = tets.Length;
            VertexCount   = initialVertexPositions.Length;
            IndexCount    = triangleIndices.Length;
            UseTetDeformation = useTetDeformation;
            RenderMesh    = renderMesh;
            Tint          = tint;
            Active        = true;

            // ── Physics buffers ──────────────────────────────────────────────
            ParticleBuffer   = CreateAndUpload(particles,  GPUStrides.Particle);
            EdgeBuffer       = CreateAndUpload(edges,      GPUStrides.Edge);
            TetBuffer        = CreateAndUpload(tets,       GPUStrides.Tetrahedral);

            // PbdPositions: predict = initial position, delta = 0
            var pbdPos = new GPUPbdPositions[ParticleCount];
            for (int i = 0; i < ParticleCount; i++)
                pbdPos[i].predict = particles[i].position;
            PositionsBuffer = CreateAndUpload(pbdPos, GPUStrides.PbdPositions);

            // Delta int buffer — initialised to 0 (CreateAndUpload zeroes by default)
            DeltaIntBuffer = new ComputeBuffer(ParticleCount * 3, sizeof(int));
            ClearIntBuffer(DeltaIntBuffer, ParticleCount * 3);

            // ── Deform buffers ────────────────────────────────────────────────
            if (!useTetDeformation)
                OrigIndicesBuffer = CreateAndUpload(origIndices, sizeof(uint));
            else
                SkinningBuffer = CreateAndUpload(skinning, GPUStrides.SkinningInfo);

            // Vertex / normal streams (RWStructuredBuffer<float3>)
            // stride = 12 bytes (float3), use stride override
            VertexPositionsBuffer = new ComputeBuffer(VertexCount, 3 * sizeof(float));
            VertexNormalsBuffer   = new ComputeBuffer(VertexCount, 3 * sizeof(float));
            VertexPositionsBuffer.SetData(initialVertexPositions);

            // Index buffer for RecalcNormals kernel
            // Convert int[] to uint[] for the GPU
            var uindices = Array.ConvertAll(triangleIndices, x => (uint)x);
            MeshIndicesBuffer = CreateAndUpload(uindices, sizeof(uint));

            // Normal int buffer
            NormalIntBuffer = new ComputeBuffer(VertexCount * 3, sizeof(int));
            ClearIntBuffer(NormalIntBuffer, VertexCount * 3);

            // ── Collision buffers ─────────────────────────────────────────────
            ColSizeBuffer = new ComputeBuffer(1, sizeof(uint));
            ColSizeBuffer.SetData(new uint[] { 0 });

            ColConstraintBuffer = new ComputeBuffer(
                SoftBodySimulationManager.MAX_COLLISION_CONSTRAINTS,
                GPUStrides.ColConstraint);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static ComputeBuffer CreateAndUpload<T>(T[] data, int stride)
            where T : struct
        {
            var buf = new ComputeBuffer(data.Length, stride);
            buf.SetData(data);
            return buf;
        }

        public static void ClearIntBuffer(ComputeBuffer buf, int count)
        {
            var zeros = new int[count];
            buf.SetData(zeros);
        }

        // ── IDisposable ───────────────────────────────────────────────────────
        public void Dispose()
        {
            Active = false;
            SafeRelease(ParticleBuffer);
            SafeRelease(PositionsBuffer);
            SafeRelease(EdgeBuffer);
            SafeRelease(TetBuffer);
            SafeRelease(DeltaIntBuffer);
            SafeRelease(OrigIndicesBuffer);
            SafeRelease(SkinningBuffer);
            SafeRelease(VertexPositionsBuffer);
            SafeRelease(VertexNormalsBuffer);
            SafeRelease(MeshIndicesBuffer);
            SafeRelease(NormalIntBuffer);
            SafeRelease(ColSizeBuffer);
            SafeRelease(ColConstraintBuffer);
        }

        private static void SafeRelease(ComputeBuffer buf)
        {
            if (buf != null && buf.IsValid()) buf.Release();
        }
    }
}
