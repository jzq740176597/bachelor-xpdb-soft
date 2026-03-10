#if UNITY_EDITOR
using UnityEditor;
#endif
// CachedSdfAsset.cs
// Stores a pre-built Signed Distance Field so TetMeshGenerator can skip the
// expensive BuildSDF step on repeat generations with the same mesh + resolution.
//
// Cache key: (sourceMeshGUID, interiorRes)
// Stored next to the source mesh in the project.

using UnityEngine;

namespace XPBD.Editor
{
	public sealed class CachedSdfAsset : ScriptableObject
	{
		// ── Identity ──────────────────────────────────────────────────────────
		public string SourceMeshGUID;   // AssetDatabase GUID of the source mesh
		public int InteriorRes;      // must match the interiorRes used at build time

		// ── Bounds (tight, no padding) ────────────────────────────────────────
		public Vector3 BoundsMin;
		public Vector3 BoundsSize;

		// ── SDF grid ──────────────────────────────────────────────────────────
		// Flat [sdfRes × sdfRes × sdfRes] array, index = iz*r*r + iy*r + ix.
		// Negative = inside mesh, positive = outside.
		// Built at resolution = interiorRes * 4 for high accuracy.
		public int SdfRes;          // r = interiorRes*4 + 1
		public float[] Data;            // length = SdfRes^3
	}
#if UNITY_EDITOR
	[CustomEditor(typeof(CachedSdfAsset))]
	class TetrahedralMeshAssetEditor : UnityEditor.Editor
	{
		public override void OnInspectorGUI()
		{
			/*Draw-Nothing*/
		}
	}
#endif
}