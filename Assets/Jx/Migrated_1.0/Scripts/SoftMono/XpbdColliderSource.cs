// XpbdColliderSource.cs
//
// Supported colliders:
//   SphereCollider  → SHAPE_SPHERE  (analytic)
//   BoxCollider     → SHAPE_OBB     (analytic OBB)
//   CapsuleCollider → SHAPE_CAPSULE (analytic, spherical end-caps)
//   MeshCollider    → SHAPE_CONVEX  (face-plane inside test; convex=true required)
//
// Collider kind auto-detected:
//   Static    — no Rigidbody
//   Kinematic — Rigidbody.isKinematic
//   Dynamic   — Rigidbody present and not kinematic
//
// ── OBB floor note ────────────────────────────────────────────────────────────
// The GPU OBB test is a pure interior test: a particle must be INSIDE the box
// to register a contact. A BoxCollider used as a floor typically has Size.y = 0
// or a very small value, making the detection band too narrow to catch fast-
// moving particles (at 60 fps with gravity, a particle moves ~0.17 m per fixed
// step — far more than a 0.001 m half-height).
//
// Fix: when hy < OBB_FLOOR_EXTEND the GPU descriptor extends the box downward
// by OBB_FLOOR_EXTEND, keeping the top face at exactly the same world position.
// The top-face normal selection (py wins when particle is near the surface) is
// unaffected because the particle will always be much closer to the top face
// than to the new deep bottom.
//
// OBB_FLOOR_EXTEND = 2.0f means the box reaches 2 m below its top surface,
// safely catching any particle moving up to ~120 m/s at 60 fps.

