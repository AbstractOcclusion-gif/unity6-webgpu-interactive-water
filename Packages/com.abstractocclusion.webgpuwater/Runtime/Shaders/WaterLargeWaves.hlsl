// WebGpuWater - open-water surface wave field (large-body path).
//
// Phase 3. Purpose: on a large body the pool->world normal map (PoolNormalToWorld) divides the
// normal's xz by the footprint extent, flattening big bodies so screen-space refraction collapses;
// and the pool-unit WaveHeight is scaled by the depth extent rather than authored in metres. This
// header supplies a WORLD-SPACE wave field - height AND matching slope - so open water gets real
// 3D waves and real normals at any body size.
//
// The field is a compact sum of directional deep-water waves in WORLD METRES (wind-biased). It is a
// placeholder GENERATOR behind a stable interface: step 2 replaces the body of LargeBodyWave() with
// an FFT-cascade lookup (Crest / KWS technique) WITHOUT changing the call sites in WaterSurface.
// Height is a pure function of world XZ, so CPU buoyancy can mirror it later with no GPU readback.
#ifndef WEBGPUWATER_LARGE_WAVES_INCLUDED
#define WEBGPUWATER_LARGE_WAVES_INCLUDED

#include "WaterShared.hlsl" // OCEAN_FFT_* cascade layout (shared with the computes)
#include "WaterSeaStateFetch.hlsl"
#if !defined(WATER_DISABLE_OCEAN_APERIODIC)
#include "WaterOceanAperiodic.hlsl"
#endif
// Footprint frame for the bounded-body edge feather (LbwEdgeWeight). Include-guarded, so consumers
// that already pulled WaterVolume.hlsl themselves (all of them today) see it exactly once.
#include "WaterVolume.hlsl"
// Layer B shoaling reads the world-frame seabed depth field (Layer A) to attenuate waves near shore.
#include "WaterShore.hlsl"
// Surf breaker wavefronts (Layer C-analytic): shore-parallel fronts driven by the SDF + depth,
// composited here so EVERY consumer of the large-wave interface (vertex height/chop, fragment
// normal, CPU mirror contract) gets the coastline through the same call sites.
#include "WaterSurfWaves.hlsl"

// Reuses _WaveTime (declared in WaterWaves.hlsl, published every frame) as the shared clock, so the
// open-water waves animate in lockstep with the rest of the water.

// Per-body controls (published via the MaterialPropertyBlock like the rest of the water uniforms):
float _LargeWaveAmplitude;   // overall height/slope multiplier; falls back to 1 when unpublished
float _LargeWaveWindHeading;  // wind direction, radians (the fan of wave directions centres here)
float _LargeWaveChoppiness;   // Gerstner horizontal-displacement scale; falls back to 0 (=smooth sine)
float _LargeWaveDetailSlope; // band-limit: the shortest wavelength the mesh can resolve grows this many
                             // metres per metre of camera distance. 0 = no band-limit (full spectrum).
float _LargeSwellWavelength;  // metres, longest LONG-PERIOD swell component (rolling horizon swell)
float _LargeSwellHeight;      // metres, amplitude of the longest swell component; 0 = no long swell
float _LargeSwellHeading;     // swell travel heading (radians, ABSOLUTE). Published as the wind heading
                              // + the authored offset: real swell comes from a distant storm, not the
                              // local wind. Offset 0 -> equals _LargeWaveWindHeading, bit-identical.
float _LargeWaveEdgeFeather;  // metres of edge feather on a BOUNDED body: the wave field fades to the
                              // rest level over this band inside the footprint border, so the surface
                              // never ends mid-wave as a standing wall of water. 0 = off (pools publish
                              // 0 via _LargeBody anyway; the publisher forces 0 for unbounded oceans,
                              // whose clipmap has no border to guard).

// Edge-guard weight for the whole open-water wave field: 1 in the body's interior, falling to 0 at
// the footprint border (|pool.xz| = 1). Metres-true on rectangular footprints (each axis' pool
// distance is scaled back by its own extent) and rotation-correct via the shared volume frame.
// Every public composition point below multiplies by this ONE weight - height, chop, normal tilt
// and whitecap foam all flatten together, and the CPU mirror applies the same weight at its own
// composition points (WaterVolume.SampleLargeWaveField et al) so buoyancy agrees with the render.
// Surface-current drift (full rationale: WaterWaves.hlsl). Guarded: whichever include lands
// first in a chain defines it; zero offset is bit-identical.
#ifndef WEBGPUWATER_OCEAN_CURRENT_INCLUDED
#define WEBGPUWATER_OCEAN_CURRENT_INCLUDED
float4 _OceanCurrentOffset;

float2 OceanCurrentDrift(float2 worldXZ)
{
    return worldXZ - _OceanCurrentOffset.xy;
}
#endif

float LbwEdgeWeight(float2 worldXZ)
{
    if (_LargeWaveEdgeFeather <= 0.0) return 1.0;
    float3 pool = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
    float3 extent = VolumeExtentSafe();
    float borderMeters = min((1.0 - abs(pool.x)) * extent.x,
                             (1.0 - abs(pool.z)) * extent.z);
    return smoothstep(0.0, _LargeWaveEdgeFeather, borderMeters);
}

// --- Placeholder spectrum constants (world units). Tuned for a light-breeze lake/ocean; these
//     become FFT spectrum inputs (wind speed / fetch) in step 2. ---
#define LBW_WAVE_COUNT         12
#define LBW_BASE_WAVELENGTH    9.0    // metres, longest component
#define LBW_WAVELENGTH_FALLOFF 0.82   // each component this fraction of the previous (shorter waves)
#define LBW_BASE_AMPLITUDE     0.14   // metres, height amplitude of the longest component
#define LBW_AMPLITUDE_FALLOFF  0.76   // shorter waves carry less energy
#define LBW_DIR_SPREAD         1.05   // radians of direction fan around the wind heading
#define LBW_CHOP_PHASE_SEED    1.0    // hash seed for the chop band (keeps the original crests exact)
// Long-period swell band (rolling horizon). Wavelength + height are art knobs (uniforms above); the
// band is narrower in direction (swell is more coherent than wind chop) and its energy falls off
// slowly across a few long components. Inert when _LargeSwellHeight = 0.
#define LBW_SWELL_COUNT              4
#define LBW_SWELL_WAVELENGTH_FALLOFF 0.68  // 4 components spanning ~1x .. ~0.2x the swell wavelength
#define LBW_SWELL_AMPLITUDE_FALLOFF  0.85  // long swell keeps energy across components (rolls, not spiky)
#define LBW_SWELL_DIR_SPREAD         0.5   // radians: tighter fan than the wind chop
#define LBW_SWELL_PHASE_SEED         101.0 // distinct hash seed so swell never aligns with the chop
#define LBW_GRAVITY            9.81
#define LBW_TWO_PI             6.28318530718
#define LBW_NORMAL_MIN_Y       1e-4   // clamps the Jacobian normal's up-component before dividing
// Distance band-limit transition (Crest keeps the wavelengths a LOD can resolve, zeroes the rest). A
// component whose wavelength is below LOW*minWavelength is dropped, above HIGH*minWavelength kept.
#define LBW_BANDLIMIT_LOW      0.7
#define LBW_BANDLIMIT_HIGH     1.5

