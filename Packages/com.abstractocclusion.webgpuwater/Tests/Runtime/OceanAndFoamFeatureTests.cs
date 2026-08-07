using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class OceanAndFoamFeatureTests
    {
        const float PeakWavelength = 10f;
        const int CascadeCount = 4;
        const float ExpectedCoarsestBand = 20f;
        const float WaveNumber = 1f;
        const float MatchingSwellWavelength = 2f * Mathf.PI;
        const float PositiveHeight = 1.5f;
        const float PositiveSwellHeight = 0.5f;
        const float PeakSharpness = 3.3f;
        const int NormalizationResolution = 8;
        const int NormalizationCascades = 1;
        const float NormalizationDomain = 40f;
        const float NormalizationBandMinimum = 1f;
        const float NormalizationBandMaximum = 40f;
        const float FlipbookFramesPerSecond = 12f;
        const float PropertyFloatTolerance = 0.0001f;

        static readonly int FlipbookGridProperty = Shader.PropertyToID("_ParticleFlipbookGrid");
        static readonly int FlipbookFpsProperty = Shader.PropertyToID("_ParticleFlipbookFps");
        static readonly int TintProperty = Shader.PropertyToID("_Tint");
        static readonly int ParticleOpacityProperty = Shader.PropertyToID("_ParticleOpacity");
        static readonly int DensityLowGainProperty = Shader.PropertyToID("_DensityLowGain");
        static readonly int DensityHighGainProperty = Shader.PropertyToID("_DensityHighGain");
        static readonly int BreakupTilingProperty = Shader.PropertyToID("_BreakupTiling");
        static readonly int BreakupStrengthProperty = Shader.PropertyToID("_BreakupStrength");

        [Test]
        public void DeriveCascadeBands_ReturnsAscendingBandsEndingAtTheCoarsestPeakLimit()
        {
            float[] bands = WaterOceanSpectrum.DeriveCascadeBands(PeakWavelength, CascadeCount);

            Assert.That(bands, Has.Length.EqualTo(CascadeCount));
            Assert.That(bands[CascadeCount - 1], Is.EqualTo(ExpectedCoarsestBand));
            for (int index = 1; index < bands.Length; index++)
                Assert.That(bands[index], Is.GreaterThan(bands[index - 1]));
        }

        [Test]
        public void SpectrumShapes_IgnoreZeroWaveNumberAndKeepSwellDirectional()
        {
            float peakAngularFrequency = WaterOceanSpectrum.PeakAngularFrequency(PeakWavelength);
            float jonswap = WaterOceanSpectrum.JonswapOmni(WaveNumber, peakAngularFrequency, PeakSharpness, 0f);
            float alignedSwell = WaterOceanSpectrum.SwellShape(
                Vector2.right, WaveNumber, MatchingSwellWavelength, Vector2.right);
            float opposingSwell = WaterOceanSpectrum.SwellShape(
                Vector2.left, WaveNumber, MatchingSwellWavelength, Vector2.right);

            Assert.That(WaterOceanSpectrum.JonswapOmni(0f, peakAngularFrequency, PeakSharpness, 0f), Is.Zero);
            Assert.That(WaterOceanSpectrum.SwellShape(Vector2.zero, 0f, MatchingSwellWavelength, Vector2.right), Is.Zero);
            Assert.That(jonswap, Is.GreaterThan(0f));
            Assert.That(alignedSwell, Is.GreaterThan(0f));
            Assert.That(opposingSwell, Is.Zero);
        }

        [Test]
        public void ComputeGains_OnlyProducesGainForAuthoredAndRepresentedEnergy()
        {
            var layout = new WaterOceanSpectrum.Layout(
                NormalizationResolution,
                NormalizationCascades,
                new Vector4(NormalizationDomain, 0f, 0f, 0f),
                new Vector4(NormalizationBandMinimum, 0f, 0f, 0f),
                new Vector4(NormalizationBandMaximum, 0f, 0f, 0f));
            float peakAngularFrequency = WaterOceanSpectrum.PeakAngularFrequency(PeakWavelength);
            var flatSea = new WaterOceanSpectrum.SeaState(0f, peakAngularFrequency, PeakSharpness, 0f, 0f, PeakWavelength);
            var authoredSea = new WaterOceanSpectrum.SeaState(
                PositiveHeight, peakAngularFrequency, PeakSharpness, 0f, PositiveSwellHeight, PeakWavelength);

            WaterOceanSpectrum.ComputeGains(layout, flatSea, out float flatWindGain, out float flatSwellGain);
            WaterOceanSpectrum.ComputeGains(layout, authoredSea, out float windGain, out float swellGain);

            Assert.That(flatWindGain, Is.Zero);
            Assert.That(flatSwellGain, Is.Zero);
            Assert.That(windGain, Is.GreaterThan(0f));
            Assert.That(swellGain, Is.GreaterThan(0f));
        }

        [Test]
        public void FoamPropertyWriters_ApplyConfiguredLookVeilAndSafeFlipbookGrid()
        {
            var profile = ScriptableObject.CreateInstance<WaterFoamProfile>();
            var properties = new MaterialPropertyBlock();
            var layerProperties = new MaterialPropertyBlock();
            Color tint = new Color(0.1f, 0.2f, 0.3f, 0.4f);
            try
            {
                profile.look.tint = tint;
                profile.look.opacity = 0.7f;
                profile.veil.opacity = 0.4f;
                profile.veil.densityLowGain = 0.6f;
                profile.veil.densityHighGain = 0.2f;
                profile.veil.breakupTiling = 5f;
                profile.veil.breakupStrength = 0.3f;

                profile.WriteLook(properties);
                profile.WriteLook(layerProperties, 0.5f);
                profile.WriteVeil(properties);
                WaterParticlePool.WriteFlipbook(properties, new Vector2Int(0, -1), FlipbookFramesPerSecond);

                AssertColor(properties.GetColor(TintProperty), tint);
                Assert.That(properties.GetFloat(ParticleOpacityProperty), Is.EqualTo(profile.veil.opacity));
                Assert.That(layerProperties.GetFloat(ParticleOpacityProperty), Is.EqualTo(0.35f));
                Assert.That(properties.GetFloat(DensityLowGainProperty), Is.EqualTo(profile.veil.densityLowGain));
                Assert.That(properties.GetFloat(DensityHighGainProperty), Is.EqualTo(profile.veil.densityHighGain));
                Assert.That(properties.GetFloat(BreakupTilingProperty), Is.EqualTo(profile.veil.breakupTiling));
                Assert.That(properties.GetFloat(BreakupStrengthProperty), Is.EqualTo(profile.veil.breakupStrength));
                Assert.That(properties.GetVector(FlipbookGridProperty), Is.EqualTo(new Vector4(1f, 1f, 0f, 0f)));
                Assert.That(properties.GetFloat(FlipbookFpsProperty), Is.EqualTo(FlipbookFramesPerSecond));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        static void AssertColor(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(PropertyFloatTolerance));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(PropertyFloatTolerance));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(PropertyFloatTolerance));
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(PropertyFloatTolerance));
        }
    }
}
