// XpbdColliderSource.cs
//
// Supported colliders:
//   SphereCollider  → SHAPE_SPHERE  (interior test, analytic)
//   BoxCollider     → SHAPE_OBB     (see below)
//   CapsuleCollider → SHAPE_CAPSULE (interior test, analytic)
//   MeshCollider    → SHAPE_CONVEX  (face-plane interior test; convex=true required)
//
// ── OBB collision modes ───────────────────────────────────────────────────────
// Thick box (hy >= OBB_CCD_THRESHOLD, e.g. a wall):
//   Interior 3D slab test. param1 = +hy. GPU: point-in-box, push to nearest face.
//
// Thin/flat box (hy < OBB_CCD_THRESHOLD, e.g. a floor with Size.y = 0):
//   Speculative half-space test. param1 = -hy (negative signals this mode to GPU).
//   The GPU treats the floor as a ONE-SIDED HALF-SPACE — any predicted position
//   within OBB_CONTACT_OFFSET (2 cm) of the top face generates a contact pushing
//   upward. Works at any particle speed; no sweep or segment test needed.
//   This mirrors Unity Speculative CCD / PhysX contact generation.
//
// Why not sweep-based CCD?
//   Sweep (segment prev→predict vs face plane) requires a finite slab thickness.
//   For a zero-thickness box the "top" and "bottom" faces are nearly co-planar.
//   At typical XPBD substep sizes (5 m/s / 1200 Hz = 4 mm/substep) a 0.2 mm slab
//   is crossed in a single substep, making the sweep numerically unreliable.
//   The speculative approach has no such limitation.

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

		// When the OBB half-height in the normal axis is below this threshold,
		// the descriptor encodes CCD mode (param1 = -halfY).
		// 0.1m covers any reasonable floor thickness.
		const float OBB_CCD_THRESHOLD = 0.1f;

		// ── Inspector ─────────────────────────────────────────────────────────
		[Header("CCD (Continuous Collision Detection)")]
		[Tooltip("Encode CCD mode for thin/flat OBB colliders (floors, walls). " +
				 "Prevents tunneling at the cost of a segment-vs-face test per substep. " +
				 "Disable if the collider is thick enough that tunneling can't occur.")]
		public bool EnableCCD = true;
		// Sentinel written into param2 to signal CCD mode to the GPU.
		// Must be > 0 so the GPU can distinguish it from a real halfZ.
		// We still encode the real halfZ into a separate field — see below.
		// For OBB CCD: we repurpose ShapeDescriptor as:
		//   param0 = halfX  param1 = halfY (thin)  param2 = halfZ
		//   axis2  = worldUp (already there)
		// The CCD flag is signalled by param1 < OBB_CCD_THRESHOLD
		// → GPU simply checks: if (hy < CCD_THRESHOLD) → CCD path
		// No extra field needed.

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
		// Per-frame start state — captured once before the substep loop so each
		// substep can interpolate the collider to its swept intermediate position.
		Vector3 _frameStartPos;
		Quaternion _frameStartRot;
		// Centre of the shape at the START of the current substep.
		// Encoded into the descriptor so the GPU can do swept-sphere CCD.
		Vector3 _prevSubstepCentre;
		// Local-space planes for convex mesh (computed once in Awake).
		Vector4[] _localPlanes;
		// Dirty-tracking for Static convex — re-transform only when moved.
		Vector3 _cachedConvexPos = new Vector3(float.MaxValue, 0, 0);
		Quaternion _cachedConvexRot = Quaternion.identity;

		// [3/12/2026 jzq]
		static bool ValidateCld_S(Collider c)
		{
			if (!c.enabled)
				return false;
			if (c is MeshCollider mc)
			{
				if (!mc.convex)
					Debug.LogError($"[XPBD] '{c.name}': MeshCollider must have convex = true", c);
				return mc.convex;
			}
			return (c is SphereCollider || c is BoxCollider || c is CapsuleCollider);
		}
		// ─────────────────────────────────────────────────────────────────────
		void Awake()
		{
			_col = GetComponent<Collider>();
			// [3/12/2026 jzq]
			if (_col && !ValidateCld_S(_col))
			{
				var oldCol = _col;
				_col = null;
				// try find the first valid collider if multiple are present
				foreach (var c in GetComponents<Collider>())
				{
					if (c != oldCol && ValidateCld_S(c))
					{
						_col = c;
						break;
					}
				}
			}
			if (_col == null)
			{
				Debug.LogError($"[XPBD] '{name}': No valid Collider found. " +
					"Ensure at least one Collider component is enabled and valid.", this);
				enabled = false;
				return;
			}
			Body = GetComponent<Rigidbody>();

			Type = Body == null ? ColType.Static
				 : Body.isKinematic ? ColType.Kinematic
				 : ColType.Dynamic;

			switch (_col)
			{
				case MeshCollider mc:
					///Did ValidateCld_S [3/12/2026 jzq]
					//if (!mc.convex)
					//{
					//	Debug.LogError(
					//		$"[XPBD] '{name}': MeshCollider must have convex=true. " +
					//		"Enable Convex on the MeshCollider component.", this);
					//	enabled = false;
					//	return;
					//}
					Shape = ShapeType.Convex;
					_localPlanes = ExtractConvexPlanes(mc.sharedMesh);
					// FacePlanes transformed on first RefreshDescriptor (not cached in Awake)
					// so that moving a Static MeshCollider at runtime is always reflected.
					break;
				case SphereCollider:
					Shape = ShapeType.Sphere;
					break;
				case BoxCollider:
					Shape = ShapeType.OBB;
					break;
				case CapsuleCollider:
					Shape = ShapeType.Capsule;
					break;
				default:
					Debug.LogError($"Invalid ColliderType '{_col.GetType()}'");
					break;
			}
			// Use Body.position for kinematic so _prevPos tracks the physics-committed
			// target, which is what SnapshotStartOfFrame will use next frame.
			_prevPos = GetCurPos();
			_prevRot = GetCurRot();
		}
		// + [3/22/2026 jzq]
		Vector3 GetCurPos()
		{
			if (Type == ColType.Kinematic && Body)
				return Body.position;

			return transform.position;
		}

		Quaternion GetCurRot()
		{
			if (Type == ColType.Kinematic && Body)
				return Body.rotation;

			return transform.rotation;
		}

		void OnEnable()
		{
			SoftBodySimulationManager.Instance?.RegisterCollider(this);
		}


		void OnDisable()
		{
			SoftBodySimulationManager.Instance?.UnregisterCollider(this);
		}


		// ─────────────────────────────────────────────────────────────────────
		// Called ONCE per fixed frame, BEFORE the substep loop.
		// Captures the collider's start-of-frame world position/rotation so
		// RefreshDescriptorAtFraction can interpolate towards the end position.
		// ─────────────────────────────────────────────────────────────────────
		public void SnapshotStartOfFrame()
		{
			_frameStartPos = _prevPos;    // _prevPos was set at the END of the last frame
			_frameStartRot = _prevRot;
			// First substep starts from last-frame end position.
			_prevSubstepCentre = _prevPos;
		}

		// ─────────────────────────────────────────────────────────────────────
		// Called once per substep (t = substep index / SubSteps, range [0..1)).
		// Interpolates sphere/capsule/OBB centre between start-of-frame and the
		// current transform position, so the GPU sees the swept intermediate
		// position rather than only the end-of-frame position.
		// Surface velocity is recomputed as displacement / subDT so the GPU
		// inherits the correct per-substep velocity from the contact correction.
		// ─────────────────────────────────────────────────────────────────────
		public void RefreshDescriptorAtFraction(float t, float subDT, uint dynSlot)
		{
			// For kinematic rigidbodies, Body.position reflects the target set by
			// rb.MovePosition() — transform.position may lag by one physics step.
			// For static/dynamic, transform.position is the correct source.
			Vector3 targetPos = GetCurPos();
			Quaternion targetRot = GetCurRot();

			// Interpolated world position/rotation at this substep fraction
			Vector3 lerpPos = Vector3.Lerp(_frameStartPos, targetPos, t);
			Quaternion lerpRot = Quaternion.Slerp(_frameStartRot, targetRot, t);

			// Surface velocity at substep granularity:
			// displacement from previous substep position to this substep position.
			// For kinematic bodies this correctly scales with subDT.
			if (Type == ColType.Dynamic && Body)
				SurfaceVelocity = Body.velocity;

			else if (Type == ColType.Kinematic)
				SurfaceVelocity = (lerpPos - _prevPos) / Mathf.Max(subDT, 1e-6f);

			else
				SurfaceVelocity = Vector3.zero;


			// Record centre at START of this substep before advancing _prevPos.
			_prevSubstepCentre = _prevPos;
			_prevPos = lerpPos;
			_prevRot = lerpRot;

			// Convex face planes: only re-transform if the collider actually moved.
			if (Shape == ShapeType.Convex)
			{
				bool moved = (lerpPos - _cachedConvexPos).sqrMagnitude > 1e-8f
						  || Mathf.Abs(Quaternion.Dot(lerpRot, _cachedConvexRot)) < 1f - 1e-6f;
				if (moved || FacePlanes == null)
				{
					var mtx = Matrix4x4.TRS(lerpPos, lerpRot, transform.lossyScale);
					FacePlanes = TransformPlanes(_localPlanes, mtx);
					_cachedConvexPos = lerpPos;
					_cachedConvexRot = lerpRot;
				}
			}

			float rbInvMass = Type == ColType.Dynamic && Body && Body.mass > 0f
							? 1f / Body.mass : 0f;
			Descriptor = BuildDescriptorAtTransform(lerpPos, lerpRot, (uint) Type, dynSlot, rbInvMass);
		}

		// ─────────────────────────────────────────────────────────────────────
		// Called by manager each fixed step. Updates surface velocity and
		// rebuilds the ShapeDescriptorCPU from current Unity transform state.
		// ─────────────────────────────────────────────────────────────────────
		public void RefreshDescriptor(float dt, uint dynSlot)
		{
			Vector3 curPos = GetCurPos();
			if (Type == ColType.Dynamic && Body)
				SurfaceVelocity = Body.velocity;

			else if (Type == ColType.Kinematic)
				SurfaceVelocity = (curPos - _prevPos) / Mathf.Max(dt, 1e-6f);

			else
				SurfaceVelocity = Vector3.zero;


			_prevPos = GetCurPos();
			_prevRot = GetCurRot();

			// Convex face planes: always re-transform when the transform changed.
			// Static bodies are expected to stay put — we only re-bake when they actually move
			// (fixes mismatch with BoxCollider which always reads transform live).
			if (Shape == ShapeType.Convex)
			{
				var pos = GetCurPos();
				var rot = GetCurRot();
				bool moved = (pos - _cachedConvexPos).sqrMagnitude > 1e-8f
						  || Mathf.Abs(Quaternion.Dot(rot, _cachedConvexRot)) < 1f - 1e-6f;
				if (moved || FacePlanes == null)
				{
					FacePlanes = TransformPlanes(_localPlanes, transform.localToWorldMatrix);
					_cachedConvexPos = pos;
					_cachedConvexRot = rot;
				}
			}

			float rbInvMass = Type == ColType.Dynamic && Body && Body.mass > 0f
							? 1f / Body.mass : 0f;

			Descriptor = BuildDescriptor((uint) Shape, (uint) Type, dynSlot, rbInvMass);
		}

		// ─────────────────────────────────────────────────────────────────────
		// Build descriptor using an explicit interpolated position/rotation.
		// Used by RefreshDescriptorAtFraction for per-substep sweep.
		ShapeDescriptorCPU BuildDescriptorAtTransform(Vector3 pos, Quaternion rot,
											uint colType, uint dynSlot, float rbInvMass)
		{
			var d = new ShapeDescriptorCPU
			{
				shapeType = (uint) Shape,
				colType = colType,
				dynSlot = dynSlot,
				rbInvMass = rbInvMass,
			};
			switch (_col)
			{
				case SphereCollider sc:
					d.centre = pos + rot * (Vector3.Scale(sc.center, transform.lossyScale));
					d.param0 = sc.radius * MaxScale();
					// axis = sphere centre at START of this substep (for GPU swept CCD).
					// rot is re-used here because sc.center offset rotates with the object;
					// _prevSubstepCentre already tracks the transform-origin sweep.
					d.axis = _prevSubstepCentre + rot * Vector3.Scale(sc.center, transform.lossyScale);
					break;
				case BoxCollider bc:
					{
						var hs = Vector3.Scale(bc.size * 0.5f, transform.lossyScale);
						d.centre = pos + rot * (Vector3.Scale(bc.center, transform.lossyScale));
						d.axis = rot * Vector3.right;
						d.param0 = Mathf.Abs(hs.x);
						d.axis2 = rot * Vector3.up;
						float hy = Mathf.Abs(hs.y);
						d.param1 = (EnableCCD && hy < OBB_CCD_THRESHOLD) ? -hy : hy;
						d.param2 = Mathf.Abs(hs.z);
						break;
					}
				case CapsuleCollider cc:
					{
						// cc.direction: 0=X, 1=Y, 2=Z — must match BuildDescriptor axis logic.
						Vector3 localAx;
						if (cc.direction == 0)
							localAx = Vector3.right;

						else if (cc.direction == 2)
							localAx = Vector3.forward;

						else
							localAx = Vector3.up;

						float axScl;
						if (cc.direction == 0)
							axScl = Mathf.Abs(transform.lossyScale.x);

						else if (cc.direction == 2)
							axScl = Mathf.Abs(transform.lossyScale.z);

						else
							axScl = Mathf.Abs(transform.lossyScale.y);

						d.centre = pos + rot * Vector3.Scale(cc.center, transform.lossyScale);
						d.axis = (rot * localAx).normalized;
						d.param0 = cc.radius * MaxScale();
						d.param1 = Mathf.Max(0f, cc.height * 0.5f * axScl - d.param0);
						break;
					}
				default: // Convex MeshCollider
					{
						// Mirror the MeshCollider case in BuildDescriptor exactly.
						// FacePlanes and FacePlanesOffset are already updated for this
						// substep's interpolated pose by RefreshDescriptorAtFraction
						// before this method is called.
						var mc2 = (MeshCollider)_col;
						d.centre = pos;  // unused by GPU; kept for debug clarity
						d.param0 = System.BitConverter.ToSingle(
							System.BitConverter.GetBytes(FacePlanesOffset), 0);
						d.param1 = System.BitConverter.ToSingle(
							System.BitConverter.GetBytes((uint)(FacePlanes?.Length ?? 0)), 0);
						// AABB for GPU bounds-reject in TestConvex.
						// mc2.bounds is world-space and Unity keeps it current.
						var bnd = mc2.bounds;
						d.axis  = bnd.center;
						d.axis2 = bnd.extents;
						break;
					}
			}
			return d;
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
					// axis = sphere centre at START of this substep (for GPU swept CCD).
					d.axis = _prevSubstepCentre + transform.rotation * Vector3.Scale(sc.center, transform.lossyScale);
					break;

				case BoxCollider bc:
					{
						var hs = Vector3.Scale(bc.size * 0.5f, transform.lossyScale);
						d.centre = transform.TransformPoint(bc.center);
						d.axis = transform.right;   // world local-X
						d.param0 = Mathf.Abs(hs.x);
						d.axis2 = transform.up;      // world local-Y
						float hy = Mathf.Abs(hs.y);
						// param1 encoding:
						//   negative → speculative half-space mode (thin/flat box, floor).
						//              GPU adds OBB_CONTACT_OFFSET (2 cm) to hy for detection,
						//              but the contact point projects to centre + ay*hy,
						//              so a zero-thickness floor (hy=0) makes particles rest
						//              exactly at the floor's transform y. ← IMPORTANT
						//   positive → interior 3D slab mode (thick box, wall).
						//
						// We store the RAW geometric hy without any clamp so the contact
						// surface stays at the collider's actual surface. The GPU is responsible
						// for adding the skin offset to expand the detection window.
						//
						// -0.0f corner case: asuint(-0.0f) has the sign bit SET, so the GPU
						// sign-bit check (asuint & 0x80000000) correctly routes hy=0 floors
						// to speculative mode. No CPU clamp needed.
						d.param1 = (EnableCCD && hy < OBB_CCD_THRESHOLD) ? -hy : hy;
						d.param2 = Mathf.Abs(hs.z);
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

				case MeshCollider mc2:
					{
						// centre unused by GPU for Convex (planes are already world-space)
						d.centre = transform.position;
						// param0 = faceOffset (uint reinterpreted as float)
						d.param0 = System.BitConverter.ToSingle(
							System.BitConverter.GetBytes(FacePlanesOffset), 0);
						// param1 = faceCount (uint reinterpreted as float)
						d.param1 = System.BitConverter.ToSingle(
							System.BitConverter.GetBytes((uint) (FacePlanes?.Length ?? 0)), 0);
						// axis  = world-space AABB centre  (for bounds-reject in TestConvex)
						// axis2 = world-space AABB half-extents (for exact per-axis reject)
						// mc2.bounds is always world-space; Unity keeps it current automatically.
						var b = mc2.bounds;
						d.axis = b.center;
						d.axis2 = b.extents;
						break;
					}
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

		float MaxScale()
		{
			var s = transform.lossyScale;
			return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
		}
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
