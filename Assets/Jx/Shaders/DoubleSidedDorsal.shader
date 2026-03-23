Shader "Custom/TwoSidedAlphaFixed" {
    Properties {
        _Color ("Front Color (Alpha)", Color) = (1,1,1,1)
        _DorsalColor ("Back Color (Alpha)", Color) = (1,0,0,1)
        _MainTex ("Base (RGB) Transparency (A)", 2D) = "white" {}
        _SpecColor ("Specular Color", Color) = (0.5, 0.5, 0.5, 1)
        _Shininess ("Shininess", Range (0.03, 1)) = 0.078125
    }
    SubShader {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 200

        // --- PASS 1: DRAW BACK FACES ONLY ---
        Cull Front 
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGPROGRAM
        #pragma surface surf BlinnPhong alpha:fade keepalpha
        sampler2D _MainTex;
        fixed4 _DorsalColor;
        half _Shininess;

        struct Input { float2 uv_MainTex; };

        void surf (Input IN, inout SurfaceOutput o) {
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            o.Albedo = tex.rgb * _DorsalColor.rgb;
            o.Alpha = tex.a * _DorsalColor.a;
            o.Specular = _Shininess;
            o.Normal *= -1.0; // Flip normals for back lighting
        }
        ENDCG

        // --- PASS 2: DRAW FRONT FACES ONLY ---
        Cull Back 
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGPROGRAM
        #pragma surface surf BlinnPhong alpha:fade keepalpha
        #pragma traget 3.0
        sampler2D _MainTex;
        fixed4 _Color;
        half _Shininess;

        struct Input { float2 uv_MainTex; };

        void surf (Input IN, inout SurfaceOutput o) {
            fixed4 tex = tex2D(_MainTex, IN.uv_MainTex);
            o.Albedo = tex.rgb * _Color.rgb;
            o.Alpha = tex.a * _Color.a;
            o.Specular = _Shininess;
        }
        ENDCG
    }
    FallBack "Transparent/VertexLit"
}
