// WebGpuWater - restores the opaque-only camera depth before rerouted transparents.
Shader "Hidden/AbstractOcclusion/WebGpuWater/WaterRestoreOpaqueDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "WaterRestoreOpaqueDepth"
            ColorMask 0
            Cull Off
            ZWrite On
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRestoreDepth
            #pragma target 4.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float FragRestoreDepth(Varyings input) : SV_Depth
            {
                return SampleSceneDepth(input.uv);
            }
            ENDHLSL
        }
    }
}
