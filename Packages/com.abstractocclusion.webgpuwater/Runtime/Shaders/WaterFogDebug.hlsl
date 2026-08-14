// WebGpuWater - false-colour views for the FULLSCREEN underwater fog (WaterFogDebug.hlsl).
// Answers, per pixel, the questions a beauty shot of the fog cannot: did this pixel get painted
// at all, WHERE was it classified against the waterline, and WHICH of the six span paths priced
// it. The surface views (WaterSurfaceDebug.hlsl) ended a long guessing phase in four screenshots
// by showing what the GPU actually received instead of what the C# gates said; this is the same
// instrument pointed at the other half of the waterline stack.
//
// HOW IT REACHES THE SCREEN - no new render pass, no C# change. The fog already draws two
// fullscreen passes back to back: Absorb blends `Zero SrcColor` (dst *= src) and Inscatter blends
// `One One` (dst += src). So Absorb returning 0 WIPES the frame and Inscatter then WRITES the
// false colour into it. The pair is the replacement.
//
// THE ONE THING TO KNOW WHEN READING THESE: both passes only record while
// WaterVolume.UnderwaterFogActive (WaterUnderwaterFogPass.RecordRenderGraph). So the view showing
// AT ALL means the fog pass ran this frame, and the view VANISHING back to normal shading means it
// did not. That is not a limitation to work around - it is a free reading of the arm gate, and the
// arm gate is a suspect in its own right: it is a CPU near-plane-CORNER test against a ~1-2 frame
// stale FFT readback (WaterVolume.Underwater.cs ComputeCameraSubmerged), while every mask below is
// live and per-pixel.
//
// WHAT ELSE COULD PAINT OVER A VIEW, and what is done about it. Three passes draw AFTER the fog -
// the god-ray shafts (fog +1), the particle/pond-foam redraw (fog +2) and the waterline meniscus -
// and all three stand down while a fog view is selected (WaterDebugView.FogViewActive). That is not
// tidiness: the shafts add WATER-TINTED light concentrated near the waterline, so they tinted every
// view green exactly where the interesting boundary is, and read as a finding. Consequence: foam
// and spray disappear entirely in a fog view, because their queue-time draw is already skipped
// while the fog is armed. POST-PROCESSING is the one thing still downstream and cannot be gated
// from here - turn it off before trusting a hue.
//
// A SPLINTER OF THE FOG PASS, NOT A LIBRARY - same status as WaterSurfaceFragStages.hlsl. It reads
// _CameraDryVolume and _UnderwaterFogSimple from WaterUnderwaterFog.shader's own uniform block, so
// it must be included AFTER them, and it is included by that shader alone.
#ifndef WEBGPUWATER_FOG_DEBUG_INCLUDED
#define WEBGPUWATER_FOG_DEBUG_INCLUDED

#include "WaterDebugMode.hlsl" // _WaterDebugMode + the shared ordinals

// _CameraUnderwater ("the eye is IN WATER", published by PublishUnderwater) belongs to the
// owning shader's uniform block: normal fog coverage now uses it as well as this debug view.

// ---- Which span path priced this pixel (view WATER_DEBUG_FOG_PATH_BRANCH) ------------------
// One id per RETURN in the ocean/pond span functions. Set at the return rather than inferred
// afterwards: the branch structure is exactly the thing under suspicion, so a view that
// re-derives it could agree with a wrong answer.
#define WATER_FOG_BRANCH_NONE          0 // nothing ran (a bug in the wiring, not in the water)
#define WATER_FOG_BRANCH_POND          1 // bounded body: the ray clipped to the pool box
#define WATER_FOG_BRANCH_FLAT_SIMPLE   2 // Simple tier: closed-form flat waterline
#define WATER_FOG_BRANCH_PREPASS_AIR   3 // rendered sheet seen from AIR -> pathLen forced to 0
#define WATER_FOG_BRANCH_PREPASS_WET   4 // submerged eye, span ends AT the rendered sheet
#define WATER_FOG_BRANCH_ANALYTIC      5 // no prepass sample: both-under / both-above early-out
#define WATER_FOG_BRANCH_CARVE_MARCH   6 // no prepass (carve discarded the sheet) -> OceanWavyPath
// id 7 (FLAT_FALLBACK) retired 2026-08-13: its span path was unreachable - the mixed no-prepass
// ray always takes OceanWavyPath now (see the fog shader's partial-submersion note). Id kept
// vacant so WAVY_MARCH screenshots stay comparable across sessions.
#define WATER_FOG_BRANCH_WAVY_MARCH    8 // no-prepass tier: OceanWavyPath's own crossing march

