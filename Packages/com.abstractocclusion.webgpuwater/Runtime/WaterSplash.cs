// WebGpuWater - object splash trigger (Unity 6 / URP port)
// Detects when this object punches through the water surface and fires a droplet
// burst (via the shared WaterSplashEmitter) plus a sharp ripple into the sim, which
// also feeds the turbulence-driven foam. Particle look/motion lives on the emitter
// so it's editable in the Inspector.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Rigidbody))]
    public class WaterSplash : MonoBehaviour
    {
        [Tooltip("Explicit splash emitter override. Left empty, the water body under the object " +
                 "supplies one (WaterVolume.ResolveSplashEmitter).")]
        [SerializeField] internal WaterSplashEmitter emitter;

        [Tooltip("Minimum downward speed at the surface to trigger a splash.")]
        [SerializeField] internal float minImpactSpeed = 0.4f;
        [Tooltip("Speed that produces the biggest splash.")]
        [SerializeField] internal float maxImpactSpeed = 3f;
        [Tooltip("Strength of the ripple injected into the sim on impact.")]
        [SerializeField] internal float rippleStrength = 0.04f;

        const float FallbackHalfExtent = 0.15f;    // used when there is no collider to size from
        const float MinRippleRadius = 0.02f;
        const float MaxRippleRadius = 0.2f;
        const float SpeedToRippleStrength = 0.02f; // downward impact speed -> injected ripple height
        const float MinDivisorSpeed = 0.01f;       // guard against maxImpactSpeed = 0

        Rigidbody _rb;
        Collider _col;
        bool _wasUnder;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        void FixedUpdate()
        {
            Vector3 center = _rb.worldCenterOfMass;
            // Resolve from the object's position so a splash fires into the lake it enters.
            WaterVolume body = WaterVolume.BodyContaining(center);
            if (body == null) { _wasUnder = false; return; }

            // ANALYTIC waterline only (rest plane + wind waves + ocean swell), valid from
            // frame 0. Deliberately NOT TryGetWaterHeight: that is a rippled READBACK query,
            // so a splash component re-stamped the surface sampler's demand window every
            // physics tick and held the full sim-field GPU->CPU transfer open for its whole
            // lifetime (sim RT as RGBAFloat - 4 MiB per frame at the High tier) to answer a
            // binary under/over test the analytic surface answers as well: interactive
            // ripples are centimetre-scale against a collider-sized entry band, and a wake
            // ripple re-triggering its own maker's splash is exactly the feedback loop
            // TryGetAnalyticWaterline exists to break. Out of footprint: keep last frame's
            // state rather than assume a surface at world y = 0, which would swallow the
            // first entry splash of any body placed off the origin.
            if (!body.TryGetAnalyticWaterline(center.x, center.z, out float surfaceY))
                return;

            float halfY = _col != null ? _col.bounds.extents.y : FallbackHalfExtent;
            float halfX = _col != null ? _col.bounds.extents.x : FallbackHalfExtent;
            bool under = (center.y - halfY) <= surfaceY;

            if (under && !_wasUnder)
            {
                float speed = Mathf.Max(0f, -_rb.linearVelocity.y);
                if (speed >= minImpactSpeed)
                {
                    float strength = Mathf.Clamp01(speed / Mathf.Max(MinDivisorSpeed, maxImpactSpeed));
                    // Explicit override wins; otherwise the body the object entered supplies the emitter.
                    WaterSplashEmitter activeEmitter = emitter != null ? emitter : body.ResolveSplashEmitter();
                    if (activeEmitter != null)
                        activeEmitter.EmitSplash(new Vector3(center.x, surfaceY, center.z), strength, halfX * 2f);
                    float impactRippleStrength = Mathf.Min(rippleStrength, speed * SpeedToRippleStrength);
                    body.AddRipple(center.x, center.z, Mathf.Clamp(halfX, MinRippleRadius, MaxRippleRadius),
                                   ApplyImpactRippleCap(impactRippleStrength, body.splashImpactRippleCap));
                }
            }
            _wasUnder = under;
        }

        static float ApplyImpactRippleCap(float strength, float cap)
        {
            return cap > 0f ? Mathf.Min(strength, cap) : strength;
        }
    }
}
