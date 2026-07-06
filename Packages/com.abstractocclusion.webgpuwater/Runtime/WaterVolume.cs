// WebGL Water - one water body: identity, lifecycle and public facade (Unity 6 / URP port).
// Port of main.js / renderer.js by Evan Wallace (MIT).
//
// WaterVolume is the single scene component; each responsibility lives in a collaborator
// it owns and orchestrates from Update:
//   WaterSimulation      - GPU heightfield sim (ping-pong RTs, compute dispatch)
//   WaterObstacle        - rasterized submerged-footprint pass (FootprintDelta mode)
//   WaterCausticsPass    - per-body caustic material/RT/command buffer
//   WaterSurfaceSampler  - async height readback + CPU bilinear surface queries
//   WaterSimWindow       - camera-following scrolling sim window for large bodies
//   WaterBedBaker        - terrain -> pool-space bed-height bake (lazy)
//   WaterUniformPublisher- per-body shader uniforms (property block + global mirror)
//   WaterInputRouter     - scene input (primary body only, play mode only)
//   WaterSimScheduler    - static per-frame visibility / sim-budget schedule
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
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
        [SerializeField] internal ComputeShader simCompute;
        [SerializeField] internal Shader causticsShader;
        [SerializeField] internal Shader obstacleShader; // WebGLWater/ObstacleDepth - footprint of interactable objects
        [SerializeField] internal Mesh waterMesh;        // XY grid plane, [-1,1], shared with the water surface renderers
        [SerializeField] internal Camera targetCamera;
        [SerializeField] internal Light sun;             // directional light: drives water, caustics AND real shadows

        [Header("Look / surfaces")]
        [SerializeField] internal Texture tiles;         // pool tile albedo sampled by the water reflection (assign your own)
        [SerializeField] internal Cubemap sky;           // sky cubemap for above-water reflections

        [Header("Water volume (placement)")]
        [Tooltip("World half-size per pool unit, per axis: X = half width, Y = depth to the " +
                 "floor, Z = half length. (1,1,1) is the original 1:1 pool. X != Z gives a " +
                 "rectangular footprint; Y alone makes it shallow/deep. The volume's POSITION " +
                 "and ROTATION come from THIS GameObject's Transform - move/rotate it to place " +
                 "the water. Set extent/transform before Play; the obstacle map reads them at startup.")]
        [SerializeField] internal Vector3 volumeExtent = Vector3.one;

        [Header("Large-water sim window")]
        [Tooltip("For bodies larger than the threshold, run the interactive ripple sim in a " +
                 "camera-following window instead of stretching the fixed grid over the whole " +
                 "surface (which goes blocky on big water). Analytic wind waves still cover " +
                 "everywhere. Small/medium bodies are unaffected.")]
        [SerializeField] internal bool enableLargeBodyWindow = true;
        [Tooltip("World half-extent (max of X,Z) above which windowing turns on. At/below this " +
                 "the whole-body sim is used exactly as before.")]
        [Min(1f)] [SerializeField] internal float largeBodyThreshold = DefaultLargeBodyThreshold;
        [Tooltip("Half-size (world metres) of the camera-following sim window. Ripple detail is " +
                 "2 * this / sim resolution per texel.")]
        [Min(1f)] [SerializeField] internal float simWindowMeters = DefaultSimWindowMeters;
        [Tooltip("On: keep the window fully inside the body footprint (enclosed bodies). Off: the " +
                 "window may overhang the edge and water beyond the footprint is analytic-only " +
                 "(natural for open water).")]
        [SerializeField] internal bool clampWindowToShore = false;
        [Tooltip("Width, in sim texels, over which the window's ripple fades to analytic-only at " +
                 "its border so there is no seam.")]
        [Range(0f, 32f)] [SerializeField] internal float simWindowEdgeFadeTexels = 8f;

        [Header("Open water (lake / ocean) - EXPERIMENTAL")]
        [Tooltip("Render this body as open water: the surface stands alone with NO analytic pool. " +
                 "The refracted view falls back to the deep-water colour where there is no scene " +
                 "geometry, and the mesh god rays are suppressed (the large-body render feature " +
                 "replaces them). OFF = the original pool / small-body look, byte-for-byte unchanged. " +
                 "Publishes the _LargeBody shader flag; the clipmap + FFT modules read the same flag.")]
        [SerializeField] internal bool openWater = false;
        [Tooltip("Open-water SWELL height multiplier. The big waves' scale and direction come from " +
                 "the Wind waves settings below (wind speed scales the swell, wind heading steers it); " +
                 "this is an artistic multiplier on top, like Wave Amplitude Scale is for the small " +
                 "waves. 0 = no big swell (small wind waves remain).")]
        [Min(0f)] [SerializeField] internal float largeWaveAmplitude = 1f;
        [Tooltip("Open-water CHOPPINESS: horizontal Gerstner displacement that sharpens wave crests. " +
                 "0 = smooth sine swell (byte-for-byte the previous look); higher = sharper, more " +
                 "ocean-like peaks. Buoyancy inverts it, so floaters still ride the visible crest.")]
        [Range(0f, LargeWaveChoppinessMax)] [SerializeField] internal float largeWaveChoppiness = 0f;

        // The open-water swell shares the body's wind settings so one wind drives both wave scales.
        // ReferenceWind maps the default breeze (windSpeed 3) to a x1 swell; stronger wind grows it,
        // calm flattens it. Both the shader publisher and the CPU buoyancy read these, so they match.
        const float LargeWaveReferenceWind = 3f;
        // Crest's _Chop range; beyond this the Gerstner surface self-intersects (pinch-through) and the
        // buoyancy inversion stops converging, so the knob is clamped here.
        const float LargeWaveChoppinessMax = 2f;
        internal float LargeWaveHeadingRad => windFromDegrees * Mathf.Deg2Rad;
        internal float LargeWaveAmplitudeEffective => largeWaveAmplitude * (windSpeed / LargeWaveReferenceWind);
        internal float LargeWaveChoppiness => largeWaveChoppiness;

        [Header("Water body (multi-instance)")]
        [Tooltip("Renderers driven by THIS body via a MaterialPropertyBlock (surface above/under, " +
                 "pool, god rays). Assigned by the scene builder.")]
        [SerializeField] internal Renderer surfaceAbove;
        [SerializeField] internal Renderer surfaceUnder;
        [SerializeField] internal Renderer poolRenderer;
        [SerializeField] internal Renderer godRayRenderer;
        [Tooltip("The primary body also mirrors its data to global shader state, the fallback " +
                 "for objects that don't carry a WaterMembership (which otherwise resolves each " +
                 "object's own containing body). Exactly one body should be primary.")]
        [SerializeField] private bool isPrimary = true;
        [Tooltip("On Play, automatically add a WaterMembership to any scene renderer that uses a " +
                 "water material (receiver / pool wall) and doesn't already have one, so a crate " +
                 "or custom pool is lit and fogged by the body it actually sits in - no manual " +
                 "wiring. Only the primary body runs the one-time scan.")]
        [SerializeField] private bool autoLinkReceivers = true;

        /// <summary>Whether this body is the primary one (mirrors its data to global shader
        /// state and acts as the fallback for objects without a WaterMembership).</summary>
        public bool IsPrimary { get => isPrimary; set => isPrimary = value; }

        [Header("Performance (Phase 3)")]
        [Tooltip("Quality tier asset scaling sim/caustic resolution and god-ray steps. Leave " +
                 "empty for the default (256/1024/24) look. Assigned by the scene builder.")]
        [SerializeField] private WaterQuality quality;
        [Tooltip("Pause a body's simulation, caustics and height readback - and stop drawing it - " +
                 "when it is off-screen OR beyond Activation Distance, and let only the nearest few " +
                 "bodies simulate at once. A single visible body is unaffected. Turn off to force " +
                 "this body to always simulate and render.")]
        [SerializeField] private bool enableCulling = true;
        [Tooltip("Bodies whose centre is farther than this from the camera pause their simulation " +
                 "(they hold their last state). Matches the camera far clip by default.")]
        [SerializeField] internal float activationDistance = CameraFarClip;

        /// <summary>Quality tier asset scaling sim/caustic resolution and god-ray steps.
        /// Read at startup; assign before the body enables.</summary>
        public WaterQuality Quality { get => quality; set => quality = value; }

        /// <summary>Pause this body's simulation and rendering when off-screen or beyond the
        /// activation distance.</summary>
        public bool EnableCulling { get => enableCulling; set => enableCulling = value; }

        public enum ReflectionMode { SkyOnly, SSR, Planar }

        // The reflection BASE (what SkyOnly shows and what SSR/Planar layer over): the built-in
        // procedural sky cubemap, or the scene's URP reflection probe / skybox (unity_SpecCube0).
        public enum EnvironmentSource { ProceduralSky, UrpProbe }

        [Header("Reflections (Phase 3c)")]
        [Tooltip("How THIS body reflects. SSR (screen-space over the procedural sky) scales to many " +
                 "bodies. Planar is a full extra scene render across this body's plane - use it for at " +
                 "most ONE 'hero' body. SkyOnly is cheapest (procedural sky only). SSR needs Depth + " +
                 "Opaque Texture enabled on the active URP asset.")]
        [SerializeField] private ReflectionMode reflectionMode = ReflectionMode.SSR;

        /// <summary>How this body reflects (SkyOnly, SSR or Planar). Named 'Reflections'
        /// because the nested <see cref="ReflectionMode"/> enum owns that identifier.</summary>
        public ReflectionMode Reflections { get => reflectionMode; set => reflectionMode = value; }

        [Tooltip("Reflection base environment. ProceduralSky uses the generated sky cubemap (the demo " +
                 "look). UrpProbe reflects the scene's active reflection probe / skybox so the water " +
                 "matches your lit environment. Orthogonal to the mode above and unaffected by the tier.")]
        [SerializeField] internal EnvironmentSource environmentSource = EnvironmentSource.ProceduralSky;

        /// <summary>The primary water body: the global fallback for objects without a
        /// <see cref="WaterMembership"/>. Per-object association goes through
        /// <see cref="BodyContaining"/>.</summary>
        public static WaterVolume Primary { get; private set; }

        /// <summary>Resolve the body an object should use when it isn't inside any specific
        /// one: the primary body, or any found body as a fallback. Prefer
        /// <see cref="BodyContaining"/> for objects that have a world position.</summary>
        public static WaterVolume Resolve()
        {
            if (Primary != null) return Primary;
            // Frame-cache the scene search: per-particle callers (splash drift) would
            // otherwise degrade to a whole-scene FindFirstObjectByType per particle per frame.
            if (_fallbackBodyFrame != Time.frameCount || _fallbackBody == null)
            {
                _fallbackBodyFrame = Time.frameCount;
                _fallbackBody = FindFirstObjectByType<WaterVolume>();
            }
            return _fallbackBody;
        }
        static WaterVolume _fallbackBody;
        static int _fallbackBodyFrame = -1;

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

        /// <summary>All live water bodies. Used by the input router to send a click to
        /// whichever body's surface the ray hits, by the sim scheduler, and by
        /// <see cref="BodyContaining"/>.</summary>
        internal static readonly List<WaterVolume> Bodies = new List<WaterVolume>();

        // Set true after the primary body's one-time autolink scan (reset per play session).
        static bool _receiversAutoLinked;

        // Water shaders whose user renderers should be per-body. Named here so the autolink
        // scan can spot a loose crate/pool that uses one and give it a WaterMembership.
        static readonly string[] WaterMaterialShaderNames =
        {
            "WebGLWater/WaterReceiver",
            "WebGLWater/PoolWall",
        };

        /// <summary>One-time play-mode scan (primary body only): give every scene renderer that
        /// uses a water material - and isn't already driven by a body - a WaterMembership, so it
        /// is lit and fogged by the body it sits in without manual wiring. Idempotent: skips
        /// renderers that already carry the component or belong to a body's own surface/pool.</summary>
        static void AutoLinkReceivers()
        {
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r.GetComponent<WaterMembership>() != null) continue;
                if (IsBodyOwnedRenderer(r)) continue;   // driven by ApplyBodyBlock already
                if (!UsesWaterMaterial(r)) continue;
                r.gameObject.AddComponent<WaterMembership>();
            }
        }

        // True when a renderer is one this-or-another body drives directly (surface/pool/god
        // rays), so the autolink scan must not also attach a membership and double-write its MPB.
        static bool IsBodyOwnedRenderer(Renderer r)
        {
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume b = Bodies[i];
                if (r == b.surfaceAbove || r == b.surfaceUnder || r == b.poolRenderer || r == b.godRayRenderer)
                    return true;
            }
            return false;
        }

        static bool UsesWaterMaterial(Renderer r)
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) continue;
                for (int s = 0; s < WaterMaterialShaderNames.Length; s++)
                    if (m.shader.name == WaterMaterialShaderNames[s]) return true;
            }
            return false;
        }

        // Fast Enter Play Mode (the Unity 6.6 default) skips the domain reload, so statics
        // survive between play sessions. Reset every piece of scene-lifetime static state
        // before each session; OnEnable/OnDisable rebuild it for the new one.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticState()
        {
            Primary = null;
            Bodies.Clear();
            _fallbackBody = null;
            _fallbackBodyFrame = -1;
            _receiversAutoLinked = false;
#if WEBGPUWATER_URP
            _pipelineOwner = null;
            _savedRenderScale = 0f;
            _savedOpaqueTexture = false;
#endif
            WaterSimScheduler.ResetStaticState();
            WaterInteractable.ResetStaticState();
        }

        [Header("Simulation")]
        [Tooltip("Direction TOWARD the light. Auto-driven from 'sun' when one is assigned.")]
        [SerializeField] internal Vector3 lightDir = new Vector3(2f, 2f, -1f);
        [SerializeField] internal int causticResolution = 1024;

        [Header("Object interaction")]
        [Tooltip("How floating objects disturb the water. MouseLikeDrops clones the mouse " +
                 "interaction: analytic cosine drops from bobbing and drift (uses Ripple " +
                 "Radius/Strength below; smooth, zero rasterization noise, slow rotation is " +
                 "silent). FootprintDelta displaces by the rasterized submerged footprint " +
                 "(shaped wakes for large hulls; costlier and noisier).")]
        [SerializeField] internal ObjectInteraction objectInteraction = ObjectInteraction.MouseLikeDrops;
        [Tooltip("FootprintDelta mode: MASTER strength for how strongly submerged objects " +
                 "displace the water. Multiplies the per-frame submerged-thickness DELTA " +
                 "(a much smaller quantity than a mouse drop's unit push), so it reads " +
                 "higher than Ripple Strength for a comparable wake. " +
                 "Per-object weighting is WaterInteractable.displaceScale.")]
        [Range(0f, 1f)] [SerializeField] internal float obstacleStrength = 0.25f;
        [Tooltip("FootprintDelta mode: soft dead-band (in submerged-thickness world units) " +
                 "that swallows tiny footprint deltas from drift/rotation rasterization " +
                 "noise. Raise to kill jitter; LOWER if a slowly moving float's wake is " +
                 "invisible (its genuine per-frame delta is sub-millimetre).")]
        [Range(0f, 0.005f)] [SerializeField] internal float obstacleDeadband = 0.0006f;
        [Tooltip("Temporal smoothing of the object footprint (0 = off). Low-pass filters " +
                 "the displacement a floater injects, so continuous bobbing/rotation emits " +
                 "a few long clean waves instead of a dense packet of tight rings. The " +
                 "total displaced volume is unchanged; higher = calmer but lazier response.")]
        [Range(0f, 0.95f)] [SerializeField] internal float obstacleSmoothing = 0.65f;
        [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
        [SerializeField] internal bool obstacleFlipY = true;

        [Header("Water fog (Beer-Lambert)")]
        [Tooltip("Global depth absorption, shared by the surface, objects and pool.")]
        [SerializeField] private bool waterFog = false;
        [SerializeField] internal Color fogColor = new Color(0.10f, 0.30f, 0.40f);
        [Tooltip("Per-channel extinction; red highest so it absorbs first. HDR: push a channel " +
                 "above 1 for very heavy absorption (fully opaque water on short paths).")]
        [ColorUsage(false, true)] [SerializeField] internal Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Tooltip("Overall fog multiplier. Higher = thicker; crank it (with extinction) for pea-soup water.")]
        [Range(0f, MaxFogDensity)] [SerializeField] internal float fogDensity = 2f;

        /// <summary>Beer-Lambert depth fog, shared by the surface, objects and pool.</summary>
        public bool WaterFog { get => waterFog; set => waterFog = value; }
        [Tooltip("Art-directed turbidity independent of depth: lerp the view THROUGH the surface " +
                 "toward the fog colour. 0 = clear, 1 = fully non-transparent water. Reflections " +
                 "still show on top (tune with the material's Reflection Strength).")]
        [Range(0f, 1f)] [SerializeField] internal float waterOpacity = 0f;

        [Header("Depth attenuation (downwelling)")]
        [Tooltip("Darken submerged surfaces, caustics and god rays the DEEPER they sit, " +
                 "independent of view distance. Separate from the view-path fog above.")]
        [SerializeField] internal bool depthDarken = false;
        [Tooltip("Per-channel downwelling extinction (red highest so deep water shifts blue). " +
                 "Applied as exp(-extinction * strength * depth).")]
        [SerializeField] internal Color depthExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Tooltip("Master multiplier on the depth term (acts like the fog density).")]
        [Range(0f, 8f)] [SerializeField] internal float depthDarkenStrength = 1f;
        [Tooltip("Extra softening of projected caustics on objects, per world unit of depth.")]
        [Range(0f, 8f)] [SerializeField] internal float causticDepthFade = 0.5f;
        [Tooltip("How fast god-ray shafts fade with depth, per world unit of depth.")]
        [Range(0f, 8f)] [SerializeField] internal float godRayDepthFade = 0.5f;
        [Tooltip("Mirror the fog extinction into the depth extinction each frame, so one dial " +
                 "drives fog + depth darkening. Off = the depth colour is fully independent.")]
        [SerializeField] internal bool linkDepthToFog = false;

        [Header("Bed depth (real terrain depth - EXPERIMENTAL)")]
        [Tooltip("Use the baked terrain bed height for real water-column depth (shoreline " +
                 "gradient). Off = flat-floor behaviour.")]
        [SerializeField] internal bool useBedDepth = false;
        [Tooltip("Terrain whose heightmap defines the lake bed. Auto-resolves to the active " +
                 "Terrain if empty. Baked once at startup; call RebakeBed() (or the context-menu " +
                 "item) if the terrain changes.")]
        [SerializeField] internal Terrain bedTerrain;
        [Tooltip("Resolution of the baked pool-space bed-height map.")]
        [Range(WaterBedBaker.MinResolution, WaterBedBaker.MaxResolution)] [SerializeField] internal int bedResolution = 256;
        [Tooltip("Colour the surface tints toward over deep water.")]
        [SerializeField] internal Color deepWaterColor = new Color(0.02f, 0.10f, 0.15f);
        [Tooltip("World-unit depth at which the shoreline gradient reaches ~63% toward the deep " +
                 "colour. Smaller = the water darkens in shallower depth.")]
        [Range(0.1f, 50f)] [SerializeField] internal float shorelineFadeDepth = 6f;
        [Tooltip("Maximum tint toward the deep-water colour.")]
        [Range(0f, 1f)] [SerializeField] internal float shorelineStrength = 0.8f;

        [Header("Wind waves (spectral)")]
        [Tooltip("Ambient wind-driven wave layer composited on top of the interactive ripples. " +
                 "Floating objects ride these waves too.")]
        [SerializeField] private bool windWaves = true;
        [Tooltip("Wind speed (m/s). ~3 = light breeze.")]
        [Range(0f, 15f)] [SerializeField] internal float windSpeed = 3f;
        [Tooltip("Wind heading in degrees: 0 = blowing toward +X (i.e. coming from the west).")]
        [Range(0f, 360f)] [SerializeField] internal float windFromDegrees = 0f;

        /// <summary>Ambient wind-driven wave layer composited on top of the interactive
        /// ripples. Floating objects ride these waves too.</summary>
        public bool WindWaves { get => windWaves; set => windWaves = value; }
        [Tooltip("Physical size the pool half-extent ([-1,1] -> +/-this) represents, in metres. " +
                 "Sets wave scale; fetch is twice this.")]
        [Range(1f, 50f)] [SerializeField] internal float poolHalfExtentMeters = 10f;
        [Tooltip("Number of sinusoidal components summed for the wave layer.")]
        [Range(1, WaterWaveBank.MaxWaves)] [SerializeField] internal int waveCount = 12;
        [Tooltip("Artistic multiplier on the physically-derived wave height (a light breeze " +
                 "on a small lake is physically sub-cm, so some exaggeration reads better).")]
        [Range(0f, 12f)] [SerializeField] internal float waveAmplitudeScale = 4f;
        [Tooltip("Higher = waves cling more tightly to the wind direction (parallel, river-like). " +
                 "Lower = broader, choppier crossing crests.")]
        [Range(1f, 12f)] [SerializeField] internal float waveDirectionSpread = 2f;
        [Tooltip("Scales how strongly the wind waves tilt the surface normal.")]
        [Range(0f, 3f)] [SerializeField] internal float waveNormalStrength = 1f;

        [Header("Foam")]
        [SerializeField] private bool foam = false;
        [Tooltip("How fast turbulence creates foam.")]
        [Range(0f, 2f)] [SerializeField] internal float foamGenRate = 0.6f;
        [Tooltip("SURVIVAL factor per step of thick, fresh foam (not a decay rate: HIGHER = foam lasts longer). Lower = bursts collapse faster.")]
        [Range(0.80f, 1f)] [SerializeField] internal float foamDecay = 0.96f;
        [Tooltip("SURVIVAL factor per step of thin residual lace. Must sit above the fresh value (clamped at runtime if not). Higher = lace lingers longer after the burst.")]
        [Range(0.90f, 1f)] [SerializeField] internal float foamDecayResidual = 0.993f;
        [Tooltip("Time scale of foam decay, frame-rate independent: 1 = authored speed, 2 = fades twice as fast, 0.5 = half. Tune fade SPEED here; the survival sliders above compound ~60x per second, so tiny changes there swing the look violently.")]
        [Range(0.05f, 4f)] [SerializeField] internal float foamDecayRate = 1f;
        [Tooltip("Diffusion of foam toward neighbours.")]
        [Range(0f, 1f)] [SerializeField] internal float foamSpread = 0.2f;
        [Tooltip("How far foam is carried along the surface flow each step (texels). 0 = old isotropic spread.")]
        [Range(0f, 8f)] [SerializeField] internal float foamAdvect = 3f;
        [SerializeField] internal float foamFromSpeed = 6f;
        [SerializeField] internal float foamFromCurvature = 30f;
        [Space]
        [SerializeField] internal Color foamColor = Color.white;
        [Range(0f, 2f)] [SerializeField] internal float foamStrength = 1f;
        [Tooltip("Softness of the foam edges: mask level over which foam fades in from nothing. 0 = hard edges (no feathering).")]
        [Range(0f, 0.5f)] [SerializeField] internal float foamFeather = 0.15f;
        [Tooltip("How much the foam pattern erodes the dense core: 0 = solid white core, 1 = core breaks into pattern detail like the residual lace.")]
        [Range(0f, 1f)] [SerializeField] internal float foamCoreCut = 0.5f;
        [Tooltip("Width of the foam band along the pool walls (pool units).")]
        [Range(0f, 0.5f)] [SerializeField] internal float foamBorderWidth = 0.08f;
        [Tooltip("Depth band for contact foam where objects meet the waterline.")]
        [Range(0f, 0.5f)] [SerializeField] internal float foamContactDepth = 0.06f;

        /// <summary>Turbulence-driven surface foam simulation and shading.</summary>
        public bool Foam { get => foam; set => foam = value; }

        [Header("Ripple tuning")]
        [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
        [Range(0.1f, 2.0f)] [SerializeField] internal float waveSpeed = 0.6f;
        [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
        [Range(0.90f, 1.0f)] [SerializeField] internal float damping = 0.99f;
        [Tooltip("Solver steps per frame AT THE 60 FPS REFERENCE - the sim accumulates real " +
                 "time and runs this rate regardless of frame rate, so wave speed is identical " +
                 "in a 30 fps build and a 144 fps editor. More = faster, smoother propagation.")]
        [Range(1, 8)] [SerializeField] internal int stepsPerFrame = 2;
        [Tooltip("Height added by a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.001f, 0.08f)] [SerializeField] private float rippleStrength = 0.025f;
        [Tooltip("Radius of a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.005f, 0.2f)] [SerializeField] private float rippleRadius = 0.05f;
        [Tooltip("Seed the pool with random ripples on start.")]
        [SerializeField] internal bool seedRipplesOnStart = true;
        [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
        [SerializeField] internal bool conserveVolume = true;

        /// <summary>Height added by a click/drag ripple (world units).</summary>
        public float RippleStrength { get => rippleStrength; set => rippleStrength = value; }

        /// <summary>Radius of a click/drag ripple (world units).</summary>
        public float RippleRadius { get => rippleRadius; set => rippleRadius = value; }
        [Tooltip("Safety cap on how far Conserve Volume can shift the whole surface per step " +
                 "(pool units). The mean is computed exactly, so this only guards against a " +
                 "diverged transient moving the plane in one step.")]
        [Range(0.005f, 0.5f)] [SerializeField] internal float conserveMaxCorrection = 0.05f;

        [Header("Camera")]
        [SerializeField] internal OrbitCamera orbit;
        [Tooltip("Apply the package's default framing (FOV, near/far clip) to the target camera " +
                 "at enable. Off by default: a drop-in water body must not silently overwrite a " +
                 "game's camera setup. The demo scene builder frames its camera at build time.")]
        [SerializeField] internal bool configureCamera = false;

        [Header("Splash")]
        [Tooltip("Shared splash emitter used for mouse interaction (and objects).")]
        [SerializeField] internal WaterSplashEmitter splashEmitter;

        // runtime collaborators (see the header comment for the responsibility map)
        WaterSimulation _water;
        WaterObstacle _obstacle;
        WaterCausticsPass _caustics;
        WaterSurfaceSampler _sampler;
        WaterSimWindow _simWindow;
        WaterBedBaker _bedBaker;
        WaterUniformPublisher _publisher;
        WaterInputRouter _inputRouter;

        // Camera-following high-detail surface over the sim window (windowed bodies, play mode).
        // Its grid is built at the SIM resolution so the near field is sampled ~one vertex per
        // texel - the far plane's fixed grid stretched over a large volume samples the ripple
        // heightfield too sparsely and aliases into false, bobbing ripples.
        Renderer _patchRenderer;
        Mesh _patchGrid;
        MaterialPropertyBlock _patchMpb;
        static readonly int ID_IsPatch = Shader.PropertyToID("_IsPatch");
        static readonly int ID_PatchPoolCenter = Shader.PropertyToID("_PatchPoolCenter");
        static readonly int ID_PatchPoolHalf = Shader.PropertyToID("_PatchPoolHalf");
        static readonly int ID_PatchDepthBias = Shader.PropertyToID("_PatchDepthBias");
        const float PatchDepthBiasNdc = 1e-4f;      // tiny nudge toward the camera to beat the coplanar far plane
        const string PatchObjectName = "Sim Window Patch";

        // Lazy: the bed baker serves the context-menu RebakeBed even on an uninitialized
        // body, and the publisher serves WriteBodyProps callers defensively.
        WaterBedBaker BedBaker => _bedBaker ??= new WaterBedBaker(this);
        WaterUniformPublisher Publisher => _publisher ??= new WaterUniformPublisher(this);
        WaterInputRouter InputRouter => _inputRouter ??= new WaterInputRouter(this);

        // Internal collaborator surface (same assembly only).
        internal WaterSimulation Simulation => _water;
        internal WaterWaveBank WaveBank => _waveBank;
        internal float WaveTime => _waveTime;
        internal RenderTexture CausticTexture => _caustics?.Texture;
        internal Texture2D BedTexture => _bedBaker?.Texture;
        internal bool IsBedBaked => _bedBaker != null && _bedBaker.IsBaked;
        internal int GodRaySteps => _godRaySteps;
        internal int PeakedRefineSteps => _peakedRefineSteps;
        internal void TogglePause() => _paused = !_paused;

        // wind-wave layer (shared by the surface shader and CPU buoyancy)
        readonly WaterWaveBank _waveBank = new WaterWaveBank();
        float _waveTime;
        // Bank-generation inputs baked into the current bank, compared field-by-field. (A
        // packed signature could alias two distinct states and silently keep stale amplitudes.)
        float _waveGenWindSpeed = float.NaN;
        float _waveGenWindFrom;
        float _waveGenExtentMeters;
        int _waveGenCount;
        float _waveGenAmpScale;
        float _waveGenSpread = float.NaN;
        float _waveGenVerticalExtent = float.NaN; // volume y-extent baked into the current bank
        bool _waveGenEnabled;

        int _simRes = WaterQuality.Default.SimResolution; // grid resolution, set from the quality tier at OnEnable
        bool _godRaysAllowed = true;                       // false when the tier turns god rays off
        bool _richReflectionsAllowed = true;               // false when the tier caps reflections to SkyOnly
        // Tier cost knobs delivered per-body through the property block (never by writing the
        // shared god-ray/surface material, which dirties the asset and lets bodies stomp each other).
        int _godRaySteps = WaterQuality.Default.GodRaySteps;
        int _maxWaveCount = WaterQuality.Default.MaxWaveCount;
        int _peakedRefineSteps = WaterQuality.Default.RefineSteps;
        // Low-end tier knobs (see WaterQuality): at their defaults every one is a no-op.
        float _renderScale = WaterQuality.Default.RenderScale;
        bool _realRefractionAllowed = true;
        int _meshDetail = WaterQuality.Default.MeshDetail;
        int _causticInterval = WaterQuality.Default.CausticInterval;
        int _readbackInterval = WaterQuality.Default.ReadbackInterval;
        int _maxFoamParticles = WaterQuality.Default.MaxFoamParticles;
        /// <summary>Tier cap on the GPU foam-particle pool (WaterFoamParticles clamps to it).</summary>
        internal int FoamParticleBudget => _maxFoamParticles;
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable (play mode only) and destroyed at
        // OnDisable, which also restores the renderer's original shared material so an
        // enable/disable cycle never leaves a renderer pointing at a destroyed instance.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        Material _surfaceAboveOriginal, _surfaceUnderOriginal;
        const string KW_SSR = "_USE_SSR";
        const string KW_PLANAR = "_USE_PLANAR";
        const string KW_URP_PROBE = "_USE_URP_PROBE";
        const string KW_REAL_REFRACTION = "_REAL_REFRACTION";
        // Low-tier coarse grid swapped onto the surface renderers at init (play mode only);
        // the originals are restored on disable, mirroring the material-instance pattern.
        Mesh _lowDetailGrid;
        Mesh _surfaceAboveOriginalMesh, _surfaceUnderOriginalMesh;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        bool _paused;
        float _stepDebt;     // fractional solver steps owed (frame-rate-independent stepping)
        float _foamTimeDebt; // reference steps elapsed since the last foam pass (foam runs once per frame, not per solver step)

        bool _windowed; // this body runs the camera-following windowed sim (decided at OnEnable)

        // Per-frame schedule flags, written for every body by WaterSimScheduler (frame-guarded,
        // so the result is independent of the arbitrary order in which the bodies Update).
        const float WaveHeightMargin = 0.1f;  // pool-space headroom above y=0 for wind-wave crests in the cull box
        internal bool _visible = true;   // inside the camera frustum -> its renderers draw
        internal bool _simulate = true;  // visible AND in range AND within the sim budget -> runs the GPU sim

        // Camera framing. activationDistance defaults to the far clip so "beyond the far clip"
        // is exactly what pauses a distant body - the two stay coupled, not coincidentally equal.
        // Internal so the editor build kit frames its demo camera from the same constants.
        internal const float CameraFieldOfView = 45f;
        internal const float CameraNearClip = 0.01f;
        internal const float CameraFarClip = 100f;

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

        // Skip a sim step after an editor hitch/breakpoint: integrating one huge dt would
        // slam the explicit solver with energy in a single step.
        const float MaxStepSeconds = 1f;

        // Frame-rate-independent stepping: 'stepsPerFrame' is authored against this frame
        // rate; the solver runs stepsPerFrame * ReferenceFrameRate steps per SECOND at any
        // fps. The per-frame cap bounds the catch-up burst on slow devices/hitches - beyond
        // it the debt is dropped, so waves degrade to "slightly slower" instead of bursting.
        const float ReferenceFrameRate = 60f;
        const int MaxSolverStepsPerFrame = 8;
        // Cap on the foam time debt (reference steps), mirroring MaxSolverStepsPerFrame:
        // after a long pause foam catches up at most this much instead of vanishing in one pass.
        const float MaxFoamTimeDebtSteps = 8f;

        // Numeric guards.
        const float MinVolumeExtent = 1e-5f;        // a zero extent would collapse the pool-space transforms
        const float MinWindowHalfExtent = 1e-3f;    // same guard for the scrolling sim window
        const float RayParallelEpsilon = 1e-6f;     // surface picking: treat near-parallel rays as a miss
        internal const float MinShorelineFadeDepth = 0.01f; // keeps the shoreline depth scale finite (publisher)
        const float MinWaveMetersPerUnit = 1e-3f;   // keeps wave-space conversions finite

        // Edit-mode preview: Update ticks come from the editor loop at an uneven cadence, so
        // the sim integrates real elapsed time, clamped so a pause between repaints doesn't
        // feed one huge step into the solver.
        const float MaxEditorDeltaSeconds = 1f / 30f;

        // True once the GPU resources exist and the body is registered; guards teardown and
        // the edit-mode lazy-init retry (see TryInitialize).
        bool _initialized;

        void OnEnable()
        {
            TryInitialize();
        }

        // Full setup, run once per enable. In edit mode ([ExecuteAlways]) missing wiring is
        // NOT an error yet: the scene builders AddComponent first and wire fields afterwards,
        // and Update retries, so a hand-wired body starts previewing the moment the last
        // reference lands. In play mode missing wiring fails fast and loud.
        void TryInitialize()
        {
            if (_initialized || !enabled) return;

            if (!HasRequiredWiring())
            {
                if (Application.isPlaying) FailMissingWiring();
                return;
            }

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

            ApplyQuality();     // sets _simRes, causticResolution, _godRaysAllowed + per-body cost knobs

            _water = new WaterSimulation(simCompute, _simRes);
            _sampler = new WaterSurfaceSampler(this); // probes readback support itself
            _simWindow = new WaterSimWindow(this);
            _lastEditorTick = 0d;
            _stepDebt = 0f;
            _foamTimeDebt = 0f;
            _windowed = ShouldWindow(); // decided once; volumeExtent is fixed before Play
            ApplySimAnisotropy();       // round ripples on a rectangular pool (no-op for square/windowed)
#if UNITY_EDITOR
            WarnIfLargeBody();           // editor-only heads-up: large bodies are experimental in this POC
            WarnIfExperimentalTerrain(); // editor-only heads-up: terrain bed-depth is experimental
#endif

            if (obstacleShader != null)
                _obstacle = new WaterObstacle(obstacleShader, _simRes,
                                              VolumeCenter, VolumeRotation, VolumeExtentSafe);

            _caustics = new WaterCausticsPass(causticsShader, causticResolution);

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

            // Opt-in only: a package component must not silently hijack the game's camera.
            if (configureCamera && targetCamera != null)
            {
                targetCamera.fieldOfView = CameraFieldOfView;
                targetCamera.nearClipPlane = CameraNearClip;
                targetCamera.farClipPlane = CameraFarClip;
            }

            if (isPrimary)
            {
                if (Primary != null && Primary != this)
                    Debug.LogWarning("WaterVolume: multiple bodies are marked Is Primary; the last " +
                                     "one enabled wins. Exactly one body should be primary.", this);
                Primary = this;
            }
            if (!Bodies.Contains(this)) Bodies.Add(this);
            _mpb = new MaterialPropertyBlock();
            ApplyReflections();
            ApplyMeshDetail();   // Low tier: coarse surface grid (play mode only)
            ApplyPipelineTier(); // Low tier: render scale / opaque-copy release (primary, play mode only)
            CreateSimWindowPatch(); // windowed bodies: dense near-field surface over the sim window

            BedBaker.EnsureBaked(); // lazy terrain -> pool-space bed bake, only when useBedDepth is on

            Publisher.PublishSharedGlobals();
            EnsureWaveBank();
            if (_windowed) _simWindow.Track();  // prime the window centre before first publish
            if (!_windowed) RenderCaustics();   // caustics are out of scope for windowed bodies
            ApplyBodyBlock();
            if (isPrimary) Publisher.PublishBodyGlobals();

            _initialized = true;
        }

        void OnDisable()
        {
            if (!_initialized) return; // never initialized (missing wiring / capability guard)

            _initialized = false;
            if (Primary == this) Primary = FindNextPrimary(this);
            Bodies.Remove(this);
            _water?.Dispose(); _water = null;
            _obstacle?.Dispose(); _obstacle = null;
            _caustics?.Dispose(); _caustics = null;
            _bedBaker?.Dispose();  // also re-arms the lazy bake gate for the next enable
            DestroySimWindowPatch(); // before restoring the surface material it borrows
            RestoreSurfaceMaterial(surfaceAbove, ref _surfaceAboveInstance, ref _surfaceAboveOriginal);
            RestoreSurfaceMaterial(surfaceUnder, ref _surfaceUnderInstance, ref _surfaceUnderOriginal);
            RestoreMeshDetail();
            RestorePipelineTier();
            // Fresh per-enable state: a re-enable must not float objects on a stale height
            // field, and the window centre re-primes from the camera.
            _sampler = null;
            _simWindow = null;
            _inputRouter = null;
        }

        // ---- Low-tier surface grid swap ----------------------------------------
        // The authored grid is 200x200 and the vertex shader runs 4 fetches + the wave sines
        // per vertex; a 128 sim doesn't need that tessellation. Play mode only (an edit-mode
        // swap could serialize the runtime mesh reference into the scene), mirroring the
        // material-instance pattern: originals restored on disable.
        void ApplyMeshDetail()
        {
            if (!Application.isPlaying || _meshDetail <= 0) return;

            _lowDetailGrid = WaterMeshBuilder.BuildGrid(_meshDetail);
            _lowDetailGrid.hideFlags = HideFlags.HideAndDontSave;
            SwapRendererMesh(surfaceAbove, _lowDetailGrid, ref _surfaceAboveOriginalMesh);
            SwapRendererMesh(surfaceUnder, _lowDetailGrid, ref _surfaceUnderOriginalMesh);
        }

        void RestoreMeshDetail()
        {
            RestoreRendererMesh(surfaceAbove, ref _surfaceAboveOriginalMesh);
            RestoreRendererMesh(surfaceUnder, ref _surfaceUnderOriginalMesh);
            if (_lowDetailGrid != null) { DestroyRuntimeObject(_lowDetailGrid); _lowDetailGrid = null; }
        }

        // The caustic pass shares whichever grid the surface uses this session.
        Mesh EffectiveWaterMesh => _lowDetailGrid != null ? _lowDetailGrid : waterMesh;

        static void SwapRendererMesh(Renderer r, Mesh replacement, ref Mesh original)
        {
            original = null;
            if (r == null) return;
            var filter = r.GetComponent<MeshFilter>();
            if (filter == null) return;
            original = filter.sharedMesh;
            filter.sharedMesh = replacement;
        }

        static void RestoreRendererMesh(Renderer r, ref Mesh original)
        {
            if (original == null) return;
            var filter = r != null ? r.GetComponent<MeshFilter>() : null;
            if (filter != null) filter.sharedMesh = original;
            original = null;
        }

        // ---- Low-tier global URP knobs ------------------------------------------
        // Render scale and the opaque-texture copy are PIPELINE-wide, so the primary body
        // applies them once (play mode only) and restores the authored values on disable -
        // the asset never keeps a tier's values.
#if WEBGPUWATER_URP
        static WaterVolume _pipelineOwner; // the body that applied the tweaks (and must restore them)
        static float _savedRenderScale;
        static bool _savedOpaqueTexture;
#endif

        void ApplyPipelineTier()
        {
#if WEBGPUWATER_URP
            if (!Application.isPlaying || !isPrimary || _pipelineOwner != null) return;
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (pipeline == null) return;

            bool wantScale = _renderScale < 1f;
            bool wantOpaqueOff = !_realRefractionAllowed; // nothing else in the package reads the opaque copy
            if (!wantScale && !wantOpaqueOff) return;

            _savedRenderScale = pipeline.renderScale;
            _savedOpaqueTexture = pipeline.supportsCameraOpaqueTexture;
            if (wantScale) pipeline.renderScale = _renderScale;
            if (wantOpaqueOff) pipeline.supportsCameraOpaqueTexture = false;
            _pipelineOwner = this;
#endif
        }

        void RestorePipelineTier()
        {
#if WEBGPUWATER_URP
            if (_pipelineOwner != this) return; // only the body that applied restores
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (pipeline != null)
            {
                pipeline.renderScale = _savedRenderScale;
                pipeline.supportsCameraOpaqueTexture = _savedOpaqueTexture;
            }
            _pipelineOwner = null;
#endif
        }

        bool HasRequiredWiring() => simCompute != null && causticsShader != null && waterMesh != null;

        // Fail fast on the required wiring (play mode); a missing piece would otherwise surface
        // later as a confusing downstream error (broken caustic material, per-frame DrawMesh errors).
        void FailMissingWiring()
        {
            if (simCompute == null) Debug.LogError("WaterVolume: simCompute not assigned.", this);
            else if (causticsShader == null) Debug.LogError("WaterVolume: causticsShader not assigned.", this);
            else Debug.LogError("WaterVolume: waterMesh not assigned.", this);
            enabled = false;
        }

        // Hand the primary role to another live body flagged isPrimary, so disabling one of two
        // (misconfigured) primaries doesn't strand Primary at null while a candidate is alive -
        // that would send every Resolve() into a per-call whole-scene search.
        static WaterVolume FindNextPrimary(WaterVolume leaving)
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i] != leaving && Bodies[i].isPrimary) return Bodies[i];
            return null;
        }

        // Restore the renderer's authored material before destroying the per-body instance, so
        // a disable/enable cycle never leaves the renderer pointing at a destroyed material.
        static void RestoreSurfaceMaterial(Renderer r, ref Material instance, ref Material original)
        {
            if (instance == null) { original = null; return; }
            if (r != null && original != null) r.sharedMaterial = original;
            DestroyRuntimeObject(instance);
            instance = null;
            original = null;
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
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
            if (quality == null) return; // keep the inspector defaults / Default-tier cost knobs

            WaterQuality.Tier tier = quality.Resolve();
            _simRes = tier.SimResolution;
            causticResolution = tier.CausticResolution;
            _godRaysAllowed = tier.GodRays;
            _richReflectionsAllowed = tier.RichReflections;
            // Delivered per-body through WriteBodyUniforms (property block), never by writing
            // the shared god-ray material - which dirtied the asset in the editor and let
            // multiple bodies stomp each other's step count. Clamped >= 1 so a "god rays off"
            // tier (0 steps) can't bake a divide-by-zero; the renderer is disabled separately.
            _godRaySteps = Mathf.Max(1, tier.GodRaySteps);
            _maxWaveCount = tier.MaxWaveCount;
            _peakedRefineSteps = tier.RefineSteps;
            _renderScale = tier.RenderScale;
            _realRefractionAllowed = tier.RealRefraction;
            _meshDetail = tier.MeshDetail;
            _causticInterval = tier.CausticInterval;
            _readbackInterval = tier.ReadbackInterval;
            _maxFoamParticles = tier.MaxFoamParticles;

            // One line per enable so a build's console shows exactly which knobs landed -
            // tier mismatches (stale build cache, wrong asset, missing serialized fields)
            // are otherwise near-impossible to diagnose on a device.
            Debug.Log($"WaterVolume '{name}': quality tier applied - sim {_simRes}, caustics {causticResolution}, " +
                      $"mesh {(_meshDetail > 0 ? _meshDetail.ToString() : "authored")}, renderScale {_renderScale:0.##}, " +
                      $"realRefraction {_realRefractionAllowed}, godRays {_godRaysAllowed} ({_godRaySteps} steps), " +
                      $"waves {_maxWaveCount}, refine {_peakedRefineSteps}, foamCap {_maxFoamParticles}", this);
        }

        // Give the surface renderers per-body material instances and set their reflection
        // keywords from the tier-capped EffectiveReflectionMode, so bodies in different modes
        // don't fight over one shared material. A Planar body also binds the scene's single
        // planar reflection.
        void ApplyReflections()
        {
            // Edit-mode preview leaves the authored shared materials untouched: an instance
            // assigned to sharedMaterial could be saved into the scene as a dead reference.
            // Preview reflections therefore follow the material's authored keywords.
            if (!Application.isPlaying) return;

            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove, out _surfaceAboveOriginal);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder, out _surfaceUnderOriginal);
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
        // the scene asset is untouched). The original is captured so OnDisable can restore it
        // before destroying the instance.
        static Material InstanceSurfaceMaterial(Renderer r, out Material original)
        {
            original = null;
            if (r == null || r.sharedMaterial == null) return null;
            original = r.sharedMaterial;
            var instance = new Material(original) { hideFlags = HideFlags.HideAndDontSave };
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
            // A tier can DISABLE real refraction (it needs the URP opaque-texture copy, a big
            // bandwidth cost on mobile tilers) but never force it on over the authored material.
            if (!_realRefractionAllowed) SetKeyword(m, KW_REAL_REFRACTION, false);
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
            // Edit-mode lazy init: a body whose wiring was assigned after AddComponent (the
            // builders' order) starts up here on the next editor tick.
            if (!_initialized)
            {
                TryInitialize();
                if (!_initialized) return;
            }

            // Input is a scene-level concern (and play-mode only): the primary body's router
            // handles mouse/keys and routes clicks to whichever body's surface the ray hits
            // (avoids two controllers fighting over one camera).
            if (Application.isPlaying && isPrimary) InputRouter.Update();

            // One-time autolink, deferred to Update (not OnEnable) so every body has registered
            // first - a body's own pool also uses a water material, and IsBodyOwnedRenderer can
            // only skip it once that body is in the registry.
            if (Application.isPlaying && isPrimary && autoLinkReceivers && !_receiversAutoLinked)
            {
                _receiversAutoLinked = true;
                AutoLinkReceivers();
            }

            // Decide (once per frame, for every body) which bodies draw and which run the
            // heavy GPU sim, then stop drawing this one if it is off-screen.
            WaterSimScheduler.EnsureSchedule();
            SetRenderersEnabled(_visible);

            // Edit-mode ticks arrive from the editor loop, so the preview integrates real
            // elapsed (clamped) time instead of the play-mode frame delta.
            float dt = Application.isPlaying ? Time.deltaTime : EditorDeltaSeconds();
            if (!_paused)
            {
                // The analytic wind waves are driven by the shared clock, so they keep moving
                // even on a budget-paused (but visible) body; only the GPU sim is gated.
                _waveTime += dt;
                if (_simulate) Step(dt);
            }

            Publisher.PublishSharedGlobals(); // sun, sky, tiles, camera-independent shared clock
            EnsureWaveBank();
            BedBaker.EnsureBaked();           // picks up useBedDepth being toggled on at runtime
            // Caustics/god rays are out of scope for windowed bodies (the caustic pass maps the
            // whole floor from _WaterTex, which now holds only the moving window). See the
            // floor-relative scheme noted in docs/large-water-sim-window-plan.md.
            // The tier can amortise the pass over N frames (the caustic RT simply holds).
            if (_simulate && !_windowed && Time.frameCount % _causticInterval == 0)
                RenderCaustics();  // renders THIS body's caustic RT from its own sim

            ApplyBodyBlock();           // per-body uniforms -> this body's renderers (MPB)
            // Primary bridge: mirror this body's data to globals as the fallback for objects
            // without a WaterMembership (those resolve their own containing body instead).
            if (isPrimary) Publisher.PublishBodyGlobals();

            // Tier-amortised readback: buoyancy already tolerates async latency, so weak
            // devices can trade a few frames of it for GPU->CPU bandwidth.
            if (_simulate && Time.frameCount % _readbackInterval == 0)
                _sampler.RequestReadback();  // paused bodies keep their last height (objects still float)
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
            ApplyPatchBlock();
        }

        // Feed the sim-window patch the same per-body uniforms PLUS the window remap it needs.
        // Kept out of the shared block so _IsPatch never leaks onto the flat surface renderers.
        // The patch's transform is cosmetic (the shader places its verts via PoolToWorld); it
        // only sizes the culling bounds, so we park it on the window to cull with the window.
        void ApplyPatchBlock()
        {
            if (_patchRenderer == null) return;
            if (_patchMpb == null) _patchMpb = new MaterialPropertyBlock();
            WriteBodyProps(_patchMpb);

            Vector3 poolCenter = WorldToPool(SimWindowCenter);
            _patchMpb.SetFloat(ID_IsPatch, 1f);
            _patchMpb.SetFloat(ID_PatchDepthBias, PatchDepthBiasNdc);
            _patchMpb.SetVector(ID_PatchPoolCenter, new Vector4(poolCenter.x, poolCenter.z, 0f, 0f));
            _patchMpb.SetVector(ID_PatchPoolHalf, new Vector4(
                SimHorizontalExtent / VolumeExtentSafe.x, SimHorizontalExtent / VolumeExtentSafe.z, 0f, 0f));
            _patchRenderer.SetPropertyBlock(_patchMpb);

            Transform t = _patchRenderer.transform;
            t.position = SimWindowCenter;
            t.localScale = SimHalfExtent;
        }

        // Build the windowed near-field patch: a grid at the sim resolution, remapped by the
        // shader into the window's pool sub-region. Reuses THIS body's surface material instance
        // (so it inherits reflections/fog) with _IsPatch riding its property block. Play mode
        // only - it depends on the per-body material instance created in ApplyReflections.
        void CreateSimWindowPatch()
        {
            if (!Application.isPlaying || !_windowed) return;
            if (_patchRenderer != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            _patchGrid = WaterMeshBuilder.BuildGrid(Mathf.Max(1, _simRes));
            _patchGrid.hideFlags = HideFlags.HideAndDontSave;

            var go = new GameObject(PatchObjectName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(surfaceAbove.transform.parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _patchGrid;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = surfaceAbove.sharedMaterial; // same per-body instance; _IsPatch rides the block
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _patchRenderer = mr;
        }

        void DestroySimWindowPatch()
        {
            if (_patchRenderer != null)
            {
                DestroyRuntimeObject(_patchRenderer.gameObject);
                _patchRenderer = null;
            }
            DestroyRuntimeObject(_patchGrid);
            _patchGrid = null;
            _patchMpb = null;
        }

        // (1/res, 1/res, res, res) of the sim texture, so shaders can bilinear-filter it manually
        // (WebGPU won't hardware-filter the RGBAFloat sim RT). Paired with every _WaterTex bind.
        internal Vector4 WaterTexel => new Vector4(1f / _simRes, 1f / _simRes, _simRes, _simRes);

        /// <summary>Overwrite <paramref name="mpb"/> with this body's per-renderer uniforms
        /// (sim + caustic textures, volume frame, waves, fog, foam). Used for this body's own
        /// renderers and by <see cref="WaterMembership"/> to light a floating object with the
        /// lake it is in. The block is cleared, so any per-object look must live in the material.</summary>
        public void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            if (mpb == null) throw new System.ArgumentNullException(nameof(mpb));
            Publisher.WriteBodyProps(mpb);
        }

        void ApplyBlockTo(Renderer r) { if (r != null) r.SetPropertyBlock(_mpb); }

        // World-space AABB of this body's volume (pool box x,z in [-1,1], y in [-1,0]) plus a
        // little headroom for wind-wave crests. The renderers keep huge bounds to avoid wrong
        // culling under the volume transform, so frustum culling tests this real box instead.
        internal Bounds CullBounds()
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
            SetRendererEnabled(_patchRenderer, on && _windowed);
            // God rays obey the quality tier as well as culling: a tier that disables them
            // keeps the renderer off even when the body is on-screen. Windowed bodies also
            // suppress god rays (out of scope, same reason as caustics).
            SetRendererEnabled(godRayRenderer, on && _godRaysAllowed && !_windowed);
        }

        static void SetRendererEnabled(Renderer r, bool on) { if (r != null && r.enabled != on) r.enabled = on; }

        /// <summary>Inject a ripple at a WORLD position (x,z). Converted into the pool
        /// footprint via the volume frame; out-of-footprint calls are ignored. Radius is
        /// in world units (kept round via the average horizontal extent).</summary>
        public void AddRipple(float worldX, float worldZ, float radius, float strength)
        {
            if (_water == null) return;

            // Windowed bodies inject into the sim WINDOW frame; ripples outside it are dropped.
            if (_windowed)
            {
                Vector3 sim = WorldToSim(new Vector3(worldX, SimWindowCenter.y, worldZ));
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
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight, out _)) return false;

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
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight, out Vector2 poolFlow)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            Vector3 worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            if (openWater)
            {
                Vector3 wave = LargeWaveField.EvaluateAtQuery(worldX, worldZ, _waveTime,
                                                       LargeWaveAmplitudeEffective, LargeWaveHeadingRad, LargeWaveChoppiness);
                height += wave.x;
                worldFlow += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
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
            if (_sampler == null) return false; // not initialized yet

            Vector3 pool = WorldToPool(worldPoint);
            if (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f) return false;
            if (!_sampler.TrySamplePoolSurface(worldPoint, pool.x, pool.z, out float surfaceH, out Vector2 poolFlow)) return false;

            depthWorld = (surfaceH - pool.y) * VolumeExtentSafe.y; // pool depth -> world depth along up
            worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            // Open water: the world-space swell is the wind-wave source (the pool wavebank is
            // suppressed for these bodies). Raise the surface by the wave height so the point sits
            // deeper on a crest, and push along the wave slope so the swell carries the object.
            if (openWater)
            {
                Vector3 wave = LargeWaveField.EvaluateAtQuery(worldPoint.x, worldPoint.z, _waveTime,
                                                       LargeWaveAmplitudeEffective, LargeWaveHeadingRad, LargeWaveChoppiness);
                depthWorld += wave.x;
                worldFlow += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
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
            // Open water layers the big world-space swell on top of the small wind waves, mirroring
            // the shader (CPU copy of WaterLargeWaves.hlsl) so floaters ride the rendered surface.
            if (openWater)
                height += LargeWaveField.HeightAtQuery(worldX, worldZ, _waveTime,
                                                LargeWaveAmplitudeEffective, LargeWaveHeadingRad, LargeWaveChoppiness);
            return true;
        }

        void Step(float seconds)
        {
            if (seconds > MaxStepSeconds) return; // hitch/breakpoint guard, see the const
            if (seconds <= 0f) return;            // first edit-mode tick: no elapsed time yet

            // Foam runs once per frame (not per solver step), so it tracks its own elapsed
            // time in reference steps. Accumulated BEFORE the whole-step early-return below,
            // or high-fps frames that owe no solver step would be lost and foam would decay
            // slower the higher the frame rate.
            _foamTimeDebt = Mathf.Min(_foamTimeDebt + seconds * ReferenceFrameRate, MaxFoamTimeDebtSteps);

            // Frame-rate-independent stepping: the explicit solver advances a fixed amount
            // per STEP, so stepping per rendered frame made wave speed scale with fps (a
            // 120 fps editor ran ripples 4x faster than a 30 fps build). Accumulate real
            // time and pay it out in whole steps at the authored rate instead.
            _stepDebt += seconds * ReferenceFrameRate * Mathf.Max(1, stepsPerFrame);
            int steps = (int)_stepDebt;
            if (steps <= 0) return; // very high fps: no full step owed yet, field unchanged
            if (steps > MaxSolverStepsPerFrame)
            {
                steps = MaxSolverStepsPerFrame;
                _stepDebt = 0f; // drop the excess: degrade to slightly-slower waves, never a burst
            }
            else
            {
                _stepDebt -= steps;
            }

            // Scroll the sim window to track the camera before injecting/stepping, so ripples
            // stay world-anchored. No-op for whole-body bodies.
            if (_windowed) _simWindow.Track();

            // FootprintDelta mode only: push the surface with the temporally-smoothed
            // submerged footprint. In MouseLikeDrops mode the WaterInteractables emit
            // analytic drops themselves (via AddRipple) and this pass is skipped entirely.
            if (_obstacle != null && objectInteraction == ObjectInteraction.FootprintDelta)
            {
                // Windowed bodies re-frame the footprint onto the scrolling window each frame.
                if (_windowed) _obstacle.SetFrame(SimWindowCenter, VolumeRotation, SimHalfExtent);
                _obstacle.Render(VolumeCenter.y, 1f - obstacleSmoothing);
                // Compensate for extent.y so an object's displacement is a fixed world height
                // regardless of pool depth (PoolToWorld scales surface height by extent.y).
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr,
                                     obstacleStrength / VolumeExtentSafe.y, obstacleFlipY,
                                     obstacleDeadband);
            }

            for (int i = 0; i < steps; i++)
                _water.StepSimulation(waveSpeed, damping);

            // Exact GPU-reduced mean (no more Blit + GenerateMips: the float-mip mean silently
            // point-sampled in WebGPU builds and popped the plane; see WaterSim.compute).
            if (conserveVolume) _water.ConserveVolume(conserveMaxCorrection);

            _water.UpdateNormals();

            if (foam)
            {
                // Bi-exponential contract: thin residual lace must SURVIVE LONGER than
                // thick fresh foam (residual >= fresh), or the blend inverts and foam
                // pops off as hard-edged blobs. Scene data can't be trusted to keep the
                // ordering (the sliders' ranges overlap), so enforce it here.
                float residualSurvival = Mathf.Max(foamDecayResidual, foamDecay);
                _water.StepFoam(foamGenRate, foamDecay, residualSurvival,
                                foamSpread, foamFromSpeed, foamFromCurvature, foamAdvect,
                                _foamTimeDebt, foamDecayRate);
                _foamTimeDebt = 0f;
            }
        }

        // Render this body's own sim into its own caustic RT. The RT reaches the renderers
        // via the MPB; the primary also mirrors it to the _CausticTex global for objects.
        void RenderCaustics() => _caustics.Render(EffectiveWaterMesh, _water?.Texture);

        // ---- volume placement frame (center + rotation + non-uniform extent) ----
        internal Vector3 VolumeExtentSafe => new Vector3(
            Mathf.Max(volumeExtent.x, MinVolumeExtent),
            Mathf.Max(volumeExtent.y, MinVolumeExtent),
            Mathf.Max(volumeExtent.z, MinVolumeExtent));
        // Position + rotation come from this GameObject's transform (move it to place water).
        internal Vector3 VolumeCenter => transform.position;
        internal Quaternion VolumeRotation => transform.rotation;
        internal Vector3 VolumeUp => VolumeRotation * Vector3.up;
        // Average horizontal extent, used to keep a click ripple round in world units.
        float VolumeHorizontalExtent => 0.5f * (VolumeExtentSafe.x + VolumeExtentSafe.z);

        // Tell the sim how to keep ripples ROUND in world on a rectangular (non-square) pool. The
        // heightfield runs on a square grid over pool space, so on a body with extent.x != extent.z
        // both the drop stamp and the wavefront would stretch to that ratio. We weight the wave
        // Laplacian per axis by ~1/extent^2 (equal WORLD propagation speed; normalised by the
        // smaller extent so the max weight stays at the stable 0.25) and squash the drop stamp by
        // extent/avg (matching the average-extent radius normalisation used by AddRipple). Windowed
        // bodies sim over a SQUARE world window already, so they use the identity values.
        void ApplySimAnisotropy()
        {
            if (_water == null) return;
            if (_windowed) { _water.SetAnisotropy(new Vector2(0.25f, 0.25f), Vector2.one); return; }

            float ex = VolumeExtentSafe.x;
            float ez = VolumeExtentSafe.z;
            float minExtent = Mathf.Min(ex, ez);
            float minSq = minExtent * minExtent;
            float avg = VolumeHorizontalExtent;
            var waveWeight = new Vector2(0.25f * minSq / (ex * ex), 0.25f * minSq / (ez * ez));
            var dropScale = new Vector2(ex / avg, ez / avg);
            _water.SetAnisotropy(waveWeight, dropScale);
        }

#if UNITY_EDITOR
        // One-time editor notice: large bodies (big lakes / oceans) are experimental in this
        // proof-of-concept. The interactive ripple sim is a POOL solver on a fixed grid, so past
        // ~20 m of extent the ripples go coarse and the analytic wind waves aren't ocean-scale.
        // Editor-only so a shipped build never logs it. See the README "Scope" notes.
        const float LargeBodyWarnExtent = 20f; // world half-extent (metres) where the pool solver frays
        bool _largeBodyWarned;

        void WarnIfLargeBody()
        {
            if (_largeBodyWarned) return;
            Vector3 e = VolumeExtentSafe;
            float maxExtent = Mathf.Max(e.x, e.z);
            if (maxExtent <= LargeBodyWarnExtent) return;

            _largeBodyWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' is a large water body (extent ~{maxExtent:0} m). Large bodies " +
                "(big lakes / oceans) are experimental in this version: the interactive ripple sim is a " +
                "pool solver, so its ripples get coarse and the wind waves aren't ocean-scale. This asset " +
                "targets small-to-mid bodies - see the README \"Scope\" notes.", this);
        }

        // One-time editor notice: Unity Terrain integration (the bed-depth bake) is experimental in
        // this proof-of-concept - it approximates a shoreline depth gradient, not full terrain support.
        bool _terrainWarned;

        void WarnIfExperimentalTerrain()
        {
            if (_terrainWarned || !useBedDepth) return;
            _terrainWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' uses terrain bed-depth (Use Bed Depth). Unity Terrain integration " +
                "is experimental in this version - the baked shoreline depth is a basic approximation, not " +
                "full terrain support. See the README \"Scope\" notes.", this);
        }
#endif

        internal Vector3 PoolToWorld(Vector3 pool) => VolumeCenter + VolumeRotation * Vector3.Scale(pool, VolumeExtentSafe);

        internal Vector3 WorldToPool(Vector3 world)
        {
            Vector3 e = VolumeExtentSafe;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (world - VolumeCenter);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // ---- large-water sim window frame ----------------------------------
        // Half-size (world) of the window: simWindowMeters horizontally, the body's depth
        // scale vertically (ripple height stays coupled to extent.y like the whole-body sim).
        internal Vector3 SimHalfExtent => new Vector3(
            Mathf.Max(simWindowMeters, MinWindowHalfExtent),
            VolumeExtentSafe.y,
            Mathf.Max(simWindowMeters, MinWindowHalfExtent));

        // Average horizontal window half-size, keeping an injected ripple round in world units.
        float SimHorizontalExtent => Mathf.Max(simWindowMeters, MinWindowHalfExtent);

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
            Publisher.WriteSimFrameUniforms(cs);
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
                Vector3 center = _windowed ? SimWindowCenter : VolumeCenter;
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
        /// <summary>World centre of the active sim window (follows the camera at runtime).
        /// The volume centre until the window exists.</summary>
        public Vector3 SimWindowCenter => _simWindow != null ? _simWindow.Center : VolumeCenter;
        /// <summary>World half-size (x,z) and depth scale (y) of the sim window.</summary>
        public Vector3 SimWindowHalfExtent => SimHalfExtent;

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window).
        internal Vector3 WorldToSim(Vector3 world) => _simWindow.WorldToSim(world);

        // Windowing turns on for bodies whose horizontal half-extent exceeds the threshold.
        bool ShouldWindow()
        {
            if (!enableLargeBodyWindow) return false;
            Vector3 e = VolumeExtentSafe;
            return Mathf.Max(e.x, e.z) > largeBodyThreshold;
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
            if (Mathf.Abs(denom) < RayParallelEpsilon) return false;
            float t = Vector3.Dot(VolumeCenter - eye, n) / denom;
            if (t < 0f) return false;
            worldHit = eye + dir * t;
            Vector3 pool = WorldToPool(worldHit);
            poolX = pool.x; poolZ = pool.z;
            return true;
        }

        // ---- wind-wave layer -----------------------------------------------
        internal float WaveMetersPerUnit => Mathf.Max(MinWaveMetersPerUnit, poolHalfExtentMeters);

        // Regenerate the bank only when a wind/scale parameter actually changes, so
        // the phases stay stable frame-to-frame (a fresh bank would pop the surface).
        void EnsureWaveBank()
        {
            int count = EffectiveWaveCount;
            float verticalExtent = VolumeExtentSafe.y;
            bool dirty = windWaves != _waveGenEnabled
                         || windSpeed != _waveGenWindSpeed
                         || windFromDegrees != _waveGenWindFrom
                         || poolHalfExtentMeters != _waveGenExtentMeters
                         || count != _waveGenCount
                         || waveAmplitudeScale != _waveGenAmpScale
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            _waveBank.Generate(windSpeed, windFromDegrees, 2f * poolHalfExtentMeters,
                               count, waveAmplitudeScale, waveDirectionSpread, WaveMetersPerUnit,
                               verticalExtent);
            _waveGenWindSpeed = windSpeed;
            _waveGenWindFrom = windFromDegrees;
            _waveGenExtentMeters = poolHalfExtentMeters;
            _waveGenCount = count;
            _waveGenAmpScale = waveAmplitudeScale;
            _waveGenSpread = waveDirectionSpread;
            _waveGenVerticalExtent = verticalExtent;
            _waveGenEnabled = windWaves;
        }

        // The authored component count capped by the quality tier (mobile tiers sum fewer
        // sinusoids per vertex/pixel/buoyancy query).
        int EffectiveWaveCount => Mathf.Min(waveCount, _maxWaveCount);

        // Wave arrays are per-body, mirrored to globals only by the primary (see WriteBodyUniforms).
        // The wave CLOCK (_WaveTime) is genuinely shared and published in PublishSharedGlobals.

        // With the link on, the depth colour tracks the fog extinction so a single dial drives
        // both; off, the depth colour is authored independently.
        internal Color EffectiveDepthExtinction => linkDepthToFog ? fogExtinction : depthExtinction;

        // ---- terrain bed-height bake (WaterBedBaker) --------------------------

        /// <summary>Re-sample the terrain heightmap into the pool-space bed map. Call after
        /// the terrain or the volume placement changes.</summary>
        [ContextMenu("Rebake Bed")]
        public void RebakeBed() => BedBaker.Rebake();

        // ---- edit-mode preview ------------------------------------------------
        // The editor preview driver (Editor/WaterEditorPreviewDriver) pumps the player loop
        // while any body is alive so Update runs without Play; these support it.

        /// <summary>Number of live (enabled) water bodies. Editor-preview driver hook.</summary>
        internal static int ActiveBodyCount => Bodies.Count;

        double _lastEditorTick;

        // Real elapsed time between edit-mode ticks, clamped (see MaxEditorDeltaSeconds).
        // First tick after enable returns 0 so no time is invented.
        float EditorDeltaSeconds()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = _lastEditorTick > 0d ? (float)(now - _lastEditorTick) : 0f;
            _lastEditorTick = now;
            return Mathf.Min(dt, MaxEditorDeltaSeconds);
        }

        // ---- interaction (WaterInputRouter drives this) -----------------------

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
    }
}
