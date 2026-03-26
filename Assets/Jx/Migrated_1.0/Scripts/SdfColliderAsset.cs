// SdfColliderAsset.cs
//
// ScriptableObject that stores a baked 3D signed-distance-field grid for a mesh.
// Used by XpbdSdfCollider to drive GPU collision against arbitrary closed meshes:
// pipes, hollow spheres, cups, irregular terrain — anything a convex hull cannot
// represent.
//
// ── Bake overview ────────────────────────────────────────────────────────────
// The SDF is a uniform 3D grid of float values in the mesh's LOCAL space.
// Each cell stores signed distance to the nearest surface:
//   < 0  →  inside the mesh
//   = 0  →  on the surface
//   > 0  →  outside the mesh
//
// Signed-ness is determined by ray-casting against the mesh (winding number
// parity). This correctly handles:
//   • Convex or concave closed meshes
//   • Hollow interiors (pipe bore, cup inside)
//   • Non-manifold meshes if readable
//
// ── GPU usage ────────────────────────────────────────────────────────────────
// At runtime the float[] grid is uploaded to a ComputeBuffer (flat array).
// The collision shader samples it trilinearly.
// Gradient (= contact normal) is computed via 6-tap central differences.
//
// ── Resolution guide ─────────────────────────────────────────────────────────
//   16  → 4K floats,  16 KB — rough, good for large slow objects
//   32  → 32K floats, 128 KB — default, good for most use cases
//   64  → 512K floats, 2 MB  — fine detail, small colliders
//   128 → 4M floats,  16 MB  — only if you really need sub-mm accuracy

using UnityEngine;

namespace XPBD
{
	[CreateAssetMenu(menuName = "XPBD/SDF Collider Asset", fileName = "MeshSdfCollider")]
	public sealed class SdfColliderAsset : ScriptableObject
	{
		#region Inspector
		[Header("Bake Settings")]
		[Tooltip("Voxel grid resolution per axis. Grid is BakeResolution³. " +
				 "32 = default (128 KB). 64 = fine (2 MB).")]
		[Range(8, 128)]
		public int BakeResolution = 32;

		[Tooltip("World-space padding added around the mesh AABB before voxelisation. " +
				 "Ensures particles approaching from outside still sample a valid gradient.")]
		public float BakePadding = 0.1f;

		// ── Baked data (serialized, filled by editor bake tool) ───────────────

		// Flat SDF grid in LOCAL space: index = x + y*Res + z*Res*Res
		// Value = signed distance to nearest surface in local units.
		// Negative inside, positive outside.
		[HideInInspector] //too large make Inspect TOO SLOW! [3/26/2026 jzq]
		public float[] SdfGrid;

		// Grid dimensions (may differ per axis in future; currently cubic)
		[ReadOnly]
		public int ResX, ResY, ResZ;

		// Local-space AABB of the baked volume (= mesh bounds + BakePadding)
		[ReadOnly]
		public Vector3 BoundsMin;
		[ReadOnly]
		public Vector3 BoundsMax;

		// Cell size in local units (= (BoundsMax - BoundsMin) / Res)
		[ReadOnly]
		public Vector3 CellSize;

		// Source mesh path (for re-bake; runtime only uses SdfGrid)
		[HideInInspector]
		public string SourceMeshGuid;
		#endregion

		// ── Accessors ─────────────────────────────────────────────────────────
		public bool IsBaked => SdfGrid != null && SdfGrid.Length > 0 && SdfGrid.Length == ResX * ResY * ResZ;

		public Vector3 BoundsSize => BoundsMax - BoundsMin;

		// Sample SDF trilinearly at LOCAL-space position p.
		// Returns float.MaxValue if outside the baked volume.
		public float SampleLocal(Vector3 p)
		{
			if (!IsBaked)
				return float.MaxValue;

			Vector3 uvw = new Vector3(
				(p.x - BoundsMin.x) / BoundsSize.x * ResX - 0.5f,
				(p.y - BoundsMin.y) / BoundsSize.y * ResY - 0.5f,
				(p.z - BoundsMin.z) / BoundsSize.z * ResZ - 0.5f);

			int x0 = Mathf.Clamp((int) uvw.x, 0, ResX - 1);
			int y0 = Mathf.Clamp((int) uvw.y, 0, ResY - 1);
			int z0 = Mathf.Clamp((int) uvw.z, 0, ResZ - 1);
			int x1 = Mathf.Min(x0 + 1, ResX - 1);
			int y1 = Mathf.Min(y0 + 1, ResY - 1);
			int z1 = Mathf.Min(z0 + 1, ResZ - 1);

			float tx = uvw.x - x0;
			float ty = uvw.y - y0;
			float tz = uvw.z - z0;

			return Trilinear(
				SdfGrid[Idx(x0, y0, z0)], SdfGrid[Idx(x1, y0, z0)],
				SdfGrid[Idx(x0, y1, z0)], SdfGrid[Idx(x1, y1, z0)],
				SdfGrid[Idx(x0, y0, z1)], SdfGrid[Idx(x1, y0, z1)],
				SdfGrid[Idx(x0, y1, z1)], SdfGrid[Idx(x1, y1, z1)],
				tx, ty, tz);
		}

		int Idx(int x, int y, int z) => x + y * ResX + z * ResX * ResY;

		static float Trilinear(
			float c000, float c100, float c010, float c110,
			float c001, float c101, float c011, float c111,
			float tx, float ty, float tz)
		{
			return Mathf.Lerp(
				Mathf.Lerp(Mathf.Lerp(c000, c100, tx), Mathf.Lerp(c010, c110, tx), ty),
				Mathf.Lerp(Mathf.Lerp(c001, c101, tx), Mathf.Lerp(c011, c111, tx), ty),
				tz);
		}
	}
}
