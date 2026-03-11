using UnityEngine;

[System.Reflection.Obfuscation(Exclude = true)]
public static class FloatCompareExtension
{
	public static bool ZeroVector(this Vector3 v , float error = EPS)
	{
		///while v == 0, LessThan() return false [3/4/2021 jzq]
		/*
		 * Use *magnitude* rather than sqrMagnitude AS error^2 MAY *EXCEED* float-Eps / Significant-number(<1E-7) [3/1/2023 jzq]	
		 */
		return !v.magnitude.GreatThan(0 , error);
	}
	public static bool Between01(this float v , bool include , float error = EPS)
	{
		// 		if (v > 0 && v < 1) /// return true for nearEqual case on !include <eg: 6E-7>
		// 			return true;
		return include ? (v.GreatEqual(0 , error) && v.LessEqual(1 , error)) : (v.GreatThan(0 , error) && v.LessThan(1 , error));
	}
	const float EPS = 1E-6f;
	public static bool NearEqual(this float f , float f2 , float error = EPS)
	{
		return Mathf.Abs(f - f2) <= error;
	}
	public static bool GreatThan(this float f , float f2 , float error = EPS)
	{
		return !NearEqual(f , f2 , error) && f > f2;
	}
	public static bool LessThan(this float f , float f2 , float error = EPS)
	{
		return !NearEqual(f , f2 , error) && f < f2;
	}
	public static bool GreatEqual(this float f , float f2 , float error = EPS)
	{
		return f > f2 || NearEqual(f , f2 , error);
	}
	public static bool LessEqual(this float f , float f2 , float error = EPS)
	{
		return f < f2 || NearEqual(f , f2 , error);
	}
	#region Vectors
	public static bool IsZero(this Vector3 v , float error = EPS)
	{
		return v.ZeroVector(error);
	}
	#endregion
}
