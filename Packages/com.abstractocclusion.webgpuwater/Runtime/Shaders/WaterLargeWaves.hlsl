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

// Reuses _WaveTime (declared in WaterWaves.hlsl, published every frame) as the shared clock, so the
// open-water waves animate in lockstep with the rest of the water and need no new uniform yet.

// --- Placeholder spectrum constants (world units). Tuned for a light-breeze lake/ocean; these
//     become FFT spectrum inputs (wind speed / fetch) in step 2. ---
#define LBW_WAVE_COUNT         12
#define LBW_BASE_WAVELENGTH    9.0    // metres, longest component
#define LBW_WAVELENGTH_FALLOFF 0.82   // each component this fraction of the previous (shorter waves)
#define LBW_BASE_AMPLITUDE     0.14   // metres, height amplitude of the longest component
#define LBW_AMPLITUDE_FALLOFF  0.76   // shorter waves carry less energy
#define LBW_DIR_SPREAD         1.05   // radians of direction fan around the wind heading
#define LBW_GRAVITY            9.81
#define LBW_WIND_HEADING       0.6    // radians; wind direction (placeholder until wired to a uniform)
#define LBW_TWO_PI             6.28318530718

// Cheap per-component hash in [0,1). Used to SCATTER each wave's direction and phase so crests do
// not line up into regular parallel ridges (the "corduroy" look of a coherent wave sum).
float LbwHash(float n)
{
    return frac(sin(n * 12.9898) * 43758.5453);
}

// Evaluate the open-water wave field at a WORLD-space xz (metres):
// returns (height, dHeight/dx, dHeight/dz). Height and slope come from the SAME components, so the
// vertex displacement and the fragment normal always agree.
float3 LargeBodyWave(float2 worldXZ)
{
    float height = 0.0;
    float2 slope = float2(0.0, 0.0);
    float wavelength = LBW_BASE_WAVELENGTH;
    float amplitude = LBW_BASE_AMPLITUDE;

    [loop]
    for (int n = 0; n < LBW_WAVE_COUNT; n++)
    {
        // Scatter each component's direction within the wind fan and give it a random phase, so
        // the crests do NOT align into parallel ridges. Wind-biased (fan centred on the heading),
        // but incoherent - the key to a natural, smooth surface rather than corduroy.
        float fn = (float)n;
        float headingJitter = (LbwHash(fn + 1.0) * 2.0 - 1.0) * LBW_DIR_SPREAD;
        float heading = LBW_WIND_HEADING + headingJitter;
        float2 dir = float2(cos(heading), sin(heading));
        float phaseOffset = LbwHash(fn + 17.0) * LBW_TWO_PI;

        float k = LBW_TWO_PI / max(wavelength, 1e-3);   // wavenumber
        float omega = sqrt(LBW_GRAVITY * k);            // deep-water dispersion
        float phase = dot(dir, worldXZ) * k - omega * _WaveTime + phaseOffset;

        height += amplitude * sin(phase);
        slope  += amplitude * k * dir * cos(phase);     // d/dxz of A*sin(phase)

        wavelength *= LBW_WAVELENGTH_FALLOFF;
        amplitude  *= LBW_AMPLITUDE_FALLOFF;
    }
    return float3(height, slope.x, slope.y);
}

// Wave HEIGHT (metres) only - for the vertex displacement.
float LargeBodyWaveHeight(float2 worldXZ)
{
    return LargeBodyWave(worldXZ).x;
}

// Tilt a WORLD-space surface normal by the open-water wave slope. 'strength' scales the effect
// (reuse the body's _WaveNormalStrength so it stays art-directable). A height gradient g gives
// normal.xz = -g, so we add (-slope) into the xz of the (near-flat) world normal and renormalise.
float3 ApplyLargeBodyWaveNormal(float3 worldNormal, float2 worldXZ, float strength)
{
    float2 slope = LargeBodyWave(worldXZ).yz * strength;
    return normalize(worldNormal + float3(-slope.x, 0.0, -slope.y));
}

#endif // WEBGPUWATER_LARGE_WAVES_INCLUDED
