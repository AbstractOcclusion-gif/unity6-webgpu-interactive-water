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
//   WaterShoreDepthField - terrain -> world-frame seabed-height bake (Layer A shoreline)
//   WaterUniformPublisher- per-body shader uniforms (property block + global mirror)
//   WaterInputRouter     - scene input (primary body only, play mode only)
//   WaterSimScheduler    - static per-frame visibility / sim-budget schedule
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-50)]
    // partial: the editor-only obstacle-footprint PNG dumper lives in WaterVolume.ObstacleDebug.cs
    // so debug instrumentation stays isolated from the runtime body and is trivial to delete.
    public partial class WaterVolume : MonoBehaviour, ISerializationCallbackReceiver
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

        /// <summary>Interactive-ripple detail for a bounded body: sets the sim grid density (texels per
        /// metre + a cap) and matches the surface mesh to it, so higher levels render rounder ripples at
        /// more GPU cost. Windowed oceans are unaffected (they keep the quality-tier resolution).</summary>
        public enum RippleQuality { Low, Medium, High, Ultra }

        /// <summary>Body archetype used by the inspector to show the relevant settings and apply sensible
        /// defaults. Advisory only: it drives the editor UI + the "Apply defaults" action, not the runtime
        /// paths (those still read openWater / unboundedOcean / enableLargeBodyWindow).</summary>
        public enum WaterBodyType { Pond, Lake, Ocean }

        // Serialized configuration surface (wiring fields, Settings blocks + accessors,
        // registry/autolink statics, legacy migrations, LUT bake) -> WaterVolume.Settings.cs.

        // runtime collaborators (see the header comment for the responsibility map)
        //
        // The eagerly-owned collaborators are formalised as IWaterModule lifecycle modules (see
        // WaterCollaboratorModules.cs): the master constructs and disposes them through the module
        // registry instead of by hand. The typed accessors below keep the rest of the class - Update,
        // the sampling/ripple facade, the caustics render - reading them exactly as before.
        SimulationModule _simulationModule;
        ObstacleModule _obstacleModule;
        CausticsModule _causticsModule;
        SurfaceSamplerModule _surfaceSamplerModule;
        OceanFftModule _oceanFftModule;
        SimWindowModule _simWindowModule;
        IWaterModule[] _modules;   // ordered registry over the modules above
        WaterContext _context;     // shared seam handed to the modules at Initialize

        WaterSimulation _water => _simulationModule?.Simulation;
        WaterObstacle _obstacle => _obstacleModule?.Obstacle;
        WaterCausticsPass _caustics => _causticsModule?.Caustics;
        WaterSurfaceSampler _sampler => _surfaceSamplerModule?.Sampler;
        WaterOceanFft _oceanFft => _oceanFftModule?.OceanFft; // ocean-only FFT wave pass; null on pools/bounded bodies
        WaterSimWindow _simWindow => _simWindowModule?.SimWindow;

        // The lazy trio stays as-is: each already uses a clean lazy pattern and serves even an
        // uninitialized body (context-menu rebake, defensive uniform writes), so it is not part of
        // the eager registry.
        WaterBedBaker _bedBaker;
        WaterShoreDepthField _shoreDepth;
        WaterUniformPublisher _publisher;
        WaterInputRouter _inputRouter;

        // Sim-window patch fields -> WaterVolume.SimWindowPatch.cs.
        // Ocean clipmap fields -> WaterVolume.OceanClipmap.cs.

        // Lazy: the bed baker serves the context-menu RebakeBed even on an uninitialized
        // body, and the publisher serves WriteBodyProps callers defensively.
        WaterBedBaker BedBaker => _bedBaker ??= new WaterBedBaker(this);
        WaterShoreDepthField ShoreDepth => _shoreDepth ??= new WaterShoreDepthField(this);
        WaterUniformPublisher Publisher => _publisher ??= new WaterUniformPublisher(this);
        WaterInputRouter InputRouter => _inputRouter ??= new WaterInputRouter(this);

        // Internal collaborator surface (same assembly only).
        internal WaterSimulation Simulation => _water;
        internal WaterWaveBank WaveBank => _waveBank;
        internal float WaveTime => _waveTime;
        internal RenderTexture CausticTexture => _caustics?.Texture;
        // Per-body occluder state for _CausticOccluderActive (see WaterCausticsPass.OccluderChannelValid):
        // 1 = caustic.g is the valid refracted object-shadow channel for this body (may be all-lit).
        internal bool CausticOccluderActive => _caustics != null && _caustics.OccluderChannelValid;
        // Ocean FFT displacement cascade array (null on non-ocean bodies / before init) - for the debug view.
        internal RenderTexture OceanFftTexture => _oceanFft?.DisplacementTexture;
        // True only when this body is an unbounded ocean whose FFT pass is producing cascades. Drives the
        // per-body _OceanFftActive flag so the surface samples the FFT instead of the analytic generator.
        internal bool OceanFftActive => _oceanFft != null && _oceanFft.Ready;
        // Cascade whitecap data for the foam-particle spawn compute (crest foam source).
        internal RenderTexture OceanFftNormalTexture => _oceanFft?.NormalTexture;
        // Spatial displacement cascade for the foam-particle density splat (swell-height glue).
        internal RenderTexture OceanFftSpatialTexture => _oceanFft?.SpatialTexture;
        internal Vector4 OceanFftDomainSizes => _oceanFft != null ? _oceanFft.DomainSizes : Vector4.one;
        internal float OceanFftCascadeCount => _oceanFft != null ? _oceanFft.CascadeCount : 0f;
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
        WaterQuality.UnderwaterMode _underwaterFogMode = WaterQuality.Default.UnderwaterFog;
        /// <summary>Tier cap on the GPU foam-particle pool (WaterFoamParticles clamps to it).</summary>
        internal int FoamParticleBudget => _maxFoamParticles;
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable (play mode only) and destroyed at
        // OnDisable, which also restores the renderer's original shared material so an
        // enable/disable cycle never leaves a renderer pointing at a destroyed instance.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        Material _surfaceAboveOriginal, _surfaceUnderOriginal;
        // Low-tier coarse grid swapped onto the surface renderers at init (play mode only);
        // the originals are restored on disable, mirroring the material-instance pattern.
        Mesh _lowDetailGrid;
        Mesh _surfaceAboveOriginalMesh, _surfaceUnderOriginalMesh;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        // Round (disc) surface footprint for a CHUNK body: ApplyMeshDetail rebuilds the play-mode
        // surface as a disc instead of the square grid, so a sphere/round chunk reads circular.
        // Default false = the square footprint every existing body uses. Serialized so it survives
        // play mode / domain reload (ApplyMeshDetail runs from the serialized state).
        [SerializeField, HideInInspector] internal bool discSurface;
        const int DiscSurfaceMinSegments = 24; // angular floor so a low sim res still reads round

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

        // Interactive-ripple density (bounded bodies): the ripple sim is a grid stretched over the
        // footprint, so a fixed resolution blurs as the plane grows (fine at ~5 m, coarse by ~40 m).
        // Scale the grid with the footprint at a per-quality texel density, clamped between a per-quality
        // floor and cap. The floor keeps SMALL pools dense (High/Ultra hold the pre-scaling 256 grid so a
        // small pool stays crisp); the cap bounds the cost on big planes. Both are multiples of the
        // compute thread-group size. The surface mesh is matched to the result (see SurfaceMeshDetail)
        // so displaced ripples are round.
        readonly struct RippleQualitySetting
        {
            public readonly float TexelsPerMeter;
            public readonly int MinResolution; // small-pool floor; multiple of WaterSimulation.ThreadGroupSize
            public readonly int MaxResolution; // big-plane cap; multiple of WaterSimulation.ThreadGroupSize

            public RippleQualitySetting(float texelsPerMeter, int minResolution, int maxResolution)
            {
                TexelsPerMeter = texelsPerMeter;
                MinResolution = minResolution;
                MaxResolution = maxResolution;
            }
        }

        static readonly System.Collections.Generic.Dictionary<RippleQuality, RippleQualitySetting> RippleQualityTable =
            new System.Collections.Generic.Dictionary<RippleQuality, RippleQualitySetting>
            {
                { RippleQuality.Low,    new RippleQualitySetting(8f, 128, 192) },
                { RippleQuality.Medium, new RippleQualitySetting(12f, 192, 256) },
                { RippleQuality.High,   new RippleQualitySetting(16f, 256, 320) },
                { RippleQuality.Ultra,  new RippleQualitySetting(24f, 256, 384) },
            };

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
        internal const float MinBedFadeDepth = 0.01f; // keeps the bed depth scale finite (publisher)
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
            // Refresh the underwater fog gate at RENDER time (see OnBeginCameraRender), not in
            // Update, so it can't lag the camera by a frame on entry.
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRender;
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

            _lastEditorTick = 0d;
            _stepDebt = 0f;
            _foamTimeDebt = 0f;
            _windowed = ShouldWindow(); // decided once; volumeExtent is fixed before Play

            // Bounded bodies: set the grid resolution from the footprint + ripple quality so ripple
            // detail holds at scale. A windowed body already keeps constant density via its fixed-size
            // scrolling window, so it keeps the quality-tier resolution.
            if (!_windowed)
                _simRes = ResolveDensitySimResolution();
            // With _windowed and the final _simRes known, measure how far the grid falls short of the
            // tier's texels-per-metre (1 = no shortfall; drives the scale-invariance corrections).
            ResolveSimDensityRatio();

            // Construct the eagerly-owned collaborators through the module registry. Ordered here (after
            // _windowed, which the ocean-FFT module gates on; before ApplySimAnisotropy, which needs the
            // simulation to already exist) so the sequence and the Enabled gates match the former inline
            // construction byte-for-byte.
            BuildAndInitializeModules();

            ApplySimAnisotropy();       // round ripples on a rectangular pool (no-op for square/windowed)
