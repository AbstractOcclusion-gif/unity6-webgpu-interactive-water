// WebGpuWater - PUBLIC underwater fog API for user transparent materials.
//
// Include this ONE header in your transparent shader (hand-written URP HLSL or a Shader Graph
// Custom Function node) and your material picks up the whole water medium, in four terms:
//   1. per-channel extinction toward the water's lit in-scatter colour over the camera->fragment
//      WET path - the same maths the package's own particles price,
//   2. downwelling depth darkening, so the prop dims and shifts blue the DEEPER it sits: the
//      Depth Attenuation block, identical to what an opaque WaterReceiver applies,
//   3. the scene-lamp glow (the Light Scatter family) along that same wet path,
//   4. Water Opacity turbidity toward the body colour on rays that cross the waterline - the
//      tint the water sheet used to supply before this renderer was rerouted past it.
// One implementation, shared with the fullscreen fog, the water surface and the sprites - your
// transparent can never drift from the water it sits in.
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
//  * Downwelling darkening uses the FRAGMENT's own depth for both halves of the pair. The
//    fullscreen fog instead darkens its in-scatter at the transmittance-weighted MEAN depth of
//    the ray (WaterUnderwaterFog.shader's downwellTMean), machinery this pure-ALU header has no
//    ray integral for. The mul half - your prop's own light, the term the eye actually reads -
//    is exact and matches WaterReceiver; only the added haze on a prop far deeper than the eye
//    darkens slightly early.
//  * Water Opacity applies only when the camera->fragment ray CROSSES the waterline, because
//    that is exactly the case the sheet's own turbidity used to cover and the reroute removed.
//    Two points on the same side of the surface are the fullscreen fog's business, and it does
//    not apply the knob either - so the rerouted prop matches whatever is behind it.
#ifndef WEBGPU_WATER_FOG_API_INCLUDED
#define WEBGPU_WATER_FOG_API_INCLUDED

#include "WaterVolume.hlsl"      // _VolumeCenter: the rest plane every lamp-glow consumer references
#include "WaterParticleFog.hlsl" // ParticleUnderwaterFog + WaterFog.hlsl + the armed/surface globals

#define WEBGPU_WATER_FOG_API_MIN_RAY 1e-4 // camera-on-fragment guard for the ray normalisation
// Depth clarity is a bed-depth field (WaterShore.hlsl) this header deliberately does not include,
// so the turbidity curve is evaluated at "clear" - which recovers the body's base _WaterOpacity
// exactly, the same value a body with no baked bed gets everywhere else.
#define WEBGPU_WATER_FOG_API_CLEAR_CLARITY 1.0

// True when the eye and the fragment sit on opposite sides of the waterline. Uses the same
// <= convention as WaterPathLength so a fragment exactly ON the line is classified once.
bool WebGpuWaterFogViewCrossesSurface(float3 worldPos)
{
    bool camUnder = _WorldSpaceCameraPos.y <= _UnderwaterSurfaceY;
    bool fragUnder = worldPos.y <= _UnderwaterSurfaceY;
    return camUnder != fragUnder;
}

// Scene-lamp glow (the A1 family): the SAME closed-form integral the fullscreen fog and the
// water surface evaluate, over the wet segment of the camera->fragment ray, so a lamp's glow on
// your transparent matches its glow in the fog behind it - and like the surface's own from-above
// term it carries NO armed gate (the published light count and the knob are the whole switch).
// Uniform-gated by design - see the header note. Returns black when there is nothing to add.
float3 WebGpuWaterFogSceneLampGlow(float3 worldPos)
{
    if (_WaterSceneLightCount < 0.5 || _UnderwaterLightScatter <= 0.0) return float3(0.0, 0.0, 0.0);
    float wet = WaterPathLength(worldPos, _WorldSpaceCameraPos.xyz, _UnderwaterSurfaceY);
    if (wet <= 0.0) return float3(0.0, 0.0, 0.0); // fragment and camera both in air

    float3 toFrag = worldPos - _WorldSpaceCameraPos.xyz;
    float len = max(length(toFrag), WEBGPU_WATER_FOG_API_MIN_RAY);
    float3 dirToFrag = toFrag / len;
    // Water begins where the ray dips under: extinction is measured from there, so an
    // above-water eye does not extinguish the glow through air (the integral's contract).
    float tWaterStart = len - min(wet, len);
    return WaterSceneLightsInscatter(_WorldSpaceCameraPos.xyz, dirToFrag, tWaterStart,
                                     len, tWaterStart, _VolumeCenter.y)
         * _UnderwaterLightScatter;
}

