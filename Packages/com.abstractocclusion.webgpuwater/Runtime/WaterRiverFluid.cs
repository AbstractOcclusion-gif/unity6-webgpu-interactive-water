// WebGpuWater - baked river-fluid ownership and renderer binding.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WaterRiverSurface))]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Fluid")]
    public sealed class WaterRiverFluid : MonoBehaviour, IWaterRiverRendererPropertySource
    {
        internal const int DefaultLateralResolution = 64;
        internal const int DefaultLongitudinalResolution = 256;
        internal const int DefaultIterations = 400;
        internal const int MaximumResolution = 2048;
        internal const int MaximumIterations = 5000;

        const float DefaultDeltaTime = 0.13f;
        const float DefaultViscosity = 0.06f;
        const float DefaultPressure = 0.15f;
        const float DefaultForce = 0.4f;
        const float DefaultVelocityDecay = 0.9999f;
        const float DefaultVorticity = 0.13f;
        const float DefaultFoamThreshold = 0.05f;
        const float DefaultFoamStrength = 5f;
        const float EnabledFeature = 1f;
        const float DisabledFeature = 0f;

        [SerializeField] internal WaterRiverFluidBakeData bakeData;
        [Range(WaterRiverFluidSolver.MinimumResolution, MaximumResolution)]
        [SerializeField] internal int lateralResolution = DefaultLateralResolution;
        [Range(WaterRiverFluidSolver.MinimumResolution, MaximumResolution)]
        [SerializeField] internal int longitudinalResolution = DefaultLongitudinalResolution;
        [Range(WaterRiverFluidSolver.MinimumIterations, MaximumIterations)]
        [SerializeField] internal int iterations = DefaultIterations;
        [Tooltip("Static colliders on these layers are rasterized as solid fluid cells.")]
        [SerializeField] internal LayerMask obstacleLayers = ~0;
        [Min(0f)] [SerializeField] internal float obstacleContactRadius = 0.1f;

        [Min(0.0001f)] [SerializeField] internal float deltaTime = DefaultDeltaTime;
        [Min(0f)] [SerializeField] internal float viscosity = DefaultViscosity;
        [Min(0f)] [SerializeField] internal float pressure = DefaultPressure;
        [Min(0f)] [SerializeField] internal float flowForce = DefaultForce;
        [Range(0f, 1f)] [SerializeField] internal float velocityDecay = DefaultVelocityDecay;
        [Min(0f)] [SerializeField] internal float vorticity = DefaultVorticity;
        [Min(0f)] [SerializeField] internal float foamThreshold = DefaultFoamThreshold;
        [Min(0f)] [SerializeField] internal float foamStrength = DefaultFoamStrength;

        WaterRiverSurface _surface;

        internal event Action ConfigurationChanged;
        public WaterRiverFluidBakeData BakeData => bakeData;
        internal WaterRiverSpline Spline => _surface != null ? _surface.Spline : null;
        internal int SamplesPerSegment => _surface != null ? _surface.samplesPerSegment : 0;

        void OnEnable()
        {
            CacheSurface();
            _surface.RegisterRendererPropertySource(this);
            _surface.RequestRendererRefresh();
            ConfigurationChanged?.Invoke();
        }

        void OnDisable()
        {
            if (_surface == null) return;
            _surface.UnregisterRendererPropertySource(this);
            _surface.RequestRendererRefresh();
            ConfigurationChanged?.Invoke();
        }

        void OnValidate()
        {
            ClampSettings();
            CacheSurface();
            if (isActiveAndEnabled) _surface.RequestRendererRefresh();
            ConfigurationChanged?.Invoke();
        }

        void IWaterRiverRendererPropertySource.WriteRendererProperties(
            MaterialPropertyBlock properties)
        {
            bool active = bakeData != null && bakeData.IsValid;
            properties.SetFloat(WaterShaderProps.RiverFluidActive,
                                active ? EnabledFeature : DisabledFeature);
            if (!active) return;
            // The packed fluid map deliberately reuses the river renderer's foam-mask slot: RG is
            // velocity and B is foam, keeping the sampler count unchanged on WebGPU.
            properties.SetTexture(WaterShaderProps.FoamMask, bakeData.PackedTexture);
            properties.SetFloat(WaterShaderProps.RiverFluidInverseLength,
                                1f / bakeData.RiverLength);
            properties.SetFloat(WaterShaderProps.RiverFluidMaximumSpeed,
                                bakeData.MaximumSpeed);
        }

        internal WaterRiverFluidSolveSettings CreateSolveSettings()
            => new WaterRiverFluidSolveSettings(
                iterations, deltaTime, viscosity, pressure, flowForce,
                velocityDecay, vorticity, foamThreshold, foamStrength);

        internal void AssignBakeData(WaterRiverFluidBakeData data)
        {
            bakeData = data != null ? data : throw new ArgumentNullException(nameof(data));
            _surface?.RequestRendererRefresh();
            ConfigurationChanged?.Invoke();
        }

        void CacheSurface()
        {
            if (_surface == null) _surface = GetComponent<WaterRiverSurface>();
        }

        void ClampSettings()
        {
            lateralResolution = Mathf.Clamp(
                lateralResolution, WaterRiverFluidSolver.MinimumResolution, MaximumResolution);
            longitudinalResolution = Mathf.Clamp(
                longitudinalResolution, WaterRiverFluidSolver.MinimumResolution, MaximumResolution);
            iterations = Mathf.Clamp(
                iterations, WaterRiverFluidSolver.MinimumIterations, MaximumIterations);
            obstacleContactRadius = Mathf.Max(0f, obstacleContactRadius);
            deltaTime = Mathf.Max(0.0001f, deltaTime);
            viscosity = Mathf.Max(0f, viscosity);
            pressure = Mathf.Max(0f, pressure);
            flowForce = Mathf.Max(0f, flowForce);
            velocityDecay = Mathf.Clamp01(velocityDecay);
            vorticity = Mathf.Max(0f, vorticity);
            foamThreshold = Mathf.Max(0f, foamThreshold);
            foamStrength = Mathf.Max(0f, foamStrength);
        }
    }
}
