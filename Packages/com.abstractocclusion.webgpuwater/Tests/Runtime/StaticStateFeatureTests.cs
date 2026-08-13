using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class StaticStateFeatureTests
    {
        const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        const int InvalidFrame = -1;
        const int StaleFrame = 42;
        const int TestCubemapSize = 1;
        const float StaleRefreshTime = 120f;
        const string SplashObjectName = "Fast Enter Play Splash";
        const string ReflectionBudgetFrameField = "_budgetFrame";
        const string ReflectionCandidatesField = "_candidates";
        const string ReflectionGrantedField = "_granted";
        const string SceneLightsField = "s_SceneLights";
        const string SceneLightRefreshField = "s_SceneLightCacheRefreshAt";
        const string SkyboxCubeField = "_skyboxCube";
        const string SkyboxFrameField = "_skyboxCubeFrame";

        [SetUp]
        public void SetUp() => WaterVolume.ResetStaticState();

        [TearDown]
        public void TearDown() => WaterVolume.ResetStaticState();

        [Test]
        public void ResetStaticState_ClearsLiveRendererRegistries()
        {
            WaterFoamParticles.Live.Add(null);
            WaterSplashEmitter.Live.Add(null);
            WaterFogTransparent.Live.Add(null);

            WaterVolume.ResetStaticState();

            Assert.That(WaterFoamParticles.Live, Is.Empty);
            Assert.That(WaterSplashEmitter.Live, Is.Empty);
            Assert.That(WaterFogTransparent.Live, Is.Empty);
        }

        [Test]
        public void ResetStaticState_ClearsFrameAndSceneCaches()
        {
            var staleCubemap = new Cubemap(TestCubemapSize, TextureFormat.RGBA32, false);
            try
            {
                SetPrivateStaticField(typeof(WaterReflections), ReflectionBudgetFrameField, StaleFrame);
                PrivateStaticField<List<WaterVolume>>(typeof(WaterReflections), ReflectionCandidatesField)
                    .Add(null);
                PrivateStaticField<HashSet<WaterVolume>>(typeof(WaterReflections), ReflectionGrantedField)
                    .Add(null);
                PrivateStaticField<List<Light>>(typeof(WaterUniformPublisher), SceneLightsField).Add(null);
                SetPrivateStaticField(typeof(WaterUniformPublisher), SceneLightRefreshField, StaleRefreshTime);
                SetPrivateStaticField(typeof(WaterUniformPublisher), SkyboxCubeField, staleCubemap);
                SetPrivateStaticField(typeof(WaterUniformPublisher), SkyboxFrameField, StaleFrame);

                WaterVolume.ResetStaticState();

                Assert.That(PrivateStaticField<int>(typeof(WaterReflections), ReflectionBudgetFrameField),
                            Is.EqualTo(InvalidFrame));
                Assert.That(PrivateStaticField<List<WaterVolume>>(
                                typeof(WaterReflections), ReflectionCandidatesField), Is.Empty);
                Assert.That(PrivateStaticField<HashSet<WaterVolume>>(
                                typeof(WaterReflections), ReflectionGrantedField), Is.Empty);
                Assert.That(PrivateStaticField<List<Light>>(
                                typeof(WaterUniformPublisher), SceneLightsField), Is.Empty);
                Assert.That(PrivateStaticField<float>(
                                typeof(WaterUniformPublisher), SceneLightRefreshField), Is.Zero);
                Assert.That(PrivateStaticField<Cubemap>(
                                typeof(WaterUniformPublisher), SkyboxCubeField), Is.Null);
                Assert.That(PrivateStaticField<int>(
                                typeof(WaterUniformPublisher), SkyboxFrameField), Is.EqualTo(InvalidFrame));
            }
            finally
            {
                Object.DestroyImmediate(staleCubemap);
            }
        }

        [Test]
        public void ResetStaticState_ReleasesRenderersMutedByAfterFogRerouting()
        {
            var transparentObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var splashObject = new GameObject(SplashObjectName);
            try
            {
                Renderer transparentRenderer = transparentObject.GetComponent<Renderer>();
                transparentObject.AddComponent<WaterFogTransparent>();

                ParticleSystem particles = splashObject.AddComponent<ParticleSystem>();
                var emitter = splashObject.AddComponent<WaterSplashEmitter>();
                emitter.particles = particles;
                ParticleSystemRenderer particleRenderer = particles.GetComponent<ParticleSystemRenderer>();

                transparentRenderer.forceRenderingOff = true;
                particleRenderer.forceRenderingOff = true;

                WaterVolume.ResetStaticState();

                Assert.That(transparentRenderer.forceRenderingOff, Is.False);
                Assert.That(particleRenderer.forceRenderingOff, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(transparentObject);
                Object.DestroyImmediate(splashObject);
            }
        }

        static T PrivateStaticField<T>(System.Type owner, string fieldName)
        {
            FieldInfo field = owner.GetField(fieldName, PrivateStatic);
            Assert.That(field, Is.Not.Null, $"Missing static field {owner.FullName}.{fieldName}");
            return (T)field.GetValue(null);
        }

        static void SetPrivateStaticField<T>(System.Type owner, string fieldName, T value)
        {
            FieldInfo field = owner.GetField(fieldName, PrivateStatic);
            Assert.That(field, Is.Not.Null, $"Missing static field {owner.FullName}.{fieldName}");
            field.SetValue(null, value);
        }
    }
}
