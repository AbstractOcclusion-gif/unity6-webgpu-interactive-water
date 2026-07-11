// WebGpuWater - shared foam look + decay helpers.
//
// ONE home for the constants and idioms every foam-like element uses, so a retune
// applies everywhere at once. Included by:
//   WaterSurface.shader   - ocean whitecap + interactive/pond foam shading
//   FoamParticles.shader  - GPU foam/spray particle shading
//   SplashParticles.shader- Shuriken splash (crown + droplets) shading
//   OceanFft.compute      - whitecap accumulation (split decay)
//   WaterSim.compute      - ripple-sim foam accumulation (split decay)
// Pure functions + #defines only: no uniforms, no samplers, safe in fragment and
// compute stages alike.
#ifndef WATER_FOAM_COMMON_INCLUDED
#define WATER_FOAM_COMMON_INCLUDED

// ---- Foam lighting ----------------------------------------------------------------
// Wrapped diffuse keeps the unlit side of foam from going black (foam scatters light),
// plus a flat ambient floor from the sky. Matched across the surface layers and both
// particle systems so every foam-like element sits in the same light.
#define FOAM_LIGHT_WRAP  0.4
#define FOAM_AMBIENT     0.35

// Wrapped-diffuse factor from a precomputed N.L (callers with no meaningful normal,
// e.g. splash sheets, pass the sun's height _LightDir.y = "upward-facing foam").
float FoamWrappedDiffuseNdotL(float ndotl)
{
    return saturate(ndotl * (1.0 - FOAM_LIGHT_WRAP) + FOAM_LIGHT_WRAP);
}

float FoamWrappedDiffuse(float3 normal, float3 lightDir)
{
    return FoamWrappedDiffuseNdotL(dot(normal, lightDir));
}

// Lit foam colour: albedo over an ambient floor plus the wrapped sun term.
float3 FoamLitColor(float3 albedo, float3 sunColor, float wrappedDiffuse)
{
    return albedo * (FOAM_AMBIENT + sunColor * wrappedDiffuse);
}

// ---- Erosion dissolve -------------------------------------------------------------
// As a foam sprite's lifetime envelope decays, the sprite ERODES - its thin/dark
// regions drop out first - instead of uniformly ghosting out. Same trick as the
// surface foam's lace, applied to particle alpha.
#define EROSION_SOFTNESS 0.35

float FoamErosionAlpha(float spriteAlpha, float envelope)
{
    return saturate((spriteAlpha - (1.0 - envelope)) / EROSION_SOFTNESS);
}

// ---- Foam particle life envelope ----------------------------------------------------
// Quick fade-in, smooth fade-out beginning at a fraction of the particle's life. Shared
// by the quad render (FoamParticles.shader) and the density splat (WaterFoamParticles
// .compute) so a particle's screen-space weight always matches what its quad would show.
#define FOAM_PARTICLE_FADE_IN_SECONDS 0.25
#define FOAM_PARTICLE_FADE_OUT_START  0.55

float FoamParticleEnvelope(float age, float life)
{
    float fadeIn = saturate(age / FOAM_PARTICLE_FADE_IN_SECONDS);
    float fadeOut = 1.0 - smoothstep(life * FOAM_PARTICLE_FADE_OUT_START, life, age);
    return fadeIn * fadeOut;
}

// ---- Split ("bi-exponential") decay -----------------------------------------------
// Both foam sims decay in two regimes blended by the local foam AMOUNT, because a
// single exponential can't make bright fresh foam collapse quickly AND thin residual
// lace linger. This helper is the shared blend; note the two callers parameterize it
// with OPPOSITE semantics, both deliberate:
//   OceanFft.compute  blends decay RATES:      dense deposited foam decays SLOWER
//                     (windrows linger)         -> valueAtReference = the slow rate.
//   WaterSim.compute  blends SURVIVAL factors: thick fresh foam decays FASTER
//                     (collapse, then lace)     -> valueAtReference = the fresh rate.
// Returns lerp(valueAtZero, valueAtReference, saturate(amount / referenceAmount)).
float FoamDecayBlend(float amount, float referenceAmount, float valueAtZero, float valueAtReference)
{
    return lerp(valueAtZero, valueAtReference, saturate(amount / referenceAmount));
}

#endif // WATER_FOAM_COMMON_INCLUDED
