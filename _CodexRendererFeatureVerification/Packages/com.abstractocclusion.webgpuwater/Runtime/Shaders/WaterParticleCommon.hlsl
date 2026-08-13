// WebGpuWater - shared GPU-particle plumbing.
//
// ONE home for the idioms every particle stage used to carry as hand-synced copies:
//   WaterFoamParticles.compute  - hash, shore-field fetch
//   FoamParticles.shader        - billboard corner expansion, flipbook atlas cell
// (The surf-roller pair this was also written for - WaterSurfRoller.compute and
//  SurfRollerParticles.shader - was removed with the curl/roller experiment on 2026-07-16.)
// Pure functions + constants; the shore-field block (uniforms + textures) is opt-in via
// WATER_PARTICLE_SHORE_FIELD so draw shaders that never read the field don't declare its
// resources. Safe in vertex, fragment and compute stages alike (SampleLevel only).
#ifndef WATER_PARTICLE_COMMON_INCLUDED
#define WATER_PARTICLE_COMMON_INCLUDED

// Sampler-free shore math (SHORE_DEEP_SENTINEL, ShoreFieldUVFrom, ShoreFieldInfluence,
// ShoreDecodeToShore, ShoalWeight...) - the shared implementations this file used to hand-copy
// as PARTICLE_SHORE_* constants + inline feather/decode math.
#include "WaterShoreMath.hlsl"

#define PARTICLE_TWO_PI 6.28318530718

// ---- Deterministic randomness -------------------------------------------------------
// PCG hash -> float in [0,1). Deterministic per (key, salt): emission dedupes and fixed
// per-particle variants rely on this never being reseeded per frame by the caller.
uint ParticlePcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float Rand01(uint2 p, uint salt)
{
    return ParticlePcg(p.x ^ ParticlePcg(p.y ^ ParticlePcg(salt))) / 4294967296.0;
}

// ---- Procedural billboard expansion --------------------------------------------------
// 6 vertices per particle (two triangles), pulled by SV_VertexID - the one quad expansion
// that works everywhere WebGPU does (no instancing path, no geometry shader).
static const float2 PARTICLE_QUAD_CORNERS[4] =
    { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1) };
static const uint PARTICLE_QUAD_INDICES[6] = { 0, 1, 2, 2, 1, 3 };

float2 ParticleQuadCorner(uint vid)
{
    return PARTICLE_QUAD_CORNERS[PARTICLE_QUAD_INDICES[vid % 6]];
}

// ---- Flipbook atlas cell -------------------------------------------------------------
// A fixed per-seed variant, or an animated flipbook when fps > 0; the seed offsets each
// particle's phase so a group never flips in lockstep. Grid (1,1) = a plain texture.
float2 ParticleFlipbookUv(float2 corner, float2 gridRaw, float seed, float age, float fps)
{
    float2 grid = max(float2(1.0, 1.0), gridRaw);
    float cellCount = grid.x * grid.y;
    float framePos = seed * cellCount + age * fps;
    float variant = fmod(floor(framePos), cellCount);
    float2 cell = float2(fmod(variant, grid.x), floor(variant / grid.x));
    return (corner * 0.5 + 0.5 + cell) / grid;
}

// ---- Layer A shore-field fetch (opt-in: #define WATER_PARTICLE_SHORE_FIELD first) -----
// Same uniform names + binder as the ripple sim's foam injection
// (WaterSimulation.ShoreFoamState.BindTo). Textures are always bound (black fallback);
// every read gates on _ShoreFoamActive. Declared here ONCE for both particle computes.
#ifdef WATER_PARTICLE_SHORE_FIELD

float _ShoreFoamActive;
float _ShoreFoamTime;        // THE MASTER SURF BEAT (matches the surface _SurfBeatTime)
float4 _ShoreFieldCenterSim; // xy = world XZ centre of the Layer A field
float4 _ShoreFieldSizeSim;   // xy = world XZ half-extent of the Layer A field
Texture2D<float>  _ShoreDepthTexSim;  SamplerState sampler_ShoreDepthTexSim;
Texture2D<float4> _ShoreSDFTexSim;    SamplerState sampler_ShoreSDFTexSim;

// One raw shore-field fetch at a world xz: depth, signed shore distance, toward-shore
// direction, beach slope, feathered influence. False = inert (layer off, off-field, or a
// degenerate SDF direction) - the ONE contract both particle systems share (fail-fast on
// a degenerate SDF; the old foam copy pressed on with toShore = 0 and silently diverged
// at SDF singularities). All outs are fully written on every path (WGSL rule).
bool ParticleSampleShoreField(float2 worldXZ, out float depth, out float sdfDist,
                              out float2 toShore, out float tanBeta, out float influence)
{
    depth = SHORE_DEEP_SENTINEL;
    sdfDist = 0.0;
    toShore = float2(0.0, 0.0);
    tanBeta = 0.0;
    influence = 0.0;
    if (_ShoreFoamActive < 0.5) return false;
    // Half-size floor: this fetch keeps a degenerate-publish guard the surface-side ShoreSample
    // does not carry (an all-zero _ShoreFieldSizeSim would divide the UV by zero here).
    float2 fieldHalf = max(_ShoreFieldSizeSim.xy, float2(1e-3, 1e-3));
    float2 fieldUv = ShoreFieldUVFrom(worldXZ, _ShoreFieldCenterSim.xy, fieldHalf);
    influence = ShoreFieldInfluence(fieldUv);
    if (influence <= 0.001) { influence = 0.0; return false; }
    depth = _ShoreDepthTexSim.SampleLevel(sampler_ShoreDepthTexSim, saturate(fieldUv), 0);
    float4 shoreSdf = _ShoreSDFTexSim.SampleLevel(sampler_ShoreSDFTexSim, saturate(fieldUv), 0);
    if (!ShoreDecodeToShore(shoreSdf.rg, toShore)) return false;
    sdfDist = shoreSdf.b;
    tanBeta = shoreSdf.a;
    return true;
}

#endif // WATER_PARTICLE_SHORE_FIELD

#endif // WATER_PARTICLE_COMMON_INCLUDED
