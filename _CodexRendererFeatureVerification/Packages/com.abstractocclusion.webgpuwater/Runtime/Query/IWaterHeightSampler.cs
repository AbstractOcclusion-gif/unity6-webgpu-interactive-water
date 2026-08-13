// WebGpuWater - the public water-height query seam.
//
// A small, allocation-free façade so gameplay code (and buoyancy, and any future physics adapter)
// can ask "where is the water surface, which way is it tilted, how fast is it moving" at one or many
// world points WITHOUT reaching into WaterVolume internals. WaterVolume implements this directly.
//
// WebGPU-first: the implementation is CPU-analytic (the shared wave mirrors), so a batched query
// returns valid data from frame 0 with no async GPU readback - the readback path that Crest's query
// system depends on is unavailable on the deployed WebGPU build.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Which fields a query should fill. Skipping Normal/Velocity skips their per-point work.</summary>
    [System.Flags]
    public enum WaterQueryFields
    {
        Height = 1,
        Normal = 2,
        Velocity = 4,
        HeightNormalVelocity = Height | Normal | Velocity,
    }

    /// <summary>One point's surface result. <see cref="Valid"/> is false when the point is outside the
    /// body's footprint, or (on readback backends) before the first height readback has landed.</summary>
    public struct WaterSample
    {
        /// <summary>World-space surface height (Y) above the queried point.</summary>
        public float Height;
        /// <summary>World-space surface normal (unit). Only filled when <see cref="WaterQueryFields.Normal"/> is requested.</summary>
        public Vector3 Normal;
        /// <summary>World-space surface velocity (wave orbital motion). Only filled when
        /// <see cref="WaterQueryFields.Velocity"/> is requested. River/current flow is a later iteration.</summary>
        public Vector3 Velocity;
        /// <summary>False = outside the footprint or not ready; the other fields are meaningless.</summary>
        public bool Valid;
    }

    /// <summary>
    /// The simple way to query water height. A single-point convenience plus a batched path for one
    /// object's many probes/vertices in a single call.
    /// </summary>
    public interface IWaterHeightSampler
    {
        /// <summary>Sample the surface at one world point. <paramref name="minimumLength"/> lets large
        /// objects ignore short ripples (LOD filtering); 0 samples the full spectrum.
        /// <paramref name="excludeInteractiveRipples"/> samples the analytic surface (rest + wind + swell)
        /// only - for a body that emits its own ripples, so it isn't pushed by them.</summary>
        bool SampleHeight(Vector3 worldPoint, out WaterSample sample, float minimumLength = 0f,
                          bool excludeInteractiveRipples = false);

        /// <summary>
        /// Fill <paramref name="results"/>[i] for each <paramref name="points"/>[i] in one call.
        /// <paramref name="ownerHash"/> identifies the caller (e.g. a floater's GetHashCode) so the
        /// implementation can cache per-owner state across frames. <paramref name="results"/> must be at
        /// least as long as <paramref name="points"/>. <paramref name="excludeInteractiveRipples"/> samples
        /// the analytic surface only, so a self-emitting body isn't carried by its own wake.
        /// </summary>
        void SampleHeights(int ownerHash, float minimumLength,
                           IReadOnlyList<Vector3> points, WaterSample[] results,
                           WaterQueryFields fields = WaterQueryFields.HeightNormalVelocity,
                           bool excludeInteractiveRipples = false);
    }
}
