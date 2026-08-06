// WebGpuWater - surface false-colour debug view (WaterSurfaceDebug.hlsl).
// Answers, per pixel and from inside the shader itself, the questions that screenshots of the final
// image cannot: which reflection path this fragment was allowed to take, WHICH RENDERER owns it
// (base sheet / near-field patch / which clipmap ring), and what the planar mirror is actually
// being sampled at. Reading the C# gates is not the same as reading what the GPU received - a
// renderer that never gets a MaterialPropertyBlock silently falls back to the MATERIAL ASSET's
// values, and that difference is invisible in a beauty shot.
//
// Driven by the _WaterDebugMode global (WaterDebugView.cs). 0 = off, and every consumer is behind
// a UNIFORM branch, so a shipped build with the component absent pays one comparison per pixel.
// Both references ship an equivalent (Crest _DEBUG_VISUALIZE_MASK, KWS its debug modes).
#ifndef WATER_SURFACE_DEBUG_INCLUDED
#define WATER_SURFACE_DEBUG_INCLUDED

// _WaterDebugMode and every WATER_DEBUG_* ordinal live in WaterDebugMode.hlsl: the fullscreen fog
// ships its own views off the same selector, and two private copies of the list would be two
// places for it to drift from WaterDebugView.Mode.
#include "WaterDebugMode.hlsl"
// The containment bounds the headroom view measures against - ONE definition, shared with the
// sim pass that actually enforces them (WaterSim.compute, Sanitize).
#include "WaterSimLimits.hlsl"

// Summed-RGB below which a mirror texel counts as "nothing was rendered here" (view 6). Low, so a
// genuinely dark reflection is not mistaken for an empty one.
#define MIRROR_EMPTY_THRESHOLD 0.02

// Distinct hue per renderer so overlapping sheets are obvious: coincident draws that should be
// resolved to ONE owner show as two colours interleaved across the same water.
float3 WaterDebugRendererColor()
{
    // The base sheet and the near-field patch carry no clipmap flag; each clipmap ring carries a
    // distinct _PatchDepthBias (WaterVolume.OceanClipmap.cs derives it per level), so the bias
    // doubles as a level id without any new uniform.
    if (_IsClipmap < 0.5) return (_IsPatch > 0.5) ? float3(0.0, 0.6, 1.0)   // near-field patch
                                                  : float3(0.35, 0.35, 0.35); // base sheet
    float level = saturate(_PatchDepthBias * 4.0);
    return float3(level, 1.0 - level, 0.5 * frac(_PatchDepthBias * 16.0));
}

// Fraction of a bound at which the field counts as CLAMPED rather than merely loaded. The whole
// question this view answers is "is the containment firing", and a red that only gets redder
// cannot show the moment it starts.
#define SIM_HEADROOM_CLAMPED 0.999
// Neighbour motion above which a dead texel reads as a REPAIR rather than as still water.
#define SIM_HEADROOM_RESET_EPSILON 1e-6

// How much room the ripple sim has left before Sanitize() contains it, read at this fragment's own
// sim texel. Point-sampled at the texel CENTRE, never filtered: the reset fingerprint is exactly
// zero in a single texel, and any interpolation smears it into its live neighbours and hides the
// one thing the view exists to show.
float3 WaterDebugSimHeadroom(float2 simUV)
{
    if (any(simUV < 0.0) || any(simUV > 1.0)) return float3(0.0, 0.0, 0.0); // outside the sim window

    float2 texel = _WaterTexel.xy;
    float2 centre = (floor(simUV * _WaterTexel.zw) + 0.5) * texel;
    float4 state = tex2Dlod(_WaterTex, float4(centre, 0, 0));

    float heightLoad   = saturate(abs(state.r) / WATER_SIM_MAX_ABS_HEIGHT);
    float velocityLoad = saturate(abs(state.g) / WATER_SIM_MAX_ABS_VELOCITY);
    if (max(heightLoad, velocityLoad) >= SIM_HEADROOM_CLAMPED) return float3(1.0, 1.0, 1.0);

    // Sanitize writes EXACTLY (0, 0) into a texel that went non-finite, and moving water is never
    // exactly zero - so a dead texel ringed by moving ones is a repair, not still water.
    float neighbourMotion = abs(tex2Dlod(_WaterTex, float4(centre + float2(texel.x, 0.0), 0, 0)).r)
                          + abs(tex2Dlod(_WaterTex, float4(centre - float2(texel.x, 0.0), 0, 0)).r)
                          + abs(tex2Dlod(_WaterTex, float4(centre + float2(0.0, texel.y), 0, 0)).r)
                          + abs(tex2Dlod(_WaterTex, float4(centre - float2(0.0, texel.y), 0, 0)).r);
    float wasReset = (state.r == 0.0 && state.g == 0.0 && neighbourMotion > SIM_HEADROOM_RESET_EPSILON)
                   ? 1.0 : 0.0;

    return float3(heightLoad, velocityLoad, wasReset);
}

