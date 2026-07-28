// WebGpuWater - the displaced water surface height (the wavy waterline), shared.
// ONE source of truth for "where is the surface at this world xz": the rest plane through
// the volume transform (extent.y + rotation exact) + the wind-wave layer + the open-water
// swell/FFT. Split out of WaterUnderwaterFog.shader (verbatim move - any behaviour change
// here is a bug) so the exclusion wall clips at the SAME surface the fog integrates
// against: the wall's flat rest-plane clip left an empty band between the wall top and a
// wave crest on partially submerged volumes.
//
// COST - this is NOT free, despite what this header used to claim ("both wave layers are analytic
// (no texture samples), so fragment-stage use costs ALU only"). The wind-wave layer is analytic ALU,
// but LargeBodyWaveHeight chains into ShoreSample (2 x tex2Dlod, WaterShore.hlsl) and
// OceanFftDisplacementShore (4 x SampleLevel, WaterLargeWaves.hlsl): roughly SIX texture fetches per
// call on an ocean body. Anything calling this in a LOOP pays that per iteration -
// WaterUnderwaterFog's 40-step crossing march plus its 5-step refine is ~290 fetches per fullscreen
// pixel, and it is the single largest mobile/WebGPU cost in the package. Budget accordingly before
// adding another marched consumer; the shore term varies slowly along a view ray and can be hoisted
// out of a march (WaterLargeWaves.hlsl's LargeBodyWaveHeightDispShore documents that trick for the
// vertex path).
#ifndef WEBGPUWATER_WATERLINE_INCLUDED
#define WEBGPUWATER_WATERLINE_INCLUDED

#include "WaterVolume.hlsl"     // WorldToPool / PoolToWorld + _VolumeCenter (rest plane)
#include "WaterWaves.hlsl"      // WaveHeight: wind-wave layer (+ _WaveTime for the swell below)
#include "WaterLargeWaves.hlsl" // LargeBodyWaveHeight: open-water swell/FFT; needs _WaveTime (above)

// Screen-space facet normal from a world position, guarded. ddx/ddy are fragment-only, which is why
// this lives here (WaterShared.hlsl is reachable from compute shaders) and why the two wall shaders
// share it rather than each rolling their own.
//
// TWO ways the raw cross(ddy, ddx) degenerates, and both produced a NaN that reached refract() and
// the fresnel term: an edge-on triangle makes the derivatives parallel so the cross is zero; and on
// a 2x2 quad straddling a mesh silhouette, lanes with no front face hold a constant substitute
// position (the eye) while their neighbours hold a real surface point, so the derivative is garbage.
// 'valid' lets the caller express the second case; the length test catches the first.
float3 SafeFacetNormal(float3 positionWS, bool valid, float3 fallback)
{
    float3 n = cross(ddy(positionWS), ddx(positionWS));
    return (valid && dot(n, n) > DEGENERATE_DIR_EPSILON) ? normalize(n) : fallback;
}

float _OceanWorldWaves; // 1 = sample wind waves in WORLD metres (ocean); 0 = pool xz (pond)

#define WAVE_METERS_MIN 1e-3 // matches WindWaveSampleXZ's guard in WaterSurface.shader

// Displaced world-space surface height at a WORLD xz: the single source of truth for the wavy
// waterline. Rest plane (via the volume transform, so extent.y + rotation are exact, matching
// TryGetAnalyticWaterline) + wind-wave layer + open-water swell/FFT. Pools: the swell is a no-op
// (_LargeBody = 0), so this reduces to the wind-wave surface over the flat pool top.
float SurfaceHeightAtXZ(float2 worldXZ)
{
    // Map to pool xz at the rest plane; the surface shader samples the wind waves off this xz.
    float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
    float2 poolXZ = poolAtRest.xz;

    // Oceans sample the wind waves in WORLD metres (extent-independent) to match WindWaveSampleXZ.
    float2 windSampleXZ = (_OceanWorldWaves > 0.5) ? (worldXZ / max(_WaveMetersPerUnit, WAVE_METERS_MIN))
                                                   : poolXZ;
    // Wind-wave height is authored in pool units; lift it to world through the full transform,
    // exactly as the vertex path does (PoolToWorld of the displaced pool point).
    float surfaceY = PoolToWorld(float3(poolXZ.x, WaveHeight(windSampleXZ), poolXZ.y)).y;

    // Open-water swell/FFT is authored in WORLD metres and layered on top (no-op for pools).
    if (_LargeBody > 0.5) surfaceY += LargeBodyWaveHeight(worldXZ);
    return surfaceY;
}

