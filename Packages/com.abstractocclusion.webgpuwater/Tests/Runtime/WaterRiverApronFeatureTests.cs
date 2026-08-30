// WebGpuWater - body-border river seam acceptance tests (2026-08-30).
// Pins the single-ownership contract: no extra transition mesh, an exact flush terminal row,
// a monotonic motion hand-off through existing rows, and an explicit source/mouth selector.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverBodySeamFeatureTests
    {
        const float StartHeight = 4f;
        const float EndHeight = 0f;
        const float RiverLength = 12f;
        const float RiverWidth = 3f;
        const float RiverSpeed = 1.5f;
        const int SamplesPerSegment = 8;
        const float BodySurfaceY = 0.05f;
        const float TransitionLengthMeters = 6f;
        const float PositionTolerance = 1e-4f;

        readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        WaterRiverSpline _spline;
        Transform _meshTransform;
        Mesh _mesh;

        [SetUp]
        public void SetUp()
        {
            _objects.Clear();
            GameObject river = new GameObject("BodySeamRiver");
            _objects.Add(river);
            river.SetActive(false);
            _spline = river.AddComponent<WaterRiverSpline>();
            _spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(new Vector3(0f, StartHeight, 0f),
                                   new Vector3(0f, 0f, RiverLength / 3f),
                                   RiverWidth, RiverSpeed),
                new WaterRiverKnot(new Vector3(0f, EndHeight, RiverLength),
                                   new Vector3(0f, 0f, RiverLength / 3f),
                                   RiverWidth, RiverSpeed),
            };
            _meshTransform = river.transform;
            _mesh = new Mesh();
            _objects.Add(_mesh);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object obj in _objects)
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        }

        static WaterRiverRibbonMeshGenerator.BodySeamEnd MouthSeam(float length =
            TransitionLengthMeters) => new WaterRiverRibbonMeshGenerator.BodySeamEnd
            {
                Enabled = true,
                LengthMeters = length,
                Centre = new Vector3(0f, BodySurfaceY, RiverLength),
                Downstream = Vector3.forward,
                Right = Vector3.right,
                Up = Vector3.up,
            };

        static WaterRiverRibbonMeshGenerator.BodySeamEnd SourceSeam(float length =
            TransitionLengthMeters) => new WaterRiverRibbonMeshGenerator.BodySeamEnd
            {
                Enabled = true,
                LengthMeters = length,
                Centre = new Vector3(0f, BodySurfaceY, 0f),
                Downstream = Vector3.forward,
                Right = Vector3.right,
                Up = Vector3.up,
            };

        int SplineSectionCount => _spline.SegmentCount * SamplesPerSegment + 1;

        List<Vector4> ReadCurrentChannel()
        {
            var channel = new List<Vector4>();
            _mesh.GetUVs(1, channel);
            return channel;
        }

        List<Vector2> ReadEndChannel()
        {
            var channel = new List<Vector2>();
            _mesh.GetUVs(2, channel);
            return channel;
        }

        [Test]
        public void UnconnectedRibbon_KeepsFullWeightAndHasNoTransitionRows()
        {
            WaterRiverRibbonMeshGenerator.Populate(
                _mesh, _spline, _meshTransform, SamplesPerSegment);

            Assert.That(_mesh.vertexCount, Is.EqualTo(
                SplineSectionCount * WaterRiverRibbonMeshGenerator.VerticesPerCrossSection));
            foreach (Vector4 data in ReadCurrentChannel())
                Assert.That(data.w, Is.EqualTo(1f));
            foreach (Vector2 data in ReadEndChannel())
                Assert.That(data, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void MouthSeam_UsesExistingRowsAndEndsFlushOnTheBodyBorder()
        {
            WaterRiverRibbonMeshGenerator.Populate(
                _mesh, _spline, _meshTransform, SamplesPerSegment, default, MouthSeam());

            int columns = WaterRiverRibbonMeshGenerator.VerticesPerCrossSection;
            int terminalStart = _mesh.vertexCount - columns;
            Assert.That(_mesh.vertexCount, Is.EqualTo(SplineSectionCount * columns),
                        "a body connection must not grow an exposed transition mesh");

            Vector3[] vertices = _mesh.vertices;
            Vector3[] normals = _mesh.normals;
            for (int column = 0; column < columns; column++)
            {
                float lateral01 = column / (float)(columns - 1);
                Vector3 expected = new Vector3(
                    Mathf.Lerp(-RiverWidth * 0.5f, RiverWidth * 0.5f, lateral01),
                    BodySurfaceY, RiverLength);
                Assert.That(Vector3.Distance(vertices[terminalStart + column], expected),
                            Is.LessThan(PositionTolerance));
                Assert.That(Vector3.Distance(normals[terminalStart + column], Vector3.up),
                            Is.LessThan(PositionTolerance));
            }

            List<Vector4> current = ReadCurrentChannel();
            List<Vector2> end = ReadEndChannel();
            int centreColumn = columns / 2;
            float previousWeight = 1f;
            for (int row = 0; row < SplineSectionCount; row++)
            {
                int vertex = row * columns + centreColumn;
                Assert.That(current[vertex].w, Is.LessThanOrEqualTo(previousWeight + 1e-5f));
                previousWeight = current[vertex].w;
            }
            Assert.That(current[terminalStart + centreColumn].w, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(end[terminalStart + centreColumn].x, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void SourceSeam_UsesANegativeSelectorAndStartsFlush()
        {
            WaterRiverRibbonMeshGenerator.Populate(
                _mesh, _spline, _meshTransform, SamplesPerSegment, SourceSeam(), default);

            List<Vector4> current = ReadCurrentChannel();
            List<Vector2> end = ReadEndChannel();
            int centreColumn = WaterRiverRibbonMeshGenerator.VerticesPerCrossSection / 2;
            Assert.That(_mesh.vertices[centreColumn].y,
                        Is.EqualTo(BodySurfaceY).Within(PositionTolerance));
            Assert.That(current[centreColumn].w, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(end[centreColumn].x, Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(end[end.Count - 1], Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void OverlappingBodyTransitionBands_FailFast()
        {
            const float OverlappingLengthMeters = 8f;
            Assert.Throws<InvalidOperationException>(() =>
                WaterRiverRibbonMeshGenerator.Populate(
                    _mesh, _spline, _meshTransform, SamplesPerSegment,
                    SourceSeam(OverlappingLengthMeters), MouthSeam(OverlappingLengthMeters)));
        }
    }
}
