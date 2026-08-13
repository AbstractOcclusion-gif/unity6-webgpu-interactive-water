// WebGpuWater - shared build kit (Unity 6 / URP port): editor-only generators shared by the
// Water Wizard and the scene builder, so both builders compose the SAME primitives instead of
// duplicating them.
//
// This file is the kit's shared vocabulary - the object names, asset paths and sizes every other
// partial builds against. The build steps themselves live in WaterBuildKit.<Step>.cs, one file
// per responsibility, because a single 1373-line static type made it impossible to see which
// constants a given generator actually owned.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // User-facing product name and log prefix. Define them ONCE here: inlining per call site
        // (four different spellings at one point) is how the pre-rebrand name survived into dialog
        // titles and the generated-asset folder long after the namespaces were renamed.
        // HONESTY NOTE (2026-07-31 audit): ~37 call sites across this assembly still inline the
        // literal instead of routing through these consts. New code must use them; migrating the
        // legacy sites is open (docs/WebGpuWater_Standards_Audit_2026-07-31.md).
        internal const string ProductName = "WebGPU Water";
        internal const string LogPrefix = "[WebGpuWater] ";

        // Consumer-side writable roots. Each authored water owns one folder; immutable defaults
        // live in the package and are referenced directly.
        internal const string Root = "Assets/WebGpuWater";
        internal const string ProjectAssetsPrefix = "Assets/";
        internal const string WatersRoot = Root + "/Waters";
        internal const string BoatAssetsRoot = Root + "/Boats";
        internal const string DryInteriorAssetsRoot = Root + "/DryInteriors";
        internal const string ProjectProfilesRoot = Root + "/Profiles";
        internal const string MaterialsFolderName = "Materials";
        internal const string DefaultWaterFolderName = "Water";
        internal const string SharedFoamProfileFileName = "DefaultFoamProfile.asset";

        // Immutable package assets loaded by path (compute shaders). They live inside the package,
        // whose root is RESOLVED (WaterPackagePaths) rather than assumed: an Asset Store
        // .unitypackage import lands the package under Assets/, where a Packages/ literal cannot
        // resolve. Properties rather than consts for the same reason - the root is only known at
        // editor runtime.
        internal static string PackageShadersRoot => WaterPackagePaths.Asset("Runtime/Shaders");
        internal static string PackageDefaultsRoot => WaterPackagePaths.Asset("Runtime/Defaults");
        internal static string DefaultMeshesRoot => PackageDefaultsRoot + "/Meshes";
        internal static string DefaultMaterialsRoot => PackageDefaultsRoot + "/Materials";
        internal static string DefaultPrefabsRoot => PackageDefaultsRoot + "/Prefabs";
        internal static string DefaultProfilesRoot => PackageDefaultsRoot + "/Profiles";
        internal static string DefaultTexturesRoot => PackageDefaultsRoot + "/Textures";
        internal static string SimComputePath => PackageShadersRoot + "/WaterSim.compute";
        internal static string OceanFftComputePath => PackageShadersRoot + "/OceanFft.compute";

        // Scene-object names, shared with WaterSceneBuilder's body-cloning path so a rename
        // here can never silently break the clone naming there.
        internal const string FrameObjectName = "Frame (WaterVolume)";
        internal const string RenderersObjectName = "Renderers";
        internal const string SurfaceAboveName = "Water (above)";
        internal const string SurfaceUnderName = "Water (under)";
        internal const string AnalyticPoolName = "Analytic Pool";
        internal const string GodRaysObjectName = "God Rays";
        internal const string MainCameraTag = "MainCamera";

        // Menu root for every editor entry point (Asset Store guideline 2.5.1.a forbids custom
        // top-level menus, so everything lives under Window/).
        internal const string MenuRoot = "Window/AbstractOcclusion/WebGpuWater/";

        internal static string GridMeshPath => DefaultMeshesRoot + "/WaterGrid.asset";
        internal static string PoolMeshPath => DefaultMeshesRoot + "/Pool.asset";
        internal static string GodRayBoxMeshPath => DefaultMeshesRoot + "/GodRayBox.asset";
        internal static string SkyCubemapPath => DefaultTexturesRoot + "/SkyCubemap.cubemap";
        internal static string TilesTexturePath => DefaultTexturesRoot + "/Tiles.png";
        internal static string WaterQualityAssetPath => DefaultProfilesRoot + "/DefaultWaterQuality.asset";
        internal static string DefaultFoamProfilePath => DefaultProfilesRoot + "/DefaultFoamProfile.asset";
        internal static string SplashEmitterPrefabPath => DefaultPrefabsRoot + "/Water Splash FX.prefab";
        internal static string DefaultSplashDropletMaterialPath =>
            DefaultMaterialsRoot + "/SplashDroplet.mat";
        internal static string DefaultSplashCrownMaterialPath =>
            DefaultMaterialsRoot + "/SplashCrown.mat";

        // Shader names: aliases of the runtime WaterShaderNames registry (one source; the
        // registry is internal and reachable via InternalsVisibleTo).
        internal const string ShaderWaterSurface = WaterShaderNames.WaterSurface;
        internal const string ShaderAnalyticPool = WaterShaderNames.AnalyticPool;
        internal const string ShaderCaustics = WaterShaderNames.Caustics;
        internal const string ShaderObstacle = WaterShaderNames.ObstacleDepth;
        internal const string ShaderGodRays = WaterShaderNames.GodRays;
        internal const string ShaderLargeBodyCaustics = WaterShaderNames.LargeBodyCaustics;
        internal const string ShaderCausticOccluder = WaterShaderNames.CausticOccluder;

        // Material property names (keep in sync with the shader Properties blocks).
        internal const string PropUnderwater = "_Underwater";
        internal const string PropCull = "_Cull";
        internal const string PropBaseColor = "_BaseColor";
        internal const string PropRealRefraction = WaterShaderProps.RealRefractionName;
        internal const string PropGodRayColor = "_GodRayColor";
        internal const string PropGodRayDensity = "_GodRayDensity";
        internal const string PropFoamTex = WaterShaderProps.FoamTexName;
        internal const string PropFoamTexFrames = WaterShaderProps.FoamTexFramesName;
        internal const string PropParticleTex = WaterShaderProps.ParticleTexName;
        internal const string PropBreakupTex = WaterShaderProps.BreakupTexName;
        internal const string PropBumpMap = "_BumpMap";

        // GPU foam particles (compute + procedural-quad shader + sprite atlas).
        internal const string ShaderFoamParticles = WaterShaderNames.FoamParticles;
        internal const string ShaderFoamDensityComposite = WaterShaderNames.FoamDensityComposite;
        internal static string FoamParticleComputePath => PackageShadersRoot + "/WaterFoamParticles.compute";

        // Shuriken splash rendering (lit + soft-fade replacement for Sprites/Default).
        internal const string ShaderSplashParticles = WaterShaderNames.SplashParticles;
        internal static string SplashCrownSheetPath => DefaultTexturesRoot + "/WaterSplashChunks_4x1.png";
        // Backlit transmission reads the packed atlas' thickness channel.
        const string TransmissionStrengthProperty = "_TransmissionStrength";
        const float DefaultCrownTransmission = 1.0f;
        // KWS-style packed droplet (R mass / G shine / B dissolve noise / A thickness).
        internal static string DropletTexturePath => DefaultTexturesRoot + "/DropletPacked.png";

        const int FoamFlipbookCols = 4;
        const int FoamFlipbookRows = 4;

        // Cooler, more underwater-blue god rays than the shader's warm default (1.0, 0.97, 0.85).
        static readonly Color DefaultGodRayColor = new Color(0.70f, 0.85f, 1.0f, 1f);
        // Authoring default for god-ray intensity: calmer than the shader's 1.5 (which reads
        // overblown on a fresh body). Shared by the legacy god-ray material AND the wizard's
        // ocean god-ray density so "god rays" mean the same strength on every body type.
        internal const float DefaultGodRayDensity = 0.8f;

        // Authored art the wizard assigns onto a new body. All defaults are imported package assets;
        // the Wizard references them directly and never copies them into the consumer project.
        internal const string FoamParticleAtlasFile = "FoamParticleAtlas_2x2.png";
        // Round soft droplet sprite for the airborne spray pass (its own look, separate from foam).
        internal const string FoamDropletTexFile = "Droplet.png";
        // Foam pattern flipbook (frames laid out in a grid; the surface shader cross-fades frames
        // over time so the foam churns internally). Relief is procedural (finite differences of the
        // pattern), so no normal-map asset.
        internal const string FoamFlipbookFile = "FoamFlipbook_4x4.png";
        // World-tiled lace the density veil breaks its alpha against (_BreakupStrength > 0).
        internal const string FoamBreakupTexFile = "FoamBreakupWorley.png";
        internal const string TilesNormalTextureFile = "Tiles_N.png";

        // Demo camera framing. FOV/clip planes come from WaterVolume's internal constants (the
        // single source of truth; the volume's activation distance is coupled to the far clip).
        // The orbit pose matches OrbitCamera's own field defaults, applied explicitly so a
        // REUSED scene camera is reframed to the demo view too.
        static readonly Vector3 DemoOrbitPivot = new Vector3(0f, -0.5f, 0f);
        const float DemoOrbitPitch = -25f;
        const float DemoOrbitYaw = -200.5f;
        const float DemoOrbitDistance = 4f;

        // Demo sun: slightly over-bright for sparkle; direction matches WaterVolume's default
        // lightDir so the analytic water and the real shadows agree before the sun is moved.
        const float DefaultSunIntensity = 1.2f;
        static readonly Vector3 DefaultSunTowardLight = new Vector3(2f, 2f, -1f);

        // Splash chunk grid; must match the WaterSplashChunks_4x1 atlas layout: four packed
        // 512px photographic chunks side by side (the KWS WaterSplash construction). Each
        // sprite steps through all four chunks once over its life.
        const int CrownSheetCols = 4;
        const int CrownSheetRows = 1;

        internal static string CreateUniqueWaterFolder()
        {
            EnsureFolder(WatersRoot);
            string path = AssetDatabase.GenerateUniqueAssetPath(WatersRoot + "/" + DefaultWaterFolderName);
            EnsureFolder(path);
            return path;
        }

        internal static string MaterialsFolder(string waterFolder) => waterFolder + "/" + MaterialsFolderName;

        // Create an asset folder (and any missing parents) if it doesn't exist yet.
        internal static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder) || AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            string leaf = Path.GetFileName(assetFolder);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

    }
}
