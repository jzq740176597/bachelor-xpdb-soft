using UnityEngine;
/********************************************************************
	fileName:	VectorEx.cs
	created:	2022/03/08 18:00
	author:		jzq
*********************************************************************/
public static class VectorEx
{
	public static Vector3 MulVec(this Vector3 v, Vector3 v1)
	{
		v[0] *= v1[0];
		v[1] *= v1[1];
		v[2] *= v1[2];
		return v;
	}
	public static Vector3 DivVec(this Vector3 v, Vector3 v1)
	{
		v[0] /= v1[0] == 0 ? 0 : v1[0];
		v[1] /= v1[1] == 0 ? 0 : v1[1];
		v[2] /= v1[2] == 0 ? 0 : v1[2];
		return v;
	}
}
