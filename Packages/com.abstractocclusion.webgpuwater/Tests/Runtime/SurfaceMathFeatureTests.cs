using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class SurfaceMathFeatureTests
    {
        const int FieldResolution = 2;
        const float HalfUv = 0.5f;
        const float SphereRadius = 2f;
        const float FullSubmersionDepth = 2f;
        const float DryDepth = -2f;
        const float WaveX = 3f;
        const float WaveZ = -5f;
        const float WaveTime = 1.25f;
        const float WaveAmplitudeScale = 1f;
        const float WindHeading = 0.4f;
        const float SwellWavelength = 30f;
        const float SwellHeight = 1f;
        const float NoChoppiness = 0f;

        static readonly Color BottomLeft = new Color(0f, 0f, 0f, 1f);
        static readonly Color BottomRight = new Color(1f, 0f, 0f, 1f);
        static readonly Color TopLeft = new Color(0f, 1f, 0f, 1f);
        static readonly Color TopRight = new Color(1f, 1f, 0f, 1f);

        [Test]
        public void ColorFieldSampling_UsesTheSameCentredBilinearConventionAsFloatFields()
        {
            Color[] field = { BottomLeft, BottomRight, TopLeft, TopRight };

            Color center = WaterFieldSampling.SampleBilinear(field, FieldResolution, HalfUv, HalfUv);
            Color outside = WaterFieldSampling.SampleBilinear(field, FieldResolution, -1f, 2f);

            Assert.That(center, Is.EqualTo(new Color(HalfUv, HalfUv, 0f, 1f)));
            Assert.That(outside, Is.EqualTo(TopLeft));
        }

        [Test]
        public void BuoyancySubmersionFraction_ClampsDryHalfAndFullSphereCases()
        {
            Assert.That(WaterBuoyancy.SphereSubmergedFraction(FullSubmersionDepth, SphereRadius), Is.EqualTo(1f));
            Assert.That(WaterBuoyancy.SphereSubmergedFraction(DryDepth, SphereRadius), Is.Zero);
            Assert.That(WaterBuoyancy.SphereSubmergedFraction(0f, SphereRadius), Is.EqualTo(HalfUv));
        }

        [Test]
        public void LargeWaveQuery_WithoutChoppinessMatchesItsSourceEvaluation()
        {
            ShoreWaveContext shore = ShoreWaveContext.Inactive;
            // Swell heading = wind heading here: the decoupled-heading default (offset 0).
            Vector3 source = LargeWaveField.Evaluate(
                WaveX, WaveZ, WaveTime, WaveAmplitudeScale, WindHeading, WindHeading, SwellWavelength,
                SwellHeight, shore);
            Vector3 query = LargeWaveField.EvaluateAtQuery(
                WaveX, WaveZ, WaveTime, WaveAmplitudeScale, WindHeading, WindHeading, SwellWavelength,
                SwellHeight, NoChoppiness, shore);
            Vector2 displacement = LargeWaveField.HorizontalDisplacementAtSource(
                WaveX, WaveZ, WaveTime, WaveAmplitudeScale, WindHeading, WindHeading, SwellWavelength,
                SwellHeight, NoChoppiness, shore);

            Assert.That(query, Is.EqualTo(source));
            Assert.That(displacement, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void LargeWaveQuery_ReturnsFiniteHeightSlopeAndVelocity()
        {
            ShoreWaveContext shore = ShoreWaveContext.Inactive;

            LargeWaveField.EvaluateAtQuery(
                WaveX, WaveZ, WaveTime, WaveAmplitudeScale, WindHeading, WindHeading, SwellWavelength,
                SwellHeight, WaveAmplitudeScale, shore, out Vector3 heightSlope, out float verticalVelocity);

            Assert.That(float.IsNaN(heightSlope.x) || float.IsInfinity(heightSlope.x), Is.False);
            Assert.That(float.IsNaN(heightSlope.y) || float.IsInfinity(heightSlope.y), Is.False);
            Assert.That(float.IsNaN(heightSlope.z) || float.IsInfinity(heightSlope.z), Is.False);
            Assert.That(float.IsNaN(verticalVelocity) || float.IsInfinity(verticalVelocity), Is.False);
        }
    }
}
