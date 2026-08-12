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
// but LargeBodyWaveHeight chains into ShoreSample (2 x tex2Dlod, WaterShore.hlsl - zero under
// WATER_STRIP_SHORE) and OceanFftDisplacementShore (WaterLargeWaves.hlsl). Price ONE field
// evaluation before budgeting anything (audit 2026-08-11, corrected from the "roughly six fetches"
// this header claimed for years):
//
//   analytic / periodic FFT ocean : 1 SampleLevel per cascade      ->  4 source reads
//   APERIODIC ocean (P4 tiling on): 3 taps + 3 direction-map reads  -> 24 source reads
//
// and note where the multipliers are. SurfaceHeightAtXZChopInvertedVertical runs the field THREE
// times (a fixed point), so a single classification point costs 3 evaluations - which is why it
// now hands back the vertical read its first iteration already computed instead of letting callers
// buy a fourth. The fog composites pay a classification per pixel, twice per frame, and the
// meniscus pass a third time.
//
// The crossing MARCH is no longer part of that budget and the old figure here (~290 fetches per
// fullscreen pixel, "the single largest mobile/WebGPU cost in the package") is obsolete: since the
// F3 height-RT work every march and refine sample is SurfaceSignedGapRT - one tex2Dlod of a 256^2
// R16F - so the 16-step march plus its 8-step refine is 26 cheap taps, on the minority of pixels
// with no prepass sample. Optimise the CLASSIFICATION, not the march.
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


// Significant height (metres) of the whole open-water field, wind sea and swell in quadrature -
// the CPU's WaterVolume.OffshoreSignificantHeight, published verbatim. This is the ONLY metre
// scale the FFT sea carries; _LargeWaveAmplitude is a dimensionless multiplier ON it, not a
// height. Unset (0) on anything the publisher has not run for, which is exactly the fallback
// SurfaceHeightBand below wants: the analytic term takes over and nothing changes.
float _OffshoreSignificantHeight;
float _ChunkBoundaryEnabled;
float _ChunkBoundaryWidth;
float _ChunkEdgeWaveHeight;

float ChunkBoundaryHeightWeight(float2 poolXZ)
{
    if (_ChunkBoundaryEnabled < 0.5) return 1.0;
    float3 extent = VolumeExtentSafe();
    float edgeDistance = min((1.0 - abs(poolXZ.x)) * extent.x,
                             (1.0 - abs(poolXZ.y)) * extent.z);
    float interior = smoothstep(0.0, max(_ChunkBoundaryWidth, 1e-4), edgeDistance);
    return lerp(_ChunkEdgeWaveHeight, 1.0, interior);
}


// Displaced world-space surface height at a WORLD xz: the single source of truth for the wavy
// waterline. Rest plane (via the volume transform, so extent.y + rotation are exact, matching
// TryGetAnalyticWaterline) + wind-wave layer + open-water swell/FFT. Pools: the swell is a no-op
// (_LargeBody = 0), so this reduces to the wind-wave surface over the flat pool top.
float SurfaceHeightAtXZ(float2 worldXZ)
{
    // Map to pool xz at the rest plane; the surface shader samples the wind waves off this xz.
    float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
    float2 poolXZ = poolAtRest.xz;

    // ONE shared coordinate rule with the surface + foam glue (WindWaveSampleXZ, WaterWaves.hlsl).
    float2 windSampleXZ = WindWaveSampleXZ(poolXZ, worldXZ);
    // Wind-wave height is authored in pool units; lift it to world through the full transform,
    // exactly as the vertex path does (PoolToWorld of the displaced pool point).
    float surfaceY = PoolToWorld(float3(poolXZ.x, WaveHeight(windSampleXZ), poolXZ.y)).y;

    // Open-water swell/FFT is authored in WORLD metres and layered on top (no-op for pools).
    if (_LargeBody > 0.5) surfaceY += LargeBodyWaveHeight(worldXZ);
    return _VolumeCenter.y + (surfaceY - _VolumeCenter.y) * ChunkBoundaryHeightWeight(poolXZ);
}

// Signed height of a world point above its local displaced surface (>0 in air, <=0 underwater).
float SurfaceSignedGap(float3 world)
{
    return world.y - SurfaceHeightAtXZ(world.xz);
}

