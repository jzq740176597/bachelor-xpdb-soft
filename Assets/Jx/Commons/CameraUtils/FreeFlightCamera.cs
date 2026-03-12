using UnityEngine;
/********************************************************************
	ext:	2024/02/21 [12:27]
	filename: 	FreeFlightCamera.cs
*********************************************************************/
/*
 *	WASD - Move
 *	Q/E - Donw / Up
 *	RMB - Orbit
 */
public class FreeFlightCamera : MonoBehaviour
{
	#region Inspector
	[SerializeField] float speedNormal = 10.0f;
	[SerializeField] float speedFast = 50.0f;

	[SerializeField] float mouseSensitivityX = 5.0f;
	[SerializeField] float mouseSensitivityY = 5.0f;
	#endregion

	#region Imp
	float rotY = 0.0f;
	#endregion

	#region Unity
	void Start()
	{
		var rb = GetComponent<Rigidbody>();
		if (rb)
			rb.freezeRotation = true;
	}

	void Update()
	{
		// rotation        
		if (Input.GetMouseButton(1))
		{
			float rotX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * mouseSensitivityX;
			rotY += Input.GetAxis("Mouse Y") * mouseSensitivityY;
			rotY = Mathf.Clamp(rotY, -89.5f, 89.5f);
			transform.localEulerAngles = new Vector3(-rotY, rotX, 0.0f);
		}

		//if (Input.GetKey(KeyCode.U))
		//	transform.localPosition = new Vector3(0.0f, 3500.0f, 0.0f);

		float speed = Input.GetKey(KeyCode.LeftShift) ? speedFast : speedNormal;
		//Q/E
		if (Input.GetKey(KeyCode.Q))
		{
			Vector3 trans = -Vector3.up * speed * Time.deltaTime;
			transform.localPosition += transform.localRotation * trans;
		}
		if (Input.GetKey(KeyCode.E))
		{
			Vector3 trans = Vector3.up * speed * Time.deltaTime;
			transform.localPosition += transform.localRotation * trans;
		}
		float forward = Input.GetAxis("Vertical");
		float strafe = Input.GetAxis("Horizontal");

		// move forwards/backwards
		if (forward != 0.0f)
		{
			Vector3 trans = new Vector3(0.0f, 0.0f, forward * speed * Time.deltaTime);
			transform.localPosition += transform.localRotation * trans;
		}

		// strafe left/right
		if (strafe != 0.0f)
		{
			Vector3 trans = new Vector3(strafe * speed * Time.deltaTime, 0.0f, 0.0f);
			transform.localPosition += transform.localRotation * trans;
		}
	}
	#endregion
}
