// WebGpuWater - physical spline current for rivers and waterfall spans.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Current Field")]
    [DisallowMultipleComponent]
    public sealed class WaterRiverCurrentField : WaterCurrentField
    {
        const float HalfWidth = 0.5f;
        const float DomainBoundaryTolerance = 1e-4f;
        const float EndpointParameterTolerance = 1e-4f;
        const float DirectionLengthEpsilonSquared = 1e-8f;

        [Tooltip("River spline whose width bounds this field and whose tangent and speed define " +
                 "the physical world-space current.")]
        [SerializeField] internal WaterRiverSpline spline;
        [Tooltip("Optional settled fluid bake. When valid, its obstacle-deflected velocity replaces " +
                 "the uniform spline speed for gameplay sampling.")]
        [SerializeField] internal WaterRiverFluid fluid;

        protected override bool TryEvaluateCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (spline == null ||
                !spline.TryProjectPoint(worldPoint, out WaterRiverSplineSample sample, out _))
                return false;
            if (!IsValidSample(sample)) return false;

            Vector3 centreToPoint = worldPoint - sample.Position;
            if (IsOutsideSplineEnds(sample, centreToPoint)) return false;

            float halfWidth = sample.Width * HalfWidth;
            float lateralDistance = Mathf.Abs(Vector3.Dot(centreToPoint, sample.Right));
            if (!float.IsFinite(lateralDistance) ||
                lateralDistance > halfWidth + DomainBoundaryTolerance)
                return false;

            float lateralU = Mathf.InverseLerp(-halfWidth, halfWidth,
                Vector3.Dot(centreToPoint, sample.Right));
            WaterRiverFluidBakeData bakeData = fluid != null && fluid.isActiveAndEnabled
                ? fluid.BakeData
                : null;
            if (bakeData != null && bakeData.IsValid)
            {
                if (!bakeData.TrySample(
                        lateralU, sample.NormalizedT,
                        out Vector2 ribbonVelocity, out _, out _))
                    return false;
                worldVelocity = sample.Right * ribbonVelocity.x +
                                sample.Tangent * ribbonVelocity.y;
                return WaterSurfaceKinematics.IsFinite(worldVelocity);
            }

            worldVelocity = sample.Tangent * sample.Speed;
            return true;
        }

        void OnEnable()
        {
            // Existing authored rivers predate the bake component. Resolve a sibling once at the
            // lifecycle boundary so they adopt the shared field without a GetComponent per sample.
            if (fluid == null) fluid = GetComponent<WaterRiverFluid>();
        }

        internal void Configure(WaterRiverSpline riverSpline)
        {
            spline = riverSpline != null
                ? riverSpline
                : throw new ArgumentNullException(nameof(riverSpline));
        }

        internal void Configure(WaterRiverSpline riverSpline, WaterRiverFluid riverFluid)
        {
            Configure(riverSpline);
            fluid = riverFluid;
        }

        void Reset()
        {
            spline = GetComponent<WaterRiverSpline>();
            fluid = GetComponent<WaterRiverFluid>();
        }

        static bool IsValidSample(WaterRiverSplineSample sample)
        {
            return WaterSurfaceKinematics.IsFinite(sample.Position) &&
                   WaterSurfaceKinematics.IsFinite(sample.Tangent) &&
                   sample.Tangent.sqrMagnitude >= DirectionLengthEpsilonSquared &&
                   WaterSurfaceKinematics.IsFinite(sample.Right) &&
                   sample.Right.sqrMagnitude >= DirectionLengthEpsilonSquared &&
                   float.IsFinite(sample.Width) &&
                   sample.Width >= WaterRiverSpline.MinimumWidth &&
                   float.IsFinite(sample.Speed) &&
                   sample.Speed >= WaterRiverSpline.MinimumSpeed;
        }

        static bool IsOutsideSplineEnds(WaterRiverSplineSample sample, Vector3 centreToPoint)
        {
            bool beforeSource = sample.SegmentIndex == 0 &&
                                sample.SegmentT <= EndpointParameterTolerance &&
                                Vector3.Dot(centreToPoint, sample.Tangent) < -DomainBoundaryTolerance;
            if (beforeSource) return true;

            bool atLastSegmentEnd = sample.NormalizedT >= 1f - EndpointParameterTolerance;
            return atLastSegmentEnd &&
                   Vector3.Dot(centreToPoint, sample.Tangent) > DomainBoundaryTolerance;
        }
    }
}
