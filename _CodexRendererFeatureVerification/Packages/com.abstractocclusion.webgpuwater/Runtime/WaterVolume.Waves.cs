// WebGpuWater - WaterVolume partial: the wind-wave layer and its CPU mirror.
//
// The surface shader and the CPU (buoyancy, queries, fog gates) must agree on the wave field or
// floaters ride a swell the eye cannot see. Both halves therefore live together: the bank
// generation that feeds the shader, and the sampling path that mirrors WaterLargeWaves.hlsl -
// including the shore-transform context the two share.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // Public gameplay facade (ripples, height/submersion queries) -> WaterVolume.Facade.cs.

        // Shore-transform + surf-front context for the CPU wave mirror: the SAME knobs the shaders
        // read as globals, plus the baked field's CPU copies (WaterShoreDepthField). Inactive (all
        // zero, null field) when the shore substrate isn't live, so open water is byte-identical.
        internal ShoreWaveContext ShoreWaveCtx
        {
            get
            {
                WaterShoreDepthField shore = ShoreDepth;
                if (!useBedDepth || !shore.DepthBaked) return ShoreWaveContext.Inactive;
                ShoreWaveContext ctx = default;
                ctx.Field = shore;
                ctx.FetchField = SeaStateFetch;
                ctx.ShoalDepth = ShoreShoalDepthEffective;
                ctx.GreenBandDepth = shoreShoalDepth;
                ctx.Refraction = shoreRefraction;
                ctx.Compression = shoreCompression;
                ctx.Greens = shoreGreens;
                ctx.SurfActive = shore.SurfLayerActive;
                ctx.SurfAmplitude = SurfAmplitudeEffective;
                ctx.SurfWavelength = SurfWavelengthEffective;
                ctx.SurfPeriod = surfPeriod;
                ctx.SurfBeatTime = SurfBeatTime;
                ctx.SurfBandDepth = surfBandDepth;
                ctx.SurfSetStrength = surfSetStrength;
                ctx.SurfCrestLength = surfCrestLength;
                ctx.SurfCrestVariation = surfCrestVariation;
                ctx.SurfCrestPersistence = surfCrestPersistence;
                ctx.SurfDirectionality = surfDirectionality;
                ctx.SurfWindDirX = Mathf.Cos(LargeWaveHeadingRad);
                ctx.SurfWindDirZ = Mathf.Sin(LargeWaveHeadingRad);
                ctx.SurfLean = surfLean;
                ctx.SurfAmbientFade = surfAmbientFade;
                return ctx;
            }
        }

        // Large-body wave field (height, dHeight/dx, dHeight/dz) at a world xz. Prefers the FFT ocean's
        // async height-field readback (so floaters ride the exact rendered swell) and falls back to the
        // analytic CPU mirror before the first readback lands or on non-FFT bodies - matching the shader's
        // own gated fallback in WaterLargeWaves.hlsl.
        Vector3 SampleLargeWaveField(float worldX, float worldZ)
            => SampleLargeWaveField(worldX, worldZ, out _);

        /// <summary>Height/slope AND the swell's vertical rate at a world xz, from ONE evaluation.</summary>
        /// <remarks>
        /// The velocity out-param exists so a caller that needs both does not pay for the surface twice.
        /// On the ANALYTIC branch that means one chop inversion instead of two (the query path used to
        /// take the height here and then re-run the whole 4-iteration inversion for the rate). On the
        /// FFT branch it means one readback lookup: the rate is measured on the readback field itself,
        /// beside the height, rather than borrowed from the analytic mirror - a surface the FFT branch
        /// does not render. Callers that only want the height use the single-argument overload above and
        /// discard the rate; on both branches that costs nothing extra.
        /// </remarks>
        Vector3 SampleLargeWaveField(float worldX, float worldZ, out float verticalRate)
        {
            // Edge guard on height AND slope, mirroring the shader's composition points: near the
            // footprint border the rendered surface feathers flat, so buoyancy must too.
            float edge = LargeWaveEdgeWeight(worldX, worldZ);
            ShoreWaveContext ctx = ShoreWaveCtx; // built from ~22 fields incl. two trig calls - hoist it
            // The FFT readback bakes the RAW cascades; the shader's FFT branch additionally shoals
            // them by depth, fades them under the surf fronts and adds the fronts on top - so the
            // readback sample gets the same treatment (mirror of LargeBodyWaveHeight's FFT path).
            if (OceanFftActive && _oceanFft.TrySampleField(worldX, worldZ, out Vector3 fft,
                                                          out float fftRate))
            {
                // Height AND rate now both come from the FFT field. The rate used to be taken from
                // the analytic mirror, which WaterLargeWaves.hlsl does not render while the FFT branch
                // is live: buoyancy's surface-relative drag was chasing a velocity belonging to an
                // invisible surface, so it never relaxed and pumped energy in proportional to Swell
                // Height. ApplyShoreToFftSample composes the rate exactly like the height it
                // differentiates.
                verticalRate = fftRate;
                Vector3 shored = LargeWaveField.ApplyShoreToFftSample(fft, worldX, worldZ, _waveTime,
                    SwellWavelength, ctx, ref verticalRate);
                verticalRate *= edge;
                return shored * edge;
            }
            LargeWaveField.EvaluateAtQuery(worldX, worldZ, _waveTime, LargeWaveAmplitudeEffective,
                LargeWaveHeadingRad, SwellHeadingRad, SwellWavelength, SwellHeight, LargeWaveChoppiness, ctx,
                out Vector3 heightSlope, out float rate);
            verticalRate = rate * edge;
            return heightSlope * edge;
        }

        // Fixed-point iterations for the chop inversion below. 4 matches LargeWaveField's own
        // InversionIterations (the buoyancy-validated count for these wave scales).
        const int ChopInversionIterations = 4;

        /// <summary>Invert the large-wave HORIZONTAL displacement at a world xz: the SOURCE point
        /// whose displaced position lands on the query (Crest's SampleInvertedDisplacement).
        ///
        /// WHY (wake drift, 2026-08-03): interactive ripples live in the sim texture, which the
        /// surface samples at each vertex's UNDISPLACED lattice xz - and the FFT/Gerstner chop then
        /// moves that vertex horizontally by metres in a heavy sea. A wake stamped at the boat's
        /// world xz therefore APPEARS at (boat + chop), sliding around the hull with the swell
        /// phase. Injecting at the inverted source instead means the displaced surface carries the
        /// stamp exactly back onto the boat. Identity for non-open-water bodies (no chop), and for
        /// an FFT sea before its first displacement readback lands (a few frames of the old
        /// behaviour, never a wrong-phase analytic guess - the two branches render mutually
        /// exclusively, so mixing them here would invert a surface nothing draws).
        ///
        /// The displacement is edge-weighted like the render (fft.xz * amplitude * edge), and the
        /// FFT branch's readback already bakes the amplitude in. Shore 'ambient' attenuation of
        /// chop is NOT mirrored (readback lanes are raw-offshore maths): near a shore the
        /// inversion slightly overshoots, bounded by the ambient fade itself.</summary>
        internal Vector2 InvertLargeWaveChopXZ(float worldX, float worldZ)
        {
            if (!openWater) return new Vector2(worldX, worldZ);
            bool fft = OceanFftActive;
            ShoreWaveContext ctx = default;
            if (!fft) ctx = ShoreWaveCtx; // analytic branch only; ~22-field build, skip when unused
            float sx = worldX, sz = worldZ;
            for (int i = 0; i < ChopInversionIterations; i++)
            {
                Vector2 d;
                if (fft)
                {
                    if (!_oceanFft.TrySampleDisplacementLatest(sx, sz, out d))
                        return i == 0 ? new Vector2(worldX, worldZ) : new Vector2(sx, sz);
                }
                else
                {
                    d = LargeWaveField.HorizontalDisplacementAtSource(sx, sz, _waveTime,
                        LargeWaveAmplitudeEffective, LargeWaveHeadingRad, SwellHeadingRad,
                        SwellWavelength, SwellHeight, LargeWaveChoppiness, ctx);
                }
                d *= LargeWaveEdgeWeight(sx, sz);
                sx -= (sx + d.x) - worldX;
                sz -= (sz + d.y) - worldZ;
            }
            return new Vector2(sx, sz);
        }

        // ---- wind-wave layer -----------------------------------------------
        // Pool [-1,1] -> metres, for the wave PHASE only. DERIVED from the body, not authored: the
        // layer's wavelength is now given in metres, and that promise only holds if this conversion
        // matches the body's real footprint. It used to be a hand-entered field that also pretended to
        // be a fetch, so a 50 m lake with the default 10 left every wavelength stretched five times.
        // A non-square footprint still stretches the pattern on its short axis - pool space is
        // normalised per axis - which is a pre-existing property of sampling in pool space.
        internal float WaveMetersPerUnit =>
            Mathf.Max(MinWaveMetersPerUnit, Mathf.Max(VolumeExtentSafe.x, VolumeExtentSafe.z));

        // Regenerate the bank only when a wind/scale parameter actually changes, so
        // the phases stay stable frame-to-frame (a fresh bank would pop the surface).
        void EnsureWaveBank()
        {
            int count = EffectiveWaveCount;
            float verticalExtent = VolumeExtentSafe.y;
            float metersPerUnit = WaveMetersPerUnit;
            // Wind is back in the dirty set - not as the old hidden amplitude coupling, but because
            // Wind Response now scales the authored length and height through it (see
            // WindWaveGrowth). At response 0 those two are constant, so the bank simply never
            // rebuilds on a wind change, which is the old cheap behaviour without the old lie.
            float lengthEffective = WaveLengthEffective;
            float heightEffective = WaveHeightEffective;
            bool dirty = windWaves != _waveGenEnabled
                         || windFromDegrees != _waveGenWindFrom
                         || metersPerUnit != _waveGenExtentMeters
                         || count != _waveGenCount
                         || lengthEffective != _waveGenLength
                         || heightEffective != _waveGenHeight
                         || waveGrouping != _waveGenGrouping
                         || waveCrestSharpness != _waveGenSharpness
                         || waveAnimationSpeed != _waveGenAnimationSpeed
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            _waveBank.Generate(windFromDegrees, lengthEffective, heightEffective, count,
                               waveDirectionSpread, waveGrouping, waveCrestSharpness,
                               waveAnimationSpeed, metersPerUnit, verticalExtent);
            _waveGenWindFrom = windFromDegrees;
            _waveGenExtentMeters = metersPerUnit;
            _waveGenCount = count;
            _waveGenLength = lengthEffective;
            _waveGenHeight = heightEffective;
            _waveGenGrouping = waveGrouping;
            _waveGenSharpness = waveCrestSharpness;
            _waveGenAnimationSpeed = waveAnimationSpeed;
            _waveGenSpread = waveDirectionSpread;
            _waveGenVerticalExtent = verticalExtent;
            _waveGenEnabled = windWaves;
        }

        // The authored component count capped by the quality tier (mobile tiers sum fewer
        // sinusoids per vertex/pixel/buoyancy query).
        int EffectiveWaveCount => Mathf.Min(waveCount, _maxWaveCount);

        // Wave arrays are per-body, mirrored to globals only by the primary (see WriteBodyUniforms).
        // The wave CLOCK (_WaveTime) is ALSO per body (TimeScale/pause are per-body controls), carried
        // in the per-renderer blocks; the primary's global mirror is the camera-pass fallback.

        // With the link on, the depth colour tracks the fog extinction so a single dial drives
        // both; off, the depth colour is authored independently.
        internal Color EffectiveDepthExtinction => linkDepthToFog ? fogExtinction : depthExtinction;
    }
}
