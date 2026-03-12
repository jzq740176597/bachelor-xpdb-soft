using UnityEngine;
/********************************************************************
	ext:	2024/02/21 [11:46]
	purpose: 	inherited from AimCamera
	author:		jzq
*********************************************************************/
/*
 *	LMB (or RMB) - Orbit
 */
[DisallowMultipleComponent, RequireComponent(typeof(Camera))]
public sealed class AimCameraMereOrbit : MonoBehaviour
{
	#region Inspector
	[SerializeField, Header("[opt]")] Transform target;
	[SerializeField] bool leftMouseBtn = true;
	[SerializeField] bool showGui;
	[SerializeField] public float ZoomSpeed = 10;
	#endregion

	#region Imp
	Vector3 f0Dir = Vector3.zero;
	Vector3 upVal = Vector3.zero;
	float zoomDistance = 5;
	float theta = 0.0F;
	float fai = 0.0F;
	//  [2/21/2024 jzq]
	Transform targetPivot;
	bool valid => targetPivot;
	Camera cam => GetComponent<Camera>();
	void SetZoomDist(float s)
	{
		//const float MIN_DIST = .1f;
		zoomDistance = Mathf.Max(cam.nearClipPlane, s);
	}
	void UpdateTr()
	{
		upVal.z = zoomDistance * Mathf.Cos(theta) * Mathf.Sin(fai + Mathf.PI / 2);
		upVal.x = zoomDistance * Mathf.Sin(theta) * Mathf.Sin(fai + Mathf.PI / 2);
		upVal.y = zoomDistance * Mathf.Cos(fai + Mathf.PI / 2);

		transform.position = upVal;
		transform.position += targetPivot.position;
		/*(Camera.main ? Camera.main.transform :*/
		cam.transform.LookAt(targetPivot.position);
	}
	#endregion

	#region Unity
	void Start()
	{
		if (!valid)
			SetTarget(target);
		else
			UpdateTr();
	}
	void Update()
	{
		if (!valid)
			return;
		float scroll = Input.mouseScrollDelta.y;
		if (Input.GetMouseButton(leftMouseBtn ? 0 : 1) || !scroll.NearEqual(0))
		{
			SetZoomDist(zoomDistance - scroll * ZoomSpeed * Time.deltaTime);
			//
			f0Dir = new Vector3(Input.GetAxis("Mouse X") * 5.0F, -Input.GetAxis("Mouse Y") * 5.0F, 0);
			theta += Mathf.Deg2Rad * f0Dir.x * 1;
			fai += -Mathf.Deg2Rad * f0Dir.y * 1;
			//
			UpdateTr();
		}
	}
	void OnGUI()
	{
		if (!showGui)
			return;
		GUIStyle myTxtStyle = new GUIStyle(GUI.skin.box);
		myTxtStyle.fontSize = 20;
		myTxtStyle.normal.textColor = Color.red;
		GUILayout.Box($"Orbit with {(leftMouseBtn ? "Left" : "Right")} Mouse Button", myTxtStyle);
	}
	#endregion

	#region Pub
	public void SetTarget(Transform t, float? dist = null)
	{
		if (targetPivot == null)
		{
			targetPivot = new GameObject("AimCameraPivot").transform;
			targetPivot.gameObject.hideFlags = HideFlags.HideAndDontSave;
		}
		target = t;
		targetPivot.position = target ? target.position : Vector3.zero; //sync to target
		SetZoomDist(dist ?? Vector3.Distance(transform.position, targetPivot.position));
	}
	#endregion
}
