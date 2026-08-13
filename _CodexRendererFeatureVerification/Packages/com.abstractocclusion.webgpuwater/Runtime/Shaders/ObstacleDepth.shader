// WebGpuWater - obstacle footprint pass (Unity 6 / URP port)
// Drawn top-down by WaterObstacle via a CommandBuffer with an orthographic VP that
// maps the pool's x,z in [-1,1] onto the render target. Each object writes, per
// column, how far its surface sits BELOW the local waterline (set per-object from C#).
// Rendering both faces (Cull Off) with additive blend means the above-water top
// contributes ~0 while the submerged underside contributes its depth, so the footprint
// encodes the object's submerged THICKNESS and tapers smoothly to zero where a rounded
// hull meets the surface - no hard silhouette edge to stamp drop-like ripples, and the
// generated wave matches the object's underwater profile.
Shader "AbstractOcclusion/WebGpuWater/ObstacleDepth"
{
    Properties
    {
        _WaterlineY ("Waterline Y (world)", Float) = 0
        _DisplaceScale ("Displace Scale (per-object weight)", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One One   // additive: across objects, and top(~0)+underside per column

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _WaterlineY;
            float _DisplaceScale;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float worldY : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                // Uses the view/projection set on the CommandBuffer (top-down ortho).
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldY = mul(unity_ObjectToWorld, v.vertex).y;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Depth of this surface below the local waterline. Above-water surfaces
                // clamp to 0, so only the submerged underside contributes per column.
                float submerged = max(0.0, _WaterlineY - i.worldY) * _DisplaceScale;
                return float4(submerged, 0, 0, 0);
            }
            ENDCG
        }
    }
}
