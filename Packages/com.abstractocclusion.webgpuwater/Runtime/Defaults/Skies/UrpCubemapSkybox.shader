Shader "AbstractOcclusion/WebGpuWater/Skybox/Cubemap"
{
    Properties
    {
        [NoScaleOffset] _Tex ("Cubemap", Cube) = "grey" {}
        [HideInInspector] _Tex_HDR ("Cubemap Decode", Vector) = (1, 1, 0, 0)
        [HDR] _Tint ("Tint", Color) = (0.5, 0.5, 0.5, 0.5)
        _Exposure ("Exposure", Range(0, 8)) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "UrpCubemapSkybox"
            ZWrite Off
            ZTest LEqual
            Cull Off
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"

            TEXTURECUBE(_Tex);
            SAMPLER(sampler_Tex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tex_HDR;
                float4 _Tint;
                float _Exposure;
                float _Rotation;
            CBUFFER_END

            static const float Pi = 3.14159265359;
            static const float DegreesToRadians = Pi / 180.0;
#if defined(UNITY_COLORSPACE_GAMMA)
            static const float BuiltInTintCompensation = 4.59479380;
#else
            static const float BuiltInTintCompensation = 2.0;
#endif

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionOS : TEXCOORD0;
            };

            float3 RotateAroundY(float3 direction, float angleDegrees)
            {
                float sineAngle;
                float cosineAngle;
                sincos(angleDegrees * DegreesToRadians, sineAngle, cosineAngle);
                return float3(
                    cosineAngle * direction.x - sineAngle * direction.z,
                    direction.y,
                    sineAngle * direction.x + cosineAngle * direction.z);
            }

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.directionOS = RotateAroundY(input.positionOS.xyz, _Rotation);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                half4 encodedColor = SAMPLE_TEXTURECUBE(_Tex, sampler_Tex, normalize(input.directionOS));
                half3 color = DecodeHDREnvironment(encodedColor, _Tex_HDR);
                color *= _Tint.rgb * BuiltInTintCompensation * _Exposure;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
