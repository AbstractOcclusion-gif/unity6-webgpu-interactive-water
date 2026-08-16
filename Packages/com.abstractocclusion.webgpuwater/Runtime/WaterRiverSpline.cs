// WebGpuWater - authorable 3D river centreline data.
//
// The component owns only curve metadata. Mesh generation, current sampling, foam baking, and wizard
// setup remain separate consumers so the spline cannot grow into a river god-object.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>One local-space river knot with a mirrored tangent for C1 curve continuity.</summary>
    [Serializable]
    public struct WaterRiverKnot
    {
        [SerializeField] internal Vector3 localPosition;
        [SerializeField] internal Vector3 localTangent;
        [Min(WaterRiverSpline.MinimumWidth)] [SerializeField] internal float width;
        [Min(WaterRiverSpline.MinimumSpeed)] [SerializeField] internal float speed;

        /// <summary>Position relative to the spline component, unaffected by Transform scale.</summary>
        public Vector3 LocalPosition => localPosition;
        /// <summary>Mirrored Bézier handle relative to the knot.</summary>
        public Vector3 LocalTangent => localTangent;
        /// <summary>Full bank-to-bank width in world metres.</summary>
        public float Width => width;
        /// <summary>Target downstream current in world metres per second.</summary>
        public float Speed => speed;

        internal WaterRiverKnot(Vector3 position, Vector3 tangent, float knotWidth, float knotSpeed)
        {
            localPosition = position;
            localTangent = tangent;
            width = knotWidth;
            speed = knotSpeed;
        }
    }

    /// <summary>One evaluated point on a river spline.</summary>
    public struct WaterRiverSplineSample
    {
        public Vector3 Position { get; internal set; }
        public Vector3 Tangent { get; internal set; }
        public Vector3 Right { get; internal set; }
        public Vector3 Up { get; internal set; }
        public float Width { get; internal set; }
        public float Speed { get; internal set; }
        public float NormalizedT { get; internal set; }
        public int SegmentIndex { get; internal set; }
        public float SegmentT { get; internal set; }
    }

    [AddComponentMenu("Abstract Occlusion/WebGpuWater/River Spline")]
    [DisallowMultipleComponent]
    public sealed class WaterRiverSpline : MonoBehaviour
    {
        internal const float MinimumWidth = 0.01f;
        internal const float MinimumSpeed = 0f;
        internal const float DefaultWidth = 5f;
        internal const float DefaultSpeed = 2f;
        internal const float DefaultSegmentLength = 10f;
        internal const float BezierHandleLengthFraction = 1f / 3f;
        internal const int MinimumKnotCount = 2;
        const float MinimumTangentLengthSquared = 1e-6f;

        static readonly Vector3 DefaultSegment = Vector3.forward * DefaultSegmentLength;
        static readonly Vector3 DefaultTangent = DefaultSegment * BezierHandleLengthFraction;

        [Tooltip("Ordered river centreline knots. Width is full bank-to-bank metres; Speed is target " +
                 "downstream metres per second. Tangents are mirrored to keep adjacent spans smooth.")]
        [SerializeField] internal List<WaterRiverKnot> knots = CreateDefaultKnots();

        internal event Action Changed;

        public int KnotCount => knots?.Count ?? 0;
        public int SegmentCount => Mathf.Max(0, KnotCount - 1);

        public WaterRiverKnot GetKnot(int index)
        {
            if (knots == null) throw new InvalidOperationException("River spline knots are not initialized.");
            if (index < 0 || index >= knots.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return knots[index];
        }

        /// <summary>Evaluate along the whole open spline, where 0 is its source and 1 its mouth.</summary>
        public bool TryEvaluate(float normalizedT, out WaterRiverSplineSample sample)
            => WaterRiverSplineEvaluator.TryEvaluate(
                knots, transform.position, transform.rotation, normalizedT, out sample);

        /// <summary>Find the nearest 3D centreline point without allocating.</summary>
        public bool TryProjectPoint(Vector3 worldPoint, out WaterRiverSplineSample sample,
                                    out float squaredDistance)
            => WaterRiverSplineEvaluator.TryProjectPoint(
                knots, transform.position, transform.rotation, worldPoint, out sample,
                out squaredDistance);

        internal bool TryEvaluateSegment(int segmentIndex, float segmentT,
                                         out WaterRiverSplineSample sample)
            => WaterRiverSplineEvaluator.TryEvaluateSegment(
                knots, transform.position, transform.rotation, segmentIndex, segmentT, out sample);

        // The spline frame deliberately ignores Transform scale: widths and speeds are authored in world
        // units, so scaling a hierarchy must not silently change the river's physical measurements.
        internal Vector3 LocalPointToWorld(Vector3 localPoint)
            => transform.position + transform.rotation * localPoint;

        internal Vector3 WorldPointToLocal(Vector3 worldPoint)
            => Quaternion.Inverse(transform.rotation) * (worldPoint - transform.position);

        internal Vector3 LocalDirectionToWorld(Vector3 localDirection)
            => transform.rotation * localDirection;

        internal Vector3 WorldDirectionToLocal(Vector3 worldDirection)
            => Quaternion.Inverse(transform.rotation) * worldDirection;

        internal void AddKnot()
        {
            EnsureValidKnots();
            WaterRiverKnot previous = knots[knots.Count - 2];
            WaterRiverKnot last = knots[knots.Count - 1];
            Vector3 continuation = last.localPosition - previous.localPosition;
            if (!WaterSurfaceKinematics.IsFinite(continuation) ||
                continuation.sqrMagnitude < MinimumTangentLengthSquared)
                continuation = DefaultSegment;

            Vector3 tangent = continuation * BezierHandleLengthFraction;
            knots.Add(new WaterRiverKnot(
                last.localPosition + continuation, tangent, last.width, last.speed));
            NotifyChanged();
        }

        internal bool RemoveLastKnot()
        {
            if (knots == null || knots.Count <= MinimumKnotCount) return false;
            knots.RemoveAt(knots.Count - 1);
            NotifyChanged();
            return true;
        }

        internal void ResetToDefaults()
        {
            knots = CreateDefaultKnots();
            NotifyChanged();
        }

        internal void NotifyChanged() => Changed?.Invoke();

        void Reset() => ResetToDefaults();

        void OnValidate()
        {
            EnsureValidKnots();
            NotifyChanged();
        }

        void EnsureValidKnots()
        {
            if (knots == null || knots.Count < MinimumKnotCount)
            {
                ResetToDefaults();
                return;
            }

            Vector3 fallbackPosition = Vector3.zero;
            for (int i = 0; i < knots.Count; i++)
            {
                WaterRiverKnot knot = knots[i];
                Vector3 position = WaterSurfaceKinematics.IsFinite(knot.localPosition)
                    ? knot.localPosition
                    : fallbackPosition;
                Vector3 fallbackTangent = ResolveFallbackTangent(i, position);
                Vector3 tangent = WaterSurfaceKinematics.IsFinite(knot.localTangent) &&
                                  knot.localTangent.sqrMagnitude >= MinimumTangentLengthSquared
                    ? knot.localTangent
                    : fallbackTangent;
                float width = float.IsFinite(knot.width)
                    ? Mathf.Max(MinimumWidth, knot.width)
                    : DefaultWidth;
                float speed = float.IsFinite(knot.speed)
                    ? Mathf.Max(MinimumSpeed, knot.speed)
                    : DefaultSpeed;

                knots[i] = new WaterRiverKnot(position, tangent, width, speed);
                fallbackPosition = position + DefaultSegment;
            }
        }

        Vector3 ResolveFallbackTangent(int index, Vector3 position)
        {
            if (index + 1 < knots.Count)
            {
                Vector3 towardNext = knots[index + 1].localPosition - position;
                if (WaterSurfaceKinematics.IsFinite(towardNext) &&
                    towardNext.sqrMagnitude >= MinimumTangentLengthSquared)
                    return towardNext * BezierHandleLengthFraction;
            }
            if (index > 0)
            {
                Vector3 fromPrevious = position - knots[index - 1].localPosition;
                if (WaterSurfaceKinematics.IsFinite(fromPrevious) &&
                    fromPrevious.sqrMagnitude >= MinimumTangentLengthSquared)
                    return fromPrevious * BezierHandleLengthFraction;
            }
            return DefaultTangent;
        }

        static List<WaterRiverKnot> CreateDefaultKnots()
        {
            return new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, DefaultTangent, DefaultWidth, DefaultSpeed),
                new WaterRiverKnot(DefaultSegment, DefaultTangent, DefaultWidth, DefaultSpeed),
            };
        }
    }
}
