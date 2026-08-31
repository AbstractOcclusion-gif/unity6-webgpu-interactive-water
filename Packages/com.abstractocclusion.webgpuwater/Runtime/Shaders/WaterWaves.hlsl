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

#define RIVER_CURRENT_PHASE_RATE 2.0
#define RIVER_CURRENT_PHASE_WINDOW_SECONDS 0.5

void RiverCurrentWaveSampleXZ(float4 currentData, out float2 sampleA,
                              out float2 sampleB, out float phaseBlend)
{
    // UV1.xy is metric ribbon space. ZW is the baked lateral/downstream velocity, so the same
    // obstacle-deflected flow transports the visible wave pattern and the physical current.
    // Local velocity multiplied by unbounded time accumulates unlimited shear wherever adjacent
    // cells differ, eventually straightening the whole surface into longitudinal lanes. Two
    // bounded phases preserve forward transport and cross-fade through each reset without a pop.
    float phaseA = frac(_WaveTime * RIVER_CURRENT_PHASE_RATE);
    float phaseB = frac(phaseA + 0.5);
    phaseBlend = abs(phaseA * 2.0 - 1.0);
    float inverseWaveMetres = rcp(max(_WaveMetersPerUnit, WAVE_METERS_MIN));
    sampleA = (currentData.xy - currentData.zw *
               (phaseA * RIVER_CURRENT_PHASE_WINDOW_SECONDS)) * inverseWaveMetres;
    sampleB = (currentData.xy - currentData.zw *
               (phaseB * RIVER_CURRENT_PHASE_WINDOW_SECONDS)) * inverseWaveMetres;
}

// ---- connected river mouth outflows ---------------------------------------------------------
// The CPU publishes the same bounded plume used by WaterRiverCurrentField. The body shader uses
// it to carry wind waves, micro detail and foam away from the mouth instead of snapping back to the
// receiving body's stationary field at the terminal row.
#define WATER_MAX_MOUTH_OUTFLOWS 4
#define MOUTH_OUTFLOW_LATERAL_FULL_WEIGHT 0.65
#define MOUTH_OUTFLOW_CURRENT_RISE_FRACTION 0.25
#define MOUTH_OUTFLOW_FOAM_STRENGTH_RISE_FRACTION 0.25
#define MOUTH_OUTFLOW_FOAM_EDGE_FEATHER_RATIO 0.15
#define MOUTH_OUTFLOW_MIN_EXTENT 0.01
#define MOUTH_OUTFLOW_DEFAULT_PATTERN_SIZE 1.0
#define RIVER_OWN_MOUTH_OUTFLOW_INDEX 0

float _MouthOutflowCount;
float4 _MouthOutflowOrigins[WATER_MAX_MOUTH_OUTFLOWS];
float4 _MouthOutflowDirections[WATER_MAX_MOUTH_OUTFLOWS];
float4 _MouthOutflowParameters[WATER_MAX_MOUTH_OUTFLOWS];
float4 _MouthOutflowFoamFrames[WATER_MAX_MOUTH_OUTFLOWS];
float4 _MouthOutflowFoamAppearance[WATER_MAX_MOUTH_OUTFLOWS];