// Per-invocation scratch. A static rather than an out parameter threaded through
// UnderwaterSegment and its four span functions: the marker must not change any shipped
// signature, because a signature change to confirmed-good code is a change the next reader has
// to re-verify, and this is an instrument, not a feature.
static int g_WaterFogDebugBranch = WATER_FOG_BRANCH_NONE;

void WaterFogDebugBranch(int branch) { g_WaterFogDebugBranch = branch; }

// The raw signed prepass depth this pixel's ownership test read (+ above sheet / - under sheet /
// 0 none). Same scratch-static shape and the same reason as the branch id above: an instrument
// must not change a shipped signature, and it must record what the span rule ACTUALLY saw.
static float g_WaterFogDebugSheetSigned = 0.0;

void WaterFogDebugSheetSigned(float signedEyeDepth) { g_WaterFogDebugSheetSigned = signedEyeDepth; }

// ---- Reading thresholds ---------------------------------------------------------------------
// At or above this the pass paints the pixel at full strength; at or below MASKED_MAX it
// contributes literally nothing. Between them is the feather, which is where the two edges of the
// waterline are supposed to overlap.
#define FOG_DEBUG_PAINTED_MIN  0.98
#define FOG_DEBUG_MASKED_MAX   0.02
// World metres below which a span counts as "no water on this ray at all".
#define FOG_DEBUG_SPAN_EPSILON 1e-4
// World metres below which the carve push moved the classification point nowhere, i.e. it fell
// back to the near plane.
#define FOG_DEBUG_PUSH_EPSILON 1e-3
// Backdrop for pixels the pass correctly leaves alone, dark enough that any hue reads as a finding.
#define FOG_DEBUG_IDLE_GREY    0.08

float3 WaterFogDebugBranchColor()
{
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_POND)          return float3(1.0, 0.5, 0.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_FLAT_SIMPLE)   return float3(0.5, 0.5, 0.5);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_PREPASS_AIR)   return float3(0.0, 1.0, 0.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_PREPASS_WET)   return float3(0.2, 0.4, 1.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_ANALYTIC)      return float3(1.0, 1.0, 0.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_CARVE_MARCH)   return float3(1.0, 0.0, 1.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_WAVY_MARCH)    return float3(0.0, 1.0, 1.0);
    return float3(0.0, 0.0, 0.0); // NONE
}

