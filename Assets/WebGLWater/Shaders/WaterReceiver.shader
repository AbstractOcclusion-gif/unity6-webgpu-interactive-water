// WebGL Water - lit receiver for interactable objects (Unity 6 / URP port)
// A proper URP surface: real main-light lighting, casts + receives shadows, and
// receives the projected caustics where it sits below the water surface. Driven by
// the same directional light as everything else (its direction also reaches the
// analytic water via the _LightDir global), so there is no separate fake light.
Shader "WebGLWater/WaterReceiver"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.82, 0.52, 0.30, 1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _CausticStrength ("Caustic Strength", Range(0,8)) = 4
        _CausticTint ("Caustic Tint", Color) = (1,1,1,1)
        _UnderwaterTint ("Underwater Tint", Color) = (0.4, 0.9, 1.0, 1)
        _WaterLevel ("Water Level Y", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On ZTest LEqual Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl" // IOR_*, ProjectCausticUV

            TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
            float3 _LightDir; // global "toward the light", driven from the Unity sun

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST;
                float4 _CausticTint;
                float4 _UnderwaterTint;
                float _CausticStrength;
                float _WaterLevel;
            CBUFFER_END

            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float2 uv:TEXCOORD0; };
            struct Varyings   { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                o.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 albedo = _BaseColor.rgb * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb;
                float3 N = normalize(IN.normalWS);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndl = saturate(dot(N, mainLight.direction));
                float3 ambient = SampleSH(N);
                float3 color = albedo * (ambient + mainLight.color * (ndl * mainLight.shadowAttenuation));

                // projected caustics where this point is below the (rippled) surface.
                // Sim height + caustics live in pool space, so convert the world point.
                float3 poolPos = WorldToPool(IN.positionWS);
                float2 wuv = poolPos.xz * 0.5 + 0.5;
                float simH = SAMPLE_TEXTURE2D(_WaterTex, sampler_WaterTex, wuv).r;
                if (poolPos.y < simH)
                {
                    float3 refractedLight = -refract(-_LightDir, float3(0,1,0), IOR_AIR / IOR_WATER);
                    float2 cuv = ProjectCausticUV(poolPos, refractedLight);
                    float caustic = SAMPLE_TEXTURE2D(_CausticTex, sampler_CausticTex, cuv).r;
                    color += albedo * _CausticTint.rgb * (caustic * _CausticStrength * mainLight.shadowAttenuation);
                    color *= _UnderwaterTint.rgb;
                }

                // depth absorption (shared with the surface so fog is consistent); the
                // surface sits at the volume centre's world Y.
                color = ApplyWaterFog(color, WaterPathLength(IN.positionWS, _WorldSpaceCameraPos, _VolumeCenter.y));
                return half4(color, 1);
            }
            ENDHLSL
        }

        // Cast real shadows onto the pool and other objects.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct V { float4 positionCS:SV_POSITION; };

            float4 GetShadowPositionHClip(A IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            V vert(A IN) { V o; o.positionCS = GetShadowPositionHClip(IN); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // Write depth so SSR / screen-space refraction see the object.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };

            V vert(A IN) { V o; o.positionCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
