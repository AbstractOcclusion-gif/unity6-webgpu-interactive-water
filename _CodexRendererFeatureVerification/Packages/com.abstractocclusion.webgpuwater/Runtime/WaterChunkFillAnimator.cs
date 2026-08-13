// WebGpuWater - showcase helper: ping-pongs a chunk body's fill level so the finale station
// visibly drains and refills. It writes the internal chunkFillLevel field directly (same
// assembly); the chunk pipeline re-derives _ChunkSurfacePoolY from it every frame when writing
// per-body props, so no extra publish step is needed.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("")] // showcase plumbing: created by the showcase builder, not hand-placed
    internal sealed class WaterChunkFillAnimator : MonoBehaviour
    {
        // Mathf.PingPong(t, 1) completes a full out-and-back over t = 2, so scale time by 2/cycle.
        const float PingPongPeriodFactor = 2f;

        [Tooltip("Chunk body whose fill level is animated.")]
        [SerializeField] internal WaterVolume chunk;

        [Range(0f, 1f)] [SerializeField] internal float minFill = 0.15f;
        [Range(0f, 1f)] [SerializeField] internal float maxFill = 1f;

        [Tooltip("Seconds for one full drain-and-refill cycle.")]
        [Min(0.5f)] [SerializeField] internal float cycleSeconds = 12f;

        void Update()
        {
            if (chunk == null) return;
            float phase = Mathf.PingPong(Time.time * PingPongPeriodFactor / cycleSeconds, 1f);
            chunk.chunkFillLevel = Mathf.Lerp(minFill, maxFill, Mathf.SmoothStep(0f, 1f, phase));
        }
    }
}
