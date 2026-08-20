// WebGpuWater - shared serialized-property paths into WaterVolume.
//
// WHY: these paths are raw strings with zero compile-time safety (a stale path already caused a
// crash once - see WaterVolumeEditor.Setup's history note), and the same paths were retyped in
// the wizard, the inspector's body-type defaults and the ocean section. One registry means a
// field rename is a one-line fix and every consumer breaks loudly together in review, not
// silently apart at runtime.
//
// SCOPE - what belongs here: a path read from MORE THAN ONE place (a different file, or twice in
// one file). Those are the ones that can drift, and drift silently: FindProperty returns null for
// the stale copy and the inspector NREs on selection while the registry copy still works.
// A path used exactly once stays inline at its single use site, where it is already
// single-sourced and reads better next to the field it draws - mirroring a whole 190-field
// inspector into consts here would add indirection without adding safety.
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
        internal const string JerlovWaterType = "jerlovWaterType"; // stored reference (inspector dropdown + Jerlov writer)
        internal const string EnableLargeBodyWindow = "enableLargeBodyWindow";

        // Water fog block (wizard look defaults + the Jerlov preset writer).
        internal const string FogDensity = "waterFogSettings.fogDensity";
        internal const string WaterFog = "waterFogSettings.waterFog";
        internal const string WaterOpacity = "waterFogSettings.waterOpacity";

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
        internal const string CurrentHeadingDegrees = "ocean.currentHeadingDegrees";
        internal const string CurrentSpeed = "ocean.currentSpeed";
        internal const string WindDrivesAmbientSeaState = "ocean.windDrivesAmbientSeaState";
        internal const string AmbientWindReferenceSpeed = "ocean.ambientWindReferenceSpeed";
        internal const string SignificantWaveHeight = "ocean.significantWaveHeight";
        internal const string PeakWavelength = "ocean.peakWavelength";
        internal const string PeakSharpness = "ocean.peakSharpness";
        internal const string WaveScale = "ocean.waveScale";
        internal const string SeaDepth = "ocean.seaDepth";
        internal const string SwellHeight = "ocean.swellHeight";
        internal const string SwellWavelength = "ocean.swellWavelength";
        internal const string SeaStateGusts = "ocean.seaStateGusts";
        internal const string SeaStateSlicks = "ocean.seaStateSlicks";
        internal const string SeaStateFetchEnabled = "ocean.seaStateFetchEnabled";
        internal const string SeaStateFetchStrength = "ocean.seaStateFetchStrength";
        internal const string OceanAperiodicEnabled = "ocean.oceanAperiodicEnabled";
        internal const string OceanDirectionMap = "ocean.oceanDirectionMap";
        internal const string OceanDirectionMapSize = "ocean.oceanDirectionMapSize";
        internal const string OceanDirectionMapStrength = "ocean.oceanDirectionMapStrength";
        internal const string OceanAperiodicTileScale = "ocean.oceanAperiodicTileScale";
        internal const string SwellHeadingOffset = "ocean.swellHeadingOffsetDegrees";
        internal const string OceanWindTurbulence = "ocean.oceanWindTurbulence";
        internal const string HorizonHazeDensity = "ocean.horizonHazeDensity";

        // Wind-wave block.
        internal const string WindSpeed = "windWaveSettings.windSpeed";
        internal const string WaveLengthMeters = "windWaveSettings.waveLengthMeters";
        internal const string WaveHeightMeters = "windWaveSettings.waveHeightMeters";
        internal const string WaveGrouping = "windWaveSettings.waveGrouping";
        internal const string WaveCrestSharpness = "windWaveSettings.waveCrestSharpness";

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

        // Read from more than one partial (the Jerlov preset writer and the Appearance
        // section write the same fog/scatter fields; the sun is drawn in two places).
        internal const string FogColor = "waterFogSettings.fogColor";
        internal const string FogExtinction = "waterFogSettings.fogExtinction";
        internal const string ScatterColor = "volumeScatterSettings.scatterColor";
        internal const string ScatterIntensity = "volumeScatterSettings.scatterIntensity";
        internal const string Sun = "sun";
    }
}
