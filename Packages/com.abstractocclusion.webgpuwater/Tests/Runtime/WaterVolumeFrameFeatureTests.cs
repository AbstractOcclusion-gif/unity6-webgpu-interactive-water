using NUnit.Framework;
using System.Reflection;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterVolumeFrameFeatureTests
    {
        const float FloatTolerance = 0.0001f;
        const float OutsideFootprintOffset = 0.01f;
        const float RayOriginHeight = 10f;
        const float OutsideRayOffset = 1f;
        const string TestVolumeName = "Water Volume Frame Test";
        const string SceneLightCountName = "_WaterSceneLightCount";
        const string SceneLightSpotDirectionName = "_WaterSceneLightSpotDir";
        const string UnderwaterPointLightsKeyword = "WATER_FOG_POINT_LIGHTS";
        const float SpotlightOuterAngle = 60f;
        const float SpotlightInnerAngle = 30f;
        static readonly int VolumeCenterProperty = Shader.PropertyToID("_VolumeCenter");
        static readonly int SceneLightCountProperty = Shader.PropertyToID(SceneLightCountName);
        static readonly int SceneLightSpotDirectionProperty = Shader.PropertyToID(SceneLightSpotDirectionName);
        static readonly FieldInfo WaterFogSettingsField = typeof(WaterVolume).GetField(
            "waterFogSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Vector3 VolumePosition = new Vector3(10f, 2f, -4f);
        static readonly Vector3 SecondaryVolumePosition = new Vector3(-10f, 2f, 4f);
        static readonly Vector3 VolumeExtent = new Vector3(4f, 2f, 6f);
        static readonly Vector3 PoolPoint = new Vector3(0.25f, -0.5f, 0.75f);
        static readonly Quaternion VolumeRotation = Quaternion.Euler(0f, 35f, 0f);

        [Test]
        public void PoolAndWorldFrames_RoundTripWithNonUniformExtentAndRotation()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.transform.SetPositionAndRotation(VolumePosition, VolumeRotation);
                volume.volumeExtent = VolumeExtent;

                Vector3 worldPoint = volume.PoolToWorld(PoolPoint);
                Vector3 returnedPoolPoint = volume.WorldToPool(worldPoint);

                AssertVector3(returnedPoolPoint, PoolPoint);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void WorldToPoolXZ_RejectsOutsidePointsAndKeepsSurfaceCoordinates()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.volumeExtent = Vector3.one;

                Assert.That(volume.WorldToPoolXZ(new Vector3(1f, 0f, -1f), out float poolX, out float poolZ), Is.True);
                Assert.That(poolX, Is.EqualTo(1f));
                Assert.That(poolZ, Is.EqualTo(-1f));
                Assert.That(volume.WorldToPoolXZ(new Vector3(1f + OutsideFootprintOffset, 0f, 0f), out _, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RaycastSurface_OnlyAcceptsForwardRaysInsideTheFootprint()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.transform.position = VolumePosition;
                volume.volumeExtent = VolumeExtent;
                Ray insideRay = new Ray(VolumePosition + Vector3.up * RayOriginHeight, Vector3.down);
                Ray outsideRay = new Ray(VolumePosition + Vector3.right * (VolumeExtent.x + OutsideRayOffset) + Vector3.up, Vector3.down);
                Ray parallelRay = new Ray(VolumePosition + Vector3.up, Vector3.right);

                Assert.That(volume.TryRaycastSurface(insideRay, out Vector3 hit), Is.True);
                AssertVector3(hit, VolumePosition);
                Assert.That(volume.TryRaycastSurface(outsideRay, out _), Is.False);
                Assert.That(volume.TryRaycastSurface(parallelRay, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void BodyContaining_SelectsTheSecondaryBodyAtTheCameraPosition()
        {
            GameObject primaryHost = CreateInactiveVolume(out WaterVolume primary);
            GameObject secondaryHost = CreateInactiveVolume(out WaterVolume secondary);
            try
            {
                primary.transform.position = VolumePosition;
                secondary.transform.position = SecondaryVolumePosition;
                primary.volumeExtent = Vector3.one;
                secondary.volumeExtent = Vector3.one;
                WaterVolume.Bodies.Add(primary);
                WaterVolume.Bodies.Add(secondary);

                WaterVolume fogSource = WaterVolume.BodyContaining(SecondaryVolumePosition);

                Assert.That(fogSource, Is.EqualTo(secondary));
            }
            finally
            {
                WaterVolume.Bodies.Remove(primary);
                WaterVolume.Bodies.Remove(secondary);
                Object.DestroyImmediate(primaryHost);
                Object.DestroyImmediate(secondaryHost);
            }
        }

        [Test]
        public void InputRouter_ResolvesTheHitBodySplashEmitter()
        {
            GameObject primaryHost = CreateInactiveVolume(out WaterVolume primary);
            GameObject hitBodyHost = CreateInactiveVolume(out WaterVolume hitBody);
            try
            {
                WaterSplashEmitter primaryEmitter = primaryHost.AddComponent<WaterSplashEmitter>();
                WaterSplashEmitter hitBodyEmitter = hitBodyHost.AddComponent<WaterSplashEmitter>();
                primary.splashEmitter = primaryEmitter;
                hitBody.splashEmitter = hitBodyEmitter;

                WaterSplashEmitter resolvedEmitter = WaterInputRouter.ResolveHitSplashEmitter(hitBody);

                Assert.That(resolvedEmitter, Is.EqualTo(hitBodyEmitter));
                Assert.That(resolvedEmitter, Is.Not.EqualTo(primaryEmitter));
            }
            finally
            {
                Object.DestroyImmediate(primaryHost);
                Object.DestroyImmediate(hitBodyHost);
            }
        }

        [Test]
        public void WakeFoamDose_UsesWorldSpeedIndependentlyOfFrameDuration()
        {
            Vector2 shortFrameTravel = new Vector2(0.1f, 0f);
            Vector2 longFrameTravel = new Vector2(0.2f, 0f);

            float shortFrameDose = WaterSimulation.CalculateWakeFoamDose(shortFrameTravel, 0.02f);
            float longFrameDose = WaterSimulation.CalculateWakeFoamDose(longFrameTravel, 0.04f);
            float shortFrameCoverage = 1f - Mathf.Exp(-shortFrameDose);
            float longFrameCoverage = 1f - Mathf.Exp(-longFrameDose);

            Assert.That(longFrameDose, Is.EqualTo(shortFrameDose * 2f).Within(FloatTolerance));
            Assert.That(1f - (1f - shortFrameCoverage) * (1f - shortFrameCoverage),
                        Is.EqualTo(longFrameCoverage).Within(FloatTolerance));
        }

        [Test]
        public void FoamTransportScaling_ComposesAcrossSubsteps()
        {
            const float referenceStep = 1f;
            const float halfStep = 0.5f;
            const float authoredSpread = 0.4f;
            const float authoredAdvection = 2f;

            float fullStepSpread = WaterSimulation.CalculateFoamSpread(authoredSpread, referenceStep);
            float halfStepSpread = WaterSimulation.CalculateFoamSpread(authoredSpread, halfStep);
            float fullStepAdvection = WaterSimulation.CalculateFoamAdvection(authoredAdvection, referenceStep);
            float halfStepAdvection = WaterSimulation.CalculateFoamAdvection(authoredAdvection, halfStep);

            Assert.That(1f - (1f - halfStepSpread) * (1f - halfStepSpread),
                        Is.EqualTo(fullStepSpread).Within(FloatTolerance));
            Assert.That(halfStepAdvection * 2f, Is.EqualTo(fullStepAdvection).Within(FloatTolerance));
        }

        [Test]
        public void SelectedFogBody_PublishesItsOwnGlobalVolumeFrame()
        {
            GameObject primaryHost = CreateInactiveVolume(out WaterVolume primary);
            GameObject secondaryHost = CreateInactiveVolume(out WaterVolume secondary);
            Vector4 previousVolumeCenter = Shader.GetGlobalVector(VolumeCenterProperty);
            try
            {
                primary.transform.position = VolumePosition;
                secondary.transform.position = SecondaryVolumePosition;
                primary.volumeExtent = Vector3.one;
                secondary.volumeExtent = Vector3.one;
                WaterVolume.Bodies.Add(primary);
                WaterVolume.Bodies.Add(secondary);

                new WaterUniformPublisher(primary).PublishBodyGlobals();
                WaterVolume fogSource = WaterVolume.BodyContaining(SecondaryVolumePosition);
                new WaterUniformPublisher(fogSource).PublishBodyGlobals();

                AssertVector3(Shader.GetGlobalVector(VolumeCenterProperty), SecondaryVolumePosition);
            }
            finally
            {
                Shader.SetGlobalVector(VolumeCenterProperty, previousVolumeCenter);
                WaterVolume.Bodies.Remove(primary);
                WaterVolume.Bodies.Remove(secondary);
                Object.DestroyImmediate(primaryHost);
                Object.DestroyImmediate(secondaryHost);
            }
        }

        [Test]
        public void PointLightScatter_ArmsInFullModeAndClearsInSimpleMode()
        {
            GameObject volumeHost = CreateInactiveVolume(out WaterVolume volume);
            GameObject lightHost = new GameObject("Water Scatter Test Light");
            float previousLightCount = Shader.GetGlobalFloat(SceneLightCountProperty);
            bool pointLightsWereEnabled = Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword);
            try
            {
                Light light = lightHost.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = RayOriginHeight;
                light.intensity = 1f;

                WaterFogSettings(volume).lightScatter = 1f;
                var publisher = new WaterUniformPublisher(volume);
                publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);

                Assert.That(Shader.GetGlobalFloat(SceneLightCountProperty), Is.GreaterThan(0f));
                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.True);

                publisher.PublishUnderwater(0f, 0f, 0f, 1f, 0f, 0f);

                Assert.That(Shader.GetGlobalFloat(SceneLightCountProperty), Is.Zero);
                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.False);
            }
            finally
            {
                Shader.SetGlobalFloat(SceneLightCountProperty, previousLightCount);
                if (pointLightsWereEnabled) Shader.EnableKeyword(UnderwaterPointLightsKeyword);
                else Shader.DisableKeyword(UnderwaterPointLightsKeyword);
                Object.DestroyImmediate(lightHost);
                Object.DestroyImmediate(volumeHost);
            }
        }

        [Test]
        public void SpotlightScatter_PublishesTheSpotConeDirection()
        {
            GameObject volumeHost = CreateInactiveVolume(out WaterVolume volume);
            GameObject lightHost = new GameObject("Water Scatter Test Spotlight");
            try
            {
                Light light = lightHost.AddComponent<Light>();
                light.type = LightType.Spot;
                light.spotAngle = SpotlightOuterAngle;
                light.innerSpotAngle = SpotlightInnerAngle;
                light.range = RayOriginHeight;
                light.intensity = 1f;
                light.transform.rotation = Quaternion.identity;

                WaterFogSettings(volume).lightScatter = 1f;
                new WaterUniformPublisher(volume).PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);

                Vector4[] spotDirections = Shader.GetGlobalVectorArray(SceneLightSpotDirectionProperty);
                Assert.That(spotDirections.Length, Is.GreaterThan(0));
                AssertVector3(spotDirections[0], Vector3.forward);
                Assert.That(spotDirections[0].w, Is.GreaterThan(0f));
            }
            finally
            {
                Shader.DisableKeyword(UnderwaterPointLightsKeyword);
                Shader.SetGlobalFloat(SceneLightCountProperty, 0f);
                Object.DestroyImmediate(lightHost);
                Object.DestroyImmediate(volumeHost);
            }
        }

        static GameObject CreateInactiveVolume(out WaterVolume volume)
        {
            var host = new GameObject(TestVolumeName);
            host.SetActive(false);
            volume = host.AddComponent<WaterVolume>();
            return host;
        }

        static WaterVolume.WaterFogSettings WaterFogSettings(WaterVolume volume)
        {
            Assert.That(WaterFogSettingsField, Is.Not.Null);
            return (WaterVolume.WaterFogSettings)WaterFogSettingsField.GetValue(volume);
        }

        static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }

        static void AssertVector3(Vector4 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }
    }
}
