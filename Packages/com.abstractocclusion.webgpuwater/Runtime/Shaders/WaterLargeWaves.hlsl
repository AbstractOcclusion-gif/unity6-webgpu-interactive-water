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

// Layer B shoaling reads the world-frame seabed depth field (Layer A) to attenuate waves near shore.
#include "WaterShore.hlsl"

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
float LbwHash(float n)
{
    return frac(sin(n * 12.9898) * 43758.5453);
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
// bands never align into ridges. Directions scatter within 'dirSpread' of the wind heading.
void LbwAccumulateBand(float2 worldXZ, int count, float baseWavelength, float wavelengthFalloff,
                       float baseAmplitude, float amplitudeFalloff, float dirSpread, float phaseSeed,
                       float amplitudeScale, float minWavelength, float shoalDepth, inout LargeBodyWaveField f)
{
    float wavelength = baseWavelength;
    float amplitude = baseAmplitude;

    [loop]
    for (int n = 0; n < count; n++)
    {
        float fn = (float)n;
        float headingJitter = (LbwHash(fn + phaseSeed) * 2.0 - 1.0) * dirSpread;
        float heading = _LargeWaveWindHeading + headingJitter;
        float2 dir = float2(cos(heading), sin(heading));
        float phaseOffset = LbwHash(fn + phaseSeed + 16.0) * LBW_TWO_PI;

        float k = LBW_TWO_PI / max(wavelength, 1e-3);   // wavenumber
        float omega = sqrt(LBW_GRAVITY * k);            // deep-water dispersion
        float phase = dot(dir, worldXZ) * k - omega * _WaveTime + phaseOffset;
        float sinP = sin(phase);
        float cosP = cos(phase);
        // Distance band-limit: drop components the local mesh cannot resolve (short waves far out),
        // keep the long swell. weight = 1 near the camera (minWavelength ~ 0), so buoyancy's CPU mirror
        // (which samples only near the camera) stays exact against this full-spectrum near field.
        float bandWeight = (minWavelength <= 0.0) ? 1.0
                         : smoothstep(minWavelength * LBW_BANDLIMIT_LOW, minWavelength * LBW_BANDLIMIT_HIGH, wavelength);
        // Shoaling: attenuate this component by depth/wavelength so short waves die first and all
        // waves fall to zero as the water column runs out (no punching below the seabed near shore).
        float a = amplitudeScale * amplitude * bandWeight * ShoalWeight(shoalDepth, wavelength);

        f.height    += a * sinP;
        f.slope     += a * k * dir * cosP;              // d/dxz of A*sin(phase)
        f.disp      += a * dir * cosP;                  // A*dir*cos(phase) (chop applied by caller)
        // d/dxz of A*dir*cos(phase) = -A*k*dir*dir*sin(phase); only three unique 2x2 terms.
        float akSin = a * k * sinP;
        f.dispDeriv += -akSin * float3(dir.x * dir.x, dir.x * dir.y, dir.y * dir.y);

        wavelength *= wavelengthFalloff;
        amplitude  *= amplitudeFalloff;
    }
}

// Gerstner is the classic sum: height = A*sin(phase), horizontal = Q*A*dir*cos(phase). Two bands are
// summed: the wind CHOP band (short crests, scaled by the wind swell amplitude - unchanged from the
// original single band) and the long-period SWELL band (rolling horizon, scaled by its height knob;
// inert when that is 0). The Jacobian of the displaced position gives the correct normal under chop.
LargeBodyWaveField EvaluateLargeBodyWave(float2 worldXZ, float minWavelength)
{
    LargeBodyWaveField f;
    f.height = 0.0;
    f.slope = float2(0.0, 0.0);
    f.disp = float2(0.0, 0.0);
    f.dispDeriv = float3(0.0, 0.0, 0.0); // (dDx/dx, dDx/dz, dDz/dz); dDz/dx == dDx/dz by symmetry

    // Layer B shoaling: still-water depth under this world xz. One sample feeds both bands so height,
    // chop and normal all attenuate together (a deep sentinel off-field leaves open water unchanged).
    float shoalDepth = ShoreShoalDepth(worldXZ);

    LbwAccumulateBand(worldXZ, LBW_WAVE_COUNT, LBW_BASE_WAVELENGTH, LBW_WAVELENGTH_FALLOFF,
                      LBW_BASE_AMPLITUDE, LBW_AMPLITUDE_FALLOFF, LBW_DIR_SPREAD, LBW_CHOP_PHASE_SEED,
                      _LargeWaveAmplitude, minWavelength, shoalDepth, f);
    LbwAccumulateBand(worldXZ, LBW_SWELL_COUNT, _LargeSwellWavelength, LBW_SWELL_WAVELENGTH_FALLOFF,
                      1.0, LBW_SWELL_AMPLITUDE_FALLOFF, LBW_SWELL_DIR_SPREAD, LBW_SWELL_PHASE_SEED,
                      _LargeSwellHeight, minWavelength, shoalDepth, f);
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

#define OCEAN_FFT_MAX_CASCADES 4

// Sum the (x, height, z) displacement across the active cascades at a world xz.
float3 OceanFftDisplacement(float2 worldXZ)
{
    float3 sum = float3(0.0, 0.0, 0.0);
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);   // never index past the array depth
        float2 uv = worldXZ / max(_OceanFftDomainSizes[c], 1e-3);
        sum += active * _OceanFftDisplacement.SampleLevel(sampler_OceanFftDisplacement, float3(uv, slice), 0).xyz;
    }
    return sum;
}

