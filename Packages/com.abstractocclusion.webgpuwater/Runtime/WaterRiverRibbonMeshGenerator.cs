// WebGpuWater - spline samples to a stable, full-3D river ribbon mesh.
//
// Body connections conform the existing terminal band to the receiving body's border frame.
// Nothing is carved and no submerged transition mesh is exposed: the terminal row is flush and
// body-like, while preceding rows smoothly hand geometry and motion back to the authored river.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Builds only river-ribbon geometry. It owns no scene objects or Unity lifecycle.</summary>
    internal static class WaterRiverRibbonMeshGenerator
    {
        internal const int MinimumSamplesPerSegment = 1;
        // The connected lake in the acceptance rig resolves one surface vertex every 6.25 cm.
        // Sixty-five lateral samples keep a 3-4 m river boundary in the same density class, so
        // both shaders piecewise-linearise their shared wave field at comparable spacing.
        internal const int VerticesPerCrossSection = 65;
        internal const int IndicesPerRibbonQuad = 6;
        internal const int RibbonQuadsPerCrossSectionPair = VerticesPerCrossSection - 1;

        const float HalfWidth = 0.5f;
        const float LeftBankUv = 0f;
        const float RightBankUv = 1f;
        const float TangentHandedness = -1f;
        const float FullSeamWeight = 1f;
        const float InvertibleMatrixDeterminantEpsilon = 1e-8f;
        const float DirectionLengthEpsilonSquared = 1e-10f;
        const float TriangleDoubleAreaEpsilonSquared = 1e-12f;
        const float TriangleOrientationEpsilon = 1e-6f;

        /// <summary>The receiving body's exact border frame for one connected end. The existing
        /// river rows are conformed toward it over LengthMeters; no extra geometry is generated.</summary>
        internal struct BodySeamEnd
        {
            internal bool Enabled;
            internal float LengthMeters;
            internal Vector3 Centre;
            internal Vector3 Downstream;
            internal Vector3 Right;
            internal Vector3 Up;

            internal bool IsActive => Enabled && LengthMeters > 0f &&
                WaterSurfaceKinematics.IsFinite(Centre) &&
                WaterSurfaceKinematics.IsFinite(Downstream) &&
                WaterSurfaceKinematics.IsFinite(Right) &&
                WaterSurfaceKinematics.IsFinite(Up) &&
                Downstream.sqrMagnitude >= DirectionLengthEpsilonSquared &&
                Right.sqrMagnitude >= DirectionLengthEpsilonSquared &&
                Up.sqrMagnitude >= DirectionLengthEpsilonSquared;

            internal bool SameAs(in BodySeamEnd other)
                => Enabled == other.Enabled && LengthMeters == other.LengthMeters &&
                   Centre == other.Centre && Downstream == other.Downstream &&
                   Right == other.Right && Up == other.Up;
        }

        /// <summary>A target river's terminal row in world space. A continuation copies this row
        /// verbatim into its source boundary, matching RAM's river-to-river ownership contract.</summary>
        internal struct SharedBoundaryRow
        {
            internal Vector3[] WorldPositions;
            internal Vector3[] WorldNormals;
            internal Vector4[] WorldTangents;
            internal Vector2[] Uv;
            internal Vector4[] CurrentData;

            internal bool IsActive =>
                WorldPositions?.Length == VerticesPerCrossSection &&
                WorldNormals?.Length == VerticesPerCrossSection &&
                WorldTangents?.Length == VerticesPerCrossSection &&
                Uv?.Length == VerticesPerCrossSection &&
                CurrentData?.Length == VerticesPerCrossSection;

            internal float LongitudinalMeters => IsActive ? CurrentData[0].y : 0f;
        }

        internal readonly struct RibbonMetrics
        {
            internal readonly float MouthLongitudinalMeters;
            internal readonly Vector3 MouthRight;

            internal RibbonMetrics(float mouthLongitudinalMeters, Vector3 mouthRight)
            {
                MouthLongitudinalMeters = mouthLongitudinalMeters;
                MouthRight = mouthRight;
            }
        }

        // Mesh channel contract:
        //   position  - ribbon geometry in the owning surface's local space;
        //   normal    - transported ribbon-up direction;
        //   tangent   - bank-left to bank-right direction, w=-1 so the bitangent is downstream;
        //   UV0.x     - lateral coordinate (left bank 0, right bank 1);
        //   UV0.y     - cumulative centreline distance in world metres;
        //   UV1.x     - signed lateral distance from the centreline in world metres;
        //   UV1.y     - cumulative centreline distance in world metres;
        //   UV1.z     - interpolated downstream current speed in world metres per second;
        //   UV1.w     - SEAM WEIGHT: 1 on the ribbon body, fading to 0 across a connected end's
        //               body-transition band. The vertex stage multiplies the river flag by it;
        //   UV2.x     - signed body blend selector: negative source, positive mouth, zero interior.
        // UV0 remains the normalized bake coordinate for later current-map and foam consumers. UV1
        // is the metric current coordinate used by the shared surface shader; vertex colors stay free.
        internal static RibbonMetrics Populate(
            Mesh mesh, WaterRiverSpline spline, Transform meshTransform,
            int samplesPerSegment)
            => Populate(mesh, spline, meshTransform, samplesPerSegment, default, default);

        internal static RibbonMetrics Populate(
            Mesh mesh, WaterRiverSpline spline, Transform meshTransform,
            int samplesPerSegment, BodySeamEnd sourceSeam, BodySeamEnd mouthSeam)
            => Populate(mesh, spline, meshTransform, samplesPerSegment, sourceSeam, mouthSeam,
                        default);

        internal static RibbonMetrics Populate(
            Mesh mesh, WaterRiverSpline spline, Transform meshTransform,
            int samplesPerSegment, BodySeamEnd sourceSeam,
            BodySeamEnd mouthSeam, SharedBoundaryRow sourceBoundary)
        {
            ValidateInputs(mesh, spline, meshTransform, samplesPerSegment);

            if (sourceBoundary.IsActive && sourceSeam.IsActive)
                throw new InvalidOperationException(
                    "A river source cannot use a body seam and a shared river boundary together.");

            int splineSections = checked(spline.SegmentCount * samplesPerSegment + 1);
            int crossSectionCount = splineSections;
            int vertexCount = checked(crossSectionCount * VerticesPerCrossSection);
            int indexCount = checked((crossSectionCount - 1) *
                                     RibbonQuadsPerCrossSectionPair *
                                     IndicesPerRibbonQuad);
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var uv = new Vector2[vertexCount];
            var currentData = new Vector4[vertexCount];
            var endBlendData = new Vector2[vertexCount];
            var indices = new int[indexCount];
            var worldPositions = new Vector3[vertexCount];
            var crossSectionUps = new Vector3[crossSectionCount];

            Matrix4x4 worldToLocal = meshTransform.worldToLocalMatrix;
            Matrix4x4 normalWorldToLocal = meshTransform.localToWorldMatrix.transpose;

            var samples = new WaterRiverSplineSample[splineSections];
            var transportedRights = new Vector3[splineSections];
            var transportedUps = new Vector3[splineSections];
            var distances = new float[splineSections];
            Vector3 previousRight = Vector3.zero;
            Vector3 previousCentre = Vector3.zero;
            float splineDistance = 0f;

            for (int splineSection = 0; splineSection < splineSections; splineSection++)
            {
                float normalizedT = splineSection / (float)(splineSections - 1);
                if (!spline.TryEvaluate(normalizedT, out WaterRiverSplineSample sample))
                    throw new InvalidOperationException(
                        $"River ribbon could not evaluate cross-section {splineSection}.");
                ValidateSample(sample, splineSection);

                Vector3 right = TransportRight(sample.Tangent, sample.Right, previousRight,
                                               splineSection == 0);
                Vector3 up = Vector3.Cross(sample.Tangent, right).normalized;
                ValidateDirection(up, "up", splineSection);

                if (splineSection > 0)
                {
                    float stepDistance = Vector3.Distance(previousCentre, sample.Position);
                    if (!float.IsFinite(stepDistance) ||
                        stepDistance * stepDistance < DirectionLengthEpsilonSquared)
                        throw new InvalidOperationException(
                            $"River ribbon cross-sections {splineSection - 1} and {splineSection} " +
                            "share a centre; increase knot separation or adjust the spline tangents.");
                    splineDistance += stepDistance;
                }

                samples[splineSection] = sample;
                transportedRights[splineSection] = right;
                transportedUps[splineSection] = up;
                distances[splineSection] = splineDistance;

                previousRight = right;
                previousCentre = sample.Position;
            }

            if (sourceSeam.IsActive && mouthSeam.IsActive &&
                sourceSeam.LengthMeters + mouthSeam.LengthMeters > splineDistance)
                throw new InvalidOperationException(
                    "River body-seam transition bands overlap; shorten one transition radius.");

            float longitudinalOrigin = sourceBoundary.LongitudinalMeters;
            Vector3 mouthRight = Vector3.zero;
            for (int splineSection = 0; splineSection < splineSections; splineSection++)
            {
                WaterRiverSplineSample sample = samples[splineSection];
                Vector3 centre = sample.Position;
                Vector3 right = transportedRights[splineSection];
                Vector3 up = transportedUps[splineSection];
                float sourceDistance = distances[splineSection];
                float mouthDistance = splineDistance - sourceDistance;
                float bodyBlend = 0f;
                float endSelector = 0f;

                if (sourceSeam.IsActive && sourceDistance < sourceSeam.LengthMeters)
                {
                    bodyBlend = ConformToBodySeam(
                        in sourceSeam, isMouth: false, sourceDistance,
                        ref centre, ref right, ref up);
                    endSelector = -bodyBlend;
                }
                else if (mouthSeam.IsActive && mouthDistance < mouthSeam.LengthMeters)
                {
                    bodyBlend = ConformToBodySeam(
                        in mouthSeam, isMouth: true, mouthDistance,
                        ref centre, ref right, ref up);
                    endSelector = bodyBlend;
                }

                WriteCrossSection(splineSection, centre, right, up,
                                  sample.Width * HalfWidth,
                                  longitudinalOrigin + sourceDistance, sample.Speed,
                                  FullSeamWeight - bodyBlend, endSelector, worldToLocal,
                                  normalWorldToLocal, vertices, normals, tangents, uv,
                                  currentData, endBlendData, worldPositions, crossSectionUps);
                if (splineSection == splineSections - 1) mouthRight = right;
            }

            if (sourceBoundary.IsActive)
                ApplySharedSourceBoundary(in sourceBoundary, 0, worldToLocal,
                                          normalWorldToLocal, vertices, normals, tangents, uv,
                                          currentData, endBlendData, worldPositions,
                                          crossSectionUps);

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
            mesh.SetUVs(2, endBlendData);
            mesh.triangles = indices;
            mesh.bounds = bounds;
            return new RibbonMetrics(longitudinalOrigin + splineDistance, mouthRight);
        }

        static void ApplySharedSourceBoundary(in SharedBoundaryRow boundary, int section,
                                              Matrix4x4 worldToLocal,
                                              Matrix4x4 normalWorldToLocal, Vector3[] vertices,
                                              Vector3[] normals, Vector4[] tangents, Vector2[] uv,
                                              Vector4[] currentData, Vector2[] endBlendData,
                                              Vector3[] worldPositions,
                                              Vector3[] crossSectionUps)
        {
            int rowStart = section * VerticesPerCrossSection;
            Vector3 averageWorldNormal = Vector3.zero;
            for (int column = 0; column < VerticesPerCrossSection; column++)
            {
                int vertexIndex = rowStart + column;
                Vector3 worldPosition = boundary.WorldPositions[column];
                Vector3 worldNormal = boundary.WorldNormals[column].normalized;
                Vector4 worldTangent = boundary.WorldTangents[column];
                Vector3 worldTangentDirection = new Vector3(
                    worldTangent.x, worldTangent.y, worldTangent.z).normalized;

                ValidatePosition(worldPosition, "shared boundary vertex", section);
                ValidateDirection(worldNormal, "shared boundary normal", section);
                ValidateDirection(worldTangentDirection, "shared boundary tangent", section);

                worldPositions[vertexIndex] = worldPosition;
                vertices[vertexIndex] = worldToLocal.MultiplyPoint3x4(worldPosition);
                normals[vertexIndex] = normalWorldToLocal.MultiplyVector(worldNormal).normalized;
                Vector3 localTangent = worldToLocal.MultiplyVector(worldTangentDirection).normalized;
                tangents[vertexIndex] = new Vector4(
                    localTangent.x, localTangent.y, localTangent.z, worldTangent.w);
                uv[vertexIndex] = boundary.Uv[column];
                Vector4 targetCurrentData = boundary.CurrentData[column];
                currentData[vertexIndex] = new Vector4(
                    targetCurrentData.x, targetCurrentData.y, targetCurrentData.z,
                    FullSeamWeight);
                endBlendData[vertexIndex] = Vector2.zero;
                averageWorldNormal += worldNormal;
            }
            ValidateDirection(averageWorldNormal, "shared boundary average normal", section);
            crossSectionUps[section] = averageWorldNormal.normalized;
        }

        static float ConformToBodySeam(in BodySeamEnd seam, bool isMouth,
                                       float distanceFromTerminal, ref Vector3 centre,
                                       ref Vector3 right, ref Vector3 up)
        {
            float bodyBlend = 1f - Mathf.SmoothStep(
                0f, 1f, Mathf.Clamp01(distanceFromTerminal / seam.LengthMeters));
            float directionSign = isMouth ? -1f : 1f;
            Vector3 targetCentre = seam.Centre +
                                   seam.Downstream * (directionSign * distanceFromTerminal);
            centre = Vector3.Lerp(centre, targetCentre, bodyBlend);

            Vector3 targetRight = Vector3.Dot(right, seam.Right) >= 0f ? seam.Right : -seam.Right;
            up = Vector3.Lerp(up, seam.Up, bodyBlend).normalized;
            right = Vector3.ProjectOnPlane(
                Vector3.Lerp(right, targetRight, bodyBlend), up).normalized;
            return bodyBlend;
        }

        static void WriteCrossSection(int section, Vector3 centre, Vector3 right, Vector3 up,
                                      float halfWidth, float longitudinal, float speed,
                                      float seamWeight, float endSelector,
                                      Matrix4x4 worldToLocal,
                                      Matrix4x4 normalWorldToLocal, Vector3[] vertices,
                                      Vector3[] normals, Vector4[] tangents, Vector2[] uv,
                                      Vector4[] currentData, Vector2[] endBlendData,
                                      Vector3[] worldPositions,
                                      Vector3[] crossSectionUps)
        {
            Vector3 localUp = normalWorldToLocal.MultiplyVector(up).normalized;
            Vector3 localRight = worldToLocal.MultiplyVector(right).normalized;
            ValidateDirection(localUp, "local normal", section);
            ValidateDirection(localRight, "local tangent", section);
            Vector4 tangent = new Vector4(
                localRight.x, localRight.y, localRight.z, TangentHandedness);

            int firstIndex = section * VerticesPerCrossSection;
            int lateralIntervalCount = VerticesPerCrossSection - 1;
            for (int column = 0; column < VerticesPerCrossSection; column++)
            {
                float lateral01 = column / (float)lateralIntervalCount;
                float lateralMeters = Mathf.Lerp(-halfWidth, halfWidth, lateral01);
                Vector3 worldPosition = centre + right * lateralMeters;
                ValidatePosition(worldPosition, "cross-section vertex", section);

                int vertexIndex = firstIndex + column;
                worldPositions[vertexIndex] = worldPosition;
                vertices[vertexIndex] = worldToLocal.MultiplyPoint3x4(worldPosition);
                normals[vertexIndex] = localUp;
                tangents[vertexIndex] = tangent;
                uv[vertexIndex] = new Vector2(Mathf.Lerp(LeftBankUv, RightBankUv, lateral01),
                                              longitudinal);
                currentData[vertexIndex] = new Vector4(
                    lateralMeters, longitudinal, speed, seamWeight);
                endBlendData[vertexIndex] = new Vector2(endSelector, 0f);
            }
            crossSectionUps[section] = up;
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
                Vector3 referenceUp = (crossSectionUps[crossSection] +
                                       crossSectionUps[crossSection + 1]).normalized;
                int currentRowStart = crossSection * VerticesPerCrossSection;
                int nextRowStart = currentRowStart + VerticesPerCrossSection;
                for (int column = 0; column < RibbonQuadsPerCrossSectionPair; column++)
                {
                    int currentLeft = currentRowStart + column;
                    int currentRight = currentLeft + 1;
                    int nextLeft = nextRowStart + column;
                    int nextRight = nextLeft + 1;

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
                            $"River ribbon cross-sections {crossSection} and " +
                            $"{crossSection + 1}, columns {column} and {column + 1}, fold or " +
                            "collapse. Reduce the width, soften the bend, or increase sampling.");

                    bool useAlternate = alternateValid &&
                                        (!standardValid || alternateScore > standardScore);
                    if (useAlternate)
                    {
                        WriteTriangle(indices, ref writeIndex,
                                      currentLeft, nextRight, currentRight);
                        WriteTriangle(indices, ref writeIndex,
                                      currentLeft, nextLeft, nextRight);
                    }
                    else
                    {
                        WriteTriangle(indices, ref writeIndex,
                                      currentLeft, nextLeft, currentRight);
                        WriteTriangle(indices, ref writeIndex,
                                      currentRight, nextLeft, nextRight);
                    }
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
