// TetWireframe.shader
// Debug wireframe visualisation of the tetrahedral simulation mesh.
// Ported from tetrahedral.vert + simple.frag (Vulkan GLSL).
//
// In Vulkan this was a procedural draw: vkCmdDraw(12 verts, tetCount instances)
// with gl_InstanceIndex used to index into the particle SSBO.
// In Unity, we use Graphics.DrawProceduralNow() from SoftBodyDebugRenderer.cs.

Shader "XPBD/TetWireframe"
{
    Properties {}

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Overlay" }
        LOD 100

        Pass
        {
            Name "TetWireframe"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   5.0

            #include "UnityCG.cginc"

            // ─── Structs matching GPU buffers ──────────────────────────────
            struct Particle
            {
                float3 position;
                float  _pad0;
                float3 velocity;
                float  invMass;
            };

            struct Tetrahedral
            {
                uint4  indices;
                float  restVolume;
                float3 _pad;
            };

            // Bound by SoftBodyDebugRenderer.cs via Material.SetBuffer
            StructuredBuffer<Particle>    _Particles;
            StructuredBuffer<Tetrahedral> _Tetrahedrals;

            float4x4 _ViewProj; // set by SoftBodyDebugRenderer each frame

            // Tet face winding — 4 faces × 3 vertices = 12 procedural vertices
            // Same winding table as the original GLSL:
            //   face 0: indices 2, 1, 0
            //   face 1: indices 0, 1, 3
            //   face 2: indices 1, 2, 3
            //   face 3: indices 2, 0, 3
            static const uint TET_FACE_VERTS[12] = { 2,1,0, 0,1,3, 1,2,3, 2,0,3 };

            struct v2f
            {
                float4 clipPos   : SV_POSITION;
                float  depth     : TEXCOORD0;
            };

            v2f vert(uint vertID : SV_VertexID, uint instID : SV_InstanceID)
            {
                v2f o;
                uint localVert = TET_FACE_VERTS[vertID];
                uint pIdx      = _Tetrahedrals[instID].indices[localVert];
                float3 worldPos = _Particles[pIdx].position;

                o.clipPos = mul(_ViewProj, float4(worldPos, 1.0));
                o.depth   = o.clipPos.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Original simple.frag: color = vec3(pow(1/z, 1/2.2))
                float brightness = pow(1.0 / max(i.depth, 0.001), 1.0 / 2.2);
                return float4(brightness, brightness, brightness, 1.0);
            }

            ENDHLSL
        }
    }
}
