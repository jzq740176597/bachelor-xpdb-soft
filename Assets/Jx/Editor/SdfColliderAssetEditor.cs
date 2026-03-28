// SdfColliderAssetEditor.cs  —  place in any Editor/ folder.
//
// Inspector for SdfColliderAsset:
//   • Bake button with progress bar
//   • Axis-slice visualiser: renders one 2D cross-section of the SDF grid
//     as a colour-coded texture (blue=outside, red=inside, green=surface)
//   • Slice axis (X/Y/Z) + slice position slider
//   • "Rebuild on change" toggle for live preview while tuning parameters

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

		// Visualiser state
		enum SliceAxis { X, Y, Z }
		SliceAxis _sliceAxis   = SliceAxis.Y;
		float     _sliceT      = 0.5f;       // 0–1 normalised position along axis
		Texture2D _sliceTex;
		bool      _showVis     = true;

		// Colour range scale — SDF values beyond ±_visRange show max saturation
		float _visRange = 0.3f;

		void OnEnable()
		{
			_bakeResolution = serializedObject.FindProperty("BakeResolution");
			_bakePadding    = serializedObject.FindProperty("BakePadding");
			// [3/28/2026 jzq]
			_sourceMesh = serializedObject.FindProperty("SourceMesh");
		}

		void OnDisable()
		{
			if (_sliceTex != null)
			{
				DestroyImmediate(_sliceTex);
				_sliceTex = null;
			}
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

			// ── Buttons ───────────────────────────────────────────────────
			EditorGUILayout.Space(4);
			bool canBake = _sourceMesh.objectReferenceValue != null;
			using (new EditorGUI.DisabledGroupScope(!canBake))
			{
				if (GUILayout.Button("Bake SDF", GUILayout.Height(28)))
				{
					DoBake(asset);
					RebuildSlice(asset);
				}
			}
			if (!canBake)
				EditorGUILayout.HelpBox("Assign a Source Mesh above.", MessageType.None);

			// ── Clear button ──────────────────────────────────────────────
			using (new EditorGUI.DisabledGroupScope(!asset.IsBaked))
			{
				if (GUILayout.Button("Clear Bake Data"))
				{
					asset.SdfGrid = null;
					asset.ResX = asset.ResY = asset.ResZ = 0;
					EditorUtility.SetDirty(asset);
					AssetDatabase.SaveAssets();
					DestroyImmediate(_sliceTex);
					_sliceTex = null;
				}
			}

			// ── Slice visualiser ──────────────────────────────────────────
			if (!asset.IsBaked)
				return;

			EditorGUILayout.Space(8);
			_showVis = EditorGUILayout.Foldout(_showVis, "SDF Slice Visualiser", true);
			if (!_showVis)
				return;

			EditorGUI.indentLevel++;

			bool changed = false;

			var newAxis = (SliceAxis)EditorGUILayout.EnumPopup("Slice Axis", _sliceAxis);
			if (newAxis != _sliceAxis)
			{
				_sliceAxis = newAxis;
				changed = true;
			}

			float newT = EditorGUILayout.Slider("Slice Position", _sliceT, 0f, 1f);
			if (!Mathf.Approximately(newT, _sliceT))
			{
				_sliceT = newT;
				changed = true;
			}

			float newRange = EditorGUILayout.FloatField("Colour Range (m)", _visRange);
			newRange = Mathf.Max(newRange, 0.001f);
			if (!Mathf.Approximately(newRange, _visRange))
			{
				_visRange = newRange;
				changed = true;
			}

			if (changed || _sliceTex == null)
				RebuildSlice(asset);

			if (_sliceTex != null)
			{
				// Draw the texture — scale to fit inspector width
				float w = EditorGUIUtility.currentViewWidth - 32f;
				var   r = GUILayoutUtility.GetRect(w, w);
				EditorGUI.DrawPreviewTexture(r, _sliceTex, null, ScaleMode.ScaleToFit);

				// Legend
				EditorGUILayout.BeginHorizontal();
				DrawColorSwatch(Color.blue);
				EditorGUILayout.LabelField("Outside");
				DrawColorSwatch(Color.green);
				EditorGUILayout.LabelField("Surface (≈0)");
				DrawColorSwatch(Color.red);
				EditorGUILayout.LabelField("Inside");
				EditorGUILayout.EndHorizontal();

				EditorGUILayout.HelpBox(
					"Green band = contact surface.\n" +
					"Red region = solid interior (particles here get pushed out).\n" +
					"Blue region = exterior (no contact).\n" +
					"Thin shell: expect a thin red band at each wall — outside walls " +
					"are blue, bore/air is also blue, shell material is red.",
					MessageType.None);
			}

			EditorGUI.indentLevel--;
		}

		// Build a 2D texture of the SDF slice.
		void RebuildSlice(SdfColliderAsset asset)
		{
			if (!asset.IsBaked)
				return;

			int res = asset.ResX; // assumes cubic for now
			int sliceIdx = Mathf.Clamp(Mathf.RoundToInt(_sliceT * (res - 1)), 0, res - 1);

			if (_sliceTex == null || _sliceTex.width != res)
			{
				DestroyImmediate(_sliceTex);
				_sliceTex = new Texture2D(res, res, TextureFormat.RGB24, false)
				{
					filterMode = FilterMode.Point,
					wrapMode   = TextureWrapMode.Clamp,
				};
			}

			var pixels = new Color[res * res];
			for (int j = 0; j < res; j++)
			{
				for (int i = 0; i < res; i++)
				{
					float sdf;
					switch (_sliceAxis)
					{
						case SliceAxis.X:
							sdf = asset.SdfGrid[sliceIdx + j * res + i * res * res];
							break;
						case SliceAxis.Z:
							sdf = asset.SdfGrid[i + j * res + sliceIdx * res * res];
							break;
						default: // Y
							sdf = asset.SdfGrid[i + sliceIdx * res + j * res * res];
							break;
					}
					pixels[j * res + i] = SdfToColor(sdf);
				}
			}

			_sliceTex.SetPixels(pixels);
			_sliceTex.Apply();
		}

		// Map SDF value to colour:
		//   outside (positive): blue, fading toward green at surface
		//   inside  (negative): red,  fading toward green at surface
		Color SdfToColor(float sdf)
		{
			float t = Mathf.Clamp01(Mathf.Abs(sdf) / _visRange);
			if (sdf >= 0f)
				return Color.Lerp(Color.green, Color.blue,  t);
			return     Color.Lerp(Color.green, Color.red,   t);
		}

		void DrawColorSwatch(Color c)
		{
			var r = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
			EditorGUI.DrawRect(r, c);
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
	}
}
#endif
