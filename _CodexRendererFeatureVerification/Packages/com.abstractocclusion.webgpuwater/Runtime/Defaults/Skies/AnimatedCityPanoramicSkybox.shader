Shader "AbstractOcclusion/WebGpuWater/Skybox/Animated City Panoramic"
{
    Properties
    {
        [NoScaleOffset] _MainTex ("Panoramic Texture", 2D) = "black" {}
        [HDR] _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Exposure ("Exposure", Range(0, 8)) = 1
        _Rotation ("Rotation", Range(0, 360)) = 0

        [Header(City light flicker)]
        [HDR] _EmissionBoost ("Emission Boost", Range(0, 8)) = 2.5
        _FlickerStrength ("Strength", Range(0, 1)) = 0.28
        _FlickerSpeed ("Speed", Range(0.05, 5)) = 0.65
        _LightThreshold ("Light Threshold", Range(0, 1)) = 0.38
        _LightSoftness ("Light Softness", Range(0.001, 0.5)) = 0.14
        _WarmLightBias ("Warm Light Bias", Range(0, 1)) = 0.2
        _CityBandMin ("City Band Minimum V", Range(0, 1)) = 0.5
        _CityBandMax ("City Band Maximum V", Range(0, 1)) = 0.84
        _FlickerGrid ("Flicker Groups (X, Y)", Vector) = (320, 160, 0, 0)
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
            Name "AnimatedCitySkybox"

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
                float4 _FlickerGrid;
                float _Exposure;
                float _Rotation;
                float _EmissionBoost;
                float _FlickerStrength;
                float _FlickerSpeed;
                float _LightThreshold;
                float _LightSoftness;
                float _WarmLightBias;
                float _CityBandMin;
                float _CityBandMax;
            CBUFFER_END

            static const float kPi = 3.14159265359;
            static const float kTwoPi = 6.28318530718;
            static const float kDegreesPerTurn = 360.0;
            static const float3 kLuminanceWeights = float3(0.2126, 0.7152, 0.0722);
            static const float2 kHashDotA = float2(127.1, 311.7);
            static const float2 kHashDotB = float2(269.5, 183.3);
            static const float kHashScale = 43758.5453;
            static const float kMinimumValue = 0.0001;
            static const float kWarmRedWeight = 0.65;
            static const float kWarmGreenWeight = 0.35;
            static const float kPrimaryWaveWeight = 0.55;
            static const float kSecondaryWaveWeight = 0.30;
            static const float kTertiaryWaveWeight = 0.15;

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
                float rotationOffset = _Rotation / kDegreesPerTurn;

                return float2(
                    frac(0.5 - longitude / kTwoPi + rotationOffset),
                    1.0 - latitude / kPi);
            }

            float Hash(float2 value)
            {
                return frac(sin(dot(value, kHashDotA)) * kHashScale);
            }

            float SecondaryHash(float2 value)
            {
                return frac(sin(dot(value, kHashDotB)) * kHashScale);
            }

            float SmoothFlicker(float2 cell, float time)
            {
                float primaryPhase = Hash(cell) * kTwoPi;
                float secondaryPhase = SecondaryHash(cell) * kTwoPi;
                float tertiaryPhase = Hash(cell + kHashDotB) * kTwoPi;

                float primaryRate = lerp(0.35, 0.85, SecondaryHash(cell + kHashDotA));
                float secondaryRate = lerp(1.1, 1.8, Hash(cell + kHashDotB));
                float tertiaryRate = lerp(0.12, 0.28, SecondaryHash(cell + kHashDotB));

                float primary = sin(time * primaryRate + primaryPhase);
                float secondary = sin(time * secondaryRate + secondaryPhase);
                float tertiary = sin(time * tertiaryRate + tertiaryPhase);
                return primary * kPrimaryWaveWeight
                     + secondary * kSecondaryWaveWeight
                     + tertiary * kTertiaryWaveWeight;
            }

            float CityBandMask(float verticalUv)
            {
                float edgeWidth = max(fwidth(verticalUv) * 2.0, kMinimumValue);
                float aboveMinimum = smoothstep(_CityBandMin - edgeWidth, _CityBandMin + edgeWidth, verticalUv);
                float aboveMaximum = smoothstep(_CityBandMax - edgeWidth, _CityBandMax + edgeWidth, verticalUv);
                return aboveMinimum * (1.0 - aboveMaximum);
            }

            float LightMask(float3 color, float2 uv)
            {
                float luminance = dot(color, kLuminanceWeights);
                float brightness = smoothstep(
                    _LightThreshold,
                    _LightThreshold + max(_LightSoftness, kMinimumValue),
                    luminance);

                float warmChannel = color.r * kWarmRedWeight + color.g * kWarmGreenWeight;
                float warmth = saturate((warmChannel - color.b) + (1.0 - _WarmLightBias));
                return brightness * warmth * CityBandMask(uv.y);
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 uv = DirectionToEquirectangularUv(input.directionOS);
                float3 baseColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                float2 cell = floor(uv * max(_FlickerGrid.xy, 1.0));
                float lightMask = LightMask(baseColor, uv);
                float flicker = SmoothFlicker(cell, _Time.y * _FlickerSpeed) * _FlickerStrength;
                float3 emission = baseColor * lightMask * _EmissionBoost * (1.0 + flicker);
                float3 finalColor = (baseColor + emission) * _Tint.rgb * _Exposure;
                return half4(finalColor, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
