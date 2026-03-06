// SoftBodyComponent.cs
// MonoBehaviour attached to each soft body GameObject.
// Reads a TetrahedralMeshAsset (ScriptableObject), allocates GPU buffers,
// and registers with SoftBodySimulationManager.
//
// Corresponds to Renderer::createSoftBody() in Renderer.cpp.
//
// Inspector workflow:
//   1. Assign TetMeshAsset (exported from Blender Tetrahedralizer plugin)
//   2. Assign RenderMeshFilter (the high-res triangle mesh)
//   3. Set UseTetDeformation = true if tet mesh is lower resolution
//   4. Assign the scene's SoftBodySimulationManager
//   5. Press Play

using UnityEngine;

namespace XPBD
{
    [RequireComponent(typeof(MeshFilter))]
    public class SoftBodyComponent : MonoBehaviour
    {
        [Header("Assets")]
        public TetrahedralMeshAsset TetMeshAsset;

        [Header("Simulation")]
        [Tooltip("True = barycentric skinning from low-res tet mesh to high-res render mesh (tetrahedral_deform path). " +
                 "False = 1-to-1 index map (deform path), tet mesh and render mesh must share the same resolution.")]
        public bool UseTetDeformation = true;

        [Header("Material Override")]
        public Color Tint      = new Color(1f, 0.15f, 0.05f, 1f);
        [Range(0f, 1f)] public float Roughness = 0.5f;
        [Range(0f, 1f)] public float Metallic  = 0.0f;

        [Header("Manager Reference")]
        public SoftBodySimulationManager Manager;

        private SoftBodyGPUState _state;
        private MeshFilter       _meshFilter;

        void Start()
        {
            if (TetMeshAsset == null)
            {
                Debug.LogError("[XPBD] No TetrahedralMeshAsset assigned.", this);
                return;
            }
            if (Manager == null)
            {
                Manager = FindObjectOfType<SoftBodySimulationManager>();
                if (Manager == null)
                {
                    Debug.LogError("[XPBD] SoftBodySimulationManager not found in scene.", this);
                    return;
                }
            }

            _meshFilter = GetComponent<MeshFilter>();

            // Instantiate the render mesh so each body has its own copy
            Mesh renderMesh = Instantiate(_meshFilter.sharedMesh);
            renderMesh.MarkDynamic(); // hint to Unity: vertices change every frame
            _meshFilter.mesh = renderMesh;

            // Convert ScriptableObject data to GPU structs
            var data = TetMeshAsset;

            var particles = new GPUParticle[data.Particles.Length];
            for (int i = 0; i < particles.Length; i++)
            {
                particles[i].position = data.Particles[i].Position + transform.position;
                particles[i].velocity = Vector3.zero;
                particles[i].invMass  = data.Particles[i].InvMass;
            }

            var edges = new GPUEdge[data.Edges.Length];
            for (int i = 0; i < edges.Length; i++)
            {
                edges[i].indexA  = data.Edges[i].IndexA;
                edges[i].indexB  = data.Edges[i].IndexB;
                edges[i].restLen = data.Edges[i].RestLen;
            }

            var tets = new GPUTetrahedral[data.Tetrahedrals.Length];
            for (int i = 0; i < tets.Length; i++)
            {
                tets[i].i0         = data.Tetrahedrals[i].I0;
                tets[i].i1         = data.Tetrahedrals[i].I1;
                tets[i].i2         = data.Tetrahedrals[i].I2;
                tets[i].i3         = data.Tetrahedrals[i].I3;
                tets[i].restVolume = data.Tetrahedrals[i].RestVolume;
            }

            GPUSkinningInfo[] skinning   = null;
            uint[]            origIndices = null;

            if (UseTetDeformation)
            {
                skinning = new GPUSkinningInfo[data.Skinning.Length];
                for (int i = 0; i < skinning.Length; i++)
                {
                    skinning[i].weights  = data.Skinning[i].Weights;
                    skinning[i].tetIndex = data.Skinning[i].TetIndex;
                }
            }
            else
            {
                origIndices = data.OrigIndices;
            }

            _state = new SoftBodyGPUState();
            _state.Tint      = Tint;
            _state.Roughness = Roughness;
            _state.Metallic  = Metallic;

            _state.Init(
                particles,
                edges,
                tets,
                renderMesh.vertices,
                renderMesh.uv,
                renderMesh.triangles,
                origIndices,
                skinning,
                UseTetDeformation,
                renderMesh,
                Tint
            );

            Manager.AddBody(_state);
        }

        void OnDestroy()
        {
            if (_state != null && Manager != null)
                Manager.RemoveBody(_state);
        }

#if UNITY_EDITOR
        // Sync Inspector changes to active simulation at runtime
        void OnValidate()
        {
            if (_state != null)
            {
                _state.Tint      = Tint;
                _state.Roughness = Roughness;
                _state.Metallic  = Metallic;
            }
        }
#endif
    }
}
