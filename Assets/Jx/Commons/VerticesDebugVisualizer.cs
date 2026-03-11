#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
/********************************************************************
	created:	2024/10/25 [14:28]
	filename: 	VerticesDebugVisualizer.cs
	author:		jzq
*********************************************************************/
[DisallowMultipleComponent]
sealed class VerticesDebugVisualizer : MonoBehaviour
{
	#region Inspector
	[SerializeField]
	Color color = Color.red;
	[SerializeField, Range(0, .1f)]
	float radius = .05f;
	[SerializeField]
	bool showIdxStr;
	[SerializeField]
	bool retrieveFromMf = true;
	#endregion

	#region Pub
	public IEnumerable<Vector3> debugPts
	{
		get; set;
	}
	#endregion

	#region Unity
	void Start()
	{
		if (retrieveFromMf)
		{
			var vts = GetComponent<MeshFilter>().sharedMesh.vertices;
			for (int i = 0; i < vts.Length; i++)
				vts[i] = transform.TransformPoint(vts[i]);
			debugPts = vts;
		}
	}
	void OnDrawGizmos()
	{
		if (enabled && debugPts != null)
		{
			var i = 0;
			foreach (var p in debugPts)
			{
				UnityEditor.Handles.color = color;
				UnityEditor.Handles.SphereHandleCap(2, p, Quaternion.identity, radius, EventType.Repaint);
				if (showIdxStr)
					GizmoString.DrawString($"{i}", p, Color.blue);
				++i;
			}
		}
	}
	#endregion
}
#endif
