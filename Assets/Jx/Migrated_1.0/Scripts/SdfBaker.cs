// SdfBaker.cs
//
// Bakes a Mesh into a signed-distance-field grid stored in SdfColliderAsset.
//
// ── Sign determination: flood-fill only ───────────────────────────────────────
// Pass 1 — Unsigned distance:
//   Per-voxel minimum distance to any mesh triangle. Always positive.
//
// Pass 2 — Flood-fill from grid boundary (6-connected BFS):
//   Seeds all 6 grid-face boundary voxels that are not too close to the surface,
//   then propagates inward. Any voxel reachable from the boundary without
//   crossing the surface shell is OUTSIDE.
//
//   A voxel stops the BFS ("blocked") when dist < floodShell, where floodShell
//   = half the shortest cell axis. Using the minimum axis (not the diagonal)
//   ensures the fill threads through narrow bores at low resolution.
//
//   After BFS: not-reached voxels ARE the mesh wall material → sign negative.
//
// ── Why winding number was removed ────────────────────────────────────────────
// The original design combined flood-fill with a winding-number gate:
//   inside = windingInside  OR  (!floodOutside AND windingMaybeIn)
// This fails completely for open-ended meshes (e.g. a pipe with no end-caps):
//   • Winding number → ~0 everywhere (solid angles from opposite walls cancel).
//   • windingInside=false, windingMaybeIn=false for ALL voxels.
//   • Even wall-material voxels enclosed by the flood-fill shell get inside=false.
//   • Result: zero negative voxels, pure unsigned SDF, no collision response.
//
// The flood-fill alone is the correct and sufficient criterion:
//   • Open bore   → fill enters through open pipe ends → bore marked outside ✓
//   • Wall material → blocked by shell → not reached → signed negative ✓
//   • Outside space → fill reaches from boundary → signed positive ✓
//   • Solid meshes → fill stops at surface everywhere → interior signed negative ✓

using System.Collections.Generic;
using UnityEngine;

namespace XPBD
{
	public static class SdfBaker
	{
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

			int   res  = Mathf.Max(asset.BakeResolution, 4);
			float pad  = asset.BakePadding;
			var   bnds = mesh.bounds;
			Vector3 bMin = bnds.min - Vector3.one * pad;
			Vector3 bMax = bnds.max + Vector3.one * pad;
			Vector3 bSz  = bMax - bMin;

			asset.ResX      = res;
			asset.ResY      = res;
			asset.ResZ      = res;
			asset.BoundsMin = bMin;
			asset.BoundsMax = bMax;
			// Corner layout: res corners span [bMin, bMax] with (res-1) intervals.
			asset.CellSize  = new Vector3(
				bSz.x / Mathf.Max(res - 1, 1),
				bSz.y / Mathf.Max(res - 1, 1),
				bSz.z / Mathf.Max(res - 1, 1));

			var verts    = mesh.vertices;
			var tris     = mesh.triangles;
			int triCount = tris.Length / 3;

			var tv0  = new Vector3[triCount];
			var tv1  = new Vector3[triCount];
			var tv2  = new Vector3[triCount];
			for (int t = 0; t < triCount; t++)
			{
				tv0[t] = verts[tris[t * 3]];
				tv1[t] = verts[tris[t * 3 + 1]];
				tv2[t] = verts[tris[t * 3 + 2]];
			}

			int     total  = res * res * res;
			var     dist   = new float[total];   // unsigned distance
			Vector3 cellSz = asset.CellSize;

			// ── Pass 1: unsigned distance ─────────────────────────────────
			for (int iz = 0; iz < res; iz++)
			{
				for (int iy = 0; iy < res; iy++)
				{
					for (int ix = 0; ix < res; ix++)
					{
						Vector3 p = new Vector3(
							bMin.x + ix * cellSz.x,
							bMin.y + iy * cellSz.y,
							bMin.z + iz * cellSz.z);

						float minDist2 = float.MaxValue;
						for (int t = 0; t < triCount; t++)
						{
							float d2 = PointTriangleDistSq(p, tv0[t], tv1[t], tv2[t]);
							if (d2 < minDist2)
								minDist2 = d2;
						}

						int idx = ix + iy * res + iz * res * res;
						dist[idx] = Mathf.Sqrt(minDist2);
					}
				}
			}

