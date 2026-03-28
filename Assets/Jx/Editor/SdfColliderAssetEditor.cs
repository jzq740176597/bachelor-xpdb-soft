// SdfColliderAssetEditor.cs
// Place in any Editor/ folder.
//
// Adds a Bake button and status info to the SdfColliderAsset inspector.
// Usage:
//   1. Create asset: right-click Project → XPBD → SDF Collider Asset
//   2. Assign a mesh (MeshFilter or direct Mesh reference) in XpbdSdfCollider
//      OR drag a Mesh asset onto the "Source Mesh" field below.
//   3. Click "Bake SDF".
//   4. Assign the baked asset to XpbdSdfCollider.SdfAsset.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace XPBD
{
	[CustomEditor(typeof(SdfColliderAsset))]
	public class SdfColliderAssetEditor : UnityEditor.Editor
	{
		// Mesh to bake from when editing the asset directly in the Project.
		// XpbdSdfCollider can also trigger a bake at Awake from its own mesh source.
		SerializedProperty _bakeResolution;
		SerializedProperty _bakePadding;
		SerializedProperty _sourceMesh;

		void OnEnable()
		{
			_bakeResolution = serializedObject.FindProperty("BakeResolution");
			_bakePadding = serializedObject.FindProperty("BakePadding");
			// [3/28/2026 jzq]
			_sourceMesh = serializedObject.FindProperty("SourceMesh");
		}

		public override void OnInspectorGUI()
		{
			var asset = (SdfColliderAsset) target;

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
				int cells = asset.ResX * asset.ResY * asset.ResZ;
				float kb = cells * 4f / 1024f;
				EditorGUILayout.HelpBox(
					$"Baked  {asset.ResX}×{asset.ResY}×{asset.ResZ} = {cells:N0} cells  ({kb:F0} KB)\n" +
					$"Bounds {asset.BoundsMin:F3} → {asset.BoundsMax:F3}",
					MessageType.Info);
			}
			else
			{
				EditorGUILayout.HelpBox(
					"Not baked yet. Assign a Source Mesh and click Bake SDF.",
					MessageType.Warning);
			}

			// ── Bake button ───────────────────────────────────────────────
			EditorGUILayout.Space(4);
			bool canBake = _sourceMesh.objectReferenceValue != null;
			using (new EditorGUI.DisabledGroupScope(!canBake))
			{
				if (GUILayout.Button("Bake SDF", GUILayout.Height(28)))
					DoBake(asset);
			}
			if (!canBake)
				EditorGUILayout.HelpBox(
					"Assign a Source Mesh above to enable baking.", MessageType.None);

			// ── Clear button ──────────────────────────────────────────────
			using (new EditorGUI.DisabledGroupScope(!asset.IsBaked))
			{
				if (GUILayout.Button("Clear Bake Data"))
				{
					asset.SdfGrid = null;
					asset.ResX = asset.ResY = asset.ResZ = 0;
					EditorUtility.SetDirty(asset);
					AssetDatabase.SaveAssets();
				}
			}
		}

		void DoBake(SdfColliderAsset asset)
		{
			if (_sourceMesh == null)
				return;

			var progress = "Baking SDF...";
			EditorUtility.DisplayProgressBar("XPBD SDF Baker", progress, 0f);
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
	}
}
#endif
