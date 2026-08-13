// WebGpuWater - full-screen skybox fog overlay. URP has already written opaque depth when this
// pass follows the skybox, so the triangle is placed at far depth and blends only untouched sky
// pixels. This avoids requesting a sampled camera-depth texture and keeps later screen-space
// passes on their existing depth path.
Shader "AbstractOcclusion/WebGpuWater/WaterSkyFog"
{
    Properties
    {
        [HideInInspector] _SkyFogOpacity ("Sky Fog Opacity", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "SkyFog"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Equal
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _SkyFogOpacity;
            CBUFFER_END

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.positionCS.z = UNITY_RAW_FAR_CLIP_VALUE * output.positionCS.w;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(unity_FogColor.rgb, _SkyFogOpacity);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
