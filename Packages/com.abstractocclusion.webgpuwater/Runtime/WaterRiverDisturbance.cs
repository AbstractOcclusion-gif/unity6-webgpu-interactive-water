// WebGpuWater - river-native analytic displacement, wake, impact, and query source.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WaterRiverSurface))]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Disturbance")]
    public sealed class WaterRiverDisturbance : MonoBehaviour, IWaterRiverRendererPropertySource
    {
        internal const int MaximumSourceCount = 8;
        internal const float UpstreamCrescentRadiusScale = 1f;
        internal const float UpstreamCrescentWidthScale = 0.32f;
        internal const float UpstreamCrescentMinimumFacing = -0.15f;
        internal const float UpstreamCrescentFullFacing = 0.45f;
        internal const float DownstreamOffsetRadiusScale = 1.15f;
        internal const float DownstreamDepressionScale = 0.65f;
        internal const float WakeAmplitudeScale = 0.28f;
        internal const float WakeAngleTangent = 0.36f;
        internal const float WakeRidgeWidthScale = 0.32f;
        internal const float WakePhaseRadiusScale = 1.5f;
        internal const float WakeStartRadiusScale = 0.25f;
        internal const float ExponentialClamp = 80f;
        internal const float ImpactProfileScale = 2f;

        const float DefaultMaximumDisplacementMeters = 0.08f;
        const float DefaultMinimumRelativeSpeed = 0.05f;
        const float DefaultFullRelativeSpeed = 0.75f;
        const float DefaultWakeLengthScale = 6f;
        const float DefaultFoamStrength = 1f;
        const float DefaultNormalSampleDistanceMeters = 0.05f;
        const float DefaultMinimumSurfaceUp = 0.15f;
        const float DefaultFullSurfaceUp = 0.5f;
        const float MinimumDistanceMeters = 0.001f;
        const float SurfaceOwnershipInsetMeters = 0.01f;
        const float HalfWidth = 0.5f;
        const float EnabledFeature = 1f;
        const float DisabledFeature = 0f;

        const string ActivePropertyName = "_RiverDisturbanceActive";
        const string CountPropertyName = "_RiverDisturbanceCount";
        const string PositionRadiusPropertyName = "_RiverDisturbancePositionRadius";
        const string DirectionWakePropertyName = "_RiverDisturbanceDirectionWake";
        const string ImpactPropertyName = "_RiverDisturbanceImpact";
        const string NormalStepPropertyName = "_RiverDisturbanceNormalStep";

        static readonly int ActivePropertyId = Shader.PropertyToID(ActivePropertyName);
        static readonly int CountPropertyId = Shader.PropertyToID(CountPropertyName);
        static readonly int PositionRadiusPropertyId = Shader.PropertyToID(PositionRadiusPropertyName);
        static readonly int DirectionWakePropertyId = Shader.PropertyToID(DirectionWakePropertyName);
        static readonly int ImpactPropertyId = Shader.PropertyToID(ImpactPropertyName);
        static readonly int NormalStepPropertyId = Shader.PropertyToID(NormalStepPropertyName);

        [Tooltip("Maximum persistent displacement in world metres at full relative speed.")]
        [Min(0f)] [SerializeField] float maximumDisplacement = DefaultMaximumDisplacementMeters;
        [Tooltip("Relative water/object speed below which no sustained disturbance is emitted.")]
        [Min(0f)] [SerializeField] float minimumRelativeSpeed = DefaultMinimumRelativeSpeed;
        [Tooltip("Relative speed that reaches the authored maximum displacement.")]
        [Min(MinimumDistanceMeters)]
        [SerializeField] float fullRelativeSpeed = DefaultFullRelativeSpeed;
        [Tooltip("Persistent downstream wake length in source radii.")]
        [Min(0f)] [SerializeField] float wakeLengthScale = DefaultWakeLengthScale;
        [Tooltip("Master multiplier for analytic disturbance foam.")]
        [Min(0f)] [SerializeField] float foamStrength = DefaultFoamStrength;
        [Tooltip("Metric finite-difference step used by both shader and gameplay normal sampling.")]
        [Min(MinimumDistanceMeters)]
        [SerializeField] float normalSampleDistance = DefaultNormalSampleDistanceMeters;
        [Tooltip("Absolute transported-normal Y below which free-surface disturbance is disabled.")]
        [Range(0f, 1f)] [SerializeField] float minimumSurfaceUp = DefaultMinimumSurfaceUp;
        [Tooltip("Absolute transported-normal Y at which full disturbance strength is restored.")]
        [Range(0f, 1f)] [SerializeField] float fullSurfaceUp = DefaultFullSurfaceUp;

        readonly Vector4[] _positionRadius = new Vector4[MaximumSourceCount];
        readonly Vector4[] _directionWake = new Vector4[MaximumSourceCount];
        readonly Vector4[] _impact = new Vector4[MaximumSourceCount];
        readonly Source[] _sources = new Source[MaximumSourceCount];
        float[] _rowDistanceMeters = Array.Empty<float>();
        WaterRiverSurface _surface;
        int _sourceCount;

        internal int SourceCount => _sourceCount;

        internal readonly struct Source
        {
            internal readonly Vector4 PositionRadius;
            internal readonly Vector4 DirectionWake;
            internal readonly Vector4 Impact;

            internal Source(Vector4 positionRadius, Vector4 directionWake, Vector4 impact)
            {
                PositionRadius = positionRadius;
                DirectionWake = directionWake;
                Impact = impact;
            }
        }

        void OnEnable()
        {
            CacheSurface();
            _surface.RegisterDisturbance(this);
            _surface.GeometryChanged += HandleGeometryChanged;
            RebuildDistanceLookup();
            RebuildSources();
            _surface.RequestRendererRefresh();
        }

        void OnDisable()
        {
            if (_surface == null) return;
            _surface.GeometryChanged -= HandleGeometryChanged;
            _surface.UnregisterDisturbance(this);
            _surface.RequestRendererRefresh();
        }

        void OnValidate()
        {
            maximumDisplacement = Mathf.Max(0f, maximumDisplacement);
            minimumRelativeSpeed = Mathf.Max(0f, minimumRelativeSpeed);
            fullRelativeSpeed = Mathf.Max(minimumRelativeSpeed + MinimumDistanceMeters,
                                          fullRelativeSpeed);
            wakeLengthScale = Mathf.Max(0f, wakeLengthScale);
            foamStrength = Mathf.Max(0f, foamStrength);
            normalSampleDistance = Mathf.Max(MinimumDistanceMeters, normalSampleDistance);
            minimumSurfaceUp = Mathf.Clamp01(minimumSurfaceUp);
            fullSurfaceUp = Mathf.Clamp(fullSurfaceUp,
                                        minimumSurfaceUp + MinimumDistanceMeters, 1f);
            CacheSurface();
            if (!isActiveAndEnabled) return;
            RebuildDistanceLookup();
            RebuildSources();
            _surface.RequestRendererRefresh();
        }

        void LateUpdate() => RebuildSources();

        void IWaterRiverRendererPropertySource.WriteRendererProperties(
            MaterialPropertyBlock properties)
        {
            properties.SetFloat(ActivePropertyId,
                isActiveAndEnabled && _sourceCount > 0 ? EnabledFeature : DisabledFeature);
            properties.SetFloat(CountPropertyId, _sourceCount);
            properties.SetVectorArray(PositionRadiusPropertyId, _positionRadius);
            properties.SetVectorArray(DirectionWakePropertyId, _directionWake);
            properties.SetVectorArray(ImpactPropertyId, _impact);
            properties.SetFloat(NormalStepPropertyId, normalSampleDistance);
        }

        internal bool TrySample(Vector3 worldPoint, in WaterRiverSplineSample splineSample,
                                out float height, out Vector2 normalTilt, out float foam)
        {
            height = 0f;
            normalTilt = Vector2.zero;
            foam = 0f;
            if (!isActiveAndEnabled || _sourceCount == 0) return false;

            float lateralMeters = Vector3.Dot(
                worldPoint - splineSample.Position, splineSample.Right);
            float longitudinalMeters = LongitudinalMeters(in splineSample);
            Vector2 metricPosition = new Vector2(lateralMeters, longitudinalMeters);
            height = EvaluateHeight(metricPosition, out foam);
            float step = normalSampleDistance;
            float left = EvaluateHeight(metricPosition - Vector2.right * step, out _);
            float right = EvaluateHeight(metricPosition + Vector2.right * step, out _);
            float behind = EvaluateHeight(metricPosition - Vector2.up * step, out _);
            float ahead = EvaluateHeight(metricPosition + Vector2.up * step, out _);
            normalTilt = new Vector2(left - right, behind - ahead) / (2f * step);
            return Mathf.Abs(height) > 0f || normalTilt.sqrMagnitude > 0f || foam > 0f;
        }

        internal float EvaluateHeight(Vector2 metricPosition, out float foam)
        {
            float height = 0f;
            foam = 0f;
            for (int index = 0; index < _sourceCount; index++)
            {
                height += EvaluateSourceHeight(metricPosition, in _sources[index], out float sourceFoam);
                foam = Mathf.Max(foam, sourceFoam);
            }
            return height;
        }

        internal static float EvaluateSourceHeight(Vector2 metricPosition, in Source source,
                                                   out float foam)
        {
            Vector2 sourcePosition = new Vector2(
                source.PositionRadius.x, source.PositionRadius.y);
            float radius = Mathf.Max(source.PositionRadius.z, MinimumDistanceMeters);
            float amplitude = source.PositionRadius.w;
            Vector2 direction = new Vector2(source.DirectionWake.x, source.DirectionWake.y);
            Vector2 delta = metricPosition - sourcePosition;

            Vector2 downstream = delta - direction * (radius * DownstreamOffsetRadiusScale);
            float inverseRadiusSquared = 1f / (radius * radius);
            float upstreamCrescent = EvaluateUpstreamCrescent(delta, direction, radius);
            float downstreamGaussian = SafeExp(-downstream.sqrMagnitude * inverseRadiusSquared);
            float height = amplitude *
                (upstreamCrescent - DownstreamDepressionScale * downstreamGaussian);

            float along = Vector2.Dot(delta, direction);
            Vector2 perpendicular = new Vector2(-direction.y, direction.x);
            float lateral = Mathf.Abs(Vector2.Dot(delta, perpendicular));
            float wakeLength = Mathf.Max(source.DirectionWake.z, radius);
            float wakeStart = SmoothStep(0f, radius * WakeStartRadiusScale, along);
            float wakeEnvelope = wakeStart * SafeExp(-Mathf.Max(0f, along) / wakeLength);
            float ridgeDistance = lateral - Mathf.Max(0f, along) * WakeAngleTangent;
            float ridgeWidth = Mathf.Max(radius * WakeRidgeWidthScale, MinimumDistanceMeters);
            float ridge = SafeExp(-(ridgeDistance * ridgeDistance) / (ridgeWidth * ridgeWidth));
            float phase = Mathf.Max(0f, along) * Mathf.PI /
                          Mathf.Max(radius * WakePhaseRadiusScale, MinimumDistanceMeters);
            height += amplitude * WakeAmplitudeScale * wakeEnvelope * ridge * Mathf.Sin(phase);

            float impactAmplitude = source.Impact.x;
            float impactRadius = source.Impact.y;
            float impactWidth = Mathf.Max(source.Impact.z, MinimumDistanceMeters);
            float radialOffset = (delta.magnitude - impactRadius) / impactWidth;
            float impactProfile = (1f - ImpactProfileScale * radialOffset * radialOffset) *
                                  SafeExp(-radialOffset * radialOffset);
            height += impactAmplitude * impactProfile;

            foam = source.DirectionWake.w * Mathf.Clamp01(
                Mathf.Max(upstreamCrescent, wakeEnvelope * ridge) +
                Mathf.Abs(impactProfile) * Mathf.Clamp01(Mathf.Abs(impactAmplitude)));
            return height;
        }

        internal static float EvaluateUpstreamCrescent(Vector2 delta, Vector2 direction,
                                                       float sourceRadius)
        {
            float radius = Mathf.Max(sourceRadius, MinimumDistanceMeters);
            float distance = delta.magnitude;
            float shellWidth = Mathf.Max(
                radius * UpstreamCrescentWidthScale, MinimumDistanceMeters);
            float radialOffset =
                (distance - radius * UpstreamCrescentRadiusScale) / shellWidth;
            float radialShell = SafeExp(-radialOffset * radialOffset);
            float upstreamFacing = -Vector2.Dot(delta, direction) /
                                   Mathf.Max(distance, MinimumDistanceMeters);
            float angularEnvelope = SmoothStep(
                UpstreamCrescentMinimumFacing, UpstreamCrescentFullFacing, upstreamFacing);
            return radialShell * angularEnvelope;
        }

        internal static float RelativeSpeedResponse(float relativeSpeed, float minimumSpeed,
                                                    float fullSpeed)
        {
            if (!float.IsFinite(relativeSpeed) || relativeSpeed <= minimumSpeed) return 0f;
            float range = Mathf.Max(fullSpeed - minimumSpeed, MinimumDistanceMeters);
            return SmoothStep(0f, 1f, (relativeSpeed - minimumSpeed) / range);
        }

        void RebuildSources()
        {
            Array.Clear(_positionRadius, 0, _positionRadius.Length);
            Array.Clear(_directionWake, 0, _directionWake.Length);
            Array.Clear(_impact, 0, _impact.Length);
            Array.Clear(_sources, 0, _sources.Length);
            _sourceCount = 0;
            if (_surface == null || _surface.Spline == null || maximumDisplacement <= 0f) return;

            var interactors = WaterRiverInteractor.Active;
            for (int index = 0; index < interactors.Count && _sourceCount < MaximumSourceCount; index++)
            {
                WaterRiverInteractor interactor = interactors[index];
                if (interactor == null || !interactor.isActiveAndEnabled ||
                    !interactor.TryGetBounds(out Bounds bounds)) continue;

                if (TryProjectOwned(bounds.center, out WaterRiverSplineSample spline,
                                    out float lateralMeters, out float longitudinalMeters) &&
                    IntersectsSurface(bounds, in spline))
                {
                    AddPersistentSource(interactor, in bounds, in spline,
                                        lateralMeters, longitudinalMeters);
                }

                if (_sourceCount >= MaximumSourceCount ||
                    !interactor.TryGetImpact(out Vector3 impactPosition, out float impactAmplitude,
                                             out float impactRadius, out float impactWidth,
                                             out float impactFade)) continue;
                if (!TryProjectOwned(impactPosition, out WaterRiverSplineSample impactSpline,
                                     out float impactLateral, out float impactLongitudinal)) continue;

                float slopeResponse = SurfaceSlopeResponse(impactSpline.Up);
                float clampedAmplitude = Mathf.Min(
                    maximumDisplacement, maximumDisplacement * impactAmplitude) *
                    impactFade * slopeResponse;
                AddSource(new Source(
                    new Vector4(impactLateral, impactLongitudinal,
                                Mathf.Max(impactWidth, MinimumDistanceMeters), 0f),
                    new Vector4(0f, 1f, Mathf.Max(impactWidth, MinimumDistanceMeters),
                                foamStrength * interactor.FoamStrength * impactFade),
                    new Vector4(clampedAmplitude, impactRadius, impactWidth, impactFade)));
            }
        }

        void AddPersistentSource(WaterRiverInteractor interactor, in Bounds bounds,
                                 in WaterRiverSplineSample spline, float lateralMeters,
                                 float longitudinalMeters)
        {
            Vector3 waterVelocity = ResolveCurrent(bounds.center, in spline);
            Vector3 objectVelocity = interactor.VelocityAt(bounds.center);
            Vector3 relativeFlow = waterVelocity - objectVelocity;
            Vector2 ribbonFlow = new Vector2(
                Vector3.Dot(relativeFlow, spline.Right),
                Vector3.Dot(relativeFlow, spline.Tangent));
            float relativeSpeed = ribbonFlow.magnitude;
            float speedResponse = RelativeSpeedResponse(
                relativeSpeed, minimumRelativeSpeed, fullRelativeSpeed);
            if (speedResponse <= 0f) return;

            float slopeResponse = SurfaceSlopeResponse(spline.Up);
            if (slopeResponse <= 0f) return;
            Vector2 direction = ribbonFlow / relativeSpeed;
            float radius = interactor.ResolveRadius(in spline, in bounds);
            float amplitude = Mathf.Min(
                maximumDisplacement,
                maximumDisplacement * interactor.Strength * speedResponse * slopeResponse);
            float wakeLength = radius * wakeLengthScale * interactor.WakeLengthScale;
            float sourceFoam = foamStrength * interactor.FoamStrength * speedResponse;
            AddSource(new Source(
                new Vector4(lateralMeters, longitudinalMeters, radius, amplitude),
                new Vector4(direction.x, direction.y, wakeLength, sourceFoam),
                Vector4.zero));
        }

        void AddSource(in Source source)
        {
            if (_sourceCount >= MaximumSourceCount) return;
            _sources[_sourceCount] = source;
            _positionRadius[_sourceCount] = source.PositionRadius;
            _directionWake[_sourceCount] = source.DirectionWake;
            _impact[_sourceCount] = source.Impact;
            _sourceCount++;
        }

        bool TryProjectOwned(Vector3 worldPoint, out WaterRiverSplineSample sample,
                             out float lateralMeters, out float longitudinalMeters)
        {
            sample = default;
            lateralMeters = 0f;
            longitudinalMeters = 0f;
            WaterRiverSpline spline = _surface.Spline;
            if (spline == null || !spline.TryProjectPoint(worldPoint, out sample, out _)) return false;
            lateralMeters = Vector3.Dot(worldPoint - sample.Position, sample.Right);
            if (Mathf.Abs(lateralMeters) > sample.Width * HalfWidth) return false;

            Vector3 ownershipPoint = sample.Position + sample.Right * lateralMeters -
                                     sample.Up * SurfaceOwnershipInsetMeters;
            WaterDomainQueryOptions options =
                WaterDomainQueryOptions.ForIntent(WaterQueryIntent.ContainingVolume);
            options.Fields = WaterQueryFields.Height;
            options.ExcludeInteractiveRipples = true;
            if (!WaterDomainResolver.Resolve(ownershipPoint, in options,
                                             out WaterDomainSample domain) ||
                domain.Provider != _surface.SurfaceProvider) return false;

            longitudinalMeters = LongitudinalMeters(in sample);
            return true;
        }

        bool IntersectsSurface(in Bounds bounds, in WaterRiverSplineSample sample)
        {
            float signedDistance = Vector3.Dot(bounds.center - sample.Position, sample.Up);
            float support = ProjectAabbExtent(bounds.extents, sample.Up);
            return signedDistance <= support &&
                   signedDistance >= -_surface.GameplayDepthMeters - support;
        }

        Vector3 ResolveCurrent(Vector3 worldPoint, in WaterRiverSplineSample spline)
        {
            WaterRiverCurrentField field = _surface.CurrentField;
            if (field != null && field.SampleCurrent(worldPoint, out Vector3 velocity))
                return velocity;
            return spline.Tangent * spline.Speed;
        }

        float SurfaceSlopeResponse(Vector3 surfaceUp)
        {
            float vertical = Mathf.Abs(surfaceUp.normalized.y);
            return SmoothStep(minimumSurfaceUp, fullSurfaceUp, vertical);
        }

        internal float LongitudinalMeters(in WaterRiverSplineSample sample)
        {
            EnsureDistanceLookup();
            // A continuation inherits its upstream ribbon's UV1.y origin. Disturbance sources and
            // CPU queries must use that same chained metric or the GPU evaluates them kilometres
            // apart in long river networks even though their world positions coincide.
            float origin = _surface != null ? _surface.SourceLongitudinalMeters : 0f;
            if (_rowDistanceMeters.Length < 2) return origin;
            int samplesPerSegment = Mathf.Max(
                WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment,
                _surface.samplesPerSegment);
            float rowPosition = sample.SegmentIndex * samplesPerSegment +
                                sample.SegmentT * samplesPerSegment;
            int lower = Mathf.Clamp(Mathf.FloorToInt(rowPosition), 0,
                                    _rowDistanceMeters.Length - 1);
            int upper = Mathf.Min(lower + 1, _rowDistanceMeters.Length - 1);
            float localDistance = Mathf.Lerp(
                _rowDistanceMeters[lower], _rowDistanceMeters[upper], rowPosition - lower);
            return origin + localDistance;
        }

        void EnsureDistanceLookup()
        {
            WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
            int expected = spline != null
                ? spline.SegmentCount * Mathf.Max(
                    WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment,
                    _surface.samplesPerSegment) + 1
                : 0;
            if (_rowDistanceMeters.Length != expected) RebuildDistanceLookup();
        }

        void RebuildDistanceLookup()
        {
            WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
            if (spline == null || spline.SegmentCount <= 0)
            {
                _rowDistanceMeters = Array.Empty<float>();
                return;
            }

            int samplesPerSegment = Mathf.Max(
                WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment,
                _surface.samplesPerSegment);
            int rowCount = spline.SegmentCount * samplesPerSegment + 1;
            _rowDistanceMeters = new float[rowCount];
            Vector3 previous = Vector3.zero;
            bool hasPrevious = false;
            int row = 0;
            for (int segment = 0; segment < spline.SegmentCount; segment++)
            {
                int firstStep = segment == 0 ? 0 : 1;
                for (int step = firstStep; step <= samplesPerSegment; step++)
                {
                    if (!spline.TryEvaluateSegment(segment, step / (float)samplesPerSegment,
                                                   out WaterRiverSplineSample sample)) continue;
                    if (hasPrevious)
                        _rowDistanceMeters[row] = _rowDistanceMeters[row - 1] +
                                                  Vector3.Distance(previous, sample.Position);
                    previous = sample.Position;
                    hasPrevious = true;
                    row++;
                }
            }
        }

        void HandleGeometryChanged()
        {
            RebuildDistanceLookup();
            RebuildSources();
        }

        void CacheSurface()
        {
            if (_surface == null) _surface = GetComponent<WaterRiverSurface>();
        }

        static float ProjectAabbExtent(Vector3 extents, Vector3 direction)
            => Mathf.Abs(direction.x) * extents.x +
               Mathf.Abs(direction.y) * extents.y +
               Mathf.Abs(direction.z) * extents.z;

        static float SafeExp(float exponent) => Mathf.Exp(Mathf.Max(-ExponentialClamp, exponent));

        static float SmoothStep(float minimum, float maximum, float value)
        {
            float denominator = Mathf.Max(maximum - minimum, MinimumDistanceMeters);
            float normalized = Mathf.Clamp01((value - minimum) / denominator);
            return normalized * normalized * (3f - 2f * normalized);
        }
    }
}
