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

// Guard for the world-metres division below and for every consumer that mirrors it.
#define WAVE_METERS_MIN 1e-3

// 1 = this body samples the wind-wave layer in WORLD metres (oceans / unbounded open water: the
// pattern must not slide or rescale with the volume box); 0 = pool xz (bounded bodies).
float _OceanWorldWaves;

// Uniform surface-current drift in METRES, premultiplied on the CPU (current velocity * the
// SAME wave clock published as _WaveTime), so every include chain - this surface graph, the
// FFT cascade reads and the caustic receiver - subtracts ONE synchronized offset with no
// per-chain time dependency. Applied at SAMPLE time: crests, the whitecap deposit and the
// waterline drift together; offsetting only a foam read would slide foam off the crests that
// made it (KWS1 precedent: its foam pattern UV is advected by the fluid velocity).
// Guarded: WaterLargeWaves.hlsl and WaterLargeCausticWaves.hlsl carry the same block so each
// include chain compiles standalone. Zero (Current Speed 0, the default) is bit-identical.
#ifndef WEBGPUWATER_OCEAN_CURRENT_INCLUDED
#define WEBGPUWATER_OCEAN_CURRENT_INCLUDED
float4 _OceanCurrentOffset;

float2 OceanCurrentDrift(float2 worldXZ)
{
    return worldXZ - _OceanCurrentOffset.xy;
}
#endif

// Coordinate fed to the wind-wave layer (WaveHeight/WaveSlope). ONE definition for every consumer -
// the surface vertex/fragment stages, the waterline field and the foam-particle surface glue. A
// consumer that picks its own coordinate silently desyncs its wind waves from the rendered surface
// (the 2026-08-10 foam-quad crossing bug on open water). Previously triplicated across
// WaterSurfaceVertStage and WaterWaterline; moved here so it can never drift again.
float2 WindWaveSampleXZ(float2 poolXZ, float2 worldXZ)
{
    if (_OceanWorldWaves > 0.5)
        return OceanCurrentDrift(worldXZ) / max(_WaveMetersPerUnit, WAVE_METERS_MIN);
    return poolXZ;
}

float2 RiverCurrentWaveSampleXZ(float4 currentData)
{
    // UV1.xy is metric ribbon space. ZW is the baked lateral/downstream velocity, so the same
    // obstacle-deflected flow transports the visible wave pattern and the physical current.
    float2 riverMetres = currentData.xy - currentData.zw * _WaveTime;
    return riverMetres / max(_WaveMetersPerUnit, WAVE_METERS_MIN);
}

// Envelope carriers: the group envelope is the MAGNITUDE of the complex sum of these four waves.
// Random phases (below) make that magnitude Rayleigh-ish - the stochastic envelope of a real
// narrow-banded sea (Longuet-Higgins 1984) - so chop arrives in APERIODIC sets and lulls instead of
// the metronome the old base+sinA+sinB envelope produced. Each is (dirX, dirZ, wavenumber, angular
// speed); their speed is the CARRIER's group velocity, so crests are still born at the back of a set
// and die at the front. C# pair: WaterWaveBank.GroupA/B/C/D.
float4 _WaveGroupA;
float4 _WaveGroupB;
float4 _WaveGroupC;
float4 _WaveGroupD;
// Random phase per envelope carrier (seeded on the CPU alongside the component phases, so a given
// authored state always reproduces the same sets). C# pair: WaterWaveBank.GroupPhases.
float4 _WaveGroupPhases;
// (envelope constant share, envelope magnitude gain, Stokes coefficient, Stokes DC offset). C# pair: WaterWaveBank.Shape.
float4 _WaveShape;
// Keeps the authored significant height honest as the crest term sharpens. C# pair: WaterWaveBank.StokesNorm.
float  _WaveStokesNorm;

// Phase of component i at metre-space position m.
float WavePhase(int i, float2 m)
{
    return dot(_WaveA[i].xy, m) * _WaveA[i].z - _WaveA[i].w * _WaveTime + _WaveB[i].y;
}

// Guards the |z| division in the envelope gradient at exact four-way phasor cancellation, where the
// gradient direction is meaningless anyway. KEEP: WaterWaveBank.GroupMagnitudeEpsilon - the CPU
// mirror divides by the same floor (validator-guarded pair).
#define WAVE_GROUP_MAG_EPSILON 0.0001

// Group envelope at metre-space position m, plus its own gradient (per metre) for the slope path.
// env = _WaveShape.x + _WaveShape.y * |z|, z = sum of the four carrier phasors. A grouping of 0
// zeroes _WaveShape.y and this is the constant _WaveShape.x, exactly as before.
float WaveGroupEnvelope(float2 m, out float2 envelopeGradient)
{
    // Grouping off -> the envelope is the constant _WaveShape.x and its gradient is zero, so the
    // transcendentals below are pure waste. The test is on a UNIFORM, so the branch is coherent across
    // the whole draw rather than per pixel. This is not a rare path: every scene migrated from the old
    // rig starts at grouping 0, and those were paying for four sincos pairs per water fragment to
    // multiply by a constant.
    if (_WaveShape.y == 0.0)
    {
        envelopeGradient = 0.0;
        return _WaveShape.x;
    }
    float argA = dot(_WaveGroupA.xy, m) * _WaveGroupA.z - _WaveGroupA.w * _WaveTime + _WaveGroupPhases.x;
    float argB = dot(_WaveGroupB.xy, m) * _WaveGroupB.z - _WaveGroupB.w * _WaveTime + _WaveGroupPhases.y;
    float argC = dot(_WaveGroupC.xy, m) * _WaveGroupC.z - _WaveGroupC.w * _WaveTime + _WaveGroupPhases.z;
    float argD = dot(_WaveGroupD.xy, m) * _WaveGroupD.z - _WaveGroupD.w * _WaveTime + _WaveGroupPhases.w;
    float sinA, cosA, sinB, cosB, sinC, cosC, sinD, cosD;
    sincos(argA, sinA, cosA);
    sincos(argB, sinB, cosB);
    sincos(argC, sinC, cosC);
    sincos(argD, sinD, cosD);
    float re = cosA + cosB + cosC + cosD;
    float im = sinA + sinB + sinC + sinD;
    float magnitude = sqrt(re * re + im * im);
    // d|z|/dm = (re * d(re)/dm + im * d(im)/dm) / |z|, with d(re)/dm = -k*dir*sin per carrier and
    // d(im)/dm = +k*dir*cos. Mirrored EXACTLY by WaterWaveBank.GroupEnvelope - buoyancy reads the
    // same sets the surface renders.
    float2 dRe = -(_WaveGroupA.xy * (_WaveGroupA.z * sinA) + _WaveGroupB.xy * (_WaveGroupB.z * sinB)
                   + _WaveGroupC.xy * (_WaveGroupC.z * sinC) + _WaveGroupD.xy * (_WaveGroupD.z * sinD));
    float2 dIm = _WaveGroupA.xy * (_WaveGroupA.z * cosA) + _WaveGroupB.xy * (_WaveGroupB.z * cosB)
                 + _WaveGroupC.xy * (_WaveGroupC.z * cosC) + _WaveGroupD.xy * (_WaveGroupD.z * cosD);
    envelopeGradient = _WaveShape.y * (re * dRe + im * dIm) / max(magnitude, WAVE_GROUP_MAG_EPSILON);
    return _WaveShape.x + _WaveShape.y * magnitude;
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
