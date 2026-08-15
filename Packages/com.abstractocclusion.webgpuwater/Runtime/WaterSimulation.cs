// WebGpuWater - GPU heightfield simulation driver (Unity 6 / URP port)
// Owns two RGBAFloat ping-pong RenderTextures and dispatches the compute kernels.
// Port of water.js by Evan Wallace (MIT).
using System.Runtime.InteropServices;   // StructLayout / Marshal.SizeOf for the GPU-matched queue structs
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterSimulation : System.IDisposable
    {
        // The compute shader dispatches in 8x8 thread groups, so the grid must be a positive
        // multiple of this. Must match [numthreads(...)] in WaterSim.compute.
        public const int ThreadGroupSize = 8;

        // Interactive ripples are authored in WORLD radius, converted to a grid fraction by the caller.
        // On a large plane that fraction can fall below one texel and inject an aliased spike, so floor
        // it to a few texels: every drop stays a smooth bump regardless of body size. _Radius is a
        // fraction of the grid side, so N texels correspond to N / Resolution.
        // Band limit on an injected stamp, in TEXELS - the unit the noise lives in. The dispersive
        // tail of the explicit integrator sits at 2-4 texel wavelengths, and a stamp of radius R
        // carries spectral content up to ~1/R, so a 2.5-texel floor fed the tail directly. 4 keeps
        // the injection out of that band at every body size. Footprint only: the peak HEIGHT is
        // untouched (see the two rejected strength compensations in WaterVolume.Quality.cs).
        // SIDE EFFECT: wider stamps carry less height CURVATURE, which is what _FoamFromCurv reads.
        const float MinDropTexelRadius = 4f;
        const float MinInteractionDeltaSeconds = 1e-5f;
        const float WakeFoamFullSpeedMetersPerSecond = 5f;
        const float WakeFoamInjectionRatePerSecond = 3f;
        const float UnusedVectorComponent = 0f;

        // Compute kernel names (must match WaterSim.compute).
        const string KernelDrop = "Drop";
        const string KernelSphereInteract = "SphereInteract";
        const string KernelUpdate = "Update";
        const string KernelNormal = "Normal";
        const string KernelObstacle = "Obstacle";
        const string KernelObstacleSmooth = "ObstacleSmooth";
        const string KernelFoam = "Foam";
        const string KernelReduceMean = "ReduceMean";
        const string KernelReduceMeanFinal = "ReduceMeanFinal";
        const string KernelReduceActivity = "ReduceActivity";
        const string KernelReduceActivityFinal = "ReduceActivityFinal";
        const string KernelConserve = "Conserve";
        const string KernelScroll = "Scroll";
        const string KernelScrollFoam = "ScrollFoam";

        // Compute property ids, cached once instead of re-hashing strings every dispatch.
        static readonly int ID_Size = WaterShaderProps.Size;
        static readonly int ID_Delta = Shader.PropertyToID("_Delta");
        static readonly int ID_Src = Shader.PropertyToID("Src");
        static readonly int ID_Dst = Shader.PropertyToID("Dst");
        static readonly int ID_DropQueue = Shader.PropertyToID("DropQueue");
        static readonly int ID_DropQueueCount = Shader.PropertyToID("_DropQueueCount");
        static readonly int ID_SphereQueue = Shader.PropertyToID("SphereQueue");
        static readonly int ID_SphereQueueCount = Shader.PropertyToID("_SphereQueueCount");
        static readonly int ID_DropAxisScale = Shader.PropertyToID("_DropAxisScale");
        static readonly int ID_SphereAxisScale = Shader.PropertyToID("_SphereAxisScale");
        static readonly int ID_WaveAxisWeight = Shader.PropertyToID("_WaveAxisWeight");
        static readonly int ID_ObstaclePrev = Shader.PropertyToID("ObstaclePrev");
        static readonly int ID_ObstacleCurr = Shader.PropertyToID("ObstacleCurr");
        static readonly int ID_ObstacleStrength = Shader.PropertyToID("_ObstacleStrength");
        static readonly int ID_ObstacleFlipY = Shader.PropertyToID("_ObstacleFlipY");
        static readonly int ID_ObstacleDeadband = Shader.PropertyToID("_ObstacleDeadband");
        static readonly int ID_ObstacleSolid = Shader.PropertyToID("ObstacleSolid");
        static readonly int ID_ObstacleReflect = Shader.PropertyToID("_ObstacleReflect");
        static readonly int ID_ObstacleSolidThreshold = Shader.PropertyToID("_ObstacleSolidThreshold");
        static readonly int ID_ObstacleRestDip = Shader.PropertyToID("_ObstacleRestDip");
        static readonly int ID_ObstacleSmoothPrev = Shader.PropertyToID("ObstacleSmoothPrev");
        static readonly int ID_ObstacleSmoothRaw = Shader.PropertyToID("ObstacleSmoothRaw");
        static readonly int ID_ObstacleSmoothDst = Shader.PropertyToID("ObstacleSmoothDst");
        static readonly int ID_ObstacleTemporalBlend = Shader.PropertyToID("_ObstacleTemporalBlend");
        static readonly int ID_WaveSpeed = Shader.PropertyToID("_WaveSpeed");
        static readonly int ID_Damping = Shader.PropertyToID("_Damping");
        static readonly int ID_RippleViscosity = Shader.PropertyToID("_RippleViscosity");
        static readonly int ID_FoamGenRate = Shader.PropertyToID("_FoamGenRate");
        static readonly int ID_FoamGenThreshold = Shader.PropertyToID("_FoamGenThreshold");
        static readonly int ID_FoamMinWaveHeight = Shader.PropertyToID("_FoamMinWaveHeight");
        static readonly int ID_FoamDecayResidual = Shader.PropertyToID("_FoamDecayResidual");
        static readonly int ID_FoamDecayFresh = Shader.PropertyToID("_FoamDecayFresh");
        static readonly int ID_FoamDtSteps = Shader.PropertyToID("_FoamDtSteps");
        static readonly int ID_FoamDecayRate = Shader.PropertyToID("_FoamDecayRate");
        static readonly int ID_WetDryDecay = Shader.PropertyToID("_WetDryDecay");
        static readonly int ID_FoamWriteMask = Shader.PropertyToID("_FoamWriteMask");
        static readonly int ID_FoamSpread = Shader.PropertyToID("_FoamSpread");
        static readonly int ID_FoamFromSpeed = Shader.PropertyToID("_FoamFromSpeed");
        static readonly int ID_FoamFromCurv = Shader.PropertyToID("_FoamFromCurv");
        static readonly int ID_FoamAdvect = Shader.PropertyToID("_FoamAdvect");
        static readonly int ID_FoamDeposit = Shader.PropertyToID("_FoamDeposit");
        static readonly int ID_FoamHeadroom = Shader.PropertyToID("_FoamHeadroom");
        static readonly int ID_FoamBreakStrength = Shader.PropertyToID("_FoamBreakStrength");
        static readonly int ID_FoamBreakRange = Shader.PropertyToID("_FoamBreakRange");
        static readonly int ID_FoamCrestBias = Shader.PropertyToID("_FoamCrestBias");
        static readonly int ID_WakeFoamStrength = Shader.PropertyToID("_WakeFoamStrength");
        static readonly int ID_WakeFoamRadiusScale = Shader.PropertyToID("_WakeFoamRadiusScale");
        static readonly int ID_WakeStartForceCap = Shader.PropertyToID("_WakeStartForceCap");
        static readonly int ID_FoamSrc = Shader.PropertyToID("FoamSrc");
        static readonly int ID_FoamDst = Shader.PropertyToID("FoamDst");
        static readonly int ID_PartialSums = Shader.PropertyToID("PartialSums");
        static readonly int ID_MeanResult = Shader.PropertyToID("MeanResult");
        static readonly int ID_MeanCorrectionMax = Shader.PropertyToID("_MeanCorrectionMax");
        static readonly int ID_ActivityPartialMax = Shader.PropertyToID("ActivityPartialMax");
        static readonly int ID_ActivityResult = Shader.PropertyToID("ActivityResult");
        static readonly int ID_ActivityInverseThresholds = Shader.PropertyToID("_ActivityInverseThresholds");
        static readonly int ID_ActivityWetMarkInverseThreshold =
            Shader.PropertyToID("_ActivityWetMarkInverseThreshold");
        static readonly int ID_ScrollOffset = Shader.PropertyToID("_ScrollOffset");
        static readonly int ID_FlowSrc = Shader.PropertyToID("FlowSrc");
        static readonly int ID_FlowDst = Shader.PropertyToID("FlowDst");
        static readonly int ID_FlowGradientToWorld = Shader.PropertyToID("_FlowGradientToWorld");
        static readonly int ID_FlowTexelMeters = Shader.PropertyToID("_FlowTexelMeters");
        static readonly int ID_BedTex = WaterShaderProps.BedTex;
        static readonly int ID_UseBedDepth = WaterShaderProps.UseBedDepth;

        /// <summary>Grid resolution of the heightfield RTs (per side). Set per quality tier.</summary>
        public int Resolution { get; }

        readonly ComputeShader _cs;
        readonly int _kDrop, _kSphereInteract, _kUpdate, _kNormal, _kObstacle, _kObstacleSmooth, _kFoam, _kConserve, _kScroll, _kScrollFoam;
        readonly int _kReduceMean, _kReduceMeanFinal, _kReduceActivity, _kReduceActivityFinal;
        readonly int _groups;
        readonly Vector4 _delta; // (1/Resolution, 1/Resolution, 0, 0), precomputed once

        // Per-axis anisotropy so ripples stay round in WORLD on a rectangular (non-square) pool.
        // Defaults are the isotropic square case, so a body that never calls SetAnisotropy is
        // identical to before. (0.25,0.25) reproduces the old 4-neighbour average Laplacian.
        Vector4 _waveAxisWeight = new Vector4(0.25f, 0.25f, 0f, 0f);
        Vector4 _dropAxisScale = new Vector4(1f, 1f, 0f, 0f);
        Vector4 _flowGradientToWorld = Vector4.one;
        Vector4 _flowTexelMeters = Vector4.one;

        // Bed-depth coupling: holds dry land flat (ripples reflect off the waterline) and drains the
        // open-shore boundary. Inactive by default so a body without a baked bed behaves exactly as a
        // bottomless pool. Bound onto the Update kernel each frame.
        Texture _bedTex;
        float _useBedDepth;         // 1 = active

        // Static reflection (opt-in). Inactive by default so the Update kernel is byte-identical.
        // Bound onto the Update kernel each frame (black solid mask when inactive).
        Texture _solidTex;
        float _reflectActive;          // 1 = reflection on
        float _reflectSolidThreshold;  // coverage above which a solid-mask cell reflects
        float _reflectRestDip;         // resting depression at solid cells (pool units)
        float _reflectFlipY;           // 1 = flip V (same convention as the obstacle map)

        // Wake foam (foam move #3): a moving interactor stamps foam into the foam buffer, scaled by its
        // speed, which StepFoam then advects + decays into a trail. Set per frame by the body from its
        // foam settings; default 0 = off, so a sphere interaction is byte-identical (pure copy-through).
        float _wakeFoamStrength;
        float _wakeFoamRadiusScale = 1.5f;

        // Wake start-force cap: limits the per-step velocity a moving interactor injects, clipping the
        // initial over-tall crest of a fresh wake without touching the developed shape. 0 = off (no cap).
        float _wakeStartForceCap;

        RenderTexture _a; // current state (height, velocity, normal.x, normal.z)
        RenderTexture _b; // scratch
        RenderTexture _horizontalFlowA, _horizontalFlowB; // local horizontal velocity, metres per second
        RenderTexture _foamA, _foamB; // foam amount ping-pong (R)
        // Exact mean-height reduction for Conserve (see the WaterSim.compute rationale:
        // the old float-mip mean silently point-sampled in WebGPU builds).
        GraphicsBuffer _partialSums; // one float per 8x8 thread group
        GraphicsBuffer _meanResult;  // single float: the exact mean
        // Periodic ocean-sleep reduction: maximum normalized activity over height, vertical velocity,
        // horizontal flow, foam and wetness. Separate buffers keep the async staging copy independent
        // from the mean-conservation reduction that runs again on following frames.
        GraphicsBuffer _activityPartialMax;
        GraphicsBuffer _activityResult;
        readonly AsyncReadbackChannel _activityReadback;
        readonly System.Action<AsyncGPUReadbackRequest> _onActivityReadback;
        int _injectionGeneration;
        int _activityReadbackGeneration;
        const float SettledNormalizedActivity = 1f;

        // ---- Queued injection (see AddDrop) ----------------------------------------------------
        // Stamps accumulate here during the frame and are applied by FlushInjections in ONE full-grid
        // pass per kind. The capacity only bounds how many stamps share a pass - AddDrop /
        // AddSphereInteraction flush early rather than discard, so overflowing costs a pass, never a
        // splash. 64 is far above the real per-frame count (WaterInteractable caps itself at 4 drops
        // each) and still a trivial buffer.
        internal const int MaxQueuedInjections = 64;

        // Blittable, GPU-matched layouts: field ORDER and types must mirror WaterDropInjection /
        // WaterSphereInjection in WaterSim.compute exactly - a structured buffer is raw bytes, and a
        // mismatch reads garbage rather than failing.
        [StructLayout(LayoutKind.Sequential)]
        struct DropInjection
        {
            public Vector2 Center;
            public float Radius;
            public float Strength;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SphereInjection
        {
            public Vector2 Center;
            public Vector2 VelXZ;
            public float Radius;
            public float VelY;
            public float Weight;
            public float Strength;
            public float WakeFoamDose;
            public float VerticalForceCap;
        }

        // Taken from the struct, not written as a literal, so adding a field cannot silently desync
        // the stride from the layout above.
        static readonly int DropInjectionStride = Marshal.SizeOf<DropInjection>();
        static readonly int SphereInjectionStride = Marshal.SizeOf<SphereInjection>();

        readonly DropInjection[] _dropQueue = new DropInjection[MaxQueuedInjections];
        readonly SphereInjection[] _sphereQueue = new SphereInjection[MaxQueuedInjections];
        int _dropCount, _sphereCount;
        bool _hasReceivedInjection;
        ComputeBuffer _dropBuffer, _sphereBuffer;   // allocated on first use; a scene with no ripples pays nothing

        // Wake latch for the owning WaterVolume. An unbounded ocean already has its FFT sea, so running
        // this second, initially-flat heightfield before gameplay touches it only burns a chain of
        // full-grid WebGPU dispatches. A periodic GPU activity reduction releases the latch only after
        // height, velocity, flow, foam and wetness are all below explicit visibility thresholds.
        internal bool HasReceivedInjection => _hasReceivedInjection;

        /// <summary>Force the wake latch on without an injection. Bounded bodies call this at
        /// enable so they boot with a warm, stepping sim (their legacy behaviour) and then EARN
        /// sleep through the same activity reduction the ocean uses.</summary>
        internal void Wake() => _hasReceivedInjection = true;

        // The activity reduction is normalized by its thresholds on the GPU; finite values at or below
        // one are settled. Kept as a pure function so the fail-closed NaN/Infinity rule is testable.
        internal static bool IsSettledActivity(float normalizedActivity)
            => !float.IsNaN(normalizedActivity)
               && !float.IsInfinity(normalizedActivity)
               && normalizedActivity <= SettledNormalizedActivity;

        /// <summary>The texture holding the current simulation state.</summary>
        public RenderTexture Texture => _a;

        /// <summary>The current foam amount texture (R channel).</summary>
        public RenderTexture FoamTexture => _foamA;

        /// <summary>Current local horizontal surface-velocity field for crest-fleck consumers only.</summary>
        public RenderTexture HorizontalFlowTexture => _horizontalFlowA;

        // Wake force lives in pool-height units, but foam is an authored visual effect. Keep its
        // source in world speed/time so changing a volume's vertical extent cannot thin or thicken
        // the wake. The dose composes linearly across frames; the compute shader converts it to a
        // coverage alpha with exp(), preserving that invariance under repeated stamps.
        internal static float CalculateWakeFoamDose(Vector2 worldHorizontalStep, float deltaSeconds)
        {
            if (deltaSeconds <= MinInteractionDeltaSeconds) return 0f;
            float speed = worldHorizontalStep.magnitude / deltaSeconds;
            float speedFactor = Mathf.Clamp01(speed / WakeFoamFullSpeedMetersPerSecond);
            return speedFactor * WakeFoamInjectionRatePerSecond * deltaSeconds;
        }

        // Foam runs once per rendered frame. Rebase its transport operators from authored reference
        // steps (1 = 1/60 s) so advection and diffusion match the existing time-scaled generation
        // and decay when the frame rate changes.
        internal static float CalculateFoamAdvection(float authoredAdvection, float elapsedReferenceSteps)
        {
            return Mathf.Max(0f, authoredAdvection) * Mathf.Max(0f, elapsedReferenceSteps);
        }

        internal static float CalculateFoamSpread(float authoredSpread, float elapsedReferenceSteps)
        {
            float retainedFraction = 1f - Mathf.Clamp01(authoredSpread);
            return 1f - Mathf.Pow(retainedFraction, Mathf.Max(0f, elapsedReferenceSteps));
        }

        // Every kernel the sim dispatches, validated up front (see the constructor guard): a wrong
        // or stale compute asset should fail with ONE clear message naming the missing kernel, not
        // an opaque ArgumentException mid-construction (mirrors WaterOceanFft.HasAllKernels).
        static readonly string[] RequiredKernels =
        {
            KernelDrop, KernelSphereInteract, KernelUpdate, KernelNormal, KernelObstacle,
            KernelObstacleSmooth, KernelFoam, KernelReduceMean, KernelReduceMeanFinal,
            KernelReduceActivity, KernelReduceActivityFinal, KernelConserve,
            KernelScroll, KernelScrollFoam
        };

        public WaterSimulation(ComputeShader cs, int resolution)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            if (resolution < ThreadGroupSize || resolution % ThreadGroupSize != 0)
                throw new System.ArgumentException(
                    $"WaterSimulation resolution must be a positive multiple of {ThreadGroupSize}, got {resolution}.",
                    nameof(resolution));
            foreach (string kernel in RequiredKernels)
                if (!cs.HasKernel(kernel))
                    throw new System.ArgumentException(
                        $"Compute shader '{cs.name}' is missing kernel '{kernel}' - assign the WaterSim compute.",
                        nameof(cs));

            Resolution = resolution;
            _delta = new Vector4(1f / Resolution, 1f / Resolution, 0f, 0f);
            _cs = cs;
            _kDrop = cs.FindKernel(KernelDrop);
            _kSphereInteract = cs.FindKernel(KernelSphereInteract);
            _kUpdate = cs.FindKernel(KernelUpdate);
            _kNormal = cs.FindKernel(KernelNormal);
            _kObstacle = cs.FindKernel(KernelObstacle);
            _kObstacleSmooth = cs.FindKernel(KernelObstacleSmooth);
            _kFoam = cs.FindKernel(KernelFoam);
            _kReduceMean = cs.FindKernel(KernelReduceMean);
            _kReduceMeanFinal = cs.FindKernel(KernelReduceMeanFinal);
            _kReduceActivity = cs.FindKernel(KernelReduceActivity);
            _kReduceActivityFinal = cs.FindKernel(KernelReduceActivityFinal);
            _kConserve = cs.FindKernel(KernelConserve);
            _kScroll = cs.FindKernel(KernelScroll);
            _kScrollFoam = cs.FindKernel(KernelScrollFoam);
            _groups = Resolution / ThreadGroupSize;

            _a = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _b = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            // TWO channels: R = foam amount, G = the wet mark (highest recent waterline, pool height
            // units). rg32float is a core WebGPU storage format, like the r32float this replaced.
            // Cleared to zero below, which is the correct inert state for BOTH: no foam, and a mark
            // sitting exactly at the still level.
            _foamA = Create(RenderTextureFormat.RGFloat, "WaterFoam");
            _foamB = Create(RenderTextureFormat.RGFloat, "WaterFoam");
            _horizontalFlowA = Create(RenderTextureFormat.RGFloat, "WaterSimHorizontalFlow");
            _horizontalFlowB = Create(RenderTextureFormat.RGFloat, "WaterSimHorizontalFlow");
            Clear(_a); Clear(_b); Clear(_foamA); Clear(_foamB); Clear(_horizontalFlowA); Clear(_horizontalFlowB);

            _partialSums = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _groups * _groups, sizeof(float));
            _meanResult = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            _meanResult.SetData(new float[1]); // mean = 0 until the first reduction
            _activityPartialMax = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                _groups * _groups, sizeof(float));
            _activityResult = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            _activityResult.SetData(new float[1]);
            _onActivityReadback = OnActivityReadback;
            // On backends where this buffer readback cannot complete (partial-WebGPU
            // mobile GPUs), the channel latches Unsupported and the sim simply never
            // auto-sleeps - its pre-sleep-check behaviour. One clear line beats silence
            // in buyer bug reports from such devices.
            _activityReadback = new AsyncReadbackChannel(onGaveUp: static () => Debug.LogWarning(
                "WaterSimulation: ripple sleep-check readback unavailable on this backend; " +
                "the ripple sim stays awake."));
        }

        RenderTexture Create(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(Resolution, Resolution, 0, format)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = name,
                hideFlags = HideFlags.HideAndDontSave // never serialized by an edit-mode preview
            };
            rt.Create();
            return rt;
        }

        static void Clear(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        public void Dispose()
        {
            ReleaseAndDestroy(ref _a);
            ReleaseAndDestroy(ref _b);
            ReleaseAndDestroy(ref _foamA);
            ReleaseAndDestroy(ref _foamB);
            ReleaseAndDestroy(ref _horizontalFlowA);
            ReleaseAndDestroy(ref _horizontalFlowB);
            _partialSums?.Dispose(); _partialSums = null;
            _meanResult?.Dispose(); _meanResult = null;
            _activityPartialMax?.Dispose(); _activityPartialMax = null;
            _activityResult?.Dispose(); _activityResult = null;
            _dropBuffer?.Dispose(); _dropBuffer = null;
            _sphereBuffer?.Dispose(); _sphereBuffer = null;
        }

        // Release frees the GPU surface immediately; Destroy frees the wrapper object, which
        // otherwise accumulates across enable/disable cycles until scene unload.
        static void ReleaseAndDestroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            WaterObjects.DestroyRuntime(rt);
            rt = null;
        }

        // Grid size + texel step, shared by every kernel dispatch.
        void SetGridUniforms()
        {
            _cs.SetFloat(ID_Size, Resolution);
            _cs.SetVector(ID_Delta, _delta);
        }

        void Dispatch(int kernel)
        {
            SetGridUniforms();
            _cs.SetTexture(kernel, ID_Src, _a);
            _cs.SetTexture(kernel, ID_Dst, _b);
            _cs.Dispatch(kernel, _groups, _groups, 1);
            (_a, _b) = (_b, _a); // ping-pong: _a is always the latest state
        }

        /// <summary>Set the per-axis anisotropy for a rectangular pool so ripples read ROUND in
        /// world. <paramref name="laplacianWeight"/> weights the wave-propagation neighbours per
        /// axis (default 0.25,0.25 = isotropic square); <paramref name="dropScale"/> squashes the
        /// drop stamp per axis (default 1,1). Computed by WaterVolume from the body's extent;
        /// windowed bodies pass the defaults (their sim window is already square in world).</summary>
        public void SetAnisotropy(Vector2 laplacianWeight, Vector2 dropScale)
        {
            _waveAxisWeight = new Vector4(laplacianWeight.x, laplacianWeight.y, 0f, 0f);
            _dropAxisScale = new Vector4(dropScale.x, dropScale.y, 0f, 0f);
        }

        /// <summary>Sets the height-gradient conversion for the passive horizontal-flow solver.</summary>
        public void SetHorizontalFlowGeometry(Vector2 horizontalHalfExtent, float verticalExtent)
        {
            Vector2 gradient = CalculateHorizontalFlowGradient(horizontalHalfExtent, verticalExtent, Resolution);
            _flowGradientToWorld = new Vector4(gradient.x, gradient.y,
                                                UnusedVectorComponent, UnusedVectorComponent);
            const float MinimumExtent = 1e-4f;
            const float GridSpanMultiplier = 2f;
            float horizontalX = Mathf.Max(horizontalHalfExtent.x, MinimumExtent);
            float horizontalZ = Mathf.Max(horizontalHalfExtent.y, MinimumExtent);
            _flowTexelMeters = new Vector4(GridSpanMultiplier * horizontalX / Resolution,
                                            GridSpanMultiplier * horizontalZ / Resolution,
                                            UnusedVectorComponent, UnusedVectorComponent);
        }

        internal static Vector2 CalculateHorizontalFlowGradient(Vector2 horizontalHalfExtent,
                                                                  float verticalExtent, int resolution)
        {
            const float MinimumExtent = 1e-4f;
            const float CentralDifferenceDenominator = 4f;
            float x = Mathf.Max(horizontalHalfExtent.x, MinimumExtent);
            float z = Mathf.Max(horizontalHalfExtent.y, MinimumExtent);
            float height = Mathf.Max(verticalExtent, MinimumExtent);
            float safeResolution = Mathf.Max(resolution, ThreadGroupSize);
            return new Vector2(
                height * safeResolution / (CentralDifferenceDenominator * x),
                height * safeResolution / (CentralDifferenceDenominator * z));
        }

        /// <summary>Bed-depth shoreline coupling: the pool-space bed-height map. With
        /// <paramref name="enabled"/> false (or a null map) the sim runs as a bottomless pool,
        /// unchanged. Bound on the Update kernel (dry-land reflect + open-shore drain).</summary>
        public void SetBedDepth(Texture bed, bool enabled)
        {
            _bedTex = bed;
            _useBedDepth = (enabled && bed != null) ? 1f : 0f;
        }

        /// <summary>Wake-foam stamp (foam move #3): how strongly a moving interactor deposits foam at
        /// the hull (<paramref name="strength"/>, 0 = off) and how far past the sphere radius the stamp
        /// reaches (<paramref name="radiusScale"/>). Applied inside <see cref="AddSphereInteraction"/>;
        /// with strength 0 an interaction copies the foam buffer through unchanged.</summary>
        public void SetWakeFoam(float strength, float radiusScale)
        {
            _wakeFoamStrength = Mathf.Max(0f, strength);
            _wakeFoamRadiusScale = Mathf.Max(1e-4f, radiusScale);
        }

        /// <summary>Cap on the per-step velocity a moving interactor injects (SphereInteract). Clips the
        /// too-tall crest of a freshly generated wake without touching the developed shape; 0 = off.</summary>
        public void SetWakeForceCap(float cap)
        {
            _wakeStartForceCap = Mathf.Max(0f, cap);
        }

        // Bind the bed map + active flag onto a kernel. A texture is always bound (black when inactive)
        // so the backend never sees an unbound sampler; the shader early-outs on _UseBedDepth.
        void BindBed(int kernel)
        {
            _cs.SetFloat(ID_UseBedDepth, _useBedDepth);
            _cs.SetTexture(kernel, ID_BedTex, _bedTex != null ? _bedTex : Texture2D.blackTexture);
        }

        // Surf breaker-front foam state (coastline Layer D), cached between SetShoreFoam and the
        // Foam dispatch. Default (inactive) keeps the kernel's surf branch entirely skipped.
        internal struct ShoreFoamState
        {
            public bool Active;            // the surf FRONT FIELD is live - what the foam particles read
            public bool InjectionActive;   // an injection gain is non-zero - what the Foam kernel needs
            public Texture DepthTex;       // Layer A column-depth field (half float)
            public Texture SdfTex;         // Layer A shoreline SDF field
            public Vector4 FieldCenter;    // xy = world XZ centre of the Layer A field
            public Vector4 FieldSize;      // xy = world XZ half-extent of the Layer A field
            public Vector4 UvToWorldOrigin;// sim-uv -> world-xz affine (same shape as the hero's)
            public Vector4 UvToWorldAxes;
            public float Time;             // THE MASTER SURF BEAT (WaterVolume.SurfBeatTime - matches _SurfBeatTime)
            public float FoamGain;         // whitewash + breaker injection gain
            public float WaterlineGain;    // standing waterline lace gain
            // The WaterSurfWaves.hlsl front-field uniforms (the same values the surface renders
            // with, so the injected foam lands exactly where the eye sees the fronts break).
            public float Amplitude, Wavelength, Period, BandDepth, SetStrength, Lean,
                         Compression, Greens, AmbientFade,
                         CrestLength, CrestVariation, CrestPersistence, Directionality;
            public float ShoalDepth; // _ShoreShoalDepth: the particles' density glue shoals with it
            public Vector4 WindDir;  // xy = (cos, sin) of the swell heading
            // FOAM-1/2 (render-only foam shaping, mirrored into the computes so injected foam
            // matches the rendered whitewash): the pop-curve LUT + the repartition weights.
            public Texture CrestFoamLut;    // baked pop curve (null = legacy fixed window)
            public bool CrestFoamLutActive;
            public float CrestFoamGain;     // matches the surface's _SurfCrestFoamGain
            public float BoreGain, TrailGain, TrailLength; // _SurfFoam* repartition weights
            // FOAM-5: persistent swash deposit - the stranded backwash foam line laid into the
            // foam BUFFER so it lingers across waves and fades by the normal decay.
            public float SwashAmplitude;   // _SurfSwashAmplitude - EvaluateSurfSwash needs it in the compute
            public float SwashMaxSlopeTan; // same reason: the compute's swash must honour the same cap
            public float SwashDepositGain; // deposit injection gain (0 = off, no injection)

            static readonly int ID_ShoreFoamActive = Shader.PropertyToID("_ShoreFoamActive");
            static readonly int ID_ShoreFoamInject = Shader.PropertyToID("_ShoreFoamInject");
            static readonly int ID_ShoreFoamGain = Shader.PropertyToID("_ShoreFoamGain");
            static readonly int ID_ShoreWaterlineFoamGain = Shader.PropertyToID("_ShoreWaterlineFoamGain");
            static readonly int ID_ShoreFoamTime = Shader.PropertyToID("_ShoreFoamTime");
            static readonly int ID_ShoreSimUvToWorldOrigin = Shader.PropertyToID("_ShoreSimUvToWorldOrigin");
            static readonly int ID_ShoreSimUvToWorldAxes = Shader.PropertyToID("_ShoreSimUvToWorldAxes");
            static readonly int ID_ShoreFieldCenterSim = Shader.PropertyToID("_ShoreFieldCenterSim");
            static readonly int ID_ShoreFieldSizeSim = Shader.PropertyToID("_ShoreFieldSizeSim");
            static readonly int ID_ShoreDepthTexSim = Shader.PropertyToID("_ShoreDepthTexSim");
            static readonly int ID_ShoreSDFTexSim = Shader.PropertyToID("_ShoreSDFTexSim");
            static readonly int ID_SurfActiveSim = WaterShaderProps.SurfActive;
            static readonly int ID_SurfAmplitudeSim = WaterShaderProps.SurfAmplitude;
            static readonly int ID_SurfWavelengthSim = WaterShaderProps.SurfWavelength;
            static readonly int ID_SurfPeriodSim = WaterShaderProps.SurfPeriod;
            static readonly int ID_SurfBandDepthSim = WaterShaderProps.SurfBandDepth;
            static readonly int ID_SurfSetStrengthSim = WaterShaderProps.SurfSetStrength;
            static readonly int ID_SurfLeanSim = WaterShaderProps.SurfLean;
            static readonly int ID_SurfCompressionSim = WaterShaderProps.SurfCompression;
            static readonly int ID_SurfGreensSim = WaterShaderProps.SurfGreens;
            static readonly int ID_SurfAmbientFadeSim = WaterShaderProps.SurfAmbientFade;
            static readonly int ID_SurfWaterlineFoamSim = WaterShaderProps.SurfWaterlineFoam;
            static readonly int ID_SurfCrestLengthSim = WaterShaderProps.SurfCrestLength;
            static readonly int ID_SurfCrestVariationSim = WaterShaderProps.SurfCrestVariation;
            static readonly int ID_SurfCrestPersistenceSim = WaterShaderProps.SurfCrestPersistence;
            static readonly int ID_SurfDirectionalitySim = WaterShaderProps.SurfDirectionality;
            static readonly int ID_SurfWindDirXZSim = WaterShaderProps.SurfWindDirXZ;
            static readonly int ID_ShoreShoalDepthSim = WaterShaderProps.ShoreShoalDepth;
            static readonly int ID_ShoreCrestFoamLutSim = Shader.PropertyToID("_ShoreCrestFoamLutSim");
            static readonly int ID_ShoreCrestFoamLutActive = Shader.PropertyToID("_ShoreCrestFoamLutActive");
            static readonly int ID_ShoreCrestFoamGain = Shader.PropertyToID("_ShoreCrestFoamGain");
            static readonly int ID_SurfFoamRepartActive = WaterShaderProps.SurfFoamRepartActive;
            static readonly int ID_SurfFoamBoreGain = WaterShaderProps.SurfFoamBoreGain;
            static readonly int ID_SurfFoamTrailGain = WaterShaderProps.SurfFoamTrailGain;
            static readonly int ID_SurfFoamTrailLength = WaterShaderProps.SurfFoamTrailLength;
            static readonly int ID_SurfSwashAmplitudeSim = WaterShaderProps.SurfSwashAmplitude;
            static readonly int ID_SurfSwashMaxSlopeTanSim = WaterShaderProps.SurfSwashMaxSlopeTan;
            static readonly int ID_ShoreSwashDepositGain = WaterShaderProps.ShoreSwashDepositGain;

            /// <summary>Push the surf-front uniforms + the Layer A field textures onto a compute
            /// kernel - the ONE binder every GPU consumer (ripple-sim foam injection, foam
            /// particles) shares, so field packing can never drift between consumers. Textures are
            /// ALWAYS bound (black fallback) so the WebGPU backend never sees an unbound sampler;
            /// the active flag gates all reads. Uniforms a compute doesn't declare are no-ops.</summary>
            internal void BindTo(ComputeShader cs, int kernel)
            {
                bool active = Active && DepthTex != null && SdfTex != null;
                cs.SetFloat(ID_ShoreFoamActive, active ? 1f : 0f);
                // Written unconditionally, ABOVE the inactive early-out below: these are
                // per-ComputeShader uniforms shared by every body's dispatch, so a body that
                // returns early must not leave the previous body's value standing.
                cs.SetFloat(ID_ShoreFoamInject, active && InjectionActive ? 1f : 0f);
                cs.SetTexture(kernel, ID_ShoreDepthTexSim,
                              DepthTex != null ? DepthTex : Texture2D.blackTexture);
                cs.SetTexture(kernel, ID_ShoreSDFTexSim,
                              SdfTex != null ? SdfTex : Texture2D.blackTexture);
                // FOAM-1 LUT: always bound (black fallback - WebGPU must never see an unbound
                // sampler); the active flag gates all reads.
                bool lutActive = active && CrestFoamLutActive && CrestFoamLut != null;
                cs.SetTexture(kernel, ID_ShoreCrestFoamLutSim,
                              CrestFoamLut != null ? CrestFoamLut : Texture2D.blackTexture);
                cs.SetFloat(ID_ShoreCrestFoamLutActive, lutActive ? 1f : 0f);
                if (!active) return;
                cs.SetFloat(ID_ShoreCrestFoamGain, CrestFoamGain);
                // FOAM-2 repartition weights: published as live so the compute's whitewash
                // matches the surface's (the gate lerps the weights in from the legacy constants).
                cs.SetFloat(ID_SurfFoamRepartActive, 1f);
                cs.SetFloat(ID_SurfFoamBoreGain, BoreGain);
                cs.SetFloat(ID_SurfFoamTrailGain, TrailGain);
                cs.SetFloat(ID_SurfFoamTrailLength, TrailLength);
                cs.SetFloat(ID_ShoreFoamGain, FoamGain);
                cs.SetFloat(ID_ShoreWaterlineFoamGain, WaterlineGain);
                cs.SetFloat(ID_ShoreFoamTime, Time);
                cs.SetVector(ID_ShoreSimUvToWorldOrigin, UvToWorldOrigin);
                cs.SetVector(ID_ShoreSimUvToWorldAxes, UvToWorldAxes);
                cs.SetVector(ID_ShoreFieldCenterSim, FieldCenter);
                cs.SetVector(ID_ShoreFieldSizeSim, FieldSize);
                cs.SetFloat(ID_SurfActiveSim, 1f);
                cs.SetFloat(ID_SurfAmplitudeSim, Amplitude);
                cs.SetFloat(ID_SurfWavelengthSim, Wavelength);
                cs.SetFloat(ID_SurfPeriodSim, Period);
                cs.SetFloat(ID_SurfBandDepthSim, BandDepth);
                cs.SetFloat(ID_SurfSetStrengthSim, SetStrength);
                cs.SetFloat(ID_SurfLeanSim, Lean);
                cs.SetFloat(ID_SurfCompressionSim, Compression);
                cs.SetFloat(ID_SurfGreensSim, Greens);
                cs.SetFloat(ID_SurfAmbientFadeSim, AmbientFade);
                cs.SetFloat(ID_SurfCrestLengthSim, CrestLength);
                cs.SetFloat(ID_SurfCrestVariationSim, CrestVariation);
                cs.SetFloat(ID_SurfCrestPersistenceSim, CrestPersistence);
                cs.SetFloat(ID_SurfDirectionalitySim, Directionality);
                cs.SetVector(ID_SurfWindDirXZSim, WindDir);
                cs.SetFloat(ID_ShoreShoalDepthSim, ShoalDepth);
                // FOAM-5: swash run-up amplitude (the compute's EvaluateSurfSwash needs it) + the
                // persistent swash-deposit injection gain.
                cs.SetFloat(ID_SurfSwashAmplitudeSim, SwashAmplitude);
                cs.SetFloat(ID_SurfSwashMaxSlopeTanSim, SwashMaxSlopeTan);
                cs.SetFloat(ID_ShoreSwashDepositGain, SwashDepositGain);
                // Zero on purpose: the sim's Foam kernel injects its OWN waterline term
                // (_ShoreWaterlineFoamGain); letting the analytic lace through too would double
                // it - and the particles' breaker signal never reads the lace anyway.
                cs.SetFloat(ID_SurfWaterlineFoamSim, 0f);
            }
        }

        ShoreFoamState _shoreFoam;

        /// <summary>Surf-front foam source for the Foam kernel. Pushed by WaterVolume.PushShoreFoam
        /// just before StepFoam; inactive (the default) is a no-op.</summary>
        internal void SetShoreFoam(in ShoreFoamState state) => _shoreFoam = state;

        // Push the surf-front foam uniforms + the Layer A field textures onto the Foam kernel.
        // The binder lives on ShoreFoamState (shared with the foam-particles compute).
        void BindShoreFoam(int kernel) => _shoreFoam.BindTo(_cs, kernel);

        /// <summary>Static reflection: the solid mask (submerged footprint of reflector objects) plus its
        /// threshold and resting dip. With <paramref name="enabled"/> false (or a null mask) the Update
        /// kernel is byte-identical to a non-reflecting sim. <paramref name="solidThreshold"/> is in the
        /// mask's coverage units (submerged thickness, world); <paramref name="restDip"/> is pool units.</summary>
        public void SetObstacleReflection(Texture solid, bool enabled, float solidThreshold, float restDip, bool flipY)
        {
            _solidTex = solid;
            _reflectActive = (enabled && solid != null) ? 1f : 0f;
            _reflectSolidThreshold = solidThreshold;
            _reflectRestDip = restDip;
            _reflectFlipY = flipY ? 1f : 0f;
        }

        // Bind the solid mask + reflection uniforms onto a kernel. A texture is always bound (black when
        // inactive) so the backend never sees an unbound sampler; the shader early-outs on _ObstacleReflect.
        void BindObstacleReflection(int kernel)
        {
            _cs.SetFloat(ID_ObstacleReflect, _reflectActive);
            _cs.SetFloat(ID_ObstacleSolidThreshold, _reflectSolidThreshold);
            _cs.SetFloat(ID_ObstacleRestDip, _reflectRestDip);
            _cs.SetFloat(ID_ObstacleFlipY, _reflectFlipY);
            _cs.SetTexture(kernel, ID_ObstacleSolid, _solidTex != null ? _solidTex : Texture2D.blackTexture);
        }

        /// <summary>QUEUE an analytic cosine drop. Every drop queued this frame is applied in ONE
        /// full-grid pass by <see cref="FlushInjections"/> instead of one pass each - a moving object
        /// emitting four drops a frame used to read and write the whole field four times just to stamp
        /// a wake. This is coalescing, not deferral: the flush runs at the same point in the frame an
        /// immediate dispatch landed (before the sim window scrolls and before the solver steps), so
        /// the queued centres are still in the field coordinates they were measured in.</summary>
        public void AddDrop(float x, float y, float radius, float strength)
        {
            if (strength != 0f) MarkInjected();
            radius = Mathf.Max(radius, MinDropTexelRadius / Resolution);
            // Never drop input: a frame busy enough to fill the queue flushes what it has and keeps
            // going, which costs one extra pass on that frame instead of silently losing splashes.
            if (_dropCount >= MaxQueuedInjections) FlushDrops();
            _dropQueue[_dropCount++] = new DropInjection
            {
                Center = new Vector2(x, y),
                Radius = radius,
                Strength = strength,
            };
        }

        /// <summary>Inject a moving sphere's velocity-dipole into the field (Crest-style wake). Unlike
        /// <see cref="AddDrop"/>, which stamps HEIGHT, this accelerates the VELOCITY channel with a
        /// directional dipole, so a travelling object lays a V-wake rather than isotropic rings. All
        /// arguments are pool/sim-normalised (mapped by the caller): <paramref name="center"/> in [-1,1]
        /// like a drop, <paramref name="radius"/> as a half-extent fraction, <paramref name="velXZ"/> the
        /// horizontal motion this step and <paramref name="velY"/> the vertical motion (pool-height units),
        /// <paramref name="weight"/> the submersion x user weight, <paramref name="strength"/> the master
        /// gain. No-op look when weight is 0. QUEUED like a drop (see <see cref="AddDrop"/>) and applied
        /// by <see cref="FlushInjections"/>, so a hull carrying several interactors costs ONE full-grid
        /// pass rather than one each. A scene with no interactor never dispatches at all.</summary>
        public void AddSphereInteraction(Vector2 center, float radius, Vector2 velXZ, float velY,
                                         float weight, float strength, float wakeFoamDose, float verticalForceCap)
        {
            if ((weight != 0f && strength != 0f) || wakeFoamDose > 0f)
                MarkInjected();
            radius = Mathf.Max(radius, MinDropTexelRadius / Resolution);
            if (_sphereCount >= MaxQueuedInjections) FlushSpheres();
            _sphereQueue[_sphereCount++] = new SphereInjection
            {
                Center = center,
                VelXZ = velXZ,
                Radius = radius,
                VelY = velY,
                Weight = weight,
                Strength = strength,
                WakeFoamDose = Mathf.Max(0f, wakeFoamDose),
                VerticalForceCap = Mathf.Max(0f, verticalForceCap),
            };
        }

        void MarkInjected()
        {
            _hasReceivedInjection = true;
            unchecked { _injectionGeneration++; }
        }

        /// <summary>Apply everything queued since the last frame: ONE full-grid pass per KIND of stamp,
        /// not one per stamp. Call once per frame from the body's update, BEFORE the sim window scrolls -
        /// the queued centres are in the field coordinates they were measured in, and scrolling first
        /// would slide them off by one window step.</summary>
        public void FlushInjections()
        {
            FlushDrops();
            FlushSpheres();
        }

        void FlushDrops()
        {
            if (_dropCount == 0) return;
            _dropBuffer ??= new ComputeBuffer(MaxQueuedInjections, DropInjectionStride);
            _dropBuffer.SetData(_dropQueue, 0, 0, _dropCount);
            _cs.SetBuffer(_kDrop, ID_DropQueue, _dropBuffer);
            _cs.SetInt(ID_DropQueueCount, _dropCount);
            _cs.SetVector(ID_DropAxisScale, _dropAxisScale);
            Dispatch(_kDrop);
            _dropCount = 0;
        }

        void FlushSpheres()
        {
            if (_sphereCount == 0) return;
            _sphereBuffer ??= new ComputeBuffer(MaxQueuedInjections, SphereInjectionStride);
            _sphereBuffer.SetData(_sphereQueue, 0, 0, _sphereCount);
            _cs.SetBuffer(_kSphereInteract, ID_SphereQueue, _sphereBuffer);
            _cs.SetInt(ID_SphereQueueCount, _sphereCount);
            _cs.SetVector(ID_SphereAxisScale, _dropAxisScale);
            // Wake foam (move #3): the kernel also stamps foam at the hull. It reads FoamSrc and writes
            // FoamDst for every texel (copy-through outside the stamp), so the foam buffer ping-pongs in
            // lockstep with the height field here - swap it too. At _wakeFoamStrength 0 this is a pure
            // copy, so foam is unchanged. StepFoam (later this frame) advects + decays the deposit.
            _cs.SetFloat(ID_WakeFoamStrength, _wakeFoamStrength);
            _cs.SetFloat(ID_WakeFoamRadiusScale, _wakeFoamRadiusScale);
            _cs.SetFloat(ID_WakeStartForceCap, _wakeStartForceCap);
            _cs.SetTexture(_kSphereInteract, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kSphereInteract, ID_FoamDst, _foamB);
            Dispatch(_kSphereInteract);
            (_foamA, _foamB) = (_foamB, _foamA);
            _sphereCount = 0;
        }

        /// <summary>Forces the surface by the change in submerged footprint
        /// (prev - curr), generalising the old sphere displacement to any meshes.</summary>
        public void ApplyObstacle(Texture prev, Texture curr, float strength, bool flipY, float deadband)
        {
            _cs.SetTexture(_kObstacle, ID_ObstaclePrev, prev);
            _cs.SetTexture(_kObstacle, ID_ObstacleCurr, curr);
            _cs.SetFloat(ID_ObstacleStrength, strength);
            _cs.SetFloat(ID_ObstacleFlipY, flipY ? 1f : 0f);
            _cs.SetFloat(ID_ObstacleDeadband, deadband);
            Dispatch(_kObstacle);
        }

        /// <summary>Temporal EMA of the obstacle footprint: writes <paramref name="curr"/> =
        /// lerp(<paramref name="prev"/>, <paramref name="raw"/>, <paramref name="blend"/>). Low-passes the
        /// footprint so moving objects emit clean waves instead of tight-ring packets. Runs as a compute
        /// kernel (the fullscreen material equivalent failed on WebGPU); <paramref name="curr"/> must be an
        /// r32 (RFloat) render texture with random write, the only RW storage format WebGPU guarantees.</summary>
        public void SmoothObstacleFootprint(Texture prev, Texture raw, RenderTexture curr, float blend)
        {
            _cs.SetTexture(_kObstacleSmooth, ID_ObstacleSmoothPrev, prev);
            _cs.SetTexture(_kObstacleSmooth, ID_ObstacleSmoothRaw, raw);
            _cs.SetTexture(_kObstacleSmooth, ID_ObstacleSmoothDst, curr);
            _cs.SetFloat(ID_ObstacleTemporalBlend, blend);
            // Dispatch directly, NOT via Dispatch(): this kernel operates on the obstacle textures, not
            // the height-field ping-pong, so it must not bind Src/Dst or swap _a/_b (which would corrupt
            // the sim state). Grid is the same size as the sim, so _groups covers it exactly.
            _cs.Dispatch(_kObstacleSmooth, _groups, _groups, 1);
        }

        public void StepSimulation(float waveSpeed, float damping, float viscosity)
        {
            _cs.SetFloat(ID_WaveSpeed, waveSpeed);
            _cs.SetFloat(ID_Damping, damping);
            _cs.SetFloat(ID_RippleViscosity, viscosity);
            _cs.SetVector(ID_WaveAxisWeight, _waveAxisWeight);
            BindBed(_kUpdate);
            BindObstacleReflection(_kUpdate);
            SetGridUniforms();
            _cs.SetTexture(_kUpdate, ID_Src, _a);
            _cs.SetTexture(_kUpdate, ID_Dst, _b);
            _cs.SetTexture(_kUpdate, ID_FlowSrc, _horizontalFlowA);
            _cs.SetTexture(_kUpdate, ID_FlowDst, _horizontalFlowB);
            _cs.SetVector(ID_FlowGradientToWorld, _flowGradientToWorld);
            _cs.SetVector(ID_FlowTexelMeters, _flowTexelMeters);
            _cs.Dispatch(_kUpdate, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
            (_horizontalFlowA, _horizontalFlowB) = (_horizontalFlowB, _horizontalFlowA);
        }

        public void UpdateNormals() => Dispatch(_kNormal);

        /// <summary>Advance the foam buffer: advect along the surface flow, diffuse,
        /// generate from turbulence, decay. Decay is bi-exponential: thick fresh foam
        /// survives at <paramref name="decayFresh"/> per reference step, thin residual
        /// lace at the (slower, closer to 1) <paramref name="decayResidual"/>. Generation
        /// and decay scale by <paramref name="dtSteps"/> (elapsed time in reference steps,
        /// 1 = 1/60 s) so foam evolves frame-rate independently; <paramref name="decayRate"/>
        /// is a user time-scale on decay only (1 = authored speed, 2 = twice as fast).
        /// <paramref name="headroom"/> scales the additive generation by the FREE surface
        /// (1 - existing foam), so turbulence yields to whatever foamed a column first instead of
        /// clipping on top of it; 0 = off, generation ignores existing foam exactly as before.
        /// Reads the current height/normal state; ping-pongs the foam textures.</summary>
        public void StepFoam(float genRate, float genThreshold, float minWaveHeight, float decayFresh,
                             float decayResidual, float spread, float fromSpeed, float fromCurv,
                             float advect, float dtSteps, float decayRate,
                             float breakStrength, float breakRange, float crestBias, float deposit,
                             float headroom,
                             float wetDrySurvival = 1f, bool foamVisible = true)
        {
            SetGridUniforms();
            _cs.SetFloat(ID_FoamCrestBias, crestBias);
            _cs.SetFloat(ID_FoamDeposit, deposit);
            _cs.SetFloat(ID_FoamHeadroom, headroom);
            _cs.SetFloat(ID_FoamGenRate, genRate);
            _cs.SetFloat(ID_FoamGenThreshold, genThreshold);
            _cs.SetFloat(ID_FoamMinWaveHeight, minWaveHeight);
            _cs.SetFloat(ID_FoamDecayFresh, decayFresh);
            _cs.SetFloat(ID_FoamDecayResidual, decayResidual);
            _cs.SetFloat(ID_FoamDtSteps, dtSteps);
            _cs.SetFloat(ID_FoamDecayRate, decayRate);
            // Folded here, not in the kernel: pow() of two uniforms is one constant, and evaluating it
            // per texel cost a transcendental on every one of the grid's threads for nothing.
            _cs.SetFloat(ID_WetDryDecay, Mathf.Pow(wetDrySurvival, dtSteps));
            // 0 keeps the R channel empty while this pass runs only to maintain the wet mark. Gating
            // at the single write site instead of zeroing gen / deposit / shore injection / wake
            // separately is what makes it impossible to leave a foam source switched on by accident.
            _cs.SetFloat(ID_FoamWriteMask, foamVisible ? 1f : 0f);
            _cs.SetFloat(ID_FoamSpread, CalculateFoamSpread(spread, dtSteps));
            _cs.SetFloat(ID_FoamFromSpeed, fromSpeed);
            _cs.SetFloat(ID_FoamFromCurv, fromCurv);
            _cs.SetFloat(ID_FoamAdvect, CalculateFoamAdvection(advect, dtSteps));
            _cs.SetFloat(ID_FoamBreakStrength, breakStrength);
            _cs.SetFloat(ID_FoamBreakRange, breakRange);
            _cs.SetTexture(_kFoam, ID_Src, _a);        // height state (read)
            _cs.SetTexture(_kFoam, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kFoam, ID_FoamDst, _foamB);
            // The shallow-water breaking boost reads the bed map (BedColumnDepth), so it must be
            // bound onto the Foam kernel too - it was previously only bound for Update. A texture is
            // always bound (black when inactive) so the WebGPU backend never sees an unbound sampler;
            // the shader gates every read on _UseBedDepth, so a bedless body is unaffected.
            BindBed(_kFoam);
            BindShoreFoam(_kFoam);                     // surf-front whitewash source (inert by default)
            _cs.Dispatch(_kFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);
        }

        /// <summary>Subtracts the mean height to conserve volume. The mean is computed EXACTLY
        /// by a two-pass compute reduction (the old Blit + GenerateMips top-mip read silently
        /// point-sampled in WebGPU builds - float32 isn't filterable there - making the "mean"
        /// one arbitrary texel and popping the whole plane). The subtracted mean stays clamped
        /// to +/- <paramref name="maxCorrection"/> (pool units) as a pure safety bound.</summary>
        public void ConserveVolume(float maxCorrection)
        {
            SetGridUniforms();
            _cs.SetFloat(ID_MeanCorrectionMax, maxCorrection);

            _cs.SetTexture(_kReduceMean, ID_Src, _a);
            _cs.SetBuffer(_kReduceMean, ID_PartialSums, _partialSums);
            _cs.Dispatch(_kReduceMean, _groups, _groups, 1);

            _cs.SetBuffer(_kReduceMeanFinal, ID_PartialSums, _partialSums);
            _cs.SetBuffer(_kReduceMeanFinal, ID_MeanResult, _meanResult);
            _cs.Dispatch(_kReduceMeanFinal, 1, 1, 1);

            _cs.SetBuffer(_kConserve, ID_MeanResult, _meanResult);
            Dispatch(_kConserve);
        }

        /// <summary>Asynchronously test whether an awakened ocean ripple field has settled enough to
        /// sleep. Thresholds are expressed in the state textures' native units: height/vertical velocity
        /// and wet mark are pool-height units, horizontal flow is metres/second, foam is coverage.
        /// Unsupported async-readback backends safely retain the awake behaviour.</summary>
        internal void RequestSleepCheck(float heightThreshold, float verticalVelocityThreshold,
                                        float horizontalFlowThreshold, float foamThreshold,
                                        float wetMarkThreshold)
        {
            ValidatePositiveThreshold(heightThreshold, nameof(heightThreshold));
            ValidatePositiveThreshold(verticalVelocityThreshold, nameof(verticalVelocityThreshold));
            ValidatePositiveThreshold(horizontalFlowThreshold, nameof(horizontalFlowThreshold));
            ValidatePositiveThreshold(foamThreshold, nameof(foamThreshold));
            ValidatePositiveThreshold(wetMarkThreshold, nameof(wetMarkThreshold));
            if (!_hasReceivedInjection || !_activityReadback.CanRequest) return;

            SetGridUniforms();
            _cs.SetVector(ID_ActivityInverseThresholds, new Vector4(
                1f / heightThreshold,
                1f / verticalVelocityThreshold,
                1f / horizontalFlowThreshold,
                1f / foamThreshold));
            _cs.SetFloat(ID_ActivityWetMarkInverseThreshold, 1f / wetMarkThreshold);
            _cs.SetTexture(_kReduceActivity, ID_Src, _a);
            _cs.SetTexture(_kReduceActivity, ID_FlowSrc, _horizontalFlowA);
            _cs.SetTexture(_kReduceActivity, ID_FoamSrc, _foamA);
            _cs.SetBuffer(_kReduceActivity, ID_ActivityPartialMax, _activityPartialMax);
            _cs.Dispatch(_kReduceActivity, _groups, _groups, 1);

            _cs.SetBuffer(_kReduceActivityFinal, ID_ActivityPartialMax, _activityPartialMax);
            _cs.SetBuffer(_kReduceActivityFinal, ID_ActivityResult, _activityResult);
            _cs.Dispatch(_kReduceActivityFinal, 1, 1, 1);

            _activityReadbackGeneration = _injectionGeneration;
            _activityReadback.Request(_activityResult, _onActivityReadback);
        }

        static void ValidatePositiveThreshold(float threshold, string parameterName)
        {
            if (float.IsNaN(threshold) || float.IsInfinity(threshold) || threshold <= 0f)
                throw new System.ArgumentOutOfRangeException(parameterName, threshold,
                    "Water ripple sleep thresholds must be finite and greater than zero.");
        }

        void OnActivityReadback(AsyncGPUReadbackRequest request)
        {
            if (_activityReadbackGeneration != _injectionGeneration) return;
            var data = request.GetData<float>();
            if (data.Length != 1 || !IsSettledActivity(data[0])) return;
            _hasReceivedInjection = false;
        }

        /// <summary>
        /// Shift the whole sim state (height/velocity/normal and foam) by an integer
        /// texel offset so ripples stay world-anchored while a windowed body's sim
        /// follows the camera. The offset is the raw kernel shift: <c>Dst[p] = Src[p - offset]</c>,
        /// so cells exposed at the trailing edge reset to rest. The caller (WaterVolume)
        /// computes the grid-space offset from the window-centre movement. No-op at (0,0).
        /// </summary>
        public void Scroll(int offsetX, int offsetY)
        {
            if (offsetX == 0 && offsetY == 0) return;

            SetGridUniforms();
            _cs.SetInts(ID_ScrollOffset, offsetX, offsetY);

            _cs.SetTexture(_kScroll, ID_Src, _a);
            _cs.SetTexture(_kScroll, ID_Dst, _b);
            _cs.SetTexture(_kScroll, ID_FlowSrc, _horizontalFlowA);
            _cs.SetTexture(_kScroll, ID_FlowDst, _horizontalFlowB);
            _cs.Dispatch(_kScroll, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
            (_horizontalFlowA, _horizontalFlowB) = (_horizontalFlowB, _horizontalFlowA);

            _cs.SetTexture(_kScrollFoam, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kScrollFoam, ID_FoamDst, _foamB);
            _cs.Dispatch(_kScrollFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);

        }
    }
}
