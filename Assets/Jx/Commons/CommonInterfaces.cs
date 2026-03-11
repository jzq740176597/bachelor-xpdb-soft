using UnityEngine;
/********************************************************************
	created:	2024/03/14 [12:21]
	filename: 	CommonInterfaces.cs
	author:		jzq
*********************************************************************/
public interface IUtilHolder
{
	T GetUtil<T>() where T : MonoBehaviour;
}
