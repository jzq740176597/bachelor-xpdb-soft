// XPBDSceneBootstrap.cs
// PLACE THIS FILE in any Assets/.../Editor/ folder.
//
// Unity menu  →  XPBD → Create Scene
//
// Builds the EXACT scene that runs when you press F5 in Visual Studio:
//   • Main Camera      pos(0,5,6)  rot(-30,0,0)  FOV 90
//   • Directional Light  rot(45,0,-45)  intensity 2.5
//   • Floor plane  scale 200×200  (collision mesh + visual)
//   • SimManager GameObject with SoftBodySimulationManager
//   • SoftBody GameObject  (sphere with the assigned TetMeshAsset)
//
// REQUIRES:
//   1. Run XPBD → Import Tet Mesh first to create sphere_5.asset + sphere_5_render.mesh
//   2. Assign those assets in the fields below before clicking Create Scene
//
// After clicking Create Scene just press Play — physics starts immediately.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace XPBD.Editor
{
	public class XPBDSceneBootstrap : EditorWindow
	{
		// ── Assets to assign ──────────────────────────────────────────────────
		TetrahedralMeshAsset _tetAsset;
		Mesh _renderMesh;
		ComputeShader _simCS;
		ComputeShader _colCS;
		ComputeShader _deformCS;
		Material _bodyMaterial;
		bool _useTetDeform = true;

		// Spawn offset matches C++ default  m_offset = (0, 5, 0)
		Vector3 _spawnOffset = new Vector3(0f, 5f, 0f);

		[MenuItem("XPBD/Create Scene")]
		public static void ShowWindow() =>
			GetWindow<XPBDSceneBootstrap>(false, "XPBD Scene Builder", true);

		// ── GUI ───────────────────────────────────────────────────────────────
		void OnGUI()
		{
			GUILayout.Space(6);
			GUILayout.Label("XPBD Scene Builder", EditorStyles.boldLabel);
			GUILayout.Label("Recreates the F5 Visual Studio startup scene in Unity.",
				EditorStyles.miniLabel);
			GUILayout.Space(10);

			GUILayout.Label("Step 1 — Import assets first  (XPBD → Import Tet Mesh)",
				EditorStyles.boldLabel);
			GUILayout.Space(6);

			_tetAsset = (TetrahedralMeshAsset) EditorGUILayout.ObjectField(
				"TetrahedralMeshAsset", _tetAsset, typeof(TetrahedralMeshAsset), false);
			_renderMesh = (Mesh) EditorGUILayout.ObjectField(
				"Render Mesh", _renderMesh, typeof(Mesh), false);
			_simCS = (ComputeShader) EditorGUILayout.ObjectField(
				"SoftBodySim.compute", _simCS, typeof(ComputeShader), false);
			_colCS = (ComputeShader) EditorGUILayout.ObjectField(
				"Collision.compute", _colCS, typeof(ComputeShader), false);
			_deformCS = (ComputeShader) EditorGUILayout.ObjectField(
				"Deform.compute", _deformCS, typeof(ComputeShader), false);
			_bodyMaterial = (Material) EditorGUILayout.ObjectField(
				"SoftBodyPBR Material", _bodyMaterial, typeof(Material), false);
			_useTetDeform = EditorGUILayout.Toggle(
				"Use Tet Deformation", _useTetDeform);
			_spawnOffset = EditorGUILayout.Vector3Field(
				"Spawn Offset", _spawnOffset);

			GUILayout.Space(12);

			bool ready = _tetAsset != null && _renderMesh != null &&
						 _simCS != null && _colCS != null &&
						 _deformCS != null;

			if (!ready)
				EditorGUILayout.HelpBox(
					"Assign all assets above, then click Create Scene.",
					MessageType.Info);

			GUI.enabled = ready;
			if (GUILayout.Button("Create Scene", GUILayout.Height(36)))
				CreateScene();
			GUI.enabled = true;
		}

		// ── Scene construction ────────────────────────────────────────────────
		void CreateScene()
		{
			// New empty scene
			var scene = EditorSceneManager.NewScene(
				NewSceneSetup.EmptyScene, NewSceneMode.Single);

			// ── 1. Camera  ────────────────────────────────────────────────────
			// m_camera.init(vec3(0,5,6), vec3(-30,0,0), 90, aspect)
			var camGO = new GameObject("Main Camera");
			camGO.tag = "MainCamera";
			camGO.transform.position = new Vector3(0f, 5f, 6f);
			camGO.transform.eulerAngles = new Vector3(-30f, 0f, 0f);
			var cam = camGO.AddComponent<Camera>();
			cam.fieldOfView = 90f;
			cam.nearClipPlane = 0.1f;
			cam.farClipPlane = 100f;
			cam.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 1f); // clearColor from recordCommandBuffer
			camGO.AddComponent<AudioListener>();

			// ── 2. Directional Light ──────────────────────────────────────────
			// static rot(45,0,-45) from ImGui default, intensity 2.5
			var lightGO = new GameObject("Directional Light");
			lightGO.transform.eulerAngles = new Vector3(45f, 0f, -45f);
			var light = lightGO.AddComponent<Light>();
			light.type = LightType.Directional;
			light.intensity = 2.5f;
			light.shadows = LightShadows.Soft;
			light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.VeryHigh;

			// ── 3. Floor ─────────────────────────────────────────────────────
			// scale 200 (matches floorVertices ±200), uvScale 50
			var floorGO = GameObject.CreatePrimitive(PrimitiveType.Plane);
			floorGO.name = "Floor";
			floorGO.transform.localScale = new Vector3(20f, 1f, 20f); // Plane is 10 units → ×20 = 200
																	  // Assign material if we have one, otherwise floor gets default
			if (_bodyMaterial != null)
			{
				// Create a copy for the floor with white tint, roughness=1, metallic=0
				var floorMat = new Material(_bodyMaterial);
				floorMat.name = "Floor_Mat";
				floorMat.SetColor("_Tint", Color.white);
				floorMat.SetFloat("_Roughness", 1f);
				floorMat.SetFloat("_Metallic", 0f);
				floorMat.SetVector("_UVTiling", new Vector4(50f, 50f, 0f, 0f));
				floorGO.GetComponent<MeshRenderer>().sharedMaterial = floorMat;
			}

			// ── 4. SimManager ─────────────────────────────────────────────────
			var mgrGO = new GameObject("SimManager");
			var mgr = mgrGO.AddComponent<SoftBodySimulationManager>();

			// Wire compute shaders
			mgr.SoftBodySimCS = _simCS;
			mgr.CollisionCS = _colCS;
			mgr.DeformCS = _deformCS;

			// Wire collision mesh (use floor's Plane mesh)
			mgr.CollisionMesh = floorGO.GetComponent<MeshFilter>().sharedMesh;

			// Simulation defaults matching Renderer.h
			mgr.FixedTimeStepFPS = 60;
			mgr.SubSteps = 20;
			mgr.EdgeCompliance = 0.01f;
			mgr.VolumeCompliance = 0.0f;
			mgr.ShadowOrthoSize = 15f;
			mgr.ShadowLightDist = 15f;
			mgr.DirectionalLight = light;
			mgr.SoftBodyMaterial = _bodyMaterial;

			// ── 5. Soft Body (Sphere) ─────────────────────────────────────────
			// createSoftBody("sphere", m_offset) default on startup
			var bodyGO = new GameObject("SoftBody_Sphere");
			bodyGO.transform.position = Vector3.zero; // offset applied inside SoftBodyComponent

			var mf = bodyGO.AddComponent<MeshFilter>();
			mf.sharedMesh = _renderMesh;

			var mr = bodyGO.AddComponent<MeshRenderer>();
			if (_bodyMaterial != null)
				mr.sharedMaterial = _bodyMaterial;

			var body = bodyGO.AddComponent<SoftBodyComponent>();
			body.TetMeshAsset = _tetAsset;
			body.UseTetDeformation = _useTetDeform;
			body.Manager = mgr;
			body.Tint = new Color(1f, 0.15f, 0.05f, 1f); // COLORS[0]
			body.Roughness = 0.5f;
			body.Metallic = 0.0f;

			// Spawn offset replaces m_offset = (0,5,0) from createSoftBody call
			// SoftBodyComponent.Start() adds transform.position to each particle
			bodyGO.transform.position = _spawnOffset;

			// ── 6. Debug renderer (optional) ──────────────────────────────────
			var dbg = mgrGO.AddComponent<SoftBodyDebugRenderer>();
			if (dbg != null)
				dbg.Manager = mgr;

			// ── Save ──────────────────────────────────────────────────────────
			EditorSceneManager.MarkSceneDirty(scene);
			EditorSceneManager.SaveScene(scene, "Assets/XPBD_Scene.unity");
			AssetDatabase.Refresh();

			Debug.Log("[XPBD] Scene created → Assets/XPBD_Scene.unity  |  Press Play to simulate.");
			Close();
		}
	}
}
