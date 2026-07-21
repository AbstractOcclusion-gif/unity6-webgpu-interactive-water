// WebGL Water - GPU foam particles (Unity 6 / URP port)
//
// Per-body foam/spray particle system, fully GPU-resident (KWS-inspired): a compute
// pass spawns particles where the body's foam sim is strong and drifts them with the
// surface flow; FoamParticles.shader draws the pool as procedural quads pulled from
// the buffer by SV_VertexID. No CPU readback, no Shuriken, no geometry shaders and
// no append buffers - every piece works on the WebGPU backend.
//
// Attach next to a WaterVolume (one system per body; buffers and draw follow that
// body's sim window, property block and cull/budget schedule).
using UnityEngine;
using UnityEngine.Rendering;
using System.Runtime.InteropServices;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Water Foam Particles")]
    public class WaterFoamParticles : MonoBehaviour
    {
        // Compute kernel names (must match WaterFoamParticles.compute).
        const string KernelBeginFrame = "BeginFrame";
        const string KernelSpawn = "Spawn";
        const string KernelSpawnBurst = "SpawnBurst";
        const string KernelUpdate = "Update";
        const string KernelClearDensity = "ClearDensity";
        const string KernelRasterizeDensity = "RasterizeDensity";

        // Thread-group sizes. MUST equal the [numthreads] in WaterFoamParticles.compute.
        const int SpawnThreadGroupSize = 8;
        const int UpdateThreadGroupSize = 64;

        const int VerticesPerParticle = 6;
        const int CounterCount = 3; // ring cursor + frame spawn count + frame burst count
                                    // (MUST match the COUNTER_* layout in the compute)

        // ---- Screen-space density foam (KWS). MUST match WaterFoamParticles.compute. ----
        const int TileGrid = 16;                    // spray-budget screen tiles per axis
        const int TileCount = TileGrid * TileGrid;
        const int SprayTileCap = 6;                 // max spray spawns per tile per frame
        const int DensityDownscale = 2;             // density buffer = camera target / this
        const float DensityWeightScale = 64f;       // fixed-point units per 1.0 of foam weight
        const int CompositeVertexCount = 3;         // one fullscreen triangle

        // ---- CPU-event splash bursts (spray unification). MUST match BurstRequest in
        // WaterFoamParticles.compute (48 bytes) and MAX_BURST_DROPLETS there. ----
        const int MaxBurstsPerFrame = 16;
        const int MaxBurstDroplets = 64;

        [StructLayout(LayoutKind.Sequential)]
        struct BurstRequest
        {
            public Vector3 center;
            public float radius, strength, upSpeed, outSpeed, seed, count;
            // Per-burst droplet life/size (was padding): splash/pump bursts tune on the
            // WaterSplashEmitter, fully independent of the ambient-mist spray ranges.
            public float lifeMin, lifeMax, size;
        }
        static readonly int BurstStride = Marshal.SizeOf<BurstRequest>();

        // Safety margin on the burst-keep-alive window (covers landing detection latency).
        const float BurstSimPadSeconds = 0.5f;
        // Until this time the sim/draw stay alive even with ambient foam OFF: event bursts
        // (pump/impact splashes) are independent of foam turbulence, so their droplets must
        // finish their airborne + deposited life after the last queued burst.
        float _burstSimActiveUntil;

        /// <summary>How the floating (surface) foam is rendered. Spray is always textured quads.</summary>
        public enum FoamRenderMode
        {
            /// <summary>KWS-style: particles accumulate into a screen-space density buffer;
            /// a fullscreen composite turns density into connected, lit foam.</summary>
            ScreenSpaceDensity,
            /// <summary>Classic per-particle textured quads (fallback; also used automatically
            /// when the device can't read structured buffers in the fragment stage).</summary>
            Quads
        }

        // Knuth's multiplicative-hash constant (2^32 / golden ratio): decorrelates the
        // per-frame GPU random seed from the plain frame counter.
        const uint FrameSeedHashPrime = 2654435761u;

        // One particle = 12 floats. MUST match FoamParticle in the compute + shader.
        [StructLayout(LayoutKind.Sequential)]
        struct FoamParticle
        {
            public Vector3 worldPos;
            public Vector3 velocity;
            public float age, life, size, seed, kind, strength;
        }

        // Compute/shader property ids.
        static readonly int ID_Particles = Shader.PropertyToID("Particles");
        static readonly int ID_ParticlesShader = Shader.PropertyToID("_Particles");
        static readonly int ID_Counters = Shader.PropertyToID("Counters");
        static readonly int ID_Sim = Shader.PropertyToID("Sim");
        static readonly int ID_FoamTex = Shader.PropertyToID("FoamTex");
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_Capacity = Shader.PropertyToID("_Capacity");
        static readonly int ID_FrameSeed = Shader.PropertyToID("_FrameSeed");
        static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        static readonly int ID_ExclusionCount = Shader.PropertyToID("_ExclusionCount");
        static readonly int ID_ExclusionWorldToBox = Shader.PropertyToID("_ExclusionWorldToBox");
        // Full-size persistent buffer (a matrix array's size locks at its first set); the
        // selection logic itself lives in WaterExclusionVolume.WriteVolumeUniforms - one
        // implementation (the edge-look buffers are null: the kill test only needs the boxes).
        static readonly Matrix4x4[] _exclusionMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
        static readonly int ID_SpawnThreshold = Shader.PropertyToID("_SpawnThreshold");
        static readonly int ID_SpawnRate = Shader.PropertyToID("_SpawnRate");
        static readonly int ID_MaxSpawnPerFrame = Shader.PropertyToID("_MaxSpawnPerFrame");
        static readonly int ID_SprayChance = Shader.PropertyToID("_SprayChance");
        static readonly int ID_SprayLaunchSpeed = Shader.PropertyToID("_SprayLaunchSpeed");
        static readonly int ID_LifeMin = Shader.PropertyToID("_LifeMin");
        static readonly int ID_LifeMax = Shader.PropertyToID("_LifeMax");
        static readonly int ID_SizeMin = Shader.PropertyToID("_SizeMin");
        static readonly int ID_SizeMax = Shader.PropertyToID("_SizeMax");
        static readonly int ID_TexelWorldArea = Shader.PropertyToID("_TexelWorldArea");
        static readonly int ID_Gravity = Shader.PropertyToID("_Gravity");
        static readonly int ID_FlowDrift = Shader.PropertyToID("_FlowDrift");
        static readonly int ID_WindDrift = Shader.PropertyToID("_WindDrift");
        static readonly int ID_Drag = Shader.PropertyToID("_Drag");
        static readonly int ID_OceanFftNormal = Shader.PropertyToID("_OceanFftNormal");
        static readonly int ID_OceanFftDomainSizes = Shader.PropertyToID("_OceanFftDomainSizes");
        static readonly int ID_OceanFftCascadeCount = Shader.PropertyToID("_OceanFftCascadeCount");
        static readonly int ID_CrestRoll = Shader.PropertyToID("_CrestRoll");
        static readonly int ID_DrawKind = Shader.PropertyToID("_DrawKind");
        static readonly int ID_SprayLifeMin = Shader.PropertyToID("_SprayLifeMin");
        static readonly int ID_SprayLifeMax = Shader.PropertyToID("_SprayLifeMax");
        static readonly int ID_SpraySizeMin = Shader.PropertyToID("_SpraySizeMin");
        static readonly int ID_SpraySizeMax = Shader.PropertyToID("_SpraySizeMax");
        static readonly int ID_DepositLifeMin = Shader.PropertyToID("_DepositLifeMin");
        static readonly int ID_DepositLifeMax = Shader.PropertyToID("_DepositLifeMax");
        static readonly int ID_DepositSizeMin = Shader.PropertyToID("_DepositSizeMin");
        static readonly int ID_DepositSizeMax = Shader.PropertyToID("_DepositSizeMax");
        // _DrawKind values for the foam/spray two-pass split (MUST match FoamParticles.shader).
        const float DrawKindFoam = 1f;
        const float DrawKindSpray = 2f;

        // Density foam + spawn quality (compute + composite shader).
        static readonly int ID_DensityBuffer = Shader.PropertyToID("DensityBuffer");
        static readonly int ID_DensityDepth = Shader.PropertyToID("DensityDepth");
        static readonly int ID_TileCounts = Shader.PropertyToID("TileCounts");
        static readonly int ID_DensitySize = Shader.PropertyToID("_DensitySize");
        static readonly int ID_DensityViewProj = Shader.PropertyToID("_DensityViewProj");
        static readonly int ID_DensityProj11 = Shader.PropertyToID("_DensityProj11");
        static readonly int ID_DensityWeightScale = Shader.PropertyToID("_DensityWeightScale");
        static readonly int ID_SpawnCameraXZ = Shader.PropertyToID("_SpawnCameraXZ");
        static readonly int ID_SpawnMaxDistance = Shader.PropertyToID("_SpawnMaxDistance");
        static readonly int ID_TileBudgetEnabled = Shader.PropertyToID("_TileBudgetEnabled");
        static readonly int ID_SprayTileCap = Shader.PropertyToID("_SprayTileCap");
        static readonly int ID_OceanFftSpatial = Shader.PropertyToID("_OceanFftSpatial");
        static readonly int ID_OceanFftAmplitude = Shader.PropertyToID("_OceanFftAmplitude");
        static readonly int ID_FoamDensityShader = Shader.PropertyToID("_FoamDensity");
        static readonly int ID_FoamDensityDepthShader = Shader.PropertyToID("_FoamDensityDepth");
        static readonly int ID_DensityInvViewProj = Shader.PropertyToID("_DensityInvViewProj");
        static readonly int ID_DensityCamPos = Shader.PropertyToID("_DensityCamPos");
        static readonly int ID_DensityCamForward = Shader.PropertyToID("_DensityCamForward");
        static readonly int ID_SizeHeroPower = Shader.PropertyToID("_SizeHeroPower");
        static readonly int ID_BurstRequests = Shader.PropertyToID("BurstRequests");
        static readonly int ID_BurstRequestCount = Shader.PropertyToID("_BurstRequestCount");
        static readonly int ID_FoamTime = Shader.PropertyToID("_FoamTime");

        // Local compute keyword: turns on the FFT-crest spawn source for the ocean body only.
        const string KeywordOceanCrest = "OCEAN_CREST_FOAM";

        [Tooltip("Master switch for this foam-particle system: off skips ALL particles - simulation, " +
                 "spawning, splash bursts and drawing (no compute dispatch). Ambient foam and event " +
                 "splashes both stop.")]
        [SerializeField] internal bool useParticles = true;

        /// <summary>Body-wide particle master. False = this body emits no foam AND no splash particles;
        /// WaterSplashEmitter reads this so the splash crown/droplets obey the same one switch.</summary>
        internal bool UseParticles => useParticles;

        [Header("Wiring")]
        [Tooltip("The water body this system spawns from. Defaults to the WaterVolume on this GameObject.")]
        [SerializeField] internal WaterVolume volume;
        [Tooltip("WaterFoamParticles.compute (spawn/update kernels). Required.")]
        [SerializeField] internal ComputeShader particleCompute;
        [Tooltip("Material using the AbstractOcclusion/WebGpuWater/FoamParticles shader. Required; the Water " +
                 "Wizard (Window > AbstractOcclusion > WebGpuWater > Water Wizard) saves a tweakable " +
                 "material asset and assigns it here.")]
        [SerializeField] internal Material particleMaterial;
        [Tooltip("How the floating foam is rendered. Screen Space Density (KWS-style) accumulates " +
                 "particles into a density field and shades it as connected foam; Quads draws every " +
                 "particle as its own textured billboard. Spray droplets are always billboards.")]
        [SerializeField] internal FoamRenderMode renderMode = FoamRenderMode.Quads;
        [Tooltip("Material using the AbstractOcclusion/WebGpuWater/FoamDensityComposite shader. Required " +
                 "for Screen Space Density mode; the Water Wizard creates and assigns it.")]
        [SerializeField] internal Material densityMaterial;
        [Tooltip("Optional master foam profile: when assigned, its driven sections override the " +
                 "fields below every frame and push the shared look (tint/opacity/atlas, veil " +
                 "values) over the materials via the property block - ONE asset to tune a body's " +
                 "whole foam. None = this component's own values, exactly as before.")]
        [SerializeField] internal WaterFoamProfile profile;

        [Header("Pool")]
        [Tooltip("Particle pool size; rounded up to a power of two. Oldest particles are recycled when full.")]
        [Range(256, 65536)] [SerializeField] internal int capacity = 4096;

        [Header("Spawning")]
        [Tooltip("Foam level (0..1) below which no particles spawn.")]
        [Range(0f, 1f)] [SerializeField] internal float spawnThreshold = 0.25f;
        [Tooltip("Expected spawns per second per square world-unit of fully-foamed water.")]
        [Range(0f, 200f)] [SerializeField] internal float spawnRate = 30f;
        [Tooltip("Hard cap on spawns per frame (spreads bursts over a few frames).")]
        [Range(16, 4096)] [SerializeField] internal int maxSpawnPerFrame = 256;
        [Tooltip("Fraction of spawns thrown as ballistic spray instead of floating foam.")]
        [Range(0f, 1f)] [SerializeField] internal float sprayChance = 0.15f;
        [Tooltip("Initial upward speed of spray droplets (world units/sec).")]
        [Range(0f, 5f)] [SerializeField] internal float sprayLaunchSpeed = 0.6f;

        [Header("Look & life")]
        [Tooltip("Particle lifetime range (seconds).")]
        [SerializeField] internal Vector2 lifeRange = new Vector2(1.5f, 4f);
        [Tooltip("Particle world half-size range.")]
        [SerializeField] internal Vector2 sizeRange = new Vector2(0.02f, 0.06f);
        [Tooltip("Size distribution bias (KWS): 1 = uniform sizes across the range; higher = most " +
                 "particles stay small with rare large 'hero' sprites - instant variety without " +
                 "new art.")]
        [Range(1f, 6f)] [SerializeField] internal float sizeHeroPower = 1f;
        [Tooltip("Distance LOD range (m): FULL particle density out to ~60% of this, then a smooth " +
                 "falloff to a sparse dusting. Larger = foam reaches further before thinning (costs " +
                 "more live particles). 0 = no distance thinning at all.")]
        [Range(0f, 400f)] [SerializeField] internal float spawnMaxDistance = 120f;

        [Header("Spray droplets")]
        [Tooltip("Optional material for airborne spray droplets (their own look). None = draw spray with " +
                 "the foam Particle Material above.")]
        [SerializeField] internal Material sprayMaterial;
        [Tooltip("Spray droplet lifetime range (seconds) - independent of the floating-foam lifetime above.")]
        [SerializeField] internal Vector2 sprayLifeRange = new Vector2(0.5f, 1.2f);
        [Tooltip("Spray droplet world half-size range - independent of the floating-foam size above.")]
        [SerializeField] internal Vector2 spraySizeRange = new Vector2(0.02f, 0.05f);
        [Tooltip("Spray sprite atlas layout (cols, rows). (1,1) = a plain droplet texture, no flipbook.")]
        [SerializeField] internal Vector2Int sprayFlipbookGrid = new Vector2Int(1, 1);
        [Tooltip("Spray flipbook speed (frames/sec). 0 = a static droplet sprite.")]
        [Range(0f, 30f)] [SerializeField] internal float sprayFlipbookFps = 0f;
        // Deposited foam: what a LANDED droplet (mist or splash burst) turns into on the
        // surface. Defaults match the old implicit behaviour (droplet kept its spray
        // size/leftover life) closely enough that nothing jumps until tuned.
        [Tooltip("Lifetime range (seconds) of the foam patch a landed droplet deposits on the surface.")]
        [SerializeField] internal Vector2 depositLifeRange = new Vector2(0.5f, 1f);
        [Tooltip("World half-size range of the deposited foam patch.")]
        [SerializeField] internal Vector2 depositSizeRange = new Vector2(0.02f, 0.05f);

        [Header("Motion")]
        // Default 1 (not the old 4): at 4 droplets slammed down so fast that lifetime
        // tuning appeared to do nothing - the fall, not the life, ended the visible arc.
        [Tooltip("Gravity on spray droplets (world units/sec^2).")]
        [Range(0f, 20f)] [SerializeField] internal float gravity = 1f;
        [Tooltip("Drift speed along the surface flow, per unit of surface slope (world units/sec).")]
        [Range(0f, 2f)] [SerializeField] internal float flowDrift = 0.25f;
        [Tooltip("Constant downwind drift of floating foam (world units/sec).")]
        [Range(0f, 0.5f)] [SerializeField] internal float windDriftSpeed = 0.02f;
        [Tooltip("How quickly foam velocity relaxes to the driven flow (1/sec).")]
        [Range(0f, 10f)] [SerializeField] internal float drag = 2f;

        [Header("Wave particles (ocean crest only)")]
        [Tooltip("How fast whitecap foam rolls forward along the wave-travel direction (world units/sec). " +
                 "0 = foam sits still. Ocean bodies only; ignored on pools.")]
        [Range(0f, 4f)] [SerializeField] internal float crestRollSpeed = 0.6f;
        [Tooltip("Foam sprite atlas layout (columns, rows). (1,1) = a plain foam texture (no flipbook); " +
                 "(2,2) = a 4-frame sheet, etc. Optional, like the surface foam's flipbook grid.")]
        [SerializeField] internal Vector2Int flipbookGrid = new Vector2Int(2, 2);
        [Tooltip("Flipbook animation speed of the foam sprite over its life (frames/sec). 0 = each particle " +
                 "shows one fixed atlas cell (or the plain texture at grid 1x1); higher = the foam churns " +
                 "through the frames as it lives. This is the ONE place to set particle flipbook speed.")]
        [Range(0f, 30f)] [SerializeField] internal float flipbookFps = 0f;

        GraphicsBuffer _particles;
        GraphicsBuffer _counters;
        GraphicsBuffer _tileCounts;
        GraphicsBuffer _density;
        GraphicsBuffer _densityDepth;
        GraphicsBuffer _burstRequests;
        readonly System.Collections.Generic.List<BurstRequest> _pendingBursts =
            new System.Collections.Generic.List<BurstRequest>(MaxBurstsPerFrame);
        BurstRequest[] _burstUpload;
        int _kBeginFrame, _kSpawn, _kSpawnBurst, _kUpdate, _kClearDensity, _kRasterizeDensity;
        int _capacityPow2;
        Vector2Int _densitySize;
        bool _densitySupported;
        // Density-splat scheduling: the splat is dispatched in beginCameraRendering (NOT in
        // LateUpdate) so it projects with the camera's FINAL transform for the frame. A camera
        // controller that also moves in LateUpdate (OrbitCamera does) can run after this
        // component; splatting from the pre-move transform made the whole foam veil lag the
        // camera by one frame of motion - "the foam drags with the camera", density mode only
        // (the quad path is re-projected by the render itself, so it never lagged).
        bool _densityPending;   // LateUpdate armed a splat; the render callback executes it
        Camera _densityCamera;  // the ONE camera the splat/composite pair is built for
        Matrix4x4 _densityViewProjThisFrame; // approx VP for the composite's breakup pattern only
        MaterialPropertyBlock _mpb;
        MaterialPropertyBlock _sprayMpb;
        MaterialPropertyBlock _densityMpb;

        bool DensityModeActive => renderMode == FoamRenderMode.ScreenSpaceDensity
                                  && _densitySupported && densityMaterial != null;

        void OnEnable()
        {
            // Parent lookup: the particle systems are often children of the body object.
            if (volume == null) volume = GetComponentInParent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterFoamParticles: no WaterVolume assigned or found in parents.", this);
                enabled = false;
                return;
            }
            if (particleCompute == null)
            {
                Debug.LogError("WaterFoamParticles: particleCompute (WaterFoamParticles.compute) not assigned.", this);
                enabled = false;
                return;
            }
            if (particleMaterial == null)
            {
                // No silent runtime material: it would be invisible in the project and
                // impossible to tweak. The Water Wizard creates and wires the asset.
                Debug.LogError("WaterFoamParticles: particleMaterial not assigned. Use " +
                               "'Window > AbstractOcclusion > WebGpuWater > Water Wizard' to generate " +
                               "and wire a material asset.", this);
                enabled = false;
                return;
            }

            // FoamParticles.shader pulls the particle buffer in the VERTEX stage. WebGPU
            // compatibility mode (older Android GPUs / constrained browsers) allows zero
            // vertex-stage storage buffers, so drawing there is a validation error. Degrade
            // to "no foam particles" instead of a broken build; surface foam still renders.
            if (SystemInfo.maxComputeBufferInputsVertex < 1)
            {
                Debug.LogWarning("WaterFoamParticles: this device does not support structured " +
                                 "buffers in the vertex stage (WebGPU compatibility mode?); " +
                                 "foam particles disabled on this body.", this);
                enabled = false;
                return;
            }

            _kBeginFrame = particleCompute.FindKernel(KernelBeginFrame);
            _kSpawn = particleCompute.FindKernel(KernelSpawn);
            _kSpawnBurst = particleCompute.FindKernel(KernelSpawnBurst);
            _kUpdate = particleCompute.FindKernel(KernelUpdate);
            _kClearDensity = particleCompute.FindKernel(KernelClearDensity);
            _kRasterizeDensity = particleCompute.FindKernel(KernelRasterizeDensity);

            // Density mode reads structured buffers in the FRAGMENT stage (density + depth).
            // Devices that can't (WebGPU compatibility mode) silently fall back to quads.
            _densitySupported = SystemInfo.maxComputeBufferInputsFragment >= 2;
            if (renderMode == FoamRenderMode.ScreenSpaceDensity && !_densitySupported)
                Debug.LogWarning("WaterFoamParticles: fragment-stage structured buffers unsupported " +
                                 "on this device; density foam falls back to quads.", this);
            if (renderMode == FoamRenderMode.ScreenSpaceDensity && densityMaterial == null)
                Debug.LogWarning("WaterFoamParticles: densityMaterial not assigned (run the Water " +
                                 "Wizard to create it); density foam falls back to quads.", this);

            // Shared pool recipe (tier cap + pow2 + dead-slot zeroing). Relies on
            // WaterVolume's earlier execution order (-50) having applied its tier.
            _capacityPow2 = WaterParticlePool.Allocate<FoamParticle>(
                capacity, volume.FoamParticleBudget, UpdateThreadGroupSize, CounterCount,
                out _particles, out _counters);
            _tileCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TileCount, sizeof(uint));
            _tileCounts.SetData(new uint[TileCount]);
            _burstRequests = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxBurstsPerFrame, BurstStride);
            _burstUpload = new BurstRequest[MaxBurstsPerFrame];

            _mpb = new MaterialPropertyBlock();
            _sprayMpb = new MaterialPropertyBlock();
            _densityMpb = new MaterialPropertyBlock();

            // The density splat runs right before its camera renders (final matrices - see
            // the _densityPending comment). SRP-only callback; this package is URP-only.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _densityPending = false;
            _particles?.Dispose(); _particles = null;
            _counters?.Dispose(); _counters = null;
            _tileCounts?.Dispose(); _tileCounts = null;
            _density?.Dispose(); _density = null;
            _densityDepth?.Dispose(); _densityDepth = null;
            _burstRequests?.Dispose(); _burstRequests = null;
            _pendingBursts.Clear();
            _densitySize = Vector2Int.zero;
        }

        // (Re)allocate the per-camera density buffers when the target size changes.
        void EnsureDensityBuffers(Vector2Int size)
        {
            if (size == _densitySize && _density != null) return;
            _density?.Dispose();
            _densityDepth?.Dispose();
            _densitySize = size;
            int count = Mathf.Max(1, size.x * size.y);
            _density = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            _densityDepth = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
        }

        // LateUpdate so the volume's Update has already stepped the sim and refreshed its
        // window/schedule for this frame.
        void LateUpdate()
        {
            if (!useParticles) return; // master gate: no simulation, no dispatch, no draw
            if (volume == null || !volume.isActiveAndEnabled) return;
            // Defensive: OnEnable can bail before allocating (compute/material assigned later in
            // the inspector, then the component re-enabled mid-setup) - never dispatch or draw
            // with a dead pool.
            if (_particles == null || _counters == null || particleCompute == null) return;
            if (volume.SimStateTexture == null || volume.FoamMaskTexture == null) return;
            // Ambient spawning needs the 2D foam sim ON or an ocean (FFT crests as source).
            // Event bursts do NOT: with both ambient sources off, keep dispatching through
            // the burst window so pump/impact splashes still spray (the Spawn kernel is
            // harmless then - the foam mask is black, so it early-outs per texel).
            bool ambientFoamActive = volume.Foam || volume.OceanFftActive;
            if (!ambientFoamActive && Time.time >= _burstSimActiveUntil) return;

            // Master profile: re-applied every frame (a handful of field copies), so retuning
            // the asset is live in play mode and no editor plumbing is needed.
            if (profile != null) profile.ApplyTo(this);

            // The density splat + spawn-quality projections follow the body's target camera when
            // one is assigned, else the main camera. In views without one (or with the sim paused) the density field
            // would be stale/unanchored, so those frames fall back to reprojectable quads.
            Camera densityCamera = volume.targetCamera != null ? volume.targetCamera : Camera.main;
            _densityPending = false;
            _densityCamera = densityCamera;

            if (volume.IsSimulating && Time.deltaTime > 0f)
                DispatchSimulation(Time.deltaTime, densityCamera);

            if (volume.IsVisibleToCamera)
                Draw();
        }

        void DispatchSimulation(float dt, Camera densityCamera)
        {
            ComputeShader cs = particleCompute;
            volume.WriteSimFrameUniforms(cs);
            // Surf breaker fronts: plunging-lip spray source in Spawn + shoal/front height in the
            // density glue (RasterizeDensity). Same binder as the ripple-sim foam injection, so
            // the particles' front evaluation can never drift from the injected whitewash their
            // spawns ride on. Inactive (no shore/surf) = inert.
            WaterSimulation.ShoreFoamState shoreFoam = volume.BuildShoreFoamState();
            shoreFoam.BindTo(cs, _kSpawn);
            shoreFoam.BindTo(cs, _kRasterizeDensity);
            // The Update kernel ALSO evaluates the surf front (the foam "glue", SurfSampleAt), so it
            // needs the same shore/surf textures bound - BindTo always binds a black fallback when there
            // is no shore layer - or the backend errors "_ShoreDepthTexSim not set" on a body that has
            // foam but no coast (e.g. the open ocean with the surf layer off).
            shoreFoam.BindTo(cs, _kUpdate);

            cs.SetFloat(ID_Size, volume.SimResolution);
            cs.SetInt(ID_Capacity, _capacityPow2);
            cs.SetInt(ID_FrameSeed, unchecked((int)(Time.frameCount * FrameSeedHashPrime)));
            cs.SetFloat(ID_DeltaTime, dt);
            cs.SetFloat(ID_FoamTime, Time.time); // slow clock for the curl/clump noise drift

            cs.SetFloat(ID_SpawnThreshold, spawnThreshold);
            cs.SetFloat(ID_SpawnRate, spawnRate);
            cs.SetInt(ID_MaxSpawnPerFrame, maxSpawnPerFrame);
            cs.SetFloat(ID_SprayChance, sprayChance);
            cs.SetFloat(ID_SprayLaunchSpeed, sprayLaunchSpeed);
            cs.SetFloat(ID_LifeMin, lifeRange.x);
            cs.SetFloat(ID_LifeMax, Mathf.Max(lifeRange.x, lifeRange.y));
            cs.SetFloat(ID_SizeMin, sizeRange.x);
            cs.SetFloat(ID_SizeMax, Mathf.Max(sizeRange.x, sizeRange.y));
            cs.SetFloat(ID_SizeHeroPower, Mathf.Max(1f, sizeHeroPower));
            // Spray droplets use their own size/life ranges (foam/spray split).
            cs.SetFloat(ID_SprayLifeMin, sprayLifeRange.x);
            cs.SetFloat(ID_SprayLifeMax, Mathf.Max(sprayLifeRange.x, sprayLifeRange.y));
            cs.SetFloat(ID_SpraySizeMin, spraySizeRange.x);
            cs.SetFloat(ID_SpraySizeMax, Mathf.Max(spraySizeRange.x, spraySizeRange.y));
            // Deposited foam ranges (landed droplets re-roll from these in the Update kernel).
            cs.SetFloat(ID_DepositLifeMin, depositLifeRange.x);
            cs.SetFloat(ID_DepositLifeMax, Mathf.Max(depositLifeRange.x, depositLifeRange.y));
            cs.SetFloat(ID_DepositSizeMin, depositSizeRange.x);
            cs.SetFloat(ID_DepositSizeMax, Mathf.Max(depositSizeRange.x, depositSizeRange.y));
            cs.SetFloat(ID_TexelWorldArea, volume.SimTexelWorldArea);

            cs.SetFloat(ID_Gravity, gravity);
            cs.SetFloat(ID_FlowDrift, flowDrift);
            cs.SetVector(ID_WindDrift, WindDriftWorld());
            cs.SetFloat(ID_Drag, drag);

            // Dry-interior exclusion volumes, bound EXPLICITLY like every other compute uniform
            // (this codebase never relies on Shader.SetGlobal* reaching compute kernels). The
            // Update kernel kills particles inside a volume; count 0 skips the test entirely.
            int exclusionCount = WaterExclusionVolume.WriteVolumeUniforms(_exclusionMatrices,
                null, null,
                densityCamera != null ? densityCamera.transform.position : volume.VolumeCenter);
            cs.SetFloat(ID_ExclusionCount, exclusionCount);
            if (exclusionCount > 0) cs.SetMatrixArray(ID_ExclusionWorldToBox, _exclusionMatrices);

            // Camera-driven spawn quality (stochastic distance LOD + spray tile budget) and the
            // density projection. Without a camera both are disabled and spawning is unchanged.
            if (densityCamera != null)
            {
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(densityCamera.projectionMatrix, false);
                Matrix4x4 viewProj = gpuProj * densityCamera.worldToCameraMatrix;
                cs.SetMatrix(ID_DensityViewProj, viewProj);
                _densityViewProjThisFrame = viewProj;
                cs.SetFloat(ID_DensityProj11, Mathf.Abs(gpuProj.m11));
                Vector3 camPos = densityCamera.transform.position;
                cs.SetVector(ID_SpawnCameraXZ, new Vector2(camPos.x, camPos.z));
                cs.SetFloat(ID_SpawnMaxDistance, spawnMaxDistance);
                cs.SetFloat(ID_TileBudgetEnabled, 1f);
                cs.SetInt(ID_SprayTileCap, SprayTileCap);
            }
            else
            {
                cs.SetFloat(ID_SpawnMaxDistance, 0f);
                cs.SetFloat(ID_TileBudgetEnabled, 0f);
            }

            cs.SetBuffer(_kBeginFrame, ID_Counters, _counters);
            cs.SetBuffer(_kBeginFrame, ID_TileCounts, _tileCounts);
            cs.Dispatch(_kBeginFrame, TileCount / UpdateThreadGroupSize, 1, 1);

            cs.SetBuffer(_kSpawn, ID_Particles, _particles);
            cs.SetBuffer(_kSpawn, ID_Counters, _counters);
            cs.SetBuffer(_kSpawn, ID_TileCounts, _tileCounts);
            cs.SetTexture(_kSpawn, ID_Sim, volume.SimStateTexture);
            cs.SetTexture(_kSpawn, ID_FoamTex, volume.FoamMaskTexture);

            // Ocean crest source: enable the keyword + bind the cascade whitecap array so the spawn
            // kernel can emit foam on breaking FFT crests. Pools leave it off (no cascade binding).
            // The spatial cascade is part of the gate: the OCEAN_CREST_FOAM variant's density glue
            // reads _OceanFftSpatial, and dispatching that variant with the texture missing is an
            // unbound-resource error on WebGPU (the old Sim fallback bound a texture the variant
            // never reads).
            bool oceanCrest = volume.OceanFftActive && volume.OceanFftNormalTexture != null
                              && volume.OceanFftSpatialTexture != null;
            if (oceanCrest)
            {
                cs.EnableKeyword(KeywordOceanCrest);
                cs.SetTexture(_kSpawn, ID_OceanFftNormal, volume.OceanFftNormalTexture);
                cs.SetVector(ID_OceanFftDomainSizes, volume.OceanFftDomainSizes);
                cs.SetFloat(ID_OceanFftCascadeCount, volume.OceanFftCascadeCount);
                cs.SetVector(ID_CrestRoll, CrestRollWorld()); // foam rolls along the wave direction
            }
            else
            {
                cs.DisableKeyword(KeywordOceanCrest);
            }

            int spawnGroups = volume.SimResolution / SpawnThreadGroupSize;
            cs.Dispatch(_kSpawn, spawnGroups, spawnGroups, 1);

            // CPU-queued splash bursts (rigidbody impacts, mouse splashes): one thread group per
            // request throws KIND_SPRAY droplets from the same pool, unifying all airborne spray
            // on one tech path (the Shuriken emitter keeps only the crown flipbook).
            if (_pendingBursts.Count > 0)
            {
                int burstCount = Mathf.Min(_pendingBursts.Count, MaxBurstsPerFrame);
                for (int i = 0; i < burstCount; i++) _burstUpload[i] = _pendingBursts[i];
                _pendingBursts.Clear();
                _burstRequests.SetData(_burstUpload, 0, 0, burstCount);
                cs.SetInt(ID_BurstRequestCount, burstCount);
                cs.SetBuffer(_kSpawnBurst, ID_Particles, _particles);
                cs.SetBuffer(_kSpawnBurst, ID_Counters, _counters);
                cs.SetBuffer(_kSpawnBurst, ID_BurstRequests, _burstRequests);
                cs.Dispatch(_kSpawnBurst, burstCount, 1, 1);
            }

            // Only the resources the Update kernel actually reads: binding an unused
            // slot is a hard error on some backends.
            cs.SetBuffer(_kUpdate, ID_Particles, _particles);
            cs.SetTexture(_kUpdate, ID_Sim, volume.SimStateTexture);
            // The whitecap-gated crest roll reads the cascade array in Update too (the
            // OCEAN_CREST_FOAM variant only; the keyword state above already matches).
            if (oceanCrest)
            {
                cs.SetTexture(_kUpdate, ID_OceanFftNormal, volume.OceanFftNormalTexture);
                // Update ALSO places crest foam on the FFT swell (the height/roll glue), so it reads the
                // spatial cascade + amplitude too - not just the normal. Binding only the normal left
                // _OceanFftSpatial unset -> "property not set" hard error on WebGPU. Amplitude is a global
                // float (0 if unset), set here so Update reads the real swell height rather than
                // RasterizeDensity's later value. Mirrors the RasterizeDensity binds below.
                cs.SetTexture(_kUpdate, ID_OceanFftSpatial, volume.OceanFftSpatialTexture);
                cs.SetFloat(ID_OceanFftAmplitude, volume.LargeWaveAmplitudeEffective);
            }
            cs.Dispatch(_kUpdate, _capacityPow2 / UpdateThreadGroupSize, 1, 1);

            // ---- Screen-space density splat (KWS): buffers + uniforms are prepared here, but
            // the clear + rasterize dispatches run in OnBeginCameraRendering with the camera's
            // FINAL matrices (see the _densityPending comment - splatting from the LateUpdate
            // transform made the veil lag any camera that also moves in LateUpdate). ----
            if (DensityModeActive && densityCamera != null)
            {
                var size = new Vector2Int(
                    Mathf.Max(1, densityCamera.pixelWidth / DensityDownscale),
                    Mathf.Max(1, densityCamera.pixelHeight / DensityDownscale));
                EnsureDensityBuffers(size);

                cs.SetInts(ID_DensitySize, size.x, size.y);
                cs.SetFloat(ID_DensityWeightScale, DensityWeightScale);
                _densityPending = true;
            }
        }

        // The deferred density splat: runs right before the density camera renders, so the
        // projection matches this frame's ACTUAL view exactly (no LateUpdate-order lag).
        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!useParticles) return; // master gate: never dispatch the deferred density splat while off
            if (!_densityPending || cam != _densityCamera) return;
            if (_particles == null || _density == null || _densityDepth == null) return;

            ComputeShader cs = particleCompute;
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
            Matrix4x4 viewProj = gpuProj * cam.worldToCameraMatrix;
            cs.SetMatrix(ID_DensityViewProj, viewProj);
            cs.SetFloat(ID_DensityProj11, Mathf.Abs(gpuProj.m11));

            int texelCount = _densitySize.x * _densitySize.y;
            cs.SetBuffer(_kClearDensity, ID_DensityBuffer, _density);
            cs.SetBuffer(_kClearDensity, ID_DensityDepth, _densityDepth);
            cs.Dispatch(_kClearDensity,
                        (texelCount + UpdateThreadGroupSize - 1) / UpdateThreadGroupSize, 1, 1);

            // The surface-height glue reads the FFT cascade on oceans and the 2D sim on
            // pools; bind only what this variant declares (unused binds hard-error on
            // some backends, mirroring the Spawn kernel's pattern). The shore binder re-runs
            // here because the kernel executes outside DispatchSimulation's bind scope.
            volume.BuildShoreFoamState().BindTo(cs, _kRasterizeDensity);
            cs.SetBuffer(_kRasterizeDensity, ID_Particles, _particles);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityBuffer, _density);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityDepth, _densityDepth);
            bool oceanCrest = volume.OceanFftActive && volume.OceanFftNormalTexture != null
                              && volume.OceanFftSpatialTexture != null;
            if (oceanCrest)
            {
                cs.SetTexture(_kRasterizeDensity, ID_OceanFftSpatial, volume.OceanFftSpatialTexture);
                cs.SetFloat(ID_OceanFftAmplitude, volume.LargeWaveAmplitudeEffective);
            }
            else
            {
                cs.SetTexture(_kRasterizeDensity, ID_Sim, volume.SimStateTexture);
            }
            cs.Dispatch(_kRasterizeDensity, _capacityPow2 / UpdateThreadGroupSize, 1, 1);
        }

        /// <summary>Queue a splash burst of ballistic spray droplets at a surface point (world).
        /// Consumed next simulation dispatch; requests beyond the per-frame cap are dropped
        /// (soft budget, like the turbulence spawns). Droplet look/motion is this system's
        /// spray path, so event splashes match turbulence-thrown spray exactly.</summary>
        public void QueueSplashBurst(Vector3 surfacePos, float strength, float radius,
                                     int dropletCount, float upSpeed, float outSpeed,
                                     Vector2 dropletLifeRange, float dropletSize)
        {
            if (!useParticles || !isActiveAndEnabled || _pendingBursts.Count >= MaxBurstsPerFrame) return;
            _pendingBursts.Add(new BurstRequest
            {
                center = surfacePos,
                radius = Mathf.Max(0f, radius),
                strength = Mathf.Clamp01(strength),
                upSpeed = Mathf.Max(0f, upSpeed),
                outSpeed = Mathf.Max(0f, outSpeed),
                seed = Random.value,
                count = Mathf.Clamp(dropletCount, 1, MaxBurstDroplets),
                lifeMin = Mathf.Max(0f, dropletLifeRange.x),
                lifeMax = Mathf.Max(dropletLifeRange.x, dropletLifeRange.y),
                size = Mathf.Max(0f, dropletSize)
            });
            // Keep the sim/draw alive (even with ambient foam OFF) until these droplets
            // have fully lived: airborne life + the deposited-foam life they roll on landing.
            float burstLifeSpan = Mathf.Max(dropletLifeRange.x, dropletLifeRange.y)
                                + Mathf.Max(depositLifeRange.x, depositLifeRange.y)
                                + BurstSimPadSeconds;
            _burstSimActiveUntil = Mathf.Max(_burstSimActiveUntil, Time.time + burstLifeSpan);
        }

        // Constant downwind drift in world space: the wave bank's heading convention is
        // 0 degrees = travelling toward +X in the body's local frame.
        Vector2 WindDriftWorld()
        {
            float radians = volume.LargeWaveHeadingRad;
            Vector3 local = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians));
            Vector3 world = volume.transform.rotation * local * windDriftSpeed;
            return new Vector2(world.x, world.z);
        }

        // Same heading as the wind drift, scaled by the crest-roll speed: whitecap foam is carried along
        // the wave-travel direction so it rolls forward with the breaking crest.
        Vector2 CrestRollWorld()
        {
            float radians = volume.LargeWaveHeadingRad;
            Vector3 local = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians));
            Vector3 world = volume.transform.rotation * local * crestRollSpeed;
            return new Vector2(world.x, world.z);
        }

        void Draw()
        {
            int vertexCount = _capacityPow2 * VerticesPerParticle;

            // Floating foam pass: its own material, look and flipbook. Skipped in density mode,
            // where the screen-space veil draws the foam instead (_DrawKind = foam-only). The body's
            // uniforms (sim texture, volume frame, waves, sun) drive the vertex shader; the particle
            // buffer rides along in the same block.
            if (!_densityPending)
            {
                volume.WriteBodyProps(_mpb);
                _mpb.SetBuffer(ID_ParticlesShader, _particles);
                WaterParticlePool.WriteFlipbook(_mpb, flipbookGrid, flipbookFps);
                if (profile != null) profile.WriteLook(_mpb); // shared look over the foam material
                _mpb.SetFloat(ID_DrawKind, DrawKindFoam);

                var foamRp = new RenderParams(particleMaterial)
                {
                    worldBounds = volume.SimWorldBounds,
                    matProps = _mpb
                };
                Graphics.RenderPrimitives(foamRp, MeshTopology.Triangles, vertexCount);
            }
            else
            {
                DrawDensityComposite();
            }

            // Spray pass: ALWAYS drawn as billboards, with its own droplet material (falls back to the
            // foam material when none is assigned) and its own flipbook (_DrawKind = spray-only). The
            // shared look's tint + opacity ride over the spray too (a "shared look" the spray ignored
            // read as a dead color/opacity knob); the spray keeps its OWN atlas/grid, so the shared
            // sprite sheet - authored for the foam's flipbook grid - is not forced onto it.
            Material sprayDrawMaterial = sprayMaterial != null ? sprayMaterial : particleMaterial;
            volume.WriteBodyProps(_sprayMpb);
            _sprayMpb.SetBuffer(ID_ParticlesShader, _particles);
            WaterParticlePool.WriteFlipbook(_sprayMpb, sprayFlipbookGrid, sprayFlipbookFps);
            if (profile != null) profile.WriteSprayLook(_sprayMpb);
            _sprayMpb.SetFloat(ID_DrawKind, DrawKindSpray);

            var sprayRp = new RenderParams(sprayDrawMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _sprayMpb
            };
            Graphics.RenderPrimitives(sprayRp, MeshTopology.Triangles, vertexCount);
        }

        // Fullscreen triangle that shades the splatted density as connected foam. The bounds
        // keep it culled with the body; queue Transparent+5 draws it over the water surface
        // but under the spray/splash billboards.
        void DrawDensityComposite()
        {
            _densityMpb.SetBuffer(ID_FoamDensityShader, _density);
            _densityMpb.SetBuffer(ID_FoamDensityDepthShader, _densityDepth);
            _densityMpb.SetVector(ID_DensitySize, new Vector4(_densitySize.x, _densitySize.y, 0f, 0f));
            _densityMpb.SetFloat(ID_DensityWeightScale, DensityWeightScale);
            // World-position reconstruction inputs for the breakup pattern. This block is
            // captured at draw-registration time, so it carries the LateUpdate approximation
            // of the camera transform - the splat/composite ALIGNMENT is exact (both run at
            // render time); only the world-space lace lookup can lag a frame, which a slow
            // tileable pattern never shows.
            _densityMpb.SetMatrix(ID_DensityInvViewProj, _densityViewProjThisFrame.inverse);
            Transform densityCamTransform = _densityCamera.transform;
            _densityMpb.SetVector(ID_DensityCamPos, densityCamTransform.position);
            _densityMpb.SetVector(ID_DensityCamForward, densityCamTransform.forward);
            // Veil values from the master profile ride over the material (assets stay clean).
            if (profile != null) profile.WriteVeil(_densityMpb);

            var rp = new RenderParams(densityMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _densityMpb,
                // The density field was projected with ONE camera's matrices; drawing the
                // composite into any other camera (scene view, secondary cams) shows a foam
                // layer that translates with the main camera. Gate it to its own camera -
                // other views keep the spray billboards, which are world-anchored.
                camera = _densityCamera
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, CompositeVertexCount);
        }
    }
}
