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
[System.Serializable]
public class UnityStringEvent : UnityEvent<string>
{
}
// [9/10/2024 jzq]
[System.Serializable]
public class UnityTrEvent : UnityEvent<Transform>
{
}
[System.Serializable]
public class UnityMeshEvent : UnityEvent<Mesh>
{
}
[System.Serializable]
public class UnityTrsEvent : UnityEvent<Transform[]>
{
}