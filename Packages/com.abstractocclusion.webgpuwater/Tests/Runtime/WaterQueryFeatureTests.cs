using System;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterQueryFeatureTests
    {
        [Test]
        public void BilinearSampling_InterpolatesAtTheFieldCentreAndClampsAtEdges()
        {
            float[] field = { 0f, 10f, 20f, 30f };

            Assert.That(WaterFieldSampling.SampleBilinear(field, 2, 0.5f, 0.5f), Is.EqualTo(15f));
            Assert.That(WaterFieldSampling.SampleBilinear(field, 2, -1f, -1f), Is.EqualTo(0f));
            Assert.That(WaterFieldSampling.SampleBilinear(field, 2, 2f, 2f), Is.EqualTo(30f));
        }

        [Test]
        public void WaterHeightQuery_ReusesBuffersAndDropsReleasedOwners()
        {
            var query = new WaterHeightQuery();

            WaterSample[] first = query.RentResults(100, 2);
            WaterSample[] reused = query.RentResults(100, 1);
            WaterSample[] grown = query.RentResults(100, 3);

            Assert.That(reused, Is.SameAs(first));
            Assert.That(grown, Is.Not.SameAs(first));
            Assert.That(grown.Length, Is.EqualTo(3));

            query.Release(100);
            Assert.That(query.RentResults(100, 1), Is.Not.SameAs(grown));
        }

        [Test]
        public void WaterHeightQuery_RejectsNegativeResultCounts()
        {
            var query = new WaterHeightQuery();
            Assert.That(() => query.RentResults(100, -1), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
