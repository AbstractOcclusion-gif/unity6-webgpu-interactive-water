// LargeWaveField - CPU mirror of the open-water wave field (Runtime/Shaders/WaterLargeWaves.hlsl).
//
// Kept BYTE-FOR-BYTE in lockstep with the shader's LargeBodyWave() so open-water buoyancy matches
// the rendered surface without a GPU readback - the same CPU/GPU-mirror pattern the package already
// uses for WaterWaveBank <-> WaterWaves.hlsl. If you change the wave constants or the hash in the
// HLSL, change them here too (and vice versa). Two bands are summed: the wind CHOP band and the
// long-period SWELL band, exactly as LbwAccumulateBand does in the shader.
//
// COASTLINE (P1/P2/P5-lite): the shore transform (per-component shoal attenuation, refraction
// toward shore, phase compression, Green's-law growth) and the surf breaker fronts
// (WaterSurfWaves.hlsl) are mirrored here too, fed by the SAME baked field the shaders sample
// (WaterShoreDepthField keeps CPU copies - no readback). Pass ShoreWaveContext.Inactive on bodies
// without a shore field and every term collapses to the old open-water math.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Shore-transform + surf-front inputs for the CPU wave mirror. Mirrors the
    /// _Shore*/_Surf* globals published by WaterShoreDepthField.Publish.</summary>
    internal struct ShoreWaveContext
    {
        public WaterShoreDepthField Field; // null = no shore (open water everywhere)
        public WaterSeaStateFetchField FetchField; // canonical CPU fetch bake; null = weight one
        public float ShoalDepth;           // _ShoreShoalDepth (attenuation band; follows the sea state)
        public float GreenBandDepth;       // _ShoreGreenBandDepth (authored band; Green's-law amplification)
        public float Refraction;           // _ShoreRefraction
        public float Compression;          // _ShoreCompression / _SurfCompression (one knob)
        public float Greens;               // _ShoreGreens / _SurfGreens (one knob)
        public bool SurfActive;            // _SurfActive
        public float SurfAmplitude;        // _SurfAmplitude
        public float SurfWavelength;       // _SurfWavelength (the EFFECTIVE spacing - auto-derived
                                           // from the period when the body's Auto toggle is on)
        public float SurfPeriod;           // _SurfPeriod
        public float SurfBeatTime;         // _SurfBeatTime - THE MASTER SURF BEAT (the body's wave
                                           // clock wrapped to SurfBeatWrapFronts periods). Every
                                           // surf term below uses THIS, never the ambient time, so
                                           // the mirror and the render agree forever (the unwrapped
                                           // clock let float32 sin() drift away from Mathf.Sin).
        public float SurfBandDepth;        // _SurfBandDepth
        public float SurfSetStrength;      // _SurfSetStrength
        public float SurfCrestLength;      // _SurfCrestLength
        public float SurfCrestVariation;   // _SurfCrestVariation
        public float SurfCrestPersistence; // _SurfCrestPersistence
        public float SurfDirectionality;   // _SurfDirectionality
        public float SurfWindDirX;         // _SurfWindDirXZ.x (cos of the swell heading)
        public float SurfWindDirZ;         // _SurfWindDirXZ.y (sin of the swell heading)
        public float SurfLean;             // _SurfLean
        public float SurfAmbientFade;      // _SurfAmbientFade

        public static ShoreWaveContext Inactive => default;
    }

    /// <summary>World-space open-water wave height, matching the large-body surface shader.</summary>
    internal static class LargeWaveField
    {
        // Chop band - these MUST match the LBW_* defines in WaterLargeWaves.hlsl.
        const int WaveCount = 12;
        const float BaseWavelength = 9.0f;
        const float WavelengthFalloff = 0.82f;
        const float BaseAmplitude = 0.14f;
        const float AmplitudeFalloff = 0.76f;
        const float DirectionSpread = 1.05f;
        const float ChopPhaseSeed = 1.0f;

        // Long-period swell band - must match the LBW_SWELL_* defines. Base wavelength/height are
        // passed in (the art knobs); base amplitude is 1 so the height knob reads as metres.
        const int SwellCount = 4;
        const float SwellBaseAmplitude = 1.0f;
        const float SwellWavelengthFalloff = 0.68f;
        const float SwellAmplitudeFalloff = 0.85f;
        const float SwellDirectionSpread = 0.5f;
        const float SwellPhaseSeed = 101.0f;

        const float Gravity = 9.81f;
        const float TwoPi = 6.28318530718f;

        // Surf-front constants - MUST match the SURF_* defines in WaterSurfWaves.hlsl (guarded by
        // WaterWaveConstantsValidator). Only height-affecting constants are mirrored; the
        // whitewash/breaker foam shaping is render-only and has no CPU counterpart.
        const float SurfMinDepth = 0.05f;
        internal const float SurfBeatWrapFronts = 1280f;      // SURF_BEAT_WRAP_FRONTS
        const float SurfCrestSeedDriftA = 0.34852044f; // SURF_CREST_SEED_DRIFT_A (2pi*71/1280)
        const float SurfCrestSeedDriftB = 0.45160394f; // SURF_CREST_SEED_DRIFT_B (2pi*92/1280)
        const float SurfFaceFraction = 0.10f;
        const float SurfBackFraction = 0.24f;
        const float SurfSetWaves = 5.0f;
        const float SurfEdgeBlendStart = 0.35f; // SURF_EDGE_BLEND_START (cell-edge amp cross-fade)
        const float SurfNearFade = 0.55f;
        const float SurfSechArgMax = 20.0f;
        const float SurfSlopeEpsilon = 0.5f;
        // SURF-PHYS breaker physics (Iribarren / Weggel / Dally-Dean-Dalrymple - see the HLSL
        // constants block for the science + the documented approximations).
        const float SurfXiSpillEndLo = 0.45f;
        const float SurfXiSpillEndHi = 0.60f;
        const float SurfXiSurgeStartLo = 2.8f;
        const float SurfXiSurgeStartHi = 3.6f;
        // internal: WaterVolume.Settings.BedDepth.cs aliases this instead of re-authoring 1.56.
        internal const float SurfDeepwaterLengthCoef = 1.56f;
        const float SurfXiHeightEpsilon = 1e-3f;
        const float SurfGammaBase = 0.6f;
        const float SurfGammaSlopeGain = 5.0f;
        const float SurfGammaMax = 1.1f;
        const float SurfBoreStableGamma = 0.40f;
        const float SurfPlungeFaceSharpen = 0.6f;
        // Knob floors (SURF_MIN_PERIOD / SURF_MIN_WAVELENGTH): both sides clamp the artist-typed
        // period/wavelength through the SAME named floor, so the mirror can never disagree on a
        // degenerate knob.
        internal const float SurfMinPeriod = 0.5f;
        internal const float SurfMinWavelength = 1.0f;
        // internal: WaterVolume.SurfaceHeightEnvelope (the CPU mirror of the shader's
        // SurfaceHeightBand) rebuilds the surf-crest reach from these two, so the fog's CPU arm
        // ceiling and the shader's march band derive from the SAME constants.
        internal const float SurfMinGreens = 1.0f; // SURF_MIN_GREENS
        // Set-amplitude shaping (SURF_SETAMP_*). The jitter MAX doubles as a cross-shader
        // contract: WaterWaterline.hlsl's SurfaceHeightBand re-points at the HLSL name
        // (SURF_SETAMP_JITTER_MAX), so keep this pair's naming stable.
        const float SurfSetAmpHashPhase = 2.4f;
        const float SurfSetAmpFloor = 0.35f;
        const float SurfSetAmpJitterMin = 0.9f;
        internal const float SurfSetAmpJitterMax = 1.1f;
        // Compression reach in front spacings (SURF_WARP_REACH_SPACINGS) - also the factor inside
        // the published _ShoreWarpReach, so WarpExtra below rebuilds the same reach the shader
        // reads back from the global.
        internal const float SurfWarpReachSpacings = 2.0f;
        // Crest-segmentation noise shaping (SURF_CREST_*).
        const float SurfCrestMinLength = 4.0f;
        const float SurfCrestSeedFreshScale = 37.0f;
        const float SurfCrestFreshOctaveRatio = 1.3f;
        const float SurfCrestDirAZ = 0.31f;
        const float SurfCrestDirBX = -0.42f;
        const float SurfCrestFreqRatio = 1.7f;
        const float SurfCrestOctaveBWeight = 0.5f;
        const float SurfCrestNoiseNorm = 1.5f;
        // Shore-exposure gate window (SURF_EXPOSURE_FACING_*).
        const float SurfExposureFacingLo = -0.25f;
        const float SurfExposureFacingHi = 0.5f;
        // Iribarren deep-length floor (SURF_XI_LENGTH_EPSILON).
        const float SurfXiLengthEpsilon = 1e-3f;
        // Front lifecycle shaping (SURF_GREEN_EXPONENT .. SURF_BORE_WIDTH_FACTOR). The bore width
        // factor is the constant whose unnamed twin once drifted to 2.0f here while the shader
        // used 1.4 - exactly the drift class these pairs now turn into a console error.
        const float SurfGreenExponent = 0.25f;
        const float SurfCapEpsilon = 1e-3f;
        const float SurfCrestingStart = 0.75f;
        const float SurfCrestingEnd = 1.05f;
        const float SurfBrokenStart = 1.05f;
        const float SurfBrokenEnd = 1.5f;
        const float SurfLeanReachFraction = 0.25f;
        const float SurfBoreWidthFactor = 1.4f;
        // Field-mask shaping (SURF_MIN_INFLUENCE / SURF_MIN_BAND_DEPTH / SURF_WET_FADE_*).
        const float SurfMinInfluence = 0.001f;
        const float SurfMinBandDepth = 0.25f;
        const float SurfWetFadeLo = -0.05f;
        const float SurfWetFadeHi = 0.1f;

        // Shore-transform constants - MUST match the SHORE_* defines in WaterShore.hlsl (guarded
        // by WaterWaveConstantsValidator): ShoalWeight/GreenGain/WarpExtra below run the same
        // math the shader's ShoalWeight/ShoreGreenGain/ShoreWarpExtra run.
        const float ShoreShoalWavelengthFactor = 2.0f;
        const float ShoreWavelengthEpsilon = 1e-3f;
        const float ShoreBandEpsilon = 1e-3f;
        const float ShoreBandInnerFraction = 0.35f;
        const float ShoreGreenMinDepth = 0.05f;
        const float ShoreGreenExponent = 0.25f;
        const float ShoreWarpReachMin = 1.0f;
        const float ShoreMinGreens = 1.0f;

        // Matches LBW_INVERSION_ITERATIONS in WaterLargeWaves.hlsl (Crest's SampleInvertedDisplacement
        // uses 4). Inverting the horizontal Gerstner displacement is how a fixed world xz maps back to
        // the wave's SOURCE point, so buoyancy samples the height under the crest the eye sees.
        const int InversionIterations = 4;

        // Matches LbwHash() / SurfHash() in the shaders. The sine-hash pair and the phase-stream
        // offset are validator-guarded (WaterWaveConstantsValidator): the CPU buoyancy mirror
        // desyncs from the rendered waves SILENTLY if either side drifts - this file's own history
        // records an unnamed twin of the stream offset once drifting to 2.0f.
        internal const float HashSineFrequency = 12.9898f;    // KEEP: LBW_/SURF_HASH_SINE_FREQ
        internal const float HashSineScale = 43758.5453f;     // KEEP: LBW_/SURF_HASH_SINE_SCALE
        // Decorrelates the phase hash stream from the heading hash stream fed the same wave index.
        internal const float PhaseHashStreamOffset = 16f;     // KEEP: LBW_PHASE_HASH_STREAM_OFFSET
        static float Hash(float n) => Fract(Mathf.Sin(n * HashSineFrequency) * HashSineScale);
        static float Fract(float x) => x - Mathf.Floor(x);

        // HLSL-semantics smoothstep (edge0, edge1, x) - Unity's Mathf.SmoothStep argument order is
        // different, so the mirror carries its own to stay byte-for-byte with the shader.
        static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-9f));
            return t * t * (3f - 2f * t);
        }

        // One shore-field sample, mirroring WaterShore.hlsl's ShoreData (deep sentinel off-field).
        struct ShoreSampleCpu
        {
            public float Depth;
            public float SdfDist;
            public float DirX, DirZ; // toward shore, unit
            public float SlopeTan;   // local beach slope tan(beta) (SURF-PHYS)
            public float Influence;
        }

        static ShoreSampleCpu SampleShore(in ShoreWaveContext ctx, float x, float z)
        {
            ShoreSampleCpu s;
            s.Depth = float.MaxValue;
            s.SdfDist = 0f;
            s.DirX = 0f;
            s.DirZ = 0f;
            s.SlopeTan = 0f;
            s.Influence = 0f;
            if (ctx.Field == null) return s;
            ctx.Field.TrySampleShore(x, z, out s.Depth, out s.SdfDist, out s.DirX, out s.DirZ,
                                     out s.SlopeTan, out s.Influence);
            return s;
        }

        // Mirrors ShoalWeight() in WaterShore.hlsl.
        static float ShoalWeight(in ShoreWaveContext ctx, float depth, float wavelength)
        {
            float clamped = Mathf.Max(depth, 0f);
            float raw = Mathf.Clamp01(ShoreShoalWavelengthFactor * clamped
                                      / Mathf.Max(wavelength, ShoreWavelengthEpsilon));
            float band = Mathf.Max(ctx.ShoalDepth, ShoreBandEpsilon);
            float deep = SmoothStep(ShoreBandInnerFraction * band, band, clamped);
            return Mathf.Lerp(raw, 1f, deep);
        }

        // Mirrors ShoreGreenGain() in WaterShore.hlsl.
        static float GreenGain(in ShoreWaveContext ctx, in ShoreSampleCpu shore)
        {
            // The AUTHORED band, not the sea-state-floored one - see _ShoreGreenBandDepth in
            // WaterShoreMath.hlsl for why amplification and attenuation stopped sharing a number.
            float band = Mathf.Max(ctx.GreenBandDepth, ShoreBandEpsilon);
            if (shore.Influence <= 0f || shore.Depth >= band) return 1f;
            float d = Mathf.Max(shore.Depth, ShoreGreenMinDepth);
            float green = Mathf.Min(Mathf.Pow(band / d, ShoreGreenExponent),
                                    Mathf.Max(ctx.Greens, ShoreMinGreens));
            green = Mathf.Lerp(green, 1f,
                               Mathf.Clamp01(1f - shore.Depth / (ShoreBandInnerFraction * band)));
            return Mathf.Lerp(1f, green, shore.Influence);
        }

        // Mirrors ShoreWarpExtra() in WaterShore.hlsl - ONE compression curve with the surf
        // fronts: reach = 2 x front spacing, the same value published as _ShoreWarpReach.
        static float WarpExtra(in ShoreWaveContext ctx, in ShoreSampleCpu shore)
        {
            if (shore.Influence <= 0f || ctx.Compression <= 0f) return 0f;
            float s = Mathf.Max(shore.SdfDist, 0f);
            // Rebuilds the published _ShoreWarpReach (reach spacings x effective spacing), then
            // applies the shader's SHORE_WARP_REACH_MIN floor on the result.
            float reach = Mathf.Max(SurfWarpReachSpacings
                                    * Mathf.Max(ctx.SurfWavelength, SurfMinWavelength),
                                    ShoreWarpReachMin);
            return ctx.Compression * s * Mathf.Exp(-s / reach) * shore.Influence;
        }

        // --- Surf breaker fronts (mirrors WaterSurfWaves.hlsl) --------------------------------

        // Mirrors SurfWrapIndex() in WaterSurfWaves.hlsl: every per-front quantity derives from
        // the WRAPPED index so hash arguments stay small (float32-sin-safe) and the field is
        // exactly periodic in the master beat wrap.
        static float SurfWrapIndex(float frontIndex)
            => frontIndex - SurfBeatWrapFronts * Mathf.Floor(frontIndex / SurfBeatWrapFronts);

        static float SurfSetAmp(in ShoreWaveContext ctx, float frontIndex)
        {
            float wrapped = SurfWrapIndex(frontIndex);
            float h = Hash(wrapped);
            float setWave = 0.5f + 0.5f * Mathf.Sin((wrapped / SurfSetWaves) * TwoPi
                                                    + h * SurfSetAmpHashPhase);
            return Mathf.Lerp(1f, Mathf.Lerp(SurfSetAmpFloor, 1f, setWave), ctx.SurfSetStrength)
                 * Mathf.Lerp(SurfSetAmpJitterMin, SurfSetAmpJitterMax, h);
        }

        static float SurfWarpDistance(in ShoreWaveContext ctx, float s)
        {
            float reach = SurfWarpReachSpacings * Mathf.Max(ctx.SurfWavelength, SurfMinWavelength);
            return s * (1f + ctx.Compression * Mathf.Exp(-Mathf.Max(s, 0f) / reach));
        }

        // Mirrors SurfCrestFactor() in WaterSurfWaves.hlsl (alongshore crest segmentation).
        static float SurfCrestFactor(in ShoreWaveContext ctx, float x, float z, float frontIndex)
        {
            if (ctx.SurfCrestVariation <= 0f) return 1f;
            float invLen = 1f / Mathf.Max(ctx.SurfCrestLength, SurfCrestMinLength);
            float wrapped = SurfWrapIndex(frontIndex);
            float persistence = Mathf.Clamp01(ctx.SurfCrestPersistence);
            float seedFresh = Hash(wrapped) * SurfCrestSeedFreshScale;
            float seedA = Mathf.Lerp(seedFresh, wrapped * SurfCrestSeedDriftA, persistence);
            float seedB = Mathf.Lerp(seedFresh * SurfCrestFreshOctaveRatio,
                                     wrapped * SurfCrestSeedDriftB, persistence);
            float n = Mathf.Sin((x * 1f + z * SurfCrestDirAZ) * (TwoPi * invLen) + seedA)
                    + SurfCrestOctaveBWeight
                    * Mathf.Sin((x * SurfCrestDirBX + z * 1f) * (TwoPi * invLen * SurfCrestFreqRatio)
                                + seedB);
            float n01 = Mathf.Clamp01(n / SurfCrestNoiseNorm * 0.5f + 0.5f);
            return 1f - ctx.SurfCrestVariation * (1f - n01);
        }

        // Mirrors SurfExposure() in WaterSurfWaves.hlsl (surf gated by shore facing the swell).
        static float SurfExposure(in ShoreWaveContext ctx, float dirX, float dirZ)
        {
            float facing = SmoothStep(SurfExposureFacingLo, SurfExposureFacingHi,
                                      ctx.SurfWindDirX * dirX + ctx.SurfWindDirZ * dirZ);
            return Mathf.Lerp(1f, facing, Mathf.Clamp01(ctx.SurfDirectionality));
        }

        // Mirrors SurfIribarren() in WaterSurfWaves.hlsl (surf-similarity number, Battjes 1974).
        static float SurfIribarren(in ShoreWaveContext ctx, float tanBeta, float deepHeight)
        {
            float period = Mathf.Max(ctx.SurfPeriod, SurfMinPeriod);
            float deepLength = SurfDeepwaterLengthCoef * period * period;
            return tanBeta / Mathf.Sqrt(Mathf.Max(deepHeight, SurfXiHeightEpsilon)
                                        / Mathf.Max(deepLength, SurfXiLengthEpsilon));
        }

        // Mirrors SurfBreakerWeights().z in WaterSurfWaves.hlsl (kills the bore hand-over).
        static float SurfSurgeWeight(float xi)
            => SmoothStep(SurfXiSurgeStartLo, SurfXiSurgeStartHi, xi);

        // Mirrors SurfBreakerWeights().y in WaterSurfWaves.hlsl: plunging drives the face
        // steepening, which moves the surface (spilling stays foam-only, no CPU side).
        static float SurfPlungeWeight(float xi)
            => SmoothStep(SurfXiSpillEndLo, SurfXiSpillEndHi, xi) * (1f - SurfSurgeWeight(xi));

        // Mirrors SurfGamma() in WaterSurfWaves.hlsl (Weggel-simplified breaker index).
        static float SurfGamma(float tanBeta)
            => Mathf.Clamp(SurfGammaBase + SurfGammaSlopeGain * tanBeta, SurfGammaBase, SurfGammaMax);

        // Mirrors SurfFrontHeight() in WaterSurfWaves.hlsl (height only - buoyancy needs no foam).
        static float SurfFrontHeight(in ShoreWaveContext ctx, float x, float z,
                                     float sWarp, float depth, float tanBeta, float time)
        {
            float wavelength = Mathf.Max(ctx.SurfWavelength, SurfMinWavelength);
            float period = Mathf.Max(ctx.SurfPeriod, SurfMinPeriod);
            float phase = sWarp / wavelength + time / period;
            float frontIndex = Mathf.Floor(phase);
            float f = phase - frontIndex;

            // Cell-edge amplitude cross-fade - keep lockstep with SurfComputeFrontTerms (C0
            // continuity at f = 0/1 so the per-front hash never steps the height mid-cell).
            float ampThis = SurfSetAmp(ctx, frontIndex) * SurfCrestFactor(ctx, x, z, frontIndex);
            float halfCell = f - 0.5f;
            float neighborIndex = frontIndex + (halfCell > 0f ? 1f : -1f);
            float ampNeighbor = SurfSetAmp(ctx, neighborIndex)
                              * SurfCrestFactor(ctx, x, z, neighborIndex);
            float edgeBlend = 0.5f * SmoothStep(SurfEdgeBlendStart, 0.5f, Mathf.Abs(halfCell));
            float setAmp = Mathf.Lerp(ampThis, ampNeighbor, edgeBlend);
            float d = Mathf.Max(depth, SurfMinDepth);

            float deepHeight = ctx.SurfAmplitude * setAmp;
            float xi = SurfIribarren(ctx, tanBeta, deepHeight);
            float surge = SurfSurgeWeight(xi);
            float plunge = SurfPlungeWeight(xi);

            float green = Mathf.Min(
                Mathf.Pow(Mathf.Max(ctx.SurfBandDepth, d) / d, SurfGreenExponent),
                Mathf.Max(ctx.Greens, SurfMinGreens));
            float height0 = ctx.SurfAmplitude * setAmp * green;
            float capH = SurfGamma(tanBeta) * d;
            float overCap = height0 / Mathf.Max(capH, SurfCapEpsilon);
            float cresting = SmoothStep(SurfCrestingStart, SurfCrestingEnd, overCap);
            float broken = SmoothStep(SurfBrokenStart, SurfBrokenEnd, overCap) * (1f - surge);
            float amp = Mathf.Min(height0, capH);

            float dAcross = (f - 0.5f) * wavelength;
            float lean = ctx.SurfLean * amp * cresting;
            dAcross += lean * Mathf.Exp(-Mathf.Abs(dAcross) / (SurfLeanReachFraction * wavelength));
            float faceLen = SurfFaceFraction * wavelength;
            float backLen = SurfBackFraction * wavelength;
            // Plunging face steepening - keep lockstep with SurfComputeFrontTerms in the HLSL.
            float faceSharpen = Mathf.Lerp(1f, SurfPlungeFaceSharpen, plunge * cresting);
            float profLen = dAcross < 0f ? faceLen * faceSharpen : backLen;
            float sech = 1f / Cosh(Mathf.Min(Mathf.Abs(dAcross) / profLen, SurfSechArgMax));
            float profile = sech * sech;
            // Bore sech width = backLen * SurfBoreWidthFactor. This factor's unnamed twin once
            // drifted (shader 1.4, mirror 2.0); it is now a named, validator-guarded pair, so any
            // future drift is a console error instead of a silent buoyancy desync. Bore amplitude
            // relaxes onto the Dally-Dean-Dalrymple stable height.
            float boreSech = 1f / Cosh(Mathf.Min(Mathf.Abs(dAcross) / (backLen * SurfBoreWidthFactor),
                                                 SurfSechArgMax));
            float boreAmp = Mathf.Lerp(amp, SurfBoreStableGamma * d, broken);
            return Mathf.Lerp(amp * profile, boreAmp * boreSech, broken);
        }

        static float Cosh(float x)
        {
            float e = Mathf.Exp(x);
            return 0.5f * (e + 1f / e);
        }

        // Mirrors EvaluateSurfWaves() height/slope/mask (foam terms omitted - physics only).
        static void EvaluateSurf(in ShoreWaveContext ctx, in ShoreSampleCpu shore,
                                 float x, float z, float time,
                                 out float height, out float slopeX, out float slopeZ, out float mask)
        {
            height = 0f;
            slopeX = 0f;
            slopeZ = 0f;
            mask = 0f;
            if (!ctx.SurfActive || shore.Influence <= SurfMinInfluence) return;

            float band = Mathf.Max(ctx.SurfBandDepth, SurfMinBandDepth);
            float develop = 1f - SmoothStep(SurfNearFade * band, band, Mathf.Max(shore.Depth, 0f));
            float wet = SmoothStep(SurfWetFadeLo, SurfWetFadeHi, shore.Depth); // SURF_WET_FADE_*
            float exposure = SurfExposure(ctx, shore.DirX, shore.DirZ);
            mask = develop * wet * shore.Influence * exposure;
            if (mask <= SurfMinInfluence) { mask = 0f; return; }

            float s = Mathf.Max(shore.SdfDist, 0f);
            float h0 = SurfFrontHeight(ctx, x, z, SurfWarpDistance(ctx, s), shore.Depth,
                                       shore.SlopeTan, time);
            float h1 = SurfFrontHeight(ctx, x, z, SurfWarpDistance(ctx, s + SurfSlopeEpsilon),
                                       shore.Depth, shore.SlopeTan, time);
            float dhds = (h1 - h0) / SurfSlopeEpsilon;

            height = h0 * mask;
            slopeX = -shore.DirX * (dhds * mask);
            slopeZ = -shore.DirZ * (dhds * mask);
        }

        // Mirrors SurfAmbientWeight() in WaterSurfWaves.hlsl.
        static float SurfAmbientWeight(in ShoreWaveContext ctx, float surfMask)
            => 1f - surfMask * Mathf.Clamp01(ctx.SurfAmbientFade);

        /// <summary>Mirror of the shader's FFT-branch shore treatment (LargeBodyWaveHeight, FFT
        /// path): the per-cascade shoal attenuation collapses on the CPU to one weight at the
        /// dominant swell wavelength, the ambient fade under the fronts, and the surf-front
        /// height/slope on top. Applied to the FFT height-field readback sample so floaters near
        /// shore keep matching the rendered surface. Identity when no shore field is live.
        /// <paramref name="verticalRate"/> is the rate MEASURED on the FFT field
        /// (WaterOceanFft.TrySampleField); it is composed exactly like the height it differentiates -
        /// same shoal/ambient weight, plus the fronts' own rate - so it stays d(height)/dt of the
        /// composed surface rather than of the raw cascade.</summary>
        internal static Vector3 ApplyShoreToFftSample(Vector3 fft, float worldX, float worldZ,
            float time, float dominantWavelength, in ShoreWaveContext ctx, ref float verticalRate)
        {
            if (ctx.Field == null) return fft;
            ShoreSampleCpu shore = SampleShore(ctx, worldX, worldZ);
            if (shore.Influence <= 0f) return fft;
            // Surf terms run on the master beat (ctx.SurfBeatTime), never the ambient clock.
            EvaluateSurf(ctx, shore, worldX, worldZ, ctx.SurfBeatTime, out float surfHeight,
                         out float surfSlopeX, out float surfSlopeZ, out float surfMask);
            float weight = Mathf.Lerp(1f, ShoalWeight(ctx, shore.Depth, dominantWavelength),
                                      shore.Influence)
                         * SurfAmbientWeight(ctx, surfMask);
            fft.x = fft.x * weight + surfHeight;
            fft.y = fft.y * weight + surfSlopeX;
            fft.z = fft.z * weight + surfSlopeZ;
            verticalRate = verticalRate * weight
                         + SurfFrontVerticalRate(ctx, shore, worldX, worldZ, surfMask, surfHeight);
            return fft;
        }

        // Height + slope + horizontal displacement accumulated across the wave components. Mirrors the
        // shader's LargeBodyWaveField, minus the Jacobian derivatives (buoyancy needs no normals).
        struct BandAccum
        {
            public float Height;
            public float HeightVelocity; // d(Height)/dt (m/s); physics-only, no shader counterpart
            public float SlopeX;
            public float SlopeZ;
            public float DisplacementX;
            public float DisplacementZ;
        }

        // Sum one band of directional Gerstner components. Mirrors LbwAccumulateBand() in the shader
        // (shore transform included: per-component shoal, refraction, phase compression).
        static void AccumulateBand(ref BandAccum a, float x, float z, float time, int count,
            float baseWavelength, float wavelengthFalloff, float baseAmplitude, float amplitudeFalloff,
            float dirSpread, float phaseSeed, float amplitudeScale, float windHeadingRadians,
            in ShoreWaveContext ctx, in ShoreSampleCpu shore, float warpExtra)
        {
            float wavelength = baseWavelength;
            float amplitude = baseAmplitude;

            for (int n = 0; n < count; n++)
            {
                float fn = n;
                float headingJitter = (Hash(fn + phaseSeed) * 2f - 1f) * dirSpread;
                float heading = windHeadingRadians + headingJitter;
                float directionX = Mathf.Cos(heading);
                float directionZ = Mathf.Sin(heading);
                float phaseOffset = Hash(fn + phaseSeed + PhaseHashStreamOffset) * TwoPi;

                // Shore transform (mirrors the shader): shoaling response drives refraction toward
                // shore and the phase-compression share of this component. Same ramp constants as
                // ShoalWeight (the WaterLargeWaves.hlsl twin still carries them inline).
                float shoalRaw = Mathf.Clamp01(ShoreShoalWavelengthFactor
                                               * Mathf.Max(shore.Depth, 0f)
                                               / Mathf.Max(wavelength, ShoreWavelengthEpsilon));
                float feel = (1f - shoalRaw) * shore.Influence;
                if (feel > 0f && ctx.Refraction > 0f)
                {
                    float t = ctx.Refraction * feel;
                    float bentX = Mathf.Lerp(directionX, shore.DirX, t);
                    float bentZ = Mathf.Lerp(directionZ, shore.DirZ, t);
                    float bentLen = Mathf.Sqrt(bentX * bentX + bentZ * bentZ);
                    if (bentLen > 1e-4f) { directionX = bentX / bentLen; directionZ = bentZ / bentLen; }
                }

                float wavenumber = TwoPi / Mathf.Max(wavelength, 1e-3f);
                float angularSpeed = Mathf.Sqrt(Gravity * wavenumber);
                float phase = (directionX * x + directionZ * z) * wavenumber - angularSpeed * time
                            + phaseOffset + wavenumber * warpExtra * feel;
                float sinP = Mathf.Sin(phase);
                float cosP = Mathf.Cos(phase);
                float fetchWeight = ctx.FetchField != null
                    ? ctx.FetchField.Weight(x, z, wavelength)
                    : 1f;
                float amp = amplitudeScale * amplitude * ShoalWeight(ctx, shore.Depth, wavelength)
                          * fetchWeight;

                a.Height += amp * sinP;
                a.HeightVelocity += amp * -angularSpeed * cosP; // d/dt sin(phase) = -angularSpeed*cos(phase)
                float slopeMagnitude = amp * wavenumber * cosP;
                a.SlopeX += slopeMagnitude * directionX;
                a.SlopeZ += slopeMagnitude * directionZ;
                a.DisplacementX += amp * directionX * cosP;
                a.DisplacementZ += amp * directionZ * cosP;

                wavelength *= wavelengthFalloff;
                amplitude *= amplitudeFalloff;
            }
        }

        static BandAccum EvaluateBands(float x, float z, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight,
            in ShoreWaveContext ctx)
        {
            ShoreSampleCpu shore = SampleShore(ctx, x, z);
            // Surf terms run on the master beat (ctx.SurfBeatTime); the ambient bands keep the
            // body's unwrapped wave clock (their omega*t phases are not beat-periodic).
            EvaluateSurf(ctx, shore, x, z, ctx.SurfBeatTime, out float surfHeight,
                         out float surfSlopeX, out float surfSlopeZ, out float surfMask);
            float green = GreenGain(ctx, shore);
            float ambient = SurfAmbientWeight(ctx, surfMask);
            float bandScale = green * ambient;
            float warpExtra = WarpExtra(ctx, shore);

            BandAccum a = default;
            AccumulateBand(ref a, x, z, time, WaveCount, BaseWavelength, WavelengthFalloff,
                BaseAmplitude, AmplitudeFalloff, DirectionSpread, ChopPhaseSeed,
                amplitudeScale * bandScale, windHeadingRadians, ctx, shore, warpExtra);
            AccumulateBand(ref a, x, z, time, SwellCount, swellWavelength, SwellWavelengthFalloff,
                SwellBaseAmplitude, SwellAmplitudeFalloff, SwellDirectionSpread, SwellPhaseSeed,
                swellHeight * bandScale, swellHeadingRadians, ctx, shore, warpExtra);

            // Surf fronts ride on top (mirrors EvaluateLargeBodyWaveShore). Their vertical velocity
            // is a finite difference - physics-only, no shader counterpart to stay lockstep with.
            if (surfMask > 0f)
            {
                a.Height += surfHeight;
                a.SlopeX += surfSlopeX;
                a.SlopeZ += surfSlopeZ;
                a.HeightVelocity += SurfFrontVerticalRate(ctx, shore, x, z, surfMask, surfHeight);
            }
            return a;
        }

        // A surf front's own d(height)/dt, by finite difference on the master beat. Physics-only, so
        // there is no shader twin to stay lockstep with. Shared by the analytic band sum above and by
        // ApplyShoreToFftSample: both compose the fronts onto a surface the same way, so they must
        // compose the fronts' MOTION the same way too.
        // surfHeight is the already-masked height at ctx.SurfBeatTime, so the forward sample is masked
        // identically - differencing a masked against an unmasked height would read the mask ramp as
        // surface velocity.
        static float SurfFrontVerticalRate(in ShoreWaveContext ctx, in ShoreSampleCpu shore,
                                           float x, float z, float surfMask, float surfHeight)
        {
            if (surfMask <= 0f) return 0f;
            float s = Mathf.Max(shore.SdfDist, 0f);
            float heightNext = SurfFrontHeight(ctx, x, z, SurfWarpDistance(ctx, s), shore.Depth,
                                               shore.SlopeTan,
                                               ctx.SurfBeatTime + SurfVelocityDeltaTime) * surfMask;
            return (heightNext - surfHeight) / SurfVelocityDeltaTime;
        }

        // Finite-difference step for the surf-front rate: one 60 Hz frame, short enough that a front
        // moves a small fraction of its own profile width within it.
        const float SurfVelocityDeltaTime = 1f / 60f;

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at world (x, z). Mirrors
        /// EvaluateLargeBodyWaveShore() in WaterLargeWaves.hlsl. <paramref name="time"/> is the body's
        /// WaveTime. Height drives buoyancy depth; the slope drives wave-carried drift.
        /// </summary>
        internal static Vector3 Evaluate(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight, in ShoreWaveContext ctx)
        {
            BandAccum a = EvaluateBands(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                        swellHeadingRadians, swellWavelength, swellHeight, ctx);
            return new Vector3(a.Height, a.SlopeX, a.SlopeZ);
        }

        /// <summary>
        /// Horizontal Gerstner offset (metres) at a SOURCE (x, z), choppiness baked in. Mirrors the
        /// .disp term of EvaluateLargeBodyWaveShore() in WaterLargeWaves.hlsl. Zero when <paramref name="choppiness"/>
        /// is 0, so the field collapses to the pure vertical swell.
        /// </summary>
        /// <summary>Internal-visible alias of <see cref="Displacement"/> for WaterVolume's chop
        /// inversion of the ANALYTIC field (wake injection lands on the displaced surface).
        /// Same fixed-point use as InvertToSource; exposed rather than duplicated so the analytic
        /// and FFT inversion loops can share one caller-side implementation with edge weighting.</summary>
        internal static Vector2 HorizontalDisplacementAtSource(float sourceX, float sourceZ, float time,
            float amplitudeScale, float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight,
            float choppiness, in ShoreWaveContext ctx)
            => Displacement(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians,
                            swellHeadingRadians, swellWavelength, swellHeight, choppiness, ctx);

        static Vector2 Displacement(float sourceX, float sourceZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            BandAccum a = EvaluateBands(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians,
                                        swellHeadingRadians, swellWavelength, swellHeight, ctx);
            return new Vector2(a.DisplacementX * choppiness, a.DisplacementZ * choppiness);
        }

        /// <summary>
        /// Invert the horizontal displacement: find the SOURCE (x, z) that Gerstner chop displaces
        /// onto the query world (x, z). Fixed-point iteration mirroring Crest's SampleInvertedDisplacement.
        /// With choppiness 0 the displacement is zero, so this returns the query point on the first pass.
        /// </summary>
        static Vector2 InvertToSource(float queryX, float queryZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            float sourceX = queryX;
            float sourceZ = queryZ;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector2 displacement = Displacement(sourceX, sourceZ, time, amplitudeScale,
                    windHeadingRadians, swellHeadingRadians, swellWavelength, swellHeight, choppiness, ctx);
                sourceX -= (sourceX + displacement.x) - queryX;
                sourceZ -= (sourceZ + displacement.y) - queryZ;
            }
            return new Vector2(sourceX, sourceZ);
        }

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at a QUERY world (x, z), accounting for
        /// horizontal chop by first inverting to the source point. This is what buoyancy needs: the
        /// surface value directly above a fixed world position. Matches the rendered (displaced) crest.
        /// </summary>
        internal static Vector3 EvaluateAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                            swellHeadingRadians, swellWavelength, swellHeight, choppiness, ctx);
            return Evaluate(source.x, source.y, time, amplitudeScale, windHeadingRadians,
                            swellHeadingRadians, swellWavelength, swellHeight, ctx);
        }

        /// <summary>
        /// Height+slope AND vertical velocity at a QUERY world (x, z) from ONE chop inversion.
        /// </summary>
        /// <remarks>
        /// Height and velocity are returned TOGETHER because a caller wanting both once took them from
        /// two separate entry points with byte-identical arguments, and EACH ran InvertToSource - four
        /// EvaluateBands passes - before its own final pass: 10 passes per point where 5 suffice.
        /// EvaluateBands is 16 Gerstner components plus the surf-front cosh chain, and buoyancy asks for
        /// HeightNormalVelocity at every probe of every floater, so the waste scaled with the scene (the
        /// shipped stress spawner's 8x8 grid x 8 probes burned ~2,500 redundant passes per FixedUpdate).
        /// BandAccum already carried HeightVelocity beside Height/Slope, so both answers fall out of the
        /// single pass that was always being done. This is now the ONLY chop-inverted query: the FFT
        /// branch measures its own rate on the readback (WaterOceanFft.TrySampleField) instead of
        /// borrowing this mirror's, so the velocity-only entry point it used has been removed.
        /// </remarks>
        internal static void EvaluateAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx, out Vector3 heightSlope, out float verticalVelocity)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                            swellHeadingRadians, swellWavelength, swellHeight, choppiness, ctx);
            BandAccum a = EvaluateBands(source.x, source.y, time, amplitudeScale, windHeadingRadians,
                                        swellHeadingRadians, swellWavelength, swellHeight, ctx);
            heightSlope = new Vector3(a.Height, a.SlopeX, a.SlopeZ);
            verticalVelocity = a.HeightVelocity;
        }
    }
}
