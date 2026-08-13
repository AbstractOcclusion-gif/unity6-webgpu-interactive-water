// WebGpuWater - editor-only 2D silhouette of a hull, drawn behind the Water Wizard's draft line.
//
// A draft is read from a side elevation, and a side elevation is a 2D drawing: no preview camera, no
// lighting rig, no picking framework. This file turns a hull's meshes into ONE convex outline in the
// (along-hull, world-Y) plane, which the wizard fills as a backdrop.
//
// The outline is deliberately APPROXIMATE - a convex hull loses the sheer and the keel's concavity.
// That is the right way round: the backdrop only has to say WHERE you are on the hull. The probe
// placement that follows reads the exact plane/triangle slice, never this outline.
//
// The VERTICAL axis is world Y, unrotated, because the draft is a horizontal world plane - so a
// pixel row in the preview maps to a world height through one linear transform. The HORIZONTAL axis
// is the hull's own forward, flattened: the boat build makes the boat's forward its bow
// (WaterBuildKit.Boat / the wizard's "Model forward"), so this reads bow-right, stern-left.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    /// <summary>Projects a hull's meshes to a side elevation and reduces them to one convex outline.</summary>
    internal static class WaterHullOutline2D
    {
        // A polygon needs three distinct points; anything less has no area to fill.
        const int MinOutlinePoints = 3;
        // Cross-product magnitude below which three points are treated as collinear and the middle
        // one dropped. Squared metres, so this is far under any real hull's precision.
        const float CollinearEpsilonSqMeters = 1e-9f;
        // The lower chain can be popped back to two points; below that there is no turn to test.
        const int LowerChainFloor = 2;
        // A forward axis shorter than this after flattening (a hull pitched to vertical) is
        // unusable as a length direction; world +Z stands in so the preview still draws.
        const float MinForwardLength = 1e-4f;

        /// <summary>
        /// A hull's side elevation: one convex outline in metres, or the reason there isn't one.
        /// X is distance along the hull's forward axis from its origin, Y is world height.
        /// </summary>
        internal readonly struct Silhouette
        {
            /// <summary>Convex outline in (along-hull, world-Y) metres, counter-clockwise. Null on failure.</summary>
            public readonly Vector2[] Outline;

            /// <summary>Bounding rect of <see cref="Outline"/>, in the same metres.</summary>
            public readonly Rect Bounds;

            /// <summary>User-facing reason the silhouette could not be built; null on success.</summary>
            public readonly string Error;

            public bool Ok => Error == null && Outline != null;

            Silhouette(Vector2[] outline, Rect bounds, string error)
            {
                Outline = outline;
                Bounds = bounds;
                Error = error;
            }

            internal static Silhouette Failed(string reason) => new Silhouette(null, default, reason);

            internal static Silhouette From(Vector2[] outline, Rect bounds) => new Silhouette(outline, bounds, null);
        }

        /// <summary>
        /// Builds the side elevation of <paramref name="hullObject"/>. When <paramref name="hullFilter"/>
        /// is set only that one mesh is used - a visual root carrying masts, a cabin and crew must not be
        /// projected as if it were the hull. Never throws: every failure comes back as
        /// <see cref="Silhouette.Error"/> so the caller can say so out loud.
        /// </summary>
        internal static Silhouette Build(GameObject hullObject, MeshFilter hullFilter, View view)
        {
            if (hullObject == null)
                return Silhouette.Failed("No hull object: select the boat in the scene, or assign one above.");

            Transform root = hullObject.transform;
            Frame frame = Frame.For(root, view);

            var projected = new List<Vector2>();
            if (hullFilter != null) AppendFilter(hullFilter, frame, projected);
            else AppendFiltersUnder(root, frame, projected);

            if (projected.Count == 0)
                return Silhouette.Failed(
                    $"'{hullObject.name}' has no readable mesh vertices to draw. Assign the hull's Mesh Filter " +
                    "above, or - if the model imports with Read/Write Enabled off - turn that on in its importer.");

            Vector2[] outline = BuildConvexOutline(projected);
            if (outline.Length < MinOutlinePoints)
                return Silhouette.Failed(
                    $"'{hullObject.name}' projects to a line, not a shape. Check the hull is not flattened on " +
                    "one axis, and that the Mesh Filter above points at the hull rather than a decal or plane.");

            return Silhouette.From(outline, BoundsOf(outline));
        }

        // ---- projection ------------------------------------------------------------------------

        /// <summary>Which way the hull is being looked at.</summary>
        internal enum View
        {
            /// <summary>Side elevation: along the hull vs world height. The one a draft is read from.</summary>
            Side,

            /// <summary>Plan view: along the hull vs across it. The one petal directions are read from.</summary>
            Top,
        }

        /// <summary>
        /// The 2D frame a view projects into. Exposed so anything drawn OVER the silhouette - probe dots,
        /// petal arrows, the draft line - lands in the same metres as the outline rather than through a
        /// second projection that could drift from it.
        /// </summary>
        internal readonly struct Frame
        {
            readonly Vector3 _origin;
            readonly Vector3 _axisAlong;
            readonly Vector3 _axisUp;
            readonly bool _worldHeight;

            Frame(Vector3 origin, Vector3 axisAlong, Vector3 axisUp, bool worldHeight)
            {
                _origin = origin;
                _axisAlong = axisAlong;
                _axisUp = axisUp;
                _worldHeight = worldHeight;
            }

            /// <summary>Projects a world point into this view's metres.</summary>
            public Vector2 Project(Vector3 world) => new Vector2(
                Vector3.Dot(world - _origin, _axisAlong),
                // The side view keeps ABSOLUTE world height, because the draft is a world plane and a
                // pixel row in the preview has to map straight back to a world Y.
                _worldHeight ? world.y : Vector3.Dot(world - _origin, _axisUp));

            /// <summary>Projects a world DIRECTION - no origin, so it stays a direction.</summary>
            public Vector2 ProjectDirection(Vector3 world) => new Vector2(
                Vector3.Dot(world, _axisAlong),
                _worldHeight ? world.y : Vector3.Dot(world, _axisUp));

            internal static Frame For(Transform hullRoot, View view)
            {
                Vector3 along = HorizontalForward(hullRoot);
                // Right-hand across-axis, so the plan view reads bow-right with port at the top - the same
                // way a deck plan is drawn.
                Vector3 across = Vector3.Cross(Vector3.up, along);
                return view == View.Side
                    ? new Frame(hullRoot.position, along, Vector3.up, worldHeight: true)
                    : new Frame(hullRoot.position, along, across, worldHeight: false);
            }
        }

        // The hull's length direction. Flattened to horizontal because the side elevation's vertical axis
        // is world Y: leaving pitch in would tilt the whole drawing away from the draft plane it is read
        // against, and the plan view wants the horizontal length either way.
        static Vector3 HorizontalForward(Transform root)
        {
            Vector3 forward = root.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > MinForwardLength * MinForwardLength ? forward.normalized : Vector3.forward;
        }

        static void AppendFiltersUnder(Transform root, in Frame frame, List<Vector2> into)
        {
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
                AppendFilter(filter, frame, into);
        }

        // Reads sharedMesh.vertices directly. This is editor-only code and the failure is handled by the
        // caller through an empty list, so a model with Read/Write Enabled off degrades to a named
        // warning rather than an exception.
        static void AppendFilter(MeshFilter filter, in Frame frame, List<Vector2> into)
        {
            if (filter == null || filter.sharedMesh == null) return;

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3[] vertices = filter.sharedMesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                into.Add(frame.Project(toWorld.MultiplyPoint3x4(vertices[i])));
        }

        static Rect BoundsOf(Vector2[] outline)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < outline.Length; i++)
            {
                min = Vector2.Min(min, outline[i]);
                max = Vector2.Max(max, outline[i]);
            }
            return new Rect(min, max - min);
        }

        // ---- convex outline (monotone chain) ---------------------------------------------------

        // Andrew's monotone chain: sort by x then y, then sweep a lower and an upper chain, each
        // discarding any point that does not turn counter-clockwise. O(n log n) on the sort and linear
        // after - a 60k-vertex hull resolves instantly at edit time, so the vertices go in raw rather
        // than being welded to a tolerance grid first.
        static Vector2[] BuildConvexOutline(List<Vector2> points)
        {
            points.Sort(CompareByXThenY);

            var chain = new Vector2[points.Count * 2];
            int count = 0;

            // Lower chain: nothing below it yet, so it may be popped back to its first two points.
            for (int i = 0; i < points.Count; i++)
                count = PushKeepingLeftTurns(chain, count, points[i], floor: LowerChainFloor);

            // Upper chain: the finished lower chain is frozen underneath it, hence the raised floor.
            int upperFloor = count + 1;
            for (int i = points.Count - 2; i >= 0; i--)
                count = PushKeepingLeftTurns(chain, count, points[i], floor: upperFloor);

            // The last point of each chain is the first point of the other, so one duplicate is dropped.
            var outline = new Vector2[Mathf.Max(0, count - 1)];
            System.Array.Copy(chain, outline, outline.Length);
            return outline;
        }

        // Pops while the last two kept points and the candidate do not turn counter-clockwise, so
        // collinear and reflex points never reach the outline.
        static int PushKeepingLeftTurns(Vector2[] chain, int count, Vector2 candidate, int floor)
        {
            while (count >= floor && Cross(chain[count - 2], chain[count - 1], candidate) <= CollinearEpsilonSqMeters)
                count--;
            chain[count++] = candidate;
            return count;
        }

        static float Cross(Vector2 origin, Vector2 a, Vector2 b)
            => (a.x - origin.x) * (b.y - origin.y) - (a.y - origin.y) * (b.x - origin.x);

        static int CompareByXThenY(Vector2 a, Vector2 b)
        {
            int byX = a.x.CompareTo(b.x);
            return byX != 0 ? byX : a.y.CompareTo(b.y);
        }
    }
}
