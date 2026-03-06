using System.Collections.Generic;
using UnityEngine;
/********************************************************************
	fileName:	MonoEx.cs
	created:	2022/03/08 18:26
	author:		jzq
*********************************************************************/
public static class MonoEx
{
	public static T GetComponentInDirectChild<T>(this Component cmp, bool includeInactive = false) where T : Component
	{
		foreach (Transform t in cmp.transform)
		{
			var c = t.GetComponent<T>();
			if (c)
			{
				if (!includeInactive && !t.gameObject.activeInHierarchy)
					continue;
				return c;
			}
		}
		return null;
	}
	public static List<T> GetComponentsInSelfDirectChild<T>(this Component cmp, bool includeInactive = false) where T : Component
	{
		var lst = cmp.GetComponentsInDirectChild<T>(includeInactive);
		//check self
		var self = cmp.GetComponent<T>();
		if (self)
			lst.Insert(0, self);
		return lst;
	}
	public static List<T> GetComponentsInDirectChild<T>(this Component cmp, bool includeInactive = false) where T : Component
	{
		var rc = new List<T>();
		foreach (Transform t in cmp.transform)
		{
			var c = t.GetComponent<T>();
			if (c)
			{
				if (!includeInactive && !t.gameObject.activeInHierarchy)
					continue;
				rc.Add(c);
			}
		}
		return rc;
	}
	public static void SetLossyScale(this Transform tr, Vector3 sv)
	{
		if (!tr.parent)
			tr.localScale = sv;
		else
		{
			var gs = tr.lossyScale.DivVec(tr.localScale);
			tr.localScale = sv.DivVec(gs);
		}
	}
	//  [1/2/2024 jzq]
	public static void SafeDestroy(this Component cmp)
	{
		if (cmp)
		{
#if UNITY_EDITOR
			GameObject.DestroyImmediate(cmp.gameObject);
#else
			GameObject.Destroy(cmp.gameObject);
#endif
		}
	}
	//  [1/9/2024 jzq]
	public static void CleanChildren(this Component cmp)
	{
		var t = cmp.transform;
		for (int i = t.childCount - 1; i >= 0; --i)
		{
			var c = t.GetChild(i);
			if (c)
			{
				c.SetParent(null);
				GameObject.Destroy(c.gameObject);
			}
		}
	}
	//  [4/5/2024 jzq]
	public static void ShuffleChildren(this Transform t)
	{
		if (t.childCount < 2)
			return;
		var idxs = new int[t.childCount];
		for (int i = 0; i < idxs.Length; ++i)
			idxs[i] = i;
		idxs.Shuffle2();
		for (int i = 0; i < idxs.Length; ++i)
			t.GetChild(idxs[i]).SetAsFirstSibling();
	}
}

static class RandShuffleUtils
{
	public static void Shuffle2<T>(this IList<T> elements)
	{
		for (int i = elements.Count - 1; i >= 0; i--)
		{
			// Swap element "i" with a random earlier element it (or itself)
			// ... except we don't really need to swap it fully, as we can
			// return it immediately, and afterwards it's irrelevant.
			int swapIndex = Random.Range(0, i + 1);
			//yield return elements[swapIndex];
			var t = elements[swapIndex];
			elements[swapIndex] = elements[i];
			elements[i] = t;
		}
	}
}