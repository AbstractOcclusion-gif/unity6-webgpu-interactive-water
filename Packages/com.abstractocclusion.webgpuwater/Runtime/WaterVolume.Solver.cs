// WebGpuWater - WaterVolume partial: the solver step, its foam sources and the caustic dispatch.
//
// The GPU work for one frame, in the order it must happen: obstacle footprint, ripple sim steps
// (frame-rate-independent, debt-capped), the surf-front foam source pushed into that sim, and the
// caustic render that reads the resulting surface. Kept in one file because the ordering between
// these stages is the contract - a caustic rendered before the step shows last frame's surface.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // Static-reflection tuning (fixed for v1; promote to per-body settings if scene tuning is needed).
        // Threshold is in the solid mask's coverage units (submerged thickness, world); a low floor just
        // rejects faint silhouette edges. Rest dip is a world depression shown under a reflector, 0 = flat.
        const float ObstacleReflectSolidThreshold = 0.02f;
        const float ObstacleReflectRestDip = 0f;

        // Ripple sleep - EVERY body since 2026-08-13 (ocean-only before). One tiny max-reduction/
        // readback per interval replaces a permanent wake state once every visible part of the
        // ripple field has faded: bounded bodies used to run the full simRes^2 dispatch chain
        // forever on still water. Height-like values are authored in world metres and converted
        // to pool units at the call.
        const int RippleSleepCheckIntervalFrames = 30;
        const float RippleSleepHeightMeters = 0.001f;
        const float RippleSleepVerticalVelocityMetersPerStep = 0.00025f;
        const float RippleSleepHorizontalFlowMetersPerSecond = 0.01f;
        const float RippleSleepFoamCoverage = 0.002f;
        const float RippleSleepWetMarkMeters = 0.001f;

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
                _water.StepSimulation(effectiveWaveSpeed, effectiveDamping, rippleViscosity);

            // Exact GPU-reduced mean (no more Blit + GenerateMips: the float-mip mean silently
            // point-sampled in WebGPU builds and popped the plane; see WaterSim.compute). Skipped on
            // shoreline bodies: the open-shore boundary drain handles the edge, and averaging in the
            // zeroed dry cells would bias the "mean" and slowly sink the wet surface.
            if (conserveVolume && !bedActive) _water.ConserveVolume(conserveMaxCorrection);

            _water.UpdateNormals();

            // Wake foam (move #3): push the stamp gain to the sim so the next interactor dispatches
            // deposit foam at the hull. Zeroed when foam is off, so interactions stay copy-through.
            _water.SetWakeFoam(foam ? foamWakeStrength : 0f, foamWakeRadiusScale);
            // Wake start-force cap: clip the too-tall crest of a freshly generated wake (0 = off).
            _water.SetWakeForceCap(wakeStartForceCap);

            // Wetness memory rides this same pass, so the sim must still step when the foam LOOK is
            // off but something in the scene reads wet ground. _FoamWriteMask keeps R empty in that
            // case, so nothing draws foam that was not asked for.
            if (foam || wetnessMemory)
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
                // ...and the readings are POOL heights (world / extent.y), so a fixed world ripple
                // foamed less the deeper the body got. Carrying the gains to world units removes that,
                // and matches the threshold pair below, which is already authored in metres.
                // KNOWN TRADE-OFF: the authored gains were tuned in the pool convention, so on a deep
                // body this scales `activity` far past _FoamGenThreshold - saturate() pins gen at 1 and
                // the response reads binary until the gains (or the threshold) are re-tuned to match.
                float foamActivityScale = VolumeExtentSafe.y / Mathf.Max(_simDensityRatio, 0.05f);
                // Min wave height AND the shallow-breaking range are authored in WORLD metres; the
                // sim's heights and bed column depths are pool units, so both divide by the extent.
                PushShoreFoam(_water);    // surf-front whitewash source (inert without the surf layer)
                _water.StepFoam(foamGenRate, foamGenThreshold,
                                foamMinWaveHeight / VolumeExtentSafe.y, foamDecay,
                                residualSurvival, foamSpread, foamFromSpeed * foamActivityScale,
                                foamFromCurvature * foamActivityScale, foamAdvect,
                                _foamTimeDebt, foamDecayRate,
                                foamBreakStrength, foamBreakRange / VolumeExtentSafe.y,
                                foamCrestBias, foamDeposit, foamHeadroom,
                                WetMarkSurvivalPerStep(wetnessDryTime),
                                foam);
                _foamTimeDebt = 0f;
            }

            RequestRippleSleepCheck();
        }

        void RequestRippleSleepCheck()
        {
            if (HasContinuousRippleSource()) return;
            if (Time.frameCount % RippleSleepCheckIntervalFrames != 0) return;

            float inverseVerticalExtent = 1f / VolumeExtentSafe.y;
            _water.RequestSleepCheck(
                RippleSleepHeightMeters * inverseVerticalExtent,
                RippleSleepVerticalVelocityMetersPerStep * inverseVerticalExtent,
                RippleSleepHorizontalFlowMetersPerSecond,
                RippleSleepFoamCoverage,
                RippleSleepWetMarkMeters * inverseVerticalExtent);
        }

        // Authored DRY TIME (seconds) -> the per-reference-step survival factor the kernel decays the
        // wet mark by. "Dry" is defined as faded to exp(-3) ~ 5% of the wetted level, which is what
        // makes the authored number match what the eye calls dry; a true exponential never reaches
        // zero, so the definition has to be stated somewhere and this is it.
        //
        // NOTE this is unitless and height-independent - unlike foamMinWaveHeight beside it, there is
        // deliberately NO divide by the volume extent. A duration does not change because the body is
        // deeper, and that height-independence is the whole reason this replaced a metres/second rate.
        const float WetMarkFadeTimeConstants = 3f;
        const float MinWetMarkDryTimeSeconds = 0.05f;

        static float WetMarkSurvivalPerStep(float dryTimeSeconds)
        {
            float seconds = Mathf.Max(dryTimeSeconds, MinWetMarkDryTimeSeconds);
            return Mathf.Exp(-WetMarkFadeTimeConstants / (seconds * ReferenceFrameRate));
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
            // TWO consumers, TWO questions - do not fold them back into one flag. Active = "the
            // surf FRONT FIELD is live and readable", which is all the foam PARTICLES need: their
            // plunging-lip spray gate and their surf-height glue read the FRONT, never the
            // injection. InjectionActive = "some injection gain is non-zero", the only thing the
            // ripple sim's Foam kernel cares about. While these shared one flag, zeroing the
            // injection gains to stop double-drawn shore foam also silently switched off the one
            // breaking-crest particle path in the package.
            state.Active = shore.SurfLayerActive;
            state.InjectionActive =
                surfFoamGain + surfWaterlineFoam + surfSwashDepositGain > 0f;
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
                state.ShoalDepth = ShoreShoalDepthEffective;
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
                state.SwashMaxSlopeTan = surfSwashMaxSlopeTan;
                state.SwashDepositGain = surfSwashDepositGain;
            }
            return state;
        }

        /// <summary>Which frame this body's caustic RT is written in.</summary>
        internal enum CausticFrame
        {
            /// <summary>Bounded body: projected onto the pool floor, indexed by ProjectCausticUV.
            /// Zero so an unpublished body reads as the original pool behaviour.</summary>
            Pool = 0,
            /// <summary>Windowed ocean: projected onto the shared reference plane and indexed in the
            /// sim window's world frame, since a moving window has no fixed floor.</summary>
            Window = 1,
            /// <summary>Windowed but not an ocean clipmap. NOTHING draws into the RT for these - it is
            /// allocated in the caustic pass's constructor and never even cleared - so its contents are
            /// undefined and every consumer must contribute its identity instead of sampling it.</summary>
            None = 2,
        }

        // Choose the caustic path for this body: bounded bodies use the pool caustic (projected onto
        // the pool floor); the windowed OCEAN uses the large-body caustic (projected in the sim-window's
        // world frame, since a moving window has no fixed floor). Other windowed bodies still skip
        // caustics - the pool projection would be mismapped over their scrolling window.
        //
        // ONE decision, read twice: here to pick the generator, and by WaterUniformPublisher to tell
        // WaterCausticProjection.shader how to undo the projection it is about to sample. Re-deriving
        // it shader-side from _LargeBody/_SimWindowed would drop the unboundedOcean term and could
        // classify a body the generator classified differently.
        internal CausticFrame CausticProjectionFrame
        {
            get
            {
                if (!_windowed) return CausticFrame.Pool;
                return IsOceanClipmap ? CausticFrame.Window : CausticFrame.None;
            }
        }

        void RenderCausticsForThisBody()
        {
            CausticFrame frame = CausticProjectionFrame;
            if (frame == CausticFrame.Pool) { RenderCaustics(); return; }
            if (frame == CausticFrame.Window) { RenderLargeBodyCaustics(); return; }
            // CausticFrame.None: nothing draws. See the enum member for why that is not an oversight.
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
    }
}