// Sum the surface-normal tilt (xz of the per-cascade world normal) across the active cascades. This is
// the crux of the FFT quality win: the normal is sampled independently of mesh tessellation with trilinear
// mip selection, so ripple detail stays crisp toward the horizon without aliasing. The mip is chosen by an
// explicit DISTANCE LOD (not screen derivatives) so the same code is valid in the vertex programs that also
// call this - e.g. the projected caustic grid - not just the fragment. A cubic distance fade then removes
// each cascade past its visible range so the finest ripples don't shimmer far away.
float2 OceanFftNormalTilt(float2 worldXZ)
{
    float camDist = distance(worldXZ, _WorldSpaceCameraPos.xz);
    float2 tilt = float2(0.0, 0.0);
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);
        float domain = max(_OceanFftDomainSizes[c], 1e-3);
        float2 uv = worldXZ / domain;
        float f = saturate(camDist / max(_OceanFftVisibleAreas[c], 1e-3));
        float fade = 1.0 - f * f * f;   // full near the camera, 0 past the cascade's visible range
        float lod = log2(1.0 + camDist / domain); // farther -> coarser mip (distance anti-aliasing)
        tilt += active * fade * _OceanFftNormal.SampleLevel(sampler_OceanFftNormal, float3(uv, slice), lod).xz;
    }
    return tilt;
}

// Sum the accumulated whitecap foam (.w of the per-cascade normal target) across the active cascades,
// with the SAME distance fade + mip LOD as the tilt above, so foam anti-aliases and fades toward the
// horizon exactly like the ripple detail it rides on. The compute silences cascade 0 and damps cascade 1,
// so this just gathers what the temporal accumulation already shaped. Saturated: overlapping cascades can
// sum past 1 on a hard break, but foam coverage is a 0..1 mask.
float OceanFftFoam(float2 worldXZ)
{
    float camDist = distance(worldXZ, _WorldSpaceCameraPos.xz);
    float foam = 0.0;
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);
        float domain = max(_OceanFftDomainSizes[c], 1e-3);
        float2 uv = worldXZ / domain;
        float f = saturate(camDist / max(_OceanFftVisibleAreas[c], 1e-3));
        float fade = 1.0 - f * f * f;
        float lod = log2(1.0 + camDist / domain);
        foam += active * fade * _OceanFftNormal.SampleLevel(sampler_OceanFftNormal, float3(uv, slice), lod).w;
    }
    return saturate(foam);
}

