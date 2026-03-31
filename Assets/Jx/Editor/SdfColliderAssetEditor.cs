// SdfColliderAssetEditor.cs  —  place in any Editor/ folder.
//
// Inspector for SdfColliderAsset.
// Renders a 3D marching-cubes iso-surface preview in the Scene view
// when any XpbdSdfCollider component using this asset is selected.
//
// Two surfaces are drawn:
//   GREEN  (iso = 0)     — the contact surface. Soft particles crossing here get pushed out.
//   RED    (iso = -skin) — the interior solid. Anything inside is deeply penetrating.
//
// The preview is rebuilt only when the asset changes or the user clicks Bake.
// It is NOT built at runtime — editor only.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace XPBD
{
	[CustomEditor(typeof(SdfColliderAsset))]
	public class SdfColliderAssetEditor : UnityEditor.Editor
	{
		SerializedProperty _bakeResolution;
		SerializedProperty _bakePadding;
		SerializedProperty _sourceMesh;

		// Preview meshes (local-space, rebuilt on bake)
		Mesh _surfaceMesh;   // iso = 0,      green — contact surface
		Mesh _interiorMesh;  // iso = auto,   red   — wall material mid-depth

		// Inspector controls
		bool  _showPreviewInScene = true;
		float _surfaceAlpha       = 0.35f;
		float _interiorAlpha      = 0.20f;

		// Materials — created once, shared across repaint calls
		Material _surfaceMat;
		Material _interiorMat;

		void OnEnable()
		{
			_bakeResolution = serializedObject.FindProperty("BakeResolution");
			_bakePadding    = serializedObject.FindProperty("BakePadding");
			// [3/28/2026 jzq]
			_sourceMesh = serializedObject.FindProperty("SourceMesh");
			
			_surfaceMat  = CreateTransparentMat(new Color(0.1f, 0.9f, 0.2f, _surfaceAlpha));
			_interiorMat = CreateTransparentMat(new Color(0.9f, 0.1f, 0.1f, _interiorAlpha));

			var asset = (SdfColliderAsset)target;
			if (asset.IsBaked)
				RebuildPreviewMeshes(asset);

			SceneView.duringSceneGui += OnSceneGUI;
		}

		void OnDisable()
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			DestroyImmediate(_surfaceMesh);
			DestroyImmediate(_interiorMesh);
			DestroyImmediate(_surfaceMat);
			DestroyImmediate(_interiorMat);
		}

		public override void OnInspectorGUI()
		{
			var asset = (SdfColliderAsset)target;

			// ── Bake settings ─────────────────────────────────────────────
			EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);
			serializedObject.Update();
			EditorGUILayout.PropertyField(_bakeResolution);
			EditorGUILayout.PropertyField(_bakePadding);
			// [3/28/2026 jzq]
			{
				EditorGUI.BeginChangeCheck();
				EditorGUILayout.PropertyField(_sourceMesh);
				if (EditorGUI.EndChangeCheck())
				{
					serializedObject.ApplyModifiedProperties();
					asset.ClearData();
					serializedObject.Update();
				}
			}
			serializedObject.ApplyModifiedProperties();

			//_sourceMesh = (Mesh) EditorGUILayout.ObjectField(
			//	"Source Mesh", _sourceMesh, typeof(Mesh), false);

			// ── Status ────────────────────────────────────────────────────
			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
			if (asset.IsBaked)
			{
				int   cells = asset.ResX * asset.ResY * asset.ResZ;
				float kb    = cells * 4f / 1024f;
				EditorGUILayout.HelpBox(
					$"Baked  {asset.ResX}×{asset.ResY}×{asset.ResZ} = {cells:N0} cells  ({kb:F0} KB)\n" +
					$"Bounds  {asset.BoundsMin:F3}  →  {asset.BoundsMax:F3}",
					MessageType.Info);
			}
			else
			{
				EditorGUILayout.HelpBox(
					"Not baked. Assign a Source Mesh and click Bake SDF.",
					MessageType.Warning);
			}

			// ── Bake / Clear buttons ───────────────────────────────────────
			EditorGUILayout.Space(4);
			bool canBake = _sourceMesh.objectReferenceValue != null;
			using (new EditorGUI.DisabledGroupScope(!canBake))
			{
				if (GUILayout.Button("Bake SDF", GUILayout.Height(28)))
				{
					DoBake(asset);
					RebuildPreviewMeshes(asset);
				}
			}
			if (!canBake)
				EditorGUILayout.HelpBox("Assign a Source Mesh above.", MessageType.None);

			using (new EditorGUI.DisabledGroupScope(!asset.IsBaked))
			{
				if (GUILayout.Button("Clear Bake Data"))
				{
					asset.SdfGrid = null;
					asset.ResX = asset.ResY = asset.ResZ = 0;
					EditorUtility.SetDirty(asset);
					AssetDatabase.SaveAssets();
					DestroyImmediate(_interiorMesh); _interiorMesh = null;
				}
			}

			// ── 3D Scene preview controls ─────────────────────────────────
			if (!asset.IsBaked)
				return;

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("3D Scene Preview", EditorStyles.boldLabel);

			_showPreviewInScene = EditorGUILayout.Toggle("Show in Scene", _showPreviewInScene);

			EditorGUI.BeginChangeCheck();
			_surfaceAlpha  = EditorGUILayout.Slider("Surface Alpha",  _surfaceAlpha,  0f, 1f);
			_interiorAlpha = EditorGUILayout.Slider("Interior Alpha", _interiorAlpha, 0f, 1f);
			if (EditorGUI.EndChangeCheck())
			{
				_surfaceMat.color  = new Color(0.1f, 0.9f, 0.2f, _surfaceAlpha);
				_interiorMat.color = new Color(0.9f, 0.1f, 0.1f, _interiorAlpha);
				RebuildPreviewMeshes(asset);
			}

			// Show what interior iso is being used
			float autoIso = ComputeAutoInteriorIso(asset);
			string interiorDesc = autoIso < 0f
				? $"RED   = wall interior (iso = {autoIso:F5}, auto = half cell diagonal depth)."
				: "RED   = not drawn (thin-shell mesh — wall depth < half cell diagonal, would Z-fight).";
			EditorGUILayout.HelpBox(
				"GREEN = contact surface (iso = 0). Particles crossing here get pushed out.\n" +
				interiorDesc + "\n\n" +
				"Hollow pipe/bracket: GREEN only — thin shell, no interior mesh drawn.\n" +
				"Solid sphere/cube:   GREEN outer shell + RED thin ring just inside.\n" +
				"If RED fills entire interior, re-bake — normals may be flipped in DCC tool.",
				MessageType.None);

			if (_showPreviewInScene)
				SceneView.RepaintAll();
		}

		// ── Scene view rendering ──────────────────────────────────────────────

		void OnSceneGUI(SceneView sv)
		{
			if (!_showPreviewInScene)
				return;

			var asset = (SdfColliderAsset)target;
			if (!asset.IsBaked)
				return;

			// Find any XpbdSdfCollider in the scene using this asset to get a transform
			// for converting local→world. If none found, draw at origin.
			var colliders = Object.FindObjectsByType<XpbdSdfCollider>(FindObjectsSortMode.None);
			Matrix4x4 localToWorld = Matrix4x4.identity;
			foreach (var col in colliders)
			{
				if (col.SdfAsset == asset)
				{
					localToWorld = col.transform.localToWorldMatrix;
					break;
				}
			}

			if (_surfaceMesh != null && _surfaceMat != null)
			{
				_surfaceMat.SetPass(0);
				Graphics.DrawMeshNow(_surfaceMesh, localToWorld);
			}
			if (_interiorMesh != null && _interiorMat != null)
			{
				_interiorMat.SetPass(0);
				Graphics.DrawMeshNow(_interiorMesh, localToWorld);
			}
		}

		void RebuildPreviewMeshes(SdfColliderAsset asset)
		{
			DestroyImmediate(_surfaceMesh);
			DestroyImmediate(_interiorMesh);
			_surfaceMesh  = SdfMarchingCubes.Extract(asset, 0f);
			// Only draw interior mesh for solid/thick-wall objects.
			// For thin shells (pipe) the negative region is < cellDiag deep —
			// the interior iso would land on top of the green surface → Z-fighting fracture.
			float autoIso = ComputeAutoInteriorIso(asset);
			_interiorMesh = autoIso < 0f ? SdfMarchingCubes.Extract(asset, autoIso) : null;
		}

		// Returns the iso-level for the red interior preview mesh, or 0 if the
		// negative region is too thin to draw without Z-fighting the green surface.
		//
		// Rule:
		//   • If the deepest negative value is shallower than half a cell diagonal
		//     (thin-shell mesh: pipe, bracket, hollow box) → return 0 (skip interior).
		//   • Otherwise (solid mesh: sphere, cube, terrain) → use half the cell diagonal
		//     as the interior depth, clamped so it never exceeds the actual data range.
		//
		// Using cellDiag*0.5 as interior depth gives a thin but visible red ring just
		// inside the green surface on any solid shape, regardless of its size.
		// It does NOT use minVal*0.5 (which would fill a huge interior on large solids).
		static float ComputeAutoInteriorIso(SdfColliderAsset asset)
		{
			if (!asset.IsBaked)
				return 0f;

			// Find the most-negative SDF value in the grid
			float minVal = 0f;
			foreach (float v in asset.SdfGrid)
			{
				if (v < minVal)
					minVal = v;
			}

			float cellDiag  = asset.CellSize.magnitude;
			float halfDiag  = cellDiag * 0.5f;

			// Thin shell: wall depth < half cell diagonal → surfaces would coincide → skip
			if (-minVal < halfDiag)
				return 0f;

			// Solid/thick: clamp interior iso to half-diagonal depth so the red ring
			// is always thin regardless of how large the solid interior is
			float iso = -halfDiag;
			// Don't go deeper than the actual data (shouldn't happen after the clamp, but safe)
			return Mathf.Max(iso, minVal * 0.99f);
		}

		void DoBake(SdfColliderAsset asset)
		{
			if (_sourceMesh == null)
				return;
			EditorUtility.DisplayProgressBar("XPBD SDF Baker", "Baking...", 0f);
			try
			{
				SdfBaker.Bake(_sourceMesh.objectReferenceValue as Mesh, asset);
				EditorUtility.SetDirty(asset);
				AssetDatabase.SaveAssets();
				if (asset.IsBaked) // [3/28/2026 jzq]
					Debug.Log($"[SdfBaker] Done. {asset.ResX}³ = {asset.SdfGrid.Length:N0} cells.", asset);
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		static Material CreateTransparentMat(Color color)
		{
			// Build a minimal inline shader that is:
			//   • Double-sided (Cull Off) — marching cubes normals point outward,
			//     so backfaces would be culled without this.
			//   • Alpha-blended transparent
			//   • Unlit — no lighting needed for a debug visualiser
			//   • Works in Built-in RP, URP, and HDRP (no pipeline-specific shader needed)
			const string shaderSrc = @"
Shader ""Hidden/XPBD_SdfDebug""
{
    Properties { _Color (""Color"", Color) = (1,1,1,0.4) }
    SubShader
    {
        Tags { ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }
        Pass
        {
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct v2f { float4 pos : SV_POSITION; float3 nrm : TEXCOORD0; };
            float4 _Color;
            v2f vert(float4 vertex : POSITION, float3 normal : NORMAL)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.nrm = UnityObjectToWorldNormal(normal);
                return o;
            }
            half4 frag(v2f i) : SV_Target
            {
                // Simple diffuse shading so depth is readable (optional)
                float ndotl = abs(dot(normalize(i.nrm), normalize(float3(1,1,-1))));
                float shade = lerp(0.6, 1.0, ndotl);
                return half4(_Color.rgb * shade, _Color.a);
            }
            ENDCG
        }
    }
}";
			var shader = ShaderUtil.CreateShaderAsset(shaderSrc, false);
			var mat    = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			mat.color  = color;
			return mat;
		}
	}
}
#endif
