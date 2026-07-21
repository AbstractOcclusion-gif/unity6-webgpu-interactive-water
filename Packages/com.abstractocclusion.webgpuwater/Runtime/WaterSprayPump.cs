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

        // Per-probe spray scale is stored as a boost ABOVE the base, so the field's serialized default of
        // zero means "no change". A plain multiplier can't work here: C# struct fields can't carry a
        // default of 1, and every probe already serialized (or added by growing the array) would come back
        // as 0 and silently mute its spray. effectiveScale = BaseSprayScale + sizeBoost, floored at zero.
        const float BaseSprayScale = 1.0f;
        const float MinSprayScale = 0f;      // a negative boost past -1 must not invert radius/velocity
        const float MinSizeBoost = -1.0f;    // inspector range floor: -1 mutes this probe
        const float MaxSizeBoost = 4.0f;     // inspector range ceiling: +4 -> 5x base

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

            [Tooltip("Extra spray for THIS probe above the pump base, scaling both burst size and intensity. " +
                     "0 = base, 0.5 = +50% (e.g. a beefier bow row), -1 mutes this probe.")]
            [Range(MinSizeBoost, MaxSizeBoost)]
            public float sizeBoost;
        }

        [Header("Probes")]
        [Tooltip("The probe points. One behaves like a single jet; a row along a bow or a ring around a rock " +
                 "reads as a sheet. Use the Auto-place button (below) to populate from the object's geometry.")]
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

        [Tooltip("Minimum seconds between two bursts from ONE probe, so a sustained impact doesn't emit every frame.")]
        [Min(0f)] [SerializeField] float emitCooldownSeconds = DefaultEmitCooldownSeconds;

        [Header("Spray")]
        [Tooltip("World radius of each spray burst passed to the emitter.")]
        [Min(0f)] [SerializeField] float sprayRadius = DefaultSprayRadius;

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

        // Drop stale history so a re-enable (or leaving and re-entering the water) can't diff across the
        // missing frames and fire a phantom burst.
        void OnDisable()
        {
            if (_states == null) return;
            for (int i = 0; i < _states.Length; i++) _states[i] = default;
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
                StepProbe(i, deltaSeconds, activeEmitter);
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

        void StepProbe(int index, float deltaSeconds, WaterSplashEmitter activeEmitter)
        {
            WaterSprayMode mode = probes[index].mode;
            WaterSample sample = mode == WaterSprayMode.Boat ? _analyticSamples[index] : _rippleSamples[index];
            if (!sample.Valid)
            {
                _states[index].HasHistory = false; // no reading this frame: don't diff across the gap
                return;
            }

            Vector3 world = _worldPoints[index];
            float surfaceHeight = sample.Height;
            TryEmit(index, mode, world, surfaceHeight, deltaSeconds, activeEmitter);

            _states[index].PreviousProbePosition = world;
            _states[index].PreviousSurfaceHeight = surfaceHeight;
            _states[index].HasHistory = true;
        }

        void TryEmit(int index, WaterSprayMode mode, Vector3 world, float surfaceHeight, float deltaSeconds,
                     WaterSplashEmitter activeEmitter)
        {
            ref ProbeState state = ref _states[index];
            if (!state.HasHistory) return;                             // need two frames to measure a speed
            if (Mathf.Abs(world.y - surfaceHeight) > surfaceBand) return; // not at the waterline
            if (Time.time < state.NextEmitTime) return;               // cooling down
            if (activeEmitter == null) return;                        // body has no emitter (or opts out): nothing to emit through

            Vector3 previous = state.PreviousProbePosition;
            float surfaceRise = (surfaceHeight - state.PreviousSurfaceHeight) / deltaSeconds; // > 0 water rising
            float probeDescent = (previous.y - world.y) / deltaSeconds;                       // > 0 point sinking
            float horizontalSpeed =
                new Vector2(world.x - previous.x, world.z - previous.z).magnitude / deltaSeconds;

            float signal = TriggerSignal(mode, surfaceRise, probeDescent, horizontalPlowWeight * horizontalSpeed);
            if (signal < minImpactSpeed) return;

            float span = Mathf.Max(MinImpactSpeedSpan, maxImpactSpeed - minImpactSpeed);
            float strength = Mathf.Clamp01((signal - minImpactSpeed) / span);

            // Per-probe boost lets one pump throw a bigger, more intense sheet at chosen points (a bow row)
            // while the rest stay at base. Same style: it scales the burst, not the emitter's foam profile.
            float sprayScale = Mathf.Max(MinSprayScale, BaseSprayScale + probes[index].sizeBoost);
            Vector3 surfacePoint = new Vector3(world.x, surfaceHeight, world.z);
            activeEmitter.EmitSplash(surfacePoint, strength * sprayScale, sprayRadius * sprayScale);
            state.NextEmitTime = Time.time + emitCooldownSeconds;
        }

        // Rock keys off the water alone (a static probe's own motion shouldn't matter); Boat keys off the
        // point driving in - its vertical plunge plus the horizontal plow term; Both is their union.
        static float TriggerSignal(WaterSprayMode mode, float surfaceRise, float probeDescent, float horizontalPlow)
        {
            switch (mode)
            {
                case WaterSprayMode.Rock: return surfaceRise;
                case WaterSprayMode.Boat: return probeDescent + horizontalPlow;
                default:                  return surfaceRise + probeDescent + horizontalPlow;
            }
        }

        // Grow-on-demand buffers, rebuilt only when the probe count changes (e.g. edited in the Inspector).
        void EnsureBuffers(int count)
        {
            if (_worldPoints != null && _worldPoints.Length == count) return;
            _worldPoints = new Vector3[count];
            _rippleSamples = new WaterSample[count];
            _analyticSamples = new WaterSample[count];
            _states = new ProbeState[count]; // fresh state: a resized array starts without history
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
        }
    }
}