// Signed height of a world point above its local displaced surface (>0 in air, <=0 underwater).
float SurfaceSignedGap(float3 world)
{
    return world.y - SurfaceHeightAtXZ(world.xz);
}

// ---- Waterline coverage: ONE curve for every consumer -------------------------------
// The fullscreen fog's mask and the exclusion wall's per-fragment classification both answer
// "how much of this pixel is below the waterline". They used to answer it with two hand-rolled
// copies of the same expression, each hard over ONE pixel - and two 1-pixel steps derived from
// DIFFERENT gap variables (the fog's near-plane / carve-exit point, the wall's own fragment) do
// not land on the same pixel. Where they missed each other the frame showed a thin band with
// neither the fog nor the wall in it: the empty zone at the crossing. Sharing the curve makes
// the two edges the same shape by construction, and widening it past one pixel makes a
// half-pixel disagreement cost a fraction of a fragment instead of a whole one.
//
// Both references do exactly this and neither relies on a razor edge: Crest hides its hard
// discard under a meniscus ~11% of screen height, KWS under a 40-80 px blurred tension band.
#define WATERLINE_FEATHER_PIXELS 6.0
// Floor for the screen derivative of the surface gap (degenerate on a view exactly parallel to
// the surface, where the gap is the same at every pixel and the ramp would divide by zero).
#define WATERLINE_GRADIENT_MIN 1e-5
// CEILING on that same derivative, expressed as the widest gap the feather may span. The floor
// alone left the divisor unbounded ABOVE, and the derivative is legitimately huge at grazing
// incidence: the exclusion wall differentiates its own fragment's positionWS, so on a carve
// floor or top face seen edge-on a single pixel covers metres of surface, the feather covers
// tens of metres of gap, and the ramp flattens toward 0.5 across a large screen area - the wall
// painting itself in at half strength instead of resolving at its waterline. Past a wave
// amplitude or so the model the ramp rests on (gap varying linearly across one pixel) has no
// meaning anyway, so clamping there costs nothing that was ever correct. Inert wherever the
// derivative is already sane: this can only ever NARROW a ramp, never widen one.
#define WATERLINE_FEATHER_METERS_MAX 0.5
// Screen pixels the fog's edge is pushed toward the AIR side when the eye is inside a dry carve
// (KWS's over-cover rule: where two masks can miss each other, a slightly thick edge reads as
// water and a gap reads as a hole). Lives here rather than in the fog because the exclusion wall
// mirrors the fog's coverage to hand off against it, and a second copy of the number would be a
// second place for the two edges to drift apart.
#define WATERLINE_CARVE_OVER_COVER_PIXELS 3.0

// surfaceGap  : signed metres above the displaced surface at this pixel's classification point.
// gapPerPixel : fwidth(surfaceGap), taken by the CALLER so the derivative sits in ITS uniform
//               control flow (fwidth is fragment-only and must not be hidden behind a branch).
//               Clamped BOTH ways below - a raw fwidth is bounded neither below (a view
//               parallel to the surface) nor above (grazing incidence).
// overCoverPixels: shift the whole ramp toward the AIR side by this many screen pixels. KWS's
//               rule - when two masks can miss each other, OVER-cover rather than under-cover
//               (gather-max one texel UP, the hole fix, the 10% OBB dilation): a slightly thick
//               edge reads as water, a gap reads as a hole. Pass 0 for an exact edge.
// Coverage at or above which a consumer should treat the pixel's ray as STARTING IN WATER. It is
// the curve's own midpoint, so it tracks the 0.5 crossing wherever that crossing has been moved to
// by an over-cover - which is the whole point of taking the hard test from the WEIGHT rather than
// from the raw gap. `surfaceGap <= 0` looks equivalent and is NOT: it flips at gapPixels 0 while
// the curve crosses 0.5 at gapPixels == overCoverPixels, so the two part company by exactly the
// over-cover. Lives beside the curve so a change to one cannot silently orphan the other.
#define WATERLINE_COVERAGE_WET_MIN 0.5

float WaterlineCoverage(float surfaceGap, float gapPerPixel, float overCoverPixels)
{
    float perPixel = clamp(gapPerPixel, WATERLINE_GRADIENT_MIN,
                           WATERLINE_FEATHER_METERS_MAX / WATERLINE_FEATHER_PIXELS);
    float gapPixels = surfaceGap / perPixel;
    return saturate(0.5 - (gapPixels - overCoverPixels) / WATERLINE_FEATHER_PIXELS);
}

#endif // WEBGPUWATER_WATERLINE_INCLUDED
