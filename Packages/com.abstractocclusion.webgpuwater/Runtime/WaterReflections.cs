// WebGpuWater - reflection policy home (Unity 6 / URP port).
//
// Reflection mode + look is UNIFORM-driven, published per body every frame by WaterUniformPublisher,
// so there are no keywords to set and no per-body material instancing for reflection. Planar is the
// one mode with a real per-frame cost (each planar body renders a full extra mirror), so this file
// owns the BUDGET that caps how many bodies may render a planar mirror in a frame. All reflection
// policy lives here.
using System.Collections.Generic;
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
        internal const int MaxActivePlanarBodies = 3;

        static int _budgetFrame = -1;
        static readonly List<WaterVolume> _candidates = new List<WaterVolume>();
        static readonly HashSet<WaterVolume> _granted = new HashSet<WaterVolume>();

        /// <summary>Whether <paramref name="body"/> is allowed to render its planar mirror this frame.</summary>
        internal static bool IsPlanarGranted(WaterVolume body)
        {
            if (body == null) return false;
            RecomputeBudget();
            return _granted.Contains(body);
        }

        // Rebuild the granted set once per frame: gather every body that WANTS planar, keep the nearest
        // MaxActivePlanarBodies to their camera. Computed from "wants" (not the granted flag) so there is
        // no self-reference. A single recompute per frame keeps the Update-time publish and the
        // render-time mirror pass agreeing on the same grant.
        static void RecomputeBudget()
        {
            if (_budgetFrame == Time.frameCount) return;
            _budgetFrame = Time.frameCount;

            _granted.Clear();
            _candidates.Clear();

            IReadOnlyList<WaterVolume> bodies = WaterVolume.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                WaterVolume body = bodies[i];
                if (body != null && body.WantsPlanar) _candidates.Add(body);
            }

            if (_candidates.Count > MaxActivePlanarBodies)
                _candidates.Sort(CompareByCameraDistance);

            int grantCount = Mathf.Min(MaxActivePlanarBodies, _candidates.Count);
            for (int i = 0; i < grantCount; i++) _granted.Add(_candidates[i]);
        }

        static int CompareByCameraDistance(WaterVolume a, WaterVolume b)
            => CameraDistanceSq(a).CompareTo(CameraDistanceSq(b));

        static float CameraDistanceSq(WaterVolume body)
        {
            Camera cam = body.targetCamera;
            if (cam == null) return float.MaxValue; // no camera = lowest priority, but still a candidate
            return (cam.transform.position - body.transform.position).sqrMagnitude;
        }
    }
}
