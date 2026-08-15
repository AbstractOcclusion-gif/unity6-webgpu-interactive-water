using NUnit.Framework;
using System.Reflection;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

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
        const string GodRayPointLightsKeyword = "WATER_GODRAY_POINT_LIGHTS";
        const string RippleCrestFleckProfileTestName = "Ripple Crest Fleck Profile Test";
        const string RippleCrestFleckDefaultsTestName = "Ripple Crest Fleck Defaults Test";
        const string SimulationDrivenSpawningDefaultsTestName = "Simulation Driven Spawning Defaults Test";
        const string DensitySurfaceSizeScaleProfileTestName = "Density Surface Size Scale Profile Test";
        const float SpotlightOuterAngle = 60f;
        const float SpotlightInnerAngle = 30f;
        const int SplashAtlasColumns = 4;
        const int SplashAtlasRows = 1;
        const float DefaultWaterParticleGravity = 1f;
        const float UnityGravityMetersPerSecondSquared = 9.81f;
        const float JetGravityModifier = DefaultWaterParticleGravity / UnityGravityMetersPerSecondSquared;
        const float JetStretchLengthScale = 4f;
        const float DisabledStreakAmount = 0f;
        const float DefaultStreakAmount = 1f;
        const float ReducedCrownLaunchHeight = 0.5f;
        const float ReducedCrownLaunchSpread = 0.25f;
        const float GenericFoamSpawnThreshold = 0.35f;
        const float GenericFoamSpawnRate = 12f;
        const float RippleCrestFleckAmount = 3.5f;
        const int RippleCrestFleckMaxPerFrame = 128;
        static readonly Vector2 RippleCrestFleckLifetimeRange = new Vector2(0.4f, 0.8f);
        static readonly Vector2 RippleCrestFleckSizeRange = new Vector2(0.01f, 0.025f);
        const float RippleCrestFleckMotion = 0.6f;
        const float DensitySurfaceSizeScale = 0.35f;
        const int FlowGradientResolution = 256;
        const float FlowGradientVerticalExtent = 2f;
        const float ExpectedFlowGradientX = 32f;
        const float ExpectedFlowGradientZ = 16f;
        const int FlowTextureResolution = 8;
        const float SettledActivity = 1f;
        const float ActiveActivity = 1.0001f;
        const float SleepTestThreshold = 1f;
        const float SleepTestDropCoordinate = 0f;
        const float SleepTestDropRadius = 0.1f;
        const float SleepTestDropStrength = 0.1f;
        const string WaterSimComputePath =
            "Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterSim.compute";
        static readonly Vector2 FlowGradientHalfExtent = new Vector2(4f, 8f);
        static readonly int VolumeCenterProperty = Shader.PropertyToID("_VolumeCenter");
        static readonly int SceneLightCountProperty = Shader.PropertyToID(SceneLightCountName);
        static readonly int SceneLightSpotDirectionProperty = Shader.PropertyToID(SceneLightSpotDirectionName);
        static readonly FieldInfo WaterFogSettingsField = typeof(WaterVolume).GetField(
            "waterFogSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo OceanSettingsField = typeof(WaterVolume).GetField(
            "ocean", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo WindowedField = typeof(WaterVolume).GetField(
            "_windowed", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly Vector3 VolumePosition = new Vector3(10f, 2f, -4f);
        static readonly Vector3 SecondaryVolumePosition = new Vector3(-10f, 2f, 4f);
        static readonly Vector3 VolumeExtent = new Vector3(4f, 2f, 6f);
        static readonly Vector3 PoolPoint = new Vector3(0.25f, -0.5f, 0.75f);
        static readonly Quaternion VolumeRotation = Quaternion.Euler(0f, 35f, 0f);

        [SetUp]
        public void SetUp() => WaterUniformPublisher.ResetStaticState();

        [TearDown]
        public void TearDown() => WaterUniformPublisher.ResetStaticState();

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
        public void SplashJets_AreConfiguredAsIndependentStretchedAtlasParticles()
        {
            GameObject host = new GameObject("Water Splash Jet Test");
            try
            {
                ParticleSystem particles = host.AddComponent<ParticleSystem>();
                WaterSplashEmitter.ConfigureJets(particles, SplashAtlasColumns, SplashAtlasRows);

                Assert.That(particles.main.simulationSpace, Is.EqualTo(ParticleSystemSimulationSpace.World));
                Assert.That(particles.main.gravityModifier.constant,
                    Is.EqualTo(JetGravityModifier).Within(FloatTolerance));
                Assert.That(particles.emission.enabled, Is.False);
                Assert.That(particles.shape.enabled, Is.False);
                Assert.That(particles.textureSheetAnimation.numTilesX, Is.EqualTo(SplashAtlasColumns));
                Assert.That(particles.textureSheetAnimation.numTilesY, Is.EqualTo(SplashAtlasRows));
                ParticleSystemRenderer renderer = host.GetComponent<ParticleSystemRenderer>();
                Assert.That(renderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Stretch));
                Assert.That(renderer.lengthScale, Is.EqualTo(JetStretchLengthScale).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SplashProfile_AppliesCrownAndEntryStreakControlsToEmitter()
        {
            GameObject host = new GameObject("Water Splash Streak Profile Test");
            WaterFoamProfile profile = ScriptableObject.CreateInstance<WaterFoamProfile>();
            try
            {
                WaterSplashEmitter emitter = host.AddComponent<WaterSplashEmitter>();
                profile.splash.crownLaunchHeight = ReducedCrownLaunchHeight;
                profile.splash.crownLaunchSpread = ReducedCrownLaunchSpread;
                profile.splash.entryStreaksEnabled = false;
                profile.splash.entryStreakAmount = DisabledStreakAmount;

                profile.ApplyTo(emitter);

                Assert.That(emitter.entryStreaksEnabled, Is.False);
                Assert.That(emitter.crownLaunchHeight,
                    Is.EqualTo(ReducedCrownLaunchHeight).Within(FloatTolerance));
                Assert.That(emitter.crownLaunchSpread,
                    Is.EqualTo(ReducedCrownLaunchSpread).Within(FloatTolerance));
                Assert.That(emitter.entryStreakAmount,
                    Is.EqualTo(DisabledStreakAmount).Within(FloatTolerance));
                Assert.That(emitter.entryStreakHeight, Is.EqualTo(DefaultStreakAmount).Within(FloatTolerance));
                Assert.That(emitter.entryStreakWidth, Is.EqualTo(DefaultStreakAmount).Within(FloatTolerance));
                Assert.That(emitter.entryStreakGravity, Is.EqualTo(DefaultStreakAmount).Within(FloatTolerance));
                Assert.That(emitter.entryStreakLifetimeRange,
                    Is.EqualTo(profile.splash.entryStreakLifetimeRange));
                Assert.That(emitter.entryStreakSizeRange,
                    Is.EqualTo(profile.splash.entryStreakSizeRange));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void FoamProfile_AppliesRippleCrestFleckControlsToParticlePool()
        {
            GameObject host = CreateInactiveFoamParticles(
                RippleCrestFleckProfileTestName, out WaterFoamParticles particles);
            WaterFoamProfile profile = ScriptableObject.CreateInstance<WaterFoamProfile>();
            try
            {
                profile.ambient.spawnThreshold = GenericFoamSpawnThreshold;
                profile.ambient.spawnRate = GenericFoamSpawnRate;
                profile.ambient.rippleCrestFlecksEnabled = true;
                profile.ambient.rippleCrestFleckAmount = RippleCrestFleckAmount;
                profile.ambient.rippleCrestFleckMaxPerFrame = RippleCrestFleckMaxPerFrame;
                profile.ambient.rippleCrestFleckLifetimeRange = RippleCrestFleckLifetimeRange;
                profile.ambient.rippleCrestFleckSizeRange = RippleCrestFleckSizeRange;
                profile.ambient.rippleCrestFleckMotion = RippleCrestFleckMotion;

                profile.ApplyTo(particles);

                Assert.That(particles.rippleCrestFlecksEnabled, Is.True);
                Assert.That(particles.rippleCrestFleckAmount,
                    Is.EqualTo(RippleCrestFleckAmount).Within(FloatTolerance));
                Assert.That(particles.rippleCrestFleckMaxPerFrame,
                    Is.EqualTo(RippleCrestFleckMaxPerFrame));
                Assert.That(particles.rippleCrestFleckLifetimeRange,
                    Is.EqualTo(RippleCrestFleckLifetimeRange));
                Assert.That(particles.rippleCrestFleckSizeRange,
                    Is.EqualTo(RippleCrestFleckSizeRange));
                Assert.That(particles.rippleCrestFleckMotion,
                    Is.EqualTo(RippleCrestFleckMotion).Within(FloatTolerance));
                Assert.That(particles.spawnThreshold,
                    Is.EqualTo(GenericFoamSpawnThreshold).Within(FloatTolerance));
                Assert.That(particles.spawnRate,
                    Is.EqualTo(GenericFoamSpawnRate).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void HorizontalFlowGradient_UsesTheBodyFrameOnEachAxis()
        {
            Vector2 gradient = WaterSimulation.CalculateHorizontalFlowGradient(
                FlowGradientHalfExtent, FlowGradientVerticalExtent, FlowGradientResolution);

            Assert.That(gradient.x, Is.EqualTo(ExpectedFlowGradientX).Within(FloatTolerance));
            Assert.That(gradient.y, Is.EqualTo(ExpectedFlowGradientZ).Within(FloatTolerance));
        }

        [Test]
        public void RippleSleepActivity_SleepsOnlyAtOrBelowTheNormalizedThreshold()
        {
            Assert.That(WaterSimulation.IsSettledActivity(0f), Is.True);
            Assert.That(WaterSimulation.IsSettledActivity(SettledActivity), Is.True);
            Assert.That(WaterSimulation.IsSettledActivity(ActiveActivity), Is.False);
        }

        [Test]
        public void RippleSleepActivity_FailsClosedForNonFiniteGpuResults()
        {
            Assert.That(WaterSimulation.IsSettledActivity(float.NaN), Is.False);
            Assert.That(WaterSimulation.IsSettledActivity(float.PositiveInfinity), Is.False);
            Assert.That(WaterSimulation.IsSettledActivity(float.NegativeInfinity), Is.False);
        }

        [Test]
        public void ParticleDefaults_KeepRippleCrestFlecksDisabled()
        {
            GameObject host = CreateInactiveFoamParticles(
                RippleCrestFleckDefaultsTestName, out WaterFoamParticles particles);
            try
            {
                Assert.That(particles.rippleCrestFlecksEnabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ParticleDefaults_KeepSimulationDrivenSpawningDisabled()
        {
            GameObject host = CreateInactiveFoamParticles(
                SimulationDrivenSpawningDefaultsTestName, out WaterFoamParticles particles);
            try
            {
                Assert.That(particles.simulationDrivenSpawning, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void FoamProfile_AppliesDensitySurfaceSizeScaleToParticlePool()
        {
            GameObject host = CreateInactiveFoamParticles(
                DensitySurfaceSizeScaleProfileTestName, out WaterFoamParticles particles);
            WaterFoamProfile profile = ScriptableObject.CreateInstance<WaterFoamProfile>();
            try
            {
                profile.veil.drive = true;
                profile.veil.surfaceSizeScale = DensitySurfaceSizeScale;

                profile.ApplyTo(particles);

                Assert.That(particles.densitySurfaceSizeScale,
                    Is.EqualTo(DensitySurfaceSizeScale).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(host);
            }
        }

#if UNITY_EDITOR
        [Test]
        public void HorizontalFlowTextures_ArePerBodyRgFloatPingPongTargets()
        {
            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(WaterSimComputePath);
            Assert.That(compute, Is.Not.Null);

            var first = new WaterSimulation(compute, FlowTextureResolution);
            var second = new WaterSimulation(compute, FlowTextureResolution);
            try
            {
                Assert.That(first.HorizontalFlowTexture, Is.Not.Null);
                Assert.That(first.HorizontalFlowTexture.format, Is.EqualTo(RenderTextureFormat.RGFloat));
                Assert.That(first.HorizontalFlowTexture.width, Is.EqualTo(FlowTextureResolution));
                Assert.That(first.HorizontalFlowTexture, Is.Not.SameAs(first.Texture));
                Assert.That(first.HorizontalFlowTexture, Is.Not.SameAs(second.HorizontalFlowTexture));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void RippleSleepReadback_ClearsOnlyAnUnchangedSettledGeneration()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("This graphics backend does not support the async activity readback.");

            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(WaterSimComputePath);
            Assert.That(compute, Is.Not.Null);
            var settled = new WaterSimulation(compute, FlowTextureResolution);
            var reinjected = new WaterSimulation(compute, FlowTextureResolution);
            try
            {
                settled.AddDrop(SleepTestDropCoordinate, SleepTestDropCoordinate,
                                SleepTestDropRadius, SleepTestDropStrength);
                settled.RequestSleepCheck(SleepTestThreshold, SleepTestThreshold, SleepTestThreshold,
                                          SleepTestThreshold, SleepTestThreshold);

                reinjected.AddDrop(SleepTestDropCoordinate, SleepTestDropCoordinate,
                                   SleepTestDropRadius, SleepTestDropStrength);
                reinjected.RequestSleepCheck(SleepTestThreshold, SleepTestThreshold, SleepTestThreshold,
                                             SleepTestThreshold, SleepTestThreshold);
                reinjected.AddDrop(SleepTestDropCoordinate, SleepTestDropCoordinate,
                                   SleepTestDropRadius, SleepTestDropStrength);

                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();

                Assert.That(settled.HasReceivedInjection, Is.False);
                Assert.That(reinjected.HasReceivedInjection, Is.True);
            }
            finally
            {
                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
                settled.Dispose();
                reinjected.Dispose();
            }
        }
#endif

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
        public void PointLightScatter_ArmsOnlyRequestedVariantsAndClearsWithoutEligibleLights()
        {
            GameObject volumeHost = CreateInactiveVolume(out WaterVolume volume);
            GameObject lightHost = new GameObject("Water Scatter Test Light");
            float previousLightCount = Shader.GetGlobalFloat(SceneLightCountProperty);
            bool fogPointLightsWereEnabled = Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword);
            bool godRayPointLightsWereEnabled = Shader.IsKeywordEnabled(GodRayPointLightsKeyword);
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
                Assert.That(Shader.IsKeywordEnabled(GodRayPointLightsKeyword), Is.False);

                light.type = LightType.Directional;
                publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);

                Assert.That(Shader.GetGlobalFloat(SceneLightCountProperty), Is.Zero);
                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.False);
                Assert.That(Shader.IsKeywordEnabled(GodRayPointLightsKeyword), Is.False);

                light.type = LightType.Point;
                WaterFogSettings(volume).lightScatter = 0f;
                WaterVolume.OceanSettings oceanSettings = OceanSettings(volume);
                oceanSettings.openWater = true;
                oceanSettings.unboundedOcean = true;
                oceanSettings.largeGodRayDensity = 1f;
                oceanSettings.largeGodRayLightScatter = 1f;
                Assert.That(WindowedField, Is.Not.Null);
                WindowedField.SetValue(volume, true);
                publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);

                Assert.That(Shader.GetGlobalFloat(SceneLightCountProperty), Is.GreaterThan(0f));
                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.False);
                Assert.That(Shader.IsKeywordEnabled(GodRayPointLightsKeyword), Is.True);

                WaterFogSettings(volume).lightScatter = 1f;
                publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);

                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.True);
                Assert.That(Shader.IsKeywordEnabled(GodRayPointLightsKeyword), Is.True);

                publisher.PublishUnderwater(0f, 0f, 0f, 1f, 0f, 0f);

                Assert.That(Shader.GetGlobalFloat(SceneLightCountProperty), Is.Zero);
                Assert.That(Shader.IsKeywordEnabled(UnderwaterPointLightsKeyword), Is.False);
                Assert.That(Shader.IsKeywordEnabled(GodRayPointLightsKeyword), Is.False);
            }
            finally
            {
                Shader.SetGlobalFloat(SceneLightCountProperty, previousLightCount);
                if (fogPointLightsWereEnabled) Shader.EnableKeyword(UnderwaterPointLightsKeyword);
                else Shader.DisableKeyword(UnderwaterPointLightsKeyword);
                if (godRayPointLightsWereEnabled) Shader.EnableKeyword(GodRayPointLightsKeyword);
                else Shader.DisableKeyword(GodRayPointLightsKeyword);
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

        static GameObject CreateInactiveFoamParticles(
            string hostName, out WaterFoamParticles particles)
        {
            var host = new GameObject(hostName);
            host.SetActive(false);
            particles = host.AddComponent<WaterFoamParticles>();
            return host;
        }

        static WaterVolume.WaterFogSettings WaterFogSettings(WaterVolume volume)
        {
            Assert.That(WaterFogSettingsField, Is.Not.Null);
            return (WaterVolume.WaterFogSettings)WaterFogSettingsField.GetValue(volume);
        }

        static WaterVolume.OceanSettings OceanSettings(WaterVolume volume)
        {
            Assert.That(OceanSettingsField, Is.Not.Null);
            return (WaterVolume.OceanSettings)OceanSettingsField.GetValue(volume);
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
