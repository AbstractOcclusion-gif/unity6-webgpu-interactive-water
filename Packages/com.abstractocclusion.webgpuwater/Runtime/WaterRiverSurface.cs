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
        // Gameplay water column below the ribbon surface. Rivers have no volumetric extent of
        // their own (the ribbon is a surface), so the domain resolver needs an authored depth to
        // answer full-XYZ containment; 2 m reads as a wadeable default.
        internal const float DefaultGameplayDepthMeters = 2f;
        const float MinGameplayDepthMeters = 0.1f;
        const int FogVolumeLayerCount = 2;
        const int TriangleIndexCount = 3;
        const int FogBoundaryIndexCapacityPerAxis = 12;
        const float BoundsSizeFromExtents = 2f;

        const string GeneratedMeshName = "Water River Ribbon (generated)";
        const string GeneratedFogVolumeMeshName = "Water River Fog Volume (generated)";
        const string UnderSurfaceChildName = "River Surface Under (generated)";
        const string RiverModePropertyName = "_IsRiver";
        const float DisabledFeature = 0f;
        const float EnabledFeature = 1f;
        static readonly int RiverModePropertyId = Shader.PropertyToID(RiverModePropertyName);
        // Per-end pool frame of the receiving body. A connected terminal band uses that body's
        // own rendered wave field when it is not the ribbon's parent volume.
        const string SourceEndWaveFramePropertyName = "_RiverEndWaveFrame0";
        const string SourceEndWaveAnchorPropertyName = "_RiverEndWaveAnchor0";
        const string MouthEndWaveFramePropertyName = "_RiverEndWaveFrame1";
        const string MouthEndWaveAnchorPropertyName = "_RiverEndWaveAnchor1";
        const float EndAnchorPoolFrameMode = 1f;
        const float MinAnchorMetersPerUnit = 1e-4f;
        static readonly int SourceEndWaveFrameId =
            Shader.PropertyToID(SourceEndWaveFramePropertyName);
        static readonly int SourceEndWaveAnchorId =
            Shader.PropertyToID(SourceEndWaveAnchorPropertyName);
        static readonly int MouthEndWaveFrameId =
            Shader.PropertyToID(MouthEndWaveFramePropertyName);
        static readonly int MouthEndWaveAnchorId =
            Shader.PropertyToID(MouthEndWaveAnchorPropertyName);
        static readonly int MouthOutflowCountId = Shader.PropertyToID("_MouthOutflowCount");
        static readonly int MouthOutflowOriginsId = Shader.PropertyToID("_MouthOutflowOrigins");
        static readonly int MouthOutflowDirectionsId = Shader.PropertyToID("_MouthOutflowDirections");
        static readonly int MouthOutflowParametersId = Shader.PropertyToID("_MouthOutflowParameters");
        static readonly int MouthOutflowFoamFramesId =
            Shader.PropertyToID("_MouthOutflowFoamFrames");
        static readonly int MouthOutflowFoamAppearanceId =
            WaterShaderProps.MouthOutflowFoamAppearance;
        static readonly List<WaterRiverSurface> LiveSurfaces = new();
        static readonly Plane[] FogFrustumPlanes = new Plane[6];

        [Tooltip("Spline data used to build this visible ribbon.")]
        [SerializeField] internal WaterRiverSpline spline;
        [Tooltip("Optional water body whose existing animated uniforms drive this ribbon.")]
        [SerializeField] internal WaterVolume waterVolume;
        [Min(WaterRiverRibbonMeshGenerator.MinimumSamplesPerSegment)]
        [Tooltip("Cross-section intervals generated for each cubic spline segment.")]
        [SerializeField] internal int samplesPerSegment = DefaultSamplesPerSegment;
        [Min(MinGameplayDepthMeters)]
        [Tooltip("Depth of the gameplay water column below the ribbon surface, metres. Domain " +
                 "queries (buoyancy, casting, fish) treat the river as water down to this depth.")]
        [SerializeField] internal float gameplayDepthMeters = DefaultGameplayDepthMeters;
        [Tooltip("Underside material (the body's cull-front, _Underwater=1 twin). When set, a " +
                 "runtime child renders the same ribbon mesh from below, so a submerged camera " +
                 "looking up sees the river surface. Empty = no underside (legacy).")]
        [SerializeField] internal Material underSurfaceMaterial;

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Mesh _generatedMesh;
        Mesh _generatedFogVolumeMesh;
        MaterialPropertyBlock _propertyBlock;
        readonly Vector4[] _mouthOutflowOrigins =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _mouthOutflowDirections =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _mouthOutflowParameters =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _mouthOutflowFoamFrames =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _mouthOutflowFoamAppearances =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        float _mouthLongitudinalMeters;
        Vector3 _mouthRight;
        WaterRiverSpline _subscribedSpline;
        readonly List<IWaterRiverRendererPropertySource> _rendererPropertySources = new();

        WaterRiverSurfaceProvider _provider;
        WaterRiverCurrentField _currentField;
        WaterRiver _riverFacade;
        // The underside twin is RUNTIME-CREATED and DontSave: the generated mesh itself never
        // serializes, so a scene-persisted child would come back holding a missing mesh. Play
        // mode is where a camera dives; edit mode keeps only the above sheet.
        GameObject _underGO;
        MeshFilter _underFilter;
        MeshRenderer _underRenderer;
        // Body-border seam descriptors, pushed by the facade from its generated connections.
        // Runtime-only: the facade re-applies them each enable/validate.
        WaterRiverRibbonMeshGenerator.BodySeamEnd _sourceBodySeam;
        WaterRiverRibbonMeshGenerator.BodySeamEnd _mouthBodySeam;
        WaterRiverSurface _sourceBoundaryTarget;
        bool _sourceBoundaryUsesTargetMouth;

        internal Mesh GeneratedMesh => _generatedMesh;
        internal Mesh GeneratedFogVolumeMesh => _generatedFogVolumeMesh;
        internal WaterRiverSpline Spline => spline;
        internal WaterVolume WaterVolume => waterVolume;
        internal Renderer SurfaceRenderer => _meshRenderer;
        internal float GameplayDepthMeters => gameplayDepthMeters;
        internal float MouthLongitudinalMeters => _mouthLongitudinalMeters;
        internal Vector3 MouthRight => _mouthRight;
        /// <summary>This ribbon's domain-query face (registered while the surface is enabled).</summary>
        internal WaterRiverSurfaceProvider SurfaceProvider => _provider;
        /// <summary>Authored current field the provider prefers over the uniform spline speed;
        /// resolved from this object, then the spline's (where the field usually lives).</summary>
        internal WaterRiverCurrentField CurrentField => _currentField;
        internal event Action ConfigurationChanged;
        internal event Action GeometryChanged;

        internal static void ResetStaticState() => LiveSurfaces.Clear();

        void OnEnable()
        {
            if (!LiveSurfaces.Contains(this)) LiveSurfaces.Add(this);
            CacheRendererComponents();
            ConfigureRenderer();
            EnsureUnderRenderer();
            RebindSplineEvents();
            RebindSourceBoundaryEvents();
            RequestRebuild();
            PublishRendererProperties();
            ResolveCurrentField();
            _provider ??= new WaterRiverSurfaceProvider(this);
            WaterSurfaceProviders.Register(_provider);
        }

        void OnDisable()
        {
            LiveSurfaces.Remove(this);
            if (_sourceBoundaryTarget != null) _sourceBoundaryTarget.RebuildFogVolumeMesh();
            if (_provider != null) WaterSurfaceProviders.Unregister(_provider);
            UnsubscribeSplineEvents();
            UnsubscribeSourceBoundaryEvents();
            ClearRendererState();
            DestroyUnderRenderer();
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
                WaterRiverRibbonMeshGenerator.RibbonMetrics metrics =
                    WaterRiverRibbonMeshGenerator.Populate(
                        _generatedMesh, spline, transform, samplesPerSegment,
                        _sourceBodySeam, _mouthBodySeam, BuildSourceBoundary());
                _mouthLongitudinalMeters = metrics.MouthLongitudinalMeters;
                _mouthRight = metrics.MouthRight;
                RebuildFogVolumeMesh();
                _meshFilter.sharedMesh = _generatedMesh;
                if (_underFilter != null) _underFilter.sharedMesh = _generatedMesh;
                PublishRendererProperties();
                GeometryChanged?.Invoke();
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
            => Configure(riverSpline, body, surfaceMaterial, null, segmentSamples);

        internal void Configure(WaterRiverSpline riverSpline, WaterVolume body,
                                Material surfaceMaterial, Material underMaterial,
                                int segmentSamples)
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
            underSurfaceMaterial = underMaterial;
            if (_underRenderer != null && underMaterial != null)
                _underRenderer.sharedMaterial = underMaterial;
            ConfigureRenderer();
            RebindSplineEvents();
            RequestRebuild();
            PublishRendererProperties();
            ConfigurationChanged?.Invoke();
        }

        /// <summary>Facade push of the receiving-body border frames. Rebuilds only when the
        /// descriptors change, so OnValidate-driven refreshes stay free.</summary>
        internal void ConfigureBodySeams(
            WaterRiverRibbonMeshGenerator.BodySeamEnd sourceBodySeam,
            WaterRiverRibbonMeshGenerator.BodySeamEnd mouthBodySeam)
        {
            if (_sourceBodySeam.SameAs(in sourceBodySeam) &&
                _mouthBodySeam.SameAs(in mouthBodySeam)) return;
            _sourceBodySeam = sourceBodySeam;
            _mouthBodySeam = mouthBodySeam;
            RequestRebuild();
        }

        /// <summary>Copy a connected upstream river's terminal row into this source row. The
        /// target mesh remains the authority; its rebuild event immediately refreshes this mesh.</summary>
        internal void ConfigureSourceBoundary(WaterRiverSurface target, bool useTargetMouth)
        {
            if (target == this)
                throw new InvalidOperationException("A river cannot stitch its source to itself.");
            if (_sourceBoundaryTarget == target &&
                _sourceBoundaryUsesTargetMouth == useTargetMouth) return;

            WaterRiverSurface previousTarget = _sourceBoundaryTarget;
            UnsubscribeSourceBoundaryEvents();
            _sourceBoundaryTarget = target;
            _sourceBoundaryUsesTargetMouth = useTargetMouth;
            RebindSourceBoundaryEvents();
            RequestRebuild();
            if (previousTarget != null) previousTarget.RebuildFogVolumeMesh();
            if (target != null) target.RebuildFogVolumeMesh();
        }

        WaterRiverRibbonMeshGenerator.SharedBoundaryRow BuildSourceBoundary()
        {
            if (_sourceBoundaryTarget == null) return default;
            Mesh targetMesh = _sourceBoundaryTarget.GeneratedMesh;
            if (targetMesh == null || targetMesh.vertexCount <
                WaterRiverRibbonMeshGenerator.VerticesPerCrossSection) return default;

            int verticesPerRow = WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
            int rowStart = _sourceBoundaryUsesTargetMouth
                ? targetMesh.vertexCount - verticesPerRow : 0;
            Vector3[] targetPositions = targetMesh.vertices;
            Vector3[] targetNormals = targetMesh.normals;
            Vector4[] targetTangents = targetMesh.tangents;
            Vector2[] targetUv = targetMesh.uv;
            var targetCurrentData = new List<Vector4>();
            targetMesh.GetUVs(1, targetCurrentData);
            if (targetPositions.Length != targetMesh.vertexCount ||
                targetNormals.Length != targetMesh.vertexCount ||
                targetTangents.Length != targetMesh.vertexCount ||
                targetUv.Length != targetMesh.vertexCount ||
                targetCurrentData.Count != targetMesh.vertexCount)
                throw new InvalidOperationException(
                    "The upstream river mesh does not satisfy the shared boundary contract.");

            var boundary = new WaterRiverRibbonMeshGenerator.SharedBoundaryRow
            {
                WorldPositions = new Vector3[verticesPerRow],
                WorldNormals = new Vector3[verticesPerRow],
                WorldTangents = new Vector4[verticesPerRow],
                Uv = new Vector2[verticesPerRow],
                CurrentData = new Vector4[verticesPerRow],
            };
            Transform targetTransform = _sourceBoundaryTarget.transform;
            Matrix4x4 normalLocalToWorld = targetTransform.worldToLocalMatrix.transpose;
            for (int column = 0; column < verticesPerRow; column++)
            {
                int targetIndex = rowStart + column;
                boundary.WorldPositions[column] =
                    targetTransform.TransformPoint(targetPositions[targetIndex]);
                boundary.WorldNormals[column] =
                    normalLocalToWorld.MultiplyVector(targetNormals[targetIndex]).normalized;
                Vector4 targetTangent = targetTangents[targetIndex];
                Vector3 worldTangent = targetTransform.localToWorldMatrix.MultiplyVector(
                    new Vector3(targetTangent.x, targetTangent.y, targetTangent.z)).normalized;
                boundary.WorldTangents[column] = new Vector4(
                    worldTangent.x, worldTangent.y, worldTangent.z, targetTangent.w);
                boundary.Uv[column] = targetUv[targetIndex];
                boundary.CurrentData[column] = targetCurrentData[targetIndex];
            }
            return boundary;
        }

        void RebindSourceBoundaryEvents()
        {
            if (!isActiveAndEnabled || _sourceBoundaryTarget == null) return;
            _sourceBoundaryTarget.GeometryChanged -= HandleSourceBoundaryChanged;
            _sourceBoundaryTarget.GeometryChanged += HandleSourceBoundaryChanged;
        }

        void UnsubscribeSourceBoundaryEvents()
        {
            if (_sourceBoundaryTarget != null)
                _sourceBoundaryTarget.GeometryChanged -= HandleSourceBoundaryChanged;
        }

        void HandleSourceBoundaryChanged() => RequestRebuild();

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

        // The current field usually sits beside the spline (WaterRiverCurrentField.Reset), but a
        // surface-local one wins so a ribbon can carry its own. Re-run by OnValidate through
        // RequestRebuild's callers when the spline reference changes.
        void ResolveCurrentField()
        {
            _currentField = GetComponent<WaterRiverCurrentField>();
            if (_currentField == null && spline != null)
                _currentField = spline.GetComponent<WaterRiverCurrentField>();
        }

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

        void RebuildFogVolumeMesh()
        {
            if (_generatedMesh == null || _generatedMesh.vertexCount == 0)
            {
                if (_generatedFogVolumeMesh != null) _generatedFogVolumeMesh.Clear();
                return;
            }

            Vector3[] topVertices = _generatedMesh.vertices;
            int topVertexCount = topVertices.Length;
            int verticesPerRow = WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
            if (topVertexCount % verticesPerRow != 0)
                throw new InvalidOperationException(
                    "The river ribbon does not contain complete cross-section rows.");

            int rowCount = topVertexCount / verticesPerRow;
            if (rowCount < 2)
                throw new InvalidOperationException(
                    "The river fog volume requires at least two cross-section rows.");

            EnsureFogVolumeMesh();
            var vertices = new Vector3[topVertexCount * FogVolumeLayerCount];
            Array.Copy(topVertices, vertices, topVertexCount);
            for (int vertexIndex = 0; vertexIndex < topVertexCount; vertexIndex++)
            {
                Vector3 topWorld = transform.TransformPoint(topVertices[vertexIndex]);
                vertices[topVertexCount + vertexIndex] = transform.InverseTransformPoint(
                    topWorld + Vector3.down * gameplayDepthMeters);
            }

            int[] topIndices = _generatedMesh.triangles;
            var indices = new List<int>(topIndices.Length * FogVolumeLayerCount +
                (rowCount + verticesPerRow) * FogBoundaryIndexCapacityPerAxis);
            indices.AddRange(topIndices);
            for (int triangle = 0; triangle < topIndices.Length;
                 triangle += TriangleIndexCount)
            {
                indices.Add(topVertexCount + topIndices[triangle]);
                indices.Add(topVertexCount + topIndices[triangle + 2]);
                indices.Add(topVertexCount + topIndices[triangle + 1]);
            }

            if (_sourceBoundaryTarget == null && !HasBoundaryConnection(useMouth: false))
                AddFogVolumeEdge(indices, topVertexCount, 0, 1, verticesPerRow);
            AddFogVolumeEdge(indices, topVertexCount,
                             verticesPerRow - 1, verticesPerRow, rowCount);
            if (!HasBoundaryConnection(useMouth: true))
                AddFogVolumeEdge(indices, topVertexCount,
                                 topVertexCount - 1, -1, verticesPerRow);
            AddFogVolumeEdge(indices, topVertexCount,
                             topVertexCount - verticesPerRow, -verticesPerRow, rowCount);

            _generatedFogVolumeMesh.Clear();
            _generatedFogVolumeMesh.indexFormat = vertices.Length > ushort.MaxValue
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            _generatedFogVolumeMesh.vertices = vertices;
            _generatedFogVolumeMesh.SetTriangles(indices, 0, calculateBounds: true);
        }

        void EnsureFogVolumeMesh()
        {
            if (_generatedFogVolumeMesh != null) return;
            _generatedFogVolumeMesh = new Mesh
            {
                name = GeneratedFogVolumeMeshName,
                hideFlags = HideFlags.DontSave,
            };
            _generatedFogVolumeMesh.MarkDynamic();
        }

        static void AddFogVolumeEdge(List<int> indices, int bottomOffset,
                                     int firstTopIndex, int topIndexStep, int vertexCount)
        {
            for (int edgeVertex = 0; edgeVertex < vertexCount - 1; edgeVertex++)
            {
                int topA = firstTopIndex + edgeVertex * topIndexStep;
                int topB = topA + topIndexStep;
                int bottomA = bottomOffset + topA;
                int bottomB = bottomOffset + topB;
                indices.Add(topA);
                indices.Add(topB);
                indices.Add(bottomA);
                indices.Add(topB);
                indices.Add(bottomB);
                indices.Add(bottomA);
            }
        }

        bool HasBoundaryConnection(bool useMouth)
        {
            for (int surfaceIndex = 0; surfaceIndex < LiveSurfaces.Count; surfaceIndex++)
            {
                WaterRiverSurface candidate = LiveSurfaces[surfaceIndex];
                if (candidate == null || candidate == this) continue;
                if (candidate._sourceBoundaryTarget == this &&
                    candidate._sourceBoundaryUsesTargetMouth == useMouth)
                    return true;
            }
            return false;
        }

        internal static bool TryFindExternalFogSource(Camera camera, out WaterVolume fogSource)
        {
            fogSource = null;
            if (camera == null || WaterVolume.CameraSubmerged) return false;

            GeometryUtility.CalculateFrustumPlanes(camera, FogFrustumPlanes);
            WaterVolume preferredSource =
                WaterVolume.UnderwaterFogActive || WaterVolume.WaterlineActive
                    ? WaterVolume.FogSource
                    : null;
            for (int surfaceIndex = 0; surfaceIndex < LiveSurfaces.Count; surfaceIndex++)
            {
                WaterRiverSurface surface = LiveSurfaces[surfaceIndex];
                if (!surface.QualifiesForExternalFog(preferredSource)) continue;
                if (!GeometryUtility.TestPlanesAABB(
                        FogFrustumPlanes, surface.FogVolumeWorldBounds())) continue;
                fogSource = surface.waterVolume;
                return true;
            }
            return false;
        }

        internal static void CollectExternalFogSurfaces(Camera camera, WaterVolume fogSource,
                                                        List<WaterRiverSurface> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            results.Clear();
            if (camera == null || fogSource == null || WaterVolume.CameraSubmerged) return;

            GeometryUtility.CalculateFrustumPlanes(camera, FogFrustumPlanes);
            for (int surfaceIndex = 0; surfaceIndex < LiveSurfaces.Count; surfaceIndex++)
            {
                WaterRiverSurface surface = LiveSurfaces[surfaceIndex];
                if (!surface.QualifiesForExternalFog(fogSource)) continue;
                if (GeometryUtility.TestPlanesAABB(
                        FogFrustumPlanes, surface.FogVolumeWorldBounds()))
                    results.Add(surface);
            }
        }

        /// <summary>Append every live river top sheet. These renderers join body surfaces in the
        /// river-fog ownership prepass, so a ribbon cannot fog through its own top or through a
        /// different connected river drawn in front of it.</summary>
        internal static void AppendActiveSurfaceRenderers(List<Renderer> results)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            for (int surfaceIndex = 0; surfaceIndex < LiveSurfaces.Count; surfaceIndex++)
            {
                WaterRiverSurface surface = LiveSurfaces[surfaceIndex];
                if (surface == null || !surface.HasRenderableSurface()) continue;
                results.Add(surface._meshRenderer);
            }
        }

        bool QualifiesForExternalFog(WaterVolume requiredSource)
        {
            return isActiveAndEnabled && waterVolume != null && waterVolume.isActiveAndEnabled &&
                   waterVolume.CanDriveExternalRiverFog &&
                   (requiredSource == null || waterVolume == requiredSource) &&
                   _generatedFogVolumeMesh != null &&
                   _generatedFogVolumeMesh.vertexCount > 0 &&
                   HasRenderableSurface();
        }

        bool HasRenderableSurface()
        {
            return isActiveAndEnabled && gameObject.activeInHierarchy &&
                   _generatedMesh != null && _generatedMesh.vertexCount > 0 &&
                   _meshFilter != null && _meshFilter.sharedMesh == _generatedMesh &&
                   _meshRenderer != null && _meshRenderer.enabled &&
                   !_meshRenderer.forceRenderingOff && _meshRenderer.sharedMaterial != null;
        }

        Bounds FogVolumeWorldBounds()
        {
            Bounds localBounds = _generatedFogVolumeMesh.bounds;
            Vector3 center = transform.TransformPoint(localBounds.center);
            Vector3 localExtents = localBounds.extents;
            Matrix4x4 matrix = transform.localToWorldMatrix;
            Vector3 worldExtents = new(
                Mathf.Abs(matrix.m00) * localExtents.x +
                Mathf.Abs(matrix.m01) * localExtents.y +
                Mathf.Abs(matrix.m02) * localExtents.z,
                Mathf.Abs(matrix.m10) * localExtents.x +
                Mathf.Abs(matrix.m11) * localExtents.y +
                Mathf.Abs(matrix.m12) * localExtents.z,
                Mathf.Abs(matrix.m20) * localExtents.x +
                Mathf.Abs(matrix.m21) * localExtents.y +
                Mathf.Abs(matrix.m22) * localExtents.z);
            return new Bounds(center, worldExtents * BoundsSizeFromExtents);
        }

        void PublishRendererProperties()
        {
            if (_meshRenderer == null) return;
            _propertyBlock ??= new MaterialPropertyBlock();
            if (waterVolume != null && waterVolume.isActiveAndEnabled)
                waterVolume.WriteBodyProps(_propertyBlock);
            else
                _propertyBlock.Clear();
            PublishMouthOutflowProperties();
            ApplyRiverShaderOverrides();
            PublishEndWaveAnchors();
            for (int i = 0; i < _rendererPropertySources.Count; i++)
                _rendererPropertySources[i].WriteRendererProperties(_propertyBlock);
            _meshRenderer.SetPropertyBlock(_propertyBlock);
            bool hasGeometry = _generatedMesh != null && _generatedMesh.vertexCount > 0 &&
                               _meshFilter != null && _meshFilter.sharedMesh == _generatedMesh;
            _meshRenderer.forceRenderingOff = !hasGeometry;
            if (_underRenderer != null)
            {
                // Same block as the above sheet - the two differ only by their MATERIAL's
                // _Underwater flag and cull mode, exactly like the body's coincident sheet pair.
                _underRenderer.SetPropertyBlock(_propertyBlock);
                _underRenderer.forceRenderingOff = !hasGeometry;
            }
        }

        void PublishMouthOutflowProperties()
        {
            Array.Clear(_mouthOutflowOrigins, 0, _mouthOutflowOrigins.Length);
            Array.Clear(_mouthOutflowDirections, 0, _mouthOutflowDirections.Length);
            Array.Clear(_mouthOutflowParameters, 0, _mouthOutflowParameters.Length);
            Array.Clear(_mouthOutflowFoamFrames, 0, _mouthOutflowFoamFrames.Length);
            Array.Clear(
                _mouthOutflowFoamAppearances, 0, _mouthOutflowFoamAppearances.Length);
            if (_riverFacade == null) _riverFacade = GetComponent<WaterRiver>();
            WaterRiverMouthOutflow outflow = default;
            bool active = _riverFacade != null &&
                          _riverFacade.TryBuildMouthOutflow(out outflow);
            if (active)
                outflow.WriteShaderData(
                    _mouthOutflowOrigins, _mouthOutflowDirections, _mouthOutflowParameters,
                    _mouthOutflowFoamFrames, _mouthOutflowFoamAppearances, 0);
            _propertyBlock.SetFloat(MouthOutflowCountId, active ? EnabledFeature : DisabledFeature);
            _propertyBlock.SetVectorArray(MouthOutflowOriginsId, _mouthOutflowOrigins);
            _propertyBlock.SetVectorArray(MouthOutflowDirectionsId, _mouthOutflowDirections);
            _propertyBlock.SetVectorArray(MouthOutflowParametersId, _mouthOutflowParameters);
            _propertyBlock.SetVectorArray(MouthOutflowFoamFramesId, _mouthOutflowFoamFrames);
            _propertyBlock.SetVectorArray(
                MouthOutflowFoamAppearanceId, _mouthOutflowFoamAppearances);
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
            _propertyBlock.SetFloat(
                WaterShaderProps.RiverFoamOverallStrength, EnabledFeature);
            _propertyBlock.SetFloat(
                WaterShaderProps.RiverCascadeTransportActive, DisabledFeature);
            _propertyBlock.SetFloat(WaterShaderProps.RiverFluidActive, DisabledFeature);
        }

        // WriteBodyProps above published the parent's wind-wave anchor, which is the wrong
        // cross-fade target for a terminal whose receiving body is a different
        // volume (the source-end reservoir of a lake-parented river) - both sides of that seam
        // would sample the same metric bank at two anchors, and the phase jump prints as a
        // moving texture line at the boundary (the "known asymmetry" of the 2026-08-29 study;
        // RAM's river blends toward the sea's own global cascades for the same reason). A zero
        // vector is mode 0: the shader keeps the parent-anchored path.
        void PublishEndWaveAnchors()
        {
            if (_riverFacade == null) _riverFacade = GetComponent<WaterRiver>();
            WriteEndWaveAnchor(_riverFacade != null ? _riverFacade.SourceEnd : null,
                               SourceEndWaveFrameId, SourceEndWaveAnchorId);
            WriteEndWaveAnchor(_riverFacade != null ? _riverFacade.MouthEnd : null,
                               MouthEndWaveFrameId, MouthEndWaveAnchorId);
        }

        void WriteEndWaveAnchor(WaterRiverEndConnection end, int frameId, int anchorId)
        {
            WaterVolume endBody = end != null ? end.body : null;
            // An ocean-clipmap end already anchors its waves in world space, and this block
            // carries no second ocean's current-drift offset - the parent path stays the honest
            // fallback there.
            bool anchorable = waterVolume != null && endBody != null &&
                              endBody.isActiveAndEnabled && endBody != waterVolume &&
                              !endBody.IsOceanClipmap;
            if (!anchorable)
            {
                _propertyBlock.SetVector(frameId, Vector4.zero);
                _propertyBlock.SetVector(anchorId, Vector4.zero);
                return;
            }
            Vector3 extent = endBody.VolumeExtentSafe;
            Vector3 axisX = endBody.VolumeRotation * Vector3.right;
            Vector3 axisZ = endBody.VolumeRotation * Vector3.forward;
            Vector3 center = endBody.VolumeCenter;
            float metersPerUnitRatio = endBody.WaveMetersPerUnit /
                Mathf.Max(waterVolume.WaveMetersPerUnit, MinAnchorMetersPerUnit);
            _propertyBlock.SetVector(frameId, new Vector4(
                axisX.x / extent.x, axisX.z / extent.x,
                axisZ.x / extent.z, axisZ.z / extent.z));
            _propertyBlock.SetVector(anchorId, new Vector4(
                center.x, center.z, metersPerUnitRatio, EndAnchorPoolFrameMode));
        }

        void EnsureUnderRenderer()
        {
            if (underSurfaceMaterial == null || _underGO != null) return;
            _underGO = new GameObject(UnderSurfaceChildName) { hideFlags = HideFlags.DontSave };
            _underGO.transform.SetParent(transform, false);
            _underGO.layer = gameObject.layer;
            _underFilter = _underGO.AddComponent<MeshFilter>();
            _underRenderer = _underGO.AddComponent<MeshRenderer>();
            _underRenderer.sharedMaterial = underSurfaceMaterial;
            _underRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _underRenderer.receiveShadows = false;
            _underFilter.sharedMesh = _generatedMesh;
        }

        void DestroyUnderRenderer()
        {
            if (_underGO == null) return;
            WaterObjects.DestroyRuntime(_underGO);
            _underGO = null;
            _underFilter = null;
            _underRenderer = null;
        }

        void ClearGeneratedGeometry()
        {
            _mouthLongitudinalMeters = 0f;
            _mouthRight = Vector3.zero;
            if (_generatedMesh != null) _generatedMesh.Clear();
            if (_generatedFogVolumeMesh != null) _generatedFogVolumeMesh.Clear();
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
            WaterObjects.DestroyRuntime(_generatedFogVolumeMesh);
            _generatedFogVolumeMesh = null;
        }
    }
}
