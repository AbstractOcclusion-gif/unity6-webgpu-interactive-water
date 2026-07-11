// WebGpuWater - WaterVolume's implementation of the IWaterHeightSampler query seam.
//
// One shared per-point evaluator (TrySampleWorld) that BOTH the single-point and the batched paths
// call, so a batch is guaranteed to agree with the single-point API for the same point. The world
// height/normal/flow are composed exactly like the existing TryGetSurface / TrySampleSubmersion (the
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
    public partial class WaterVolume : IWaterHeightSampler
    {
        // Explicit Scripts category so a ProfilerRecorder (WaterMetricsOverlay) can match it by (category, name).
        static readonly ProfilerMarker SampleHeightsMarker =
            new ProfilerMarker(ProfilerCategory.Scripts, "WaterVolume.SampleHeights");

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

        // The shared per-point evaluator. Height/normal/flow mirror TryGetSurface + TrySampleSubmersion
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
            if (!_sampler.TrySamplePoolSurface(probe, poolX, poolZ, out float poolHeight, out Vector2 poolFlow,
                                               minWavelength, excludeInteractiveRipples)) return false;

            float worldHeight = PoolToWorld(new Vector3(poolX, poolHeight, poolZ)).y;
            Vector3 worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            if (openWater)
            {
                // Open water carries the wind-wave layer AND the big world-space swell (the pool wavebank is
                // suppressed for these bodies); layer the swell on top exactly as the single-point path does.
                Vector3 wave = SampleLargeWaveField(worldPoint.x, worldPoint.z);
                worldHeight += wave.x;
                worldFlow += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }

            sample.Height = worldHeight;
            sample.Valid = true;
            if ((fields & WaterQueryFields.Normal) != 0)
                sample.Normal = SurfaceNormalFromFlow(worldFlow);
            if ((fields & WaterQueryFields.Velocity) != 0)
                sample.Velocity = SurfaceVelocity(worldPoint, poolX, poolZ, worldFlow, minWavelength);

            return true;
        }

        // The surface's downhill push (worldFlow) already points along -gradient, so the tilted surface
        // normal is the volume up leaned by that same vector. Consistent with the wave slope the shader
        // shades with (scaled by waveNormalStrength); an approximation, but the one buoyancy already trusts.
        Vector3 SurfaceNormalFromFlow(Vector3 worldFlow)
        {
            Vector3 normal = VolumeUp + worldFlow;
            float length = normal.magnitude;
            return length > NormalEpsilon ? normal / length : VolumeUp;
        }

        // World surface velocity = analytic vertical wave velocity (exact d(Height)/dt from the closed-form
        // wave mirrors, no cross-frame state) plus the horizontal wave-drift push buoyancy already uses.
        // Interactive ripple / FFT dynamics are not yet folded into the velocity (they add in a later phase).
        Vector3 SurfaceVelocity(Vector3 worldPoint, float poolX, float poolZ, Vector3 worldFlow, float minWavelength)
        {
            // Match the sampler's ocean-vs-pool coordinate choice for the wind-wave layer.
            float metersPerUnit = WaveMetersPerUnit;
            float waveX = IsOceanClipmap ? worldPoint.x / metersPerUnit : poolX;
            float waveZ = IsOceanClipmap ? worldPoint.z / metersPerUnit : poolZ;

            float poolRate = WindWaves ? WaveBank.SampleVerticalVelocity(waveX, waveZ, WaveTime, metersPerUnit, minWavelength) : 0f;
            // Pool vertical rate -> world Y rate along the same transform the height uses (PoolToWorld scales
            // by extent.y and rotates), so Velocity.y is exactly d(Height)/dt for the wind-wave layer.
            float worldRate = (VolumeRotation * new Vector3(0f, poolRate * VolumeExtentSafe.y, 0f)).y;
            if (openWater)
                worldRate += LargeWaveField.VerticalVelocityAtQuery(worldPoint.x, worldPoint.z, WaveTime,
                    LargeWaveAmplitudeEffective, LargeWaveHeadingRad, SwellWavelength, SwellHeight, LargeWaveChoppiness);

            Vector3 velocity = worldFlow;
            velocity.y += worldRate;
            return velocity;
        }

        const float NormalEpsilon = 1e-6f;
    }
}
