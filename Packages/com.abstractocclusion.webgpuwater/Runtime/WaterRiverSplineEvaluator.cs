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
        // Relative slack on the segment reject bound (TryProjectPoint): the hull bound is exact
        // in real arithmetic, but the scan's sample positions carry float32 rounding (~1e-7
        // relative), so a segment is only rejected when its bound beats the nearest knot by
        // this much - four orders of magnitude more than that rounding, at every river scale.
        const float ProjectionRejectSlack = 1e-3f;

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

        // Nearest-point projection: a coarse sample scan over every segment, then a ternary
        // refinement on the best one. The scan is the cost (17 frame evaluations per segment, per
        // query point), so segments that cannot hold the answer are rejected first: a cubic
        // Bezier lies inside the convex hull of its four control points, so the distance from
        // the point to that hull's AABB is a LOWER bound on its distance to every sample of the
        // segment, and the nearest knot is a sample of the scan (t = 0 and t = 1) whose
        // distance is therefore an UPPER bound on the scan's minimum. A segment whose lower
        // bound exceeds that upper bound can never win the scan, ties included - the scan keeps
        // the first strictly-nearer sample and a rejected segment is strictly farther - so the
        // result is identical to the unrejected scan. The bound costs 4 rotations per segment
        // (the scan costs 68), and it is rebuilt per call rather than cached: it needs the
        // world pose as well as the knots, and this evaluator owns neither the transform nor the
        // spline's change event - a stale cache here would silently move the river.
        internal static bool TryProjectPoint(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                             Quaternion rotation, Vector3 worldPoint,
                                             out WaterRiverSplineSample sample,
                                             out float squaredDistance)
        {
            sample = default;
            squaredDistance = float.PositiveInfinity;
            if (!HasEnoughKnots(knots) || !WaterSurfaceKinematics.IsFinite(worldPoint)) return false;

            float rejectSquaredDistance =
                NearestKnotSquaredDistance(knots, origin, rotation, worldPoint)
                * (1f + ProjectionRejectSlack);
            int bestSegment = 0;
            float bestSegmentT = 0f;
            for (int segmentIndex = 0; segmentIndex < knots.Count - 1; segmentIndex++)
            {
                if (SegmentHullSquaredDistance(knots, origin, rotation, segmentIndex, worldPoint)
                    > rejectSquaredDistance)
                    continue;
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

        // Upper bound on the projection scan's minimum: every knot is one of its samples
        // (segment t = 0 / t = 1), evaluated through the same world placement.
        static float NearestKnotSquaredDistance(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                                Quaternion rotation, Vector3 worldPoint)
        {
            float nearest = float.PositiveInfinity;
            for (int knotIndex = 0; knotIndex < knots.Count; knotIndex++)
            {
                Vector3 knotPosition = origin + rotation * knots[knotIndex].LocalPosition;
                float candidate = (knotPosition - worldPoint).sqrMagnitude;
                if (candidate < nearest) nearest = candidate;
            }
            return nearest;
        }

        // Lower bound on the distance from worldPoint to ANY point of one segment: the squared
        // distance to the axis-aligned box of its four Bezier control points (convex-hull
        // property). Zero when the point is inside the box.
        static float SegmentHullSquaredDistance(IReadOnlyList<WaterRiverKnot> knots, Vector3 origin,
                                                Quaternion rotation, int segmentIndex,
                                                Vector3 worldPoint)
        {
            WaterRiverKnot start = knots[segmentIndex];
            WaterRiverKnot end = knots[segmentIndex + 1];
            Vector3 startPosition = origin + rotation * start.LocalPosition;
            Vector3 endPosition = origin + rotation * end.LocalPosition;
            Vector3 startControl = startPosition + rotation * start.LocalTangent;
            Vector3 endControl = endPosition - rotation * end.LocalTangent;

            Vector3 boxMin = new Vector3(
                Mathf.Min(Mathf.Min(startPosition.x, endPosition.x), Mathf.Min(startControl.x, endControl.x)),
                Mathf.Min(Mathf.Min(startPosition.y, endPosition.y), Mathf.Min(startControl.y, endControl.y)),
                Mathf.Min(Mathf.Min(startPosition.z, endPosition.z), Mathf.Min(startControl.z, endControl.z)));
            Vector3 boxMax = new Vector3(
                Mathf.Max(Mathf.Max(startPosition.x, endPosition.x), Mathf.Max(startControl.x, endControl.x)),
                Mathf.Max(Mathf.Max(startPosition.y, endPosition.y), Mathf.Max(startControl.y, endControl.y)),
                Mathf.Max(Mathf.Max(startPosition.z, endPosition.z), Mathf.Max(startControl.z, endControl.z)));
            Vector3 nearestInBox = new Vector3(
                Mathf.Clamp(worldPoint.x, boxMin.x, boxMax.x),
                Mathf.Clamp(worldPoint.y, boxMin.y, boxMax.y),
                Mathf.Clamp(worldPoint.z, boxMin.z, boxMax.z));
            return (nearestInBox - worldPoint).sqrMagnitude;
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
