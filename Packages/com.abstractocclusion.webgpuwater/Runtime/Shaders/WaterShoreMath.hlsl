// WebGpuWater - shore-substrate PURE MATH (constants, structs, sampler-free functions).
//
// WHY THIS FILE EXISTS: WaterShore.hlsl declares sampler2D objects, so compute kernels can never
// include it - and that forced three hand-synced copies of the shore-field math to grow:
//   WaterSim.compute            (foam injection)  - field UV + border feather + SDF dir decode
//   WaterParticleCommon.hlsl    (particle fetch)  - the same, plus its own deep-sentinel/feather
//                                                   constants (PARTICLE_SHORE_*)
//   WaterFoamParticles.compute  (ShoalWeightSim)  - a full replica of ShoalWeight()
// This header is the ONE home for everything shore that does NOT touch a sampler, following the
// WaterShared.hlsl contract (#defines, structs and pure math - no texture/sampler objects), so
// vertex, fragment and compute stages can all include it. WaterShore.hlsl includes it and keeps
// only the sampler declarations + the sampling functions. Compute consumers keep their own
// Texture2D<...>/SampleLevel fetches (they own their texture objects) and feed the fetched values
// through the functions below.
//
// Contract deviation, on purpose: the four shoal-transform knob uniforms (_ShoreShoalDepth,
// _ShoreCompression, _ShoreGreens, _ShoreWarpReach) live here WITH the functions that read them.
// Plain float uniforms are valid in every stage including compute - only sampler objects were the
// blocker - and keeping knob + math together means a compute calling ShoalWeight() binds the same
// uniform name the surface does instead of re-declaring it (which is exactly how the replicas
// started drifting).
#ifndef WEBGPUWATER_SHORE_MATH_INCLUDED
#define WEBGPUWATER_SHORE_MATH_INCLUDED

// Deep-water sentinel: a depth this large attenuates nothing (used off-field / when no field is baked).
#define SHORE_DEEP_SENTINEL 1e9
// Outer UV fraction of the field over which shore influence feathers to zero, so the field's
// rectangular border never prints a seam into the swell (B5 in the coastline audit).
#define SHORE_BORDER_FEATHER 0.08
// Length floor on the SDF's decoded toward-shore direction: below this the encoded
// direction has cancelled to ~zero (shore ridge / field centre singularities) and normalizing
// would blow up, so the decode reports it degenerate instead.
#define SHORE_SDF_DIR_EPSILON 1e-4

// Shore-transform shaping (ShoalWeight / ShoreGreenGain / ShoreWarpExtra). ALL height-affecting:
// LargeWaveField.cs runs the same math on the CPU for buoyancy, so every value below is mirrored
// there as a const and guarded by WaterWaveConstantsValidator - retune HERE and the C# twin,
// never inline. (WaterLargeWaves.hlsl's per-component shoal ramp still carries an inline copy of
// the factor/epsilon - candidates to re-point at these defines when that file is next touched.)
#define SHORE_SHOAL_WAVELENGTH_FACTOR 2.0  // shoal ramp = saturate(this * depth / wavelength)
                                           // (Crest's saturate(2*depth/L) recovery rule)
#define SHORE_WAVELENGTH_EPSILON      1e-3 // wavelength floor under the shoal-ramp divide
#define SHORE_BAND_EPSILON            1e-3 // _ShoreShoalDepth floor (0 = "shoaling off", never /0)
// Inner fraction of the shoal band: ShoalWeight's hand-over to full strength STARTS here and the
// Green's-law gain fades out across the same stretch (ShoreGreenGain), so amplification and
// attenuation trade over one shared band instead of two competing depth thresholds.
#define SHORE_BAND_INNER_FRACTION     0.35
#define SHORE_GREEN_MIN_DEPTH         0.05 // metres; depth floor under the Green's-law divide
#define SHORE_GREEN_EXPONENT          0.25 // Green's law: amplitude ~ depth^(-1/4)
#define SHORE_WARP_REACH_MIN          1.0  // metres; floor on _ShoreWarpReach (degenerate publish)
#define SHORE_MIN_GREENS              1.0  // _ShoreGreens floor: a growth CAP below 1 would attenuate

