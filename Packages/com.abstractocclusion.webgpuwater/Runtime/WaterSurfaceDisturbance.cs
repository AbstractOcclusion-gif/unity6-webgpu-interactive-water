using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>The quiet presence layer for a creature near the surface: a directional wake
    /// behind it while it moves, a barely-there idle shimmer while it hovers, and occasional
    /// tail-flick bursts - the visual language of a fish PRESENT in shallow water, as opposed
    /// to WaterBreachSplash's dramatic surface crossings. Attach alongside or instead of it.
    ///
    /// Ported from the KWS-side FishSurfaceDisturbance driver (FlockReflections). The KWS
    /// version dials persistent force effectors up and down each frame; this package's
    /// injections are impulses, so the mapping is: wake effector -> per-frame sphere-dipole
    /// injection from the tail's actual displacement (the same mechanism WaterSphereInteractor
    /// uses for hulls), idle pulse -> jittered ripple stamps, tail flick -> a short train of
    /// lateral dipole impulses under a sine envelope. Nothing persists, so there is nothing
    /// to silence on disable.</summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Surface Disturbance")]
    public sealed class WaterSurfaceDisturbance : MonoBehaviour
    {
        // Guard against division by a paused frame.
        const float MinDeltaTime = 0.001f;
        // The tail-flick dipole needs a step length to carry its energy; this is the lateral
        // distance (world units/sec equivalent) a full-force flick sweeps.
        const float TailFlickSweepSpeed = 1.2f;
        // Idle pulses use a small stamp so the shimmer stays sub-wavelength.
        const float IdleRippleRadius = 0.25f;

        [Header("Surface Detection")]
        [Tooltip("Maximum distance from the surface (above or below) where disturbance is generated.")]
        [Min(0.05f)] [SerializeField] float depthThreshold = 0.6f;

        [Tooltip("Fade disturbance smoothly toward the threshold edge instead of cutting hard.")]
        [SerializeField] bool fadeWithDepth = true;

        [Header("Wake (movement)")]
        [Tooltip("Directional wake ripples behind the object as it moves through the surface zone.")]
        [SerializeField] bool enableWake = true;

        [Tooltip("Wake dipole gain (same currency as Water Sphere Interactor's Strength).")]
        [Range(0f, 4f)] [SerializeField] float wakeStrength = 1f;

        [Tooltip("Speed (units/sec) at which the wake reaches full strength.")]
        [Min(0.01f)] [SerializeField] float wakeFullSpeedReference = 2f;

        [Tooltip("World radius of the wake injection sphere.")]
        [Range(0.1f, 3f)] [SerializeField] float wakeRadius = 0.4f;

        [Tooltip("Offset behind the object (local -forward) where the wake injects - the tail.")]
        [Min(0f)] [SerializeField] float wakeTailOffset = 0.3f;

        [Header("Idle Ripple (presence)")]
        [Tooltip("Gentle ambient pulse while nearly still - breathing, fin micro-movements.")]
        [SerializeField] bool enableIdleRipple = true;

        [Tooltip("Idle stamp strength in pool-height units (keep tiny: a shimmer, not a ring).")]
        [Range(0f, 0.02f)] [SerializeField] float idleStrength = 0.006f;

        [Tooltip("Seconds between idle pulses.")]
        [Min(0.1f)] [SerializeField] float idlePulseInterval = 0.8f;

        [Tooltip("Random variance on the pulse interval, so it never reads mechanical.")]
        [Min(0f)] [SerializeField] float idlePulseJitter = 0.25f;

        [Header("Tail Flick (periodic burst)")]
        [Tooltip("Occasional stronger lateral burst - the fish adjusting or darting.")]
        [SerializeField] bool enableTailFlick = true;

        [Tooltip("Flick dipole gain at the burst peak.")]
        [Range(0f, 4f)] [SerializeField] float tailFlickStrength = 1.5f;

        [Tooltip("Average seconds between flicks.")]
        [Min(0.2f)] [SerializeField] float tailFlickInterval = 3f;

        [Tooltip("Random variance on the flick interval.")]
        [Min(0f)] [SerializeField] float tailFlickJitter = 1.5f;

        [Tooltip("Seconds one flick burst lasts.")]
        [Min(0.05f)] [SerializeField] float tailFlickDuration = 0.2f;

        Vector3 _prevPosition;
        Vector3 _prevTailPosition;
        bool _primed;
        float _idleTimer;
        float _nextIdlePulse;
        float _tailFlickTimer;
        float _nextTailFlick;
        float _tailFlickElapsed;
        bool _tailFlicking;
        int _tailFlickSide;
        bool _isNearSurface;

        void OnEnable()
        {
            _primed = false;
            _nextIdlePulse = NextInterval(idlePulseInterval, idlePulseJitter);
            _nextTailFlick = NextInterval(tailFlickInterval, tailFlickJitter);
        }

        // LateUpdate, matching WaterSphereInteractor: motion has settled, so the wake dipole
        // measures the frame's real displacement.
        void LateUpdate()
        {
            Vector3 position = transform.position;
            Vector3 tailPosition = position - transform.forward * wakeTailOffset;
            if (!_primed)
            {
                _prevPosition = position;
                _prevTailPosition = tailPosition;
                _primed = true;
                return;
            }

            if (!WaterVolume.TrySampleHeightAt(position, out float surfaceY))
            {
                _isNearSurface = false;
                _prevPosition = position;
                _prevTailPosition = tailPosition;
                return;
            }

            float distanceToSurface = Mathf.Abs(surfaceY - position.y);
            _isNearSurface = distanceToSurface <= depthThreshold;
            if (!_isNearSurface)
            {
                _tailFlicking = false;
                _prevPosition = position;
                _prevTailPosition = tailPosition;
                return;
            }

            float depthFactor = fadeWithDepth
                ? 1f - Mathf.Clamp01(distanceToSurface / depthThreshold)
                : 1f;

            float deltaTime = Mathf.Max(Time.deltaTime, MinDeltaTime);
            Vector3 velocity = (position - _prevPosition) / deltaTime;
            float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;

            if (enableWake)
                UpdateWake(tailPosition, surfaceY, horizontalSpeed, depthFactor);
            if (enableIdleRipple)
                UpdateIdleRipple(position, surfaceY, horizontalSpeed, depthFactor, deltaTime);
            if (enableTailFlick)
                UpdateTailFlick(tailPosition, surfaceY, depthFactor, deltaTime);

            _prevPosition = position;
            _prevTailPosition = tailPosition;
        }

        void UpdateWake(Vector3 tailPosition, float surfaceY, float speed, float depthFactor)
        {
            Vector3 tailStep = tailPosition - _prevTailPosition;
            if (tailStep.sqrMagnitude <= 0f) return;

            float speedFactor = Mathf.Clamp01(speed / wakeFullSpeedReference);
            float strength = wakeStrength * speedFactor * depthFactor;
            if (strength <= 0f) return;

            Vector3 injectAt = new Vector3(tailPosition.x, surfaceY, tailPosition.z);
            WaterVolume.TrySphereInteractionAt(injectAt, tailStep, wakeRadius, strength);
        }

        void UpdateIdleRipple(Vector3 position, float surfaceY, float speed, float depthFactor,
                              float deltaTime)
        {
            // The wake owns the look while moving; idle shimmer belongs to a hovering fish.
            float idleBlend = 1f - Mathf.Clamp01(speed / (wakeFullSpeedReference * 0.5f));
            if (idleBlend <= 0f) return;

            _idleTimer += deltaTime;
            if (_idleTimer < _nextIdlePulse) return;

            _idleTimer = 0f;
            _nextIdlePulse = NextInterval(idlePulseInterval, idlePulseJitter);
            Vector3 injectAt = new Vector3(position.x, surfaceY, position.z);
            WaterVolume.TrySpawnRippleAt(injectAt, IdleRippleRadius,
                                         idleStrength * depthFactor * idleBlend);
        }

        void UpdateTailFlick(Vector3 tailPosition, float surfaceY, float depthFactor,
                             float deltaTime)
        {
            if (_tailFlicking)
            {
                _tailFlickElapsed += deltaTime;
                float t = _tailFlickElapsed / tailFlickDuration;
                if (t >= 1f)
                {
                    _tailFlicking = false;
                    return;
                }

                // Sharp bell: quick burst, quick fade - the KWS driver's flick envelope.
                float envelope = Mathf.Sin(t * Mathf.PI);
                Vector3 lateral = Vector3.Cross(transform.forward, Vector3.up).normalized
                                  * _tailFlickSide;
                Vector3 flickStep = lateral * (TailFlickSweepSpeed * envelope * deltaTime);
                Vector3 injectAt = new Vector3(tailPosition.x, surfaceY, tailPosition.z);
                WaterVolume.TrySphereInteractionAt(injectAt, flickStep, wakeRadius,
                                                   tailFlickStrength * envelope * depthFactor);
                return;
            }

            _tailFlickTimer += deltaTime;
            if (_tailFlickTimer < _nextTailFlick) return;
            BeginFlick();
        }

        /// <summary>Trigger an immediate tail flick (e.g. when the fish darts or is startled).</summary>
        public void TriggerFlick()
        {
            if (!_isNearSurface) return;
            BeginFlick();
        }

        void BeginFlick()
        {
            _tailFlicking = true;
            _tailFlickElapsed = 0f;
            _tailFlickTimer = 0f;
            _nextTailFlick = NextInterval(tailFlickInterval, tailFlickJitter);
            _tailFlickSide = Random.value > 0.5f ? 1 : -1; // one side per flick, not per frame
        }

        static float NextInterval(float interval, float jitter)
            => Mathf.Max(0.05f, interval + Random.Range(-jitter, jitter));

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Vector3 center = transform.position;
            if (WaterVolume.TrySampleHeightAt(center, out float surfaceY)) center.y = surfaceY;

            Gizmos.color = new Color(0.3f, 0.8f, 0.9f, 0.2f);
            Gizmos.DrawWireSphere(center, wakeRadius);
            Gizmos.DrawLine(center + Vector3.up * depthThreshold + Vector3.left * 0.5f,
                            center + Vector3.up * depthThreshold + Vector3.right * 0.5f);
            Gizmos.DrawLine(center + Vector3.down * depthThreshold + Vector3.left * 0.5f,
                            center + Vector3.down * depthThreshold + Vector3.right * 0.5f);

            if (enableWake)
            {
                Gizmos.color = new Color(0.2f, 0.5f, 1f, 0.5f);
                Vector3 tail = center - transform.forward * wakeTailOffset;
                Gizmos.DrawWireSphere(tail, wakeRadius * 0.5f);
                Gizmos.DrawLine(center, tail);
            }
        }
#endif
    }
}
