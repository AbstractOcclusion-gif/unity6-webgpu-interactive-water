// WebGpuWater - third-party SCENE-fog hook (the above-water atmosphere term, not the water medium).
//
// The package fogs its own sheet/walls/transparents with Unity fog (UNITY_APPLY_FOG / MixFog).
// A sky asset that replaces Unity fog (Enviro 3 today) fogs opaques with a fullscreen pass the
// transparent water never receives, so the sheet would not match the terrain around it. This
// header routes every scene-fog call site through ONE macro/function pair that either evaluates
// the third-party fog or falls back to Unity fog - byte-identical to the old behaviour when off.
//
// WHY A WRITTEN FILE, NOT A KEYWORD: the third-party path must cost nothing - no variants, no
// samplers - in the projects and passes that do not use it, and its own keywords are GLOBAL
// multi_compiles that a shader_feature could not fence. The Water Wizard (Utilities >
// Third-party fog) rewrites the WIZARD-MANAGED block below; off = the block is empty.
//
// The third-party maths is PORTED into the package (WaterEnviro3Fog.hlsl), never #included from
// the asset: its include pulls 8 samplers behind it and overflows WaterSurface's 16-register
// budget. The wizard hashes the upstream files and warns when they drift.
//
// Include with #include_with_pragmas AFTER UnityCG.cginc (CG passes) or URP Core.hlsl (HLSL
// passes): the flavour is picked from UNIVERSAL_PIPELINE_CORE_INCLUDED, and the multi_compile
// below is only honoured through include_with_pragmas. Do NOT add multi_compile_fog to a pass
// that includes this header with a third-party fog active: Unity fog is dead weight there.
#ifndef WATER_THIRD_PARTY_FOG_INCLUDED
#define WATER_THIRD_PARTY_FOG_INCLUDED

// ---- BEGIN WIZARD-MANAGED BLOCK (do not edit by hand; Water Wizard > Utilities > Third-party fog) ----
#define WATER_ENVIRO3_FOG 1
// ---- END WIZARD-MANAGED BLOCK ----

#if defined(WATER_ENVIRO3_FOG)
    // Height/distance fog is deliberately ALU-only here. WaterSurface already occupies all
    // sixteen ps_4_0 texture registers on D3D11; sampling Enviro's volumetric-light buffer
    // creates t16 and fails that shader variant. Enviro's fullscreen pass still supplies
    // volumetric lighting to opaque geometry, while transparent water receives the matching
    // atmospheric fog without adding another texture or another global-keyword variant.
    #include "WaterEnviro3Fog.hlsl"

    #if defined(UNIVERSAL_PIPELINE_CORE_INCLUDED)
        // HLSL passes: SV_POSITION.z is the depth-buffer value; URP's Linear01Depth takes it directly.
        float3 WaterApplySceneFog(float3 color, float4 positionCS, float3 positionWS, float unityFogFactor)
        {
            float2 uv = GetNormalizedScreenSpaceUV(positionCS);
            float linear01 = Linear01Depth(positionCS.z, _ZBufferParams);
            return WaterEnviroApplyFog(color, uv, positionWS, linear01);
        }
    #else
        // CG passes: same contract, UnityCG's single-argument Linear01Depth.
        float3 WaterApplySceneFogCG(float3 color, float4 screenPos, float3 worldPos, float4 clipPos)
        {
            float2 uv = screenPos.xy / max(screenPos.w, 1e-5);
            float linear01 = Linear01Depth(clipPos.z);
            return WaterEnviroApplyFog(color, uv, worldPos, linear01);
        }
        #define WATER_APPLY_SCENE_FOG(i, color) color = WaterApplySceneFogCG(color, (i).screenPos, (i).worldPos, (i).pos)
    #endif
#else
    #pragma multi_compile_fog
    #if defined(UNIVERSAL_PIPELINE_CORE_INCLUDED)
        float3 WaterApplySceneFog(float3 color, float4 positionCS, float3 positionWS, float unityFogFactor)
        {
            return MixFog(color, unityFogFactor);
        }
    #else
        #define WATER_APPLY_SCENE_FOG(i, color) UNITY_APPLY_FOG((i).fogCoord, color)
    #endif
#endif

#endif // WATER_THIRD_PARTY_FOG_INCLUDED
