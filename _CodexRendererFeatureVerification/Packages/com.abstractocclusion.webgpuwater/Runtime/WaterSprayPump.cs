// WebGpuWater - water-driven spray emitter ("spray pump").
//
// Floats one or more probe points on the water surface and throws spray through the shared
// WaterSplashEmitter wherever a probe and the surface under it move sharply against each other. Unlike
// WaterSplash (which fires only when a Rigidbody punches DOWN through the waterline, so a stationary
// object stays silent), this reads the water's motion relative to each tracked point.
//
// Each probe carries its own mode, so one object can mix a Boat-mode bow row with Rock-mode points:
//   - Rock : reacts to how fast the WATER rises toward the point (incoming waves/ripples included) -
//            a fixed rock or pier throwing spray as a wave slams it.
//   - Boat : reacts to the point driving into the water - both a vertical plunge AND, via
//            horizontalPlowWeight, horizontal speed across flat water (bow spray) - sampled against the
//            analytic surface only, so a hull's own emitted wake can't re-trigger it.
//   - Both : fires on either source (rising water OR a moving point).
//
// Step 7 of the "WOW pass": flat-water plow. A bow gliding fast over calm water has no vertical motion,
// so the earlier closing-speed signal missed it; horizontalPlowWeight scales the point's own horizontal
// ground speed into the Boat/Both trigger (0 = off, vertical motion only).
//
// PETALS. A probe carrying an outwardLocal direction throws an ARC instead of a ring, centred on that
// direction and turned toward astern by the rake. Astern is measured PER PROBE from its own motion, so a
// hard turn shears the flower - inner and outer probes rake differently. That is wanted; it reads as
// alive. A probe with a zero direction keeps the full ring, unchanged, all the way to the spawn kernel.
//
// All same-sampling probes are gathered into one batched WaterVolume.SampleHeights call into reused
// buffers (at most two: ripples-included and analytic-only), so an N-probe pump allocates nothing per frame.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>What a spray probe reacts to. See <see cref="WaterSprayPump"/> for the per-mode signal.</summary>
    public enum WaterSprayMode
    {
        Both, // default: rising water OR a moving point
        Boat, // the point driving into the water (plunge + horizontal plow); analytic surface only
        Rock, // the water rising toward a (near-)static point; interactive ripples included
    }

    /// <summary>How a probe emits once its trigger holds. See <see cref="WaterSprayPump"/>.</summary>
    public enum WaterSprayEmission
    {
        Burst,      // default: discrete splashes paced by the emit cooldown
        Continuous, // steady stream while the trigger holds, rate scaling with the trigger speed
    }

    /// <summary>Which vertical movement of the water can drive a Rock or Both probe.</summary>
    public enum WaterSprayWaterMotion
    {
        Rising,  // default: a wave climbing into the probe
        Falling, // water receding away from the probe
        Both,    // either direction of water motion
    }

    /// <summary>Why a probe did or did not spray on a given frame.</summary>
    /// <remarks>Every one of these reads as the same thing on screen - no spray - so they are named and
    /// counted rather than inferred. Returned unconditionally (an enum return costs nothing over void)
    /// but only recorded in the editor.</remarks>
    internal enum SprayProbeGate
    {
        Fired,
        OutsideBody,   // the batched query came back invalid: this probe is off the water body entirely
        NoHistory,     // fewer than two frames of motion to difference
        OutOfBand,     // further than surfaceBand from the waterline
        CoolingDown,
        Accumulating,  // continuous probe: trigger holds, fractional emit budget below one emit
        NoEmitter,
        BelowMinSpeed,
    }

    /// <summary>Why a probe did or did not ask its emitter for a crown on its latest emit.</summary>
    internal enum SprayCrownGate
    {
        NotTriggered,
        NoEmitter,
        NoCrownParticles,
        BelowCrownStrength,
        BelowContinuousCrownTrigger,
        WaitingForContinuousCrownCadence,
        Requested,
    }

    [DisallowMultipleComponent]
    public class WaterSprayPump : MonoBehaviour
    {
        // ---- serialized defaults (named so no literal is buried in the field initializers) ----
        const float DefaultSurfaceBand = 0.25f;
        const float DefaultMinImpactSpeed = 0.6f;
        const float DefaultMaxImpactSpeed = 4.0f;
        const float DefaultEmitCooldownSeconds = 0.06f;
        const float DefaultSprayRadius = 0.25f;
        const float DefaultPlowWeight = 0.5f;
        const float DefaultContinuousPlowMultiplier = 2f;
        const float DefaultContinuousCrownTriggerStrength = 0.75f;
        const float DefaultContinuousCrownRate = 0.75f;
        const float DefaultTurnRateForFullResponse = 90f;
        const float DefaultTurnOutsideAmountBoost = 1f;
        const float DefaultTurnOutsideSpawnOffset = 0.2f;
        const float MinTurnRateForFullResponse = 0.01f;
        // Continuous emission defaults + shaping. The rate floor keeps a just-triggered probe
        // audible instead of one emit every few seconds; the accumulator cap stops a hitched
        // frame from banking a machine-gun volley.
        const float DefaultContinuousRate = 10f;          // emits/sec at full trigger strength
        const float DefaultContinuousAmountScale = 0.35f; // droplet-count scale of each continuous emit
        const float ContinuousRateAtThreshold = 0.25f;    // fraction of the rate right at min speed
        const float ContinuousAccumulatorMax = 2f;        // most emits a single frame can owe
        // Petal defaults chosen so ADDING these fields changes nothing: a full ring, no rake, no spin.
        // The arc bounds are internal so the Water Wizard's hull fit offers the same range this
        // inspector accepts, instead of a copy of the numbers that could drift out of it.
        internal const float FullRingDegrees = 360f;
        internal const float MinPetalArcDegrees = 10f;
        const float MaxPetalSpinDegrees = 180f;
        // Elevation is an OFFSET on the emitter's own launch angle, so the useful range is asymmetric:
        // straight up is the ceiling, and the floor only has to reach far enough to flatten a lively
        // emitter back toward horizontal (the burst path clamps the total at horizontal anyway).
        internal const float MinPetalElevationDegrees = -45f;
        internal const float MaxPetalElevationDegrees = 90f;
        // Below this squared length a direction is unusable - an unset probe direction, or a hull that
        // has not moved this frame - and the petal falls back to straight out (or, for the probe's own
        // direction, to the legacy full ring).
        const float MinPetalLengthSquared = 1e-8f;

        // Per-probe spray amount is stored as a boost ABOVE the base, so the field's serialized default of
        // zero means "no change". A plain multiplier can't work here: C# struct fields can't carry a
        // default of 1, and every probe already serialized (or added by growing the array) would come back
        // as 0 and silently mute its spray. effectiveScale = BaseAmountScale + amountBoost, floored at zero.
        const float BaseAmountScale = 1.0f;
        const float MinAmountScale = 0f;     // a negative boost past -1 must not go negative; 0 = muted probe
        const float MinAmountBoost = -1.0f;  // inspector range floor: -1 mutes this probe
        const float MaxAmountBoost = 4.0f;   // inspector range ceiling: +4 -> 5x the droplet count

        // ---- internal guards ----
        // Below this frame time the finite-difference speeds are numerically unstable: a single hitched
        // frame would read as an enormous impact and fire a false burst, so such frames are skipped.
        const float MinFrameDeltaSeconds = 1e-4f;
        // Floors the min..max span so a misconfigured maxImpactSpeed <= minImpactSpeed can't divide by zero.
        const float MinImpactSpeedSpan = 1e-3f;
        // The trigger only needs the surface height; skipping Normal/Velocity skips their per-point work.
        const WaterQueryFields TriggerFields = WaterQueryFields.Height;

        /// <summary>One probe: a local-space point and what it reacts to.</summary>
        [System.Serializable]
        public struct SprayProbe
        {
            [Tooltip("Local-space offset from this object's origin where the surface is sampled and spray is thrown.")]
            public Vector3 localOffset;

            [Tooltip("Boat = point driving into water, plunge + plow (own wake ignored); Rock = water rising " +
                     "at a static point (ripples included); Both = either.")]
            public WaterSprayMode mode;

            [Tooltip("Burst = discrete splashes paced by the emit cooldown (impacts, wave slams). " +
                     "Continuous = a steady stream while the trigger holds, its rate scaling with " +
                     "speed - a planing bow sheet, a rock in a standing bore. The crown/jet accent " +
                     "plays only on the first continuous emit strong enough to produce it.")]
            public WaterSprayEmission emission;

            [Tooltip("Which vertical water movement can trigger the water-driven part of Rock or Both: " +
                     "Rising = a wave climbing into the probe, Falling = receding water, Both = either. " +
                     "Boat probes use their own descent and plow signal regardless of this setting.")]
            public WaterSprayWaterMotion waterMotion;

            [Tooltip("Emit even while this probe sits outside Surface Band - for probes that ride " +
                     "above the waterline (spray rails, a planing bow lifting out) or plunge deep. " +
                     "The spray still spawns AT the waterline under the probe; only the height gate " +
                     "is skipped, so the trigger speeds keep working unchanged.")]
            public bool ignoreSurfaceBand;

            [Tooltip("Extra spray VOLUME for THIS probe: scales the droplet count only - launch speed, " +
                     "droplet size and spread stay identical, so the spray flies the same at any boost. " +
                     "0 = base, 0.5 = +50% droplets (e.g. a denser bow row), -1 mutes this probe.")]
            [Range(MinAmountBoost, MaxAmountBoost)]
            [UnityEngine.Serialization.FormerlySerializedAs("sizeBoost")]
            public float amountBoost;

            [Tooltip("Local-space horizontal direction this probe throws toward - out of the hull. " +
                     "ZERO means the legacy full-ring burst, which is what every probe placed before " +
                     "object fitting does. Set by Water Wizard > Fit Spray To Object.")]
            public Vector3 outwardLocal;
        }

        [Header("Probes")]
        [Tooltip("The probe points. One behaves like a single jet; a row along a bow or a ring around a rock " +
                 "reads as a sheet. Use Water Wizard > Fit Spray To Object to generate and configure them globally.")]
        [SerializeField] SprayProbe[] probes = { new SprayProbe { localOffset = Vector3.zero, mode = WaterSprayMode.Both } };

        [Tooltip("Only spray while a probe sits within this vertical distance (world units) of the surface, " +
                 "so a point held in mid-air or dragged deep underwater stays silent.")]
        [Min(0f)] [SerializeField] float surfaceBand = DefaultSurfaceBand;

        [Header("Trigger")]
        [Tooltip("Trigger speed (world units/sec) below which nothing sprays. Interpreted per the probe's mode.")]
        [Min(0f)] [SerializeField] float minImpactSpeed = DefaultMinImpactSpeed;

        [Tooltip("Trigger speed that produces the strongest spray; faster impacts clamp to full strength.")]
        [Min(0f)] [SerializeField] float maxImpactSpeed = DefaultMaxImpactSpeed;

        [Tooltip("Flat-water bow spray: scales the point's own horizontal ground speed into the Boat/Both " +
                 "trigger, so a hull gliding fast across calm water sprays with no vertical motion. 0 = off.")]
        [Min(0f)] [SerializeField] float horizontalPlowWeight = DefaultPlowWeight;

        [Tooltip("Extra horizontal-plow response for Continuous Boat/Both probes. Keeps a planing bow " +
                 "spraying at practical boat speeds without making one-shot impact bursts too sensitive.")]
        [Min(0f)] [SerializeField] float continuousPlowMultiplier = DefaultContinuousPlowMultiplier;

        [Tooltip("Minimum seconds between two bursts from ONE probe, so a sustained impact doesn't emit every frame.")]
        [Min(0f)] [SerializeField] float emitCooldownSeconds = DefaultEmitCooldownSeconds;

        [Header("Spray")]
        [Tooltip("World radius of each spray burst passed to the emitter.")]
        [Min(0f)] [SerializeField] float sprayRadius = DefaultSprayRadius;

        [Header("Continuous emission (probes set to Continuous)")]
        [Tooltip("Emits per second from a Continuous probe at FULL trigger strength; right at the " +
                 "trigger threshold the rate falls to a quarter of this. Each emit is a small burst, " +
                 "so the stream reads as a sheet, not a strobe.")]
        [Range(1f, 30f)] [SerializeField] float continuousRatePerSecond = DefaultContinuousRate;
        [Tooltip("Droplet-count scale of EACH continuous emit (multiplies the probe's own Amount " +
                 "Boost). Small values at a steady rate spread a burst's volume through time.")]
        [Range(0.05f, 1f)] [SerializeField] float continuousAmountScale = DefaultContinuousAmountScale;
        [Tooltip("Normalized trigger strength required before a Continuous probe starts its Crown/jet accents. " +
                 "Droplet spray starts below this as usual. 1 waits for full trigger strength.")]
        [Range(0f, 1f)] [SerializeField] float continuousCrownTriggerStrength = DefaultContinuousCrownTriggerStrength;
        [Tooltip("Crown/jet accents per second from EACH Continuous probe once it reaches Crown Trigger Strength. " +
                 "This is paced independently from the droplet stream, keeping a boat wake alive without a crown per droplet emit.")]
        [Range(0.1f, 10f)] [SerializeField] float continuousCrownRatePerSecond = DefaultContinuousCrownRate;

        [Header("Turning wake")]
        [Tooltip("Yaw speed in degrees per second at which the outside side of a turning hull reaches its full spray response.")]
        [Min(MinTurnRateForFullResponse)] [SerializeField] float turnRateForFullResponse = DefaultTurnRateForFullResponse;

        [Tooltip("Extra droplet volume on the OUTSIDE of a turn. 1 adds 100% at the full turn rate; the inside side is unchanged.")]
        [Min(0f)] [SerializeField] float turnOutsideAmountBoost = DefaultTurnOutsideAmountBoost;

        [Tooltip("Moves the OUTSIDE burst away from the hull at the full turn rate, so its droplets begin beyond the boat exclusion volume.")]
        [Min(0f)] [SerializeField] float turnOutsideSpawnOffset = DefaultTurnOutsideSpawnOffset;

        [Header("Petals")]
        [Tooltip("Width of each burst's wedge. 360 is the full ring every splash threw before hull " +
                 "fitting; narrow it and a probe throws a petal instead. Needs a probe direction, which " +
                 "Water Wizard > Fit Spray To Object writes.")]
        [Range(MinPetalArcDegrees, FullRingDegrees)]
        [SerializeField] float petalArcDegrees = FullRingDegrees;

        [Tooltip("How far each petal turns from straight out toward straight astern while the hull is " +
                 "barely moving. 0 = straight out.")]
        [Range(0f, 1f)] [SerializeField] float petalRakeAtRest;

        [Tooltip("The same at full trigger speed. Raise it above Rake At Rest and the flower sweeps " +
                 "astern as the boat accelerates.")]
        [Range(0f, 1f)] [SerializeField] float petalRakeAtSpeed;

        [Tooltip("Flat extra rotation applied to every petal after the rake, for a deliberately " +
                 "asymmetric look. 0 = none.")]
        [Range(-MaxPetalSpinDegrees, MaxPetalSpinDegrees)]
        [SerializeField] float petalSpinDegrees;

        [Tooltip("Lift every burst toward vertical, ON TOP of the launch angle the emitter's Upward " +
                 "Bias and Outward Spread already give it. 0 = unchanged; 90 tops out straight up; " +
                 "negative flattens it toward horizontal. Unlike the other petal knobs this needs no " +
                 "probe direction, so it lifts full-ring probes too.")]
        [Range(MinPetalElevationDegrees, MaxPetalElevationDegrees)]
        [SerializeField] float petalElevationDegrees;

        [Tooltip("Explicit splash emitter override. Left empty, the water body under the pump " +
                 "supplies one (WaterVolume.ResolveSplashEmitter).")]
        [SerializeField] WaterSplashEmitter emitter;

        // Reused per-frame buffers (no per-frame allocation). Two sample buffers because Boat probes read
        // the analytic-only surface while Rock/Both read the ripple-included surface: each group is one
        // batched query, filled only when at least one probe needs it.
        Vector3[] _worldPoints;
        WaterSample[] _rippleSamples;   // interactive ripples included -> Rock, Both
        WaterSample[] _analyticSamples; // analytic surface only -> Boat
        ProbeState[] _states;
        Vector3 _previousForward;
        bool _hasForwardHistory;

#if UNITY_EDITOR
        // Editor diagnostics (compiles to nothing in a build). This counts EMITS, not droplets: it is
        // what separates "this probe never triggered" - a trigger/waterline problem - from "it triggered
        // and the particle pool dropped the burst" - a frame-budget problem. The two look identical on
        // screen and want opposite fixes.
        int[] _probeEmitCounts;

        /// <summary>Bursts each probe has successfully handed to the emitter since the buffers were built.</summary>
        internal int[] ProbeEmitCounts => _probeEmitCounts;

        SprayProbeGate[] _probeGates;
        float[] _probeBandDistances;
        float[] _probeWaterSignals;
        float[] _probeBoatSignals;
        float[] _probeTriggerSignals;
        SprayCrownGate[] _probeCrownGates;

        /// <summary>What stopped each probe on the most recent frame (or that it fired).</summary>
        internal SprayProbeGate[] ProbeGates => _probeGates;

        /// <summary>Each probe's current vertical distance from the waterline, in metres. Compare against
        /// Surface Band: a hull whose probes all read further than the band is not a trigger problem, it
        /// is a band that is too tight for how far the surface varies along the hull.</summary>
        internal float[] ProbeBandDistances => _probeBandDistances;

        /// <summary>Water-motion contribution to each probe's latest trigger decision.</summary>
        internal float[] ProbeWaterSignals => _probeWaterSignals;

        /// <summary>Boat-motion contribution to each probe's latest trigger decision.</summary>
        internal float[] ProbeBoatSignals => _probeBoatSignals;

        /// <summary>Final trigger signal after source selection for each probe.</summary>
        internal float[] ProbeTriggerSignals => _probeTriggerSignals;

        /// <summary>Why each probe did or did not request a crown on its most recent actual emit.</summary>
        internal SprayCrownGate[] ProbeCrownGates => _probeCrownGates;
#endif

        // Drop stale history so a re-enable (or leaving and re-entering the water) can't diff across the
        // missing frames and fire a phantom burst.
        void OnDisable()
        {
            if (_states != null)
                for (int i = 0; i < _states.Length; i++) _states[i] = default;
            _hasForwardHistory = false;
        }

        // LateUpdate: sample AFTER the sims have stepped this frame, so the surface reflects the current
        // waves - the same ordering WaterSplashEmitter's droplet drift relies on.
        void LateUpdate()
        {
            float deltaSeconds = Time.deltaTime;
            if (deltaSeconds < MinFrameDeltaSeconds) return;

            int count = probes != null ? probes.Length : 0;
            if (count == 0) return;
            EnsureBuffers(count);
            float signedYawRate = SampleSignedYawRate(deltaSeconds);

            for (int i = 0; i < count; i++)
                _worldPoints[i] = transform.TransformPoint(probes[i].localOffset);

            // One body for the whole cluster: a pump belongs to a single object floating on a single body.
            // Any probe outside that body's footprint comes back Valid=false and is skipped below.
            WaterVolume body = WaterVolume.BodyContaining(transform.position);
            if (body == null)
            {
                InvalidateAll();
                return;
            }

            SampleSurfaces(body, count);

            // Explicit override wins; otherwise the body the pump floats on supplies the emitter
            // (resolved once for the whole cluster, since every probe shares that one body).
            WaterSplashEmitter activeEmitter = emitter != null ? emitter : body.ResolveSplashEmitter();

            for (int i = 0; i < count; i++)
                StepProbe(i, count, deltaSeconds, signedYawRate, activeEmitter);
        }

        float SampleSignedYawRate(float deltaSeconds)
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < MinPetalLengthSquared) return 0f;
            forward.Normalize();

            if (!_hasForwardHistory)
            {
                _previousForward = forward;
                _hasForwardHistory = true;
                return 0f;
            }

            float yawDegrees = Vector3.SignedAngle(_previousForward, forward, Vector3.up);
            _previousForward = forward;
            return yawDegrees / deltaSeconds;
        }

        // At most two batched queries: one ripple-included (Rock/Both), one analytic-only (Boat). Each is
        // run only if some probe needs it, so a uniform-mode pump pays for a single query.
        void SampleSurfaces(WaterVolume body, int count)
        {
            bool needRipples = false;
            bool needAnalytic = false;
            for (int i = 0; i < count; i++)
            {
                if (probes[i].mode == WaterSprayMode.Boat) needAnalytic = true;
                else needRipples = true;
            }

            int owner = GetInstanceID();
            if (needRipples)
                body.SampleHeights(owner, 0f, _worldPoints, _rippleSamples, TriggerFields, excludeInteractiveRipples: false);
            if (needAnalytic)
                body.SampleHeights(owner, 0f, _worldPoints, _analyticSamples, TriggerFields, excludeInteractiveRipples: true);
        }

        void StepProbe(int index, int probeCount, float deltaSeconds, float signedYawRate,
                       WaterSplashEmitter activeEmitter)
        {
            WaterSprayMode mode = probes[index].mode;
            WaterSample sample = mode == WaterSprayMode.Boat ? _analyticSamples[index] : _rippleSamples[index];
            if (!sample.Valid)
            {
                _states[index].HasHistory = false; // no reading this frame: don't diff across the gap
                ResetContinuousRun(ref _states[index]);
#if UNITY_EDITOR
                _probeGates[index] = SprayProbeGate.OutsideBody;
#endif
                return;
            }

            Vector3 world = _worldPoints[index];
            float surfaceHeight = sample.Height;
#if UNITY_EDITOR
            _probeBandDistances[index] = Mathf.Abs(world.y - surfaceHeight);
            _probeGates[index] =
#endif
            TryEmit(index, probeCount, mode, world, surfaceHeight, deltaSeconds, signedYawRate, activeEmitter);

            _states[index].PreviousProbePosition = world;
            _states[index].PreviousSurfaceHeight = surfaceHeight;
            _states[index].HasHistory = true;
        }

        SprayProbeGate TryEmit(int index, int probeCount, WaterSprayMode mode, Vector3 world,
                               float surfaceHeight, float deltaSeconds, float signedYawRate,
                               WaterSplashEmitter activeEmitter)
        {
            ref ProbeState state = ref _states[index];
            bool continuous = probes[index].emission == WaterSprayEmission.Continuous;
            if (!state.HasHistory)
            {
                ResetContinuousRun(ref state);
                return SprayProbeGate.NoHistory;    // need two frames to measure a speed
            }
            if (!probes[index].ignoreSurfaceBand && Mathf.Abs(world.y - surfaceHeight) > surfaceBand)
            {
                ResetContinuousRun(ref state);
                return SprayProbeGate.OutOfBand;
            }
            // The cooldown paces BURST probes only; a continuous probe is paced by its rate
            // accumulator below and must not be silenced between emits.
            if (!continuous && Time.time < state.NextEmitTime) return SprayProbeGate.CoolingDown;
            if (activeEmitter == null)
            {
#if UNITY_EDITOR
                _probeCrownGates[index] = SprayCrownGate.NoEmitter;
#endif
                return SprayProbeGate.NoEmitter; // body has no emitter, or opts out
            }

            Vector3 previous = state.PreviousProbePosition;
            float surfaceRise = (surfaceHeight - state.PreviousSurfaceHeight) / deltaSeconds; // > 0 water rising
            float probeDescent = (previous.y - world.y) / deltaSeconds;                       // > 0 point sinking
            // The horizontal step is kept as a VECTOR, not just its length: its direction is where the
            // petal rakes to, and it is already paid for by the speed the trigger needs.
            var horizontalStep = new Vector2(world.x - previous.x, world.z - previous.z);
            float horizontalSpeed = horizontalStep.magnitude / deltaSeconds;

            float waterSignal = WaterMotionSignal(surfaceRise, probes[index].waterMotion);
            float plowMultiplier = continuous ? continuousPlowMultiplier : 1f;
            float boatSignal = Mathf.Max(0f, probeDescent)
                             + horizontalPlowWeight * plowMultiplier * horizontalSpeed;
            float signal = TriggerSignal(mode, waterSignal, boatSignal);
#if UNITY_EDITOR
            _probeWaterSignals[index] = waterSignal;
            _probeBoatSignals[index] = boatSignal;
            _probeTriggerSignals[index] = signal;
#endif
            if (signal < minImpactSpeed)
            {
                ResetContinuousRun(ref state);
                return SprayProbeGate.BelowMinSpeed;
            }

            float span = Mathf.Max(MinImpactSpeedSpan, maxImpactSpeed - minImpactSpeed);
            float strength = Mathf.Clamp01((signal - minImpactSpeed) / span);
            bool continuousCrownReady = !continuous || strength >= continuousCrownTriggerStrength;
            bool continuousCrownDue = false;
            if (continuous)
            {
                if (!continuousCrownReady)
                {
                    ResetContinuousCrownCadence(ref state);
                }
                else if (!state.HasEmittedContinuousCrown)
                {
                    continuousCrownDue = true;
                }
                else
                {
                    state.ContinuousCrownAccumulator = Mathf.Min(
                        state.ContinuousCrownAccumulator + continuousCrownRatePerSecond * deltaSeconds, 1f);
                    continuousCrownDue = state.ContinuousCrownAccumulator >= 1f;
                }
            }

            // Continuous pacing: a fractional emit budget accrues at a strength-scaled rate and one
            // emit is spent per whole unit. The fraction persists between frames, so low rates add
            // up instead of never firing; the cap keeps a hitched frame from owing a volley.
            if (continuous)
            {
                float rate = continuousRatePerSecond * Mathf.Lerp(ContinuousRateAtThreshold, 1f, strength);
                state.EmitAccumulator = Mathf.Min(state.EmitAccumulator + rate * deltaSeconds,
                                                  ContinuousAccumulatorMax);
                if (state.EmitAccumulator < 1f) return SprayProbeGate.Accumulating;
                state.EmitAccumulator -= 1f;
            }

            // Per-probe volume: a denser sheet at chosen points (a bow row) with IDENTICAL motion.
            // The boost rides EmitSplash's amountScale, which multiplies only the droplet COUNT.
            // It must NOT touch strength or radius: inside the emitter both feed the launch
            // velocity (up = f(strength), out = radius * spread * strength), so scaling them - the
            // old wiring - made droplet SPEED change with volume, quadratically and through a
            // Clamp01 saturation, which is why the boost felt untunable.
            float amountScale = Mathf.Max(MinAmountScale, BaseAmountScale + probes[index].amountBoost);
            if (continuous) amountScale *= continuousAmountScale; // small per-emit volume, steady stream
            float outsideTurnWeight = ResolveOutsideTurnWeight(index, signedYawRate);
            amountScale *= 1f + turnOutsideAmountBoost * outsideTurnWeight;
            Vector3 surfacePoint = new Vector3(world.x, surfaceHeight, world.z)
                                 + ResolveOutsideSpawnOffset(index, outsideTurnWeight);
            // strength IS the normalised trigger speed. Reusing it rather than normalising the speed a
            // second time keeps the rake tied to maxImpactSpeed instead of drifting from it.
            Vector3 petalDirection = ResolvePetalDirection(index, horizontalStep, strength);
            // Crown chunks are a distinct accent layer. Their independent cadence keeps a planing wake
            // alive, but avoids stamping one crown cloud for every droplet burst from every bow probe.
            bool allowCrown = !continuous || continuousCrownDue;
            activeEmitter.EmitSplash(surfacePoint, strength, sprayRadius, amountScale,
                                     petalDirection, petalArcDegrees, petalElevationDegrees, allowCrown);
            if (continuous)
            {
                if (allowCrown && activeEmitter.HasImpactAccentAt(strength))
                {
                    state.HasEmittedContinuousCrown = true;
                    state.ContinuousCrownAccumulator = 0f;
                }
            }
            else
            {
                state.NextEmitTime = Time.time + StaggeredCooldown(ref state, index, probeCount);
            }
#if UNITY_EDITOR
            _probeCrownGates[index] = ResolveCrownGate(activeEmitter, strength, continuous,
                                                        continuousCrownReady, continuousCrownDue);
#endif
#if UNITY_EDITOR
            _probeEmitCounts[index]++;
#endif
            return SprayProbeGate.Fired;
        }

        // A positive yaw rotates forward toward world-right. A left-side probe is therefore outside
        // that turn; the signed side test makes the response work for either steering direction.
        float ResolveOutsideTurnWeight(int index, float signedYawRate)
        {
            float fullResponseRate = Mathf.Max(MinTurnRateForFullResponse, turnRateForFullResponse);
            float signedTurn = Mathf.Clamp(signedYawRate / fullResponseRate, -1f, 1f);
            if (Mathf.Approximately(signedTurn, 0f)) return 0f;

            Vector3 outward = transform.TransformDirection(probes[index].outwardLocal);
            outward.y = 0f;
            if (outward.sqrMagnitude < MinPetalLengthSquared) return 0f;
            outward.Normalize();

            Vector3 right = transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < MinPetalLengthSquared) return 0f;
            right.Normalize();

            float side = Vector3.Dot(outward, right);
            return Mathf.Max(0f, -signedTurn * side);
        }

        Vector3 ResolveOutsideSpawnOffset(int index, float outsideTurnWeight)
        {
            if (outsideTurnWeight <= 0f || turnOutsideSpawnOffset <= 0f) return Vector3.zero;

            Vector3 outward = transform.TransformDirection(probes[index].outwardLocal);
            outward.y = 0f;
            if (outward.sqrMagnitude < MinPetalLengthSquared) return Vector3.zero;
            return outward.normalized * (turnOutsideSpawnOffset * outsideTurnWeight);
        }

        // WaterFoamParticles DROPS the burst requests past its per-frame cap rather than deferring them,
        // and the drops land on whichever probes sit late in the array - one whole side of a hull goes
        // quiet. Pushing each probe's FIRST burst out by its own fraction of the cooldown spreads the
        // array across the window once and for all: same total spray, no systematic loser.
        //
        // ONLY when the array can actually overrun the cap, though. Spreading is not free: probes that
        // fire together throw one SHEET of spray, and probes spread across the window throw a steady
        // dribble of the same total volume. A pump small enough to fit inside the budget never had a
        // problem to fix, so it keeps the sheet.
        float StaggeredCooldown(ref ProbeState state, int index, int probeCount)
        {
            if (state.HasEmitted || probeCount <= WaterFoamParticles.MaxBurstsPerFrame)
                return emitCooldownSeconds;

            state.HasEmitted = true;
            return emitCooldownSeconds * (1f + index / (float)probeCount);
        }

        // Where this probe throws: straight out of the hull, turned toward astern by the rake, then by
        // the flat spin. A probe with no direction returns ZERO, which is the legacy full-ring sentinel
        // all the way down to the spawn kernel.
        Vector3 ResolvePetalDirection(int index, Vector2 horizontalStep, float normalisedTriggerSpeed)
        {
            Vector3 outward = transform.TransformDirection(probes[index].outwardLocal);
            outward.y = 0f;
            if (outward.sqrMagnitude < MinPetalLengthSquared) return Vector3.zero;

            outward.Normalize();
            float rake = Mathf.Lerp(petalRakeAtRest, petalRakeAtSpeed, normalisedTriggerSpeed);
            Vector3 raked = RakeTowardAstern(outward, horizontalStep, rake);
            return Quaternion.AngleAxis(petalSpinDegrees, Vector3.up) * raked;
        }

        // ROTATE toward astern; never lerp-and-normalise. A linear blend collapses to the zero vector
        // when outward and astern are opposed - a transom probe while the boat backs up - and
        // normalising that yields NaN. The signed angle has no such degenerate case. A hull that has not
        // moved leaves astern undefined, so the rake falls to zero and the petal points straight out,
        // which is what a stationary hull should do.
        static Vector3 RakeTowardAstern(Vector3 outward, Vector2 horizontalStep, float rake)
        {
            if (rake <= 0f || horizontalStep.sqrMagnitude < MinPetalLengthSquared) return outward;

            Vector3 astern = new Vector3(-horizontalStep.x, 0f, -horizontalStep.y).normalized;
            float angleToAstern = Vector3.SignedAngle(outward, astern, Vector3.up);
            return Quaternion.AngleAxis(angleToAstern * Mathf.Clamp01(rake), Vector3.up) * outward;
        }

        // Keep water and boat contributions independent until source selection. Adding the signed values
        // made a falling wave cancel a fast boat's plow, so the old Both mode was neither source - it was
        // an accidental cancellation gate.
        static float TriggerSignal(WaterSprayMode mode, float waterSignal, float boatSignal)
        {
            switch (mode)
            {
                case WaterSprayMode.Rock: return waterSignal;
                case WaterSprayMode.Boat: return boatSignal;
                default:                  return Mathf.Max(waterSignal, boatSignal);
            }
        }

        static float WaterMotionSignal(float surfaceRise, WaterSprayWaterMotion waterMotion)
        {
            switch (waterMotion)
            {
                case WaterSprayWaterMotion.Falling: return Mathf.Max(0f, -surfaceRise);
                case WaterSprayWaterMotion.Both: return Mathf.Abs(surfaceRise);
                default: return Mathf.Max(0f, surfaceRise);
            }
        }

        static void ResetContinuousRun(ref ProbeState state)
        {
            state.EmitAccumulator = 0f;
            ResetContinuousCrownCadence(ref state);
        }

        static void ResetContinuousCrownCadence(ref ProbeState state)
        {
            state.ContinuousCrownAccumulator = 0f;
            state.HasEmittedContinuousCrown = false;
        }

