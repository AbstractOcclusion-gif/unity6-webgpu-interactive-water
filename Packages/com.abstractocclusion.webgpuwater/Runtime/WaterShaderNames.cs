// WebGpuWater - the ONE registry of the package's Shader "..." declaration names.
//
// WHY: these names were inlined in up to three places each (runtime Shader.Find fallbacks,
// editor build kit, scene builder) with only comments holding them together - renaming a
// shader silently broke whichever copy was forgotten. Every C# consumer now reads this
// registry (the editor assembly via InternalsVisibleTo); the .shader files themselves remain
// the declaration site, so a rename is: change the Shader "..." line + the one const here.
namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterShaderNames
    {
        internal const string Root = "AbstractOcclusion/WebGpuWater/";

        internal const string WaterSurface = Root + "WaterSurface";
        internal const string AnalyticPool = Root + "AnalyticPool";
        internal const string WaterReceiver = Root + "WaterReceiver";
        internal const string Caustics = Root + "Caustics";
        internal const string LargeBodyCaustics = Root + "LargeBodyCaustics";
        internal const string CausticOccluder = Root + "CausticOccluder";
        internal const string ObstacleDepth = Root + "ObstacleDepth";
        internal const string GodRays = Root + "GodRays";
        internal const string LargeBodyGodRays = Root + "LargeBodyGodRays";
        internal const string FoamParticles = Root + "FoamParticles";
        internal const string FoamDensityComposite = Root + "FoamDensityComposite";
        internal const string SplashParticles = Root + "SplashParticles";
        internal const string WaterUnderwaterFog = Root + "WaterUnderwaterFog";
        internal const string WaterExclusionWall = Root + "WaterExclusionWall";
        internal const string WaterChunkWall = Root + "WaterChunkWall";
        internal const string WaterChunkDepth = Root + "WaterChunkDepth";
    }
}
