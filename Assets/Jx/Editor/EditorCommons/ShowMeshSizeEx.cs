/********************************************************************
	created:	2024/05/30 [17:12]
	filename: 	ShowMeshSizeEx.cs
	author:		jzq
	revision:	SUPPORT SkinnedMeshRenderer
*********************************************************************/
using UnityEngine;
using UnityEditor;

class ShowMeshSizeEx : EditorWindow
{
	static ShowMeshSizeEx sizeWindow;
	[MenuItem("Window/ShowSizeEx")]
	static void Init()
	{
		// Get existing open window or if none, make a new one:
		//Next line replaces old statment causing a warning in Unity 4.6 used to be "ShowSize sizeWindow = new ShowSize();"
		if (!sizeWindow)
		{
			sizeWindow = CreateInstance(typeof(ShowMeshSizeEx)) as ShowMeshSizeEx;
			sizeWindow.autoRepaintOnSceneChange = true;
		}
		sizeWindow.Show();
	}
	void OnGUI()
	{
		GameObject thisObject = Selection.activeObject as GameObject;
		if (thisObject == null)
			return;
		if (thisObject.TryGetComponent<MeshFilter>(out var mf)) //NO ***InChildren for scale-sake!
		{
			var meshTarget = mf.sharedMesh;
			var size = meshTarget.bounds.size;
			var scale = mf.transform.localScale;
			size[0] *= scale[0];
			size[1] *= scale[1];
			size[2] *= scale[2];
			LogSize(size);
		}
		else if (thisObject.TryGetComponent<SkinnedMeshRenderer>(out var skinMeshRd)) // [5/30/2024 jzq]
		{
			LogSize(skinMeshRd.bounds.size);
		}
		else
		{
			Component c = thisObject.GetComponentInChildren<MeshFilter>();
			if (!c)
				c = thisObject.GetComponentInChildren<SkinnedMeshRenderer>();
			if (!c)
				EditorGUILayout.HelpBox("No Valid Component Found **Even in Children**", MessageType.Error);
			else
				EditorGUILayout.HelpBox($"Tip: Try-Sel: {c.GetPath(thisObject.transform)}", MessageType.Info);
		}
		//
		void LogSize(Vector3 size)
		{
			GUILayout.Label("Size\n( " + (size.x).ToString("0.##") + " , " + (size.y).ToString("0.##") + " , " + (size.z).ToString("0.##") + " )");
		}
	}
	void OnSelectionChange()
	{
		if (Selection.activeGameObject != null)
			Repaint();
	}
}
static class _MonoEx_
{
	public static string GetPath(this Component obj, Transform root = null)
	{
		var path = obj.name;
		var p = obj.transform;
		while (p = p.parent)
		{
			if (root == p)
				break;
			path = $"{p.name}/{path}";
		}
		return path;
	}
}