// ---- Chop-aware classification gap ---------------------------------------------------
// SurfaceHeightAtXZ reads the field STRAIGHT DOWN, but the large-body layer displaces
// horizontally (FFT chop / Gerstner pinch): the crest drawn over this xz was SOURCED metres
// away, so a vertical read can disagree with the rendered surface by whole wave heights in a
// heavy sea. For pixels with NO rasterised prepass sample (sky above the wave silhouette,
// the near-clip strip, off-mesh rays) that lie decided the waterline - fog painted over open
// sky just before submersion, blinking as each crest rolled through. Fixed-point inversion
// of the horizontal displacement (Crest's InvertDisplacement move): find the SOURCE xz whose
// displaced column actually lands here and read the height THERE. Two correction steps plus
// the final read = 3 field evaluations, paid only at classification points (near plane /
// carve exit), which share nearly one xz across the whole screen - cache-hot by construction.
// Ponds and bounded bodies skip the loop entirely (_LargeBody = 0); the wind-wave layer
// stays a vertical read (its ripples carry no horizontal displacement worth inverting).
// verticalOut receives the STRAIGHT-DOWN height at worldXZ - byte-identical to what
// SurfaceHeightAtXZ(worldXZ) returns, for FREE. The fixed point's first iteration runs at
// srcXZ = worldXZ (see the assignment below), so it already evaluates the vertical field; every
// caller that wants both answers used to pay a second full evaluation for the one it discarded.
// That was 1 of the 4 field evaluations a classification costs, and on an aperiodic FFT ocean one
// evaluation is 4 cascades x 15 source reads - see the fog audit, 2026-08-11.
// The recentring below is applied to verticalOut and NOT to the inverted return value, because
// that is the existing split: SurfaceHeightAtXZ:81 recentres, this function never has.
float SurfaceHeightAtXZChopInvertedVertical(float2 worldXZ, out float verticalOut)
{
    float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
    float2 poolXZ = poolAtRest.xz;
    float2 windSampleXZ = WindWaveSampleXZ(poolXZ, worldXZ);
    float surfaceY = PoolToWorld(float3(poolXZ.x, WaveHeight(windSampleXZ), poolXZ.y)).y;
    // The wind-wave layer is shared by both answers; keep it before the large-body add mutates it.
    float windWaveY = surfaceY;
    // Pond default: with no large-body layer the vertical read IS the wind-wave surface, which is
    // also what SurfaceHeightAtXZ returns there.
    float verticalHeight = 0.0;
    if (_LargeBody > 0.5)
    {
        float2 srcXZ = worldXZ;
        float height = 0.0;
        // [loop], NOT [unroll]: each iteration inlines the ENTIRE analytic field (16 Gerstner
        // components + the surf cosh chain + shore + the FFT cascade branch), and this function is
        // itself inlined at every fog / god-ray / meniscus call site. Unrolled, the optimizer chewed
        // 3x that code at ~28 call sites - the measured ~500 s WaterUnderwaterFog compile
        // (2026-08-10). The loop-form costs two jumps per classification point at runtime; every
        // texture read inside is explicit-LOD (tex2Dlod / SampleLevel), so the loop is legal.
        [loop]
        for (int i = 0; i < 3; i++)
        {
            ShoreData shore = ShoreSample(srcXZ);
            SurfWaveSample surf = EvaluateSurfWaves(srcXZ, shore.depth, shore.sdfDist,
                                                    shore.toShore, shore.slopeTan,
                                                    shore.influence, _SurfBeatTime);
            float2 disp;
            LargeBodyWaveHeightDispShore(srcXZ, shore, surf, height, disp);
            // A select, not a branch: iteration 0 ran at srcXZ == worldXZ, so ITS height is the
            // vertical read. Capturing it costs one move; recomputing it costs a field evaluation.
            verticalHeight = (i == 0) ? height : verticalHeight;
            srcXZ = worldXZ - disp;
        }
        surfaceY += height;
    }
    // Recentred exactly as SurfaceHeightAtXZ:81 does, so a caller can substitute this for a second
    // call to it on any body kind, chunk boundaries included.
    verticalOut = _VolumeCenter.y
                + ((windWaveY + verticalHeight) - _VolumeCenter.y) * ChunkBoundaryHeightWeight(poolXZ);
    return surfaceY;
}

