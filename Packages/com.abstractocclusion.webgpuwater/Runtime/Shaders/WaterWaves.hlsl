// WebGpuWater - wind-driven spectral wave layer (Unity 6 / URP port)
//
// A sum of directional sinusoids whose parameters (direction, wavenumber, angular
// speed, amplitude, phase) are generated on the CPU by WaterWaveBank from an AUTHORED
// wavelength and significant height, then shaped by a travelling group envelope and a
// second-order Stokes crest term. The SAME evaluation runs here and on the CPU
// (WaterWaveBank.SampleHeight/SampleSlope) so the rendered surface and the buoyancy
// physics can never diverge - read the two side by side when changing either.
//
// Vertical-only displacement (no Gerstner horizontal pinch) is deliberate: it keeps
// height a true function of (x,z), which both the buoyancy sampler and the existing
// _WaterTex normal lookup rely on. Crest sharpening is therefore done in the VERTICAL,
// by the Stokes term, which preserves that property exactly.
#ifndef WEBGL_WATER_WAVES_INCLUDED
#define WEBGL_WATER_WAVES_INCLUDED

// NAMING TRAP: do not call a local here "linear" - it is a reserved interpolation modifier in D3D
// HLSL (alongside centroid / nointerpolation / noperspective / sample) and the declaration parses as
// a modifier on the next token, giving a bare "syntax error: unexpected token" a long way from the
// real cause. Hence linearHeight below.
//
// Must match WaterWaveBank.MaxWaves on the C# side - WaterWaveConstantsValidator guards the pair.
// C# larger over-runs these declared arrays on SetVectorArray; C# smaller leaves waves unwritten.
#define WATER_MAX_WAVES 16

// _WaveA[i] = (directionX, directionZ, wavenumber k, angular speed omega)
// _WaveB[i] = (amplitude in pool units, phase offset, unused, unused)
float4 _WaveA[WATER_MAX_WAVES];
float4 _WaveB[WATER_MAX_WAVES];
float  _WaveCount;          // active components (float so it binds via MaterialPropertyBlock); 0 disables
float  _WaveTime;           // shared animation time (published with the bank)
float  _WaveMetersPerUnit;  // pool unit -> metres (waves are defined in metres)

// Group envelopes: two crossing modulations that make the chop arrive in SETS instead of a uniform
// buzz. Each is (dirX, dirZ, wavenumber, angular speed); their speed is the CARRIER's group velocity,
// so crests are born at the back of a set and die at the front. C# pair: WaterWaveBank.GroupA/GroupB.
float4 _WaveGroupA;
float4 _WaveGroupB;
// (envelope base, envelope amplitude, Stokes coefficient, Stokes DC offset). C# pair: WaterWaveBank.Shape.
float4 _WaveShape;
// Keeps the authored significant height honest as the crest term sharpens. C# pair: WaterWaveBank.StokesNorm.
float  _WaveStokesNorm;

// Phase of component i at metre-space position m.
float WavePhase(int i, float2 m)
{
    return dot(_WaveA[i].xy, m) * _WaveA[i].z - _WaveA[i].w * _WaveTime + _WaveB[i].y;
}

// Group envelope at metre-space position m, plus its own gradient (per metre) for the slope path.
// Mean is _WaveShape.x, so a grouping of 0 collapses the amplitude to zero and this is a constant.
float WaveGroupEnvelope(float2 m, out float2 envelopeGradient)
{
    // Grouping off -> the envelope is the constant _WaveShape.x and its gradient is zero, so the four
    // transcendentals below are pure waste. The test is on a UNIFORM, so the branch is coherent across
    // the whole draw rather than per pixel. This is not a rare path: every scene migrated from the old
    // rig starts at grouping 0, and those were paying for two sines and two cosines per water fragment
    // to multiply by a constant.
    if (_WaveShape.y == 0.0)
    {
        envelopeGradient = 0.0;
        return _WaveShape.x;
    }
    float argA = dot(_WaveGroupA.xy, m) * _WaveGroupA.z - _WaveGroupA.w * _WaveTime;
    float argB = dot(_WaveGroupB.xy, m) * _WaveGroupB.z - _WaveGroupB.w * _WaveTime;
    float amplitude = _WaveShape.y;
    envelopeGradient = amplitude * (_WaveGroupA.xy * (_WaveGroupA.z * cos(argA))
                                    + _WaveGroupB.xy * (_WaveGroupB.z * cos(argB)));
    return _WaveShape.x + amplitude * (sin(argA) + sin(argB));
}

// Second-order Stokes crest shaping: h -> norm * (h + a*h^2 - a*variance). Sharpens crests and
// flattens troughs the way a real wave does, stays a pure function of (x,z), and scales with k*h so
// it is inert on calm water and strongest exactly where the pure sines looked worst.
float WaveStokesSharpen(float height)
{
    return _WaveStokesNorm * (height + _WaveShape.z * height * height - _WaveShape.w);
}

// d/dh of the above - the chain-rule factor every derivative path needs.
float WaveStokesDerivative(float height)
{
    return _WaveStokesNorm * (1.0 + 2.0 * _WaveShape.z * height);
}

// Height (pool units) of the wind-wave layer at pool-space xz in [-1, 1].
float WaveHeight(float2 poolXZ)
{
    float2 m = poolXZ * _WaveMetersPerUnit;
    int count = (int)_WaveCount;
    float linearHeight = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
        linearHeight += _WaveB[i].x * sin(WavePhase(i, m));
    float2 unusedGradient;
    return WaveStokesSharpen(linearHeight * WaveGroupEnvelope(m, unusedGradient));
}

// Surface gradient d(height)/d(poolXZ) of the wind-wave layer, in pool units.
// Used to perturb the surface normal: normal.xz = -gradient.
float2 WaveSlope(float2 poolXZ)
{
    float2 m = poolXZ * _WaveMetersPerUnit;
    int count = (int)_WaveCount;
    float linearHeight = 0.0;
    float2 gradient = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float phase = WavePhase(i, m);
        linearHeight += _WaveB[i].x * sin(phase);
        // d/d(poolXZ) introduces a factor k * dir * d(m)/d(poolXZ) = k * dir * metersPerUnit.
        gradient += _WaveB[i].x * cos(phase) * _WaveA[i].z * _WaveA[i].xy * _WaveMetersPerUnit;
    }
    float2 envelopeGradient;
    float envelope = WaveGroupEnvelope(m, envelopeGradient);
    envelopeGradient *= _WaveMetersPerUnit;   // the envelope's phase is in metres too
    // Product rule through the envelope, then the chain rule through the crest term.
    return WaveStokesDerivative(linearHeight * envelope)
           * (gradient * envelope + envelopeGradient * linearHeight);
}

#endif // WEBGL_WATER_WAVES_INCLUDED
