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
        /// The velocity out-param exists so a caller that needs both does not pay the chop inversion
        /// twice: the query path used to take the height here and then call
        /// LargeWaveField.VerticalVelocityAtQuery with identical arguments, which re-ran the whole
        /// 4-iteration inversion. Callers that only want the height use the single-argument overload
        /// above and discard it - on the analytic branch that costs nothing extra (same evaluation),
        /// and on the FFT branch the rate is computed from the analytic mirror exactly as before.
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
            if (OceanFftActive && _oceanFft.TrySampleField(worldX, worldZ, out Vector3 fft))
            {
                // The readback carries no time derivative, so the rate stays on the analytic mirror -
                // unchanged from before, just no longer recomputed by the caller.
                verticalRate = LargeWaveField.VerticalVelocityAtQuery(worldX, worldZ, _waveTime,
                    LargeWaveAmplitudeEffective, LargeWaveHeadingRad, SwellWavelength, SwellHeight,
                    LargeWaveChoppiness, ctx) * edge;
                return LargeWaveField.ApplyShoreToFftSample(fft, worldX, worldZ, _waveTime,
                    SwellWavelength, ctx) * edge;
            }
            LargeWaveField.EvaluateAtQuery(worldX, worldZ, _waveTime, LargeWaveAmplitudeEffective,
                LargeWaveHeadingRad, SwellWavelength, SwellHeight, LargeWaveChoppiness, ctx,
                out Vector3 heightSlope, out float rate);
            verticalRate = rate * edge;
            return heightSlope * edge;
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
            bool dirty = windWaves != _waveGenEnabled
                         || windFromDegrees != _waveGenWindFrom
                         || metersPerUnit != _waveGenExtentMeters
                         || count != _waveGenCount
                         || waveLengthMeters != _waveGenLength
                         || waveHeightMeters != _waveGenHeight
                         || waveGrouping != _waveGenGrouping
                         || waveCrestSharpness != _waveGenSharpness
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            // Wind SPEED is deliberately absent: it steers direction and gates foam, but the layer's
            // size is authored in metres now, so a wind change no longer forces a bank rebuild.
            _waveBank.Generate(windFromDegrees, waveLengthMeters, waveHeightMeters, count,
                               waveDirectionSpread, waveGrouping, waveCrestSharpness,
                               metersPerUnit, verticalExtent);
            _waveGenWindFrom = windFromDegrees;
            _waveGenExtentMeters = metersPerUnit;
            _waveGenCount = count;
            _waveGenLength = waveLengthMeters;
            _waveGenHeight = waveHeightMeters;
            _waveGenGrouping = waveGrouping;
            _waveGenSharpness = waveCrestSharpness;
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
