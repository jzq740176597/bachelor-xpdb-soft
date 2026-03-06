using UnityEngine;
using System.Collections;
//by jzq 2016-8
public static class DelayInvokeExtension
{
	public static Coroutine DelayInvokeWaitNextFrame(this MonoBehaviour mono, System.Action ac)
	{
		return mono.StartCoroutine(_DelayInvoke(ac));
	}
	public static Coroutine DelayInvokeWaitAnim(this MonoBehaviour mono, Animation anim, string animName, System.Action ac)
	{
		return mono.StartCoroutine(_WaitAnim(anim, animName, ac));
	}
	public static Coroutine DelayInvokeWaitSec(this MonoBehaviour mono, float time, System.Action ac)
	{
		return mono.StartCoroutine(_DelayInvoke(time, ac));
	}
	public static Coroutine DelayInvokeWaitCoroutine(this MonoBehaviour mono, IEnumerator co, System.Action ac)
	{
		return mono.StartCoroutine(_WaitCoroutine(mono, co, ac));
	}
	public static Coroutine DelayInvokeWaitEndOfFrame(this MonoBehaviour mono, System.Action ac)
	{
		return mono.StartCoroutine(_DelayInvokeEndOfFrame(ac));
	}
	#region IMP
	static IEnumerator _DelayInvoke(float time, System.Action ac)
	{
		yield return new WaitForSeconds(time);
		if (ac != null)
			ac();
	}
	static IEnumerator _DelayInvoke(System.Action ac)
	{
		yield return null;//next frame
		if (ac != null)
			ac();
	}
	static IEnumerator _DelayInvokeEndOfFrame(System.Action ac)
	{
		yield return new WaitForEndOfFrame();
		if (ac != null)
			ac();
	}
	static IEnumerator _WaitAnim(Animation anim, string animName, System.Action ac = null)
	{
		if (anim[animName] == null)
			yield break;
		anim.Play(animName);
		yield return new WaitForSeconds(anim[animName].length / anim[animName].speed);
		if (ac != null)
			ac();
	}
	static IEnumerator _WaitCoroutine(MonoBehaviour mono, IEnumerator co, System.Action ac)
	{
		yield return mono.StartCoroutine(co);
		if (ac != null)
			ac();
	}
	#endregion
}