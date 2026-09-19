// WebGpuWater - reflection policy home (Unity 6 / URP port).
//
// Reflection mode + look is UNIFORM-driven, published per body every frame by WaterUniformPublisher,
// so there are no keywords to set and no per-body material instancing for reflection. Planar is the
// one mode with a real per-frame cost (each planar body renders a full extra mirror), so this file
// owns the BUDGET that caps how many bodies may render a planar mirror in a frame. All reflection
// policy lives here.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Reflection policy for <see cref="WaterVolume"/>: the per-frame planar-mirror budget.</summary>
    internal static class WaterReflections
    {
        /// <summary>
        /// At most this many bodies render a planar mirror in a frame. Each mirror is a full extra
        /// scene render, so the count is capped; the bodies nearest the camera win and the rest degrade
        /// to SSR / sky. Bumping this raises planar fidelity across many pools at a linear GPU cost.
        /// </summary>
        internal const int MaxActivePlanarBodies = WaterRuntimeRelevance.PlanarReflectionBudget;

        internal static void ResetStaticState() { }

        /// <summary>Whether <paramref name="body"/> is allowed to render its planar mirror this frame.</summary>
        internal static bool IsPlanarGranted(WaterVolume body)
        {
            if (body == null) return false;
            Camera camera = body.Eye;
            return WaterRuntimeRelevance.IsPlanarGranted(body, camera);
        }
    }
}