#if UNITY_EDITOR
        static SprayCrownGate ResolveCrownGate(WaterSplashEmitter activeEmitter, float strength, bool continuous,
                                                bool continuousCrownReady, bool continuousCrownDue)
        {
            if (!activeEmitter.HasCrownParticles) return SprayCrownGate.NoCrownParticles;
            if (continuous && !continuousCrownReady)
                return SprayCrownGate.BelowContinuousCrownTrigger;
            if (continuous && !continuousCrownDue)
                return SprayCrownGate.WaitingForContinuousCrownCadence;
            return activeEmitter.IsCrownEligibleAt(strength)
                ? SprayCrownGate.Requested
                : SprayCrownGate.BelowCrownStrength;
        }
#endif

        // Grow-on-demand buffers, rebuilt only when the probe count changes (e.g. edited in the Inspector).
        void EnsureBuffers(int count)
        {
            if (_worldPoints != null && _worldPoints.Length == count) return;
            _worldPoints = new Vector3[count];
            _rippleSamples = new WaterSample[count];
            _analyticSamples = new WaterSample[count];
            _states = new ProbeState[count]; // fresh state: a resized array starts without history
#if UNITY_EDITOR
            _probeEmitCounts = new int[count];
            _probeGates = new SprayProbeGate[count];
            _probeBandDistances = new float[count];
            _probeWaterSignals = new float[count];
            _probeBoatSignals = new float[count];
            _probeTriggerSignals = new float[count];
            _probeCrownGates = new SprayCrownGate[count];
#endif
        }

        void InvalidateAll()
        {
            for (int i = 0; i < _states.Length; i++) _states[i].HasHistory = false;
        }

        // Per-probe temporal state, one entry per point. Probe position and surface height are kept
        // separately (not just their gap) so Rock reads the surface-only rate, Boat the point-only rates
        // (vertical descent and horizontal plow), independently.
        struct ProbeState
        {
            public Vector3 PreviousProbePosition;
            public float PreviousSurfaceHeight;
            public float NextEmitTime;
            public bool HasHistory;
            // Whether this probe has ever fired, so the one-off cooldown stagger is applied exactly once
            // and every burst after it keeps the plain cooldown.
            public bool HasEmitted;
            // Continuous emission: the fractional emit budget (whole units are spent as emits), and
            // Crown accents have their own rate so a boat's crown layer stays continuous without
            // matching the much denser droplet-emission cadence.
            public float EmitAccumulator;
            public float ContinuousCrownAccumulator;
            public bool HasEmittedContinuousCrown;
        }
    }
}
