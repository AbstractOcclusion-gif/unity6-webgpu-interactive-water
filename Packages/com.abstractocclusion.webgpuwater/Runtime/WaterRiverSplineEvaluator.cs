// WebGpuWater - pure, allocation-free cubic river-spline evaluation.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterRiverSplineEvaluator
    {
        const int ProjectionSamplesPerSegment = 16;
        const int ProjectionRefinementIterations = 6;
        const float ProjectionThird = 1f / 3f;
        const float DirectionLengthEpsilonSquared = 1e-8f;

        internal static bool TryEvaluate(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                         Quaternion rotation, float normalizedT,
                                         out WaterRiverSplineSample sample)
        {
            sample = default;
            if (!HasEnoughKnots(knots) || !float.IsFinite(normalizedT)) return false;

            int segmentCount = knots.Count - 1;
            float clampedT = Mathf.Clamp01(normalizedT);
            float scaledT = clampedT * segmentCount;
            int segmentIndex = Mathf.Min(Mathf.FloorToInt(scaledT), segmentCount - 1);
            float segmentT = segmentIndex == segmentCount - 1 && clampedT >= 1f
                ? 1f
                : scaledT - segmentIndex;
            return TryEvaluateSegment(
                knots, origin, rotation, segmentIndex, segmentT, out sample);
        }

        internal static bool TryEvaluateSegment(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                                Quaternion rotation, int segmentIndex, float segmentT,
                                                out WaterRiverSplineSample sample)
        {
            sample = default;
            if (!HasEnoughKnots(knots) || segmentIndex < 0 || segmentIndex >= knots.Count - 1 ||
                !float.IsFinite(segmentT)) return false;

            float t = Mathf.Clamp01(segmentT);
            WaterRiverKnot start = knots[segmentIndex];
            WaterRiverKnot end = knots[segmentIndex + 1];
            Vector3 startPosition = origin + rotation * start.LocalPosition;
            Vector3 endPosition = origin + rotation * end.LocalPosition;
            Vector3 startControl = startPosition + rotation * start.LocalTangent;
            Vector3 endControl = endPosition - rotation * end.LocalTangent;
            Vector3 position = EvaluateCubic(
                startPosition, startControl, endControl, endPosition, t);
            Vector3 derivative = EvaluateCubicDerivative(
                startPosition, startControl, endControl, endPosition, t);
            Vector3 fallbackDirection = endPosition - startPosition;
            Vector3 tangent = NormalizeDirection(
                derivative, fallbackDirection, rotation * Vector3.forward);
            Vector3 right = CalculateRight(tangent, rotation * Vector3.forward);
            Vector3 up = Vector3.Cross(tangent, right).normalized;
            int segmentCount = knots.Count - 1;

            sample = new WaterRiverSplineSample
            {
                Position = position,
                Tangent = tangent,
                Right = right,
                Up = up,
                Width = Mathf.Lerp(start.Width, end.Width, t),
                Speed = Mathf.Lerp(start.Speed, end.Speed, t),
                NormalizedT = (segmentIndex + t) / segmentCount,
                SegmentIndex = segmentIndex,
                SegmentT = t,
            };
            return true;
        }

        internal static bool TryProjectPoint(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                             Quaternion rotation, Vector3 worldPoint,
                                             out WaterRiverSplineSample sample,
                                             out float squaredDistance)
        {
            sample = default;
            squaredDistance = float.PositiveInfinity;
            if (!HasEnoughKnots(knots) || !WaterSurfaceKinematics.IsFinite(worldPoint)) return false;

            int bestSegment = 0;
            float bestSegmentT = 0f;
            for (int segmentIndex = 0; segmentIndex < knots.Count - 1; segmentIndex++)
            {
                for (int step = 0; step <= ProjectionSamplesPerSegment; step++)
                {
                    float segmentT = step / (float)ProjectionSamplesPerSegment;
                    float candidateDistance = SquaredDistanceAt(
                        knots, origin, rotation, segmentIndex, segmentT, worldPoint);
                    if (candidateDistance >= squaredDistance) continue;

                    squaredDistance = candidateDistance;
                    bestSegment = segmentIndex;
                    bestSegmentT = segmentT;
                }
            }

            float sampleSpan = 1f / ProjectionSamplesPerSegment;
            float left = Mathf.Max(0f, bestSegmentT - sampleSpan);
            float right = Mathf.Min(1f, bestSegmentT + sampleSpan);
            for (int iteration = 0; iteration < ProjectionRefinementIterations; iteration++)
            {
                float rangeThird = (right - left) * ProjectionThird;
                float leftCandidate = left + rangeThird;
                float rightCandidate = right - rangeThird;
                float leftDistance = SquaredDistanceAt(
                    knots, origin, rotation, bestSegment, leftCandidate, worldPoint);
                float rightDistance = SquaredDistanceAt(
                    knots, origin, rotation, bestSegment, rightCandidate, worldPoint);
                if (leftDistance <= rightDistance) right = rightCandidate;
                else left = leftCandidate;
            }

            float refinedT = (left + right) * 0.5f;
            float refinedDistance = SquaredDistanceAt(
                knots, origin, rotation, bestSegment, refinedT, worldPoint);
            if (refinedDistance < squaredDistance)
            {
                squaredDistance = refinedDistance;
                bestSegmentT = refinedT;
            }

            return TryEvaluateSegment(
                knots, origin, rotation, bestSegment, bestSegmentT, out sample);
        }

        internal static Vector3 CalculateRight(Vector3 tangent, Vector3 fallbackForward)
        {
            Vector3 horizontalDirection = Vector3.ProjectOnPlane(tangent, Vector3.up);
            if (horizontalDirection.sqrMagnitude < DirectionLengthEpsilonSquared)
                horizontalDirection = Vector3.ProjectOnPlane(fallbackForward, Vector3.up);
            if (horizontalDirection.sqrMagnitude < DirectionLengthEpsilonSquared)
                horizontalDirection = Vector3.forward;
            return Vector3.Cross(Vector3.up, horizontalDirection.normalized).normalized;
        }

        static float SquaredDistanceAt(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                       Quaternion rotation, int segmentIndex, float segmentT,
                                       Vector3 worldPoint)
        {
            if (!TryEvaluateSegment(
                    knots, origin, rotation, segmentIndex, segmentT,
                    out WaterRiverSplineSample candidate))
                return float.PositiveInfinity;
            return (candidate.Position - worldPoint).sqrMagnitude;
        }

        static Vector3 EvaluateCubic(Vector3 start, Vector3 startControl,
                                     Vector3 endControl, Vector3 end, float t)
        {
            float inverseT = 1f - t;
            float inverseTSquared = inverseT * inverseT;
            float tSquared = t * t;
            return inverseTSquared * inverseT * start
                   + 3f * inverseTSquared * t * startControl
                   + 3f * inverseT * tSquared * endControl
                   + tSquared * t * end;
        }

        static Vector3 EvaluateCubicDerivative(Vector3 start, Vector3 startControl,
                                               Vector3 endControl, Vector3 end, float t)
        {
            float inverseT = 1f - t;
            return 3f * inverseT * inverseT * (startControl - start)
                   + 6f * inverseT * t * (endControl - startControl)
                   + 3f * t * t * (end - endControl);
        }

        static Vector3 NormalizeDirection(Vector3 direction, Vector3 fallback,
                                          Vector3 finalFallback)
        {
            if (direction.sqrMagnitude >= DirectionLengthEpsilonSquared) return direction.normalized;
            if (fallback.sqrMagnitude >= DirectionLengthEpsilonSquared) return fallback.normalized;
            return finalFallback.sqrMagnitude >= DirectionLengthEpsilonSquared
                ? finalFallback.normalized
                : Vector3.forward;
        }

        static bool HasEnoughKnots(IReadOnlyList<WaterRiverKnot> knots)
            => knots != null && knots.Count >= WaterRiverSpline.MinimumKnotCount;
    }
}