using System.Collections.Generic;
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
			Sphere = 0, OBB = 1, Capsule = 2, Convex = 4
		}

		// Minimum OBB half-extent in the thin axis before the floor-extend kicks in.
		// Any hy below this triggers the extension.
		const float OBB_FLOOR_EXTEND = 2.0f;
		const float OBB_THIN_THRESHOLD = OBB_FLOOR_EXTEND; // extend whenever hy < this

		// ── Runtime properties ────────────────────────────────────────────────
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

		// World-space face planes for SHAPE_CONVEX (null for other types).
		public Vector4[] FacePlanes
		{
			get; private set;
		}
		// Set by manager before RefreshDescriptor for convex shapes.
		public uint FacePlanesOffset
		{
			get; set;
		}

		Collider _col;
		Vector3 _prevPos;
		Quaternion _prevRot;
		// Local-space planes for convex mesh (computed once in Awake).
		Vector4[] _localPlanes;

		// ─────────────────────────────────────────────────────────────────────
		void Awake()
		{
			_col = GetComponent<Collider>();
			Body = GetComponent<Rigidbody>();

			Type = Body == null ? ColType.Static
				 : Body.isKinematic ? ColType.Kinematic
				 : ColType.Dynamic;

			switch (_col)
			{
				case MeshCollider mc:
					if (!mc.convex)
					{
						Debug.LogError(
							$"[XPBD] '{name}': MeshCollider must have convex=true. " +
							"Enable Convex on the MeshCollider component.", this);
						enabled = false;
						return;
					}
					Shape = ShapeType.Convex;
					_localPlanes = ExtractConvexPlanes(mc.sharedMesh);
					if (Type == ColType.Static)
						FacePlanes = TransformPlanes(_localPlanes, transform.localToWorldMatrix);
					break;

				case SphereCollider:
					Shape = ShapeType.Sphere;
					break;
				case BoxCollider:
					Shape = ShapeType.OBB;
					break;
				default:
					Shape = ShapeType.Capsule;
					break;
			}

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

			if (Shape == ShapeType.Convex && Type != ColType.Static)
				FacePlanes = TransformPlanes(_localPlanes, transform.localToWorldMatrix);

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
					{
						var hs = Vector3.Scale(bc.size * 0.5f, transform.lossyScale);
						float hx = Mathf.Abs(hs.x);
						float hy = Mathf.Abs(hs.y);
						float hz = Mathf.Abs(hs.z);

						// ── Floor-extend fix ─────────────────────────────────────
						// When hy is thinner than OBB_THIN_THRESHOLD the GPU interior
						// test would miss particles that tunnel through between fixed
						// steps. We extend the box DOWNWARD by OBB_FLOOR_EXTEND so it
						// becomes thick enough to catch any realistically fast particle.
						// The top face world position is preserved by shifting the
						// centre down by the same amount we added to hy.
						//
						// Before: centre.y = C,  top = C+hy,  bot = C-hy
						// After:  centre.y = C - (OBB_FLOOR_EXTEND - hy)
						//         top = newCentre + OBB_FLOOR_EXTEND = C+hy  ← unchanged
						//         bot = newCentre - OBB_FLOOR_EXTEND = C+hy - 2*OBB_FLOOR_EXTEND
						//
						// The top-face normal wins because the particle (just below the
						// original surface) is always much closer to the top than to the
						// deep new bottom:
						//   py_top  = OBB_FLOOR_EXTEND - |ly_top|  ≈ small (particle near top face)
						//   py_bot  = OBB_FLOOR_EXTEND + small     ≈ large
						//   → py_top < py_bot → top face normal always selected ✓
						// ─────────────────────────────────────────────────────────
						Vector3 worldCentre = transform.TransformPoint(bc.center);
						Vector3 worldUp = transform.up; // local Y in world space

						if (hy < OBB_THIN_THRESHOLD)
						{
							float shift = OBB_FLOOR_EXTEND - hy;
							worldCentre -= worldUp * shift;
							hy = OBB_FLOOR_EXTEND;
						}

						d.centre = worldCentre;
						d.axis = transform.right;  // world local-X
						d.param0 = hx;
						d.axis2 = worldUp;          // world local-Y
						d.param1 = hy;
						d.param2 = hz;
						break;
					}

				case CapsuleCollider cc:
					{
						d.centre = transform.TransformPoint(cc.center);
						var localAx = cc.direction == 0 ? Vector3.right
									 : cc.direction == 1 ? Vector3.up : Vector3.forward;
						d.axis = transform.TransformDirection(localAx).normalized;
						d.param0 = cc.radius * MaxScale();
						float axScl = cc.direction == 0 ? Mathf.Abs(transform.lossyScale.x)
									 : cc.direction == 1 ? Mathf.Abs(transform.lossyScale.y)
									 : Mathf.Abs(transform.lossyScale.z);
						d.param1 = Mathf.Max(0f, cc.height * 0.5f * axScl - d.param0);
						break;
					}

				case MeshCollider:
					// Convex mesh: manager already set FacePlanesOffset
					d.centre = transform.position;
					d.param0 = System.BitConverter.ToSingle(
						System.BitConverter.GetBytes(FacePlanesOffset), 0);
					d.param1 = System.BitConverter.ToSingle(
						System.BitConverter.GetBytes((uint) (FacePlanes?.Length ?? 0)), 0);
					break;
			}

			return d;
		}

		// ── Convex plane extraction ───────────────────────────────────────────

		// Extract unique face planes from a convex mesh in LOCAL space.
		// plane = float4(normal.xyz, d)  where dot(n, localPoint) + d == 0
		// Planes with near-parallel normals (dot > 0.999) are deduplicated.
		static Vector4[] ExtractConvexPlanes(Mesh mesh)
		{
			if (mesh == null)
				return System.Array.Empty<Vector4>();
			var verts = mesh.vertices;
			var tris = mesh.triangles;
			var planes = new List<Vector4>();

			for (int i = 0; i < tris.Length; i += 3)
			{
				var v0 = verts[tris[i]];
				var v1 = verts[tris[i + 1]];
				var v2 = verts[tris[i + 2]];
				var n = Vector3.Cross(v1 - v0, v2 - v0);
				if (n.sqrMagnitude < 1e-10f)
					continue;
				n.Normalize();
				float d = -Vector3.Dot(n, v0);

				bool dup = false;
				foreach (var pl in planes)
				{
					if (Vector3.Dot(new Vector3(pl.x, pl.y, pl.z), n) > 0.999f &&
						Mathf.Abs(pl.w - d) < 1e-4f)
					{
						dup = true;
						break;
					}
				}
				if (!dup)
					planes.Add(new Vector4(n.x, n.y, n.z, d));
			}
			return planes.ToArray();
		}

		// Transform local-space planes to world space.
		// Normals use inverse-transpose; d is re-derived from a surface point.
		static Vector4[] TransformPlanes(Vector4[] local, Matrix4x4 m)
		{
			if (local == null || local.Length == 0)
				return System.Array.Empty<Vector4>();
			var result = new Vector4[local.Length];
			var mIT = m.inverse.transpose;
			for (int i = 0; i < local.Length; i++)
			{
				var lp = local[i];
				var wn = mIT.MultiplyVector(new Vector3(lp.x, lp.y, lp.z)).normalized;
				var wp = m.MultiplyPoint3x4(new Vector3(lp.x, lp.y, lp.z) * (-lp.w));
				float d = -Vector3.Dot(wn, wp);
				result[i] = new Vector4(wn.x, wn.y, wn.z, d);
			}
			return result;
		}

		float MaxScale() => Mathf.Max(
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
