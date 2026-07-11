// WebGL Water - shared splash particle emitter (Unity 6 / URP port)
// Owns (or references) a real Particle System so the splash is fully editable in
// the Inspector: select the "Splash Particles" object to tweak modules, and swap
// the droplet texture on its ParticleSystemRenderer material. Both object impacts
// (WaterSplash) and the mouse interaction (WaterVolume) emit through this.
//
// Droplets pop, then stick to the water surface and DRIFT with the waves: they
// launch ballistically (low gravity), and once they reach the live waterline they
// snap to it and are carried along the local surface flow, reacting as ripples
// pass under them. The drift is driven on the CPU from WaterVolume's height
// readback, so it tracks the same surface the shader renders.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    public class WaterSplashEmitter : MonoBehaviour
    {
        // Below this depth past the surface a droplet is considered "landed".
        const float SurfaceContactBand = 0.01f;

        // Crown size mapping: base size scales between these factors with impact
        // strength, plus a contribution from the impact radius.
        const float CrownMinSizeFactor = 0.6f;
        const float CrownMaxSizeFactor = 1.4f;
        const float CrownRadiusContribution = 0.5f;

        // ---- burst shaping (EmitSplash) ----
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
        const float CrownStartLifetime = 0.5f;
        const float CrownStartSize = 0.4f;
        static readonly Color CrownStartColor = new Color(0.95f, 0.98f, 1.0f, 1.0f);
        const int CrownMaxParticles = 64;
        const float CrownFadeStartFraction = 0.7f;    // flipbook tail softening

        [Tooltip("The particle system to emit from. Auto-created if left empty.")]
        [SerializeField] internal ParticleSystem particles;

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

        [Header("Crown splash (flipbook)")]
        [Tooltip("Optional flipbook splash emitted at the impact point. Leave empty to disable.")]
        [SerializeField] internal ParticleSystem crownParticles;
        [Tooltip("Minimum impact strength (0..1) that spawns a crown splash.")]
        [Range(0f, 1f)] [SerializeField] internal float crownMinStrength = 0.25f;
        [Tooltip("Base world size of the crown splash, scaled up by impact strength.")]
        [SerializeField] internal float crownBaseSize = 0.4f;
        [Tooltip("Crown lifetime; the flipbook plays through once over this time.")]
        [SerializeField] internal float crownLifetime = 0.5f;

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

        // Pop -> stick -> drift. Runs after the controllers have stepped their sims so the
        // surface query reflects this frame's waves.
        void LateUpdate()
        {
            if (particles == null) return;

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
            if (body == null || !body.TryGetSurface(position.x, position.z, out float surfaceY, out Vector2 flow))
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
            velocity += new Vector3(flow.x, 0f, flow.y) * (driftStrength * dt);
            velocity -= velocity * Mathf.Min(1f, driftDamping * dt);

            position += velocity * dt;
            droplet.position = position;
            droplet.velocity = velocity;
        }

        /// <summary>Emit a splash at a surface point. strength is 0..1. Droplets are thrown by
        /// the body's GPU foam-particle system when one is present (spray unification: every
        /// airborne droplet shares the KIND_SPRAY tech + look); the Shuriken system here then
        /// only plays the crown flipbook. Bodies without a GPU system keep the legacy
        /// Shuriken droplet burst.</summary>
        public void EmitSplash(Vector3 surfacePos, float strength, float radius)
        {
            if (particles == null) return;
            strength = Mathf.Clamp01(strength);
            int count = Mathf.Clamp(Mathf.RoundToInt(strength * maxParticlesPerBurst),
                                    MinBurstCount, maxParticlesPerBurst);

            WaterVolume body = WaterVolume.BodyContaining(surfacePos);
            WaterFoamParticles gpuSpray = body != null ? body.GetComponent<WaterFoamParticles>() : null;
            if (gpuSpray != null && gpuSpray.isActiveAndEnabled)
            {
                // Map the burst shaping onto the GPU request; per-droplet jitter runs in-kernel.
                float upSpeed = upwardBias * (UpwardStrengthFloor + UpwardStrengthGain * strength);
                float outSpeed = radius * outwardSpread * Mathf.Max(MinOutwardStrength, strength);
                gpuSpray.QueueSplashBurst(surfacePos, strength, radius, count, upSpeed, outSpeed);
                EmitCrown(surfacePos, strength, radius);
                return;
            }

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 r = Random.insideUnitCircle;
                Vector3 outward = new Vector3(r.x, 0f, r.y)
                                  * (radius * outwardSpread * Random.Range(OutwardJitterMin, OutwardJitterMax));
                float up = Random.Range(UpwardJitterMin, UpwardJitterMax) * upwardBias
                           * (UpwardStrengthFloor + UpwardStrengthGain * strength);

                ep.position = surfacePos + new Vector3(r.x * radius * SpawnRingRadiusScale,
                                                       SpawnHeightAboveSurface,
                                                       r.y * radius * SpawnRingRadiusScale);
                ep.velocity = outward * Mathf.Max(MinOutwardStrength, strength) + new Vector3(0f, up, 0f);
                ep.startLifetime = Random.Range(lifetime.x, lifetime.y);
                ep.startSize = dropletSize * Random.Range(SizeJitterMin, SizeJitterMax);
                // Velocity-proportional opacity (DWP2): faint droplets on a soft entry,
                // near-opaque on a hard slam. colorOverLifetime multiplies on top.
                Color dropletColor = DriftStartColor;
                dropletColor.a *= AlphaStrengthFloor + AlphaStrengthGain * strength;
                ep.startColor = dropletColor;
                particles.Emit(ep, 1);
            }

            EmitCrown(surfacePos, strength, radius);
        }

        // One flipbook crown splash at the impact, for strong-enough hits. The crown
        // is a separate particle system (set up by ConfigureCrown), so the drifting
        // droplets above are unaffected.
        void EmitCrown(Vector3 surfacePos, float strength, float radius)
        {
            if (crownParticles == null || strength < crownMinStrength) return;

            var ep = new ParticleSystem.EmitParams();
            ep.position = surfacePos;
            ep.velocity = Vector3.zero;
            ep.startLifetime = crownLifetime;
            ep.startSize = crownBaseSize * Mathf.Lerp(CrownMinSizeFactor, CrownMaxSizeFactor, strength)
                         + radius * CrownRadiusContribution;
            crownParticles.Emit(ep, 1);
        }

        /// <summary>Configure a particle system for drifting droplets (used by the
        /// scene builder and the auto-created fallback).</summary>
        public static void ConfigureForDrift(ParticleSystem ps)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // droplets live in world space
            main.gravityModifier = DriftGravityModifier;
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
            // The crown system is left as plain billboards - its flipbook is directional.
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

        /// <summary>Configure a particle system to play a splash flipbook once over each
        /// particle's lifetime (used by the scene builder for the crown splash). The
        /// caller assigns the sprite-sheet material and matching tile counts.</summary>
        public static void ConfigureCrown(ParticleSystem ps, int tilesX, int tilesY)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;     // the crown stays put on the surface
            main.startSpeed = 0f;
            main.startLifetime = CrownStartLifetime;
            main.startSize = CrownStartSize;
            main.startColor = CrownStartColor;
            main.maxParticles = CrownMaxParticles;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // play the whole sprite sheet exactly once across each particle's life
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

            // soften the tail so the splash dissolves instead of cutting off
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(CrownFadeStartFraction);

            ps.Play();
        }
    }
}
