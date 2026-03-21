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
using UnityEditor.Callbacks;
using UnityEngine;

namespace XPBD.Editor
{
	[CustomEditor(typeof(TetrahedralMeshAsset))]
	public sealed class TetrahedralMeshAssetEditor : UnityEditor.Editor
	{
		// ── Edit-mode state ───────────────────────────────────────────────────
		bool _editing;
		// Scene isolation: GameObjects hidden on edit-enter, restored on exit.
		GameObject[] _hiddenObjects;
		// Fixed EditorPrefs key: written when isolation begins, cleared when restored.
		// A single global key is sufficient — only one asset can be isolated at a time.
		const string IsolationKey = "XPBD_WasIsolating";

		// Selection
		enum SelectMode
		{
			Paint, Rectangle
		}
		SelectMode _selectMode = SelectMode.Paint;
		float _brushSize = 40f;     // screen-space circle radius in pixels
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
		bool _showInnerParticles = false;   // draw interior (geometrically inside mesh) particles in dim colour

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
				? "Drag: new.  Shift+drag: append (teal).  Ctrl+drag: subtract (red)."
				: $"{selCount} / {visCount} visible selected  ({totCount} total)\n"
				  + "Shift=append  Ctrl=subtract  (circle & rect modes)";
			EditorGUILayout.HelpBox(selInfo, MessageType.None);

			// Mode toolbar
			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.Label("Mode", GUILayout.Width(42));
				_selectMode = (SelectMode) GUILayout.Toolbar((int) _selectMode,
					new[] { "Paint", "Rectangle" }, GUILayout.Height(22));
			}

			_brushSize = EditorGUILayout.Slider("Circle radius (px)", _brushSize, 5f, 200f);

			using (new EditorGUILayout.HorizontalScope())
			{
				GUILayout.Label("Culling", GUILayout.Width(52));
				_cullingMode = (CullingMode) GUILayout.Toolbar((int) _cullingMode,
					new[] { "None", "Back", "Depth" }, GUILayout.Height(20));
			}
			// Culling controls which particles are SELECTABLE (not how the circle looks).
			// The 2D circle still draws at fixed pixel size; culling prevents back/hidden
			// particles from being selected even when the circle overlaps them on screen.
			string cullingTip = _cullingMode == CullingMode.None
				? "None: all particles selectable, front and back."
				: _cullingMode == CullingMode.Back
					? "Back: back-hemisphere particles not selectable (fast, good for convex meshes)."
					: "Depth: raycasts for accurate occlusion. Slower on high-res meshes.";
			EditorGUILayout.HelpBox(cullingTip, MessageType.None);

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
						"Interior particles (geometrically inside the mesh volume) shown in dim colour. " +
						"This is camera-independent — based on mesh geometry only.",
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
		// Applies identically to Paint circle and Rectangle:
		//   bare LMB/drag  → New      (replace selection)
		//   Shift+drag     → Append   (add to selection)
		//   Ctrl+drag      → Subtract (remove from selection, circle shows red)
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
			bool ctrlHeld = e.control;
			// Alt → camera orbit/pan/zoom: don't grab default control.
			// Ctrl → circle-subtract: we still grab default control so Unity doesn't
			// intercept our click, but we skip it only for Alt (camera navigation).
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

