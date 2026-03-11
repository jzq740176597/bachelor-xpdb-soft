//#define STEAM_VR
using UnityEngine;
using UnityEngine.EventSystems;
#if STEAM_VR
using Valve.VR.InteractionSystem;
#endif
/********************************************************************
	Ex:	2023/03/15 [15:30]
	filename: 	DragDropByMouseNewEvent.cs
	author:		jzq
*********************************************************************/
[DisallowMultipleComponent]
public class DragDropByMouseNewEvent : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
	public enum DragLock
	{
		none,
		lockY,
	}
	#region Inspectors
	[Header("Optional"), SerializeField]
	Camera specCam;
	[SerializeField]
	Color highColor = Color.yellow;
	[SerializeField]
	public DragLock dragLock;
	[SerializeField]
	public UnityBoolEvent onDragToggled = new UnityBoolEvent(); // [9/6/2024 jzq]
	[SerializeField]
	public UnityTrEvent onDraging = new UnityTrEvent();
	#endregion

	#region Fields
	bool dragging;
	Vector3 screenPoint;
	Vector3 offset;
	Color? colorNormal;
	Color colorEmissNormal;
	protected System.Func<bool> dragingLegalFilter; // [11/24/2024 jzq]
	#endregion //Fields

	#region IMP
	void ChangeColorScale(bool highlight)
	{
		if (highColor == Color.clear)
			return;
		var rd = GetComponentInParent<Renderer>();
		if (!rd)
			rd = GetComponentInChildren<Renderer>();
		var mat = rd.material;
		bool Emiss = mat.HasProperty("_EmissionColor");
		if (colorNormal == null)
		{
			colorNormal = mat.color;
			if (Emiss)
				colorEmissNormal = mat.GetColor("_EmissionColor");
			//scaleNorm = transform.localScale;
		}
		mat.color = !highlight ? colorNormal.Value : highColor;
		if (Emiss)
			mat.SetColor("_EmissionColor", !highlight ? colorEmissNormal : highColor);
		//const float scaler = 1.5f;
		//transform.localScale = !highlight ? scaleNorm : scaleNorm * scaler;
	}
	void BeginDrag()
	{
		dragging = true;
		//calc the postion
		screenPoint = specCam.WorldToScreenPoint(basePt);
		offset = basePt - specCam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, screenPoint.z));
		ChangeColorScale(true);
		// [9/9/2024 jzq]
		onDragToggled?.Invoke(true);
	}

	void OnDraging()
	{
#if STEAM_VR
        if(!attached)
#endif
		{
			Vector3 cursorPoint = new Vector3(Input.mousePosition.x, Input.mousePosition.y, screenPoint.z);
			Vector3 cursorPosition = specCam.ScreenToWorldPoint(cursorPoint) + offset;
			//  [6/14/2019 jzq]
			var off = cursorPosition - basePt;
			FilterDragingPosByLock_S(dragLock, ref off);
			basePt += off;
		}
		// [9/9/2024 jzq]
		onDraging?.Invoke(transform);
		//else
		//	Debug.Log($"[Note] onDraging : BEEN skipped AS dragingLegalFilter() return false".WrapLogColor());
		//
		void FilterDragingPosByLock_S(DragLock lockTrans, ref Vector3 vecOff)
		{
			if ((lockTrans & DragLock.lockY) > 0)
				vecOff.y = 0;
			// 		if ((lockTrans & LockEn.MoveY) > 0)
			// 		if ((lockTrans & LockEn.MoveZ) > 0)
			// 			vecOff.z = 0;
		}
	}
	#endregion//IMP

	#region Overriable
	protected virtual void Start()
	{
		if (!specCam)
			specCam = Camera.main;
	}
	//false for cancel
	public virtual bool OnStartDrag(PointerEventData eventData)
	{
		return eventData.button == PointerEventData.InputButton.Left;
	}

	//  [6/14/2019 jzq]
	public virtual Vector3 basePt
	{
		get
		{
			return transform.position;
		}
		set
		{
			transform.position = value;
		}
	}

	public virtual void OnEndDrag(PointerEventData eventData)
	{
		if (!dragging)
			return;
		dragging = false;
		if (colorNormal != null)
			ChangeColorScale(false);
		onDragToggled?.Invoke(false);
	}
	#endregion

	#region IDragHandler_Interface
	void IBeginDragHandler.OnBeginDrag(PointerEventData eventData)
	{
		if (!OnStartDrag(eventData))
		{
			Debug.Log("Cancel the Drag operation");
			eventData.pointerDrag = null;
			return;
		}
		BeginDrag();
	}
	void IDragHandler.OnDrag(PointerEventData eventData)
	{
		if (dragging)
			OnDraging();
	}
	#endregion
#if STEAM_VR
    bool attached;
    bool validForHand => enabled;
    void HandHoverUpdate(Hand hand)
    {
        if (!validForHand)
            return;
        if (!attached)
        {
            GrabTypes startingGrabType = hand.GetGrabStarting();
            if (startingGrabType != GrabTypes.None)
            {
                var attachmentFlags = Hand.AttachmentFlags.ParentToHand | Hand.AttachmentFlags.DetachFromOtherHand | Hand.AttachmentFlags.TurnOnKinematic;
                hand.AttachObject(gameObject, startingGrabType, attachmentFlags /*, attachmentOffset*/ );
                hand.HideGrabHint();
            }
        }
    }
    void OnAttachedToHand(Hand hand)
    {
        if (!validForHand)
            return;
        Debug.Log($"{name} : OnAttachedToHand");
        attached = true;
        hand.HoverLock(null);
        BeginDrag();
    }
    void OnDetachedFromHand(Hand hand)
	{
        if (!validForHand)
            return;
        Debug.Log($"{name} : OnDetachedFromHand");
        attached = false;
        hand.HoverUnlock(null);
        CancelDragging();
	}
    void HandAttachedUpdate(Hand hand)
    {
        if (!validForHand || !attached)
            return;
        if (hand.IsGrabEnding(gameObject))
        {
            hand.DetachObject(gameObject, /*restoreOriginalParent*/false);
            return;
        }
        OnDraging();
    }
#endif
	#region Pub
	public void DisableHighLight()
	{
		highColor = Color.clear;
	}
	public void CancelDragging()
	{
		if (dragging)
			OnEndDrag(null);
	}
	#endregion
}
