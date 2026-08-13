Shader "AbstractOcclusion/WebGpuWater/Skybox/Panoramic"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Panoramic Texture", 2D) = "grey" {}
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
            Name "UrpPanoramicSkybox"
            ZWrite Off
            ZTest LEqual
            Cull Off
            ColorMask RGB

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _Exposure;
                float _Rotation;
            CBUFFER_END

            static const float Pi = 3.14159265359;
            static const float TwoPi = 6.28318530718;
            static const float DegreesPerTurn = 360.0;
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

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.directionOS = input.positionOS.xyz;
                return output;
            }

            float2 DirectionToEquirectangularUv(float3 direction)
            {
                float3 normalizedDirection = normalize(direction);
                float longitude = atan2(normalizedDirection.z, normalizedDirection.x);
                float latitude = acos(clamp(normalizedDirection.y, -1.0, 1.0));
                float rotationOffset = _Rotation / DegreesPerTurn;
                return float2(
                    frac(0.5 - longitude / TwoPi + rotationOffset),
                    1.0 - latitude / Pi);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 uv = DirectionToEquirectangularUv(input.directionOS);
                half3 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                color *= _Tint.rgb * BuiltInTintCompensation * _Exposure;
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
