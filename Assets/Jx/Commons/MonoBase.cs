using UnityEngine;
/*******************************************************
*	filename:	MonoBase.cs
*	created:  	9/29/2025
*	author:		jzq
********************************************************/
public abstract class MonoBase : MonoBehaviour
{
	#region Unity
	protected virtual void Start()
	{
		Init();
	}
	protected void OnApplicationQuit()
	{
		DeInit();
	}
	protected void OnDestroy()
	{
		DeInit();
	}
	#endregion

	#region Pub
	public void Init()
	{
		if (!Inited)
		{
			Inited = true;
			OnInit();
		}
	}

	public bool Inited
	{
		get; private set;
	}
	#endregion

	#region Imp
	protected bool Destroying
	{
		get; private set;
	}
	void DeInit()
	{
		if (this && !Destroying)
		{
			Destroying = true;
			DoDeInit();
		}
	}
	#endregion

	#region Overiable
	protected virtual void OnInit()
	{
	}
	protected virtual void DoDeInit()
	{
	}
	#endregion
}