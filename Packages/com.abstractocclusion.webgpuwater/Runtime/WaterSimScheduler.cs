// WebGpuWater - per-frame sim culling / active-sim budget schedule (Phase 3).
// Extracted from WaterVolume: sets _visible / _simulate for EVERY live body, once per
// frame. Frame-guarded so whichever body Updates first does the work and the rest reuse
// it (order-independent). Only the nearest bodies simulate each frame; off-screen bodies
// also stop drawing.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterSimScheduler
    {
        const int ActiveSimBudget = 4; // nearest bodies allowed to simulate at once

        static readonly Plane[] _frustumPlanes = new Plane[6];
        static int _scheduleFrame = -1;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState() => _scheduleFrame = -1;

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

            Camera cam = ScheduleCamera();
            if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Pass 1: visibility, plus a provisional "simulate" for visible + in-range bodies.
            for (int i = 0; i < bodies.Count; i++)
            {
                WaterVolume body = bodies[i];
                if (!body.EnableCulling || cam == null)
                {
                    body._visible = true;
                    body._simulate = true; // culling off -> always draw + simulate
                    continue;
                }
                body._visible = GeometryUtility.TestPlanesAABB(_frustumPlanes, body.CullBounds());
                body._simulate = IsSimEligible(body, cam.transform.position);
            }

            // Pass 2: cap the simulating set to the nearest ActiveSimBudget eligible bodies.
            EnforceSimBudget(cam);
        }

        // Eligible to simulate = culling on, visible, and within the activation distance.
        // Recomputed (not read from _simulate) so the budget pass can rank without its own
        // writes skewing the counts.
        static bool IsSimEligible(WaterVolume body, Vector3 camPos)
        {
            if (!body.EnableCulling || !body._visible) return false;
            // An unbounded ocean follows the camera and spans everywhere, so it must never be paused by
            // distance from its (fixed) origin - that froze the surface once the camera roamed past
            // activationDistance. It stays eligible (still subject to visibility and the sim budget).
            if (body.IsOceanClipmap) return true;
            float distSqr = (body.VolumeCenter - camPos).sqrMagnitude;
            return distSqr <= body.activationDistance * body.activationDistance;
        }

        // Keep only the nearest ActiveSimBudget eligible bodies simulating; pause the rest.
        // The body count is small, so an O(n^2) "how many eligible bodies are nearer than me"
        // rank avoids allocating a sorted list each frame.
        static void EnforceSimBudget(Camera cam)
        {
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;
            var bodies = WaterVolume.Bodies;

            int eligible = 0;
            for (int i = 0; i < bodies.Count; i++)
                if (IsSimEligible(bodies[i], camPos)) eligible++;
            if (eligible <= ActiveSimBudget) return;

            for (int i = 0; i < bodies.Count; i++)
            {
                WaterVolume body = bodies[i];
                if (!IsSimEligible(body, camPos)) continue;
                float d = (body.VolumeCenter - camPos).sqrMagnitude;

                int nearer = 0;
                for (int j = 0; j < bodies.Count; j++)
                {
                    WaterVolume other = bodies[j];
                    if (other == body || !IsSimEligible(other, camPos)) continue;
                    float od = (other.VolumeCenter - camPos).sqrMagnitude;
                    if (od < d || (od == d && j < i)) nearer++; // stable tiebreak by registry index
                }
                if (nearer >= ActiveSimBudget) body._simulate = false;
            }
        }

        // Prefer the primary's camera; fall back to any body's camera, then the main camera.
        static Camera ScheduleCamera()
        {
            if (WaterVolume.Primary != null && WaterVolume.Primary.targetCamera != null)
                return WaterVolume.Primary.targetCamera;
            var bodies = WaterVolume.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i].targetCamera != null) return bodies[i].targetCamera;
            return Camera.main;
        }
    }
}
