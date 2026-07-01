// WebGL Water - object splash trigger (Unity 6 / URP port)
// Detects when this object punches through the water surface and fires a droplet
// burst (via the shared WaterSplashEmitter) plus a sharp ripple into the sim, which
// also feeds the turbulence-driven foam. Particle look/motion lives on the emitter
// so it's editable in the Inspector.
using UnityEngine;

namespace WebGLWater
{
    [RequireComponent(typeof(Rigidbody))]
    public class WaterSplash : MonoBehaviour
    {
        [Tooltip("Shared splash emitter. Auto-found in the scene if left empty.")]
        public WaterSplashEmitter emitter;

        [Tooltip("Minimum downward speed at the surface to trigger a splash.")]
        public float minImpactSpeed = 0.4f;
        [Tooltip("Speed that produces the biggest splash.")]
        public float maxImpactSpeed = 3f;
        [Tooltip("Strength of the ripple injected into the sim on impact.")]
        public float rippleStrength = 0.04f;

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

        void Start()
        {
            if (emitter == null) emitter = FindFirstObjectByType<WaterSplashEmitter>();
        }

        void FixedUpdate()
        {
            Vector3 center = _rb.worldCenterOfMass;
            // Resolve from the object's position so a splash fires into the lake it enters.
            WaterController body = WaterController.BodyContaining(center);
            float surfaceY = 0f;
            if (body != null) body.TryGetWaterHeight(center.x, center.z, out surfaceY);

            float halfY = _col != null ? _col.bounds.extents.y : FallbackHalfExtent;
            float halfX = _col != null ? _col.bounds.extents.x : FallbackHalfExtent;
            bool under = (center.y - halfY) <= surfaceY;

            if (under && !_wasUnder)
            {
                float speed = Mathf.Max(0f, -_rb.linearVelocity.y);
                if (speed >= minImpactSpeed)
                {
                    float strength = Mathf.Clamp01(speed / Mathf.Max(MinDivisorSpeed, maxImpactSpeed));
                    if (emitter != null)
                        emitter.EmitSplash(new Vector3(center.x, surfaceY, center.z), strength, halfX * 2f);
                    if (body != null)
                        body.AddRipple(center.x, center.z, Mathf.Clamp(halfX, MinRippleRadius, MaxRippleRadius),
                                       Mathf.Min(rippleStrength, speed * SpeedToRippleStrength));
                }
            }
            _wasUnder = under;
        }
    }
}
