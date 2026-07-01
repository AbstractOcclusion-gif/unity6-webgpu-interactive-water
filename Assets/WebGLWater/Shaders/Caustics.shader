// WebGL Water - caustics pass (Unity 6 / URP port)
// Renders the water grid mesh into the caustic RenderTexture. The vertex shader
// projects each water vertex along the refracted light onto the pool floor and
// outputs clip-space position directly (no view/projection matrix). The fragment
// shader brightens where the projected area shrinks (light focusing). The green
// channel is left at 1.0 (no occluder shadow).
//
// Drawn manually from C# via CommandBuffer.DrawMesh with an identity matrix.
Shader "WebGLWater/Caustics"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl" // brings WaterShared: CAUSTIC_PROJECTION_SCALE, RIM_SHADOW_*, POOL_*

            #define CAUSTIC_FOCUS_SCALE 0.2 // brightness of the focused caustic

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 oldPos : TEXCOORD0;
                float3 newPos : TEXCOORD1;
            };

            // project the ray onto the pool floor plane
            float3 project(float3 origin, float3 ray, float3 refractedLight)
            {
                float2 tcube = IntersectCube(origin, ray, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                origin += ray * tcube.y;
                float tplane = (-origin.y - 1.0) / refractedLight.y;
                return origin + refractedLight * tplane;
            }

            v2f vert(appdata v)
            {
                v2f o;
                float4 info = tex2Dlod(_WaterTex, float4(v.vertex.xy * 0.5 + 0.5, 0, 0));
                info.ba *= 0.5;
                float3 normal = float3(info.b, sqrt(max(0.0, 1.0 - dot(info.ba, info.ba))), info.a);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                float3 ray = refract(-_LightDir, normal, IOR_AIR / IOR_WATER);

                o.oldPos = project(v.vertex.xzy, refractedLight, refractedLight);
                o.newPos = project(v.vertex.xzy + float3(0.0, info.r, 0.0), ray, refractedLight);

                o.pos = float4(CAUSTIC_PROJECTION_SCALE * (o.newPos.xz + refractedLight.xz / refractedLight.y), 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // if the projected triangle gets smaller it gets brighter, and vice versa
                float oldArea = length(ddx(i.oldPos)) * length(ddy(i.oldPos));
                float newArea = length(ddx(i.newPos)) * length(ddy(i.newPos));
                // green channel = occluder shadow term; 1.0 means unshadowed.
                // Guard newArea: a degenerate (near-parallel) projected triangle would divide
                // by ~0 and write Inf/NaN into the caustic RT that every other pass samples.
                float4 col = float4(oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE, 1.0, 0.0, 0.0);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

                // shadow for the rim of the pool
                float2 t = IntersectCube(i.newPos, -refractedLight, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                col.r *= 1.0 / (1.0 + exp(-RIM_SHADOW_SHARPNESS / (1.0 + RIM_SHADOW_SPREAD * (t.y - t.x)) * (i.newPos.y - refractedLight.y * t.y - POOL_RIM_HEIGHT)));

                return col;
            }
            ENDCG
        }
    }
}
