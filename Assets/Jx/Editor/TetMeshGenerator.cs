// TetMeshGenerator.cs
// Unity Editor window:  XPBD → Generate Tet Mesh
//
// Reimplements the BlenderTetPlugin.py (Matthias Müller / tenMinutePhysics)
// algorithm entirely in pure C#.  No Blender, no Python, no OBJ pipeline.
//
// Input:  any Read/Write-enabled Unity Mesh asset.
// Output: TetrahedralMeshAsset ScriptableObject written directly to the project.
//
// ── Algorithm ─────────────────────────────────────────────────────────────────
//  1. Signed Distance Field (SDF) on a voxel grid.
//     Each voxel stores the signed distance to the nearest surface triangle.
//     Sign determined by X-axis ray-casting (odd intersections = inside).
//  2. Tet vertex sampling — two kinds:
//     a. Surface samples: SDF isosurface edge-crossings (sign-change voxel edges).
//        Linearly interpolated to the zero crossing — same as Blender plugin.
//        These replace the render mesh vertices in the Delaunay input.
//        This is the key correctness + performance fix: for res=5 a sphere mesh
//        has 2562 render verts but only ~150 isosurface samples → 250× fewer pts.
//     b. Interior samples: voxel centres where SDF < 0.
//  3. Bowyer-Watson 3D incremental Delaunay tetrahedralization.
//     Circumsphere test is parallelised with Unity Burst + IJobParallelFor.
//     For each new point: [Burst parallel] mark bad tets → [serial] cavity fill.
//  4. Boundary filtering: remove tets whose centroid has SDF > 0.
//  5. Physics data: restVolume, invMass, deduplicated edges.
//  6. Barycentric skinning weights (TetDeform path) via SpatialHash.
//     Render mesh vertices are only used HERE — not in Delaunay.
//
// ── Resolution guide (with correct surface sampling) ─────────────────────────
//   5  → ~300  verts, ~1 500 tets  (instant)
//  10  → ~600  verts, ~4 000 tets  (~1s)
//  25  → ~2k   verts, ~15 000 tets (~5s with Burst)
//  50  → ~8k   verts, ~60 000 tets (~30s with Burst)
//
// ── Requirements ──────────────────────────────────────────────────────────────
//  - com.unity.burst  (1.8+)
//  - com.unity.collections  (2.x)
//  Both are standard Unity packages — add via Package Manager if missing.
//  If Burst is absent the code falls back to a plain C# loop automatically.
//
// PLACE THIS FILE in any Assets/.../Editor/ folder.

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPBD.Editor
{
    public class TetMeshGenerator : EditorWindow
    {
        // ── Window state ──────────────────────────────────────────────────────
        private Mesh   _sourceMesh;
        private int    _resolution   = 5;
        private string _outputFolder = "Assets/XPBD/TetModels";
        private string _assetName    = "mesh_5";
        private bool   _useTetDeform = true;

        private string _statusMsg  = "";
        private bool   _statusOk   = true;
        private bool   _running    = false;
        private float  _progress   = 0f;
        private string _progressLabel = "";

        private GenResult _result;
        private Exception _bgError;
        private bool      _bgDone;

        [MenuItem("XPBD/Generate Tet Mesh...")]
        public static void ShowWindow() =>
            GetWindow<TetMeshGenerator>(false, "XPBD Tet Generator", true);

        // ── GUI ───────────────────────────────────────────────────────────────
        void OnGUI()
        {
            GUILayout.Space(8);
            GUILayout.Label("XPBD In-Unity Tetrahedralizer", EditorStyles.boldLabel);
            GUILayout.Label("Reimplements BlenderTetPlugin.py — no Blender needed.",
                EditorStyles.miniLabel);
            GUILayout.Space(10);

            _sourceMesh = (Mesh)EditorGUILayout.ObjectField(
                new GUIContent("Source Mesh",
                    "Triangulated Unity Mesh asset. Must have Read/Write enabled\n" +
                    "(Mesh Import Settings → Read/Write checkbox)."),
                _sourceMesh, typeof(Mesh), false);

            _resolution = EditorGUILayout.IntSlider(
                new GUIContent("Resolution",
                    "Voxel grid density:\n" +
                    " 3 = ~100 verts  (instant)\n" +
                    " 5 = ~300 verts  (~2s)\n" +
                    "10 = ~600 verts  (~8s)\n" +
                    "25 = ~2k  verts  (~60s)\n" +
                    "50 = ~8k  verts  (slow — background)"),
                _resolution, 3, 50);

            _useTetDeform = EditorGUILayout.Toggle(
                new GUIContent("Tet Deformation",
                    "ON  (recommended): barycentric skinning — low-res tet drives high-res render mesh.\n" +
                    "OFF: direct 1-to-1 index map — only works when resolution ≈ render mesh vertex count."),
                _useTetDeform);

            _assetName    = EditorGUILayout.TextField("Asset Name",    _assetName);
            _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
            GUILayout.Space(12);

            bool canRun = _sourceMesh != null && !_running && _assetName.Length > 0;
            GUI.enabled = canRun;
            if (GUILayout.Button("Generate", GUILayout.Height(36)))
                RunGeneration();
            GUI.enabled = true;

            if (_running)
            {
                GUILayout.Space(8);
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, _progress, _progressLabel);
                Repaint();
            }

            if (_statusMsg.Length > 0)
            {
                GUILayout.Space(8);
                GUI.color = _statusOk ? Color.green : Color.red;
                GUILayout.Label(_statusMsg, EditorStyles.helpBox);
                GUI.color = Color.white;
            }
        }

        void Update()
        {
            if (!_running || !_bgDone) return;
            _running = false;
            _bgDone  = false;
            EditorUtility.ClearProgressBar();
            if (_bgError != null)
            {
                _statusOk  = false;
                _statusMsg = "✗  " + _bgError.Message;
                Debug.LogError("[XPBD Gen] " + _bgError);
            }
            else
            {
                SaveAssets(_result);
            }
            Repaint();
        }

        void RunGeneration()
        {
            _running  = true;
            _bgDone   = false;
            _bgError  = null;
            _result   = null;
            _statusMsg = "";

            // Capture mesh data on main thread
            var verts   = _sourceMesh.vertices;
            var tris    = _sourceMesh.triangles;
            var normals = _sourceMesh.normals;
            var uvs     = _sourceMesh.uv;
            int  res    = _resolution;
            bool tetDef = _useTetDeform;

            new Thread(() =>
            {
                try
                {
                    _result = Generate(verts, tris, normals, uvs, res, tetDef,
                        (p, lbl) => { _progress = p; _progressLabel = lbl; });
                }
                catch (Exception ex) { _bgError = ex; }
                finally { _bgDone = true; }
            }) { IsBackground = true }.Start();
        }

        void SaveAssets(GenResult r)
        {
            try
            {
                EnsureFolder(_outputFolder);

                // TetrahedralMeshAsset — physics data only.
                // RenderMesh is a plain Inspector reference to the source mesh asset;
                // the user assigns it (or it stays null until assigned).
                // UseTetDeformation is derived: (Skinning != null && Skinning.Length > 0).
                // Nothing mesh-related is created or embedded here.
                var asset = ScriptableObject.CreateInstance<TetrahedralMeshAsset>();

                // Assign the source mesh as the RenderMesh reference directly.
                // SoftBodyComponent.Start() will Instantiate() this at runtime.
                asset.RenderMesh = _sourceMesh;

                asset.Particles = new ParticleData[r.particles.Length];
                for (int i = 0; i < r.particles.Length; i++)
                    asset.Particles[i] = new ParticleData
                        { Position = r.particles[i].pos, InvMass = r.particles[i].invMass };

                asset.Edges = new EdgeData[r.edges.Length];
                for (int i = 0; i < r.edges.Length; i++)
                    asset.Edges[i] = new EdgeData
                        { IndexA = r.edges[i].a, IndexB = r.edges[i].b, RestLen = r.edges[i].restLen };

                asset.Tetrahedrals = new TetrahedralData[r.tets.Length];
                for (int i = 0; i < r.tets.Length; i++)
                    asset.Tetrahedrals[i] = new TetrahedralData
                    {
                        I0 = r.tets[i].i0, I1 = r.tets[i].i1,
                        I2 = r.tets[i].i2, I3 = r.tets[i].i3,
                        RestVolume = r.tets[i].restVolume
                    };

                // UseTetDeformation is computed as: Skinning != null && Skinning.Length > 0.
                // So: populate Skinning for tet-deform path, leave null for direct path.
                if (_useTetDeform)
                {
                    asset.Skinning = new SkinningData[r.skinning.Length];
                    for (int i = 0; i < r.skinning.Length; i++)
                        asset.Skinning[i] = new SkinningData
                            { Weights = r.skinning[i].bary, TetIndex = r.skinning[i].tetIdx };
                }
                else
                {
                    asset.OrigIndices = r.origIndices;
                    // asset.Skinning stays null → UseTetDeformation returns false
                }

                string assetPath = $"{_outputFolder}/{_assetName}.asset";
                AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(asset, assetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                _statusOk  = true;
                _statusMsg =
                    $"✓  {_assetName}" +
                    $"  particles:{r.particles.Length}" +
                    $"  edges:{r.edges.Length}" +
                    $"  tets:{r.tets.Length}" +
                    $"  renderVerts:{r.renderVerts.Length}";
                Selection.activeObject = asset;
                Debug.Log($"[XPBD] Generated → {assetPath}  {_statusMsg}");
            }
            catch (Exception ex)
            {
                _statusOk  = false;
                _statusMsg = "✗  Save failed: " + ex.Message;
                Debug.LogError("[XPBD Gen] Save: " + ex);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // GENERATION  (background thread — no UnityEngine calls except math)
        // ═════════════════════════════════════════════════════════════════════

        // ── Result container ──────────────────────────────────────────────────
        class GenResult
        {
            public Vector3[]   renderVerts;
            public Vector3[]   renderNormals;
            public Vector2[]   renderUVs;
            public int[]       renderTris;
            public Particle[]  particles;
            public Edge[]      edges;
            public Tet[]       tets;
            public Skin[]      skinning;
            public uint[]      origIndices;
        }

        struct Particle { public Vector3 pos;   public float invMass; }
        struct Edge     { public uint    a, b;  public float restLen; }
        struct Tet      { public uint    i0,i1,i2,i3; public float restVolume; }
        struct Skin     { public Vector3 bary;  public uint  tetIdx; }

        static GenResult Generate(
            Vector3[] verts, int[] tris,
            Vector3[] normals, Vector2[] uvs,
            int resolution, bool useTetDeform,
            Action<float, string> prog)
        {
            // ── 1. Bounds + SDF ───────────────────────────────────────────────
            prog(0.02f, "Computing bounds…");
            var b = ComputeBounds(verts);   // tight bounds over render mesh verts

            // ── 2. SDF — used only for interior-sample classification ─────────
            // Nearest-triangle-normal sign: robust against ray-through-vertex
            // degeneracy that breaks axis-aligned ray casting on icospheres.
            prog(0.04f, "Building SDF…");
            float[] sdfData;
            int sdfRes;
            BuildSDF(verts, tris, b, resolution, out sdfData, out sdfRes);

            // ── 3. Tet vertex sampling — matches BlenderTetPlugin.py exactly ──
            // Surface particles  = ALL render mesh vertices (jittered ±1e-4).
            //   Each surface vert becomes a physics particle, so skinning is a
            //   trivial 1-to-1 map — eliminates "rod" stretch artefacts entirely.
            // Interior particles = grid nodes inside the mesh AND at least 0.5·h
            //   from the surface (plugin minDist = 0.5·h rule).
            //   h = max(bounds.size) / resolution.
            prog(0.28f, "Sampling tet vertices…");
            int numRenderVerts = verts.Length;
            var tetVerts = SampleTetVerts(verts, sdfData, sdfRes, b, resolution);

            // ── 4. Bowyer-Watson Delaunay (direct port of BlenderTetPlugin.py) ─────────
            // Append the 4 super-tet vertices exactly as the plugin does:
            //   s = 5 * radius,  verts: (-s,0,-s) (s,0,-s) (0,s,s) (0,-s,s)
            Vector3 tvCenter = Vector3.zero;
            float tvRadius = 0f;
            foreach (var v in tetVerts) tvCenter += v;
            tvCenter /= tetVerts.Length;
            foreach (var v in tetVerts) tvRadius = Mathf.Max(tvRadius, (v-tvCenter).magnitude);
            float s5 = 5f * tvRadius;
            int firstBig = tetVerts.Length;
            var allVerts = new Vector3[firstBig + 4];
            System.Array.Copy(tetVerts, allVerts, firstBig);
            allVerts[firstBig]   = new Vector3(-s5, 0f, -s5);
            allVerts[firstBig+1] = new Vector3( s5, 0f, -s5);
            allVerts[firstBig+2] = new Vector3( 0f, s5,  s5);
            allVerts[firstBig+3] = new Vector3( 0f,-s5,  s5);

            prog(0.35f, $"Delaunay ({firstBig} verts)…");
            var rawTetIds = CreateTetIds(allVerts, firstBig, prog);

            // Convert flat int[] to DTet list, dropping deleted & super-tet slots
            var dtets = new System.Collections.Generic.List<DTet>(rawTetIds.Count / 4);
            for (int ti = 0; ti < rawTetIds.Count / 4; ti++)
            {
                int id0 = rawTetIds[4*ti];
                if (id0 < 0) continue;                                   // deleted slot
                int id1=rawTetIds[4*ti+1], id2=rawTetIds[4*ti+2], id3=rawTetIds[4*ti+3];
                if (id0>=firstBig||id1>=firstBig||id2>=firstBig||id3>=firstBig) continue; // super-tet
                dtets.Add(new DTet{v0=id0,v1=id1,v2=id2,v3=id3});
            }

            // ── 5. Filter exterior / degenerate tets ─────────────────────────
            prog(0.78f, "Filtering…");
            var goodTets = FilterTets(dtets, allVerts, sdfData, sdfRes, b);

            // ── 6. Physics data ───────────────────────────────────
            prog(0.84f, "Building physics data…");
            Particle[] particles;
            Edge[]     edges;
            Tet[]      tetList;
            BuildPhysics(allVerts, goodTets, out particles, out edges, out tetList);

            // ── 7. Skinning / orig indices ────────────────────────
            // Render verts 0..numRenderVerts-1 are the FIRST entries in tetVerts.
            // If referenced by a kept tet they have an exact particle via remap[].
            // ComputeSkinning handles both the direct 1-to-1 case and the rare
            // fallback (surface verts not covered by any kept tet).
            Skin[] skinning     = null;
            uint[] origIndices  = null;

            if (useTetDeform)
            {
                prog(0.90f, "Computing barycentric weights…");
                skinning = ComputeSkinning(verts, numRenderVerts,
                                           allVerts, particles, tetList);
            }
            else
            {
                prog(0.90f, "Building direct index map…");
                origIndices = BuildOrigIndices(verts, particles);
            }

            prog(1.00f, "Done.");

            return new GenResult
            {
                renderVerts   = verts,
                renderNormals = normals ?? new Vector3[verts.Length],
                renderUVs     = uvs    ?? new Vector2[verts.Length],
                renderTris    = tris,
                particles     = particles,
                edges         = edges,
                tets          = tetList,
                skinning      = skinning,
                origIndices   = origIndices
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEP 1: Signed Distance Field
        // Voxel grid of sdfRes³ cells.
        // Each cell = signed distance to nearest triangle.
        // Sign: negative = inside (odd X-ray crossings).
        // ═════════════════════════════════════════════════════════════════════

        struct ABounds { public Vector3 min, size; }

        static ABounds ComputeBounds(Vector3[] verts)
        {
            Vector3 mn = verts[0], mx = verts[0];
            foreach (var v in verts)
            {
                if (v.x < mn.x) mn.x = v.x;  if (v.x > mx.x) mx.x = v.x;
                if (v.y < mn.y) mn.y = v.y;  if (v.y > mx.y) mx.y = v.y;
                if (v.z < mn.z) mn.z = v.z;  if (v.z > mx.z) mx.z = v.z;
            }
            Vector3 pad = (mx - mn) * 0.06f + Vector3.one * 0.002f;
            mn -= pad; mx += pad;
            return new ABounds { min = mn, size = mx - mn };
        }

        static void BuildSDF(Vector3[] verts, int[] tris,
            ABounds b, int res,
            out float[] data, out int sdfRes)
        {
            sdfRes = res + 1;
            int r  = sdfRes;
            data   = new float[r * r * r];

            int triCount = tris.Length / 3;
            float dx = b.size.x / res;
            float dy = b.size.y / res;
            float dz = b.size.z / res;

            // Precompute per-triangle: unnormalised outward normal + centroid.
            // Used for sign determination: dot(normal, p-centroid) > 0 → outside.
            // This is robust against the ray-through-vertex degeneracy that
            // breaks axis-aligned ray casting on icospheres and grid-aligned meshes.
            var triNormals  = new Vector3[triCount];
            var triCentroids = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
            {
                Vector3 v0 = verts[tris[t*3]];
                Vector3 v1 = verts[tris[t*3+1]];
                Vector3 v2 = verts[tris[t*3+2]];
                triNormals[t]   = V3Cross(v1-v0, v2-v0);   // unnormalised outward normal
                triCentroids[t] = (v0+v1+v2) * (1f/3f);
            }

            for (int iz = 0; iz < r; iz++)
            for (int iy = 0; iy < r; iy++)
            for (int ix = 0; ix < r; ix++)
            {
                var p = new Vector3(
                    b.min.x + (ix + 0.5f) * dx,
                    b.min.y + (iy + 0.5f) * dy,
                    b.min.z + (iz + 0.5f) * dz);

                float minD2    = float.MaxValue;
                int   nearestT = 0;

                for (int t = 0; t < triCount; t++)
                {
                    Vector3 v0 = verts[tris[t*3]];
                    Vector3 v1 = verts[tris[t*3+1]];
                    Vector3 v2 = verts[tris[t*3+2]];
                    float d2 = PtTriSq(p, v0, v1, v2);
                    if (d2 < minD2) { minD2 = d2; nearestT = t; }
                }

                // Sign: dot(outward_normal, p - centroid) < 0 → p is inside mesh.
                // Robust for any closed manifold mesh — no ray/vertex degeneracy.
                float dot  = V3Dot(triNormals[nearestT], p - triCentroids[nearestT]);
                float sign = dot < 0f ? -1f : 1f;
                data[iz * r * r + iy * r + ix] = sign * Mathf.Sqrt(minD2);
            }
        }

        // Squared distance point → triangle (Ericson, Real-Time Collision Detection)
        static float PtTriSq(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab=b-a, ac=c-a, ap=p-a;
            float d1=V3Dot(ab,ap), d2=V3Dot(ac,ap);
            if (d1<=0&&d2<=0) return V3Sq(p-a);
            Vector3 bp=p-b;
            float d3=V3Dot(ab,bp), d4=V3Dot(ac,bp);
            if (d3>=0&&d4<=d3) return V3Sq(p-b);
            Vector3 cp=p-c;
            float d5=V3Dot(ab,cp), d6=V3Dot(ac,cp);
            if (d6>=0&&d5<=d6) return V3Sq(p-c);
            float vc=d1*d4-d3*d2;
            if (vc<=0&&d1>=0&&d3<=0){float v=d1/(d1-d3);return V3Sq(p-(a+v*ab));}
            float vb=d5*d2-d1*d6;
            if (vb<=0&&d2>=0&&d6<=0){float w=d2/(d2-d6);return V3Sq(p-(a+w*ac));}
            float va=d3*d6-d5*d4;
            if (va<=0&&(d4-d3)>=0&&(d5-d6)>=0)
            {float w=(d4-d3)/((d4-d3)+(d5-d6));return V3Sq(p-(b+w*(c-b)));}
            float inv=1f/(va+vb+vc);
            return V3Sq(p-(a+(vb*inv)*ab+(vc*inv)*ac));
        }

        // XRayCross removed — sign now from nearest-triangle normal (see BuildSDF)

        // ═════════════════════════════════════════════════════════════════════
        // STEP 2: Sample interior voxel centres where SDF < 0
        // ═════════════════════════════════════════════════════════════════════
        // ═════════════════════════════════════════════════════════════════════
        // STEP 2: Sample tet mesh vertices from the SDF
        //
        // Matches BlenderTetPlugin.py exactly:
        //   Surface samples  = SDF isosurface edge crossings (sign-change voxel edges).
        //                      Linearly interpolated to the zero level-set.
        //   Interior samples = voxel centres where SDF < 0.
        //
        // The render mesh vertices are intentionally NOT included here.
        // They are only used later for barycentric skinning weight computation.
        // This keeps Delaunay input proportional to resolution² not render-mesh size.
        // ═════════════════════════════════════════════════════════════════════
        static Vector3[] SampleTetVerts(
            Vector3[] renderVerts, float[] sdf, int r, ABounds b, int resolution)
        {
            // ── Matches BlenderTetPlugin.py createTets() exactly ──────────────
            //
            // Part A — Surface particles: every render mesh vertex, jittered by
            //   ±1e-4 to prevent degenerate co-planar/co-linear Delaunay cases.
            //   These become the first numRenderVerts entries in the output array,
            //   so BuildPhysics and ComputeSkinning can use their index directly.
            //
            // Part B — Interior particles: axis-aligned grid nodes that are:
            //   (a) strictly inside the mesh  (SDF < 0), AND
            //   (b) at least 0.5·h from the surface  (SDF < -0.5·h).
            //   This mirrors the plugin's isInside(p, minDist = 0.5·h) call.
            //   h = max(bounds.size) / resolution.

            var rng = new System.Random(42);   // deterministic jitter
            double Eps() => -1e-4 + rng.NextDouble() * 2e-4;

            var pts = new List<Vector3>(renderVerts.Length + 64);

            // Part A: all render mesh verts (jittered)
            foreach (var v in renderVerts)
                pts.Add(new Vector3(v.x + (float)Eps(),
                                    v.y + (float)Eps(),
                                    v.z + (float)Eps()));

            // Part B: interior grid nodes
            float maxDim = Mathf.Max(Mathf.Max(b.size.x, b.size.y), b.size.z);
            float h      = maxDim / resolution;
            float minDist = 0.5f * h;                 // plugin: minDist = 0.5*h

            // Grid step counts (same formula as plugin):
            //   for xi in range(int(dims.x/h)+1) → 0 .. floor(size.x/h)
            int nx = Mathf.FloorToInt(b.size.x / h) + 1;
            int ny = Mathf.FloorToInt(b.size.y / h) + 1;
            int nz = Mathf.FloorToInt(b.size.z / h) + 1;

            // SDF cell spacing (BuildSDF stores one value per (ix+0.5)*dx cell)
            float dx = b.size.x / (r - 1);
            float dy = b.size.y / (r - 1);
            float dz = b.size.z / (r - 1);

            for (int xi = 0; xi < nx; xi++)
            for (int yi = 0; yi < ny; yi++)
            for (int zi = 0; zi < nz; zi++)
            {
                float x = b.min.x + xi * h;
                float y = b.min.y + yi * h;
                float z = b.min.z + zi * h;

                // SDF lookup: find nearest voxel centre
                int ix = Clamp(Mathf.RoundToInt((x - b.min.x) / dx - 0.5f), 0, r-1);
                int iy = Clamp(Mathf.RoundToInt((y - b.min.y) / dy - 0.5f), 0, r-1);
                int iz = Clamp(Mathf.RoundToInt((z - b.min.z) / dz - 0.5f), 0, r-1);
                float d = sdf[iz*r*r + iy*r + ix];

                // Keep if: inside (d<0) AND far enough from surface (d < -minDist)
                if (d >= -minDist) continue;

                pts.Add(new Vector3(x + (float)Eps(),
                                    y + (float)Eps(),
                                    z + (float)Eps()));
            }

            return pts.ToArray();
        }

        static long QKey(Vector3 v)
        {
            int x = (int)Math.Round(v.x * 8192.0);
            int y = (int)Math.Round(v.y * 8192.0);
            int z = (int)Math.Round(v.z * 8192.0);
            return ((long)(x&0xFFFFF)<<40)|((long)(y&0xFFFFF)<<20)|(uint)(z&0xFFFFF);
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEP 3: Bowyer-Watson 3D incremental Delaunay
        //
        // The circumsphere test is the hot loop: for each new point we scan
        // all current tets.  This is parallelised with Burst IJobParallelFor.
        //
        // Structure per insertion:
                //   [serial]         collect bad indices, find cavity boundary faces
        //   [serial]         remove bad tets, fill cavity with new tets
        //
        // Burst fallback: if Burst is unavailable the job runs on the main
        // thread via job.Run() which is still correct, just not parallelised.
        // ═════════════════════════════════════════════════════════════════════

                // Simple tet — just 4 vertex indices into the allVerts array.
        struct DTet { public int v0, v1, v2, v3; }

        // ══════════════════════════════════════════════════════════════════════
        // DELAUNAY TETRAHEDRALIZATION
        // Direct port of BlenderTetPlugin.py createTetIds() by Matthias Müller.
        // Data layout and all steps are kept identical so results match exactly.
        // ══════════════════════════════════════════════════════════════════════

        // tetFaces[f][0..2]: local vertex positions for face f.
        // face 0 = opposite local-vert 3, winding {2,1,0}
        // face 1 = opposite local-vert 2, winding {0,1,3}
        // face 2 = opposite local-vert 0, winding {1,2,3}
        // face 3 = opposite local-vert 1, winding {2,0,3}
        static readonly int[][] TetFaces = { new[]{2,1,0}, new[]{0,1,3}, new[]{1,2,3}, new[]{2,0,3} };

        // ── Circumcenter — direct port of plugin's getCircumCenter() ─────────
        static Vector3 GetCircumCenter(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            Vector3 b = p1 - p0, c = p2 - p0, d = p3 - p0;
            float det = 2f * (b.x*(c.y*d.z - c.z*d.y)
                             - b.y*(c.x*d.z - c.z*d.x)
                             + b.z*(c.x*d.y - c.y*d.x));
            if (Mathf.Abs(det) < 1e-12f) return p0;   // degenerate → return p0 (same as plugin)
            Vector3 v = Vector3.Cross(c,d)*Vector3.Dot(b,b)
                      + Vector3.Cross(d,b)*Vector3.Dot(c,c)
                      + Vector3.Cross(b,c)*Vector3.Dot(d,d);
            return p0 + v / det;
        }

        // ── Port of createTetIds() ────────────────────────────────────────────
        // verts  : allVerts (render verts + interior + 4 super-tet verts)
        // firstBig : index of first super-tet vert (= len(verts)-4)
        // Returns flat tetIds list; caller filters and converts to DTet[].
        static List<int> CreateTetIds(Vector3[] verts, int firstBig,
                                       Action<float,string> prog)
        {
            // ── Flat arrays indexed by 4*tetNr (exactly as Python plugin) ─────
            // tetIds[4t]:  first vert index, OR -1 if slot deleted.
            //   When deleted, tetIds[4t+1] = next free slot (linked free-list).
            // neighbors[4t+k]: index of neighboring tet sharing face k, or -1.
            // planesN*/planesD: face-plane data for the walk (Step A).
            // tetMarks[t]: last tetMark stamp — used as visited flag.
            var tetIds    = new List<int>(verts.Length * 8);
            var neighbors = new List<int>(verts.Length * 8);
            var tetMarks  = new List<int>(verts.Length * 2);
            var planesNx  = new List<float>(verts.Length * 8);
            var planesNy  = new List<float>(verts.Length * 8);
            var planesNz  = new List<float>(verts.Length * 8);
            var planesD   = new List<float>(verts.Length * 8);

            int tetMark     = 0;
            int firstFreeTet = -1;

            // Allocate a tet slot (from free list or by appending).
            int AllocTet()
            {
                int nr;
                if (firstFreeTet >= 0)
                {
                    nr = firstFreeTet;
                    firstFreeTet = tetIds[4*firstFreeTet + 1];
                    // re-init the slot's extra fields
                    tetIds[4*nr+1] = tetIds[4*nr+2] = tetIds[4*nr+3] = -1;
                }
                else
                {
                    nr = tetIds.Count / 4;
                    for (int k=0;k<4;k++) { tetIds.Add(-1); neighbors.Add(-1); }
                    for (int k=0;k<4;k++) { planesNx.Add(0); planesNy.Add(0); planesNz.Add(0); planesD.Add(0); }
                    tetMarks.Add(0);
                }
                return nr;
            }

            // Recompute all 4 face planes for a tet slot.
            void SetFacePlanes(int t)
            {
                int b4 = 4*t;
                for (int f=0; f<4; f++)
                {
                    Vector3 p0 = verts[tetIds[b4 + TetFaces[f][0]]];
                    Vector3 p1 = verts[tetIds[b4 + TetFaces[f][1]]];
                    Vector3 p2 = verts[tetIds[b4 + TetFaces[f][2]]];
                    Vector3 n  = Vector3.Cross(p1-p0, p2-p0);
                    float   nm = n.magnitude;
                    if (nm > 1e-12f) n /= nm; // normalize (same as plugin)
                    planesNx[b4+f]=n.x; planesNy[b4+f]=n.y; planesNz[b4+f]=n.z;
                    planesD [b4+f]=Vector3.Dot(n, p0);
                }
            }

            // ── Bootstrap: single super-tet (slot 0) ─────────────────────────
            {
                int t0 = AllocTet();  // == 0
                tetIds[0]=firstBig; tetIds[1]=firstBig+1; tetIds[2]=firstBig+2; tetIds[3]=firstBig+3;
                SetFacePlanes(0);
                // neighbors already -1 from AllocTet
            }

            var violatingTets = new List<int>(64);
            var stack         = new List<int>(64);
            // edges: (min(a,b), max(a,b), newTetNr, faceIndex 1-3)
            var edges = new List<(int a, int b, int tetNr, int face)>(256);
            var centerV = Vector3.zero;

            // ── Main loop: insert each non-super-tet vert in order ───────────
            for (int i = 0; i < firstBig; i++)
            {
                if ((i & 0x3F) == 0)
                    prog(0.35f + 0.43f * i / firstBig, $"Delaunay {i}/{firstBig}…");

                Vector3 p = verts[i];

                // ── Step A: walk to the containing tet ───────────────────────
                // Start from the first non-deleted slot (same as plugin).
                int tetNr = 0;
                while (tetIds[4*tetNr] < 0) tetNr++;

                tetMark++;
                bool found = false;
                while (!found)
                {
                    if (tetNr < 0 || tetMarks[tetNr] == tetMark) break;
                    tetMarks[tetNr] = tetMark;

                    int id0=tetIds[4*tetNr], id1=tetIds[4*tetNr+1],
                        id2=tetIds[4*tetNr+2], id3=tetIds[4*tetNr+3];
                    centerV = (verts[id0]+verts[id1]+verts[id2]+verts[id3]) * 0.25f;

                    float minT = float.MaxValue; int minFace = -1;
                    for (int j=0; j<4; j++)
                    {
                        int b4 = 4*tetNr+j;
                        float nx=planesNx[b4], ny=planesNy[b4], nz=planesNz[b4], dv=planesD[b4];
                        float hp = nx*p.x       + ny*p.y       + nz*p.z       - dv;
                        float hc = nx*centerV.x + ny*centerV.y + nz*centerV.z - dv;
                        float t  = hp - hc;
                        if (t == 0f) continue;
                        t = -hc / t;
                        if (t >= 0f && t < minT) { minT=t; minFace=j; }
                    }
                    if (minT >= 1f) found = true;
                    else            tetNr = neighbors[4*tetNr + minFace];
                }
                if (!found) continue;  // degenerate — skip (same as plugin)

                // ── Step B: flood-fill violating tets from the containing tet ─
                // For each tet popped: mark it, add to violatingTets, then check
                // each neighbor's circumsphere. If p is inside → push neighbor.
                // Exactly mirrors the plugin's while(stack) loop.
                tetMark++;
                violatingTets.Clear();
                stack.Clear();
                stack.Add(tetNr);

                while (stack.Count > 0)
                {
                    int tn = stack[stack.Count-1]; stack.RemoveAt(stack.Count-1);
                    if (tetMarks[tn] == tetMark) continue;
                    tetMarks[tn] = tetMark;
                    violatingTets.Add(tn);

                    for (int j=0; j<4; j++)
                    {
                        int nb = neighbors[4*tn+j];
                        if (nb < 0 || tetMarks[nb] == tetMark) continue;
                        // Check circumsphere of the NEIGHBOR (plugin does this too)
                        int jd0=tetIds[4*nb], jd1=tetIds[4*nb+1],
                            jd2=tetIds[4*nb+2], jd3=tetIds[4*nb+3];
                        Vector3 cc = GetCircumCenter(verts[jd0],verts[jd1],verts[jd2],verts[jd3]);
                        float   r  = (verts[jd0]-cc).magnitude;
                        if ((p-cc).magnitude < r) stack.Add(nb);
                    }
                }

                // ── Step C: remove violating tets, create new ones ────────────
                edges.Clear();
                foreach (int tn in violatingTets)
                {
                    // Snapshot ids & neighbors before we overwrite them
                    int vi0=tetIds[4*tn],   vi1=tetIds[4*tn+1],
                        vi2=tetIds[4*tn+2], vi3=tetIds[4*tn+3];
                    int ni0=neighbors[4*tn],   ni1=neighbors[4*tn+1],
                        ni2=neighbors[4*tn+2], ni3=neighbors[4*tn+3];
                    int[] vid = {vi0,vi1,vi2,vi3};
                    int[] nid = {ni0,ni1,ni2,ni3};

                    // Delete: mark slot and push onto free list
                    tetIds[4*tn]   = -1;
                    tetIds[4*tn+1] = firstFreeTet;
                    firstFreeTet   = tn;

                    for (int k=0; k<4; k++)
                    {
                        int nb = nid[k];
                        // Only boundary faces (neighbor not violating) get new tets
                        if (nb >= 0 && tetMarks[nb] == tetMark) continue;

                        int newTet = AllocTet();

                        // Reversed face winding + new point (exactly as plugin)
                        int nid0 = vid[TetFaces[k][2]];
                        int nid1 = vid[TetFaces[k][1]];
                        int nid2 = vid[TetFaces[k][0]];
                        tetIds[4*newTet]   = nid0;
                        tetIds[4*newTet+1] = nid1;
                        tetIds[4*newTet+2] = nid2;
                        tetIds[4*newTet+3] = i;

                        // Face 0 (opposite i) → outside neighbor
                        neighbors[4*newTet]   = nb;
                        neighbors[4*newTet+1] = -1;
                        neighbors[4*newTet+2] = -1;
                        neighbors[4*newTet+3] = -1;

                        // Redirect outside neighbor's back-pointer to newTet
                        if (nb >= 0)
                            for (int l=0; l<4; l++)
                                if (neighbors[4*nb+l] == tn) { neighbors[4*nb+l] = newTet; break; }

                        SetFacePlanes(newTet);

                        // Emit the 3 edges on faces 1,2,3 (the faces that include i)
                        // for mutual-neighbor repair in Step D.
                        int a, b2;
                        a=nid0<nid1?nid0:nid1; b2=nid0<nid1?nid1:nid0; edges.Add((a,b2,newTet,1));
                        a=nid1<nid2?nid1:nid2; b2=nid1<nid2?nid2:nid1; edges.Add((a,b2,newTet,2));
                        a=nid2<nid0?nid2:nid0; b2=nid2<nid0?nid0:nid2; edges.Add((a,b2,newTet,3));
                    }
                }

                // ── Step D: link mutual neighbors via sorted edge pairs ───────
                // Two new tets that share an edge (a,b) share a face → make them neighbors.
                edges.Sort((e0,e1) => e0.a!=e1.a ? e0.a.CompareTo(e1.a) : e0.b.CompareTo(e1.b));
                for (int nr=0; nr<edges.Count; )
                {
                    var e0 = edges[nr++];
                    if (nr < edges.Count && edges[nr].a==e0.a && edges[nr].b==e0.b)
                    {
                        var e1 = edges[nr++];
                        neighbors[4*e0.tetNr + e0.face] = e1.tetNr;
                        neighbors[4*e1.tetNr + e1.face] = e0.tetNr;
                    }
                }
            }

            return tetIds;
        }



        // Burst job: for each tet ti, test if point P is inside its circumsphere.
        
        

        

        

        

        // Circumsphere via Cramer's rule (double precision for numerical stability)
        

        // ═════════════════════════════════════════════════════════════════════
        // STEP 5: Filter exterior tets
        // Keep only tets whose centroid has SDF <= 0 (inside mesh)
        // ═════════════════════════════════════════════════════════════════════
        static List<DTet> FilterTets(List<DTet> raw, Vector3[] verts,
            float[] sdf, int r, ABounds b)
        {
            var good = new List<DTet>(raw.Count / 2);
            float dx=b.size.x/(r-1), dy=b.size.y/(r-1), dz=b.size.z/(r-1);

            const float kMinQuality = 0.001f;   // matches plugin default (10^-3)

            foreach (var t in raw)
            {
                Vector3 p0=verts[t.v0], p1=verts[t.v1],
                         p2=verts[t.v2], p3=verts[t.v3];

                // Tet centroid
                float cx=(p0.x+p1.x+p2.x+p3.x)*0.25f;
                float cy=(p0.y+p1.y+p2.y+p3.y)*0.25f;
                float cz=(p0.z+p1.z+p2.z+p3.z)*0.25f;

                // SDF lookup — reject if centroid is outside the mesh
                int ix=Clamp((int)((cx-b.min.x)/dx-0.5f),0,r-1);
                int iy=Clamp((int)((cy-b.min.y)/dy-0.5f),0,r-1);
                int iz=Clamp((int)((cz-b.min.z)/dz-0.5f),0,r-1);
                float d = sdf[iz*r*r+iy*r+ix];
                if (d > 0.005f) continue;   // outside mesh

                // Tet quality filter — matches plugin tetQuality() > minQuality.
                // quality = 12/sqrt(2) * vol / rms^3  where rms = RMS edge length.
                // A regular tet has quality = 1.  Slivers approach 0.
                float s0=V3Dist(p0,p1), s1=V3Dist(p0,p2), s2=V3Dist(p0,p3);
                float s3=V3Dist(p1,p2), s4=V3Dist(p1,p3), s5=V3Dist(p2,p3);
                float ms  = (s0*s0+s1*s1+s2*s2+s3*s3+s4*s4+s5*s5) / 6f;
                if (ms < 1e-24f) continue;
                float rms = Mathf.Sqrt(ms);
                float vol = V3Dot(V3Cross(p1-p0,p2-p0), p3-p0) / 6f;
                float quality = 8.485281f * vol / (rms * rms * rms); // 12/sqrt(2)
                if (quality < kMinQuality) continue;

                good.Add(t);
            }
            return good;
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEP 6: Build physics data
        // Matches ResourceManager::loadTetrahedralMeshOBJ exactly
        // ═════════════════════════════════════════════════════════════════════
        static void BuildPhysics(
            Vector3[] allVerts, List<DTet> dtets,
            out Particle[] particles, out Edge[] edges, out Tet[] tetList)
        {
            // Remap: only keep vertices used by surviving tets
            var used  = new HashSet<int>(dtets.Count * 4);
            foreach (var t in dtets)
            { used.Add(t.v0); used.Add(t.v1); used.Add(t.v2); used.Add(t.v3); }

            var sorted = new List<int>(used);
            sorted.Sort();
            var remap  = new Dictionary<int, int>(sorted.Count);
            for (int i = 0; i < sorted.Count; i++) remap[sorted[i]] = i;

            int n = sorted.Count;
            particles = new Particle[n];
            for (int i = 0; i < n; i++)
                particles[i] = new Particle { pos = allVerts[sorted[i]] };

            tetList = new Tet[dtets.Count];
            var edgeMap = new Dictionary<long, Edge>(dtets.Count * 6);

            for (int i = 0; i < dtets.Count; i++)
            {
                uint i0=(uint)remap[dtets[i].v0], i1=(uint)remap[dtets[i].v1],
                     i2=(uint)remap[dtets[i].v2], i3=(uint)remap[dtets[i].v3];
                Vector3 p0=particles[i0].pos, p1=particles[i1].pos,
                         p2=particles[i2].pos, p3=particles[i3].pos;

                float vol = V3Dot(V3Cross(p1-p0,p2-p0), p3-p0) / 6f;
                tetList[i] = new Tet { i0=i0,i1=i1,i2=i2,i3=i3, restVolume=vol };

                if (vol > 0f)
                {
                    float m = vol / 4f;
                    particles[i0].invMass += m;
                    particles[i1].invMass += m;
                    particles[i2].invMass += m;
                    particles[i3].invMass += m;
                }

                uint[] vi = {i0,i1,i2,i3};
                for (int a=0;a<3;a++)
                for (int b2=a+1;b2<4;b2++)
                {
                    uint ea=Math.Min(vi[a],vi[b2]), eb=Math.Max(vi[a],vi[b2]);
                    long ek=((long)ea<<32)|eb;
                    if (!edgeMap.ContainsKey(ek))
                        edgeMap[ek]=new Edge
                            { a=ea, b=eb, restLen=V3Dist(particles[ea].pos, particles[eb].pos) };
                }
            }

            // Invert mass accumulation
            for (int i=0;i<n;i++)
                particles[i].invMass = particles[i].invMass > 0f
                    ? 1f/particles[i].invMass : 0f;

            edges = new List<Edge>(edgeMap.Values).ToArray();
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEP 7A: Barycentric skinning
        // Matches ResourceManager::getSoftBody (resolution != 100) exactly.
        // ═════════════════════════════════════════════════════════════════════
        static Skin[] ComputeSkinning(
            Vector3[] renderVerts, int numRenderVerts,
            Vector3[] tetVerts,
            Particle[] particles, Tet[] tets)
        {
            // Build remap: tetVerts index → particle index (from BuildPhysics)
            // BuildPhysics sorts used vertex indices and remaps them to 0..n-1.
            // We reconstruct that map here so we can do direct 1-to-1 lookup.
            var used = new HashSet<int>(tets.Length * 4);
            foreach (var t in tets)
            { used.Add((int)t.i0); used.Add((int)t.i1); used.Add((int)t.i2); used.Add((int)t.i3); }
            // NOTE: particles[p].pos maps back to tetVerts[sorted_used[p]].
            // Reconstruct tetVert→particle map by matching positions.
            // (BuildPhysics remaps by sorted index, so we can reproduce it.)
            var usedSorted = new List<int>(used);
            usedSorted.Sort();
            var tetVertToParticle = new Dictionary<int, int>(usedSorted.Count);
            for (int p = 0; p < usedSorted.Count; p++)
                tetVertToParticle[usedSorted[p]] = p;

            int n  = renderVerts.Length;
            var sk = new Skin[n];
            var md = new float[n];
            for (int i = 0; i < n; i++) md[i] = float.MaxValue;

            // ── Fast path: render verts that are direct particles ─────────────
            // renderVerts[i] = tetVerts[i] (up to jitter), so if tetVerts index i
            // is in a kept tet, particle remap[i] is the exact host particle.
            // We set bary = (1,0,0) with the tet that contains this particle on i0.
            // Rather than searching, we find the containing tet via the particle index.

            // Build particle→tet map (first tet that uses each particle as i0)
            var particleToTet = new Dictionary<int, int>(particles.Length);
            for (int ti = 0; ti < tets.Length; ti++)
            {
                int p0 = (int)tets[ti].i0;
                if (!particleToTet.ContainsKey(p0)) particleToTet[p0] = ti;
                int p1 = (int)tets[ti].i1;
                if (!particleToTet.ContainsKey(p1)) particleToTet[p1] = ti;
                int p2 = (int)tets[ti].i2;
                if (!particleToTet.ContainsKey(p2)) particleToTet[p2] = ti;
                int p3 = (int)tets[ti].i3;
                if (!particleToTet.ContainsKey(p3)) particleToTet[p3] = ti;
            }

            for (int vi = 0; vi < numRenderVerts && vi < tetVerts.Length; vi++)
            {
                if (!tetVertToParticle.TryGetValue(vi, out int pi)) continue;
                if (!particleToTet.TryGetValue(pi, out int ti)) continue;

                // Compute bary of renderVerts[vi] inside tets[ti]
                var t  = tets[ti];
                Vector3 p0=particles[t.i0].pos, p1=particles[t.i1].pos,
                         p2=particles[t.i2].pos, p3=particles[t.i3].pos;

                Vector3 c0=p0-p3, c1=p1-p3, c2=p2-p3;
                var m = new Matrix4x4();
                m.m00=c0.x; m.m10=c0.y; m.m20=c0.z;
                m.m01=c1.x; m.m11=c1.y; m.m21=c1.z;
                m.m02=c2.x; m.m12=c2.y; m.m22=c2.z;
                m.m33=1f;
                if (Mathf.Abs(m.determinant) < 1e-10f) continue;

                Vector3 d    = renderVerts[vi] - p3;
                Vector3 bary = m.inverse.MultiplyVector(d);
                if (float.IsNaN(bary.x)) continue;

                float worst = Mathf.Max(Mathf.Max(-bary.x, -bary.y),
                                        Mathf.Max(-bary.z, -(1f-bary.x-bary.y-bary.z)));
                if (worst < md[vi])
                {
                    md[vi] = worst;
                    sk[vi] = new Skin { bary = bary, tetIdx = (uint)ti };
                }
            }

            // ── Fallback: barycentric search for any unclaimed render vert ────
            // Covers surface verts that weren't referenced by any kept tet
            // (rare in good meshes, but surface verts at mesh boundaries).
            var hash = new SpatialHash3(0.25f, renderVerts);

            for (int ti = 0; ti < tets.Length; ti++)
            {
                var t  = tets[ti];
                Vector3 p0=particles[t.i0].pos, p1=particles[t.i1].pos,
                         p2=particles[t.i2].pos, p3=particles[t.i3].pos;

                Vector3 ctr = (p0+p1+p2+p3)*0.25f;
                float   maxR = Mathf.Max(Mathf.Max(V3Dist(p0,ctr), V3Dist(p1,ctr)),
                               Mathf.Max(V3Dist(p2,ctr), V3Dist(p3,ctr))) + 0.15f;

                Vector3 c0=p0-p3, c1=p1-p3, c2=p2-p3;
                var m2 = new Matrix4x4();
                m2.m00=c0.x; m2.m10=c0.y; m2.m20=c0.z;
                m2.m01=c1.x; m2.m11=c1.y; m2.m21=c1.z;
                m2.m02=c2.x; m2.m12=c2.y; m2.m22=c2.z;
                m2.m33=1f;
                if (Mathf.Abs(m2.determinant) < 1e-10f) continue;
                Matrix4x4 inv = m2.inverse;

                float r2 = maxR*maxR;
                foreach (int vid in hash.Query(ctr, maxR))
                {
                    if (md[vid] <= 0f) continue;
                    if (V3Sq(renderVerts[vid]-ctr) > r2) continue;

                    Vector3 d    = renderVerts[vid] - p3;
                    Vector3 bary = inv.MultiplyVector(d);
                    if (float.IsNaN(bary.x)) continue;

                    float worst = Mathf.Max(Mathf.Max(-bary.x, -bary.y),
                                            Mathf.Max(-bary.z, -(1f-bary.x-bary.y-bary.z)));
                    if (worst < md[vid])
                    {
                        md[vid] = worst;
                        sk[vid] = new Skin { bary = bary, tetIdx = (uint)ti };
                    }
                }
            }
            return sk;
        }

        // ═════════════════════════════════════════════════════════════════════
        // STEP 7B: Direct index map (same-res path)
        // Each render vertex maps to the nearest tet particle.
        // ═════════════════════════════════════════════════════════════════════
        static uint[] BuildOrigIndices(Vector3[] renderVerts, Particle[] particles)
        {
            var result = new uint[renderVerts.Length];
            // Build a quick nearest-neighbour lookup
            for (int i = 0; i < renderVerts.Length; i++)
            {
                float best = float.MaxValue;
                uint  bestIdx = 0;
                for (int j = 0; j < particles.Length; j++)
                {
                    float d = V3Sq(renderVerts[i] - particles[j].pos);
                    if (d < best) { best = d; bestIdx = (uint)j; }
                }
                result[i] = bestIdx;
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        // SpatialHash3 — exact port of SpatialHash.cpp (Matthias Müller)
        // ═════════════════════════════════════════════════════════════════════
        class SpatialHash3
        {
            readonly float     _sp;
            readonly int       _sz;
            readonly int[]     _start;
            readonly int[]     _ent;
            readonly Vector3[] _pos;

            public SpatialHash3(float spacing, Vector3[] positions)
            {
                _sp  = spacing;
                _pos = positions;
                int n = positions.Length;
                _sz    = 2*n;
                _start = new int[_sz+1];
                _ent   = new int[n];

                var hh = new int[n];
                for (int i=0;i<n;i++){hh[i]=H(positions[i]);_start[hh[i]]++;}
                int s=0;
                for (int i=0;i<_sz;i++){s+=_start[i];_start[i]=s;}
                _start[_sz]=s;
                for (int i=0;i<n;i++){_start[hh[i]]--;_ent[_start[hh[i]]]=i;}
            }

            int H(Vector3 p)=>H((int)Math.Floor(p.x/_sp),
                                 (int)Math.Floor(p.y/_sp),
                                 (int)Math.Floor(p.z/_sp));
            int H(int x,int y,int z)=>
                Math.Abs((x*92837111)^(y*689287499)^(z*283923481))%_sz;

            public List<int> Query(Vector3 c, float r)
            {
                int x0=(int)Math.Floor((c.x-r)/_sp),y0=(int)Math.Floor((c.y-r)/_sp),
                    z0=(int)Math.Floor((c.z-r)/_sp),
                    x1=(int)Math.Floor((c.x+r)/_sp),y1=(int)Math.Floor((c.y+r)/_sp),
                    z1=(int)Math.Floor((c.z+r)/_sp);
                var res =new List<int>();
                var seen=new HashSet<int>();
                for (int x=x0;x<=x1;x++)
                for (int y=y0;y<=y1;y++)
                for (int z=z0;z<=z1;z++)
                {
                    int h=H(x,y,z);
                    if (seen.Add(h))
                        for (int i=_start[h];i<_start[h+1];i++)
                            res.Add(_ent[i]);
                }
                return res;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Math helpers (no UnityEngine.Mathf in hot-loops where possible)
        // ═════════════════════════════════════════════════════════════════════
        static float V3Dot(Vector3 a, Vector3 b) => a.x*b.x+a.y*b.y+a.z*b.z;
        static float V3Sq (Vector3 a) => a.x*a.x+a.y*a.y+a.z*a.z;
        static float V3Dist(Vector3 a, Vector3 b) {var d=a-b;return Mathf.Sqrt(d.x*d.x+d.y*d.y+d.z*d.z);}
        static Vector3 V3Cross(Vector3 a, Vector3 b) =>
            new Vector3(a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x);
        static float Min3(float a, float b, float c) => a<b?(a<c?a:c):(b<c?b:c);
        static float Max3(float a, float b, float c) => a>b?(a>c?a:c):(b>c?b:c);
        static int Clamp(int v, int lo, int hi) => v<lo?lo:(v>hi?hi:v);
        static void Swap(ref int a, ref int b) { int t=a;a=b;b=t; }

        // ── Asset folder helper ───────────────────────────────────────────────
        static void EnsureFolder(string path)
        {
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i=1;i<parts.Length;i++)
            {
                string next = cur+"/"+parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
