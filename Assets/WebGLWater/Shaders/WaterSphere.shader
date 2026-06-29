// WebGL Water - the draggable sphere (Unity 6 / URP port)
// Positioned analytically from _SphereCenter / _SphereRadius, so the mesh must be
// a unit-radius sphere (the scene builder generates one). The transform is only
// used to keep the renderer's bounds near the sphere for frustum culling.
Shader "WebGLWater/WaterSphere"
{
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2 // Back
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.position = _SphereCenter + v.vertex.xyz * _SphereRadius;
                o.pos = mul(UNITY_MATRIX_VP, float4(o.position, 1.0));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 color = GetSphereColor(i.position);
                float4 info = tex2D(_WaterTex, i.position.xz * 0.5 + 0.5);
                if (i.position.y < info.r) color *= UNDERWATER_COLOR * 1.2;
                return float4(color, 1.0);
            }
            ENDCG
        }
    }
}
