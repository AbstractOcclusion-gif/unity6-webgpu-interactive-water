using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverSplineFeatureTests
    {
        const float FloatTolerance = 0.001f;
        const float StartWidth = 4f;
        const float EndWidth = 8f;
        const float StartSpeed = 1f;
        const float EndSpeed = 5f;
        const float StartTangentLength = 3f;
        const float ProjectionOffset = 2f;
        const float ExpectedProjectionDistanceSquared = ProjectionOffset * ProjectionOffset;
        const float RootYawDegrees = 90f;
        const float IgnoredRootScale = 7f;
        const string SplineHostName = "River Spline Test";

        static readonly Vector3 RootPosition = new Vector3(3f, 4f, 5f);
        static readonly Vector3 StraightEnd = new Vector3(0f, 0f, 12f);
        static readonly Vector3 StraightTangent = StraightEnd * WaterRiverSpline.BezierHandleLengthFraction;
        static readonly Vector3 MiddlePosition = new Vector3(0f, 0f, 10f);
        static readonly Vector3 EndPosition = new Vector3(8f, -6f, 20f);
        static readonly Vector3 SharedMiddleTangent = new Vector3(2f, -1f, 3f);
        static readonly Vector3 WaterfallEnd = new Vector3(0f, -10f, 10f);
        static readonly Vector3 WaterfallTangent =
            WaterfallEnd * WaterRiverSpline.BezierHandleLengthFraction;

        [Test]
        public void Evaluation_UsesPositionAndRotationButIgnoresTransformScale()
        {
            GameObject host = CreateSpline(out WaterRiverSpline spline);
            try
            {
                spline.knots = TwoKnotSpline(StraightEnd, StraightTangent);
                host.transform.SetPositionAndRotation(
                    RootPosition, Quaternion.Euler(0f, RootYawDegrees, 0f));
                host.transform.localScale = Vector3.one * IgnoredRootScale;

                Assert.That(spline.TryEvaluate(1f, out WaterRiverSplineSample sample), Is.True);
                Vector3 expected = RootPosition + host.transform.rotation * StraightEnd;
                AssertVector3(sample.Position, expected);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AdjacentSegments_SharePositionAndTangentAtTheirKnot()
        {
            GameObject host = CreateSpline(out WaterRiverSpline spline);
            try
            {
                spline.knots = new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(
                        Vector3.zero, Vector3.forward * StartTangentLength, StartWidth, StartSpeed),
                    new WaterRiverKnot(MiddlePosition, SharedMiddleTangent, StartWidth, StartSpeed),
                    new WaterRiverKnot(EndPosition, SharedMiddleTangent, EndWidth, EndSpeed),
                };

                Assert.That(spline.TryEvaluateSegment(0, 1f, out WaterRiverSplineSample first), Is.True);
                Assert.That(spline.TryEvaluateSegment(1, 0f, out WaterRiverSplineSample second), Is.True);
                AssertVector3(first.Position, second.Position);
                AssertVector3(first.Tangent, second.Tangent);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Evaluation_InterpolatesWidthAndSpeedBetweenKnots()
        {
            GameObject host = CreateSpline(out WaterRiverSpline spline);
            try
            {
                spline.knots = TwoKnotSpline(StraightEnd, StraightTangent);

                Assert.That(spline.TryEvaluate(0.5f, out WaterRiverSplineSample sample), Is.True);
                Assert.That(sample.Width, Is.EqualTo((StartWidth + EndWidth) * 0.5f).Within(FloatTolerance));
                Assert.That(sample.Speed, Is.EqualTo((StartSpeed + EndSpeed) * 0.5f).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Projection_FindsAThreeDimensionalWaterfallCentreline()
        {
            GameObject host = CreateSpline(out WaterRiverSpline spline);
            try
            {
                spline.knots = TwoKnotSpline(WaterfallEnd, WaterfallTangent);
                Vector3 midpoint = WaterfallEnd * 0.5f;
                Vector3 queryPoint = midpoint + Vector3.right * ProjectionOffset;

                Assert.That(spline.TryProjectPoint(
                    queryPoint, out WaterRiverSplineSample sample, out float squaredDistance), Is.True);
                AssertVector3(sample.Position, midpoint);
                AssertVector3(sample.Right, Vector3.right);
                Assert.That(squaredDistance,
                    Is.EqualTo(ExpectedProjectionDistanceSquared).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        static GameObject CreateSpline(out WaterRiverSpline spline)
        {
            var host = new GameObject(SplineHostName);
            spline = host.AddComponent<WaterRiverSpline>();
            return host;
        }

        static List<WaterRiverKnot> TwoKnotSpline(Vector3 endPosition, Vector3 tangent)
        {
            return new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, tangent, StartWidth, StartSpeed),
                new WaterRiverKnot(endPosition, tangent, EndWidth, EndSpeed),
            };
        }

        static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }
    }
}
