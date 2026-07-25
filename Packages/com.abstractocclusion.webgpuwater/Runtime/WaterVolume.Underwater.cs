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
        /// primary body). Ocean fog is infinite, so it runs only when the camera is submerged; a bounded
        /// pond is a finite volume the shader clips to its box, so its fog runs from ANY angle whenever
        /// Water Fog is on (circle the pond and see the murk inside). The feature reads this to gate.</summary>
        internal static bool UnderwaterFogActive { get; private set; }

        /// <summary>True while the camera's near plane straddles the (displaced) surface, so the
        /// screen-space waterline meniscus pass should draw this frame (set each frame by the primary
        /// body, reset by the last body out in OnDisable like <see cref="UnderwaterFogActive"/>).
        /// Independent of the submerge flag: the line arms BEFORE the eye goes under.</summary>
        internal static bool WaterlineActive { get; private set; }

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
            UpdateUnderwaterState();
        }

        // Fraction of screen resolution + clip-plane push for the per-body planar mirror. Constants (not
        // per-body inspector fields yet) to keep the Reflections block small - the budget, not resolution,
        // is the cost lever. KEEP in sync with PlanarReflection's inspector defaults.
        // Also the field-initializer defaults of the standalone PlanarReflection component, so the
        // per-body path and the legacy global component start from the same tuning by construction.
        internal const float PlanarMirrorResolutionScale = 0.5f;
        internal const float PlanarMirrorClipPlaneOffset = 0.02f;

        PlanarMirror _planarMirror;

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
                _planarMirror?.Dispose();
                _planarMirror = null;
                return;
            }
            _planarMirror ??= new PlanarMirror(name + "_PlanarMirror");
            _planarMirror.Render(cam, transform.position.y, PlanarMirrorResolutionScale,
                                 PlanarMirrorClipPlaneOffset, PlanarReflectLayers());
        }

        // Reflect everything the camera sees EXCEPT this body's own water surface layer, so the mirror
        // never contains the surface it feeds (a feedback smear). Matches AssignSurfaceLayers, which puts
        // the surface on its own layer precisely so planar can exclude it.
        LayerMask PlanarReflectLayers()
        {
            int surfaceLayer = surfaceAbove != null ? surfaceAbove.gameObject.layer : gameObject.layer;
            return ~(1 << surfaceLayer);
        }

        // Detect whether the camera is submerged in THIS (primary) body and publish the globals the
        // underwater fog shader needs. The surface height is wave-aware at the camera's xz (swell + shoal
        // + surf front on the master beat; see SurfaceHeightAtCamera), so the gate tracks the rendered
        // surface. Bounded bodies require the camera inside their footprint; an ocean clipmap spans
        // everywhere, so only the height test applies.
        void UpdateUnderwaterState()
        {
            bool submerged = ComputeCameraSubmerged(out float surfaceY, out bool nearPlaneStraddles);
            // Ocean fog is infinite, so it only matters when the camera is submerged. A bounded pond is a
            // finite fog volume clipped to its box, so it should render from ANY angle (circle it and see
            // the murk inside) whenever Water Fog is on. The quality tier's Off mode wins over everything:
            // the fullscreen pass never enqueues on tiers that can't afford it.
            bool tierAllowsFog = _underwaterFogMode != WaterQuality.UnderwaterMode.Off;
            UnderwaterFogActive = waterFog && tierAllowsFog && (IsOceanClipmap ? submerged : true);
            // Screen-space waterline (meniscus): armed while the near plane STRADDLES the displaced
            // surface - exactly the half-in/half-out band the binary submerge gate cannot represent -
            // so the crossing shows a surface-tension line instead of a hard pop. Rides the same tier
            // gate as the fog (it is a pass of the same fullscreen material).
            WaterlineActive = MeniscusEnabled && tierAllowsFog && nearPlaneStraddles;
            Publisher.PublishWaterline(MeniscusWidthPixels, MeniscusStrength);
            // The unbounded flag tells the shader to fog the whole below-surface half-space (ocean) vs
            // clip the fog to this body's box (pond / bounded lake = a finite fog volume). Simple mode
            // swaps the shader's per-pixel wavy-waterline march for the closed-form flat waterline at
            // surfaceY (wave-aware at the camera's xz, so the line still rides the local swell).
            bool fogSimple = _underwaterFogMode == WaterQuality.UnderwaterMode.Simple;
            // fogArmed mirrors UnderwaterFogActive to the GPU: the exclusion wall self-completes
            // (reconstructs the fog behind its veil) ONLY when the fullscreen pass will not paint,
            // and the surface's underside stage skips its own camera-depth downwelling dim (the
            // fog pass applies the identical term, which used to double-darken the ceiling).
            Publisher.PublishUnderwater(submerged ? 1f : 0f, surfaceY, IsOceanClipmap ? 1f : 0f,
                                        fogSimple ? 1f : 0f, UnderwaterFogActive ? 1f : 0f);
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
        bool ComputeCameraSubmerged(out float surfaceY, out bool nearPlaneStraddles)
        {
            surfaceY = SurfaceHeightAtCamera();
            nearPlaneStraddles = false;
            if (!waterFog) { _wasCameraSubmerged = false; return false; } // one Water Fog toggle drives both looks
            Camera cam = targetCamera;
            if (cam == null) { _wasCameraSubmerged = false; return false; }

            // NOTE: deliberately NO camera-inside-exclusion-volume early-out here. An eye in a dry
            // room below the surface still needs the fog pass ARMED: the shader carves the dry span
            // out of every ray (ExclusionRayLength), so the room reads dry while water seen through
            // a window stays fogged - Crest's carved-volume behaviour. A CPU gate here was tried and
            // reverted: it unarmed the whole fullscreen pass and killed ALL fog from inside the room.

            // Reference height for the submerge test: the LOWEST of the eye and the four near-plane
            // corners, for OCEANS too now. Oceans used to test only the eye - arming on the corners
            // fogged the from-above water ~a near-plane-extent (~0.2 m) early with nothing on screen
            // to justify the cut - but the waterline meniscus pass now draws the crossing line across
            // exactly that band, so the early arm IS the correct look: fog below the line, dry scene
            // above it, a meniscus between. The corner span also brackets the on-screen waterline for
            // the meniscus gate below.
            float near = cam.nearClipPlane;
            float eyeY = cam.transform.position.y;
            float lowestY = eyeY;
            float highestY = eyeY;
            AccumulateNearPlaneCorner(cam, 0f, 0f, near, ref lowestY, ref highestY);
            AccumulateNearPlaneCorner(cam, 1f, 0f, near, ref lowestY, ref highestY);
            AccumulateNearPlaneCorner(cam, 0f, 1f, near, ref lowestY, ref highestY);
            AccumulateNearPlaneCorner(cam, 1f, 1f, near, ref lowestY, ref highestY);
            float referenceY = lowestY;

            // Footprint: bounded bodies fog (and draw their waterline) only with the camera roughly
            // over them; an ocean clipmap spans everywhere.
            bool inFootprint = IsOceanClipmap;
            if (!inFootprint)
            {
                Vector3 pool = WorldToPool(cam.transform.position);
                inFootprint = Mathf.Abs(pool.x) <= UnderwaterFootprintMargin
                           && Mathf.Abs(pool.z) <= UnderwaterFootprintMargin;
            }

            // The waterline crosses the screen while the surface sits inside the near plane's
            // vertical span (padded so the line is already armed when its band touches the edge).
            nearPlaneStraddles = inFootprint
                              && surfaceY >= lowestY - WaterlineArmPad
                              && surfaceY <= highestY + WaterlineArmPad;

            // Hysteresis around the surface: once submerged, the reference must rise a little ABOVE the
            // surface to flip back (and vice versa), so a crest bobbing across the waterline can't toggle
            // the whole fog on and off every frame.
            float threshold = _wasCameraSubmerged ? surfaceY + SubmergeHysteresis : surfaceY - SubmergeHysteresis;
            if (referenceY >= threshold) { _wasCameraSubmerged = false; return false; }

            _wasCameraSubmerged = inFootprint;
            return inFootprint;
        }

        // Fold one near-plane corner's world height into the running [lowest, highest] span.
        static void AccumulateNearPlaneCorner(Camera cam, float viewportX, float viewportY, float near,
                                              ref float lowestY, ref float highestY)
        {
            float y = cam.ViewportToWorldPoint(new Vector3(viewportX, viewportY, near)).y;
            lowestY = Mathf.Min(lowestY, y);
            highestY = Mathf.Max(highestY, y);
        }

        // World-space surface height at the camera's xz. Open water bobs with the large swell (analytic
        // + FFT), the dominant partial-submersion motion; pools / bounded bodies use the rest plane
        // (their wind-wave detail is small and the pond fog is box-clipped anyway).
        float SurfaceHeightAtCamera()
        {
            Camera cam = targetCamera;
            if (cam == null) return VolumeCenter.y;
            Vector3 p = cam.transform.position;
            float y = VolumeCenter.y;
            if (!openWater) return y;
            // Fog gate: use the latest FFT height readback (~1-2 frames stale; tolerable because the fog
            // shader's per-pixel waterline is already current and reads the same FFT surface - the gate only
            // arms the pass). Falls back to the plain field / analytic sample when the readback isn't
            // available (non-FFT body, first frames, or the camera outside the readback region).
            if (OceanFftActive && _oceanFft.TrySampleHeightLatest(p.x, p.z, out float fftHeight))
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
                y += LargeWaveField.ApplyShoreToFftSample(new Vector3(fftHeight, 0f, 0f),
                         p.x, p.z, _waveTime, SwellWavelength, ShoreWaveCtx).x
                     * LargeWaveEdgeWeight(p.x, p.z);
            else
                y += SampleLargeWaveField(p.x, p.z).x;
            return y;
        }

        // Hysteresis half-band (world units) around the surface for the camera-submerged flag.
        const float SubmergeHysteresis = 0.05f;
        // Vertical pad (world units) on the near-plane span for the waterline-straddle test, so the
        // meniscus band is already armed when it touches the screen edge rather than popping in.
        const float WaterlineArmPad = 0.1f;
        bool _wasCameraSubmerged;
    }
}
