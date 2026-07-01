// WebGL Water - wind-driven spectral wave layer (Unity 6 / URP port)
//
// A sum of directional sinusoids whose parameters (direction, wavenumber, angular
// speed, amplitude, phase) are generated on the CPU by WaterWaveBank from a
// JONSWAP-shaped spectrum and uploaded as globals. The SAME evaluation runs here
// and on the CPU (WaterWaveBank.SampleHeight/SampleSlope) so the rendered surface
// and the buoyancy physics can never diverge.
//
// Vertical-only displacement (no Gerstner horizontal pinch) is deliberate: it keeps
// height a true function of (x,z), which both the buoyancy sampler and the existing
// _WaterTex normal lookup rely on. At light-breeze lake steepness the missing pinch
// is visually imperceptible.
#ifndef WEBGL_WATER_WAVES_INCLUDED
#define WEBGL_WATER_WAVES_INCLUDED

// Must match WaterWaveBank.MaxWaves on the C# side.
#define WATER_MAX_WAVES 16

// _WaveA[i] = (directionX, directionZ, wavenumber k, angular speed omega)
// _WaveB[i] = (amplitude in pool units, phase offset, unused, unused)
float4 _WaveA[WATER_MAX_WAVES];
float4 _WaveB[WATER_MAX_WAVES];
float  _WaveCount;          // active components (float so it binds via MaterialPropertyBlock); 0 disables
float  _WaveTime;           // shared animation time (published with the bank)
float  _WaveMetersPerUnit;  // pool unit -> metres (waves are defined in metres)

// Phase of component i at metre-space position m.
float WavePhase(int i, float2 m)
{
    return dot(_WaveA[i].xy, m) * _WaveA[i].z - _WaveA[i].w * _WaveTime + _WaveB[i].y;
}

// Height (pool units) of the wind-wave layer at pool-space xz in [-1, 1].
float WaveHeight(float2 poolXZ)
{
    float2 m = poolXZ * _WaveMetersPerUnit;
    int count = (int)_WaveCount;
    float height = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
        height += _WaveB[i].x * sin(WavePhase(i, m));
    return height;
}

// Surface gradient d(height)/d(poolXZ) of the wind-wave layer, in pool units.
// Used to perturb the surface normal: normal.xz = -gradient.
float2 WaveSlope(float2 poolXZ)
{
    float2 m = poolXZ * _WaveMetersPerUnit;
    int count = (int)_WaveCount;
    float2 gradient = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float c = cos(WavePhase(i, m));
        // d/d(poolXZ) introduces a factor k * dir * d(m)/d(poolXZ) = k * dir * metersPerUnit.
        gradient += _WaveB[i].x * c * _WaveA[i].z * _WaveA[i].xy * _WaveMetersPerUnit;
    }
    return gradient;
}

#endif // WEBGL_WATER_WAVES_INCLUDED
