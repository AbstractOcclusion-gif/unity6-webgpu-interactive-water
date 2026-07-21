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
        public Shader Water, Pool, Caustics, Obstacle;
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
        internal const string OceanFftComputePath = PackageShadersRoot + "/OceanFft.compute";

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
        internal const string PropRealRefraction = "_RealRefraction";
        internal const string KeywordRealRefraction = "_REAL_REFRACTION";
        internal const string PropGodRayColor = "_GodRayColor";
        internal const string PropFoamTex = "_FoamTex";
        internal const string PropFoamTexFrames = "_FoamTexFrames";
        internal const string PropParticleTex = "_ParticleTex";

        // GPU foam particles (compute + procedural-quad shader + sprite atlas).
        internal const string ShaderFoamParticles = WaterShaderNames.FoamParticles;
        internal const string ShaderFoamDensityComposite = WaterShaderNames.FoamDensityComposite;
        internal const string FoamParticleComputePath = PackageShadersRoot + "/WaterFoamParticles.compute";
        internal const string FoamParticleAtlasPath = Gen + "/FoamParticleAtlas_2x2.png";
        // Round soft droplet sprite for the airborne spray pass (its own look, separate from foam).
        internal const string FoamDropletTexPath = Gen + "/FoamDroplet.png";

        // Shuriken splash rendering (lit + soft-fade replacement for Sprites/Default).
        internal const string ShaderSplashParticles = WaterShaderNames.SplashParticles;
        internal const string SplashDropletMaterialPath = Gen + "/SplashDroplet.mat";
        internal const string SplashCrownMaterialPath = Gen + "/SplashCrown.mat";
        internal const string SplashCrownSheetPath = Gen + "/SplashFlipbook_8x8.png";
        // The crown flipbook ships inside the package's Samples~ folder, which Unity never imports.
        // This is its path RELATIVE to the resolved package root; the wizard copies it out to
        // SplashCrownSheetPath on first build (see LoadOrProvisionCrownSheet) so the crown is textured
        // even in projects that never imported the demo samples.
        const string CrownSheetPackageRelativePath = "Samples~/Demos/Common/SplashFlipbook_8x8.png";
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
            if (!TryBuildSharedAssets(assetFolder, buildPoolMaterial, out ctx)) return false;
            RigScene(ctx, sceneRoot);
            return true;
        }

        // The pure ASSET half of a build (meshes, sky, tiles, quality, materials) - no scene
        // mutation, so the prefab builder can reuse it without also rigging a camera/sun
        // into the open scene. Camera/Orbit/Sun stay null until RigScene fills them.
        internal static bool TryBuildSharedAssets(string assetFolder, bool buildPoolMaterial, out BuildContext ctx)
        {
            ctx = null;
            EnsureGenFolder();
            EnsureFolder(assetFolder);
            if (!TryLoadShaders(out ShaderSet shaders)) return false;

            // Create-once (delete the Generated/ asset to regenerate): the meshes/sky are
            // deterministic functions of the constants above, so rebuilding + CopySerialized on
            // every click only cost import time and dirtied version control for identical bytes.
            var grid = LoadOrSaveMesh(GridMeshPath, () => BuildGrid(GridDetail));
            var poolMesh = LoadOrSaveMesh(PoolMeshPath, BuildPool);
            var sky = LoadOrSaveCubemap(SkyCubemapPath, () => BuildSky(SkyCubemapSize));
            var tiles = LoadOrBuildTiles(TilesTexturePath);
            var quality = LoadOrCreateWaterQuality(WaterQualityAssetPath);
            var (matAbove, matUnder, matPool) = CreateWaterMaterials(shaders.Water, shaders.Pool, buildPoolMaterial, assetFolder);

            ctx = new BuildContext
            {
                Shaders = shaders,
                Grid = grid,
                PoolMesh = poolMesh,
                Sky = sky,
                Tiles = tiles,
                Quality = quality,
                MatAbove = matAbove,
                MatUnder = matUnder,
                MatPool = matPool,
                Folder = assetFolder
            };
            return true;
        }

        // The SCENE half of a build: camera framing + orbit and sun. Split from the asset half so
        // each caller takes exactly what it needs. The splash emitter is rigged per-body in
        // CreateWaterBody (the body owns its splash), not as a loose scene-root object.
        internal static void RigScene(BuildContext ctx, Transform sceneRoot)
        {
            ctx.Camera = SetUpCamera(out OrbitCamera orbit);
            ctx.Orbit = orbit;
            ctx.Sun = CreateSun(sceneRoot);
        }

        // A fully-wired water body: a "Frame" GameObject carrying the WaterVolume (its transform IS
        // the volume frame - move/rotate it to place the water; volumeExtent sizes it) plus the
        // surface renderers (and optional analytic pool + god-ray volume) at world identity, which
        // the volume frame places in the shader. Only ONE body per scene should be primary.
        internal static WaterVolume CreateWaterBody(BuildContext ctx, Transform parent, string name,
            Vector3 position, Vector3 extent, bool primary, bool withPool, bool withGodRays,
            bool withFoamParticles = true, bool withSplash = true)
        {
            var bodyRoot = NewUndoableGameObject(name);
            bodyRoot.transform.SetParent(parent);

            var frameGO = NewUndoableGameObject(FrameObjectName);
            frameGO.transform.SetParent(bodyRoot.transform);
            frameGO.transform.position = position;

            var volume = frameGO.AddComponent<WaterVolume>();
            WireWaterVolumeAssets(volume, ctx.Shaders, ctx.Grid, ctx.Tiles, ctx.Sky, ctx.Quality);
            volume.targetCamera = ctx.Camera;
            volume.sun = ctx.Sun;
            volume.orbit = ctx.Orbit;
            volume.volumeExtent = extent;
            volume.IsPrimary = primary;

            // Renderers at world identity; the shader places the pool-space meshes via the frame.
            var rendGO = NewUndoableGameObject(RenderersObjectName);
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

            // The body owns its splash: the authored emitter (drift droplets + flipbook crown) lives
            // under this body's frame, not as a loose scene-root object. Off = this body stays silent.
            volume.provideSplashEmitter = withSplash;
            if (withSplash) volume.splashEmitter = CreateSplashEmitter(volume.transform);

            // ONE profile is the single tweak surface for foam + splash: auto-create it and
            // point BOTH components at it, so a new body is configured from one asset instead
            // of two components carrying duplicated knobs.
            if (withFoamParticles || withSplash)
                AssignFoamProfileToBody(volume, LoadOrCreateFoamProfile(ctx.Folder));

            EditorUtility.SetDirty(volume);
            return volume;
        }

        // GPU foam/spray particles alongside the body's WaterVolume. The component idles
        // until the body's foam toggle is on, so bodies without foam pay nothing. Skipped
        // silently when the compute/shader/atlas assets are missing (feature simply absent).
        internal static WaterFoamParticles AddFoamParticles(WaterVolume volume, string materialFolder)
        {
            if (volume == null) return null;

            // Don't add a component we can't wire: bail if the required compute/shader is missing.
            if (AssetDatabase.LoadAssetAtPath<ComputeShader>(FoamParticleComputePath) == null ||
                Shader.Find(ShaderFoamParticles) == null)
            {
                Debug.LogWarning("WebGL Water: foam particle compute/shader missing; skipping particle setup.");
                return null;
            }

            var particles = Undo.AddComponent<WaterFoamParticles>(volume.gameObject);
            particles.volume = volume;
            WireFoamAssets(particles, materialFolder);
            return particles;
        }

        // Load (or create) and assign the foam compute + quad material + density-composite material
        // onto an EXISTING WaterFoamParticles. Shared by AddFoamParticles and the component
        // inspector's Wire/Repair button, so both paths produce identical wiring. Assets are
        // create-once in the given folder; hand-tuned material values survive a repair.
        internal static void WireFoamAssets(WaterFoamParticles particles, string materialFolder)
        {
            if (particles == null) return;

            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(FoamParticleComputePath);
            var shader = Shader.Find(ShaderFoamParticles);
            if (compute == null || shader == null)
            {
                Debug.LogWarning("WebGL Water: foam particle compute/shader missing; foam assets not wired.");
                return;
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

            if (particles.volume == null) particles.volume = particles.GetComponentInParent<WaterVolume>();
            particles.particleCompute = compute;
            particles.particleMaterial = material;
            particles.densityMaterial = densityMaterial;

            // Spray droplet material: same FoamParticles shader, a round droplet sprite so airborne
            // spray reads as droplets not foam clumps. Only set when unassigned (never clobber a
            // hand-picked one); if the droplet texture is missing the material just draws a soft dot.
            if (particles.sprayMaterial == null)
                particles.sprayMaterial = LoadOrCreateMaterial(materialFolder + "/FoamDroplet.mat", shader, m =>
                {
                    var droplet = LoadFlipbook(FoamDropletTexPath, TextureWrapMode.Clamp, true);
                    if (droplet != null) m.SetTexture(PropParticleTex, droplet);
                });

            EditorUtility.SetDirty(particles);
        }

        // Load (or create) the body's shared foam+splash profile: ONE asset per material folder,
        // the single surface both components read. Its sections default to Drive=on, so it takes
        // over the instant it is assigned.
        internal static WaterFoamProfile LoadOrCreateFoamProfile(string materialFolder)
        {
            string path = materialFolder + "/WaterFoamProfile.asset";
            var existing = AssetDatabase.LoadAssetAtPath<WaterFoamProfile>(path);
            if (existing != null) return existing;

            var profile = ScriptableObject.CreateInstance<WaterFoamProfile>();
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            return profile;
        }

        // Point BOTH of a body's foam components (GPU foam particles + splash emitter) at one
        // profile, so foam and splash are tweaked in a single place. Editor-safe (Undo + dirty);
        // either component may be absent.
        internal static void AssignFoamProfileToBody(WaterVolume body, WaterFoamProfile profile)
        {
            if (body == null || profile == null) return;

            var foam = body.GetComponent<WaterFoamParticles>();
            if (foam != null)
            {
                Undo.RecordObject(foam, "Assign Foam Profile");
                foam.profile = profile;
                EditorUtility.SetDirty(foam);
            }

            var emitter = body.splashEmitter != null
                ? body.splashEmitter
                : body.GetComponentInChildren<WaterSplashEmitter>();
            if (emitter != null)
            {
                Undo.RecordObject(emitter, "Assign Foam Profile");
                emitter.profile = profile;
                EditorUtility.SetDirty(emitter);
            }
        }

        // ---------------------------------------------------------------- demo props
        // A thin box collider under the water so sinking props have something to rest on.
        internal static GameObject CreateFloorCollider(Transform parent, Vector3 center, Vector3 size)
        {
            var go = NewUndoableGameObject("Floor Collider");
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        // ---------------------------------------------------------------- buoyant props
        // Built-in shapes for the wizard's one-click floater. CustomMesh takes a user mesh instead.
        internal enum FloaterShape { Cube, Sphere, Capsule, CustomMesh }

        const string BuoyantObjectName = "Buoyant Object";
        // Metres above the resolved water surface a new prop spawns, so it visibly drops in and
        // settles rather than popping half-submerged.
        const float PropSpawnHeightAboveWater = 1f;

        // The GEOMETRY of a one-click floater: primitive (mesh + fitting collider for free) or the
        // user's mesh with a convex MeshCollider + the pipeline's default material. The caller wires
        // the buoyancy component set on top (the wizard owns the preset/advanced tuning).
        internal static GameObject CreateBuoyantObjectBody(FloaterShape shape, Mesh customMesh, float size)
        {
            GameObject go;
            if (shape == FloaterShape.CustomMesh)
            {
                if (customMesh == null)
                {
                    Debug.LogError("[WebGL Water] Assign a mesh to create a custom-mesh floater.");
                    return null;
                }
                go = NewUndoableGameObject(BuoyantObjectName);
                go.AddComponent<MeshFilter>().sharedMesh = customMesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = DefaultPipelineMaterial();
                // Convex: a floater is a rigidbody, and non-convex MeshColliders can't collide as one.
                var meshCollider = go.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = customMesh;
                meshCollider.convex = true;
            }
            else
            {
                var primitive = shape == FloaterShape.Sphere ? PrimitiveType.Sphere
                              : shape == FloaterShape.Capsule ? PrimitiveType.Capsule
                                                              : PrimitiveType.Cube;
                go = GameObject.CreatePrimitive(primitive);
                go.name = BuoyantObjectName;
                Undo.RegisterCreatedObjectUndo(go, BuoyantObjectName);
            }
            go.transform.localScale = Vector3.one * Mathf.Max(size, MinPropSize);
            go.transform.position = PropSpawnPosition();
            return go;
        }

        const float MinPropSize = 0.01f;

        // Spawn above the primary body's surface when one exists (the prop drops in and floats);
        // else in front of the origin so it's still findable in an empty scene.
        internal static Vector3 PropSpawnPosition()
        {
            var bodies = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            WaterVolume primary = System.Array.Find(bodies, b => b.IsPrimary) ?? (bodies.Length > 0 ? bodies[0] : null);
            if (primary != null)
                return primary.VolumeCenter + Vector3.up * PropSpawnHeightAboveWater;
            return Vector3.up * PropSpawnHeightAboveWater;
        }

        // The active render pipeline's default lit material (URP here), so a custom-mesh prop
        // isn't magenta. Built-in fallback kept for safety. Internal: the showcase builder derives
        // its tinted prop materials from this material's shader.
        internal static Material DefaultPipelineMaterial()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null) return pipeline.defaultMaterial;
            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");
        }

        // ---------------------------------------------------------------- boat
        // Resurrected from the retired WaterBoatDemoBuilder: the same probe-buoyancy + BoatController
        // rig, now a wizard one-click that works with or without water in the scene. Tuning values are
        // the demo's proven set (they made the primitive boat float level and carve properly).
        const string BoatName = "Boat";
        const string BoatCabinName = "Cabin";
        static readonly Vector3 BoatHullScale = new Vector3(2f, 0.6f, 5f);   // wide, low, long
        static readonly Vector3 BoatCabinScale = new Vector3(1.2f, 0.5f, 1.8f);
        static readonly Vector3 BoatCabinLocalPosition = new Vector3(0f, 0.55f, -0.4f);
        const float BoatMass = 200f;
        const float BoatBuoyancy = 2.6f;
        const int BoatSamplesPerAxis = 3;   // 27 probes -> good roll/pitch + length torque
        // (Ripple-LOD objectWidth is derived from the hull's real footprint in CreateBoat -
        // max(x, z) of the fitted collider - which reproduces the old hand-tuned 5 m for the
        // primitive hull and scales correctly for custom models.)

        const string BoatHullName = "Hull";

        // ---- dry interior (water exclusion) -----------------------------------
        const string BoatDryInteriorName = "Dry Interior";
        // Primitive hull: the dry box is the hull box inset by a wall thickness per face, so the
        // surface's cut edge stays hidden INSIDE the hull walls (the content rule both reference
        // implementations state: the walls must cover the cut).
        const float DryInteriorWallInset = 0.05f; // metres, per face
        // Custom hull model: renderer bounds shrunk by this factor - a hull mesh is wider than
        // its interior, and the fitted box is a starting point the user refines on the child.
        const float DryInteriorBoundsShrink = 0.9f;
        // Floor on a fitted dry-box edge so an extreme inset/shrink on a tiny hull can never
        // collapse (or invert) the box.
        const float DryInteriorMinEdge = 0.05f; // metres

        /// <summary>A drivable boat: probe buoyancy, BoatController drive, wake + membership,
        /// optional splash. The ROOT stays at scale (1,1,1) and carries all physics (Rigidbody,
        /// fitted BoxCollider, buoyancy - WaterBuoyancy reads the collider on its own object);
        /// the visuals are CHILDREN, so a custom hull model drops in without inheriting the
        /// primitive hull's (2, 0.6, 5) stretch - and can be swapped later by replacing the child.
        /// withDryInterior adds a "Dry Interior" WaterExclusionVolume child fitted to the same
        /// box the collider uses, so the water surface never renders inside the hull.
        /// Undo-registered; the caller owns the undo group.</summary>
        internal static GameObject CreateBoat(GameObject hullModel, bool withSplash, bool withDryInterior)
        {
            var boat = NewUndoableGameObject(BoatName);
            boat.transform.position = PropSpawnPosition();

            Vector3 hullSize;
            Vector3 hullCenterLocal;
            if (hullModel != null)
            {
                GameObject visual = InstantiateVisual(hullModel, boat.transform);
                if (!TryGetCombinedRendererBounds(visual, out Bounds worldBounds))
                {
                    // A model with no renderers can't size the collider; fall back to the
                    // primitive hull's box so the boat still floats and drives predictably.
                    Debug.LogWarning("[WebGL Water] Hull model has no renderers; using the default hull-sized collider.");
                    worldBounds = new Bounds(boat.transform.position, BoatHullScale);
                }
                var box = boat.AddComponent<BoxCollider>();
                box.center = boat.transform.InverseTransformPoint(worldBounds.center);
                box.size = worldBounds.size; // root is unscaled + unrotated at creation, so world == local
                hullSize = worldBounds.size;
                hullCenterLocal = box.center;
            }
            else
            {
                AddPrimitiveHull(boat.transform);
                var box = boat.AddComponent<BoxCollider>();
                box.size = BoatHullScale;
                hullSize = BoatHullScale;
                hullCenterLocal = Vector3.zero;
            }

            var rigidbody = boat.AddComponent<Rigidbody>();
            rigidbody.mass = BoatMass;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

            var buoyancy = boat.AddComponent<WaterBuoyancy>();
            buoyancy.buoyancy = BoatBuoyancy;
            buoyancy.samplesPerAxis = BoatSamplesPerAxis;
            // Ripple LOD follows the hull's real footprint (a custom hull may be far from 5 m):
            // ignore ripples shorter than the hull so a big boat rides swell without buzzing.
            buoyancy.objectWidth = Mathf.Max(hullSize.x, hullSize.z);
            buoyancy.surfaceRelativeDrag = true;
            buoyancy.ignoreInteractiveRipples = true; // don't let the boat's own wake ripples propel it

            boat.AddComponent<BoatController>();
            boat.AddComponent<WaterMembership>();
            boat.AddComponent<WaterInteractable>(); // wake ripples
            if (withSplash) boat.AddComponent<WaterSplash>();
            if (withDryInterior) AddDryInterior(boat.transform, hullCenterLocal, hullSize, hullModel != null);
            return boat;
        }

        // The "boat doesn't fill with water" step: a WaterExclusionVolume over the hull so the
        // surface sheet never renders inside it. Sized from the SAME fitted box physics uses -
        // inset (primitive hull) or shrunk (custom model) so the cut edge stays behind the hull
        // walls. Visual-only (buoyancy reads the collider, not this); resize or delete the child
        // freely to fit an open cockpit. Creation is undo-registered like every build step.
        static void AddDryInterior(Transform root, Vector3 hullCenterLocal, Vector3 hullSize, bool customHull)
        {
            var dry = NewUndoableGameObject(BoatDryInteriorName);
            dry.transform.SetParent(root, worldPositionStays: false);
            dry.transform.localPosition = hullCenterLocal;

            Vector3 size = customHull
                ? hullSize * DryInteriorBoundsShrink
                : hullSize - 2f * DryInteriorWallInset * Vector3.one;
            var volume = dry.AddComponent<WaterExclusionVolume>();
            volume.size = Vector3.Max(size, DryInteriorMinEdge * Vector3.one);
            // The hull IS the boundary geometry (the content rule): water walls here would paint
            // fog colour over the cockpit interior. Bare standalone volumes keep them on.
            volume.drawWaterWalls = false;
        }

        // Instantiate the hull visual under the boat root (prefab-linked when the source is a
        // prefab asset, plain clone otherwise) at local identity - the ROOT owns placement.
        static GameObject InstantiateVisual(GameObject source, Transform parent)
        {
            var visual = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (visual == null) visual = Object.Instantiate(source);
            Undo.RegisterCreatedObjectUndo(visual, BoatName);
            visual.name = BoatHullName;
            visual.transform.SetParent(parent, worldPositionStays: false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            return visual;
        }

        // Combined world bounds of every renderer under the visual (a real boat model is usually
        // several meshes/materials). False when there is nothing to measure.
        static bool TryGetCombinedRendererBounds(GameObject visual, out Bounds bounds)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>();
            bounds = default;
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        // The visual-only primitive hull + cabin, as CHILDREN of the unscaled root: the hull cube
        // carries the (2, 0.6, 5) stretch itself, and the cabin sits in plain root space (its old
        // divide-out-the-hull-stretch dance is gone with the scaled root). Both colliders are
        // removed - physics lives on the root's fitted BoxCollider.
        static void AddPrimitiveHull(Transform root)
        {
            var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hull.name = BoatHullName;
            Undo.RegisterCreatedObjectUndo(hull, BoatHullName);
            hull.transform.SetParent(root, worldPositionStays: false);
            hull.transform.localScale = BoatHullScale;
            Object.DestroyImmediate(hull.GetComponent<Collider>());

            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = BoatCabinName;
            Undo.RegisterCreatedObjectUndo(cabin, BoatCabinName);
            cabin.transform.SetParent(root, worldPositionStays: false);
            // Same world pose as the old scaled-root rig: the cabin offset was authored in the
            // stretched hull's local space, so scale it out once here (one source, no new literals).
            cabin.transform.localPosition = Vector3.Scale(BoatCabinLocalPosition, BoatHullScale);
            cabin.transform.localScale = BoatCabinScale;
            Object.DestroyImmediate(cabin.GetComponent<Collider>());
        }

        /// <summary>Point the scene at the boat: swap the camera's controller for a follow camera
        /// (orbit/fly disabled, not destroyed - bodies may reference them) and focus the primary
        /// open-water body's ripple window on the hull instead of the trailing camera.</summary>
        internal static void FocusSceneOnBoat(GameObject boat)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                var orbit = cam.GetComponent<OrbitCamera>();
                if (orbit != null) { Undo.RecordObject(orbit, "Focus On Boat"); orbit.enabled = false; }
                var fly = cam.GetComponent<FlyCamera>();
                if (fly != null) { Undo.RecordObject(fly, "Focus On Boat"); fly.enabled = false; }
                var follow = cam.GetComponent<SimpleFollowCamera>();
                if (follow == null) follow = Undo.AddComponent<SimpleFollowCamera>(cam.gameObject);
                else Undo.RecordObject(follow, "Focus On Boat");
                follow.target = boat.transform;
            }

            var bodies = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            WaterVolume primary = System.Array.Find(bodies, b => b.IsPrimary);
            if (primary != null && primary.IsWindowed)
            {
                Undo.RecordObject(primary, "Focus On Boat");
                primary.simWindowFocus = boat.transform;
                EditorUtility.SetDirty(primary);
            }
        }

        // ---------------------------------------------------------------- exclusion volume
        // Standalone dry rooms (underwater houses, caves): a SCENE-OBJECT creator, so it lives
        // on the GameObject menu like Unity's own primitives - the Window/ MenuRoot hosts tool
        // windows, not scene objects. Boats get theirs automatically via CreateBoat.
        const string ExclusionVolumeMenuPath = "GameObject/AbstractOcclusion/Water Exclusion Volume";
        const string ExclusionVolumeObjectName = "Water Exclusion Volume";
        const int ExclusionVolumeMenuPriority = 10; // Unity's standard create-menu priority band
        static readonly Vector3 ExclusionVolumeDefaultSize = new Vector3(4f, 3f, 4f); // a small room

        [MenuItem(ExclusionVolumeMenuPath, false, ExclusionVolumeMenuPriority)]
        static void CreateExclusionVolume(MenuCommand command)
        {
            var go = NewUndoableGameObject(ExclusionVolumeObjectName);
            // Parent under the right-clicked object (context menu) like Unity's built-in creators.
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            go.AddComponent<WaterExclusionVolume>().size = ExclusionVolumeDefaultSize;
            Selection.activeGameObject = go;
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

        // Give a water surface material the animated foam pattern. Skipped silently when the
        // flipbook asset is absent: the shader's white default degrades to flat foam. Relief
        // is procedural now (finite differences of the pattern, like the ocean whitecap), so
        // no normal-map assignment; the generated FoamFlipbookNormal asset stays on disk for
        // old materials that still serialize it.
        internal static void AssignFoamFlipbook(Material m)
        {
            var flipbook = LoadFlipbook(FoamFlipbookPath, TextureWrapMode.Repeat, true);
            if (flipbook == null) return;
            m.SetTexture(PropFoamTex, flipbook);
            m.SetVector(PropFoamTexFrames, new Vector4(FoamFlipbookCols, FoamFlipbookRows, 0f, 0f));
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
        // other), then attach the orbit helper.
        internal static Camera SetUpCamera(out OrbitCamera orbit)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = NewUndoableGameObject("Water Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = MainCameraTag;
            }
            // Leave the camera's clear flags / background (skybox) and far clip alone: forcing a solid
            // black clear and a 100 m far plane clipped the user's scene. Only the framing (fov/near) is
            // set - recorded, because the camera may be the USER'S pre-existing one.
            Undo.RecordObject(cam, "Frame Water Camera");
            cam.fieldOfView = WaterVolume.CameraFieldOfView;
            cam.nearClipPlane = WaterVolume.CameraNearClip;

            orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = Undo.AddComponent<OrbitCamera>(cam.gameObject);
            else Undo.RecordObject(orbit, "Frame Water Camera");
            orbit.pivot = DemoOrbitPivot;
            orbit.pitch = DemoOrbitPitch;
            orbit.yaw = DemoOrbitYaw;
            orbit.distance = DemoOrbitDistance;
            // No PlanarReflection component here: per-body planar mirrors (WaterVolume.RenderPlanarMirror)
            // supersede the global camera-attached reflection, so attaching it (disabled) was dead weight.
            return cam;
        }

        // Single directional light: drives the analytic water + caustics (via the _LightDir global
        // the controller publishes) AND casts real URP shadows.
        internal static Light CreateSun(Transform parent)
        {
            var sunGO = NewUndoableGameObject("Sun");
            sunGO.transform.SetParent(parent);
            var sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = DefaultSunIntensity;
            sun.transform.rotation = Quaternion.LookRotation(-DefaultSunTowardLight.normalized);
            return sun;
        }

        // Hierarchy names for the splash feature: ONE root GO holding the emitter, with
        // both particle systems as clearly-labelled children (the old flat siblings
        // "Splash Particles"/"Splash Crown" read as two unrelated features).
        internal const string SplashRootName = "Water Splash FX";
        internal const string SplashDropletChildName = "Droplet Spray (CPU Fallback)";
        internal const string SplashCrownChildName = "Crown Ring";

        // Shared, fully editable splash particles (drift droplets + a flipbook crown).
        // Materials are create-once assets on the lit splash shader, so hand-tuning
        // survives rebuilds (same convention as the water/foam-particle materials).
        // One root so the hierarchy reads as a single feature: the emitter on the root
        // drives both children. "Droplet Spray" is the CPU fallback - bodies with an
        // active GPU WaterFoamParticles route droplets there instead, so it only bursts
        // on non-GPU bodies. "Crown Ring" always plays on both paths.
        internal static WaterSplashEmitter CreateSplashEmitter(Transform parent)
        {
            var rootGO = NewUndoableGameObject(SplashRootName);
            rootGO.transform.SetParent(parent);
            var splashEmitter = rootGO.AddComponent<WaterSplashEmitter>();

            var splashGO = NewUndoableGameObject(SplashDropletChildName);
            splashGO.transform.SetParent(rootGO.transform);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            var splashPSR = splashGO.GetComponent<ParticleSystemRenderer>();
            splashPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            // Render mode is owned by ConfigureForDrift (stretched billboards: fast droplets
            // streak along their motion) - no override here.
            splashEmitter.particles = splashPS;

            var crownGO = NewUndoableGameObject(SplashCrownChildName);
            crownGO.transform.SetParent(rootGO.transform);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, CrownSheetCols, CrownSheetRows);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);
            crownPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashCrownMaterialPath,
                LoadOrProvisionCrownSheet());
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
                LoadOrProvisionCrownSheet());
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
            var go = NewUndoableGameObject(name);
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
            // WITH a mip chain: the water surface samples this cube at a roughness-driven mip
            // (texCUBElod in WaterSurface.shader), so a rough/distant surface reflects a BLURRED
            // sky. Without mips that lod sample silently clamps to mip 0 and the blur is dead.
            // Apply() below regenerates the mips from the authored mip 0 (its default).
            var cube = new Cubemap(size, TextureFormat.RGB24, true);
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
            return SavePngAsset(path, tex, imp =>
            {
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.mipmapEnabled = true;
            });
        }

        // Packed droplet sprite (KWS channel layout, consumed by SplashParticles' packed path):
        // R = mass (round falloff), G = shine (tight hot core, cubed in the shader),
        // B = dissolve noise (lifetime burn threshold), A = thickness (soft-fade band).
        static Texture2D LoadOrBuildDroplet(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            // 128: the 64px original went soft on big hero droplets (velocity-stretched
            // sprites magnify it further). All maths below are sprite-space, so the bump is
            // resolution-independent. Delete Generated/DropletPacked.png to regenerate.
            const int s = 128;
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
            return SavePngAsset(path, tex, imp =>
            {
                imp.sRGBTexture = false;          // channel-packed DATA, not color
                imp.alphaIsTransparency = false;  // A is thickness, not coverage
                imp.wrapMode = TextureWrapMode.Clamp;
            });
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

        // The crown sheet is an authored 8x8 art asset (not procedurally buildable like the droplet),
        // and it lives only in the package's Samples~ folder, which Unity does not import. So a project
        // that never imported the demos has nothing for LoadFlipbook to load and the crown renders
        // untextured. Provision it once by copying the packaged source into Generated, then load it with
        // the crown's import settings (packed DATA sheet -> linear, clamped, no mips).
        static Texture2D LoadOrProvisionCrownSheet()
        {
            if (!File.Exists(SplashCrownSheetPath))
            {
                string packagedSheet = PackagedCrownSheetPath();
                if (packagedSheet == null || !File.Exists(packagedSheet))
                {
                    Debug.LogWarning($"WebGL Water: crown flipbook not found in the package " +
                                     $"('{CrownSheetPackageRelativePath}'); the splash crown will be untextured.");
                    return null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(SplashCrownSheetPath));
                File.Copy(packagedSheet, SplashCrownSheetPath, overwrite: false);
                AssetDatabase.ImportAsset(SplashCrownSheetPath);
            }

            return LoadFlipbook(SplashCrownSheetPath, TextureWrapMode.Clamp, mipmaps: false, linear: true);
        }

        // Physical path of the crown sheet inside the package's Samples~ folder. resolvedPath differs
        // between embedded and registry/tarball installs, so it is resolved via the package system
        // rather than assumed to sit under the project's Packages folder.
        static string PackagedCrownSheetPath()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(WaterBuildKit).Assembly);
            return package == null ? null : Path.Combine(package.resolvedPath, CrownSheetPackageRelativePath);
        }

        // ---------------------------------------------------------------- helpers
        // Load + validate the water shaders. Fails fast (dialog + false) if a REQUIRED shader
        // (surface, caustics, compute) is missing; optional shaders only warn.
        internal static bool TryLoadShaders(out ShaderSet shaders)
        {
            shaders = new ShaderSet
            {
                Water = Shader.Find(ShaderWaterSurface),
                Pool = Shader.Find(ShaderAnalyticPool),
                Caustics = Shader.Find(ShaderCaustics),
                Obstacle = Shader.Find(ShaderObstacle),
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

        // ONE source for a body's asset wiring. This block existed as three hand-synced copies
        // (here, the prefab builder, the secondary-body cloner) whose "parity" comments prove it
        // had already drifted once - a new serialized slot now lands everywhere by construction.
        internal static void WireWaterVolumeAssets(WaterVolume volume, in ShaderSet shaders,
                                                   Mesh grid, Texture tiles, Cubemap sky, WaterQuality quality)
        {
            volume.simCompute = shaders.Compute;
            volume.causticsShader = shaders.Caustics;
            volume.obstacleShader = shaders.Obstacle;
            // Optional (oceans only): near-field caustics in the sim-window frame. Non-fatal if
            // absent - Shader.Find just leaves the field null and the pass no-ops.
            volume.largeBodyCausticsShader = Shader.Find(ShaderLargeBodyCaustics);
            // Optional: refracted-light object shadow into the caustic RT. Non-fatal if absent (the
            // occluder pass no-ops and object shadows stay on the un-refracted shadow map).
            volume.occluderShader = Shader.Find(ShaderCausticOccluder);
            // Optional (oceans only): the FFT-cascade wave compute. The runtime module only arms on
            // an ocean clipmap body AND with this assigned, so wiring it everywhere is inert for
            // pools/lakes. Non-fatal if absent (analytic large-wave fallback).
            volume.oceanFftCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(OceanFftComputePath);
            volume.waterMesh = grid;
            volume.tiles = tiles;
            volume.sky = sky;
            volume.Quality = quality;
        }

        // Copy-wiring for a body cloned NEXT TO an existing one (secondary bodies): same slots as
        // WireWaterVolumeAssets plus the shared scene refs, sourced from the live body so a scene
        // whose primary was hand-rewired clones faithfully.
        internal static void WireWaterVolumeFrom(WaterVolume target, WaterVolume source)
        {
            target.simCompute = source.simCompute;
            target.causticsShader = source.causticsShader;
            target.obstacleShader = source.obstacleShader;
            target.largeBodyCausticsShader = source.largeBodyCausticsShader;
            target.occluderShader = source.occluderShader;
            target.oceanFftCompute = source.oceanFftCompute;
            target.waterMesh = source.waterMesh;
            target.tiles = source.tiles;
            target.sky = source.sky;
            target.Quality = source.Quality;
            target.targetCamera = source.targetCamera;
            target.sun = source.sun;
        }

        // Every scene object the builders create goes through here, so a single Undo step
        // (grouped by the caller) removes an entire build - the editor assembly previously had
        // NO undo for creation at all.
        internal static GameObject NewUndoableGameObject(string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            return go;
        }

        internal static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // Create-once mesh asset: reuse what's on disk (the builders' meshes are deterministic
        // functions of named constants), build only when missing. Delete the asset to regenerate
        // after changing the constants.
        static Mesh LoadOrSaveMesh(string path, System.Func<Mesh> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            return existing != null ? existing : SaveAsset(build(), path);
        }

        static Cubemap LoadOrSaveCubemap(string path, System.Func<Cubemap> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            return existing != null ? existing : SaveCubemap(build(), path);
        }

        // Write a generated PNG and configure its importer in one guarded path: the old inline
        // copies cast AssetImporter.GetAtPath unchecked right after an unchecked File.WriteAllBytes,
        // so a failed write/import NRE'd halfway through a build.
        static Texture2D SavePngAsset(string path, Texture2D tex, System.Action<TextureImporter> configure)
        {
            try
            {
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            catch (System.IO.IOException ioException)
            {
                Debug.LogError($"[WebGL Water] Could not write '{path}': {ioException.Message}");
                return null;
            }
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                configure(importer);
                importer.SaveAndReimport();
            }
            else
            {
                Debug.LogError($"[WebGL Water] '{path}' imported without a TextureImporter; texture settings not applied.");
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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
