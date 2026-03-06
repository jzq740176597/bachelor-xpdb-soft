// SoftBodyComponent.cs
// MonoBehaviour attached to each soft body GameObject.
// Reads a TetrahedralMeshAsset (ScriptableObject), allocates GPU buffers,
// and registers with SoftBodySimulationManager.
//
// Corresponds to Renderer::createSoftBody() in Renderer.cpp.
//
// Inspector workflow:
//   1. Assign TetMeshAsset (exported from Blender Tetrahedralizer plugin)
//   2. Assign RenderMeshFilter (the high-res triangle mesh)
//   3. Set UseTetDeformation = true if tet mesh is lower resolution
//   4. Assign the scene's SoftBodySimulationManager
//   5. Press Play

using UnityEngine;

namespace XPBD
{
	[RequireComponent(typeof(MeshFilter))]
	public sealed class SoftBodyComponent : MonoBehaviour
	{
		#region Inspector
		[SerializeField]
		TetrahedralMeshAsset tetMeshAsset;
		#endregion

		#region Imp
		//[Header("Manager Reference")]
		SoftBodySimulationManager manager;
		SoftBodyGPUState _state;
		MeshFilter _meshFilter;
		//bool inited => _state != null;
		bool valid => tetMeshAsset;
		#endregion

		#region Pub
		public void Init(TetrahedralMeshAsset tetMeshAsset)
		{
			if (tetMeshAsset == null)
			{
				Debug.LogError("[XPBD] No TetrahedralMeshAsset assigned.", this);
				return;
			}
			this.tetMeshAsset = tetMeshAsset;
		}
		#endregion

		#region Unity
		void Start()
		{
			if (!valid)
			{
				Debug.LogError("SoftBodyComponent invalid!", this);
				return;
			}
			if (manager == null)
			{
				manager = FindObjectOfType<SoftBodySimulationManager>();
				if (manager == null)
				{
					Debug.LogError("[XPBD] SoftBodySimulationManager not found in scene.", this);
					return;
				}
			}
			//cache & reset transform for render [3/6/2026 jzq]
			var mat = transform.localToWorldMatrix;
			{
				transform.position = Vector3.zero;
				transform.rotation = Quaternion.identity;
				if (transform.lossyScale != Vector3.one)
				{
					Debug.LogError($"'{name}' : transform.lossyScale SHOULD be (1,1,1)");
					transform.SetLossyScale(Vector3.one);
				}
			}
			_meshFilter = GetComponent<MeshFilter>();

			// Instantiate the render mesh so each body has its own copy
			var renderMesh = Instantiate(tetMeshAsset.RenderMesh);
			renderMesh.MarkDynamic(); // hint to Unity: vertices change every frame
			_meshFilter.sharedMesh = renderMesh;
			//
			_state = new SoftBodyGPUState(tetMeshAsset, mat, renderMesh);
			manager.AddBody(_state);
		}

		void OnDestroy()
		{
			Destroy(_meshFilter.sharedMesh); // clean up the instantiated render mesh - jzq [3/6/2026]
			if (_state != null && manager != null)
				manager.RemoveBody(_state);
		}
		#endregion
	}
}
