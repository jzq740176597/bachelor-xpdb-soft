using UnityEngine;
using UnityEngine.Events;
/********************************************************************
	created:	2022/06/09
	filename: 	UnityEventDefines
	author:		jzq
*********************************************************************/
[System.Serializable]
public class UnityBoolEvent : UnityEvent<bool>
{
}
[System.Serializable]
public class UnityFloatEvent : UnityEvent<float>
{
}
[System.Serializable]
public class UnityIntEvent : UnityEvent<int>
{
}
[System.Serializable]
public class UnityVec3Event : UnityEvent<Vector3>
{
}
//  [1/11/2024 jzq]
[System.Serializable]
public class UnityIntBoolEvent : UnityEvent<int, bool>
{
}