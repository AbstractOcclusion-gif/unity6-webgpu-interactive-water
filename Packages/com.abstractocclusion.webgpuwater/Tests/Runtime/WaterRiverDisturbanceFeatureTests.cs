using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverDisturbanceFeatureTests
    {
        const float SourceRadiusMeters = 1f;
        const float SourceAmplitudeMeters = 0.1f;
        const float WakeLengthMeters = 6f;
        const float FoamStrength = 0.7f;
        const float MinimumRelativeSpeed = 0.05f;
        const float FullRelativeSpeed = 0.75f;
        const float FloatTolerance = 1e-5f;
        const float ConnectedSegmentLengthMeters = 10f;
        const float ConnectedRiverWidthMeters = 3f;
        const float ConnectedRiverSpeedMetersPerSecond = 1.5f;
        const float ConnectedRiverStartMeters = 0f;
        const float ConnectedRiverMiddleNormalizedT = 0.5f;
        const float MaximumCrescentCentreCoverage = 0.01f;
        const int ConnectedSamplesPerSegment = 8;
        const string UpstreamRiverName = "Disturbance Upstream River";
        const string DownstreamRiverName = "Disturbance Downstream River";

        [Test]
        public void RelativeSpeedResponse_StaticObjectInStillWaterProducesNoSustainedWake()
        {
            float response = WaterRiverDisturbance.RelativeSpeedResponse(
                0f, MinimumRelativeSpeed, FullRelativeSpeed);

            Assert.That(response, Is.EqualTo(0f));
        }

        [Test]
        public void RelativeSpeedResponse_StaticObjectInFlowReachesFullStrength()
        {
            float response = WaterRiverDisturbance.RelativeSpeedResponse(
                FullRelativeSpeed, MinimumRelativeSpeed, FullRelativeSpeed);

            Assert.That(response, Is.EqualTo(1f).Within(FloatTolerance));
        }

        [Test]
        public void ObstacleProfile_RaisesUpstreamCrescentAndDepressesDownstream()
        {
            var source = new WaterRiverDisturbance.Source(
                new Vector4(0f, 0f, SourceRadiusMeters, SourceAmplitudeMeters),
                new Vector4(0f, 1f, WakeLengthMeters, FoamStrength),
                Vector4.zero);
            Vector2 upstreamPosition = new Vector2(
                0f, -SourceRadiusMeters *
                    WaterRiverDisturbance.UpstreamCrescentRadiusScale);
            Vector2 downstreamPosition = new Vector2(
                0f, SourceRadiusMeters * WaterRiverDisturbance.DownstreamOffsetRadiusScale);

            float upstream = WaterRiverDisturbance.EvaluateSourceHeight(
                upstreamPosition, in source, out float upstreamFoam);
            float downstream = WaterRiverDisturbance.EvaluateSourceHeight(
                downstreamPosition, in source, out float downstreamFoam);

            Assert.That(upstream, Is.GreaterThan(0f));
            Assert.That(downstream, Is.LessThan(0f));
            Assert.That(upstreamFoam, Is.GreaterThan(0f));
            Assert.That(downstreamFoam, Is.GreaterThanOrEqualTo(0f));
        }

        [Test]
        public void UpstreamProfile_IsAFrontFacingCrescentInsteadOfACentreBulge()
        {
            Vector2 direction = Vector2.up;
            float crescentRadius =
                SourceRadiusMeters * WaterRiverDisturbance.UpstreamCrescentRadiusScale;
            float front = WaterRiverDisturbance.EvaluateUpstreamCrescent(
                Vector2.down * crescentRadius, direction, SourceRadiusMeters);
            float side = WaterRiverDisturbance.EvaluateUpstreamCrescent(
                Vector2.right * crescentRadius, direction, SourceRadiusMeters);
            float back = WaterRiverDisturbance.EvaluateUpstreamCrescent(
                Vector2.up * crescentRadius, direction, SourceRadiusMeters);
            float centre = WaterRiverDisturbance.EvaluateUpstreamCrescent(
                Vector2.zero, direction, SourceRadiusMeters);

            Assert.That(front, Is.EqualTo(1f).Within(FloatTolerance));
            Assert.That(side, Is.GreaterThan(0f).And.LessThan(front));
            Assert.That(back, Is.LessThan(side));
            Assert.That(centre, Is.LessThan(MaximumCrescentCentreCoverage));
        }

        [Test]
        public void ImpactProfile_IsFiniteAndCreatesAnExpandingCrest()
        {
            const float impactAmplitude = 0.05f;
            const float ringRadius = 2f;
            const float ringWidth = 0.25f;
            var source = new WaterRiverDisturbance.Source(
                new Vector4(0f, 0f, ringWidth, 0f),
                new Vector4(0f, 1f, ringWidth, FoamStrength),
                new Vector4(impactAmplitude, ringRadius, ringWidth, 1f));

            float height = WaterRiverDisturbance.EvaluateSourceHeight(
                new Vector2(ringRadius, 0f), in source, out float foam);

            Assert.That(float.IsFinite(height), Is.True);
            Assert.That(height, Is.EqualTo(impactAmplitude).Within(FloatTolerance));
            Assert.That(foam, Is.GreaterThan(0f));
        }

        [Test]
        public void ConnectedRiver_DisturbanceUsesTheMeshLongitudinalCoordinate()
        {
            GameObject upstreamHost = CreateStraightRiver(
                UpstreamRiverName, ConnectedRiverStartMeters,
                out _, out WaterRiverSurface upstreamSurface);
            GameObject downstreamHost = CreateStraightRiver(
                DownstreamRiverName, ConnectedSegmentLengthMeters,
                out WaterRiverSpline downstreamSpline, out WaterRiverSurface downstreamSurface);
            try
            {
                downstreamSurface.ConfigureSourceBoundary(upstreamSurface, useTargetMouth: true);
                WaterRiverDisturbance disturbance =
                    downstreamHost.AddComponent<WaterRiverDisturbance>();
                Assert.That(downstreamSpline.TryEvaluate(
                    ConnectedRiverMiddleNormalizedT, out WaterRiverSplineSample sample), Is.True);

                float disturbanceLongitudinal = disturbance.LongitudinalMeters(in sample);
                var meshCurrentData = new List<Vector4>();
                downstreamSurface.GeneratedMesh.GetUVs(1, meshCurrentData);
                int middleRow = ConnectedSamplesPerSegment / 2;
                int middleVertex = middleRow *
                                   WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;

                Assert.That(downstreamSurface.SourceLongitudinalMeters,
                    Is.EqualTo(upstreamSurface.MouthLongitudinalMeters).Within(FloatTolerance));
                Assert.That(disturbanceLongitudinal,
                    Is.EqualTo(meshCurrentData[middleVertex].y).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(downstreamHost);
                Object.DestroyImmediate(upstreamHost);
            }
        }

        static GameObject CreateStraightRiver(string name, float startMeters,
                                              out WaterRiverSpline spline,
                                              out WaterRiverSurface surface)
        {
            var host = new GameObject(name);
            spline = host.AddComponent<WaterRiverSpline>();
            Vector3 start = Vector3.forward * startMeters;
            Vector3 end = start + Vector3.forward * ConnectedSegmentLengthMeters;
            Vector3 tangent = Vector3.forward *
                              (ConnectedSegmentLengthMeters *
                               WaterRiverSpline.BezierHandleLengthFraction);
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(start, tangent, ConnectedRiverWidthMeters,
                                   ConnectedRiverSpeedMetersPerSecond),
                new WaterRiverKnot(end, tangent, ConnectedRiverWidthMeters,
                                   ConnectedRiverSpeedMetersPerSecond),
            };
            surface = host.AddComponent<WaterRiverSurface>();
            surface.spline = spline;
            surface.samplesPerSegment = ConnectedSamplesPerSegment;
            surface.RequestRebuild();
            return host;
        }
    }
}
