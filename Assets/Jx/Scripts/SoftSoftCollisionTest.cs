using XPBD;
/********************************************************************
	created:	2026/03/07
	filename: 	SoftSoftCollisionTest
	author:		jzq
*********************************************************************/
public sealed class SoftSoftCollisionTest : MonoBase
{
	#region Unity
	protected override void OnInit()
	{
		base.OnInit();
		//wait for softBody REGISTER
		this.DelayInvokeWaitEndOfFrame(() => SoftBodySimulationManager.Instance.RebuildLayerPairs());
	}
	#endregion

}
