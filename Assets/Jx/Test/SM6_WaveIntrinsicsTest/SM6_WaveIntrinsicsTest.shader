Shader "Unlit/SM6_WaveIntrinsics"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" { }
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		LOD 100

		Pass
		{
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog

			//Unity 2022.3 requirements for SM 6.0
			#pragma target 5.0
			#pragma use_dxc
			//#pragma require wavebasic //seem no-acquired

			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				UNITY_FOG_COORDS(1)
				float4 vertex : SV_POSITION;
			};

			sampler2D _MainTex;
			float4 _MainTex_ST;

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				// WaveGetLaneCount() is an SM 6.0 intrinsic.
				// If it compiles and runs, SM 6.0 is active.
				uint lanes = WaveGetLaneCount();
				fixed4 col;
				if (lanes > 0)
					col = fixed4(0, 1, 0, 1); // Green = Success
				else
					col = fixed4(1, 0, 0, 1); // Red = Fail
				UNITY_APPLY_FOG(i.fogCoord, col);
				return col;
			}
			ENDHLSL
		}
	}
}