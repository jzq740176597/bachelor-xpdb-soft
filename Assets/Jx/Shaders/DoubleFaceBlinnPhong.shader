/*--by jzq-2024*/
Shader "My/DoubleFaceBlinnPhong"
{
	Properties
	{
		_Color ("Color", Color) = (.6, .6, .6, 1)
		_MainTex ("Texture", 2D) = "white" { }
		[Toggle] _dorsal_same("Dorsal_Same_With_Front", integer) = 0
		_ColorDorsal ("ColorDorsal", Color) = (.5, 0, 0, 1)
	}
	SubShader
	{
		Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
		Cull Off

		CGINCLUDE
		#pragma vertex vert
		#pragma fragment frag
		#pragma shader_feature_local _DORSAL_SAME_ON
		//#include "UnityCG.cginc"
		#include "Lighting.cginc" //indirect inc "UnityCG.cginc"
		#include "AutoLight.cginc" //inc SHADOW_COORDS
		
		struct appdata
		{
			float4 vertex : POSITION;
			float2 uv : TEXCOORD0;
			float3 normal : NORMAL;
		};

		struct v2f
		{
			float2 uv : TEXCOORD0;
			float3 wpos : TEXCOORD1;
			float3 wnorm : TEXCOORD2;
			UNITY_SHADOW_COORDS(3)
			float4 pos : SV_POSITION;
		};

		sampler2D _MainTex;
		float4 _MainTex_ST;
		fixed4 _Color, _ColorDorsal;
		v2f vert(appdata v)
		{
			v2f o;
			UNITY_INITIALIZE_OUTPUT(v2f, o)
			o.pos = UnityObjectToClipPos(v.vertex);
			o.uv = TRANSFORM_TEX(v.uv, _MainTex);
			//UNITY_TRANSFER_FOG(o,o.vertex);
			TRANSFER_SHADOW(o);
			o.wpos = mul(unity_ObjectToWorld, v.vertex);
			o.wnorm = UnityObjectToWorldNormal(v.normal);
			return o;
		}
		
		fixed4 frag(v2f i) : SV_Target
		{
			fixed4 col = tex2D(_MainTex, i.uv) * _Color;
			//UNITY_APPLY_FOG(i.fogCoord, col);

			/*---diffuse + specular + emission + ambient---*/
			float3 vdir = normalize(UnityWorldSpaceViewDir(i.wpos));
			float3 ldir = normalize(UnityWorldSpaceLightDir(i.wpos));
			//surf
			float3 normal = normalize(i.wnorm);
			fixed4 albedo, diffuse;
#ifndef _DORSAL_SAME_ON
			bool back = dot(normal, vdir) < 0;
			albedo = _LightColor0 * (back ? _ColorDorsal : col);
			diffuse = albedo * (back ? abs(dot(ldir, normal)) : saturate(dot(ldir, normal)));
#else
			albedo = _LightColor0 * col;
			diffuse = albedo * saturate(abs(dot(ldir, normal)));
#endif
			//
			//blinn-model ([n] <normal> * [h] <half>) => to avoid to calc the reflectDir
			//float3 halfDir = normalize(ldir + vdir); //*acquired*
			fixed4 specular = 0; //_LightColor0 * _SpecClr * pow(saturate(dot(halfDir, normal)), 60);
			//
			fixed4 ambient = fixed4(UNITY_LIGHTMODEL_AMBIENT.xyz * albedo, 1); //fixed4(ShadeSH9(half4(normal, 0)), 1);

			UNITY_LIGHT_ATTENUATION(atten, i, i.wpos);

			return ambient + (diffuse + specular) * atten;
		}
		ENDCG
		Pass
		{
			Tags { "LightMode" = "ForwardBase" }
			CGPROGRAM
			#pragma multi_compile_fwdbase
			ENDCG
		}
		Pass
		{
			Tags { "LightMode" = "ForwardAdd" }
			//Blend One One
			BlendOp max

			CGPROGRAM
			#pragma multi_compile_fwdadd
			ENDCG
		}
	}
	Fallback "Specular"
}