// WebGpuWater - WaterVolume's implementation of the IWaterHeightSampler query seam.
//
// One shared per-point evaluator (TrySampleWorld) that BOTH the single-point and the batched paths
// call, so a batch is guaranteed to agree with the single-point API for the same point. The world
// height/normal/wave drift are composed exactly like the existing TryGetSurface / TrySampleSubmersion (the
// verified buoyancy path); this file only adds the batched entry point and the surface velocity, it
// does not change how a single point is sampled.
//
// WebGPU-safe: everything here is CPU-analytic (the ripple readback when present, plus the wave
// mirrors), so it is valid from frame 0 without async GPU readback.
using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume : IWaterHeightSampler, IWaterCurrentSampler
    {
        // Explicit Scripts category so a ProfilerRecorder (WaterMetricsOverlay) can match it by
        // (category, name). The name is a shared const: the overlay reads the SAME string, so a
        // rename here can no longer silently blank its buoyancy line.
        internal const string SampleHeightsMarkerName = "WaterVolume.SampleHeights";
        static readonly ProfilerMarker SampleHeightsMarker =
            new ProfilerMarker(ProfilerCategory.Scripts, SampleHeightsMarkerName);

        /// <inheritdoc/>
        public bool SampleHeight(Vector3 worldPoint, out WaterSample sample, float minimumLength = 0f,
                                 bool excludeInteractiveRipples = false)
            => TrySampleWorld(worldPoint, WaterQueryFields.HeightNormalVelocity, minimumLength,
                              excludeInteractiveRipples, out sample);

        /// <inheritdoc/>
        public void SampleHeights(int ownerHash, float minimumLength,
                                  IReadOnlyList<Vector3> points, WaterSample[] results,
                                  WaterQueryFields fields = WaterQueryFields.HeightNormalVelocity,
                                  bool excludeInteractiveRipples = false)
        {
            // Validate at the boundary - a short results array would corrupt memory / throw deep in the loop.
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (results.Length < points.Count)
                throw new ArgumentException(
                    $"results length ({results.Length}) is smaller than points ({points.Count}).", nameof(results));

            // ownerHash is part of the seam for a future GPU / finite-difference cache; the CPU-analytic
            // path is stateless and needs no per-owner memory, so it is unused here.
            using (SampleHeightsMarker.Auto())
            {
                for (int i = 0; i < points.Count; i++)
                    TrySampleWorld(points[i], fields, minimumLength, excludeInteractiveRipples, out results[i]);
            }
        }

        /// <summary>Sample the surface at a world point on whichever body contains it (resolved per point),
        /// so a hull spanning two bodies floats correctly on each. False + invalid sample when no body
        /// contains the point.</summary>
        public static bool SampleHeightAcrossBodies(Vector3 worldPoint, out WaterSample sample, float minimumLength = 0f)
        {
            WaterVolume body = BodyContaining(worldPoint);
            if (body == null) { sample = default; return false; }
            return body.TrySampleWorld(worldPoint, WaterQueryFields.HeightNormalVelocity, minimumLength, false, out sample);
        }

        // The shared per-point evaluator. Height/normal/wave drift mirror TryGetSurface + TrySampleSubmersion
        // exactly so single-point and batched queries agree. Returns false (and leaves sample invalid) when
        // the point is outside the footprint or a supported readback has not landed yet.
        internal bool TrySampleWorld(Vector3 worldPoint, WaterQueryFields fields, float minimumLength,
                                     bool excludeInteractiveRipples, out WaterSample sample)
        {
            sample = default;
            if (_sampler == null) return false; // not initialized yet

            // minimumLength (the object's size) becomes a wavelength cut-off: wind-wave components shorter
            // than the object are dropped so a large floater ignores small ripples. 0 = full spectrum.
            float minWavelength = minimumLength;

            // QueryPoolXZ accepts points beyond the bounded extent on an unbounded ocean (its surface spans
            // everywhere), so a floater driven past the edge keeps its buoyancy. Bounded bodies stay gated.
            Vector3 probe = new Vector3(worldPoint.x, VolumeCenter.y, worldPoint.z);
            if (!QueryPoolXZ(probe, out float poolX, out float poolZ)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, poolX, poolZ, out float poolHeight,
                                               out Vector2 poolSurfaceTilt,
                                               minWavelength, excludeInteractiveRipples)) return false;

            float worldHeight = PoolToWorld(new Vector3(poolX, poolHeight, poolZ)).y;
            Vector3 worldSurfaceTilt =
                WaterSurfaceKinematics.TiltToWorld(VolumeRotation, poolSurfaceTilt);
            // Carried out of the swell sample below so SurfaceVelocity does not re-derive it: the two
            // used to run the same 4-iteration chop inversion on the same point in the same call.
            float largeWaveVerticalRate = 0f;
            if (openWater)
            {
                // Open water carries the wind-wave layer AND the big world-space swell (the pool wavebank is
                // suppressed for these bodies); layer the swell on top exactly as the single-point path does.
                Vector3 wave = SampleLargeWaveField(worldPoint.x, worldPoint.z, out largeWaveVerticalRate);
                worldHeight += wave.x;
                worldSurfaceTilt += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }

            sample.Height = worldHeight;
            sample.Valid = true;
            if ((fields & WaterQueryFields.Normal) != 0)
                sample.Normal = WaterSurfaceKinematics.NormalFromTilt(VolumeUp, worldSurfaceTilt);
            if ((fields & WaterQueryFields.Velocity) != 0)
            {
                Vector3 waveDriftVelocity =
                    WaterSurfaceKinematics.WaveDriftVelocityFromTilt(worldSurfaceTilt);
                sample.Velocity = SurfaceVelocity(worldPoint, poolX, poolZ, waveDriftVelocity,
                                                  minWavelength, largeWaveVerticalRate);
            }

            return true;
        }

        // World surface velocity = analytic vertical wave velocity (exact d(Height)/dt from the closed-form
        // wave mirrors, no cross-frame state), the horizontal wave-drift push buoyancy already uses, and
        // every authored physical-current field affecting this body.
        // Interactive ripple / FFT dynamics are not yet folded into the velocity (they add in a later phase).
        // largeWaveVerticalRate is the open-water swell's d(height)/dt, ALREADY edge-weighted, handed
        // down from the swell sample the caller just took. It used to be recomputed here from the
        // world point, which meant a second full chop inversion of the very same point in the very
        // same call; 0 on bodies without open water, exactly as the old branch produced.
        Vector3 SurfaceVelocity(Vector3 worldPoint, float poolX, float poolZ, Vector3 waveDriftVelocity,
                                float minWavelength, float largeWaveVerticalRate)
        {
            // Match the sampler's ocean-vs-pool coordinate choice for the wind-wave layer.
            float metersPerUnit = WaveMetersPerUnit;
            float waveX = IsOceanClipmap ? worldPoint.x / metersPerUnit : poolX;
            float waveZ = IsOceanClipmap ? worldPoint.z / metersPerUnit : poolZ;

            float poolRate = WindWaves ? WaveBank.SampleVerticalVelocity(waveX, waveZ, WaveTime, metersPerUnit, minWavelength) : 0f;
            // Pool vertical rate -> world Y rate along the same transform the height uses (PoolToWorld scales
            // by extent.y and rotates), so Velocity.y is exactly d(Height)/dt for the wind-wave layer.
            float worldRate = (VolumeRotation * new Vector3(0f, poolRate * VolumeExtentSafe.y, 0f)).y;
            worldRate += largeWaveVerticalRate;

            Vector3 currentVelocity = SampleCurrentFields(worldPoint);
            return WaterSurfaceKinematics.ComposeVelocity(
                waveDriftVelocity, currentVelocity, worldRate);
        }
    }
}
