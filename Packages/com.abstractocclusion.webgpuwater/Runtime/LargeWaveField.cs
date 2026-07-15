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
        public float ShoalDepth;           // _ShoreShoalDepth
        public float Refraction;           // _ShoreRefraction
        public float Compression;          // _ShoreCompression / _SurfCompression (one knob)
        public float Greens;               // _ShoreGreens / _SurfGreens (one knob)
        public bool SurfActive;            // _SurfActive
        public float SurfAmplitude;        // _SurfAmplitude
        public float SurfWavelength;       // _SurfWavelength
        public float SurfPeriod;           // _SurfPeriod
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
        const float SurfCrestSeedDrift = 0.35f;
        const float SurfFaceFraction = 0.10f;
        const float SurfBackFraction = 0.24f;
        const float SurfSetWaves = 5.0f;
        const float SurfNearFade = 0.55f;
        const float SurfSechArgMax = 20.0f;
        const float SurfSlopeEpsilon = 0.5f;
        // SURF-PHYS breaker physics (Iribarren / Weggel / Dally-Dean-Dalrymple - see the HLSL
        // constants block for the science + the documented approximations).
        const float SurfXiSpillEndLo = 0.45f;
        const float SurfXiSpillEndHi = 0.60f;
        const float SurfXiSurgeStartLo = 2.8f;
        const float SurfXiSurgeStartHi = 3.6f;
        const float SurfDeepwaterLengthCoef = 1.56f;
        const float SurfXiHeightEpsilon = 1e-3f;
        const float SurfGammaBase = 0.6f;
        const float SurfGammaSlopeGain = 5.0f;
        const float SurfGammaMax = 1.1f;
        const float SurfBoreStableGamma = 0.40f;
        const float SurfPlungeFaceSharpen = 0.6f;

        // Matches LBW_INVERSION_ITERATIONS in WaterLargeWaves.hlsl (Crest's SampleInvertedDisplacement
        // uses 4). Inverting the horizontal Gerstner displacement is how a fixed world xz maps back to
        // the wave's SOURCE point, so buoyancy samples the height under the crest the eye sees.
        const int InversionIterations = 4;

        // Matches LbwHash() / SurfHash() in the shaders: frac(sin(n * 12.9898) * 43758.5453).
        static float Hash(float n) => Fract(Mathf.Sin(n * 12.9898f) * 43758.5453f);
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
            float raw = Mathf.Clamp01(2f * clamped / Mathf.Max(wavelength, 1e-3f));
            float band = Mathf.Max(ctx.ShoalDepth, 1e-3f);
            float deep = SmoothStep(0.35f * band, band, clamped);
            return Mathf.Lerp(raw, 1f, deep);
        }

        // Mirrors ShoreGreenGain() in WaterShore.hlsl.
        static float GreenGain(in ShoreWaveContext ctx, in ShoreSampleCpu shore)
        {
            float band = Mathf.Max(ctx.ShoalDepth, 1e-3f);
            if (shore.Influence <= 0f || shore.Depth >= band) return 1f;
            float d = Mathf.Max(shore.Depth, 0.05f);
            float green = Mathf.Min(Mathf.Pow(band / d, 0.25f), Mathf.Max(ctx.Greens, 1f));
            green = Mathf.Lerp(green, 1f, Mathf.Clamp01(1f - shore.Depth / (0.35f * band)));
            return Mathf.Lerp(1f, green, shore.Influence);
        }

        // Mirrors ShoreWarpExtra() in WaterShore.hlsl.
        static float WarpExtra(in ShoreWaveContext ctx, in ShoreSampleCpu shore)
        {
            if (shore.Influence <= 0f || ctx.Compression <= 0f) return 0f;
            float s = Mathf.Max(shore.SdfDist, 0f);
            float reach = Mathf.Max(4f * ctx.ShoalDepth, 8f);
            return ctx.Compression * s * Mathf.Exp(-s / reach) * shore.Influence;
        }

        // --- Surf breaker fronts (mirrors WaterSurfWaves.hlsl) --------------------------------

        static float SurfSetAmp(in ShoreWaveContext ctx, float frontIndex)
        {
            float h = Hash(frontIndex);
            float setWave = 0.5f + 0.5f * Mathf.Sin((frontIndex / SurfSetWaves) * TwoPi + h * 2.4f);
            return Mathf.Lerp(1f, Mathf.Lerp(0.35f, 1f, setWave), ctx.SurfSetStrength)
                 * Mathf.Lerp(0.9f, 1.1f, h);
        }

        static float SurfWarpDistance(in ShoreWaveContext ctx, float s)
        {
            float reach = 2f * Mathf.Max(ctx.SurfWavelength, 1f);
            return s * (1f + ctx.Compression * Mathf.Exp(-Mathf.Max(s, 0f) / reach));
        }

        // Mirrors SurfCrestFactor() in WaterSurfWaves.hlsl (alongshore crest segmentation).
        static float SurfCrestFactor(in ShoreWaveContext ctx, float x, float z, float frontIndex)
        {
            if (ctx.SurfCrestVariation <= 0f) return 1f;
            float invLen = 1f / Mathf.Max(ctx.SurfCrestLength, 4f);
            float seed = Mathf.Lerp(Hash(frontIndex) * 37f, frontIndex * SurfCrestSeedDrift,
                                    Mathf.Clamp01(ctx.SurfCrestPersistence));
            float n = Mathf.Sin((x * 1f + z * 0.31f) * (TwoPi * invLen) + seed)
                    + 0.5f * Mathf.Sin((x * -0.42f + z * 1f) * (TwoPi * invLen * 1.7f) + seed * 1.3f);
            float n01 = Mathf.Clamp01(n / 1.5f * 0.5f + 0.5f);
            return 1f - ctx.SurfCrestVariation * (1f - n01);
        }

        // Mirrors SurfExposure() in WaterSurfWaves.hlsl (surf gated by shore facing the swell).
        static float SurfExposure(in ShoreWaveContext ctx, float dirX, float dirZ)
        {
            float facing = SmoothStep(-0.25f, 0.5f,
                                      ctx.SurfWindDirX * dirX + ctx.SurfWindDirZ * dirZ);
            return Mathf.Lerp(1f, facing, Mathf.Clamp01(ctx.SurfDirectionality));
        }

        // Mirrors SurfIribarren() in WaterSurfWaves.hlsl (surf-similarity number, Battjes 1974).
        static float SurfIribarren(in ShoreWaveContext ctx, float tanBeta, float deepHeight)
        {
            float period = Mathf.Max(ctx.SurfPeriod, 0.5f); // matches the HLSL _SurfPeriod floor
            float deepLength = SurfDeepwaterLengthCoef * period * period;
            return tanBeta / Mathf.Sqrt(Mathf.Max(deepHeight, SurfXiHeightEpsilon)
                                        / Mathf.Max(deepLength, 1e-3f));
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

        /// <summary>Break-criterion ratio H/(gamma*depth) for a MEAN set wave (setAmp = 1) at a
        /// column depth + beach slope: the break LINE is where this crosses 1. Composed from the
        /// same mirrored terms the height math uses, so it can never drift from where the shader
        /// actually breaks. Consumed by WaterSurfCurl's camera-following placement (closed-form
        /// from the CPU shore arrays - no readback).</summary>
        internal static float SurfBreakOverCap(in ShoreWaveContext ctx, float depth, float slopeTan)
        {
            float d = Mathf.Max(depth, SurfMinDepth);
            float green = Mathf.Min(Mathf.Pow(Mathf.Max(ctx.SurfBandDepth, d) / d, 0.25f),
                                    Mathf.Max(ctx.Greens, 1f));
            float capH = SurfGamma(slopeTan) * d;
            return (ctx.SurfAmplitude * green) / Mathf.Max(capH, 1e-3f);
        }

        // Mirrors SurfFrontHeight() in WaterSurfWaves.hlsl (height only - buoyancy needs no foam).
        static float SurfFrontHeight(in ShoreWaveContext ctx, float x, float z,
                                     float sWarp, float depth, float tanBeta, float time)
        {
            float wavelength = Mathf.Max(ctx.SurfWavelength, 1f);
            float period = Mathf.Max(ctx.SurfPeriod, 0.5f);
            float phase = sWarp / wavelength + time / period;
            float frontIndex = Mathf.Floor(phase);
            float f = phase - frontIndex;

            float setAmp = SurfSetAmp(ctx, frontIndex) * SurfCrestFactor(ctx, x, z, frontIndex);
            float d = Mathf.Max(depth, SurfMinDepth);

            float deepHeight = ctx.SurfAmplitude * setAmp;
            float xi = SurfIribarren(ctx, tanBeta, deepHeight);
            float surge = SurfSurgeWeight(xi);
            float plunge = SurfPlungeWeight(xi);

            float green = Mathf.Min(Mathf.Pow(Mathf.Max(ctx.SurfBandDepth, d) / d, 0.25f),
                                    Mathf.Max(ctx.Greens, 1f));
            float height0 = ctx.SurfAmplitude * setAmp * green;
            float capH = SurfGamma(tanBeta) * d;
            float overCap = height0 / Mathf.Max(capH, 1e-3f);
            float cresting = SmoothStep(0.75f, 1.05f, overCap);
            float broken = SmoothStep(1.05f, 1.5f, overCap) * (1f - surge);
            float amp = Mathf.Min(height0, capH);

            float dAcross = (f - 0.5f) * wavelength;
            float lean = ctx.SurfLean * amp * cresting;
            dAcross += lean * Mathf.Exp(-Mathf.Abs(dAcross) / (0.25f * wavelength));
            float faceLen = SurfFaceFraction * wavelength;
            float backLen = SurfBackFraction * wavelength;
            // Plunging face steepening - keep lockstep with SurfComputeFrontTerms in the HLSL.
            float faceSharpen = Mathf.Lerp(1f, SurfPlungeFaceSharpen, plunge * cresting);
            float profLen = dAcross < 0f ? faceLen * faceSharpen : backLen;
            float sech = 1f / Cosh(Mathf.Min(Mathf.Abs(dAcross) / profLen, SurfSechArgMax));
            float profile = sech * sech;
            // Parity fix: the bore sech width is backLen * 1.4 in the shader; this mirror had
            // drifted to * 2 (inline literals dodge the constants validator - keep them lockstep
            // by hand). Bore amplitude relaxes onto the Dally-Dean-Dalrymple stable height.
            float boreSech = 1f / Cosh(Mathf.Min(Mathf.Abs(dAcross) / (backLen * 1.4f), SurfSechArgMax));
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
            if (!ctx.SurfActive || shore.Influence <= 0.001f) return;

            float band = Mathf.Max(ctx.SurfBandDepth, 0.25f);
            float develop = 1f - SmoothStep(SurfNearFade * band, band, Mathf.Max(shore.Depth, 0f));
            float wet = SmoothStep(-0.05f, 0.1f, shore.Depth); // keep lockstep with the HLSL wet fade
            float exposure = SurfExposure(ctx, shore.DirX, shore.DirZ);
            mask = develop * wet * shore.Influence * exposure;
            if (mask <= 0.001f) { mask = 0f; return; }

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
        /// shore keep matching the rendered surface. Identity when no shore field is live.</summary>
        internal static Vector3 ApplyShoreToFftSample(Vector3 fft, float worldX, float worldZ,
            float time, float dominantWavelength, in ShoreWaveContext ctx)
        {
            if (ctx.Field == null) return fft;
            ShoreSampleCpu shore = SampleShore(ctx, worldX, worldZ);
            if (shore.Influence <= 0f) return fft;
            EvaluateSurf(ctx, shore, worldX, worldZ, time, out float surfHeight,
                         out float surfSlopeX, out float surfSlopeZ, out float surfMask);
            float weight = Mathf.Lerp(1f, ShoalWeight(ctx, shore.Depth, dominantWavelength),
                                      shore.Influence)
                         * SurfAmbientWeight(ctx, surfMask);
            fft.x = fft.x * weight + surfHeight;
            fft.y = fft.y * weight + surfSlopeX;
            fft.z = fft.z * weight + surfSlopeZ;
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
                float phaseOffset = Hash(fn + phaseSeed + 16f) * TwoPi;

                // Shore transform (mirrors the shader): shoaling response drives refraction toward
                // shore and the phase-compression share of this component.
                float shoalRaw = Mathf.Clamp01(2f * Mathf.Max(shore.Depth, 0f)
                                               / Mathf.Max(wavelength, 1e-3f));
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
                float amp = amplitudeScale * amplitude * ShoalWeight(ctx, shore.Depth, wavelength);

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
            float windHeadingRadians, float swellWavelength, float swellHeight,
            in ShoreWaveContext ctx)
        {
            ShoreSampleCpu shore = SampleShore(ctx, x, z);
            EvaluateSurf(ctx, shore, x, z, time, out float surfHeight,
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
                swellHeight * bandScale, windHeadingRadians, ctx, shore, warpExtra);

            // Surf fronts ride on top (mirrors EvaluateLargeBodyWaveShore). Their vertical velocity
            // is a finite difference - physics-only, no shader counterpart to stay lockstep with.
            if (surfMask > 0f)
            {
                a.Height += surfHeight;
                a.SlopeX += surfSlopeX;
                a.SlopeZ += surfSlopeZ;
                const float velocityDt = 1f / 60f;
                float s = Mathf.Max(shore.SdfDist, 0f);
                float hNext = SurfFrontHeight(ctx, x, z, SurfWarpDistance(ctx, s), shore.Depth,
                                              shore.SlopeTan, time + velocityDt) * surfMask;
                a.HeightVelocity += (hNext - surfHeight) / velocityDt;
            }
            return a;
        }

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at world (x, z). Mirrors
        /// EvaluateLargeBodyWave() in WaterLargeWaves.hlsl. <paramref name="time"/> is the body's
        /// WaveTime. Height drives buoyancy depth; the slope drives wave-carried drift.
        /// </summary>
        internal static Vector3 Evaluate(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, in ShoreWaveContext ctx)
        {
            BandAccum a = EvaluateBands(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                        swellWavelength, swellHeight, ctx);
            return new Vector3(a.Height, a.SlopeX, a.SlopeZ);
        }

        /// <summary>
        /// Horizontal Gerstner offset (metres) at a SOURCE (x, z), choppiness baked in. Mirrors
        /// LargeBodyWaveDisplacement() in WaterLargeWaves.hlsl. Zero when <paramref name="choppiness"/>
        /// is 0, so the field collapses to the pure vertical swell.
        /// </summary>
        static Vector2 Displacement(float sourceX, float sourceZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            BandAccum a = EvaluateBands(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians,
                                        swellWavelength, swellHeight, ctx);
            return new Vector2(a.DisplacementX * choppiness, a.DisplacementZ * choppiness);
        }

        /// <summary>
        /// Invert the horizontal displacement: find the SOURCE (x, z) that Gerstner chop displaces
        /// onto the query world (x, z). Fixed-point iteration mirroring Crest's SampleInvertedDisplacement.
        /// With choppiness 0 the displacement is zero, so this returns the query point on the first pass.
        /// </summary>
        static Vector2 InvertToSource(float queryX, float queryZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            float sourceX = queryX;
            float sourceZ = queryZ;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector2 displacement = Displacement(sourceX, sourceZ, time, amplitudeScale,
                    windHeadingRadians, swellWavelength, swellHeight, choppiness, ctx);
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
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                            swellWavelength, swellHeight, choppiness, ctx);
            return Evaluate(source.x, source.y, time, amplitudeScale, windHeadingRadians,
                            swellWavelength, swellHeight, ctx);
        }

        /// <summary>Wave height (metres) at a QUERY world (x, z), chop-inverted. See EvaluateAtQuery.</summary>
        internal static float HeightAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
            => EvaluateAtQuery(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                               swellWavelength, swellHeight, choppiness, ctx).x;

        /// <summary>
        /// Vertical surface velocity d(height)/dt (m/s) at a QUERY world (x, z), chop-inverted: the swell's
        /// contribution to buoyancy drag velocity. Closed-form time derivative of the band sum, evaluated at
        /// the inverted source (same point the height is read from). Physics-only, so no shader mirror.
        /// </summary>
        internal static float VerticalVelocityAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness,
            in ShoreWaveContext ctx)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians,
                                            swellWavelength, swellHeight, choppiness, ctx);
            return EvaluateBands(source.x, source.y, time, amplitudeScale, windHeadingRadians,
                                 swellWavelength, swellHeight, ctx).HeightVelocity;
        }
    }
}
