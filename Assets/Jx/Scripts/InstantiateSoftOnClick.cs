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
	float minHeight = 5;
	[SerializeField]
	float maxHeight = 10;
	#endregion

	#region Pub
	public void DoInstantiate(Vector3 p)
	{
		p.y = Random.Range(minHeight, maxHeight);
		Debug.Log($"Instantiate pos = {p}");
		var soft = Instantiate(softPrefab, p, Quaternion.identity).GetComponent<SoftBodyComponent>();
		//soft.Init()
	}
	#endregion
}
