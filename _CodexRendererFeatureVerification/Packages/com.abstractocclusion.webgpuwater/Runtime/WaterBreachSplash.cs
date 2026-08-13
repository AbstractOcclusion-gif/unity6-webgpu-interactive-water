using System.Collections;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Breach splash driver: attach to any moving object (a fish, a projectile, a
    /// diving bird) and it fires a full splash - GPU droplets, crown, bubbles - through the
    /// body's WaterSplashEmitter whenever the object crosses the water surface fast enough,
    /// plus a short bell-curved ripple train on the interactive sim.
    ///
    /// Ported from the KWS-side FishSplashEffect driver (FlockReflections), same trigger
    /// state machine (Auto / Manual / Continuous), mapped onto this package's primitives:
    /// prefab instantiation becomes WaterSplashEmitter.EmitSplash (one shared splash look,
    /// no per-splash GameObject churn), the animated wave effector becomes a pulsed
    /// WaterVolume.TrySpawnRippleAt bell (drops ARE impulses here, so a few pulses along the
    /// same sine envelope reproduce the effector animation), and the flat KWS WaterLevel
    /// becomes the live sampled surface height.</summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Breach Splash")]
    public sealed class WaterBreachSplash : MonoBehaviour
    {
        public enum SplashTriggerMode
        {
            Auto,       // splash when crossing the surface with enough vertical speed
            Manual,     // caller invokes TriggerSplash() / TriggerSplashAt()
            Continuous  // periodic splashes while lingering near the surface
        }

        // Vertical speed (units/sec) at which a breach reaches FULL splash strength; the
        // trigger threshold below scales the splash between its floor and 1 across this band.
        const float FullStrengthVerticalSpeed = 3f;
        // Strength floor for a barely-qualifying breach: a soft entry still reads as a splash.
        const float MinBreachStrength = 0.25f;
        // Ripple bell: pulse count across the duration; each pulse strength rides sin(t*PI),
        // mirroring the KWS driver's per-frame animated effector force.
        const int RipplePulses = 4;

        [Header("Splash Output")]
        [Tooltip("Splash emitter that renders the burst (droplets + crown + bubbles). Leave " +
                 "empty to auto-resolve from the water body under the splash point.")]
        [SerializeField] internal WaterSplashEmitter splashEmitter;

        [Tooltip("Base world radius of the splash before the random scale variation.")]
        [Min(0.05f)] [SerializeField] internal float splashRadius = 0.35f;

        [Tooltip("Master scale on the emitted splash strength (droplet count, crown, bubbles). " +
                 "1 = the speed-derived strength as-is; below 1 lightens every splash.")]
        [Range(0f, 2f)] [SerializeField] internal float splashStrengthScale = 1f;

        [Header("Splash Trigger")]
        [Tooltip("Minimum vertical speed (units/sec) to trigger a splash. Prevents splashes " +
                 "while calmly swimming.")]
        [Min(0f)] [SerializeField] float minVerticalSpeed = 0.5f;

        [Tooltip("Minimum seconds between consecutive splashes.")]
        [Min(0f)] [SerializeField] float splashCooldown = 0.4f;

        [Tooltip("How close (world units) to the surface counts as 'near' in Continuous mode.")]
        [Min(0f)] [SerializeField] float surfaceProximityThreshold = 0.3f;

        [Header("Splash Variation")]
        [Tooltip("Minimum random scale multiplier applied to radius and strength per splash.")]
        [Min(0.05f)] [SerializeField] float minScale = 0.5f;

        [Tooltip("Maximum random scale multiplier applied to radius and strength per splash.")]
        [Min(0.05f)] [SerializeField] float maxScale = 1.2f;

        [Header("Trigger Mode")]
        [SerializeField] SplashTriggerMode triggerMode = SplashTriggerMode.Auto;

        [Tooltip("(Continuous mode) Seconds between splashes while near the surface.")]
        [Min(0.05f)] [SerializeField] float continuousInterval = 1f;

        [Header("Ripples")]
        [Tooltip("Also inject a short ripple train at the splash point (the interactive sim " +
                 "ring the flecks and foam respond to).")]
        [SerializeField] bool createRipples = true;

        [Tooltip("Ripple stamp strength in pool-height units (same currency as the body's " +
                 "Ripple Strength).")]
        [Range(0f, 0.08f)] [SerializeField] internal float rippleStrength = 0.03f;

        [Tooltip("Ripple stamp radius in world units.")]
        [Min(0.02f)] [SerializeField] internal float rippleRadius = 0.4f;

        [Tooltip("Seconds the ripple bell lasts (pulses ride a sine envelope across it).")]
        [Min(0.05f)] [SerializeField] float rippleDuration = 0.5f;

        Vector3 _lastPosition;
        float _lastSplashTime = float.NegativeInfinity;
        bool _wasAboveWater;
        bool _hasSurfaceState;
        float _continuousTimer;

        void OnEnable()
        {
            _lastPosition = transform.position;
            // Surface side is unknown until the first successful height sample; Auto mode
            // must not fire a phantom splash on the first frame in the water.
            _hasSurfaceState = false;
        }

        // LateUpdate, like WaterSphereInteractor / WaterInteractable: motion (physics or
        // animation) has settled, so the crossing test sees the frame's real position.
        void LateUpdate()
        {
            Vector3 position = transform.position;
            if (!WaterVolume.TrySampleHeightAt(position, out float surfaceY))
            {
                _hasSurfaceState = false;
                _lastPosition = position;
                return;
            }

            switch (triggerMode)
            {
                case SplashTriggerMode.Auto:
                    UpdateAutoMode(position, surfaceY);
                    break;
                case SplashTriggerMode.Continuous:
                    UpdateContinuousMode(position, surfaceY);
                    break;
                // Manual: nothing per-frame; the caller drives TriggerSplash().
            }

            _lastPosition = position;
        }

        void UpdateAutoMode(Vector3 position, float surfaceY)
        {
            bool isAboveWater = position.y > surfaceY;
            if (!_hasSurfaceState)
            {
                // First valid sample only records which side we are on.
                _wasAboveWater = isAboveWater;
                _hasSurfaceState = true;
                return;
            }

            bool crossedSurface = isAboveWater != _wasAboveWater;
            _wasAboveWater = isAboveWater;
            if (!crossedSurface) return;

            float verticalSpeed = Mathf.Abs(position.y - _lastPosition.y)
                                  / Mathf.Max(Time.deltaTime, 0.001f);
            if (verticalSpeed < minVerticalSpeed) return;
            if (Time.time - _lastSplashTime < splashCooldown) return;

            float strength = Mathf.Lerp(MinBreachStrength, 1f,
                                        Mathf.InverseLerp(minVerticalSpeed,
                                                          FullStrengthVerticalSpeed,
                                                          verticalSpeed));
            SpawnSplash(new Vector3(position.x, surfaceY, position.z), strength);
        }

        void UpdateContinuousMode(Vector3 position, float surfaceY)
        {
            float distanceToSurface = Mathf.Abs(position.y - surfaceY);
            if (distanceToSurface > surfaceProximityThreshold)
            {
                _continuousTimer = 0f;
                return;
            }

            _continuousTimer += Time.deltaTime;
            if (_continuousTimer < continuousInterval) return;
            if (Time.time - _lastSplashTime < splashCooldown) return;

            _continuousTimer = 0f;
            // Continuous splashes are the "fins breaking the water" case: gentler than a breach.
            SpawnSplash(new Vector3(position.x, surfaceY, position.z), MinBreachStrength);
        }

        /// <summary>Manually trigger a splash at this object's position (respects cooldown).</summary>
        public void TriggerSplash() => TriggerSplashAt(transform.position);

        /// <summary>Manually trigger a splash at a world position, snapped to the live surface.</summary>
        public void TriggerSplashAt(Vector3 worldPosition)
        {
            if (Time.time - _lastSplashTime < splashCooldown) return;
            if (!WaterVolume.TrySampleHeightAt(worldPosition, out float surfaceY)) return;
            SpawnSplash(new Vector3(worldPosition.x, surfaceY, worldPosition.z), 1f);
        }

        void SpawnSplash(Vector3 surfacePos, float strength)
        {
            WaterSplashEmitter emitter = ResolveEmitter(surfacePos);
            if (emitter == null) return;

            // The KWS driver's per-splash scale variation, applied to radius AND strength so
            // a small splash is small in every way.
            float scale = Random.Range(minScale, maxScale);
            emitter.EmitSplash(surfacePos, Mathf.Clamp01(strength * scale * splashStrengthScale),
                               splashRadius * scale);
            _lastSplashTime = Time.time;

            if (createRipples)
                StartCoroutine(RippleBell(surfacePos, scale));
        }

        // A few sim stamps riding one sine envelope: the drop kernel injects impulses, so a
        // pulsed bell reproduces the KWS driver's per-frame animated effector force.
        IEnumerator RippleBell(Vector3 surfacePos, float scale)
        {
            float pulseInterval = rippleDuration / RipplePulses;
            for (int pulse = 0; pulse < RipplePulses; pulse++)
            {
                float t = (pulse + 0.5f) / RipplePulses;
                float envelope = Mathf.Sin(t * Mathf.PI);
                WaterVolume.TrySpawnRippleAt(surfacePos, rippleRadius * scale,
                                             rippleStrength * envelope * scale);
                yield return new WaitForSeconds(pulseInterval);
            }
        }

        WaterSplashEmitter ResolveEmitter(Vector3 surfacePos)
        {
            if (splashEmitter != null) return splashEmitter;
            WaterVolume body = WaterVolume.BodyContaining(surfacePos);
            if (body == null) return null;
            // Cache the auto-resolved emitter: bodies do not swap emitters at runtime.
            splashEmitter = body.GetComponent<WaterSplashEmitter>();
            return splashEmitter;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Vector3 center = transform.position;
            if (WaterVolume.TrySampleHeightAt(center, out float surfaceY)) center.y = surfaceY;
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.4f);
            Gizmos.DrawWireSphere(center, surfaceProximityThreshold);
        }
#endif
    }
}
