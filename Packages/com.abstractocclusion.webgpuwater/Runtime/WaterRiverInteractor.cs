// WebGpuWater - explicit source for river-native analytic disturbances.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Interactor")]
    public sealed class WaterRiverInteractor : MonoBehaviour
    {
        const float DefaultStrength = 1f;
        const float DefaultRadiusScale = 1f;
        const float DefaultWakeLengthScale = 1f;
        const float DefaultFoamStrength = 0.7f;
        const float DefaultImpactStrength = 1f;
        const float DefaultImpactDurationSeconds = 2.5f;
        const float DefaultImpactPropagationSpeed = 1.5f;
        const float DefaultImpactWidthMeters = 0.2f;
        const float MinimumRadiusMeters = 0.05f;
        const float MinimumImpactDurationSeconds = 0.05f;
        const float MinimumImpactWidthMeters = 0.01f;
        const float MinimumDeltaTimeSeconds = 1e-4f;
        const float MaximumTrackedSpeedMetersPerSecond = 100f;
        const float InactiveImpactTime = -1f;

        static readonly List<WaterRiverInteractor> ActiveInteractors = new();

        [Tooltip("Renderer used to derive the water-contact bounds. Empty uses this object, then children.")]
        [SerializeField] Renderer rendererOverride;
        [Tooltip("Collider used to derive the water-contact bounds. Empty uses this object, then children, then the renderer bounds.")]
        [SerializeField] Collider colliderOverride;
        [Tooltip("World-metre disturbance radius. Zero derives it from the projected object bounds.")]
        [Min(0f)] [SerializeField] float radiusOverride;
        [Tooltip("Multiplier on persistent pile-up, depression, and wake displacement.")]
        [Min(0f)] [SerializeField] float strength = DefaultStrength;
        [Tooltip("Multiplier applied only when radius is derived from the object bounds.")]
        [Min(MinimumRadiusMeters)] [SerializeField] float radiusScale = DefaultRadiusScale;
        [Tooltip("Multiplier on the downstream wake length selected by the river disturbance system.")]
        [Min(0f)] [SerializeField] float wakeLengthScale = DefaultWakeLengthScale;
        [Tooltip("Relative contribution to disturbance foam.")]
        [Min(0f)] [SerializeField] float foamStrength = DefaultFoamStrength;

        [Header("Impact ring")]
        [Tooltip("Multiplier on the river system's maximum displacement for one impact ring.")]
        [Min(0f)] [SerializeField] float impactStrength = DefaultImpactStrength;
        [Tooltip("Seconds an explicitly emitted impact ring remains alive.")]
        [Min(MinimumImpactDurationSeconds)]
        [SerializeField] float impactDuration = DefaultImpactDurationSeconds;
        [Tooltip("Impact-ring expansion speed in world metres per second.")]
        [Min(0f)] [SerializeField] float impactPropagationSpeed = DefaultImpactPropagationSpeed;
        [Tooltip("World-metre width of the expanding impact crest and trough.")]
        [Min(MinimumImpactWidthMeters)]
        [SerializeField] float impactWidth = DefaultImpactWidthMeters;

        Renderer _renderer;
        Collider _collider;
        Rigidbody _rigidbody;
        Vector3 _previousCentre;
        Vector3 _trackedVelocity;
        bool _tracksCentre;
        float _impactStartTime = InactiveImpactTime;
        float _impactScale;
        Vector3 _impactWorldPosition;

        internal static IReadOnlyList<WaterRiverInteractor> Active => ActiveInteractors;
        internal float Strength => strength;
        internal float WakeLengthScale => wakeLengthScale;
        internal float FoamStrength => foamStrength;

        internal static void ResetStaticState() => ActiveInteractors.Clear();

        void OnEnable()
        {
            CacheDependencies();
            PrimeVelocityTracking();
            if (!ActiveInteractors.Contains(this)) ActiveInteractors.Add(this);
        }

        void OnDisable()
        {
            ActiveInteractors.Remove(this);
            _tracksCentre = false;
        }

        void OnValidate()
        {
            radiusOverride = Mathf.Max(0f, radiusOverride);
            strength = Mathf.Max(0f, strength);
            radiusScale = Mathf.Max(MinimumRadiusMeters, radiusScale);
            wakeLengthScale = Mathf.Max(0f, wakeLengthScale);
            foamStrength = Mathf.Max(0f, foamStrength);
            impactStrength = Mathf.Max(0f, impactStrength);
            impactDuration = Mathf.Max(MinimumImpactDurationSeconds, impactDuration);
            impactPropagationSpeed = Mathf.Max(0f, impactPropagationSpeed);
            impactWidth = Mathf.Max(MinimumImpactWidthMeters, impactWidth);
            CacheDependencies();
        }

        void LateUpdate()
        {
            if (!TryGetBounds(out Bounds bounds))
            {
                _tracksCentre = false;
                _trackedVelocity = Vector3.zero;
                return;
            }

            float deltaTime = Application.isPlaying ? Time.deltaTime : 0f;
            if (!_tracksCentre || deltaTime < MinimumDeltaTimeSeconds)
            {
                _previousCentre = bounds.center;
                _trackedVelocity = Vector3.zero;
                _tracksCentre = true;
                return;
            }

            Vector3 displacement = bounds.center - _previousCentre;
            Vector3 candidateVelocity = displacement / deltaTime;
            _trackedVelocity = candidateVelocity.magnitude <= MaximumTrackedSpeedMetersPerSecond
                ? candidateVelocity : Vector3.zero;
            _previousCentre = bounds.center;
        }

        /// <summary>Emit one expanding river-local impact ring at the current contact bounds.</summary>
        public void EmitImpact(float strengthMultiplier = DefaultStrength)
        {
            if (!float.IsFinite(strengthMultiplier) || strengthMultiplier < 0f)
                throw new ArgumentOutOfRangeException(nameof(strengthMultiplier));
            if (!TryGetBounds(out Bounds bounds))
                throw new InvalidOperationException(
                    "A river interactor needs a Collider or Renderer before it can emit an impact.");

            _impactWorldPosition = bounds.center;
            _impactScale = strengthMultiplier;
            _impactStartTime = CurrentTime;
        }

        internal bool TryGetBounds(out Bounds bounds)
        {
            CacheDependencies();
            if (_collider != null)
            {
                bounds = _collider.bounds;
                return true;
            }
            if (_renderer != null)
            {
                bounds = _renderer.bounds;
                return true;
            }
            bounds = default;
            return false;
        }

        internal Vector3 VelocityAt(Vector3 worldPoint)
        {
            CacheDependencies();
            return _rigidbody != null ? _rigidbody.GetPointVelocity(worldPoint) : _trackedVelocity;
        }

        internal float ResolveRadius(in WaterRiverSplineSample sample, in Bounds bounds)
        {
            if (radiusOverride > 0f) return Mathf.Max(MinimumRadiusMeters, radiusOverride);
            Vector3 extents = bounds.extents;
            float lateralExtent = ProjectAabbExtent(extents, sample.Right);
            float longitudinalExtent = ProjectAabbExtent(extents, sample.Tangent);
            return Mathf.Max(MinimumRadiusMeters,
                             Mathf.Max(lateralExtent, longitudinalExtent) * radiusScale);
        }

        internal bool TryGetImpact(out Vector3 worldPosition, out float amplitude,
                                   out float radius, out float width, out float fade)
        {
            worldPosition = _impactWorldPosition;
            amplitude = 0f;
            radius = 0f;
            width = impactWidth;
            fade = 0f;
            if (_impactStartTime < 0f) return false;

            float age = Mathf.Max(0f, CurrentTime - _impactStartTime);
            if (age >= impactDuration)
            {
                _impactStartTime = InactiveImpactTime;
                return false;
            }

            fade = 1f - age / impactDuration;
            amplitude = impactStrength * _impactScale;
            radius = age * impactPropagationSpeed;
            return amplitude > 0f && fade > 0f;
        }

        static float ProjectAabbExtent(Vector3 extents, Vector3 direction)
            => Mathf.Abs(direction.x) * extents.x +
               Mathf.Abs(direction.y) * extents.y +
               Mathf.Abs(direction.z) * extents.z;

        float CurrentTime => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        void CacheDependencies()
        {
            _collider = colliderOverride != null ? colliderOverride : GetComponent<Collider>();
            if (_collider == null) _collider = GetComponentInChildren<Collider>();
            _renderer = rendererOverride != null ? rendererOverride : GetComponent<Renderer>();
            if (_renderer == null) _renderer = GetComponentInChildren<Renderer>();
            _rigidbody = GetComponentInParent<Rigidbody>();
        }

        void PrimeVelocityTracking()
        {
            if (!TryGetBounds(out Bounds bounds)) return;
            _previousCentre = bounds.center;
            _trackedVelocity = Vector3.zero;
            _tracksCentre = true;
        }
    }
}
