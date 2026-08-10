using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class ShoreUniformFeatureTests
    {
        const float FirstRefraction = 0.2f;
        const float SecondRefraction = 0.8f;
        const float FirstSurfPeriod = 3f;
        const float SecondSurfPeriod = 7f;
        const float ShortFetchNormalized = 0.01f;
        const float LongFetchNormalized = 0.5f;
        const float RippleWavelengthMeters = 1f;
        const float LongWaveWavelengthMeters = 20f;
        const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const string BedDepthSettingsFieldName = "bedDepthSettings";
        static readonly int ShoreRefractionProperty = Shader.PropertyToID("_ShoreRefraction");
        static readonly int SurfPeriodProperty = Shader.PropertyToID("_SurfPeriod");
        static readonly int ShoreDepthValidProperty = Shader.PropertyToID("_ShoreDepthValid");
        static readonly int ShoreDepthTextureProperty = Shader.PropertyToID("_ShoreDepthTex");

        [Test]
        public void ShoreUniforms_WriteDistinctBodyValuesThroughIndependentSinks()
        {
            GameObject firstObject = CreateInactiveVolume("First Shore", out WaterVolume first);
            GameObject secondObject = CreateInactiveVolume("Second Shore", out WaterVolume second);
            try
            {
                ConfigureShoreSettings(first, FirstRefraction, FirstSurfPeriod);
                ConfigureShoreSettings(second, SecondRefraction, SecondSurfPeriod);
                var firstSink = new CapturingUniformSink();
                var secondSink = new CapturingUniformSink();

                new WaterShoreDepthField(first).WriteUniforms(firstSink);
                new WaterShoreDepthField(second).WriteUniforms(secondSink);

                Assert.That(firstSink.GetFloat(ShoreRefractionProperty), Is.EqualTo(FirstRefraction));
                Assert.That(secondSink.GetFloat(ShoreRefractionProperty), Is.EqualTo(SecondRefraction));
                Assert.That(firstSink.GetFloat(SurfPeriodProperty), Is.EqualTo(FirstSurfPeriod));
                Assert.That(secondSink.GetFloat(SurfPeriodProperty), Is.EqualTo(SecondSurfPeriod));
                Assert.That(firstSink.GetFloat(ShoreDepthValidProperty), Is.Zero);
                Assert.That(secondSink.GetFloat(ShoreDepthValidProperty), Is.Zero);
                Assert.That(firstSink.GetTexture(ShoreDepthTextureProperty), Is.EqualTo(Texture2D.blackTexture));
                Assert.That(secondSink.GetTexture(ShoreDepthTextureProperty), Is.EqualTo(Texture2D.blackTexture));
            }
            finally
            {
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void SeaStateFetchWeight_IsAttenuationOnlyAndGrowsWithFetch()
        {
            float shortFetch = WaterSeaStateFetchField.PhysicalWeight(
                ShortFetchNormalized, LongWaveWavelengthMeters);
            float longFetch = WaterSeaStateFetchField.PhysicalWeight(
                LongFetchNormalized, LongWaveWavelengthMeters);

            Assert.That(shortFetch, Is.InRange(0f, 1f));
            Assert.That(longFetch, Is.InRange(0f, 1f));
            Assert.That(longFetch, Is.GreaterThan(shortFetch));
        }

        [Test]
        public void SeaStateFetchWeight_PreservesRipplesMoreThanLongWaves()
        {
            float ripple = WaterSeaStateFetchField.PhysicalWeight(
                ShortFetchNormalized, RippleWavelengthMeters);
            float longWave = WaterSeaStateFetchField.PhysicalWeight(
                ShortFetchNormalized, LongWaveWavelengthMeters);

            Assert.That(ripple, Is.GreaterThan(longWave));
        }

        static GameObject CreateInactiveVolume(string objectName, out WaterVolume volume)
        {
            var gameObject = new GameObject(objectName);
            gameObject.SetActive(false);
            volume = gameObject.AddComponent<WaterVolume>();
            return gameObject;
        }

        static void ConfigureShoreSettings(WaterVolume volume, float refraction, float period)
        {
            FieldInfo settingsField = typeof(WaterVolume).GetField(BedDepthSettingsFieldName, InstancePrivate);
            Assert.That(settingsField, Is.Not.Null, "WaterVolume shore settings must remain serializable.");
            var settings = (WaterVolume.BedDepthSettings)settingsField.GetValue(volume);
            settings.shoreRefraction = refraction;
            settings.surfPeriod = period;
        }

        sealed class CapturingUniformSink : WaterUniformPublisher.IUniformSink
        {
            readonly Dictionary<int, float> _floats = new Dictionary<int, float>();
            readonly Dictionary<int, Texture> _textures = new Dictionary<int, Texture>();

            public void SetFloat(int id, float value) => _floats[id] = value;
            public void SetColor(int id, Color value) { }
            public void SetVector(int id, Vector4 value) { }
            public void SetMatrix(int id, Matrix4x4 value) { }
            public void SetVectorArray(int id, Vector4[] value) { }
            public void SetTexture(int id, Texture value) => _textures[id] = value;

            public float GetFloat(int id) => _floats[id];
            public Texture GetTexture(int id) => _textures[id];
        }
    }
}
