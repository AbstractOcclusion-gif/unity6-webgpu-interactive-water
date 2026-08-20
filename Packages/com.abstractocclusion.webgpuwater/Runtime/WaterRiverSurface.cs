// WebGpuWater - river ribbon mesh ownership and renderer wiring.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal interface IWaterRiverRendererPropertySource
    {
        void WriteRendererProperties(MaterialPropertyBlock properties);
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Surface")]
    public sealed class WaterRiverSurface : MonoBehaviour
    {
        internal const int DefaultSamplesPerSegment = 16;

        const string GeneratedMeshName = "Water River Ribbon (generated)";
        const string RiverModePropertyName = "_IsRiver";
        const float DisabledFeature = 0f;
        const float EnabledFeature = 1f;
        static readonly int RiverModePropertyId = Shader.PropertyToID(RiverModePropertyName);

        [Tooltip("Spline data used to build this visible ribbon.")]
        [SerializeField] internal WaterRiverSpline spline;
        [Tooltip("Optional water body whose existing animated uniforms drive this ribbon.")]
        [SerializeField] internal WaterVolume waterVolume;
        [Min(WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment)]
        [Tooltip("Cross-section intervals generated for each cubic spline segment.")]
        [SerializeField] internal int samplesPerSegment = DefaultSamplesPerSegment;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _generatedMesh;
        MaterialPropertyBlock _propertyBlock;
        WaterRiverSpline _subscribedSpline;
        readonly List<IWaterRiverRendererPropertySource> _rendererPropertySources = new();

        internal Mesh GeneratedMesh => _generatedMesh;
        internal WaterRiverSpline Spline => spline;
        internal WaterVolume WaterVolume => waterVolume;
        internal Renderer SurfaceRenderer => _meshRenderer;
        internal event Action ConfigurationChanged;

        void OnEnable()
        {
            CacheRendererComponents();
            ConfigureRenderer();
            RebindSplineEvents();
            RequestRebuild();
            PublishRendererProperties();
        }

        void OnDisable()
        {
            UnsubscribeSplineEvents();
            ClearRendererState();
            DestroyGeneratedMesh();
        }

        void OnValidate()
        {
            samplesPerSegment = Mathf.Max(
                WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment, samplesPerSegment);
            CacheRendererComponents();
            ConfigureRenderer();
            RebindSplineEvents();
            if (isActiveAndEnabled) RequestRebuild();
            ConfigurationChanged?.Invoke();
        }

        void OnTransformParentChanged() => RequestRebuild();

        void OnDidApplyAnimationProperties() => RequestRebuild();

        // An assigned WaterVolume owns animated shader state. Publishing is separate from mesh
        // rebuilds so the volume can animate without continuously rebuilding authored geometry.
        void LateUpdate() => PublishRendererProperties();

        /// <summary>Rebuild the owned mesh after programmatic spline or Transform changes.</summary>
        public void RequestRebuild()
        {
            if (!isActiveAndEnabled) return;
            CacheRendererComponents();
            RebindSplineEvents();
            if (spline == null)
            {
                ClearGeneratedGeometry();
                return;
            }

            try
            {
                EnsureGeneratedMesh();
                WaterRiverRibbonMeshGenerator.Populate(
                    _generatedMesh, spline, transform, samplesPerSegment);
                _meshFilter.sharedMesh = _generatedMesh;
                PublishRendererProperties();
            }
            catch (Exception exception)
            {
                ClearGeneratedGeometry();
                PublishRendererProperties();
                Debug.LogError($"WaterRiverSurface rebuild failed: {exception.Message}", this);
            }
        }

        internal void Configure(WaterRiverSpline riverSpline, WaterVolume body,
                                Material surfaceMaterial, int segmentSamples)
        {
            if (riverSpline == null) throw new ArgumentNullException(nameof(riverSpline));
            if (surfaceMaterial == null) throw new ArgumentNullException(nameof(surfaceMaterial));
            if (surfaceMaterial.shader == null ||
                surfaceMaterial.shader.name != WaterShaderNames.WaterSurface)
                throw new ArgumentException(
                    $"River surface material must use {WaterShaderNames.WaterSurface}.",
                    nameof(surfaceMaterial));
            if (segmentSamples < WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment)
                throw new ArgumentOutOfRangeException(nameof(segmentSamples));

            CacheRendererComponents();
            spline = riverSpline;
            waterVolume = body;
            samplesPerSegment = segmentSamples;
            _meshRenderer.sharedMaterial = surfaceMaterial;
            ConfigureRenderer();
            RebindSplineEvents();
            RequestRebuild();
            PublishRendererProperties();
            ConfigurationChanged?.Invoke();
        }

        internal void RegisterRendererPropertySource(IWaterRiverRendererPropertySource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (_rendererPropertySources.Contains(source)) return;
            _rendererPropertySources.Add(source);
            PublishRendererProperties();
        }

        internal void UnregisterRendererPropertySource(IWaterRiverRendererPropertySource source)
        {
            if (source == null || !_rendererPropertySources.Remove(source)) return;
            PublishRendererProperties();
        }

        internal void RequestRendererRefresh() => PublishRendererProperties();

        void CacheRendererComponents()
        {
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
        }

        void ConfigureRenderer()
        {
            if (_meshRenderer == null) return;
            _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            int waterLayer = LayerMask.NameToLayer(WaterVolume.WaterLayerName);
            if (waterLayer >= 0) gameObject.layer = waterLayer;
        }

        void RebindSplineEvents()
        {
            if (_subscribedSpline == spline) return;
            UnsubscribeSplineEvents();
            _subscribedSpline = spline;
            if (_subscribedSpline != null) _subscribedSpline.Changed += RequestRebuild;
        }

        void UnsubscribeSplineEvents()
        {
            if (_subscribedSpline != null) _subscribedSpline.Changed -= RequestRebuild;
            _subscribedSpline = null;
        }

        void EnsureGeneratedMesh()
        {
            if (_generatedMesh != null) return;
            _generatedMesh = new Mesh
            {
                name = GeneratedMeshName,
                hideFlags = HideFlags.DontSave,
            };
            _generatedMesh.MarkDynamic();
        }

        void PublishRendererProperties()
        {
            if (_meshRenderer == null) return;
            _propertyBlock ??= new MaterialPropertyBlock();
            if (waterVolume != null && waterVolume.isActiveAndEnabled)
                waterVolume.WriteBodyProps(_propertyBlock);
            else
                _propertyBlock.Clear();
            ApplyRiverShaderOverrides();
            for (int i = 0; i < _rendererPropertySources.Count; i++)
                _rendererPropertySources[i].WriteRendererProperties(_propertyBlock);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
            bool hasGeometry = _generatedMesh != null && _generatedMesh.vertexCount > 0 &&
                               _meshFilter != null && _meshFilter.sharedMesh == _generatedMesh;
            _meshRenderer.forceRenderingOff = !hasGeometry;
        }

        void ApplyRiverShaderOverrides()
        {
            // The ribbon shares the established water look, including wind waves, detail normals,
            // fog and refraction. Only features whose coordinates require a rectangular pool or
            // baked shore field are inert until the dedicated river baking steps own that data.
            _propertyBlock.SetFloat(RiverModePropertyId, EnabledFeature);
            // Large bodies create a Play-only dense patch and ask their flat base sheet to discard
            // underneath it. A ribbon is independent geometry, so inheriting that ownership flag
            // makes the entire river vanish as soon as the patch is created on entering Play Mode.
            _propertyBlock.SetFloat(WaterShaderProps.PatchCoverActive, DisabledFeature);
            _propertyBlock.SetFloat(WaterShaderProps.SurfActive, DisabledFeature);
            _propertyBlock.SetFloat(WaterShaderProps.UseBedDepth, DisabledFeature);
            _propertyBlock.SetFloat(WaterShaderProps.RiverFoamActive, DisabledFeature);
            _propertyBlock.SetFloat(WaterShaderProps.RiverFluidActive, DisabledFeature);
        }

        void ClearGeneratedGeometry()
        {
            if (_generatedMesh != null) _generatedMesh.Clear();
            if (_meshFilter != null && _meshFilter.sharedMesh == _generatedMesh)
                _meshFilter.sharedMesh = null;
        }

        void ClearRendererState()
        {
            if (_meshRenderer == null) return;
            _meshRenderer.SetPropertyBlock(null);
            _meshRenderer.forceRenderingOff = false;
        }

        void DestroyGeneratedMesh()
        {
            if (_meshFilter != null && _meshFilter.sharedMesh == _generatedMesh)
                _meshFilter.sharedMesh = null;
            WaterObjects.DestroyRuntime(_generatedMesh);
            _generatedMesh = null;
        }
    }
}