			// ── Brush overlay — 2D screen-space circle (Blender-style) ─────────
			// Fixed pixel radius: never wobbles. Red tint when Ctrl held (deselect mode).
			if (_selectMode == SelectMode.Paint && e.type != EventType.Layout && !altHeld)
			{
				// Circle colour signals current mode:
				//   red   = Ctrl held (subtract mode)
				//   teal  = Shift held (append mode)
				//   white = bare (new-select / append during drag)
				Color circleCol = ctrlHeld ? new Color(1f, 0.25f, 0.25f, 0.9f)
								: e.shift ? new Color(0.3f, 1f, 0.5f, 0.8f)
								: ColBrush;
				DrawScreenCircle(e.mousePosition, _brushSize, circleCol);
				repaint = true;
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
			// Modifier summary:
			//   bare   → New select (replace)   | works for both circle and rect
			//   Shift  → Append                 | circle turns white; rect adds
			//   Ctrl   → Subtract               | circle turns red;   rect removes
			if (!altHeld)
			{
				if (e.type == EventType.MouseDown && e.button == 0)
				{
					if (_selectMode == SelectMode.Paint)
					{
						// Capture op at stroke-start; locked for this stroke's first paint.
						_paintStrokeOp = GetSelectOp(e);
						PaintAtMouse(asset, e, sv, _paintStrokeOp);
						e.Use();
					}
					else // Rectangle
					{
						_rectDragging = true;
						_rectStart = e.mousePosition;
						_rectOp = GetSelectOp(e);   // captured at drag-start
						e.Use();
					}
					repaint = true;
				}
				else if (e.type == EventType.MouseDrag && e.button == 0)
				{
					if (_selectMode == SelectMode.Paint)
					{
						// During a drag, re-read modifier live so user can hold/release
						// Ctrl or Shift mid-stroke and get the expected result.
						SelectOp dragOp = ctrlHeld ? SelectOp.Subtract :
										  e.shift ? SelectOp.Append :
													  SelectOp.Append;   // bare drag = append-extend
						PaintAtMouse(asset, e, sv, dragOp);
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

		// ── Caches ──────────────────────────────────────────────────────────────

		// Static geometric interior test — computed once on edit-enter from RenderMesh.
		// _isInterior[i] = true  → particle is geometrically INSIDE the mesh volume.
		// _isInterior[i] = false → particle is on/outside the surface.
		// This is camera-independent; it never changes as you orbit.
		bool[] _isInterior;
		int _isInteriorN = -1;   // particle count at last build
		Mesh _isInteriorMesh;       // mesh used at last build

		// Per-frame camera-dependent occlusion cache (used only for CULLING selectability,
		// NOT for the show/hide inner-particle toggle — that now uses _isInterior).
		bool[] _occlusionCache;
		Vector3 _occlusionCamPos = new Vector3(float.MaxValue, 0, 0);
		int _occlusionN = -1;
		CullingMode _occlusionLastMode = (CullingMode) (-1);

		Vector3 _meshCentroid;
		int _meshCentroidCount = -1;

		// Batched wire-line cache for render mesh
		Vector3[] _wireLinesCache;
		Mesh _wireLinesMesh;

		// Depth-cull mesh data cached to avoid per-frame .vertices/.triangles alloc
		Vector3[] _depthVerts;
		int[] _depthTris;
		Mesh _depthMesh;

		// Visible set = surface particles + interior particles if _showInnerParticles is on.
		// "Interior" is the static geometric test (_isInterior), not camera-dependent culling.
		HashSet<int> GetVisibleSet(TetrahedralMeshAsset asset)
		{
			var set = new HashSet<int>();
			if (asset?.Particles == null)
				return set;
			int n = asset.Particles.Length;
			bool hasCache = _isInterior != null && _isInterior.Length == n;
			for (int i = 0; i < n; i++)
			{
				bool interior = hasCache && _isInterior[i];
				if (!interior || _showInnerParticles)
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

		// True if the ray from camPos to wpos is blocked by the asset's own RenderMesh.
		// IMPORTANT: we deliberately do NOT use Physics.Raycast here because that hits
		// every collider in the scene (floors, walls, other objects). During editing we
		// only care about the asset itself as an occluder — nothing else should affect
		// which particles are considered "inner".
		bool IsOccludedByMesh(Vector3 wpos, Vector3 camPos)
		{
			if (_depthVerts == null || _depthTris == null)
				return false;
			Vector3 dir = wpos - camPos;
			float dist = dir.magnitude;
			if (dist < 1e-4f)
				return false;
			var ray = new Ray(camPos, dir / dist);
			for (int t = 0; t < _depthTris.Length; t += 3)
			{
				if (RayTriangleIntersect(ray,
						_depthVerts[_depthTris[t]],
						_depthVerts[_depthTris[t + 1]],
						_depthVerts[_depthTris[t + 2]],
						out float hitT) && hitT > 0f && hitT < dist - 0.01f)
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
			RebuildOcclusionCache(asset, cam);   // for culling/selectability only

			// Ensure interior cache exists (should be built on enter, but guard anyway)
			if (_isInterior == null || _isInterior.Length != asset.Particles.Length)
				BuildInteriorCache(asset);

			Event e = Event.current;
			bool isClick = e.type == EventType.MouseDown && e.button == 0 && !e.alt
							&& _selectMode == SelectMode.Paint;

			for (int i = 0; i < asset.Particles.Length; i++)
			{
				Vector3 wpos = asset.Particles[i].Position;

				// INTERIOR = geometrically inside the mesh volume (static, camera-independent).
				// OCCLUDED  = camera-angle culling (Back/Depth), only affects selectability.
				bool interior = _isInterior != null && i < _isInterior.Length && _isInterior[i];
				bool occluded = _occlusionCache != null && i < _occlusionCache.Length && _occlusionCache[i];

				// Hide interior particles when toggle is off — purely geometric, never flickers.
				if (interior && !_showInnerParticles)
					continue;

				bool sel = _selected.Contains(i);
				bool pinned = asset.Particles[i].InvMass < 1e-6f;

				float size = HandleUtility.GetHandleSize(wpos) * 0.04f * _particleSizeScale;
				size = Mathf.Clamp(size, 0.003f, 0.15f);

				// Colour priority: selected > pinned > interior > back-culled > normal
				Color col;
				if (sel)
					col = ColSelected;
				else if (pinned)
					col = (interior || occluded) ? new Color(0f, 1f, 1f, 0.2f) : Color.cyan;
				else if (interior)
					col = ColInner;
				else if (occluded && _cullingMode != CullingMode.None)
					col = ColInner;   // back/depth-occluded surface particles dim the same way
				else
					col = ColParticle;

				Handles.color = col;

				bool dimmed = interior || (occluded && _cullingMode != CullingMode.None);
				if (dimmed)
				{
					// Interior OR back-culled: flat disc, smaller, visually de-emphasised
					Handles.DrawSolidDisc(wpos, cam.transform.forward, size * 0.6f);
				}
				else
				{
					// Fully visible surface particle: full sphere
					Handles.SphereHandleCap(0, wpos, Quaternion.identity, size * 2f, EventType.Repaint);

					// Precise single-particle click — skip occluded/back-culled particles.
					if (isClick && !occluded)
					{
						Vector2 screenPt = HandleUtility.WorldToGUIPoint(wpos);
						float pixelR = Mathf.Clamp(
							HandleUtility.GetHandleSize(wpos) * _particleSizeScale * 28f, 5f, 36f);
						if (Vector2.Distance(screenPt, e.mousePosition) <= pixelR)
						{
							ApplySelectOp(new[] { i }, GetSelectOp(e));
							e.Use();
							Repaint();
							isClick = false;
						}
					}
				}

				if (pinned && !dimmed)
					Handles.Label(wpos + Vector3.up * size * 2.5f, "📌", EditorStyles.miniLabel);
			}
		}

		// Fixed-pixel 2D screen-circle. Drawn in GUI layer so it never wobbles.
		static void DrawScreenCircle(Vector2 center, float radiusPx, Color col)
		{
			Handles.BeginGUI();
			const int segs = 48;
			Color prev = Handles.color;
			Handles.color = col;
			for (int s = 0; s < segs; s++)
			{
				float a0 = s / (float) segs * Mathf.PI * 2f;
				float a1 = (s + 1) / (float) segs * Mathf.PI * 2f;
				Handles.DrawLine(
					new Vector3(center.x + Mathf.Cos(a0) * radiusPx,
								center.y + Mathf.Sin(a0) * radiusPx),
					new Vector3(center.x + Mathf.Cos(a1) * radiusPx,
								center.y + Mathf.Sin(a1) * radiusPx));
			}
			Handles.color = prev;
			Handles.EndGUI();
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
		// 2D screen-space circle selection (Blender-style).
		// Projects particles to GUI pixels; selects all within pixel radius.
		// Culling still controls which particles are eligible to be selected.
		void PaintAtMouse(TetrahedralMeshAsset asset, Event e, SceneView sv, SelectOp op)
		{
			RebuildOcclusionCache(asset, sv.camera);
			Camera cam = sv.camera;
			Vector2 mousePos = e.mousePosition;
			float r2px = _brushSize * _brushSize;
			bool hasInner = _isInterior != null && _isInterior.Length == asset.Particles.Length;

			var hits = new List<int>();
			for (int i = 0; i < asset.Particles.Length; i++)
			{
				// Never select interior particles — they are inside the mesh volume.
				if (hasInner && _isInterior[i])
					continue;
				// Camera-based culling still applies for surface particles.
				if (_cullingMode != CullingMode.None && _occlusionCache != null && _occlusionCache[i])
					continue;
				Vector3 vp = cam.WorldToScreenPoint(asset.Particles[i].Position);
				if (vp.z <= 0f)
					continue;
				Vector2 guiPt = new Vector2(vp.x, cam.pixelHeight - vp.y);
				if ((guiPt - mousePos).sqrMagnitude <= r2px)
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
			bool hasInner = _isInterior != null && _isInterior.Length == asset.Particles.Length;

			var hits = new List<int>();
			for (int i = 0; i < asset.Particles.Length; i++)
			{
				if (hasInner && _isInterior[i])
					continue;   // skip interior particles
				if (_cullingMode != CullingMode.None && _occlusionCache != null && _occlusionCache[i])
					continue;
				Vector3 screen = cam.WorldToScreenPoint(asset.Particles[i].Position);
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
				BuildInteriorCache((TetrahedralMeshAsset) target);
				IsolateAssetInScene((TetrahedralMeshAsset) target);
				Selection.selectionChanged += OnSelectionChanged;
				SceneView.duringSceneGui += OnSceneGUI;
				if (SceneView.lastActiveSceneView == null)
					EditorWindow.GetWindow<SceneView>();
				SceneView.lastActiveSceneView?.Focus();
			}
			else
			{
				RestoreSceneVisibility();
				Selection.selectionChanged -= OnSelectionChanged;
				SceneView.duringSceneGui -= OnSceneGUI;
				_selected.Clear();
				_rectDragging = false;
				_dirty = false;
				SceneView.RepaintAll();
			}
			Repaint();
		}

		// Hide all renderers in the scene except those belonging to the asset being edited.
		// Uses SceneVisibilityManager so no GameObject dirty state is modified.
		void IsolateAssetInScene(TetrahedralMeshAsset asset)
		{
			// Find which GameObjects share the asset's RenderMesh — those are "ours".
			var ownedGOs = new HashSet<GameObject>();
			if (asset.RenderMesh != null)
			{
				foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
					if (mf.sharedMesh == asset.RenderMesh)
						ownedGOs.Add(mf.gameObject);
				foreach (var mr in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
					if (mr.sharedMesh == asset.RenderMesh)
						ownedGOs.Add(mr.gameObject);
			}

			// Collect every root GO in every loaded scene, hide those not in ownedGOs.
			var toHide = new List<GameObject>();
			for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
			{
				var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
				if (!scene.isLoaded)
					continue;
				foreach (var root in scene.GetRootGameObjects())
					CollectHideTargets(root, ownedGOs, toHide);
			}

			var svm = SceneVisibilityManager.instance;
			foreach (var go in toHide)
				svm.Hide(go, includeDescendants: true);
			_hiddenObjects = toHide.ToArray();
			// Write simple sentinel so [InitializeOnLoad] restorer finds it on next launch.
			// A plain fixed key avoids any AssetDatabase/GUID lookup at startup time.
			EditorPrefs.SetInt(IsolationKey, 1);

			// Isolation is silent — no dialog. Scene restores automatically on Done/deselect/restart.
		}

		// Recursively collect GOs to hide: hide any GO that is not an ancestor/descendant of owned ones.
		static void CollectHideTargets(GameObject go, HashSet<GameObject> owned, List<GameObject> result)
		{
			if (owned.Contains(go))
				return;   // this GO is the asset's mesh object — keep visible
						  // Check if any owned GO is a descendant of this one (keep ancestors visible too)
			bool isAncestor = false;
			foreach (var o in owned)
				if (o.transform.IsChildOf(go.transform))
				{
					isAncestor = true;
					break;
				}
			if (isAncestor)
				return;
			result.Add(go);
		}

		// ── Static geometric interior test ─────────────────────────────────────
		// A particle is "interior" if its minimum distance to any triangle of the
		// RenderMesh surface is greater than a threshold.
		//
		// Why NOT ray-crossing: Unity sphere RenderMesh triangles are in local space
		// and have outward normals. Surface particles sit exactly on the mesh shell.
		// Floating-point places some of them a hair *inside* the shell, giving an odd
		// crossing count (→ wrongly classified as interior). This is especially bad
		// for back-hemisphere particles whose ray exits from behind.
		//
		// Why distance-to-surface works:
		//   • Surface particles (placed by the tet generator ON the mesh) are within
		//     a very small epsilon of a triangle. dist ≈ 0 → NOT interior.
		//   • Interior tet particles are well inside the mesh shell. dist >> epsilon.
		//   • Threshold is set relative to the mean edge length of the mesh so it
		//     adapts to any mesh scale automatically.
		//   • Camera-independent: purely geometric, never flickers on orbit.
		void BuildInteriorCache(TetrahedralMeshAsset asset)
		{
			int n = asset?.Particles?.Length ?? 0;
			if (n == _isInteriorN && asset.RenderMesh == _isInteriorMesh
				&& _isInterior != null && _isInterior.Length == n)
				return;

			_isInteriorN = n;
			_isInteriorMesh = asset.RenderMesh;
			_isInterior = new bool[n];

			if (asset.RenderMesh == null || n == 0)
				return;

			var verts = asset.RenderMesh.vertices;
			var tris = asset.RenderMesh.triangles;

			// Adaptive threshold: half the mean triangle edge length.
			// For the sphere asset (r=1, ~2800 verts) this is roughly 0.05–0.08.
			// Surface particles are within 1e-4 of the surface; interior ones are 0.1+.
			double edgeSum = 0;
			int edgeCount = 0;
			for (int t = 0; t < tris.Length; t += 3)
			{
				edgeSum += (verts[tris[t + 1]] - verts[tris[t]]).magnitude;
				edgeSum += (verts[tris[t + 2]] - verts[tris[t + 1]]).magnitude;
				edgeSum += (verts[tris[t]] - verts[tris[t + 2]]).magnitude;
				edgeCount += 3;
			}
			float threshold = edgeCount > 0 ? (float) (edgeSum / edgeCount) * 0.4f : 0.05f;

			for (int i = 0; i < n; i++)
			{
				Vector3 p = asset.Particles[i].Position;
				float min2 = float.MaxValue;

				for (int t = 0; t < tris.Length; t += 3)
				{
					float d2 = PointTriangleDist2(p,
						verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]]);
					if (d2 < min2)
						min2 = d2;
					// Early-out: already closer than threshold — definitely surface
					if (min2 < 1e-6f)
						break;
				}

				_isInterior[i] = min2 > threshold * threshold;
			}
		}

		// Squared distance from point p to the closest point on triangle (a,b,c).
		static float PointTriangleDist2(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
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

			float denom = 1f / (va + vb + vc);
			float vv = vb * denom, ww = vc * denom;
			return (p - (a + vv * ab + ww * ac)).sqrMagnitude;
		}

		void RestoreSceneVisibility()
		{
			// DO NOT delete IsolationKey here.
			// The key must survive Unity quit so the [InitializeOnLoad] restorer
			// can call ShowAll() on next launch if Unity was closed mid-isolation.
			// RestoreIfNeeded() is the sole place that deletes the key.
			// This is idempotent: ShowAll() on an already-visible scene is harmless.
			if (_hiddenObjects == null || _hiddenObjects.Length == 0)
				return;
			var svm = SceneVisibilityManager.instance;
			foreach (var go in _hiddenObjects)
				if (go != null)
					svm.Show(go, includeDescendants: true);
			_hiddenObjects = null;
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
			// OnDisable fires whenever this editor instance is destroyed:
			// - user deselects the asset in the Project window
			// - Inspector is closed / changed to another asset
			// - script recompilation, play-mode entry, etc.
			// Always restore the scene so no objects are left hidden.
			RestoreSceneVisibility();
			Selection.selectionChanged -= OnSelectionChanged;
			SceneView.duringSceneGui -= OnSceneGUI;
		}

		// Called whenever the user changes the Editor selection.
		// If we're still in edit mode but the asset has been deselected, exit cleanly.
		void OnSelectionChanged()
		{
			if (!_editing)
				return;
			// Check whether our target is still the active selection
			if (Selection.activeObject == target)
				return;

			// Asset was deselected while still in edit mode.
			// Exit silently (no dialog — OnDisable will restore visibility).
			// We do NOT call DoSave here: deselection mid-edit should not auto-save.
			// The scene is restored; any unsaved particle edits are left in memory
			// and will be present if the user re-selects the asset.
			_editing = false;
			RestoreSceneVisibility();
			Selection.selectionChanged -= OnSelectionChanged;
			SceneView.duringSceneGui -= OnSceneGUI;
			_rectDragging = false;
			SceneView.RepaintAll();
			// Note: _dirty stays true so when the user re-selects, the * indicator
			// still shows and they know there are unsaved changes.
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
		// [3/21/2026 jzq]
		[OnOpenAsset]
		public static bool OnOpenAsset(int instanceID, int line)
		{
			// 1. Get the object from the ID
			Object obj = EditorUtility.InstanceIDToObject(instanceID);

			if (obj is TetrahedralMeshAsset myAsset)
			{
				// 1. Ensure it's selected (this forces the Inspector to load the editor)
				Selection.activeObject = myAsset;

				// 2. Find the existing Inspector window
				var allEditors = Resources.FindObjectsOfTypeAll<UnityEditor.Editor>();

				foreach (var editor in allEditors)
				{
					// 3. Check if this editor is the one drawing our asset
					if (editor is TetrahedralMeshAssetEditor myEditor && editor.target == myAsset)
					{
						if (!myEditor._editing)
							myEditor.SetEditing(true);
						return true;
					}
				}

				// Fallback: If for some reason the Inspector isn't open yet
				//Debug.LogWarning("Inspector not found, using logic-only trigger.");
				return true;
			}
			return false;
		}
	}

	// [InitializeOnLoad]: static constructor runs on every Unity startup and script
	// recompile, before any Inspector or user interaction. This is the only hook
	// that fires unconditionally after a restart without requiring asset selection.
	//
	// If Unity quit while objects were isolated (user closed without Done),
	// SceneVisibilityManager keeps them hidden on disk. The fixed EditorPrefs key
	// "XPBD_WasIsolating" written by IsolateAssetInScene tells us to restore.
	[InitializeOnLoad]
	static class XpbdIsolationStartupRestorer
	{
		const string Key = "XPBD_WasIsolating";

		static XpbdIsolationStartupRestorer()
		{
			// EditorApplication.delayCall only fires when the editor loop ticks
			// (requires user interaction on cold start — too late).
			// EditorApplication.update fires every editor tick automatically,
			// including immediately after startup with no user input required.
			// Unsubscribe inside the callback so it only runs once.
			EditorApplication.update += RestoreIfNeeded;
		}

		static void RestoreIfNeeded()
		{
			// Unsubscribe first — run exactly once regardless of outcome.
			EditorApplication.update -= RestoreIfNeeded;

			if (!EditorPrefs.HasKey(Key))
				return;
			EditorPrefs.DeleteKey(Key);
			SceneVisibilityManager.instance.ShowAll();
			Debug.Log("[XPBD] Scene visibility restored on startup (previous session not closed cleanly).");
		}
	}
}
#endif
