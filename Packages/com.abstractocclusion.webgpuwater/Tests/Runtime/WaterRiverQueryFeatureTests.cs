// WebGpuWater - river provider acceptance tests (2026-08-29 round, cases 9, 10, 12).
// The provider is pure spline math, so a sloped authored spline pins that height, normal and
// current come from the RIBBON - the elevations the parent volume's flat plane cannot produce.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverQueryFeatureTests
    {
        const float StartHeight = 10f;
        const float EndHeight = 5f;
        const float RiverLength = 20f;
        const float RiverWidth = 5f;
        const float RiverSpeed = 2f;

        readonly List<GameObject> _objects = new List<GameObject>();
        WaterRiverSurface _surface;
        WaterRiverSurfaceProvider _provider;

        [SetUp]
        public void SetUp()
        {
            WaterSurfaceProviders.ResetStaticState();
            _objects.Clear();

            // Inactive while wiring so no OnEnable runs against half-built state.
            GameObject river = new GameObject("SlopedRiver");
            _objects.Add(river);
            river.SetActive(false);
            WaterRiverSpline spline = river.AddComponent<WaterRiverSpline>();
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(new Vector3(0f, StartHeight, 0f),
                                   new Vector3(0f, 0f, RiverLength / 3f), RiverWidth, RiverSpeed),
                new WaterRiverKnot(new Vector3(0f, EndHeight, RiverLength),
                                   new Vector3(0f, 0f, RiverLength / 3f), RiverWidth, RiverSpeed),
            };
            _surface = river.AddComponent<WaterRiverSurface>();
            _surface.spline = spline;
            _provider = new WaterRiverSurfaceProvider(_surface);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            WaterSurfaceProviders.ResetStaticState();
        }

        [Test]
        public void SlopedRiver_ReturnsSplineDerivedHeightAndNormal()
        {
            Vector3 midPoint = new Vector3(0.5f, 8f, RiverLength * 0.5f);
            Assert.That(_provider.TrySampleSurface(midPoint, WaterQueryFields.HeightNormalVelocity,
                                                   0f, false, out WaterSample sample), Is.True);

            // The ribbon descends from 10 to 5; the parent plane could only ever answer one
            // constant height. Mid-river must sit strictly between the ends, near the middle.
            Assert.That(sample.Height, Is.LessThan(StartHeight));
            Assert.That(sample.Height, Is.GreaterThan(EndHeight));
            Assert.That(sample.Height, Is.EqualTo((StartHeight + EndHeight) * 0.5f).Within(1.5f));

            // A surface descending along +Z tilts its normal into the slope plane, leaning
            // downstream: n ~ (-dh/dx, 1, -dh/dz) and dh/dz < 0 puts a POSITIVE z component.
            Assert.That(sample.Normal.magnitude, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(Vector3.Dot(sample.Normal, Vector3.up), Is.LessThan(0.9999f));
            Assert.That(Vector3.Dot(sample.Normal, Vector3.up), Is.GreaterThan(0.7f));
            Assert.That(sample.Normal.z, Is.GreaterThan(0.1f));
        }

        [Test]
        public void RiverCurrent_FollowsTheAuthoredSplineDirection()
        {
            Vector3 midPoint = new Vector3(0f, 8f, RiverLength * 0.5f);
            Assert.That(_provider.TrySampleSurface(midPoint, WaterQueryFields.Velocity,
                                                   0f, false, out WaterSample sample), Is.True);

            // Downstream is +Z (with a downhill component); speed magnitude is the authored one.
            Assert.That(sample.Velocity.magnitude, Is.EqualTo(RiverSpeed).Within(0.05f));
            Assert.That(sample.Velocity.z, Is.GreaterThan(0f));
            Assert.That(Vector3.Dot(sample.Velocity.normalized, Vector3.forward),
                        Is.GreaterThan(0.85f));
        }

        [Test]
        public void RibbonDomain_BoundsLaterallyAndByGameplayDepth()
        {
            float halfWidth = RiverWidth * 0.5f;
            Vector3 mid = new Vector3(0f, 7.5f, RiverLength * 0.5f);

            Assert.That(_provider.ContainsPoint(mid), Is.True);
            Assert.That(_provider.ContainsPoint(mid + Vector3.right * (halfWidth + 1f)), Is.False,
                        "outside the banks is not river");
            float belowColumn = WaterRiverSurface.DefaultGameplayDepthMeters + 1.5f;
            Assert.That(_provider.ContainsPoint(mid + Vector3.down * belowColumn), Is.False,
                        "below the gameplay column is not river");
        }

        [Test]
        public void DormantProvider_AnswersAnalyticQueries_WithNoGpuSimulation()
        {
            // The provider has no WaterVolume, no lease, no GPU anything - the whole query
            // surface must still answer (acceptance 12's analytic half).
            Assert.That(_provider.Body, Is.Null);
            Assert.That(WaterSimLeasePool.ActiveLeaseCount, Is.Zero);
            Assert.That(_provider.TrySampleSurface(new Vector3(0f, 8f, 2f),
                                                   WaterQueryFields.HeightNormalVelocity, 0f, false,
                                                   out WaterSample sample), Is.True);
            Assert.That(sample.Valid, Is.True);
        }
    }
}