// Fixed-point iterations that invert Gerstner horizontal displacement when sampling height at a
// world xz (Crest's SampleInvertedDisplacement uses 4). Declared here as the SHARED count so the CPU
// buoyancy mirror (LargeWaveField.cs) uses exactly the same value. Render never needs it (the vertex
// carries its own source xz to the fragment), but keeping it in one place documents the contract.
#define LBW_INVERSION_ITERATIONS 4

// Cheap per-component hash in [0,1). Used to SCATTER each wave's direction and phase so crests do
// not line up into regular parallel ridges (the "corduroy" look of a coherent wave sum).
// The constants are validator-guarded against the CPU mirror (LargeWaveField.Hash) - buoyancy
// desyncs from the rendered crests silently if either side drifts.
#define LBW_HASH_SINE_FREQ 12.9898
#define LBW_HASH_SINE_SCALE 43758.5453
// Decorrelates the phase hash stream from the heading hash stream fed the same wave index.
#define LBW_PHASE_HASH_STREAM_OFFSET 16.0
float LbwHash(float n)
{
    return frac(sin(n * LBW_HASH_SINE_FREQ) * LBW_HASH_SINE_SCALE);
}

// ================== SEA STATE: gusts + slicks (fragment-shading layer) ============================
// What the eye reads at distance is the mean-square slope of SUB-METRE waves (Cox & Munk 1954:
// mss ~ 0.003 + 5.12e-3 * wind), and a real sea modulates it SPATIALLY: wind gusts ("cat's paws")
// roughen drifting patches, while surfactant slicks/windrows damp only the shortest waves into
// glassy streaks aligned with the wind - the long swell rolls through them untouched. This layer is
// SHADING-ONLY by design: it scales the FFT normal tilt / crest pinch / whitecap coverage and the
// micro-detail normals, NEVER the displacement, so heights stay a pure function of (x,z) and the CPU
// buoyancy mirror needs no counterpart.
//   _SeaStateParams.x  gust strength 0..1 (both x and y 0 -> the layer costs one uniform test)
//   _SeaStateParams.y  slick strength 0..1
//   _SeaStateParams.z  gust advection speed (m/s - gust cells ride the wind)
//   _SeaStateParams.w  gust cell size (metres; also the crosswind windrow spacing)
// C# pair: WaterVolume.SeaStateParams (WaterVolume.Settings.Ocean.cs).
float4 _SeaStateParams;

// Streaks are ALONG-WIND features: both fields sample noise in a wind-aligned frame, gusts mildly
// elongated, slicks extremely so (Langmuir windrows run >100:1 length to spacing).
#define SEA_STATE_GUST_ELONGATION      3.0
#define SEA_STATE_SLICK_ELONGATION     12.0
// Slicks drift slower than the gust cells riding the wind above them.
#define SEA_STATE_SLICK_SPEED_FRACTION 0.35
// Noise band carved into slick streaks (smoothstep window edges) - higher = sparser streaks.
#define SEA_STATE_SLICK_THRESHOLD_LO   0.55
#define SEA_STATE_SLICK_THRESHOLD_HI   0.75
// Slicks cut mss by 2-3x in the Cox & Munk slick measurements; this floors the slick multiplier.
#define SEA_STATE_SLICK_FLOOR          0.3
// Gust swing: +/- this fraction of the local roughness at full strength (bright/dark patches).
#define SEA_STATE_GUST_SPAN            0.65
// Second gust octave: finer cells at lower weight (single-octave value noise reads as blobs).
#define SEA_STATE_GUST_OCTAVE_SCALE    0.37
#define SEA_STATE_GUST_OCTAVE_WEIGHT   0.35
// 2D lattice hash constants (the classic pair; LBW_HASH_* is the 1D stream the wave bands use).
#define SEA_STATE_HASH_DOT_X           12.9898
#define SEA_STATE_HASH_DOT_Y           78.233
#define SEA_STATE_HASH_SCALE           43758.5453

float SeaStateHash2(float2 cell)
{
    return frac(sin(dot(cell, float2(SEA_STATE_HASH_DOT_X, SEA_STATE_HASH_DOT_Y))) * SEA_STATE_HASH_SCALE);
}

// Bilinear value noise in [0,1] with a smoothstep fade. Derivative-free on purpose: this layer only
// SCALES normals that already exist, it never builds one, so C1 continuity is not required.
float SeaStateValueNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = p - cell;
    float2 u = f * f * (3.0 - 2.0 * f);
    float h00 = SeaStateHash2(cell);
    float h10 = SeaStateHash2(cell + float2(1.0, 0.0));
    float h01 = SeaStateHash2(cell + float2(0.0, 1.0));
    float h11 = SeaStateHash2(cell + float2(1.0, 1.0));
    return lerp(lerp(h00, h10, u.x), lerp(h01, h11, u.x), u.y);
}

// Local roughness multiplier at a world xz: 1 = the authored sea state, < 1 toward glassy (slicks),
// > 1 in gust patches. Applied multiplicatively to slope-DERIVED shading only.
float SeaStateMssScale(float2 worldXZ)
{
    // Uniform-coherent early-out: bodies that never author the layer pay one comparison per call.
    if (_SeaStateParams.x <= 0.0 && _SeaStateParams.y <= 0.0) return 1.0;

    float2 windDir = float2(cos(_LargeWaveWindHeading), sin(_LargeWaveWindHeading));
    // Wind-aligned frame: x along the wind (advected + elongated), y across it.
    float2 windFrame = float2(dot(worldXZ, windDir), dot(worldXZ, float2(-windDir.y, windDir.x)));
    float cellSize = max(_SeaStateParams.w, 1.0);

    float scale = 1.0;
    if (_SeaStateParams.x > 0.0)
    {
        float2 gustP = float2((windFrame.x - _SeaStateParams.z * _WaveTime) / SEA_STATE_GUST_ELONGATION,
                              windFrame.y) / cellSize;
        float gust = SeaStateValueNoise(gustP);
        gust = lerp(gust, SeaStateValueNoise(gustP / SEA_STATE_GUST_OCTAVE_SCALE),
                    SEA_STATE_GUST_OCTAVE_WEIGHT);
        // Signed swing about the authored state: lulls go glassy, gust patches roughen.
        scale *= 1.0 + _SeaStateParams.x * SEA_STATE_GUST_SPAN * (gust * 2.0 - 1.0);
    }
    if (_SeaStateParams.y > 0.0)
    {
        float2 slickP = float2((windFrame.x - _SeaStateParams.z * SEA_STATE_SLICK_SPEED_FRACTION * _WaveTime)
                               / SEA_STATE_SLICK_ELONGATION, windFrame.y) / cellSize;
        float streak = smoothstep(SEA_STATE_SLICK_THRESHOLD_LO, SEA_STATE_SLICK_THRESHOLD_HI,
                                  SeaStateValueNoise(slickP));
        scale *= 1.0 - _SeaStateParams.y * (1.0 - SEA_STATE_SLICK_FLOOR) * streak;
    }
    return scale;
}

