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
using System.Runtime.InteropServices;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("WebGL Water/Water Foam Particles")]
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
        const int CounterCount = 2; // ring cursor + per-frame spawn count

        // ---- Screen-space density foam (KWS). MUST match WaterFoamParticles.compute. ----
        const int TileGrid = 16;                    // spray-budget screen tiles per axis
        const int TileCount = TileGrid * TileGrid;
        const int SprayTileCap = 6;                 // max spray spawns per tile per frame
        const int DensityDownscale = 2;             // density buffer = camera target / this
        const float DensityWeightScale = 64f;       // fixed-point units per 1.0 of foam weight
        const float SpawnMaxDistance = 80f;         // stochastic distance LOD range (metres)
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
            public Vector3 _pad;
        }
        static readonly int BurstStride = Marshal.SizeOf<BurstRequest>();

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
        static readonly int ParticleStride = Marshal.SizeOf<FoamParticle>();

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
        static readonly int ID_FlipbookGrid = Shader.PropertyToID("_ParticleFlipbookGrid");
        static readonly int ID_FlipbookFps = Shader.PropertyToID("_ParticleFlipbookFps");
        static readonly int ID_SurfaceQuadsEnabled = Shader.PropertyToID("_SurfaceQuadsEnabled");

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
        static readonly int ID_BurstRequests = Shader.PropertyToID("BurstRequests");
        static readonly int ID_BurstRequestCount = Shader.PropertyToID("_BurstRequestCount");
        static readonly int ID_FoamTime = Shader.PropertyToID("_FoamTime");

        // Local compute keyword: turns on the FFT-crest spawn source for the ocean body only.
        const string KeywordOceanCrest = "OCEAN_CREST_FOAM";

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
        [SerializeField] internal FoamRenderMode renderMode = FoamRenderMode.ScreenSpaceDensity;
        [Tooltip("Material using the AbstractOcclusion/WebGpuWater/FoamDensityComposite shader. Required " +
                 "for Screen Space Density mode; the Water Wizard creates and assigns it.")]
        [SerializeField] internal Material densityMaterial;

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

        [Header("Motion")]
        [Tooltip("Gravity on spray droplets (world units/sec^2).")]
        [Range(0f, 20f)] [SerializeField] internal float gravity = 4f;
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
        bool _densityValidThisFrame; // rasterized this frame (a camera was available)
        MaterialPropertyBlock _mpb;
        MaterialPropertyBlock _densityMpb;

        bool DensityModeActive => renderMode == FoamRenderMode.ScreenSpaceDensity
                                  && _densitySupported && densityMaterial != null;

        void OnEnable()
        {
            if (volume == null) volume = GetComponent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterFoamParticles: no WaterVolume assigned or found on this GameObject.", this);
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

            // Tier cap first: the whole pool is drawn every frame (dead slots emit degenerate
            // quads), so weak devices pay for capacity whether particles are alive or not.
            // Relies on WaterVolume's earlier execution order (-50) having applied its tier.
            int budget = Mathf.Min(capacity, volume.FoamParticleBudget);
            _capacityPow2 = Mathf.NextPowerOfTwo(Mathf.Max(UpdateThreadGroupSize, budget));
            _particles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacityPow2, ParticleStride);
            _particles.SetData(new FoamParticle[_capacityPow2]); // life = 0 -> every slot dead
            _counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, CounterCount, sizeof(uint));
            _counters.SetData(new uint[CounterCount]);
            _tileCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TileCount, sizeof(uint));
            _tileCounts.SetData(new uint[TileCount]);
            _burstRequests = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxBurstsPerFrame, BurstStride);
            _burstUpload = new BurstRequest[MaxBurstsPerFrame];

            _mpb = new MaterialPropertyBlock();
            _densityMpb = new MaterialPropertyBlock();
        }

        void OnDisable()
        {
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
            if (volume == null || !volume.isActiveAndEnabled) return;
            if (volume.SimStateTexture == null || volume.FoamMaskTexture == null) return;
            // Spawn when the 2D foam sim is on OR this is an ocean (whose FFT crests are the source).
            if (!volume.Foam && !volume.OceanFftActive) return;

            // The density splat + spawn-quality projections follow the main camera. In views
            // without one (or with the sim paused) the density field would be stale/unanchored,
            // so those frames fall back to reprojectable quads automatically.
            Camera densityCamera = Camera.main;
            _densityValidThisFrame = false;

            if (volume.IsSimulating && Time.deltaTime > 0f)
                DispatchSimulation(Time.deltaTime, densityCamera);

            if (volume.IsVisibleToCamera)
                Draw();
        }

        void DispatchSimulation(float dt, Camera densityCamera)
        {
            ComputeShader cs = particleCompute;
            volume.WriteSimFrameUniforms(cs);
            // Hero wave: lip-spray source in Spawn + base height in the density glue. The shared
            // struct binder keeps packing identical to every other GPU consumer; inactive = inert.
            volume.HeroWaveState.BindTo(cs);
            // Surf breaker fronts: plunging-lip spray source in Spawn + shoal/front height in the
            // density glue (RasterizeDensity). Same binder as the ripple-sim foam injection, so
            // the particles' front evaluation can never drift from the injected whitewash their
            // spawns ride on. Inactive (no shore/surf) = inert.
            WaterSimulation.ShoreFoamState shoreFoam = volume.BuildShoreFoamState();
            shoreFoam.BindTo(cs, _kSpawn);
            shoreFoam.BindTo(cs, _kRasterizeDensity);

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
            cs.SetFloat(ID_TexelWorldArea, volume.SimTexelWorldArea);

            cs.SetFloat(ID_Gravity, gravity);
            cs.SetFloat(ID_FlowDrift, flowDrift);
            cs.SetVector(ID_WindDrift, WindDriftWorld());
            cs.SetFloat(ID_Drag, drag);

            // Camera-driven spawn quality (stochastic distance LOD + spray tile budget) and the
            // density projection. Without a camera both are disabled and spawning is unchanged.
            if (densityCamera != null)
            {
                Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(densityCamera.projectionMatrix, false);
                Matrix4x4 viewProj = gpuProj * densityCamera.worldToCameraMatrix;
                cs.SetMatrix(ID_DensityViewProj, viewProj);
                cs.SetFloat(ID_DensityProj11, Mathf.Abs(gpuProj.m11));
                Vector3 camPos = densityCamera.transform.position;
                cs.SetVector(ID_SpawnCameraXZ, new Vector2(camPos.x, camPos.z));
                cs.SetFloat(ID_SpawnMaxDistance, SpawnMaxDistance);
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
            bool oceanCrest = volume.OceanFftActive && volume.OceanFftNormalTexture != null;
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
            cs.Dispatch(_kUpdate, _capacityPow2 / UpdateThreadGroupSize, 1, 1);

            // ---- Screen-space density splat (KWS): clear, then rasterize every floating
            // particle into the density + min-depth buffers for this camera. ----
            if (DensityModeActive && densityCamera != null)
            {
                var size = new Vector2Int(
                    Mathf.Max(1, densityCamera.pixelWidth / DensityDownscale),
                    Mathf.Max(1, densityCamera.pixelHeight / DensityDownscale));
                EnsureDensityBuffers(size);

                cs.SetInts(ID_DensitySize, size.x, size.y);
                cs.SetFloat(ID_DensityWeightScale, DensityWeightScale);

                int texelCount = size.x * size.y;
                cs.SetBuffer(_kClearDensity, ID_DensityBuffer, _density);
                cs.SetBuffer(_kClearDensity, ID_DensityDepth, _densityDepth);
                cs.Dispatch(_kClearDensity,
                            (texelCount + UpdateThreadGroupSize - 1) / UpdateThreadGroupSize, 1, 1);

                cs.SetBuffer(_kRasterizeDensity, ID_Particles, _particles);
                cs.SetBuffer(_kRasterizeDensity, ID_DensityBuffer, _density);
                cs.SetBuffer(_kRasterizeDensity, ID_DensityDepth, _densityDepth);
                // The surface-height glue reads the FFT cascade on oceans and the 2D sim on
                // pools; bind only what this variant declares (unused binds hard-error on
                // some backends, mirroring the Spawn kernel's pattern).
                if (oceanCrest && volume.OceanFftSpatialTexture != null)
                {
                    cs.SetTexture(_kRasterizeDensity, ID_OceanFftSpatial, volume.OceanFftSpatialTexture);
                    cs.SetFloat(ID_OceanFftAmplitude, volume.LargeWaveAmplitudeEffective);
                }
                else
                {
                    cs.SetTexture(_kRasterizeDensity, ID_Sim, volume.SimStateTexture);
                }
                cs.Dispatch(_kRasterizeDensity, _capacityPow2 / UpdateThreadGroupSize, 1, 1);
                _densityValidThisFrame = true;
            }
        }

        /// <summary>Queue a splash burst of ballistic spray droplets at a surface point (world).
        /// Consumed next simulation dispatch; requests beyond the per-frame cap are dropped
        /// (soft budget, like the turbulence spawns). Droplet look/motion is this system's
        /// spray path, so event splashes match turbulence-thrown spray exactly.</summary>
        public void QueueSplashBurst(Vector3 surfacePos, float strength, float radius,
                                     int dropletCount, float upSpeed, float outSpeed)
        {
            if (!isActiveAndEnabled || _pendingBursts.Count >= MaxBurstsPerFrame) return;
            _pendingBursts.Add(new BurstRequest
            {
                center = surfacePos,
                radius = Mathf.Max(0f, radius),
                strength = Mathf.Clamp01(strength),
                upSpeed = Mathf.Max(0f, upSpeed),
                outSpeed = Mathf.Max(0f, outSpeed),
                seed = Random.value,
                count = Mathf.Clamp(dropletCount, 1, MaxBurstDroplets)
            });
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
            // The body's own uniforms (sim texture, volume frame, waves, sun) drive the
            // vertex shader; the particle buffer rides along in the same block.
            volume.WriteBodyProps(_mpb);
            _mpb.SetBuffer(ID_ParticlesShader, _particles);
            // Flipbook atlas + speed are driven from here (the single control point), not material sliders.
            _mpb.SetVector(ID_FlipbookGrid, new Vector4(Mathf.Max(1, flipbookGrid.x), Mathf.Max(1, flipbookGrid.y), 0f, 0f));
            _mpb.SetFloat(ID_FlipbookFps, flipbookFps); // 0 = static variant (unchanged); >0 animates the atlas
            // When the density field was rasterized this frame, the quad draw carries ONLY the
            // ballistic spray; otherwise (quads mode, no camera, paused sim) it draws everything.
            _mpb.SetFloat(ID_SurfaceQuadsEnabled, _densityValidThisFrame ? 0f : 1f);

            var rp = new RenderParams(particleMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _mpb
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, _capacityPow2 * VerticesPerParticle);

            if (_densityValidThisFrame)
                DrawDensityComposite();
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

            var rp = new RenderParams(densityMaterial)
            {
                worldBounds = volume.SimWorldBounds,
                matProps = _densityMpb
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, CompositeVertexCount);
        }
    }
}