void EvaluateMouthOutflow(float2 worldXZ, out float currentInfluence,
                          out float2 currentVelocity, out float foamCoverage,
                          out float2 foamPatternMetres, out float foamPatternInfluence,
                          out float3 foamAppearance)
{
    currentInfluence = 0.0;
    currentVelocity = 0.0;
    foamCoverage = 0.0;
    foamPatternMetres = worldXZ;
    foamPatternInfluence = 0.0;
    foamAppearance = float3(MOUTH_OUTFLOW_DEFAULT_PATTERN_SIZE, 0.0, 0.0);
    float dominantPatternCoverage = 0.0;
    float accumulatedTransportWeight = 0.0;
    int count = clamp((int)_MouthOutflowCount, 0, WATER_MAX_MOUTH_OUTFLOWS);
    [loop]
    for (int outflowIndex = 0; outflowIndex < count; outflowIndex++)
    {
        float4 originData = _MouthOutflowOrigins[outflowIndex];
        float4 directionData = _MouthOutflowDirections[outflowIndex];
        float4 parameters = _MouthOutflowParameters[outflowIndex];
        float2 downstream = normalize(directionData.xy);
        float2 right = float2(downstream.y, -downstream.x);
        float2 offset = worldXZ - originData.xy;
        float downstreamDistance = dot(offset, downstream);
        float lengthMeters = max(directionData.z, MOUTH_OUTFLOW_MIN_EXTENT);
        float halfWidthMeters = max(originData.z, MOUTH_OUTFLOW_MIN_EXTENT);
        if (downstreamDistance < 0.0 || downstreamDistance >= lengthMeters) continue;

        float expandedHalfWidth = halfWidthMeters +
                                  directionData.w * downstreamDistance;
        float lateralDistance = abs(dot(offset, right));
        float lateralRatio = lateralDistance /
                             max(expandedHalfWidth, MOUTH_OUTFLOW_MIN_EXTENT);
        float lateralWeight = 1.0 - smoothstep(
            MOUTH_OUTFLOW_LATERAL_FULL_WEIGHT, 1.0, lateralRatio);

        // The receiving body must equal its own undrifted wave field exactly on the border.
        // Current then rises inside the body before decaying, avoiding a time-varying phase seam
        // while retaining the physical current's full terminal speed in gameplay queries.
        float currentRiseLength = max(
            min(halfWidthMeters, lengthMeters * MOUTH_OUTFLOW_CURRENT_RISE_FRACTION),
            MOUTH_OUTFLOW_MIN_EXTENT);
        float currentLongitudinalWeight =
            smoothstep(0.0, currentRiseLength, downstreamDistance) *
            (1.0 - smoothstep(currentRiseLength, lengthMeters, downstreamDistance));
        float currentPlumeWeight = currentLongitudinalWeight * lateralWeight;

        float currentStrength = max(parameters.y, 0.0);
        float currentWeight = currentPlumeWeight * currentStrength;
        // Transport speed is deliberately NOT multiplied by the spatial plume envelope. Scaling
        // velocity by position and then by absolute time continuously stretches neighbouring UVs
        // apart. The envelope belongs only to the output mix; each source transports a rigid field.
        currentVelocity += downstream * parameters.x * currentStrength * currentWeight;
        accumulatedTransportWeight += currentWeight;
        currentInfluence = max(currentInfluence, saturate(currentWeight));

        // Foam has its own shorter lifetime. It starts at full terminal coverage on the receiving
        // side only, so neither renderer overlaps across the ownership border.
        float foamLengthMeters = clamp(
            parameters.w, MOUTH_OUTFLOW_MIN_EXTENT, lengthMeters);
        float foamLongitudinalWeight =
            1.0 - smoothstep(0.0, foamLengthMeters, downstreamDistance);
        // Current fades before the banks to avoid a hard velocity edge, but river bank foam must
        // cross the whole mouth. Give foam full coverage through the authored half-width and only
        // feather outside it; sharing lateralWeight created a small triangular miss at oblique
        // body junctions where one bank lies visibly inside the receiving surface.
        float foamFeatherWidth = max(
            expandedHalfWidth * MOUTH_OUTFLOW_FOAM_EDGE_FEATHER_RATIO,
            MOUTH_OUTFLOW_MIN_EXTENT);
        float foamLateralWeight = 1.0 - smoothstep(
            expandedHalfWidth, expandedHalfWidth + foamFeatherWidth, lateralDistance);
        // Artist strength shapes persistence inside the body, not ownership at the shared row.
        // Starting with the exact terminal value prevents any strength setting from reopening
        // the junction seam.
        float foamStrengthRiseLength = max(
            min(halfWidthMeters,
                foamLengthMeters * MOUTH_OUTFLOW_FOAM_STRENGTH_RISE_FRACTION),
            MOUTH_OUTFLOW_MIN_EXTENT);
        float foamStrengthWeight = lerp(
            1.0, max(parameters.z, 0.0),
            smoothstep(0.0, foamStrengthRiseLength, downstreamDistance));
        float sourceFoam = saturate(
            originData.w * foamStrengthWeight *
            foamLongitudinalWeight * foamLateralWeight);
        foamCoverage = 1.0 - (1.0 - foamCoverage) * (1.0 - sourceFoam);

        // The river pattern uses signed lateral metres and cumulative downstream metres. Keep
        // that exact frame on the receiving side so the ownership handoff changes only geometry,
        // never foam phase. Coordinates remain rigidly advected at the terminal river speed;
        // spatial plume weights blend sampled outputs later and therefore cannot shear the UVs.
        if (sourceFoam > dominantPatternCoverage)
        {
            float4 foamFrame = _MouthOutflowFoamFrames[outflowIndex];
            float2 foamRight = foamFrame.xy * rsqrt(max(
                dot(foamFrame.xy, foamFrame.xy),
                MOUTH_OUTFLOW_MIN_EXTENT * MOUTH_OUTFLOW_MIN_EXTENT));
            float lateralMeters = dot(offset, foamRight) - foamFrame.w * _WaveTime;
            float downstreamMetres = foamFrame.z + downstreamDistance -
                                     parameters.x * _WaveTime;
            foamPatternMetres = float2(lateralMeters, downstreamMetres);
            foamPatternInfluence = foamLongitudinalWeight * foamLateralWeight;
            foamAppearance = _MouthOutflowFoamAppearance[outflowIndex].xyz;
            dominantPatternCoverage = sourceFoam;
        }
    }

    if (accumulatedTransportWeight > MOUTH_OUTFLOW_MIN_EXTENT)
        currentVelocity /= accumulatedTransportWeight;
}