// Tint painted where the fragment falls OUTSIDE the sim window, so the window's rectangle is
// visible as a shape rather than inferred from where the foam stops.
#define FOAM_MASK_OUTSIDE_TINT float3(0.25, 0.0, 0.25)

// What the water actually reads out of the foam buffer at this pixel, in three separable channels:
//   RED   - the RAW buffer value, no window fade. Is there foam here AT ALL?
//   GREEN - the value the surface really gets (raw x fade). What is actually delivered?
//   BLUE  - the window fade alone: 1 well inside, ramping down over _SimEdgeFadeTexels, 0 outside.
// Reading it: black = the buffer is empty here, so this is a GENERATION problem and the render side
// is innocent. Red with no green = foam exists and the edge fade is eating it. Red AND green = the
// foam is present and delivered, so whatever is wrong is downstream of this read. Magenta = outside
// the window entirely, where no foam can exist by construction.
float3 WaterDebugFoamMask(float2 simUV)
{
    if (any(simUV < 0.0) || any(simUV > 1.0)) return FOAM_MASK_OUTSIDE_TINT;

    float raw = SampleFoamMaskBilinear(simUV);
    float fade = FoamWindowFade(simUV);
    return float3(raw, raw * fade, fade);
}

// ---- Sim-window view -----------------------------------------------------------------
// Everything the ripple sim owns lives inside this rectangle, and everything outside it is analytic
// water that no interaction can reach. Drawn as a SHAPE so its size, where it sits relative to the
// boat, and how wide its fade band really is are all read directly instead of inferred from where
// effects stop.
#define SIM_WINDOW_OUTSIDE       float3(0.06, 0.06, 0.08)  // dark, so the scene stays readable
#define SIM_WINDOW_INTERIOR      float3(0.0, 0.55, 0.15)   // green: full-strength sim
#define SIM_WINDOW_EDGE_BAND     float3(0.85, 0.15, 0.0)   // red: the fade band eating the signal
#define SIM_WINDOW_CENTRE_MARK   float3(0.0, 0.9, 1.0)     // cyan cross at the window centre
#define SIM_WINDOW_CENTRE_HALF   0.006   // half-width of the centre cross, in UV
#define SIM_WINDOW_TEXEL_CHECK   0.12    // contrast of the per-texel checker
#define SIM_WINDOW_CHECKER_STEP  0.5     // one checker square per sim texel

float3 WaterDebugSimWindow(float2 simUV)
{
    if (any(simUV < 0.0) || any(simUV > 1.0)) return SIM_WINDOW_OUTSIDE;

    // The SAME fade the foam mask and SampleRipple use, so the band drawn here is the band that
    // actually attenuates them - not a second opinion about where the border is.
    float fade = FoamWindowFade(simUV);
    float3 color = lerp(SIM_WINDOW_EDGE_BAND, SIM_WINDOW_INTERIOR, fade);

    // One checker square per sim texel: the grid's real world density, countable on screen. This is
    // the number that decides how coarse a ripple can be, and it is invisible in every other view.
    float2 texelPos = simUV * _WaterTexel.zw;
    float checker = fmod(floor(texelPos.x) + floor(texelPos.y), 2.0);
    color *= 1.0 + (checker - SIM_WINDOW_CHECKER_STEP) * SIM_WINDOW_TEXEL_CHECK;

    // Centre cross: where the window is ANCHORED. On a boat-focused window this should sit on the
    // hull - if it lags behind or leads, the follow target or its offset is the thing to look at.
    float2 fromCentre = abs(simUV - 0.5);
    if (min(fromCentre.x, fromCentre.y) < SIM_WINDOW_CENTRE_HALF) return SIM_WINDOW_CENTRE_MARK;

    return color;
}

// ---- Ripple field view ---------------------------------------------------------------
// The sim's own state, in WORLD units so it reads the same on a 1 m pond and a 100 m-deep sea:
// heights and velocities are stored in pool units (world / extent.y), and a view that skipped that
// conversion would fade to black on exactly the deep bodies worth inspecting.
//   RED   - crest, height above rest
//   BLUE  - trough, height below rest
//   GREEN - speed, how hard the water is moving (the wake's ENERGY, which outlives its shape)
// Still water is BLACK, so anything visible is something the sim was told to do. A wake reads as
// red/blue bands with a green core; grid-frequency noise reads as a red/blue checker at texel scale.
#define RIPPLE_FIELD_HEIGHT_REFERENCE   0.25   // world metres of displacement at full channel
#define RIPPLE_FIELD_VELOCITY_REFERENCE 0.05   // world metres per step at full channel

