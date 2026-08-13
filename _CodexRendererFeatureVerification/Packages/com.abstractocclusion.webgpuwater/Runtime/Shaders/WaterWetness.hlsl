// WebGpuWater - THE wetness model: how a dry surface looks where water has touched it.
//
// WHY THIS FILE EXISTS: "wet" was about to be written a third time. The water SURFACE already owns a
// wet LOOK (the swash glaze in WaterSurfaceFragStages) and the surf layer already owns the analytic
// wet LINE (EvaluateSurfSwash in WaterSurfWaves). What existed nowhere was the shading answer to
// "this ground is wet" - so the receiver and the terrain shader would each have grown their own copy,
// and two copies of one look drift into a visible seam exactly where they meet: the waterline.
//
// THE SPLIT OF CONCERNS - read this before adding anything here:
//   * WHERE it is wet is the CALLER's job. Every source (the band above the waterline, the beach
//     swash line, foam coverage) is sampled by the .shader, which owns its own texture declarations,
//     and handed in as a plain scalar.
//   * HOW wet looks is THIS file's job, and it is exactly three effects. Nothing else belongs here.
//
// INCLUDE CONTRACT - deliberately NO includes and NO texture/sampler declarations, matching
// WaterSurfWaves.hlsl. That is what lets a URP shader (TEXTURE2D/SAMPLER style) and a legacy
// sampler2D-style consumer both include it without the declaration collision that already forces
// WaterReceiver.shader to keep its own sim-height sampler instead of including WaterCommon.hlsl.
#ifndef WEBGPUWATER_WETNESS_INCLUDED
#define WEBGPUWATER_WETNESS_INCLUDED

// Water fills the pores and the micro-relief of a rough material. Three things follow, and every
// wet-surface model worth copying is some version of these three:
//   1. it DARKENS and saturates - light entering the film is internally reflected back down into the
//      substrate instead of escaping, so the effect is MULTIPLICATIVE, not a lerp toward grey,
//   2. it gets SMOOTHER - the film is a mirror-flat sheet lying over the roughness,
//   3. its micro-normal FLATTENS - the same sheet, stated geometrically.
// Modelling the darkening as albedo * albedo (rather than a lerp toward black) is what keeps a wet
// surface COLOURED: dark values fall furthest, saturation rises, and the result can never go negative
// or need a clamp. Wet sand going warm-brown instead of grey is this term.
#define WET_POROUS_SQUARE_GAIN 1.0

// Never let a fully wet surface become a perfect mirror: real wet ground keeps some scatter, and the
// Blinn-Phong exponent its consumers use (exp2(s * 10 + 1)) explodes as smoothness approaches 1.
#define WET_SMOOTHNESS_CEILING 0.92

// A band height at or below this is treated as "no band": the feather collapses to the hard
// submersion test, which is the pre-wetness behaviour and must stay reachable.
#define WET_BAND_MIN_HEIGHT 1e-3

// Fraction of the swash wet line over which the drying edge feathers. The film itself reads opaque
// wet; only its TOP edge is soft, which is the same asymmetry the water mesh's glaze has.
#define WET_SWASH_EDGE_FRACTION 0.25

// Below this length the flattened normal is degenerate (mapped and geometric normals cancelled) and
// normalising it would produce NaNs; fall back to the geometric normal, which is always valid.
#define WET_NORMAL_MIN_LENGTH 1e-4

// The per-material response: the same three numbers that make one substrate a beach and another a
// rock. A terrain shader carries one of these per substrate; the receiver carries exactly one.
struct WaterWetLook
{
    float darken;        // 0..1 how far toward the porous-wet albedo at full wetness
    float smoothness;    // target smoothness at full wetness
    float normalFlatten; // 0..1 how far the micro-normal flattens at full wetness
};

// What a consumer gets back. Returned as a struct rather than written through inout parameters so a
// caller cannot apply two of the three effects and silently ship a half-wet surface.
struct WaterWetSurface
{
    float3 albedo;
    float  smoothness;
    float3 normal;
};

// --- WHERE: source weights, each 0..1 -------------------------------------------------------------

// 1 at and below the wet line, easing to 0 'bandHeight' metres above it.
//
// THE WET LINE HANGS OFF THE HIGHER OF THE LIVE SURFACE AND THE STILL PLANE - never off the live
// surface alone. Anchoring on the live surface alone WAS the "wetness does not persist" bug: a crest
// passes, surfaceY falls back to the trough, and ground the water covered a frame ago is bone dry
// again. A crest still wets UPWARD (it raises the line); it simply cannot drag the line back DOWN
// below the level the body actually rests at.
//
// NOTE WHAT THIS IS NOT: a stable band, not a drying trail. It has no memory of a crest that has
// already left - it only refuses to forget the still level. Real drying memory needs state, and the
// place for it is the foam sim's existing decay, not a second timeline invented here.
//
// This is still the CONTINUOUS sibling of the hard submersion test: both are derived from the same
// sampled surface, because a feathered weight and a hard bool taken from one field have to share one
// contour or they disagree along it and print a line. A zero band collapses onto the hard step
// exactly, so the feature is genuinely off at its off value.
// 'wetFloorY' is the lowest the wet line may fall. With no drying memory that is simply the still
// plane (Fix A). With the sim's high-water mark feeding it, it is the drying waterline - which is
// what turns a stable band into a real trail without this function changing at all.
float WaterWetBand(float worldY, float surfaceY, float wetFloorY, float bandHeight)
{
    float wetLine = max(surfaceY, wetFloorY);
    if (bandHeight <= WET_BAND_MIN_HEIGHT) return (worldY <= wetLine) ? 1.0 : 0.0;
    return 1.0 - smoothstep(wetLine, wetLine + bandHeight, worldY);
}

