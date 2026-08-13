// WebGpuWater - GPU foam particles (Unity 6 / URP port)
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
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Foam Particles")]
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
        const int CounterCount = 4; // ring cursor + ambient, burst and crest-fleck frame counts
                                    // (MUST match the COUNTER_* layout in the compute)
        const int CrestFleckHistoryPositionStrideBytes = sizeof(float) * 3;

        // ---- Screen-space density foam (KWS). MUST match WaterFoamParticles.compute. ----
        const int TileGrid = 16;                    // spray-budget screen tiles per axis
        const int TileCount = TileGrid * TileGrid;
        const int SprayTileCap = 6;                 // max spray spawns per tile per frame
        const int DensityDownscale = 2;             // density buffer = camera target / this
        const float DensityWeightScale = 64f;       // fixed-point units per 1.0 of foam weight
        const int CompositeVertexCount = 3;         // one fullscreen triangle

        // ---- CPU-event splash bursts (spray unification). MUST match BurstRequest in
        // WaterFoamParticles.compute (68 bytes) and MAX_BURST_DROPLETS there. ----
        // Internal so authoring tools QUOTE these caps instead of carrying copies of the numbers.
        // Per-frame GPU upload cap. Overflow CARRIES OVER to later frames (FIFO) instead of being
        // dropped: a hull outline wants 20-40 probes firing together, and the old drop always ate
        // the probes late in the array, i.e. one side of the boat.
        internal const int MaxBurstsPerFrame = 16;
        // Pending-queue bound across frames. Past it the OLDEST request is retired, because the
        // newest events carry the freshest positions - on a moving boat a stale burst would
        // spawn behind the hull.
        internal const int MaxPendingBursts = 64;
        const int MaxBurstDroplets = 64;


        [StructLayout(LayoutKind.Sequential)]
        struct BurstRequest
        {
            public Vector3 center;
            public float radius, strength, upSpeed, outSpeed, seed, count;
            // Per-burst droplet life/size (was padding): splash/pump bursts tune on the
            // WaterSplashEmitter, fully independent of the ambient-mist spray ranges.
            public float lifeMin, lifeMax, size;
            // Petal arc: which way the burst throws (world XZ, unit) and how wide the wedge is.
            // A ZERO direction is the legacy full ring, end to end - an old scene deserialises to
            // zero, flows here as zero, and hits the kernel's untouched r0 * 2pi line.
            public float dirX, dirZ, arcHalfRadians;
            // Lifts (or flattens) the whole burst in its own vertical plane; ZERO is untouched.
            // Independent of the arc - a full-ring burst tilts just as happily as a petal.
            public float elevationRadians;
            // Per-impact visual control. Kept in the request rather than on the body-wide pool so
            // a boat splash can fade without muting ambient whitecap spray.
            public float dropletOpacity;
        }
        static readonly int BurstStride = Marshal.SizeOf<BurstRequest>();

#if UNITY_EDITOR
        // ---- burst budget diagnostics (editor only; compiles to nothing in a build) ----
        //
        // Requests past MaxBurstsPerFrame are DROPPED, and always the ones that arrive late in the frame
        // - so a hull's probes lose whichever end of the array queues last, and a second caller can be
        // starved entirely by the first. That failure is invisible by design: QueueSplashBurst just
        // returns. These counters exist so it can be READ instead of inferred from what looks wrong.

        /// <summary>Bursts asked for this frame, accepted or not.</summary>
        internal int BurstsRequestedThisFrame { get; private set; }

        /// <summary>Bursts refused this frame because the per-frame cap was already full.</summary>
        internal int BurstsDroppedThisFrame { get; private set; }

        /// <summary>Every burst dropped since play started.</summary>
        internal int BurstsDroppedTotal { get; private set; }

        /// <summary>Bursts refused because particles are off entirely - a different fault from a drop.</summary>
        internal int BurstsSuppressedTotal { get; private set; }

        /// <summary>The busiest frame seen. The cap is <see cref="MaxBurstsPerFrame"/>.</summary>
        internal int PeakBurstsRequestedPerFrame { get; private set; }

        int _burstDiagnosticFrame = -1;

        // Keyed off the frame counter rather than the drain, so the numbers stay honest even when the
        // pool's own LateUpdate early-outs and never drains at all - which is itself worth seeing.
        void BeginBurstFrame()
        {
            if (_burstDiagnosticFrame == Time.frameCount) return;

            _burstDiagnosticFrame = Time.frameCount;
            PeakBurstsRequestedPerFrame = Mathf.Max(PeakBurstsRequestedPerFrame, BurstsRequestedThisFrame);
            BurstsRequestedThisFrame = 0;
            BurstsDroppedThisFrame = 0;
        }
