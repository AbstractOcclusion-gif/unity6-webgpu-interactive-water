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
// IMPORTANT - the SORTING half (why a component exists): the water sheet renders with
// ZWrite On (its Pass 0 computes an opaque-looking colour), so anything drawn after it and
// BEHIND it in depth z-fails and vanishes - and the fullscreen fog additionally paints the
// whole column's fog over queue-time transparents on submerged frames. Put the
// WaterFogTransparent component on your renderer: on EVERY frame a water body is active it
// suppresses the queue-time draw, and the water feature re-draws the renderer AFTER the
// whole water stack over RESTORED opaque-only depth - so your prop z-tests correctly
// against walls and terrain, sees THROUGH the sheet from either side (submerged prop from
// the air, above-water prop from below), and never double-fogs. No water in the scene:
// the component is inert and your material draws exactly as it always did.
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
    // Extinction + lit in-scatter over the wet path: the particles' own maths, through the
    // ALWAYS entry point - priced whenever the fog FEATURE is on, not only while the
    // fullscreen pass is armed. A rerouted prop bypasses the sheet's refraction, so from
    // above the water it must carry its own tint even on disarmed frames (the sheet would
    // otherwise have provided it); the sprites keep their armed-gated wrapper untouched.
    ParticleUnderwaterFogAlways(worldPos, lightDir, sunColor, fogMul, fogAdd);

    // Scene-lamp glow (the A1 family): the SAME closed-form integral the fullscreen fog and
    // the water surface evaluate, over the wet segment of the camera->fragment ray, so a
    // lamp's glow on your transparent matches its glow in the fog behind it - and like the
    // surface's own from-above term it carries NO armed gate (the published light count and
    // the knob are the whole switch). Uniform-gated by design - see the header note.
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
