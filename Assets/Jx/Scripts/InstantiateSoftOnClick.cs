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
	[SerializeField]
	bool tryAddSoftToCol;
	#endregion

	#region Pub
	public void DoInstantiate(Vector3 p)
	{
		p.y = Random.Range(minHeight, maxHeight);
		Debug.Log($"Instantiate pos = {p}");
		var prefab = instantiateAnother ? anotherPrefab : softPrefab;
		var soft = Instantiate(prefab, p, Quaternion.identity).GetComponent<SoftBodyComponent>();
		//Post : Invoke RebuildLayerPairs() for soft-soft-col [3/13/2026 jzq]
		if (tryAddSoftToCol)
			TryAddSoftToCol(soft);
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

	#region Static
	public static void TryAddSoftToCol(SoftBodyComponent soft)
	{
		soft.Init(); ///Ensure added to manager [3/13/2026 jzq]
		if (soft.SoftCollisionMask != 0)
			SoftBodySimulationManager.Instance.RebuildLayerPairs();
	}
	#endregion
}
