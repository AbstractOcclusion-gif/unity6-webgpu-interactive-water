// WebGpuWater - moving-sphere water interactor (Crest-style velocity dipole).
// Drop this on anything that should carve a WAKE as it moves through the water (a boat, a buoy,
// a swimmer, a thrown ball). Unlike WaterInteractable's mouse-like drops - which stamp isotropic
// HEIGHT rings - this injects a DIRECTIONAL velocity dipole (pushed ahead of travel, pulled behind)
// into the sim, so a travelling object lays a V-wake. It self-registers nothing and needs no wiring:
// each frame it measures its own displacement and pushes the water via the WaterVolume facade, which
// resolves whichever body it is over.
//
// Composition, not inheritance: it reads only the transform (plus an optional collider/renderer for
// auto-sizing), so it coexists with WaterBuoyancy (water -> object) and BoatController (propulsion) on
// the same rigidbody to close the loop sim -> buoyancy -> motion -> wake -> sim.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Sphere Interactor")]
    public sealed class WaterSphereInteractor : MonoBehaviour
    {
        // Below this per-frame displacement (world units) the object is effectively still, so there is no
        // wake to inject and nothing is queued (stamps batch into one dispatch via FlushInjections).
        const float MinStepDistance = 1e-4f;
        const float AutoRadiusFloor = 0.05f;
        const float FallbackRadius = 0.5f;
        // Auto radius = the MEAN of the two horizontal bounds extents: a sphere approximation of a
        // possibly-oblong footprint (a box's exact half-diagonal over-drives the dipole on long
        // hulls; the mean under-shoots evenly instead).
        const float HorizontalExtentMeanWeight = 0.5f;

        [Tooltip("Local-space offset from this object's origin used as the sphere centre - the point that " +
                 "pushes the water. Put it near the waterline / hull centre.")]
        [SerializeField] Vector3 centerOffset = Vector3.zero;

        [Tooltip("Sphere radius in WORLD units. 0 = auto from the collider (or renderer) bounds. A larger " +
                 "radius throws a wider, longer-wavelength wake.")]
        [Min(0f)] [SerializeField] float radius = 0f;

        [Tooltip("Master gain on the injected wake. Raise for a stronger bow wave, lower for a subtle ripple.")]
        [Range(0f, 4f)] [SerializeField] internal float strength = 1f;

        [Tooltip("Caps this interactor's vertical plunge/heave contribution to the wake, preventing a " +
                 "boat falling after a swell from throwing an oversized symmetric wave. 0 = off. " +
                 "Does not limit the horizontal travelling wake.")]
        [Range(0f, 0.5f)] [SerializeField] float verticalForceCap = 0f;

        [Tooltip("Ignore any single-frame move larger than this (world units) - a teleport or respawn - so " +
                 "it doesn't fire one huge splash. Normal motion is far below this.")]
        [Min(0f)] [SerializeField] float maxStepDistance = 5f;

        [Tooltip("Boat SPEED (world units/sec) below which the wake fades out. The dipole ADDS velocity " +
                 "every frame, so a boat slowing to a creep dwells over the same water and its pushes pile " +
                 "into one oversized bow wave (then relax once it fully stops). Full wake at/above this " +
                 "speed, tapering to nothing at a standstill. 0 = no fade (old behaviour).")]
        [Min(0f)] [SerializeField] float wakeFadeSpeed = 0.5f;

        Collider _collider;
        Renderer _renderer;
        Vector3 _prevCenter;
        bool _primed;

        void Awake()
        {
            _collider = GetComponent<Collider>();
            _renderer = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            _prevCenter = CenterWorld();
            _primed = true;
        }

        void OnDisable() => _primed = false;

        // LateUpdate (mirrors WaterInteractable): physics has moved the transform and the body's sim step
        // for this frame has run, so we measure the settled displacement and inject the wake for next step.
        void LateUpdate()
        {
            Vector3 center = CenterWorld();
            if (!_primed) { _prevCenter = center; _primed = true; return; }

            Vector3 step = center - _prevCenter;
            _prevCenter = center;

            float stepSqr = step.sqrMagnitude;
            if (stepSqr < MinStepDistance * MinStepDistance) return;      // effectively still
            if (stepSqr > maxStepDistance * maxStepDistance) return;      // teleport guard

            // Low-speed wake fade: the velocity dipole ADDS to the sim each frame, so a boat that slows to
            // a creep dwells over the same cells and those pushes accumulate (toward a ~1/(1-damping) steady
            // state) into one giant bow wave, which relaxes once it fully stops. Taper the injected strength
            // out below wakeFadeSpeed so a slowing boat sheds its wake smoothly instead of piling it.
            // Frame-rate independent: speed is world units / second.
            float dt = Time.deltaTime;
            float speed = dt > 1e-5f ? step.magnitude / dt : 0f;
            float speedRamp = wakeFadeSpeed > 1e-5f ? Mathf.SmoothStep(0f, 1f, speed / wakeFadeSpeed) : 1f;
            if (speedRamp <= 0f) return;

            // The facade applies the submersion weight (an airborne or deeply-sunk sphere makes no wake),
            // so we always forward and let it gate.
            WaterVolume.TrySphereInteractionAt(center, step, EffectiveRadius(), strength * speedRamp,
                                                verticalForceCap);
        }

        Vector3 CenterWorld() => transform.TransformPoint(centerOffset);

        // World radius: explicit if set, otherwise half the horizontal bounds of the collider (preferred)
        // or renderer. Falls back to a small default when the object has neither.
        float EffectiveRadius()
        {
            if (radius > 0f) return radius;
            if (_collider != null)
            {
                Vector3 e = _collider.bounds.extents;
                return Mathf.Max(AutoRadiusFloor, HorizontalExtentMeanWeight * (e.x + e.z));
            }
            if (_renderer != null)
            {
                Vector3 e = _renderer.bounds.extents;
                return Mathf.Max(AutoRadiusFloor, HorizontalExtentMeanWeight * (e.x + e.z));
            }
            return FallbackRadius;
        }

        void OnDrawGizmosSelected()
        {
            float r = radius > 0f
                ? radius
                : (Application.isPlaying ? EffectiveRadius() : FallbackRadius);
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.6f);
            Gizmos.DrawWireSphere(CenterWorld(), r);
        }
    }
}
