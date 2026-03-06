// TetMeshImporter.cs
// Unity Editor window  →  XPBD → Import Tet Mesh...
//
// Converts the ORIGINAL C++ thesis project OBJ files directly into the
// TetrahedralMeshAsset ScriptableObject + Unity Mesh that the rest of the
// port needs. No manual data entry. No Blender plugin needed at import time.
//
// What it replicates (from ResourceManager.cpp):
//   loadMeshOBJ()              → builds Unity Mesh (render mesh)
//   loadTetrahedralMeshOBJ()   → fills Particles / Tets / Edges in the asset
//   getSoftBody() bary weights → fills asset.Skinning[]  (res != 100)
//
// PLACE THIS FILE inside any Assets/.../Editor/ folder.
// Unity compiles it as Editor-only automatically.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPBD.Editor
{
	public class TetMeshImporter : EditorWindow
	{
		// ── Window fields ─────────────────────────────────────────────────────
		string _renderObjPath = "";
		string _tetObjPath = "";
		string _outputFolder = "Assets/Jx/Gen_TetModels";
		string _assetName = "sphere_5";
		bool _useTetDeform = true;

		string _statusMsg = "";
		bool _statusOk = true;

		[MenuItem("XPBD/Import Tet Mesh...")]
		public static void ShowWindow() =>
			GetWindow<TetMeshImporter>(false, "XPBD Importer", true);

		// ── GUI ───────────────────────────────────────────────────────────────
		void OnGUI()
		{
			GUILayout.Space(8);
			GUILayout.Label("XPBD Soft Body Importer", EditorStyles.boldLabel);
			GUILayout.Label("Converts thesis project OBJ files → TetrahedralMeshAsset + Mesh",
				EditorStyles.miniLabel);
			GUILayout.Space(10);

			// Render OBJ
			EditorGUILayout.LabelField("Render Mesh OBJ   (assets/models/sphere.obj)");
			using (new GUILayout.HorizontalScope())
			{
				_renderObjPath = EditorGUILayout.TextField(_renderObjPath);
				if (GUILayout.Button("...", GUILayout.Width(28)))
					_renderObjPath = EditorUtility.OpenFilePanel(
						"Select render OBJ (triangulated)", "", "obj");
			}
			GUILayout.Space(6);

			// Tet OBJ
			EditorGUILayout.LabelField("Tet Mesh OBJ   (assets/tet_models/sphere/5.obj)");
			using (new GUILayout.HorizontalScope())
			{
				_tetObjPath = EditorGUILayout.TextField(_tetObjPath);
				if (GUILayout.Button("...", GUILayout.Width(28)))
					_tetObjPath = EditorUtility.OpenFilePanel(
						"Select tet OBJ (quad faces)", "", "obj");
			}
			GUILayout.Space(6);

			_useTetDeform = EditorGUILayout.Toggle(
				new GUIContent("Use Tet Deformation",
					"Tick for resolution 5/10/25/50 (barycentric skinning).\n" +
					"Untick for resolution 100 (same-res direct index map)."),
				_useTetDeform);
			GUILayout.Space(8);

			_assetName = EditorGUILayout.TextField("Asset name", _assetName);
			_outputFolder = EditorGUILayout.TextField("Output folder", _outputFolder);
			GUILayout.Space(12);

			bool canImport = File.Exists(_renderObjPath) && File.Exists(_tetObjPath)
							 && _assetName.Length > 0;
			GUI.enabled = canImport;
			if (GUILayout.Button("Import", GUILayout.Height(36)))
				Import();
			GUI.enabled = true;

			if (_statusMsg.Length > 0)
			{
				GUILayout.Space(8);
				var style = new GUIStyle(EditorStyles.helpBox);
				GUI.color = _statusOk ? Color.green : Color.red;
				GUILayout.Label(_statusMsg, style);
				GUI.color = Color.white;
			}
		}

		// ── Main import entry ─────────────────────────────────────────────────
		void Import()
		{
			_statusMsg = "";
			try
			{
				EditorUtility.DisplayProgressBar("XPBD Import", "Parsing render mesh...", 0.10f);
				var renderData = ParseRenderOBJ(_renderObjPath);

				EditorUtility.DisplayProgressBar("XPBD Import", "Parsing tet mesh...", 0.30f);
				var tetData = ParseTetOBJ(_tetObjPath);

				GPUSkinningInfo[] skinning = null;
				uint[] origIdx = null;

				if (_useTetDeform)
				{
					EditorUtility.DisplayProgressBar("XPBD Import",
						$"Computing bary weights ({renderData.positions.Length} verts)...", 0.50f);
					skinning = ComputeSkinning(renderData.positions, tetData);
				}
				else
				{
					origIdx = renderData.origIndices;
				}

				EditorUtility.DisplayProgressBar("XPBD Import", "Building Unity Mesh...", 0.80f);
				Mesh unityMesh = BuildUnityMesh(renderData);

				EditorUtility.DisplayProgressBar("XPBD Import", "Saving assets...", 0.92f);
				EnsureFolder(_outputFolder);

				// Save Unity mesh
				string meshPath = $"{_outputFolder}/{_assetName}_render.mesh";
				AssetDatabase.DeleteAsset(meshPath);
				AssetDatabase.CreateAsset(unityMesh, meshPath);

				// Build and save TetrahedralMeshAsset
				var asset = ScriptableObject.CreateInstance<TetrahedralMeshAsset>();
				// --- Particles ---
				asset.Particles = new ParticleData[tetData.particles.Length];
				for (int i = 0; i < tetData.particles.Length; i++)
					asset.Particles[i] = new ParticleData
					{
						Position = tetData.particles[i].position,
						InvMass = tetData.particles[i].invMass
					};
				// --- Edges ---
				asset.Edges = new EdgeData[tetData.edges.Length];
				for (int i = 0; i < tetData.edges.Length; i++)
					asset.Edges[i] = new EdgeData
					{
						IndexA = tetData.edges[i].a,
						IndexB = tetData.edges[i].b,
						RestLen = tetData.edges[i].restLen
					};
				// --- Tets ---
				asset.Tetrahedrals = new TetrahedralData[tetData.tets.Length];
				for (int i = 0; i < tetData.tets.Length; i++)
					asset.Tetrahedrals[i] = new TetrahedralData
					{
						I0 = tetData.tets[i].i0,
						I1 = tetData.tets[i].i1,
						I2 = tetData.tets[i].i2,
						I3 = tetData.tets[i].i3,
						RestVolume = tetData.tets[i].restVolume
					};
				// --- Deform path ---
				if (_useTetDeform)
				{
					asset.Skinning = new SkinningData[skinning.Length];
					for (int i = 0; i < skinning.Length; i++)
						asset.Skinning[i] = new SkinningData
						{
							Weights = skinning[i].weights,
							TetIndex = skinning[i].tetIndex
						};
				}
				else
				{
					asset.OrigIndices = origIdx;
				}

				string assetPath = $"{_outputFolder}/{_assetName}.asset";
				AssetDatabase.DeleteAsset(assetPath);
				AssetDatabase.CreateAsset(asset, assetPath);
				AssetDatabase.SaveAssets();
				AssetDatabase.Refresh();

				EditorUtility.ClearProgressBar();
				_statusOk = true;
				_statusMsg = $"✓  {_assetName}  |  particles:{tetData.particles.Length}" +
							 $"  edges:{tetData.edges.Length}  tets:{tetData.tets.Length}" +
							 $"  renderVerts:{renderData.positions.Length}";
				Selection.activeObject = asset;
				Debug.Log($"[XPBD] Import complete → {assetPath}  {_statusMsg}");
			}
			catch (Exception ex)
			{
				EditorUtility.ClearProgressBar();
				_statusOk = false;
				_statusMsg = "✗  " + ex.Message;
				Debug.LogError("[XPBD] Import failed: " + ex);
			}
		}

		// ═════════════════════════════════════════════════════════════════════
		// Replicates  ResourceManager::loadMeshOBJ()
		// Reads triangulated OBJ (p/n/t face format).
		// Deduplicates vertices by (posIdx, normIdx, uvIdx) triple.
		// ═════════════════════════════════════════════════════════════════════
		struct RenderData
		{
			public Vector3[] positions;
			public Vector3[] normals;
			public Vector2[] uvs;
			public int[] indices;
			public uint[] origIndices;  // 0-based raw position index per render vertex
		}

		static RenderData ParseRenderOBJ(string path)
		{
			// Raw 1-based lists  (index 0 = sentinel)
			var rPos = new List<Vector3> { Vector3.zero };
			var rNorm = new List<Vector3> { Vector3.zero };
			var rUV = new List<Vector2> { Vector2.zero };
			var faces = new List<(int p, int n, int t)[]>();

			foreach (var raw in File.ReadLines(path))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
					continue;
				var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				switch (tok[0])
				{
					case "v":
						rPos.Add(new Vector3(
							float.Parse(tok[1]), float.Parse(tok[2]), float.Parse(tok[3])));
						break;
					case "vn":
						rNorm.Add(new Vector3(
							float.Parse(tok[1]), float.Parse(tok[2]), float.Parse(tok[3])));
						break;
					case "vt":
						rUV.Add(new Vector2(float.Parse(tok[1]), float.Parse(tok[2])));
						break;
					case "f":
						var face = new (int p, int n, int t)[tok.Length - 1];
						for (int i = 1; i < tok.Length; i++)
						{
							var parts = tok[i].Split('/');
							int p = int.Parse(parts[0]);
							int tx = parts.Length > 1 && parts[1].Length > 0
									 ? int.Parse(parts[1]) : 0;
							int n = parts.Length > 2 && parts[2].Length > 0
									 ? int.Parse(parts[2]) : 0;
							face[i - 1] = (p, n, tx);
						}
						faces.Add(face);
						break;
				}
			}

			if (faces.Count == 0)
				throw new Exception($"No faces found in render OBJ: {path}");
			if (faces[0].Length != 3)
				throw new Exception("Render OBJ faces must be triangulated (3 vertices per face).");

			// Dedup by (p,n,t) key — identical to C++ unordered_map<string,uint32_t>
			var unique = new Dictionary<long, int>();
			var oPos = new List<Vector3>();
			var oNorm = new List<Vector3>();
			var oUV = new List<Vector2>();
			var oOrig = new List<uint>();
			var oIdx = new List<int>();

			foreach (var face in faces)
				foreach (var (p, n, t) in face)
				{
					// Pack (p,n,t) into a single long as a fast key
					long key = ((long) p << 42) | ((long) n << 21) | (long) t;
					if (!unique.TryGetValue(key, out int idx))
					{
						idx = oPos.Count;
						unique[key] = idx;
						oPos.Add(rPos[p]);
						oNorm.Add(n > 0 ? rNorm[n] : Vector3.up);
						oUV.Add(t > 0 ? rUV[t] : Vector2.zero);
						oOrig.Add((uint) (p - 1));
					}
					oIdx.Add(idx);
				}

			return new RenderData
			{
				positions = oPos.ToArray(),
				normals = oNorm.ToArray(),
				uvs = oUV.ToArray(),
				indices = oIdx.ToArray(),
				origIndices = oOrig.ToArray()
			};
		}

		// ═════════════════════════════════════════════════════════════════════
		// Replicates  ResourceManager::loadTetrahedralMeshOBJ()
		// Reads quad-face OBJ (each quad = one tet, no normals/UVs).
		// Computes: restVolume, invMass (volume-weighted), deduplicated edges.
		// ═════════════════════════════════════════════════════════════════════
		struct TetParticle
		{
			public Vector3 position; public float invMass;
		}
		struct TetEdge
		{
			public uint a, b; public float restLen;
		}
		struct Tet
		{
			public uint i0, i1, i2, i3; public float restVolume;
		}

		struct TetData
		{
			public TetParticle[] particles;
			public Tet[] tets;
			public TetEdge[] edges;
		}

		static TetData ParseTetOBJ(string path)
		{
			var rPos = new List<Vector3> { Vector3.zero }; // 1-based
			var rFace = new List<int[]>();

			foreach (var raw in File.ReadLines(path))
			{
				var line = raw.Trim();
				if (line.Length == 0 || line[0] == '#')
					continue;
				var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (tok[0] == "v")
					rPos.Add(new Vector3(
						float.Parse(tok[1]), float.Parse(tok[2]), float.Parse(tok[3])));
				else if (tok[0] == "f")
				{
					var ids = new int[tok.Length - 1];
					for (int i = 1; i < tok.Length; i++)
						ids[i - 1] = int.Parse(tok[i].Split('/')[0]);
					if (ids.Length != 4)
						throw new Exception($"Tet OBJ faces must have 4 vertices (got {ids.Length}).");
					rFace.Add(ids);
				}
			}

			int n = rPos.Count - 1;
			var particles = new TetParticle[n];
			for (int i = 0; i < n; i++)
				particles[i] = new TetParticle { position = rPos[i + 1], invMass = 0f };

			var tets = new Tet[rFace.Count];
			var edgeMap = new Dictionary<long, TetEdge>();

			for (int i = 0; i < rFace.Count; i++)
			{
				uint i0 = (uint) (rFace[i][0] - 1), i1 = (uint) (rFace[i][1] - 1),
					 i2 = (uint) (rFace[i][2] - 1), i3 = (uint) (rFace[i][3] - 1);

				Vector3 p0 = particles[i0].position, p1 = particles[i1].position,
						p2 = particles[i2].position, p3 = particles[i3].position;

				float vol = Vector3.Dot(
					Vector3.Cross(p1 - p0, p2 - p0), p3 - p0) / 6f;

				tets[i] = new Tet { i0 = i0, i1 = i1, i2 = i2, i3 = i3, restVolume = vol };

				// Mass accumulation (matches C++: only if volume > 0)
				if (vol > 0f)
				{
					float mass = vol / 4f;
					particles[i0].invMass += mass;
					particles[i1].invMass += mass;
					particles[i2].invMass += mass;
					particles[i3].invMass += mass;
				}

				// 6 unique edges per tet
				uint[] vi = { i0, i1, i2, i3 };
				for (int j = 0; j < 3; j++)
					for (int k = j + 1; k < 4; k++)
					{
						uint a = Math.Min(vi[j], vi[k]), b = Math.Max(vi[j], vi[k]);
						long ek = ((long) a << 32) | b;
						if (!edgeMap.ContainsKey(ek))
							edgeMap[ek] = new TetEdge
							{
								a = a,
								b = b,
								restLen = Vector3.Distance(particles[a].position,
														   particles[b].position)
							};
					}
			}

			// Finalize invMass  (matches C++)
			for (int i = 0; i < n; i++)
				particles[i].invMass = particles[i].invMass > 0f
					? 1f / particles[i].invMass : 0f;

			return new TetData
			{
				particles = particles,
				tets = tets,
				edges = new List<TetEdge>(edgeMap.Values).ToArray()
			};
		}

		// ═════════════════════════════════════════════════════════════════════
		// Replicates  ResourceManager::getSoftBody()  — barycentric weight block
		// Matches the C++ SpatialHash + matrix-inverse logic exactly.
		// ═════════════════════════════════════════════════════════════════════
		static GPUSkinningInfo[] ComputeSkinning(Vector3[] renderPos, TetData tet)
		{
			int n = renderPos.Length;
			var skin = new GPUSkinningInfo[n];
			var minDist = new float[n];
			for (int i = 0; i < n; i++)
				minDist[i] = float.MaxValue;

			var hash = new SpatialHash(0.25f, renderPos);

			for (int i = 0; i < tet.tets.Length; i++)
			{
				var t = tet.tets[i];
				Vector3 p0 = tet.particles[t.i0].position,
						 p1 = tet.particles[t.i1].position,
						 p2 = tet.particles[t.i2].position,
						 p3 = tet.particles[t.i3].position;

				Vector3 center = (p0 + p1 + p2 + p3) * 0.25f;
				float maxR = 0f;
				maxR = Mathf.Max(maxR, Vector3.Distance(p0, center));
				maxR = Mathf.Max(maxR, Vector3.Distance(p1, center));
				maxR = Mathf.Max(maxR, Vector3.Distance(p2, center));
				maxR = Mathf.Max(maxR, Vector3.Distance(p3, center));
				maxR += 0.1f;

				// Build column matrix  mat3(p0-p3, p1-p3, p2-p3)  then invert
				// Unity Matrix4x4 is column-major: m[col][row]
				var m = Matrix4x4.zero;
				Vector3 c0 = p0 - p3, c1 = p1 - p3, c2 = p2 - p3;
				m.m00 = c0.x;
				m.m10 = c0.y;
				m.m20 = c0.z;
				m.m01 = c1.x;
				m.m11 = c1.y;
				m.m21 = c1.z;
				m.m02 = c2.x;
				m.m12 = c2.y;
				m.m22 = c2.z;
				m.m33 = 1f;

				if (Mathf.Abs(m.determinant) < 1e-10f)
					continue;
				Matrix4x4 inv = m.inverse;

				var nearby = hash.Query(center, maxR);
				float maxR2 = maxR * maxR;

				foreach (int vid in nearby)
				{
					if (minDist[vid] <= 0f)
						continue;
					if ((renderPos[vid] - center).sqrMagnitude > maxR2)
						continue;

					Vector3 d = renderPos[vid] - p3;
					Vector3 bary = inv.MultiplyVector(d);

					if (float.IsNaN(bary.x) || float.IsNaN(bary.y) || float.IsNaN(bary.z))
						continue;

					float b3 = 1f - (bary.x + bary.y + bary.z);
					float maxNeg = Mathf.Max(-bary.x, -bary.y, -bary.z, -b3);

					if (maxNeg < minDist[vid])
					{
						minDist[vid] = maxNeg;
						skin[vid] = new GPUSkinningInfo { weights = bary, tetIndex = (uint) i };
					}
				}
			}
			return skin;
		}

		// ─── Build Unity Mesh from render data ───────────────────────────────
		static Mesh BuildUnityMesh(RenderData d)
		{
			var m = new Mesh { name = "SoftBodyRender" };
			m.indexFormat = IndexFormat.UInt32;
			m.SetVertices(d.positions);
			m.SetNormals(d.normals);
			m.SetUVs(0, d.uvs);
			m.SetTriangles(d.indices, 0);
			m.RecalculateBounds();
			m.MarkDynamic();
			return m;
		}

		// ─── Folder creation helper ───────────────────────────────────────────
		static void EnsureFolder(string path)
		{
			var parts = path.Split('/');
			string cur = parts[0];
			for (int i = 1; i < parts.Length; i++)
			{
				string next = cur + "/" + parts[i];
				if (!AssetDatabase.IsValidFolder(next))
					AssetDatabase.CreateFolder(cur, parts[i]);
				cur = next;
			}
		}

		// ═════════════════════════════════════════════════════════════════════
		// Minimal C# port of SpatialHash.cpp (spacing 0.25, same hash function)
		// Used only at import time — not shipped to the player.
		// ═════════════════════════════════════════════════════════════════════
		class SpatialHash
		{
			readonly float _sp;
			readonly int _sz;
			readonly int[] _start;
			readonly int[] _entries;
			readonly Vector3[] _pos;

			public SpatialHash(float spacing, Vector3[] positions)
			{
				_sp = spacing;
				_pos = positions;
				int n = positions.Length;
				_sz = 2 * n;
				_start = new int[_sz + 1];
				_entries = new int[n];

				var hashes = new int[n];
				for (int i = 0; i < n; i++)
				{
					hashes[i] = H(positions[i]);
					_start[hashes[i]]++;
				}

				int s = 0;
				for (int i = 0; i < _sz; i++)
				{
					s += _start[i];
					_start[i] = s;
				}
				_start[_sz] = s;

				for (int i = 0; i < n; i++)
				{
					_start[hashes[i]]--;
					_entries[_start[hashes[i]]] = i;
				}
			}

			int H(Vector3 p) => H((int) Mathf.Floor(p.x / _sp),
								   (int) Mathf.Floor(p.y / _sp),
								   (int) Mathf.Floor(p.z / _sp));
			int H(int x, int y, int z) =>
				Mathf.Abs((x * 92837111) ^ (y * 689287499) ^ (z * 283923481)) % _sz;

			public List<int> Query(Vector3 c, float r)
			{
				int x0 = (int) Mathf.Floor((c.x - r) / _sp), y0 = (int) Mathf.Floor((c.y - r) / _sp),
					z0 = (int) Mathf.Floor((c.z - r) / _sp),
					x1 = (int) Mathf.Floor((c.x + r) / _sp), y1 = (int) Mathf.Floor((c.y + r) / _sp),
					z1 = (int) Mathf.Floor((c.z + r) / _sp);

				var res = new List<int>();
				var used = new HashSet<int>();
				for (int x = x0; x <= x1; x++)
					for (int y = y0; y <= y1; y++)
						for (int z = z0; z <= z1; z++)
						{
							int h = H(x, y, z);
							if (used.Add(h))
								for (int i = _start[h]; i < _start[h + 1]; i++)
									res.Add(_entries[i]);
						}
				return res;
			}
		}
	}
}
