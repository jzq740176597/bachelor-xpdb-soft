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
				transform.SetLossyScale(Vector3.one);
			}
			_meshFilter = GetComponent<MeshFilter>();

			// Convert ScriptableObject data to GPU structs
			var data = tetMeshAsset;

			// Instantiate the render mesh so each body has its own copy
			Mesh renderMesh = Instantiate(data.RenderMesh);
			renderMesh.MarkDynamic(); // hint to Unity: vertices change every frame
			_meshFilter.sharedMesh = renderMesh;
			//------------------->> Data associated with @tetMeshAsset----------
			var particles = new GPUParticle[data.Particles.Length];
			for (int i = 0; i < particles.Length; i++)
			{
				particles[i].position = mat.MultiplyPoint3x4(data.Particles[i].Position);
				particles[i].velocity = Vector3.zero;
				particles[i].invMass = data.Particles[i].InvMass;
			}

			var edges = new GPUEdge[data.Edges.Length];
			for (int i = 0; i < edges.Length; i++)
			{
				edges[i].indexA = data.Edges[i].IndexA;
				edges[i].indexB = data.Edges[i].IndexB;
				edges[i].restLen = data.Edges[i].RestLen;
			}

			var tets = new GPUTetrahedral[data.Tetrahedrals.Length];
			for (int i = 0; i < tets.Length; i++)
			{
				tets[i].i0 = data.Tetrahedrals[i].I0;
				tets[i].i1 = data.Tetrahedrals[i].I1;
				tets[i].i2 = data.Tetrahedrals[i].I2;
				tets[i].i3 = data.Tetrahedrals[i].I3;
				tets[i].restVolume = data.Tetrahedrals[i].RestVolume;
			}

			GPUSkinningInfo[] skinning = null;
			uint[] origIndices = null;

			if (tetMeshAsset.UseTetDeformation)
			{
				skinning = new GPUSkinningInfo[data.Skinning.Length];
				for (int i = 0; i < skinning.Length; i++)
				{
					skinning[i].weights = data.Skinning[i].Weights;
					skinning[i].tetIndex = data.Skinning[i].TetIndex;
				}
			}
			else
			{
				origIndices = data.OrigIndices;
			}
			//-------------------<<----------
			_state = new SoftBodyGPUState(
				particles,
				edges,
				tets,
				renderMesh.vertices,
				renderMesh.uv,
				renderMesh.triangles,
				origIndices,
				skinning,
				tetMeshAsset.UseTetDeformation,
				renderMesh
			);

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