// Everything the surface needs from the wave field at one WORLD-space xz, from a SINGLE pass over
// the components so height, horizontal displacement and their derivatives always agree.
//   height    : metres (drives the vertex Y)
//   slope     : (dHeight/dx, dHeight/dz)                       - the smooth-surface normal tilt
//   disp      : (Dx, Dz) horizontal Gerstner offset, chop BAKED IN (0 when choppiness = 0)
//   dispDeriv : (dDx/dx, dDx/dz == dDz/dx, dDz/dz), chop NOT baked in - the Jacobian uses raw terms
// All are scaled by the wind-driven _LargeWaveAmplitude so shading and geometry track the swell size.
struct LargeBodyWaveField
{
    float  height;
    float2 slope;
    float2 disp;
    float3 dispDeriv;
};

// Sum one band of directional Gerstner components (height = A*sin, horizontal = A*dir*cos) into the
// accumulating field. 'amplitudeScale' multiplies the whole band (the wind swell size for the chop
// band, the swell-height knob for the long band). 'phaseSeed' picks an independent hash stream so the
// bands never align into ridges. Directions scatter within 'dirSpread' of 'bandHeading' - the chop
// band follows the wind, the swell band its own decoupled heading.
void LbwAccumulateBand(float2 worldXZ, int count, float baseWavelength, float wavelengthFalloff,
                       float baseAmplitude, float amplitudeFalloff, float dirSpread, float phaseSeed,
                       float bandHeading, float amplitudeScale, float minWavelength, ShoreData shore,
                       float warpExtra, inout LargeBodyWaveField f)
{
    float wavelength = baseWavelength;
    float amplitude = baseAmplitude;

    [loop]
    for (int n = 0; n < count; n++)
    {
        float fn = (float)n;
        float headingJitter = (LbwHash(fn + phaseSeed) * 2.0 - 1.0) * dirSpread;
        float heading = bandHeading + headingJitter;
        float2 dir = float2(cos(heading), sin(heading));
        float phaseOffset = LbwHash(fn + phaseSeed + LBW_PHASE_HASH_STREAM_OFFSET) * LBW_TWO_PI;

        // Shoaling response of THIS component: 1 in deep water, falling toward 0 as the column
        // depth drops below half its wavelength. Drives attenuation, refraction and compression
        // together, so long waves feel the bottom sooner than short chop - exactly the cue that
        // separates a coastline from a bathtub edge.
        float shoalRaw = saturate(SHORE_SHOAL_WAVELENGTH_FACTOR * max(shore.depth, 0.0) / max(wavelength, SHORE_WAVELENGTH_EPSILON));
        float feel = (1.0 - shoalRaw) * shore.influence; // how much this component feels the bottom

        // Refraction: bend the travel direction toward the shore as the component feels the
        // bottom (Snell-flavoured heuristic - crests swing parallel to the beach). Off-field
        // toShore is (0,0), so the lerp shrinks-then-renormalizes to the original direction.
        float2 dirR = dir;
        if (feel > 0.0 && _ShoreRefraction > 0.0)
        {
            float2 bent = lerp(dir, shore.toShore, _ShoreRefraction * feel);
            float bentLen = length(bent);
            dirR = bentLen > 1e-4 ? bent / bentLen : dir;
        }

        float k = LBW_TWO_PI / max(wavelength, 1e-3);   // wavenumber
        float omega = sqrt(LBW_GRAVITY * k);            // deep-water dispersion
        // Phase compression: the shared shore-distance warp adds extra phase where waves slow in
        // the shallows, scaled by how much this component feels the bottom - crests bunch.
        // Current drift: the PHASE coordinate drifts (OceanCurrentDrift); the fetch weight
        // below stays on the geographic xz - fetch is anchored to the shore, not to the water.
        float phase = dot(dirR, OceanCurrentDrift(worldXZ)) * k - omega * _WaveTime + phaseOffset
                    + k * warpExtra * feel;
        float sinP = sin(phase);
        float cosP = cos(phase);
        // Distance band-limit: drop components the local mesh cannot resolve (short waves far out),
        // keep the long swell. weight = 1 near the camera (minWavelength ~ 0), so buoyancy's CPU mirror
        // (which samples only near the camera) stays exact against this full-spectrum near field.
        float bandWeight = (minWavelength <= 0.0) ? 1.0
                         : smoothstep(minWavelength * LBW_BANDLIMIT_LOW, minWavelength * LBW_BANDLIMIT_HIGH, wavelength);
        // Shoaling: attenuate this component by depth/wavelength so short waves die first and all
        // waves fall to zero as the water column runs out (no punching below the seabed near shore).
        float a = amplitudeScale * amplitude * bandWeight * ShoalWeight(shore.depth, wavelength)
                * SeaStateFetchWeight(worldXZ, wavelength);

        f.height    += a * sinP;
        f.slope     += a * k * dirR * cosP;             // d/dxz of A*sin(phase)
        f.disp      += a * dirR * cosP;                 // A*dir*cos(phase) (chop applied by caller)
        // d/dxz of A*dir*cos(phase) = -A*k*dir*dir*sin(phase); only three unique 2x2 terms.
        float akSin = a * k * sinP;
        f.dispDeriv += -akSin * float3(dirR.x * dirR.x, dirR.x * dirR.y, dirR.y * dirR.y);

        wavelength *= wavelengthFalloff;
        amplitude  *= amplitudeFalloff;
    }
}

