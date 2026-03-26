// SdfBaker.cs
//
// CPU utility that voxelises a Mesh into a signed-distance-field grid and
// stores the result in an SdfColliderAsset.
//
// ── Algorithm ────────────────────────────────────────────────────────────────
// For each voxel centre (local space):
//   1. Find the nearest triangle and compute unsigned distance to it.
//   2. Determine sign via ray-casting (parity of axis-aligned ray vs triangles).
//      Odd intersection count → inside → negative distance.
//
// This handles:
//   • Concave meshes (a convex hull would miss all interior features)
//   • Hollow geometry (pipe bore, cup inside) — interior is negative
//   • Any topology that is closed and readable
//
// The brute-force O(voxels × triangles) is acceptable for bake-time.
// At Res=32 and a 500-tri mesh: 32³ × 500 = ~16M ops → < 1s on desktop.
// At Res=64: ~130M ops → a few seconds. Run from editor, not at runtime.
//
// ── Sign convention ──────────────────────────────────────────────────────────
// Negative = inside (penetrating). Positive = outside (safe).
// GPU test: sdf < 0 → contact. Correct normal = gradient (points outward).

using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	public static class SdfBaker
	{
		/// <summary>
		/// Bake the SDF of <paramref name="mesh"/> (in LOCAL space) into
		/// <paramref name="asset"/>. Overwrites all baked fields.
		/// Call from an Editor tool or from XpbdSdfCollider.Awake when no
		/// baked data exists yet (runtime fallback, slow).
		/// </summary>
		public static void Bake(Mesh mesh, SdfColliderAsset asset)
		{
			if (mesh == null || asset == null)
			{
				Debug.LogError("[SdfBaker] Null mesh or asset.");
				return;
			}
			if (!mesh.isReadable)
			{
				Debug.LogError($"[SdfBaker] Mesh '{mesh.name}' is not readable. " +
					"Enable Read/Write in import settings.", mesh);
				return;
			}

			int res     = asset.BakeResolution;
			float pad   = asset.BakePadding;
			var  bounds = mesh.bounds;
			Vector3 bMin = bounds.min - Vector3.one * pad;
			Vector3 bMax = bounds.max + Vector3.one * pad;
			Vector3 bSz  = bMax - bMin;

			asset.ResX     = res;
			asset.ResY     = res;
			asset.ResZ     = res;
			asset.BoundsMin = bMin;
			asset.BoundsMax = bMax;
			asset.CellSize  = new Vector3(bSz.x / res, bSz.y / res, bSz.z / res);

			var verts = mesh.vertices;
			var tris  = mesh.triangles;
			int triCount = tris.Length / 3;

			// Pre-build triangle list for cache coherency
			var triV0 = new Vector3[triCount];
			var triV1 = new Vector3[triCount];
			var triV2 = new Vector3[triCount];
			for (int t = 0; t < triCount; t++)
			{
				triV0[t] = verts[tris[t * 3]];
				triV1[t] = verts[tris[t * 3 + 1]];
				triV2[t] = verts[tris[t * 3 + 2]];
			}

			int total   = res * res * res;
			var grid    = new float[total];
			Vector3 cellSz = asset.CellSize;

			// Bake: brute-force per voxel
			for (int iz = 0; iz < res; iz++)
			{
				for (int iy = 0; iy < res; iy++)
				{
					for (int ix = 0; ix < res; ix++)
					{
						// Voxel centre in local space
						Vector3 p = new Vector3(
							bMin.x + (ix + 0.5f) * cellSz.x,
							bMin.y + (iy + 0.5f) * cellSz.y,
							bMin.z + (iz + 0.5f) * cellSz.z);

						float minDist2 = float.MaxValue;
						for (int t = 0; t < triCount; t++)
						{
							float d2 = PointTriangleDistSq(p, triV0[t], triV1[t], triV2[t]);
							if (d2 < minDist2)
								minDist2 = d2;
						}

						float unsignedDist = Mathf.Sqrt(minDist2);
						bool  inside       = IsInsideMesh(p, triV0, triV1, triV2, triCount);
						grid[ix + iy * res + iz * res * res] = inside ? -unsignedDist : unsignedDist;
					}
				}
			}

			asset.SdfGrid = grid;
			Debug.Log($"[SdfBaker] Baked '{mesh.name}' → {res}³ grid, " +
				$"{total} cells, bounds {bMin:F2}–{bMax:F2}");
		}

		// ── Geometry helpers ──────────────────────────────────────────────────

		// Squared distance from point p to triangle (a,b,c).
		// Standard closest-point-on-triangle test.
		static float PointTriangleDistSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
		{
			Vector3 ab = b - a, ac = c - a, ap = p - a;
			float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
			if (d1 <= 0f && d2 <= 0f)
				return (p - a).sqrMagnitude;

			Vector3 bp = p - b;
			float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
			if (d3 >= 0f && d4 <= d3)
				return (p - b).sqrMagnitude;

			Vector3 cp = p - c;
			float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
			if (d6 >= 0f && d5 <= d6)
				return (p - c).sqrMagnitude;

			float vc = d1 * d4 - d3 * d2;
			if (vc <= 0f && d1 >= 0f && d3 <= 0f)
			{
				float v = d1 / (d1 - d3);
				return (p - (a + v * ab)).sqrMagnitude;
			}

			float vb = d5 * d2 - d1 * d6;
			if (vb <= 0f && d2 >= 0f && d6 <= 0f)
			{
				float w = d2 / (d2 - d6);
				return (p - (a + w * ac)).sqrMagnitude;
			}

			float va = d3 * d6 - d5 * d4;
			if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
			{
				float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
				return (p - (b + w * (c - b))).sqrMagnitude;
			}

			float denom = 1f / (va + vb + vc);
			float vv = vb * denom;
			float ww = vc * denom;
			return (p - (a + ab * vv + ac * ww)).sqrMagnitude;
		}

		// Ray-cast sign test: shoot ray from p along +X and count intersections.
		// Odd count → inside. Uses a slight Y-jitter to avoid degenerate edge hits.
		static bool IsInsideMesh(Vector3 p,
			Vector3[] v0, Vector3[] v1, Vector3[] v2, int triCount)
		{
			int hits = 0;
			// Slightly offset ray origin to avoid exact edge hits
			float py = p.y + 1e-5f;
			float pz = p.z + 2e-5f;

			for (int t = 0; t < triCount; t++)
			{
				Vector3 a = v0[t], b = v1[t], c = v2[t];

				// Check if any vertex is behind the ray start in X
				// and the triangle spans the ray (Y,Z within triangle's projected bounds)
				float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
				float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
				float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
				float maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));

				if (py < minY || py > maxY || pz < minZ || pz > maxZ)
					continue;

				// Compute ray-triangle intersection (Möller–Trumbore, +X axis)
				Vector3 e1 = b - a, e2 = c - a;
				Vector3 h  = new Vector3(0, -e2.z, e2.y); // cross(+X, e2)
				float   det = Vector3.Dot(e1, h);
				if (Mathf.Abs(det) < 1e-8f)
					continue;

				float invDet = 1f / det;
				Vector3 s  = new Vector3(p.x - a.x, py - a.y, pz - a.z);
				float   u  = Vector3.Dot(s, h) * invDet;
				if (u < 0f || u > 1f)
					continue;

				Vector3 q = Vector3.Cross(s, e1);
				float   v = new Vector3(0, -1, 0).x * q.x   // dot(+X, q) → q.x
					+ 0f * q.y + 0f * q.z;
				// dot(rayDir=(1,0,0), q) = q.x
				v = q.x * invDet;
				if (v < 0f || u + v > 1f)
					continue;

				float tHit = Vector3.Dot(e2, q) * invDet;
				if (tHit > 0f)
					hits++;
			}
			return (hits % 2) == 1;
		}
	}
}
