// WebGpuWater - source composition, appearance and fog-overlay wiring for river foam.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WaterRiverSurface), typeof(WaterRiverFluid))]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Foam")]
    public sealed class WaterRiverFoam : MonoBehaviour, IWaterRiverRendererPropertySource
    {
        const float DefaultStrength = 1f;
        const float DefaultPatternSize = 2f;
        const float DefaultEdgeFeather = 0.15f;
        const float DefaultCoreCut = 0.5f;
        const float DefaultContactStrength = 1f;
        const float DefaultContactDepth = 0.1f;
        const float DefaultCascadeStrength = 1f;
        const float DefaultCascadeStartAngle = 4f;
        const float DefaultCascadeFullAngle = 12f;
        const float DefaultCascadePersistenceMeters = 6f;
        const float MinimumPatternSize = 0.25f;
        const float MinimumContactDepth = 0.001f;
        const float MinimumCascadeAngleSeparation = 0.1f;
        const float MinimumCascadePersistenceMeters = 0.01f;
        const float MaximumCascadeStartAngle = 89f;
        const float MaximumCascadeFullAngle = 90f;
        const float MaximumEdgeFeather = 0.5f;
        const float MinimumCascadeCosineRange = 1e-5f;
        const int CascadeTransportResolution = 256;
        const int CascadeTransportSamplesPerVector = 4;
        const int CascadeTransportVectorCount =
            CascadeTransportResolution / CascadeTransportSamplesPerVector;
        const float EnabledFeature = 1f;
        const float DisabledFeature = 0f;

        [Tooltip("Final intensity applied to contact, cascade and baked turbulence foam.")]
        [Range(0f, DefaultStrength)]
        [SerializeField] internal float overallStrength = DefaultStrength;
        [Range(0f, DefaultStrength)]
        [SerializeField] internal float strength = DefaultStrength;
        [Range(0f, DefaultContactStrength)]
        [SerializeField] internal float contactStrength = DefaultContactStrength;
        [Min(MinimumContactDepth)]
        [SerializeField] internal float contactDepth = DefaultContactDepth;
        [Range(0f, DefaultCascadeStrength)]
        [SerializeField] internal float cascadeStrength = DefaultCascadeStrength;
        [Range(0f, MaximumCascadeStartAngle)]
        [SerializeField] internal float cascadeStartAngle = DefaultCascadeStartAngle;
        [Range(MinimumCascadeAngleSeparation, MaximumCascadeFullAngle)]
        [SerializeField] internal float cascadeFullAngle = DefaultCascadeFullAngle;
        [Tooltip("Downstream drift distance in metres. On flat water, transported cascade " +
                 "coverage falls to about 37% after this distance.")]
        [Min(MinimumCascadePersistenceMeters)]
        [SerializeField] internal float cascadePersistenceMeters =
            DefaultCascadePersistenceMeters;
        [Min(MinimumPatternSize)]
        [SerializeField] internal float patternSize = DefaultPatternSize;
        [Range(0f, MaximumEdgeFeather)]
        [SerializeField] internal float edgeFeather = DefaultEdgeFeather;
        [Range(0f, DefaultStrength)]
        [SerializeField] internal float coreCut = DefaultCoreCut;

        WaterRiverSurface _surface;
        WaterRiverFluid _fluid;
        WaterVolume _registeredVolume;
        Renderer _registeredRenderer;
        readonly Vector4[] _cascadeTransportSamples =
            new Vector4[CascadeTransportVectorCount];
        float _cascadeTransportLength;
        float _transportedCascadeAtMouth;

        internal Texture2D BakedTexture => ActiveData?.PackedTexture;
        internal float RiverLength => ActiveData?.RiverLength ?? 0f;
        internal float TransportLength => _cascadeTransportLength;
        internal float TransportedCascadeAtMouth => _transportedCascadeAtMouth;
        WaterRiverFluidBakeData ActiveData
            => _fluid != null && _fluid.isActiveAndEnabled &&
               _fluid.BakeData != null && _fluid.BakeData.IsValid
                ? _fluid.BakeData
                : null;

        internal float EvaluateCascadeCoverage(Vector3 worldNormal)
        {
            if (!WaterSurfaceKinematics.IsFinite(worldNormal) || worldNormal.sqrMagnitude <= 0f)
                return 0f;

            float vertical = Mathf.Clamp01(Mathf.Abs(worldNormal.normalized.y));
            float startCosine = Mathf.Cos(cascadeStartAngle * Mathf.Deg2Rad);
            float fullCosine = Mathf.Cos(cascadeFullAngle * Mathf.Deg2Rad);
            float cosineRange = Mathf.Max(
                startCosine - fullCosine, MinimumCascadeCosineRange);
            float grade = Mathf.Clamp01((startCosine - vertical) / cosineRange);
            return Mathf.SmoothStep(0f, 1f, grade) * cascadeStrength;
        }

        void OnEnable()
        {
            CacheDependencies();
            _surface.RegisterRendererPropertySource(this);
            _surface.GeometryChanged += Refresh;
            _fluid.ConfigurationChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            UnregisterOverlayRenderer();
            if (_fluid != null) _fluid.ConfigurationChanged -= Refresh;
            if (_surface != null)
            {
                _surface.GeometryChanged -= Refresh;
                _surface.UnregisterRendererPropertySource(this);
            }
            ClearCascadeTransport();
        }

        void OnValidate()
        {
            overallStrength = Mathf.Clamp01(overallStrength);
            strength = Mathf.Clamp01(strength);
            contactStrength = Mathf.Clamp01(contactStrength);
            contactDepth = Mathf.Max(MinimumContactDepth, contactDepth);
            cascadeStrength = Mathf.Clamp01(cascadeStrength);
            cascadeStartAngle = Mathf.Clamp(cascadeStartAngle, 0f, MaximumCascadeStartAngle);
            cascadeFullAngle = Mathf.Clamp(
                cascadeFullAngle,
                cascadeStartAngle + MinimumCascadeAngleSeparation,
                MaximumCascadeFullAngle);
            cascadePersistenceMeters = Mathf.Max(
                MinimumCascadePersistenceMeters, cascadePersistenceMeters);
            patternSize = Mathf.Max(MinimumPatternSize, patternSize);
            edgeFeather = Mathf.Clamp(edgeFeather, 0f, MaximumEdgeFeather);
            coreCut = Mathf.Clamp01(coreCut);
            CacheDependencies();
            if (isActiveAndEnabled) Refresh();
        }

        public void RequestRebuild() => Refresh();

        internal void Configure(float maskStrength)
        {
            if (!float.IsFinite(maskStrength) || maskStrength < 0f || maskStrength > DefaultStrength)
                throw new ArgumentOutOfRangeException(nameof(maskStrength));
            strength = maskStrength;
            Refresh();
        }

        void IWaterRiverRendererPropertySource.WriteRendererProperties(
            MaterialPropertyBlock properties)
        {
            properties.SetFloat(WaterShaderProps.RiverFoamActive, EnabledFeature);
            properties.SetFloat(
                WaterShaderProps.RiverFoamOverallStrength, overallStrength);
            properties.SetFloat(WaterShaderProps.RiverFoamStrength, strength);
            properties.SetFloat(WaterShaderProps.RiverContactFoamStrength, contactStrength);
            properties.SetFloat(WaterShaderProps.FoamContactDepth, contactDepth);
            properties.SetFloat(WaterShaderProps.RiverCascadeFoamStrength, cascadeStrength);
            properties.SetFloat(WaterShaderProps.RiverCascadeStartCosine,
                                Mathf.Cos(cascadeStartAngle * Mathf.Deg2Rad));
            properties.SetFloat(WaterShaderProps.RiverCascadeFullCosine,
                                Mathf.Cos(cascadeFullAngle * Mathf.Deg2Rad));
            bool transportActive =
                _cascadeTransportLength > MinimumCascadePersistenceMeters;
            properties.SetFloat(WaterShaderProps.RiverCascadeTransportActive,
                                transportActive ? EnabledFeature : DisabledFeature);
            if (transportActive)
            {
                properties.SetVectorArray(
                    WaterShaderProps.RiverCascadeTransportSamples, _cascadeTransportSamples);
                properties.SetFloat(
                    WaterShaderProps.RiverCascadeTransportInverseLength,
                    1f / _cascadeTransportLength);
            }
            properties.SetFloat(WaterShaderProps.FoamTileSize, patternSize);
            properties.SetFloat(WaterShaderProps.FoamFeather, edgeFeather);
            properties.SetFloat(WaterShaderProps.FoamCoreCut, coreCut);
            properties.SetFloat(WaterShaderProps.FoamEnabled, EnabledFeature);
        }

        void Refresh()
        {
            CacheDependencies();
            RebuildCascadeTransport();
            UpdateOverlayRegistration();
            _surface?.RequestRendererRefresh();
        }

        void RebuildCascadeTransport()
        {
            WaterRiverSpline spline = _surface != null ? _surface.Spline : null;
            if (spline == null || spline.KnotCount < WaterRiverSpline.MinimumKnotCount)
            {
                ClearCascadeTransport();
                return;
            }

            var distances = new float[CascadeTransportResolution];
            var transportedCoverage = new float[CascadeTransportResolution];
            if (!TryEvaluateCascadeTransport(
                    spline, distances, transportedCoverage, out float riverLength))
            {
                ClearCascadeTransport();
                return;
            }

            PackCascadeTransport(distances, transportedCoverage, riverLength);
            _cascadeTransportLength = riverLength;
            _transportedCascadeAtMouth = transportedCoverage[CascadeTransportResolution - 1];
        }

        bool TryEvaluateCascadeTransport(
            WaterRiverSpline spline, float[] distances, float[] transportedCoverage,
            out float riverLength)
        {
            riverLength = 0f;
            Vector3 previousPosition = Vector3.zero;
            float persistenceMeters = Mathf.Max(
                cascadePersistenceMeters, MinimumCascadePersistenceMeters);
            for (int sampleIndex = 0; sampleIndex < CascadeTransportResolution; sampleIndex++)
            {
                float normalizedT = sampleIndex / (float)(CascadeTransportResolution - 1);
                if (!spline.TryEvaluate(normalizedT, out WaterRiverSplineSample sample))
                    return false;

                float stepDistance = sampleIndex > 0
                    ? Vector3.Distance(previousPosition, sample.Position) : 0f;
                riverLength += stepDistance;
                distances[sampleIndex] = riverLength;
                float generatedCoverage = EvaluateCascadeCoverage(sample.Up);
                if (sampleIndex == 0)
                    transportedCoverage[sampleIndex] = generatedCoverage;
                else
                {
                    float survivingCoverage = transportedCoverage[sampleIndex - 1] *
                        Mathf.Exp(-stepDistance / persistenceMeters);
                    // The local slope defines the available whitewater coverage. Downstream
                    // transport may keep more of an upstream cascade alive, but repeated samples
                    // inside one cascade must not accumulate coverage based on texture resolution.
                    transportedCoverage[sampleIndex] = Mathf.Max(
                        survivingCoverage, generatedCoverage);
                }
                previousPosition = sample.Position;
            }

            return float.IsFinite(riverLength) &&
                   riverLength > MinimumCascadePersistenceMeters;
        }

        void PackCascadeTransport(
            float[] distances, float[] transportedCoverage, float riverLength)
        {
            Array.Clear(_cascadeTransportSamples, 0, _cascadeTransportSamples.Length);
            int sourceIndex = 1;
            for (int sampleIndex = 0; sampleIndex < CascadeTransportResolution; sampleIndex++)
            {
                float targetDistance = riverLength *
                    (sampleIndex / (float)(CascadeTransportResolution - 1));
                while (sourceIndex < CascadeTransportResolution - 1 &&
                       distances[sourceIndex] < targetDistance)
                    sourceIndex++;
                int previousIndex = sourceIndex - 1;
                float interval = Mathf.Max(
                    distances[sourceIndex] - distances[previousIndex],
                    MinimumCascadePersistenceMeters);
                float weight = Mathf.Clamp01(
                    (targetDistance - distances[previousIndex]) / interval);
                float coverage = Mathf.Lerp(
                    transportedCoverage[previousIndex], transportedCoverage[sourceIndex], weight);
                SetCascadeTransportSample(sampleIndex, coverage);
            }
        }

        void SetCascadeTransportSample(int sampleIndex, float coverage)
        {
            int vectorIndex = sampleIndex / CascadeTransportSamplesPerVector;
            int componentIndex = sampleIndex -
                                 vectorIndex * CascadeTransportSamplesPerVector;
            Vector4 packedSamples = _cascadeTransportSamples[vectorIndex];
            packedSamples[componentIndex] = coverage;
            _cascadeTransportSamples[vectorIndex] = packedSamples;
        }

        void ClearCascadeTransport()
        {
            _cascadeTransportLength = 0f;
            _transportedCascadeAtMouth = 0f;
            Array.Clear(_cascadeTransportSamples, 0, _cascadeTransportSamples.Length);
        }

        void CacheDependencies()
        {
            if (_surface == null) _surface = GetComponent<WaterRiverSurface>();
            if (_fluid == null) _fluid = GetComponent<WaterRiverFluid>();
        }

        void UpdateOverlayRegistration()
        {
            WaterVolume targetVolume = _surface != null ? _surface.WaterVolume : null;
            Renderer targetRenderer = targetVolume != null ? _surface.SurfaceRenderer : null;
            if (_registeredVolume == targetVolume && _registeredRenderer == targetRenderer) return;
            UnregisterOverlayRenderer();
            _registeredVolume = targetVolume;
            _registeredRenderer = targetRenderer;
            if (_registeredVolume != null && _registeredRenderer != null)
                _registeredVolume.RegisterExternalFoamRenderer(_registeredRenderer);
        }

        void UnregisterOverlayRenderer()
        {
            if (_registeredVolume != null && _registeredRenderer != null)
                _registeredVolume.UnregisterExternalFoamRenderer(_registeredRenderer);
            _registeredVolume = null;
            _registeredRenderer = null;
        }
    }
}
