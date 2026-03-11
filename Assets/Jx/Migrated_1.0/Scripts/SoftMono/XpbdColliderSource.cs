// XpbdColliderSource.cs
//
// Attach to any GameObject with a primitive Collider to participate in
// XPBD soft-body collision (Phase 2 — analytic shape tests).
//
// Supported:   SphereCollider, BoxCollider, CapsuleCollider
// Unsupported: MeshCollider — logs a warning and self-disables.
//
// Collider kind is auto-detected:
//   Static    — no Rigidbody
//   Kinematic — Rigidbody.isKinematic == true
//   Dynamic   — Rigidbody present and not kinematic
//
// Registration with SoftBodySimulationManager happens automatically on Enable/Disable.

using UnityEngine;

namespace XPBD
{
	[DisallowMultipleComponent]
	[RequireComponent(typeof(Collider))]
	public class XpbdColliderSource : MonoBehaviour
	{
		public enum ColType
		{
			Static, Kinematic, Dynamic
		}
		public enum ShapeType
		{
			Sphere = 0, OBB = 1, Capsule = 2
		}

		// ── Runtime state (read by manager each fixed step) ───────────────────
		public ColType Type
		{
			get; private set;
		}
		public ShapeType Shape
		{
			get; private set;
		}
		public Rigidbody Body
		{
			get; private set;
		}
		public ShapeDescriptorCPU Descriptor
		{
			get; private set;
		}
		public Vector3 SurfaceVelocity
		{
			get; private set;
		}

		Collider _col;
		Vector3 _prevPos;
		Quaternion _prevRot;

		// ─────────────────────────────────────────────────────────────────────
		void Awake()
		{
			_col = GetComponent<Collider>();
			Body = GetComponent<Rigidbody>();

			if (_col is MeshCollider)
			{
				Debug.LogWarning(
					$"[XPBD] '{name}': MeshCollider not supported by XpbdColliderSource. " +
					"Use SphereCollider, BoxCollider or CapsuleCollider.", this);
				enabled = false;
				return;
			}

			Type = Body == null ? ColType.Static
				  : Body.isKinematic ? ColType.Kinematic
				  : ColType.Dynamic;

			Shape = _col is SphereCollider ? ShapeType.Sphere
				  : _col is BoxCollider ? ShapeType.OBB
				  : ShapeType.Capsule;

			_prevPos = transform.position;
			_prevRot = transform.rotation;
		}

		void OnEnable() => SoftBodySimulationManager.Instance?.RegisterCollider(this);
		void OnDisable() => SoftBodySimulationManager.Instance?.UnregisterCollider(this);

		// ─────────────────────────────────────────────────────────────────────
		// Called by manager each fixed step. Updates surface velocity and
		// rebuilds the ShapeDescriptorCPU from current Unity transform state.
		// ─────────────────────────────────────────────────────────────────────
		public void RefreshDescriptor(float dt, uint dynSlot)
		{
			SurfaceVelocity =
				Type == ColType.Dynamic && Body ? Body.velocity :
				Type == ColType.Kinematic ? (transform.position - _prevPos) / Mathf.Max(dt, 1e-6f)
												  : Vector3.zero;

			_prevPos = transform.position;
			_prevRot = transform.rotation;

			float rbInvMass = Type == ColType.Dynamic && Body && Body.mass > 0f
							? 1f / Body.mass : 0f;

			Descriptor = BuildDescriptor((uint) Shape, (uint) Type, dynSlot, rbInvMass);
		}

		// ─────────────────────────────────────────────────────────────────────
		ShapeDescriptorCPU BuildDescriptor(uint shapeType, uint colType,
											uint dynSlot, float rbInvMass)
		{
			var d = new ShapeDescriptorCPU
			{
				shapeType = shapeType,
				colType = colType,
				dynSlot = dynSlot,
				rbInvMass = rbInvMass,
			};

			switch (_col)
			{
				case SphereCollider sc:
					d.centre = transform.TransformPoint(sc.center);
					d.param0 = sc.radius * MaxScale();
					break;

				case BoxCollider bc:
					d.centre = transform.TransformPoint(bc.center);
					var hs = Vector3.Scale(bc.size * 0.5f, transform.lossyScale);
					d.axis = transform.right;
					//Fix param0 == 0 NOT Working : CLD_MIN_SIZE_PARAM [3/11/2026 jzq]
					d.param0 = Mathf.Max(SoftConst.CLD_MIN_SIZE_PARAM, Mathf.Abs(hs.x));
					d.axis2 = transform.up;
					d.param1 = Mathf.Max(SoftConst.CLD_MIN_SIZE_PARAM, Mathf.Abs(hs.y));
					d.param2 = Mathf.Max(SoftConst.CLD_MIN_SIZE_PARAM, Mathf.Abs(hs.z));
					break;

				case CapsuleCollider cc:
					d.centre = transform.TransformPoint(cc.center);
					var localAxis = cc.direction == 0 ? Vector3.right
								  : cc.direction == 1 ? Vector3.up
								  : Vector3.forward;
					d.axis = transform.TransformDirection(localAxis).normalized;
					d.param0 = cc.radius * MaxScale();
					float axisScale = cc.direction == 0 ? Mathf.Abs(transform.lossyScale.x)
									: cc.direction == 1 ? Mathf.Abs(transform.lossyScale.y)
									: Mathf.Abs(transform.lossyScale.z);
					d.param1 = Mathf.Max(0f, cc.height * 0.5f * axisScale - d.param0);
					break;
			}

			return d;
		}

		float MaxScale() =>
			Mathf.Max(
				Mathf.Abs(transform.lossyScale.x),
				Mathf.Max(
					Mathf.Abs(transform.lossyScale.y),
					Mathf.Abs(transform.lossyScale.z)));
	}

	// ── CPU mirror of HLSL ShapeDescriptor — must match layout exactly ────────
	// 64 bytes, 16-byte aligned. StructLayout Sequential guarantees no padding.
	[System.Runtime.InteropServices.StructLayout(
		System.Runtime.InteropServices.LayoutKind.Sequential)]
	public struct ShapeDescriptorCPU
	{
		// word 0 (bytes 0-15)
		public uint shapeType;
		public uint colType;
		public uint dynSlot;
		public float rbInvMass;
		// word 1 (bytes 16-31)
		public Vector3 centre;
		public float param0;    // sphere/capsule: radius;  OBB: halfX
								// word 2 (bytes 32-47)
		public Vector3 axis;      // capsule: segment axis;   OBB: world local-X
		public float param1;    // capsule: halfHeight;     OBB: halfY
								// word 3 (bytes 48-63)
		public Vector3 axis2;     // OBB: world local-Y       (zero for others)
		public float param2;    // OBB: halfZ               (zero for others)
	}
}
