using System;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterRendererFeatureCatalog
    {
        internal const string RendererDataListProperty = "m_RendererDataList";
        internal const string DefaultRendererIndexProperty = "m_DefaultRendererIndex";
        internal const string RendererFeaturesProperty = "m_RendererFeatures";
        internal const string RendererFeatureMapProperty = "m_RendererFeatureMap";

        const string FeatureNamespacePrefix = "AbstractOcclusion.WebGpuWater.";
        const string ShaderFolder = "Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/";

        internal readonly struct ShaderBinding
        {
            internal readonly string PropertyName;
            internal readonly string ShaderPath;
            internal readonly string DisplayName;

            internal ShaderBinding(string propertyName, string shaderFileName, string displayName)
            {
                PropertyName = propertyName;
                ShaderPath = ShaderFolder + shaderFileName;
                DisplayName = displayName;
            }
        }

        internal readonly struct Feature
        {
            internal readonly string TypeName;
            internal readonly string Purpose;
            internal readonly ShaderBinding[] ShaderBindings;

            internal Feature(string typeName, string purpose, params ShaderBinding[] shaderBindings)
            {
                TypeName = typeName;
                Purpose = purpose;
                ShaderBindings = shaderBindings;
            }
        }

        internal static readonly Feature[] Features =
        {
            new Feature(
                "WaterUnderwaterFogFeature",
                "Underwater fog while the camera is submerged",
                new ShaderBinding("underwaterFogShader", "WaterUnderwaterFog.shader", "underwater fog shader"),
                new ShaderBinding("heightRtShader", "WaterHeightRT.shader", "water height shader")),
            new Feature(
                "WaterCausticProjectionFeature",
                "Screen-space caustics on terrain and other non-water surfaces",
                new ShaderBinding("causticProjectionShader", "WaterCausticProjection.shader", "caustic projection shader")),
            new Feature(
                "WaterChunkDepthFeature",
                "Mesh-footprint water chunks",
                new ShaderBinding("chunkDepthShader", "WaterChunkDepth.shader", "chunk depth shader")),
            new Feature(
                "WaterExclusionDepthFeature",
                "Mesh-shaped exclusion volumes",
                new ShaderBinding("exclusionDepthShader", "WaterExclusionDepth.shader", "exclusion depth shader")),
            new Feature(
                "LargeBodyAtmosphereFeature",
                "Ocean god-ray shafts",
                new ShaderBinding("godRayShader", "LargeBodyGodRays.shader", "god-ray shader")),
            new Feature(
                "WaterSkyFogFeature",
                "Unity scene fog on the skybox",
                new ShaderBinding("skyFogShader", "WaterSkyFog.shader", "sky fog shader")),
        };

        internal static Type ResolveFeatureType(string typeName) =>
            typeof(WaterVolume).Assembly.GetType(FeatureNamespacePrefix + typeName);
    }
}
