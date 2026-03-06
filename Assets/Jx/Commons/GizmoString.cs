#if UNITY_EDITOR
using UnityEngine;
/********************************************************************
	created:	2024/08/16 [18:34]
	filename: 	GizmoString.cs
	author:		jzq
	src:		https://gist.github.com/Arakade/9dd844c2f9c10e97e3d0
*********************************************************************/
public static class GizmoString
{
	public static void DrawString(string text, Vector3 worldPos, Color? colour = null)
	{
		UnityEditor.Handles.BeginGUI();
		if (colour.HasValue)
			GUI.color = colour.Value;
		var view = UnityEditor.SceneView.currentDrawingSceneView;
		if (view == null)
			return;
		Vector3 screenPos = view.camera.WorldToScreenPoint(worldPos);
		if (screenPos.z < 0)
			return;
		Vector2 size = GUI.skin.label.CalcSize(new GUIContent(text));
		GUI.Label(new Rect(screenPos.x - (size.x / 2), -screenPos.y + view.position.height - 4, size.x, size.y), text);
		UnityEditor.Handles.EndGUI();
	}
}
#endif