// THE ONE THIS WAS BUILT FOR. Magenta wherever the pass computed a real wet span and then threw
// it away on the waterline mask - a pixel that has water in front of it and no fog on it. That is
// the "empty zone between the fog and the water" as a measurement instead of an impression, and
// its shape says which mask is wrong: a band hugging the waterline is a feather/derivative
// problem, a screen-wide sheet is the classification point being wrong for every pixel at once.
//
// The carve is deliberately checked FIRST and coloured separately: a span eaten by
// ExclusionRayLength is the carve doing its job, not a hole, and conflating the two would make
// every correctly dry room read as the bug.
//
// ORANGE closes a BLIND SPOT this view shipped with. The prepass-from-air rule zeroes the span
// before it is ever measured, so a pixel suppressed by it arrived here as wetSpanLen 0 and was
// painted "no water on this ray" - indistinguishable from open sky, and invisible to the hole
// hunt. It is checked from the BRANCH id instead, ahead of every span test. Orange over open
// water is CORRECT (the surface shader owns a from-above pixel and already applied the column's
// absorption); orange inside a carve is worth a hard look, because the premise of that rule is an
// AIR path from the eye to the sheet, which a submerged room does not give it.
float3 WaterFogDebugUnpainted(float armWeight, float wetSpanLen, float pathLen)
{
    // NaN witness (2026-08-11). This view showed its carve BLUE in a scene whose exclusion
    // counts are 0 - and with dryLen 0, pathLen == wetSpanLen algebraically, so that blue is
    // unreachable with REAL numbers. It IS reachable through a NaN: every comparison against
    // NaN is false (skipping the grey no-water exit) and max(NaN - 0, 0) returns 0 on GPU
    // minmax, landing exactly on the carve return. Paint the non-finite itself, most
    // specific first:
    //   YELLOW = the prepass sheet sample is non-finite -> the corruption arrived IN the RT
    //            (surface displacement / wave field) - hunt upstream of the fog;
    //   GREEN  = the span/mask numbers this pass computed are non-finite -> fog-side math.
    if (isnan(g_WaterFogDebugSheetSigned) || isinf(g_WaterFogDebugSheetSigned))
        return float3(1.0, 1.0, 0.0);
    if (isnan(wetSpanLen) || isinf(wetSpanLen) || isnan(pathLen) || isinf(pathLen)
        || isnan(armWeight) || isinf(armWeight))
        return float3(0.0, 1.0, 0.0);
    if (g_WaterFogDebugBranch == WATER_FOG_BRANCH_PREPASS_AIR)
    {
        // SPLIT BY THE WATERLINE MASK, because "orange over open water is CORRECT" made this
        // view blind to the case it should shout about: the from-air rule zeroing a span the
        // MASK says must be painted. That is not a difference of opinion about ownership, it is
        // a contradiction - the same one WaterFogDebugMaskVsSpan reports - and it is what an
        // edge-on coincident-sheet coin toss looks like from here. RED = suppressed while the
        // mask wanted full fog. Orange keeps its old meaning: from-air over a dry pixel.
        if (armWeight >= FOG_DEBUG_PAINTED_MIN) return float3(1.0, 0.0, 0.0);
        return float3(1.0, 0.45, 0.0);
    }
    if (wetSpanLen <= FOG_DEBUG_SPAN_EPSILON)
        return float3(FOG_DEBUG_IDLE_GREY, FOG_DEBUG_IDLE_GREY, FOG_DEBUG_IDLE_GREY); // no water here
    if (pathLen    <= FOG_DEBUG_SPAN_EPSILON) return float3(0.0, 0.0, 0.5);   // carved away (correct)
    if (armWeight  <  FOG_DEBUG_MASKED_MAX)   return float3(1.0, 0.0, 1.0);   // MASKED AWAY - the hole
    if (armWeight  >= FOG_DEBUG_PAINTED_MIN)  return float3(1.0, 1.0, 1.0);   // painted at full strength
    return float3(0.0, armWeight, armWeight);                                 // the feather band
}

// WHICH SHEET TWIN WON, straight off the prepass RT, before any span rule interprets it.
// _OceanSurfaceEyeDepth is written as LinearEyeDepth * visibleSide from one canonical two-sided
// surface rasterization. RED is the air-facing side and BLUE the underwater-facing side. Because
// the ownership pass no longer submits coincident material twins, an opposite-colour island now
// identifies an actual displaced triangle/LOD continuity fault rather than depth-equal overwrite.
float3 WaterFogDebugSheetSide()
{
    if (g_WaterFogDebugSheetSigned > 0.0) return float3(1.0, 0.0, 0.0); // ABOVE sheet -> fog suppressed
    if (g_WaterFogDebugSheetSigned < 0.0) return float3(0.0, 0.3, 1.0); // UNDER sheet -> normal span
    return float3(0.0, 0.0, 0.0);                                       // no surface rasterised here
}

// Where ArmWeight classified this pixel against the waterline. Red is the failure case worth
// hunting: the eye is inside a dry carve, so the near-plane point sits in AIR below sea level and
// says nothing about the water being looked at - but ExclusionPushToExit moved it nowhere (the
// near-plane point is not inside any ANALYTIC volume, e.g. it poked through the wall, or the
// volume is Mesh-shaped and the analytic push skips it), so the classification fell back to that
// useless point anyway.
float3 WaterFogDebugClassifySource(float classifyPushDist)
{
    if (_CameraDryVolume < 0.5)                    return float3(0.0, 0.3, 1.0); // near plane (open water)
    if (classifyPushDist > FOG_DEBUG_PUSH_EPSILON) return float3(0.0, 1.0, 0.0); // pushed to the carve exit
    return float3(1.0, 0.0, 0.0);                                                // fell back to the near plane
}

