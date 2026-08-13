// WebGpuWater - editor-only waterline cross-section of a hull, and the spray probes that ride it.
//
// A plane/triangle walk, deliberately NOT a convex hull: WaterBuildKit.BuildConvexHullMesh is 3D
// quickhull for the dry-interior carve and would smooth away a transom or a twin hull. This keeps
// concavity, and keeps a catamaran's two loops.
//
// The pipeline is: intersect every triangle with the draft plane -> weld the endpoints -> chain them
// into loops -> drop slivers and any loop enclosed by another (an open cockpit's rim, which would
// otherwise get probes spraying inboard) -> orient each loop counter-clockwise -> resample by ARC
// LENGTH so a dense bow does not crowd probes -> push each sample out along the loop's own normal.
//
// The outward normal comes from the loop's WINDING, never from its centroid: a concave outline can put
// its centroid outside the loop, which would flip the sign for a whole run of probes and spray them
// into the hull. With the winding fixed, no probe can face inward, so there is no inward-facing case
// to special-case afterwards.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    /// <summary>Slices a hull at a horizontal plane and lays spray probes around the resulting outline.</summary>
    internal static class WaterHullSlice
    {
        // A polygon needs three distinct points; below that there is no loop to walk.
        const int MinLoopNodes = 3;
        // A loop smaller than this share of the biggest loop's area is a sliver - a shard off a rudder
        // fin or a bolt head - not a hull. Relative, so it holds at any model scale.
        const float SliverAreaFraction = 0.02f;
        // Absolute floor for a loop's perimeter, so a fully degenerate loop cannot divide the arc-length
        // resampling by zero.
        const float MinLoopPerimeterMeters = 1e-4f;
        // Vertices exactly on the plane are pushed to the ABOVE side by this much, so every crossing
        // triangle yields exactly two crossing edges and the walk never has to special-case a grazing hit.
        const float PlaneTouchEpsilonMeters = 1e-6f;
        const int TriangleVertexCount = 3;
        // Half of a 64-bit key, so two 32-bit node indices pack into one long without colliding.
        const int NodeIndexBits = 32;

        /// <summary>One placed probe: where it sits on the draft plane, and which way it faces.</summary>
        internal readonly struct Probe
        {
            /// <summary>World position on the draft plane, already pushed out by the inset.</summary>
            public readonly Vector3 WorldPosition;

            /// <summary>Unit horizontal direction out of the hull, in world XZ. Carried for I3; unused here.</summary>
            public readonly Vector2 OutwardXZ;

            public Probe(Vector3 worldPosition, Vector2 outwardXZ)
            {
                WorldPosition = worldPosition;
                OutwardXZ = outwardXZ;
            }
        }

        /// <summary>The slice, or the reason there isn't one. Never throws - failure is a value.</summary>
        internal readonly struct Result
        {
            public readonly Probe[] Probes;

            /// <summary>Loops kept after the sliver and containment filters. Two is a catamaran, not a bug.</summary>
            public readonly int LoopCount;

            /// <summary>User-facing reason the slice failed; null on success.</summary>
            public readonly string Error;

            /// <summary>Something was repaired or discarded and the user should know; null when clean.</summary>
            public readonly string Warning;

            public bool Ok => Error == null && Probes != null;

            Result(Probe[] probes, int loopCount, string error, string warning)
            {
                Probes = probes;
                LoopCount = loopCount;
                Error = error;
                Warning = warning;
            }

            internal static Result Failed(string reason) => new Result(null, 0, reason, null);

            internal static Result From(Probe[] probes, int loopCount, string warning)
                => new Result(probes, loopCount, null, warning);
        }

        /// <summary>
        /// Slices <paramref name="hullObject"/> at world height <paramref name="draftY"/> and spreads
        /// <paramref name="probeCount"/> probes evenly by arc length around the result, each pushed
        /// <paramref name="insetMeters"/> clear of the plating. When <paramref name="hullFilter"/> is set
        /// only that mesh is sliced - a visual root's masts and crew are not part of the waterline.
        /// </summary>
        internal static Result Build(GameObject hullObject, MeshFilter hullFilter, float draftY,
                                     int probeCount, float insetMeters)
        {
            if (hullObject == null) return Result.Failed("No hull object to slice.");
            if (probeCount < 1) return Result.Failed("Probe count must be at least 1.");

            var nodes = new List<Vector2>();
            var edges = new List<Edge>();
            CollectSegments(hullObject.transform, hullFilter, draftY, nodes, edges);

            if (edges.Count == 0)
                return Result.Failed(
                    $"The draft plane misses '{hullObject.name}' entirely - no triangle crosses it. Drag the " +
                    "draft line onto the object, or point the Slice mesh field at the water-touching mesh.");

            List<List<int>> loops = ChainLoops(nodes, edges, out int openLoops, out float widestGap);
            loops = KeepHullLoops(nodes, loops);

            if (loops.Count == 0)
                return Result.Failed(
                    $"'{hullObject.name}' slices into no usable outline at this draft - only fragments too " +
                    "small to be a hull. Try a draft further from the very top or bottom of the mesh.");

            Probe[] probes = PlaceProbes(nodes, loops, draftY, probeCount, insetMeters);
            return Result.From(probes, loops.Count, DescribeRepairs(openLoops, widestGap));
        }

        static string DescribeRepairs(int openLoops, float widestGap)
            => openLoops == 0
                ? null
                : $"{openLoops} outline(s) came back open and were closed across the gap (widest {widestGap:0.###} m). " +
                  "That usually means the hull mesh is not watertight at this height; check the probes near the seam.";

        // ---- 1. triangles -> plane segments ----------------------------------------------------

        static void CollectSegments(Transform root, MeshFilter hullFilter, float draftY,
                                    List<Vector2> nodes, List<Edge> edges)
        {
            var weld = new Dictionary<Vector2Int, int>();
            // Two triangles that share an edge lying in the plane emit the SAME segment twice; a doubled
            // edge would send the walk straight back down the outline it just came up.
            var seen = new HashSet<long>();
            if (hullFilter != null)
            {
                AppendFilterSegments(hullFilter, draftY, weld, seen, nodes, edges);
                return;
            }
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>())
                AppendFilterSegments(filter, draftY, weld, seen, nodes, edges);
        }

        static void AppendFilterSegments(MeshFilter filter, float draftY, Dictionary<Vector2Int, int> weld,
                                         HashSet<long> seen, List<Vector2> nodes, List<Edge> edges)
        {
            if (filter == null || filter.sharedMesh == null) return;

            Matrix4x4 toWorld = filter.transform.localToWorldMatrix;
            Vector3[] vertices = filter.sharedMesh.vertices;
            int[] triangles = filter.sharedMesh.triangles;

            var corner = new Vector3[TriangleVertexCount];
            for (int i = 0; i + TriangleVertexCount <= triangles.Length; i += TriangleVertexCount)
            {
                for (int c = 0; c < TriangleVertexCount; c++)
                    corner[c] = toWorld.MultiplyPoint3x4(vertices[triangles[i + c]]);

                if (TryCrossPlane(corner, draftY, out Vector2 a, out Vector2 b))
                    AddEdge(weld, seen, nodes, edges, a, b);
            }
        }

        // A triangle crossing a horizontal plane crosses exactly two of its edges, once vertices sitting
        // exactly ON the plane are nudged to one side - which is what the epsilon buys. The crossings are
        // counted rather than assumed, so a triangle that somehow yields one or three is dropped instead
        // of shipping a segment with a default-valued endpoint.
        static bool TryCrossPlane(Vector3[] corner, float draftY, out Vector2 a, out Vector2 b)
        {
            a = default;
            b = default;

            float h0 = SignedHeight(corner[0], draftY);
            float h1 = SignedHeight(corner[1], draftY);
            float h2 = SignedHeight(corner[2], draftY);

            int found = 0;
            found = TryCrossEdge(corner[0], corner[1], h0, h1, ref a, ref b, found);
            found = TryCrossEdge(corner[1], corner[2], h1, h2, ref a, ref b, found);
            found = TryCrossEdge(corner[2], corner[0], h2, h0, ref a, ref b, found);
            return found == 2 && a != b;
        }

        static float SignedHeight(Vector3 world, float draftY)
        {
            float height = world.y - draftY;
            return Mathf.Abs(height) < PlaneTouchEpsilonMeters ? PlaneTouchEpsilonMeters : height;
        }

        static int TryCrossEdge(Vector3 from, Vector3 to, float hFrom, float hTo,
                                ref Vector2 a, ref Vector2 b, int found)
        {
            if ((hFrom > 0f) == (hTo > 0f)) return found;

            Vector3 crossing = Vector3.Lerp(from, to, hFrom / (hFrom - hTo));
            var flat = new Vector2(crossing.x, crossing.z);
            if (found == 0) a = flat;
            else if (found == 1) b = flat;
            return found + 1;
        }

        // ---- 2. weld + edge list ----------------------------------------------------------------

        readonly struct Edge
        {
            public readonly int A;
            public readonly int B;

            public Edge(int a, int b)
            {
                A = a;
                B = b;
            }
        }

        static void AddEdge(Dictionary<Vector2Int, int> weld, HashSet<long> seen, List<Vector2> nodes,
                            List<Edge> edges, Vector2 a, Vector2 b)
        {
            int nodeA = WeldNode(weld, nodes, a);
            int nodeB = WeldNode(weld, nodes, b);
            if (nodeA == nodeB) return; // collapsed to a point at the weld tolerance: no segment
            if (!seen.Add(UndirectedKey(nodeA, nodeB))) return;
            edges.Add(new Edge(nodeA, nodeB));
        }

        // Order-independent identity for a node pair, so a->b and b->a are one edge.
        static long UndirectedKey(int a, int b)
        {
            int low = Mathf.Min(a, b);
            int high = Mathf.Max(a, b);
            return ((long)low << NodeIndexBits) | (uint)high;
        }

        // Coincident endpoints from adjacent triangles must become ONE node or the chain never closes.
        // The tolerance is the package's existing weld grid, not a second copy of the same idea.
        static int WeldNode(Dictionary<Vector2Int, int> weld, List<Vector2> nodes, Vector2 point)
        {
            float grid = WaterBuildKit.ConvexWeldGridMeters;
            var cell = new Vector2Int(Mathf.RoundToInt(point.x / grid), Mathf.RoundToInt(point.y / grid));
            if (weld.TryGetValue(cell, out int existing)) return existing;

            weld.Add(cell, nodes.Count);
            nodes.Add(point);
            return nodes.Count - 1;
        }

        // ---- 3. chain into loops -----------------------------------------------------------------

        static List<List<int>> ChainLoops(List<Vector2> nodes, List<Edge> edges,
                                          out int openLoops, out float widestGap)
        {
            List<int>[] incident = BuildIncidence(nodes.Count, edges);
            var used = new bool[edges.Count];
            var loops = new List<List<int>>();
            openLoops = 0;
            widestGap = 0f;

            for (int seed = 0; seed < edges.Count; seed++)
            {
                if (used[seed]) continue;

                List<int> loop = WalkLoop(edges, incident, used, seed, out bool closed);
                if (loop.Count < MinLoopNodes) continue;

                if (!closed)
                {
                    openLoops++;
                    widestGap = Mathf.Max(widestGap, Vector2.Distance(nodes[loop[0]], nodes[loop[loop.Count - 1]]));
                }
                loops.Add(loop);
            }
            return loops;
        }

        static List<int>[] BuildIncidence(int nodeCount, List<Edge> edges)
        {
            var incident = new List<int>[nodeCount];
            for (int i = 0; i < nodeCount; i++) incident[i] = new List<int>();
            for (int e = 0; e < edges.Count; e++)
            {
                incident[edges[e].A].Add(e);
                incident[edges[e].B].Add(e);
            }
            return incident;
        }

        // Follows unused edges from the seed until the walk returns to its start (closed) or runs out
        // (open, and the caller closes it across the gap). A node with more than two edges - a T-junction
        // welded out of non-manifold geometry - takes the first unused branch rather than failing.
        static List<int> WalkLoop(List<Edge> edges, List<int>[] incident, bool[] used, int seed, out bool closed)
        {
            Edge start = edges[seed];
            used[seed] = true;

            var loop = new List<int> { start.A, start.B };
            int current = start.B;
            closed = false;

            while (true)
            {
                int next = NextUnusedEdge(incident[current], used);
                if (next < 0) break;

                used[next] = true;
                current = OtherEnd(edges[next], current);
                if (current == start.A)
                {
                    closed = true;
                    break;
                }
                loop.Add(current);
            }
            return loop;
        }

        static int NextUnusedEdge(List<int> candidates, bool[] used)
        {
            for (int i = 0; i < candidates.Count; i++)
                if (!used[candidates[i]]) return candidates[i];
            return -1;
        }

        static int OtherEnd(Edge edge, int from) => edge.A == from ? edge.B : edge.A;

        // ---- 4. keep the loops that are hull, oriented counter-clockwise --------------------------

        static List<List<int>> KeepHullLoops(List<Vector2> nodes, List<List<int>> loops)
        {
            var areas = new float[loops.Count];
            float largest = 0f;
            for (int i = 0; i < loops.Count; i++)
            {
                areas[i] = Mathf.Abs(SignedArea(nodes, loops[i]));
                largest = Mathf.Max(largest, areas[i]);
            }

            var kept = new List<List<int>>();
            var keptAreas = new List<float>();
            for (int i = 0; i < loops.Count; i++)
            {
                if (areas[i] < largest * SliverAreaFraction) continue;
                kept.Add(loops[i]);
                keptAreas.Add(areas[i]);
            }

            var hull = new List<List<int>>();
            for (int i = 0; i < kept.Count; i++)
            {
                if (IsEnclosed(nodes, kept, keptAreas, i)) continue; // an open cockpit's rim, not the plating
                hull.Add(OrientCounterClockwise(nodes, kept[i]));
            }
            return hull;
        }

        // A loop strictly inside a LARGER kept loop is a hole. Comparing areas first means two loops that
        // merely overlap in their bounding boxes - a catamaran's hulls - can never eliminate each other.
        static bool IsEnclosed(List<Vector2> nodes, List<List<int>> loops, List<float> areas, int index)
        {
            Vector2 probe = nodes[loops[index][0]];
            for (int other = 0; other < loops.Count; other++)
            {
                if (other == index || areas[other] <= areas[index]) continue;
                if (ContainsPoint(nodes, loops[other], probe)) return true;
            }
            return false;
        }

        // Ray casting along +x: a point is inside when it crosses the outline an odd number of times.
        static bool ContainsPoint(List<Vector2> nodes, List<int> loop, Vector2 point)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                Vector2 a = nodes[loop[i]];
                Vector2 b = nodes[loop[j]];
                if (a.y > point.y == b.y > point.y) continue;
                float crossingX = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
                if (point.x < crossingX) inside = !inside;
            }
            return inside;
        }

        static List<int> OrientCounterClockwise(List<Vector2> nodes, List<int> loop)
        {
            if (SignedArea(nodes, loop) >= 0f) return loop;
            loop.Reverse();
            return loop;
        }

        // Shoelace. Positive for a counter-clockwise loop in these (x, z) coordinates.
        static float SignedArea(List<Vector2> nodes, List<int> loop)
        {
            float twiceArea = 0f;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                Vector2 a = nodes[loop[j]];
                Vector2 b = nodes[loop[i]];
                twiceArea += a.x * b.y - b.x * a.y;
            }
            return twiceArea * 0.5f;
        }

        // ---- 5. resample by arc length -----------------------------------------------------------

        static Probe[] PlaceProbes(List<Vector2> nodes, List<List<int>> loops, float draftY,
                                   int probeCount, float insetMeters)
        {
            var perimeters = new float[loops.Count];
            for (int i = 0; i < loops.Count; i++)
                perimeters[i] = Mathf.Max(MinLoopPerimeterMeters, Perimeter(nodes, loops[i]));

            int[] share = SharePerimeterProportionally(perimeters, probeCount);

            var probes = new List<Probe>(probeCount);
            for (int i = 0; i < loops.Count; i++)
                AppendLoopProbes(nodes, loops[i], perimeters[i], share[i], draftY, insetMeters, probes);
            return probes.ToArray();
        }

        // Largest-remainder allocation, so the probe count the user asked for is the count they get -
        // per-loop rounding would drift by one or two on a catamaran and quietly change the burst budget.
        // Every loop keeps at least one probe, so a hull slicing into MORE loops than the requested count
        // returns one probe per loop and overshoots. That is the honest outcome: silently dropping a whole
        // hull of a catamaran to hit a number would be worse than exceeding it by one.
        static int[] SharePerimeterProportionally(float[] perimeters, int total)
        {
            float sum = 0f;
            for (int i = 0; i < perimeters.Length; i++) sum += perimeters[i];

            var share = new int[perimeters.Length];
            var remainder = new float[perimeters.Length];
            int assigned = 0;
            for (int i = 0; i < perimeters.Length; i++)
            {
                float exact = total * perimeters[i] / sum;
                int floor = Mathf.FloorToInt(exact);
                share[i] = Mathf.Max(1, floor);
                remainder[i] = exact - floor;
                assigned += share[i];
            }

            while (assigned < total)
            {
                int grow = IndexOfLargest(remainder);
                share[grow]++;
                remainder[grow] = float.MinValue;
                assigned++;
            }
            while (assigned > total)
            {
                int shrink = IndexOfLargest(share);
                if (share[shrink] <= 1) break;
                share[shrink]--;
                assigned--;
            }
            return share;
        }

        static int IndexOfLargest<T>(T[] values) where T : System.IComparable<T>
        {
            int best = 0;
            for (int i = 1; i < values.Length; i++)
                if (values[i].CompareTo(values[best]) > 0) best = i;
            return best;
        }

        static float Perimeter(List<Vector2> nodes, List<int> loop)
        {
            float total = 0f;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
                total += Vector2.Distance(nodes[loop[j]], nodes[loop[i]]);
            return total;
        }

        // Even spacing along the outline itself, not along its index: a bow modelled with ten times the
        // triangles of a flat topside would otherwise take ten times the probes.
        static void AppendLoopProbes(List<Vector2> nodes, List<int> loop, float perimeter, int count,
                                     float draftY, float insetMeters, List<Probe> into)
        {
            float step = perimeter / count;
            int segment = 0;
            float segmentStart = 0f;
            float segmentLength = SegmentLength(nodes, loop, 0);

            for (int i = 0; i < count; i++)
            {
                float target = i * step;
                while (target > segmentStart + segmentLength && segment < loop.Count - 1)
                {
                    segmentStart += segmentLength;
                    segment++;
                    segmentLength = SegmentLength(nodes, loop, segment);
                }

                Vector2 from = nodes[loop[segment]];
                Vector2 to = nodes[loop[(segment + 1) % loop.Count]];
                float along = segmentLength > 0f ? (target - segmentStart) / segmentLength : 0f;
                Vector2 point = Vector2.Lerp(from, to, along);

                Vector2 tangent = (to - from).normalized;
                // Counter-clockwise loop: the interior lies to the LEFT of travel, so out is to the right.
                var outward = new Vector2(tangent.y, -tangent.x);
                Vector2 placed = point + outward * insetMeters;
                into.Add(new Probe(new Vector3(placed.x, draftY, placed.y), outward));
            }
        }

        static float SegmentLength(List<Vector2> nodes, List<int> loop, int index)
            => Vector2.Distance(nodes[loop[index]], nodes[loop[(index + 1) % loop.Count]]);
    }
}
