using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class MeshWaveAndInputFeatureTests
    {
        [Test]
        public void WaterMeshBuilders_CreateExpectedTopologyAndRejectInvalidDetail()
        {
            Mesh grid = WaterMeshBuilder.BuildGrid(2);
            Mesh disc = WaterMeshBuilder.BuildDisc(2, 4);
            Mesh cube = WaterMeshBuilder.BuildUnitCube();
            Mesh sphere = WaterMeshBuilder.BuildUnitSphere();
            try
            {
                Assert.That(grid.vertexCount, Is.EqualTo(9));
                Assert.That(grid.triangles.Length, Is.EqualTo(24));
                Assert.That(disc.vertexCount, Is.EqualTo(12));
                Assert.That(disc.triangles.Length, Is.EqualTo(48));
                Assert.That(cube.vertexCount, Is.EqualTo(8));
                Assert.That(cube.triangles.Length, Is.EqualTo(36));
                Assert.That(sphere.bounds.extents.x, Is.EqualTo(0.5f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(grid);
                Object.DestroyImmediate(disc);
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(sphere);
            }

            Assert.That(() => WaterMeshBuilder.BuildGrid(0), Throws.InstanceOf<System.ArgumentException>());
            Assert.That(() => WaterMeshBuilder.BuildDisc(0, 4), Throws.InstanceOf<System.ArgumentException>());
            Assert.That(() => WaterMeshBuilder.BuildDisc(1, 2), Throws.InstanceOf<System.ArgumentException>());
        }

        [Test]
        public void WaveBank_IsDeterministicAndAZeroHeightSeaIsFlat()
        {
            var first = new WaterWaveBank();
            var second = new WaterWaveBank();
            first.Generate(45f, 8f, 1.5f, WaterWaveBank.MaxWaves + 1, 2f, 0.5f, 0.4f, 1f, 1f, 1f);
            second.Generate(45f, 8f, 1.5f, WaterWaveBank.MaxWaves + 1, 2f, 0.5f, 0.4f, 1f, 1f, 1f);

            Assert.That(first.Count, Is.EqualTo(WaterWaveBank.MaxWaves));
            Assert.That(first.SampleHeight(2f, 3f, 4f, 1f),
                        Is.EqualTo(second.SampleHeight(2f, 3f, 4f, 1f)).Within(0.000001f));

            var flat = new WaterWaveBank();
            flat.Generate(0f, 8f, 0f, 4, 1f, 0f, 0f, 1f, 1f, 1f);
            Assert.That(flat.SampleHeight(2f, 3f, 4f, 1f), Is.EqualTo(0f));
            Assert.That(flat.SampleVerticalVelocity(2f, 3f, 4f, 1f), Is.EqualTo(0f));
        }

        [Test]
        public void WaveBank_FilteringEveryComponentProducesAFlatSurface()
        {
            const float AllWavelengthsFiltered = 1000f;
            var bank = new WaterWaveBank();
            bank.Generate(45f, 8f, 1.5f, 4, 2f, 0.5f, 1f, 1f, 1f, 1f);

            Assert.That(bank.SampleHeight(2f, 3f, 4f, 1f, AllWavelengthsFiltered), Is.Zero);
            Assert.That(bank.SampleSlope(2f, 3f, 4f, 1f, AllWavelengthsFiltered), Is.EqualTo(Vector2.zero));
            Assert.That(bank.SampleVerticalVelocity(2f, 3f, 4f, 1f, AllWavelengthsFiltered), Is.Zero);
        }

        [Test]
        public void PinchTracker_OnlyReportsDeltaAfterTheFirstSampleAndResets()
        {
            var tracker = new PinchTracker();

            Assert.That(tracker.Update(Vector2.zero, Vector2.right, out float firstDelta), Is.False);
            Assert.That(firstDelta, Is.EqualTo(0f));
            Assert.That(tracker.Update(Vector2.zero, Vector2.right * 3f, out float secondDelta), Is.True);
            Assert.That(secondDelta, Is.EqualTo(2f));

            tracker.Reset();
            Assert.That(tracker.Update(Vector2.zero, Vector2.right * 5f, out float resetDelta), Is.False);
            Assert.That(resetDelta, Is.EqualTo(0f));
        }
    }
}
