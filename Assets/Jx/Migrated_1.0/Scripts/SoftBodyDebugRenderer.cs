// SoftBodyDebugRenderer.cs
// Optional debug overlay that draws the tetrahedral wireframe mesh.
// Replaces the m_renderTetMesh / tetrahedral.vert / simple.frag path
// from Renderer.cpp (vkCmdDraw(12, tetCount, 0, 0) procedural draw).
//
// Attach to the same GameObject as SoftBodySimulationManager.
// Toggle via the 'Z' key (matching original ImGui behaviour) or the Inspector.

using UnityEngine;

namespace XPBD
{
	public class SoftBodyDebugRenderer : MonoBehaviour
	{
		[Header("References")]
		public SoftBodySimulationManager Manager;
		public Material TetWireframeMaterial; // TetWireframe.shader

		[Header("Toggle")]
		public bool ShowWireframe = false;
		public KeyCode ToggleKey = KeyCode.Z;

		// 12 procedural vertices per tet = 4 faces × 3 verts
		// matches vkCmdDraw(12, tetCount, 0, 0)
		const int VERTS_PER_TET = 12;

		void Update()
		{
			if (Input.GetKeyDown(ToggleKey))
				ShowWireframe = !ShowWireframe;
		}

		void OnRenderObject()
		{
			if (!ShowWireframe || Manager == null || TetWireframeMaterial == null)
				return;

			// Use Camera.current view-projection (matches UBO.viewProj in original)
			Matrix4x4 vp = Camera.current.projectionMatrix * Camera.current.worldToCameraMatrix;

			TetWireframeMaterial.SetMatrix("_ViewProj", vp);
			TetWireframeMaterial.SetPass(0);

			// Iterate over all active bodies — each body gets a separate
			// DrawProceduralNow call with its own particle/tet buffers.
			foreach (var body in GetActiveBodies())
			{
				TetWireframeMaterial.SetBuffer("_Particles", body.ParticleBuffer);
				TetWireframeMaterial.SetBuffer("_Tetrahedrals", body.TetBuffer);

				// Procedural draw: VERTS_PER_TET verts × tetCount instances
				// Graphics.DrawProceduralNow replaces vkCmdDraw
				Graphics.DrawProceduralNow(
					MeshTopology.Triangles,
					VERTS_PER_TET,
					body.TetCount
				);
			}
		}

		// Reflection-free access to the manager's body list
		// In a production project expose a public IReadOnlyList<SoftBodyGPUState>
		// on SoftBodySimulationManager instead.
		System.Collections.Generic.List<SoftBodyGPUState> GetActiveBodies()
		{
			// Access via the internal list — add a public accessor to
			// SoftBodySimulationManager if preferred.
			var field = typeof(SoftBodySimulationManager)
				.GetField("_bodies",
					System.Reflection.BindingFlags.NonPublic |
					System.Reflection.BindingFlags.Instance);

			return field?.GetValue(Manager)
				as System.Collections.Generic.List<SoftBodyGPUState>
				?? new System.Collections.Generic.List<SoftBodyGPUState>();
		}
	}
}
