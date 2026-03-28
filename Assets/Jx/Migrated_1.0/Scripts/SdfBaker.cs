// SdfBaker.cs
//
// CPU utility — bakes a Mesh into a signed-distance-field grid stored in
// SdfColliderAsset. Handles both solid primitives AND thin-shell hollow
// geometry (pipes, cups, cylinders without end caps).
//
// ── Sign determination strategy ──────────────────────────────────────────────
// Simple ray-parity fails on thin-shell meshes because:
//   • Meshes often lack end-caps → not manifold → wrong parity
//   • A ray through a thin shell hits both the front AND back face → even = "outside"
//     but the voxel is actually inside the shell material
//   • Coincident/near-coincident faces from inner+outer shell surfaces confuse parity
//
// We use ANGLE-WEIGHTED PSEUDONORMAL (generalized winding number approximation):
//   For each voxel, accumulate the solid angle subtended by each triangle as seen
//   from the voxel, weighted by the triangle's normal direction.
//   Σ solidAngle * sign(dot(triNormal, triCentre-p)) > 0 → inside
//
// This is O(voxels × triangles) but much more robust than parity for:
//   • Non-manifold meshes
//   • Meshes with holes / missing caps
//   • Thin shells (the inner and outer faces contribute consistently)
//   • Meshes with flipped triangles (they subtract rather than add)
//
// ── Unsigned distance ────────────────────────────────────────────────────────
// Standard closest-point-on-triangle test, brute-force over all triangles.

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
			asset.CellSize  = new Vector3(bSz.x / res, bSz.y / res, bSz.z / res);

			var verts    = mesh.vertices;
			var tris     = mesh.triangles;
			int triCount = tris.Length / 3;

			// Pre-build per-triangle data
			var tv0   = new Vector3[triCount];
			var tv1   = new Vector3[triCount];
			var tv2   = new Vector3[triCount];
			var tnrm  = new Vector3[triCount];  // unnormalized face normal
			for (int t = 0; t < triCount; t++)
			{
				var a = verts[tris[t * 3]];
				var b = verts[tris[t * 3 + 1]];
				var c = verts[tris[t * 3 + 2]];
				tv0[t]  = a;
				tv1[t]  = b;
				tv2[t]  = c;
				tnrm[t] = Vector3.Cross(b - a, c - a); // not normalized; magnitude = 2*area
			}

			int   total  = res * res * res;
			var   grid   = new float[total];
			Vector3 cellSz = asset.CellSize;

			for (int iz = 0; iz < res; iz++)
			{
				for (int iy = 0; iy < res; iy++)
				{
					for (int ix = 0; ix < res; ix++)
					{
						Vector3 p = new Vector3(
							bMin.x + (ix + 0.5f) * cellSz.x,
							bMin.y + (iy + 0.5f) * cellSz.y,
							bMin.z + (iz + 0.5f) * cellSz.z);

						float minDist2    = float.MaxValue;
						float windingSum  = 0f;

						for (int t = 0; t < triCount; t++)
						{
							// Unsigned distance
							float d2 = PointTriangleDistSq(p, tv0[t], tv1[t], tv2[t]);
							if (d2 < minDist2)
								minDist2 = d2;

							// Generalized winding number contribution
							// Solid angle of triangle as seen from p, signed by normal
							windingSum += SignedSolidAngle(p, tv0[t], tv1[t], tv2[t], tnrm[t]);
						}

						// windingSum ≈ 4π if inside a closed mesh, ≈ 0 if outside.
						// Threshold at 2π (half). Works for non-manifold / open meshes.
						bool inside = windingSum > Mathf.PI * 2f
								   || windingSum < -Mathf.PI * 2f;

						float unsignedDist = Mathf.Sqrt(minDist2);
						grid[ix + iy * res + iz * res * res] = inside ? -unsignedDist : unsignedDist;
					}
				}
			}

			asset.SdfGrid = grid;
			Debug.Log($"[SdfBaker] '{mesh.name}' → {res}³ = {total:N0} cells  " +
				$"bounds {bMin:F2} → {bMax:F2}");
		}

		// ── Geometry helpers ──────────────────────────────────────────────────

		// Signed solid angle contribution of triangle (a,b,c) to point p.
		// Based on the Van Oosterom & Strackee formula.
		// Returns value in range [-2π, 2π]; sum over all triangles ≈ ±4π inside.
		static float SignedSolidAngle(Vector3 p,
			Vector3 a, Vector3 b, Vector3 c, Vector3 faceNrm)
		{
			Vector3 ra = a - p;
			Vector3 rb = b - p;
			Vector3 rc = c - p;

			float la = ra.magnitude;
			float lb = rb.magnitude;
			float lc = rc.magnitude;

			if (la < 1e-10f || lb < 1e-10f || lc < 1e-10f)
				return 0f;

			// Numerator: triple product ra·(rb×rc)
			float num = Vector3.Dot(ra, Vector3.Cross(rb, rc));

			// Denominator: la*lb*lc + dot products
			float denom = la * lb * lc
				+ Vector3.Dot(ra, rb) * lc
				+ Vector3.Dot(rb, rc) * la
				+ Vector3.Dot(rc, ra) * lb;

			if (Mathf.Abs(denom) < 1e-10f)
				return 0f;

			// 2 * atan2(num, denom) gives the solid angle with sign from face orientation
			return 2f * Mathf.Atan2(num, denom);
		}

		// Squared distance from point p to triangle (a,b,c).
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

			float den = 1f / (va + vb + vc);
			return (p - (a + ab * (vb * den) + ac * (vc * den))).sqrMagnitude;
		}
	}
}