// Mask against span - the two halves of "should this pixel be fogged", shown disagreeing, with
// everything they agree on flattened to neutral grey so only the faults carry colour. This is the
// decisive view: it does not care WHY the span is zero, only that the waterline mask wanted fog
// somewhere the pass had none to give (or the reverse), which is the empty zone stated as a
// contradiction rather than as a symptom.
//
// A bounded pond returns mask 1 for every pixel by design (it is a finite fog volume meant to be
// seen from outside), so every ray that misses its box reads RED. Expected, not a fault - read
// this view on oceans.
float3 WaterFogDebugMaskVsSpan(float armWeight, float pathLen)
{
    bool hasSpan = pathLen > FOG_DEBUG_SPAN_EPSILON;
    if (armWeight >= FOG_DEBUG_PAINTED_MIN && !hasSpan) return float3(1.0, 0.0, 0.0); // wanted fog, none exists
    if (armWeight <  FOG_DEBUG_MASKED_MAX  &&  hasSpan) return float3(1.0, 0.0, 1.0); // span exists, mask killed it
    if (hasSpan) return float3(0.35, 0.35, 0.35);                                     // agree: fogged
    return float3(FOG_DEBUG_IDLE_GREY, FOG_DEBUG_IDLE_GREY, FOG_DEBUG_IDLE_GREY);     // agree: untouched
}

// Returns true when a fog debug view owns this pixel; 'color' is then the frame.
// armWeight        : the waterline mask this pixel ended up with.
// classifyPushDist : metres ArmWeight's classification point was pushed out to a carve exit.
// wetSpanLen       : in-water span BEFORE the exclusion carve is subtracted, world metres.
// pathLen          : the same span AFTER the carve - what the fog actually integrates.
bool WaterFogDebugColor(float armWeight, float classifyPushDist, float wetSpanLen, float pathLen,
                        out float3 color)
{
    color = float3(0.0, 0.0, 0.0);
    if (_WaterDebugMode < 0.5) return false;

    int mode = (int)(_WaterDebugMode + 0.5);
    // A surface view, on either side of the fog block: not ours to paint.
    if (mode < WATER_DEBUG_FOG_FIRST || mode > WATER_DEBUG_FOG_LAST) return false;

    if (mode == WATER_DEBUG_FOG_ARM_WEIGHT)
    {
        float w = saturate(armWeight);
        color = float3(w, w, w); // black = this pass contributes nothing here
        return true;
    }
    if (mode == WATER_DEBUG_FOG_UNPAINTED)
    {
        color = WaterFogDebugUnpainted(armWeight, wetSpanLen, pathLen);
        return true;
    }
    if (mode == WATER_DEBUG_FOG_CLASSIFY_SRC)
    {
        color = WaterFogDebugClassifySource(classifyPushDist);
        return true;
    }
    if (mode == WATER_DEBUG_FOG_PATH_BRANCH)
    {
        color = WaterFogDebugBranchColor();
        return true;
    }
    if (mode == WATER_DEBUG_FOG_GATES)
    {
        // Flat screen colour: the CPU state AS THE GPU RECEIVED IT. R = eye in water,
        // G = eye in a dry carve, B = Simple tier. _UnderwaterFogArmed needs no channel - the
        // view only draws while the pass is armed (see the header note).
        color = float3(_CameraUnderwater, _CameraDryVolume, _UnderwaterFogSimple);
        return true;
    }
    if (mode == WATER_DEBUG_FOG_MASK_VS_SPAN)
    {
        color = WaterFogDebugMaskVsSpan(armWeight, pathLen);
        return true;
    }
    if (mode == WATER_DEBUG_FOG_SHEET_SIDE)
    {
        color = WaterFogDebugSheetSide();
        return true;
    }
    return false;
}

#endif // WEBGPUWATER_FOG_DEBUG_INCLUDED