// P1 shoal-transform knobs (published by WaterShoreDepthField.Publish from the body settings).
// Declared here - not in WaterShore.hlsl - because the functions below read them and computes
// must be able to call those functions against the same uniform names.
float _ShoreShoalDepth;   // depth (m) over which waves ATTENUATE; full strength beyond it (0 = no shoaling)
// The band Green's law amplifies over, kept SEPARATE from the attenuation band above. They were one
// value until the attenuation band started following the sea state (WaterVolume.ShoreShoalDepthEffective
// floors it at twice the offshore significant height, so a big sea begins flattening further out). That
// floor is a request to flatten SOONER - but the same number also drives ShoreGreenGain as (band/depth)^0.25,
// so deepening it silently amplified the near-shore waves as well, which is the exact opposite. This one
// stays on the authored coastal profile so the two jobs can no longer drag each other.
float _ShoreGreenBandDepth;
float _ShoreCompression;  // phase-compression gain near shore (crests bunch as waves slow)
float _ShoreGreens;       // Green's-law amplification cap (1 = off; shoaling waves GROW before dying)
float _ShoreWarpReach;    // compression e-folding reach (m) = 2 x the surf front wavelength - ONE
                          // curve shared with SurfWarpDistance (WaterSurfWaves.hlsl), so the ambient
                          // swell and the surf fronts bunch identically instead of sliding against
                          // each other in the hand-over band. Published by WaterShoreDepthField.

// Everything the wave/foam/swash code needs from the shore substrate at one world xz, from ONE
// depth fetch + ONE sdf fetch. 'influence' is the feathered in-field weight: every shore effect
// multiplies by it, so leaving the field is always seamless.
struct ShoreData
{
    float depth;      // still-water column depth (m); DEEP sentinel off-field / unbaked
    float sdfDist;    // signed distance to shore (m, + in water); 0 off-field / unbaked
    float2 toShore;   // unit direction toward the waterline ((0,0) off-field / unbaked)
    float slopeTan;   // local beach slope tan(beta) (SURF-PHYS); 0 off-field / unbaked
    float influence;  // 1 fully inside the field .. 0 at/outside the feathered border
};

// The inert (deep open-water) sample: one definition, so every early-out and every caller's
// default is provably fully-initialized for the compiler's definite-assignment analysis.
ShoreData ShoreDataInert()
{
    ShoreData s;
    s.depth = SHORE_DEEP_SENTINEL;
    s.sdfDist = 0.0;
    s.toShore = float2(0.0, 0.0);
    s.slopeTan = 0.0;
    s.influence = 0.0;
    return s;
}

// World XZ -> shore-field UV for an axis-aligned field given by its world centre + half-extent.
// Parameterized on the frame because the surface binds it as _ShoreDepthCenter/_ShoreDepthSize
// (WaterShore.hlsl) while the sim/particle computes bind the SAME field as _ShoreFieldCenterSim/
// _ShoreFieldSizeSim - one projection, two binding names.
float2 ShoreFieldUVFrom(float2 worldXZ, float2 fieldCenter, float2 fieldHalfSize)
{
    return (worldXZ - fieldCenter) / (2.0 * fieldHalfSize) + 0.5;
}

// Feathered in-field weight: 1 well inside, ramping to 0 across the outer border band and staying
// 0 outside (saturate of a negative edge distance). Product of the two axes keeps corners smooth.
float ShoreFieldInfluence(float2 uv)
{
    float2 edge = min(uv, 1.0 - uv);                     // distance to the closest border, per axis
    float2 ramp = saturate(edge / SHORE_BORDER_FEATHER); // 0 at/off the border, 1 inside the band
    return ramp.x * ramp.y;
}

