Shader "My/CullDim"
{
	Properties
	{
		_Color ("Color", Color) = (1, 1, 1, 1)
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" "Queue"="Geometry"}
		//Alpha
		Blend SrcAlpha OneMinusSrcAlpha
		pass
		{
			CGPROGRAM
			#include "UnityCG.cginc"

			#pragma vertex vert
			#pragma fragment frag

			fixed4 _Color;

			float4 vert(appdata_base v) : SV_POSITION
			{
				return UnityObjectToClipPos(v.vertex);
			}
			fixed4 frag() : SV_TARGET
			{
				return _Color * .8;
			}
			ENDCG
		}
		//extra pass
		pass
		{
			ZWrite Off
			ZTest Greater

			CGPROGRAM
			#include "UnityCG.cginc"

			#pragma vertex vert
			#pragma fragment frag

			fixed4 _Color;

			float4 vert(appdata_base v) : SV_POSITION
			{
				return UnityObjectToClipPos(v.vertex);
			}
			fixed4 frag() : SV_TARGET
			{
				return _Color * .4;
			}
			ENDCG
		}
	}
	FallBack "Diffuse"
}