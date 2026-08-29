// WebGpuWater - independent fog quality acceptance test (2026-08-29 round, case 17).
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterFogQualityFeatureTests
    {
        const float OverrideSolveScale = 0.5f;

        WaterQuality _asset;

        [SetUp]
        public void SetUp() => _asset = ScriptableObject.CreateInstance<WaterQuality>();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_asset);

        [Test]
        public void FollowTier_ResolvesExactlyTheTierFogFields()
        {
            _asset.selection = WaterQuality.Selection.ForceLow;
            WaterQuality.Tier lowTier = _asset.Resolve();

            Assert.That(lowTier.UnderwaterFog, Is.EqualTo(WaterQuality.UnderwaterMode.Simple),
                        "Low ships Simple fog - the coupling this feature exists to break");
            Assert.That(_asset.ResolveFogMode(in lowTier), Is.EqualTo(lowTier.UnderwaterFog));
            Assert.That(_asset.ResolveFogSolveScale(in lowTier), Is.EqualTo(lowTier.FogSolveScale));
        }

        [Test]
        public void Override_PromotesFogIndependentlyOfTheGlobalTier()
        {
            _asset.selection = WaterQuality.Selection.ForceLow;
            _asset.ConfigureFogOverride(WaterQuality.FogQualitySource.Override,
                                        WaterQuality.UnderwaterMode.Full, OverrideSolveScale);
            WaterQuality.Tier lowTier = _asset.Resolve();

            // The tier still carries every OTHER Low budget (sim res, FFT, foam, mesh...);
            // only the fog fields answer with the override - fog quality is now a free axis.
            Assert.That(_asset.ResolveFogMode(in lowTier),
                        Is.EqualTo(WaterQuality.UnderwaterMode.Full));
            Assert.That(_asset.ResolveFogSolveScale(in lowTier), Is.EqualTo(OverrideSolveScale));
            Assert.That(lowTier.UnderwaterFog, Is.EqualTo(WaterQuality.UnderwaterMode.Simple),
                        "the tier itself is untouched - the override lives beside it");
        }

        [Test]
        public void Override_SanitisesTheSolveScale()
        {
            _asset.ConfigureFogOverride(WaterQuality.FogQualitySource.Override,
                                        WaterQuality.UnderwaterMode.Full, 0f);
            WaterQuality.Tier tier = _asset.Resolve();
            Assert.That(_asset.ResolveFogSolveScale(in tier),
                        Is.EqualTo(WaterQuality.MinFogSolveScale));
        }
    }
}
