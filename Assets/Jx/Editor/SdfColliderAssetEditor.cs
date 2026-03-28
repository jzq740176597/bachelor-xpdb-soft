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
		Mesh _surfaceMesh;   // iso = 0,      green
		Mesh _interiorMesh;  // iso = -_skin,  red

		// Inspector controls
		bool  _showPreviewInScene = true;
		float _previewSkin        = 0.05f;  // interior offset in metres
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
			_previewSkin   = EditorGUILayout.Slider("Interior Offset (m)", _previewSkin, 0.001f, 0.3f);
			_surfaceAlpha  = EditorGUILayout.Slider("Surface Alpha",  _surfaceAlpha,  0f, 1f);
			_interiorAlpha = EditorGUILayout.Slider("Interior Alpha", _interiorAlpha, 0f, 1f);
			if (EditorGUI.EndChangeCheck())
			{
				_surfaceMat.color  = new Color(0.1f, 0.9f, 0.2f, _surfaceAlpha);
				_interiorMat.color = new Color(0.9f, 0.1f, 0.1f, _interiorAlpha);
				RebuildPreviewMeshes(asset);
			}

			EditorGUILayout.HelpBox(
				"GREEN = contact surface (iso = 0). Particles crossing this get pushed out.\n" +
				"RED   = solid interior (iso = −offset). Deep penetration zone.\n\n" +
				"Hollow pipe: expect GREEN shells on outer + inner walls, RED band = shell material.\n" +
				"If bore shows RED, the mesh normals are flipped — flip them in Blender.",
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
			_interiorMesh = SdfMarchingCubes.Extract(asset, -_previewSkin);
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
			// Use the built-in transparent diffuse shader available in all Unity versions
			var mat = new Material(Shader.Find("Transparent/Diffuse")
					?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
					?? Shader.Find("Standard"));
			mat.color = color;
			if (mat.HasProperty("_Mode"))
			{
				mat.SetFloat("_Mode", 3);
				mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
				mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
				mat.SetInt("_ZWrite", 0);
				mat.DisableKeyword("_ALPHATEST_ON");
				mat.EnableKeyword("_ALPHABLEND_ON");
				mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
				mat.renderQueue = 3000;
			}
			mat.hideFlags = HideFlags.HideAndDontSave;
			return mat;
		}
	}
}
#endif