// Turbidity toward the body colour, folded into the mul/add pair. Reuses the sheet's own
// function instead of restating its formula: it is LINEAR in its colour argument, so evaluating
// it at the two basis colours recovers the exact pair it would have produced - white against a
// black body colour gives (1 - opacity), black against the real body colour gives inscatter *
// opacity. One source of truth, and the compiler folds both calls.
void WebGpuWaterFogApplyTurbidity(float3 inscatter, inout float3 fogMul, inout float3 fogAdd)
{
    float3 keep = ApplyWaterOpacityTintedClarity(float3(1.0, 1.0, 1.0), float3(0.0, 0.0, 0.0),
                                                 WEBGPU_WATER_FOG_API_CLEAR_CLARITY);
    float3 tint = ApplyWaterOpacityTintedClarity(float3(0.0, 0.0, 0.0), inscatter,
                                                 WEBGPU_WATER_FOG_API_CLEAR_CLARITY);
    fogMul *= keep;
    fogAdd = fogAdd * keep + tint;
}

// The medium for one transparent fragment/vertex at 'worldPos'. Outputs the mul/add pair
// described in the header. Each of the four terms carries its own gate, so the pair collapses to
// identity exactly when every water feature the fragment sits in is off or it is out of the water.
void WebGpuWaterFogTransparent(float3 worldPos, float3 lightDir, float3 sunColor,
                               out float3 fogMul, out float3 fogAdd)
{
    // Extinction + lit in-scatter over the wet path: the particles' own maths, through the
    // ALWAYS entry point - priced whenever the fog FEATURE is on, not only while the
    // fullscreen pass is armed. A rerouted prop bypasses the sheet's refraction, so from
    // above the water it must carry its own tint even on disarmed frames (the sheet would
    // otherwise have provided it); the sprites keep their armed-gated wrapper untouched.
    ParticleUnderwaterFogAlways(worldPos, lightDir, sunColor, fogMul, fogAdd);

    // Light lost travelling straight DOWN from the surface - the term every OTHER consumer of a
    // submerged point applies (WaterReceiver, WaterTerrain, the chunk/exclusion walls, and the
    // fullscreen fog on both its passes). It is NOT gated on the fog feature: it has its own
    // master switch inside DownwellingAttenuation, which also returns identity above the line,
    // so an unlit-but-submerged prop still sinks into the dark exactly like the terrain beside it.
    // Multiplies BOTH halves, mirroring the fog's absorb pass (scene * pathTrans * depthAtten)
    // and its in-scatter pass (inscatter * depthAtten).
    float3 downwelling = DownwellingAttenuation(worldPos.y, _UnderwaterSurfaceY);
    fogMul *= downwelling;
    fogAdd *= downwelling;

    // Lamp glow is added AFTER the downwelling multiply and BEFORE turbidity, the same order the
    // fullscreen fog and the sheet use: a local lamp never crossed the surface, so the sun's
    // depth darkening does not apply to it - but murk still swallows it.
    fogAdd += WebGpuWaterFogSceneLampGlow(worldPos);

    // Turbidity last, on the whole medium, exactly where the sheet applies it to its refracted
    // view (WaterSurfaceFragStages' from-above and from-below stages both close on this call).
    if (!WebGpuWaterFogViewCrossesSurface(worldPos)) return;
    float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - worldPos);
    float3 inscatter = WaterInscatterColor(viewDirWS, lightDir, sunColor, 0.0);
    WebGpuWaterFogApplyTurbidity(inscatter, fogMul, fogAdd);
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
