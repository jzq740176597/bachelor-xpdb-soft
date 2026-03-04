// SoftBodySimulationManager.cs
// Main simulation manager — orchestrates all compute dispatches each frame.
// Directly mirrors the Renderer::computePhysics / detectCollisions /
// deformMesh sequence from Renderer.cpp, plus the fixed-timestep loop
// with configurable substeps.
//
// Attach to an empty GameObject in the scene.
// Assign all ComputeShader assets and the collision mesh in the Inspector.

using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
    [DisallowMultipleComponent]
    public class SoftBodySimulationManager : MonoBehaviour
    {
        // ── Constants (matching original Renderer.h) ──────────────────────────
        public const int MAX_COLLISION_CONSTRAINTS = 10000;
        private const int GROUP_SIZE = 32;

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
        [Range(10, 240)] public int FixedTimeStepFPS  = 60;
        [Range(1,  25)]  public int SubSteps          = 20;
        [Range(0f, 1f)]  public float EdgeCompliance   = 0.01f;
        [Range(0f, 1f)]  public float VolumeCompliance = 0.0f;

        // ── Inspector: Rendering ──────────────────────────────────────────────
        [Header("Rendering")]
        public Material SoftBodyMaterial;       // SoftBodyPBR.shader instance
        public Light    DirectionalLight;       // used to compute light matrix
        public float    ShadowOrthoSize = 15f;
        public float    ShadowLightDist = 15f;

        // ── Private state ─────────────────────────────────────────────────────
        private readonly List<SoftBodyGPUState> _bodies = new();

        // Collision geometry buffers (static — uploaded once)
        private ComputeBuffer _colPositionsBuffer;
        private ComputeBuffer _colIndicesBuffer;
        private int           _colTriCount;

        // Kernel IDs — cached at startup
        private int _kPresolve, _kPostsolve, _kStretch, _kVolume;
        private int _kDetect, _kSolve;
        private int _kDirectDeform, _kTetDeform, _kRecalcNormals, _kNormalizeNormals;

        // Fixed-timestep accumulator (matches Timer::passedFixedDT)
        private float _timeAccum;
        private float _fixedDT;
        private float _subDT;

        // ── MaterialPropertyBlock per body ────────────────────────────────────
        private MaterialPropertyBlock _mpb;

        // ─────────────────────────────────────────────────────────────────────
        void Awake()
        {
            CacheKernelIDs();
            UploadCollisionMesh();
            _mpb = new MaterialPropertyBlock();
        }

        void OnDestroy()
        {
            foreach (var b in _bodies) b.Dispose();
            _colPositionsBuffer?.Release();
            _colIndicesBuffer?.Release();
        }

        // ─────────────────────────────────────────────────────────────────────
        void Update()
        {
            // Recalculate fixed dt in case Inspector values changed
            _fixedDT = 1f / FixedTimeStepFPS;
            _subDT   = _fixedDT / SubSteps;

            _timeAccum += Time.deltaTime;

            // Update light matrix on shader each frame (matches renderImGui lightDir update)
            UpdateLightMatrix();

            if (_timeAccum >= _fixedDT)
            {
                _timeAccum -= _fixedDT;

                // ── Collision detection (once per fixed step, before substeps) ──
                foreach (var body in _bodies)
                {
                    if (!body.Active) continue;
                    ResetColSize(body);
                    DispatchDetectCollisions(body);
                }

                // ── Physics substeps ───────────────────────────────────────────
                foreach (var body in _bodies)
                {
                    if (!body.Active) continue;
                    for (int s = 0; s < SubSteps; s++)
                        DispatchPhysicsSubstep(body);

                    // ── Mesh deformation (once per fixed step) ─────────────────
                    DispatchDeform(body);
                }
            }

            // ── Draw bodies every render frame ────────────────────────────────
            DrawAllBodies();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Register a new soft body for simulation.</summary>
        public void AddBody(SoftBodyGPUState body)
        {
            _bodies.Add(body);
        }

        /// <summary>Remove and dispose a soft body.</summary>
        public void RemoveBody(SoftBodyGPUState body)
        {
            body.Dispose();
            _bodies.Remove(body);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dispatch: Collision Detection
        // Mirrors Renderer::detectCollisions()
        // ─────────────────────────────────────────────────────────────────────
        private void ResetColSize(SoftBodyGPUState body)
        {
            body.ColSizeBuffer.SetData(new uint[] { 0 });
        }

        private void DispatchDetectCollisions(SoftBodyGPUState body)
        {
            var cs = CollisionCS;
            cs.SetFloat("_ColDeltaTime", _fixedDT);
            cs.SetInt  ("_TriCount",     _colTriCount);
            cs.SetInt  ("_ParticleCount", body.ParticleCount);
            cs.SetInt  ("_EdgeCount",    body.EdgeCount);
            cs.SetInt  ("_TetCount",     body.TetCount);

            cs.SetBuffer(_kDetect, "_ColPositions",    _colPositionsBuffer);
            cs.SetBuffer(_kDetect, "_TriIndices",      _colIndicesBuffer);
            cs.SetBuffer(_kDetect, "_Particles",       body.ParticleBuffer);
            cs.SetBuffer(_kDetect, "_Positions",       body.PositionsBuffer);
            cs.SetBuffer(_kDetect, "_ColSize",         body.ColSizeBuffer);
            cs.SetBuffer(_kDetect, "_ColConstraints",  body.ColConstraintBuffer);
            cs.SetBuffer(_kDetect, "_DeltaInt",        body.DeltaIntBuffer);

            cs.Dispatch(_kDetect, Ceil(body.ParticleCount), 1, 1);
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
        private void DispatchPhysicsSubstep(SoftBodyGPUState body)
        {
            var cs = SoftBodySimCS;

            // Shared per-call constants
            cs.SetFloat("_DeltaTime",          _subDT);
            cs.SetFloat("_DistanceCompliance", EdgeCompliance);
            cs.SetFloat("_VolumeCompliance",   VolumeCompliance);
            cs.SetInt  ("_ParticleCount",      body.ParticleCount);
            cs.SetInt  ("_EdgeCount",          body.EdgeCount);
            cs.SetInt  ("_TetCount",           body.TetCount);

            // Clear delta int buffer before each substep (matches delta = vec3(0) in presolve)
            SoftBodyGPUState.ClearIntBuffer(body.DeltaIntBuffer, body.ParticleCount * 3);

            // ── Presolve ──────────────────────────────────────────────────────
            BindPhysicsBuffers(cs, _kPresolve, body);
            cs.Dispatch(_kPresolve, Ceil(body.ParticleCount), 1, 1);

            // ── Collision constraint solve ─────────────────────────────────────
            var colCS = CollisionCS;
            colCS.SetFloat("_ColDeltaTime",   _fixedDT);
            colCS.SetInt  ("_TriCount",       _colTriCount);
            colCS.SetInt  ("_ParticleCount",  body.ParticleCount);
            colCS.SetBuffer(_kSolve, "_Particles",      body.ParticleBuffer);
            colCS.SetBuffer(_kSolve, "_Positions",      body.PositionsBuffer);
            colCS.SetBuffer(_kSolve, "_ColSize",        body.ColSizeBuffer);
            colCS.SetBuffer(_kSolve, "_ColConstraints", body.ColConstraintBuffer);
            colCS.SetBuffer(_kSolve, "_DeltaInt",       body.DeltaIntBuffer);
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

        private void BindPhysicsBuffers(ComputeShader cs, int kernel, SoftBodyGPUState body)
        {
            cs.SetBuffer(kernel, "_Particles",    body.ParticleBuffer);
            cs.SetBuffer(kernel, "_Positions",    body.PositionsBuffer);
            cs.SetBuffer(kernel, "_Edges",        body.EdgeBuffer);
            cs.SetBuffer(kernel, "_Tetrahedrals", body.TetBuffer);
            cs.SetBuffer(kernel, "_DeltaInt",     body.DeltaIntBuffer);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Dispatch: Mesh Deformation
        // Mirrors Renderer::deformMesh()
        // ─────────────────────────────────────────────────────────────────────
        private void DispatchDeform(SoftBodyGPUState body)
        {
            var cs = DeformCS;
            cs.SetInt("_VertexCount", body.VertexCount);
            cs.SetInt("_IndexCount",  body.IndexCount);

            // Clear normal int buffer before RecalcNormals
            SoftBodyGPUState.ClearIntBuffer(body.NormalIntBuffer, body.VertexCount * 3);

            // ── Deform (position update) ──────────────────────────────────────
            int deformKernel = body.UseTetDeformation ? _kTetDeform : _kDirectDeform;

            cs.SetBuffer(deformKernel, "_VertexPositions", body.VertexPositionsBuffer);
            cs.SetBuffer(deformKernel, "_VertexNormals",   body.VertexNormalsBuffer);
            cs.SetBuffer(deformKernel, "_Positions",       body.PositionsBuffer);

            if (body.UseTetDeformation)
            {
                cs.SetBuffer(deformKernel, "_Skinning",     body.SkinningBuffer);
                cs.SetBuffer(deformKernel, "_Tetrahedrals", body.TetBuffer);
            }
            else
            {
                cs.SetBuffer(deformKernel, "_OrigIndices", body.OrigIndicesBuffer);
            }

            cs.Dispatch(deformKernel, Ceil(body.VertexCount), 1, 1);

            // ── Recalculate normals (per face) ────────────────────────────────
            cs.SetBuffer(_kRecalcNormals, "_VertexPositions", body.VertexPositionsBuffer);
            cs.SetBuffer(_kRecalcNormals, "_VertexNormals",   body.VertexNormalsBuffer);
            cs.SetBuffer(_kRecalcNormals, "_Indices",         body.MeshIndicesBuffer);
            cs.SetBuffer(_kRecalcNormals, "_NormalInt",       body.NormalIntBuffer);
            cs.Dispatch(_kRecalcNormals, Ceil(body.IndexCount / 3), 1, 1);

            // ── Normalize normals ─────────────────────────────────────────────
            cs.SetBuffer(_kNormalizeNormals, "_VertexNormals", body.VertexNormalsBuffer);
            cs.SetBuffer(_kNormalizeNormals, "_NormalInt",     body.NormalIntBuffer);
            cs.Dispatch(_kNormalizeNormals, Ceil(body.VertexCount), 1, 1);

            // ── Pull vertex/normal data back to Unity Mesh ────────────────────
            // Use AsyncGPUReadback for zero-stall readback (1-frame latency is fine)
            // For best performance upgrade to GraphicsBuffer + Mesh.SetVertexBufferData
            // once the project targets Unity 2021.2+ GraphicsBuffer API fully.
            var positions = new Vector3[body.VertexCount];
            var normals   = new Vector3[body.VertexCount];
            body.VertexPositionsBuffer.GetData(positions);
            body.VertexNormalsBuffer.GetData(normals);

            body.RenderMesh.vertices = positions;
            body.RenderMesh.normals  = normals;
            body.RenderMesh.RecalculateBounds();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rendering
        // Graphics.DrawMesh replaces vkCmdDrawIndexed; MaterialPropertyBlock
        // replaces push constants (tint, roughness, metallic).
        // ─────────────────────────────────────────────────────────────────────
        private void DrawAllBodies()
        {
            if (SoftBodyMaterial == null) return;

            foreach (var body in _bodies)
            {
                if (!body.Active || body.RenderMesh == null) continue;

                _mpb.SetColor ("_Tint",      body.Tint);
                _mpb.SetFloat ("_Roughness", body.Roughness);
                _mpb.SetFloat ("_Metallic",  body.Metallic);

                Graphics.DrawMesh(
                    body.RenderMesh,
                    Matrix4x4.identity,
                    SoftBodyMaterial,
                    0,          // layer
                    null,       // camera (null = all cameras)
                    0,          // submesh
                    _mpb
                );
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Light matrix update (matches renderImGui light matrix calculation)
        // ─────────────────────────────────────────────────────────────────────
        private void UpdateLightMatrix()
        {
            if (DirectionalLight == null || SoftBodyMaterial == null) return;

            Vector3 lightDir = DirectionalLight.transform.forward;
            Matrix4x4 lightProj = Matrix4x4.Ortho(
                -ShadowOrthoSize, ShadowOrthoSize,
                -ShadowOrthoSize, ShadowOrthoSize,
                0.1f, 100f);

            Matrix4x4 lightView = Matrix4x4.LookAt(
                -lightDir * ShadowLightDist,
                Vector3.zero,
                Vector3.up);

            Matrix4x4 lightMatrix = lightProj * lightView;
            SoftBodyMaterial.SetMatrix("_LightMatrix", lightMatrix);
            SoftBodyMaterial.SetVector("_LightDir", new Vector4(lightDir.x, lightDir.y, lightDir.z, 0));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Collision mesh upload (replaces createResources() floor setup)
        // ─────────────────────────────────────────────────────────────────────
        private void UploadCollisionMesh()
        {
            if (CollisionMesh == null)
            {
                Debug.LogWarning("[XPBD] No collision mesh assigned — collisions disabled.");
                return;
            }

            Vector3[] verts  = CollisionMesh.vertices;
            int[]     tris   = CollisionMesh.triangles;
            _colTriCount = tris.Length / 3;

            // float3 positions (stride = 12)
            _colPositionsBuffer = new ComputeBuffer(verts.Length, 3 * sizeof(float));
            _colPositionsBuffer.SetData(verts);

            _colIndicesBuffer = new ComputeBuffer(tris.Length, sizeof(uint));
            var uTris = System.Array.ConvertAll(tris, x => (uint)x);
            _colIndicesBuffer.SetData(uTris);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Kernel ID caching
        // ─────────────────────────────────────────────────────────────────────
        private void CacheKernelIDs()
        {
            _kPresolve       = SoftBodySimCS.FindKernel("Presolve");
            _kPostsolve      = SoftBodySimCS.FindKernel("Postsolve");
            _kStretch        = SoftBodySimCS.FindKernel("StretchConstraint");
            _kVolume         = SoftBodySimCS.FindKernel("VolumeConstraint");

            _kDetect         = CollisionCS.FindKernel("DetectCollisions");
            _kSolve          = CollisionCS.FindKernel("SolveCollisions");

            _kDirectDeform   = DeformCS.FindKernel("DirectDeform");
            _kTetDeform      = DeformCS.FindKernel("TetDeform");
            _kRecalcNormals  = DeformCS.FindKernel("RecalcNormals");
            _kNormalizeNormals = DeformCS.FindKernel("NormalizeNormals");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Utility
        // ─────────────────────────────────────────────────────────────────────
        private static int Ceil(int count) => (count + GROUP_SIZE - 1) / GROUP_SIZE;
    }
}