float2 MouthOutflowDriftedWorldXZ(float2 worldXZ)
{
    float influence;
    float2 velocity;
    float foam;
    float2 foamPatternMetres;
    float foamPatternInfluence;
    float3 foamAppearance;
    EvaluateMouthOutflow(
        worldXZ, influence, velocity, foam, foamPatternMetres, foamPatternInfluence,
        foamAppearance);
    return worldXZ - velocity * _WaveTime;
}

float MouthOutflowCurrentInfluence(float2 worldXZ)
{
    float influence;
    float2 velocity;
    float foam;
    float2 foamPatternMetres;
    float foamPatternInfluence;
    float3 foamAppearance;
    EvaluateMouthOutflow(
        worldXZ, influence, velocity, foam, foamPatternMetres, foamPatternInfluence,
        foamAppearance);
    return influence;
}

float MouthOutflowFoamCoverage(float2 worldXZ)
{
    float influence;
    float2 velocity;
    float foam;
    float2 foamPatternMetres;
    float foamPatternInfluence;
    float3 foamAppearance;
    EvaluateMouthOutflow(
        worldXZ, influence, velocity, foam, foamPatternMetres, foamPatternInfluence,
        foamAppearance);
    return foam;
}

float2 MouthOutflowFoamPatternMetres(
    float2 worldXZ, out float patternInfluence, out float3 foamAppearance)
{
    float currentInfluence;
    float2 currentVelocity;
    float foamCoverage;
    float2 patternMetres;
    EvaluateMouthOutflow(
        worldXZ, currentInfluence, currentVelocity, foamCoverage,
        patternMetres, patternInfluence, foamAppearance);
    return patternMetres;
}

