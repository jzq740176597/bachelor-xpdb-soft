// TetrahedralMeshAsset.cs
// ScriptableObject that holds all per-body tetrahedral mesh data.
// Replaces the ResourceManager + TetrahedralMeshData loading pipeline.
//
// How to populate:
//   1. Export your .obj + .tet pair from Blender (using the matthias-research
//      BlenderTetPlugin.py, same as the original project).
//   2. Write a small Editor import tool (TetrahedralMeshImporter.cs) or
//      populate via code from a TetrahedralMeshLoader.cs at runtime.
//   3. Right-click in Project → Create → XPBD → TetrahedralMeshAsset.
//
// Data layout mirrors TetrahedralMeshData (C++) and ResourceManager logic.

using UnityEngine;

namespace XPBD
{
    // ─── Sub-structs (plain C# — not sent to GPU directly) ───────────────────
    [System.Serializable]
    public struct ParticleData
    {
        public Vector3 Position;
        public float   InvMass;   // 0 = pinned / infinite mass
    }

    [System.Serializable]
    public struct EdgeData
    {
        public uint  IndexA;
        public uint  IndexB;
        public float RestLen;
    }

    [System.Serializable]
    public struct TetrahedralData
    {
        public uint  I0, I1, I2, I3;
        public float RestVolume;
    }

    [System.Serializable]
    public struct SkinningData
    {
        public Vector3 Weights;   // barycentric coords; w4 = 1-(x+y+z)
        public uint    TetIndex;
    }

    // ─── ScriptableObject asset ───────────────────────────────────────────────
    [CreateAssetMenu(menuName = "XPBD/TetrahedralMeshAsset", fileName = "NewTetMesh")]
    public class TetrahedralMeshAsset : ScriptableObject
    {
        [Header("Tet Mesh (Physics)")]
        public ParticleData[]    Particles;
        public EdgeData[]        Edges;
        public TetrahedralData[] Tetrahedrals;

        [Header("Render Mesh Deformation")]
        [Tooltip("Set when UseTetDeformation = false (same-resolution path). " +
                 "One uint per render vertex: maps render vertex → tet particle index.")]
        public uint[] OrigIndices;

        [Tooltip("Set when UseTetDeformation = true (barycentric skinning). " +
                 "One entry per high-res render vertex.")]
        public SkinningData[] Skinning;

#if UNITY_EDITOR
        // Convenience: compute rest volumes and edge rest lengths from particle positions.
        // Call this from an Editor tool after populating Particles / Edges / Tetrahedrals.
        [ContextMenu("Recompute Rest State")]
        public void RecomputeRestState()
        {
            // Edge rest lengths
            for (int i = 0; i < Edges.Length; i++)
            {
                Vector3 a = Particles[Edges[i].IndexA].Position;
                Vector3 b = Particles[Edges[i].IndexB].Position;
                Edges[i].RestLen = (a - b).magnitude;
            }

            // Tet rest volumes  (V = dot(cross(e01,e02), e03) / 6)
            for (int i = 0; i < Tetrahedrals.Length; i++)
            {
                Vector3 p0 = Particles[Tetrahedrals[i].I0].Position;
                Vector3 p1 = Particles[Tetrahedrals[i].I1].Position;
                Vector3 p2 = Particles[Tetrahedrals[i].I2].Position;
                Vector3 p3 = Particles[Tetrahedrals[i].I3].Position;

                Vector3 e01 = p1 - p0;
                Vector3 e02 = p2 - p0;
                Vector3 e03 = p3 - p0;

                Tetrahedrals[i].RestVolume = Vector3.Dot(Vector3.Cross(e01, e02), e03) / 6f;
            }

            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[XPBD] Rest state recomputed: {Edges.Length} edges, {Tetrahedrals.Length} tets.");
        }
#endif
    }
}