// Beach swash. 'wetLevel' is EvaluateSurfSwash's run-up wet line (metres above the still plane) and
// 'riseAboveStill' is how far this point sits above that plane. Callers must pass the wet line from
// the shared surf model rather than inventing one, so the ground and the water agree by construction.
float WaterWetSwash(float riseAboveStill, float wetLevel)
{
    if (wetLevel <= 0.0) return 0.0;
    return 1.0 - smoothstep(wetLevel * (1.0 - WET_SWASH_EDGE_FRACTION), wetLevel, riseAboveStill);
}

// Foam lying on the ground wets it.
//
// NOTE what this is NOT: it is "wet where foam IS", not a drying trail behind foam that has passed.
// No residual state exists outside the swash model, and inventing one here would be a second,
// disagreeing memory of the same waterline. If a trail is wanted, it belongs in the surf model with
// the wet line, not here.
float WaterWetFoam(float coverage)
{
    return saturate(coverage);
}

// Sources COMPETE, they do not accumulate: each independently answers "is this spot wet", and two of
// them agreeing does not make it wetter than wet. max() also keeps the result continuous wherever
// sources overlap - an additive blend would print a bright seam along every overlap contour.
float WaterWetCombine(float band, float swash, float foam)
{
    return saturate(max(band, max(swash, foam)));
}

// --- HOW: the three effects ------------------------------------------------------------------------

// URP's smoothness -> Blinn-Phong exponent remap. Named because the wet look needs it TWICE (the dry
// and the wet exponent) and the terrain shader will want the identical curve.
#define SPEC_EXPONENT_SCALE 10.0
#define SPEC_EXPONENT_BIAS   1.0

float WaterSpecularExponent(float smoothness)
{
    return exp2(smoothness * SPEC_EXPONENT_SCALE + SPEC_EXPONENT_BIAS);
}

// Blinn-Phong spreads a FIXED peak over a lobe whose width the exponent sets. So raising smoothness
// on its own makes a highlight tighter and HARDER TO CATCH - never brighter - and a wet surface ends
// up reading DULLER than the dry one it replaced. The energy the narrowing lobe should concentrate is
// simply discarded.
//
// The missing factor is the lobe normalization, (n + 2) / 2pi. Only the RATIO between the wet and dry
// exponents matters here - the 2pi, and whatever constant the consumer already folded into its
// specular colour, cancel - so this returns exactly 1.0 when the two exponents agree. A dry surface
// is therefore untouched bit for bit, which is what keeps existing materials safe. Both arguments
// come from exp2(), so both are positive and the denominator can never approach zero: no guard.
float WaterWetSpecularGain(float dryExponent, float wetExponent)
{
    return (wetExponent + 2.0) / (dryExponent + 2.0);
}

float3 WaterWetAlbedo(float3 albedo, float wet, float darken)
{
    float3 porous = albedo * lerp(1.0, albedo, WET_POROUS_SQUARE_GAIN);
    return lerp(albedo, porous, saturate(wet * darken));
}

float WaterWetSmoothness(float smoothness, float wet, float wetSmoothness)
{
    return lerp(smoothness, min(wetSmoothness, WET_SMOOTHNESS_CEILING), saturate(wet));
}

float3 WaterWetNormal(float3 mappedNormal, float3 geometricNormal, float wet, float flatten)
{
    float3 flattened = lerp(mappedNormal, geometricNormal, saturate(wet * flatten));
    float len = length(flattened);
    return (len > WET_NORMAL_MIN_LENGTH) ? (flattened / len) : geometricNormal;
}

// The whole look in one call. A zero 'wet' returns the dry inputs untouched - not merely
// mathematically equal to them - so a material that has not opted in is provably unchanged.
WaterWetSurface WaterApplyWetness(WaterWetLook look, float wet, float3 albedo, float smoothness,
                                  float3 mappedNormal, float3 geometricNormal)
{
    WaterWetSurface o;
    o.albedo = albedo;
    o.smoothness = smoothness;
    o.normal = mappedNormal;
    if (wet <= 0.0) return o;

    o.albedo = WaterWetAlbedo(albedo, wet, look.darken);
    o.smoothness = WaterWetSmoothness(smoothness, wet, look.smoothness);
    o.normal = WaterWetNormal(mappedNormal, geometricNormal, wet, look.normalFlatten);
    return o;
}

#endif // WEBGPUWATER_WETNESS_INCLUDED
