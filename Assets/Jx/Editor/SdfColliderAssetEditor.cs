// SdfColliderAssetEditor.cs  —  place in any Editor/ folder.
//
// Inspector for SdfColliderAsset.
// Renders a 3D marching-cubes iso-surface preview in the Scene view
// when any XpbdSdfCollider component using this asset is selected.
//
// One mesh (iso = 0), drawn in up to two independent passes:
//
//   OUTER pass — Cull Front OFF, Cull Back ON  (front faces only)
//     GREEN. Shows the contact surface — the boundary particles collide against.
//     Visible from outside the collider looking in.
//
//   INNER pass — Cull Front ON, Cull Back OFF  (back faces only)
//     RED. Shows the interior wall face — the inward-facing side of the shell.
//     Visible from inside the collider looking out.
//
// Each pass is independently toggled by a checkbox and has its own alpha.
// Because each pass renders a different set of faces, they never compete
// for the same pixel → zero Z-fighting, no depth-bias tricks needed.

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

		// Single mesh, two materials for independent face-set rendering
		Mesh     _surfaceMesh;
		Material _outerMat;   // Cull Back  — front faces — GREEN
		Material _innerMat;   // Cull Front — back faces  — RED

		// Inspector controls
		bool  _showPreviewInScene = true;
		bool  _showOuter          = true;
		bool  _showInner          = true;
		float _outerAlpha         = 0.40f;
		float _innerAlpha         = 0.35f;

		void OnEnable()
		{
			_bakeResolution = serializedObject.FindProperty("BakeResolution");
			_bakePadding    = serializedObject.FindProperty("BakePadding");
			_sourceMesh     = serializedObject.FindProperty("SourceMesh");

			_outerMat = MakeMat(new Color(0.1f, 0.9f, 0.2f, _outerAlpha), cullFront: false);
			_innerMat = MakeMat(new Color(0.9f, 0.2f, 0.1f, _innerAlpha), cullFront: true);

			var asset = (SdfColliderAsset)target;
			if (asset.IsBaked)
				RebuildMesh(asset);

			SceneView.duringSceneGui += OnSceneGUI;
		}

		void OnDisable()
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			DestroyImmediate(_surfaceMesh);
			DestroyImmediate(_outerMat);
			DestroyImmediate(_innerMat);
		}

		public override void OnInspectorGUI()
		{
			var asset = (SdfColliderAsset)target;

			// ── Bake settings ─────────────────────────────────────────────
			EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);
			serializedObject.Update();
			EditorGUILayout.PropertyField(_bakeResolution);
			EditorGUILayout.PropertyField(_bakePadding);
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

			// ── Status ────────────────────────────────────────────────────
			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
			if (asset.IsBaked)
			{
				int   cells = asset.ResX * asset.ResY * asset.ResZ;
				float kb    = cells * 4f / 1024f;
				int negCount = 0;
				foreach (float v in asset.SdfGrid)
					if (v < 0f) negCount++;
				EditorGUILayout.HelpBox(
					$"Baked  {asset.ResX}×{asset.ResY}×{asset.ResZ} = {cells:N0} cells  ({kb:F0} KB)\n" +
					$"Bounds  {asset.BoundsMin:F3}  →  {asset.BoundsMax:F3}\n" +
					$"Signed voxels (interior): {negCount:N0}  " +
					(negCount == 0
						? "⚠ zero — re-bake with fixed SdfBaker.cs"
						: "✓"),
					negCount == 0 ? MessageType.Warning : MessageType.Info);
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
					RebuildMesh(asset);
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
					DestroyImmediate(_surfaceMesh); _surfaceMesh = null;
				}
			}

			if (!asset.IsBaked)
				return;

			// ── 3D Scene preview controls ─────────────────────────────────
			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("3D Scene Preview", EditorStyles.boldLabel);

			_showPreviewInScene = EditorGUILayout.Toggle("Show in Scene", _showPreviewInScene);
			if (!_showPreviewInScene)
				return;

			EditorGUI.BeginChangeCheck();

			// Outer surface row
			using (new EditorGUILayout.HorizontalScope())
			{
				_showOuter  = EditorGUILayout.ToggleLeft(
					new GUIContent("Outer  (GREEN = front faces, contact surface)"),
					_showOuter, GUILayout.Width(320));
				_outerAlpha = EditorGUILayout.Slider(_outerAlpha, 0f, 1f);
			}

			// Inner surface row
			using (new EditorGUILayout.HorizontalScope())
			{
				_showInner  = EditorGUILayout.ToggleLeft(
					new GUIContent("Inner  (RED   = back faces,  wall interior)"),
					_showInner, GUILayout.Width(320));
				_innerAlpha = EditorGUILayout.Slider(_innerAlpha, 0f, 1f);
			}

			if (EditorGUI.EndChangeCheck())
			{
				_outerMat.color = new Color(0.1f, 0.9f, 0.2f, _outerAlpha);
				_innerMat.color = new Color(0.9f, 0.2f, 0.1f, _innerAlpha);
				SceneView.RepaintAll();
			}

			EditorGUILayout.HelpBox(
				"One mesh (iso = 0), two independent face-culling passes.\n" +
				"OUTER: Cull Back  → front faces → green contact surface.\n" +
				"INNER: Cull Front → back faces  → red interior wall face.\n\n" +
				"Different face sets → zero Z-fighting by construction.\n" +
				"Hollow pipe:  outer green shell + inner green bore wall (red between them).\n" +
				"Solid sphere: outer green shell + inner red hemisphere visible through it.",
				MessageType.None);

			SceneView.RepaintAll();
		}

		// ── Scene view rendering ──────────────────────────────────────────────

		void OnSceneGUI(SceneView sv)
		{
			if (!_showPreviewInScene || _surfaceMesh == null)
				return;

			var asset = (SdfColliderAsset)target;
			if (!asset.IsBaked)
				return;

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

			// Draw inner pass first (back faces, red) so it is behind outer pass
			// in painter's-algorithm order. Depth test handles the rest.
			if (_showInner && _innerMat != null)
			{
				_innerMat.SetPass(0);
				Graphics.DrawMeshNow(_surfaceMesh, localToWorld);
			}
			if (_showOuter && _outerMat != null)
			{
				_outerMat.SetPass(0);
				Graphics.DrawMeshNow(_surfaceMesh, localToWorld);
			}
		}

		void RebuildMesh(SdfColliderAsset asset)
		{
			DestroyImmediate(_surfaceMesh);
			_surfaceMesh = SdfMarchingCubes.Extract(asset, 0f);
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
				if (asset.IsBaked)
					Debug.Log($"[SdfBaker] Done. {asset.ResX}³ = {asset.SdfGrid.Length:N0} cells.", asset);
			}
			finally
			{
				EditorUtility.ClearProgressBar();
			}
		}

		// cullFront=false → Cull Back  → only front faces rendered (outer/green)
		// cullFront=true  → Cull Front → only back  faces rendered (inner/red)
		static Material MakeMat(Color color, bool cullFront)
		{
			string cull = cullFront ? "Front" : "Back";
			string shaderSrc = $@"
Shader ""Hidden/XPBD_SdfDebug_{cull}""
{{
    Properties {{ _Color (""Color"", Color) = (1,1,1,0.4) }}
    SubShader
    {{
        Tags {{ ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }}
        Pass
        {{
            Cull {cull}
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct v2f {{ float4 pos : SV_POSITION; float3 nrm : TEXCOORD0; }};
            float4 _Color;
            v2f vert(float4 vertex : POSITION, float3 normal : NORMAL)
            {{
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.nrm = UnityObjectToWorldNormal(normal);
                return o;
            }}
            half4 frag(v2f i) : SV_Target
            {{
                float ndotl = abs(dot(normalize(i.nrm), normalize(float3(1,1,-1))));
                float shade = lerp(0.55, 1.0, ndotl);
                return half4(_Color.rgb * shade, _Color.a);
            }}
            ENDCG
        }}
    }}
}}";
			var shader = ShaderUtil.CreateShaderAsset(shaderSrc, false);
			var mat    = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
			mat.color  = color;
			return mat;
		}
	}
}
#endif
