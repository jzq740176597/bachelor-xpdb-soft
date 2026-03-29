// SdfBaker.cs
//
// Bakes a Mesh into a signed-distance-field grid stored in SdfColliderAsset.
//
// ── Sign determination: two-pass robust method ────────────────────────────────
// Pass 1 — Generalized winding number (Van Oosterom & Strackee):
//   Accumulates signed solid angles. Robust for closed + thin-shell meshes.
//   Threshold: |winding| > π (not 2π) — catches thin shells at grazing angles.
//
// Pass 2 — Flood-fill from grid boundary:
//   Any voxel reachable from the 6 grid-face boundaries without crossing the
//   zero-distance shell is definitively OUTSIDE. This corrects winding-number
//   failures on open meshes (missing end-caps, non-manifold geometry).
//   Algorithm: 6-connected BFS starting from all boundary voxels.
//   A voxel is "blocked" (interior) when its unsigned dist < floodFillShell,
//   which is set to one cell diagonal — just wide enough to stop the fill
//   at the surface without leaking through thin geometry.
//
// The two passes are combined: a voxel is inside only if BOTH agree, OR
// if the winding number is confident (|w| > 2π) regardless of flood-fill.
// This makes the bake work correctly for:
//   • Solid primitives (sphere, cube)           — winding number suffices
//   • Thin shells (pipe, bracket, hollow box)   — flood-fill prevents bore leakage
//   • Open meshes (no bottom cap)               — flood-fill marks open space outside

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
			var tnrm = new Vector3[triCount];
			for (int t = 0; t < triCount; t++)
			{
				var a = verts[tris[t * 3]];
				var b = verts[tris[t * 3 + 1]];
				var c = verts[tris[t * 3 + 2]];
				tv0[t]  = a;
				tv1[t]  = b;
				tv2[t]  = c;
				tnrm[t] = Vector3.Cross(b - a, c - a);
			}

			int     total  = res * res * res;
			var     dist   = new float[total];   // unsigned distance
			var     wind   = new float[total];   // winding number
			Vector3 cellSz = asset.CellSize;

			// ── Pass 1: unsigned distance + winding number ────────────────
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
						float wSum     = 0f;
						for (int t = 0; t < triCount; t++)
						{
							float d2 = PointTriangleDistSq(p, tv0[t], tv1[t], tv2[t]);
							if (d2 < minDist2)
								minDist2 = d2;
							wSum += SignedSolidAngle(p, tv0[t], tv1[t], tv2[t], tnrm[t]);
						}

						int idx = ix + iy * res + iz * res * res;
						dist[idx] = Mathf.Sqrt(minDist2);
						wind[idx] = wSum;
					}
				}
			}

			// ── Pass 2: flood-fill outside from boundary ──────────────────
			// Shell thickness = distance at which flood-fill is blocked.
			// Use half a cell diagonal to catch thin geometry robustly.
			float cellDiag   = cellSz.magnitude;
			float floodShell = cellDiag * 0.5f;

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
					// Blocked by surface (dist < floodShell = near the mesh surface)
					if (dist[nIdx] < floodShell)
						continue;
					outside[nIdx] = true;
					queue.Enqueue(nIdx);
				}
			}

			// ── Combine: assign final signed distance ─────────────────────
			var grid = new float[total];
			for (int i = 0; i < total; i++)
			{
				// Winding number confident inside: |w| > 2π
				bool windingInside   = Mathf.Abs(wind[i]) > Mathf.PI * 2f;
				// Winding number ambiguous inside: π < |w| <= 2π (thin shells)
				bool windingMaybeIn  = Mathf.Abs(wind[i]) > Mathf.PI;
				// Flood-fill says outside
				bool floodOutside    = outside[i];

				bool inside;
				if (windingInside)
					// Strong winding signal — trust it regardless of flood-fill
					inside = true;
				else if (floodOutside)
					// Flood-fill reached here from boundary — definitively outside
					inside = false;
				else
					// Flood-fill didn't reach (enclosed pocket) + maybe winding → inside
					inside = windingMaybeIn;

				grid[i] = inside ? -dist[i] : dist[i];
			}

			asset.SdfGrid = grid;
			Debug.Log($"[SdfBaker] '{mesh.name}' → {res}³ = {total:N0} cells  " +
				$"bounds {bMin:F2} → {bMax:F2}");
		}

		// ── Geometry helpers ──────────────────────────────────────────────────

		static float SignedSolidAngle(Vector3 p,
			Vector3 a, Vector3 b, Vector3 c, Vector3 faceNrm)
		{
			Vector3 ra = a - p, rb = b - p, rc = c - p;
			float la = ra.magnitude, lb = rb.magnitude, lc = rc.magnitude;
			if (la < 1e-10f || lb < 1e-10f || lc < 1e-10f)
				return 0f;
			float num   = Vector3.Dot(ra, Vector3.Cross(rb, rc));
			float denom = la * lb * lc
				+ Vector3.Dot(ra, rb) * lc
				+ Vector3.Dot(rb, rc) * la
				+ Vector3.Dot(rc, ra) * lb;
			if (Mathf.Abs(denom) < 1e-10f)
				return 0f;
			return 2f * Mathf.Atan2(num, denom);
		}

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
