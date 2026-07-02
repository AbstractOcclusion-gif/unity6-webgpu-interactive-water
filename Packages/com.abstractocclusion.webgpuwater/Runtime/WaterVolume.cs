// WebGL Water - main controller (Unity 6 / URP port)
// Drives the simulation, caustics, mouse interaction and the
// orbiting camera. Port of main.js / renderer.js by Evan Wallace (MIT).
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [DefaultExecutionOrder(-50)]
    public class WaterVolume : MonoBehaviour
    {
        /// <summary>How WaterInteractable objects disturb the surface.</summary>
        public enum ObjectInteraction
        {
            /// <summary>Analytic cosine drops from bobbing/drift, cloned from the mouse
            /// interaction (WaterInteractable emits via AddRipple).</summary>
            MouseLikeDrops,
            /// <summary>Rasterized submerged-footprint displacement (prev - curr delta).</summary>
            FootprintDelta
        }

        [Header("Assigned by the scene builder")]
        public ComputeShader simCompute;
        public Shader causticsShader;
        public Shader obstacleShader;     // WebGLWater/ObstacleDepth - footprint of interactable objects
        public Mesh waterMesh;            // XY grid plane, [-1,1], shared with the water surface renderers
        public Camera targetCamera;
        public Light sun;                 // directional light: drives water, caustics AND real shadows

        [Header("Look / surfaces")]
        public Texture tiles;             // pool tile albedo sampled by the water reflection (assign your own)
        public Cubemap sky;               // sky cubemap for above-water reflections

        [Header("Water volume (placement)")]
        [Tooltip("World half-size per pool unit, per axis: X = half width, Y = depth to the " +
                 "floor, Z = half length. (1,1,1) is the original 1:1 pool. X != Z gives a " +
                 "rectangular footprint; Y alone makes it shallow/deep. The volume's POSITION " +
                 "and ROTATION come from THIS GameObject's Transform - move/rotate it to place " +
                 "the water. Set extent/transform before Play; the obstacle map reads them at startup.")]
        public Vector3 volumeExtent = Vector3.one;

        [Header("Large-water sim window")]
        [Tooltip("For bodies larger than the threshold, run the interactive ripple sim in a " +
                 "camera-following window instead of stretching the fixed grid over the whole " +
                 "surface (which goes blocky on big water). Analytic wind waves still cover " +
                 "everywhere. Small/medium bodies are unaffected.")]
        public bool enableLargeBodyWindow = true;
        [Tooltip("World half-extent (max of X,Z) above which windowing turns on. At/below this " +
                 "the whole-body sim is used exactly as before.")]
        [Min(1f)] public float largeBodyThreshold = DefaultLargeBodyThreshold;
        [Tooltip("Half-size (world metres) of the camera-following sim window. Ripple detail is " +
                 "2 * this / sim resolution per texel.")]
        [Min(1f)] public float simWindowMeters = DefaultSimWindowMeters;
        [Tooltip("On: keep the window fully inside the body footprint (enclosed bodies). Off: the " +
                 "window may overhang the edge and water beyond the footprint is analytic-only " +
                 "(natural for open water).")]
        public bool clampWindowToShore = false;
        [Tooltip("Width, in sim texels, over which the window's ripple fades to analytic-only at " +
                 "its border so there is no seam.")]
        [Range(0f, 32f)] public float simWindowEdgeFadeTexels = 8f;

        [Header("Water body (multi-instance)")]
        [Tooltip("Renderers driven by THIS body via a MaterialPropertyBlock (surface above/under, " +
                 "pool, god rays). Assigned by the scene builder.")]
        public Renderer surfaceAbove;
        public Renderer surfaceUnder;
        public Renderer poolRenderer;
        public Renderer godRayRenderer;
        [Tooltip("The primary body also mirrors its data to global shader state, the fallback " +
                 "for objects that don't carry a WaterMembership (which otherwise resolves each " +
                 "object's own containing body). Exactly one body should be primary.")]
        public bool isPrimary = true;

        [Header("Performance (Phase 3)")]
        [Tooltip("Quality tier asset scaling sim/caustic resolution and god-ray steps. Leave " +
                 "empty for the default (256/1024/24) look. Assigned by the scene builder.")]
        public WaterQuality quality;
        [Tooltip("Pause a body's simulation, caustics and height readback - and stop drawing it - " +
                 "when it is off-screen OR beyond Activation Distance, and let only the nearest few " +
                 "bodies simulate at once. A single visible body is unaffected. Turn off to force " +
                 "this body to always simulate and render.")]
        public bool enableCulling = true;
        [Tooltip("Bodies whose centre is farther than this from the camera pause their simulation " +
                 "(they hold their last state). Matches the camera far clip by default.")]
        public float activationDistance = CameraFarClip;

        public enum ReflectionMode { SkyOnly, SSR, Planar }

        // The reflection BASE (what SkyOnly shows and what SSR/Planar layer over): the built-in
        // procedural sky cubemap, or the scene's URP reflection probe / skybox (unity_SpecCube0).
        public enum EnvironmentSource { ProceduralSky, UrpProbe }

        [Header("Reflections (Phase 3c)")]
        [Tooltip("How THIS body reflects. SSR (screen-space over the procedural sky) scales to many " +
                 "bodies. Planar is a full extra scene render across this body's plane - use it for at " +
                 "most ONE 'hero' body. SkyOnly is cheapest (procedural sky only). SSR needs Depth + " +
                 "Opaque Texture enabled on the active URP asset.")]
        public ReflectionMode reflectionMode = ReflectionMode.SSR;

        [Tooltip("Reflection base environment. ProceduralSky uses the generated sky cubemap (the demo " +
                 "look). UrpProbe reflects the scene's active reflection probe / skybox so the water " +
                 "matches your lit environment. Orthogonal to the mode above and unaffected by the tier.")]
        public EnvironmentSource environmentSource = EnvironmentSource.ProceduralSky;

        /// <summary>The primary water body: the global fallback for objects without a
        /// <see cref="WaterMembership"/>. Per-object association goes through
        /// <see cref="BodyContaining"/>.</summary>
        public static WaterVolume Primary { get; private set; }

        /// <summary>Resolve the body an object should use when it isn't inside any specific
        /// one: the primary body, or any found body as a fallback. Prefer
        /// <see cref="BodyContaining"/> for objects that have a world position.</summary>
        public static WaterVolume Resolve() => Primary != null ? Primary : FindFirstObjectByType<WaterVolume>();

        /// <summary>The water body a world point belongs to: the body whose horizontal
        /// footprint contains the point, nearest-centre wins when several overlap, and the
        /// primary body as a fallback when the point is outside every footprint. Objects call
        /// this each frame so they float on, and are lit by, the lake they are actually in.</summary>
        public static WaterVolume BodyContaining(Vector3 worldPoint)
        {
            WaterVolume best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (!body.WorldToPoolXZ(worldPoint, out _, out _)) continue;

                // Tiebreak on HORIZONTAL distance to centre; the footprint ignores height,
                // so a vertical gap between the point and a body must not sway the choice.
                Vector3 toCenter = body.VolumeCenter - worldPoint;
                float sqr = toCenter.x * toCenter.x + toCenter.z * toCenter.z;
                if (sqr < bestSqr) { bestSqr = sqr; best = body; }
            }
            return best != null ? best : Resolve();
        }

        /// <summary>All live water bodies. Used by the primary's input router to send a
        /// click to whichever body's surface the ray hits, and by <see cref="BodyContaining"/>.</summary>
        static readonly List<WaterVolume> Bodies = new List<WaterVolume>();

        [Header("Simulation")]
        [Tooltip("Direction TOWARD the light. Auto-driven from 'sun' when one is assigned.")]
        public Vector3 lightDir = new Vector3(2f, 2f, -1f);
        public int causticResolution = 1024;

        [Header("Object interaction")]
        [Tooltip("How floating objects disturb the water. MouseLikeDrops clones the mouse " +
                 "interaction: analytic cosine drops from bobbing and drift (uses Ripple " +
                 "Radius/Strength below; smooth, zero rasterization noise, slow rotation is " +
                 "silent). FootprintDelta displaces by the rasterized submerged footprint " +
                 "(shaped wakes for large hulls; costlier and noisier).")]
        public ObjectInteraction objectInteraction = ObjectInteraction.MouseLikeDrops;
        [Tooltip("FootprintDelta mode: MASTER strength for how strongly submerged objects " +
                 "displace the water (height units, comparable to Ripple Strength). " +
                 "Per-object weighting is WaterInteractable.displaceScale.")]
        [Range(0f, 0.5f)] public float obstacleStrength = 0.08f;
        [Tooltip("Temporal smoothing of the object footprint (0 = off). Low-pass filters " +
                 "the displacement a floater injects, so continuous bobbing/rotation emits " +
                 "a few long clean waves instead of a dense packet of tight rings. The " +
                 "total displaced volume is unchanged; higher = calmer but lazier response.")]
        [Range(0f, 0.95f)] public float obstacleSmoothing = 0.65f;
        [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
        public bool obstacleFlipY = true;

        [Header("Water fog (Beer-Lambert)")]
        [Tooltip("Global depth absorption, shared by the surface, objects and pool.")]
        public bool waterFog = false;
        public Color fogColor = new Color(0.10f, 0.30f, 0.40f);
        [Tooltip("Per-channel extinction; red highest so it absorbs first. HDR: push a channel " +
                 "above 1 for very heavy absorption (fully opaque water on short paths).")]
        [ColorUsage(false, true)] public Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Tooltip("Overall fog multiplier. Higher = thicker; crank it (with extinction) for pea-soup water.")]
        [Range(0f, MaxFogDensity)] public float fogDensity = 2f;
        [Tooltip("Art-directed turbidity independent of depth: lerp the view THROUGH the surface " +
                 "toward the fog colour. 0 = clear, 1 = fully non-transparent water. Reflections " +
                 "still show on top (tune with the material's Reflection Strength).")]
        [Range(0f, 1f)] public float waterOpacity = 0f;

        [Header("Depth attenuation (downwelling)")]
        [Tooltip("Darken submerged surfaces, caustics and god rays the DEEPER they sit, " +
                 "independent of view distance. Separate from the view-path fog above.")]
        public bool depthDarken = false;
        [Tooltip("Per-channel downwelling extinction (red highest so deep water shifts blue). " +
                 "Applied as exp(-extinction * strength * depth).")]
        public Color depthExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Tooltip("Master multiplier on the depth term (acts like the fog density).")]
        [Range(0f, 8f)] public float depthDarkenStrength = 1f;
        [Tooltip("Extra softening of projected caustics on objects, per world unit of depth.")]
        [Range(0f, 8f)] public float causticDepthFade = 0.5f;
        [Tooltip("How fast god-ray shafts fade with depth, per world unit of depth.")]
        [Range(0f, 8f)] public float godRayDepthFade = 0.5f;
        [Tooltip("Mirror the fog extinction into the depth extinction each frame, so one dial " +
                 "drives fog + depth darkening. Off = the depth colour is fully independent.")]
        public bool linkDepthToFog = false;

        [Header("Bed depth (real terrain depth)")]
        [Tooltip("Use the baked terrain bed height for real water-column depth (shoreline " +
                 "gradient). Off = flat-floor behaviour.")]
        public bool useBedDepth = false;
        [Tooltip("Terrain whose heightmap defines the lake bed. Auto-resolves to the active " +
                 "Terrain if empty. Baked once at startup; call RebakeBed() (or the context-menu " +
                 "item) if the terrain changes.")]
        public Terrain bedTerrain;
        [Tooltip("Resolution of the baked pool-space bed-height map.")]
        [Range(MinBedResolution, MaxBedResolution)] public int bedResolution = 256;
        [Tooltip("Colour the surface tints toward over deep water.")]
        public Color deepWaterColor = new Color(0.02f, 0.10f, 0.15f);
        [Tooltip("World-unit depth at which the shoreline gradient reaches ~63% toward the deep " +
                 "colour. Smaller = the water darkens in shallower depth.")]
        [Range(0.1f, 50f)] public float shorelineFadeDepth = 6f;
        [Tooltip("Maximum tint toward the deep-water colour.")]
        [Range(0f, 1f)] public float shorelineStrength = 0.8f;

        [Header("Wind waves (spectral)")]
        [Tooltip("Ambient wind-driven wave layer composited on top of the interactive ripples. " +
                 "Floating objects ride these waves too.")]
        public bool windWaves = true;
        [Tooltip("Wind speed (m/s). ~3 = light breeze.")]
        [Range(0f, 15f)] public float windSpeed = 3f;
        [Tooltip("Wind heading in degrees: 0 = blowing toward +X (i.e. coming from the west).")]
        [Range(0f, 360f)] public float windFromDegrees = 0f;
        [Tooltip("Physical size the pool half-extent ([-1,1] -> +/-this) represents, in metres. " +
                 "Sets wave scale; fetch is twice this.")]
        [Range(1f, 50f)] public float poolHalfExtentMeters = 10f;
        [Tooltip("Number of sinusoidal components summed for the wave layer.")]
        [Range(1, WaterWaveBank.MaxWaves)] public int waveCount = 12;
        [Tooltip("Artistic multiplier on the physically-derived wave height (a light breeze " +
                 "on a small lake is physically sub-cm, so some exaggeration reads better).")]
        [Range(0f, 12f)] public float waveAmplitudeScale = 4f;
        [Tooltip("Higher = waves cling more tightly to the wind direction (parallel, river-like). " +
                 "Lower = broader, choppier crossing crests.")]
        [Range(1f, 12f)] public float waveDirectionSpread = 2f;
        [Tooltip("Scales how strongly the wind waves tilt the surface normal.")]
        [Range(0f, 3f)] public float waveNormalStrength = 1f;

        [Header("Foam")]
        public bool foam = false;
        [Tooltip("How fast turbulence creates foam.")]
        [Range(0f, 2f)] public float foamGenRate = 0.6f;
        [Tooltip("Survival per step of thick, fresh foam. Lower = bursts collapse faster.")]
        [Range(0.80f, 1f)] public float foamDecay = 0.96f;
        [Tooltip("Survival per step of thin residual lace (should sit above the fresh decay, near 1). Higher = lace lingers longer after the burst.")]
        [Range(0.90f, 1f)] public float foamDecayResidual = 0.993f;
        [Tooltip("Diffusion of foam toward neighbours.")]
        [Range(0f, 1f)] public float foamSpread = 0.2f;
        [Tooltip("How far foam is carried along the surface flow each step (texels). 0 = old isotropic spread.")]
        [Range(0f, 8f)] public float foamAdvect = 3f;
        public float foamFromSpeed = 6f;
        public float foamFromCurvature = 30f;
        [Space]
        public Color foamColor = Color.white;
        [Range(0f, 2f)] public float foamStrength = 1f;
        [Tooltip("Width of the foam band along the pool walls (pool units).")]
        [Range(0f, 0.5f)] public float foamBorderWidth = 0.08f;
        [Tooltip("Depth band for contact foam where objects meet the waterline.")]
        [Range(0f, 0.5f)] public float foamContactDepth = 0.06f;

        [Header("Ripple tuning")]
        [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
        [Range(0.1f, 2.0f)] public float waveSpeed = 0.6f;
        [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
        [Range(0.90f, 1.0f)] public float damping = 0.99f;
        [Tooltip("Simulation sub-steps per frame. More = faster, smoother propagation.")]
        [Range(1, 8)] public int stepsPerFrame = 2;
        [Tooltip("Height added by a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.001f, 0.08f)] public float rippleStrength = 0.025f;
        [Tooltip("Radius of a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.005f, 0.2f)] public float rippleRadius = 0.05f;
        [Tooltip("Seed the pool with random ripples on start.")]
        public bool seedRipplesOnStart = true;
        [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
        public bool conserveVolume = true;

        [Header("Camera")]
        public OrbitCamera orbit;

        [Header("Splash")]
        [Tooltip("Shared splash emitter used for mouse interaction (and objects).")]
        public WaterSplashEmitter splashEmitter;

        // runtime
        WaterSimulation _water;
        WaterObstacle _obstacle;

        // wind-wave layer (shared by the surface shader and CPU buoyancy)
        readonly WaterWaveBank _waveBank = new WaterWaveBank();
        float _waveTime;
        Vector4 _waveGenSignature = new Vector4(float.NaN, 0f, 0f, 0f);
        float _waveGenSpread = float.NaN;
        float _waveGenVerticalExtent = float.NaN; // volume y-extent baked into the current bank
        bool _waveGenEnabled;

        // CPU copy of the height field for buoyancy queries
        Color[] _heightCpu;
        bool _heightReady, _readbackInFlight;
        // True on backends without AsyncGPUReadback (e.g. WebGPU): buoyancy and surface queries
        // fall back to the analytic waterline (flat rest + wind waves) so objects still float.
        bool _analyticBuoyancyFallback;
        int _simRes = WaterQuality.Default.SimResolution; // grid resolution, set from the quality tier at OnEnable
        bool _godRaysAllowed = true;                       // false when the tier turns god rays off
        bool _richReflectionsAllowed = true;               // false when the tier caps reflections to SkyOnly
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable, destroyed at OnDisable.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        const string KW_SSR = "_USE_SSR";
        const string KW_PLANAR = "_USE_PLANAR";
        const string KW_URP_PROBE = "_USE_URP_PROBE";
        Material _causticMat;
        RenderTexture _causticRT;
        Texture2D _bedTex;   // pool-space terrain bed height (R), baked from the Terrain
        bool _bedBaked;
        RenderTexture _heightMip;
        CommandBuffer _cb;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        // Two sinks over the SAME uniform derivations (see WriteBodyUniforms): one writes into
        // this body's property block (per-body renderers), one writes the global fallback that
        // object shaders without a WaterMembership read. Cached to avoid per-frame allocation.
        readonly MpbUniformSink _mpbSink = new MpbUniformSink();
        readonly GlobalUniformSink _globalSink = new GlobalUniformSink();

        bool _paused;

        // ---- large-water sim window (camera-following) ----
        bool _windowed;           // this body runs the windowed sim (decided at OnEnable)
        Vector3 _simCenter;       // world centre of the window, on the surface plane, texel-snapped
        int _simCellX, _simCellZ; // window centre as integer texel indices in the volume's local frame
        bool _simCenterInit;

        // ---- sim culling / active-sim budget (Phase 3) ----
        // Only the nearest bodies simulate each frame; off-screen bodies also stop drawing.
        // The schedule is computed once per frame (frame-guarded) for ALL bodies, so it is
        // independent of the arbitrary order in which the bodies Update.
        const int ActiveSimBudget = 4;        // nearest bodies allowed to simulate at once
        const float WaveHeightMargin = 0.1f;  // pool-space headroom above y=0 for wind-wave crests in the cull box
        static readonly Plane[] _frustumPlanes = new Plane[6];
        static int _scheduleFrame = -1;
        bool _visible = true;   // inside the camera frustum -> its renderers draw
        bool _simulate = true;  // visible AND in range AND within the sim budget -> runs the GPU sim

        // Camera framing. activationDistance defaults to the far clip so "beyond the far clip"
        // is exactly what pauses a distant body - the two stay coupled, not coincidentally equal.
        const float CameraFieldOfView = 45f;
        const float CameraNearClip = 0.01f;
        const float CameraFarClip = 100f;

        // Baked bed-height map resolution bounds (pool-space).
        const int MinBedResolution = 64;
        const int MaxBedResolution = 1024;

        // Large-water sim-window defaults (world metres). Threshold sits above the window
        // half-size so a body only marginally larger than the window stays whole-body
        // (windowing it would scroll for near-zero detail gain).
        const float DefaultLargeBodyThreshold = 48f;
        const float DefaultSimWindowMeters = 32f;

        // Upper bound on fog density; high enough that (with extinction) water can read fully
        // opaque even on short view paths.
        const float MaxFogDensity = 50f;

        // Startup pool seeding: a few random ripples so the surface isn't dead-flat on load.
        const int SeedRippleCount = 20;
        const float SeedRippleRadius = 0.03f;
        const float SeedRippleStrength = 0.01f;

        // interaction (only the primary body runs this; it routes clicks to the hit body)
        const int MODE_NONE = -1, MODE_ADD_DROPS = 0, MODE_ORBIT = 2;

        // World distance the cursor must travel between injected ripples while dragging.
        // Holding still otherwise re-injects into the same texels every frame, pumping
        // unbounded energy into the explicit solver. The initial press bypasses this.
        const float MinDragWorldSpacing = 0.02f;

        int _mode = MODE_NONE;
        Vector2 _oldMouse;
        Vector3 _prevWorld;         // last world-space ripple point during a drag
        WaterVolume _dragBody;  // body being rippled this drag
        bool _forceDrop;

        // shader global ids
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_WaterTexel = Shader.PropertyToID("_WaterTexel");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = Shader.PropertyToID("_LightDir");
        static readonly int ID_SunColor = Shader.PropertyToID("_SunColor");
        static readonly int ID_FogColor = Shader.PropertyToID("_WaterFogColor");
        static readonly int ID_FogExt = Shader.PropertyToID("_WaterExtinction");
        static readonly int ID_FogDensity = Shader.PropertyToID("_WaterFogDensity");
        static readonly int ID_FogEnabled = Shader.PropertyToID("_WaterFogEnabled");
        static readonly int ID_WaterOpacity = Shader.PropertyToID("_WaterOpacity");
        static readonly int ID_DepthExt = Shader.PropertyToID("_DepthExtinction");
        static readonly int ID_DepthStrength = Shader.PropertyToID("_DepthDarkenStrength");
        static readonly int ID_DepthEnabled = Shader.PropertyToID("_DepthDarkenEnabled");
        static readonly int ID_CausticDepthFade = Shader.PropertyToID("_CausticDepthFade");
        static readonly int ID_GodRayDepthFade = Shader.PropertyToID("_GodRayDepthFade");
        static readonly int ID_BedTex = Shader.PropertyToID("_BedTex");
        static readonly int ID_BedValid = Shader.PropertyToID("_BedValid");
        static readonly int ID_UseBedDepth = Shader.PropertyToID("_UseBedDepth");
        static readonly int ID_DeepWaterColor = Shader.PropertyToID("_DeepWaterColor");
        static readonly int ID_ShorelineScale = Shader.PropertyToID("_ShorelineDepthScale");
        static readonly int ID_ShorelineStrength = Shader.PropertyToID("_ShorelineStrength");
        static readonly int ID_FoamMask = Shader.PropertyToID("_FoamMask");
        static readonly int ID_FoamColor = Shader.PropertyToID("_FoamColor");
        static readonly int ID_FoamEnabled = Shader.PropertyToID("_FoamEnabled");
        static readonly int ID_FoamStrength = Shader.PropertyToID("_FoamStrength");
        static readonly int ID_FoamBorder = Shader.PropertyToID("_FoamBorderWidth");
        static readonly int ID_FoamContact = Shader.PropertyToID("_FoamContactDepth");
        static readonly int ID_WaveA = Shader.PropertyToID("_WaveA");
        static readonly int ID_WaveB = Shader.PropertyToID("_WaveB");
        static readonly int ID_WaveCount = Shader.PropertyToID("_WaveCount");
        static readonly int ID_WaveTime = Shader.PropertyToID("_WaveTime");
        static readonly int ID_WaveMeters = Shader.PropertyToID("_WaveMetersPerUnit");
        static readonly int ID_WaveNormal = Shader.PropertyToID("_WaveNormalStrength");
        static readonly int ID_VolumeCenter = Shader.PropertyToID("_VolumeCenter");
        static readonly int ID_VolumeExtent = Shader.PropertyToID("_VolumeExtent");
        static readonly int ID_VolumeRot = Shader.PropertyToID("_VolumeRot");
        static readonly int ID_GodRaySteps = Shader.PropertyToID("_GodRaySteps");
        static readonly int ID_SimWindowed = Shader.PropertyToID("_SimWindowed");
        static readonly int ID_SimCenter = Shader.PropertyToID("_SimCenter");
        static readonly int ID_SimExtent = Shader.PropertyToID("_SimExtent");
        static readonly int ID_SimEdgeFade = Shader.PropertyToID("_SimEdgeFadeTexels");

        void OnEnable()
        {
            if (simCompute == null) { Debug.LogError("WaterVolume: simCompute not assigned."); enabled = false; return; }

            // Hard capability guard: the sim needs compute shaders + a float random-write RT. On a
            // backend without them, disable this body cleanly instead of dispatching into a crash.
            // (The quality tier already scales cost; this handles the total absence of support.)
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Debug.LogWarning("WaterVolume: device lacks compute shaders or float render textures; " +
                                 "water simulation disabled on this body.", this);
                enabled = false;
                return;
            }

            ResolveSceneRefs(); // let a dropped-in prefab find the scene's camera/sun without manual wiring

            ApplyQuality();     // sets _simRes, causticResolution, _godRaysAllowed + god-ray steps

            _water = new WaterSimulation(simCompute, _simRes);
            _analyticBuoyancyFallback = !SystemInfo.supportsAsyncGPUReadback; // no readback -> analytic waterline
            _windowed = ShouldWindow(); // decided once; volumeExtent is fixed before Play

            if (obstacleShader != null)
                _obstacle = new WaterObstacle(obstacleShader, _simRes,
                                              VolumeCenter, VolumeRotation, VolumeExtentSafe);

            _causticMat = new Material(causticsShader);
            _causticRT = new RenderTexture(causticResolution, causticResolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex"
            };
            _causticRT.Create();
            _cb = new CommandBuffer { name = "WebGLWater.Caustics" };

            _heightMip = new RenderTexture(_simRes, _simRes, 0, RenderTextureFormat.RFloat)
            {
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "HeightMip"
            };
            _heightMip.Create();

            // seed the pool with a few ripples. Compensate the strength for extent.y (like
            // AddRipple) so seed splashes keep a fixed world height on a deep pool - PoolToWorld
            // multiplies surface height by extent.y.
            if (seedRipplesOnStart)
            {
                float seedStrength = SeedRippleStrength / VolumeExtentSafe.y;
                for (int i = 0; i < SeedRippleCount; i++)
                    _water.AddDrop(Random.value * 2f - 1f, Random.value * 2f - 1f, SeedRippleRadius,
                                   (i & 1) == 1 ? seedStrength : -seedStrength);
            }

            if (targetCamera != null)
            {
                targetCamera.fieldOfView = CameraFieldOfView;
                targetCamera.nearClipPlane = CameraNearClip;
                targetCamera.farClipPlane = CameraFarClip;
            }

            if (isPrimary) Primary = this;
            if (!Bodies.Contains(this)) Bodies.Add(this);
            _mpb = new MaterialPropertyBlock();
            ApplyReflections();

            RebakeBed(); // one-time terrain -> pool-space bed-height map (no-op without a Terrain)

            PublishSharedGlobals();
            EnsureWaveBank();
            _simCenter = VolumeCenter;
            if (_windowed) UpdateSimWindow();   // prime the window centre before first publish
            if (!_windowed) UpdateCaustics();   // caustics are out of scope for windowed bodies
            ApplyBodyBlock();
            if (isPrimary) PublishBodyGlobals();
        }

        void OnDisable()
        {
            if (Primary == this) Primary = null;
            Bodies.Remove(this);
            _water?.Dispose();
            _obstacle?.Dispose();
            if (_causticRT != null) _causticRT.Release();
            if (_heightMip != null) _heightMip.Release();
            DestroyBedTexture(); _bedBaked = false;
            _cb?.Release();
            if (_surfaceAboveInstance != null) { Destroy(_surfaceAboveInstance); _surfaceAboveInstance = null; }
            if (_surfaceUnderInstance != null) { Destroy(_surfaceUnderInstance); _surfaceUnderInstance = null; }
        }

        // Fill in the scene-level references a prefab can't carry, so dropping the WaterVolume
        // prefab into a fresh scene "just works". Only unset fields are touched, so an explicitly
        // wired scene (e.g. the demo builder) is left exactly as authored.
        void ResolveSceneRefs()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (sun == null) sun = ResolveSun();
            if (orbit == null && targetCamera != null) orbit = targetCamera.GetComponent<OrbitCamera>();
            if (splashEmitter == null) splashEmitter = FindFirstObjectByType<WaterSplashEmitter>();
        }

        // The scene's key light: the lighting-settings sun if set, else the first directional light.
        static Light ResolveSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i].type == LightType.Directional) return lights[i];
            return null;
        }

        // Apply the quality tier's cost knobs. Called once at startup, before the sim/caustic
        // RTs are created, so the resolutions are fixed for the session (a tier change takes
        // effect on restart). With no asset assigned the inspector defaults are left untouched
        // (_simRes stays at its default), so existing scenes are unaffected.
        void ApplyQuality()
        {
            if (quality == null) return;

            WaterQuality.Tier tier = quality.Resolve();
            _simRes = tier.SimResolution;
            causticResolution = tier.CausticResolution;
            _godRaysAllowed = tier.GodRays;
            _richReflectionsAllowed = tier.RichReflections;

            // Clamp to >= 1 so a "god rays off" tier (0 steps) never bakes a divide-by-zero
            // into the shared god-ray material; the renderer is disabled separately via _godRaysAllowed.
            if (godRayRenderer != null && godRayRenderer.sharedMaterial != null)
                godRayRenderer.sharedMaterial.SetFloat(ID_GodRaySteps, Mathf.Max(1, tier.GodRaySteps));
        }

        // Give the surface renderers per-body material instances and set their reflection
        // keywords from the tier-capped EffectiveReflectionMode, so bodies in different modes
        // don't fight over one shared material. A Planar body also binds the scene's single
        // planar reflection.
        void ApplyReflections()
        {
            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder);
            ApplyReflectionKeywords(_surfaceAboveInstance);
            ApplyReflectionKeywords(_surfaceUnderInstance);

            if (EffectiveReflectionMode == ReflectionMode.Planar) BindHeroPlanar();
        }

        // The authored reflectionMode capped by the active quality tier: SSR/Planar collapse to
        // SkyOnly when the tier disallows rich reflections (e.g. Low). Keeps the field's intent
        // intact so raising the tier restores the authored mode without re-editing the body.
        ReflectionMode EffectiveReflectionMode =>
            _richReflectionsAllowed ? reflectionMode : ReflectionMode.SkyOnly;

        // Replace the renderer's shared material with a per-body instance (play-mode only, so
        // the scene asset is untouched; the instance is destroyed in OnDisable).
        static Material InstanceSurfaceMaterial(Renderer r)
        {
            if (r == null || r.sharedMaterial == null) return null;
            var instance = new Material(r.sharedMaterial);
            r.sharedMaterial = instance;
            return instance;
        }

        void ApplyReflectionKeywords(Material m)
        {
            if (m == null) return;
            SetKeyword(m, KW_SSR, EffectiveReflectionMode == ReflectionMode.SSR);
            SetKeyword(m, KW_PLANAR, EffectiveReflectionMode == ReflectionMode.Planar);
            // Base environment is independent of the mode above and of the tier: a single cube
            // sample either way, so it is not capped on Low.
            SetKeyword(m, KW_URP_PROBE, environmentSource == EnvironmentSource.UrpProbe);
        }

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        // Point the scene's planar reflection at THIS body's plane and turn it on. The planar
        // texture is a single global plane, so only one hero body should use Planar mode; with
        // several, the last to enable at OnEnable wins.
        void BindHeroPlanar()
        {
            if (targetCamera == null) return;
            var planar = targetCamera.GetComponent<PlanarReflection>();
            if (planar == null) return;
            planar.enableReflection = true;
            planar.waterHeight = transform.position.y;
        }

        void Update()
        {
            // Input is a scene-level concern: only the primary body handles mouse/keys and
            // routes clicks to whichever body's surface the ray hits (avoids two controllers
            // fighting over one camera). TODO(Phase 2): a dedicated WaterInput component.
            if (isPrimary)
            {
                HandleKeys();
                HandleMouse();
            }

            // Decide (once per frame, for every body) which bodies draw and which run the
            // heavy GPU sim, then stop drawing this one if it is off-screen.
            EnsureSchedule();
            SetRenderersEnabled(_visible);

            float dt = Time.deltaTime;
            if (!_paused)
            {
                // The analytic wind waves are driven by the shared clock, so they keep moving
                // even on a budget-paused (but visible) body; only the GPU sim is gated.
                _waveTime += dt;
                if (_simulate) Step(dt);
            }

            PublishSharedGlobals();     // sun, sky, tiles, camera-independent shared clock
            EnsureWaveBank();
            // Caustics/god rays are out of scope for windowed bodies (the caustic pass maps the
            // whole floor from _WaterTex, which now holds only the moving window). See the
            // floor-relative scheme noted in docs/large-water-sim-window-plan.md.
            if (_simulate && !_windowed) UpdateCaustics();  // renders THIS body's caustic RT from its own sim

            ApplyBodyBlock();           // per-body uniforms -> this body's renderers (MPB)
            // Primary bridge: mirror this body's data to globals as the fallback for objects
            // without a WaterMembership (those resolve their own containing body instead).
            if (isPrimary) PublishBodyGlobals();

            if (_simulate) RequestHeightReadback();  // paused bodies keep their last height (objects still float)
        }

        // Genuinely shared across all bodies: the sun, the environment, and the wave clock.
        void PublishSharedGlobals()
        {
            if (sun != null) lightDir = -sun.transform.forward;
            Shader.SetGlobalVector(ID_Light, lightDir.normalized);
            Shader.SetGlobalColor(ID_SunColor, sun != null ? sun.color * sun.intensity : Color.white);
            Shader.SetGlobalFloat(ID_WaveTime, _waveTime);
            if (tiles != null) Shader.SetGlobalTexture(ID_Tiles, tiles);
            if (sky != null) Shader.SetGlobalTexture(ID_Sky, sky);
        }

        // Per-body uniforms pushed to THIS body's own renderers via a property block, so
        // multiple water bodies never fight over global state.
        void ApplyBodyBlock()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            WriteBodyProps(_mpb);

            ApplyBlockTo(surfaceAbove);
            ApplyBlockTo(surfaceUnder);
            ApplyBlockTo(poolRenderer);
            ApplyBlockTo(godRayRenderer);
        }

        // (1/res, 1/res, res, res) of the sim texture, so shaders can bilinear-filter it manually
        // (WebGPU won't hardware-filter the RGBAFloat sim RT). Paired with every _WaterTex bind.
        Vector4 WaterTexel => new Vector4(1f / _simRes, 1f / _simRes, _simRes, _simRes);

        /// <summary>Overwrite <paramref name="mpb"/> with this body's per-renderer uniforms
        /// (sim + caustic textures, volume frame, waves, fog, foam). Used for this body's own
        /// renderers and by <see cref="WaterMembership"/> to light a floating object with the
        /// lake it is in. The block is cleared, so any per-object look must live in the material.</summary>
        public void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            mpb.Clear();
            _mpbSink.Target = mpb;
            WriteBodyUniforms(_mpbSink);
        }

        // Single source of truth for this body's per-frame uniform derivations. Written either
        // into a property block (WriteBodyProps) or to shader globals (PublishBodyGlobals) via
        // the sink, so the values are derived once. Texture guards match both former paths.
        void WriteBodyUniforms(IUniformSink sink)
        {
            if (_water != null)
            {
                sink.SetTexture(ID_Water, _water.Texture);
                sink.SetVector(ID_WaterTexel, WaterTexel);
                if (_water.FoamTexture != null) sink.SetTexture(ID_FoamMask, _water.FoamTexture);
            }
            if (_causticRT != null) sink.SetTexture(ID_Caustic, _causticRT);

            sink.SetVector(ID_VolumeCenter, VolumeCenter);
            sink.SetVector(ID_VolumeExtent, VolumeExtentSafe);
            sink.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(VolumeRotation));

            sink.SetFloat(ID_SimWindowed, _windowed ? 1f : 0f);
            sink.SetVector(ID_SimCenter, _simCenter);
            sink.SetVector(ID_SimExtent, SimHalfExtent);
            sink.SetFloat(ID_SimEdgeFade, simWindowEdgeFadeTexels);

            sink.SetVectorArray(ID_WaveA, _waveBank.PackedA);
            sink.SetVectorArray(ID_WaveB, _waveBank.PackedB);
            sink.SetFloat(ID_WaveCount, windWaves ? _waveBank.Count : 0f);
            sink.SetFloat(ID_WaveMeters, WaveMetersPerUnit);
            sink.SetFloat(ID_WaveNormal, waveNormalStrength);

            sink.SetColor(ID_FogColor, fogColor);
            sink.SetColor(ID_FogExt, fogExtinction);
            sink.SetFloat(ID_FogDensity, fogDensity);
            sink.SetFloat(ID_FogEnabled, waterFog ? 1f : 0f);
            sink.SetFloat(ID_WaterOpacity, waterOpacity);

            sink.SetColor(ID_DepthExt, EffectiveDepthExtinction);
            sink.SetFloat(ID_DepthStrength, depthDarkenStrength);
            sink.SetFloat(ID_DepthEnabled, depthDarken ? 1f : 0f);
            sink.SetFloat(ID_CausticDepthFade, causticDepthFade);
            sink.SetFloat(ID_GodRayDepthFade, godRayDepthFade);

            if (_bedTex != null) sink.SetTexture(ID_BedTex, _bedTex);
            sink.SetFloat(ID_BedValid, _bedBaked ? 1f : 0f);
            sink.SetFloat(ID_UseBedDepth, useBedDepth ? 1f : 0f);
            sink.SetColor(ID_DeepWaterColor, deepWaterColor);
            sink.SetFloat(ID_ShorelineScale, 1f / Mathf.Max(0.01f, shorelineFadeDepth));
            sink.SetFloat(ID_ShorelineStrength, shorelineStrength);

            sink.SetColor(ID_FoamColor, foamColor);
            sink.SetFloat(ID_FoamEnabled, foam ? 1f : 0f);
            sink.SetFloat(ID_FoamStrength, foamStrength);
            sink.SetFloat(ID_FoamBorder, foamBorderWidth);
            sink.SetFloat(ID_FoamContact, foamContactDepth);
        }

        // A write target for the per-body uniforms: either a MaterialPropertyBlock or the
        // global shader state. Only the id-keyed setters WriteBodyUniforms needs are exposed.
        interface IUniformSink
        {
            void SetFloat(int id, float value);
            void SetColor(int id, Color value);
            void SetVector(int id, Vector4 value);
            void SetMatrix(int id, Matrix4x4 value);
            void SetVectorArray(int id, Vector4[] value);
            void SetTexture(int id, Texture value);
        }

        sealed class MpbUniformSink : IUniformSink
        {
            public MaterialPropertyBlock Target;
            public void SetFloat(int id, float value) => Target.SetFloat(id, value);
            public void SetColor(int id, Color value) => Target.SetColor(id, value);
            public void SetVector(int id, Vector4 value) => Target.SetVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => Target.SetMatrix(id, value);
            public void SetVectorArray(int id, Vector4[] value) => Target.SetVectorArray(id, value);
            public void SetTexture(int id, Texture value) => Target.SetTexture(id, value);
        }

        sealed class GlobalUniformSink : IUniformSink
        {
            public void SetFloat(int id, float value) => Shader.SetGlobalFloat(id, value);
            public void SetColor(int id, Color value) => Shader.SetGlobalColor(id, value);
            public void SetVector(int id, Vector4 value) => Shader.SetGlobalVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => Shader.SetGlobalMatrix(id, value);
            public void SetVectorArray(int id, Vector4[] value) => Shader.SetGlobalVectorArray(id, value);
            public void SetTexture(int id, Texture value) => Shader.SetGlobalTexture(id, value);
        }

        void ApplyBlockTo(Renderer r) { if (r != null) r.SetPropertyBlock(_mpb); }

        // ---- sim culling schedule (Phase 3) ---------------------------------
        // Sets _visible / _simulate for EVERY live body, once per frame. Frame-guarded so
        // whichever body Updates first does the work and the rest reuse it (order-independent).
        static void EnsureSchedule()
        {
            if (_scheduleFrame == Time.frameCount) return;
            _scheduleFrame = Time.frameCount;

            Camera cam = ScheduleCamera();
            if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

            // Pass 1: visibility, plus a provisional "simulate" for visible + in-range bodies.
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (!body.enableCulling || cam == null)
                {
                    body._visible = true;
                    body._simulate = true; // culling off -> always draw + simulate
                    continue;
                }
                body._visible = GeometryUtility.TestPlanesAABB(_frustumPlanes, body.CullBounds());
                body._simulate = IsSimEligible(body, cam.transform.position);
            }

            // Pass 2: cap the simulating set to the nearest ActiveSimBudget eligible bodies.
            EnforceSimBudget(cam);
        }

        // Eligible to simulate = culling on, visible, and within the activation distance.
        // Recomputed (not read from _simulate) so the budget pass can rank without its own
        // writes skewing the counts.
        static bool IsSimEligible(WaterVolume body, Vector3 camPos)
        {
            if (!body.enableCulling || !body._visible) return false;
            float distSqr = (body.VolumeCenter - camPos).sqrMagnitude;
            return distSqr <= body.activationDistance * body.activationDistance;
        }

        // Keep only the nearest ActiveSimBudget eligible bodies simulating; pause the rest.
        // The body count is small, so an O(n^2) "how many eligible bodies are nearer than me"
        // rank avoids allocating a sorted list each frame.
        static void EnforceSimBudget(Camera cam)
        {
            if (cam == null) return;
            Vector3 camPos = cam.transform.position;

            int eligible = 0;
            for (int i = 0; i < Bodies.Count; i++)
                if (IsSimEligible(Bodies[i], camPos)) eligible++;
            if (eligible <= ActiveSimBudget) return;

            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (!IsSimEligible(body, camPos)) continue;
                float d = (body.VolumeCenter - camPos).sqrMagnitude;

                int nearer = 0;
                for (int j = 0; j < Bodies.Count; j++)
                {
                    WaterVolume other = Bodies[j];
                    if (other == body || !IsSimEligible(other, camPos)) continue;
                    float od = (other.VolumeCenter - camPos).sqrMagnitude;
                    if (od < d || (od == d && j < i)) nearer++; // stable tiebreak by registry index
                }
                if (nearer >= ActiveSimBudget) body._simulate = false;
            }
        }

        // Prefer the primary's camera; fall back to any body's camera, then the main camera.
        static Camera ScheduleCamera()
        {
            if (Primary != null && Primary.targetCamera != null) return Primary.targetCamera;
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i].targetCamera != null) return Bodies[i].targetCamera;
            return Camera.main;
        }

        // World-space AABB of this body's volume (pool box x,z in [-1,1], y in [-1,0]) plus a
        // little headroom for wind-wave crests. The renderers keep huge bounds to avoid wrong
        // culling under the volume transform, so frustum culling tests this real box instead.
        Bounds CullBounds()
        {
            Bounds b = new Bounds(PoolToWorld(new Vector3(-1f, -1f, -1f)), Vector3.zero);
            b.Encapsulate(PoolToWorld(new Vector3( 1f, -1f, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, -1f,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, -1f,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, WaveHeightMargin, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, WaveHeightMargin, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, WaveHeightMargin,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, WaveHeightMargin,  1f)));
            return b;
        }

        void SetRenderersEnabled(bool on)
        {
            SetRendererEnabled(surfaceAbove, on);
            SetRendererEnabled(surfaceUnder, on);
            SetRendererEnabled(poolRenderer, on);
            // God rays obey the quality tier as well as culling: a tier that disables them
            // keeps the renderer off even when the body is on-screen. Windowed bodies also
            // suppress god rays (out of scope, same reason as caustics).
            SetRendererEnabled(godRayRenderer, on && _godRaysAllowed && !_windowed);
        }

        static void SetRendererEnabled(Renderer r, bool on) { if (r != null && r.enabled != on) r.enabled = on; }

        // Mirror this (primary) body's per-body data to global shader state, so objects and
        // the analytic receivers - which still read globals in Phase 1 - follow this body.
        // The primary body mirrors its per-body uniforms to shader globals, the fallback that
        // object shaders without a WaterMembership read. Same derivations as the property block.
        void PublishBodyGlobals() => WriteBodyUniforms(_globalSink);

        // ---- height readback for buoyancy ----------------------------------
        void RequestHeightReadback()
        {
            if (_readbackInFlight || _water == null) return;
            if (!SystemInfo.supportsAsyncGPUReadback) return; // no readback; TrySamplePoolSurface uses the analytic fallback
            _readbackInFlight = true;
            AsyncGPUReadback.Request(_water.Texture, 0, TextureFormat.RGBAFloat, OnHeightReadback);
        }

        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            _readbackInFlight = false;
            if (req.hasError) return;
            var data = req.GetData<Color>();
            if (_heightCpu == null || _heightCpu.Length != data.Length)
                _heightCpu = new Color[data.Length];
            data.CopyTo(_heightCpu);
            _heightReady = true;
        }

        /// <summary>Inject a ripple at a WORLD position (x,z). Converted into the pool
        /// footprint via the volume frame; out-of-footprint calls are ignored. Radius is
        /// in world units (kept round via the average horizontal extent).</summary>
        public void AddRipple(float worldX, float worldZ, float radius, float strength)
        {
            if (_water == null) return;

            // Windowed bodies inject into the sim WINDOW frame; ripples outside it are dropped.
            if (_windowed)
            {
                Vector3 sim = WorldToSim(new Vector3(worldX, _simCenter.y, worldZ));
                if (sim.x < -1f || sim.x > 1f || sim.z < -1f || sim.z > 1f) return;
                _water.AddDrop(sim.x, sim.z, radius / SimHorizontalExtent, strength / VolumeExtentSafe.y);
                return;
            }

            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return;
            _water.AddDrop(px, pz, radius / VolumeHorizontalExtent, strength / VolumeExtentSafe.y);
        }

        /// <summary>World-space height (Y) of the water surface above WORLD (x,z).
        /// Returns false until the first readback lands or if outside the footprint.</summary>
        public bool TryGetWaterHeight(float worldX, float worldZ, out float height)
        {
            height = 0f;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!TrySamplePoolSurface(probe, px, pz, out float poolHeight, out _)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y; // pool -> world Y
            return true;
        }

        /// <summary>World surface height (Y) plus the horizontal surface-flow (world x,z)
        /// above WORLD (x,z). For surface effects that ride the waterline (splash drift).
        /// Approximate under steep tilt; exact for rotation/rectangular/depth.</summary>
        public bool TryGetSurface(float worldX, float worldZ, out float height, out Vector2 flow)
        {
            height = 0f;
            flow = Vector2.zero;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!TrySamplePoolSurface(probe, px, pz, out float poolHeight, out Vector2 poolFlow)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            Vector3 worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            flow = new Vector2(worldFlow.x, worldFlow.z);
            return true;
        }

        /// <summary>Sample submersion for a buoyancy point at an arbitrary WORLD point.
        /// Works under rotation/tilt/non-uniform extent because it is evaluated in pool
        /// space. Returns the world-space depth below the surface (negative = above),
        /// the volume's up direction, and the world-space surface-flow push.</summary>
        public bool TrySampleSubmersion(Vector3 worldPoint, out float depthWorld, out Vector3 up, out Vector3 worldFlow)
        {
            depthWorld = 0f;
            up = VolumeUp;
            worldFlow = Vector3.zero;

            Vector3 pool = WorldToPool(worldPoint);
            if (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f) return false;
            if (!TrySamplePoolSurface(worldPoint, pool.x, pool.z, out float surfaceH, out Vector2 poolFlow)) return false;

            depthWorld = (surfaceH - pool.y) * VolumeExtentSafe.y; // pool depth -> world depth along up
            worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            return true;
        }

        // ---- gameplay façade -----------------------------------------------
        // World-position-first wrappers over the sim primitives, so gameplay code (swimming,
        // audio, VFX, projectiles) queries the water without touching x/z or internals. The
        // static *At variants resolve the body that contains the point via BodyContaining.

        /// <summary>World-space surface height (Y) at a world position's x,z on THIS body.
        /// False until the first readback lands or if the point is outside the footprint.</summary>
        public bool TrySampleHeight(Vector3 worldPos, out float worldY)
            => TryGetWaterHeight(worldPos.x, worldPos.z, out worldY);

        /// <summary>True if the world point is below THIS body's surface.</summary>
        public bool IsSubmerged(Vector3 worldPos)
            => TrySampleSubmersion(worldPos, out float depth, out _, out _) && depth > 0f;

        /// <summary>Inject a ripple at a world position on THIS body (footsteps, projectiles,
        /// boats). Radius/strength are world units; out-of-footprint calls are ignored.</summary>
        public void SpawnRipple(Vector3 worldPos, float radius, float strength)
            => AddRipple(worldPos.x, worldPos.z, radius, strength);

        /// <summary>Surface height (Y) at a world position, resolving the body that contains it.
        /// False if there is no water or the readback isn't ready / point is out of footprint.</summary>
        public static bool TrySampleHeightAt(Vector3 worldPos, out float worldY)
        {
            worldY = 0f;
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.TrySampleHeight(worldPos, out worldY);
        }

        /// <summary>True if the world point is below the surface of whichever body contains it.</summary>
        public static bool IsSubmergedAt(Vector3 worldPos)
        {
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.IsSubmerged(worldPos);
        }

        /// <summary>Spawn a ripple at a world position on whichever body contains it. Returns
        /// false if there is no water body to receive it.</summary>
        public static bool TrySpawnRippleAt(Vector3 worldPos, float radius, float strength)
        {
            WaterVolume body = BodyContaining(worldPos);
            if (body == null) return false;
            body.SpawnRipple(worldPos, radius, strength);
            return true;
        }

        /// <summary>Waterline for the obstacle footprint: the ANALYTIC surface only (rest
        /// plane + wind waves), deliberately EXCLUDING the interactive ripples. Including
        /// them fed an object's own displacement back into its footprint through the stale
        /// async readback - a delayed feedback loop that kept re-exciting micro-ripples
        /// around every floater. Wind waves stay in, so a wave-riding float keeps a constant
        /// submerged depth against its waterline and injects nothing; scattering off passing
        /// ripples becomes a small, damped, open-loop effect (like the mouse, which injects
        /// without ever being influenced by the water). No readback needed: valid from frame 0.</summary>
        public bool TryGetAnalyticWaterline(float worldX, float worldZ, out float height)
        {
            height = 0f;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;

            float poolHeight = windWaves ? _waveBank.SampleHeight(px, pz, _waveTime, WaveMetersPerUnit) : 0f;
            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            return true;
        }

        // Pool-space surface height + flow (normal.xz) at a world point (pool xz in [-1,1]).
        // Uses the GPU readback ripple field when available; on backends without AsyncGPUReadback
        // it falls back to the analytic surface (flat rest + wind waves) so buoyancy and surface
        // queries keep working (interactive ripples / obstacle displacement are simply absent there).
        // Returns false only when readback is supported but hasn't landed yet (first frames).
        bool TrySamplePoolSurface(Vector3 world, float poolX, float poolZ, out float surfaceH, out Vector2 poolFlow)
        {
            surfaceH = 0f;
            poolFlow = Vector2.zero;

            bool haveReadback = _heightReady && _heightCpu != null;
            if (haveReadback)
            {
                Color sample = SampleRipple(world, poolX, poolZ);
                surfaceH = sample.r;
                poolFlow = new Vector2(sample.b, sample.a); // (normal.x, normal.z)
            }
            else if (!_analyticBuoyancyFallback)
            {
                return false; // readback supported but not ready yet
            }
            // else: analytic fallback -> rest surface (0) + wind waves added below

            if (windWaves)
            {
                surfaceH += _waveBank.SampleHeight(poolX, poolZ, _waveTime, WaveMetersPerUnit);
                poolFlow -= _waveBank.SampleSlope(poolX, poolZ, _waveTime, WaveMetersPerUnit) * waveNormalStrength;
            }
            return true;
        }

        // Interactive ripple sample (r = height, b/a = normal.xz) at a world point. Windowed
        // bodies read the camera-following window by world position (rest outside it); whole-body
        // bodies read the fixed grid at pool UV. Mirrors the shader's SampleRipple.
        // BILINEAR across the four surrounding texels: the old nearest-texel read made every
        // CPU consumer (buoyancy, splash drift, waterline queries) jump in a step whenever a
        // mover crossed a texel boundary - one visible micro-pulse per crossing.
        Color SampleRipple(Vector3 world, float poolX, float poolZ)
        {
            float u, v;
            if (_windowed)
            {
                Vector3 sim = WorldToSim(new Vector3(world.x, _simCenter.y, world.z));
                if (sim.x < -1f || sim.x > 1f || sim.z < -1f || sim.z > 1f)
                    return new Color(0f, 0f, 0f, 0f); // outside the window: flat rest
                u = sim.x * 0.5f + 0.5f; v = sim.z * 0.5f + 0.5f;
            }
            else
            {
                u = poolX * 0.5f + 0.5f; v = poolZ * 0.5f + 0.5f;
            }

            float sx = Mathf.Clamp(u * _simRes - 0.5f, 0f, _simRes - 1f);
            float sz = Mathf.Clamp(v * _simRes - 0.5f, 0f, _simRes - 1f);
            int x0 = (int)sx, z0 = (int)sz;
            int x1 = Mathf.Min(x0 + 1, _simRes - 1);
            int z1 = Mathf.Min(z0 + 1, _simRes - 1);
            float tx = sx - x0, tz = sz - z0;

            Color bottom = Color.Lerp(_heightCpu[z0 * _simRes + x0], _heightCpu[z0 * _simRes + x1], tx);
            Color top    = Color.Lerp(_heightCpu[z1 * _simRes + x0], _heightCpu[z1 * _simRes + x1], tx);
            return Color.Lerp(bottom, top, tz);
        }

        void Step(float seconds)
        {
            if (seconds > 1f) return;

            // Scroll the sim window to track the camera before injecting/stepping, so ripples
            // stay world-anchored. No-op for whole-body bodies.
            if (_windowed) UpdateSimWindow();

            // FootprintDelta mode only: push the surface with the temporally-smoothed
            // submerged footprint. In MouseLikeDrops mode the WaterInteractables emit
            // analytic drops themselves (via AddRipple) and this pass is skipped entirely.
            if (_obstacle != null && objectInteraction == ObjectInteraction.FootprintDelta)
            {
                // Windowed bodies re-frame the footprint onto the scrolling window each frame.
                if (_windowed) _obstacle.SetFrame(_simCenter, VolumeRotation, SimHalfExtent);
                _obstacle.Render(VolumeCenter.y, 1f - obstacleSmoothing);
                // Compensate for extent.y so an object's displacement is a fixed world height
                // regardless of pool depth (PoolToWorld scales surface height by extent.y).
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr,
                                     obstacleStrength / VolumeExtentSafe.y, obstacleFlipY);
            }

            int steps = Mathf.Max(1, stepsPerFrame);
            for (int i = 0; i < steps; i++)
                _water.StepSimulation(waveSpeed, damping);

            if (conserveVolume)
            {
                Graphics.Blit(_water.Texture, _heightMip); // copy height (R) into the mipped RT
                _heightMip.GenerateMips();                 // top 1x1 mip = mean height
                _water.ConserveVolume(_heightMip);         // subtract the mean
            }

            _water.UpdateNormals();

            if (foam)
                _water.StepFoam(foamGenRate, foamDecay, foamDecayResidual,
                                foamSpread, foamFromSpeed, foamFromCurvature, foamAdvect);
        }

        void UpdateCaustics()
        {
            // Feed THIS body's sim to its own caustic material so caustics don't come from
            // whatever body last wrote the _WaterTex global.
            if (_water != null) _causticMat.SetTexture(ID_Water, _water.Texture);

            _cb.Clear();
            _cb.SetRenderTarget(_causticRT);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _causticMat, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
            // The caustic RT reaches this body's renderers via the MPB; the primary also
            // mirrors it to the _CausticTex global (PublishBodyGlobals) for objects.
        }

        // ---- volume placement frame (center + rotation + non-uniform extent) ----
        Vector3 VolumeExtentSafe => new Vector3(
            Mathf.Max(volumeExtent.x, 1e-5f),
            Mathf.Max(volumeExtent.y, 1e-5f),
            Mathf.Max(volumeExtent.z, 1e-5f));
        // Position + rotation come from this GameObject's transform (move it to place water).
        Vector3 VolumeCenter => transform.position;
        Quaternion VolumeRotation => transform.rotation;
        Vector3 VolumeUp => VolumeRotation * Vector3.up;
        // Average horizontal extent, used to keep a click ripple round in world units.
        float VolumeHorizontalExtent => 0.5f * (VolumeExtentSafe.x + VolumeExtentSafe.z);

        Vector3 PoolToWorld(Vector3 pool) => VolumeCenter + VolumeRotation * Vector3.Scale(pool, VolumeExtentSafe);

        Vector3 WorldToPool(Vector3 world)
        {
            Vector3 e = VolumeExtentSafe;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (world - VolumeCenter);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // ---- large-water sim window frame ----------------------------------
        // Half-size (world) of the window: simWindowMeters horizontally, the body's depth
        // scale vertically (ripple height stays coupled to extent.y like the whole-body sim).
        Vector3 SimHalfExtent => new Vector3(
            Mathf.Max(simWindowMeters, 1e-3f),
            VolumeExtentSafe.y,
            Mathf.Max(simWindowMeters, 1e-3f));

        // Average horizontal window half-size, keeping an injected ripple round in world units.
        float SimHorizontalExtent => Mathf.Max(simWindowMeters, 1e-3f);

        // ---- GPU consumer API (foam particles and similar per-body effects) ----

        /// <summary>Sim state texture (height, velocity, normal.xz) for GPU consumers.</summary>
        public RenderTexture SimStateTexture => _water?.Texture;
        /// <summary>Current foam-amount texture (R channel) for GPU consumers.</summary>
        public RenderTexture FoamMaskTexture => _water?.FoamTexture;
        /// <summary>Grid resolution of the active sim (per side), fixed at startup.</summary>
        public int SimResolution => _simRes;
        /// <summary>True when this body runs its GPU sim this frame (visible, in range,
        /// within the sim budget, not paused). GPU consumers should idle when false.</summary>
        public bool IsSimulating => _simulate && !_paused;
        /// <summary>True when this body's renderers draw this frame (frustum cull).</summary>
        public bool IsVisibleToCamera => _visible;

        /// <summary>Push this body's placement-frame uniforms (volume + sim window) onto a
        /// compute shader so GPU consumers can include WaterVolume.hlsl and share the exact
        /// same pool/window/world transforms as the render side.</summary>
        public void WriteSimFrameUniforms(ComputeShader cs)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            cs.SetVector(ID_VolumeCenter, VolumeCenter);
            cs.SetVector(ID_VolumeExtent, VolumeExtentSafe);
            cs.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(VolumeRotation));
            cs.SetFloat(ID_SimWindowed, _windowed ? 1f : 0f);
            cs.SetVector(ID_SimCenter, _simCenter);
            cs.SetVector(ID_SimExtent, SimHalfExtent);
        }

        /// <summary>World-space area covered by one sim texel (m^2), for density-normalised
        /// GPU spawning. Uses the window frame when windowed, else the whole volume.</summary>
        public float SimTexelWorldArea
        {
            get
            {
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                float texelX = 2f * half.x / _simRes;
                float texelZ = 2f * half.z / _simRes;
                return texelX * texelZ;
            }
        }

        /// <summary>Loose world bounds of the active sim frame (surface plane plus wave
        /// headroom), for culling GPU-driven draws that follow this body.</summary>
        public Bounds SimWorldBounds
        {
            get
            {
                Vector3 center = _windowed ? _simCenter : VolumeCenter;
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                // Rotation-safe: expand horizontally by the diagonal, vertically by the
                // depth plus wave headroom.
                float horizontal = Mathf.Sqrt(half.x * half.x + half.z * half.z);
                float vertical = half.y * (1f + WaveHeightMargin);
                return new Bounds(center, 2f * new Vector3(horizontal, vertical, horizontal));
            }
        }

        /// <summary>True if this body runs the camera-following windowed sim (decided at
        /// startup from its size and the threshold).</summary>
        public bool IsWindowed => _windowed;
        /// <summary>World centre of the active sim window (follows the camera at runtime).</summary>
        public Vector3 SimWindowCenter => _simCenter;
        /// <summary>World half-size (x,z) and depth scale (y) of the sim window.</summary>
        public Vector3 SimWindowHalfExtent => SimHalfExtent;

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window). Shares the
        // volume rotation; centred on the scrolling window centre.
        Vector3 WorldToSim(Vector3 world)
        {
            Vector3 e = SimHalfExtent;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (world - _simCenter);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // Windowing turns on for bodies whose horizontal half-extent exceeds the threshold.
        bool ShouldWindow()
        {
            if (!enableLargeBodyWindow) return false;
            Vector3 e = VolumeExtentSafe;
            return Mathf.Max(e.x, e.z) > largeBodyThreshold;
        }

        // Move the window to the camera: project the camera onto the surface plane, clamp into
        // the footprint, snap to the sim-texel lattice, and scroll the sim state by the integer
        // texel delta so ripples stay pinned in world space. Called once per simulated frame.
        void UpdateSimWindow()
        {
            if (targetCamera == null || _water == null) return;

            // Camera projected onto the surface plane (through the volume centre, along up).
            Vector3 up = VolumeUp;
            Vector3 camPos = targetCamera.transform.position;
            Vector3 onPlane = camPos - Vector3.Dot(camPos - VolumeCenter, up) * up;

            // Work in the volume's local horizontal frame so the lattice is axis-aligned.
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (onPlane - VolumeCenter);

            float texel = 2f * simWindowMeters / _simRes;
            // Clamp the window centre so it stays inside the footprint (or may overhang the edge).
            Vector3 e = VolumeExtentSafe;
            float limitX = clampWindowToShore ? Mathf.Max(0f, e.x - simWindowMeters) : e.x;
            float limitZ = clampWindowToShore ? Mathf.Max(0f, e.z - simWindowMeters) : e.z;
            float clampedX = Mathf.Clamp(local.x, -limitX, limitX);
            float clampedZ = Mathf.Clamp(local.z, -limitZ, limitZ);

            int cellX = Mathf.RoundToInt(clampedX / texel);
            int cellZ = Mathf.RoundToInt(clampedZ / texel);

            if (!_simCenterInit)
            {
                _simCellX = cellX; _simCellZ = cellZ;
                _simCenterInit = true;
            }
            else
            {
                int dx = cellX - _simCellX;
                int dz = cellZ - _simCellZ;
                if (dx != 0 || dz != 0)
                {
                    // Local x -> sim texel u, local z -> sim texel v. The kernel does
                    // Dst[p] = Src[p - offset]; offsetting by -delta keeps world features fixed
                    // (see WaterSimulation.Scroll).
                    _water.Scroll(-dx, -dz);
                    _simCellX = cellX; _simCellZ = cellZ;
                }
            }

            _simCenter = VolumeCenter + VolumeRotation * new Vector3(_simCellX * texel, 0f, _simCellZ * texel);
        }

        // World point -> pool. Returns false if outside the [-1,1] horizontal footprint.
        bool WorldToPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f;
        }

        // Intersect a camera ray with the (possibly tilted) surface plane through the
        // volume centre. Returns the world hit and its pool x,z (which may fall outside
        // [-1,1]); false only if the ray is parallel to or points away from the plane.
        bool TryPickSurface(Vector3 eye, Vector3 dir, out Vector3 worldHit, out float poolX, out float poolZ)
        {
            worldHit = Vector3.zero; poolX = 0f; poolZ = 0f;
            Vector3 n = VolumeUp;
            float denom = Vector3.Dot(dir, n);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float t = Vector3.Dot(VolumeCenter - eye, n) / denom;
            if (t < 0f) return false;
            worldHit = eye + dir * t;
            Vector3 pool = WorldToPool(worldHit);
            poolX = pool.x; poolZ = pool.z;
            return true;
        }

        // ---- wind-wave layer -----------------------------------------------
        float WaveMetersPerUnit => Mathf.Max(1e-3f, poolHalfExtentMeters);

        // Regenerate the bank only when a wind/scale parameter actually changes, so
        // the phases stay stable frame-to-frame (a fresh bank would pop the surface).
        void EnsureWaveBank()
        {
            var signature = new Vector4(windSpeed, windFromDegrees, poolHalfExtentMeters,
                                        waveCount + 100f * waveAmplitudeScale);
            float verticalExtent = VolumeExtentSafe.y;
            bool dirty = windWaves != _waveGenEnabled
                         || signature != _waveGenSignature
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            _waveBank.Generate(windSpeed, windFromDegrees, 2f * poolHalfExtentMeters,
                               waveCount, waveAmplitudeScale, waveDirectionSpread, WaveMetersPerUnit,
                               verticalExtent);
            _waveGenSignature = signature;
            _waveGenSpread = waveDirectionSpread;
            _waveGenVerticalExtent = verticalExtent;
            _waveGenEnabled = windWaves;
        }

        // Wave arrays are per-body, mirrored to globals only by the primary (see WriteBodyUniforms).
        // The wave CLOCK (_WaveTime) is genuinely shared and published in PublishSharedGlobals.

        // With the link on, the depth colour tracks the fog extinction so a single dial drives
        // both; off, the depth colour is authored independently.
        Color EffectiveDepthExtinction => linkDepthToFog ? fogExtinction : depthExtinction;

        // ---- terrain bed-height bake ----------------------------------------
        // Sample the terrain heightmap into a pool-space map so shaders can read the real
        // water-column depth (surface - bed). One-time CPU bake aligned to THIS body's volume
        // frame; re-run via the context menu / RebakeBed() if the terrain or placement changes.
        [ContextMenu("Rebake Bed")]
        public void RebakeBed()
        {
            Terrain terrain = bedTerrain != null ? bedTerrain : Terrain.activeTerrain;
            if (terrain == null) { _bedBaked = false; return; }

            int res = Mathf.Clamp(bedResolution, MinBedResolution, MaxBedResolution);
            EnsureBedTexture(res);

            float terrainBaseY = terrain.transform.position.y;
            var pixels = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                float poolZ = ((z + 0.5f) / res) * 2f - 1f;
                for (int x = 0; x < res; x++)
                {
                    float poolX = ((x + 0.5f) / res) * 2f - 1f;
                    Vector3 world = PoolToWorld(new Vector3(poolX, 0f, poolZ));
                    float bedWorldY = terrainBaseY + terrain.SampleHeight(world);
                    // Only the Y differs from the surface probe, so this yields the bed's pool-space
                    // height under the same volume frame (correct under rotation / non-uniform extent).
                    float bedPoolY = WorldToPool(new Vector3(world.x, bedWorldY, world.z)).y;
                    pixels[z * res + x] = new Color(bedPoolY, 0f, 0f, 0f);
                }
            }
            _bedTex.SetPixels(pixels);
            _bedTex.Apply(false, false);
            _bedBaked = true;
        }

        void EnsureBedTexture(int res)
        {
            if (_bedTex != null && _bedTex.width == res) return;
            if (_bedTex != null) DestroyBedTexture();
            _bedTex = new Texture2D(res, res, TextureFormat.RFloat, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "BedHeightPool"
            };
        }

        // Destroy the bed texture safely from either play mode or the editor context menu.
        void DestroyBedTexture()
        {
            if (_bedTex == null) return;
            if (Application.isPlaying) Destroy(_bedTex); else DestroyImmediate(_bedTex);
            _bedTex = null;
        }

        // ---- camera ---------------------------------------------------------
        Ray PixelRay(Vector2 p)
        {
            return targetCamera.ScreenPointToRay(new Vector3(p.x, p.y, 0f));
        }

        // ---- interaction (primary body acts as the scene input router) -------

        /// <summary>Does this body's surface plane lie under the ray, within its footprint?
        /// Returns the world hit point. Lets the input router pick which lake was clicked.</summary>
        public bool TryRaycastSurface(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            if (!TryPickSurface(ray.origin, ray.direction, out Vector3 hit, out float px, out float pz)) return false;
            if (Mathf.Abs(px) > 1f || Mathf.Abs(pz) > 1f) return false;
            worldHit = hit;
            return true;
        }

        // Nearest water body whose surface the ray hits (null = none, so we orbit instead).
        static WaterVolume FindHitBody(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            WaterVolume best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < Bodies.Count; i++)
            {
                if (!Bodies[i].TryRaycastSurface(ray, out Vector3 hit)) continue;
                float sqr = (hit - ray.origin).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = Bodies[i]; worldHit = hit; }
            }
            return best;
        }

        void HandleMouse()
        {
            // No camera -> no rays to cast; skip input rather than NRE in PixelRay.
            if (targetCamera == null) return;

            // While pinching (2+ fingers), don't ripple/orbit — let the camera zoom.
            if (MultiTouch()) { _mode = MODE_NONE; return; }

            Vector2 m = MousePos();

            if (MouseDown())
            {
                _oldMouse = m;
                _dragBody = FindHitBody(PixelRay(m), out Vector3 hit);
                if (_dragBody != null)
                {
                    _mode = MODE_ADD_DROPS;
                    _prevWorld = hit;
                    _forceDrop = true; // the initial press always injects one ripple
                    DuringDrag(m);
                }
                else
                {
                    _mode = MODE_ORBIT; // clicked empty space -> orbit the camera
                }
            }
            else if (MouseHeld())
            {
                DuringDrag(m);
            }
            else if (MouseUp())
            {
                _mode = MODE_NONE;
                _dragBody = null;
            }
        }

        void DuringDrag(Vector2 m)
        {
            switch (_mode)
            {
                case MODE_ADD_DROPS:
                {
                    if (_dragBody == null) break;
                    if (!_dragBody.TryRaycastSurface(PixelRay(m), out Vector3 hit)) break;

                    // Throttle injection by world distance travelled so holding the cursor
                    // still doesn't pump energy into the same texels every frame.
                    float moved = Vector2.Distance(new Vector2(hit.x, hit.z), new Vector2(_prevWorld.x, _prevWorld.z));
                    if (!_forceDrop && moved < MinDragWorldSpacing) break;
                    _forceDrop = false;

                    // Route the ripple to the clicked body (world-space API; it converts).
                    _dragBody.AddRipple(hit.x, hit.z, rippleRadius, rippleStrength);

                    if (splashEmitter != null)
                    {
                        float strength = Mathf.Clamp01(moved / 0.08f);
                        if (strength > 0.1f)
                            splashEmitter.EmitSplash(hit, strength * 0.6f, rippleRadius * 4f);
                    }
                    _prevWorld = hit;
                    break;
                }
                case MODE_ORBIT:
                {
                    if (orbit != null) orbit.Rotate(m.x - _oldMouse.x, m.y - _oldMouse.y);
                    break;
                }
            }
            _oldMouse = m;
        }

        void HandleKeys()
        {
            if (KeySpaceDown()) _paused = !_paused;
            if (KeyLHeld() && targetCamera != null)
            {
                // Point the real sun along the camera view (or the fallback vector).
                if (sun != null)
                    sun.transform.rotation = Quaternion.LookRotation(targetCamera.transform.forward);
                else
                    lightDir = -targetCamera.transform.forward;
            }
        }

        // ---- input abstraction (mouse, touch or pen via Pointer; legacy fallback) ---
        // Pointer.current resolves to the mouse on desktop and the touchscreen on
        // mobile, so the same drag logic drives both.
        static Vector2 MousePos()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null ? Pointer.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
        static bool MouseDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }
        static bool MouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }
        static bool MouseUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(0);
#endif
        }

        // True while two or more fingers are down, so single-touch ripple/orbit
        // yields to the camera's pinch-zoom.
        static bool MultiTouch()
        {
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts == null) return false;
            int n = 0;
            foreach (var t in ts.touches)
                if (t.press.isPressed) n++;
            return n >= 2;
#else
            return Input.touchCount >= 2;
#endif
        }
        static bool KeySpaceDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }
        static bool KeyLHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.lKey.isPressed;
#else
            return Input.GetKey(KeyCode.L);
#endif
        }
    }
}