// Sample the TRUE wave-crest "pinch" - the raw displacement-Jacobian fold, saturate(1 - J), written to
// _OceanFftNormal.y by the FFT compute. Peaks on steep / breaking crests (the same fold that seeds foam),
// so it drives the subsurface glow exactly where the surface is folding, rather than proxying it with
// wave height. Same distance fade + mip LOD as the foam/tilt so it anti-aliases identically.
float OceanFftJacobian(float2 worldXZ)
{
    float camDist = distance(worldXZ, _WorldSpaceCameraPos.xz);
    float pinch = 0.0;
    for (int c = 0; c < OCEAN_FFT_MAX_CASCADES; c++)
    {
        float active = (c < (int)_OceanFftCascadeCount) ? 1.0 : 0.0;
        float slice = min((float)c, _OceanFftCascadeCount - 1.0);
        float domain = max(_OceanFftDomainSizes[c], 1e-3);
        float2 uv = worldXZ / domain;
        float f = saturate(camDist / max(_OceanFftVisibleAreas[c], 1e-3));
        float fade = 1.0 - f * f * f;
        float lod = log2(1.0 + camDist / domain);
        pinch += active * fade * _OceanFftNormal.SampleLevel(sampler_OceanFftNormal, float3(uv, slice), lod).y;
    }
    return saturate(pinch);
}

// Shortest wavelength the mesh can resolve at this world xz: grows with distance from the camera
// (the clipmap triangles get bigger). 0 when band-limiting is off (bounded bodies, _LargeWaveDetailSlope = 0).
float LargeBodyWaveMinWavelength(float2 worldXZ)
{
    return distance(worldXZ, _WorldSpaceCameraPos.xz) * _LargeWaveDetailSlope;
}

// Wave HEIGHT (metres) only - for the vertex Y displacement. FFT cascades when active; the amplitude
// knob still scales the swell so the inspector stays live.
float LargeBodyWaveHeight(float2 worldXZ)
{
    if (_OceanFftActive > 0.5)
        return OceanFftDisplacement(worldXZ).y * _LargeWaveAmplitude;
    return EvaluateLargeBodyWave(worldXZ, LargeBodyWaveMinWavelength(worldXZ)).height;
}

// Horizontal Gerstner offset (metres) for the vertex xz displacement, choppiness baked in. Zero when
// _LargeWaveChoppiness = 0, so the surface reduces to the pure vertical swell (unchanged).
float2 LargeBodyWaveDisplacement(float2 worldXZ)
{
    if (_OceanFftActive > 0.5)
        return OceanFftDisplacement(worldXZ).xz * _LargeWaveChoppiness * _LargeWaveAmplitude;
    return EvaluateLargeBodyWave(worldXZ, LargeBodyWaveMinWavelength(worldXZ)).disp * _LargeWaveChoppiness;
}

// Tilt a WORLD-space surface normal by the open-water wave shape at its SOURCE xz (the undisplaced
// position the vertex carried through). 'strength' scales the effect (reuse the body's
// _WaveNormalStrength so it stays art-directable). The tilt is the Jacobian normal of the displaced
// Gerstner surface; at choppiness = 0 it equals -slope, i.e. the original smooth-swell normal.
float3 ApplyLargeBodyWaveNormal(float3 worldNormal, float2 sourceXZ, float strength)
{
    // FFT path: the cascade normals already encode the surface tilt; blend their xz and lean the base
    // normal by it. The per-pixel mipmapped detail (crisp ripples to the horizon) arrives in step 3.
    if (_OceanFftActive > 0.5)
    {
        float2 fftTilt = OceanFftNormalTilt(sourceXZ);
        return normalize(worldNormal + float3(fftTilt.x, 0.0, fftTilt.y) * strength);
    }

    LargeBodyWaveField f = EvaluateLargeBodyWave(sourceXZ, LargeBodyWaveMinWavelength(sourceXZ));
    float q = _LargeWaveChoppiness;
    float dDxdx = f.dispDeriv.x;
    float dDxdz = f.dispDeriv.y; // == dDz/dx
    float dDzdz = f.dispDeriv.z;

    // Tangents of P(x,z) = (x + Q*Dx, height, z + Q*Dz); their cross product is the surface normal.
    float3 tangentX = float3(1.0 + q * dDxdx, f.slope.x, q * dDxdz);
    float3 tangentZ = float3(q * dDxdz, f.slope.y, 1.0 + q * dDzdz);
    float3 n = cross(tangentZ, tangentX);
    float2 tilt = n.xz / max(n.y, LBW_NORMAL_MIN_Y);
    return normalize(worldNormal + float3(tilt.x, 0.0, tilt.y) * strength);
}

#endif // WEBGPUWATER_LARGE_WAVES_INCLUDED
