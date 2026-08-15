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
        const float Gravity = 9.81f;
        const float FloatStrength = 2.5f;
        const float GlueIntensity = 1f;
        const float VerticalWaveVelocity = 0.75f;
        const float EquilibriumFraction = 1f / FloatStrength;
        const float AboveEquilibriumFraction = 0.2f;
        const float BelowEquilibriumFraction = 0.6f;

        static readonly Color BottomLeft = new Color(0f, 0f, 0f, 1f);
        static readonly Color BottomRight = new Color(1f, 0f, 0f, 1f);
        static readonly Color TopLeft = new Color(0f, 1f, 0f, 1f);
        static readonly Color TopRight = new Color(1f, 1f, 0f, 1f);
        static readonly Vector3 SurfaceUp = Vector3.up;
        static readonly Vector3 SurfaceTilt = new Vector3(0.25f, 0f, -0.5f);
        static readonly Vector3 AuthoredCurrentVelocity = new Vector3(2f, 0f, 1f);

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
        public void SurfaceGlue_IsInertWhenDisabledOrAtEquilibrium()
        {
            float disabled = WaterBuoyancy.SurfaceGlueAcceleration(
                Gravity, FloatStrength, AboveEquilibriumFraction, 0f);
            float balanced = WaterBuoyancy.SurfaceGlueAcceleration(
                Gravity, FloatStrength, EquilibriumFraction, GlueIntensity);

            Assert.That(disabled, Is.Zero);
            Assert.That(balanced, Is.EqualTo(0f).Within(float.Epsilon));
        }

        [Test]
        public void SurfaceGlue_PullsDownAboveDraftAndPushesUpBelowDraft()
        {
            float aboveDraft = WaterBuoyancy.SurfaceGlueAcceleration(
                Gravity, FloatStrength, AboveEquilibriumFraction, GlueIntensity);
            float belowDraft = WaterBuoyancy.SurfaceGlueAcceleration(
                Gravity, FloatStrength, BelowEquilibriumFraction, GlueIntensity);

            Assert.That(aboveDraft, Is.LessThan(0f));
            Assert.That(belowDraft, Is.GreaterThan(0f));
        }

        [Test]
        public void SurfaceKinematics_WaveDriftCompatibilityMappingPreservesExistingResponse()
        {
            Vector3 waveDriftVelocity =
                WaterSurfaceKinematics.WaveDriftVelocityFromTilt(SurfaceTilt);

            Assert.That(waveDriftVelocity, Is.EqualTo(SurfaceTilt));
        }

        [Test]
        public void SurfaceKinematics_CurrentChangesVelocityWithoutChangingNormal()
        {
            Vector3 normalBeforeCurrent =
                WaterSurfaceKinematics.NormalFromTilt(SurfaceUp, SurfaceTilt);
            Vector3 velocityWithoutCurrent = WaterSurfaceKinematics.ComposeVelocity(
                SurfaceTilt, Vector3.zero, VerticalWaveVelocity);
            Vector3 velocityWithCurrent = WaterSurfaceKinematics.ComposeVelocity(
                SurfaceTilt, AuthoredCurrentVelocity, VerticalWaveVelocity);
            Vector3 normalAfterCurrent =
                WaterSurfaceKinematics.NormalFromTilt(SurfaceUp, SurfaceTilt);

            Assert.That(normalAfterCurrent, Is.EqualTo(normalBeforeCurrent));
            Assert.That(velocityWithCurrent - velocityWithoutCurrent,
                        Is.EqualTo(AuthoredCurrentVelocity));
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