#if UNITY_EDITOR
            WarnIfLargeBody();           // editor-only heads-up: large bodies are experimental in this POC
            WarnIfExperimentalTerrain(); // editor-only heads-up: terrain bed-depth is experimental
#endif

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
                // An unbounded ocean's clipmap reaches ClipmapOuterReach (the outermost LOD level); the
                // 100 m pool far-plane would clip the horizon surface (and the fog that fills it), which
                // reads as fog "popping" out there. Bounded bodies keep the pool default.
                targetCamera.farClipPlane = IsOceanClipmap ? ClipmapOuterReach : CameraFarClip;
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
            AssignSurfaceLayers(); // water on the "Water" layer so the planar reflection excludes it
            ApplyReflections();
            ApplyMeshDetail();   // Low tier: coarse surface grid (play mode only)
            ApplyPipelineTier(); // Low tier: render scale / opaque-copy release (primary, play mode only)
            CreateSimWindowPatch(); // windowed bodies: dense near-field surface over the sim window
            CreateOceanClipmap();   // unbounded-ocean bodies: horizon-reaching camera-following surface

            BedBaker.EnsureBaked(); // lazy terrain -> pool-space bed bake, only when useBedDepth is on
            ShoreDepth.EnsureBakedAndPublish(); // Layer A: world-frame seabed field + publish globals

            Publisher.PublishSharedGlobals();
            EnsureWaveBank();
            if (_windowed) _simWindow.Track();  // prime the window centre before first publish
            RenderCausticsForThisBody();        // pool caustic (bounded), or the window-frame ocean caustic
            ApplyBodyBlock();
            if (isPrimary) Publisher.PublishBodyGlobals();

            _initialized = true;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRender;
            DestroySurfCrestFoamLut(); // FOAM-1 LUT is lazy-baked, so it may exist pre-init too
            if (!_initialized) return; // never initialized (missing wiring / capability guard)

            _initialized = false;
            if (Primary == this) Primary = FindNextPrimary(this);
            Bodies.Remove(this);
            // Last body out (scene teardown / File > New Scene): the static fog gate and the
            // underwater globals it mirrors are only ever WRITTEN by a live primary body, so
            // without this reset they keep the LAST scene's values - and the fullscreen
            // WaterUnderwaterFogFeature (which lives on the URP renderer asset, active in
            // every scene) keeps enqueueing on the stale gate and paints the dead scene's
            // water fog into the new one. When other bodies remain, the next primary
            // republishes on its next frame, so no reset is needed.
            if (Bodies.Count == 0)
            {
                UnderwaterFogActive = false;
                WaterlineActive = false; // same static-gate pattern: the meniscus pass reads it too
                Publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f);
            }
            DisposeModules();      // disposes the six eager collaborator modules (sim, obstacle, caustics,
                                   // surface sampler, ocean FFT, sim window) - releases the same GPU
                                   // resources the inline disposal did, and clears the sampler/window refs.
            _bedBaker?.Dispose();  // also re-arms the lazy bake gate for the next enable
            _shoreDepth?.Dispose(); // Layer A field; re-arms its own lazy bake gate too
            DestroySimWindowPatch(); // before restoring the surface material it borrows
            DestroyOceanClipmap();   // ditto - it borrows the same surface material
            _planarMirror?.Dispose(); // frees this body's planar mirror camera + RT
            _planarMirror = null;
            RestoreSurfaceMaterial(surfaceAbove, ref _surfaceAboveInstance, ref _surfaceAboveOriginal);
            RestoreSurfaceMaterial(surfaceUnder, ref _surfaceUnderInstance, ref _surfaceUnderOriginal);
            RestoreMeshDetail();
            RestorePipelineTier();
            // Fresh per-enable state: a re-enable must not float objects on a stale height
            // field, and the window centre re-primes from the camera. (The sampler and sim-window
            // refs are cleared by DisposeModules above; the lazy input router is cleared here.)
            _inputRouter = null;
        }

        // Build the ordered collaborator registry for this enable and initialize each enabled module.
        // Order mirrors the original construction sequence (sim, sampler, sim window, obstacle, caustics,
        // ocean FFT); the context is the shared seam the modules will read from as their per-frame tick
        // moves onto IWaterModule.
        void BuildAndInitializeModules()
        {
            _context = new WaterContext(this);
            _simulationModule = new SimulationModule(this);
            _surfaceSamplerModule = new SurfaceSamplerModule(this);
            _simWindowModule = new SimWindowModule(this);
            _obstacleModule = new ObstacleModule(this);
            _causticsModule = new CausticsModule(this);
            _oceanFftModule = new OceanFftModule(this);
            _modules = new IWaterModule[]
            {
                _simulationModule, _surfaceSamplerModule, _simWindowModule,
                _obstacleModule, _causticsModule, _oceanFftModule,
            };

            for (int i = 0; i < _modules.Length; i++)
                if (_modules[i].Enabled) _modules[i].Initialize(_context);
        }

        // Dispose every collaborator module. Safe on modules that were disabled or never initialized.
        void DisposeModules()
        {
            if (_modules == null) return;
            for (int i = 0; i < _modules.Length; i++) _modules[i].Dispose();
        }

        // ---- Low-tier surface grid swap ----------------------------------------
        // The authored grid is 200x200 and the vertex shader runs 4 fetches + the wave sines
        // per vertex; a 128 sim doesn't need that tessellation. Play mode only (an edit-mode
        // swap could serialize the runtime mesh reference into the scene), mirroring the
        // material-instance pattern: originals restored on disable.
        void ApplyMeshDetail()
        {
            if (!Application.isPlaying) return;

            int detail = SurfaceMeshDetail();
            if (detail <= 0) return; // keep the authored mesh

            _lowDetailGrid = discSurface
                ? WaterMeshBuilder.BuildDisc(detail, Mathf.Max(detail, DiscSurfaceMinSegments))
                : WaterMeshBuilder.BuildGrid(detail);
            _lowDetailGrid.hideFlags = HideFlags.HideAndDontSave;
            SwapRendererMesh(surfaceAbove, _lowDetailGrid, ref _surfaceAboveOriginalMesh);
            SwapRendererMesh(surfaceUnder, _lowDetailGrid, ref _surfaceUnderOriginalMesh);
        }

        // Bounded bodies match the surface grid to the sim grid (one vertex per texel) so displaced
        // ripples are round rather than faceted triangles; the vertex count follows the ripple quality.
        // Windowed bodies keep the tier's mesh-detail override (their dense near-field is the separate
        // sim-window patch, so their main plane needs no matching).
        int SurfaceMeshDetail() => _windowed ? _meshDetail : _simRes;

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
            // splashEmitter is resolved lazily on first impact (ResolveSplashEmitter), not eagerly here,
            // so a body that never splashes never searches the scene or creates an emitter.
        }

        // Name of the emitter auto-created when a body must supply splashes but none is authored.
        const string AutoSplashEmitterName = "Splash Emitter (auto)";

        /// <summary>The splash emitter this body routes impacts through - resolved lazily and cached:
        /// an assigned emitter, one already under the body, any emitter in the scene (back-compat with
        /// a single rigged emitter), or a droplet-only emitter created on the body on demand. Returns
        /// null when the body opts out of splashes (<see cref="provideSplashEmitter"/> off), so triggers
        /// over it stay silent.</summary>
        internal WaterSplashEmitter ResolveSplashEmitter()
        {
            if (!provideSplashEmitter) return null;
            if (splashEmitter != null) return splashEmitter;

            splashEmitter = GetComponentInChildren<WaterSplashEmitter>();
            if (splashEmitter != null) return splashEmitter;

            splashEmitter = FindFirstObjectByType<WaterSplashEmitter>();
            if (splashEmitter != null) return splashEmitter;

            if (!Application.isPlaying) return null; // never spawn content into a scene being edited
            return splashEmitter = CreateOwnedSplashEmitter();
        }

        // A droplet-only emitter parented to this body. WaterSplashEmitter.Awake builds a drift
        // ParticleSystem with no editor assets; the crown flipbook is an editor-only asset, so an
        // auto-created emitter has no crown - droplets still fire (GPU-routed when the body has a
        // WaterFoamParticles). The authored wizard emitter is the path that carries the crown.
        WaterSplashEmitter CreateOwnedSplashEmitter()
        {
            var host = new GameObject(AutoSplashEmitterName);
            host.transform.SetParent(transform, worldPositionStays: false);
            return host.AddComponent<WaterSplashEmitter>();
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
            // Runtime field, NOT the serialized causticResolution: ApplyQuality also runs in edit
            // mode (TryInitialize under [ExecuteAlways]), and writing the serialized field baked the
            // device-probed tier value into authored scene data on save. Every other tier knob
            // already uses a '_' runtime field; this one was the odd one out.
            _causticRes = tier.CausticResolution;
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
            _underwaterFogMode = tier.UnderwaterFog;

            // One line per enable so a build's console shows exactly which knobs landed -
            // tier mismatches (stale build cache, wrong asset, missing serialized fields)
            // are otherwise near-impossible to diagnose on a device.
            Debug.Log($"WaterVolume '{name}': quality tier applied - sim {_simRes}, caustics {EffectiveCausticResolution}, " +
                      $"mesh {(_meshDetail > 0 ? _meshDetail.ToString() : "authored")}, renderScale {_renderScale:0.##}, " +
                      $"realRefraction {_realRefractionAllowed}, godRays {_godRaysAllowed} ({_godRaySteps} steps), " +
                      $"waves {_maxWaveCount}, refine {_peakedRefineSteps}, foamCap {_maxFoamParticles}, " +
                      $"underwaterFog {_underwaterFogMode}", this);
        }

        // Scale the interactive-sim grid to the body's footprint at the chosen ripple quality so
        // world-metres-per-texel stays roughly constant, keeping ripples crisp on larger planes. Rounded
        // up to the compute thread-group size (the sim requires a multiple), then clamped to the
        // quality's floor/cap.
        int ResolveDensitySimResolution()
        {
            RippleQualitySetting setting = RippleQualityTable[rippleQuality];
            float fullWidth = 2f * Mathf.Max(VolumeExtentSafe.x, VolumeExtentSafe.z);
            int group = WaterSimulation.ThreadGroupSize;
            int target = Mathf.CeilToInt(fullWidth * setting.TexelsPerMeter);
            target = Mathf.CeilToInt(target / (float)group) * group;
            return Mathf.Clamp(target, setting.MinResolution, setting.MaxResolution);
        }

        // ---- Scale-invariant ripples on cap-limited grids --------------------------------------------
        // How coarse the sim grid actually is versus the tier's authored texels-per-metre: 1 while the
        // grid holds tier density (every body below the resolution cap - their look is untouched), < 1
        // once the cap forces metres-per-texel to grow (bounded bodies wider than cap/texelsPerMeter,
        // and windowed bodies whose window outgrows the tier resolution). Feeds three corrections that
        // are all identity at 1: wave-speed dispersion, damping-per-world-metre, and drop-floor energy.
        // Without them the integrator's fixed texel-space units make world propagation speed, energy
        // persistence and injected footprints all drift with extent - the "harsh above 5 m, intensity
        // needs re-tweaking per size" complaint.
        float _simDensityRatio = 1f;

        void ResolveSimDensityRatio()
        {
            RippleQualitySetting setting = RippleQualityTable[rippleQuality];
            float fullWidth = 2f * (_windowed ? SimHorizontalExtent
                                              : Mathf.Max(VolumeExtentSafe.x, VolumeExtentSafe.z));
            float actualTexelsPerMeter = _simRes / Mathf.Max(fullWidth, MinVolumeExtent);
            // Never > 1: a small body clamped UP to the tier's minimum resolution is denser than
            // authored, which needs no correction (and boosting wave speed there would break CFL).
            _simDensityRatio = Mathf.Min(1f, actualTexelsPerMeter / setting.TexelsPerMeter);
        }

        // NOTE on the drop footprint floor: the sim floors every drop to MinDropTexelRadius texels,
        // which is physically wider on a cap-limited grid. Strength compensation for that widening
        // was tried in two flavours (volume-conserving ratio^2, then linear width ratio) and BOTH
        // rejected: any peak reduction reads as "ripples are weaker on big ponds" - incoherent.
        // With the wave speed and damping corrections above keeping the DYNAMICS world-consistent,
        // an uncompensated equal world PEAK (guaranteed by the strength / extent.y division in
        // AddRipple) is what actually looks coherent across sizes; only the bump footprint widens.

        // Give the surface renderers per-body material instances and set their reflection
        // keywords + look floats from the tier-capped toggles, so bodies with different reflection
        // settings don't fight over one shared material. A planar body also binds the scene's
        // single planar reflection.
        void ApplyReflections()
        {
            // Play-mode only: an instance assigned to sharedMaterial in edit mode could be saved
            // into the scene as a dead reference. Reflection is uniform-driven and published every
            // frame by WaterUniformPublisher (edit + play), so no keywords are baked here.
            if (!Application.isPlaying) return;

            // Per-body material instances so the ocean clipmap / patch renderers and the low-tier
            // mesh swap share this body's surface material.
            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove, out _surfaceAboveOriginal);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder, out _surfaceUnderOriginal);

            // Planar reflection is self-driven per body now (see RenderPlanarMirror in OnBeginCameraRender);
            // no hero binding here.
        }

        // Put water surfaces on the built-in "Water" layer so the planar reflection - configured to
        // exclude that layer - never mirrors the water into itself (which reads as a second, independently
        // waving surface). The scene camera still renders the layer, so the water itself is unaffected.
        const string WaterLayerName = "Water";

        void AssignSurfaceLayers()
        {
            ApplyWaterLayer(surfaceAbove);
            ApplyWaterLayer(surfaceUnder);
        }

        static void ApplyWaterLayer(Renderer r)
        {
            if (r != null) ApplyWaterLayer(r.gameObject);
        }

        static void ApplyWaterLayer(GameObject go)
        {
            int layer = LayerMask.NameToLayer(WaterLayerName);
            if (go != null && layer >= 0 && go.layer != layer) go.layer = layer;
        }

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
            dt *= Mathf.Max(0f, timeScale); // per-body master animation speed: scales the wave clock + ripple step (surface only)
            if (!_paused)
            {
                // The analytic wind waves are driven by the shared clock, so they keep moving
                // even on a budget-paused (but visible) body; only the GPU sim is gated.
                _waveTime += dt;
                if (_simulate) Step(dt);
            }

            Publisher.PublishSharedGlobals(); // sun, ambient, tiles (the wave clock is per body)
            EnsureWaveBank();
            BedBaker.EnsureBaked();           // picks up useBedDepth being toggled on at runtime
            ShoreDepth.EnsureBakedAndPublish(); // Layer A: keep the seabed field + globals live
            // Bounded bodies render the pool caustic; the windowed OCEAN renders the large-body caustic
            // in the sim-window's world frame (other windowed bodies still skip - see RenderCausticsForThisBody).
            // The tier can amortise the pass over N frames (the caustic RT simply holds).
            // Ocean FFT cascades refresh on the shared wave clock (NOT gated on _simulate: like the analytic
            // large waves they must animate whenever the body is live, or the surface would sample stale
            // cascades and render differently in edit vs play, where _simulate follows game-camera culling).
            // The surface only reads them when _OceanFftActive is published, so this stays ocean-only.
            if (IsOceanClipmap && !_paused)
            {
                Vector2 camXZ = targetCamera != null
                    ? new Vector2(targetCamera.transform.position.x, targetCamera.transform.position.z)
                    : new Vector2(VolumeCenter.x, VolumeCenter.z);
                // Deposit knob maps to the compute's slow-fade fraction inverted (more deposit = slower dense
                // fade). Drift and max buildup pass straight through.
                var foam = new WaterOceanFft.FoamParams(OceanFoamWindThreshold, OceanFoamCoverage,
                                                        OceanFoamStrength, OceanFoamFadeRate,
                                                        1f - OceanFoamDeposit, OceanFoamDrift, OceanFoamMaxBuildup);
                _oceanFft?.Dispatch(_waveTime, windSpeed, LargeWaveHeadingRad, LargeWaveAmplitudeEffective,
                                    SwellWavelength, SwellHeight, camXZ, foam);
            }
            if (_simulate && Time.frameCount % _causticInterval == 0)
                RenderCausticsForThisBody();

            ApplyBodyBlock();           // per-body uniforms -> this body's renderers (MPB)
            // Primary bridge: mirror this body's data to globals as the fallback for objects
            // without a WaterMembership (those resolve their own containing body instead).
            if (isPrimary) Publisher.PublishBodyGlobals();
            // The camera-submerged fog gate is refreshed in OnBeginCameraRender, NOT here: this body
            // updates at DefaultExecutionOrder -50, before the OrbitCamera moves the camera in
            // LateUpdate, so an Update-time read used the pre-move position and lagged the fog one
            // frame on entry (out->in). beginCameraRendering runs after LateUpdate, just before the
            // fog feature's AddRenderPasses, so the gate is current the same frame the camera crosses.

            // Tier-amortised readback: buoyancy already tolerates async latency, so weak
            // devices can trade a few frames of it for GPU->CPU bandwidth.
            if (_simulate && Time.frameCount % _readbackInterval == 0)
            {
                _sampler.RequestReadback();  // paused bodies keep their last height (objects still float)
                if (IsOceanClipmap) _oceanFft?.RequestHeightReadback(); // FFT swell height for buoyancy
            }
        }

        // Per-body uniforms pushed to THIS body's own renderers via a property block, so
        // multiple water bodies never fight over global state.
        void ApplyBodyBlock()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            WriteBodyProps(_mpb);
            SetChunkSurfaceProps(_mpb); // _ChunkSphereClip for the disc surface (before it receives the block)

            ApplyBlockTo(surfaceAbove);
            ApplyBlockTo(surfaceUnder);
            ApplyBlockTo(poolRenderer);
            ApplyBlockTo(godRayRenderer);
            ApplyPatchBlock();
            ApplyClipmapBlock();
            ApplyChunkShellBlock(_mpb); // chunk shell reuses this body's block (frame + waves + fog)
        }

        // Sim-window patch build/placement/teardown -> WaterVolume.SimWindowPatch.cs.

        // Ocean clipmap build/placement/teardown -> WaterVolume.OceanClipmap.cs.

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
            // An unbounded ocean follows the camera and is drawn everywhere, so it must never be
            // frustum-culled by its (small) footprint - that is what made the horizon surface vanish
            // once the camera left the volume bounds. Report effectively-infinite bounds instead.
            if (IsOceanClipmap)
                return new Bounds(VolumeCenter, Vector3.one * OceanCullBoundsSize);

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
            // An ocean body draws the horizon-reaching clipmaps INSTEAD of the bounded surface planes,
            // so the two never double-draw (z-fight). Above and under each have their own twin; the
            // clipmaps only exist in play mode, so gate on their ACTUAL presence - otherwise edit mode
            // hides a plane with nothing to replace it (the surface looks cut).
            bool clipmapActive = _clipmapLevels != null;
            bool underClipmapActive = clipmapActive && _clipmapLevels.Length > 0 && _clipmapLevels[0].under != null;
            SetRendererEnabled(surfaceAbove, on && !clipmapActive);
            SetRendererEnabled(surfaceUnder, on && !underClipmapActive);
            SetRendererEnabled(poolRenderer, on);
            SetRendererEnabled(_patchRenderer, on && _windowed);
            SetRendererEnabled(_patchUnderRenderer, on && IsOceanClipmap);
            SetClipmapRenderersEnabled(on && IsOceanClipmap);
            // God rays obey the quality tier as well as culling: a tier that disables them
            // keeps the renderer off even when the body is on-screen. Windowed bodies also
            // suppress god rays (out of scope, same reason as caustics). A CHUNK draws its own
            // shafts inside the shell wall (shaped to its primitive + fill), so the pool god-ray
            // box is suppressed for chunks to avoid double, unshaped shafts.
            SetRendererEnabled(godRayRenderer, on && _godRaysAllowed && !_windowed && !IsChunk);
            SetChunkShellEnabled(on);
        }

        static void SetRendererEnabled(Renderer r, bool on) { if (r != null && r.enabled != on) r.enabled = on; }

        // Public gameplay facade (ripples, height/submersion queries) -> WaterVolume.Facade.cs.

        // Shore-transform + surf-front context for the CPU wave mirror: the SAME knobs the shaders
        // read as globals, plus the baked field's CPU copies (WaterShoreDepthField). Inactive (all
        // zero, null field) when the shore substrate isn't live, so open water is byte-identical.
        internal ShoreWaveContext ShoreWaveCtx
        {
            get
            {
                WaterShoreDepthField shore = ShoreDepth;
                if (!useBedDepth || !shore.DepthBaked) return ShoreWaveContext.Inactive;
                ShoreWaveContext ctx = default;
                ctx.Field = shore;
                ctx.ShoalDepth = shoreShoalDepth;
                ctx.Refraction = shoreRefraction;
                ctx.Compression = shoreCompression;
                ctx.Greens = shoreGreens;
                ctx.SurfActive = shore.SurfLayerActive;
                ctx.SurfAmplitude = SurfAmplitudeEffective;
                ctx.SurfWavelength = SurfWavelengthEffective;
                ctx.SurfPeriod = surfPeriod;
                ctx.SurfBeatTime = SurfBeatTime;
                ctx.SurfBandDepth = surfBandDepth;
                ctx.SurfSetStrength = surfSetStrength;
                ctx.SurfCrestLength = surfCrestLength;
                ctx.SurfCrestVariation = surfCrestVariation;
                ctx.SurfCrestPersistence = surfCrestPersistence;
                ctx.SurfDirectionality = surfDirectionality;
                ctx.SurfWindDirX = Mathf.Cos(LargeWaveHeadingRad);
                ctx.SurfWindDirZ = Mathf.Sin(LargeWaveHeadingRad);
                ctx.SurfLean = surfLean;
                ctx.SurfAmbientFade = surfAmbientFade;
                return ctx;
            }
        }

        // Large-body wave field (height, dHeight/dx, dHeight/dz) at a world xz. Prefers the FFT ocean's
        // async height-field readback (so floaters ride the exact rendered swell) and falls back to the
        // analytic CPU mirror before the first readback lands or on non-FFT bodies - matching the shader's
        // own gated fallback in WaterLargeWaves.hlsl.
        Vector3 SampleLargeWaveField(float worldX, float worldZ)
        {
            // Edge guard on height AND slope, mirroring the shader's composition points: near the
            // footprint border the rendered surface feathers flat, so buoyancy must too.
            float edge = LargeWaveEdgeWeight(worldX, worldZ);
            // The FFT readback bakes the RAW cascades; the shader's FFT branch additionally shoals
            // them by depth, fades them under the surf fronts and adds the fronts on top - so the
            // readback sample gets the same treatment (mirror of LargeBodyWaveHeight's FFT path).
            if (OceanFftActive && _oceanFft.TrySampleField(worldX, worldZ, out Vector3 fft))
                return LargeWaveField.ApplyShoreToFftSample(fft, worldX, worldZ, _waveTime,
                    SwellWavelength, ShoreWaveCtx) * edge;
            return LargeWaveField.EvaluateAtQuery(worldX, worldZ, _waveTime, LargeWaveAmplitudeEffective,
                LargeWaveHeadingRad, SwellWavelength, SwellHeight, LargeWaveChoppiness, ShoreWaveCtx)
                * edge;
        }

        // Static-reflection tuning (fixed for v1; promote to per-body settings if scene tuning is needed).
        // Threshold is in the solid mask's coverage units (submerged thickness, world); a low floor just
        // rejects faint silhouette edges. Rest dip is a world depression shown under a reflector, 0 = flat.
        const float ObstacleReflectSolidThreshold = 0.02f;
        const float ObstacleReflectRestDip = 0f;

        // True when at least one enabled interactable is flagged as a wave reflector. The solid mask clips
        // to this body's frame, so a reflector living in another body contributes nothing here.
        static bool AnyReflectorActive()
        {
            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                WaterInteractable it = list[i];
                if (it != null && it.reflectsWaves && it.isActiveAndEnabled) return true;
            }
            return false;
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
                _obstacle.Render(VolumeCenter.y);
                // Temporal EMA (compute): Curr = lerp(Prev, Raw, blend). blend = 1 - obstacleSmoothing,
                // so smoothing 0 = no low-pass (Curr = Raw), higher = heavier anti-flicker smoothing.
                _water.SmoothObstacleFootprint(_obstacle.Prev, _obstacle.Raw, _obstacle.Curr,
                                               1f - obstacleSmoothing);
                // Compensate for extent.y so an object's displacement is a fixed world height
                // regardless of pool depth (PoolToWorld scales surface height by extent.y).
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr,
                                     obstacleStrength / VolumeExtentSafe.y, obstacleFlipY,
                                     obstacleDeadband);
            }

            // Static reflection (opt-in per WaterInteractable.reflectsWaves, independent of the emission
            // mode above): build a solid mask from the reflector objects and feed it to the Update kernel
            // so ripples bounce off them. No reflectors -> a null mask, so the sim stays byte-identical.
            bool anyReflector = _obstacle != null && AnyReflectorActive();
            if (anyReflector)
            {
                if (_windowed) _obstacle.SetFrame(SimWindowCenter, VolumeRotation, SimHalfExtent);
                _obstacle.RenderSolid(VolumeCenter.y);
            }
            _water.SetObstacleReflection(
                anyReflector ? _obstacle.Solid : null, anyReflector,
                ObstacleReflectSolidThreshold, ObstacleReflectRestDip / VolumeExtentSafe.y, obstacleFlipY);

            // Shoreline (bed depth): couple the baked terrain bed into the sim so dry land holds flat
            // (ripples reflect off the waterline) and the open-shore boundary drains. Bounded bodies
            // only - a windowed ocean's sim is a world-space scrolling window, not the pool frame the
            // bed is baked in.
            bool bedActive = !_windowed && useBedDepth && IsBedBaked;
            _water.SetBedDepth(bedActive ? BedTexture : null, bedActive);

            // Scale-invariance for cap-limited grids (identity at density ratio 1, i.e. every body
            // whose grid holds the tier's texels-per-metre - small bodies are byte-identical):
            //  - WAVE SPEED: the integrator propagates a fixed ~sqrt(waveSpeed) TEXELS per step, so
            //    once metres-per-texel grows, world speed grows linearly with it (a 40 m pool ran
            //    ~6-8x faster than a 5 m pool - the frantic, harsh look). Physically a coarse grid
            //    resolves only longer wavelengths, whose speed grows like sqrt(metres-per-texel)
            //    (Crest: c = sqrt(g * 2*texel / 2pi) per LOD slice). Scaling the texel-space speed
            //    by the density ratio lands exactly on c_world ∝ sqrt(metres-per-texel).
            //  - DAMPING: authored per STEP; a coarse grid crosses 1/sqrt(ratio) more world-metres
            //    per step (after the speed fix), so re-base the survival exponent to keep the
            //    attenuation PER WORLD METRE constant - big pools stop ringing with leftover energy.
            float effectiveWaveSpeed = waveSpeed * _simDensityRatio;
            float effectiveDamping = (_simDensityRatio < 1f)
                ? Mathf.Pow(damping, 1f / Mathf.Sqrt(_simDensityRatio))
                : damping;
            for (int i = 0; i < steps; i++)
                _water.StepSimulation(effectiveWaveSpeed, effectiveDamping);

            // Exact GPU-reduced mean (no more Blit + GenerateMips: the float-mip mean silently
            // point-sampled in WebGPU builds and popped the plane; see WaterSim.compute). Skipped on
            // shoreline bodies: the open-shore boundary drain handles the edge, and averaging in the
            // zeroed dry cells would bias the "mean" and slowly sink the wet surface.
            if (conserveVolume && !bedActive) _water.ConserveVolume(conserveMaxCorrection);

            _water.UpdateNormals();

            // Wake foam (move #3): push the stamp gain to the sim so the next interactor dispatches
            // deposit foam at the hull. Zeroed when foam is off, so interactions stay copy-through.
            _water.SetWakeFoam(foam ? foamWakeStrength : 0f, foamWakeRadiusScale);

            if (foam)
            {
                // Bi-exponential contract: thin residual lace must SURVIVE LONGER than
                // thick fresh foam (residual >= fresh), or the blend inverts and foam
                // pops off as hard-edged blobs. Scene data can't be trusted to keep the
                // ordering (the sliders' ranges overlap), so enforce it here.
                float residualSurvival = Mathf.Max(foamDecayResidual, foamDecay);
                // Scale-invariant foam ACTIVITY on cap-limited grids: the wave-speed correction
                // above legitimately shrinks per-step pool velocities by the density ratio, which
                // would sink the sim's speed/shear/curvature readings toward zero on mid/large
                // bodies - the gen threshold could no longer tell a real ripple from noise, and
                // the response knobs would need re-tuning per size. Boosting the response gains
                // by 1/ratio restores the activity magnitude the knobs and threshold were
                // authored against. Identity at ratio 1 (small bodies unchanged).
                float foamActivityScale = 1f / Mathf.Max(_simDensityRatio, 0.05f);
                // Min wave height AND the shallow-breaking range are authored in WORLD metres; the
                // sim's heights and bed column depths are pool units, so both divide by the extent.
                PushShoreFoam(_water);    // surf-front whitewash source (inert without the surf layer)
                _water.StepFoam(foamGenRate, foamGenThreshold,
                                foamMinWaveHeight / VolumeExtentSafe.y, foamDecay,
                                residualSurvival, foamSpread, foamFromSpeed * foamActivityScale,
                                foamFromCurvature * foamActivityScale, foamAdvect,
                                _foamTimeDebt, foamDecayRate,
                                foamBreakStrength, foamBreakRange / VolumeExtentSafe.y,
                                foamCrestBias, foamDeposit);
                _foamTimeDebt = 0f;
            }
        }

        /// <summary>Push this frame's surf-front foam source to the ripple sim: the Layer A field
        /// textures + frame, the sim-uv -> world-xz affine (same shape as the hero wave's), and the
        /// front-field values the surface renders with - so the injected foam lands exactly where
        /// the eye sees the fronts break. Inert unless the surf layer is live on this body.</summary>
        void PushShoreFoam(WaterSimulation sim)
        {
            if (sim == null) return;
            sim.SetShoreFoam(BuildShoreFoamState());
        }

        /// <summary>The surf-front foam source state: the SAME front-field values the surface
        /// renders with, packaged for compute consumers (ripple-sim foam injection, foam-particle
        /// lip spray) via ShoreFoamState.BindTo. Inactive unless the surf layer is live here.</summary>
        internal WaterSimulation.ShoreFoamState BuildShoreFoamState()
        {
            WaterShoreDepthField shore = ShoreDepth;
            var state = new WaterSimulation.ShoreFoamState();
            state.Active = shore.SurfLayerActive
                           && surfFoamGain + surfWaterlineFoam + surfSwashDepositGain > 0f;
            if (state.Active)
            {
                // The sim domain is the scrolling window on windowed bodies, the whole footprint
                // otherwise - the SAME frames the render side uses.
                Vector3 domainCenter = IsWindowed ? SimWindowCenter : VolumeCenter;
                Vector3 domainExtent = IsWindowed ? SimHalfExtent : VolumeExtentSafe;
                Quaternion rotation = VolumeRotation;
                Vector3 uvOrigin = domainCenter + rotation * new Vector3(-domainExtent.x, 0f, -domainExtent.z);
                Vector3 uvAxisX = rotation * new Vector3(2f * domainExtent.x, 0f, 0f);
                Vector3 uvAxisZ = rotation * new Vector3(0f, 0f, 2f * domainExtent.z);
                state.DepthTex = shore.DepthTexture;
                state.SdfTex = shore.SdfTexture;
                state.FieldCenter = new Vector4(shore.FieldCenter.x, shore.FieldCenter.y, 0f, 0f);
                state.FieldSize = new Vector4(shore.FieldHalfSize.x, shore.FieldHalfSize.y, 0f, 0f);
                state.UvToWorldOrigin = new Vector4(uvOrigin.x, uvOrigin.z, 0f, 0f);
                state.UvToWorldAxes = new Vector4(uvAxisX.x, uvAxisX.z, uvAxisZ.x, uvAxisZ.z);
                state.Time = SurfBeatTime; // the master beat, same clock the surface renders with
                state.FoamGain = surfFoamGain;
                state.WaterlineGain = surfWaterlineFoam;
                state.Amplitude = SurfAmplitudeEffective;
                state.Wavelength = SurfWavelengthEffective;
                state.Period = surfPeriod;
                state.BandDepth = surfBandDepth;
                state.SetStrength = surfSetStrength;
                state.CrestLength = surfCrestLength;
                state.CrestVariation = surfCrestVariation;
                state.CrestPersistence = surfCrestPersistence;
                state.Directionality = surfDirectionality;
                state.WindDir = new Vector4(Mathf.Cos(LargeWaveHeadingRad),
                                            Mathf.Sin(LargeWaveHeadingRad), 0f, 0f);
                state.Lean = surfLean;
                state.Compression = shoreCompression;
                state.Greens = shoreGreens;
                state.AmbientFade = surfAmbientFade;
                state.ShoalDepth = shoreShoalDepth;
                // FOAM-1/2: the pop-curve LUT + repartition weights, so the sim's injected foam
                // pops and repartitions exactly like the rendered whitewash.
                state.CrestFoamLutActive = SurfCrestFoamLutActive;
                state.CrestFoamLut = SurfCrestFoamLutTexture;
                state.CrestFoamGain = surfCrestFoamGain;
                state.BoreGain = surfFoamBoreGain;
                state.TrailGain = surfFoamTrailGain;
                state.TrailLength = surfFoamTrailLength;
                // FOAM-5: persistent swash deposit (lingers in the buffer, decays over real time).
                state.SwashAmplitude = surfSwashAmplitude;
                state.SwashDepositGain = surfSwashDepositGain;
            }
            return state;
        }

        // Choose the caustic path for this body: bounded bodies use the pool caustic (projected onto
        // the pool floor); the windowed OCEAN uses the large-body caustic (projected in the sim-window's
        // world frame, since a moving window has no fixed floor). Other windowed bodies still skip
        // caustics - the pool projection would be mismapped over their scrolling window.
        void RenderCausticsForThisBody()
        {
            if (!_windowed) { RenderCaustics(); return; }
            if (IsOceanClipmap) RenderLargeBodyCaustics();
        }

        // Render this body's own sim into its own caustic RT. The RT reaches the renderers
        // via the MPB; the primary also mirrors it to the _CausticTex global for objects.
        void RenderCaustics() => _caustics.Render(EffectiveWaterMesh, _water?.Texture, VolumeCenter.y,
                                                  VolumeCenter, VolumeExtentSafe, VolumeRotation, EffectiveLightDir.normalized);

        // The caustic pass draws with its own material before ApplyBodyBlock runs, so it has no per-body
        // wave params; it calls this at draw time to fold the surface's wind waves into the caustic.
        internal void ApplyCausticWaveUniforms(Material causticMaterial) => Publisher.ApplyWaveUniforms(causticMaterial);

        // Project the ocean's near-field window sim into the caustic RT via the large-body (world-frame)
        // caustic, so the underwater god rays can sample real surface-focused shimmer near the camera.
        void RenderLargeBodyCaustics() =>
            _caustics.RenderLargeBody(_patchGrid, _water?.Texture, SimWindowCenter, SimHalfExtent);

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

        // CPU mirror of LbwEdgeWeight() in WaterLargeWaves.hlsl: the bounded-body edge guard that
        // feathers the whole open-water wave field to rest toward the footprint border. Every CPU
        // consumer of the wave field (buoyancy sample, fog gate, query velocity) multiplies by this
        // at the same composition points the shader does, so floaters and gates keep matching the
        // flattened border the surface actually renders.
        internal float LargeWaveEdgeWeight(float worldX, float worldZ)
        {
            float feather = LargeWaveEdgeFeatherEffective;
            if (feather <= 0f) return 1f;
            Vector3 pool = WorldToPool(new Vector3(worldX, VolumeCenter.y, worldZ));
            Vector3 extent = VolumeExtentSafe;
            float borderMeters = Mathf.Min((1f - Mathf.Abs(pool.x)) * extent.x,
                                           (1f - Mathf.Abs(pool.z)) * extent.z);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(borderMeters / feather));
        }

        // Underwater-fog gate + per-body planar mirror -> WaterVolume.Underwater.cs.

        // ---- large-water sim window frame ----------------------------------
        // Half-size (world) of the window: simWindowMeters horizontally, the body's depth
        // scale vertically (ripple height stays coupled to extent.y like the whole-body sim).
        internal Vector3 SimHalfExtent => new Vector3(
            Mathf.Max(simWindowMeters, MinWindowHalfExtent),
            VolumeExtentSafe.y,
            Mathf.Max(simWindowMeters, MinWindowHalfExtent));

        // Average horizontal window half-size, keeping an injected ripple round in world units.
        float SimHorizontalExtent => Mathf.Max(simWindowMeters, MinWindowHalfExtent);

        // GPU consumer API (sim state texture, frame uniforms, window accessors) -> WaterVolume.Facade.cs.

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window).
        internal Vector3 WorldToSim(Vector3 world) => _simWindow.WorldToSim(world);

        // Windowing turns on for bodies whose horizontal half-extent exceeds the threshold.
        bool ShouldWindow()
        {
            if (!enableLargeBodyWindow) return false;
            // An unbounded ocean is infinite by definition, so the footprint-size threshold does not
            // apply - it always needs the camera-following window for its near-field ripples.
            if (openWater && unboundedOcean) return true;
            Vector3 e = VolumeExtentSafe;
            return Mathf.Max(e.x, e.z) > largeBodyThreshold;
        }

        // World point -> pool. Returns false if outside the [-1,1] horizontal footprint.
        // Internal: WaterCausticsPass gates occluder draws on it (only in-footprint objects
        // may stamp the caustic green channel).
        internal bool WorldToPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f;
        }

        // World point -> pool for the surface QUERIES (height/submersion/flow). Same as WorldToPoolXZ, except
        // an unbounded ocean has no footprint edge - its surface spans everywhere (clipmap to the horizon) -
        // so points beyond the bounded extent are accepted. Without this a floater (or the boat's propulsion,
        // which gates on IsSubmerged) cuts out at the extent edge. BodyContaining still uses the strict
        // footprint so per-body membership stays bounded.
        bool QueryPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return IsOceanClipmap || (poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f);
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
        internal float WaveMetersPerUnit => Mathf.Max(MinWaveMetersPerUnit, waveScaleMeters);

        // Regenerate the bank only when a wind/scale parameter actually changes, so
        // the phases stay stable frame-to-frame (a fresh bank would pop the surface).
        void EnsureWaveBank()
        {
            int count = EffectiveWaveCount;
            float verticalExtent = VolumeExtentSafe.y;
            bool dirty = windWaves != _waveGenEnabled
                         || windSpeed != _waveGenWindSpeed
                         || windFromDegrees != _waveGenWindFrom
                         || waveScaleMeters != _waveGenExtentMeters
                         || count != _waveGenCount
                         || waveAmplitudeScale != _waveGenAmpScale
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            _waveBank.Generate(windSpeed, windFromDegrees, 2f * waveScaleMeters,
                               count, waveAmplitudeScale, waveDirectionSpread, WaveMetersPerUnit,
                               verticalExtent);
            _waveGenWindSpeed = windSpeed;
            _waveGenWindFrom = windFromDegrees;
            _waveGenExtentMeters = waveScaleMeters;
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
        // The wave CLOCK (_WaveTime) is ALSO per body (TimeScale/pause are per-body controls), carried
        // in the per-renderer blocks; the primary's global mirror is the camera-pass fallback.

        // With the link on, the depth colour tracks the fog extinction so a single dial drives
        // both; off, the depth colour is authored independently.
        internal Color EffectiveDepthExtinction => linkDepthToFog ? fogExtinction : depthExtinction;

        // ---- terrain bed-height bake (WaterBedBaker) --------------------------

        /// <summary>Re-sample the terrain heightmap into the pool-space bed map. Call after
        /// the terrain or the volume placement changes.</summary>
        [ContextMenu("Rebake Bed")]
        public void RebakeBed() => BedBaker.Rebake();

        [ContextMenu("Rebake Shore Depth (Layer A)")]
        public void RebakeShoreDepth() => ShoreDepth.Rebake();

        [ContextMenu("Toggle Shore Depth Debug (Layer A)")]
        public void ToggleShoreDepthDebug()
        {
            WaterShoreDepthField.ToggleDepthDebug();
            ShoreDepth.EnsureBakedAndPublish(); // push the flag now so it shows without waiting for a tick
        }

        [ContextMenu("Toggle Shore SDF Debug (Layer A)")]
        public void ToggleShoreSdfDebug()
        {
            WaterShoreDepthField.ToggleSdfDebug();
            ShoreDepth.EnsureBakedAndPublish(); // push the flag now so it shows without waiting for a tick
        }

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
