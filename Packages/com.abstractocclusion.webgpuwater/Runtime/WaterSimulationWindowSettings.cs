using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Coverage profile for one focus. Texture resolution remains fixed.</summary>
    [System.Serializable]
    public sealed class WaterSimulationWindowSettings
    {
        [Tooltip("Window half-size in metres at rest. 4 means an 8 by 8 metre window.")]
        [Min(1f)] public float halfSizeMeters = 4f;
        [Tooltip("Expand coverage with horizontal target speed, then shrink when it slows.")]
        public bool adaptive;
        [Min(1f)] public float maximumHalfSizeMeters = 16f;
        [Min(0.1f)] public float speedForMaximumSize = 8f;
        [Tooltip("Minimum time between adaptive resizes. Reduces resampling and size chatter.")]
        [Min(0.25f)] public float resizeIntervalSeconds = 1.5f;

        public float ResolveHalfSize(float speed)
        {
            float minimum = float.IsFinite(halfSizeMeters) ? Mathf.Max(1f, halfSizeMeters) : 4f;
            if (!adaptive) return minimum;
            float maximum = float.IsFinite(maximumHalfSizeMeters)
                ? Mathf.Max(minimum, maximumHalfSizeMeters) : minimum;
            float referenceSpeed = float.IsFinite(speedForMaximumSize)
                ? Mathf.Max(0.1f, speedForMaximumSize) : 8f;
            float t = float.IsFinite(speed) ? Mathf.Clamp01(speed / referenceSpeed) : 0f;
            // Half-metre bands avoid continuously changing texel size.
            return Mathf.Clamp(Mathf.Round(Mathf.Lerp(minimum, maximum, t) * 2f) * 0.5f,
                minimum, maximum);
        }
    }
}
