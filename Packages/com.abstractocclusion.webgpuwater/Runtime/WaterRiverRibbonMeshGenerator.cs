// WebGpuWater - spline samples to a stable, full-3D river ribbon mesh.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Builds only river-ribbon geometry. It owns no scene objects or Unity lifecycle.</summary>
    internal static class WaterRiverRibbonMeshGenerator
    {
        internal const int MinimumSamplesPerSegment = 1;
        internal const int VerticesPerCrossSection = 2;
        internal const int IndicesPerRibbonQuad = 6;

        const int LeftVertexOffset = 0;
        const int RightVertexOffset = 1;
        const float HalfWidth = 0.5f;
        const float LeftBankUv = 0f;
        const float RightBankUv = 1f;
        const float TangentHandedness = -1f;
        const float InvertibleMatrixDeterminantEpsilon = 1e-8f;
        const float DirectionLengthEpsilonSquared = 1e-10f;
        const float TriangleDoubleAreaEpsilonSquared = 1e-12f;
        const float TriangleOrientationEpsilon = 1e-6f;

        // Mesh channel contract:
        //   position  - ribbon geometry in the owning surface's local space;
        //   normal    - transported ribbon-up direction;
        //   tangent   - bank-left to bank-right direction, w=-1 so the bitangent is downstream;
        //   UV0.x     - lateral coordinate (left bank 0, right bank 1);
        //   UV0.y     - cumulative centreline distance in world metres.
        //   UV1.x     - signed lateral distance from the centreline in world metres;
        //   UV1.y     - cumulative centreline distance in world metres;
        //   UV1.z     - interpolated downstream current speed in world metres per second.
        // UV0 remains the normalized bake coordinate for later current-map and foam consumers. UV1
        // is the metric current coordinate used by the shared surface shader; vertex colors stay free.
        internal static void Populate(Mesh mesh, WaterRiverSpline spline, Transform meshTransform,
                                      int samplesPerSegment)
        {
            ValidateInputs(mesh, spline, meshTransform, samplesPerSegment);

            int crossSectionCount = checked(spline.SegmentCount * samplesPerSegment + 1);
            int vertexCount = checked(crossSectionCount * VerticesPerCrossSection);
            int indexCount = checked((crossSectionCount - 1) * IndicesPerRibbonQuad);
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var uv = new Vector2[vertexCount];
            var currentData = new Vector3[vertexCount];
            var indices = new int[indexCount];
            var worldPositions = new Vector3[vertexCount];
            var crossSectionUps = new Vector3[crossSectionCount];

            Matrix4x4 worldToLocal = meshTransform.worldToLocalMatrix;
            Matrix4x4 normalWorldToLocal = meshTransform.localToWorldMatrix.transpose;
            Vector3 previousRight = Vector3.zero;
            Vector3 previousCentre = Vector3.zero;
            float longitudinalDistance = 0f;

            for (int crossSection = 0; crossSection < crossSectionCount; crossSection++)
            {
                float normalizedT = crossSection / (float)(crossSectionCount - 1);
                if (!spline.TryEvaluate(normalizedT, out WaterRiverSplineSample sample))
                    throw new InvalidOperationException(
                        $"River ribbon could not evaluate cross-section {crossSection}.");
                ValidateSample(sample, crossSection);

                Vector3 right = TransportRight(sample.Tangent, sample.Right, previousRight,
                                               crossSection == 0);
                Vector3 up = Vector3.Cross(sample.Tangent, right).normalized;
                ValidateDirection(up, "up", crossSection);
                float halfWidth = sample.Width * HalfWidth;
                Vector3 leftBank = sample.Position - right * halfWidth;
                Vector3 rightBank = sample.Position + right * halfWidth;
                ValidatePosition(leftBank, "left bank", crossSection);
                ValidatePosition(rightBank, "right bank", crossSection);

                if (crossSection > 0)
                {
                    float stepDistance = Vector3.Distance(previousCentre, sample.Position);
                    if (!float.IsFinite(stepDistance) ||
                        stepDistance * stepDistance < DirectionLengthEpsilonSquared)
                        throw new InvalidOperationException(
                            $"River ribbon cross-sections {crossSection - 1} and {crossSection} " +
                            "share a centre; increase knot separation or adjust the spline tangents.");
                    longitudinalDistance += stepDistance;
                }

                int leftIndex = crossSection * VerticesPerCrossSection + LeftVertexOffset;
                int rightIndex = leftIndex + RightVertexOffset;
                worldPositions[leftIndex] = leftBank;
                worldPositions[rightIndex] = rightBank;
                vertices[leftIndex] = worldToLocal.MultiplyPoint3x4(leftBank);
                vertices[rightIndex] = worldToLocal.MultiplyPoint3x4(rightBank);

                Vector3 localUp = normalWorldToLocal.MultiplyVector(up).normalized;
                Vector3 localRight = worldToLocal.MultiplyVector(right).normalized;
                ValidateDirection(localUp, "local normal", crossSection);
                ValidateDirection(localRight, "local tangent", crossSection);
                normals[leftIndex] = localUp;
                normals[rightIndex] = localUp;
                tangents[leftIndex] = new Vector4(
                    localRight.x, localRight.y, localRight.z, TangentHandedness);
                tangents[rightIndex] = tangents[leftIndex];
                uv[leftIndex] = new Vector2(LeftBankUv, longitudinalDistance);
                uv[rightIndex] = new Vector2(RightBankUv, longitudinalDistance);
                currentData[leftIndex] = new Vector3(
                    -halfWidth, longitudinalDistance, sample.Speed);
                currentData[rightIndex] = new Vector3(
                    halfWidth, longitudinalDistance, sample.Speed);
                crossSectionUps[crossSection] = up;

                previousRight = right;
                previousCentre = sample.Position;
            }

            BuildIndices(worldPositions, crossSectionUps, indices);
            Bounds bounds = CalculateFiniteBounds(vertices);

            mesh.Clear();
            mesh.indexFormat = vertexCount > ushort.MaxValue
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uv;
            mesh.SetUVs(1, currentData);
            mesh.triangles = indices;
            mesh.bounds = bounds;
        }

        static void ValidateInputs(Mesh mesh, WaterRiverSpline spline, Transform meshTransform,
                                   int samplesPerSegment)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (spline == null) throw new ArgumentNullException(nameof(spline));
            if (meshTransform == null) throw new ArgumentNullException(nameof(meshTransform));
            if (samplesPerSegment < MinimumSamplesPerSegment)
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerSegment), samplesPerSegment,
                    $"River samples per segment must be at least {MinimumSamplesPerSegment}.");
            if (spline.SegmentCount < 1)
                throw new InvalidOperationException("River ribbon requires at least one spline segment.");

            float determinant = meshTransform.localToWorldMatrix.determinant;
            if (!float.IsFinite(determinant) ||
                Mathf.Abs(determinant) < InvertibleMatrixDeterminantEpsilon)
                throw new InvalidOperationException(
                    "River surface Transform must have finite, non-zero scale on every axis.");
        }

        static void ValidateSample(WaterRiverSplineSample sample, int crossSection)
        {
            ValidatePosition(sample.Position, "centre", crossSection);
            ValidateDirection(sample.Tangent, "downstream tangent", crossSection);
            ValidateDirection(sample.Right, "spline right", crossSection);
            if (!float.IsFinite(sample.Width) || sample.Width < WaterRiverSpline.MinimumWidth)
                throw new InvalidOperationException(
                    $"River ribbon cross-section {crossSection} has invalid width {sample.Width}.");
        }

        static Vector3 TransportRight(Vector3 tangent, Vector3 splineRight, Vector3 previousRight,
                                      bool isFirst)
        {
            Vector3 candidate = isFirst
                ? Vector3.ProjectOnPlane(splineRight, tangent)
                : Vector3.ProjectOnPlane(previousRight, tangent);
            if (candidate.sqrMagnitude < DirectionLengthEpsilonSquared)
                candidate = Vector3.ProjectOnPlane(splineRight, tangent);
            if (candidate.sqrMagnitude < DirectionLengthEpsilonSquared)
                candidate = Vector3.Cross(Vector3.up, tangent);
            if (candidate.sqrMagnitude < DirectionLengthEpsilonSquared)
                candidate = Vector3.Cross(Vector3.forward, tangent);
            if (candidate.sqrMagnitude < DirectionLengthEpsilonSquared)
                throw new InvalidOperationException("River ribbon could not resolve a width frame.");

            candidate.Normalize();
            if (!isFirst && Vector3.Dot(candidate, previousRight) < 0f) candidate = -candidate;
            return candidate;
        }

        static void BuildIndices(Vector3[] worldPositions, Vector3[] crossSectionUps, int[] indices)
        {
            int writeIndex = 0;
            for (int crossSection = 0; crossSection < crossSectionUps.Length - 1; crossSection++)
            {
                int currentLeft = crossSection * VerticesPerCrossSection + LeftVertexOffset;
                int currentRight = currentLeft + RightVertexOffset;
                int nextLeft = currentLeft + VerticesPerCrossSection;
                int nextRight = nextLeft + RightVertexOffset;
                Vector3 referenceUp = (crossSectionUps[crossSection] +
                                       crossSectionUps[crossSection + 1]).normalized;

                bool standardValid = TryScoreTriangulation(
                    worldPositions, referenceUp,
                    currentLeft, nextLeft, currentRight,
                    currentRight, nextLeft, nextRight,
                    out float standardScore);
                bool alternateValid = TryScoreTriangulation(
                    worldPositions, referenceUp,
                    currentLeft, nextRight, currentRight,
                    currentLeft, nextLeft, nextRight,
                    out float alternateScore);
                if (!standardValid && !alternateValid)
                    throw new InvalidOperationException(
                        $"River ribbon cross-sections {crossSection} and {crossSection + 1} fold " +
                        "or collapse. Reduce the width, soften the bend, or increase sampling.");

                bool useAlternate = alternateValid &&
                                    (!standardValid || alternateScore > standardScore);
                if (useAlternate)
                {
                    WriteTriangle(indices, ref writeIndex, currentLeft, nextRight, currentRight);
                    WriteTriangle(indices, ref writeIndex, currentLeft, nextLeft, nextRight);
                }
                else
                {
                    WriteTriangle(indices, ref writeIndex, currentLeft, nextLeft, currentRight);
                    WriteTriangle(indices, ref writeIndex, currentRight, nextLeft, nextRight);
                }
            }
        }

        static bool TryScoreTriangulation(Vector3[] positions, Vector3 referenceUp,
                                          int firstA, int firstB, int firstC,
                                          int secondA, int secondB, int secondC,
                                          out float score)
        {
            bool firstValid = TryScoreTriangle(
                positions[firstA], positions[firstB], positions[firstC], referenceUp,
                out float firstScore);
            bool secondValid = TryScoreTriangle(
                positions[secondA], positions[secondB], positions[secondC], referenceUp,
                out float secondScore);
            score = Mathf.Min(firstScore, secondScore);
            return firstValid && secondValid;
        }

        static bool TryScoreTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 referenceUp,
                                     out float score)
        {
            Vector3 doubleArea = Vector3.Cross(b - a, c - a);
            float doubleAreaSquared = doubleArea.sqrMagnitude;
            if (!float.IsFinite(doubleAreaSquared) ||
                doubleAreaSquared < TriangleDoubleAreaEpsilonSquared)
            {
                score = float.NegativeInfinity;
                return false;
            }

            score = Vector3.Dot(doubleArea / Mathf.Sqrt(doubleAreaSquared), referenceUp);
            return float.IsFinite(score) && score > TriangleOrientationEpsilon;
        }

        static void WriteTriangle(int[] indices, ref int writeIndex, int a, int b, int c)
        {
            indices[writeIndex++] = a;
            indices[writeIndex++] = b;
            indices[writeIndex++] = c;
        }

        static Bounds CalculateFiniteBounds(Vector3[] vertices)
        {
            Bounds bounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 0; i < vertices.Length; i++)
            {
                ValidatePosition(vertices[i], "local vertex", i);
                bounds.Encapsulate(vertices[i]);
            }
            ValidatePosition(bounds.center, "bounds centre", 0);
            ValidatePosition(bounds.size, "bounds size", 0);
            return bounds;
        }

        static void ValidatePosition(Vector3 value, string label, int crossSection)
        {
            if (WaterSurfaceKinematics.IsFinite(value)) return;
            throw new InvalidOperationException(
                $"River ribbon {label} is not finite at cross-section {crossSection}.");
        }

        static void ValidateDirection(Vector3 value, string label, int crossSection)
        {
            if (WaterSurfaceKinematics.IsFinite(value) &&
                value.sqrMagnitude >= DirectionLengthEpsilonSquared) return;
            throw new InvalidOperationException(
                $"River ribbon {label} is invalid at cross-section {crossSection}.");
        }
    }
}