// Gerstner is the classic sum: height = A*sin(phase), horizontal = Q*A*dir*cos(phase). Two bands are
// summed: the wind CHOP band (short crests, scaled by the wind swell amplitude - unchanged from the
// original single band) and the long-period SWELL band (rolling horizon, scaled by its height knob;
// inert when that is 0). The Jacobian of the displaced position gives the correct normal under chop.
// Core analytic evaluation with the shore substrate + surf front layer already sampled - the
// public wrappers below sample them once and share across height/chop/normal so a vertex never
// pays the shore fetches twice.
LargeBodyWaveField EvaluateLargeBodyWaveShore(float2 worldXZ, float minWavelength,
                                              ShoreData shore, SurfWaveSample surf)
{
    LargeBodyWaveField f;
    f.height = 0.0;
    f.slope = float2(0.0, 0.0);
    f.disp = float2(0.0, 0.0);
    f.dispDeriv = float3(0.0, 0.0, 0.0); // (dDx/dx, dDx/dz, dDz/dz); dDz/dx == dDx/dz by symmetry

    // Shore transform terms shared by both bands: Green's-law growth (waves RISE as the column
    // shrinks, before attenuation/breaking takes them), the phase-compression warp, and the
    // ambient fade where the surf fronts own the surface (anti-double-crest replace rule).
    float green = ShoreGreenGain(shore);
    float warpExtra = ShoreWarpExtra(shore);
    float ambient = SurfAmbientWeight(surf.mask);
    float bandScale = green * ambient;

    LbwAccumulateBand(worldXZ, LBW_WAVE_COUNT, LBW_BASE_WAVELENGTH, LBW_WAVELENGTH_FALLOFF,
                      LBW_BASE_AMPLITUDE, LBW_AMPLITUDE_FALLOFF, LBW_DIR_SPREAD, LBW_CHOP_PHASE_SEED,
                      _LargeWaveWindHeading, _LargeWaveAmplitude * bandScale, minWavelength, shore,
                      warpExtra, f);
    LbwAccumulateBand(worldXZ, LBW_SWELL_COUNT, _LargeSwellWavelength, LBW_SWELL_WAVELENGTH_FALLOFF,
                      1.0, LBW_SWELL_AMPLITUDE_FALLOFF, LBW_SWELL_DIR_SPREAD, LBW_SWELL_PHASE_SEED,
                      _LargeSwellHeading, _LargeSwellHeight * bandScale, minWavelength, shore,
                      warpExtra, f);

    // Surf breaker fronts ride on top (they replaced the ambient share above). No horizontal
    // displacement of their own: the lean is baked into the profile shape.
    f.height += surf.height;
    f.slope  += surf.slopeXZ;
    return f;
}

// --- FFT-cascade lookup (step 2) ------------------------------------------------------------------
// The WaterOceanFft pass publishes these globals for ocean bodies. When _OceanFftActive is 0 (pools,
// bounded bodies, or an unsupported device) the functions below fall back to the analytic generator
// above, so nothing but an opted-in ocean changes. Cascades tile across world XZ (Repeat wrap); half-
// float targets are hardware-filterable on WebGPU, so a plain linear sample is safe.
Texture2DArray _OceanFftDisplacement;  SamplerState sampler_OceanFftDisplacement; // (x, height, z, foam)
Texture2DArray _OceanFftNormal;        SamplerState sampler_OceanFftNormal;       // (nx, pinch, nz, foam)
float4 _OceanFftDomainSizes;   // metres per cascade
float4 _OceanFftVisibleAreas;  // per-cascade view distance (m) at which its detail fully fades out
float  _OceanFftCascadeCount;  // active cascades (<= 4)
float  _OceanFftActive;        // 1 when the FFT pass drives this body; 0 -> analytic fallback
float4 _OceanFoamColor;        // whitecap tint (rgb) + master opacity (a); default opaque white
float  _OceanFoamTileSize;     // metres per foam-pattern tile on the ocean surface
float  _OceanFoamFeather;      // black-point dissolve softness (0..1) for the foam texture
#if !defined(WATER_DISABLE_OCEAN_APERIODIC)
Texture2D<float4> _OceanDirectionMap;
float4 _OceanAperiodicParams;    // x = enabled, y = tile scale, z = direction strength
float4 _OceanDirectionMapFrame; // xy = world centre, z = inverse world size
#endif

// OCEAN_FFT_MAX_CASCADES / OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION live in WaterShared.hlsl
// (included above), shared with OceanFft.compute and WaterFoamParticles.compute.

// Depth attenuation for one cascade near shore (P0 fix B1: the FFT path never shoaled at all -
// on the one body type a coastline is for, depth changed nothing).
float OceanCascadeShoalWeight(int c, ShoreData shore)
{
    float wavelength = max(_OceanFftDomainSizes[c], 1e-3) * OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION;
    return lerp(1.0, ShoalWeight(shore.depth, wavelength), shore.influence);
}

#if !defined(WATER_DISABLE_OCEAN_APERIODIC)
// Bilinear tap of the direction map. TWO implementations, and which one a shader gets is a
// SAMPLER REGISTER decision, not a taste one.
//
// This is one of the hottest reads in the package: three tile vertices x four cascades, and a
// waterline classification runs the field three times - so the fullscreen fog composites pay it
// ~36 times per pixel and the meniscus pass again (fog audit, 2026-08-11). The texture unit does
// this exact filter in one instruction... but only for a program that can afford a sampler.
//
// It CANNOT be assumed. WaterSurface.shader pass 0 sits exactly at the ps_4_0 sampler cap of 16
// (its own header says so), and its foam-overlay pass does not reference the FFT arrays at all.
// Making this a hardware sample unconditionally is a compile error on both counts - measured,
// 2026-08-11: "maximum ps_4_0 sampler register index (16) exceeded". THAT is why the manual
// version below exists: Load needs no sampler. Do not "optimise" it away again without checking
// the register budget of every program that includes this header.
//
// So: fullscreen passes with headroom define WATER_APERIODIC_MAP_SAMPLER and get the fast path;
// everything else keeps the Loads. The two produce the same value - the half-texel conversion is
// what makes that true rather than approximate. The manual version indexes texel CORNERS over
// [0, size-1]; hardware bilinear samples texel CENTRES, so the same corner coordinate mapped
// through (texel + 0.5) / size lands on the same four texels with the same fractional weights.
// Callers have already rejected uv outside [0,1] (OceanAperiodicTileAngle), so edge handling
// matches the old min()/saturate() clamping. The only residual is that the texture unit computes
// its weights at fixed sub-texel precision rather than fp32 - far below what a wave heading angle
// can express.
#if defined(WATER_APERIODIC_MAP_SAMPLER)
// Unity inline sampler state: linear filter, clamp addressing, resolved from the NAME, so it needs
// no import settings and no companion texture. Costs one sampler register in this program only.
SamplerState sampler_linear_clamp;
#endif

