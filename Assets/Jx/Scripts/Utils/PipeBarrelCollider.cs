using UnityEngine;
/*******************************************************
*	filename:	CurvedPipeCollider.cs
*	created:  	3/26/2026
*	author:		jzq
********************************************************/
[DisallowMultipleComponent]
public sealed class PipeBarrelCollider : MonoBehaviour
{
	enum Axis
	{
		X, Y, Z
	}
	#region Inspector
	[Header("Collider Settings")]
	[Min(0)]
	public float wallThickness = 0.1f;
	[Range(0, 1)]
	public float widthOverlap_Percent = .05f; // Slight overlap to prevent gaps
	[Range(0, 1)]
	public float lengthOverlap_Percent = .01f;
	[SerializeField] Axis domainAxis;
	#endregion

	[ContextMenu("Generate Structured Barrel")]
	public void Generate()
	{
		MeshFilter mf = GetComponent<MeshFilter>();
		if (!mf || !mf.sharedMesh)
			return;
		const string rootName = "CLDs";
		// 1. Setup Hierarchy: Model -> CLDs
		Transform cldsRoot = transform.Find(rootName);
		if (cldsRoot)
			cldsRoot.CleanChildren();
		else
		{
			cldsRoot = new GameObject(rootName).transform;
			cldsRoot.SetParent(transform, false);
		}

		Mesh mesh = mf.sharedMesh;
		Vector3[] verts = mesh.vertices;
		int[] tris = mesh.triangles;
		Vector3 worldScale = transform.lossyScale;
		// [3/26/2026 jzq]
		var domainDir = domainAxis == Axis.X ? transform.right : domainAxis == Axis.Y ? transform.up : transform.forward;
		// 2. Iterate through mesh quads (2 triangles = 1 face)
		// Adjust i += 6 based on your mesh topology (how many tris per 'slat')
		for (int i = 0; i < tris.Length; i += 6)
		{
			Vector3 v0 = verts[tris[i]];
			Vector3 v1 = verts[tris[i + 1]];
			Vector3 v2 = verts[tris[i + 2]];

			// Calculate Center and Orientation
			Vector3 center = (v0 + v1 + v2) / 3f;
			Vector3 dirForward = (v1 - v0).normalized; // Direction along pipe length
			Vector3 dirSide = (v2 - v0).normalized;    // Direction around circumference
			Vector3 normal = Vector3.Cross(dirForward, dirSide).normalized;

			// 3. Create Slat
			GameObject slat = new GameObject($"Slat_{i / 6}");
			slat.transform.SetParent(cldsRoot, false);

			slat.transform.localPosition = center;
			// Orient box so Z points along pipe and Y points 'out' of the wall
			slat.transform.localRotation = Quaternion.LookRotation(dirForward, normal);

			BoxCollider bc = slat.AddComponent<BoxCollider>();

			// Calculate dimensions based on vertex distances
			float length = (v1 - v0).magnitude * (1 + lengthOverlap_Percent);
			float width = (v2 - v0).magnitude * (1 + widthOverlap_Percent);

			// thickness / worldScale ensures the collider doesn't bloat if you scale the parent
			bc.size = new Vector3(width, wallThickness / worldScale.y, length);
			// vertical [3/26/2026 jzq]
			if (Mathf.Abs(Vector3.Dot(domainDir, FindMinDir())) > .8f)
				DestroyImmediate(slat);
			//
			Vector3 FindMinDir()
			{
				var s = bc.size;
				var minAxis = Axis.X;
				if (s.y < s.x)
					minAxis = s.y < s.z ? Axis.Y : Axis.Z;
				else if (s.z < s.x)
					minAxis = Axis.Z;
				var tr = bc.transform;
				return minAxis == Axis.X ? tr.right : minAxis == Axis.Y ? tr.up : tr.forward;
			}
		}

		Debug.Log($"Paved {cldsRoot.childCount / 6} slats into {rootName}");
	}
}
