using UnityEngine;
using XPBD;
/********************************************************************
	created:	2026/03/07
	filename: 	InstantiateSoftOnClick
	author:		jzq
*********************************************************************/
public sealed class InstantiateSoftOnClick : MonoBehaviour
{
	#region Inspector
	[SerializeField]
	GameObject softPrefab;
	[SerializeField]
	GameObject anotherPrefab;
	[SerializeField]
	float minHeight = 5;
	[SerializeField]
	float maxHeight = 10;
	#endregion

	#region Pub
	public void DoInstantiate(Vector3 p)
	{
		p.y = Random.Range(minHeight, maxHeight);
		Debug.Log($"Instantiate pos = {p}");
		var prefab = instantiateAnother ? anotherPrefab : softPrefab;
		var soft = Instantiate(prefab, p, Quaternion.identity).GetComponent<SoftBodyComponent>();
		//soft.Init()
	}
	#endregion

	#region Unity
	void OnGUI()
	{
		// 1. Define the panel area at (10, 10) with width 200 and height 150
		// Use GUI.skin.box to give it a visible background panel
		GUILayout.BeginArea(new Rect(10, 10, 200, 150), GUI.skin.box);
		if (anotherPrefab)
			instantiateAnother = GUILayout.Toggle(instantiateAnother, $"instantiate-{anotherPrefab.name}");
		else
			GUILayout.Label("Assign @anotherPrefab", GUI.skin.box);
		GUILayout.EndArea();
	}
	#endregion

	#region Imp
	bool instantiateAnother;
	#endregion
}
