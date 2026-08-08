// WebGpuWater - full-screen skybox fog overlay. Opaque geometry renders after this pass, so only
// the skybox/background receives the blend. The opacity is calculated from Unity RenderSettings
// and the rendering camera's far clip, matching the colour a far-away fogged object converges to.
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
            ZTest Always
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
