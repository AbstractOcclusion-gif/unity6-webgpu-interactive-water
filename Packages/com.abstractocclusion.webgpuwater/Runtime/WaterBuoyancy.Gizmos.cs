// WebGpuWater - buoyancy debug gizmos (editor only).
//
// Visualises the probe model so the new opt-in tuning (objectWidth, drag, max force) can be SEEN:
// the probe grid, each point's submersion, the sampled water surface points, the depth of each probe
// below the surface, and the buoyant lift arrows (green -> red by submersion). In play mode it reads
// the live per-frame query the solver already computed; out of play it previews the probe layout from
// the collider so you can place a floater before pressing play.
//
// The whole file is UNITY_EDITOR-guarded, so it compiles to nothing in a build - the runtime
// WaterBuoyancy never carries any gizmo cost. Delete this file to remove the instrumentation.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterBuoyancy
    {
        static readonly Color ProbeAboveColor = new Color(0.55f, 0.55f, 0.55f, 0.6f);   // dry point
        static readonly Color ProbeSubmergedColor = new Color(0.25f, 0.85f, 1f, 0.9f);  // fully submerged
        static readonly Color SurfacePointColor = new Color(0.35f, 0.65f, 1f, 0.85f);   // sampled water surface
        static readonly Color DepthLineColor = new Color(0.35f, 0.65f, 1f, 0.35f);      // probe -> surface
        static readonly Color ForceLowColor = new Color(0.3f, 1f, 0.3f, 0.9f);          // small lift
        static readonly Color ForceHighColor = new Color(1f, 0.35f, 0.2f, 0.9f);        // large lift

        const float ForceArrowRadii = 4f;         // lift-arrow length at full submersion, in probe radii
        const float MarkerRadiusFraction = 0.25f; // surface / arrow-tip marker size, as a fraction of the probe radius

        void OnDrawGizmosSelected()
        {
            if (!drawDebugGizmos) return;

            bool live = Application.isPlaying && _worldPoints != null && _results != null
                        && _results.Length >= _worldPoints.Length;
            if (live) DrawLiveGizmos();
            else DrawLayoutPreview();
        }

        // Play mode: read the solver's own per-frame query so the gizmo matches the applied forces exactly.
        void DrawLiveGizmos()
        {
            Vector3 up = _body != null ? _body.VolumeUp : Vector3.up;
            float markerRadius = _sphereRadius * MarkerRadiusFraction;

            for (int i = 0; i < _worldPoints.Length; i++)
            {
                Vector3 world = _worldPoints[i];
                WaterSample sample = _results[i];
                // Submersion computed exactly as FixedUpdate does, so the colours track the real forces.
                float depth = Vector3.Dot(new Vector3(0f, sample.Height - world.y, 0f), up);
                float fraction = sample.Valid ? Mathf.Clamp01(SphereSubmergedFraction(depth, _sphereRadius)) : 0f;

                DrawProbe(world, _sphereRadius, fraction);
                if (sample.Valid) DrawSurfaceLink(world, sample.Height, markerRadius);
                if (fraction > 0f) DrawLiftArrow(world, up, fraction, markerRadius);
            }
        }

        // Edit mode / before init: preview the probe layout from the collider (no water sampled yet).
        void DrawLayoutPreview()
        {
            Collider col = GetComponent<Collider>();
            GetLocalBox(col, out Vector3 center, out Vector3 size);

            int n = Mathf.Clamp(samplesPerAxis, MinSamplesPerAxis, MaxSamplesPerAxis);
            var points = new List<Vector3>(n * n * n);
            AppendLatticePoints(center, size, n, points);

            float radius = PreviewProbeRadius(size, n);
            for (int i = 0; i < points.Count; i++)
                DrawProbe(transform.TransformPoint(points[i]), radius, 0f);
        }

        void DrawProbe(Vector3 world, float radius, float submergedFraction)
        {
            Gizmos.color = Color.Lerp(ProbeAboveColor, ProbeSubmergedColor, submergedFraction);
            Gizmos.DrawWireSphere(world, radius);
        }

        void DrawSurfaceLink(Vector3 world, float surfaceHeight, float markerRadius)
        {
            Vector3 surfacePoint = new Vector3(world.x, surfaceHeight, world.z);
            Gizmos.color = DepthLineColor;
            Gizmos.DrawLine(world, surfacePoint);
            Gizmos.color = SurfacePointColor;
            Gizmos.DrawSphere(surfacePoint, markerRadius);
        }

        void DrawLiftArrow(Vector3 world, Vector3 up, float submergedFraction, float markerRadius)
        {
            Gizmos.color = Color.Lerp(ForceLowColor, ForceHighColor, submergedFraction);
            Vector3 tip = world + up * (submergedFraction * ForceArrowRadii * _sphereRadius);
            Gizmos.DrawLine(world, tip);
            Gizmos.DrawSphere(tip, markerRadius);
        }

        // Same radius BuildSamplePoints derives, recomputed for the edit-mode preview (which runs before Start).
        float PreviewProbeRadius(Vector3 localSize, int n)
        {
            Vector3 worldSize = Vector3.Scale(localSize, AbsScale(transform.lossyScale));
            return Mathf.Max(MinSphereRadius, 0.5f * (worldSize.y / n) * floatRadiusScale);
        }
    }
}
#endif
