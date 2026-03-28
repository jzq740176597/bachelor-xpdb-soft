// XpbdSdfCollider.cs
//
// Scene component that registers a baked SDF volume as a collision shape
// with the SoftBodySimulationManager. Works alongside XpbdColliderSource —
// it is NOT a replacement for sphere/OBB/capsule (which remain analytic).
// Use this for:
//   • Pipes and hollow cylinders
//   • Cup / bowl interiors
//   • Irregular concave terrain patches
//   • Any closed mesh whose interior the soft body must interact with
//
// ── Pipeline integration ──────────────────────────────────────────────────
// Registers with SoftBodySimulationManager.RegisterSdfCollider().
// Manager uploads SDF grid data to a shared flat GPU buffer (_sdfDataBuffer)
// and a per-shape metadata buffer (_sdfShapesBuffer), analogous to how
// _meshFacePlanesBuffer and _shapesBuffer work for analytic shapes.
//
// ── Mesh source ───────────────────────────────────────────────────────────
// Priority: SdfAsset (pre-baked) > MeshFilter > MeshAsset.
// If only MeshFilter/MeshAsset is set and no baked asset exists, the baker
// runs at Awake time (slow — intended for quick iteration, not shipping).
//
// ── Coordinate space ──────────────────────────────────────────────────────
// The SDF grid is in LOCAL space. The GPU receives the world-to-local matrix
// each substep via a separate per-shape transform buffer, so the shape moves
// and rotates correctly without re-baking.

using UnityEngine;

namespace XPBD
{
	[AddComponentMenu("XPBD/SDF Collider")]
	[DisallowMultipleComponent]
	public sealed class XpbdSdfCollider : MonoBehaviour
	{
		// ── Inspector ─────────────────────────────────────────────────────────
		[Header("SDF Data")]
		[Tooltip("Pre-baked SDF asset. If null, bakes from MeshFilter/MeshAsset at Awake (slow).")]
		public SdfColliderAsset SdfAsset;

		//[Tooltip("MeshFilter to bake from if SdfAsset is null. " +
		//		 "The filter's sharedMesh is used in its local space.")]
		//[Header("--Null (to Get On Self)--")] // [3/26/2026 jzq]
		////public MeshFilter SourceMeshFilter;

		//[Tooltip("Direct mesh asset fallback if SourceMeshFilter is also null.")]
		//public Mesh SourceMeshAsset;

		[Header("Collider Behaviour")]
		[Tooltip("Static: no velocity, no impulse feedback.\n" +
				 "Kinematic: surface velocity fed to particles.\n" +
				 "Dynamic: two-way Newton-3rd coupling (requires Rigidbody).")]
		public XpbdColliderSource.ColType ColliderType = XpbdColliderSource.ColType.Static;

		[Tooltip("Contact skin width in metres. Particles within this distance of the " +
				 "surface generate a contact. Should match the soft body's ContactSkin range.")]
		public float ContactSkin = 0.02f;

		// ── Runtime ───────────────────────────────────────────────────────────
		// GPU buffer holding this collider's flat SDF float grid.
		// Uploaded once at Enable; re-uploaded only if SdfAsset changes.
		// Manager reads SdfDataBuffer and SdfDataOffset to fill its shared flat buffer.
		public ComputeBuffer SdfDataBuffer
		{
			get; private set;
		}
		public int SdfDataOffset
		{
			get; set;
		}  // set by manager during rebuild

		// Current world-to-local matrix, updated every substep.
		public Matrix4x4 WorldToLocal
		{
			get; private set;
		}

		// Descriptor uploaded to _sdfShapesBuffer each substep.
		public SdfShapeDescriptorCPU Descriptor
		{
			get; private set;
		}

		Rigidbody _body;
		bool _registered;

		// ── Lifecycle ─────────────────────────────────────────────────────────
		void Awake()
		{
			_body = GetComponent<Rigidbody>();

			// Auto-detect collider type from Rigidbody
			if (ColliderType == XpbdColliderSource.ColType.Static && _body != null)
			{
				ColliderType = _body.isKinematic
					? XpbdColliderSource.ColType.Kinematic
					: XpbdColliderSource.ColType.Dynamic;
			}

			if (EnsureSdfAsset())
				UploadSdfBuffer();
		}

		void OnEnable()
		{
			if (SdfAsset == null || !SdfAsset.IsBaked)
			{
				Debug.LogError(
					$"[XpbdSdfCollider] '{name}': SdfAsset is not baked " +
					"(ResX=0 or SdfGrid empty). Open the asset and click 'Bake SDF' " +
					"in the Inspector, then assign a Source Mesh.", this);
				return;
			}
			SoftBodySimulationManager.Instance?.RegisterSdfCollider(this);
			_registered = true;
		}

		void OnDisable()
		{
			if (_registered)
			{
				SoftBodySimulationManager.Instance?.UnregisterSdfCollider(this);
				_registered = false;
			}
		}
		// [3/28/2026 jzq]
		void OnDrawGizmos()
		{
			if (!SdfAsset.IsBaked /* || !SdfAsset.SourceMesh*/)
				return;
			var gizmoColor = Color.gray;
			Gizmos.color = gizmoColor;
			//Gizmos.DrawWireMesh(SdfAsset.SourceMesh, transform.position, transform.rotation, transform.localScale);
			Gizmos.DrawMesh(
				SdfAsset.SourceMesh,
				transform.position,
				transform.rotation,
				transform.localScale
			);
		}
		void OnDestroy()
		{
			SdfDataBuffer?.Release();
			SdfDataBuffer = null;
		}

