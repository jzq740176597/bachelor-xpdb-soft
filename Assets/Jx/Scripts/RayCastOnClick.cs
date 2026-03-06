using UnityEngine;
/********************************************************************
	created:	2026/03/07
	filename: 	RayCastOnClick
	author:		jzq
*********************************************************************/
public sealed class RayCastOnClick : MonoBehaviour
{
	#region Inspector
	[SerializeField] LayerMask clickableLayers = 1 << 0; // Assign your layers in the Inspector
	[SerializeField] float rayDistance = 100;
	public UnityVec3Event OnHitPt;
	#endregion

	#region Unity
	void Update()
	{
		// Check for left mouse click (0)
		if (Input.GetMouseButtonDown(0))
			PerformRaycast();
	}
	#endregion

	void PerformRaycast()
	{
		// Create a ray from the camera through the mouse position
		Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
		RaycastHit hit;

		// Perform the raycast using the LayerMask
		if (Physics.Raycast(ray, out hit, rayDistance, clickableLayers))
		{
			// Successfully hit something on the correct layer
			Debug.Log("Hit: " + hit.collider.name + " pos: " + hit.point);
			OnHitPt.Invoke(hit.point);
		}
		else
		{
			Debug.Log("Raycast hit nothing on the specified layers.");
		}
	}
}