#endif

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
            [InspectorName("Screen-Space Density (Experimental / Active Dev)")]
            ScreenSpaceDensity,
            /// <summary>Classic per-particle textured quads (fallback; also used automatically
            /// when the device can't read structured buffers in the fragment stage).</summary>
            Quads
        }

        // Knuth's multiplicative-hash constant (2^32 / golden ratio): decorrelates the
        // per-frame GPU random seed from the plain frame counter.
        const uint FrameSeedHashPrime = 2654435761u;

        // One particle = 13 floats. MUST match FoamParticle in the compute + shader - and that is now
        // machine-checked, see WaterWaveConstantsValidator's FoamParticle layout check.
        [StructLayout(LayoutKind.Sequential)]
        struct FoamParticle
        {
            public Vector3 worldPos;
            public Vector3 velocity;
            public float age, life, size, seed, kind, strength, opacity;
        }

        /// <summary>Bytes per GPU particle. The ONE derived source for every consumer's buffer stride.</summary>
        // Derived, never written down: WaterParticlePool used to hardcode 48 with a "MUST match" comment
        // beside it, which is exactly the copy that goes stale. The type stays private; only the size
        // crosses the boundary.
        internal static readonly int ParticleStrideBytes = Marshal.SizeOf<FoamParticle>();

        // Compute/shader property ids.
        static readonly int ID_Particles = Shader.PropertyToID("Particles");
        static readonly int ID_ParticlesShader = WaterShaderProps.Particles;
        static readonly int ID_ParticleOpacity = Shader.PropertyToID("_ParticleOpacity");
        static readonly int ID_Counters = Shader.PropertyToID("Counters");
        static readonly int ID_Sim = Shader.PropertyToID("Sim");
        static readonly int ID_SimHorizontalFlow = Shader.PropertyToID("SimHorizontalFlow");
        static readonly int ID_CrestFleckPreviousPositions =
            Shader.PropertyToID("CrestFleckPreviousPositions");
        static readonly int ID_CrestFleckPreviousPositionsShader =
            Shader.PropertyToID("_CrestFleckPreviousPositions");
        static readonly int ID_FoamTex = Shader.PropertyToID("FoamTex");
        static readonly int ID_Size = WaterShaderProps.Size;
        static readonly int ID_SimEdgeFadeTexels = Shader.PropertyToID("_SimEdgeFadeTexels");
        static readonly int ID_Capacity = Shader.PropertyToID("_Capacity");
        static readonly int ID_FrameSeed = Shader.PropertyToID("_FrameSeed");
        static readonly int ID_DeltaTime = Shader.PropertyToID("_DeltaTime");
        static readonly int ID_ExclusionCount = WaterShaderProps.ExclusionCount;
        static readonly int ID_ExclusionWorldToLocal = WaterShaderProps.ExclusionWorldToLocal;
        static readonly int ID_ExclusionShape = WaterShaderProps.ExclusionShape;
        static readonly int ID_ExclusionEdgeParams = WaterShaderProps.ExclusionEdgeParams;
        // Full-size persistent buffers (a global array's size locks at its first set); the
        // selection logic itself lives in WaterExclusionVolume.WriteVolumeUniforms - one
        // implementation. The kill/dissolve tests need the volumes' frames AND SHAPES (a shape-less
        // bind would cull particles against a box where the author placed a sphere) plus the
        // per-volume particle handling packed in the edge-params lane (affect flag + dissolve
        // speed); the edge COLOR buffer stays null - particles never shade the carve boundary.
        static readonly Matrix4x4[] _exclusionMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
        static readonly Vector4[] _exclusionShapes = new Vector4[WaterExclusionVolume.MaxVolumes];
        static readonly Vector4[] _exclusionEdgeParams = new Vector4[WaterExclusionVolume.MaxVolumes];
        static readonly int ID_SpawnThreshold = Shader.PropertyToID("_SpawnThreshold");
        static readonly int ID_SpawnRate = Shader.PropertyToID("_SpawnRate");
        static readonly int ID_MaxSpawnPerFrame = Shader.PropertyToID("_MaxSpawnPerFrame");
        static readonly int ID_SprayChance = Shader.PropertyToID("_SprayChance");
        static readonly int ID_SprayLaunchSpeed = Shader.PropertyToID("_SprayLaunchSpeed");
        static readonly int ID_RippleCrestFlecksEnabled = Shader.PropertyToID("_RippleCrestFlecksEnabled");
        static readonly int ID_RippleCrestFleckAmount = Shader.PropertyToID("_RippleCrestFleckAmount");
        static readonly int ID_RippleCrestFleckLifeMin = Shader.PropertyToID("_RippleCrestFleckLifeMin");
        static readonly int ID_RippleCrestFleckLifeMax = Shader.PropertyToID("_RippleCrestFleckLifeMax");
        static readonly int ID_RippleCrestFleckSizeMin = Shader.PropertyToID("_RippleCrestFleckSizeMin");
        static readonly int ID_RippleCrestFleckSizeMax = Shader.PropertyToID("_RippleCrestFleckSizeMax");
        static readonly int ID_RippleCrestFleckMotion = Shader.PropertyToID("_RippleCrestFleckMotion");
        static readonly int ID_RippleCrestFleckMaxPerFrame = Shader.PropertyToID("_RippleCrestFleckMaxPerFrame");
        static readonly int ID_LifeMin = Shader.PropertyToID("_LifeMin");
        static readonly int ID_LifeMax = Shader.PropertyToID("_LifeMax");
        static readonly int ID_SizeMin = Shader.PropertyToID("_SizeMin");
        static readonly int ID_SizeMax = Shader.PropertyToID("_SizeMax");
        static readonly int ID_TexelWorldArea = Shader.PropertyToID("_TexelWorldArea");
        static readonly int ID_Gravity = Shader.PropertyToID("_Gravity");
        static readonly int ID_FlowDrift = Shader.PropertyToID("_FlowDrift");
        static readonly int ID_WindDrift = Shader.PropertyToID("_WindDrift");
        static readonly int ID_Drag = Shader.PropertyToID("_Drag");
        static readonly int ID_OceanFftDomainSizes = WaterShaderProps.OceanFftDomainSizes;
        static readonly int ID_OceanFftCascadeCount = WaterShaderProps.OceanFftCascadeCount;
        static readonly int ID_DrawKind = Shader.PropertyToID("_DrawKind");
        static readonly int ID_SprayLifeMin = Shader.PropertyToID("_SprayLifeMin");
        static readonly int ID_SprayLifeMax = Shader.PropertyToID("_SprayLifeMax");
        static readonly int ID_SpraySizeMin = Shader.PropertyToID("_SpraySizeMin");
        static readonly int ID_SpraySizeMax = Shader.PropertyToID("_SpraySizeMax");
        static readonly int ID_DepositLifeMin = Shader.PropertyToID("_DepositLifeMin");
        static readonly int ID_DepositLifeMax = Shader.PropertyToID("_DepositLifeMax");
        static readonly int ID_DepositSizeMin = Shader.PropertyToID("_DepositSizeMin");
        static readonly int ID_DepositSizeMax = Shader.PropertyToID("_DepositSizeMax");
        static readonly int ID_BubbleAmount = Shader.PropertyToID("_BubbleAmount");
        static readonly int ID_BubbleRiseSpeed = Shader.PropertyToID("_BubbleRiseSpeed");
        static readonly int ID_BubbleLifeMin = Shader.PropertyToID("_BubbleLifeMin");
        static readonly int ID_BubbleLifeMax = Shader.PropertyToID("_BubbleLifeMax");
        static readonly int ID_BubbleSizeMin = Shader.PropertyToID("_BubbleSizeMin");
        static readonly int ID_BubbleSizeMax = Shader.PropertyToID("_BubbleSizeMax");
        static readonly int ID_BubbleWobble = Shader.PropertyToID("_BubbleWobble");
        // _DrawKind values for the foam/spray/bubble pass split (MUST match FoamParticles.shader).
        const float DrawKindFoam = 1f;
        const float DrawKindSpray = 2f;
        const float DrawKindBubble = 3f;
        const float DrawKindCrestFleck = 4f;
        internal const float MinimumDensitySurfaceSizeScale = 0.05f;
        internal const float MaximumDensitySurfaceSizeScale = 4f;
        internal const float DefaultDensitySurfaceSizeScale = 1f;

        // Density foam + spawn quality (compute + composite shader).
        static readonly int ID_DensityBuffer = Shader.PropertyToID("DensityBuffer");
        static readonly int ID_DensityDepth = Shader.PropertyToID("DensityDepth");
        static readonly int ID_DensityBufferTier1 = Shader.PropertyToID("DensityBufferTier1");
        static readonly int ID_DensityBufferTier2 = Shader.PropertyToID("DensityBufferTier2");
        static readonly int ID_TileCounts = Shader.PropertyToID("TileCounts");
        static readonly int ID_DensitySize = Shader.PropertyToID("_DensitySize");
        static readonly int ID_DensityViewProj = Shader.PropertyToID("_DensityViewProj");
        static readonly int ID_DensityProj11 = Shader.PropertyToID("_DensityProj11");
        static readonly int ID_DensityWeightScale = Shader.PropertyToID("_DensityWeightScale");
        static readonly int ID_DensitySurfaceSizeScale = Shader.PropertyToID("_DensitySurfaceSizeScale");
        static readonly int ID_SpawnCameraXZ = Shader.PropertyToID("_SpawnCameraXZ");
        static readonly int ID_SpawnMaxDistance = Shader.PropertyToID("_SpawnMaxDistance");
        static readonly int ID_TileBudgetEnabled = Shader.PropertyToID("_TileBudgetEnabled");
        static readonly int ID_SprayTileCap = Shader.PropertyToID("_SprayTileCap");
        static readonly int ID_OceanFftSpatial = Shader.PropertyToID("_OceanFftSpatial");
        static readonly int ID_OceanFftAmplitude = Shader.PropertyToID("_OceanFftAmplitude");
        static readonly int ID_OceanDirectionMap = WaterShaderProps.OceanDirectionMap;
        static readonly int ID_OceanAperiodicParams = WaterShaderProps.OceanAperiodicParams;
        static readonly int ID_OceanDirectionMapFrame = WaterShaderProps.OceanDirectionMapFrame;
        static readonly int ID_FoamDensityShader = Shader.PropertyToID("_FoamDensity");
        static readonly int ID_FoamDensityDepthShader = Shader.PropertyToID("_FoamDensityDepth");
        static readonly int ID_FoamDensityTier1Shader = Shader.PropertyToID("_FoamDensityTier1");
        static readonly int ID_FoamDensityTier2Shader = Shader.PropertyToID("_FoamDensityTier2");
        static readonly int ID_DensityInvViewProj = Shader.PropertyToID("_DensityInvViewProj");
        static readonly int ID_DensityCamPos = Shader.PropertyToID("_DensityCamPos");
        static readonly int ID_DensityCamForward = Shader.PropertyToID("_DensityCamForward");
        static readonly int ID_SizeHeroPower = Shader.PropertyToID("_SizeHeroPower");
        static readonly int ID_DensityStampTex = Shader.PropertyToID("_DensityStampTex");
        static readonly int ID_DensityStampGrid = Shader.PropertyToID("_DensityStampGrid");
        static readonly int ID_BurstRequests = Shader.PropertyToID("BurstRequests");
        static readonly int ID_BurstRequestCount = Shader.PropertyToID("_BurstRequestCount");
        static readonly int ID_FoamTime = Shader.PropertyToID("_FoamTime");

        // Local compute keyword, ocean bodies only: FFT placement glue (particles ride the swell) and
        // the ambient spawn gate (open-sea ambient sprite foam is OFF; only the surf lip throws).
        const string KeywordOceanFftGlue = "OCEAN_FFT_GLUE";

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
        [Tooltip("How the floating foam is rendered. Screen Space Density is EXPERIMENTAL / ACTIVE " +
                 "DEVELOPMENT; it accumulates particles into a density field and shades it as connected " +
                 "foam. Quads draws every particle as its own textured billboard. Spray droplets are " +
                 "always billboards.")]
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
        [Tooltip("EXPERIMENTAL / ACTIVE DEVELOPMENT. Allow the simulation foam mask and ripple crests " +
                 "to create autonomous surface particles. Off keeps only event splash droplets and " +
                 "the foam they deposit after landing.")]
        [SerializeField] internal bool simulationDrivenSpawning;
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
        [Header("Ripple crest flecks")]
        [Tooltip("Emit small floating flecks from moving ripple crests. Disabled by default to preserve existing scenes.")]
        [SerializeField] internal bool rippleCrestFlecksEnabled;
        [Tooltip("Crest-fleck density multiplier. One matches the KWS-style default recipe.")]
        [Range(0f, 4f)] [SerializeField] internal float rippleCrestFleckAmount = 1f;
        [Tooltip("Hard cap on crest flecks emitted each frame. Keeps a strong ripple from filling the shared pool.")]
        [Range(16, 4096)] [SerializeField] internal int rippleCrestFleckMaxPerFrame = 256;
        [Tooltip("Lifetime range of flecks emitted directly from ripple crests.")]
        [SerializeField] internal Vector2 rippleCrestFleckLifetimeRange = new Vector2(0.4f, 0.8f);
        [Tooltip("World half-size range of ripple crest flecks.")]
        [SerializeField] internal Vector2 rippleCrestFleckSizeRange = new Vector2(0.01f, 0.025f);
        [Tooltip("How strongly crest flecks keep their outward ripple-propagation motion.")]
        [Range(0f, 1f)] [SerializeField] internal float rippleCrestFleckMotion = 0.6f;

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
        [Range(0f, 1f)] [SerializeField] internal float surfaceFoamOpacity = 1f;
        [Range(0f, 1f)] [SerializeField] internal float sprayOpacity = 1f;
        [Range(0f, 1f)] [SerializeField] internal float bubbleOpacity = 1f;
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
        [Tooltip("Live render-time size multiplier for landed foam in Screen-Space Density mode. " +
                 "Unlike Landed Size, this updates particles that are already on the water.")]
        [Range(MinimumDensitySurfaceSizeScale, MaximumDensitySurfaceSizeScale)]
        [SerializeField] internal float densitySurfaceSizeScale = DefaultDensitySurfaceSizeScale;

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

        [Header("Bubbles (underwater, from splash bursts)")]
        [Tooltip("Bubble plume share of every splash/pump burst: bubbles injected DOWNWARD per droplet " +
                 "thrown up (0 = none, and the bubble draw pass is skipped). The same impact that " +
                 "throws spray drives air under the surface; the plume decelerates, then buoyancy " +
                 "rises it back to pop at the waterline as landed foam.")]
        [Range(0f, 1f)] [SerializeField] internal float bubbleAmount = 0.5f;
        [Tooltip("Terminal rise speed of the LARGEST bubbles (world units/sec). The measured band " +
                 "for mm-to-cm bubbles is 0.20-0.30; smaller bubbles rise proportionally slower.")]
        [Range(0.05f, 0.6f)] [SerializeField] internal float bubbleRiseSpeed = 0.25f;
        [Tooltip("Bubble lifetime range (seconds). A bubble that reaches the surface pops into a " +
                 "deposited foam fleck; one that ages out first dissolves underwater.")]
        [SerializeField] internal Vector2 bubbleLifeRange = new Vector2(2f, 4f);
        [Tooltip("Bubble sprite half-size range (world units), skewed toward small on spawn.")]
        [SerializeField] internal Vector2 bubbleSizeRange = new Vector2(0.015f, 0.05f);
        [Tooltip("Sideways wobble while rising. Physically only bubbles above ~2 mm zigzag, so the " +
                 "amplitude scales with bubble size.")]
        [Range(0f, 2f)] [SerializeField] internal float bubbleWobble = 1f;

        [Header("Foam flipbook")]
        [Tooltip("Foam sprite atlas layout (columns, rows). (1,1) = a plain foam texture (no flipbook); " +
                 "(2,2) = a 4-frame sheet, etc. Optional, like the surface foam's flipbook grid.")]
        [SerializeField] internal Vector2Int flipbookGrid = new Vector2Int(2, 2);
        [Tooltip("Flipbook animation speed of the foam sprite over its life (frames/sec). 0 = each particle " +
                 "shows one fixed atlas cell (or the plain texture at grid 1x1); higher = the foam churns " +
                 "through the frames as it lives. This is the ONE place to set particle flipbook speed.")]
        [Range(0f, 30f)] [SerializeField] internal float flipbookFps = 0f;

        GraphicsBuffer _particles;
        GraphicsBuffer _crestFleckPreviousPositions;
        GraphicsBuffer _counters;
        GraphicsBuffer _tileCounts;
        GraphicsBuffer _density;
        GraphicsBuffer _densityDepth;
        // KWS LOD tiers: half- and quarter-resolution splat buffers - a crest fleck's dot
        // size is the resolution of the tier it lands in (see WaterFoamParticles.compute).
        GraphicsBuffer _densityTier1;
        GraphicsBuffer _densityTier2;
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
        MaterialPropertyBlock _crestFleckMpb;
        MaterialPropertyBlock _bubbleMpb;
        MaterialPropertyBlock _densityMpb;

        bool DensityModeActive => renderMode == FoamRenderMode.ScreenSpaceDensity
                                  && _densitySupported && densityMaterial != null;

        // ---- after-fog particle reroute (the particle/fog SORTING fix) -------------------
        // On frames where the fullscreen underwater fog runs, the queue-time draws are
        // skipped and WaterUnderwaterFogFeature's after-fog pass calls RenderAfterFog
        // instead - otherwise the fog (which integrates to OPAQUE depth) paints the whole
        // water column's fog over every sprite. The sprite shaders price their own
        // camera->particle fog on those frames (WaterParticleFog.hlsl). Mirrors
        // WaterSplashEmitter's reroute of the Shuriken systems.

        /// <summary>Live components, drawn by the fog feature's after-fog particle pass.</summary>
        internal static readonly System.Collections.Generic.List<WaterFoamParticles> Live =
            new System.Collections.Generic.List<WaterFoamParticles>();

        internal static void ResetStaticState() => Live.Clear();

        bool _afterFogArmed;  // this frame's queue-time draws were skipped for the fog pass
        bool _rerouteDensity; // the skipped foam draw was the density composite, not quads

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
            _crestFleckPreviousPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _capacityPow2, CrestFleckHistoryPositionStrideBytes);
            _crestFleckPreviousPositions.SetData(new Vector3[_capacityPow2]);
            _tileCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, TileCount, sizeof(uint));
            _tileCounts.SetData(new uint[TileCount]);
            _burstRequests = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxBurstsPerFrame, BurstStride);
            _burstUpload = new BurstRequest[MaxBurstsPerFrame];

            _mpb = new MaterialPropertyBlock();
            _sprayMpb = new MaterialPropertyBlock();
            _crestFleckMpb = new MaterialPropertyBlock();
            _bubbleMpb = new MaterialPropertyBlock();
            _densityMpb = new MaterialPropertyBlock();

            // The density splat runs right before its camera renders (final matrices - see
            // the _densityPending comment). SRP-only callback; this package is URP-only.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;

            // Registered LAST: every bail-out above leaves the component off the after-fog
            // pass's list, so the pass never draws a half-initialised pool.
            if (!Live.Contains(this)) Live.Add(this);
        }

        void OnDisable()
        {
            Live.Remove(this);
            _afterFogArmed = false;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            _densityPending = false;
            _particles?.Dispose(); _particles = null;
            _crestFleckPreviousPositions?.Dispose(); _crestFleckPreviousPositions = null;
            _counters?.Dispose(); _counters = null;
            _tileCounts?.Dispose(); _tileCounts = null;
            _density?.Dispose(); _density = null;
            _densityDepth?.Dispose(); _densityDepth = null;
            _densityTier1?.Dispose(); _densityTier1 = null;
            _densityTier2?.Dispose(); _densityTier2 = null;
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
            _densityTier1?.Dispose();
            _densityTier2?.Dispose();
            _densitySize = size;
            int count = Mathf.Max(1, size.x * size.y);
            _density = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            _densityDepth = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            // Tier sizes use the SAME integer halving as DensityTierSize in the compute and
            // the composite, so all three agree on every odd-size edge case.
            int countTier1 = Mathf.Max(1, Mathf.Max(1, size.x >> 1) * Mathf.Max(1, size.y >> 1));
            int countTier2 = Mathf.Max(1, Mathf.Max(1, size.x >> 2) * Mathf.Max(1, size.y >> 2));
            _densityTier1 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, countTier1, sizeof(uint));
            _densityTier2 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, countTier2, sizeof(uint));
        }

        // LateUpdate so the volume's Update has already stepped the sim and refreshed its
        // window/schedule for this frame.
        void LateUpdate()
        {
            // Disarm BOTH deferred hooks first: any early-out below must leave the after-fog pass
            // with nothing to submit AND the OnBeginCameraRendering density splat disarmed (stale
            // property blocks from a previous frame are never re-drawn).
            //
            // _densityPending used to be cleared further down, AFTER five early-returns. Take any of
            // them - the volume disabled, its sim textures released, ambient foam lapsing past the
            // burst window - and the flag stayed set from the last good frame, so
            // OnBeginCameraRendering (which guards the buffers and the camera, but NOT the volume)
            // kept clearing and rasterising the density field every camera render, against a volume
            // that had already stopped: a full-pool dispatch per frame reading a released
            // SimStateTexture.
            _afterFogArmed = false;
            _densityPending = false;
            // Apply before every runtime gate. The profile is the authored source of truth even
            // while particles are disabled, the volume is idle, or GPU resources are unavailable.
            // Applying below those returns made profile-driven Motion appear broken until the
            // first active foam/burst frame happened to run.
            if (profile != null) profile.ApplyTo(this);
            if (!useParticles) return; // master gate: no simulation, no dispatch, no draw
            if (volume == null || !volume.isActiveAndEnabled) return;
            // Defensive: OnEnable can bail before allocating (compute/material assigned later in
            // the inspector, then the component re-enabled mid-setup) - never dispatch or draw
            // with a dead pool.
            if (_particles == null || _crestFleckPreviousPositions == null || _counters == null ||
                particleCompute == null) return;
            if (volume.SimStateTexture == null || volume.SimHorizontalFlowTexture == null ||
                volume.FoamMaskTexture == null) return;
            // Ambient spawning needs the 2D foam sim ON or an ocean (FFT crests as source).
            // Event bursts do NOT: with both ambient sources off, keep dispatching through
            // the burst window so pump/impact splashes still spray (the Spawn kernel is
            // harmless then - the foam mask is black, so it early-outs per texel).
            bool ambientFoamActive = volume.Foam || volume.OceanFftActive;
            if (!ambientFoamActive && Time.time >= _burstSimActiveUntil) return;

            // The density splat + spawn-quality projections follow the body's target camera when
            // one is assigned, else the main camera. In views without one (or with the sim paused) the density field
            // would be stale/unanchored, so those frames fall back to reprojectable quads.
            Camera densityCamera = volume.targetCamera != null ? volume.targetCamera : Camera.main;
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
            volume.WriteWaveUniforms(cs);
            // Surf breaker fronts: plunging-lip spray source in Spawn + shoal/front height in the
            // density glue (RasterizeDensity). Same binder as the ripple-sim foam injection, so
            // the particles' front evaluation can never drift from the injected whitewash their
            // spawns ride on. Inactive (no shore/surf) = inert.
            WaterSimulation.ShoreFoamState shoreFoam = volume.BuildShoreFoamState();
            if (simulationDrivenSpawning) shoreFoam.BindTo(cs, _kSpawn);
            shoreFoam.BindTo(cs, _kRasterizeDensity);
            // The Update kernel ALSO evaluates the surf front (the foam "glue", SurfSampleAt), so it
            // needs the same shore/surf textures bound - BindTo always binds a black fallback when there
            // is no shore layer - or the backend errors "_ShoreDepthTexSim not set" on a body that has
            // foam but no coast (e.g. the open ocean with the surf layer off).
            shoreFoam.BindTo(cs, _kUpdate);
            cs.SetFloat(ID_Size, volume.SimResolution);
            // Same band the surface fades its ripple over, so a particle and the water under it
            // agree on the height through the window border instead of by up to a full amplitude.
            cs.SetFloat(ID_SimEdgeFadeTexels, volume.simWindowEdgeFadeTexels);
            cs.SetInt(ID_Capacity, _capacityPow2);
            cs.SetInt(ID_FrameSeed, unchecked((int)(Time.frameCount * FrameSeedHashPrime)));
            cs.SetFloat(ID_DeltaTime, dt);
            cs.SetFloat(ID_FoamTime, Time.time); // slow clock for the curl/clump noise drift

            cs.SetFloat(ID_SpawnThreshold, spawnThreshold);
            cs.SetFloat(ID_SpawnRate, spawnRate);
            cs.SetInt(ID_MaxSpawnPerFrame, maxSpawnPerFrame);
            cs.SetFloat(ID_SprayChance, sprayChance);
            cs.SetFloat(ID_SprayLaunchSpeed, sprayLaunchSpeed);
            cs.SetFloat(ID_RippleCrestFlecksEnabled, rippleCrestFlecksEnabled ? 1f : 0f);
            cs.SetFloat(ID_RippleCrestFleckAmount, rippleCrestFleckAmount);
            cs.SetInt(ID_RippleCrestFleckMaxPerFrame, rippleCrestFleckMaxPerFrame);
            cs.SetFloat(ID_RippleCrestFleckLifeMin, rippleCrestFleckLifetimeRange.x);
            cs.SetFloat(ID_RippleCrestFleckLifeMax,
                Mathf.Max(rippleCrestFleckLifetimeRange.x, rippleCrestFleckLifetimeRange.y));
            cs.SetFloat(ID_RippleCrestFleckSizeMin, rippleCrestFleckSizeRange.x);
            cs.SetFloat(ID_RippleCrestFleckSizeMax,
                Mathf.Max(rippleCrestFleckSizeRange.x, rippleCrestFleckSizeRange.y));
            cs.SetFloat(ID_RippleCrestFleckMotion, rippleCrestFleckMotion);
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
            // Bubble plume tuning (KIND_BUBBLE: injected by SpawnBurst, risen/popped in Update).
            cs.SetFloat(ID_BubbleAmount, bubbleAmount);
            cs.SetFloat(ID_BubbleRiseSpeed, bubbleRiseSpeed);
            cs.SetFloat(ID_BubbleLifeMin, bubbleLifeRange.x);
            cs.SetFloat(ID_BubbleLifeMax, Mathf.Max(bubbleLifeRange.x, bubbleLifeRange.y));
            cs.SetFloat(ID_BubbleSizeMin, bubbleSizeRange.x);
            cs.SetFloat(ID_BubbleSizeMax, Mathf.Max(bubbleSizeRange.x, bubbleSizeRange.y));
            cs.SetFloat(ID_BubbleWobble, bubbleWobble);
            cs.SetFloat(ID_TexelWorldArea, volume.SimTexelWorldArea);

            cs.SetFloat(ID_Gravity, gravity);
            cs.SetFloat(ID_FlowDrift, flowDrift);
            cs.SetVector(ID_WindDrift, WindDriftWorld());
            cs.SetFloat(ID_Drag, drag);

            // Dry-interior exclusion volumes, bound EXPLICITLY like every other compute uniform
            // (this codebase never relies on Shader.SetGlobal* reaching compute kernels). The
            // Update kernel dissolves particles inside a volume; count 0 skips the test
            // entirely. The edge-params lane rides along for the per-volume particle handling
            // (affect flag, dissolve speed) the spawn/update tests now read.
            int exclusionCount = WaterExclusionVolume.WriteVolumeUniforms(_exclusionMatrices,
                _exclusionShapes, null, _exclusionEdgeParams,
                densityCamera != null ? densityCamera.transform.position : volume.VolumeCenter);
            cs.SetFloat(ID_ExclusionCount, exclusionCount);
            if (exclusionCount > 0)
            {
                cs.SetMatrixArray(ID_ExclusionWorldToLocal, _exclusionMatrices);
                cs.SetVectorArray(ID_ExclusionShape, _exclusionShapes);
                cs.SetVectorArray(ID_ExclusionEdgeParams, _exclusionEdgeParams);
            }

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

            // Ocean FFT glue: enable the keyword + bind the cascade layout so the kernels place
            // particles on the real swell (SurfaceWorldY). The variant also gates ambient spawning
            // OFF on ocean bodies - only the surf lip throws. Pools leave it off (no cascade
            // binding); the spatial-texture check is part of the gate because dispatching the
            // variant with the texture missing is an unbound-resource error on WebGPU.
            bool oceanFftGlue = volume.OceanFftActive && volume.OceanFftSpatialTexture != null;
            if (oceanFftGlue)
            {
                cs.EnableKeyword(KeywordOceanFftGlue);
                cs.SetVector(ID_OceanFftDomainSizes, volume.OceanFftDomainSizes);
                cs.SetFloat(ID_OceanFftCascadeCount, volume.OceanFftCascadeCount);
            }
            else
            {
                cs.DisableKeyword(KeywordOceanFftGlue);
            }

            if (simulationDrivenSpawning)
            {
                cs.SetBuffer(_kSpawn, ID_Particles, _particles);
                cs.SetBuffer(_kSpawn, ID_CrestFleckPreviousPositions, _crestFleckPreviousPositions);
                cs.SetBuffer(_kSpawn, ID_Counters, _counters);
                cs.SetBuffer(_kSpawn, ID_TileCounts, _tileCounts);
                cs.SetTexture(_kSpawn, ID_Sim, volume.SimStateTexture);
                cs.SetTexture(_kSpawn, ID_SimHorizontalFlow, volume.SimHorizontalFlowTexture);
                cs.SetTexture(_kSpawn, ID_FoamTex, volume.FoamMaskTexture);

                int spawnGroups = volume.SimResolution / SpawnThreadGroupSize;
                cs.Dispatch(_kSpawn, spawnGroups, spawnGroups, 1);
            }

            // CPU-queued splash bursts (rigidbody impacts, mouse splashes): one thread group per
            // request throws KIND_SPRAY droplets from the same pool, unifying all airborne spray
            // on one tech path (the Shuriken emitter keeps only the crown flipbook).
            if (_pendingBursts.Count > 0)
            {
                // FIFO carry-over: upload the oldest MaxBurstsPerFrame requests and KEEP the
                // rest for the next frame - a caller past the frame cap is delayed, never
                // dropped (the bound lives at enqueue time, MaxPendingBursts).
                int burstCount = Mathf.Min(_pendingBursts.Count, MaxBurstsPerFrame);
                for (int i = 0; i < burstCount; i++) _burstUpload[i] = _pendingBursts[i];
                _pendingBursts.RemoveRange(0, burstCount);
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
            cs.SetBuffer(_kUpdate, ID_CrestFleckPreviousPositions, _crestFleckPreviousPositions);
            cs.SetTexture(_kUpdate, ID_Sim, volume.SimStateTexture);
            cs.SetTexture(_kUpdate, ID_SimHorizontalFlow, volume.SimHorizontalFlowTexture);
            // Update places floating foam on the FFT swell (SurfaceWorldY), so the OCEAN_FFT_GLUE
            // variant reads the spatial cascade + amplitude (a missing bind is a hard error on
            // WebGPU). Amplitude is set here so Update reads the real swell height rather than
            // RasterizeDensity's later value. Keyword state above already matches.
            if (oceanFftGlue)
            {
                cs.SetTexture(_kUpdate, ID_OceanFftSpatial, volume.OceanFftSpatialTexture);
                cs.SetFloat(ID_OceanFftAmplitude, volume.LargeWaveAmplitudeEffective);
                BindOceanAperiodic(cs, _kUpdate);
                volume.SeaStateFetch.BindTo(cs, _kUpdate);
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
            if (_particles == null || _density == null || _densityDepth == null ||
                _densityTier1 == null || _densityTier2 == null) return;

            ComputeShader cs = particleCompute;
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
            Matrix4x4 viewProj = gpuProj * cam.worldToCameraMatrix;
            cs.SetMatrix(ID_DensityViewProj, viewProj);
            cs.SetFloat(ID_DensityProj11, Mathf.Abs(gpuProj.m11));
            cs.SetFloat(ID_DensitySurfaceSizeScale,
                Mathf.Clamp(densitySurfaceSizeScale,
                            MinimumDensitySurfaceSizeScale,
                            MaximumDensitySurfaceSizeScale));

            int texelCount = _densitySize.x * _densitySize.y;
            cs.SetBuffer(_kClearDensity, ID_DensityBuffer, _density);
            cs.SetBuffer(_kClearDensity, ID_DensityDepth, _densityDepth);
            cs.SetBuffer(_kClearDensity, ID_DensityBufferTier1, _densityTier1);
            cs.SetBuffer(_kClearDensity, ID_DensityBufferTier2, _densityTier2);
            cs.Dispatch(_kClearDensity,
                        (texelCount + UpdateThreadGroupSize - 1) / UpdateThreadGroupSize, 1, 1);

            // The surface-height glue reads the 2D sim on every body and the FFT cascade only on
            // oceans; bind only what this variant declares (unused binds hard-error on some
            // backends, mirroring the Spawn kernel's pattern). The shore binder re-runs here
            // because the kernel executes outside DispatchSimulation's bind scope.
            volume.BuildShoreFoamState().BindTo(cs, _kRasterizeDensity);
            // The shared compute asset may have been used by another body since LateUpdate.
            // Rebind this body's wind-wave bank immediately before its deferred surface query.
            volume.WriteWaveUniforms(cs);
            cs.SetBuffer(_kRasterizeDensity, ID_Particles, _particles);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityBuffer, _density);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityDepth, _densityDepth);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityBufferTier1, _densityTier1);
            cs.SetBuffer(_kRasterizeDensity, ID_DensityBufferTier2, _densityTier2);
            cs.SetTexture(_kRasterizeDensity, ID_DensityStampTex, ResolveDensityStampTexture());
            Vector2Int densityStampGrid = ResolveDensityStampGrid();
            cs.SetVector(ID_DensityStampGrid,
                         new Vector4(densityStampGrid.x, densityStampGrid.y, 0f, 0f));
            // The 2D sim is read on BOTH paths now: the ocean glue adds the interactive ripple
            // (the wake) on top of the swell, exactly as the surface mesh does, so leaving Sim
            // unbound on oceans would be the unbound-resource error this bind pattern exists to
            // avoid. The cascade textures stay ocean-only (the variant that reads them).
            cs.SetTexture(_kRasterizeDensity, ID_Sim, volume.SimStateTexture);
            bool oceanFftGlue = volume.OceanFftActive && volume.OceanFftSpatialTexture != null;
            if (oceanFftGlue)
            {
                cs.SetTexture(_kRasterizeDensity, ID_OceanFftSpatial, volume.OceanFftSpatialTexture);
                cs.SetFloat(ID_OceanFftAmplitude, volume.LargeWaveAmplitudeEffective);
                BindOceanAperiodic(cs, _kRasterizeDensity);
                volume.SeaStateFetch.BindTo(cs, _kRasterizeDensity);
            }
            cs.Dispatch(_kRasterizeDensity, _capacityPow2 / UpdateThreadGroupSize, 1, 1);
        }

        void BindOceanAperiodic(ComputeShader compute, int kernel)
        {
            Vector3 center = volume.VolumeCenter;
            float mapSize = Mathf.Max(1f, volume.oceanDirectionMapSize);
            bool active = volume.oceanAperiodicEnabled;
            compute.SetTexture(kernel, ID_OceanDirectionMap,
                volume.oceanDirectionMap ? volume.oceanDirectionMap : Texture2D.grayTexture);
            compute.SetVector(ID_OceanAperiodicParams,
                new Vector4(active ? 1f : 0f, Mathf.Clamp(volume.oceanAperiodicTileScale, 0.5f, 2f),
                            Mathf.Clamp01(volume.oceanDirectionMapStrength), 0f));
            compute.SetVector(ID_OceanDirectionMapFrame,
                new Vector4(center.x, center.z, 1f / mapSize, 0f));
        }

        /// <summary>Queue a splash burst of ballistic spray droplets at a surface point (world).
        /// Uploaded to the GPU at MaxBurstsPerFrame per frame, FIFO; overflow CARRIES OVER to
        /// later frames (bounded by MaxPendingBursts, oldest retired first). The burst also
        /// injects its bubble-plume share (bubbleAmount). Droplet look/motion is this system's
        /// spray path, so event splashes match turbulence-thrown spray exactly.</summary>
        /// <param name="petalDirection">World XZ direction the wedge is centred on. ZERO (the default)
        /// is the legacy full ring, and every caller that omits it behaves exactly as before.</param>
        /// <param name="arcHalfRadians">Half-width of the wedge. Ignored when the direction is zero.</param>
        /// <param name="elevationRadians">Lifts the burst in its own vertical plane. ZERO (the default)
        /// leaves the launch angle exactly as the up/out speeds imply.</param>
        public void QueueSplashBurst(Vector3 surfacePos, float strength, float radius,
                                     int dropletCount, float upSpeed, float outSpeed,
                                     Vector2 dropletLifeRange, float dropletSize,
                                     Vector2 petalDirection = default, float arcHalfRadians = Mathf.PI,
                                     float elevationRadians = 0f, float dropletOpacity = 1f)
        {
#if UNITY_EDITOR
            BeginBurstFrame();
            BurstsRequestedThisFrame++;
#endif
            // Split from the cap check below so the two silences are told apart: a body with particles
            // switched off is a different fault from a body that ran out of frame budget, and they used
            // to look identical from the outside.
            if (!useParticles || !isActiveAndEnabled)
            {
#if UNITY_EDITOR
                BurstsSuppressedTotal++;
#endif
                return;
            }
            if (_pendingBursts.Count >= MaxPendingBursts)
            {
                // Queue saturated across frames: retire the OLDEST request instead of refusing
                // the newest (fresh events carry a moving emitter's current position/energy).
                _pendingBursts.RemoveAt(0);
#if UNITY_EDITOR
                BurstsDroppedThisFrame++;
                BurstsDroppedTotal++;
#endif
            }
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
                size = Mathf.Max(0f, dropletSize),
                // Passed through unnormalised-checked: the caller owns the sentinel, because the same
                // zero-vs-direction decision has to drive the Shuriken fallback identically.
                dirX = petalDirection.x,
                dirZ = petalDirection.y,
                arcHalfRadians = Mathf.Max(0f, arcHalfRadians),
                elevationRadians = elevationRadians,
                dropletOpacity = Mathf.Clamp01(dropletOpacity)
            });
            // Keep the sim/draw alive (even with ambient foam OFF) until everything this burst
            // made has fully lived: the longer of airborne-droplet or bubble-plume life, plus
            // the deposited-foam life both convert into.
            float airborneOrBubbleLife = Mathf.Max(
                Mathf.Max(dropletLifeRange.x, dropletLifeRange.y),
                Mathf.Max(bubbleLifeRange.x, bubbleLifeRange.y));
            float burstLifeSpan = airborneOrBubbleLife
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

        void Draw()
        {
            int vertexCount = _capacityPow2 * VerticesPerParticle;

            // After-fog reroute: on fog frames the property blocks are still filled HERE (one
            // authoring place), but the queue-time submissions are skipped - RenderAfterFog
            // re-submits the same blocks from the fog feature's pass (see the reroute comment
            // block above OnEnable). Fog-off frames are byte-identical through this path.
            bool reroute = WaterVolume.UnderwaterFogActive;

            // Floating foam pass: its own material, look and flipbook. Skipped in density mode,
            // where the screen-space veil draws the foam instead (_DrawKind = foam-only). The body's
            // uniforms (sim texture, volume frame, waves, sun) drive the vertex shader; the particle
            // buffer rides along in the same block.
            if (!_densityPending)
            {
                volume.WriteBodyProps(_mpb);
                _mpb.SetBuffer(ID_ParticlesShader, _particles);
                _mpb.SetBuffer(ID_CrestFleckPreviousPositionsShader, _crestFleckPreviousPositions);
                WaterParticlePool.WriteFlipbook(_mpb, flipbookGrid, flipbookFps);
                if (profile != null && profile.look.drive) profile.WriteLook(_mpb, surfaceFoamOpacity);
                else WriteLayerOpacity(_mpb, particleMaterial, surfaceFoamOpacity);
                _mpb.SetFloat(ID_DrawKind, DrawKindFoam);

                if (!reroute)
                {
                    var foamRp = new RenderParams(particleMaterial)
                    {
                        worldBounds = volume.SimWorldBounds,
                        matProps = _mpb
                    };
                    Graphics.RenderPrimitives(foamRp, MeshTopology.Triangles, vertexCount);
                }
            }
            else if (!reroute)
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
            _sprayMpb.SetBuffer(ID_CrestFleckPreviousPositionsShader, _crestFleckPreviousPositions);
            WaterParticlePool.WriteFlipbook(_sprayMpb, sprayFlipbookGrid, sprayFlipbookFps);
            if (profile != null && profile.look.drive) profile.WriteSprayLook(_sprayMpb, sprayOpacity);
            else WriteLayerOpacity(_sprayMpb, sprayDrawMaterial, sprayOpacity);
            _sprayMpb.SetFloat(ID_DrawKind, DrawKindSpray);

            if (!reroute)
            {
                var sprayRp = new RenderParams(sprayDrawMaterial)
                {
                    worldBounds = volume.SimWorldBounds,
                    matProps = _sprayMpb
                };
                Graphics.RenderPrimitives(sprayRp, MeshTopology.Triangles, vertexCount);
            }

            // Crest flecks stay surface-bound and use FoamParticles.shader's analytic KWS-style
            // dot branch. Their simulation kind remains KIND_RIPPLE_CREST, so they never inherit
            // ballistic spray motion or depend on a droplet texture. In DENSITY mode the veil
            // owns them (RasterizeDensity splats the same particles into the LOD tiers), so the
            // quad pass is skipped - drawing both was a double representation of every fleck.
            volume.WriteBodyProps(_crestFleckMpb);
            _crestFleckMpb.SetBuffer(ID_ParticlesShader, _particles);
            _crestFleckMpb.SetBuffer(ID_CrestFleckPreviousPositionsShader, _crestFleckPreviousPositions);
            if (profile != null && profile.look.drive) profile.WriteSprayLook(_crestFleckMpb, surfaceFoamOpacity);
            else WriteLayerOpacity(_crestFleckMpb, particleMaterial, surfaceFoamOpacity);
            _crestFleckMpb.SetFloat(ID_DrawKind, DrawKindCrestFleck);

            if (!reroute && !_densityPending)
            {
                var crestFleckRp = new RenderParams(particleMaterial)
                {
                    worldBounds = volume.SimWorldBounds,
                    matProps = _crestFleckMpb
                };
                Graphics.RenderPrimitives(crestFleckRp, MeshTopology.Triangles, vertexCount);
            }

            // Bubble pass (_DrawKind = bubble-only): underwater plume sprites on the foam
            // material - the shader draws them as analytic rim circles, so no atlas and no
            // extra material asset. Skipped entirely while the body injects no bubbles.
            if (bubbleAmount > 0f)
            {
                volume.WriteBodyProps(_bubbleMpb);
                _bubbleMpb.SetBuffer(ID_ParticlesShader, _particles);
                _bubbleMpb.SetBuffer(ID_CrestFleckPreviousPositionsShader, _crestFleckPreviousPositions);
                if (profile != null && profile.look.drive) profile.WriteLook(_bubbleMpb, bubbleOpacity);
                else WriteLayerOpacity(_bubbleMpb, particleMaterial, bubbleOpacity);
                _bubbleMpb.SetFloat(ID_DrawKind, DrawKindBubble);

                if (!reroute)
                {
                    var bubbleRp = new RenderParams(particleMaterial)
                    {
                        worldBounds = volume.SimWorldBounds,
                        matProps = _bubbleMpb
                    };
                    Graphics.RenderPrimitives(bubbleRp, MeshTopology.Triangles, vertexCount);
                }
            }

            // Arm the after-fog pass with THIS frame's decision (LateUpdate disarmed it, so a
            // frame that never reaches Draw leaves nothing to re-submit).
            _afterFogArmed = reroute;
            _rerouteDensity = reroute && _densityPending;
        }

        /// <summary>Submit this frame's skipped particle draws AFTER the fullscreen underwater
        /// fog (called by WaterUnderwaterFogFeature's after-fog pass with the rendering camera).
        /// Only armed on frames where Draw() filled the property blocks but withheld its
        /// queue-time submissions; re-submits those exact blocks, so the two paths can never
        /// disagree about the look.</summary>
        internal void RenderAfterFog(RasterCommandBuffer cmd, Camera camera)
        {
            if (!_afterFogArmed || !isActiveAndEnabled) return;
            if (_particles == null || _crestFleckPreviousPositions == null || volume == null ||
                particleMaterial == null) return;

            int vertexCount = _capacityPow2 * VerticesPerParticle;
            if (!_rerouteDensity)
            {
                cmd.DrawProcedural(Matrix4x4.identity, particleMaterial, 0,
                                   MeshTopology.Triangles, vertexCount, 1, _mpb);
            }
            else if (camera == _densityCamera && densityMaterial != null)
            {
                // The composite is camera-locked like the queue-time path (RenderParams.camera):
                // its density field was splatted with ONE camera's matrices.
                WriteDensityCompositeProps();
                cmd.DrawProcedural(Matrix4x4.identity, densityMaterial, 0,
                                   MeshTopology.Triangles, CompositeVertexCount, 1, _densityMpb);
            }

            Material sprayDrawMaterial = sprayMaterial != null ? sprayMaterial : particleMaterial;
            cmd.DrawProcedural(Matrix4x4.identity, sprayDrawMaterial, 0,
                               MeshTopology.Triangles, vertexCount, 1, _sprayMpb);
            // Density mode owns the crest flecks (they are splatted into the LOD tiers the
            // composite above just drew) - the quad pass on top was a double representation.
            if (!_rerouteDensity)
                cmd.DrawProcedural(Matrix4x4.identity, particleMaterial, 0,
                                   MeshTopology.Triangles, vertexCount, 1, _crestFleckMpb);

            // Bubble pass rides the reroute like the others; its block was filled in Draw().
            if (bubbleAmount > 0f)
                cmd.DrawProcedural(Matrix4x4.identity, particleMaterial, 0,
                                   MeshTopology.Triangles, vertexCount, 1, _bubbleMpb);
        }

        static void WriteLayerOpacity(MaterialPropertyBlock properties, Material material, float layerOpacity)
        {
            float authoredOpacity = material != null && material.HasProperty(ID_ParticleOpacity)
                ? material.GetFloat(ID_ParticleOpacity)
                : 1f;
            properties.SetFloat(ID_ParticleOpacity, authoredOpacity * Mathf.Clamp01(layerOpacity));
        }

        // Fullscreen triangle that shades the splatted density as connected foam. The bounds
        // keep it culled with the body; queue Transparent+5 draws it over the water surface
        // but under the spray/splash billboards.
        void DrawDensityComposite()
        {
            WriteDensityCompositeProps();
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

        // Property fill only, shared by the queue-time submit above and the after-fog
        // re-submit (RenderAfterFog) - split so the two paths can never drift.
        void WriteDensityCompositeProps()
        {
            _densityMpb.SetBuffer(ID_FoamDensityShader, _density);
            _densityMpb.SetBuffer(ID_FoamDensityDepthShader, _densityDepth);
            _densityMpb.SetBuffer(ID_FoamDensityTier1Shader, _densityTier1);
            _densityMpb.SetBuffer(ID_FoamDensityTier2Shader, _densityTier2);
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
        }

        Texture ResolveDensityStampTexture()
        {
            if (profile != null && profile.look.drive && profile.look.particleAtlas != null)
                return profile.look.particleAtlas;

            Texture texture = particleMaterial != null
                ? particleMaterial.GetTexture(WaterShaderProps.ParticleTex)
                : null;
            return texture != null ? texture : Texture2D.whiteTexture;
        }

        Vector2Int ResolveDensityStampGrid()
        {
            Vector2Int grid = profile != null && profile.look.drive
                ? profile.look.flipbookGrid
                : flipbookGrid;
            return new Vector2Int(Mathf.Max(1, grid.x), Mathf.Max(1, grid.y));
        }

    }
}
