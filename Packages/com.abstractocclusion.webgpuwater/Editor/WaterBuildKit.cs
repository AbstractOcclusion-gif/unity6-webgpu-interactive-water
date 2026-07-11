// WebGL Water - shared build kit (Unity 6 / URP port)
// Editor-only generators shared by the Water Wizard and the scene builder:
// meshes, procedural sky/tiles, materials, camera/sun/splash rigging, and a
// fully-wired water body. Kept in one place so both builders compose the same
// primitives instead of duplicating them.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    // The water shaders + compute, loaded and validated once (see WaterBuildKit.TryLoadShaders).
    internal struct ShaderSet
    {
        public Shader Water, Pool, Caustics, Obstacle, Receiver;
        public ComputeShader Compute;
    }

    // Shared assets built once per scene build and threaded through the body/prop creators, so
    // several water bodies reuse one grid/sky/material set (each body still instances its own
    // surface material at runtime, so sharing the asset is safe).
    internal sealed class BuildContext
    {
        public ShaderSet Shaders;
        public Mesh Grid;
        public Mesh PoolMesh;
        public Cubemap Sky;
        public Texture2D Tiles;
        public WaterQuality Quality;
        public Camera Camera;
        public OrbitCamera Orbit;
        public Light Sun;
        public WaterSplashEmitter Splash;
        public Material MatAbove, MatUnder, MatPool;
        public string Folder; // per-build asset folder for this scene's materials
    }

    internal static class WaterBuildKit
    {
        // Consumer-side, writable roots: generated meshes/materials/textures and the sample prefab
        // are created into the OPEN project's Assets, never into this read-only package.
        internal const string Root = "Assets/WebGLWater";
        internal const string Gen = "Assets/WebGLWater/Generated";

        // Immutable package assets loaded by path (compute shaders). Lives inside the package, so it
        // must be addressed via the Packages/ mount, not Assets/.
        internal const string PackageShadersRoot = "Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders";
        internal const string SimComputePath = PackageShadersRoot + "/WaterSim.compute";

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

        // Generated shared-asset paths (create-once; see LoadOrCreateMaterial et al).
        internal const string GridMeshPath = Gen + "/WaterGrid.asset";
        internal const string PoolMeshPath = Gen + "/Pool.asset";
        internal const string GodRayBoxMeshPath = Gen + "/GodRayBox.asset";
        internal const string SkyCubemapPath = Gen + "/SkyCubemap.cubemap";
        internal const string TilesTexturePath = Gen + "/Tiles.png";
        internal const string WaterQualityAssetPath = Gen + "/WaterQuality.asset";

        // Shader names (keep in sync with the Shader "..." declarations in Shaders/).
        internal const string ShaderWaterSurface = "AbstractOcclusion/WebGpuWater/WaterSurface";
        internal const string ShaderPoolWall = "AbstractOcclusion/WebGpuWater/PoolWall";
        internal const string ShaderCaustics = "AbstractOcclusion/WebGpuWater/Caustics";
        internal const string ShaderObstacle = "AbstractOcclusion/WebGpuWater/ObstacleDepth";
        internal const string ShaderReceiver = "AbstractOcclusion/WebGpuWater/WaterReceiver";
        internal const string ShaderGodRays = "AbstractOcclusion/WebGpuWater/GodRays";

        // Material property names (keep in sync with the shader Properties blocks).
        internal const string PropUnderwater = "_Underwater";
        internal const string PropCull = "_Cull";
        internal const string PropBaseColor = "_BaseColor";
        internal const string PropRealRefraction = "_RealRefraction";
        internal const string KeywordRealRefraction = "_REAL_REFRACTION";
        internal const string PropGodRayColor = "_GodRayColor";
        internal const string PropFoamTex = "_FoamTex";
        internal const string PropFoamTexFrames = "_FoamTexFrames";
        internal const string PropFoamNormalTex = "_FoamNormalTex";
        internal const string PropParticleTex = "_ParticleTex";

        // GPU foam particles (compute + procedural-quad shader + sprite atlas).
        internal const string ShaderFoamParticles = "AbstractOcclusion/WebGpuWater/FoamParticles";
        internal const string ShaderFoamDensityComposite = "AbstractOcclusion/WebGpuWater/FoamDensityComposite";
        internal const string FoamParticleComputePath = PackageShadersRoot + "/WaterFoamParticles.compute";
        internal const string FoamParticleAtlasPath = Gen + "/FoamParticleAtlas_2x2.png";

        // Shuriken splash rendering (lit + soft-fade replacement for Sprites/Default).
        internal const string ShaderSplashParticles = "AbstractOcclusion/WebGpuWater/SplashParticles";
        internal const string SplashDropletMaterialPath = Gen + "/SplashDroplet.mat";
        internal const string SplashCrownMaterialPath = Gen + "/SplashCrown.mat";
        internal const string SplashCrownSheetPath = Gen + "/SplashFlipbook_8x8.png";
        // KWS-style packed droplet (R mass / G shine / B dissolve noise / A thickness). The
        // legacy Gen/Droplet.png (RGB white, shape in A) is left on disk untouched for old
        // materials still on the legacy shader path.
        internal const string DropletTexturePath = Gen + "/DropletPacked.png";

        // Foam pattern flipbook (frames laid out in a grid; the surface shader
        // cross-fades frames over time so the foam churns internally) and its
        // frame-matched relief normal map (raw-RGB encoded, imported linear).
        const string FoamFlipbookPath = Gen + "/FoamFlipbook_4x4.png";
        const string FoamNormalFlipbookPath = Gen + "/FoamFlipbookNormal_4x4.png";
        const int FoamFlipbookCols = 4;
        const int FoamFlipbookRows = 4;

        // Cooler, more underwater-blue god rays than the shader's warm default (1.0, 0.97, 0.85).
        static readonly Color DefaultGodRayColor = new Color(0.70f, 0.85f, 1.0f, 1f);

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

        // ---------------------------------------------------------------- context
        // Build the shared assets and scene rig for a build. Materials go into 'assetFolder' (one
        // folder per scene) so building or rebuilding one scene never overwrites another's tuned
        // materials. Shared deterministic assets (meshes, sky, tiles, quality) stay in Generated.
        // Returns false (with a dialog) when a required shader is missing, so callers can abort.
        internal static bool CreateContext(Transform sceneRoot, out BuildContext ctx, string assetFolder,
                                           bool buildPoolMaterial = true)
        {
            ctx = null;
            EnsureGenFolder();
            EnsureFolder(assetFolder);
            if (!TryLoadShaders(out ShaderSet shaders)) return false;

            var grid = SaveAsset(BuildGrid(GridDetail), GridMeshPath);
            var poolMesh = SaveAsset(BuildPool(), PoolMeshPath);
            var sky = SaveCubemap(BuildSky(SkyCubemapSize), SkyCubemapPath);
            var tiles = LoadOrBuildTiles(TilesTexturePath);
            var quality = LoadOrCreateWaterQuality(WaterQualityAssetPath);
            var (matAbove, matUnder, matPool) = CreateWaterMaterials(shaders.Water, shaders.Pool, buildPoolMaterial, assetFolder);

            var cam = SetUpCamera(out OrbitCamera orbit);
            var sun = CreateSun(sceneRoot);
            var splash = CreateSplashEmitter(sceneRoot);

            ctx = new BuildContext
            {
                Shaders = shaders,
                Grid = grid,
                PoolMesh = poolMesh,
                Sky = sky,
                Tiles = tiles,
                Quality = quality,
                Camera = cam,
                Orbit = orbit,
                Sun = sun,
                Splash = splash,
                MatAbove = matAbove,
                MatUnder = matUnder,
                MatPool = matPool,
                Folder = assetFolder
            };
            return true;
        }

        // A fully-wired water body: a "Frame" GameObject carrying the WaterVolume (its transform IS
        // the volume frame - move/rotate it to place the water; volumeExtent sizes it) plus the
        // surface renderers (and optional analytic pool + god-ray volume) at world identity, which
        // the volume frame places in the shader. Only ONE body per scene should be primary.
        internal static WaterVolume CreateWaterBody(BuildContext ctx, Transform parent, string name,
            Vector3 position, Vector3 extent, bool primary, bool withPool, bool withGodRays,
            bool withFoamParticles = true)
        {
            var bodyRoot = new GameObject(name);
            bodyRoot.transform.SetParent(parent);

            var frameGO = new GameObject(FrameObjectName);
            frameGO.transform.SetParent(bodyRoot.transform);
            frameGO.transform.position = position;

            var volume = frameGO.AddComponent<WaterVolume>();
            volume.simCompute = ctx.Shaders.Compute;
            volume.causticsShader = ctx.Shaders.Caustics;
            // Optional (oceans only): near-field caustics in the sim-window frame. Non-fatal if absent,
            // so bounded/pool builds don't require it - Shader.Find just leaves the field null.
            volume.largeBodyCausticsShader = Shader.Find("AbstractOcclusion/WebGpuWater/LargeBodyCaustics");
            volume.obstacleShader = ctx.Shaders.Obstacle;
            volume.waterMesh = ctx.Grid;
            volume.targetCamera = ctx.Camera;
            volume.sun = ctx.Sun;
            volume.orbit = ctx.Orbit;
            volume.splashEmitter = ctx.Splash;
            volume.tiles = ctx.Tiles;
            volume.sky = ctx.Sky;
            volume.Quality = ctx.Quality;
            volume.volumeExtent = extent;
            volume.IsPrimary = primary;

            // Renderers at world identity; the shader places the pool-space meshes via the frame.
            var rendGO = new GameObject(RenderersObjectName);
            rendGO.transform.SetParent(bodyRoot.transform);

            var above = CreateRenderer(SurfaceAboveName, ctx.Grid, ctx.MatAbove, rendGO.transform);
            var under = CreateRenderer(SurfaceUnderName, ctx.Grid, ctx.MatUnder, rendGO.transform);
            volume.surfaceAbove = above.GetComponent<Renderer>();
            volume.surfaceUnder = under.GetComponent<Renderer>();

            if (withPool && ctx.MatPool != null)
            {
                var poolGO = CreateRenderer(AnalyticPoolName, ctx.PoolMesh, ctx.MatPool, rendGO.transform);
                poolGO.GetComponent<MeshRenderer>().receiveShadows = true; // catch object shadows
                volume.poolRenderer = poolGO.GetComponent<Renderer>();
            }
            if (withGodRays)
            {
                var godGO = CreateGodRays(rendGO.transform, ctx.Folder);
                if (godGO != null) volume.godRayRenderer = godGO.GetComponent<Renderer>();
            }

            if (withFoamParticles) AddFoamParticles(volume, ctx.Folder);

            EditorUtility.SetDirty(volume);
            return volume;
        }

        // GPU foam/spray particles alongside the body's WaterVolume. The component idles
        // until the body's foam toggle is on, so bodies without foam pay nothing. Skipped
        // silently when the compute/shader/atlas assets are missing (feature simply absent).
        internal static WaterFoamParticles AddFoamParticles(WaterVolume volume, string materialFolder)
        {
            if (volume == null) return null;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(FoamParticleComputePath);
            var shader = Shader.Find(ShaderFoamParticles);
            if (compute == null || shader == null)
            {
                Debug.LogWarning("WebGL Water: foam particle compute/shader missing; skipping particle setup.");
                return null;
            }

            var material = LoadOrCreateMaterial(materialFolder + "/FoamParticles.mat", shader, m =>
            {
                var atlas = LoadFlipbook(FoamParticleAtlasPath, TextureWrapMode.Clamp, true);
                if (atlas != null) m.SetTexture(PropParticleTex, atlas);
            });

            // Screen-space density composite (KWS-style connected foam). Optional: when the
            // shader is missing the component warns and falls back to quads at runtime.
            Material densityMaterial = null;
            var densityShader = Shader.Find(ShaderFoamDensityComposite);
            if (densityShader != null)
                densityMaterial = LoadOrCreateMaterial(materialFolder + "/FoamDensityComposite.mat",
                                                       densityShader, m => { });

            var particles = volume.gameObject.AddComponent<WaterFoamParticles>();
            particles.volume = volume;
            particles.particleCompute = compute;
            particles.particleMaterial = material;
            particles.densityMaterial = densityMaterial;
            EditorUtility.SetDirty(particles);
            return particles;
        }

        // ---------------------------------------------------------------- demo props
        // A thin box collider under the water so sinking props have something to rest on.
        internal static GameObject CreateFloorCollider(Transform parent, Vector3 center, Vector3 size)
        {
            var go = new GameObject("Floor Collider");
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        // ---------------------------------------------------------------- materials
        // The above-water pass culls BACK faces; the underwater pass culls FRONT faces (inverted
        // from the shader's own defaults, which reads better here). The pool interior culls back
        // faces (_Cull maps to UnityEngine.Rendering.CullMode). Both surface materials enable REAL
        // screen-space refraction by default, so the water is transparent without hand-tweaking
        // (needs Opaque Texture + Depth Texture on the active URP asset).
        internal static (Material above, Material under, Material pool) CreateWaterMaterials(
            Shader sfWater, Shader sfPool, bool buildAnalyticPool, string folder)
        {
            float cullFront = (float)UnityEngine.Rendering.CullMode.Front;
            float cullBack = (float)UnityEngine.Rendering.CullMode.Back;
            var above = LoadOrCreateMaterial(folder + "/WaterAbove.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 0f); m.SetFloat(PropCull, cullBack); EnableRealRefraction(m); AssignFoamFlipbook(m); });
            var under = LoadOrCreateMaterial(folder + "/WaterUnder.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 1f); m.SetFloat(PropCull, cullFront); EnableRealRefraction(m); });
            Material pool = null;
            if (buildAnalyticPool && sfPool != null)
                pool = LoadOrCreateMaterial(folder + "/Pool.mat", sfPool, m => m.SetFloat(PropCull, cullBack));
            return (above, under, pool);
        }

        // Turn on the surface shader's real (screen-space) refraction toggle: set the property AND
        // the linked shader keyword, since setting the float alone doesn't flip the keyword.
        static void EnableRealRefraction(Material m)
        {
            m.SetFloat(PropRealRefraction, 1f);
            m.EnableKeyword(KeywordRealRefraction);
        }

        // Give a water surface material the animated foam pattern + its relief normal
        // map. Skipped silently when the flipbook asset is absent: the shader's white/bump
        // defaults degrade to flat foam.
        internal static void AssignFoamFlipbook(Material m)
        {
            var flipbook = LoadFlipbook(FoamFlipbookPath, TextureWrapMode.Repeat, true);
            if (flipbook == null) return;
            m.SetTexture(PropFoamTex, flipbook);
            m.SetVector(PropFoamTexFrames, new Vector4(FoamFlipbookCols, FoamFlipbookRows, 0f, 0f));

            var relief = LoadFlipbook(FoamNormalFlipbookPath, TextureWrapMode.Repeat, true, linear: true);
            if (relief != null) m.SetTexture(PropFoamNormalTex, relief);
        }

        // Underwater god-ray volume (caustic-masked light shafts). Returns null if the shader is
        // missing (the feature is simply absent then).
        internal static GameObject CreateGodRays(Transform parent, string folder)
        {
            var sfGodRays = Shader.Find(ShaderGodRays);
            if (sfGodRays == null) return null;

            var godRayMat = LoadOrCreateMaterial(folder + "/GodRays.mat", sfGodRays,
                                                 m => m.SetColor(PropGodRayColor, DefaultGodRayColor));
            var go = CreateRenderer(GodRaysObjectName, SaveAsset(BuildGodRayBox(), GodRayBoxMeshPath),
                                    godRayMat, parent);
            var gmr = go.GetComponent<MeshRenderer>();
            gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gmr.receiveShadows = false;
            return go;
        }

        // ---------------------------------------------------------------- scene rig
        // Reuse the scene's main camera if there is one (avoids two cameras rendering on top of each
        // other), then attach the orbit + planar-reflection helpers.
        internal static Camera SetUpCamera(out OrbitCamera orbit)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = new GameObject("Water Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = MainCameraTag;
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            // Same constants the runtime couples its activation distance to (see WaterVolume).
            cam.fieldOfView = WaterVolume.CameraFieldOfView;
            cam.nearClipPlane = WaterVolume.CameraNearClip;
            cam.farClipPlane = WaterVolume.CameraFarClip;

            orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = cam.gameObject.AddComponent<OrbitCamera>();
            orbit.pivot = DemoOrbitPivot;
            orbit.pitch = DemoOrbitPitch;
            orbit.yaw = DemoOrbitYaw;
            orbit.distance = DemoOrbitDistance;

            var planar = cam.GetComponent<PlanarReflection>();
            if (planar == null) planar = cam.gameObject.AddComponent<PlanarReflection>();
            planar.sourceCamera = cam;
            planar.waterHeight = 0f;
            planar.enableReflection = false;
            return cam;
        }

        // Single directional light: drives the analytic water + caustics (via the _LightDir global
        // the controller publishes) AND casts real URP shadows.
        internal static Light CreateSun(Transform parent)
        {
            var sunGO = new GameObject("Sun");
            sunGO.transform.SetParent(parent);
            var sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = DefaultSunIntensity;
            sun.transform.rotation = Quaternion.LookRotation(-DefaultSunTowardLight.normalized);
            return sun;
        }

        // Shared, fully editable splash particles (drift droplets + a flipbook crown).
        // Materials are create-once assets on the lit splash shader, so hand-tuning
        // survives rebuilds (same convention as the water/foam-particle materials).
        internal static WaterSplashEmitter CreateSplashEmitter(Transform parent)
        {
            var splashGO = new GameObject("Splash Particles");
            splashGO.transform.SetParent(parent);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            var splashPSR = splashGO.GetComponent<ParticleSystemRenderer>();
            splashPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            // Render mode is owned by ConfigureForDrift (stretched billboards: fast droplets
            // streak along their motion) - no override here.
            var splashEmitter = splashGO.AddComponent<WaterSplashEmitter>();
            splashEmitter.particles = splashPS;

            var crownGO = new GameObject("Splash Crown");
            crownGO.transform.SetParent(parent);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, CrownSheetCols, CrownSheetRows);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);
            crownPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashCrownMaterialPath,
                LoadFlipbook(SplashCrownSheetPath, TextureWrapMode.Clamp, false, linear: true));
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;
        }

        // Upgrade (or create) both shared splash materials on the lit shader. They are
        // referenced by every demo scene, so this fixes all of them at once.
        internal static void UpgradeSplashMaterials()
        {
            EnsureGenFolder();
            LoadOrCreateSplashMaterial(SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            LoadOrCreateSplashMaterial(SplashCrownMaterialPath,
                LoadFlipbook(SplashCrownSheetPath, TextureWrapMode.Clamp, false, linear: true));
            AssetDatabase.SaveAssets();
        }

        // A splash material on the lit shader (create-once). Also the one-click upgrade
        // path for materials created before the lit shader existed: an existing material
        // still on another shader is switched in place, keeping its texture.
        static Material LoadOrCreateSplashMaterial(string path, Texture2D sprite)
        {
            var shader = Shader.Find(ShaderSplashParticles);
            if (shader == null)
            {
                Debug.LogWarning($"WebGL Water: shader '{ShaderSplashParticles}' missing; splash material not created.");
                return null;
            }

            var material = LoadOrCreateMaterial(path, shader, m =>
            {
                if (sprite != null) m.mainTexture = sprite;
            });
            if (material.shader != shader)
            {
                material.shader = shader; // upgrade in place; _MainTex carries over by name
                EditorUtility.SetDirty(material);
            }
            // This creator is only ever handed the KWS-packed textures now, so force both the
            // texture and the packed-channel flag every call: it doubles as the one-click
            // upgrade for materials created before the packed format existed.
            if (sprite != null && material.mainTexture != sprite)
            {
                material.mainTexture = sprite;
                EditorUtility.SetDirty(material);
            }
            const string PackedChannelsProperty = "_PackedChannels";
            if (material.HasProperty(PackedChannelsProperty) &&
                !Mathf.Approximately(material.GetFloat(PackedChannelsProperty), 1f))
            {
                material.SetFloat(PackedChannelsProperty, 1f);
                EditorUtility.SetDirty(material);
            }
            return material;
        }

        // ---------------------------------------------------------------- meshes
        internal static GameObject CreateRenderer(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        // XY-plane grid in [-1,1], z = 0. Shared with the runtime (the Low tier rebuilds a
        // coarser grid on weak devices), so the actual builder lives in WaterMeshBuilder.
        internal static Mesh BuildGrid(int detail) => WaterMeshBuilder.BuildGrid(detail);

        // Open-top box: floor at y=-1, walls up to y=2/12, spanning x,z in [-1,1]. Faces inward.
        internal static Mesh BuildPool()
        {
            const float top = 2f / 12f;
            const float lo = -1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, lo, -1), new Vector3(-1, lo, 1), new Vector3(1, lo, 1), new Vector3(1, lo, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, top, -1), new Vector3(-1, top, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(-1, lo, 1), new Vector3(-1, top, 1), new Vector3(1, top, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, lo, -1), new Vector3(-1, top, -1), new Vector3(-1, top, 1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(1, top, 1), new Vector3(1, top, -1));

            var mesh = new Mesh { name = "Pool" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeMeshBoundsSize);
            return mesh;
        }

        // Closed box in POOL space: y in [-1,0], x,z in [-1,1]. Outward-wound (like a primitive
        // cube) so the GodRays pass's Cull Front renders the back faces.
        internal static Mesh BuildGodRayBox()
        {
            const float lo = -1f, hi = 0f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, hi, -1), new Vector3(-1, hi, 1), new Vector3(1, hi, 1), new Vector3(1, hi, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, -1), new Vector3(-1, hi, -1), new Vector3(1, hi, -1), new Vector3(1, lo, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(1, hi, 1), new Vector3(-1, hi, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, hi, 1), new Vector3(-1, hi, -1), new Vector3(-1, lo, -1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, hi, -1), new Vector3(1, hi, 1), new Vector3(1, lo, 1));

            var mesh = new Mesh { name = "GodRayBox" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeMeshBoundsSize);
            return mesh;
        }

        // ---------------------------------------------------------------- textures
        internal static Cubemap BuildSky(int size)
        {
            var cube = new Cubemap(size, TextureFormat.RGB24, false);
            CubemapFace[] faces = {
                CubemapFace.PositiveX, CubemapFace.NegativeX,
                CubemapFace.PositiveY, CubemapFace.NegativeY,
                CubemapFace.PositiveZ, CubemapFace.NegativeZ
            };
            foreach (var face in faces)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float w = (y + 0.5f) / size * 2f - 1f;
                        Vector3 dir = FaceDir(face, u, w).normalized;
                        cube.SetPixel(face, x, y, SkyColor(dir));
                    }
            cube.Apply();
            return cube;
        }

        static Vector3 FaceDir(CubemapFace f, float u, float v)
        {
            switch (f)
            {
                case CubemapFace.PositiveX: return new Vector3(1, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1);
                default: return new Vector3(-u, -v, -1);
            }
        }

        // Procedural sky palette + gradient curvature (pow eases the blend toward the horizon).
        static readonly Color SkyHorizonColor = new Color(0.78f, 0.86f, 0.96f);
        static readonly Color SkyZenithColor = new Color(0.26f, 0.47f, 0.86f);
        static readonly Color SkyGroundColor = new Color(0.30f, 0.30f, 0.33f);
        const float SkyZenithCurve = 0.6f;
        const float SkyGroundCurve = 0.5f;

        static Color SkyColor(Vector3 dir)
        {
            if (dir.y >= 0f) return Color.Lerp(SkyHorizonColor, SkyZenithColor, Mathf.Pow(dir.y, SkyZenithCurve));
            return Color.Lerp(SkyHorizonColor, SkyGroundColor, Mathf.Pow(-dir.y, SkyGroundCurve));
        }

        internal static Texture2D LoadOrBuildTiles(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int TextureSize = 256;
            const int TileCellSize = 32;         // pixels per tile
            const int GroutWidthPixels = 2;
            const float NoiseFloor = 0.85f;      // brightness variation: floor + amplitude * Perlin
            const float NoiseAmplitude = 0.15f;
            const float NoiseFrequency = 0.08f;
            Color tileColor = new Color(0.55f, 0.75f, 0.85f);
            Color groutColor = new Color(0.30f, 0.45f, 0.55f);

            var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGB24, true);
            for (int y = 0; y < TextureSize; y++)
                for (int x = 0; x < TextureSize; x++)
                {
                    bool grout = (x % TileCellSize < GroutWidthPixels) || (y % TileCellSize < GroutWidthPixels);
                    float n = NoiseFloor + NoiseAmplitude * Mathf.PerlinNoise(x * NoiseFrequency, y * NoiseFrequency);
                    tex.SetPixel(x, y, grout ? groutColor : tileColor * n);
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Packed droplet sprite (KWS channel layout, consumed by SplashParticles' packed path):
        // R = mass (round falloff), G = shine (tight hot core, cubed in the shader),
        // B = dissolve noise (lifetime burn threshold), A = thickness (soft-fade band).
        static Texture2D LoadOrBuildDroplet(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int s = 64;
            const float ShineFalloffPower = 6f;   // hot core confined near the centre
            const float NoiseFrequency = 9f;      // dissolve-noise feature size across the sprite
            const float NoiseFloor = 0.15f;       // keeps every texel erodable (never sticks at 0)
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f;
                    float dy = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    float mass = a * a;
                    float shine = Mathf.Pow(a, ShineFalloffPower);
                    float noise = NoiseFloor + (1f - NoiseFloor)
                                * Mathf.PerlinNoise(x / (float)s * NoiseFrequency,
                                                    y / (float)s * NoiseFrequency);
                    tex.SetPixel(x, y, new Color(mass, shine, noise, mass));
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.sRGBTexture = false;          // channel-packed DATA, not color
            imp.alphaIsTransparency = false;  // A is thickness, not coverage
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // 'linear' is for data textures (e.g. the raw-RGB foam normal map): sRGB sampling
        // would distort the decoded vectors.
        static Texture2D LoadFlipbook(string path, TextureWrapMode wrap, bool mipmaps, bool linear = false)
        {
            if (!File.Exists(path)) return null;
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = !linear;
                imp.alphaIsTransparency = !linear;
                imp.wrapMode = wrap;
                imp.mipmapEnabled = mipmaps;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------------------------------------------------------------- helpers
        // Load + validate the water shaders. Fails fast (dialog + false) if a REQUIRED shader
        // (surface, caustics, compute) is missing; optional shaders only warn.
        internal static bool TryLoadShaders(out ShaderSet shaders)
        {
            shaders = new ShaderSet
            {
                Water = Shader.Find(ShaderWaterSurface),
                Pool = Shader.Find(ShaderPoolWall),
                Caustics = Shader.Find(ShaderCaustics),
                Obstacle = Shader.Find(ShaderObstacle),
                Receiver = Shader.Find(ShaderReceiver),
                Compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(SimComputePath)
            };

            if (shaders.Water == null || shaders.Caustics == null || shaders.Compute == null)
            {
                EditorUtility.DisplayDialog("WebGL Water",
                    "Could not find the shaders / compute shader. Make sure the WebGLWater/Shaders folder imported without errors, then try again.",
                    "OK");
                return false;
            }

            if (shaders.Obstacle == null) Debug.LogWarning($"[WebGL Water] Shader '{ShaderObstacle}' not found; object->water displacement will be disabled.");
            return true;
        }

        internal static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // Create-once: reuse the material already at 'path' (preserving any hand-tuning) instead of
        // overwriting it, so rebuilding a scene - or building a different one - never resets it.
        internal static Material LoadOrCreateMaterial(string path, Shader shader, System.Action<Material> configure = null)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var m = new Material(shader);
            configure?.Invoke(m);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        internal static Cubemap SaveCubemap(Cubemap c, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (existing != null) { EditorUtility.CopySerialized(c, existing); return existing; }
            AssetDatabase.CreateAsset(c, path);
            return c;
        }

        internal static WaterQuality LoadOrCreateWaterQuality(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
            if (existing != null) return existing;
            var q = ScriptableObject.CreateInstance<WaterQuality>();
            AssetDatabase.CreateAsset(q, path);
            return q;
        }
    }
}
