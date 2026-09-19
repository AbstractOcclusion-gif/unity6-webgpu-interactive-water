using NUnit.Framework;
using System.Reflection;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterDisturbanceSizeTests
    {
        [TestCase(0.5f)]
        [TestCase(0.85f)]
        [TestCase(1.2f)]
        public void FishDisturbanceUsesIndividualWorldScaleWhenEnabled(float scale)
        {
            var fish = new GameObject("Sized fish");
            try
            {
                var disturbance = fish.AddComponent<WaterSurfaceDisturbance>();
                fish.transform.localScale = Vector3.one * scale;
                Assert.That(disturbance.DisturbanceScale, Is.EqualTo(1f),
                    "Existing non-fish users retain authored world dimensions.");
                typeof(WaterSurfaceDisturbance).GetField("scaleWithTransform",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(disturbance, true);
                Assert.That(disturbance.DisturbanceScale, Is.EqualTo(scale).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(fish); }
        }
    }
}
