using UnityEngine;
/*
	RMB - Orbit
	ScrollWheel - Zoom In
	[Note]: not works very well on target's POS CHANGED CONTANTLY!
	*
	[**Refactoring & Support Touch] [2/25/2025 jzq]
	 *
*/
public sealed class MouseOrbitImproved : MonoBehaviour
{
	#region Inspector
	[SerializeField, Header("Target-Acquired")]
	public Transform Target;
	[SerializeField, Min(0)] float xSpeed = 15f;
	[SerializeField, Min(0)] float ySpeed = 15f;
	[SerializeField, Min(0)] float zoomSpeed = 1f;
	[Header("YLimit")]
	[SerializeField] float yMinLimit = -20f;
	[SerializeField] float yMaxLimit = 80f;
	[Header("Dist")]
	[SerializeField, Min(0)] float distance = 5.0f;
	[SerializeField, Min(0)] float distanceMin = .5f;
	[SerializeField, Min(0)] float distanceMax = 15f;
	[Header("Larger to quick-stop")]
	[SerializeField, Min(0)] float smoothScaler = 3f;
	#endregion

	#region Imp
	float rotationYAxis, rotationXAxis, velocityX, velocityY;
	// [4/1/2025 jzq]
	float velocityZoom, touchZoomDelta;
	Vector2 touchXyDelta;
	void ClearInput()
	{
		if (touch2InitDist != null)
		{
			touch2InitDist = null;
			touchZoomDelta = 0;
		}
	}
	void ClearVelocity()
	{
		velocityX = 0;
		velocityY = 0;
		velocityZoom = 0;
	}
	static float ClampAngle(float angle, float min, float max)
	{
		if (angle < -360F)
			angle += 360F;
		if (angle > 360F)
			angle -= 360F;
		return Mathf.Clamp(angle, min, max);
	}
	float? touch2InitDist;
	bool CheckInteractive()
	{
		if (ShouldInteractive == null)
			return true;
		foreach (DelCheckInteractive del in ShouldInteractive.GetInvocationList())
		{
			if (del())
				return true;
		}
		return false;
	}
	#endregion

	#region Pub
	public delegate bool DelCheckInteractive();
	public event DelCheckInteractive ShouldInteractive;
	public void SetDeltaRotXY(float x, float y)
	{
		velocityX += x;
		velocityY += y;
	}
	public void SetDeltaDistance(float delta)
	{
		velocityZoom += delta;
	}
	public struct OrbitParams
	{
		public float rotationXAxis, rotationYAxis;
		public float distance;
	}
	public OrbitParams GetCurOrbitParams()
	{
		return new OrbitParams()
		{
			rotationXAxis = rotationXAxis,
			rotationYAxis = rotationYAxis,
			distance = distance,
		};
	}
	//instantly
	public void SetCurOrbitParams(OrbitParams orbitParams)
	{
		ClearInput();
		ClearVelocity();
		rotationXAxis = orbitParams.rotationXAxis;
		rotationYAxis = orbitParams.rotationYAxis;
		distance = orbitParams.distance;
	}
	#endregion

	#region Unity
	void Start()
	{
		Vector3 angles = transform.eulerAngles;
		rotationYAxis = angles.y;
		rotationXAxis = angles.x;
		// Make the rigid body not change rotation
		if (TryGetComponent<Rigidbody>(out var r))
			r.freezeRotation = true;
	}
	void LateUpdate()
	{
		if (!Target)
		{
			Debug.LogError($"{nameof(MouseOrbitImproved)} @{name} Target == null");
			enabled = false;
			return;
		}
		//Collect Input (Should not Guard by CheckInteractive()) [4/1/2025 jzq]
		if (Input.touchCount >= 2)
		{
			var dist = (Input.GetTouch(0).position - Input.GetTouch(1).position).magnitude;
			if (touch2InitDist == null)
				touch2InitDist = dist;
			else
				touchZoomDelta = touch2InitDist.Value - dist;
		}
		else
		{
			if (Input.touchCount > 0)
				touchXyDelta = Input.GetTouch(0).deltaPosition;
			ClearInput();
		}
		if (CheckInteractive())
		{
			const float MOUSE_SCALER = .02f;
			if (Input.GetMouseButton(1))
				SetDeltaRotXY(xSpeed * Input.GetAxis("Mouse X") * distance * MOUSE_SCALER, ySpeed * Input.GetAxis("Mouse Y") * MOUSE_SCALER);
			SetDeltaDistance(-Input.GetAxis("Mouse ScrollWheel") * zoomSpeed);
			//touch
			if (Input.touchCount > 0)
			{
				const float TOUCH_XY_SCALER = .001f;
				const float TOUCH_ZOOM_SCALER = TOUCH_XY_SCALER;
				if (touch2InitDist != null)
					SetDeltaDistance(touchZoomDelta * zoomSpeed * TOUCH_ZOOM_SCALER);
				else
					SetDeltaRotXY(xSpeed * touchXyDelta.x * distance * TOUCH_XY_SCALER, ySpeed * touchXyDelta.y * TOUCH_XY_SCALER);
			}

		}
		//damping
		rotationYAxis += velocityX;
		rotationXAxis -= velocityY;
		rotationXAxis = ClampAngle(rotationXAxis, yMinLimit, yMaxLimit);
		transform.rotation = Quaternion.Euler(rotationXAxis, rotationYAxis, 0);
		// [4/1/2025 jzq]
		{
			distance = Mathf.Clamp(distance + velocityZoom, distanceMin, distanceMax);
			//RaycastHit hit;
			//if (Physics.Linecast(Target.position, transform.position, out hit))
			//{
			//	distance -= hit.distance;
			//}
			Vector3 negDistance = new Vector3(0.0f, 0.0f, -distance);
			Vector3 position = transform.rotation * negDistance + Target.position;
			transform.position = position;
			//
			velocityZoom = Mathf.Lerp(velocityZoom, 0, Time.deltaTime * smoothScaler * 5);
		}
		velocityX = Mathf.Lerp(velocityX, 0, Time.deltaTime * smoothScaler);
		velocityY = Mathf.Lerp(velocityY, 0, Time.deltaTime * smoothScaler);
	}
	#endregion
}