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

        Rigidbody _rb;
        Collider _col;
        WaterController _ctrl;
        bool _wasUnder;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        void Start()
        {
            _ctrl = WaterController.Resolve(); // TODO(Phase 2): the body containing this object
            if (emitter == null) emitter = FindFirstObjectByType<WaterSplashEmitter>();
        }

        void FixedUpdate()
        {
            Vector3 c = _rb.worldCenterOfMass;
            float surfaceY = 0f;
            if (_ctrl != null) _ctrl.TryGetWaterHeight(c.x, c.z, out surfaceY);

            float halfY = _col != null ? _col.bounds.extents.y : 0.15f;
            float halfX = _col != null ? _col.bounds.extents.x : 0.15f;
            bool under = (c.y - halfY) <= surfaceY;

            if (under && !_wasUnder)
            {
                float speed = Mathf.Max(0f, -_rb.linearVelocity.y);
                if (speed >= minImpactSpeed)
                {
                    float strength = Mathf.Clamp01(speed / Mathf.Max(0.01f, maxImpactSpeed));
                    if (emitter != null)
                        emitter.EmitSplash(new Vector3(c.x, surfaceY, c.z), strength, halfX * 2f);
                    if (_ctrl != null)
                        _ctrl.AddRipple(c.x, c.z, Mathf.Clamp(halfX, 0.02f, 0.2f),
                                        Mathf.Min(rippleStrength, speed * 0.02f));
                }
            }
            _wasUnder = under;
        }
    }
}
