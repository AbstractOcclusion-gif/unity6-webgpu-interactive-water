// WebGpuWater - owner-keyed reusable buffers for batched water queries.
//
// A caller that samples the same points every frame (a floater's probes, a hull's sim vertices) should
// reuse one WaterSample[] rather than allocating each FixedUpdate. This registrar hands each owner its own
// grow-on-demand buffer, keyed by a stable owner id. It is the CPU analogue of Crest's per-caller query
// segments, minus the GPU readback ring buffer (our query path is CPU-analytic, so there is no latency to
// absorb). Main-thread only; not thread-safe.
using System;
using System.Collections.Generic;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Reusable per-owner result buffers for <see cref="IWaterHeightSampler.SampleHeights"/> callers.</summary>
    public sealed class WaterHeightQuery
    {
        readonly Dictionary<int, WaterSample[]> _resultsByOwner = new Dictionary<int, WaterSample[]>();

        /// <summary>A results buffer of at least <paramref name="count"/> for <paramref name="ownerId"/>,
        /// reused across frames and grown when the count rises. Use a stable id (e.g. GetInstanceID()).</summary>
        public WaterSample[] RentResults(int ownerId, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (!_resultsByOwner.TryGetValue(ownerId, out WaterSample[] buffer) || buffer.Length < count)
            {
                buffer = new WaterSample[count];
                _resultsByOwner[ownerId] = buffer;
            }
            return buffer;
        }

        /// <summary>Drop an owner's buffer (call when the owner is destroyed) so long-lived scenes don't retain it.</summary>
        public void Release(int ownerId) => _resultsByOwner.Remove(ownerId);
    }
}
