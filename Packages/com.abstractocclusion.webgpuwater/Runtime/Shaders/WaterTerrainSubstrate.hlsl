// WebGpuWater - the procedural terrain substrate model: WHICH ground is under this fragment.
//
// Four substrates, derived from geometry rather than a painted splatmap: a terrain that has never
// been painted still reads as a coastline, and - the reason this exists - the beach lands wherever
// the WATER is, automatically, because its band is measured against the waterline rather than a
// texture someone has to keep in sync by hand.
//
// SPLIT OF CONCERNS, same shape as WaterWetness.hlsl:
//   * WHICH substrate is here = this file. Pure geometry -> four weights. No textures, no lighting.
//   * WHAT that substrate looks like = the .shader, which owns the texture declarations.
//   * HOW it changes when wet = WaterWetness.hlsl, unchanged and shared with the receiver.
//
// INCLUDE CONTRACT - no includes, no texture/sampler declarations, so this stays includable from
// either shader style (see WaterWetness.hlsl for why that matters here).
#ifndef WEBGPUWATER_TERRAIN_SUBSTRATE_INCLUDED
#define WEBGPUWATER_TERRAIN_SUBSTRATE_INCLUDED

// The four weights travel as one float4 in THIS order. Kept as a documented convention rather than
// four separate floats so the whole set can be normalised, lerped and passed around in one value.
//   x = SEABED  submerged ground: darker, siltier, permanently wet
//   y = BEACH   the band just above the waterline - sand, shingle, wrack
//   z = ROCK    exposed stone; slope-selected, so it overrides the other three
//   w = GRASS   everything above the beach that is flat enough to hold soil
#define SUBSTRATE_WEIGHT_EPSILON 1e-4

// Below this the triplanar blend is degenerate (a normal with no dominant axis cannot happen on real
// geometry, but a zero/NaN normal from a broken mesh can) and the divide would produce Inf.
#define TRIPLANAR_MIN_WEIGHT_SUM 1e-4

// Substrate selection is TWO independent questions, resolved in this order:
//   1. HEIGHT above the waterline stacks seabed -> beach -> grass, which is just what a coastline is.
//   2. SLOPE then OVERRIDES all three with rock, at any altitude.
//
// SLOPE WINS BECAUSE GRAVITY DOES: loose sand and soil cannot rest on a steep face, so what shows
// there is the rock underneath. Resolving it the other way round - height first, slope as a tie-break
// - paints grass onto vertical cliff walls, which is the single most common way an auto-terrain
// shader gives itself away.
//
// heightAboveWater is metres above the still waterline (negative = submerged). slope01 is 0 on flat
// ground and 1 on a vertical face. The returned weights sum to exactly 1 by construction, so a
// consumer never has to normalise and can never get a dark seam from weights that quietly don't.
float4 WaterTerrainSubstrateWeights(float heightAboveWater, float slope01,
                                    float seabedTop, float beachTop, float heightFeather,
                                    float rockSlope, float slopeFeather)
{
    // Feathers are HALF-widths around each boundary, so a feather of 0 gives a hard line and the
    // boundary itself never moves as the feather is widened - a knob that shifts the thing it is
    // supposed to soften is a knob nobody can tune.
    float hf = max(heightFeather, SUBSTRATE_WEIGHT_EPSILON);
    float aboveSeabed = smoothstep(seabedTop - hf, seabedTop + hf, heightAboveWater);
    float aboveBeach  = smoothstep(beachTop  - hf, beachTop  + hf, heightAboveWater);

    float4 weights;
    weights.x = 1.0 - aboveSeabed;                 // seabed
    weights.y = aboveSeabed * (1.0 - aboveBeach);  // beach
    weights.z = 0.0;                               // rock: not height-selected
    weights.w = aboveBeach;                        // grass

    float sf = max(slopeFeather, SUBSTRATE_WEIGHT_EPSILON);
    float rock = smoothstep(rockSlope - sf, rockSlope + sf, slope01);

    // The three height substrates give up exactly the share rock takes, so the sum stays 1.
    weights *= (1.0 - rock);
    weights.z = rock;
    return weights;
}

// Slope as 0 (flat) .. 1 (vertical) from a world normal. Named because "1 - N.y" reads as an
// arbitrary expression at every call site, and because the clamp matters: an interpolated normal can
// come back slightly longer than unit and drive the smoothstep above outside its range.
float WaterTerrainSlope01(float3 normalWS)
{
    return saturate(1.0 - normalWS.y);
}

// Blend weights for a triplanar projection. 'sharpness' controls how quickly the dominant axis takes
// over: low values cross-fade broadly (soft, but three taps visible as a smear on 45-degree faces),
// high values snap to one axis (crisp, but the seam between axes narrows into a visible line).
float3 WaterTerrainTriplanarWeights(float3 normalWS, float sharpness)
{
    float3 weights = pow(abs(normalWS), sharpness);
    return weights / max(weights.x + weights.y + weights.z, TRIPLANAR_MIN_WEIGHT_SUM);
}

#endif // WEBGPUWATER_TERRAIN_SUBSTRATE_INCLUDED
