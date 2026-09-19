using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterWindowProfileTests
    {
        [Test]
        public void FixedProfileIgnoresSpeed()
        {
            var profile = new WaterSimulationWindowSettings { halfSizeMeters = 4f };
            Assert.That(profile.ResolveHalfSize(0f), Is.EqualTo(4f));
            Assert.That(profile.ResolveHalfSize(100f), Is.EqualTo(4f));
        }

        [TestCase(-1f, 4f)]
        [TestCase(0f, 4f)]
        [TestCase(4f, 10f)]
        [TestCase(8f, 16f)]
        [TestCase(100f, 16f)]
        public void AdaptiveProfileIsBoundedAndRespondsToSpeed(float speed, float expected)
        {
            var profile = new WaterSimulationWindowSettings
            {
                halfSizeMeters = 4f, adaptive = true,
                maximumHalfSizeMeters = 16f, speedForMaximumSize = 8f
            };
            Assert.That(profile.ResolveHalfSize(speed), Is.EqualTo(expected));
        }

        [Test]
        public void InvalidValuesCannotCreateAnEmptyOrNonFiniteWindow()
        {
            var profile = new WaterSimulationWindowSettings
            {
                halfSizeMeters = float.NaN, maximumHalfSizeMeters = float.NaN,
                speedForMaximumSize = 0f, adaptive = true
            };
            Assert.That(profile.ResolveHalfSize(float.PositiveInfinity), Is.EqualTo(4f));
        }

        [Test]
        public void HigherPriorityRetainsFocusAndItsOwnSizeProfile()
        {
            var root = new GameObject("Window profiles");
            root.SetActive(false);
            try
            {
                var body = root.AddComponent<WaterVolume>();
                var bobber = new WaterSimulationWindowSettings { halfSizeMeters = 4f };
                var boat = new WaterSimulationWindowSettings { halfSizeMeters = 20f };
                var high = body.PushSimulationFocus(root, root.transform, bobber, 100);
                using (body.PushSimulationFocus(root, root.transform, boat, 10))
                {
                    body.ResolveSimulationFocus(out _, out _, out var selected);
                    Assert.That(selected, Is.SameAs(bobber));
                    high.Dispose();
                    body.ResolveSimulationFocus(out _, out _, out selected);
                    Assert.That(selected, Is.SameAs(boat));
                }
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
