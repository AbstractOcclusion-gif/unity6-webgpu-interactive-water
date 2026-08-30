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
        const float MinimumPatternSize = 0.25f;
        const float MinimumContactDepth = 0.001f;
        const float MinimumCascadeAngleSeparation = 0.1f;
        const float MaximumCascadeStartAngle = 89f;
        const float MaximumCascadeFullAngle = 90f;
        const float MaximumEdgeFeather = 0.5f;
        const float EnabledFeature = 1f;
        const float DisabledFeature = 0f;

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

        internal Texture2D BakedTexture => ActiveData?.PackedTexture;
        internal float RiverLength => ActiveData?.RiverLength ?? 0f;
        WaterRiverFluidBakeData ActiveData
            => _fluid != null && _fluid.isActiveAndEnabled &&
               _fluid.BakeData != null && _fluid.BakeData.IsValid
                ? _fluid.BakeData
                : null;

        void OnEnable()
        {
            CacheDependencies();
            _surface.RegisterRendererPropertySource(this);
            _fluid.ConfigurationChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            UnregisterOverlayRenderer();
            if (_fluid != null) _fluid.ConfigurationChanged -= Refresh;
            if (_surface != null) _surface.UnregisterRendererPropertySource(this);
        }

        void OnValidate()
        {
            strength = Mathf.Clamp01(strength);
            contactStrength = Mathf.Clamp01(contactStrength);
            contactDepth = Mathf.Max(MinimumContactDepth, contactDepth);
            cascadeStrength = Mathf.Clamp01(cascadeStrength);
            cascadeStartAngle = Mathf.Clamp(cascadeStartAngle, 0f, MaximumCascadeStartAngle);
            cascadeFullAngle = Mathf.Clamp(
                cascadeFullAngle,
                cascadeStartAngle + MinimumCascadeAngleSeparation,
                MaximumCascadeFullAngle);
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
            properties.SetFloat(WaterShaderProps.RiverFoamStrength, strength);
            properties.SetFloat(WaterShaderProps.RiverContactFoamStrength, contactStrength);
            properties.SetFloat(WaterShaderProps.FoamContactDepth, contactDepth);
            properties.SetFloat(WaterShaderProps.RiverCascadeFoamStrength, cascadeStrength);
            properties.SetFloat(WaterShaderProps.RiverCascadeStartCosine,
                                Mathf.Cos(cascadeStartAngle * Mathf.Deg2Rad));
            properties.SetFloat(WaterShaderProps.RiverCascadeFullCosine,
                                Mathf.Cos(cascadeFullAngle * Mathf.Deg2Rad));
            properties.SetFloat(WaterShaderProps.FoamTileSize, patternSize);
            properties.SetFloat(WaterShaderProps.FoamFeather, edgeFeather);
            properties.SetFloat(WaterShaderProps.FoamCoreCut, coreCut);
            properties.SetFloat(WaterShaderProps.FoamEnabled, EnabledFeature);
        }

        void Refresh()
        {
            CacheDependencies();
            UpdateOverlayRegistration();
            _surface?.RequestRendererRefresh();
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
