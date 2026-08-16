// WebGpuWater - WaterVolume partial: the per-frame tick and the per-body uniform publish.
//
// Update is the orchestration seam - it decides what runs this frame and in what order - while
// the rest of this file is what it pushes out afterwards: the property block carrying this body's
// uniforms to its own renderers (never globals, so multiple bodies cannot fight), the real cull
// box the scheduler tests, and the renderer enable/disable it drives. The edit-mode tick source
// sits here too because it exists only to feed this loop without Play.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        void Update()
        {
            // Edit-mode lazy init: a body whose wiring was assigned after AddComponent (the
            // builders' order) starts up here on the next editor tick.
            if (!_initialized)
            {
                TryInitialize();
                if (!_initialized) return;
            }

            // Destroy a planar mirror retired inside a render callback, where destroying it was
            // illegal (see RetirePlanarMirror). Done here because Update is not a rendering callback.
            DrainRetiredPlanarMirror();

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
                if (_simulate)
                {
                    // Apply every drop / wake interactor queued since the last frame in ONE full-grid
                    // pass each, instead of one pass per stamp. Deliberately OUTSIDE Step: Step returns
                    // early whenever no whole solver step is owed, which at high frame rates is most
                    // frames, and the stamps must still land every frame the way an immediate dispatch
                    // did. Also before Step so the flush precedes the sim window's scroll.
                    _water?.FlushInjections();
                    if (ShouldRunRippleSolver())
                        Step(dt);
                    else if (_windowed)
                        _simWindow?.Track(scrollSimulation: false);
                }
            }

            // Sun, ambient and exclusion data are scene-global. Publishing them from every body
            // repeated the same native shader writes and made the result depend on body update
            // order when scenes authored different light references. The primary is the package's
            // established global authority for membership-less renderers and camera passes.
            if (isPrimary) Publisher.PublishSharedGlobals();
            EnsureWaveBank();
            BedBaker.EnsureBaked();           // picks up useBedDepth being toggled on at runtime
            ShoreDepth.EnsureBaked(); // Layer A: keep the seabed field available for this body's property block
            SeaStateFetch.EnsureBaked(); // detects direction changes and rebakes the bounded exposure field
            // Bounded bodies render the pool caustic; the windowed OCEAN renders the large-body caustic
            // in the sim-window's world frame (other windowed bodies still skip - see RenderCausticsForThisBody).
            // The tier can amortise the pass over N frames (the caustic RT simply holds).
            // Ocean FFT cascades refresh on the shared wave clock (NOT gated on _simulate: like the analytic
            // large waves they must animate whenever the body is live, or the surface would sample stale
            // cascades and render differently in edit vs play, where _simulate follows game-camera culling).
            // The surface only reads them when _OceanFftActive is published, so this stays ocean-only.
            // Tier-amortised too, but on a tighter cap: a stale caustic RT only dims the pattern,
            // whereas a skipped dispatch freezes the ocean SURFACE itself (WaterQuality.MaxOceanFftInterval).
            if (IsOceanClipmap && !_paused && Time.frameCount % _oceanFftInterval == 0)
            {
                Vector2 camXZ = targetCamera != null
                    ? new Vector2(targetCamera.transform.position.x, targetCamera.transform.position.z)
                    : new Vector2(VolumeCenter.x, VolumeCenter.z);
                // Deposit knob maps to the compute's slow-fade fraction inverted (more deposit = slower dense
                // fade). Drift and max buildup pass straight through.
                var foam = new WaterOceanFft.FoamParams(OceanFoamWindThreshold, OceanFoamCoverage,
                                                        OceanFoamStrength, OceanFoamFadeRate,
                                                        1f - OceanFoamDeposit, OceanFoamDrift, OceanFoamMaxBuildup,
                                                        OceanFoamCrestAnisotropy, OceanFoamCrestGate,
                                                        OceanFoamFaceBias, OceanFoamCascadeMix);
                var sea = new WaterOceanFft.SeaParams(windSpeed, LargeWaveHeadingRad, OceanWindTurbulence,
                                                     SignificantWaveHeight, PeakWavelengthEffective,
                                                     PeakSharpness, SeaDepth, LargeWaveChoppiness,
                                                     SwellWavelength, SwellHeight,
                                                     SwellHeadingRad, OceanCascadeReach);
                Vector3 volumeCenter = VolumeCenter;
                var aperiodic = new WaterOceanFft.AperiodicParams(
                    oceanAperiodicEnabled, oceanDirectionMap,
                    new Vector2(volumeCenter.x, volumeCenter.z), oceanDirectionMapSize,
                    oceanDirectionMapStrength, oceanAperiodicTileScale);
                _oceanFft?.Dispatch(_waveTime, sea, LargeWaveAmplitudeEffective, camXZ, foam,
                                    SeaStateFetch, aperiodic);
            }
            if (_simulate && Time.frameCount % _causticInterval == 0)
                RenderCausticsForThisBody();

            ApplyBodyBlock();           // per-body uniforms -> this body's renderers (MPB)
            // Primary bridge: mirror this body's data to globals as the fallback for objects
            // without a WaterMembership (those resolve their own containing body instead).
            if (isPrimary) PublishBodyGlobalsTracked();
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

        bool ShouldRunRippleSolver()
        {
            // EVERY body earns sleep through the injection latch + activity reduction (bounded
            // bodies used to return true unconditionally and dispatch the whole solver chain on
            // still water forever). Bounded bodies boot AWAKE (TryInitialize calls Wake) so the
            // state textures are warm from frame one; any queued drop/sphere injection re-arms
            // the latch at ENQUEUE time, so a sleeping pool wakes the same frame it is touched.
            if (_water == null) return false;
            return HasContinuousRippleSource() || _water.HasReceivedInjection;
        }

        // Sources that do not pass through AddDrop/AddSphereInteraction cannot participate in the
        // injection-generation latch. They keep the solver live and suppress activity readbacks.
        bool HasContinuousRippleSource()
        {
            if (objectInteraction == ObjectInteraction.FootprintDelta) return true;
            WaterSimulation.ShoreFoamState shoreFoam = BuildShoreFoamState();
            return shoreFoam.Active && shoreFoam.InjectionActive;
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
        /// lake it is in. The block is publisher-owned (persistent + cached: only changed values
        /// are pushed), so any per-object look must live in the material.</summary>
        public void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            if (mpb == null) throw new System.ArgumentNullException(nameof(mpb));
            Publisher.WriteBodyProps(mpb);
        }

        // ONE block shared by every WaterMembership object inside this body, rebuilt at most once a
        // frame. SetPropertyBlock COPIES into the renderer, so handing the same instance to N members
        // is safe; each member used to run its own WriteBodyProps (a clear plus ~138 native property
        // writes) for byte-identical values, so fifty wet objects cost ~6,900 writes a frame.
        // Deliberately NOT _mpb: that one also carries SetChunkSurfaceProps / ApplyChunkShellBlock
        // additions, which members have never received and which would change a chunk body's look.
        MaterialPropertyBlock _membershipBlock;
        int _membershipBlockFrame = -1;

        /// <summary>This body's per-renderer uniforms, built at most once per frame and shared by
        /// every <see cref="WaterMembership"/> object inside it. Do NOT mutate the returned block:
        /// it is handed to every member, so per-object look must live in the material.</summary>
        internal MaterialPropertyBlock MembershipBlock
        {
            get
            {
                if (_membershipBlockFrame == Time.frameCount) return _membershipBlock;
                _membershipBlockFrame = Time.frameCount;
                _membershipBlock ??= new MaterialPropertyBlock();
                WriteBodyProps(_membershipBlock);
                return _membershipBlock;
            }
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

        internal void SetRenderersEnabled(bool on)
        {
            bool renderGeometry = on && renderBuiltInGeometry;
            // An ocean body draws the horizon-reaching clipmaps INSTEAD of the bounded surface planes,
            // so the two never double-draw (z-fight). Above and under each have their own twin; the
            // clipmaps only exist in play mode, so gate on their ACTUAL presence - otherwise edit mode
            // hides a plane with nothing to replace it (the surface looks cut).
            bool clipmapActive = _clipmapLevels != null;
            bool underClipmapActive = clipmapActive && _clipmapLevels.Length > 0 && _clipmapLevels[0].under != null;
            SetRendererEnabled(surfaceAbove, renderGeometry && !clipmapActive);
            SetRendererEnabled(surfaceUnder, renderGeometry && !underClipmapActive);
            SetRendererEnabled(poolRenderer, renderGeometry);
            SetRendererEnabled(_patchRenderer, renderGeometry && _windowed);
            SetRendererEnabled(_patchUnderRenderer, renderGeometry && IsOceanClipmap);
            SetClipmapRenderersEnabled(renderGeometry && IsOceanClipmap);
            // God rays obey the quality tier as well as culling: a tier that disables them
            // keeps the renderer off even when the body is on-screen. Windowed bodies also
            // suppress god rays (out of scope, same reason as caustics). A CHUNK draws its own
            // shafts inside the shell wall (shaped to its primitive + fill), so the pool god-ray
            // box is suppressed for chunks to avoid double, unshaped shafts.
            SetRendererEnabled(godRayRenderer,
                               renderGeometry && _godRaysAllowed && !_windowed && !IsChunk);
            SetChunkShellEnabled(renderGeometry);
        }

        // forceRenderingOff, NOT '.enabled': enabled is SERIALIZED, and this runs every frame from
        // Update under [ExecuteAlways] - so the culling gate was writing the user's scene, and saving
        // while a body sat off-screen baked its water renderer disabled. forceRenderingOff is the
        // runtime-only equivalent, the same idiom WaterSplashEmitter uses to mute its Shuriken draws.
        // This is the ONE choke point: the clipmap levels route through it too.
        static void SetRendererEnabled(Renderer r, bool on)
        {
            if (r == null) return;
            bool off = !on;
            if (r.forceRenderingOff != off) r.forceRenderingOff = off;
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
    }
}
