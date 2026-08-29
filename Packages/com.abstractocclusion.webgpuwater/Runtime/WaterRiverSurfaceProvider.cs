// WebGpuWater - the river ribbon as a first-class surface provider.
//
// Before this seam existed, a river answered height queries with its parent volume's flat
// rectangular plane - a sloped river reported the wrong elevation everywhere except one contour.
// The spline projection already computed the real ribbon frame (position WITH height, right, up,
// width, speed) and dropped most of it; this provider is that data answering the unified query
// contract. Registered by WaterRiverSurface with RibbonSpecificity, so inside the ribbon's own
// footprint it beats the parent plane it is authored over.
//
// CPU-analytic throughout: projection + authored current, valid with no GPU simulation at all.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Surface provider for one WaterRiverSurface ribbon (see IWaterSurfaceProvider).</summary>
    public sealed class WaterRiverSurfaceProvider : IWaterSurfaceProvider
    {
        // Lateral acceptance matches WaterRiverCurrentField's bound so the surface and the
        // current agree about where the river ends.
        const float HalfWidth = 0.5f;
        const float DomainBoundaryTolerance = 1e-4f;

        readonly WaterRiverSurface _surface;
        readonly int _bodyId;

        internal WaterRiverSurfaceProvider(WaterRiverSurface surface)
        {
            _surface = surface != null
                ? surface : throw new System.ArgumentNullException(nameof(surface));
            _bodyId = WaterSurfaceProviders.NextBodyId();
        }

        public int BodyId => _bodyId;

        /// <summary>The parent volume when one is assigned - a ribbon may stand alone, so
        /// gameplay keys on BodyId, not on this.</summary>
        public WaterVolume Body => _surface != null ? _surface.WaterVolume : null;

        public int Specificity => WaterSurfaceProviders.RibbonSpecificity;

        public bool ContainsXZ(Vector3 worldPoint)
            => TryProject(worldPoint, 0f, out _, out _);

        public bool ContainsPoint(Vector3 worldPoint)
            => ContainsPointWithin(worldPoint, 0f);

        public bool ContainsPointWithin(Vector3 worldPoint, float boundaryMarginMeters)
        {
            if (!TryProject(worldPoint, boundaryMarginMeters, out WaterRiverSplineSample sample, out _))
                return false;
            // The gameplay water column hangs below the ribbon surface by the authored depth;
            // the ribbon itself is the top face.
            float top = sample.Position.y + boundaryMarginMeters;
            float bottom = sample.Position.y - _surface.GameplayDepthMeters - boundaryMarginMeters;
            return worldPoint.y <= top && worldPoint.y >= bottom;
        }

        public bool TrySampleSurface(Vector3 worldPoint, WaterQueryFields fields,
                                     float minimumWaveLength, bool excludeInteractiveRipples,
                                     out WaterSample sample)
        {
            sample = default;
            if (!TryProject(worldPoint, 0f, out WaterRiverSplineSample spline, out _)) return false;

            // Real spline elevation - the whole point of this provider. The ribbon's Up is the
            // spline frame's normal, already unit length (WaterRiverSplineEvaluator).
            sample.Height = spline.Position.y;
            sample.Valid = true;
            if ((fields & WaterQueryFields.Normal) != 0) sample.Normal = spline.Up;
            if ((fields & WaterQueryFields.Velocity) != 0)
                sample.Velocity = ResolveCurrent(worldPoint, spline);
            return true;
        }

        public Bounds DomainBounds
        {
            get
            {
                WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
                if (spline == null || spline.KnotCount == 0)
                    return new Bounds(Vector3.zero, Vector3.zero);

                Transform frame = spline.transform;
                Bounds bounds = new Bounds(
                    frame.position + frame.rotation * spline.GetKnot(0).LocalPosition, Vector3.zero);
                float widestKnot = 0f;
                for (int i = 0; i < spline.KnotCount; i++)
                {
                    WaterRiverKnot knot = spline.GetKnot(i);
                    bounds.Encapsulate(frame.position + frame.rotation * knot.LocalPosition);
                    widestKnot = Mathf.Max(widestKnot, knot.Width);
                }
                // Grow by the widest half-width horizontally and the water column vertically so
                // the bounds cover the ribbon area the containment tests accept.
                bounds.Expand(new Vector3(widestKnot, _surface.GameplayDepthMeters, widestKnot));
                return bounds;
            }
        }

        // Shared projection + lateral bound: the single geometric truth every public member uses,
        // so the surface, the containment and the footprint can never disagree about the banks.
        bool TryProject(Vector3 worldPoint, float boundaryMarginMeters,
                        out WaterRiverSplineSample sample, out float lateralDistance)
        {
            sample = default;
            lateralDistance = float.PositiveInfinity;
            WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
            if (spline == null) return false;
            if (!spline.TryProjectPoint(worldPoint, out sample, out _)) return false;
            if (!WaterSurfaceKinematics.IsFinite(sample.Position)) return false;

            Vector3 centreToPoint = worldPoint - sample.Position;
            lateralDistance = Mathf.Abs(Vector3.Dot(centreToPoint, sample.Right));
            float halfWidth = sample.Width * HalfWidth + boundaryMarginMeters;
            return float.IsFinite(lateralDistance) &&
                   lateralDistance <= halfWidth + DomainBoundaryTolerance;
        }

        // Prefer the authored current field (it folds the settled fluid bake when one exists);
        // fall back to the uniform spline current so a bare ribbon still reports real flow.
        Vector3 ResolveCurrent(Vector3 worldPoint, in WaterRiverSplineSample spline)
        {
            WaterRiverCurrentField field = _surface != null ? _surface.CurrentField : null;
            if (field != null && field.SampleCurrent(worldPoint, out Vector3 fieldVelocity))
                return fieldVelocity;
            return spline.Tangent * spline.Speed;
        }
    }
}
