// WebGpuWater - one water body: identity, lifecycle and public facade (Unity 6 / URP port).
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

        /// <summary>Density of the caustic generator's OWN sampling lattice, as a multiple of the
        /// ripple-sim grid. The caustic pattern is band-limited by that lattice and NOT by the caustic
        /// map size - the generator writes one focus sample per lattice vertex, so a bigger RT can only
        /// interpolate what the lattice already carried.</summary>
        public enum CausticDetail { MatchSim = 1, Double = 2 }

        /// <summary>Body archetype used by the inspector to show the relevant settings and apply sensible
        /// defaults. Advisory only: it drives the editor UI + the "Apply defaults" action, not the runtime
        /// paths (those still read openWater / unboundedOcean / enableLargeBodyWindow).</summary>
        public enum WaterBodyType { Pond, Lake, Ocean }

        // Serialized configuration surface (wiring fields, Settings blocks + accessors,
        // registry/autolink statics, legacy migrations, LUT bake) -> the WaterVolume.Settings*.cs
        // family; it outgrew one file (the Bodies registry, for one, lives in .Settings.Underwater.cs).

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
        WaterSeaStateFetchField _seaStateFetch;
        WaterUniformPublisher _publisher;
        WaterInputRouter _inputRouter;

        // Sim-window patch fields -> WaterVolume.SimWindowPatch.cs.
        // Ocean clipmap fields -> WaterVolume.OceanClipmap.cs.

        // Lazy: the bed baker serves the context-menu RebakeBed even on an uninitialized
        // body, and the publisher serves WriteBodyProps callers defensively.
        WaterBedBaker BedBaker => _bedBaker ??= new WaterBedBaker(this);
        internal WaterShoreDepthField ShoreDepth => _shoreDepth ??= new WaterShoreDepthField(this);
        internal WaterSeaStateFetchField SeaStateFetch
            => _seaStateFetch ??= new WaterSeaStateFetchField(this);
        internal bool SeaStateFetchBaked => _seaStateFetch != null && _seaStateFetch.IsBaked;
        internal Vector2 SeaStateFetchHalfSize
            => new Vector2(VolumeExtentSafe.x, VolumeExtentSafe.z);
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

            // Seed bounded water with a few ripples. An unbounded ocean already starts animated by
            // its FFT sea; seeding its separate near-field ripple grid defeats the pristine-ocean
            // sleep guard before gameplay has touched the water and enables several full-grid
            // compute passes until that imperceptible startup detail settles.
            // Compensate the strength for extent.y (like AddRipple) so seed splashes keep a fixed
            // world height on a deep pool - PoolToWorld multiplies surface height by extent.y.
            if (seedRipplesOnStart && !IsOceanClipmap)
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
            ShoreDepth.EnsureBaked(); // Layer A: world-frame seabed field, published per body below
            SeaStateFetch.EnsureBaked(); // bounded wind exposure; inert unless explicitly enabled

            Publisher.PublishSharedGlobals();
            EnsureWaveBank();
            if (_windowed) _simWindow.Track();  // prime the window centre before first publish
            RenderCausticsForThisBody();        // pool caustic (bounded), or the window-frame ocean caustic
            ApplyBodyBlock();
            if (isPrimary) PublishBodyGlobalsTracked();

            // Bounded bodies boot AWAKE so the ripple state textures are warm from frame one
            // (their legacy always-on behaviour), then EARN sleep through the activity reduction
            // once everything visible has faded - see ShouldRunRippleSolver. Oceans keep booting
            // asleep until gameplay touches them.
            if (!IsOceanClipmap) _water?.Wake();

            _initialized = true;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRender;
            DestroySurfCrestFoamLut(); // FOAM-1 LUT is lazy-baked, so it may exist pre-init too
            if (!_initialized)
            {
                // Never initialized (missing wiring / capability guard) - but TryInitialize
                // registers into Bodies BEFORE it sets _initialized, so a mid-init exception
                // (device loss, late wiring failure) used to leave a ghost entry here forever:
                // ActiveBodyCount stayed > 0, which kept the editor preview driver pumping the
                // player loop at 60 Hz even in an empty scene, and Update retried the throwing
                // init every tick. Unregister unconditionally before bailing.
                if (Primary == this) Primary = FindNextPrimary(this);
                Bodies.Remove(this);
                return;
            }

            _initialized = false;
            // A disabled body must STOP DRAWING. Nothing else hid these renderers - SetRenderersEnabled
            // only ever ran from Update - so the surface planes kept drawing with a property block full
            // of sim/caustic RTs destroyed moments later: a flat dark plane where the water should have
            // disappeared. Done FIRST, while the clipmap/patch renderers still exist to be hidden.
            // Both calls touch runtime-only state (forceRenderingOff and property blocks are not
            // serialized), so they are safe in edit mode - which is where the symptom shows.
            SetRenderersEnabled(false);
            ClearBodyRendererBlocks();

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
                CameraSubmerged = false; // same pattern: the after-fog foam overlay reads it
                FogSource = null;
                _globalsSource = null;   // the globals occupant stands down with ClearBodyGlobals below
                _globalsFrame = -1;
                Publisher.PublishUnderwater(0f, 0f, 0f, 0f, 0f, 0f);
                // The rest of the body globals - the volume frame above all. Without this the dead
                // body's footprint still describes a real box, and a WaterReceiver floor in the NEXT
                // scene renders wet inside it. Must run BEFORE DisposeModules, while the textures it
                // stands down are still the ones actually bound.
                WaterUniformPublisher.ClearBodyGlobals();
            }
            DisposeModules();      // disposes the six eager collaborator modules (sim, obstacle, caustics,
                                   // surface sampler, ocean FFT, sim window) - releases the same GPU
                                   // resources the inline disposal did, and clears the sampler/window refs.
            _bedBaker?.Dispose();  // also re-arms the lazy bake gate for the next enable
            _shoreDepth?.Dispose(); // Layer A field; re-arms its own lazy bake gate too
            _seaStateFetch?.Dispose(); // CPU/GPU wind-fetch field
            DestroySimWindowPatch(); // before restoring the surface material it borrows
            DestroyOceanClipmap();   // ditto - it borrows the same surface material
            DestroyChunkShell();     // per-body fog shell; shared material/mesh outlive it by design
            _planarMirror?.Dispose(); // frees this body's planar mirror camera + RT
            _planarMirror = null;
            DrainRetiredPlanarMirror(); // OnDisable is not a render callback, so a pending retire is legal here
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
            // Drain in-flight readbacks FIRST. Modules dispose in registry order, so the simulation
            // and the ocean FFT release + destroy their RTs before the surface sampler disposes - and
            // both of those RTs are readback SOURCES. AsyncReadbackChannel has no cancel path, so a
            // request whose source is destroyed underneath it errors on every disable / scene change.
            // Unity absorbs the result through the request's hasError branch (so this was console
            // noise, not bad data), but it is exactly the kind of error a user reports as a bug.
            AsyncGPUReadback.WaitAllRequests();
            for (int i = 0; i < _modules.Length; i++) _modules[i].Dispose();
        }
    }
}