			// ── Pass 2: flood-fill outside from boundary ──────────────────
			// Block the BFS at voxels closer than half the shortest cell axis.
			// Using the minimum axis (not the diagonal) lets the fill thread
			// through narrow bores without being blocked by overly thick walls.
			float floodShell = Mathf.Min(cellSz.x, Mathf.Min(cellSz.y, cellSz.z)) * 0.5f;

			// outside[i] = true if voxel is confirmed OUTSIDE via flood fill
			var outside = new bool[total];
			var queue   = new Queue<int>(total / 4);

			// Seed: all 6 boundary faces are outside (padding ensures they are)
			for (int iz = 0; iz < res; iz++)
			{
				for (int iy = 0; iy < res; iy++)
				{
					for (int ix = 0; ix < res; ix++)
					{
						bool isBoundary = ix == 0 || ix == res - 1
								       || iy == 0 || iy == res - 1
								       || iz == 0 || iz == res - 1;
						if (!isBoundary)
							continue;
						int idx = ix + iy * res + iz * res * res;
						if (dist[idx] >= floodShell)
						{
							outside[idx] = true;
							queue.Enqueue(idx);
						}
					}
				}
			}

			// 6-connected BFS
			int[] dx = { 1,-1, 0, 0, 0, 0 };
			int[] dy = { 0, 0, 1,-1, 0, 0 };
			int[] dz = { 0, 0, 0, 0, 1,-1 };

			while (queue.Count > 0)
			{
				int cur = queue.Dequeue();
				int cz  = cur / (res * res);
				int cy  = (cur / res) % res;
				int cx  = cur % res;

				for (int d = 0; d < 6; d++)
				{
					int nx = cx + dx[d];
					int ny = cy + dy[d];
					int nz = cz + dz[d];
					if (nx < 0 || nx >= res || ny < 0 || ny >= res || nz < 0 || nz >= res)
						continue;
					int nIdx = nx + ny * res + nz * res * res;
					if (outside[nIdx])
						continue;
					// Blocked by surface shell — this voxel is wall material
					if (dist[nIdx] < floodShell)
						continue;
					outside[nIdx] = true;
					queue.Enqueue(nIdx);
				}
			}

			// ── Combine: sign = flood-fill result only ────────────────────
			// Voxels not reached by flood-fill ARE the mesh wall material.
			// This works for both open meshes (pipe bores entered through open
			// ends → bore correctly stays outside) and closed meshes (fill
			// stops at all surfaces → interior correctly stays inside).
			// The winding-number gate was removed because it breaks completely
			// for open-ended meshes where winding → 0 everywhere.
			var grid = new float[total];
			for (int i = 0; i < total; i++)
				grid[i] = outside[i] ? dist[i] : -dist[i];

			asset.SdfGrid = grid;
			Debug.Log($"[SdfBaker] '{mesh.name}' → {res}³ = {total:N0} cells  " +
				$"bounds {bMin:F2} → {bMax:F2}");
		}

		// ── Geometry helpers ──────────────────────────────────────────────────

		static float PointTriangleDistSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
		{
			Vector3 ab = b - a, ac = c - a, ap = p - a;
			float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
			if (d1 <= 0f && d2 <= 0f) return (p - a).sqrMagnitude;

			Vector3 bp = p - b;
			float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
			if (d3 >= 0f && d4 <= d3) return (p - b).sqrMagnitude;

			Vector3 cp = p - c;
			float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
			if (d6 >= 0f && d5 <= d6) return (p - c).sqrMagnitude;

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
			float den = 1f / (va + vb + vc);
			return (p - (a + ab * (vb * den) + ac * (vc * den))).sqrMagnitude;
		}
	}
}
