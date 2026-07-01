// WebGL Water - shared splash particle emitter (Unity 6 / URP port)
// Owns (or references) a real Particle System so the splash is fully editable in
// the Inspector: select the "Splash Particles" object to tweak modules, and swap
// the droplet texture on its ParticleSystemRenderer material. Both object impacts
// (WaterSplash) and the mouse interaction (WaterController) emit through this.
//
// Droplets pop, then stick to the water surface and DRIFT with the waves: they
// launch ballistically (low gravity), and once they reach the live waterline they
// snap to it and are carried along the local surface flow, reacting as ripples
// pass under them. The drift is driven on the CPU from WaterController's height
// readback, so it tracks the same surface the shader renders.
using UnityEngine;

namespace WebGLWater
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

        [Tooltip("The particle system to emit from. Auto-created if left empty.")]
        public ParticleSystem particles;

        [Header("Burst shaping")]
        [Range(1, 128)] public int maxParticlesPerBurst = 48;
        [Tooltip("Upward launch bias. Higher = droplets jump more before settling.")]
        [Range(0f, 3f)] public float upwardBias = 1.0f;
        [Tooltip("Outward (horizontal) spread, so droplets drift across the surface.")]
        [Range(0f, 3f)] public float outwardSpread = 1.3f;
        public float dropletSize = 0.02f;
        public Vector2 lifetime = new Vector2(0.6f, 1.3f);

        [Header("Surface drift")]
        [Tooltip("Seconds a droplet stays ballistic (the 'pop') before it can stick.")]
        [Range(0f, 0.5f)] public float popDuration = 0.12f;
        [Tooltip("How strongly settled droplets are carried by the local wave flow.")]
        [Range(0f, 6f)] public float driftStrength = 2.0f;
        [Tooltip("Horizontal damping on drifting droplets (higher = settles sooner).")]
        [Range(0f, 8f)] public float driftDamping = 2.5f;
        [Tooltip("How high above the surface a settled droplet rides (world units).")]
        public float surfaceRideHeight = 0.004f;

        [Header("Crown splash (flipbook)")]
        [Tooltip("Optional flipbook splash emitted at the impact point. Leave empty to disable.")]
        public ParticleSystem crownParticles;
        [Tooltip("Minimum impact strength (0..1) that spawns a crown splash.")]
        [Range(0f, 1f)] public float crownMinStrength = 0.25f;
        [Tooltip("Base world size of the crown splash, scaled up by impact strength.")]
        public float crownBaseSize = 0.4f;
        [Tooltip("Crown lifetime; the flipbook plays through once over this time.")]
        public float crownLifetime = 0.5f;

        WaterController _controller;
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

        void Start()
        {
            _controller = WaterController.Resolve(); // TODO(Phase 2): the body containing this emitter
        }

        // Pop -> stick -> drift. Runs after the controller has stepped the sim so the
        // surface query reflects this frame's waves.
        void LateUpdate()
        {
            if (particles == null || _controller == null) return;

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
            if (!_controller.TryGetSurface(position.x, position.z, out float surfaceY, out Vector2 flow))
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

        /// <summary>Emit a splash at a surface point. strength is 0..1.</summary>
        public void EmitSplash(Vector3 surfacePos, float strength, float radius)
        {
            if (particles == null) return;
            strength = Mathf.Clamp01(strength);
            int count = Mathf.Clamp(Mathf.RoundToInt(strength * maxParticlesPerBurst), 3, maxParticlesPerBurst);

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 r = Random.insideUnitCircle;
                Vector3 outward = new Vector3(r.x, 0f, r.y) * (radius * outwardSpread * Random.Range(0.4f, 1f));
                float up = Random.Range(0.5f, 1.2f) * upwardBias * (0.4f + 0.6f * strength);

                ep.position = surfacePos + new Vector3(r.x * radius * 0.5f, 0.01f, r.y * radius * 0.5f);
                ep.velocity = outward * Mathf.Max(0.4f, strength) + new Vector3(0f, up, 0f);
                ep.startLifetime = Random.Range(lifetime.x, lifetime.y);
                ep.startSize = dropletSize * Random.Range(0.6f, 1.3f);
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
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // droplets live in world space
            main.gravityModifier = 0.4f;   // low -> they drift rather than dive
            main.startSpeed = 0f;          // velocity is set per-emit
            main.startLifetime = 0.5f;
            main.startSize = 0.02f;
            main.startColor = new Color(0.9f, 0.97f, 1.0f, 0.9f);
            main.maxParticles = 2000;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // damping so droplets slow and settle onto the surface instead of plunging
            var lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.dampen = 0.2f;
            lim.drag = 1.5f;
            lim.multiplyDragByParticleSize = false;

            // fade out over the last part of life so settled droplets dissolve into the
            // surface instead of popping out of existence
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.6f),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = fade;

            ps.Play();
        }

        /// <summary>Configure a particle system to play a splash flipbook once over each
        /// particle's lifetime (used by the scene builder for the crown splash). The
        /// caller assigns the sprite-sheet material and matching tile counts.</summary>
        public static void ConfigureCrown(ParticleSystem ps, int tilesX, int tilesY)
        {
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;     // the crown stays put on the surface
            main.startSpeed = 0f;
            main.startLifetime = 0.5f;
            main.startSize = 0.4f;
            main.startColor = new Color(0.95f, 0.98f, 1.0f, 1.0f);
            main.maxParticles = 64;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // play the whole sprite sheet exactly once across each particle's life
            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            tsa.mode = ParticleSystemAnimationMode.Grid;
            tsa.numTilesX = tilesX;
            tsa.numTilesY = tilesY;
            tsa.animation = ParticleSystemAnimationType.WholeSheet;
            tsa.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            tsa.cycleCount = 1;
            tsa.startFrame = 0f;
            tsa.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));

            // soften the tail so the splash dissolves instead of cutting off
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.7f),
                    new GradientAlphaKey(0f, 1f)
                });
            col.color = fade;

            ps.Play();
        }
    }
}