float2 OceanAperiodicDirectionMapBilinear(float2 uv)
{
    uint width, height;
    _OceanDirectionMap.GetDimensions(width, height);
#if defined(WATER_APERIODIC_MAP_SAMPLER)
    float2 size = float2(max(width, 1u), max(height, 1u));
    float2 texel = saturate(uv) * max(size - 1.0, 0.0);
    return _OceanDirectionMap.SampleLevel(sampler_linear_clamp, (texel + 0.5) / size, 0).rg;
#else
    float2 texel = saturate(uv) * float2(max((int)width - 1, 0), max((int)height - 1, 0));
    int2 p0 = (int2)floor(texel);
    int2 p1 = min(p0 + 1, int2((int)width - 1, (int)height - 1));
    float2 fraction = frac(texel);
    float2 row0 = lerp(_OceanDirectionMap.Load(int3(p0, 0)).rg,
                       _OceanDirectionMap.Load(int3(p1.x, p0.y, 0)).rg, fraction.x);
    float2 row1 = lerp(_OceanDirectionMap.Load(int3(p0.x, p1.y, 0)).rg,
                       _OceanDirectionMap.Load(int3(p1, 0)).rg, fraction.x);
    return lerp(row0, row1, fraction.y);
#endif
}

float OceanAperiodicTileAngle(float2 tileWorldCenter)
{
    float2 mapUv = (tileWorldCenter - _OceanDirectionMapFrame.xy) * _OceanDirectionMapFrame.z + 0.5;
    if (any(mapUv < 0.0) || any(mapUv > 1.0)) return 0.0;
    float2 encodedDirection = OceanAperiodicDirectionMapBilinear(mapUv);
    return OceanAperiodicDirectionAngle(encodedDirection, _OceanAperiodicParams.z);
}

float2 OceanAperiodicSampleUv(float2 worldXZ, float domain, int2 vertex, out float angle)
{
    float2 tileCenterUv = OceanAperiodicVertexUv(vertex, _OceanAperiodicParams.y);
    float2 tileWorldCenter = tileCenterUv * domain;
    angle = OceanAperiodicTileAngle(tileWorldCenter);
    float2 localWorld = OceanAperiodicRotate(worldXZ - tileWorldCenter, -angle);
    return localWorld / domain + tileCenterUv + OceanAperiodicHash(vertex);
}

float3 OceanAperiodicDisplacement(float2 worldXZ, float domain, float slice)
{
    float2 exemplarUv = worldXZ / domain;
    OceanAperiodicTriangle tileTriangle = OceanAperiodicTriangleAt(exemplarUv, _OceanAperiodicParams.y);
    float3 weights = OceanAperiodicVarianceWeights(tileTriangle.weights);
    float angle0, angle1, angle2;
    float3 tap0 = _OceanFftDisplacement.SampleLevel(sampler_OceanFftDisplacement,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex0, angle0), slice), 0).xyz;
    float3 tap1 = _OceanFftDisplacement.SampleLevel(sampler_OceanFftDisplacement,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex1, angle1), slice), 0).xyz;
    float3 tap2 = _OceanFftDisplacement.SampleLevel(sampler_OceanFftDisplacement,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex2, angle2), slice), 0).xyz;
    tap0.xz = OceanAperiodicRotate(tap0.xz, angle0);
    tap1.xz = OceanAperiodicRotate(tap1.xz, angle1);
    tap2.xz = OceanAperiodicRotate(tap2.xz, angle2);
    return tap0 * weights.x + tap1 * weights.y + tap2 * weights.z;
}

float4 OceanAperiodicNormal(float2 worldXZ, float domain, float slice, float lod)
{
    float2 exemplarUv = worldXZ / domain;
    OceanAperiodicTriangle tileTriangle = OceanAperiodicTriangleAt(exemplarUv, _OceanAperiodicParams.y);
    float3 varianceWeights = OceanAperiodicVarianceWeights(tileTriangle.weights);
    float angle0, angle1, angle2;
    float4 tap0 = _OceanFftNormal.SampleLevel(sampler_OceanFftNormal,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex0, angle0), slice), lod);
    float4 tap1 = _OceanFftNormal.SampleLevel(sampler_OceanFftNormal,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex1, angle1), slice), lod);
    float4 tap2 = _OceanFftNormal.SampleLevel(sampler_OceanFftNormal,
        float3(OceanAperiodicSampleUv(worldXZ, domain, tileTriangle.vertex2, angle2), slice), lod);
    tap0.xz = OceanAperiodicRotate(tap0.xz, angle0);
    tap1.xz = OceanAperiodicRotate(tap1.xz, angle1);
    tap2.xz = OceanAperiodicRotate(tap2.xz, angle2);
    float2 tilt = tap0.xz * varianceWeights.x + tap1.xz * varianceWeights.y + tap2.xz * varianceWeights.z;
    float2 coverage = float2(tap0.y, tap0.w) * tileTriangle.weights.x
                    + float2(tap1.y, tap1.w) * tileTriangle.weights.y
                    + float2(tap2.y, tap2.w) * tileTriangle.weights.z;
    return float4(tilt.x, coverage.x, tilt.y, coverage.y);
}
#endif

// Sum the (x, height, z) displacement across the active cascades at a world xz, each cascade
// attenuated by the shore depth (pass an inert ShoreData - influence 0 - for open water) and by the
// SAME cubic distance fade the fragment normals use.
//
// That fade is the distance BAND-LIMIT, and it was missing here: this is the VERTEX path, so every
// cascade was displacing the mesh at full strength at every distance while the shading had already
// faded the fine ones out. The ocean clipmap's cells run from ~2 m at level 0 to ~527 m at level 8, so
// several hundred metres out the mesh was carrying 5 m and 20 m waves on 16-33 m triangles - far below
// Nyquist - which resolves as a moving moire lattice: patterns and lines that swim when the camera
// does. The analytic path has always band-limited itself (LargeBodyWaveMinWavelength); the FFT path
// never did. The CPU buoyancy bake applies the identical fade (OceanFft.compute BakeHeightField), so
// floaters still ride the surface that is drawn.
//
// The displacement array carries NO mips (only the normal target does), so the fade IS the band-limit
// here - there is no coarser level to drop to.
float3 OceanFftDisplacementShore(float2 worldXZ, ShoreData shore)
{
    float camDist = distance(worldXZ, _WorldSpaceCameraPos.xz);
    // Wave-space coordinate drifts with the current; fetch/fade stay on the geographic xz.
    float2 waveXZ = OceanCurrentDrift(worldXZ);
    float3 sum = float3(0.0, 0.0, 0.0);
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);   // never index past the array depth
        float domain = max(_OceanFftDomainSizes[c], 1e-3);
        float2 uv = waveXZ / domain;
        float fade = OceanCascadeDistanceFade(camDist, _OceanFftVisibleAreas[c]);
        float fetch = SeaStateFetchWeight(worldXZ,
            max(_OceanFftDomainSizes[c], 1e-3) * OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION);