// Chop-inverted height only - the shape every existing call site uses. A thin wrapper so the
// fused version above has ONE implementation; the discarded out param folds away.
float SurfaceHeightAtXZChopInverted(float2 worldXZ)
{
    float verticalIgnored;
    return SurfaceHeightAtXZChopInvertedVertical(worldXZ, verticalIgnored);
}

// Signed gap against the chop-inverted height - the CLASSIFICATION twin of SurfaceSignedGap.
// The marches keep the cheap vertical read on purpose (40 steps x 3 field evaluations would
// be ruinous, and the mask wins anyway: a span the mask zeroes never paints).
float SurfaceSignedGapChopInverted(float3 world)
{
    return world.y - SurfaceHeightAtXZChopInverted(world.xz);
}

// Both gaps at one point from ONE solve: the chop-inverted gap that decides which medium the
// point is in, and the vertical gap whose screen derivative gives the feather its calm width.
// The pair is exactly what ArmWeight and the meniscus fragment each used to buy with two solves.
float SurfaceSignedGapChopInvertedPair(float3 world, out float verticalGap)
{
    float verticalY;
    float invertedY = SurfaceHeightAtXZChopInvertedVertical(world.xz, verticalY);
    verticalGap = world.y - verticalY;
    return world.y - invertedY;
}

// Camera-following top-down height authority for FAR classifications and march samples.
// The lens-level mask deliberately remains analytic: a 2 m lattice cannot represent its
// centimetre-range crossing, while the far field benefits from replacing repeated wave maths.
#define WATER_HEIGHT_RT_RESOLUTION 256
#define WATER_HEIGHT_RT_WINDOW_SIZE 512.0
#define WATER_HEIGHT_RT_FEATHER_METERS 16.0
sampler2D _WaterHeightRT;
// xy = snapped world-XZ centre, z = half extent, w = validity. One atomically refreshed
// frame prevents a stale texture and a fresh transform (or the reverse) from ever mixing.
float4 _WaterHeightRTFrame;

float HeightRTFeatherWeight(float2 worldXZ)
{
    float edgeDistance = _WaterHeightRTFrame.z
                       - max(abs(worldXZ.x - _WaterHeightRTFrame.x),
                             abs(worldXZ.y - _WaterHeightRTFrame.y));
    return _WaterHeightRTFrame.w * saturate(edgeDistance / WATER_HEIGHT_RT_FEATHER_METERS);
}

float SampleHeightRTWorldY(float2 worldXZ)
{
    float2 uv = (worldXZ - _WaterHeightRTFrame.xy) / (_WaterHeightRTFrame.z * 2.0) + 0.5;
    return _VolumeCenter.y + tex2Dlod(_WaterHeightRT, float4(uv, 0.0, 0.0)).r;
}

float HeightRTSurfaceY(float2 worldXZ, float flatFallbackY)
{
    return lerp(flatFallbackY, SampleHeightRTWorldY(worldXZ), HeightRTFeatherWeight(worldXZ));
}

float SurfaceSignedGapRT(float3 world, float flatFallbackY)
{
    return world.y - HeightRTSurfaceY(world.xz, flatFallbackY);
}

// How far the CAMERA must be from its local surface before the accurate waterline solve can be
// skipped in favour of one height-RT tap. The only thing this has to exceed is the RT's own
// interpolation error against the surface it was rendered FROM - a 2 m lattice of the real
// displaced mesh, so sub-texel error is a fraction of a metre even in a steep sea. Four is
// generous. RAISE IT if the fog or the meniscus ever pops as a crest approaches; the only cost of
// raising it is that the expensive path starts from further away.
#define WATERLINE_RT_SKIP_MARGIN_METERS 4.0

