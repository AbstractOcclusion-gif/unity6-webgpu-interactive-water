// WebGpuWater - WaterVolume: underwater-fog gate + per-body planar mirror.
// Split out of WaterVolume.cs (final-clean E, verbatim move - any behavior change here is a bug):
// the camera-submerged detection (wave-aware, with hysteresis) that arms the fullscreen fog pass,
// and the per-body planar-mirror render driven from OnBeginCameraRender.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        /// <summary>True when the underwater fog pass should run this frame (set each frame by the
        /// body containing the target camera). Ocean fog is infinite, so it runs only when the camera is submerged; a bounded
        /// pond is a finite volume the shader clips to its box, so its fog runs from ANY angle whenever
        /// Water Fog is on (circle the pond and see the murk inside). The feature reads this to gate.</summary>
        internal static bool UnderwaterFogActive { get; private set; }

        /// <summary>The body whose camera-relative data drives this frame's fullscreen fog passes.</summary>
        internal static WaterVolume FogSource { get; private set; }

        /// <summary>True while the camera's near plane straddles the (displaced) surface, so the
        /// screen-space waterline meniscus pass should draw this frame (set each frame by the primary
        /// body, reset by the last body out in OnDisable like <see cref="UnderwaterFogActive"/>).
        /// Independent of the submerge flag: the line arms BEFORE the eye goes under.</summary>
        internal static bool WaterlineActive { get; private set; }

        /// <summary>True while the camera is submerged in the primary body (the CPU mirror of
        /// the _CameraUnderwater global). The after-fog pond-foam overlay reads it: submerged
        /// frames keep the queue-time foam draw (the fog is in front of the foam), so the
        /// overlay never enqueues work it would only discard.</summary>
        internal static bool CameraSubmerged { get; private set; }

        /// <summary>True when this body's quality tier selected the Simple underwater fog - the
        /// closed-form flat waterline, no per-pixel wavy march. Exposed because the fog PASS has to
        /// know: on Simple the shader short-circuits to OceanFlatPath before it ever tests
        /// _OceanSurfaceDepthValid, so the rendered-surface eye-depth prepass has no reader and must
        /// not be recorded. Mirrors the _UnderwaterFogSimple global published by PublishUnderwater
        /// from the SAME field, so the CPU gate and the shader branch cannot disagree.</summary>
        internal bool UnderwaterFogSimple
            => _underwaterFogMode == WaterQuality.UnderwaterMode.Simple;

        // Screen-space caustic projection runs PER BODY: any active body with a caustic RT and its
        // Screen-Space Caustics opt-in on gets its own fullscreen projection (drawn with THAT body's
        // _CausticTex + volume frame), so a SECONDARY chunk's foreign floors receive the CHUNK's caustics
        // - not only the primary's. Unlike fog this is NOT gated to a submerged camera: floor caustics are
        // the main use case, seen from ABOVE the water too. The feature reads these to gate + enumerate.
        static bool QualifiesForCausticProjection(WaterVolume body)
            => body != null && body.isActiveAndEnabled && body.screenSpaceCaustics && body.CausticTexture != null;

        /// <summary>True when at least one active body should project screen-space caustics this frame
        /// (the feature's cheap CPU gate before it enqueues the pass).</summary>
        internal static bool AnyCausticProjectionBody()
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (QualifiesForCausticProjection(Bodies[i])) return true;
            return false;
        }

        /// <summary>Fill <paramref name="into"/> with every body that projects screen-space caustics this
        /// frame, so the pass can draw one fullscreen projection per body (each framed on its own RT).</summary>
        internal static void CollectCausticProjectionBodies(List<WaterVolume> into)
        {
            into.Clear();
            for (int i = 0; i < Bodies.Count; i++)
                if (QualifiesForCausticProjection(Bodies[i])) into.Add(Bodies[i]);
        }

        // Pond-foam overlay (the after-fog surface-foam redraw): a body qualifies when its sim
        // foam is on. Chunk bodies are excluded - their disc footprint clips (sphere/mesh) are
        // Pass-0 state the overlay pass does not replicate, so their foam keeps the queue-time
        // path (PondFoamLayer's overlay-skip gate makes the same exception on the GPU).
        static bool QualifiesForFoamOverlay(WaterVolume body)
            => body != null && body.isActiveAndEnabled && body.Foam && !body.IsChunk;

        /// <summary>True when at least one body needs the after-fog pond-foam overlay (the
        /// feature's cheap CPU gate before it enqueues the after-fog pass).</summary>
        internal static bool AnyFoamOverlayBody()
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (QualifiesForFoamOverlay(Bodies[i])) return true;
            return false;
        }

        /// <summary>Fill <paramref name="into"/> with every ABOVE-water surface renderer whose
        /// pond foam the after-fog overlay should re-draw this frame.</summary>
        internal static void CollectFoamOverlayRenderers(List<Renderer> into)
        {
            into.Clear();
            for (int i = 0; i < Bodies.Count; i++)
                if (QualifiesForFoamOverlay(Bodies[i])) Bodies[i].CollectAboveSurfaceRenderers(into);
        }

        // Refresh the underwater fog gate at the START of the target camera's render. WHY here and not
        // in Update: Update runs at DefaultExecutionOrder -50, before the OrbitCamera moves the camera
        // in LateUpdate, so an Update-time read lagged the fog one frame on entry. This fires after
        // LateUpdate, just before the fog feature's AddRenderPasses. Gated to the primary body's own
        // target camera so the reflection and scene-view cameras never drive the gate.
        void OnBeginCameraRender(ScriptableRenderContext context, Camera cam)
        {
            if (!_initialized) return;
            if (cam != targetCamera) return; // ignore reflection / scene-view cameras

            RenderPlanarMirror(cam); // per-body planar: every planar body mirrors its OWN plane, not just primary

            if (!isPrimary) return;
            WaterVolume fogSource = BodyContaining(cam.transform.position);
            if (fogSource == null) return;
            fogSource.UpdateUnderwaterState(cam);
        }

        // Fraction of screen resolution + clip-plane push for the per-body planar mirror. Constants (not
        // per-body inspector fields yet) to keep the Reflections block small - the budget, not resolution,
        // is the cost lever. KEEP in sync with PlanarReflection's inspector defaults.
        // Also the field-initializer defaults of the standalone PlanarReflection component, so the
        // per-body path and the legacy global component start from the same tuning by construction.
        internal const float PlanarMirrorResolutionScale = 0.5f;
        internal const float PlanarMirrorClipPlaneOffset = 0.02f;

        PlanarMirror _planarMirror;
        // A mirror retired mid-frame, waiting for a legal moment to be destroyed. RenderPlanarMirror runs
        // from beginCameraRendering, and PlanarMirror.Dispose destroys its reflection camera GAMEOBJECT -
        // which outside play mode goes through DestroyImmediate, and Unity forbids that inside a rendering
        // callback ("You must use Destroy instead"), so retiring a mirror in place threw once per
        // planar/budget flip. Handing it over here and destroying it from Update keeps the destroy out of
        // the callback in BOTH modes. Runtime-only state; never serialized.
        PlanarMirror _planarMirrorRetiring;

        /// <summary>This body's most recent planar mirror, or null when it isn't rendering planar.</summary>
        internal Texture PlanarReflectionTexture => _planarMirror?.Texture;

        // Render THIS body's planar mirror across its own surface plane into its own RT (bound per body by
        // the publisher as _PlanarReflectionTex). WHY per body: a single shared mirror can only be correct
        // for one plane, so multiple planar pools used to collide onto one hero plane. Gated by the frame
        // budget via EffectiveUsePlanar, so an over-budget (or planar-off) pool frees its mirror and
        // degrades to SSR / sky.
        void RenderPlanarMirror(Camera cam)
        {
            if (!EffectiveUsePlanar)
            {
                RetirePlanarMirror();
                return;
            }
            _planarMirror ??= new PlanarMirror(name + "_PlanarMirror");
            // Mirror across this body's REST plane, and nothing else. This used to track the WAVE
            // height under the camera, which helped the one thing that rides the camera's own wave
            // phase (a nearby floating boat) and wrecked everything that does not.
            // A mirror plane puts a static point's image at 2*planeY - y, so moving the plane one
            // metre moves the ENTIRE reflected world two. On a raging sea the camera's wave height
            // swings by the full swell amplitude every frame, so a static island - which shares none
            // of that phase - had its reflection sliding by twice the amplitude and tearing away
            // from its own base. A still plane cannot do that, at any sea state.
            // A floating object's reflection is NOT this function's job: one plane can never fit a
            // displaced surface, so an object h above the plane is imaged at -h whatever the plane
            // does. PlanarExcludeLayers keeps it out of the mirror and SSR, which marches the real
            // reflected ray, owns it.
            _planarMirror.Render(cam, VolumeCenter.y, PlanarMirrorResolutionScale,
                                 PlanarMirrorClipOffset(), PlanarReflectLayers());
        }

        // The oblique near-clip offset the mirror crops with, along the surface normal from the mirror
        // plane. The positive constant is the seam guard (crop a hair ABOVE the plane so the plane's own
        // pixels cannot bleed into their own reflection); PlanarClipDepth subtracts from it to keep a
        // band BELOW the plane instead.
        // WHY that band is wanted: the surface is displaced and the mirror plane is not, so a wave
        // TROUGH exposes shoreline sitting under the rest plane. Cropped out, the mirror has a hole
        // there and answers with the reflection camera's own skybox - the island's base reflecting SKY.
        // WHY the depth is an art knob and not the live wave height: the crop is exactly what a live
        // value would change every frame, which is the flicker the still plane above just removed.
        float PlanarMirrorClipOffset() => PlanarMirrorClipPlaneOffset - PlanarClipDepth;

        // Hand the live mirror to the retire slot instead of destroying it here. _planarMirror is cleared
        // IMMEDIATELY so PlanarReflectionTexture stops answering with an RT that is about to be released -
        // the publisher must not bind a dead mirror for the frame before the drain.
        void RetirePlanarMirror()
        {
            if (_planarMirror == null) return;
            // At most one can ever be pending: the slot is filled only when a LIVE mirror exists, and the
            // next live mirror is built only once EffectiveUsePlanar is true again - the branch that never
            // retires. The Update drain therefore always runs in between.
            _planarMirrorRetiring = _planarMirror;
            _planarMirror = null;
        }

        // Destroy a mirror retired inside the render callback. Call ONLY from Update or OnDisable, never
        // from beginCameraRendering - that restriction is the whole reason the slot exists.
        void DrainRetiredPlanarMirror()
        {
            if (_planarMirrorRetiring == null) return;
            _planarMirrorRetiring.Dispose();
            _planarMirrorRetiring = null;
        }

        // Reflect everything the camera sees EXCEPT this body's own water surface layer, so the mirror
        // never contains the surface it feeds (a feedback smear). Matches AssignSurfaceLayers, which puts
        // the surface on its own layer precisely so planar can exclude it.
        //
        // Plus whatever the author excluded (Reflections > Planar Exclude Layers), which exists because
        // a plane CANNOT fit a displaced surface: a floating object h above the mirror plane has its
        // image placed at -h while the wave carrying it is at +h, so the reflection sits low and swims
        // as the swell moves it. That is a property of planar reflection, not a bug to chase - the fix
        // is to keep dynamic floaters out of the mirror and let SSR, which marches the real reflected
        // ray, own them. Default 0 excludes nothing, so an existing scene is unchanged.
        //
        // Doing it HERE rather than in PlanarMirror keeps one owner for "what this body reflects":
        // the mirror is handed a finished mask and never has to know why a layer is missing.
        LayerMask PlanarReflectLayers()
        {
            int surfaceLayer = surfaceAbove != null ? surfaceAbove.gameObject.layer : gameObject.layer;
            return ~(1 << surfaceLayer) & ~PlanarExcludeLayers.value;
        }

        // Detect whether the camera is submerged in THIS fog-source body and publish the globals the
        // underwater fog shader needs. The surface height is wave-aware at the camera's xz (swell + shoal
        // + surf front on the master beat; see SurfaceHeightAtCamera), so the gate tracks the rendered
        // surface. Bounded bodies require the camera inside their footprint; an ocean clipmap spans
        // everywhere, so only the height test applies.
        void UpdateUnderwaterState(Camera eyeCamera)
        {
            FogSource = this;
            // The fullscreen fog and waterline passes intentionally use the established global
            // shader path: it is the path that keeps exclusion-wall scattering stable. Refresh
            // that global body frame from the camera-selected source here, after all bodies have
            // updated, so a secondary pool's fog no longer inherits the primary body's volume.
            Publisher.PublishBodyGlobals();
            bool submerged = ComputeCameraSubmerged(eyeCamera, out float surfaceY, out bool nearPlaneStraddles);
            // "The fog pass must run" and "the eye is in water" are two DIFFERENT questions, and
            // inside a semi-submerged exclusion volume they have opposite answers: the eye sits in
            // AIR, in a sunken room, below sea level, with water all around it. They used to be one
            // flag, which is why every camera-height term downstream (the fog's murk arm-fade, the
            // prepass dry-camera guard) fired at a waterline the eye was never actually crossing -
            // the fog visibly fading out and vanishing at water level from inside a carve.
            // ARMING is unchanged and still keys on the near-plane band below: the pass MUST stay
            // armed in there, because it is what carves the dry room out of every ray. Only the
            // "eye in water" flag stands down. KWS makes the same split - it clears
            // IsCameraPartialUnderwater when the camera is inside a clip zone while leaving the
            // pass alive; Crest disables its camera-height heuristics outright while a portal is
            // active, for the same reason (you can be anywhere relative to the sea and still be
            // looking into an aquarium).
            bool eyeInDryVolume = WaterExclusionVolume.ContainsPoint(eyeCamera.transform.position);
            bool eyeInWater = submerged && !eyeInDryVolume;
            CameraSubmerged = eyeInWater; // CPU mirror for the after-fog foam overlay's gate
            // Ocean fog is infinite, so it only matters when the camera is submerged. A bounded pond is a
            // finite fog volume clipped to its box, so it should render from ANY angle (circle it and see
            // the murk inside) whenever Water Fog is on. The quality tier's Off mode wins over everything:
            // the fullscreen pass never enqueues on tiers that can't afford it.
            bool tierAllowsFog = _underwaterFogMode != WaterQuality.UnderwaterMode.Off;
            // Ocean arming uses the WIDE near-surface band (not the submerge flag), and the rule
            // that makes toggling it invisible is that the band must be a strict SUPERSET of what
            // the shader's per-pixel mask can admit - so on the frame the pass first runs, the set
            // of pixels the mask lets through is still empty. Both references depend on the same
            // property (Crest a +-2 m dead band, KWS a wind-scaled downward bias).
            //
            // INSIDE A DRY CARVE that property fails, and the near-plane band cannot restore it.
            // The mask does not classify at the near plane there: WaterlineClassifyPoint pushes the
            // point out to where the ray LEAVES the carve (the Crest portal move), which sits an
            // arbitrary distance and height away - so a corner test on the lens says nothing about
            // which pixels the mask will admit. The gap was visible: crossing the water level
            // inside a semi-submerged room, a band between the surface and the fog popped for the
            // few frames before the pass armed, and vanished the moment it did.
            //
            // Arming unconditionally in there restores the superset by making the question moot,
            // and it also DELETES a handoff rather than moving it: the exclusion wall
            // self-completes the whole fog integral only while _UnderwaterFogArmed is 0
            // (WaterExclusionWall.shader), and from inside a room its own veil is an exiting face
            // carrying ~zero chord, so those were the frames nobody was painting. With the pass
            // always armed in a carve the wall never has to take over at all. Costs one fullscreen
            // pass while the eye is inside a carve - bounded, and the pass is what carves the dry
            // room out of every ray in the first place (see the note in ComputeCameraSubmerged
            // about the CPU early-out that was tried there and reverted).
            UnderwaterFogActive = waterFog && tierAllowsFog
                               && (IsOceanClipmap ? (_fogNearSurface || eyeInDryVolume) : true);
            // Screen-space waterline (meniscus): armed while the near plane STRADDLES the displaced
            // surface - exactly the half-in/half-out band the binary submerge gate cannot represent -
            // so the crossing shows a surface-tension line instead of a hard pop. Rides the same tier
            // gate as the fog (it is a pass of the same fullscreen material).
            WaterlineActive = MeniscusEnabled && tierAllowsFog && nearPlaneStraddles;
            Publisher.PublishWaterline(MeniscusWidthPixels, MeniscusStrength, MeniscusWarp);
            // The unbounded flag tells the shader to fog the whole below-surface half-space (ocean) vs
            // clip the fog to this body's box (pond / bounded lake = a finite fog volume). Simple mode
            // swaps the shader's per-pixel wavy-waterline march for the closed-form flat waterline at
            // surfaceY (wave-aware at the camera's xz, so the line still rides the local swell).
            bool fogSimple = _underwaterFogMode == WaterQuality.UnderwaterMode.Simple;
            // fogArmed mirrors UnderwaterFogActive to the GPU: the exclusion wall self-completes
            // (reconstructs the fog behind its veil) ONLY when the fullscreen pass will not paint,
            // and the surface's underside stage skips its own camera-depth downwelling dim (the
            // fog pass applies the identical term, which used to double-darken the ceiling).
            Publisher.PublishUnderwater(eyeInWater ? 1f : 0f, surfaceY, IsOceanClipmap ? 1f : 0f,
                                        fogSimple ? 1f : 0f, UnderwaterFogActive ? 1f : 0f,
                                        eyeInDryVolume ? 1f : 0f);
            // Screen-space caustics are gated PER BODY (AnyCausticProjectionBody / CollectCausticProjectionBodies),
            // not from this primary-only path, so a secondary chunk drives its own projection independently.
        }

        // A little beyond the [-1,1] footprint so an edge-on view of a pond still triggers; the shader
        // box-clips the fog per pixel, so this CPU gate only has to be roughly right.
        const float UnderwaterFootprintMargin = 1.25f;

        // Water intersects the view as soon as the camera's NEAR PLANE dips below the surface (partial
        // submersion, KWS-style), not only when the whole camera is under - otherwise a shallow pond
        // never triggers. Sample the four near-plane corners (plus the eye) and run on the lowest.
        // The surface height is WAVE-AWARE at the camera's xz (not the flat rest plane), so the
        // waterline tracks the swell and the fog stops toggling frame-to-frame at a bobbing crest.
        // 'nearPlaneStraddles' additionally reports the surface sitting INSIDE the near plane's
        // vertical span - the partial-submersion band the waterline meniscus pass draws over.
        bool ComputeCameraSubmerged(Camera cam, out float surfaceY, out bool nearPlaneStraddles)
        {
            surfaceY = SurfaceHeightAtCamera(cam);
            nearPlaneStraddles = false;
            _fogNearSurface = false; // recomputed below; the early-outs must not keep a stale band
            if (!waterFog) { _wasCameraSubmerged = false; return false; } // one Water Fog toggle drives both looks

            // NOTE: deliberately NO camera-inside-exclusion-volume early-out here. An eye in a dry
            // room below the surface still needs the fog pass ARMED: the shader carves the dry span
            // out of every ray (ExclusionRayLength), so the room reads dry while water seen through
            // a window stays fogged - Crest's carved-volume behaviour. A CPU gate here was tried and
            // reverted: it unarmed the whole fullscreen pass and killed ALL fog from inside the room.

            // KWS-style partial submersion: every near-plane corner is tested against the surface
            // height AT ITS OWN xz. The old single camera-xz height mis-timed the gate on a CALM
            // ocean - a slow low swell keeps the corner-local heights different from the camera's
            // for seconds at a time, so the fog armed/disarmed at visibly wrong moments (fast seas
            // hid the error; ponds never gate). The two BOTTOM corners are additionally tested with
            // a small DOWNWARD prediction offset: the FFT height readback is ~1-2 frames stale, so
            // the gate arms a touch early instead of late (KWS's OceanWavesPredictionOffset trick).
            // Hysteresis rides the per-corner threshold: once submerged, a corner must rise a little
            // ABOVE its surface to count dry again, so a bobbing crest can't toggle the fog.
            float near = cam.nearClipPlane;
            float hysteresis = _wasCameraSubmerged ? SubmergeHysteresis : -SubmergeHysteresis;
            // Ceiling arming the OCEAN fog pass. REWRITTEN 2026-07-31: each corner used to be
            // tested against its STALE readback height with a fixed 0.5 m slack, and in a heavy
            // sea that slack is routinely exceeded (readback latency x the swell's vertical
            // speed, plus the horizontal chop the height field cannot see) - so the whole
            // fullscreen fog toggled frame-to-frame while the shader's per-pixel mask still
            // admitted pixels: the arming rule's own superset property, broken, on screen as the
            // fog "completely popping" at partial submersion. The ceiling now comes from the
            // WAVE ENVELOPE around the rest plane (the KWS CurrentMaxOceanWaveHeight move): NO
            // readback in the test at all, so it cannot flap on staleness - it only moves when
            // the CAMERA moves. Over-arming is the intended trade: an armed pass whose mask
            // admits nothing changes no pixel (the property this band was always meant to
            // have); it merely runs.
            float fogArmCeilingY = VolumeCenter.y + SurfaceHeightEnvelope() + FogArmBandMeters;
            int cornersUnder = 0;
            int straddleUnder = 0;
            int straddleAbove = 0;
            int cornersNearOrUnder = 0;
            bool predictedUnder = false;
            for (int i = 0; i < NearPlaneCornersViewport.Length; i++)
            {
                Vector2 viewport = NearPlaneCornersViewport[i];
                Vector3 corner = cam.ViewportToWorldPoint(new Vector3(viewport.x, viewport.y, near));
                float cornerSurfaceY = SurfaceHeightAtWorldXZ(corner.x, corner.z);
                if (corner.y < cornerSurfaceY + hysteresis) cornersUnder++;
                // Padded both-ways counts for the waterline-straddle test below.
                if (corner.y < cornerSurfaceY + WaterlineArmPad) straddleUnder++;
                if (corner.y > cornerSurfaceY - WaterlineArmPad) straddleAbove++;
                // Bottom corners (viewport y = 0) double as the early-arm prediction points.
                if (viewport.y < 0.5f && corner.y - WavePredictionMeters < cornerSurfaceY + hysteresis)
                    predictedUnder = true;
                // Envelope ceiling - see fogArmCeilingY above. Deliberately NOT cornerSurfaceY:
                // the stale per-corner height is exactly what made this gate flap in a heavy sea.
                if (corner.y < fogArmCeilingY) cornersNearOrUnder++;
            }
            _fogNearSurface = cornersNearOrUnder > 0;

            // Footprint: bounded bodies fog (and draw their waterline) only with the camera roughly
            // over them; an ocean clipmap spans everywhere.
            bool inFootprint = IsOceanClipmap;
            if (!inFootprint)
            {
                Vector3 pool = WorldToPool(cam.transform.position);
                inFootprint = Mathf.Abs(pool.x) <= UnderwaterFootprintMargin
                           && Mathf.Abs(pool.z) <= UnderwaterFootprintMargin;
            }

            // The waterline crosses the screen while the near plane has corners on BOTH sides of
            // their local surface (padded so the line is armed before its band touches the edge).
            nearPlaneStraddles = inFootprint && straddleUnder > 0 && straddleAbove > 0;

            bool partial = inFootprint && (cornersUnder > 0 || predictedUnder);
            _wasCameraSubmerged = partial;
            return partial;
        }

        // The four near-plane corners in viewport space; the y = 0 pair are also the KWS-style
        // prediction points (see ComputeCameraSubmerged).
        static readonly Vector2[] NearPlaneCornersViewport =
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f)
        };
        // Downward test offset (metres) absorbing the FFT readback staleness, so the fog arms a
        // touch early rather than late on descent (KWS's OceanWavesPredictionOffset equivalent).
        const float WavePredictionMeters = 0.1f;
        // Extra pad on top of the wave-envelope arm ceiling (fogArmCeilingY at the arming site).
        // The envelope bounds where the surface CAN be; this pad covers the near plane's own
        // vertical extent and camera travel within a frame.
        const float FogArmBandMeters = 0.5f;

        // CPU mirror of the shader's SurfaceHeightBand (WaterWaterline.hlsl): the conservative
        // half-band around the rest plane containing every height the displaced surface can
        // reach this frame - swell reach (an amplitude multiple) vs surf-crest reach, plus the
        // wind-chop pad. Derived from the SAME values the shader's version reads off the
        // published globals (LargeWaveAmplitudeEffective IS _LargeWaveAmplitude; the ctx fields
        // ARE the _Surf* globals) and from LargeWaveField's own surf constants, so the two
        // derivations cannot drift unless one is edited alone.
        internal float SurfaceHeightEnvelope()
        {
            if (!openWater) return 0f;
            ShoreWaveContext ctx = ShoreWaveCtx;
            float surfReach = ctx.SurfActive
                ? ctx.SurfAmplitude * LargeWaveField.SurfSetAmpJitterMax
                  * Mathf.Max(ctx.Greens, LargeWaveField.SurfMinGreens)
                : 0f;
            // Two wave scales, mirroring the shader exactly - see the long note in
            // WaterWaterline.hlsl's SurfaceHeightBand for WHY the amplitude term alone bounded
            // nothing on the FFT path.
            float analyticReach = Mathf.Abs(LargeWaveAmplitudeEffective) * SurfaceBandAmplitudes;
            float seaReach = OffshoreSignificantHeight * Mathf.Abs(LargeWaveAmplitudeEffective)
                           * SurfaceBandCrestReach;
            return Mathf.Max(Mathf.Max(analyticReach, seaReach), surfReach)
                 + SurfaceBandPadMeters;
        }
        // KEEP IN SYNC with WaterWaterline.hlsl (SURFACE_BAND_AMPLITUDES / SURFACE_BAND_PAD_METERS
        // / SURFACE_BAND_CREST_REACH). Machine-checked: WaterWaveConstantsValidator guards the trio.
        const float SurfaceBandAmplitudes = 3f;
        const float SurfaceBandPadMeters = 2f;
        const float SurfaceBandCrestReach = 1.2f;
        // This frame's "any near-plane corner within FogArmBandMeters of its surface" flag.
        bool _fogNearSurface;

        // World-space surface height at the camera's xz. Open water bobs with the large swell (analytic
        // + FFT), the dominant partial-submersion motion; pools / bounded bodies use the rest plane
        // (their wind-wave detail is small and the pond fog is box-clipped anyway).
        float SurfaceHeightAtCamera() => SurfaceHeightAtCamera(targetCamera);

        float SurfaceHeightAtCamera(Camera cam)
        {
            if (cam == null) return VolumeCenter.y;
            Vector3 p = cam.transform.position;
            return SurfaceHeightAtWorldXZ(p.x, p.z);
        }

        // World-space surface height at ANY xz (the per-corner form of the gate height: each
        // near-plane corner tests against ITS OWN local surface, KWS-style).
        float SurfaceHeightAtWorldXZ(float x, float z)
        {
            float y = VolumeCenter.y;
            if (!openWater) return y;
            // Fog gate: use the latest FFT height readback (~1-2 frames stale; tolerable because the fog
            // shader's per-pixel waterline is already current and reads the same FFT surface - the gate only
            // arms the pass). Falls back to the plain field / analytic sample when the readback isn't
            // available (non-FFT body, first frames, or the point outside the readback region).
            if (OceanFftActive && _oceanFft.TrySampleHeightLatest(x, z, out float fftHeight))
                // Run the extrapolated (current-time) swell through the SAME shore/surf treatment the
                // readback path (SampleLargeWaveField) and the GPU FFT branch (LargeBodyWaveHeight) use, so
                // the submerge gate matches the rendered shore surface near shore: shoal attenuation +
                // ambient fade + the surf-front height on the master beat (ShoreWaveCtx.SurfBeatTime).
                // Without it the gate saw bare (un-shoaled, deep-amplitude) swell and the fog popped on
                // against the wrong height wherever the shore surface differs - fogging the ABOVE-water
                // scene near shore. Height uses only fft.x (ApplyShoreToFftSample), so zero derivs are
                // correct for this height-only gate. Identity offshore (no shore field).
                // Edge guard mirrors the render: the gate must not arm against wave height the
                // feathered border no longer displays.
                // The rate out-param is composed for buoyancy's drag reference; a height-only gate has
                // no use for it, so it is fed a local that is written and dropped rather than plumbed.
                y += ApplyShoreToFftHeightOnly(fftHeight, x, z) * LargeWaveEdgeWeight(x, z);
            else
                y += SampleLargeWaveField(x, z).x;
            return y;
        }

        // Shore/surf treatment of an FFT height sample, for callers that want the HEIGHT only.
        // Zero slopes in and out: ApplyShoreToFftSample composes height from fft.x alone, so the
        // derivative channels are unused here rather than wrong.
        float ApplyShoreToFftHeightOnly(float fftHeight, float x, float z)
        {
            float unusedVerticalRate = 0f;
            return LargeWaveField.ApplyShoreToFftSample(new Vector3(fftHeight, 0f, 0f),
                       x, z, _waveTime, SwellWavelength, ShoreWaveCtx, ref unusedVerticalRate).x;
        }

        // Hysteresis half-band (world units) around the surface for the camera-submerged flag.
        const float SubmergeHysteresis = 0.05f;
        // Vertical pad (world units) on the near-plane span for the waterline-straddle test. WIDE
        // on purpose (same doctrine as FogArmBandMeters): the meniscus and tension warp are
        // per-pixel analytic and self-extinguish when the line is off screen, so arming across a
        // generous band is visually free and the pass only ever toggles when the line is
        // provably not visible - the readback's ~frame staleness stops mattering.
        const float WaterlineArmPad = 0.5f;
        bool _wasCameraSubmerged;
    }
}