#if defined(WATER_DISABLE_OCEAN_APERIODIC)
        float3 tap = _OceanFftDisplacement.SampleLevel(
            sampler_OceanFftDisplacement, float3(uv, slice), 0).xyz;
#else
        float3 tap = _OceanAperiodicParams.x > 0.5
            ? OceanAperiodicDisplacement(waveXZ, domain, slice)
            : _OceanFftDisplacement.SampleLevel(sampler_OceanFftDisplacement, float3(uv, slice), 0).xyz;
#endif
        sum += (active * fade * OceanCascadeShoalWeight(c, shore) * fetch) * tap;
    }
    return sum;
}

// Per-cascade FAR-FIELD SLOPE FLOOR (KWS KWS_WaterHelpers.cginc:309-312, saturate(fade + .15/.20/.25/.25)).
// The cubic fade below reaches EXACTLY zero, so past a cascade's visible area its normal contributes
// nothing at all - and past the last one the ocean shades as a smooth deformed plane, a mirror rather than
// water. KWS never lets the slope vanish: a residual share of every cascade survives at any distance, and
// that residue is what keeps the horizon reading as sea.
//
// TILT ONLY - deliberately. Pinch and foam must still fade to zero, or whitecaps and crest glow would sit
// forever on water whose waves are no longer drawn ("patches corresponding to nothing"). That is the exact
// divergence the shared-weight rule below is written against, so the two weights are derived from the SAME
// 'fade' one line apart rather than in separate loops, where they could silently drift.
static const float4 OceanFftFarSlopeFloor = float4(0.15, 0.20, 0.25, 0.25);

// Weighted sum of _OceanFftNormal across the ACTIVE cascades - the cubic distance fade, the explicit
// distance mip LOD and the per-cascade shore attenuation, all in one place.
//
// ONE loop for all three because the WEIGHT is the thing they must agree on. Tilt, pinch and foam are
// the same wave read three ways: retune the fade curve or the shoal attenuation in some of them and not
// the others and you get foam and glow over water that carries no visible wave - precisely the failure
// OceanFftJacobianShore's own header warns about. Sharing the weight makes that drift impossible.
// Accumulating all four channels rather than one costs a couple of MADs per cascade and NO extra
// bandwidth: SampleLevel returns the full float4 either way.
struct OceanFftCascadeSum
{
    float2 tilt;   // surface-normal tilt (xz) - floored, so the far field never goes mirror-flat
    float  pinch;  // Jacobian crest pinch    - fades to zero with the drawn wave
    float  foam;   // whitecap coverage       - fades to zero with the drawn wave
};

OceanFftCascadeSum OceanFftNormalSumShore(float2 worldXZ, ShoreData shore)
{
    float camDist = distance(worldXZ, _WorldSpaceCameraPos.xz);
    // Wave-space coordinate drifts with the current; fetch/fade stay on the geographic xz.
    float2 waveXZ = OceanCurrentDrift(worldXZ);
    OceanFftCascadeSum sum;
    sum.tilt = float2(0.0, 0.0);
    sum.pinch = 0.0;
    sum.foam = 0.0;
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);
        float domain = max(_OceanFftDomainSizes[c], 1e-3);
        float2 uv = waveXZ / domain;
        float fade = OceanCascadeDistanceFade(camDist, _OceanFftVisibleAreas[c]);
        float lod = log2(1.0 + camDist / domain); // farther -> coarser mip (distance anti-aliasing)
        float fetch = SeaStateFetchWeight(worldXZ,
            domain * OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION);
        float shoal = active * OceanCascadeShoalWeight(c, shore) * fetch;
#if defined(WATER_DISABLE_OCEAN_APERIODIC)
        float4 tap = _OceanFftNormal.SampleLevel(sampler_OceanFftNormal, float3(uv, slice), lod);
#else
        float4 tap = _OceanAperiodicParams.x > 0.5
            ? OceanAperiodicNormal(waveXZ, domain, slice, lod)
            : _OceanFftNormal.SampleLevel(sampler_OceanFftNormal, float3(uv, slice), lod);
#endif
        sum.tilt  += (shoal * max(fade, OceanFftFarSlopeFloor[c])) * tap.xz;
        sum.pinch += (shoal * fade) * tap.y;
        sum.foam  += (shoal * fade) * tap.w;
    }
    // Gust/slick modulation rides the SAME sum for tilt, pinch and foam (the shared-weight rule
    // above: roughness, crest glow and whitecaps must tell one story or foam sits on glassy water).
    // Foam responds quadratically - real whitecap coverage is strongly super-linear in local wind.
    float seaState = SeaStateMssScale(worldXZ);
    sum.tilt  *= seaState;
    sum.pinch *= seaState;
    sum.foam  *= seaState * seaState;
    return sum;
}

// Sum the surface-normal tilt (xz of the per-cascade world normal) across the active cascades. This is
// the crux of the FFT quality win: the normal is sampled independently of mesh tessellation with trilinear
// mip selection, so ripple detail stays crisp toward the horizon without aliasing. The mip is chosen by an
// explicit DISTANCE LOD (not screen derivatives) so the same code is valid in the vertex programs that also
// call this - e.g. the projected caustic grid - not just the fragment. A cubic distance fade then removes
// each cascade past its visible range so the finest ripples don't shimmer far away.
float2 OceanFftNormalTiltShore(float2 worldXZ, ShoreData shore)
{
    return OceanFftNormalSumShore(worldXZ, shore).tilt;
}

float2 OceanFftNormalTilt(float2 worldXZ)
{
    // Edge guard: standalone callers (the foam-particle render glue) must see the same flattened
    // border the surface renders; ApplyLargeBodyWaveNormalFoamShore weights its own tilt instead.
    return OceanFftNormalTiltShore(worldXZ, ShoreSample(worldXZ)) * LbwEdgeWeight(worldXZ);
}

// Sum the accumulated whitecap foam (.w of the per-cascade normal target) across the active cascades,
// with the SAME distance fade + mip LOD as the tilt above, so foam anti-aliases and fades toward the
// horizon exactly like the ripple detail it rides on. The compute silences cascade 0 and damps cascade 1,
// so this just gathers what the temporal accumulation already shaped. Saturated: overlapping cascades can
// sum past 1 on a hard break, but foam coverage is a 0..1 mask.
float OceanFftFoam(float2 worldXZ)
{
    // Shore attenuation keeps whitecaps off water the depth field has already flattened (the
    // surf whitewash layer owns the foam story there instead).
    ShoreData shore = ShoreSample(worldXZ);
    float foam = OceanFftNormalSumShore(worldXZ, shore).foam;
    // Edge guard: no whitecaps on the flattened border band (foam over visibly calm water reads
    // as detached from the waves - the "patches corresponding to nothing" rule).
    return saturate(foam) * LbwEdgeWeight(worldXZ);
}

