// WebGpuWater build kit - the shared build context and the fully-wired water body.
// This is the kit's entry point: create the context once, build the assets several bodies share,
// then stamp bodies from it. Everything else in the kit is a primitive this composes.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
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
                Debug.LogWarning("WebGpuWater: foam particle compute/shader missing; skipping particle setup.");
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
                Debug.LogWarning("WebGpuWater: foam particle compute/shader missing; foam assets not wired.");
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

    }
}
