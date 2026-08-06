using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class QualityAndInteractionFeatureTests
    {
        [Test]
        public void QualityTier_SanitisesUnsafeValues()
        {
            var tier = new WaterQuality.Tier(1, 1, -1, false, false, 0, -1, 0f, false,
                                             -1, 0, 0, 99, 0, WaterQuality.UnderwaterMode.Off);

            Assert.That(tier.SimResolution, Is.EqualTo(WaterSimulation.ThreadGroupSize));
            Assert.That(tier.CausticResolution, Is.GreaterThanOrEqualTo(64));
            Assert.That(tier.GodRaySteps, Is.EqualTo(0));
            Assert.That(tier.RenderScale, Is.EqualTo(0.25f));
            Assert.That(tier.CausticInterval, Is.EqualTo(1));
            Assert.That(tier.OceanFftInterval, Is.EqualTo(4));
            Assert.That(tier.MaxFoamParticles, Is.GreaterThanOrEqualTo(64));
        }

        [Test]
        public void JerlovPresets_AreDefinedForEveryWaterType()
        {
            foreach (JerlovWaterType type in System.Enum.GetValues(typeof(JerlovWaterType)))
            {
                JerlovPreset preset = JerlovWaterTypes.Get(type);
                Assert.That(preset.DisplayName, Is.Not.Empty);
                Assert.That(preset.Extinction.r, Is.GreaterThanOrEqualTo(0f));
                Assert.That(preset.Extinction.g, Is.GreaterThanOrEqualTo(0f));
                Assert.That(preset.Extinction.b, Is.GreaterThanOrEqualTo(0f));
            }
        }

        [Test]
        public void WaterInteractable_SubmersionUsesTheRendererBottom()
        {
            var gameObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                gameObject.transform.localScale = Vector3.one * 2f;
                WaterInteractable interactable = gameObject.AddComponent<WaterInteractable>();

                Assert.That(interactable.IsSubmerged(-1f), Is.False);
                Assert.That(interactable.IsSubmerged(0f), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
