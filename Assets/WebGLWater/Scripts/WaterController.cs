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

namespace WebGLWater
{
    [DefaultExecutionOrder(-50)]
    public class WaterController : MonoBehaviour
    {
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

        [Header("Reflections (Phase 3c)")]
        [Tooltip("How THIS body reflects. SSR (screen-space over the procedural sky) scales to many " +
                 "bodies. Planar is a full extra scene render across this body's plane - use it for at " +
                 "most ONE 'hero' body. SkyOnly is cheapest (procedural sky only). SSR needs Depth + " +
                 "Opaque Texture enabled on the active URP asset.")]
        public ReflectionMode reflectionMode = ReflectionMode.SSR;

        /// <summary>The primary water body: the global fallback for objects without a
        /// <see cref="WaterMembership"/>. Per-object association goes through
        /// <see cref="BodyContaining"/>.</summary>
        public static WaterController Primary { get; private set; }

        /// <summary>Resolve the body an object should use when it isn't inside any specific
        /// one: the primary body, or any found body as a fallback. Prefer
        /// <see cref="BodyContaining"/> for objects that have a world position.</summary>
        public static WaterController Resolve() => Primary != null ? Primary : FindFirstObjectByType<WaterController>();

        /// <summary>The water body a world point belongs to: the body whose horizontal
        /// footprint contains the point, nearest-centre wins when several overlap, and the
        /// primary body as a fallback when the point is outside every footprint. Objects call
        /// this each frame so they float on, and are lit by, the lake they are actually in.</summary>
        public static WaterController BodyContaining(Vector3 worldPoint)
        {
            WaterController best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterController body = Bodies[i];
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
        static readonly List<WaterController> Bodies = new List<WaterController>();

        [Header("Simulation")]
        [Tooltip("Direction TOWARD the light. Auto-driven from 'sun' when one is assigned.")]
        public Vector3 lightDir = new Vector3(2f, 2f, -1f);
        public int causticResolution = 1024;

        [Header("Object interaction")]
        [Tooltip("MASTER strength for how strongly submerged objects displace the water " +
                 "(height units, comparable to Ripple Strength). Per-object weighting is " +
                 "WaterInteractable.displaceScale (leave those at 1 for uniform objects).")]
        [Range(0f, 0.5f)] public float obstacleStrength = 0.08f;
        [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
        public bool obstacleFlipY = false;

        [Header("Water fog (Beer-Lambert)")]
        [Tooltip("Global depth absorption, shared by the surface, objects and pool.")]
        public bool waterFog = false;
        public Color fogColor = new Color(0.10f, 0.30f, 0.40f);
        [Tooltip("Per-channel extinction; red highest so it absorbs first.")]
        public Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [Range(0f, 8f)] public float fogDensity = 2f;

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
        [Tooltip("Foam survival per step. Lower = fades faster.")]
        [Range(0.80f, 1f)] public float foamDecay = 0.97f;
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
        [Range(0.1f, 2.0f)] public float waveSpeed = 2.0f;
        [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
        [Range(0.90f, 1.0f)] public float damping = 0.995f;
        [Tooltip("Simulation sub-steps per frame. More = faster, smoother propagation.")]
        [Range(1, 8)] public int stepsPerFrame = 2;
        [Tooltip("Height added by a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.001f, 0.08f)] public float rippleStrength = 0.01f;
        [Tooltip("Radius of a click/drag ripple (world units; volume-scale independent).")]
        [Range(0.005f, 0.2f)] public float rippleRadius = 0.03f;
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
        bool _waveGenEnabled;

        // CPU copy of the height field for buoyancy queries
        Color[] _heightCpu;
        bool _heightReady, _readbackInFlight;
        int _simRes = WaterQuality.Default.SimResolution; // grid resolution, set from the quality tier at OnEnable
        bool _godRaysAllowed = true;                       // false when the tier turns god rays off
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable, destroyed at OnDisable.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        const string KW_SSR = "_USE_SSR";
        const string KW_PLANAR = "_USE_PLANAR";
        Material _causticMat;
        RenderTexture _causticRT;
        RenderTexture _heightMip;
        CommandBuffer _cb;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        bool _paused;

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
        WaterController _dragBody;  // body being rippled this drag
        bool _forceDrop;

        // shader global ids
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = Shader.PropertyToID("_LightDir");
        static readonly int ID_SunColor = Shader.PropertyToID("_SunColor");
        static readonly int ID_FogColor = Shader.PropertyToID("_WaterFogColor");
        static readonly int ID_FogExt = Shader.PropertyToID("_WaterExtinction");
        static readonly int ID_FogDensity = Shader.PropertyToID("_WaterFogDensity");
        static readonly int ID_FogEnabled = Shader.PropertyToID("_WaterFogEnabled");
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

        void OnEnable()
        {
            if (simCompute == null) { Debug.LogError("WaterController: simCompute not assigned."); enabled = false; return; }

            ApplyQuality();     // sets _simRes, causticResolution, _godRaysAllowed + god-ray steps

            _water = new WaterSimulation(simCompute, _simRes);

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

            // seed the pool with a few ripples
            if (seedRipplesOnStart)
                for (int i = 0; i < SeedRippleCount; i++)
                    _water.AddDrop(Random.value * 2f - 1f, Random.value * 2f - 1f, SeedRippleRadius,
                                   (i & 1) == 1 ? SeedRippleStrength : -SeedRippleStrength);

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

            PublishSharedGlobals();
            EnsureWaveBank();
            UpdateCaustics();
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
            _cb?.Release();
            if (_surfaceAboveInstance != null) { Destroy(_surfaceAboveInstance); _surfaceAboveInstance = null; }
            if (_surfaceUnderInstance != null) { Destroy(_surfaceUnderInstance); _surfaceUnderInstance = null; }
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

            // Clamp to >= 1 so a "god rays off" tier (0 steps) never bakes a divide-by-zero
            // into the shared god-ray material; the renderer is disabled separately via _godRaysAllowed.
            if (godRayRenderer != null && godRayRenderer.sharedMaterial != null)
                godRayRenderer.sharedMaterial.SetFloat(ID_GodRaySteps, Mathf.Max(1, tier.GodRaySteps));
        }

        // Give the surface renderers per-body material instances and set their reflection
        // keywords from reflectionMode, so bodies in different modes don't fight over one
        // shared material. A Planar body also binds the scene's single planar reflection.
        void ApplyReflections()
        {
            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder);
            ApplyReflectionKeywords(_surfaceAboveInstance);
            ApplyReflectionKeywords(_surfaceUnderInstance);

            if (reflectionMode == ReflectionMode.Planar) BindHeroPlanar();
        }

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
            SetKeyword(m, KW_SSR, reflectionMode == ReflectionMode.SSR);
            SetKeyword(m, KW_PLANAR, reflectionMode == ReflectionMode.Planar);
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
            if (_simulate) UpdateCaustics();  // renders THIS body's caustic RT from its own sim

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

        /// <summary>Overwrite <paramref name="mpb"/> with this body's per-renderer uniforms
        /// (sim + caustic textures, volume frame, waves, fog, foam). Used for this body's own
        /// renderers and by <see cref="WaterMembership"/> to light a floating object with the
        /// lake it is in. The block is cleared, so any per-object look must live in the material.</summary>
        public void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            mpb.Clear();

            if (_water != null)
            {
                mpb.SetTexture(ID_Water, _water.Texture);
                if (_water.FoamTexture != null) mpb.SetTexture(ID_FoamMask, _water.FoamTexture);
            }
            if (_causticRT != null) mpb.SetTexture(ID_Caustic, _causticRT);

            mpb.SetVector(ID_VolumeCenter, VolumeCenter);
            mpb.SetVector(ID_VolumeExtent, VolumeExtentSafe);
            mpb.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(VolumeRotation));

            mpb.SetVectorArray(ID_WaveA, _waveBank.PackedA);
            mpb.SetVectorArray(ID_WaveB, _waveBank.PackedB);
            mpb.SetFloat(ID_WaveCount, windWaves ? _waveBank.Count : 0f);
            mpb.SetFloat(ID_WaveMeters, WaveMetersPerUnit);
            mpb.SetFloat(ID_WaveNormal, waveNormalStrength);

            mpb.SetColor(ID_FogColor, fogColor);
            mpb.SetColor(ID_FogExt, fogExtinction);
            mpb.SetFloat(ID_FogDensity, fogDensity);
            mpb.SetFloat(ID_FogEnabled, waterFog ? 1f : 0f);

            mpb.SetColor(ID_FoamColor, foamColor);
            mpb.SetFloat(ID_FoamEnabled, foam ? 1f : 0f);
            mpb.SetFloat(ID_FoamStrength, foamStrength);
            mpb.SetFloat(ID_FoamBorder, foamBorderWidth);
            mpb.SetFloat(ID_FoamContact, foamContactDepth);
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
                WaterController body = Bodies[i];
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
        static bool IsSimEligible(WaterController body, Vector3 camPos)
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
                WaterController body = Bodies[i];
                if (!IsSimEligible(body, camPos)) continue;
                float d = (body.VolumeCenter - camPos).sqrMagnitude;

                int nearer = 0;
                for (int j = 0; j < Bodies.Count; j++)
                {
                    WaterController other = Bodies[j];
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
            // keeps the renderer off even when the body is on-screen.
            SetRendererEnabled(godRayRenderer, on && _godRaysAllowed);
        }

        static void SetRendererEnabled(Renderer r, bool on) { if (r != null && r.enabled != on) r.enabled = on; }

        // Mirror this (primary) body's per-body data to global shader state, so objects and
        // the analytic receivers - which still read globals in Phase 1 - follow this body.
        void PublishBodyGlobals()
        {
            if (_water != null)
            {
                Shader.SetGlobalTexture(ID_Water, _water.Texture);
                if (_water.FoamTexture != null) Shader.SetGlobalTexture(ID_FoamMask, _water.FoamTexture);
            }
            if (_causticRT != null) Shader.SetGlobalTexture(ID_Caustic, _causticRT);
            PublishVolume();
            PublishFog();
            PublishFoam();
            PublishWaves();
        }

        // ---- height readback for buoyancy ----------------------------------
        void RequestHeightReadback()
        {
            if (_readbackInFlight || _water == null) return;
            if (!SystemInfo.supportsAsyncGPUReadback) return; // buoyancy degrades gracefully
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
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return;
            _water?.AddDrop(px, pz, radius / VolumeHorizontalExtent, strength / VolumeExtentSafe.y);
        }

        /// <summary>World-space height (Y) of the water surface above WORLD (x,z).
        /// Returns false until the first readback lands or if outside the footprint.</summary>
        public bool TryGetWaterHeight(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (!_heightReady || _heightCpu == null) return false;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;

            float poolHeight = SamplePoolHeight(px, pz);
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
            if (!_heightReady || _heightCpu == null) return false;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;

            Color sample = SamplePoolTexel(px, pz);
            float poolHeight = sample.r;
            Vector2 poolFlow = new Vector2(sample.b, sample.a); // (normal.x, normal.z)
            if (windWaves)
            {
                poolHeight += _waveBank.SampleHeight(px, pz, _waveTime, WaveMetersPerUnit);
                poolFlow -= _waveBank.SampleSlope(px, pz, _waveTime, WaveMetersPerUnit) * waveNormalStrength;
            }
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
            if (!_heightReady || _heightCpu == null) return false;

            Vector3 pool = WorldToPool(worldPoint);
            if (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f) return false;

            Color sample = SamplePoolTexel(pool.x, pool.z);
            float surfaceH = sample.r;
            Vector2 poolFlow = new Vector2(sample.b, sample.a); // (normal.x, normal.z)
            if (windWaves)
            {
                surfaceH += _waveBank.SampleHeight(pool.x, pool.z, _waveTime, WaveMetersPerUnit);
                poolFlow -= _waveBank.SampleSlope(pool.x, pool.z, _waveTime, WaveMetersPerUnit) * waveNormalStrength;
            }

            depthWorld = (surfaceH - pool.y) * VolumeExtentSafe.y; // pool depth -> world depth along up
            worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            return true;
        }

        // Pool-space height (sim ripple + wind waves) at pool xz in [-1,1].
        float SamplePoolHeight(float poolX, float poolZ)
        {
            float h = SamplePoolTexel(poolX, poolZ).r;
            if (windWaves) h += _waveBank.SampleHeight(poolX, poolZ, _waveTime, WaveMetersPerUnit);
            return h;
        }

        Color SamplePoolTexel(float poolX, float poolZ)
        {
            float u = poolX * 0.5f + 0.5f, v = poolZ * 0.5f + 0.5f;
            int px = Mathf.Clamp((int)(u * _simRes), 0, _simRes - 1);
            int pz = Mathf.Clamp((int)(v * _simRes), 0, _simRes - 1);
            return _heightCpu[pz * _simRes + px];
        }

        void Step(float seconds)
        {
            if (seconds > 1f) return;

            // Push the surface with the live submerged footprint of interactable objects.
            if (_obstacle != null)
            {
                _obstacle.Render(VolumeCenter.y);
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr, obstacleStrength, obstacleFlipY);
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
                _water.StepFoam(foamGenRate, foamDecay, foamSpread, foamFromSpeed, foamFromCurvature, foamAdvect);
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

        void PublishVolume()
        {
            Shader.SetGlobalVector(ID_VolumeCenter, VolumeCenter);
            Shader.SetGlobalVector(ID_VolumeExtent, VolumeExtentSafe);
            Shader.SetGlobalMatrix(ID_VolumeRot, Matrix4x4.Rotate(VolumeRotation));
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
            bool dirty = windWaves != _waveGenEnabled
                         || signature != _waveGenSignature
                         || waveDirectionSpread != _waveGenSpread;
            if (!dirty) return;

            _waveBank.Generate(windSpeed, windFromDegrees, 2f * poolHalfExtentMeters,
                               waveCount, waveAmplitudeScale, waveDirectionSpread, WaveMetersPerUnit);
            _waveGenSignature = signature;
            _waveGenSpread = waveDirectionSpread;
            _waveGenEnabled = windWaves;
        }

        // Wave arrays are per-body (mirrored to globals only by the primary). The wave
        // CLOCK (_WaveTime) is shared and published in PublishSharedGlobals.
        void PublishWaves()
        {
            Shader.SetGlobalVectorArray(ID_WaveA, _waveBank.PackedA);
            Shader.SetGlobalVectorArray(ID_WaveB, _waveBank.PackedB);
            Shader.SetGlobalFloat(ID_WaveCount, windWaves ? _waveBank.Count : 0f);
            Shader.SetGlobalFloat(ID_WaveMeters, WaveMetersPerUnit);
            Shader.SetGlobalFloat(ID_WaveNormal, waveNormalStrength);
        }

        void PublishFog()
        {
            Shader.SetGlobalColor(ID_FogColor, fogColor);
            Shader.SetGlobalColor(ID_FogExt, fogExtinction);
            Shader.SetGlobalFloat(ID_FogDensity, fogDensity);
            Shader.SetGlobalFloat(ID_FogEnabled, waterFog ? 1f : 0f);
        }

        void PublishFoam()
        {
            if (_water != null && _water.FoamTexture != null)
                Shader.SetGlobalTexture(ID_FoamMask, _water.FoamTexture);
            Shader.SetGlobalColor(ID_FoamColor, foamColor);
            Shader.SetGlobalFloat(ID_FoamEnabled, foam ? 1f : 0f);
            Shader.SetGlobalFloat(ID_FoamStrength, foamStrength);
            Shader.SetGlobalFloat(ID_FoamBorder, foamBorderWidth);
            Shader.SetGlobalFloat(ID_FoamContact, foamContactDepth);
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
        static WaterController FindHitBody(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            WaterController best = null;
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
