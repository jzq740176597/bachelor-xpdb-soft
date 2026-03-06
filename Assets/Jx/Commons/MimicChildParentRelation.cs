using System.Collections;
using UnityEngine;
/********************************************************************
	created:	2017/03/14
	author:		jzq
	How:	attach this to child
*********************************************************************/
[DisallowMultipleComponent]
public sealed class MimicChildParentRelation : MonoBehaviour
{
	#region Inspector
	[SerializeField]
	Transform _parentTransform;
	[SerializeField]
	bool syncLocal;
	[SerializeField]
	bool keepOffset;
	// If true, will attempt to scale the child accurately as the parent scales
	// Will not be accurate if starting rotations are different or irregular
	// Experimental
	[SerializeField]
	bool attemptUseScale;
	#endregion

	#region Pub
	public Transform parentTransform
	{
		get
		{
			return _parentTransform;
		}
		set
		{
			if (_parentTransform != value)
			{
				_parentTransform = value;
				Init();
			}
		}
	}
	#endregion

	#region Field
	Vector3 startParentPosition;
	Quaternion startParentRotationQ;
	Vector3 startParentScale;

	Vector3 startChildPosition;
	Quaternion startChildRotationQ;
	Vector3 startChildScale;

	Matrix4x4 parentMatrix;
	#endregion

	#region Imp
	void Init()
	{
		if (parentTransform == null)
		{
			Debug.Log("MimicChildParentRelation's parentTransform is null while OnEnable()");
			return;
		}
		startParentPosition = !syncLocal ? parentTransform.position : parentTransform.localPosition;
		startParentRotationQ = !syncLocal ? parentTransform.rotation : parentTransform.localRotation;
		startParentScale = !syncLocal ? parentTransform.lossyScale : parentTransform.localScale;

		startChildPosition = !keepOffset ? startParentPosition : !syncLocal ? transform.position : transform.localPosition;
		startChildRotationQ = !keepOffset ? startParentRotationQ : !syncLocal ? transform.rotation : transform.localRotation;
		startChildScale = (!keepOffset && attemptUseScale) ? startParentScale : !syncLocal ? transform.lossyScale : transform.localScale;

		// Keeps child position from being modified at the start by the parent's initial transform
		startChildPosition = DivideVectors(Quaternion.Inverse(parentTransform.rotation) * (startChildPosition - startParentPosition), startParentScale);
		DoSync();
	}
	static Vector3 DivideVectors(Vector3 num, Vector3 den)
	{
		return new Vector3(num.x / den.x, num.y / den.y, num.z / den.z);
	}
	void DoSync()
	{
		if (!syncLocal)
		{
			parentMatrix = Matrix4x4.TRS(parentTransform.position, parentTransform.rotation, parentTransform.lossyScale);

			transform.position = parentMatrix.MultiplyPoint3x4(startChildPosition);

			transform.rotation = (parentTransform.rotation * Quaternion.Inverse(startParentRotationQ)) * startChildRotationQ;
			// Incorrect scale code; it scales the child locally not gloabally; Might work in some cases, but will be inaccurate in others
			if (attemptUseScale)
				transform.localScale = Vector3.Scale(startChildScale, DivideVectors(parentTransform.lossyScale, startParentScale));
		}
		else
		{
			transform.localPosition = parentTransform.localPosition;
			transform.localRotation = parentTransform.localRotation;
			if (attemptUseScale)
				transform.localScale = parentTransform.localScale;
		}
	}
	IEnumerator CoDelayEndOfFrame(System.Action ac)
	{
		yield return new WaitForEndOfFrame();
		if (ac != null)
			ac();
	}
	#endregion

	#region Unity
	void OnEnable() //for Multiple init chance  -by jzq
	{
		Init();
	}

	void LateUpdate()
	{
		// [12/12/2023 jzq]
		if (parentTransform == null || (Application.isPlaying && !parentTransform.hasChanged))
			return;
		//To void Effect other Check this : Like TransformChangeMonitor [7/26/2024 jzq]
		//this.DelayInvokeWaitEndOfFrame();
		StartCoroutine(CoDelayEndOfFrame(() => parentTransform.hasChanged = false));
		DoSync();
	}
	#endregion
}