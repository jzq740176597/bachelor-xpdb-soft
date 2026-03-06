/********************************************************************
	created:	2024/4/10 9:49
	filename: 	WarpLogExtension.cs
	author:		HR
*********************************************************************/
public static class WarpLogExtension
{
	public static string WrapLogColor(this string msg, string colorName = "yellow")
	{
		if (string.IsNullOrEmpty(msg))
			return msg;
#if UNITY_EDITOR
		return string.Format($"<color={colorName}>{msg}</color>");
#else
        return msg;
#endif
	}
	public static string WrapLogBold(this string msg)
	{
		if (string.IsNullOrEmpty(msg))
			return msg;
#if UNITY_EDITOR
		return string.Format($"<b>{msg}</b>");
#else
        return msg;
#endif
	}
}