// Decode the SDF texture's RG channels (toward-shore direction, 0..1-encoded) into a unit vector.
// Returns false - with toShore forced to zero - where the encoded direction is degenerate, so
// fail-fast callers (the particle fetch) and press-on callers (surface, sim) share ONE decode and
// ONE epsilon instead of three inline ternaries.
bool ShoreDecodeToShore(float2 encodedRG, out float2 toShore)
{
    float2 dir = encodedRG * 2.0 - 1.0;
    float len = length(dir);
    bool valid = len > SHORE_SDF_DIR_EPSILON;
    toShore = valid ? dir / len : float2(0.0, 0.0);
    return valid;
}

// Depth-based shoaling weight for one wave component: 0 at the waterline, ramping to 1 within the
// near-shore band. Short waves recover shallower than long ones (Crest's saturate(2*depth/L)). The
// _ShoreShoalDepth clamp forces full strength past that depth so ONLY the near-shore band
// attenuates - and (P0 fix, B3 in the audit) blends in over a smoothstep instead of a hard lerp
// ramp, so there is no derivative kink / visible wall at exactly the band depth. Negative depth
// (dry land) clamps to 0, so waves vanish rather than punch through the seabed. _ShoreShoalDepth
// of 0 (or unpublished) disables shoaling entirely (weight 1 everywhere).
float ShoalWeight(float depth, float wavelength)
{
    float clamped = max(depth, 0.0);
    float raw = saturate(SHORE_SHOAL_WAVELENGTH_FACTOR * clamped
                         / max(wavelength, SHORE_WAVELENGTH_EPSILON));
    float band = max(_ShoreShoalDepth, SHORE_BAND_EPSILON);
    // Smooth hand-over to full strength across the outer part of the band.
    float deep = smoothstep(SHORE_BAND_INNER_FRACTION * band, band, clamped);
    return lerp(raw, 1.0, deep);
}

// Green's-law shoaling gain: waves GROW as the water column shrinks (a ~ depth^-1/4), clamped by
// the _ShoreGreens art cap so the growth never runs away, and faded right at the waterline where
// the breaking/whitewash layer takes over. 1 offshore / off-field: pure amplification-only term.
float ShoreGreenGain(ShoreData shore)
{
    float band = max(_ShoreGreenBandDepth, SHORE_BAND_EPSILON);
    if (shore.influence <= 0.0 || shore.depth >= band) return 1.0;
    float d = max(shore.depth, SHORE_GREEN_MIN_DEPTH);
    float green = min(pow(band / d, SHORE_GREEN_EXPONENT), max(_ShoreGreens, SHORE_MIN_GREENS));
    // Fade the gain out over the last stretch to the waterline: attenuation (ShoalWeight) wins the
    // final metres, so amplified waves hand over to the surf/whitewash layer instead of spiking.
    green = lerp(green, 1.0, saturate(1.0 - shore.depth / (SHORE_BAND_INNER_FRACTION * band)));
    return lerp(1.0, green, shore.influence);
}

// Extra phase distance from near-shore compression: waves slow down in shallow water, so crests
// bunch together. Implemented as a smooth warp of the shore-distance field (monotonic for gains
// up to ~2, so crests never fold back), added to a component's plane phase scaled by its own
// shoaling response - long waves feel the bottom (and compress) sooner than short ones.
// The reach is the SAME curve the surf fronts use (SurfWarpDistance adds s*c*exp(-s/(2L)) of
// extra distance) - one warp, so swell crests and front crests bunch in lockstep near the beach.
float ShoreWarpExtra(ShoreData shore)
{
    if (shore.influence <= 0.0 || _ShoreCompression <= 0.0) return 0.0;
    float s = max(shore.sdfDist, 0.0);
    float reach = max(_ShoreWarpReach, SHORE_WARP_REACH_MIN);
    return _ShoreCompression * s * exp(-s / reach) * shore.influence;
}

#endif // WEBGPUWATER_SHORE_MATH_INCLUDED
