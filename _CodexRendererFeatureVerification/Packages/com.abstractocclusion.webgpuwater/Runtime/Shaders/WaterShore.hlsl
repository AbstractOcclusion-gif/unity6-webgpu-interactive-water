// WebGpuWater - shoreline substrate uniforms + sampling helpers (Layer A/B).
//
// The world-frame seabed-depth field and its jump-flood SDF are baked by WaterShoreDepthField and
// published as globals. This header is the single place that declares those uniforms and the helpers
// that read them, so the vertex wave code (shoaling), the fragment (debug, whitewash, swash), and any
// other shader can share one definition. tex2Dlod everywhere so the samplers are valid in the vertex
// stage.
//
// SAMPLER-BEARING HALF ONLY: everything shore that is pure math (the SHORE_* constants, the
// ShoreData struct, ShoalWeight / ShoreGreenGain / ShoreWarpExtra, the field-UV/feather/SDF-decode
// helpers) lives in WaterShoreMath.hlsl so compute kernels - which cannot include sampler2D
// declarations - share the one implementation instead of hand-copying it.
//
// P0 semantics change: _ShoreDepthTex now stores the STILL-WATER COLUMN DEPTH (metres, + = water,
// - = dry land above the level) instead of the seabed's absolute world Y. Half-float precision is
// spent on the small numbers near the waterline - where every consumer needs it - instead of on a
// large absolute height, which banded the shallows (depth = level - seabed subtracted two similar
// big numbers). Every reader (this file, the surface debug, the foam-sim injection) uses the new
// semantics; _ShoreWaterLevel remains published for consumers that need the absolute plane.
#ifndef WEBGPUWATER_SHORE_INCLUDED
#define WEBGPUWATER_SHORE_INCLUDED

#include "WaterShoreMath.hlsl" // SHORE_* constants, ShoreData, shoal/green/warp + field math

// Depth field: R = still-water column depth (m), + in water / - on dry land. SDF field: RG =
// toward-shore direction (0..1), B = signed distance to shore (m, + in water / - on land), A =
// local beach slope tan(beta) (SURF-PHYS breaker physics). Both share one world frame.
sampler2D _ShoreDepthTex;
float4 _ShoreDepthCenter; // world XZ centre of the field (.xy)
float4 _ShoreDepthSize;   // world XZ half-extent of the field (.xy)
float _ShoreDepthValid;   // 1 = a seabed field is baked
float _ShoreDepthDebug;   // 1 = visualize seabed depth on the surface (debug only)
float _ShoreWaterLevel;   // still-water plane world Y used when the field was baked
sampler2D _ShoreSDFTex;   // RG = toward-shore dir (0..1), B = signed distance (m), A = slope tan(beta)
float _ShoreSDFValid;     // 1 = a shoreline SDF is baked
float _ShoreSDFDebug;     // 1 = visualize the SDF on the surface (debug only)
// PER-BODY gate on this shared substrate (published via the property block, default 0): only the
// body that opted into bed depth consumes the shore field, so another body overlapping the field's
// rectangle (a pond next to the terrain lake) can never catch its shoal/surf/swash.
float _ShoreBodyGate;

// P1 shoal-transform knob published alongside the field. Its siblings (_ShoreShoalDepth,
// _ShoreCompression, _ShoreGreens, _ShoreWarpReach) moved to WaterShoreMath.hlsl with the
// functions that read them.
float _ShoreRefraction;   // 0..1: how hard shoaling waves bend toward the shore (crests align to beach)

// World XZ -> shore-field UV through THIS header's binding of the field frame (the computes bind
// the same field as _ShoreFieldCenterSim/_ShoreFieldSizeSim and call ShoreFieldUVFrom themselves).
float2 ShoreFieldUV(float2 worldXZ)
{
    return ShoreFieldUVFrom(worldXZ, _ShoreDepthCenter.xy, _ShoreDepthSize.xy);
}

// One-stop shore fetch. Off-field or unbaked returns the inert ShoreData (deep, no direction,
// zero influence) so every consumer's math collapses to open-water behaviour with no branches.
ShoreData ShoreSample(float2 worldXZ)
{
    ShoreData s = ShoreDataInert();
#ifdef WATER_STRIP_SHORE
    // Compile-time strip (fog passes of shoreless bodies): the runtime gate below returns this
    // same inert value every frame there - the #ifdef makes that PROVABLE to the optimizer, so
    // the two field fetches and every downstream shoal/refraction/surf consumer fold away instead
    // of bloating the variant (the ~600 KB fog fragments, 2026-08-10). Inert is fully initialized
    // (ShoreDataInert), so no definite-assignment warnings appear.
    return s;
#endif
    if (_ShoreDepthValid < 0.5 || _ShoreBodyGate < 0.5) return s;

    float2 uv = ShoreFieldUV(worldXZ);
    float influence = ShoreFieldInfluence(uv);
    if (influence <= 0.0) return s;

    s.influence = influence;
    s.depth = tex2Dlod(_ShoreDepthTex, float4(saturate(uv), 0, 0)).r;
    if (_ShoreSDFValid > 0.5)
    {
        float4 sdf = tex2Dlod(_ShoreSDFTex, float4(saturate(uv), 0, 0));
        ShoreDecodeToShore(sdf.rg, s.toShore); // zero on a degenerate direction - press on
        s.sdfDist = sdf.b;
        s.slopeTan = sdf.a;
    }
    return s;
}

// Still-water column depth (metres) under a world xz - kept for consumers that only need depth.
// Returns a deep sentinel where no field is baked or the point is outside it, so those places
// never shoal.
float ShoreShoalDepth(float2 worldXZ)
{
#ifdef WATER_STRIP_SHORE
    // The compile-time strip ShoreSample has carried since 2026-08-10, applied here too: a
    // stripped variant is by definition a body whose runtime gate below returns the sentinel on
    // every frame, so the fetch was unreachable in effect but still compiled in - and the fog's
    // clarity term calls this once per pixel per pass. Same value, no texture unit touched.
    return SHORE_DEEP_SENTINEL;
#endif
    if (_ShoreDepthValid < 0.5 || _ShoreBodyGate < 0.5) return SHORE_DEEP_SENTINEL;
    float2 uv = ShoreFieldUV(worldXZ);
    if (ShoreFieldInfluence(uv) <= 0.0) return SHORE_DEEP_SENTINEL;
    return tex2Dlod(_ShoreDepthTex, float4(saturate(uv), 0, 0)).r;
}

#endif // WEBGPUWATER_SHORE_INCLUDED
