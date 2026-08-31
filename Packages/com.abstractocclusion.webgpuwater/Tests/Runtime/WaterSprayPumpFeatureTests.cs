using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterSprayPumpFeatureTests
    {
        const float FlowSpeed = 2f;
        const float Tolerance = 0.0001f;

        [Test]
        public void RelativeCurrentImpact_FiresOnlyOnUpstreamFacingProbe()
        {
            Vector3 downstreamFlow = Vector3.forward * FlowSpeed;

            float upstreamFace = WaterSprayPump.RelativeCurrentImpact(
                downstreamFlow, Vector3.back);
            float downstreamFace = WaterSprayPump.RelativeCurrentImpact(
                downstreamFlow, Vector3.forward);

            Assert.That(upstreamFace, Is.EqualTo(FlowSpeed).Within(Tolerance));
            Assert.That(downstreamFace, Is.Zero.Within(Tolerance));
        }

        [Test]
        public void RelativeCurrentImpact_ZeroDirectionUsesFullHorizontalFlow()
        {
            Vector3 diagonalFlow = new Vector3(FlowSpeed, FlowSpeed, FlowSpeed);

            float impact = WaterSprayPump.RelativeCurrentImpact(diagonalFlow, Vector3.zero);

            Assert.That(impact, Is.EqualTo(FlowSpeed * Mathf.Sqrt(2f)).Within(Tolerance));
        }
    }
}
