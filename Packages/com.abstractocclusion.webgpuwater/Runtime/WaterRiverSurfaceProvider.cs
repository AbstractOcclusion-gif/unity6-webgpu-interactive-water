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
//
// Perf (river round, 2026-08-29): a full projection costs ~17 samples x segments + 6 bisections
// of cubic Bezier evaluation, and the resolver's candidate scan hits EVERY live provider for
// EVERY gameplay query - so a coarse XZ bounds reject runs first, and a frame-stamped memo
// collapses the resolver's ContainsPoint + TrySampleSurface pair (same point, same frame) into
// one projection. Both caches are bypassed outside play mode so editor tooling and edit-mode
// tests never read a stale frame stamp.
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
        const int InvalidFrameStamp = -1;

        readonly WaterRiverSurface _surface;
        readonly int _bodyId;

        Bounds _cachedBounds;
        int _boundsFrameStamp = InvalidFrameStamp;

        Vector3 _memoPoint;
        float _memoMargin;
        bool _memoHit;
        WaterRiverSplineSample _memoSample;
        float _memoLateral;
        int _memoFrameStamp = InvalidFrameStamp;

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

        /// <summary>The owning ribbon component (the river fog gate walks through it to the
        /// facade's connected ends for the F1 medium handoff).</summary>
        internal WaterRiverSurface Surface => _surface;

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
            float disturbanceHeight = 0f;
            Vector2 disturbanceNormalTilt = Vector2.zero;
            WaterRiverDisturbance disturbance = _surface.Disturbance;
            if (!excludeInteractiveRipples && disturbance != null)
                disturbance.TrySample(worldPoint, in spline, out disturbanceHeight,
                                      out disturbanceNormalTilt, out _);
            sample.Height = spline.Position.y + disturbanceHeight * spline.Up.y;
            sample.Valid = true;
            if ((fields & WaterQueryFields.Normal) != 0)
                sample.Normal = Vector3.Normalize(
                    spline.Up + spline.Right * disturbanceNormalTilt.x +
                    spline.Tangent * disturbanceNormalTilt.y);
            if ((fields & WaterQueryFields.Velocity) != 0)
                sample.Velocity = ResolveCurrent(worldPoint, spline);
            return true;
        }

        public Bounds DomainBounds => CurrentBounds();

        // Frame-stamped bounds: the knots move rarely, the resolver reads bounds per query.
        // Outside play the stamp never advances, so the cache is bypassed there.
        Bounds CurrentBounds()
        {
            if (Application.isPlaying && _boundsFrameStamp == Time.frameCount)
                return _cachedBounds;
            _cachedBounds = ComputeBounds();
            _boundsFrameStamp = Time.frameCount;
            return _cachedBounds;
        }

        Bounds ComputeBounds()
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

        // Shared projection + lateral bound: the single geometric truth every public member uses,
        // so the surface, the containment and the footprint can never disagree about the banks.
        bool TryProject(Vector3 worldPoint, float boundaryMarginMeters,
                        out WaterRiverSplineSample sample, out float lateralDistance)
        {
            sample = default;
            lateralDistance = float.PositiveInfinity;
            WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
            if (spline == null) return false;

            // Coarse reject BEFORE any projection - XZ ONLY: the buoyancy fall-through samples
            // the surface from far above the water column, so height must never gate footprint
            // questions (ContainsPointWithin applies the vertical column itself).
            if (!WithinBoundsXZ(worldPoint, boundaryMarginMeters)) return false;

            if (Application.isPlaying && _memoFrameStamp == Time.frameCount &&
                _memoPoint == worldPoint && _memoMargin == boundaryMarginMeters)
            {
                sample = _memoSample;
                lateralDistance = _memoLateral;
                return _memoHit;
            }

            bool hit = ProjectUncached(spline, worldPoint, boundaryMarginMeters,
                                       out sample, out lateralDistance);
            _memoPoint = worldPoint;
            _memoMargin = boundaryMarginMeters;
            _memoHit = hit;
            _memoSample = sample;
            _memoLateral = lateralDistance;
            _memoFrameStamp = Time.frameCount;
            return hit;
        }

        bool WithinBoundsXZ(Vector3 worldPoint, float boundaryMarginMeters)
        {
            Bounds bounds = CurrentBounds();
            return worldPoint.x >= bounds.min.x - boundaryMarginMeters &&
                   worldPoint.x <= bounds.max.x + boundaryMarginMeters &&
                   worldPoint.z >= bounds.min.z - boundaryMarginMeters &&
                   worldPoint.z <= bounds.max.z + boundaryMarginMeters;
        }

        bool ProjectUncached(WaterRiverSpline spline, Vector3 worldPoint,
                             float boundaryMarginMeters, out WaterRiverSplineSample sample,
                             out float lateralDistance)
        {
            sample = default;
            lateralDistance = float.PositiveInfinity;
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
