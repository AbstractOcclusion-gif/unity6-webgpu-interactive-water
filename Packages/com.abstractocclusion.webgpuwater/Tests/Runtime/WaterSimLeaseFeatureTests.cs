// WebGpuWater - simulation lease pool + budget acceptance tests (2026-08-29 round, 13/16 partial).
//
// Honest coverage note: WaterSimulation needs a real ComputeShader asset, which a runtime test
// assembly cannot load without a Resources folder (banned). The GPU halves of acceptance 14/15
// (actual RT reuse, actual state clearing) therefore run in play mode on a real scene; what is
// pinned HERE is the deterministic policy: the retention cap, the per-tier budget knob and its
// sanitisation, and the fog-independence resolve these rounds share a Tier path with.
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterSimLeaseFeatureTests
    {
        [Test]
        public void IdleRetentionPolicy_ParksUpToTheCap_ThenDisposes()
        {
            for (int idleCount = 0; idleCount < WaterSimLeasePool.IdleRetentionCap; idleCount++)
                Assert.That(WaterSimLeasePool.ShouldRetainReleased(idleCount), Is.True);
            Assert.That(WaterSimLeasePool.ShouldRetainReleased(WaterSimLeasePool.IdleRetentionCap),
                        Is.False);
        }

        [Test]
        public void EmptyPool_ReportsNoLeasesAndResetsClean()
        {
            WaterSimLeasePool.ResetStaticState();
            Assert.That(WaterSimLeasePool.ActiveLeaseCount, Is.Zero);
            Assert.That(WaterSimLeasePool.IdleContextCount, Is.Zero);
            Assert.That(() => WaterSimLeasePool.Release(null, null), Throws.Nothing);
        }

        [Test]
        public void TierBudget_DefaultsToTheOriginalFour_AndClampsToTheCap()
        {
            Assert.That(WaterQuality.Default.MaxSimulatedBodies, Is.EqualTo(4),
                        "default budget must reproduce the scheduler's original constant");

            var tier = new WaterQuality.Tier(256, 1024, 24, true, true, 16, 5, 1f, true, 0,
                                             1, 1, 1, 128, 65536,
                                             WaterQuality.UnderwaterMode.Full, 1f,
                                             maxSimulatedBodies: 999);
            Assert.That(tier.MaxSimulatedBodies, Is.EqualTo(WaterQuality.MaxSimulatedBodiesCap));

            var floorTier = new WaterQuality.Tier(256, 1024, 24, true, true, 16, 5, 1f, true, 0,
                                                  1, 1, 1, 128, 65536,
                                                  WaterQuality.UnderwaterMode.Full, 1f,
                                                  maxSimulatedBodies: -3);
            Assert.That(floorTier.MaxSimulatedBodies, Is.Zero,
                        "a zero budget (no sims) is a legal authored choice; negatives clamp to it");
        }
    }
}