float3 WaterDebugRippleField(float2 simUV)
{
    if (any(simUV < 0.0) || any(simUV > 1.0)) return float3(0.0, 0.0, 0.0);

    // Point-sampled at the texel CENTRE: the filtered read hides exactly the texel-to-texel
    // structure this view exists to expose.
    float2 centre = (floor(simUV * _WaterTexel.zw) + 0.5) * _WaterTexel.xy;
    float4 state = tex2Dlod(_WaterTex, float4(centre, 0, 0));

    float verticalExtent = VolumeExtentSafe().y;
    float heightMeters = state.r * verticalExtent;
    float speedMeters = abs(state.g) * verticalExtent;

    float crest = saturate(max(0.0, heightMeters) / RIPPLE_FIELD_HEIGHT_REFERENCE);
    float trough = saturate(max(0.0, -heightMeters) / RIPPLE_FIELD_HEIGHT_REFERENCE);
    float speed = saturate(speedMeters / RIPPLE_FIELD_VELOCITY_REFERENCE);
    return float3(crest, speed, trough);
}

// Returns true when the debug view owns this pixel; 'color' is then the final output.
// poolPos / rippleSourcePos address the sim exactly as EvaluateSurfaceGeometry does.
bool WaterDebugColor(float4 screenPos, float3 normalWS, float3 poolPos, float3 rippleSourcePos,
                     out float3 color)
{
    color = float3(0.0, 0.0, 0.0);
    if (_WaterDebugMode < 0.5) return false;

    int mode = (int)(_WaterDebugMode + 0.5);
    // Fog ordinals belong to the fullscreen pass (WaterFogDebug.hlsl), which replaces the whole
    // frame - the surface must leave those pixels alone or both would paint the same mode.
    if (mode >= WATER_DEBUG_FOG_FIRST && mode <= WATER_DEBUG_FOG_LAST) return false;
    if (mode == WATER_DEBUG_SIM_HEADROOM)
    {
        color = WaterDebugSimHeadroom(RippleSimUV(poolPos, rippleSourcePos));
        return true;
    }
    if (mode == WATER_DEBUG_FOAM_MASK)
    {
        color = WaterDebugFoamMask(RippleSimUV(poolPos, rippleSourcePos));
        return true;
    }
    if (mode == WATER_DEBUG_SIM_WINDOW)
    {
        color = WaterDebugSimWindow(RippleSimUV(poolPos, rippleSourcePos));
        return true;
    }
    if (mode == WATER_DEBUG_RIPPLE_FIELD)
    {
        color = WaterDebugRippleField(RippleSimUV(poolPos, rippleSourcePos));
        return true;
    }
    if (mode == WATER_DEBUG_REFLECTION_GATE)
    {
        // RED = SSR on, GREEN = planar on, BLUE = real refraction on - AS THE SHADER READS THEM.
        // Any water that stays red after unticking SSR is a renderer missing its property block.
        color = float3(_UseSSR, _UsePlanar, _RealRefraction);
        return true;
    }
    if (mode == WATER_DEBUG_RENDERER_ID)
    {
        color = WaterDebugRendererColor();
        return true;
    }
    if (mode == WATER_DEBUG_PLANAR_UV)
    {
        // The exact UV the planar mirror is sampled at. A band or a discontinuity here IS the
        // artifact; smooth means the sampler is innocent and the cause is upstream.
        float2 uv = ScreenUV(screenPos);
        uv += mul((float3x3)UNITY_MATRIX_V, normalWS).xy * _ReflectionDistortion;
        color = float3(frac(uv * 8.0), 0.0); // x8 so a sub-percent shift is still visible
        return true;
    }
    if (mode == WATER_DEBUG_VIEW_NORMAL)
    {
        color = mul((float3x3)UNITY_MATRIX_V, normalWS) * 0.5 + 0.5;
        return true;
    }
    if (mode == WATER_DEBUG_RAW_MIRROR)
    {
        // The mirror RT itself, undecorated: no wave nudge, no roughness mip, no aniso smear, no
        // parallax. Whatever is wrong here was rendered wrong by PlanarMirror.cs and no amount of
        // sampler work can repair it. Read it as an image: the scene should appear upside-down,
        // filling the frame, with the horizon at the same screen height as the real one.
        color = tex2Dlod(_PlanarReflectionTex, float4(ScreenUV(screenPos), 0.0, 0.0)).rgb;
        return true;
    }
    if (mode == WATER_DEBUG_MIRROR_EMPTY)
    {
        // MAGENTA wherever the mirror holds (near) nothing - the reflection camera rendered no
        // geometry and no sky there. Large magenta regions mean the RT is the problem: wrong
        // frustum, wrong oblique clip plane, or culling that removed the scene.
        float3 mirror = tex2Dlod(_PlanarReflectionTex, float4(ScreenUV(screenPos), 0.0, 0.0)).rgb;
        color = (dot(mirror, float3(1.0, 1.0, 1.0)) < MIRROR_EMPTY_THRESHOLD)
              ? float3(1.0, 0.0, 1.0) : mirror;
        return true;
    }
    return false;
}

#endif // WATER_SURFACE_DEBUG_INCLUDED