// A river renderer publishes only its own mouth, in slot zero. Unlike the receiving-body query
// above, this projection deliberately remains valid immediately upstream of the border: the
// river's terminal band needs to approach the exact pattern that starts on the body's first row.
// The sampled looks are blended later; these incompatible metric coordinates are never lerped.
float2 RiverMouthOutflowFoamPatternMetres(
    float2 worldXZ, out float patternInfluence, out float3 foamAppearance)
{
    patternInfluence = 0.0;
    foamAppearance = float3(MOUTH_OUTFLOW_DEFAULT_PATTERN_SIZE, 0.0, 0.0);
    if (_MouthOutflowCount < 0.5) return worldXZ;

    float4 originData = _MouthOutflowOrigins[RIVER_OWN_MOUTH_OUTFLOW_INDEX];
    float4 directionData = _MouthOutflowDirections[RIVER_OWN_MOUTH_OUTFLOW_INDEX];
    float4 parameters = _MouthOutflowParameters[RIVER_OWN_MOUTH_OUTFLOW_INDEX];
    float4 foamFrame = _MouthOutflowFoamFrames[RIVER_OWN_MOUTH_OUTFLOW_INDEX];
    float2 downstream = directionData.xy * rsqrt(max(
        dot(directionData.xy, directionData.xy),
        MOUTH_OUTFLOW_MIN_EXTENT * MOUTH_OUTFLOW_MIN_EXTENT));
    float2 foamRight = foamFrame.xy * rsqrt(max(
        dot(foamFrame.xy, foamFrame.xy),
        MOUTH_OUTFLOW_MIN_EXTENT * MOUTH_OUTFLOW_MIN_EXTENT));
    float2 offset = worldXZ - originData.xy;
    float lateralMeters = dot(offset, foamRight) - foamFrame.w * _WaveTime;
    float downstreamMetres = foamFrame.z + dot(offset, downstream) -
                             parameters.x * _WaveTime;
    patternInfluence = 1.0;
    foamAppearance =
        _MouthOutflowFoamAppearance[RIVER_OWN_MOUTH_OUTFLOW_INDEX].xyz;
    return float2(lateralMeters, downstreamMetres);
}

float RiverMouthOutflowTerminalFoamCoverage()
{
    if (_MouthOutflowCount < 0.5) return 0.0;
    return saturate(_MouthOutflowOrigins[RIVER_OWN_MOUTH_OUTFLOW_INDEX].w);
}

// ---- body-border seam wave anchor (KWS/RAM study 2026-08-30) ----
// A connected terminal band cross-fades TOWARD the receiving body's wind-wave field, but
// WindWaveSampleXZ above anchors in THIS renderer's pool frame - the PARENT body's for a river
// ribbon. At an end whose receiving body is NOT the parent (the source-end reservoir of a
// lake-parented river), the two sides of the seam sample the same metric wave bank at two
// different anchors, and the phase jump prints as a moving texture line at the boundary. These
// per-end uniforms carry the receiving body's own pool anchor so the terminal grid target IS that
// body's rendered field: frame = world->pool rows (xAxis.xz / extent.x, zAxis.xz / extent.z),
// anchor = (center.xz, receiving-body metres-per-pool-unit / PARENT metres-per-pool-unit - the
// bank phase multiplies by the PARENT's _WaveMetersPerUnit downstream - and w as the mode gate:
// 0 = inactive, keep the parent-anchored path; 1 = anchor to the receiving body). Published by
// WaterRiverSurface each LateUpdate; unpublished defaults are zero, so every non-river renderer
// and every unconnected end keeps today's path bit-for-bit.
float4 _RiverEndWaveFrame0;  // source end
float4 _RiverEndWaveAnchor0;
float4 _RiverEndWaveFrame1;  // mouth end
float4 _RiverEndWaveAnchor1;

#define RIVER_END_SELECTOR_EPSILON 0.0001

float2 RiverEndWindWaveSampleXZ(float2 poolXZ, float2 worldXZ, float endSelector)
{
    // The dedicated selector avoids inferring an end from longitudinal coordinates. Zero means
    // an interior river vertex or a pool mesh and therefore preserves the parent wave path.
    if (abs(endSelector) < RIVER_END_SELECTOR_EPSILON)
        return WindWaveSampleXZ(poolXZ, worldXZ);
    bool sourceEnd = endSelector < 0.0;
    float4 frame = sourceEnd ? _RiverEndWaveFrame0 : _RiverEndWaveFrame1;
    float4 anchor = sourceEnd ? _RiverEndWaveAnchor0 : _RiverEndWaveAnchor1;
    if (anchor.w < 0.5) return WindWaveSampleXZ(poolXZ, worldXZ);
    // Pure ALU consumers only (WaveHeight/WaveSlope), so this varying branch is derivative-safe.
    float2 rel = worldXZ - anchor.xy;
    return float2(dot(frame.xy, rel), dot(frame.zw, rel)) * anchor.z;
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
