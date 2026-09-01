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
        const float OceanSurfaceLevel = 10f;
        const float WetColumnDepth = 2f;
        const float DryColumnDepth = -1f;
        const float WetSampleX = -0.99f;
        const float DrySampleX = 0.99f;
        const float SampleZ = 0f;
        const float AboveTerrainY = 9f;
        const float BelowTerrainY = 7f;
        const float OutsideFieldX = 3f;
        const int TestFieldResolution = 2;
        const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        const string BedDepthSettingsFieldName = "bedDepthSettings";
        const string OceanSettingsFieldName = "ocean";
        const string WindowedFieldName = "_windowed";
        const string DepthBakedFieldName = "_depthBaked";
        const string CpuDepthFieldName = "_cpuDepth";
        const string FieldCenterFieldName = "_center";
        const string FieldHalfSizeFieldName = "_halfSize";
        const string FieldWaterLevelFieldName = "_waterLevel";
        const string FieldResolutionFieldName = "_res";
        static readonly int ShoreRefractionProperty = Shader.PropertyToID("_ShoreRefraction");
        static readonly int SurfPeriodProperty = Shader.PropertyToID("_SurfPeriod");
        static readonly int ShoreDepthValidProperty = Shader.PropertyToID("_ShoreDepthValid");
        static readonly int ShoreDepthTextureProperty = Shader.PropertyToID("_ShoreDepthTex");
        static readonly int ClipOceanToTerrainProperty = WaterShaderProps.ClipOceanToTerrain;

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

        [Test]
        public void OceanTerrainFootprint_DefaultsOffAndPublishesOnlyForAnOptedInOcean()
        {
            GameObject gameObject = CreateInactiveVolume("Ocean Terrain Footprint", out WaterVolume volume);
            try
            {
                var properties = new MaterialPropertyBlock();
                new WaterUniformPublisher(volume).WriteBodyProps(properties);
                Assert.That(properties.GetFloat(ClipOceanToTerrainProperty), Is.Zero);

                ConfigureOceanTerrainFootprint(volume);
                new WaterUniformPublisher(volume).WriteBodyProps(properties);
                Assert.That(properties.GetFloat(ClipOceanToTerrainProperty), Is.EqualTo(1f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void OceanTerrainFootprint_RejectsDryAndBelowTerrainButKeepsWetAndOffFieldOcean()
        {
            GameObject gameObject = CreateInactiveVolume("Ocean Terrain Domain", out WaterVolume volume);
            try
            {
                ConfigureOceanTerrainFootprint(volume);
                volume.transform.position = new Vector3(0f, OceanSurfaceLevel, 0f);
                volume.volumeExtent = new Vector3(1f, OceanSurfaceLevel, 1f);
                InjectSignedDepthField(volume.ShoreDepth);

                Assert.That(volume.OceanSurfaceExistsAt(WetSampleX, SampleZ), Is.True);
                Assert.That(volume.OceanSurfaceExistsAt(DrySampleX, SampleZ), Is.False);
                Assert.That(volume.OceanSurfaceExistsAt(OutsideFieldX, SampleZ), Is.True,
                            "outside the finite terrain field must remain unbounded ocean");

                Assert.That(volume.ContainsPointXYZ(
                    new Vector3(WetSampleX, AboveTerrainY, SampleZ)), Is.True);
                Assert.That(volume.ContainsPointXYZ(
                    new Vector3(WetSampleX, BelowTerrainY, SampleZ)), Is.False,
                    "the ocean column must stop at the sampled terrain floor");
                Assert.That(volume.ContainsPointXYZ(
                    new Vector3(DrySampleX, AboveTerrainY, SampleZ)), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
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

        static void ConfigureOceanTerrainFootprint(WaterVolume volume)
        {
            FieldInfo bedField = typeof(WaterVolume).GetField(BedDepthSettingsFieldName, InstancePrivate);
            Assert.That(bedField, Is.Not.Null);
            var bedSettings = (WaterVolume.BedDepthSettings)bedField.GetValue(volume);
            bedSettings.useBedDepth = true;
            bedSettings.clipOceanToTerrain = true;

            FieldInfo oceanField = typeof(WaterVolume).GetField(OceanSettingsFieldName, InstancePrivate);
            Assert.That(oceanField, Is.Not.Null);
            var oceanSettings = (WaterVolume.OceanSettings)oceanField.GetValue(volume);
            oceanSettings.openWater = true;
            oceanSettings.unboundedOcean = true;
            SetPrivateField(volume, WindowedFieldName, true);
        }

        static void InjectSignedDepthField(WaterShoreDepthField shoreDepth)
        {
            float[] depths =
            {
                WetColumnDepth, DryColumnDepth,
                WetColumnDepth, DryColumnDepth
            };
            SetPrivateField(shoreDepth, DepthBakedFieldName, true);
            SetPrivateField(shoreDepth, CpuDepthFieldName, depths);
            SetPrivateField(shoreDepth, FieldCenterFieldName, Vector2.zero);
            SetPrivateField(shoreDepth, FieldHalfSizeFieldName, Vector2.one);
            SetPrivateField(shoreDepth, FieldWaterLevelFieldName, OceanSurfaceLevel);
            SetPrivateField(shoreDepth, FieldResolutionFieldName, TestFieldResolution);
        }

        static void SetPrivateField<TTarget, TValue>(TTarget target, string fieldName, TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(fieldName, InstancePrivate);
            Assert.That(field, Is.Not.Null, $"{typeof(TTarget).Name}.{fieldName} must remain available.");
            field.SetValue(target, value);
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
