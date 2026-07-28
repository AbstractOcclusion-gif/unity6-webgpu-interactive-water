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
        // User-facing product name and log prefix. ONE definition each: these were inlined per call
        // site in four different spellings ("[WebGpuWater]", "WebGpuWater:", "[WebGpuWater]",
        // "WaterVolume:"), which is how the pre-rebrand name survived into dialog titles and the
        // generated-asset folder long after the namespaces were renamed.
        internal const string ProductName = "WebGPU Water";
        internal const string LogPrefix = "[WebGpuWater] ";

        // Consumer-side, writable roots: generated meshes/materials/textures and the sample prefab
        // are created into the OPEN project's Assets, never into this read-only package.
        internal const string Root = "Assets/WebGpuWater";
        internal const string Gen = "Assets/WebGpuWater/Generated";

        // Immutable package assets loaded by path (compute shaders). They live inside the package,
        // whose root is RESOLVED (WaterPackagePaths) rather than assumed: an Asset Store
        // .unitypackage import lands the package under Assets/, where a Packages/ literal cannot
        // resolve. Properties rather than consts for the same reason - the root is only known at
        // editor runtime.
        internal static string PackageShadersRoot => WaterPackagePaths.Asset("Runtime/Shaders");
        internal static string SimComputePath => PackageShadersRoot + "/WaterSim.compute";
        internal static string OceanFftComputePath => PackageShadersRoot + "/OceanFft.compute";

        internal const int GridDetail = 200;
        internal const int SkyCubemapSize = 128;

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

        // Generated shared-asset paths (create-once; see LoadOrCreateMaterial et al).
        internal const string GridMeshPath = Gen + "/WaterGrid.asset";
        internal const string PoolMeshPath = Gen + "/Pool.asset";
        internal const string GodRayBoxMeshPath = Gen + "/GodRayBox.asset";
        internal const string SkyCubemapPath = Gen + "/SkyCubemap.cubemap";
        internal const string TilesTexturePath = Gen + "/Tiles.png";
        internal const string WaterQualityAssetPath = Gen + "/WaterQuality.asset";

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

        // GPU foam particles (compute + procedural-quad shader + sprite atlas).
        internal const string ShaderFoamParticles = WaterShaderNames.FoamParticles;
        internal const string ShaderFoamDensityComposite = WaterShaderNames.FoamDensityComposite;
        internal static string FoamParticleComputePath => PackageShadersRoot + "/WaterFoamParticles.compute";
        internal const string FoamParticleAtlasPath = Gen + "/FoamParticleAtlas_2x2.png";
        // Round soft droplet sprite for the airborne spray pass (its own look, separate from foam).
        internal const string FoamDropletTexPath = Gen + "/FoamDroplet.png";

        // Shuriken splash rendering (lit + soft-fade replacement for Sprites/Default).
        internal const string ShaderSplashParticles = WaterShaderNames.SplashParticles;
        internal const string SplashDropletMaterialPath = Gen + "/SplashDroplet.mat";
        internal const string SplashCrownMaterialPath = Gen + "/SplashCrown.mat";
        internal const string SplashCrownSheetPath = Gen + "/SplashFlipbook_8x8.png";
        internal const string SplashCrownLightSheetAPath = Gen + "/SplashFlipbookLightA_8x8.png";
        internal const string SplashCrownLightSheetBPath = Gen + "/SplashFlipbookLightB_8x8.png";
        // The crown flipbook (and its six-way light sheets) ship inside the package's Samples~
        // folder, which Unity never imports. These are their paths RELATIVE to the resolved
        // package root; the wizard copies them out to the Gen paths above on first build (see
        // LoadOrProvisionPackagedSheet) so the crown is textured even in projects that never
        // imported the demo samples.
        const string CrownSheetPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbook_8x8.png";
        const string CrownLightSheetAPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbookLightA_8x8.png";
        const string CrownLightSheetBPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbookLightB_8x8.png";
        // Crown material upgrades applied when the six-way light sheets are provisioned:
        // directional flipbook lighting on, plus a default backlit-transmission glow.
        const string SixWayProperty = "_SixWay";
        const string LightSheetAProperty = "_LightSheetA";
        const string LightSheetBProperty = "_LightSheetB";
        const string TransmissionStrengthProperty = "_TransmissionStrength";
        const float DefaultCrownTransmission = 1.0f;
        // KWS-style packed droplet (R mass / G shine / B dissolve noise / A thickness). The
        // legacy Gen/Droplet.png (RGB white, shape in A) is left on disk untouched for old
        // materials still on the legacy shader path.
        internal const string DropletTexturePath = Gen + "/DropletPacked.png";

        // Foam pattern flipbook (frames laid out in a grid; the surface shader
        // cross-fades frames over time so the foam churns internally). Relief is
        // procedural (finite differences of the pattern), so no normal-map asset.
        const string FoamFlipbookPath = Gen + "/FoamFlipbook_4x4.png";
        const int FoamFlipbookCols = 4;
        const int FoamFlipbookRows = 4;

        // Cooler, more underwater-blue god rays than the shader's warm default (1.0, 0.97, 0.85).
        static readonly Color DefaultGodRayColor = new Color(0.70f, 0.85f, 1.0f, 1f);
        // Authoring default for god-ray intensity: calmer than the shader's 1.5 (which reads
        // overblown on a fresh body). Shared by the legacy god-ray material AND the wizard's
        // ocean god-ray density so "god rays" mean the same strength on every body type.
        internal const float DefaultGodRayDensity = 0.8f;

        // Default surface textures the wizard assigns onto a new WaterVolume's Textures block.
        // They live in the package's IMPORTED Runtime/Textures folder (with their authored .meta
        // import settings - the detail map stays a Normal Map), unlike the crown sheet, which is
        // provisioned out of Samples~ because it is copied into consumer-project Gen assets.
        internal static string DefaultTexturesRoot => WaterPackagePaths.Asset("Runtime/Textures");

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

        // Crown splash flipbook grid; must match the SplashFlipbook_8x8 sheet layout.
        const int CrownSheetCols = 8;
        const int CrownSheetRows = 8;

        // Generated meshes keep huge bounds so Unity's renderer culling can never wrongly cull
        // a surface placed by the volume frame; real frustum culling is WaterVolume.CullBounds.
        const float HugeMeshBoundsSize = 1000f;

        internal static void EnsureGenFolder() => EnsureFolder(Gen);

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
