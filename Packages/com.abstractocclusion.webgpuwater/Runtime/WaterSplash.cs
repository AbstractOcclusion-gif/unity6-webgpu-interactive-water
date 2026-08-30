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
        int _domainBodyId; // hysteresis hint for the domain resolver (0 = none)

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        void FixedUpdate()
        {
            Vector3 center = _rb.worldCenterOfMass;
            // Resolve from the object's position so a splash fires into the lake it enters.
            // Domain-resolved (2026-08-29): nearest surface in vertical reach, exclusion-aware -
            // a crate falling through a carved (dry) interior must NOT splash - and never the
            // Primary fallback, which used to splash objects that were nowhere near water.
            // River round (2026-08-29): the height now comes from the resolved DOMAIN, so a
            // splash into a sloped ribbon fires at the ribbon's local elevation instead of the
            // parent plane, and a standalone ribbon (Body null) still splashes through the
            // override emitter. ExcludeInteractiveRipples keeps the old TryGetAnalyticWaterline
            // doctrine: analytic-only, valid from frame 0, no readback demand window, and a wake
            // ripple can never re-trigger its own maker's splash.
            WaterDomainQueryOptions options =
                WaterDomainQueryOptions.ForIntent(WaterQueryIntent.NearestWithinVerticalLimits);
            options.PreviousBodyId = _domainBodyId;
            options.Fields = WaterQueryFields.Height;
            options.ExcludeInteractiveRipples = true;
            if (!WaterDomainResolver.Resolve(center, in options, out WaterDomainSample domain))
            {
                _domainBodyId = 0;
                _wasUnder = false;
                return;
            }
            _domainBodyId = domain.BodyId;
            WaterVolume body = domain.Body;
            float surfaceY = domain.SurfaceHeight;

            float halfY = _col != null ? _col.bounds.extents.y : FallbackHalfExtent;
            float halfX = _col != null ? _col.bounds.extents.x : FallbackHalfExtent;
            bool under = (center.y - halfY) <= surfaceY;

            if (under && !_wasUnder)
            {
                float speed = Mathf.Max(0f, -_rb.linearVelocity.y);
                if (speed >= minImpactSpeed)
                {
                    float strength = Mathf.Clamp01(speed / Mathf.Max(MinDivisorSpeed, maxImpactSpeed));
                    // Explicit override wins; otherwise the body the object entered supplies the
                    // emitter (a standalone ribbon has none - the override is its only source).
                    WaterSplashEmitter activeEmitter = emitter != null
                        ? emitter : body != null ? body.ResolveSplashEmitter() : null;
                    if (activeEmitter != null)
                        activeEmitter.EmitSplash(new Vector3(center.x, surfaceY, center.z), strength, halfX * 2f);
                    float impactRippleStrength = Mathf.Min(rippleStrength, speed * SpeedToRippleStrength);
                    // Ripples stamp into the BODY's sim: over a parented ribbon that is the
                    // parent's water around the banks (the ribbon itself renders no interactive
                    // ripples - the river gate in WaterSurfaceFragStages); a standalone ribbon
                    // has no sim at all.
                    if (body != null)
                        body.AddRipple(center.x, center.z,
                                       Mathf.Clamp(halfX, MinRippleRadius, MaxRippleRadius),
                                       ApplyImpactRippleCap(impactRippleStrength,
                                                           body.splashImpactRippleCap));
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
