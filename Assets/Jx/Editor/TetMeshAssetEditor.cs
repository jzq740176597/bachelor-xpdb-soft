// TetMeshAssetEditor.cs
// Custom Editor for TetrahedralMeshAsset — scene-view particle paint / rect-select,
// render-mode visualization, particle property editing, and particle-group management.
//
// Inspired by Obi Softbody 7.0 asset inspector workflow.
//
// USAGE:
//   1. Select a TetrahedralMeshAsset in the Project window.
//   2. Click "Edit" to enter particle editing mode.
//      The Scene View opens and shows particles overlaid on the render mesh.
//   3. Choose a selection mode (Paint or Circle) from the toolbar.
//   4. Paint / drag-circle to select particles in the scene view.
//   5. Use "Particle groups" section to create groups from the selection.
//   6. Use "Properties" section to inspect / edit invMass of selected particles.
//   7. Toggle Render Modes to visualize mesh, edges, tets, etc.
//   8. Click "Done" (or press Escape) to exit edit mode.
//
// ARCHITECTURE NOTES:
//   - The editor uses SceneView.duringSceneGui to intercept mouse events.
//   - Particle world positions = asset rest positions (no runtime transform).
//   - All edits call EditorUtility.SetDirty + AssetDatabase.SaveAssets.
//   - The editor holds transient selection state (HashSet<int>) — not serialised.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace XPBD
{
	[CustomEditor(typeof(TetrahedralMeshAsset))]
	public sealed class TetMeshAssetEditor : UnityEditor.Editor
	{
		// ── Edit-mode state ───────────────────────────────────────────────────
		bool _editing;

		// Selection
		enum SelectMode
		{
			Paint, Rectangle
		}
		SelectMode _selectMode = SelectMode.Paint;
		float _brushSize = 0.15f;   // world-space radius
		enum CullingMode
		{
			None, Back, Depth
		}
		CullingMode _cullingMode = CullingMode.Back;

		readonly HashSet<int> _selected = new();

		// Rectangle-drag state
		bool _rectDragging;
		Vector2 _rectStart;
		SelectOp _rectOp = SelectOp.New;   // op captured at drag-start
		SelectOp _paintStrokeOp = SelectOp.New;  // op captured at stroke-start (MouseDown)

		// Render modes (bitmask flags)
		bool _showParticles = true;
		bool _showMesh = true;
		bool _showEdges = false;
		bool _showTets = false;

		// Property panel
		enum EditProperty
		{
			InvMass
		}
		EditProperty _editProp = EditProperty.InvMass;
		float _propValue = 0f;
		bool _propMixed = false;

		// Property-based selection
		float _selMinMass = 0f, _selMaxMass = 0f;

		// Groups panel — which group entry is being renamed
		int _renamingGroup = -1;

		// ── Dirty / save state ────────────────────────────────────────────────
		// _dirty: true when any edit has happened since entering Edit mode.
		// _snapshot: deep copy of asset data captured at Edit-enter used for Discard.
		bool _dirty;
		ParticleData[] _snapshotParticles;
		ParticleGroup[] _snapshotGroups;

		// Particle display
		float _particleSizeScale = 1.0f;    // multiplier on the auto-computed handle size
		bool _showInnerParticles = true;    // draw occluded/inner particles in dim colour

		// Colours
		static readonly Color ColParticle = new Color(1f, 0.55f, 0.05f, 0.9f);
		static readonly Color ColInner = new Color(1f, 0.55f, 0.05f, 0.25f); // dim: inner/occluded
		static readonly Color ColSelected = new Color(0.15f, 0.8f, 1f, 1.0f);
		static readonly Color ColEdge = new Color(0.5f, 0.5f, 0.5f, 0.4f);
		static readonly Color ColTet = new Color(0.3f, 1.0f, 0.3f, 0.15f);
		static readonly Color ColBrush = new Color(1f, 1f, 1f, 0.25f);

		// ── Inspector GUI ─────────────────────────────────────────────────────
		public override void OnInspectorGUI()
		{
			var asset = (TetrahedralMeshAsset) target;

			// Header stat bar
			EditorGUILayout.LabelField(
				$"Particles: {asset.Particles?.Length ?? 0}   " +
				$"Edges: {asset.Edges?.Length ?? 0}   " +
				$"Tets: {asset.Tetrahedrals?.Length ?? 0}",
				EditorStyles.centeredGreyMiniLabel);
			EditorGUILayout.Space(4);

			// ── Edit / Save / Discard buttons ───────────────────────────────
			if (!_editing)
			{
				using (new EditorGUILayout.HorizontalScope())
				{
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("  Edit  ", GUILayout.Width(80), GUILayout.Height(24)))
						SetEditing(true);
					GUILayout.FlexibleSpace();
				}
				EditorGUILayout.Space(6);
				// Read-only default fields while not editing
				EditorGUI.BeginDisabledGroup(true);
				DrawDefaultInspector();
				EditorGUI.EndDisabledGroup();
				return;
			}

			// While editing: Save As | Done (2 buttons)
			// Done behaviour:
			//   clean (no changes) → exit immediately, no dialog
			//   dirty (changes)    → 3-option dialog: Save / Discard / Cancel
			EditorGUILayout.Space(4);
			using (new EditorGUILayout.HorizontalScope())
			{
				// Save As — always available; pops a save-file dialog for a copy
				if (GUILayout.Button("Save As…", GUILayout.Height(24)))
					DoSaveAs(asset);

				// Done — smart exit
				var doneStyle = new GUIStyle(GUI.skin.button);
				if (_dirty)
					doneStyle.fontStyle = FontStyle.Bold;
				string doneLabel = _dirty ? "Done  *" : "Done";
				if (GUILayout.Button(doneLabel, doneStyle, GUILayout.Height(24)))
				{
					if (!_dirty)
					{
						// No changes → silent exit
						SetEditing(false);
					}
					else
					{
						// Changes exist → ask what to do (3 options via two dialogs:
						// Unity's DisplayDialog only supports 2 buttons natively;
						// we use the 3-button overload available since Unity 2019.1)
						int choice = EditorUtility.DisplayDialogComplex(
							"Exit edit mode",
							"You have unsaved changes.",
							"Save",      // 0
							"Cancel",    // 1  (middle = cancel is Unity convention)
							"Discard"    // 2
						);
						if (choice == 0)
						{
							DoSave(asset);
							SetEditing(false);
						}
						else if (choice == 2)
						{
							DoDiscard(asset);
							SetEditing(false);
						}
						// choice == 1 → Cancel: do nothing, stay in edit mode
					}
				}
			}
			EditorGUILayout.Space(6);

			// ── While editing ──────────────────────────────────────────────

			// Particle Selection toolbar
			DrawSectionHeader("Particle selection");

			int totCount = asset.Particles?.Length ?? 0;

			// Visible set = the particles currently shown (respects culling + _showInnerParticles).
			// All selection ops (count, All/None/Invert) operate on this set only.
			var visibleSet = GetVisibleSet(asset);
			int visCount = visibleSet.Count;
			int selCount = _selected.Count(i => visibleSet.Contains(i));

			string selInfo = selCount == 0
				? "Click/drag: new sel.  Shift+click/drag: append.  Ctrl+click/drag: subtract."
				: $"{selCount} / {visCount} visible selected  ({totCount} total)"
				  + "Shift=append  Ctrl=subtract  (paint & rect modes)";
			EditorGUILayout.HelpBox(selInfo, MessageType.None);

			// Mode toolbar
			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.Label("Mode", GUILayout.Width(42));
				_selectMode = (SelectMode) GUILayout.Toolbar((int) _selectMode,
					new[] { "Paint", "Rectangle" }, GUILayout.Height(22));
			}

			_brushSize = EditorGUILayout.Slider("Brush size", _brushSize, 0.01f, 2f);

			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.Label("Culling", GUILayout.Width(52));
				_cullingMode = (CullingMode) GUILayout.Toolbar((int) _cullingMode,
					new[] { "None", "Back", "Depth" }, GUILayout.Height(20));
			}
			if (_cullingMode == CullingMode.Depth)
				EditorGUILayout.HelpBox(
					"Depth culling uses mesh raycasting. Accurate but slower for high-res meshes.",
					MessageType.None);

			// All / None / Invert operate on the VISIBLE set only
			using (new EditorGUILayout.HorizontalScope())
			{
				if (GUILayout.Button("All"))
					foreach (int i in visibleSet)
						_selected.Add(i);
				if (GUILayout.Button("None"))
					foreach (int i in visibleSet)
						_selected.Remove(i);
				if (GUILayout.Button("Invert"))
					foreach (int i in visibleSet)
					{
						if (_selected.Contains(i))
							_selected.Remove(i);
						else
							_selected.Add(i);
					}
			}

			EditorGUILayout.Space(4);

			// ── Property-based selection ──────────────────────────────────
			DrawSectionHeader("Property-based selection");
			EditorGUILayout.LabelField("Drag slider to select by Mass.", EditorStyles.miniLabel);
			using (new EditorGUILayout.HorizontalScope())
			{
				_selMinMass = EditorGUILayout.FloatField("Min Mass", _selMinMass);
				_selMaxMass = EditorGUILayout.FloatField("Max Mass", _selMaxMass);
			}
			if (GUILayout.Button("Select by Mass range"))
			{
				// Only operates on visible set (respects culling + inner-particle toggle)
				var vis = GetVisibleSet(asset);
				foreach (int i in vis)
				{
					float m = asset.Particles[i].InvMass < 1e-6f ? 0f : 1f / asset.Particles[i].InvMass;
					if (m >= _selMinMass && m <= _selMaxMass)
						_selected.Add(i);
					else
						_selected.Remove(i);
				}
			}

			EditorGUILayout.Space(4);

			// ── Properties ────────────────────────────────────────────────
			DrawSectionHeader("Properties");
			if (asset.Particles != null && totCount > 0)
			{
				EditorGUILayout.LabelField(
					selCount == 0 ? "Select particles to edit their properties."
								  : $"Editing {selCount} particle(s). Property: {_editProp}",
					EditorStyles.miniLabel);

				_editProp = (EditProperty) EditorGUILayout.EnumPopup("Property", _editProp);

				RefreshPropertyValue(asset);

				EditorGUI.showMixedValue = _propMixed;
				EditorGUI.BeginChangeCheck();
				float newVal = EditorGUILayout.FloatField(
					_editProp == EditProperty.InvMass ? "Inv Mass (0=pinned)" : _editProp.ToString(),
					_propValue);
				if (EditorGUI.EndChangeCheck() && selCount > 0)
				{
					Undo.RecordObject(asset, "Edit Particle Property");
					foreach (int idx in _selected)
					{
						var p = asset.Particles[idx];
						p.InvMass = Mathf.Max(0f, newVal);
						asset.Particles[idx] = p;
					}
					MarkDirty(asset);
					_propValue = newVal;
					_propMixed = false;
				}
				EditorGUI.showMixedValue = false;
			}

			EditorGUILayout.Space(4);

			// ── Render modes ──────────────────────────────────────────────
			DrawSectionHeader("Render modes");
			using (new EditorGUILayout.HorizontalScope())
			{
				_showParticles = GUILayout.Toggle(_showParticles, "Particles", GUI.skin.button, GUILayout.Height(20));
				_showMesh = GUILayout.Toggle(_showMesh, "Mesh", GUI.skin.button, GUILayout.Height(20));
				_showEdges = GUILayout.Toggle(_showEdges, "Edges", GUI.skin.button, GUILayout.Height(20));
				_showTets = GUILayout.Toggle(_showTets, "Tets", GUI.skin.button, GUILayout.Height(20));
			}
			if (_showParticles)
			{
				_particleSizeScale = EditorGUILayout.Slider("Particle size", _particleSizeScale, 0.1f, 5f);
				_showInnerParticles = EditorGUILayout.Toggle("Show inner particles", _showInnerParticles);
				if (_showInnerParticles)
					EditorGUILayout.HelpBox(
						"Inner/occluded particles shown in dim colour. " +
						"Culling (below) controls whether they are selectable.",
						MessageType.None);
			}

			EditorGUILayout.Space(4);

			// ── Particle groups ───────────────────────────────────────────
			DrawSectionHeader("Particle groups");
			DrawGroupsPanel(asset);

			EditorGUILayout.Space(8);
			SceneView.RepaintAll();
		}

		// ── Groups panel ──────────────────────────────────────────────────────
		void DrawGroupsPanel(TetrahedralMeshAsset asset)
		{
			if (asset.Groups == null)
				asset.Groups = System.Array.Empty<ParticleGroup>();

			EditorGUILayout.LabelField("Groups", EditorStyles.boldLabel);

			for (int g = 0; g < asset.Groups.Length; g++)
			{
				var grp = asset.Groups[g];
				using var box = new EditorGUILayout.VerticalScope(GUI.skin.box);

				using (new EditorGUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField("Name", GUILayout.Width(42));
					if (_renamingGroup == g)
					{
						string newName = EditorGUILayout.TextField(grp.Name);
						if (newName != grp.Name)
						{
							Undo.RecordObject(asset, "Rename Particle Group");
							grp.Name = newName;
							MarkDirty(asset);
						}
						if (GUILayout.Button("✓", GUILayout.Width(22)))
							_renamingGroup = -1;
					}
					else
					{
						EditorGUILayout.LabelField(grp.Name);
						if (GUILayout.Button("✎", GUILayout.Width(22)))
							_renamingGroup = g;
					}
					EditorGUILayout.LabelField($"({grp.ParticleIndices?.Length ?? 0} pts)",
						EditorStyles.miniLabel, GUILayout.Width(55));
				}

				using (new EditorGUILayout.HorizontalScope())
				{
					// Select → load group's particles into _selected
					if (GUILayout.Button("Select"))
					{
						_selected.Clear();
						if (grp.ParticleIndices != null)
							foreach (int i in grp.ParticleIndices)
								_selected.Add(i);
					}
					// Set → overwrite group's particles with current selection
					if (GUILayout.Button("Set"))
					{
						Undo.RecordObject(asset, "Set Particle Group");
						grp.ParticleIndices = _selected.OrderBy(i => i).ToArray();
						MarkDirty(asset);
					}
				}
			}

			// Add / Remove buttons
			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("+", GUILayout.Width(28), GUILayout.Height(20)))
				{
					Undo.RecordObject(asset, "Add Particle Group");
					var list = asset.Groups.ToList();
					list.Add(new ParticleGroup { Name = $"Group{list.Count}" });
					asset.Groups = list.ToArray();
					MarkDirty(asset);
				}
				if (GUILayout.Button("−", GUILayout.Width(28), GUILayout.Height(20))
					&& asset.Groups.Length > 0)
				{
					Undo.RecordObject(asset, "Remove Particle Group");
					var list = asset.Groups.ToList();
					list.RemoveAt(list.Count - 1);
					asset.Groups = list.ToArray();
					MarkDirty(asset);
				}
			}
		}

		// ── Selection operation from modifier keys ──────────────────────────────
		// LMB         → New      (replace)
		// Shift+LMB   → Append   (add)
		// Ctrl+LMB    → Subtract (remove)
		// Ctrl is checked before Shift so Ctrl+Shift = Subtract.
		enum SelectOp
		{
			New, Append, Subtract
		}
		static SelectOp GetSelectOp(Event e) =>
			e.control ? SelectOp.Subtract :
			e.shift ? SelectOp.Append :
						SelectOp.New;

		// Apply op to a set of candidate indices.
		void ApplySelectOp(IEnumerable<int> candidates, SelectOp op)
		{
			switch (op)
			{
				case SelectOp.New:
					_selected.Clear();
					foreach (int i in candidates)
						_selected.Add(i);
					break;
				case SelectOp.Append:
					foreach (int i in candidates)
						_selected.Add(i);
					break;
				case SelectOp.Subtract:
					foreach (int i in candidates)
						_selected.Remove(i);
					break;
			}
		}

		// ── Scene View drawing & input ────────────────────────────────────────
		void OnSceneGUI(SceneView sv)
		{
			if (!_editing)
				return;
			var asset = (TetrahedralMeshAsset) target;
			if (asset?.Particles == null || asset.Particles.Length == 0)
				return;

			Event e = Event.current;

			// Alt is held → user wants to orbit / pan / zoom the scene view camera.
			// Do NOT consume mouse events or grab default control; let Unity handle it.
			bool altHeld = e.alt;
			if (!altHeld)
				HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

			bool repaint = false;

			// ── Draw geometry ─────────────────────────────────────────────
			if (_showMesh && asset.RenderMesh != null)
				DrawWireMesh(asset.RenderMesh, ColEdge);

			if (_showEdges && asset.Edges != null)
				DrawEdges(asset);

			if (_showTets && asset.Tetrahedrals != null)
				DrawTets(asset);

			if (_showParticles)
				DrawParticles(asset, sv);

			// ── Brush overlay ─────────────────────────────────────────────
			// PERF: EstimateBrushCenter iterates all particles. Only recalculate when
			// the mouse actually moves — reuse _lastBrushCenter on every Repaint.
			if (_selectMode == SelectMode.Paint)
			{
				if (!altHeld && (e.type == EventType.MouseMove || e.type == EventType.MouseDrag))
				{
					_lastBrushCenter = EstimateBrushCenter(asset,
						HandleUtility.GUIPointToWorldRay(e.mousePosition));
					_brushCenterValid = true;
					repaint = true;
				}
				if (_brushCenterValid && e.type != EventType.Layout)
				{
					Handles.color = ColBrush;
					Handles.DrawWireDisc(_lastBrushCenter, sv.camera.transform.forward, _brushSize);
				}
			}

			// ── Rect-select overlay ─────────────────────────────────────
			if (_selectMode == SelectMode.Rectangle && _rectDragging && e.type != EventType.Layout)
			{
				Handles.BeginGUI();
				var rect = GUIRectFromPoints(_rectStart, e.mousePosition);
				Handles.DrawSolidRectangleWithOutline(new[]
				{
					new Vector3(rect.xMin, rect.yMin),
					new Vector3(rect.xMax, rect.yMin),
					new Vector3(rect.xMax, rect.yMax),
					new Vector3(rect.xMin, rect.yMax)
				}, new Color(0.3f, 0.7f, 1f, 0.08f), new Color(0.3f, 0.7f, 1f, 0.7f));
				Handles.EndGUI();
				repaint = true;
			}

			// ── Input ─────────────────────────────────────────────────────
			// Skip ALL paint/select input when Alt is held so the scene-view camera
			// orbit (Alt+LMB), pan (Alt+MMB) and zoom (Alt+RMB / scroll) work normally.
			if (!altHeld)
			{
				if (e.type == EventType.MouseDown && e.button == 0)
				{
					if (_selectMode == SelectMode.Paint)
					{
						// MouseDown starts a new paint stroke.
						// SelectOp is sampled once at stroke-start and stored for the drag.
						_paintStrokeOp = GetSelectOp(e);
						PaintAtMouse(asset, e, sv, _paintStrokeOp);
						e.Use();
					}
					else // Rectangle
					{
						_rectDragging = true;
						_rectStart = e.mousePosition;
						_rectOp = GetSelectOp(e);   // capture op at drag-start
						e.Use();
					}
					repaint = true;
				}
				else if (e.type == EventType.MouseDrag && e.button == 0)
				{
					if (_selectMode == SelectMode.Paint)
					{
						// Drag continues same stroke → always Append (never replace again)
						PaintAtMouse(asset, e, sv, SelectOp.Append);
						e.Use();
					}
					repaint = true;
				}
				else if (e.type == EventType.MouseUp && e.button == 0)
				{
					if (_selectMode == SelectMode.Rectangle && _rectDragging)
					{
						RectSelect(asset, e, sv, _rectOp);
						_rectDragging = false;
						e.Use();
					}
					repaint = true;
				}
			}
			else if (_rectDragging)
			{
				// Alt pressed mid-rect-drag: cancel cleanly
				_rectDragging = false;
				repaint = true;
			}

			if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
			{
				SetEditing(false);
				e.Use();
			}

			if (repaint)
				sv.Repaint();
		}

		// ── Drawing helpers ───────────────────────────────────────────────────

		// ── Per-frame caches ─────────────────────────────────────────────────────
		bool[] _occlusionCache;
		// Occlusion is invalidated when the camera moves or culling settings change.
		// We store the last camera position used for the build; any significant
		// movement (>0.001 units) triggers a rebuild. This works in Edit mode where
		// Time.frameCount does NOT tick between OnSceneGUI mouse-move events.
		Vector3 _occlusionCamPos = new Vector3(float.MaxValue, 0, 0);
		int _occlusionN = -1;   // particle count at last build
		CullingMode _occlusionLastMode = (CullingMode) (-1);

		Vector3 _meshCentroid;
		int _meshCentroidCount = -1;

		Vector3 _lastBrushCenter;
		bool _brushCenterValid;

		// Batched wire-line cache for render mesh
		Vector3[] _wireLinesCache;
		Mesh _wireLinesMesh;

		// Depth-cull mesh data cached to avoid per-frame .vertices/.triangles alloc
		Vector3[] _depthVerts;
		int[] _depthTris;
		Mesh _depthMesh;

		// Visible set = indices not occluded, OR occluded but _showInnerParticles is on.
		// Uses the last-built occlusion cache (frame-guarded). When the cache is stale
		// (e.g. Inspector draw before first scene-view frame) all particles are visible.
		HashSet<int> GetVisibleSet(TetrahedralMeshAsset asset)
		{
			var set = new HashSet<int>();
			if (asset?.Particles == null)
				return set;
			int n = asset.Particles.Length;
			bool valid = _occlusionCache != null && _occlusionCache.Length == n;
			for (int i = 0; i < n; i++)
			{
				bool occ = valid && _occlusionCache[i];
				if (!occ || _showInnerParticles)
					set.Add(i);
			}
			return set;
		}

		Vector3 GetMeshCentroid(TetrahedralMeshAsset asset)
		{
			int n = asset.Particles.Length;
			if (_meshCentroidCount == n)
				return _meshCentroid;
			Vector3 sum = Vector3.zero;
			foreach (var p in asset.Particles)
				sum += p.Position;
			_meshCentroid = n > 0 ? sum / n : Vector3.zero;
			_meshCentroidCount = n;
			return _meshCentroid;
		}

		void RebuildOcclusionCache(TetrahedralMeshAsset asset, Camera cam)
		{
			int n = asset.Particles.Length;
			Vector3 camPos = cam.transform.position;

			// Rebuild whenever: camera moved, particle count changed, or culling mode changed.
			// Using camera-position change rather than Time.frameCount because the Editor does
			// NOT tick frameCount between OnSceneGUI events — a frameCount guard would cause
			// the occlusion to be computed once at "init view" and never updated as you orbit.
			bool camMoved = (camPos - _occlusionCamPos).sqrMagnitude > 1e-6f;
			bool countChanged = n != _occlusionN;
			bool modeChanged = _cullingMode != _occlusionLastMode;

			if (!camMoved && !countChanged && !modeChanged
				&& _occlusionCache != null && _occlusionCache.Length == n)
				return;

			_occlusionCamPos = camPos;
			_occlusionN = n;
			_occlusionLastMode = _cullingMode;

			if (_occlusionCache == null || _occlusionCache.Length != n)
				_occlusionCache = new bool[n];

			Vector3 centre = GetMeshCentroid(asset);
			Vector3 frontDir = (centre - camPos).normalized;

			if (_cullingMode == CullingMode.Depth && asset.RenderMesh != null
				&& asset.RenderMesh != _depthMesh)
			{
				_depthVerts = asset.RenderMesh.vertices;
				_depthTris = asset.RenderMesh.triangles;
				_depthMesh = asset.RenderMesh;
			}

			for (int i = 0; i < n; i++)
			{
				Vector3 wpos = asset.Particles[i].Position;
				switch (_cullingMode)
				{
					case CullingMode.None:
						_occlusionCache[i] = false;
						break;
					case CullingMode.Back:
						// Positive projection → particle is behind the mesh centre → occluded.
						_occlusionCache[i] = Vector3.Dot(wpos - centre, frontDir) > 0f;
						break;
					case CullingMode.Depth:
						_occlusionCache[i] = IsOccludedByMesh(wpos, camPos);
						break;
				}
			}
		}

		// True if the ray from camPos to wpos is blocked.
		// Uses pre-cached _depthVerts/_depthTris (no per-call alloc).
		bool IsOccludedByMesh(Vector3 wpos, Vector3 camPos)
		{
			Vector3 dir = wpos - camPos;
			float dist = dir.magnitude;
			if (dist < 1e-4f)
				return false;
			Vector3 dirN = dir / dist;

			if (Physics.Raycast(camPos, dirN, dist - 0.01f))
				return true;

			if (_depthVerts == null || _depthTris == null)
				return false;
			var ray = new Ray(camPos, dirN);
			for (int t = 0; t < _depthTris.Length; t += 3)
			{
				if (RayTriangleIntersect(ray,
						_depthVerts[_depthTris[t]],
						_depthVerts[_depthTris[t + 1]],
						_depthVerts[_depthTris[t + 2]],
						out float hitT) && hitT > 0 && hitT < dist - 0.01f)
					return true;
			}
			return false;
		}

		// Möller–Trumbore ray-triangle intersection.
		static bool RayTriangleIntersect(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
		{
			t = 0;
			Vector3 e1 = v1 - v0;
			Vector3 e2 = v2 - v0;
			Vector3 h = Vector3.Cross(ray.direction, e2);
			float det = Vector3.Dot(e1, h);
			if (Mathf.Abs(det) < 1e-7f)
				return false;
			float invDet = 1f / det;
			Vector3 s = ray.origin - v0;
			float u = Vector3.Dot(s, h) * invDet;
			if (u < 0 || u > 1)
				return false;
			Vector3 q = Vector3.Cross(s, e1);
			float v = Vector3.Dot(ray.direction, q) * invDet;
			if (v < 0 || u + v > 1)
				return false;
			t = Vector3.Dot(e2, q) * invDet;
			return t > 1e-7f;
		}

		// PERF NOTE: Handles.Button() raycasts a collider for every particle every Repaint
		// → ~320 physics queries per frame = >1s hover lag.
		// Fix: pure render (SphereHandleCap / DrawSolidDisc) every frame; click-test only
		// on actual MouseDown by projecting to screen space (no physics, O(N) screen math).
		void DrawParticles(TetrahedralMeshAsset asset, SceneView sv)
		{
			Camera cam = sv.camera;
			RebuildOcclusionCache(asset, cam);   // frame-guarded, free if already done

			Event e = Event.current;
			bool isClick = e.type == EventType.MouseDown && e.button == 0 && !e.alt
							&& _selectMode == SelectMode.Paint;

			for (int i = 0; i < asset.Particles.Length; i++)
			{
				Vector3 wpos = asset.Particles[i].Position;
				bool occluded = _occlusionCache[i];

				if (occluded && !_showInnerParticles)
					continue;

				bool sel = _selected.Contains(i);
				bool pinned = asset.Particles[i].InvMass < 1e-6f;

				float size = HandleUtility.GetHandleSize(wpos) * 0.04f * _particleSizeScale;
				size = Mathf.Clamp(size, 0.003f, 0.15f);

				Color col;
				if (sel)
					col = ColSelected;
				else if (pinned)
					col = occluded ? new Color(0f, 1f, 1f, 0.2f) : Color.cyan;
				else
					col = occluded ? ColInner : ColParticle;

				Handles.color = col;

				if (occluded)
				{
					Handles.DrawSolidDisc(wpos, cam.transform.forward, size * 0.6f);
				}
				else
				{
					// Render-only: zero hit-test overhead
					Handles.SphereHandleCap(0, wpos, Quaternion.identity, size * 2f, EventType.Repaint);

					// Click-test only on actual MouseDown — project to GUI pixels
					if (isClick)
					{
						Vector2 screenPt = HandleUtility.WorldToGUIPoint(wpos);
						float pixelR = Mathf.Clamp(
							HandleUtility.GetHandleSize(wpos) * _particleSizeScale * 28f, 5f, 36f);
						if (Vector2.Distance(screenPt, e.mousePosition) <= pixelR)
						{
							// Use same modifier-key scheme as brush/rect:
							// Ctrl=subtract, Shift=append, bare=new(replace)
							SelectOp op = GetSelectOp(e);
							ApplySelectOp(new[] { i }, op);
							e.Use();
							Repaint();
							isClick = false;   // one pick per click
						}
					}
				}

				if (pinned && !occluded)
					Handles.Label(wpos + Vector3.up * size * 2.5f, "📌", EditorStyles.miniLabel);
			}
		}

		void DrawEdges(TetrahedralMeshAsset asset)
		{
			Handles.color = ColEdge;
			foreach (var e in asset.Edges)
			{
				if (e.IndexA >= asset.Particles.Length || e.IndexB >= asset.Particles.Length)
					continue;
				Handles.DrawLine(asset.Particles[e.IndexA].Position, asset.Particles[e.IndexB].Position);
			}
		}

		void DrawTets(TetrahedralMeshAsset asset)
		{
			Handles.color = ColTet;
			foreach (var t in asset.Tetrahedrals)
			{
				if (t.I0 >= asset.Particles.Length)
					continue;
				Vector3 p0 = asset.Particles[t.I0].Position;
				Vector3 p1 = asset.Particles[t.I1].Position;
				Vector3 p2 = asset.Particles[t.I2].Position;
				Vector3 p3 = asset.Particles[t.I3].Position;
				// 4 faces of the tet as lines
				Handles.DrawLine(p0, p1);
				Handles.DrawLine(p0, p2);
				Handles.DrawLine(p0, p3);
				Handles.DrawLine(p1, p2);
				Handles.DrawLine(p1, p3);
				Handles.DrawLine(p2, p3);
			}
		}

		void DrawWireMesh(Mesh mesh, Color col)
		{
			// PERF: 5120 individual Handles.DrawLine calls → 1 batched Handles.DrawLines call.
			// Rebuild pair-array only when mesh reference changes.
			if (mesh != _wireLinesMesh || _wireLinesCache == null)
			{
				var t = mesh.triangles;
				var v = mesh.vertices;
				_wireLinesCache = new Vector3[t.Length * 2];
				int w = 0;
				for (int i = 0; i < t.Length; i += 3)
				{
					_wireLinesCache[w++] = v[t[i]];
					_wireLinesCache[w++] = v[t[i + 1]];
					_wireLinesCache[w++] = v[t[i + 1]];
					_wireLinesCache[w++] = v[t[i + 2]];
					_wireLinesCache[w++] = v[t[i + 2]];
					_wireLinesCache[w++] = v[t[i]];
				}
				_wireLinesMesh = mesh;
			}
			Handles.color = col;
			Handles.DrawLines(_wireLinesCache);
		}

		// ── Selection helpers ─────────────────────────────────────────────────
		void PaintAtMouse(TetrahedralMeshAsset asset, Event e, SceneView sv, SelectOp op)
		{
			Vector3 brushCenter = _brushCenterValid
				? _lastBrushCenter
				: EstimateBrushCenter(asset, HandleUtility.GUIPointToWorldRay(e.mousePosition));

			RebuildOcclusionCache(asset, sv.camera);

			float r2 = _brushSize * _brushSize;
			var hits = new List<int>();
			for (int i = 0; i < asset.Particles.Length; i++)
			{
				if (_cullingMode != CullingMode.None && _occlusionCache[i])
					continue;
				if ((asset.Particles[i].Position - brushCenter).sqrMagnitude <= r2)
					hits.Add(i);
			}
			ApplySelectOp(hits, op);
			Repaint();
		}

		void RectSelect(TetrahedralMeshAsset asset, Event e, SceneView sv, SelectOp op)
		{
			var rect = GUIRectFromPoints(_rectStart, e.mousePosition);
			var cam = sv.camera;
			RebuildOcclusionCache(asset, cam);

			var hits = new List<int>();
			for (int i = 0; i < asset.Particles.Length; i++)
			{
				if (_cullingMode != CullingMode.None && _occlusionCache[i])
					continue;
				Vector3 wpos = asset.Particles[i].Position;
				Vector3 screen = cam.WorldToScreenPoint(wpos);
				Vector2 gui = new Vector2(screen.x, cam.pixelHeight - screen.y);
				if (screen.z > 0 && rect.Contains(gui))
					hits.Add(i);
			}
			ApplySelectOp(hits, op);
			Repaint();
		}

		// Returns the world position of the particle nearest to the ray.
		// Using the particle's OWN position (not the foot of the perpendicular)
		// ensures the brush disc "sticks" to the particle cloud surface and
		// the brush volume in world-space correctly encloses neighbouring particles.
		// The old approach (foot of perpendicular) placed the disc at varying depths
		// inside the mesh, causing visible wobble and missing particles in paint mode.
		Vector3 EstimateBrushCenter(TetrahedralMeshAsset asset, Ray ray)
		{
			float bestDist2 = float.MaxValue;
			Vector3 bestPos = ray.origin + ray.direction * 2f;
			foreach (var p in asset.Particles)
			{
				float t = Vector3.Dot(p.Position - ray.origin, ray.direction);
				if (t < 0)
					continue;
				Vector3 foot = ray.origin + ray.direction * t;
				float dist2 = (foot - p.Position).sqrMagnitude;
				if (dist2 < bestDist2)
				{
					bestDist2 = dist2;
					bestPos = p.Position;
				}
			}
			return bestPos;
		}

		static Rect GUIRectFromPoints(Vector2 a, Vector2 b) =>
			new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
					 Mathf.Abs(a.x - b.x), Mathf.Abs(a.y - b.y));

		// ── Property helpers ──────────────────────────────────────────────────
		void RefreshPropertyValue(TetrahedralMeshAsset asset)
		{
			if (_selected.Count == 0)
			{
				_propValue = 0;
				_propMixed = false;
				return;
			}

			float first = float.NaN;
			_propMixed = false;
			foreach (int idx in _selected)
			{
				float v = asset.Particles[idx].InvMass;
				if (float.IsNaN(first))
				{
					first = v;
					continue;
				}
				if (!Mathf.Approximately(v, first))
				{
					_propMixed = true;
					break;
				}
			}
			_propValue = float.IsNaN(first) ? 0f : first;
		}

		// ── Lifecycle ─────────────────────────────────────────────────────────
		void SetEditing(bool on)
		{
			_editing = on;
			if (on)
			{
				TakeSnapshot((TetrahedralMeshAsset) target);
				_dirty = false;
				SceneView.duringSceneGui += OnSceneGUI;
				if (SceneView.lastActiveSceneView == null)
					EditorWindow.GetWindow<SceneView>();
				SceneView.lastActiveSceneView?.Focus();
			}
			else
			{
				SceneView.duringSceneGui -= OnSceneGUI;
				_selected.Clear();
				_rectDragging = false;
				_dirty = false;
				SceneView.RepaintAll();
			}
			Repaint();
		}

		// Deep-copy the asset data for Discard
		void TakeSnapshot(TetrahedralMeshAsset asset)
		{
			_snapshotParticles = asset.Particles == null ? null
				: (ParticleData[]) asset.Particles.Clone();
			if (asset.Groups == null)
			{
				_snapshotGroups = null;
				return;
			}
			_snapshotGroups = new ParticleGroup[asset.Groups.Length];
			for (int g = 0; g < asset.Groups.Length; g++)
			{
				var src = asset.Groups[g];
				_snapshotGroups[g] = new ParticleGroup
				{
					Name = src.Name,
					ParticleIndices = src.ParticleIndices == null ? null
						: (int[]) src.ParticleIndices.Clone()
				};
			}
		}

		// Restore asset to snapshot state
		void DoDiscard(TetrahedralMeshAsset asset)
		{
			Undo.RecordObject(asset, "Discard Particle Edits");
			asset.Particles = _snapshotParticles == null ? null
				: (ParticleData[]) _snapshotParticles.Clone();
			asset.Groups = _snapshotGroups == null ? null
				: System.Array.ConvertAll(_snapshotGroups, g => new ParticleGroup
				{
					Name = g.Name,
					ParticleIndices = g.ParticleIndices == null ? null
						: (int[]) g.ParticleIndices.Clone()
				});
			EditorUtility.SetDirty(asset);
			AssetDatabase.SaveAssets();
		}

		// Write current state to the original asset file
		void DoSave(TetrahedralMeshAsset asset)
		{
			EditorUtility.SetDirty(asset);
			AssetDatabase.SaveAssets();
			_dirty = false;
		}

		// Pop a save-file dialog and write a copy to the chosen location
		void DoSaveAs(TetrahedralMeshAsset asset)
		{
			string srcPath = AssetDatabase.GetAssetPath(asset);
			string dir = System.IO.Path.GetDirectoryName(srcPath);
			string baseName = System.IO.Path.GetFileNameWithoutExtension(srcPath);
			string destPath = EditorUtility.SaveFilePanelInProject(
				"Save Copy As", baseName + "_copy", "asset",
				"Choose where to save the copy.", dir);
			if (string.IsNullOrEmpty(destPath))
				return;

			// Write current (possibly edited) asset data into a brand-new asset
			var copy = CreateInstance<TetrahedralMeshAsset>();
			copy.Particles = asset.Particles == null ? null : (ParticleData[]) asset.Particles.Clone();
			copy.Edges = asset.Edges;
			copy.Tetrahedrals = asset.Tetrahedrals;
			copy.OrigIndices = asset.OrigIndices;
			copy.Skinning = asset.Skinning;
			copy.RenderMesh = asset.RenderMesh;
			copy.Groups = asset.Groups == null ? null
				: System.Array.ConvertAll(asset.Groups, g => new ParticleGroup
				{
					Name = g.Name,
					ParticleIndices = g.ParticleIndices == null ? null
						: (int[]) g.ParticleIndices.Clone()
				});

			AssetDatabase.CreateAsset(copy, destPath);
			AssetDatabase.SaveAssets();
			EditorGUIUtility.PingObject(copy);
			Debug.Log($"[XPBD] Asset copy saved to: {destPath}");
		}

		// Mark asset dirty and set _dirty flag
		void MarkDirty(TetrahedralMeshAsset asset)
		{
			EditorUtility.SetDirty(asset);
			if (!_dirty)
			{
				_dirty = true;
				Repaint();
			}
		}

		void OnDisable()
		{
			SceneView.duringSceneGui -= OnSceneGUI;
		}

		// ── UI helper ─────────────────────────────────────────────────────────
		static void DrawSectionHeader(string title)
		{
			EditorGUILayout.Space(2);
			var rect = EditorGUILayout.GetControlRect(false, 18);
			EditorGUI.DrawRect(rect, new Color(0.18f, 0.18f, 0.18f, 1f));
			EditorGUI.LabelField(rect, title, EditorStyles.boldLabel);
			EditorGUILayout.Space(2);
		}
	}
}
#endif