// Sample the TRUE wave-crest "pinch" - the raw displacement-Jacobian fold, saturate(1 - J), written to
// _OceanFftNormal.y by the FFT compute. Peaks on steep / breaking crests (the same fold that seeds foam),
// so it drives the subsurface glow exactly where the surface is folding, rather than proxying it with
// wave height. Same distance fade + mip LOD as the foam/tilt so it anti-aliases identically.
float OceanFftJacobianShore(float2 worldXZ, ShoreData shore)
{
    // Per-cascade shore attenuation matches the DISPLACEMENT's: a wave the depth field has
    // flattened must not keep emitting its full-strength pinch signal, or foam/glow appears
    // over water that visibly carries no wave ("patches corresponding to nothing").
    float pinch = OceanFftNormalSumShore(worldXZ, shore).pinch;
    return saturate(pinch);
}

// Shortest wavelength the mesh can resolve at this world xz: grows with distance from the camera
// (the clipmap triangles get bigger). 0 when band-limiting is off (bounded bodies, _LargeWaveDetailSlope = 0).
float LargeBodyWaveMinWavelength(float2 worldXZ)
{
    return distance(worldXZ, _WorldSpaceCameraPos.xz) * _LargeWaveDetailSlope;
}

// Wave HEIGHT (metres) only - for the vertex Y displacement. FFT cascades when active; the amplitude
// knob still scales the swell so the inspector stays live. Both paths carry the shore transform:
// per-cascade/per-component shoal attenuation, ambient fade under the surf fronts, and the fronts
// themselves on top (the FFT keeps the deep-water texture; the front layer owns the coastline).
// Height from a PRE-SAMPLED shore + surf pair. Any caller that needs the height AND something
// else off the same field - the tilt, or a second evaluation at another xz - must hoist one
// ShoreSample + one EvaluateSurfWaves and use this: the wrapper below re-samples both, so two
// wrapper calls pay the shore, the surf fronts and the cascade fetch twice over (the ~2.5x
// this file's LargeBodyWaveHeightDispShore comment already warns about).
float LargeBodyWaveHeightShore(float2 worldXZ, ShoreData shore, SurfWaveSample surf)
{
    // Edge guard scales the WHOLE composite (surf fronts included): a breaker cresting exactly on
    // the border would rebuild the wall the feather exists to remove.
    float edge = LbwEdgeWeight(worldXZ);
    float height;
    if (_OceanFftActive > 0.5)
        height = OceanFftDisplacementShore(worldXZ, shore).y * _LargeWaveAmplitude
                 * SurfAmbientWeight(surf.mask) + surf.height;
    else
        height = EvaluateLargeBodyWaveShore(worldXZ, LargeBodyWaveMinWavelength(worldXZ),
                                            shore, surf).height;
    return height * edge;
}

float LargeBodyWaveHeight(float2 worldXZ)
{
    ShoreData shore = ShoreSample(worldXZ);
    SurfWaveSample surf = EvaluateSurfWaves(worldXZ, shore.depth, shore.sdfDist, shore.toShore,
                                            shore.slopeTan, shore.influence, _SurfBeatTime);
    return LargeBodyWaveHeightShore(worldXZ, shore, surf);
}


// Height + horizontal chop from ONE field evaluation - the vertex's hot path. The separate
// LargeBodyWaveHeight/Displacement wrappers each re-sample the shore, re-evaluate the surf fronts
// AND re-run the cascade fetch / band loop, so a vertex calling both paid the whole field ~2.5x
// (the swash's third ShoreSample included). The surface vertex now hoists ONE ShoreSample + ONE
// EvaluateSurfWaves and calls this: FFT bodies read the cascades once for both height and chop,
// analytic bodies run the Gerstner band loop once. Values are byte-identical to the wrappers.
void LargeBodyWaveHeightDispShore(float2 worldXZ, ShoreData shore, SurfWaveSample surf,
                                  out float height, out float2 disp)
{
    height = 0.0;
    disp = float2(0.0, 0.0);

    // Edge guard on height AND chop: unfeathered horizontal displacement would push border
    // vertices past the footprint even after the height flattens.
    float edge = LbwEdgeWeight(worldXZ);
    if (_OceanFftActive > 0.5)
    {
        float3 fft = OceanFftDisplacementShore(worldXZ, shore);
        float ambient = SurfAmbientWeight(surf.mask);
        height = (fft.y * _LargeWaveAmplitude * ambient + surf.height) * edge;
        // NO _LargeWaveChoppiness here: the FFT branch bakes chop into the spectrum itself
        // (OceanFft.compute's SpectrumUpdate scales the horizontal spectra by OceanChoppiness), so
        // applying it again would square it. Doing it in the compute is the correct place, because
        // the whitecap Jacobian is measured from the displacement AFTER chop - which is what finally
        // makes the foam respond to the chop slider at all. The analytic branch below still applies
        // it here, since its generator has no such stage.
        disp = fft.xz * (_LargeWaveAmplitude * ambient * edge);
        return;
    }
    LargeBodyWaveField f = EvaluateLargeBodyWaveShore(worldXZ, LargeBodyWaveMinWavelength(worldXZ),
                                                      shore, surf);
    height = f.height * edge;
    disp = f.disp * (_LargeWaveChoppiness * edge);
}

// Tilt a WORLD-space surface normal by the open-water wave shape at its SOURCE xz (the undisplaced
// position the vertex carried through). 'strength' scales the effect (reuse the body's
// _WaveNormalStrength so it stays art-directable). The tilt is the Jacobian normal of the displaced
// Gerstner surface; at choppiness = 0 it equals -slope, i.e. the original smooth-swell normal.
// Geometry-foam thresholds: a surface steeper than BREAK_SLOPE_MIN starts to whiten, fully white
// by BREAK_SLOPE_MAX (a breaking face's slope ~ height / face length); PINCH_GAIN scales the
// Jacobian fold. Foam derived from the RENDERED geometry can never detach from the waves - this
// is Crest's whitecap (displacement-Jacobian) + KWS's breaking front (slope gate) computed from
// the very field that displaces the vertices, gated to the near-shore band.
#define LBW_BREAK_SLOPE_MIN 0.28
#define LBW_BREAK_SLOPE_MAX 0.65
#define LBW_PINCH_GAIN      1.5

// Ambient geometry-foam floor, published PER BODY (0 default). An ocean-surface CHUNK has
// no FFT accumulator (the FFT runs only for unbounded clipmap oceans) and no surf band -
// with the shore-gated gate below returning 0, it had NO whitecap source at all. The floor
// lets the analytic Jacobian/steepness foam emit off-shore for exactly those bodies
// (WaterUniformPublisher sets 1 for chunk + open-water, 0 everywhere else, so FFT oceans
// and every existing scene are byte-identical).
float _LbwGeomFoamFloor;

