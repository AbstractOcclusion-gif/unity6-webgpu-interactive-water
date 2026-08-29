// WebGpuWater - 3D domain resolution acceptance tests (2026-08-29 round, cases 1-8 + 18).
// Fake providers stand in for water bodies so the resolution RULES are pinned without GPU
// resources; the WaterVolume-backed geometry (ContainsPointXYZ) is exercised in play mode.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    /// <summary>Configurable stand-in provider shared by the domain/topology test fixtures.</summary>
    sealed class FakeWaterSurfaceProvider : IWaterSurfaceProvider
    {
        public readonly int Id;
        public int SpecificityValue;
        public Bounds BoundsValue;
        public float SurfaceHeightValue;
        public Vector3 NormalValue = Vector3.up;
        public Vector3 VelocityValue;
        public bool SampleSucceeds = true;

        public FakeWaterSurfaceProvider(Bounds bounds, float surfaceHeight)
        {
            Id = WaterSurfaceProviders.NextBodyId();
            BoundsValue = bounds;
            SurfaceHeightValue = surfaceHeight;
        }

        public int BodyId => Id;
        public WaterVolume Body => null;
        public int Specificity => SpecificityValue;
        public bool ContainsXZ(Vector3 p)
            => BoundsValue.Contains(new Vector3(p.x, BoundsValue.center.y, p.z));
        public bool ContainsPoint(Vector3 p) => BoundsValue.Contains(p);
        public bool ContainsPointWithin(Vector3 p, float marginMeters)
        {
            Bounds widened = BoundsValue;
            widened.Expand(2f * marginMeters); // Expand takes the TOTAL growth per axis
            return widened.Contains(p);
        }
        public bool TrySampleSurface(Vector3 p, WaterQueryFields fields, float minimumWaveLength,
                                     bool excludeInteractiveRipples, out WaterSample sample)
        {
            sample = default;
            if (!SampleSucceeds || !ContainsXZ(p)) return false;
            sample.Height = SurfaceHeightValue;
            sample.Normal = NormalValue;
            sample.Velocity = VelocityValue;
            sample.Valid = true;
            return true;
        }
        public Bounds DomainBounds => BoundsValue;
    }

    public sealed class WaterDomainResolutionFeatureTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();

        // A pond over a sewer sharing XZ - the architecture's founding case.
        FakeWaterSurfaceProvider _pond;   // column y [9, 11], surface 11
        FakeWaterSurfaceProvider _sewer;  // column y [-1, 1], surface 1

        [SetUp]
        public void SetUp()
        {
            WaterSurfaceProviders.ResetStaticState();
            WaterTopology.ResetStaticState();
            WaterExclusionVolume.ResetStaticState();
            _objects.Clear();
            _pond = RegisterFake(new Bounds(new Vector3(0f, 10f, 0f), new Vector3(10f, 2f, 10f)), 11f);
            _sewer = RegisterFake(new Bounds(new Vector3(0f, 0f, 0f), new Vector3(10f, 2f, 10f)), 1f);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            WaterSurfaceProviders.ResetStaticState();
            WaterTopology.ResetStaticState();
            WaterExclusionVolume.ResetStaticState();
        }

        FakeWaterSurfaceProvider RegisterFake(Bounds bounds, float surfaceHeight)
        {
            var fake = new FakeWaterSurfaceProvider(bounds, surfaceHeight);
            WaterSurfaceProviders.Register(fake);
            return fake;
        }

        static WaterDomainQueryOptions Containing()
            => WaterDomainQueryOptions.ForIntent(WaterQueryIntent.ContainingVolume);

        [Test]
        public void StackedBodies_ResolveByElevation_NotByFootprint()
        {
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 10f, 0f), Containing(),
                                                    out WaterDomainSample inPond), Is.True);
            Assert.That(inPond.BodyId, Is.EqualTo(_pond.Id));
            Assert.That(inPond.SurfaceHeight, Is.EqualTo(11f));
            Assert.That(inPond.SignedDepth, Is.EqualTo(1f));
            Assert.That(inPond.InsideDomain, Is.True);

            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 0.5f, 0f), Containing(),
                                                    out WaterDomainSample inSewer), Is.True);
            Assert.That(inSewer.BodyId, Is.EqualTo(_sewer.Id));
            Assert.That(inSewer.SurfaceHeight, Is.EqualTo(1f));
        }

        [Test]
        public void AirGapBetweenStackedBodies_ReportsNoContainingBody_NeverPrimary()
        {
            bool resolved = WaterDomainResolver.Resolve(new Vector3(0f, 5f, 0f), Containing(),
                                                        out WaterDomainSample sample);
            Assert.That(resolved, Is.False);
            Assert.That(sample.Validity, Is.EqualTo(WaterDomainValidity.NoContainingBody));
            Assert.That(sample.Body, Is.Null);
            Assert.That(sample.BodyId, Is.Zero);
        }

        [Test]
        public void ExplicitFallback_IsHonoured_OnlyWhenRequested()
        {
            // No WaterVolume exists in this fixture, so even the explicit policy finds nothing -
            // but it must fail CLEANLY, and the default policy must not have consulted it at all.
            WaterDomainQueryOptions options = Containing();
            options.Fallback = WaterFallbackPolicy.PrimaryBody;
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 5f, 0f), options,
                                                    out WaterDomainSample sample), Is.False);
            Assert.That(sample.Validity, Is.EqualTo(WaterDomainValidity.NoContainingBody));
        }

        [Test]
        public void OverlappingDomains_SmallerVolumeWins_ThenSpecificity()
        {
            FakeWaterSurfaceProvider lake = RegisterFake(
                new Bounds(new Vector3(0f, 10f, 0f), new Vector3(100f, 4f, 100f)), 11f);
            Vector3 point = new Vector3(0f, 10f, 0f);

            Assert.That(WaterDomainResolver.Resolve(point, Containing(),
                                                    out WaterDomainSample sample), Is.True);
            Assert.That(sample.BodyId, Is.EqualTo(_pond.Id), "smaller domain must win the overlap");

            lake.SpecificityValue = WaterSurfaceProviders.RibbonSpecificity;
            Assert.That(WaterDomainResolver.Resolve(point, Containing(), out sample), Is.True);
            Assert.That(sample.BodyId, Is.EqualTo(lake.Id), "higher specificity must beat volume");
        }

        [Test]
        public void BoundaryHysteresis_PreviousDomainHoldsWithinMargin_NotBeyond()
        {
            Vector3 justOutside = new Vector3(5.2f, 10f, 0f);  // pond half-extent x = 5
            Vector3 farOutside = new Vector3(5.2f + WaterDomainResolver.DomainSwitchMarginMeters,
                                             10f, 0f);

            Assert.That(WaterDomainResolver.Resolve(justOutside, Containing(), out _), Is.False,
                        "without a hint the edge point is outside");

            WaterDomainQueryOptions withHint = Containing();
            withHint.PreviousBodyId = _pond.Id;
            Assert.That(WaterDomainResolver.Resolve(justOutside, withHint,
                                                    out WaterDomainSample held), Is.True);
            Assert.That(held.BodyId, Is.EqualTo(_pond.Id));

            Assert.That(WaterDomainResolver.Resolve(farOutside, withHint, out _), Is.False,
                        "the hysteresis band must not stretch past its margin");
        }

        [Test]
        public void BuoyancyIntent_FindsTheSurfaceBelow_AFallingLure()
        {
            WaterDomainQueryOptions options =
                WaterDomainQueryOptions.ForIntent(WaterQueryIntent.BuoyancySurface);
            // 1.5 m above the pond surface: not contained, but the pond is directly below.
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 12.5f, 0f), options,
                                                    out WaterDomainSample sample), Is.True);
            Assert.That(sample.BodyId, Is.EqualTo(_pond.Id));
            Assert.That(sample.InsideDomain, Is.False);
            Assert.That(sample.SignedDepth, Is.EqualTo(-1.5f));
        }

        [Test]
        public void BoxAndSphereExclusions_SuppressGameplayWater()
        {
            GameObject carve = new GameObject("Carve");
            _objects.Add(carve);
            carve.transform.position = new Vector3(0f, 10f, 0f);
            WaterExclusionVolume volume = carve.AddComponent<WaterExclusionVolume>();
            volume.shape = WaterExclusionVolume.Shape.Box;
            volume.size = new Vector3(2f, 2f, 2f);

            Vector3 dryPoint = new Vector3(0f, 10f, 0f);
            Assert.That(WaterDomainResolver.Resolve(dryPoint, Containing(),
                                                    out WaterDomainSample vetoed), Is.False);
            Assert.That(vetoed.Validity, Is.EqualTo(WaterDomainValidity.Excluded));
            Assert.That(vetoed.Excluded, Is.True);

            WaterDomainQueryOptions debug = Containing();
            debug.IncludeExcludedSpace = true;
            Assert.That(WaterDomainResolver.Resolve(dryPoint, debug,
                                                    out WaterDomainSample inspected), Is.True);
            Assert.That(inspected.Excluded, Is.True);

            Vector3 wetPoint = new Vector3(3f, 10f, 0f); // outside the carve, inside the pond
            Assert.That(WaterDomainResolver.Resolve(wetPoint, Containing(), out _), Is.True);
        }

        [Test]
        public void MeshExclusions_FollowTheDocumentedProxyPolicy()
        {
            GameObject carve = new GameObject("MeshCarve");
            _objects.Add(carve);
            carve.transform.position = new Vector3(0f, 10f, 0f);
            WaterExclusionVolume volume = carve.AddComponent<WaterExclusionVolume>();
            volume.shape = WaterExclusionVolume.Shape.Mesh;
            volume.meshProxy = WaterExclusionVolume.Shape.Sphere;
            volume.size = new Vector3(2f, 2f, 2f);

            // The CPU test answers through the SPHERE proxy: a box corner outside the inscribed
            // ball must stay water even though a box-shaped carve would have excluded it.
            Vector3 insideProxy = new Vector3(0f, 10f, 0f);
            Vector3 boxCornerOutsideProxy = new Vector3(0.9f, 10.9f, 0.9f);
            Assert.That(WaterDomainResolver.Resolve(insideProxy, Containing(),
                                                    out WaterDomainSample vetoed), Is.False);
            Assert.That(vetoed.Validity, Is.EqualTo(WaterDomainValidity.Excluded));
            Assert.That(WaterDomainResolver.Resolve(boxCornerOutsideProxy, Containing(), out _),
                        Is.True);
        }

        [Test]
        public void ResolveBatch_ValidatesAtTheBoundary()
        {
            var points = new List<Vector3> { Vector3.zero, Vector3.one };
            Assert.That(() => WaterDomainResolver.ResolveBatch(points, new WaterDomainSample[1],
                                                               Containing()),
                        Throws.ArgumentException);
            Assert.That(() => WaterDomainResolver.ResolveBatch(null, new WaterDomainSample[1],
                                                               Containing()),
                        Throws.ArgumentNullException);
        }

        [Test]
        public void SteadyStateResolution_DoesNotAllocateManagedMemory()
        {
            WaterDomainQueryOptions options = Containing();
            options.PreviousBodyId = _pond.Id;
            Vector3 point = new Vector3(0f, 10f, 0f);
            // Warm up (profiler marker, JIT), then measure a burst.
            for (int i = 0; i < 32; i++) WaterDomainResolver.Resolve(point, in options, out _);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1024; i++) WaterDomainResolver.Resolve(point, in options, out _);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(allocated, Is.Zero,
                        "Resolve must stay allocation-free in steady state (acceptance 18)");
        }
    }
}
