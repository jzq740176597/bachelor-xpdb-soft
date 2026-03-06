// SoftBodyPBR.shader
// Built-in Render Pipeline surface shader for XPBD soft bodies
// Ported from shader.vert + shader.frag (Vulkan GLSL)
//
// Preserves the original PBR model:
//   - Cook-Torrance GGX specular (D, G, F terms)
//   - Schlick fresnel with metallic workflow
//   - PCF shadow map (5×5 kernel, range 2) sampled from _ShadowTex
//   - Tone mapping (Reinhard) + gamma correction
//   - Depth-based fog fade into background
//
// Per-body properties (tint, roughness, metallic) are set via MaterialPropertyBlock
// from SoftBodyComponent.cs, matching the original push constants mechanism.

Shader "XPBD/SoftBodyPBR"
{
	Properties
	{
		_MainTex ("Albedo Texture", 2D) = "white" { }
		_ShadowTex ("Shadow Map", 2D) = "white" { }
		_Tint ("Tint Color", Color) = (1, 0.15, 0.05, 1)
		_Roughness ("Roughness", Range(0, 1)) = 0.5
		_Metallic ("Metallic", Range(0, 1)) = 0.0
		_LightDir ("Light Direction", Vector) = (0, -1, 0, 0)
		_LightIntensity ("Light Intensity", Float) = 2.5
		_Ambient ("Ambient", Float) = 0.05
		_Fresnel ("Fresnel F0", Float) = 0.04
		_ShadowAlpha ("Shadow Alpha", Range(0, 1)) = 0.35
		_FogStart ("Fog Start Dist", Float) = 10.0
		_FogEnd ("Fog End Dist", Float) = 75.0
	}

	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		LOD 200

		Pass
		{
			Name "ForwardBase"
			Tags { "LightMode" = "ForwardBase" }

			Cull Back
			ZWrite On
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex   vert
			#pragma fragment frag
			#pragma target   5.0

			#include "UnityCG.cginc"

			// ─── Uniforms ──────────────────────────────────────────────────
			sampler2D _MainTex;
			sampler2D _ShadowTex;

			float4 _Tint;
			float _Roughness;
			float _Metallic;
			float4 _LightDir;        // xyz = direction (world space, normalised)
			float _LightIntensity;
			float _Ambient;
			float _Fresnel;
			float _ShadowAlpha;
			float _FogStart;
			float _FogEnd;

			// Light-space matrix — set by SoftBodySimulationManager.cs each frame
			float4x4 _LightMatrix;

			// ─── Vertex input / output ─────────────────────────────────────
			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float2 uv : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct v2f
			{
				float4 clipPos : SV_POSITION;
				float3 worldPos : TEXCOORD0;
				float4 lightPos : TEXCOORD1; // light-space position for shadow
				float3 worldNormal : TEXCOORD2;
				float2 uv : TEXCOORD3;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			// ─── PBR helper functions (identical to GLSL originals) ────────
			#define PI 3.14159265359

			float DistributionGGX(float3 N, float3 H, float roughness)
			{
				float a2 = roughness * roughness;
				float NdotH = max(dot(N, H), 0.0);
				float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
				denom = PI * denom * denom;
				return a2 / denom;
			}

			float GeometrySchlickGGX(float NdotV, float roughness)
			{
				float r = roughness + 1.0;
				float k = (r * r) / 8.0;
				return NdotV / (NdotV * (1.0 - k) + k);
			}

			float GeometrySmith(float3 N, float3 V, float3 L, float roughness)
			{
				return GeometrySchlickGGX(max(dot(N, L), 0.0), roughness) *
				GeometrySchlickGGX(max(dot(N, V), 0.0), roughness);
			}

			float3 FresnelSchlick(float cosTheta, float3 F0)
			{
				return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
			}

			// PCF shadow sampling (5×5 kernel, range = 2, matching original)
			float GetShadow(float4 shadowCoord, float2 offset, float3 normal)
			{
				float2 uv = float2(
					0.5 * shadowCoord.x + 0.5,
					- 0.5 * shadowCoord.y + 0.5
				);
				shadowCoord.z -= 0.0025; // bias (epsilon from original frag)

				bool inShadow =
				tex2D(_ShadowTex, uv + offset).r < shadowCoord.z &&
				dot(_LightDir.xyz, normal) < 0.0 &&
				abs(shadowCoord.z) < 1.0;

				return inShadow ? (1.0 - _ShadowAlpha) : 1.0;
			}

			float FilterPCF(float4 sc, float3 normal)
			{
				float texelSize = 1.0 / 2048.0; // matches ShadowRenderer shadow map size
				float shadow = 0.0;
				int count = 0;

				for (int x = -2; x <= 2; x++)
				{
					for (int y = -2; y <= 2; y++)
					{
						shadow += GetShadow(sc, float2(texelSize * x, texelSize * y), normal);
						count++;
					}
				}
				return shadow / (float)count;
			}

			// ─── Vertex shader ─────────────────────────────────────────────
			v2f vert(appdata v)
			{
				v2f o;
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				float4 worldPos4 = mul(unity_ObjectToWorld, v.vertex);

				o.clipPos = mul(UNITY_MATRIX_VP, worldPos4);
				o.worldPos = worldPos4.xyz;
				o.lightPos = mul(_LightMatrix, worldPos4);
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.uv = v.uv;

				return o;
			}

			// ─── Fragment shader ───────────────────────────────────────────
			float4 frag(v2f i) : SV_Target
			{
				float3 N = normalize(i.worldNormal);
				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);

				float3 albedoTex = tex2D(_MainTex, i.uv).rgb;
				float3 albedo = albedoTex * _Tint.rgb;
				float roughness = _Roughness;
				float metallic = _Metallic;

				float3 F0 = float3(_Fresnel, _Fresnel, _Fresnel);
				F0 = lerp(F0, albedo, metallic);

				// Single directional light (matching original single-light loop)
				float3 L = -_LightDir.xyz;
				float3 H = normalize(V + L);
				float3 radiance = float3(1.0, 1.0, 1.0) * _LightIntensity;

				float D = DistributionGGX(N, H, roughness * roughness);
				float G = GeometrySmith(N, V, L, roughness);
				float3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

				float3 kS = F;
				float3 kD = (float3(1.0, 1.0, 1.0) - kS) * (1.0 - metallic);

				float denom = 4.0 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 0.00001;
				float3 specular = (D * G * F) / denom;
				float3 Lo = (kD * albedo / PI + specular) * radiance * max(dot(N, L), 0.0);

				// PCF shadow
				float4 sc = i.lightPos / i.lightPos.w;
				float shadow = FilterPCF(sc, N);

				float3 ambient = float3(_Ambient, _Ambient, _Ambient) * albedo;
				float3 color = ambient + Lo;

				// Reinhard tone mapping + gamma
				color = color / (color + float3(1.0, 1.0, 1.0));
				color = pow(color, float3(1.0 / 2.2, 1.0 / 2.2, 1.0 / 2.2));
				color *= shadow;

				// Depth fade (original: mix toward 0.1 background)
				float dist = length(_WorldSpaceCameraPos - i.worldPos);
				float fogT = clamp((dist - _FogStart) / _FogEnd, 0.0, 1.0);
				color = lerp(color, float3(0.1, 0.1, 0.1), fogT);

				return float4(color, 1.0);
			}

			ENDHLSL
		}

		// Shadow caster pass — Unity's built-in handles shadow map generation
		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On ZTest LEqual Cull Back
			CGPROGRAM
			#pragma vertex   vert_shadow
			#pragma fragment frag_shadow
			#pragma target   5.0
			#include "UnityCG.cginc"

			struct appdata_s
			{
				float4 vertex : POSITION; float3 normal : NORMAL;
			};
			struct v2f_s
			{
				float4 pos : SV_POSITION;
			};

			v2f_s vert_shadow(appdata_s v)
			{
				v2f_s o;
				o.pos = UnityClipSpaceShadowCasterPos(v.vertex, float4(v.normal, 0));
				o.pos = UnityApplyLinearShadowBias(o.pos);
				return o;
			}

			float4 frag_shadow(v2f_s i) : SV_Target
			{
				return 0;
			}
			ENDCG
		}
	}

	FallBack "Diffuse"
}