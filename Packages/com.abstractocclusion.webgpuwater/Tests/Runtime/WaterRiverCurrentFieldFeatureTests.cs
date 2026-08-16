using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverCurrentFieldFeatureTests
    {
        const float FloatTolerance = 0.001f;
        const float StartWidth = 4f;
        const float EndWidth = 8f;
        const float StartSpeed = 1f;
        const float EndSpeed = 5f;
        const float RiverLength = 12f;
        const float WaterfallDrop = 12f;
        const float OutsideBankOffset = 0.01f;
        const float OutsideEndOffset = 1f;
        const float IgnoredTransformScale = 7f;
        const string HostName = "River Current Field Test";

        static readonly Vector3 StraightEnd = Vector3.forward * RiverLength;
        static readonly Vector3 StraightTangent =
            StraightEnd * WaterRiverSpline.BezierHandleLengthFraction;
        static readonly Vector3 WaterfallEnd =
            new Vector3(0f, -WaterfallDrop, RiverLength);
        static readonly Vector3 WaterfallTangent =
            WaterfallEnd * WaterRiverSpline.BezierHandleLengthFraction;

        [Test]
        public void Current_UsesInterpolatedSplineSpeedAndDownstreamDirection()
        {
            GameObject host = CreateField(StraightEnd, StraightTangent,
                                          out _, out WaterRiverCurrentField field);
            try
            {
                Vector3 midpoint = StraightEnd * 0.5f;

                Assert.That(field.SampleCurrent(midpoint, out Vector3 velocity), Is.True);
                AssertVector3(velocity, Vector3.forward * MidpointSpeed);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Current_UsesInterpolatedBankWidthAsItsLateralDomain()
        {
            GameObject host = CreateField(StraightEnd, StraightTangent,
                                          out _, out WaterRiverCurrentField field);
            try
            {
                Vector3 midpoint = StraightEnd * 0.5f;
                float midpointHalfWidth = MidpointWidth * 0.5f;
                Vector3 bankPoint = midpoint + Vector3.right * midpointHalfWidth;
                Vector3 outsidePoint = bankPoint + Vector3.right * OutsideBankOffset;

                Assert.That(field.SampleCurrent(bankPoint, out _), Is.True);
                Assert.That(field.SampleCurrent(outsidePoint, out Vector3 outsideVelocity), Is.False);
                AssertVector3(outsideVelocity, Vector3.zero);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Current_RejectsPointsBeyondTheOpenSplineEnds()
        {
            GameObject host = CreateField(StraightEnd, StraightTangent,
                                          out _, out WaterRiverCurrentField field);
            try
            {
                Vector3 beforeSource = Vector3.back * OutsideEndOffset;
                Vector3 afterMouth = StraightEnd + Vector3.forward * OutsideEndOffset;

                Assert.That(field.SampleCurrent(beforeSource, out _), Is.False);
                Assert.That(field.SampleCurrent(afterMouth, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Current_PreservesFullThreeDimensionalWaterfallVelocity()
        {
            GameObject host = CreateField(WaterfallEnd, WaterfallTangent,
                                          out _, out WaterRiverCurrentField field);
            try
            {
                Vector3 midpoint = WaterfallEnd * 0.5f;
                Vector3 expectedVelocity = WaterfallEnd.normalized * MidpointSpeed;

                Assert.That(field.SampleCurrent(midpoint, out Vector3 velocity), Is.True);
                AssertVector3(velocity, expectedVelocity);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Current_IgnoresSplineTransformScaleForWidthSpeedAndVelocity()
        {
            GameObject host = CreateField(StraightEnd, StraightTangent,
                                          out WaterRiverSpline spline,
                                          out WaterRiverCurrentField field);
            try
            {
                host.transform.localScale = Vector3.one * IgnoredTransformScale;
                Assert.That(spline.TryEvaluate(0.5f, out WaterRiverSplineSample midpoint), Is.True);

                Assert.That(field.SampleCurrent(midpoint.Position, out Vector3 velocity), Is.True);
                AssertVector3(velocity, Vector3.forward * MidpointSpeed);
                Assert.That(midpoint.Width, Is.EqualTo(MidpointWidth).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void Current_WithoutSplineReturnsNoVelocity()
        {
            var host = new GameObject(HostName);
            WaterRiverCurrentField field = host.AddComponent<WaterRiverCurrentField>();
            try
            {
                field.spline = null;

                Assert.That(field.SampleCurrent(Vector3.zero, out Vector3 velocity), Is.False);
                AssertVector3(velocity, Vector3.zero);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        static float MidpointWidth => (StartWidth + EndWidth) * 0.5f;
        static float MidpointSpeed => (StartSpeed + EndSpeed) * 0.5f;

        static GameObject CreateField(Vector3 endPosition, Vector3 tangent,
                                      out WaterRiverSpline spline,
                                      out WaterRiverCurrentField field)
        {
            var host = new GameObject(HostName);
            spline = host.AddComponent<WaterRiverSpline>();
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, tangent, StartWidth, StartSpeed),
                new WaterRiverKnot(endPosition, tangent, EndWidth, EndSpeed),
            };
            field = host.AddComponent<WaterRiverCurrentField>();
            field.Configure(spline);
            return host;
        }

        static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }
    }
}
