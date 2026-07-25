// WebGpuWater - shared serialized-property paths into WaterVolume.
//
// WHY: these paths are raw strings with zero compile-time safety (a stale path already caused a
// crash once - see WaterVolumeEditor.Setup's history note), and the same paths were retyped in
// the wizard, the inspector's body-type defaults and the ocean section. One registry means a
// field rename is a one-line fix and every consumer breaks loudly together in review, not
// silently apart at runtime.
namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterVolumePropertyPaths
    {
        internal const string OpenWater = "ocean.openWater";
        internal const string UnboundedOcean = "ocean.unboundedOcean";
        internal const string ScreenSpaceReflection = "reflectionSettings.useScreenSpaceReflection";
        internal const string PlanarReflection = "reflectionSettings.usePlanarReflection";
        internal const string RealRefraction = "reflectionSettings.realRefraction";
        internal const string BodyType = "bodyType";
        internal const string EnableLargeBodyWindow = "enableLargeBodyWindow";

        // Water fog block (wizard look defaults).
        internal const string FogDensity = "waterFogSettings.fogDensity";

        // Detail-normal block (Textures section; wizard look defaults).
        internal const string DetailNormalTexture = "detailNormalSettings.texture";
        internal const string DetailNormalStrength = "detailNormalSettings.strength";

        // Ocean god rays (wizard look defaults).
        internal const string LargeGodRayDensity = "ocean.largeGodRayDensity";

        // Depth attenuation block (wizard look defaults).
        internal const string GodRayDepthFade = "depthAttenuation.godRayDepthFade";

        // Ocean block (large waves / swell / horizon), used by the feature-showcase builder
        // and the ocean inspector section.
        internal const string EdgeFeatherMeters = "ocean.edgeFeatherMeters";
        internal const string LargeWaveAmplitude = "ocean.largeWaveAmplitude";
        internal const string LargeWaveChoppiness = "ocean.largeWaveChoppiness";
        internal const string SwellHeight = "ocean.swellHeight";
        internal const string SwellWavelength = "ocean.swellWavelength";
        internal const string HorizonHazeDensity = "ocean.horizonHazeDensity";

        // Wind-wave block.
        internal const string WindSpeed = "windWaveSettings.windSpeed";
        internal const string WaveScaleMeters = "windWaveSettings.waveScaleMeters";
        internal const string WaveAmplitudeScale = "windWaveSettings.waveAmplitudeScale";

        // Foam block.
        internal const string FoamGenRate = "foamSettings.foamGenRate";

        // Volume scattering block.
        internal const string VolumeScatter = "volumeScatterSettings.volumeScatter";
        internal const string CrestScatter = "volumeScatterSettings.crestScatter";

        // Bed depth / shoreline / clarity block.
        internal const string UseBedDepth = "bedDepthSettings.useBedDepth";
        internal const string BedTerrain = "bedDepthSettings.bedTerrain";
        internal const string SurfEnabled = "bedDepthSettings.surfEnabled";
        internal const string SurfAmplitude = "bedDepthSettings.surfAmplitude";
        internal const string ClarityFromDepth = "bedDepthSettings.clarityFromDepth";
        internal const string ClarityShallowDepth = "bedDepthSettings.clarityShallowDepth";
        internal const string ClarityDeepDepth = "bedDepthSettings.clarityDeepDepth";
    }
}
