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
	public sealed class SoftBodyComponent : MonoBase
	{
		#region Inspector
		[SerializeField]
		TetrahedralMeshAsset tetMeshAsset;
		public TetrahedralMeshAsset TetMeshAsset => tetMeshAsset;
		//for soft-collision [3/12/2026 jzq]
		[Header("Soft-Soft-Collision")]
		public int   SoftCollisionLayer = 0;
		public int   SoftCollisionMask  = 0;   // 0 = no layer-based pairing
		public float SoftSoftParticleRadius = 0f; // 0 = auto-compute from bounding radius

		/// <summary>
		/// Bounding sphere radius of this body's particle cloud.
		/// Set automatically by SoftBodySimulationManager.AddBody.
		/// Used to auto-derive the soft-soft contact radius when
		/// SoftSoftParticleRadius == 0.
		/// </summary>
		public float BoundingRadius  { get; internal set; }
		public float ParticleRadius   { get; internal set; }
		#endregion

		#region Imp
		//[Header("Manager Reference")]
		SoftBodySimulationManager manager;
		SoftBodyGPUState _state;
		MeshFilter _meshFilter;
		Renderer _rd;
		//bool inited => _state != null;
		bool valid => tetMeshAsset;
		bool visible
		{
			get => _rd && _rd.enabled;
			set
			{
				if (_rd)
					_rd.enabled = value;
			}
		}
		// [3/7/2026 jzq]
		internal void InternalOnDeformed()
		{
			//gameObject.SetActive(true);
			visible = true;
		}
		#endregion

		#region Pub
		public SoftBodyGPUState State => _state;
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
		protected override void OnInit()
		{
			base.OnInit();
			if (!valid)
			{
				Debug.LogError("SoftBodyComponent invalid!", this);
				return;
			}
			_rd = GetComponent<Renderer>();
			if (!_rd)
				Debug.LogError("SoftBodyComponent rd == null!", this);
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
				visible = false;
			}
			_meshFilter = GetComponent<MeshFilter>();

			// Instantiate the render mesh so each body has its own copy
			var renderMesh = Instantiate(tetMeshAsset.RenderMesh);
			renderMesh.MarkDynamic(); // hint to Unity: vertices change every frame
			_meshFilter.sharedMesh = renderMesh;
			//
			_state = new SoftBodyGPUState(tetMeshAsset, mat, renderMesh);
			manager.AddBody(/*_state*/this);
		}

		protected override void DoDeInit()
		{
			base.DoDeInit();
			Destroy(_meshFilter.sharedMesh); // clean up the instantiated render mesh - jzq [3/6/2026]
			if (_state != null && manager != null)
			{
				manager.RemoveBody(/*_state*/this); // manager might still be mid-dispatch on this frame
				{// [3/7/2026 jzq]
					_state.Dispose(); //Safe: post GPU buffers released
					_state = null;
				}
			}
		}
		#endregion
	}
}