// Near-shore gate for the geometry foam - and the whitecap-suppression weight in the fragment
// (accumulated FFT whitecaps fade by 1 - gate where the surf owns the shallows). This is EXACTLY
// SurfFieldMask: the same window, wet fade and shore-exposure gate the surf whitewash itself uses,
// so whitecaps are only ever suppressed where whitewash actually replaces them. The old wider
// depth-only window (0.7..1.5 x band, no exposure) killed whitecaps on the lee side of an island
// and in the outer band ring where NO surf foam appears - a visibly barren strip of clean water.
float LbwGeometryFoamGate(ShoreData shore)
{
    if (_SurfActive < 0.5) return _LbwGeomFoamFloor;
    return SurfFieldMask(shore.depth, shore.toShore, shore.influence);
}

// The SUPPRESSION half, split off from the gate above. That one has TWO callers doing two different
// jobs: ApplyLargeBodyWaveNormalFoamShore uses it to scale the surf's OWN geometry foam (which must
// keep the front contour, wet term and all), while the fragment used it to decide which OTHER foam
// engines stand down. Only the second job wants the ownership contour, so it gets its own function -
// changing the shared one would have silently moved the geometry foam too.
//
// Off surf bodies this is byte-identical to LbwGeometryFoamGate on purpose: the chunk geometry-foam
// floor is the value the whitecap suppression at the call site was already reading there.
float LbwFoamOwnershipGate(ShoreData shore)
{
    if (_SurfActive < 0.5) return _LbwGeomFoamFloor;
    return SurfOwnershipMask(shore.depth, shore.toShore, shore.influence);
}

// Shore-aware normal + GEOMETRY FOAM: xyz = tilted world normal, w = breaker foam (0..1) derived
// from the composite surface's own slope + displacement Jacobian. The caller has already sampled
// the shore substrate + surf-front layer at the source xz (the fragment hoists ONE sample and
// shares it between the normal, the foam, the crest glow and the swash).
float4 ApplyLargeBodyWaveNormalFoamShore(float3 worldNormal, float2 sourceXZ, float strength,
                                         ShoreData shore, SurfWaveSample surf)
{
    float foamGate = LbwGeometryFoamGate(shore);
    // Edge guard: the border band renders a flattened surface, so its normal tilt and its
    // breaker foam must flatten with it (same weight the height/chop composition used).
    float edge = LbwEdgeWeight(sourceXZ);

    // FFT path: the cascade normals already encode the surface tilt; blend their xz and lean the base
    // normal by it. Shore-attenuated + ambient-faded like the height, plus the surf fronts' own
    // slope so breaker faces catch the light. A height gradient g contributes normal.xz = -g.
    // Geometry foam = the cascades' TRUE Jacobian pinch + the front layer's own face steepness.
    if (_OceanFftActive > 0.5)
    {
        float2 fftTilt = (OceanFftNormalTiltShore(sourceXZ, shore) * SurfAmbientWeight(surf.mask)
                       - surf.slopeXZ) * edge;
        float geomFoam = 0.0;
        if (foamGate > 0.0)
        {
            // Shore-attenuated + ambient-faded pinch: only waves that are actually RENDERED at
            // this depth may whiten (the raw Jacobian made foam patches over flattened water).
            float pinch = OceanFftJacobianShore(sourceXZ, shore)
                        * (LBW_PINCH_GAIN * SurfAmbientWeight(surf.mask));
            float steep = smoothstep(LBW_BREAK_SLOPE_MIN, LBW_BREAK_SLOPE_MAX, length(fftTilt));
            geomFoam = saturate(max(pinch, steep)) * foamGate * edge;
        }
        return float4(normalize(worldNormal + float3(fftTilt.x, 0.0, fftTilt.y) * strength), geomFoam);
    }

    LargeBodyWaveField f = EvaluateLargeBodyWaveShore(sourceXZ, LargeBodyWaveMinWavelength(sourceXZ),
                                                      shore, surf);
    float q = _LargeWaveChoppiness;
    float dDxdx = f.dispDeriv.x;
    float dDxdz = f.dispDeriv.y; // == dDz/dx
    float dDzdz = f.dispDeriv.z;

    // Tangents of P(x,z) = (x + Q*Dx, height, z + Q*Dz); their cross product is the surface normal.
    float3 tangentX = float3(1.0 + q * dDxdx, f.slope.x, q * dDxdz);
    float3 tangentZ = float3(q * dDxdz, f.slope.y, 1.0 + q * dDzdz);
    float3 n = cross(tangentZ, tangentX);
    float2 tilt = (n.xz / max(n.y, LBW_NORMAL_MIN_Y)) * edge;

    float geomFoamA = 0.0;
    if (foamGate > 0.0)
    {
        // Crest whitecap: determinant of the horizontal-displacement Jacobian folds below 1 where
        // chop pinches a crest. KWS breaking front: the total surface slope (ambient + front face).
        float jac = (1.0 + q * dDxdx) * (1.0 + q * dDzdz) - (q * dDxdz) * (q * dDxdz);
        float pinch = saturate(1.0 - jac) * LBW_PINCH_GAIN;
        float steep = smoothstep(LBW_BREAK_SLOPE_MIN, LBW_BREAK_SLOPE_MAX, length(f.slope));
        geomFoamA = saturate(max(pinch, steep)) * foamGate * edge;
    }
    return float4(normalize(worldNormal + float3(tilt.x, 0.0, tilt.y) * strength), geomFoamA);
}

// Normal-only wrapper (kept for callers that don't consume the geometry foam).
float3 ApplyLargeBodyWaveNormalShore(float3 worldNormal, float2 sourceXZ, float strength,
                                     ShoreData shore, SurfWaveSample surf)
{
    return ApplyLargeBodyWaveNormalFoamShore(worldNormal, sourceXZ, strength, shore, surf).xyz;
}

// Back-compat wrapper: samples the shore + surf itself. Prefer the Shore variant when the caller
// already holds the samples (the water-surface fragment does).
float3 ApplyLargeBodyWaveNormal(float3 worldNormal, float2 sourceXZ, float strength)
{
    ShoreData shore = ShoreSample(sourceXZ);
    SurfWaveSample surf = EvaluateSurfWaves(sourceXZ, shore.depth, shore.sdfDist, shore.toShore,
                                            shore.slopeTan, shore.influence, _SurfBeatTime);
    return ApplyLargeBodyWaveNormalShore(worldNormal, sourceXZ, strength, shore, surf);
}

#endif // WEBGPUWATER_LARGE_WAVES_INCLUDED