		// ── Called by Manager each substep ────────────────────────────────────
		public void RefreshDescriptor(float subDt)
		{
			if (SdfAsset == null)
				return;

			// World-to-local for GPU: transforms world-space particle position into
			// the SDF grid's local space for sampling.
			WorldToLocal = transform.worldToLocalMatrix;

			// Surface velocity for kinematic coupling
			Vector3 surfVel = Vector3.zero;
			if (ColliderType == XpbdColliderSource.ColType.Dynamic && _body)
				surfVel = _body.velocity;
			else if (ColliderType == XpbdColliderSource.ColType.Kinematic)
				surfVel = Vector3.zero; // filled by manager sweep if needed

			Descriptor = new SdfShapeDescriptorCPU
			{
				// World-space AABB for broad-phase reject (GPU skips SDF sample if outside)
				aabbCentre = transform.TransformPoint(
					(SdfAsset.BoundsMin + SdfAsset.BoundsMax) * 0.5f),
				aabbExtents = Vector3.Scale(
					(SdfAsset.BoundsMax - SdfAsset.BoundsMin) * 0.5f,
					AbsScale(transform.lossyScale)),

				// Grid layout
				sdfDataOffset = (uint) SdfDataOffset,
				resX = (uint) SdfAsset.ResX,
				resY = (uint) SdfAsset.ResY,
				resZ = (uint) SdfAsset.ResZ,

				// Local-space grid bounds
				boundsMin = SdfAsset.BoundsMin,
				boundsMax = SdfAsset.BoundsMax,

				// Collider behaviour
				colType = (uint) ColliderType,
				dynSlot = 0, // set by manager
				rbInvMass = (_body && _body.mass > 0f) ? 1f / _body.mass : 0f,
				contactSkin = ContactSkin,

				surfaceVelocity = surfVel,
			};
		}

		// ── Helpers ───────────────────────────────────────────────────────────
		bool EnsureSdfAsset()
		{
			if (SdfAsset != null && SdfAsset.IsBaked)
				return true;
			//No runtime bake [3/28/2026 jzq]
			Debug.LogError($"'{name}' should Ensure : SdfAsset != null && SdfAsset.IsBaked");
			return false;

			//Mesh mesh = ResolveMesh();
			//if (mesh == null)
			//{
			//	Debug.LogWarning($"[XpbdSdfCollider] '{name}': No mesh source found. " +
			//		"Assign SdfAsset, SourceMeshFilter, or SourceMeshAsset.", this);
			//	return false;
			//}

			//if (SdfAsset == null)
			//{
			//	// Create a temporary runtime-only asset (not persisted to disk)
			//	SdfAsset = ScriptableObject.CreateInstance<SdfColliderAsset>();
			//	SdfAsset.BakeResolution = 32;
			//	SdfAsset.BakePadding = 0.05f;
			//	Debug.LogWarning($"[XpbdSdfCollider] '{name}': No SdfAsset assigned. " +
			//		"Baking at runtime (slow). Use the XPBD → Bake SDF menu for production.", this);
			//}

			//SdfBaker.Bake(mesh, SdfAsset);
			//return true;
		}

		void UploadSdfBuffer()
		{
			SdfDataBuffer?.Release();
			SdfDataBuffer = null;

			if (SdfAsset == null || !SdfAsset.IsBaked)
				return;

			SdfDataBuffer = new ComputeBuffer(SdfAsset.SdfGrid.Length, sizeof(float));
			SdfDataBuffer.SetData(SdfAsset.SdfGrid);
		}

		//Mesh ResolveMesh()
		//{
		//	if (!SourceMeshFilter) // [3/26/2026 jzq]
		//		SourceMeshFilter = GetComponent<MeshFilter>();
		//	if (SourceMeshFilter && SourceMeshFilter.sharedMesh)
		//		return SourceMeshFilter.sharedMesh;
		//	return null; // SourceMeshAsset;
		//}

		static Vector3 AbsScale(Vector3 s) =>
			new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
	}

	// CPU mirror of the HLSL SdfShapeDescriptor (128 bytes).
	// Split into two float4x4-sized blocks so it can be uploaded as
	// a structured buffer with stride 128.
	[System.Runtime.InteropServices.StructLayout(
		System.Runtime.InteropServices.LayoutKind.Sequential)]
	public struct SdfShapeDescriptorCPU
	{
		// block 0 — grid layout and behaviour (64 bytes)
		public Vector3 aabbCentre;      // world-space AABB centre for broad reject
		public float contactSkin;
		public Vector3 aabbExtents;     // world-space AABB half-extents
		public uint sdfDataOffset;   // index into flat _sdfDataBuffer
		public uint resX, resY, resZ; // grid dimensions
		public uint colType;
		public uint dynSlot;
		public float rbInvMass;
		public float _pad0, _pad0b; //+ _pad0b [3/26/2026 jzq]

		// block 1 — local-space grid bounds + surface velocity (64 bytes)
		public Vector3 boundsMin;
		public float _pad1;
		public Vector3 boundsMax;
		public float _pad2;
		public Vector3 surfaceVelocity;
		public float _pad3;
		// 16 bytes remaining — world-to-local matrix stored separately
		// in _sdfTransformBuffer (Matrix4x4, 64 bytes per shape) to keep
		// descriptor stride at 128 bytes.
		public float _pad4, _pad5, _pad6, _pad7;
	}
}
