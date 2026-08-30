// WebGpuWater - river gameplay-parity acceptance tests (river harmonization round, 2026-08-29).
// Pins the R2/R3 contracts that pure code review cannot: the resolver hands gameplay the winning
// PROVIDER (a standalone ribbon floats with no body at all), the fog gate's ribbon scan honours
// the pre-arm margin, and the provider's frame-stamped projection memo + bounds reject never
// change an answer - only its cost.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverGameplayFeatureTests
    {
        const float StartHeight = 10f;
        const float EndHeight = 5f;
        const float RiverLength = 20f;
        const float RiverWidth = 5f;
        const float RiverSpeed = 2f;
        const float ArmBandMeters = 0.5f;

        readonly List<GameObject> _objects = new List<GameObject>();
        WaterRiverSurface _surface;
        WaterRiverSurfaceProvider _provider;

        [SetUp]
        public void SetUp()
        {
            WaterSurfaceProviders.ResetStaticState();
            _objects.Clear();

            GameObject river = new GameObject("GameplayRiver");
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
            WaterSurfaceProviders.Register(_provider);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            WaterSurfaceProviders.ResetStaticState();
        }

        [Test]
        public void Resolver_HandsGameplayTheRiverProvider_EvenWithNoBody()
        {
            // The R2 buoyancy contract: a standalone ribbon must resolve with the PROVIDER set
            // and Body null - out of the old GameplayBodyAt shape, floating keyed on the sample.
            WaterDomainQueryOptions options =
                WaterDomainQueryOptions.ForIntent(WaterQueryIntent.BuoyancySurface);
            Vector3 inColumn = new Vector3(0.5f, 7f, RiverLength * 0.5f);

            Assert.That(WaterDomainResolver.Resolve(inColumn, in options,
                                                    out WaterDomainSample sample), Is.True);
            Assert.That(sample.Provider, Is.SameAs(_provider));
            Assert.That(sample.Body, Is.Null, "a standalone ribbon has no owning volume");
            Assert.That(sample.SurfaceHeight, Is.GreaterThan(EndHeight));
            Assert.That(sample.SurfaceHeight, Is.LessThan(StartHeight));
        }

        [Test]
        public void ExtraProviderScan_HonoursThePreArmMargin()
        {
            // The R3 fog gate: just ABOVE the ribbon surface (inside the arm band) the scan must
            // already claim the eye, so the fog pass pre-arms before the crossing; well above it
            // must not, so lake frames stay on the legacy path.
            Vector3 mid = new Vector3(0f, 0f, RiverLength * 0.5f);
            Assert.That(_provider.TrySampleSurface(mid, WaterQueryFields.Height, 0f, false,
                                                   out WaterSample surface), Is.True);

            Vector3 justAbove = new Vector3(mid.x, surface.Height + ArmBandMeters * 0.5f, mid.z);
            Vector3 wellAbove = new Vector3(mid.x, surface.Height + ArmBandMeters * 4f, mid.z);
            Assert.That(WaterSurfaceProviders.ExtraProviderContaining(justAbove, ArmBandMeters),
                        Is.SameAs(_provider));
            Assert.That(WaterSurfaceProviders.ExtraProviderContaining(wellAbove, ArmBandMeters),
                        Is.Null);
        }

        [Test]
        public void ProjectionMemoAndBoundsReject_NeverChangeAnAnswer()
        {
            // The R2 perf caches must be invisible: interleaved queries (memo thrash) and a
            // repeat of the first query must answer exactly like a fresh provider.
            Vector3 pointA = new Vector3(0.5f, 8f, RiverLength * 0.25f);
            Vector3 pointB = new Vector3(-0.5f, 6f, RiverLength * 0.75f);
            Vector3 farOutside = new Vector3(500f, 8f, 500f); // bounds-rejected without projecting

            Assert.That(_provider.TrySampleSurface(pointA, WaterQueryFields.HeightNormalVelocity,
                                                   0f, false, out WaterSample firstA), Is.True);
            Assert.That(_provider.TrySampleSurface(pointB, WaterQueryFields.HeightNormalVelocity,
                                                   0f, false, out WaterSample firstB), Is.True);
            Assert.That(_provider.ContainsXZ(farOutside), Is.False);
            Assert.That(_provider.TrySampleSurface(pointA, WaterQueryFields.HeightNormalVelocity,
                                                   0f, false, out WaterSample repeatA), Is.True);

            var fresh = new WaterRiverSurfaceProvider(_surface);
            Assert.That(fresh.TrySampleSurface(pointA, WaterQueryFields.HeightNormalVelocity,
                                               0f, false, out WaterSample freshA), Is.True);
            Assert.That(repeatA.Height, Is.EqualTo(firstA.Height));
            Assert.That(repeatA.Height, Is.EqualTo(freshA.Height).Within(1e-6f));
            Assert.That(repeatA.Normal, Is.EqualTo(freshA.Normal));
            Assert.That(firstB.Height, Is.EqualTo(fresh.TrySampleSurface(pointB,
                WaterQueryFields.HeightNormalVelocity, 0f, false, out WaterSample freshB)
                    ? freshB.Height : float.NaN).Within(1e-6f));
        }

        [Test]
        public void BuoyancyFallThrough_StillFindsTheRibbonFromAbove()
        {
            // The XZ-only bounds reject exists precisely so this keeps working: a lure far above
            // the water column (outside the 3D bounds) must still resolve the surface BELOW it.
            WaterDomainQueryOptions options =
                WaterDomainQueryOptions.ForIntent(WaterQueryIntent.BuoyancySurface);
            options.MaxVerticalDistance = 50f;
            Vector3 highAbove = new Vector3(0f, StartHeight + 30f, RiverLength * 0.5f);

            Assert.That(WaterDomainResolver.Resolve(highAbove, in options,
                                                    out WaterDomainSample sample), Is.True);
            Assert.That(sample.Provider, Is.SameAs(_provider));
            Assert.That(sample.InsideDomain, Is.False);
            Assert.That(sample.SurfaceHeight, Is.LessThan(highAbove.y));
        }
    }
}
