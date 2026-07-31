// WebGpuWater - PUBLIC underwater fog API for user transparent materials.
//
// Include this ONE header in your transparent shader (hand-written URP HLSL or a Shader Graph
// Custom Function node) and your material picks up the water medium exactly as the package's
// own particles do: per-channel extinction toward the water's lit in-scatter colour over the
// camera->fragment WET path, plus the scene-lamp glow (the Light Scatter family) along that
// same path. One implementation, shared with the fullscreen fog, the water surface and the
// sprites - your transparent can never drift from the water it sits in.
//
// USAGE (fragment or vertex stage - pure ALU, no textures):
//     float3 fogMul, fogAdd;
//     WebGpuWaterFogTransparent(worldPos, lightDir, sunColor, fogMul, fogAdd);
//     color.rgb = color.rgb * fogMul + fogAdd;   // AFTER your texture/albedo multiply - exact,
//                                                // because the fog lerp is linear in the colour
// 'lightDir' = direction TOWARD the sun, 'sunColor' = its colour x intensity (pass your own;
// this header deliberately declares neither, so it never fights the declarations your shader
// already carries). Shader Graph: add a Custom Function node (File mode) pointing here with
// function name "WebGpuWaterFog" - the _float wrapper below handles preview.
//
// IMPORTANT - the SORTING half (why a component exists): the fullscreen underwater fog runs
// AFTER every transparent and integrates to the OPAQUE depth, so on submerged frames it paints
// the full water column's fog OVER anything drawn in the transparent queue - self-fogged or
// not (the exact artifact the package sprites had before their after-fog reroute). Put the
// WaterFogTransparent component on your renderer: on fog-armed frames it suppresses the
// queue-time draw and the water feature re-draws the renderer AFTER the fog and the god rays,
// beside the package sprites. Fog-off frames are untouched - your material draws exactly as
// it always did, and this function returns identity (mul 1, add 0).
//
// Limits, stated up front:
//  * The waterline here is the flat closed-form one (_UnderwaterSurfaceY, the Simple-fog
//    model) - per-fragment error vs the wavy crossing is below prop scale, the same accepted
//    approximation as the particles.
//  * The lamp glow is gated on UNIFORMS, not the WATER_FOG_POINT_LIGHTS keyword - a deliberate
//    deviation from the package's keyword-fence rule, because a multi_compile cannot be forced
//    into user shaders. Safe: the publisher writes _WaterSceneLightCount = 0 whenever the
//    feature is disarmed (so the loop body never runs), and the guarded code is a small flat
//    pure-ALU loop, not the kind of texture march the fence rule exists for.
//  * Exclusion volumes do not shadow the glow (same as every scatter consumer), and on Simple
//    fog tiers the glow is off (count publishes 0) while extinction/in-scatter still apply.
#ifndef WEBGPU_WATER_FOG_API_INCLUDED
#define WEBGPU_WATER_FOG_API_INCLUDED

#include "WaterVolume.hlsl"      // _VolumeCenter: the rest plane every lamp-glow consumer references
#include "WaterParticleFog.hlsl" // ParticleUnderwaterFog + WaterFog.hlsl + the armed/surface globals

#define WEBGPU_WATER_FOG_API_MIN_RAY 1e-4 // camera-on-fragment guard for the ray normalisation

// The medium for one transparent fragment/vertex at 'worldPos'. Outputs the mul/add pair
// described in the header; identity whenever the underwater fog is not armed this frame.
void WebGpuWaterFogTransparent(float3 worldPos, float3 lightDir, float3 sunColor,
                               out float3 fogMul, out float3 fogAdd)
{
    // Extinction + lit in-scatter over the wet path: the particles' own function, verbatim -
    // one implementation for every "self-fogged transparent" in and out of the package.
    ParticleUnderwaterFog(worldPos, lightDir, sunColor, fogMul, fogAdd);

    // Scene-lamp glow (the A1 family): the SAME closed-form integral the fullscreen fog and
    // the water surface evaluate, over the wet segment of the camera->fragment ray, so a
    // lamp's glow on your transparent matches its glow in the fog behind it. Uniform-gated
    // by design - see the header note.
    if (_UnderwaterFogArmed < 0.5) return;
    if (_WaterSceneLightCount < 0.5 || _UnderwaterLightScatter <= 0.0) return;
    float3 toFrag = worldPos - _WorldSpaceCameraPos.xyz;
    float len = max(length(toFrag), WEBGPU_WATER_FOG_API_MIN_RAY);
    float wet = WaterPathLength(worldPos, _WorldSpaceCameraPos.xyz, _UnderwaterSurfaceY);
    if (wet <= 0.0) return; // fragment and camera both in air
    float3 dirToFrag = toFrag / len;
    // Water begins where the ray dips under: extinction is measured from there, so an
    // above-water eye does not extinguish the glow through air (the integral's contract).
    float tWaterStart = len - min(wet, len);
    fogAdd += WaterSceneLightsInscatter(_WorldSpaceCameraPos.xyz, dirToFrag, tWaterStart,
                                        len, tWaterStart, _VolumeCenter.y)
            * _UnderwaterLightScatter;
}

// Convenience: apply the pair to a lit colour.
float3 WebGpuWaterApplyFog(float3 rgb, float3 fogMul, float3 fogAdd)
{
    return rgb * fogMul + fogAdd;
}

// Shader Graph Custom Function entry point (File mode, function name "WebGpuWaterFog").
// The preview guard returns identity so graph thumbnails never sample water state.
void WebGpuWaterFog_float(float3 WorldPos, float3 LightDir, float3 SunColor,
                          out float3 FogMul, out float3 FogAdd)
{
#ifdef SHADERGRAPH_PREVIEW
    FogMul = float3(1.0, 1.0, 1.0);
    FogAdd = float3(0.0, 0.0, 0.0);
#else
    WebGpuWaterFogTransparent(WorldPos, LightDir, SunColor, FogMul, FogAdd);
#endif
}

#endif // WEBGPU_WATER_FOG_API_INCLUDED