// The waterline classification, collapsed to one texture tap for the frames where it cannot matter.
//
// WHY IT IS SOUND: every consumer classifies a point on the NEAR PLANE, a patch a few tens of
// centimetres across. So when the camera is metres from its own local surface, EVERY classification
// point is on the same side by the same large margin - the coverage feather is saturated at 0 or 1
// across the whole screen and the meniscus band is off screen entirely. The three iterations of
// chop inversion that produced that saturated answer were pure cost. The height RT is precisely the
// authority for this question (it IS the displaced surface, rasterised), and one tap answers it.
//
// WHY THE TEST READS THE CAMERA AND NOT THE POINT: it must be UNIFORM. Both consumers take a screen
// derivative of the value this feeds, and a per-pixel branch could split a quad exactly where the
// two paths disagree - which is where derivatives stop being defined. Sampling at the camera's own
// xz makes the decision identical for every lane by construction. The value returned still uses the
// caller's own y, so the gap stays smooth across the screen and its derivative stays meaningful.
//
// Returns false when the RT is unavailable - not recorded this frame, or the camera sits in the
// window's feather - which falls back to the accurate path. Never a wrong answer, only a slower one.
bool WaterlineFarFromSurface(float3 classifyPoint, out float farGap)
{
    farGap = 0.0;
    if (HeightRTFeatherWeight(_WorldSpaceCameraPos.xz) < 1.0) return false;
    float surfaceY = SampleHeightRTWorldY(_WorldSpaceCameraPos.xz);
    if (abs(_WorldSpaceCameraPos.y - surfaceY) <= WATERLINE_RT_SKIP_MARGIN_METERS) return false;
    farGap = classifyPoint.y - surfaceY;
    return true;
}

// ---- Displaced-surface height envelope ----------------------------------------------
// Conservative half-band (metres) around the rest plane that brackets every height the displaced
// surface can reach this frame: the swell reach (an amplitude multiple), the surf-front crest
// reach (fronts shoal + break well above the swell), plus a pad for wind-wave chop. Moved here
// from WaterUnderwaterFog.shader so "how far from the rest plane can the surface be" has exactly
// one home: the fog sizes its crossing-march band with it, and the god-ray pass early-outs above
// the ceiling it implies before paying any surface fetches. Widening it costs march steps /
// early-outs, never correctness; narrowing it below a real crest clips a crossing.
#define SURFACE_BAND_AMPLITUDES 3.0
#define SURFACE_BAND_PAD_METERS 2.0
// Crest reach as a multiple of SIGNIFICANT height, for the FFT term below. A Gaussian sea is
// Rayleigh-distributed, giving H_max ~ 1.86 * Hs over a ~1000-wave record and a crest of about
// half that, ~0.93 * Hs. The rest is headroom for horizontal choppiness, which sharpens crests
// ABOVE the linear height the spectrum was normalised to. Bounding, not descriptive: this may
// only ever be raised, never trimmed toward the 0.93 the linear theory alone would justify.
#define SURFACE_BAND_CREST_REACH 1.2

float SurfaceHeightBand()
{
    // Surf fronts shoal + break to crests well above the swell (H <= _SurfAmplitude * setAmp_max
    // * _SurfGreens; see WaterSurfWaves EvaluateSurfWaves), so a swell-only band would sit BELOW
    // a tall shore crest. SURF_SETAMP_JITTER_MAX is the set-jitter ceiling the compute itself
    // uses - the SAME constant (via the WaterSurfWaves include above), not a hand copy.
    // Inert (0) when surf is off.
    float surfReach = (_SurfActive > 0.5)
                    ? _SurfAmplitude * SURF_SETAMP_JITTER_MAX * max(_SurfGreens, SURF_MIN_GREENS)
                    : 0.0;
    // TWO wave scales, because two generators author height in different units, and the band has
    // to bound BOTH. On the ANALYTIC generator the amplitude IS the metre scale (the chop band
    // sums to ~0.58 m per unit amplitude, so 3x is a ~5x conservative bound) - that term is
    // unchanged, and it is what keeps pools and bounded bodies byte-identical. On the FFT path
    // the metres live in the sea state and the amplitude multiplies it, so an amplitude-only
    // band bounded NOTHING: a 15 m sea at the default amplitude 1 produced a 5 m band, three
    // times narrower than its own crests, and the header contract above says narrowing below a
    // real crest clips a crossing. Taking the max of both means this can only ever WIDEN, which
    // that same contract states is always safe.
    float analyticReach = abs(_LargeWaveAmplitude) * SURFACE_BAND_AMPLITUDES;
    float seaReach = _OffshoreSignificantHeight * abs(_LargeWaveAmplitude)
                   * SURFACE_BAND_CREST_REACH;
    return max(max(analyticReach, seaReach), surfReach) + SURFACE_BAND_PAD_METERS;
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
