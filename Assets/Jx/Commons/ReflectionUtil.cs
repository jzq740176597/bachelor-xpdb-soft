using System.Reflection;
/********************************************************************
	created:	2024/03/13 [17:15]
	filename: 	ReflectionUtil.cs
	author:		jzq
*********************************************************************/
public static class ReflectionUtil
{
	public static void CopyFieldsTo<T>(this T s, T t) where T : class
	{
		var fields = typeof(T).GetFields(BindingFlags.Public |
			   BindingFlags.NonPublic | BindingFlags.Instance);
		foreach (var f in fields)
			f.SetValue(t, f.GetValue(s));
	}
}
