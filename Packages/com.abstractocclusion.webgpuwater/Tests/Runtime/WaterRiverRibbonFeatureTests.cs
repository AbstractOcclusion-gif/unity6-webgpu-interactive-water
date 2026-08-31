using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverRibbonFeatureTests
    {
        const int SamplesPerSegment = 8;
        const int TwoSegmentCount = 2;
        const float FloatTolerance = 0.001f;
        const float StartWidth = 4f;
        const float MiddleWidth = 7f;
        const float EndWidth = 2f;
        const float Speed = 3f;
        const float StartCurrentSpeed = 1f;
        const float EndCurrentSpeed = 5f;
        const float MinimumTriangleDoubleArea = 1e-5f;
        const float FogDepthMeters = 3.5f;
        const int FogVolumeLayerCount = 2;
        const int TriangleIndexCount = 3;
        const int ClosedEdgeUseCount = 2;
        const float TetrahedronVolumeDivisor = 6f;
        const int WaterFogOccluderDepthPassIndex = 3;
        const string WaterFogOccluderDepthPassName = "WaterFogOccluderDepth";
        const string SplineHostName = "River Ribbon Spline Test";
        const string MeshHostName = "River Ribbon Mesh Test";
        const string GeneratedMeshName = "River Ribbon Test Mesh";
        const string VolumeHostName = "River Uniform Provider Test";
        const string BuiltInSurfaceName = "Built-in Water Surface Test";

        static readonly Vector3 StraightEnd = new Vector3(0f, 0f, 16f);
        static readonly Vector3 StraightTangent =
            StraightEnd * WaterRiverSpline.BezierHandleLengthFraction;
        static readonly Vector3 BendMiddle = new Vector3(0f, -1f, 10f);
        static readonly Vector3 BendEnd = new Vector3(10f, -3f, 18f);
        static readonly Vector3 BendStartTangent = new Vector3(0f, -0.5f, 4f);
        static readonly Vector3 BendMiddleTangent = new Vector3(3f, -1f, 3f);
        static readonly Vector3 BendEndTangent = new Vector3(4f, -1f, 0f);
        static readonly Vector3 WaterfallEnd = new Vector3(0.5f, -14f, 2f);
        static readonly Vector3 WaterfallTangent =
            WaterfallEnd * WaterRiverSpline.BezierHandleLengthFraction;
        static readonly Vector3 MeshHostPosition = new Vector3(5f, -2f, 3f);
        static readonly Vector3 MeshHostScale = new Vector3(2.5f, 0.4f, 1.75f);

        [Test]
        public void Populate_HasDeterministicVertexAndIndexCounts()
        {
            using var context = CreateContext(TwoSegmentKnots(), SamplesPerSegment);

            int expectedCrossSections = TwoSegmentCount * SamplesPerSegment + 1;
            Assert.That(context.Mesh.vertexCount,
                Is.EqualTo(expectedCrossSections *
                           WaterRiverRibbonMeshGenerator.VerticesPerCrossSection));
            Assert.That(context.Mesh.triangles.Length,
                Is.EqualTo((expectedCrossSections - 1) *
                           WaterRiverRibbonMeshGenerator.RibbonQuadsPerCrossSectionPair *
                           WaterRiverRibbonMeshGenerator.IndicesPerRibbonQuad));
        }

        [Test]
        public void Populate_StraightBanksMatchSplineSamplesAndWidth()
        {
            using var context = CreateContext(StraightKnots(), SamplesPerSegment);
            Vector3[] vertices = context.Mesh.vertices;

            for (int crossSection = 0; crossSection <= SamplesPerSegment; crossSection++)
            {
                float normalizedT = crossSection / (float)SamplesPerSegment;
                Assert.That(context.Spline.TryEvaluate(
                    normalizedT, out WaterRiverSplineSample sample), Is.True);
                int leftIndex = crossSection *
                                WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
                Vector3 left = context.MeshHost.transform.TransformPoint(vertices[leftIndex]);
                int rightIndex = leftIndex +
                                 WaterRiverRibbonMeshGenerator.VerticesPerCrossSection - 1;
                Vector3 right = context.MeshHost.transform.TransformPoint(vertices[rightIndex]);

                AssertVector3((left + right) * 0.5f, sample.Position);
                AssertVector3(left, sample.Position - sample.Right * (sample.Width * 0.5f));
                AssertVector3(right, sample.Position + sample.Right * (sample.Width * 0.5f));
                Assert.That(Vector3.Distance(left, right),
                    Is.EqualTo(sample.Width).Within(FloatTolerance));
            }
        }

        [Test]
        public void Populate_LongitudinalUvIsMonotonicAndLateralUvMarksBanks()
        {
            using var context = CreateContext(TwoSegmentKnots(), SamplesPerSegment);
            Vector2[] uv = context.Mesh.uv;
            float previousLongitudinal = float.NegativeInfinity;

            for (int crossSection = 0;
                 crossSection < TwoSegmentCount * SamplesPerSegment + 1;
                 crossSection++)
            {
                int leftIndex = crossSection *
                                WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
                int rightIndex = leftIndex +
                                 WaterRiverRibbonMeshGenerator.VerticesPerCrossSection - 1;
                Assert.That(uv[leftIndex].x, Is.EqualTo(0f).Within(FloatTolerance));
                Assert.That(uv[rightIndex].x, Is.EqualTo(1f).Within(FloatTolerance));
                Assert.That(uv[leftIndex].y,
                    Is.EqualTo(uv[rightIndex].y).Within(FloatTolerance));
                Assert.That(uv[leftIndex].y, Is.GreaterThan(previousLongitudinal));
                previousLongitudinal = uv[leftIndex].y;
            }
        }

        [Test]
        public void Populate_CurrentMetadataCarriesMetricRibbonCoordinatesAndSplineSpeed()
        {
            var knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(
                    Vector3.zero, StraightTangent, StartWidth, StartCurrentSpeed),
                new WaterRiverKnot(
                    StraightEnd, StraightTangent, EndWidth, EndCurrentSpeed),
            };
            using var context = CreateContext(knots, SamplesPerSegment);
            Vector2[] normalizedUv = context.Mesh.uv;
            var currentData = new List<Vector4>();
            context.Mesh.GetUVs(1, currentData);

            Assert.That(currentData.Count, Is.EqualTo(context.Mesh.vertexCount));
            for (int crossSection = 0; crossSection <= SamplesPerSegment; crossSection++)
            {
                float normalizedT = crossSection / (float)SamplesPerSegment;
                Assert.That(context.Spline.TryEvaluate(
                    normalizedT, out WaterRiverSplineSample sample), Is.True);
                int leftIndex = crossSection *
                                WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
                int rightIndex = leftIndex +
                                 WaterRiverRibbonMeshGenerator.VerticesPerCrossSection - 1;
                Vector4 left = currentData[leftIndex];
                Vector4 right = currentData[rightIndex];

                AssertFinite(left);
                AssertFinite(right);
                Assert.That(left.x, Is.EqualTo(-sample.Width * 0.5f).Within(FloatTolerance));
                Assert.That(right.x, Is.EqualTo(sample.Width * 0.5f).Within(FloatTolerance));
                Assert.That(left.y,
                    Is.EqualTo(normalizedUv[leftIndex].y).Within(FloatTolerance));
                Assert.That(right.y, Is.EqualTo(left.y).Within(FloatTolerance));
                Assert.That(left.z, Is.EqualTo(sample.Speed).Within(FloatTolerance));
                Assert.That(right.z, Is.EqualTo(sample.Speed).Within(FloatTolerance));
                Assert.That(left.w, Is.EqualTo(1f).Within(FloatTolerance));
                Assert.That(right.w, Is.EqualTo(1f).Within(FloatTolerance));
            }
        }

        [Test]
        public void Populate_SharedSourceBoundaryResetsBakeDistanceButContinuesMetricDistance()
        {
            GameObject splineHost = CreateSpline(StraightKnots(), out WaterRiverSpline spline);
            var meshHost = new GameObject(MeshHostName);
            var mesh = new Mesh { name = GeneratedMeshName };
            try
            {
                int columns = WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
                var boundary = new WaterRiverRibbonMeshGenerator.SharedBoundaryRow
                {
                    WorldPositions = new Vector3[columns],
                    WorldNormals = new Vector3[columns],
                    WorldTangents = new Vector4[columns],
                    Uv = new Vector2[columns],
                    CurrentData = new Vector4[columns],
                };
                const float upstreamLongitudinalMeters = 23f;
                for (int column = 0; column < columns; column++)
                {
                    float lateral01 = column / (float)(columns - 1);
                    float lateralMeters = Mathf.Lerp(-StartWidth * 0.5f,
                                                      StartWidth * 0.5f, lateral01);
                    boundary.WorldPositions[column] = new Vector3(lateralMeters, 0f, 0f);
                    boundary.WorldNormals[column] = Vector3.up;
                    boundary.WorldTangents[column] = new Vector4(1f, 0f, 0f, -1f);
                    boundary.Uv[column] = new Vector2(lateral01, upstreamLongitudinalMeters);
                    boundary.CurrentData[column] = new Vector4(
                        lateralMeters, upstreamLongitudinalMeters, Speed, 1f);
                }

                WaterRiverRibbonMeshGenerator.RibbonMetrics metrics =
                    WaterRiverRibbonMeshGenerator.Populate(
                        mesh, spline, meshHost.transform, SamplesPerSegment, default, default,
                        boundary);

                var currentData = new List<Vector4>();
                mesh.GetUVs(1, currentData);
                for (int column = 0; column < columns; column++)
                {
                    AssertVector3(mesh.vertices[column], boundary.WorldPositions[column]);
                    AssertVector3(mesh.normals[column], boundary.WorldNormals[column]);
                    Assert.That(mesh.tangents[column], Is.EqualTo(boundary.WorldTangents[column]));
                    Assert.That(mesh.uv[column].x,
                                Is.EqualTo(boundary.Uv[column].x).Within(FloatTolerance));
                    Assert.That(mesh.uv[column].y, Is.Zero.Within(FloatTolerance));
                    Assert.That(currentData[column], Is.EqualTo(boundary.CurrentData[column]));
                }
                int secondRow = columns;
                Assert.That(mesh.uv[secondRow].y, Is.GreaterThan(0f),
                            "the local bake coordinate advances from this river's source");
                Assert.That(currentData[secondRow].y, Is.GreaterThan(upstreamLongitudinalMeters),
                            "the downstream ribbon continues the upstream phase coordinate");
                Assert.That(currentData[secondRow].y - mesh.uv[secondRow].y,
                            Is.EqualTo(upstreamLongitudinalMeters).Within(FloatTolerance));
                Assert.That(metrics.SourceLongitudinalMeters,
                            Is.EqualTo(upstreamLongitudinalMeters).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(meshHost);
                Object.DestroyImmediate(splineHost);
            }
        }

        [Test]
        public void Populate_ProducesFiniteGeometryTangentsAndBounds()
        {
            using var context = CreateContext(TwoSegmentKnots(), SamplesPerSegment);

            foreach (Vector3 vertex in context.Mesh.vertices) AssertFinite(vertex);
            foreach (Vector3 normal in context.Mesh.normals)
            {
                AssertFinite(normal);
                Assert.That(normal.sqrMagnitude, Is.EqualTo(1f).Within(FloatTolerance));
            }
            foreach (Vector4 tangent in context.Mesh.tangents)
            {
                AssertFinite(tangent);
                Assert.That(new Vector3(tangent.x, tangent.y, tangent.z).sqrMagnitude,
                    Is.EqualTo(1f).Within(FloatTolerance));
                Assert.That(tangent.w, Is.EqualTo(-1f).Within(FloatTolerance));
            }
            AssertFinite(context.Mesh.bounds.center);
            AssertFinite(context.Mesh.bounds.size);
            Assert.That(context.Mesh.bounds.size.sqrMagnitude, Is.GreaterThan(0f));
        }

        [Test]
        public void Populate_TransportedFrameStaysStableOnDescendingWaterfall()
        {
            using var context = CreateContext(WaterfallKnots(), SamplesPerSegment);
            Vector3[] normals = context.Mesh.normals;
            Vector4[] tangents = context.Mesh.tangents;
            Vector3 previousRight = Vector3.zero;

            for (int crossSection = 0; crossSection <= SamplesPerSegment; crossSection++)
            {
                int vertexIndex = crossSection *
                                  WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
                Vector3 worldUp = context.MeshHost.transform.localToWorldMatrix.inverse.transpose
                    .MultiplyVector(normals[vertexIndex]).normalized;
                Vector4 tangent = tangents[vertexIndex];
                Vector3 localRight = new Vector3(tangent.x, tangent.y, tangent.z);
                // Tangents are transformed by object-to-world in the water vertex shader. Unity's
                // TransformDirection deliberately ignores scale, so it cannot validate the frame
                // carried by a non-uniformly scaled ribbon.
                Vector3 worldRight = context.MeshHost.transform.localToWorldMatrix
                    .MultiplyVector(localRight).normalized;
                AssertFinite(worldUp);
                AssertFinite(worldRight);
                Assert.That(Mathf.Abs(Vector3.Dot(worldUp, worldRight)),
                    Is.LessThan(FloatTolerance));
                if (crossSection > 0)
                    Assert.That(Vector3.Dot(previousRight, worldRight), Is.GreaterThan(0f));
                previousRight = worldRight;
            }
        }

        [Test]
        public void Populate_TightBendHasNoFlippedOrDegenerateTriangles()
        {
            using var context = CreateContext(TwoSegmentKnots(), SamplesPerSegment);
            Vector3[] vertices = context.Mesh.vertices;
            Vector3[] normals = context.Mesh.normals;
            int[] triangles = context.Mesh.triangles;

            for (int index = 0; index < triangles.Length; index += 3)
            {
                int aIndex = triangles[index];
                int bIndex = triangles[index + 1];
                int cIndex = triangles[index + 2];
                Vector3 a = context.MeshHost.transform.TransformPoint(vertices[aIndex]);
                Vector3 b = context.MeshHost.transform.TransformPoint(vertices[bIndex]);
                Vector3 c = context.MeshHost.transform.TransformPoint(vertices[cIndex]);
                Vector3 doubleArea = Vector3.Cross(b - a, c - a);
                Vector3 localAverageNormal =
                    (normals[aIndex] + normals[bIndex] + normals[cIndex]).normalized;
                Vector3 worldAverageNormal =
                    context.MeshHost.transform.localToWorldMatrix.inverse.transpose
                        .MultiplyVector(localAverageNormal).normalized;

                Assert.That(doubleArea.magnitude, Is.GreaterThan(MinimumTriangleDoubleArea));
                Assert.That(Vector3.Dot(doubleArea.normalized, worldAverageNormal),
                    Is.GreaterThan(0f));
            }
        }

        [Test]
        public void Populate_NonUniformSurfaceScaleDoesNotChangeWorldWidth()
        {
            using var context = CreateContext(StraightKnots(), SamplesPerSegment);
            Vector3[] vertices = context.Mesh.vertices;
            int lastLeftIndex = SamplesPerSegment *
                                WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
            int lastRightIndex = lastLeftIndex +
                                 WaterRiverRibbonMeshGenerator.VerticesPerCrossSection - 1;
            Vector3 left = context.MeshHost.transform.TransformPoint(vertices[lastLeftIndex]);
            Vector3 right = context.MeshHost.transform.TransformPoint(vertices[lastRightIndex]);

            Assert.That(Vector3.Distance(left, right),
                Is.EqualTo(EndWidth).Within(FloatTolerance));
        }

        [Test]
        public void Surface_RebuildsOnSplineNotificationAndCleansOwnedMesh()
        {
            GameObject splineHost = CreateSpline(StraightKnots(), out WaterRiverSpline spline);
            var surfaceHost = new GameObject(MeshHostName);
            WaterRiverSurface surface = surfaceHost.AddComponent<WaterRiverSurface>();
            try
            {
                surface.spline = spline;
                surface.samplesPerSegment = SamplesPerSegment;
                surface.RequestRebuild();
                Mesh firstMesh = surface.GeneratedMesh;
                Assert.That(firstMesh, Is.Not.Null);
                Assert.That(surfaceHost.GetComponent<MeshRenderer>().forceRenderingOff, Is.False,
                    "A river ribbon must remain visible when no WaterVolume is assigned.");
                float originalLength = firstMesh.bounds.size.z;

                Vector3 longerEnd = StraightEnd * 2f;
                spline.knots = new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(Vector3.zero, StraightTangent, StartWidth, Speed),
                    new WaterRiverKnot(longerEnd, StraightTangent, EndWidth, Speed),
                };
                spline.NotifyChanged();

                Assert.That(surface.GeneratedMesh, Is.SameAs(firstMesh));
                Assert.That(surface.GeneratedMesh.bounds.size.z, Is.GreaterThan(originalLength));

                surface.enabled = false;
                // Unity defers Object.Destroy until the end of a Play Mode frame. Ownership and
                // renderer references must be released synchronously; object death is deferred by
                // the engine and is not a component lifecycle contract.
                Assert.That(surface.GeneratedMesh, Is.Null);
                Assert.That(surfaceHost.GetComponent<MeshFilter>().sharedMesh, Is.Null);

                surface.enabled = true;
                Assert.That(surface.GeneratedMesh, Is.Not.Null,
                    "The generated ribbon must be recreated after an Edit/Play-style enable cycle.");
                Assert.That(surfaceHost.GetComponent<MeshFilter>().sharedMesh,
                    Is.SameAs(surface.GeneratedMesh));
                Assert.That(surfaceHost.GetComponent<MeshRenderer>().forceRenderingOff, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(surfaceHost);
                Object.DestroyImmediate(splineHost);
            }
        }

        [Test]
        public void Surface_FogVolumeIsClosedAndUsesWorldVerticalGameplayDepth()
        {
            GameObject splineHost = CreateSpline(StraightKnots(), out WaterRiverSpline spline);
            var surfaceHost = new GameObject(MeshHostName);
            surfaceHost.transform.SetPositionAndRotation(
                MeshHostPosition, Quaternion.Euler(12f, 37f, -8f));
            surfaceHost.transform.localScale = MeshHostScale;
            WaterRiverSurface surface = surfaceHost.AddComponent<WaterRiverSurface>();
            try
            {
                surface.spline = spline;
                surface.samplesPerSegment = SamplesPerSegment;
                surface.gameplayDepthMeters = FogDepthMeters;
                surface.RequestRebuild();

                Mesh ribbon = surface.GeneratedMesh;
                Mesh fogVolume = surface.GeneratedFogVolumeMesh;
                Assert.That(fogVolume, Is.Not.Null);
                Assert.That(fogVolume.vertexCount,
                    Is.EqualTo(ribbon.vertexCount * FogVolumeLayerCount));

                Vector3[] vertices = fogVolume.vertices;
                for (int topIndex = 0; topIndex < ribbon.vertexCount; topIndex++)
                {
                    Vector3 topWorld = surfaceHost.transform.TransformPoint(vertices[topIndex]);
                    Vector3 bottomWorld = surfaceHost.transform.TransformPoint(
                        vertices[ribbon.vertexCount + topIndex]);
                    Assert.That(bottomWorld.x,
                        Is.EqualTo(topWorld.x).Within(FloatTolerance));
                    Assert.That(bottomWorld.y,
                        Is.EqualTo(topWorld.y - FogDepthMeters).Within(FloatTolerance));
                    Assert.That(bottomWorld.z,
                        Is.EqualTo(topWorld.z).Within(FloatTolerance));
                }

                var edgeUseCounts = new Dictionary<(int, int), int>();
                int[] triangles = fogVolume.triangles;
                for (int triangle = 0; triangle < triangles.Length;
                     triangle += TriangleIndexCount)
                {
                    CountEdge(edgeUseCounts, triangles[triangle], triangles[triangle + 1]);
                    CountEdge(edgeUseCounts, triangles[triangle + 1], triangles[triangle + 2]);
                    CountEdge(edgeUseCounts, triangles[triangle + 2], triangles[triangle]);
                }
                foreach (int useCount in edgeUseCounts.Values)
                    Assert.That(useCount, Is.EqualTo(ClosedEdgeUseCount),
                        "Every proxy edge must belong to two triangles for a closed depth volume.");
                Assert.That(SignedVolume(vertices, triangles), Is.GreaterThan(0f),
                    "The closed proxy must face outward for front/back depth culling.");

                surface.enabled = false;
                Assert.That(surface.GeneratedFogVolumeMesh, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(surfaceHost);
                Object.DestroyImmediate(splineHost);
            }
        }

        [Test]
        public void Surface_WaterShaderProvidesRiverFogOccluderDepthPass()
        {
            Shader shader = Shader.Find(WaterShaderNames.WaterSurface);
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(material.FindPass(WaterFogOccluderDepthPassName),
                    Is.EqualTo(WaterFogOccluderDepthPassIndex));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void Surface_RiverFogOwnershipCollectorIncludesOnlyRenderableTopSheets()
        {
            GameObject splineHost = CreateSpline(StraightKnots(), out WaterRiverSpline spline);
            var surfaceHost = new GameObject(MeshHostName);
            WaterRiverSurface surface = surfaceHost.AddComponent<WaterRiverSurface>();
            Material material = null;
            try
            {
                Shader shader = Shader.Find(WaterShaderNames.WaterSurface);
                Assert.That(shader, Is.Not.Null);
                material = new Material(shader);
                surfaceHost.GetComponent<MeshRenderer>().sharedMaterial = material;
                surface.spline = spline;
                surface.samplesPerSegment = SamplesPerSegment;
                surface.RequestRebuild();

                var renderers = new List<Renderer>();
                WaterRiverSurface.AppendActiveSurfaceRenderers(renderers);
                Assert.That(renderers.Contains(surface.SurfaceRenderer), Is.True);

                surface.SurfaceRenderer.forceRenderingOff = true;
                renderers.Clear();
                WaterRiverSurface.AppendActiveSurfaceRenderers(renderers);
                Assert.That(renderers.Contains(surface.SurfaceRenderer), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(surfaceHost);
                Object.DestroyImmediate(splineHost);
                if (material != null) Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void WaterVolume_BuiltInGeometryGateDoesNotDisableComponent()
        {
            var volumeHost = new GameObject(VolumeHostName);
            volumeHost.SetActive(false);
            WaterVolume volume = volumeHost.AddComponent<WaterVolume>();
            var builtInSurfaceHost = new GameObject(BuiltInSurfaceName);
            builtInSurfaceHost.transform.SetParent(volumeHost.transform);
            MeshRenderer builtInSurface = builtInSurfaceHost.AddComponent<MeshRenderer>();
            try
            {
                volume.surfaceAbove = builtInSurface;
                volume.renderBuiltInGeometry = false;
                volume.SetRenderersEnabled(on: true);

                Assert.That(volume.enabled, Is.True);
                Assert.That(builtInSurface.forceRenderingOff, Is.True);

                volume.renderBuiltInGeometry = true;
                volume.SetRenderersEnabled(on: true);
                Assert.That(builtInSurface.forceRenderingOff, Is.False,
                    "Built-in water rendering must remain enabled by default for existing bodies.");
            }
            finally
            {
                Object.DestroyImmediate(volumeHost);
            }
        }

        static RibbonContext CreateContext(List<WaterRiverKnot> knots, int samplesPerSegment)
        {
            GameObject splineHost = CreateSpline(knots, out WaterRiverSpline spline);
            var meshHost = new GameObject(MeshHostName);
            meshHost.transform.SetPositionAndRotation(
                MeshHostPosition, Quaternion.Euler(12f, 37f, -8f));
            meshHost.transform.localScale = MeshHostScale;
            var mesh = new Mesh { name = GeneratedMeshName };
            WaterRiverRibbonMeshGenerator.Populate(mesh, spline, meshHost.transform,
                                                   samplesPerSegment);
            return new RibbonContext(splineHost, meshHost, spline, mesh);
        }

        static GameObject CreateSpline(List<WaterRiverKnot> knots, out WaterRiverSpline spline)
        {
            var splineHost = new GameObject(SplineHostName);
            spline = splineHost.AddComponent<WaterRiverSpline>();
            spline.knots = knots;
            return splineHost;
        }

        static List<WaterRiverKnot> StraightKnots()
        {
            return new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, StraightTangent, StartWidth, Speed),
                new WaterRiverKnot(StraightEnd, StraightTangent, EndWidth, Speed),
            };
        }

        static List<WaterRiverKnot> TwoSegmentKnots()
        {
            return new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, BendStartTangent, StartWidth, Speed),
                new WaterRiverKnot(BendMiddle, BendMiddleTangent, MiddleWidth, Speed),
                new WaterRiverKnot(BendEnd, BendEndTangent, EndWidth, Speed),
            };
        }

        static List<WaterRiverKnot> WaterfallKnots()
        {
            return new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, WaterfallTangent, StartWidth, Speed),
                new WaterRiverKnot(WaterfallEnd, WaterfallTangent, EndWidth, Speed),
            };
        }

        static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }

        static void CountEdge(Dictionary<(int, int), int> counts, int first, int second)
        {
            var edge = first < second ? (first, second) : (second, first);
            counts.TryGetValue(edge, out int useCount);
            counts[edge] = useCount + 1;
        }

        static float SignedVolume(Vector3[] vertices, int[] triangles)
        {
            float sixTimesVolume = 0f;
            for (int triangle = 0; triangle < triangles.Length;
                 triangle += TriangleIndexCount)
            {
                Vector3 first = vertices[triangles[triangle]];
                Vector3 second = vertices[triangles[triangle + 1]];
                Vector3 third = vertices[triangles[triangle + 2]];
                sixTimesVolume += Vector3.Dot(first, Vector3.Cross(second, third));
            }
            return sixTimesVolume / TetrahedronVolumeDivisor;
        }

        static void AssertFinite(Vector3 value)
        {
            Assert.That(float.IsFinite(value.x), Is.True);
            Assert.That(float.IsFinite(value.y), Is.True);
            Assert.That(float.IsFinite(value.z), Is.True);
        }

        static void AssertFinite(Vector4 value)
        {
            Assert.That(float.IsFinite(value.x), Is.True);
            Assert.That(float.IsFinite(value.y), Is.True);
            Assert.That(float.IsFinite(value.z), Is.True);
            Assert.That(float.IsFinite(value.w), Is.True);
        }

        sealed class RibbonContext : System.IDisposable
        {
            readonly GameObject _splineHost;
            public GameObject MeshHost { get; }
            public WaterRiverSpline Spline { get; }
            public Mesh Mesh { get; }

            public RibbonContext(GameObject splineHost, GameObject meshHost,
                                 WaterRiverSpline spline, Mesh mesh)
            {
                _splineHost = splineHost;
                MeshHost = meshHost;
                Spline = spline;
                Mesh = mesh;
            }

            public void Dispose()
            {
                Object.DestroyImmediate(Mesh);
                Object.DestroyImmediate(MeshHost);
                Object.DestroyImmediate(_splineHost);
            }
        }
    }
}
