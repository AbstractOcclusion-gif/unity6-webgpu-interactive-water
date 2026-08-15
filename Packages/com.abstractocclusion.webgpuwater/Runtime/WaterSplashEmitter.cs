// WebGpuWater - shared splash particle emitter (Unity 6 / URP port)
// Owns (or references) a real Particle System so the splash is fully editable in
// the Inspector: the builder creates one "Water Splash FX" root (this component)
// with two children - "Droplet Spray (CPU Fallback)" (Shuriken droplets, only
// bursts on bodies WITHOUT an active GPU WaterFoamParticles) and "Crown Ring"
// (a cloud of photographic chunk sprites, always plays). Swap the droplet texture on the fallback's
// ParticleSystemRenderer material. Both object impacts
// (WaterSplash) and the mouse interaction (WaterVolume) emit through this.
//
// Droplets pop, then stick to the water surface and DRIFT with the waves: they
// launch ballistically (low gravity), and once they reach the live waterline they
// snap to it and are carried along the local surface flow, reacting as ripples
// pass under them. The drift is driven on the CPU from WaterVolume's height
// readback, so it tracks the same surface the shader renders.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    public class WaterSplashEmitter : MonoBehaviour
    {
        // Below this depth past the surface a droplet is considered "landed".
        const float SurfaceContactBand = 0.01f;

        // EmitSplash amountScale: 1 = the caller's burst at its authored size, 0 = fully muted.
        // The scale touches ONLY the droplet count, never the launch speeds, so "more spray"
        // and "faster spray" stay independent knobs (per-probe pump boosts rely on this).
        const float BaseAmountScale = 1f;
        const float MutedAmountScale = 0f;

        // Crown size mapping: base size scales between these factors with impact
        // strength, plus a contribution from the impact radius.
        const float CrownMinSizeFactor = 0.6f;
        const float CrownMaxSizeFactor = 1.4f;
        const float CrownRadiusContribution = 0.5f;

        // ---- burst shaping (EmitSplash) ----
        // The jitter/ring/height constants MUST match the BURST_* consts in
        // WaterFoamParticles.compute (the GPU spray path both look-alike) - guarded by
        // WaterWaveConstantsValidator, so a retune on either side is reported on editor load.
        const int MinBurstCount = 3;                  // even the softest splash reads as a few droplets
        const float OutwardJitterMin = 0.4f;          // per-droplet randomisation of the outward throw
        const float OutwardJitterMax = 1f;
        const float UpwardJitterMin = 0.5f;           // per-droplet randomisation of the upward pop
        const float UpwardJitterMax = 1.2f;
        const float UpwardStrengthFloor = 0.4f;       // soft splashes still pop a little...
        const float UpwardStrengthGain = 0.6f;        // ...and strong ones scale the rest of the way
        const float SpawnRingRadiusScale = 0.5f;      // droplets spawn inside half the impact radius
        const float SpawnHeightAboveSurface = 0.01f;  // just above the waterline so they never spawn submerged
        const float MinOutwardStrength = 0.4f;        // horizontal throw floor for soft splashes
        const float SizeJitterMin = 0.6f;             // per-droplet size randomisation
        const float SizeJitterMax = 1.3f;
        // ---- petal arc ----
        // Below this squared length a caller's direction is the ZERO SENTINEL and the burst is the
        // legacy full ring. Mirrored by BURST_DIR_MIN_SQ in the compute so the GPU and Shuriken
        // paths agree on which bursts are petals; a fork there and the look depends on whether a
        // body happens to have a GPU pool.
        const float BurstDirectionMinSquared = 1e-8f;
        const float FullRingDegrees = 360f;
        // Below this the elevation is "unset" and the velocity is passed through untouched, so a burst
        // authored before the tilt existed stays exactly what it was. Mirrors BURST_ELEVATION_MIN.
        const float BurstElevationMinRadians = 1e-5f;
        // Straight up (pi/2, written as a literal so the constants validator can parse it). The tilt ADDS
        // to the angle upwardBias/outwardSpread already imply, so it is capped rather than allowed past
        // vertical. Mirrors BURST_MAX_ELEVATION.
        const float MaxBurstElevationRadians = 1.5707963f;
        // Droplet opacity scales with impact strength (DWP2's velocity-proportional spray:
        // emission AND alpha ride the object's speed) - a slow entry dribbles faint droplets,
        // a hard slam throws an opaque sheet. Floor keeps soft splashes visible.
        const float AlphaStrengthFloor = 0.45f;
        const float AlphaStrengthGain = 0.55f;

        // ---- drift particle-system defaults (ConfigureForDrift) ----
        const float DriftGravityModifier = 0.4f;      // low gravity: droplets drift rather than dive
        const float DriftStartLifetime = 0.5f;
        const float DriftStartSize = 0.02f;
        static readonly Color DriftStartColor = new Color(0.9f, 0.97f, 1.0f, 0.9f);
        const int DriftMaxParticles = 2000;
        const float DriftVelocityDampen = 0.2f;       // slows droplets so they settle onto the surface
        const float DriftVelocityDrag = 1.5f;
        const float DriftFadeStartFraction = 0.6f;    // alpha holds until this fraction of life, then fades
        // Stretched-billboard droplets (KWS splash): fast droplets elongate along their motion
        // into streaks/jets while settled drifters stay near-round. velocityScale adds length
        // per unit speed; lengthScale keeps the at-rest sprite unstretched.
        const float DropletStretchVelocityScale = 0.06f;
        const float DropletStretchLengthScale = 1f;

        // ---- crown particle-system defaults (ConfigureCrown) ----
        // The crown is a CLOUD of photographic chunk sprites (KWS WaterSplashes.prefab
        // droplet layer), not a single flipbook card. Gravity/drag/tumble are the measured
        // KWS prefab values (docs/RESEARCH_kws_splash_definition_2026-08-06.md section 3).
        const float CrownStartLifetime = 0.5f;
        const float CrownStartSize = 0.4f;
        static readonly Color CrownStartColor = new Color(0.95f, 0.98f, 1.0f, 1.0f);
        const int CrownMaxParticles = 256;            // bursts are sprite CLOUDS now, not 1 card
        const float CrownGravityModifier = 1.2f;      // chunks arc over and fall (KWS 1..1.5)
        const float CrownVelocityDampen = 0.03f;      // air drag (KWS LimitVelocity dampen)
        const float CrownTumbleMaxDegrees = 30f;      // slow random spin, +/- deg per second
        // Size-over-lifetime pop: a chunk reaches CrownPopFraction of its size within
        // CrownPopTime of its life, then grows linearly to full size (KWS pop shape).
        const float CrownPopTime = 0.04f;
        const float CrownPopFraction = 0.5f;

        // ---- chunk-cloud burst shaping (EmitCrown) ----
        const int CrownBurstMinCount = 6;             // a threshold hit still reads as a cloud
        const int CrownBurstMaxCount = 16;            // full-strength slam
        const float CrownUpSpeedMin = 0.5f;           // vertical throw at threshold strength...
        const float CrownUpSpeedMax = 2.0f;           // ...and at full strength
        const float CrownOutSpeedMax = 0.8f;          // horizontal scatter at full strength
        const float CrownSizeJitterMin = 0.5f;        // per-sprite size randomisation...
        const float CrownSizeJitterMax = 1.1f;
        const float CrownHeroSizePower = 10f;         // ...pow-shaped so a RARE sprite lands
        const float CrownHeroSizeBonus = 1.5f;        //    near hero size (KWS pow10 distro)
        const float CrownLifetimeJitterMin = 0.75f;   // per-sprite life spread (KWS 0.75..1.25)
        const float CrownLifetimeJitterMax = 1.25f;

        // ---- vertical jet layer (KWS WaterSplashes layer A) ----
        // This is deliberately separate from the CPU fallback droplets: GPU-spray bodies do
        // not emit that fallback, yet they still need the unmistakable entry columns.
        const int JetBurstMinCount = 4;
        const int JetBurstMaxCount = 6;
        const float JetUpSpeedMin = 0.5f;
        const float JetUpSpeedMax = 2f;
        const float JetOutSpeedMax = 0.2f;
        const float JetSizeMin = 0.35f;
        const float JetSizeMax = 0.5f;
        const float JetLifetimeMin = 0.75f;
        const float JetLifetimeMax = 1.5f;
        const int JetMaxParticles = 96;
        const float JetDefaultGravityModifier = 1f;
        const float JetVelocityDampen = 0.2f;
        const float JetVelocityDrag = 1.5f;
        const float JetStretchVelocityScale = 0.4f;
        const float JetStretchLengthScale = 4f;
        const float UnityGravityMetersPerSecondSquared = 9.81f;
        const float DefaultWaterParticleGravity = 1f;

        [Tooltip("The particle system to emit from. Auto-created if left empty.")]
        [SerializeField] internal ParticleSystem particles;
        [Tooltip("Optional master foam profile: when assigned, its Splash section overrides " +
                 "the burst/crown fields below on every emit. None = this component's own values.")]
        [SerializeField] internal WaterFoamProfile profile;

        [Header("Burst shaping")]
        [Range(1, 128)] [SerializeField] internal int maxParticlesPerBurst = 48;
        [Tooltip("Upward launch bias. Higher = droplets jump more before settling.")]
        [Range(0f, 3f)] [SerializeField] internal float upwardBias = 1.0f;
        [Tooltip("Outward (horizontal) spread, so droplets drift across the surface.")]
        [Range(0f, 3f)] [SerializeField] internal float outwardSpread = 1.3f;
        [SerializeField] internal float dropletSize = 0.02f;
        [SerializeField] internal Vector2 lifetime = new Vector2(0.6f, 1.3f);

        [Header("Surface drift")]
        [Tooltip("Seconds a droplet stays ballistic (the 'pop') before it can stick.")]
        [Range(0f, 0.5f)] [SerializeField] internal float popDuration = 0.12f;
        [Tooltip("How strongly settled droplets are carried by the local wave flow.")]
        [Range(0f, 6f)] [SerializeField] internal float driftStrength = 2.0f;
        [Tooltip("Horizontal damping on drifting droplets (higher = settles sooner).")]
        [Range(0f, 8f)] [SerializeField] internal float driftDamping = 2.5f;
        [Tooltip("How high above the surface a settled droplet rides (world units).")]
        [SerializeField] internal float surfaceRideHeight = 0.004f;

        [Header("Crown splash (chunk cloud)")]
        [Tooltip("Optional chunk-sprite cloud emitted at the impact point. Leave empty to disable.")]
        [SerializeField] internal ParticleSystem crownParticles;
        [Tooltip("Minimum impact strength (0..1) that spawns a crown splash.")]
        [Range(0f, 1f)] [SerializeField] internal float crownMinStrength = 0.25f;
        [Tooltip("Base world size of the crown splash, scaled up by impact strength.")]
        [SerializeField] internal float crownBaseSize = 0.4f;
        [Tooltip("Crown lifetime; each sprite steps through the chunk atlas once over this time.")]
        [SerializeField] internal float crownLifetime = 0.5f;
        [Tooltip("Vertical launch multiplier for the crown cloud. Lower this to keep the splash close to the water.")]
        [Range(0f, 3f)] [SerializeField] internal float crownLaunchHeight = 1f;
        [Tooltip("Horizontal launch multiplier for the crown cloud. Lower this to reduce its projected spread.")]
        [Range(0f, 3f)] [SerializeField] internal float crownLaunchSpread = 1f;
        [Tooltip("Crown tint, applied per emit as the particle start color (multiplies the material).")]
        [SerializeField] internal Color crownTint = CrownStartColor;
        [Tooltip("Crown opacity multiplier on top of the tint's alpha.")]
        [Range(0f, 1f)] [SerializeField] internal float crownOpacity = 1f;
        [Tooltip("Opacity of impact droplets before the global Shared Look opacity is applied. " +
                 "Controls both GPU-routed and CPU-fallback splash droplets; ambient sea spray is separate.")]
        [Range(0f, 1f)]
        [UnityEngine.Serialization.FormerlySerializedAs("cpuFallbackOpacity")]
        [SerializeField] internal float dropletOpacity = 1f;
        [Tooltip("Optional stretched water-column layer emitted with the crown. Leave empty to disable.")]
        [SerializeField] internal ParticleSystem jetParticles;
        [Header("Entry streaks")]
        [Tooltip("Enable the narrow vertical water columns that precede the crown cloud.")]
        [SerializeField] internal bool entryStreaksEnabled = true;
        [Tooltip("Streak count and opacity multiplier. Zero disables streak emission without removing the layer.")]
        [Range(0f, 2f)] [SerializeField] internal float entryStreakAmount = 1f;
        [Tooltip("Vertical launch-speed multiplier. Higher values create a taller ballistic arc.")]
        [Range(0f, 3f)] [SerializeField] internal float entryStreakHeight = 1f;
        [Tooltip("Horizontal spread and sprite-width multiplier. Higher values make the breach read wider.")]
        [Range(0.1f, 3f)] [SerializeField] internal float entryStreakWidth = 1f;
        [Tooltip("Multiplier over Water Foam Particles > Motion > Gravity. One follows the shared particle gravity.")]
        [Range(0f, 2f)] [SerializeField] internal float entryStreakGravity = JetDefaultGravityModifier;
        [Range(0f, 1f)] [SerializeField] internal float entryStreakOpacity = 1f;
        [Range(0f, 1f)] [SerializeField] internal float entryStreakMinStrength = 0.2f;
        [SerializeField] internal Vector2 entryStreakLifetimeRange =
            new Vector2(JetLifetimeMin, JetLifetimeMax);
        [SerializeField] internal Vector2 entryStreakSizeRange = new Vector2(JetSizeMin, JetSizeMax);
        [SerializeField] internal Color entryStreakTint = CrownStartColor;

        ParticleSystem.Particle[] _buffer;

        void Awake()
        {
            if (particles == null) particles = GetComponent<ParticleSystem>();
            if (particles == null)
            {
                particles = gameObject.AddComponent<ParticleSystem>();
                ConfigureForDrift(particles);
            }
        }

        // ---- after-fog reroute (particle/fog sorting fix) --------------------------------
        // The fullscreen underwater fog runs AFTER all transparents and integrates to opaque
        // depth, so at queue time it painted the water column's fog OVER these Shuriken sprites
        // (crown ring + CPU-fallback droplets). While the fog is armed, their renderers are
        // muted (forceRenderingOff) and WaterUnderwaterFogFeature's particle pass DrawRenderers
        // them AFTER the fog instead; SplashParticles.shader prices its own camera->particle
        // fog (WaterParticleFog.hlsl). Mirrors WaterFoamParticles' reroute of the GPU quads.

        /// <summary>Live emitters, drawn by the fog feature's after-fog particle pass.</summary>
        internal static readonly System.Collections.Generic.List<WaterSplashEmitter> Live =
            new System.Collections.Generic.List<WaterSplashEmitter>();

        internal static void ResetStaticState()
        {
            for (int index = 0; index < Live.Count; index++)
            {
                WaterSplashEmitter emitter = Live[index];
                if (emitter != null) emitter.SetAfterFogReroute(false);
            }
            Live.Clear();
        }

        ParticleSystemRenderer _dropletRenderer; // lazy cache: the fallback droplets' renderer
        ParticleSystemRenderer _crownRenderer;   // lazy cache: the crown ring's renderer
        ParticleSystemRenderer _jetRenderer;     // lazy cache: the always-on vertical jet layer

        void OnEnable()
        {
            if (!Live.Contains(this)) Live.Add(this);
        }

        void OnDisable()
        {
            Live.Remove(this);
            SetAfterFogReroute(false); // never leave a muted renderer behind
        }

        // Cheap enough to run every frame (two null checks + two bool writes); re-resolves the
        // renderers lazily so systems assigned after enable are still picked up.
        void SetAfterFogReroute(bool reroute)
        {
            if (particles != null)
            {
                if (_dropletRenderer == null)
                    _dropletRenderer = particles.GetComponent<ParticleSystemRenderer>();
                if (_dropletRenderer != null) _dropletRenderer.forceRenderingOff = reroute;
            }
            if (crownParticles != null)
            {
                if (_crownRenderer == null)
                    _crownRenderer = crownParticles.GetComponent<ParticleSystemRenderer>();
                if (_crownRenderer != null) _crownRenderer.forceRenderingOff = reroute;
            }
            if (jetParticles != null)
            {
                if (_jetRenderer == null)
                    _jetRenderer = jetParticles.GetComponent<ParticleSystemRenderer>();
                if (_jetRenderer != null) _jetRenderer.forceRenderingOff = reroute;
            }
        }

        /// <summary>Issues the muted Shuriken draws into the after-fog pass. forceRenderingOff
        /// only stops the automatic queue submission - manual DrawRenderer still works, which is
        /// exactly the split this reroute needs.</summary>
        internal void DrawAfterFog(RasterCommandBuffer cmd)
        {
            if (!isActiveAndEnabled) return;
            if (_dropletRenderer != null && particles != null && particles.particleCount > 0
                && _dropletRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_dropletRenderer, _dropletRenderer.sharedMaterial, 0, 0);
            if (_crownRenderer != null && crownParticles != null && crownParticles.particleCount > 0
                && _crownRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_crownRenderer, _crownRenderer.sharedMaterial, 0, 0);
            if (_jetRenderer != null && jetParticles != null && jetParticles.particleCount > 0
                && _jetRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_jetRenderer, _jetRenderer.sharedMaterial, 0, 0);
        }

        // Pop -> stick -> drift. Runs after the controllers have stepped their sims so the
        // surface query reflects this frame's waves.
        void LateUpdate()
        {
            // After-fog reroute gate, refreshed BEFORE the early-outs below: the crown can be
            // alive while the droplet system holds zero particles, and the mute must track the
            // fog state every frame either way (see the reroute comment block above).
            SetAfterFogReroute(WaterVolume.UnderwaterFogActive);
            if (particles == null) return;
            // Idle diet: with the GPU spray path active this system usually holds ZERO droplets
            // (only the crown lives here), yet the round-trip below still copied the whole
            // Shuriken buffer both ways every frame. particleCount is a cheap property.
            if (particles.particleCount == 0) return;

            int capacity = particles.main.maxParticles;
            if (_buffer == null || _buffer.Length < capacity)
                _buffer = new ParticleSystem.Particle[capacity];

            int alive = particles.GetParticles(_buffer);
            float dt = Time.deltaTime;
            for (int i = 0; i < alive; i++)
                DriftOnSurface(ref _buffer[i], dt);
            particles.SetParticles(_buffer, alive);
        }

        // One droplet's surface behaviour. Stateless: a droplet is "settled" once it is
        // past its pop window AND at or below the local waterline; the per-frame y-snap
        // keeps it there, so it stays settled without tracking persistent flags.
        void DriftOnSurface(ref ParticleSystem.Particle droplet, float dt)
        {
            Vector3 position = droplet.position;
            // Resolve the body under THIS droplet so a splash in lake B drifts on lake B's
            // surface, not the primary's. Outside every footprint TryGetSurface returns false.
            WaterVolume body = WaterVolume.BodyContaining(position);
            if (body == null ||
                !body.TryGetSurface(position.x, position.z, out float surfaceY, out Vector2 waveDrift))
                return; // outside the pool or no readback yet: stay ballistic

            float age = droplet.startLifetime - droplet.remainingLifetime;
            bool stillPopping = age < popDuration;
            bool reachedSurface = position.y <= surfaceY + SurfaceContactBand;
            if (stillPopping || !reachedSurface)
                return; // popping upward or still falling: let the system integrate it

            // Settled: ride the live waterline (bobs as waves pass) and get carried by
            // the local flow, damped so droplets ease into the surface motion.
            position.y = surfaceY + surfaceRideHeight;

            Vector3 velocity = droplet.velocity;
            velocity.y = 0f;
            velocity += new Vector3(waveDrift.x, 0f, waveDrift.y) * (driftStrength * dt);
            velocity -= velocity * Mathf.Min(1f, driftDamping * dt);

            position += velocity * dt;
            droplet.position = position;
            droplet.velocity = velocity;
        }

        /// <summary>Emit a splash at a surface point. strength is 0..1. Droplets are thrown by
        /// the body's GPU foam-particle system when one is present (spray unification: every
        /// airborne droplet shares the KIND_SPRAY tech + look); the Shuriken system here then
        /// only throws the crown chunk cloud. Bodies without a GPU system keep the legacy
        /// Shuriken droplet burst. amountScale scales ONLY the droplet count (spray volume):
        /// launch speed, droplet size, spread and opacity are untouched, so a boosted caller
        /// throws MORE spray, never FASTER spray. 0 mutes the burst (crown included).</summary>
        /// <param name="petalDirection">World direction the burst throws toward; only its horizontal
        /// part is used. ZERO (the default) is the legacy full ring, so no existing call site moves.</param>
        /// <param name="arcDegrees">Wedge width around that direction. 360 is a full ring either way.</param>
        /// <param name="elevationDegrees">Lifts the whole burst toward vertical, on top of the angle
        /// Upward Bias and Outward Spread already imply. ZERO (the default) changes nothing, and it
        /// applies to full rings as readily as to petals.</param>
        /// <param name="allowCrown">False suppresses the crown chunk cloud for THIS emit - a
        /// continuous stream plays the crown on its first emit only. Droplets are unaffected.</param>
        public void EmitSplash(Vector3 surfacePos, float strength, float radius,
                               float amountScale = BaseAmountScale,
                               Vector3 petalDirection = default, float arcDegrees = FullRingDegrees,
                               float elevationDegrees = 0f, bool allowCrown = true)
        {
            if (particles == null) return;
            // Master profile: applied at emit time (splashes are event-driven; there is no
            // per-frame dispatch to hook like the GPU systems).
            if (profile != null) profile.ApplyTo(this);
            strength = Mathf.Clamp01(strength);
            amountScale = Mathf.Max(MutedAmountScale, amountScale);
            if (amountScale <= MutedAmountScale) return; // muted caller (e.g. a probe boosted to -1)
            int count = Mathf.Clamp(Mathf.RoundToInt(strength * maxParticlesPerBurst),
                                    MinBurstCount, maxParticlesPerBurst);
            // Amount multiplies AFTER the per-burst clamp, on purpose: maxParticlesPerBurst caps an
            // UNBOOSTED burst, and a boosted caller is meant to exceed it. Hard safety stays
            // downstream - the GPU path clamps to MaxBurstDroplets, Shuriken to main.maxParticles.
            count = Mathf.Max(1, Mathf.RoundToInt(count * amountScale));

            // Resolved ONCE here, so the GPU kernel and the Shuriken loop below can never disagree
            // about whether this burst is a petal or a ring.
            Vector2 petal = HorizontalPetal(petalDirection);
            float arcHalfRadians = 0.5f * arcDegrees * Mathf.Deg2Rad;
            float elevationRadians = elevationDegrees * Mathf.Deg2Rad;

            WaterVolume body = WaterVolume.BodyContaining(surfacePos);
            WaterFoamParticles gpuSpray = body != null ? body.GetComponent<WaterFoamParticles>() : null;
            // Body-wide particle master (WaterFoamParticles "Use Particles"): off = this body emits NO splash
            // at all - GPU droplets AND the Shuriken crown - matching the ambient foam the same switch silences.
            // A body with no foam system has no switch, so it keeps splashing as before.
            if (gpuSpray != null && !gpuSpray.UseParticles) return;
            ApplyImpactLayerGravity(gpuSpray);
            if (gpuSpray != null && gpuSpray.isActiveAndEnabled)
            {
                // Map the burst shaping onto the GPU request; per-droplet jitter runs in-kernel.
                float upSpeed = upwardBias * (UpwardStrengthFloor + UpwardStrengthGain * strength);
                float outSpeed = radius * outwardSpread * Mathf.Max(MinOutwardStrength, strength);
                // Droplet life/size travel WITH the request: pump/splash bursts obey THIS
                // component (or the profile's Splash section), not the ambient-mist ranges.
                gpuSpray.QueueSplashBurst(surfacePos, strength, radius, count, upSpeed, outSpeed,
                                          lifetime, dropletSize, petal, arcHalfRadians, elevationRadians,
                                          dropletOpacity);
                if (allowCrown) EmitCrown(surfacePos, strength, radius);
                return;
            }

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                // The fallback must honour the arc too, or a body without a GPU pool silently loses
                // the petals and the two paths fork. Same remap the kernel does: a full-circle angle
                // when the direction is the zero sentinel, a wedge around it otherwise.
                Vector2 r = PetalUnitCircle(petal, arcHalfRadians);
                Vector3 outward = new Vector3(r.x, 0f, r.y)
                                  * (radius * outwardSpread * Random.Range(OutwardJitterMin, OutwardJitterMax));
                float up = Random.Range(UpwardJitterMin, UpwardJitterMax) * upwardBias
                           * (UpwardStrengthFloor + UpwardStrengthGain * strength);

                ep.position = surfacePos + new Vector3(r.x * radius * SpawnRingRadiusScale,
                                                       SpawnHeightAboveSurface,
                                                       r.y * radius * SpawnRingRadiusScale);
                ep.velocity = Elevate(outward * Mathf.Max(MinOutwardStrength, strength)
                                      + new Vector3(0f, up, 0f), elevationRadians);
                ep.startLifetime = Random.Range(lifetime.x, lifetime.y);
                ep.startSize = dropletSize * Random.Range(SizeJitterMin, SizeJitterMax);
                // Velocity-proportional opacity (DWP2): faint droplets on a soft entry,
                // near-opaque on a hard slam. colorOverLifetime multiplies on top.
                Color dropletColor = DriftStartColor;
                dropletColor.a *= (AlphaStrengthFloor + AlphaStrengthGain * strength)
                                  * EffectiveOpacity(dropletOpacity);
                ep.startColor = dropletColor;
                particles.Emit(ep, 1);
            }

            if (allowCrown) EmitCrown(surfacePos, strength, radius);
        }

        /// <summary>
        /// Whether an allowed accent emit at this strength can produce a crown cloud or entry streaks.
        /// The pump asks after <see cref="EmitSplash"/> has applied any linked profile, so its continuous
        /// run only consumes the one-shot accent once something visible was actually eligible to emit.
        /// </summary>
        internal bool HasImpactAccentAt(float strength)
        {
            float clampedStrength = Mathf.Clamp01(strength);
            bool crownEligible = IsCrownEligibleAt(clampedStrength);
            bool jetsEligible = jetParticles != null && entryStreaksEnabled
                             && entryStreakAmount > 0f && clampedStrength >= entryStreakMinStrength;
            return crownEligible || jetsEligible;
        }

        internal bool HasCrownParticles => crownParticles != null;

        internal bool IsCrownEligibleAt(float strength)
        {
            return crownParticles != null && Mathf.Clamp01(strength) >= crownMinStrength;
        }

        // A caller's direction flattened to horizontal and normalised, or ZERO when there isn't one.
        // Zero is the sentinel the whole feature rests on: it reaches the GPU as a zero direction and
        // hits the kernel's untouched full-ring line, so pre-petal splashes are unchanged end to end.
        static Vector2 HorizontalPetal(Vector3 direction)
        {
            var flat = new Vector2(direction.x, direction.z);
            return flat.sqrMagnitude < BurstDirectionMinSquared ? Vector2.zero : flat.normalized;
        }

        // The Shuriken twin of the kernel's angle remap. The sentinel branch calls insideUnitCircle
        // itself rather than reimplementing it, so a legacy burst draws from the identical distribution
        // it always did.
        static Vector2 PetalUnitCircle(Vector2 petal, float arcHalfRadians)
        {
            if (petal.sqrMagnitude < BurstDirectionMinSquared) return Random.insideUnitCircle;

            float angle = Mathf.Atan2(petal.y, petal.x) + Random.Range(-arcHalfRadians, arcHalfRadians);
            // sqrt keeps the distribution uniform over AREA, as insideUnitCircle is, so narrowing the
            // arc changes where droplets go without changing how far out they start.
            float distance = Mathf.Sqrt(Random.value);
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
        }

        // The Shuriken twin of the kernel's Elevate: swing a droplet's velocity in its own vertical
        // plane, preserving SPEED so a tilt changes where the droplet goes and not how hard it was
        // thrown. A body without a GPU pool must tilt identically, or the look forks on whether one
        // happens to be present.
        static Vector3 Elevate(Vector3 velocity, float elevationRadians)
        {
            if (Mathf.Abs(elevationRadians) < BurstElevationMinRadians) return velocity;

            var horizontal = new Vector2(velocity.x, velocity.z);
            // Already straight up: there is no heading left to swing it around.
            if (horizontal.sqrMagnitude < BurstDirectionMinSquared) return velocity;

            float speed = velocity.magnitude;
            float elevation = ElevationOf(velocity.y, horizontal.magnitude, elevationRadians);
            Vector2 heading = horizontal.normalized * (Mathf.Cos(elevation) * speed);
            return new Vector3(heading.x, Mathf.Sin(elevation) * speed, heading.y);
        }

        // The launch angle above horizontal, tilt included and capped at straight up. One definition,
        // shared by the Shuriken path above and the editor gizmo below, so a drawn wedge cannot claim
        // an angle the emitter does not throw.
        static float ElevationOf(float upSpeed, float outSpeed, float elevationRadians)
            => Mathf.Clamp(Mathf.Atan2(upSpeed, outSpeed) + elevationRadians, 0f, MaxBurstElevationRadians);

        /// <summary>
        /// The angle above horizontal a burst's droplets leave at, before their per-droplet jitter.
        /// Editor previews call this rather than re-deriving it, since the angle comes from THIS
        /// component's Upward Bias and Outward Spread and only then from the caller's tilt.
        /// </summary>
        /// <remarks>A linked Foam Profile overrides those fields at emit time, so a preview taken here
        /// reflects the profile only once it has been applied.</remarks>
        internal float PreviewLaunchElevationRadians(float strength, float radius, float elevationDegrees)
        {
            float upSpeed = upwardBias * (UpwardStrengthFloor + UpwardStrengthGain * strength);
            float outSpeed = radius * outwardSpread * Mathf.Max(MinOutwardStrength, strength);
            return ElevationOf(upSpeed, outSpeed, elevationDegrees * Mathf.Deg2Rad);
        }

        // One chunk-cloud burst at the impact, for strong-enough hits. Overlapping
        // photographic chunk sprites, each eroding on its own clock, are what reads as
        // a defined splash (the KWS WaterSplashes.prefab construction). The crown is a
        // separate particle system (ConfigureCrown), so the drifting droplets above are
        // unaffected.
        void EmitCrown(Vector3 surfacePos, float strength, float radius)
        {
            EmitJets(surfacePos, strength);
            if (crownParticles == null || strength < crownMinStrength) return;

            int count = Mathf.RoundToInt(
                Mathf.Lerp(CrownBurstMinCount, CrownBurstMaxCount, strength));
            float baseSize = crownBaseSize * Mathf.Lerp(CrownMinSizeFactor, CrownMaxSizeFactor, strength)
                           + radius * CrownRadiusContribution;
            // Per-particle start color (same channel the droplets already use for their
            // velocity-proportional alpha) - the profile can retint the crown without
            // touching the shared material asset.
            Color crownColor = crownTint;
            crownColor.a *= EffectiveOpacity(crownOpacity);

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * SpawnRingRadiusScale);
                float up = Mathf.Lerp(CrownUpSpeedMin, CrownUpSpeedMax, strength)
                           * Random.Range(UpwardJitterMin, UpwardJitterMax)
                           * crownLaunchHeight;
                float outwardSpeed = CrownOutSpeedMax * strength * crownLaunchSpread;
                ep.velocity = new Vector3(ring.x * outwardSpeed, up, ring.y * outwardSpeed);
                // pow-shaped size distribution: most sprites modest, a rare one near hero size
                float hero = Mathf.Pow(Random.value, CrownHeroSizePower) * CrownHeroSizeBonus;
                ep.startSize = baseSize * (Random.Range(CrownSizeJitterMin, CrownSizeJitterMax) + hero);
                ep.rotation = Random.Range(0f, 360f);
                ep.startLifetime = crownLifetime
                                   * Random.Range(CrownLifetimeJitterMin, CrownLifetimeJitterMax);
                ep.startColor = crownColor;
                crownParticles.Emit(ep, 1);
            }
        }

        // Narrow, velocity-stretched chunks give a breach an initial upward impulse before
        // the broader cloud opens. This runs regardless of the GPU droplet route.
        void EmitJets(Vector3 surfacePos, float strength)
        {
            if (jetParticles == null || !entryStreaksEnabled || entryStreakAmount <= 0f ||
                strength < entryStreakMinStrength) return;

            int count = Mathf.RoundToInt(
                Mathf.Lerp(JetBurstMinCount, JetBurstMaxCount, strength) * entryStreakAmount);
            if (count <= 0) return;
            Color jetColor = entryStreakTint;
            jetColor.a *= EffectiveOpacity(entryStreakOpacity) * Mathf.Min(1f, entryStreakAmount);
            var emission = new ParticleSystem.EmitParams();
            for (int index = 0; index < count; index++)
            {
                Vector2 lateral = Random.insideUnitCircle * JetOutSpeedMax * strength * entryStreakWidth;
                emission.position = surfacePos + Vector3.up * SpawnHeightAboveSurface;
                emission.velocity = new Vector3(lateral.x,
                    Mathf.Lerp(JetUpSpeedMin, JetUpSpeedMax, strength) * entryStreakHeight, lateral.y);
                emission.startSize = Random.Range(entryStreakSizeRange.x,
                                                   Mathf.Max(entryStreakSizeRange.x, entryStreakSizeRange.y))
                                     * entryStreakWidth;
                emission.startLifetime = Random.Range(entryStreakLifetimeRange.x,
                                                       Mathf.Max(entryStreakLifetimeRange.x,
                                                                 entryStreakLifetimeRange.y));
                emission.rotation = Random.Range(0f, FullRingDegrees);
                emission.startColor = jetColor;
                jetParticles.Emit(emission, 1);
            }
        }

        float EffectiveOpacity(float layerOpacity)
        {
            float globalOpacity = profile != null && profile.look.drive ? profile.look.opacity : 1f;
            return Mathf.Clamp01(layerOpacity) * Mathf.Clamp01(globalOpacity);
        }

        void ApplyImpactLayerGravity(WaterFoamParticles bodyParticles)
        {
            float globalGravity = bodyParticles != null
                ? bodyParticles.gravity
                : DefaultWaterParticleGravity;
            ApplyParticleGravity(particles, globalGravity, DriftGravityModifier);
            ApplyParticleGravity(crownParticles, globalGravity, CrownGravityModifier);
            ApplyParticleGravity(jetParticles, globalGravity, entryStreakGravity);
        }

        static void ApplyParticleGravity(ParticleSystem system, float globalGravity, float localMultiplier)
        {
            if (system == null) return;
            var main = system.main;
            main.gravityModifier = Mathf.Max(0f, globalGravity) / UnityGravityMetersPerSecondSquared
                                   * Mathf.Max(0f, localMultiplier);
        }

        /// <summary>Configure a particle system for drifting droplets (used by the
        /// scene builder and the auto-created fallback).</summary>
        public static void ConfigureForDrift(ParticleSystem ps)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // droplets live in world space
            main.gravityModifier = DefaultWaterParticleGravity / UnityGravityMetersPerSecondSquared
                                   * DriftGravityModifier;
            main.startSpeed = 0f;          // velocity is set per-emit
            main.startLifetime = DriftStartLifetime;
            main.startSize = DriftStartSize;
            main.startColor = DriftStartColor;
            main.maxParticles = DriftMaxParticles;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // damping so droplets slow and settle onto the surface instead of plunging
            var velocityLimit = ps.limitVelocityOverLifetime;
            velocityLimit.enabled = true;
            velocityLimit.dampen = DriftVelocityDampen;
            velocityLimit.drag = DriftVelocityDrag;
            velocityLimit.multiplyDragByParticleSize = false;

            // fade out over the last part of life so settled droplets dissolve into the
            // surface instead of popping out of existence
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(DriftFadeStartFraction);

            // Stretched billboards: fast droplets read as streaks along their motion (KWS
            // splash look); settled drifters are slow, so they stay effectively round.
            // The crown system stays on plain billboards - its chunk sprites tumble instead.
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = DropletStretchVelocityScale;
                renderer.lengthScale = DropletStretchLengthScale;
                renderer.cameraVelocityScale = 0f;
            }

            ps.Play();
        }

        // Opaque white until startFraction of the particle's life, then a linear fade to zero.
        static Gradient FadeTailGradient(float startFraction)
        {
            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, startFraction),
                    new GradientAlphaKey(0f, 1f)
                });
            return fade;
        }

        /// <summary>Configure a particle system as the splash chunk cloud: gravity, drag,
        /// tumble, pop-then-grow size, and the chunk atlas stepped once over each
        /// particle's lifetime (used by the scene builder for the crown splash). The
        /// caller assigns the sprite-sheet material and matching tile counts.</summary>
        public static void ConfigureCrown(ParticleSystem ps, int tilesX, int tilesY)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = DefaultWaterParticleGravity / UnityGravityMetersPerSecondSquared
                                   * CrownGravityModifier;
            main.startSpeed = 0f;
            main.startLifetime = CrownStartLifetime;
            main.startSize = CrownStartSize;
            main.startColor = CrownStartColor;
            main.maxParticles = CrownMaxParticles;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // air drag so thrown chunks decelerate and arc instead of flying ballistic
            var velocityLimit = ps.limitVelocityOverLifetime;
            velocityLimit.enabled = true;
            velocityLimit.dampen = CrownVelocityDampen;
            velocityLimit.multiplyDragByParticleSize = false;

            // slow random tumble, half the sprites spinning each way
            var rotationOverLifetime = ps.rotationOverLifetime;
            rotationOverLifetime.enabled = true;
            rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(
                -CrownTumbleMaxDegrees * Mathf.Deg2Rad, CrownTumbleMaxDegrees * Mathf.Deg2Rad);

            // pop to CrownPopFraction almost immediately, then grow to full size
            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, PopCurve());

            // step through the chunk atlas exactly once across each particle's life
            var sheetAnimation = ps.textureSheetAnimation;
            sheetAnimation.enabled = true;
            sheetAnimation.mode = ParticleSystemAnimationMode.Grid;
            sheetAnimation.numTilesX = tilesX;
            sheetAnimation.numTilesY = tilesY;
            sheetAnimation.animation = ParticleSystemAnimationType.WholeSheet;
            sheetAnimation.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            sheetAnimation.cycleCount = 1;
            sheetAnimation.startFrame = 0f;
            sheetAnimation.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));

            // Linear alpha 1 -> 0 across the WHOLE life: this is the erosion clock.
            // SplashParticles.shader burns the sprite through its noise channel as this
            // alpha falls, so the chunk disintegrates instead of ghost-fading (KWS).
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(0f);

            ps.Play();
        }

        /// <summary>Configures the narrow, stretched entry columns that precede the crown.
        /// They use the same packed four-chunk atlas but never depend on CPU fallback spray.</summary>
        public static void ConfigureJets(ParticleSystem ps, int tilesX, int tilesY)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = DefaultWaterParticleGravity / UnityGravityMetersPerSecondSquared
                                   * JetDefaultGravityModifier;
            main.startSpeed = 0f;
            main.startLifetime = JetLifetimeMin;
            main.startSize = JetSizeMin;
            main.maxParticles = JetMaxParticles;
            main.playOnAwake = true;

            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var velocityLimit = ps.limitVelocityOverLifetime;
            velocityLimit.enabled = true;
            velocityLimit.dampen = JetVelocityDampen;
            velocityLimit.drag = JetVelocityDrag;
            var sheetAnimation = ps.textureSheetAnimation;
            sheetAnimation.enabled = true;
            sheetAnimation.numTilesX = tilesX;
            sheetAnimation.numTilesY = tilesY;
            sheetAnimation.animation = ParticleSystemAnimationType.WholeSheet;
            sheetAnimation.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            sheetAnimation.cycleCount = 1;
            sheetAnimation.startFrame = 0f;
            sheetAnimation.frameOverTime = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(0f);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = JetStretchVelocityScale;
            renderer.lengthScale = JetStretchLengthScale;
            ps.Play();
        }

        // The chunk pop-then-grow size curve: (0,0) -> (PopTime, PopFraction) -> (1,1).
        static AnimationCurve PopCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(CrownPopTime, CrownPopFraction),
                new Keyframe(1f, 1f));
            return curve;
        }
    }
}
