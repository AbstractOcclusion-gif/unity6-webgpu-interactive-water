// WebGpuWater - simulation-facing adapter over the shared per-camera relevance snapshot.
// The snapshot owns visibility, nearest-bounds distance, deterministic ranking and budgets;
// this class only preserves the established once-per-frame Update integration point.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterSimScheduler
    {
        // Never equal to a Time.frameCount, so the first EnsureSchedule after a reset always runs.
        const int InvalidFrame = -1;
        static int _scheduleFrame = InvalidFrame;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState() => _scheduleFrame = InvalidFrame;

        // Decide (once per frame, for every body) which bodies draw and which run the
        // heavy GPU sim.
        internal static void EnsureSchedule()
        {
            if (_scheduleFrame == Time.frameCount) return;
            _scheduleFrame = Time.frameCount;

            var bodies = WaterVolume.Bodies;

            // Edit-mode preview: no culling/budget, every body draws and simulates, so the
            // scene view shows live water wherever the user looks (the game camera's frustum
            // is meaningless while framing in the scene view).
            if (!Application.isPlaying)
            {
                for (int i = 0; i < bodies.Count; i++)
                {
                    bodies[i]._visible = true;
                    bodies[i]._simulate = true;
                }
                return;
            }

            WaterRuntimeRelevance.ApplySimulationSchedule(ScheduleCamera());
        }

        // The eye drives the schedule (frustum culling, budgets, LOD centre). WaterEye owns the
        // whole ladder now - live WaterEye, then the primary's legacy targetCamera, then any
        // body's, then Camera.main - so this is one call instead of a second, subtly different
        // copy of the same fallback chain (the old copy accepted INACTIVE cameras, which is what
        // let a disabled rig keep winning the election).
        internal static Camera ScheduleCamera() => WaterEye.Resolve();
    }
}
