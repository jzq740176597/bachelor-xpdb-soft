using System.Reflection;
using UnityEngine;
/********************************************************************
	created:	2023/09/20 [19:00]
	filename: 	DmMonoEx.cs
	author:		jzq
*********************************************************************/
//namespace Dm
public static class DmMonoEx
{
	public static Transform GetOrCreateChild(this Transform p, string n)
	{
		var c = p.Find(n);
		if (!c)
		{
			c = new GameObject(n).transform;
			c.SetParent(p, false);
		}
		return c;
	}
	public static void SetRectTrFull(this RectTransform p)
	{
		p.anchorMin = Vector2.zero;
		p.anchorMax = Vector2.one;
		p.offsetMin = p.offsetMax = Vector2.zero;
	}
	public static void CopyValuesFrom<T>(this T comp, T other) where T : Component
	{
		var type = comp.GetType();
		var othersType = other.GetType();
		if (type != othersType)
		{
			Debug.LogError($"The type \"{type.AssemblyQualifiedName}\" of \"{comp}\" does not match the type \"{othersType.AssemblyQualifiedName}\" of \"{other}\"!");
			return;
		}

		var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Default;
		var pinfos = type.GetProperties(flags);

		foreach (var pinfo in pinfos)
		{
			if (pinfo.CanWrite)
			{
				try
				{
					pinfo.SetValue(comp, pinfo.GetValue(other, null), null);
				}
				catch
				{
					/*
					 * In case of NotImplementedException being thrown.
					 * For some reason specifying that exception didn't seem to catch it,
					 * so I didn't catch anything specific.
					 */
				}
			}
		}
		var finfos = type.GetFields(flags);
		foreach (var finfo in finfos)
			finfo.SetValue(comp, finfo.GetValue(other));
	}
	public static GameObject EnsureChildGo(this Component p, string child)
	{
		var t = p.transform;
		var c = t.Find(child);
		if (!c)
		{
			c = new GameObject(child).transform;
			c.SetParent(t, false);
		}
		return c.gameObject;
	}
	public static GameObject EnsureGo(string name)
	{
		var g = GameObject.Find(name);
		if (!g)
			g = new GameObject(name);
		return g;
	}
	public static void SetLossyScale(this Component t, Vector3 v)
	{
		var tr = t.transform;
		if (!tr.parent)
			tr.localScale = v;
		else
		{
			var ps = tr.parent.lossyScale;
			tr.localScale = new Vector3(v.x / ps.x, v.y / ps.y, v.z / ps.z);
		}
	}
}
