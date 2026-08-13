// WebGpuWater build kit - convex hull approximation for the dry-interior carve.
// Quickhull over the hull model's combined render vertices, in the visual root's local frame.
// Editor-only and one-shot at create time, so the priorities are robustness over speed:
// welded input cloud, DOUBLE-precision plane math, and a final check that the result really is
// a convex hull - every degenerate or non-convex outcome returns null so the caller falls back
// to the fitted box LOUDLY instead of carving with a broken hull.
//
// Why double precision: the input vertices are floats, and differences of floats - and pairwise
// products of those differences - are exact in a 53-bit double mantissa. Plane distances are
// therefore near-exact, which lets the visible-region walk use the honest d > 0 predicate. The
// previous float implementation thresholded visibility at a model-scale epsilon instead; on
// coplanar-heavy meshes (a boat's flat panels) that cut the horizon THROUGH the coplanar band,
// erected fresh faces with old vertices far outside them, and the crust silently folded - the
// ski-boat hull came back with 2.2x its true volume and inward-wound triangles that backface
// culling rendered as HOLES. Root-caused 2026-08-01 against a double-precision reference:
// the fold started at a single insertion (#56) and every later insertion compounded it.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // Weld grid for the input cloud: coincident/near-coincident vertices collapse so the
        // hull works from unique positions (a dense model drops to a few thousand candidates).
        // Internal because WaterHullSlice welds its plane-crossing endpoints on the same tolerance:
        // one weld grid for the package, not two that drift apart.
        internal const float ConvexWeldGridMeters = 0.005f;
        // Face-distance epsilon as a fraction of the cloud's bounds diagonal: a point within
        // this of a face is ON it and never spawns a new face - what terminates quickhull.
        // Visibility does NOT use it (see CollectVisible); only apex acceptance and validation do.
        const double ConvexEpsilonFraction = 1e-4;
        // Absolute floor for the epsilon so a centimetre-sized cloud still terminates.
        const double ConvexEpsilonFloorMeters = 1e-9;
        // Below this squared cross product the three points are colinear in double precision
        // and the face is degenerate.
        const double DegenerateFaceCrossSqr = 1e-24;
        // A returned hull must CONTAIN its input: no welded point may sit further above any face
        // than this many epsilons. Points inside the epsilon band are deliberately absorbed by
        // the build (they never enter an outside list), so a few epsilons of slack is honest;
        // the folds this check exists to catch measured in the THOUSANDS of epsilons.
        const double ContainmentSlackEpsilons = 8.0;
        // On validation failure the whole build retries once at a coarser epsilon - a larger
        // band swallows the near-coplanar clusters that defeated the finer pass. Two attempts:
        // if a 4x coarser hull still is not convex, the input deserves the loud box fallback.
        const double EpsilonRetryScale = 4.0;
        const int BuildAttempts = 2;
        const string ConvexHullSuffix = "_ConvexHull";
        // A tetrahedron is the smallest closed hull: 4 triangles, 12 indices.
        const int MinHullIndices = 12;
        // On a closed 2-manifold every edge is shared by exactly two faces.
        const int ManifoldEdgeUses = 2;

        /// <summary>Convex hull of every render vertex under <paramref name="visualRoot"/>, as
        /// a mesh in the root's local frame - or null when the cloud is degenerate or the build
        /// cannot produce a verified convex hull. The caller owns the returned mesh's lifetime;
        /// it is SCRATCH for the dry-interior normalisation, never itself saved.</summary>
        internal static Mesh BuildConvexHullMesh(Transform visualRoot, string baseName)
            => BuildHullFromPoints(CollectWeldedLocalVertices(visualRoot, null, false), baseName);

        /// <summary>Convex hull of every occurrence of <paramref name="sourceMesh"/> under
        /// <paramref name="visualRoot"/>, transformed into the root's local frame. This keeps a
        /// standalone dry interior aligned even when the chosen hull MeshFilter is nested under
        /// imported pivots. Null when the mesh is absent or the selected cloud is degenerate.</summary>
        internal static Mesh BuildConvexHullMesh(Transform visualRoot, Mesh sourceMesh, string baseName)
            => BuildHullFromPoints(CollectWeldedLocalVertices(visualRoot, sourceMesh, true), baseName);

        /// <summary>The convex hull of ONE mesh's own vertices, in that mesh's own space - the repair for
        /// a concave mesh handed to the dry-interior carve, which needs one front and one back face along
        /// every ray. Null when the cloud is degenerate, exactly as the Transform overload.</summary>
        internal static Mesh BuildConvexHullMesh(Mesh source, string baseName)
        {
            if (source == null) return null;
            var seen = new HashSet<Vector3Int>();
            var points = new List<Vector3>();
            WeldInto(source.vertices, Matrix4x4.identity, seen, points);
            return BuildHullFromPoints(points, baseName);
        }

        static Mesh BuildHullFromPoints(List<Vector3> points, string baseName)
        {
            if (points.Count < 4) return null;
            List<int> tris = QuickHull(points);
            if (tris == null) return null;

            // FLAT-SHADED output: every triangle gets its own three vertices. Sharing vertices
            // would make RecalculateNormals SMOOTH across the hull's sharp facets - interpolated
            // normals swing up to ~80 degrees off the true face normal and whole front-facing
            // triangles shade to black in the editor preview, which reads exactly like holes
            // (measured on the ski-boat hull: 27-47 of 241 front-facing triangles went dark).
            // The carve itself never reads normals - facing comes from winding - so the only cost
            // is vertex count: 3 per face on a few-hundred-face editor-time mesh.
            var verts = new List<Vector3>(tris.Count);
            var outTris = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                outTris[i] = verts.Count;
                verts.Add(points[tris[i]]);
            }
            var mesh = new Mesh { name = baseName + ConvexHullSuffix };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = outTris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static List<Vector3> CollectWeldedLocalVertices(Transform visualRoot, Mesh sourceMesh,
                                                        bool includeInactive)
        {
            var seen = new HashSet<Vector3Int>();
            var points = new List<Vector3>();
            Matrix4x4 toRoot = visualRoot.worldToLocalMatrix;
            foreach (MeshFilter filter in visualRoot.GetComponentsInChildren<MeshFilter>(includeInactive))
            {
                if (filter.sharedMesh == null) continue;
                if (sourceMesh != null && filter.sharedMesh != sourceMesh) continue;
                WeldInto(filter.sharedMesh.vertices, toRoot * filter.transform.localToWorldMatrix, seen, points);
            }
            return points;
        }

        // One weld, three callers (the visual-root hull, the single-mesh hull, the concavity measure) -
        // a second copy of the grid rounding would let them disagree about which points are distinct.
        static void WeldInto(Vector3[] source, Matrix4x4 transform, HashSet<Vector3Int> seen, List<Vector3> into)
        {
            foreach (Vector3 v in source)
            {
                Vector3 p = transform.MultiplyPoint3x4(v);
                var cell = new Vector3Int(
                    Mathf.RoundToInt(p.x / ConvexWeldGridMeters),
                    Mathf.RoundToInt(p.y / ConvexWeldGridMeters),
                    Mathf.RoundToInt(p.z / ConvexWeldGridMeters));
                if (seen.Add(cell)) into.Add(p);
            }
        }

        /// <summary>
        /// How far the deepest cavity of <paramref name="mesh"/> lies inside its own convex hull, in the
        /// mesh's units. Zero means convex; a boat's cockpit reads as its depth below the sheerline.
        /// </summary>
        /// <remarks>Every vertex of a CONVEX mesh sits ON its hull, so "distance inside the hull" IS the
        /// concavity - and it is the number that matters, because the carve breaks exactly where a ray
        /// meets a second surface between the deck and the bottom. Tested against the HULL's faces (a few
        /// hundred) rather than the mesh's own (tens of thousands), so it stays cheap on real models.
        /// False when the cloud is degenerate and no hull exists to measure against.</remarks>
        internal static bool TryMeasureConcavity(Mesh mesh, out float deepestMeters)
        {
            deepestMeters = 0f;
            if (mesh == null) return false;

            var seen = new HashSet<Vector3Int>();
            var points = new List<Vector3>();
            WeldInto(mesh.vertices, Matrix4x4.identity, seen, points);
            if (points.Count < 4) return false;

            List<int> tris = QuickHull(points);
            if (tris == null) return false;

            var pts = ToDouble(points);
            for (int i = 0; i < pts.Count; i++)
            {
                // Signed distance to the nearest hull face from outside: <= 0 everywhere means inside,
                // and the largest (least negative) value is how far this point sits below the surface.
                double closestFace = double.MinValue;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    DVec a = pts[tris[t]];
                    DVec normal = DVec.Cross(pts[tris[t + 1]] - a, pts[tris[t + 2]] - a);
                    if (normal.SqrMagnitude < DegenerateFaceCrossSqr) continue;
                    normal = normal.Normalized;
                    closestFace = System.Math.Max(closestFace, DVec.Dot(normal, pts[i] - a));
                }
                if (closestFace > double.MinValue)
                    deepestMeters = Mathf.Max(deepestMeters, (float)(-closestFace));
            }
            return true;
        }

        // Minimal double-precision vector for the hull's plane math. Unity's Vector3 stays at the
        // boundaries (weld input, mesh output); everything the algorithm DECIDES on runs in double.
        struct DVec
        {
            public double X, Y, Z;
            public DVec(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static DVec operator -(DVec a, DVec b) => new DVec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static double Dot(DVec a, DVec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static DVec Cross(DVec a, DVec b) => new DVec(
                a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            public double SqrMagnitude => X * X + Y * Y + Z * Z;
            public double Magnitude => System.Math.Sqrt(SqrMagnitude);
            public DVec Negated => new DVec(-X, -Y, -Z);
            public DVec Normalized { get { double m = Magnitude; return new DVec(X / m, Y / m, Z / m); } }
        }

        static List<DVec> ToDouble(List<Vector3> points)
        {
            var pts = new List<DVec>(points.Count);
            foreach (Vector3 p in points) pts.Add(new DVec(p.x, p.y, p.z));
            return pts;
        }

        sealed class HullFace
        {
            public int A, B, C;
            public DVec Normal;                           // unit, outward
            public double PlaneD;                         // dot(Normal, vertex A)
            public List<int> Outside = new List<int>();   // points strictly outside this face
            public bool Alive = true;
            public double Dist(DVec p) => DVec.Dot(Normal, p) - PlaneD;
        }

        static List<int> QuickHull(List<Vector3> points)
        {
            List<DVec> pts = ToDouble(points);
            double baseEps = System.Math.Max(BoundsDiagonal(pts) * ConvexEpsilonFraction,
                                             ConvexEpsilonFloorMeters);
            for (int attempt = 0; attempt < BuildAttempts; attempt++)
            {
                List<int> tris = BuildHull(pts, baseEps * System.Math.Pow(EpsilonRetryScale, attempt));
                if (tris != null) return tris;
            }
            return null;
        }

        static List<int> BuildHull(List<DVec> pts, double eps)
        {
            if (!InitialSimplex(pts, eps, out int i0, out int i1, out int i2, out int i3)) return null;

            var inside = new DVec(
                (pts[i0].X + pts[i1].X + pts[i2].X + pts[i3].X) * 0.25,
                (pts[i0].Y + pts[i1].Y + pts[i2].Y + pts[i3].Y) * 0.25,
                (pts[i0].Z + pts[i1].Z + pts[i2].Z + pts[i3].Z) * 0.25);
            var faces = new List<HullFace>
            {
                MakeSimplexFace(pts, i0, i1, i2, inside), MakeSimplexFace(pts, i0, i1, i3, inside),
                MakeSimplexFace(pts, i0, i2, i3, inside), MakeSimplexFace(pts, i1, i2, i3, inside),
            };
            if (faces.Contains(null)) return null;

            for (int p = 0; p < pts.Count; p++)
            {
                if (p == i0 || p == i1 || p == i2 || p == i3) continue;
                foreach (HullFace f in faces)
                    if (f.Dist(pts[p]) > eps) { f.Outside.Add(p); break; }
            }

            // Each point can be an apex at most once, so this bound is unreachable in a sane
            // run - it exists so a numerical pathology terminates in the box fallback, never
            // in a hang.
            int guard = pts.Count + 8;
            while (guard-- > 0)
            {
                HullFace work = null;
                foreach (HullFace f in faces)
                    if (f.Alive && f.Outside.Count > 0) { work = f; break; }
                if (work == null) break; // no face sees a point: the hull is complete

                int apex = -1;
                double best = eps;
                foreach (int p in work.Outside)
                {
                    double d = work.Dist(pts[p]);
                    if (d > best) { best = d; apex = p; }
                }
                if (apex < 0) { work.Outside.Clear(); continue; }

                var visible = new List<HullFace>();
                var orphans = new List<int>();
                CollectVisible(faces, pts, work, apex, visible, orphans);

                // Horizon = directed edges of visible faces whose reverse is NOT visible; the
                // directed winding hands each new face its outward orientation for free.
                var directed = new HashSet<(int, int)>();
                foreach (HullFace f in visible)
                {
                    directed.Add((f.A, f.B));
                    directed.Add((f.B, f.C));
                    directed.Add((f.C, f.A));
                }
                var fresh = new List<HullFace>();
                foreach ((int a, int b) in directed)
                {
                    if (directed.Contains((b, a))) continue; // interior edge between visible faces
                    HullFace made = MakeHorizonFace(pts, a, b, apex);
                    if (made == null) return null;           // degenerate horizon: box fallback
                    fresh.Add(made);
                }
                if (fresh.Count == 0) return null;           // visible region wrapped the crust

                faces.AddRange(fresh);

                // Re-home the dead faces' outside points against EVERY live face, not just the new
                // ones: numerically a point near the horizon can miss every fresh face by a hair,
                // and a dropped point is a point the hull never grows to reach.
                foreach (int p in orphans)
                {
                    if (p == apex) continue;
                    foreach (HullFace f in faces)
                        if (f.Alive && f.Dist(pts[p]) > eps) { f.Outside.Add(p); break; }
                }
            }
            if (guard < 0) return null;

            var tris = new List<int>();
            foreach (HullFace f in faces)
                if (f.Alive) { tris.Add(f.A); tris.Add(f.B); tris.Add(f.C); }
            if (tris.Count < MinHullIndices) return null; // a closed hull is at least a tetrahedron
            if (!IsClosedTriangulation(tris)) return null;
            return IsGeometricallyConvex(pts, tris, eps) ? tris : null;
        }

        // The apex's visible region, grown from the face that saw it by walking SHARED EDGES with
        // the strict d > 0 predicate.
        //
        // Growing across shared edges keeps the region connected and the horizon a single loop -
        // but that theorem only holds for the EXACT visibility set. Testing against a model-scale
        // epsilon here (as this used to) declares near-coplanar faces "not visible", the walk stops
        // at them, and the horizon cuts through a flat panel: the fresh faces then leave old crust
        // vertices strictly outside and the hull folds. In double precision on float input, d > 0
        // is reliable, so the walk uses it verbatim. The epsilon still governs which points are
        // worth becoming apexes at all - that is a termination question, not a visibility one.
        static void CollectVisible(List<HullFace> faces, List<DVec> pts, HullFace seed, int apex,
                                   List<HullFace> visible, List<int> orphans)
        {
            Dictionary<(int, int), List<HullFace>> byEdge = BuildEdgeMap(faces);

            var pending = new Stack<HullFace>();
            Consume(seed, visible, orphans, pending);

            while (pending.Count > 0)
            {
                HullFace face = pending.Pop();
                foreach ((int a, int b) in FaceEdges(face))
                {
                    if (!byEdge.TryGetValue(UndirectedEdge(a, b), out List<HullFace> shared)) continue;
                    foreach (HullFace neighbour in shared)
                        if (neighbour.Alive && neighbour.Dist(pts[apex]) > 0.0)
                            Consume(neighbour, visible, orphans, pending);
                }
            }
        }

        static void Consume(HullFace face, List<HullFace> visible, List<int> orphans, Stack<HullFace> pending)
        {
            face.Alive = false;
            visible.Add(face);
            orphans.AddRange(face.Outside);
            pending.Push(face);
        }

        // Face (a,b,c) wound so its normal points AWAY from a point known to be inside the hull.
        //
        // ONLY the initial simplex needs this: it is built from four loose points with no winding to
        // inherit. Every later face inherits its orientation from the horizon edge it is built on
        // (see MakeHorizonFace), which is exact.
        static HullFace MakeSimplexFace(List<DVec> pts, int a, int b, int c, DVec inside)
        {
            DVec normal = DVec.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            if (normal.SqrMagnitude < DegenerateFaceCrossSqr) return null;
            normal = normal.Normalized;
            if (DVec.Dot(normal, inside - pts[a]) > 0.0) { (b, c) = (c, b); normal = normal.Negated; }
            return new HullFace { A = a, B = b, C = c, Normal = normal, PlaneD = DVec.Dot(normal, pts[a]) };
        }

        // A face raised on a horizon edge, KEEPING the edge's direction. The horizon edge comes
        // from a face that was already correctly wound, so (a, b, apex) is correctly wound too -
        // and using it verbatim is what makes the new face agree with the neighbour still holding
        // the reverse edge. Re-deriving the orientation from a reference point would flip a coin
        // wherever that point lies near the new face's plane.
        static HullFace MakeHorizonFace(List<DVec> pts, int a, int b, int apex)
        {
            DVec normal = DVec.Cross(pts[b] - pts[a], pts[apex] - pts[a]);
            if (normal.SqrMagnitude < DegenerateFaceCrossSqr) return null;
            normal = normal.Normalized;
            return new HullFace { A = a, B = b, C = apex, Normal = normal, PlaneD = DVec.Dot(normal, pts[a]) };
        }

        static double BoundsDiagonal(List<DVec> pts)
        {
            DVec mn = pts[0], mx = pts[0];
            foreach (DVec p in pts)
            {
                mn = new DVec(System.Math.Min(mn.X, p.X), System.Math.Min(mn.Y, p.Y), System.Math.Min(mn.Z, p.Z));
                mx = new DVec(System.Math.Max(mx.X, p.X), System.Math.Max(mx.Y, p.Y), System.Math.Max(mx.Z, p.Z));
            }
            return (mx - mn).Magnitude;
        }

        // The farthest pair among the six axis-extreme points seeds the hull; then the point
        // farthest from that line, then the point farthest from that plane. Any stage failing
        // its epsilon means the cloud is degenerate (a point/segment/plate) - return false.
        static bool InitialSimplex(List<DVec> pts, double eps,
                                   out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = -1;
            int[] ext = { 0, 0, 0, 0, 0, 0 };
            for (int p = 1; p < pts.Count; p++)
            {
                if (pts[p].X < pts[ext[0]].X) ext[0] = p;
                if (pts[p].X > pts[ext[1]].X) ext[1] = p;
                if (pts[p].Y < pts[ext[2]].Y) ext[2] = p;
                if (pts[p].Y > pts[ext[3]].Y) ext[3] = p;
                if (pts[p].Z < pts[ext[4]].Z) ext[4] = p;
                if (pts[p].Z > pts[ext[5]].Z) ext[5] = p;
            }
            double bestSq = 0.0;
            for (int a = 0; a < 6; a++)
                for (int b = a + 1; b < 6; b++)
                {
                    double dSq = (pts[ext[a]] - pts[ext[b]]).SqrMagnitude;
                    if (dSq > bestSq) { bestSq = dSq; i0 = ext[a]; i1 = ext[b]; }
                }
            if (bestSq < eps * eps) return false;

            DVec dir = (pts[i1] - pts[i0]).Normalized;
            double bestLine = eps;
            for (int p = 0; p < pts.Count; p++)
            {
                DVec rel = pts[p] - pts[i0];
                double along = DVec.Dot(rel, dir);
                var off = new DVec(rel.X - along * dir.X, rel.Y - along * dir.Y, rel.Z - along * dir.Z);
                double d = off.Magnitude;
                if (d > bestLine) { bestLine = d; i2 = p; }
            }
            if (i2 < 0) return false;

            DVec n = DVec.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]).Normalized;
            double bestPlane = eps;
            for (int p = 0; p < pts.Count; p++)
            {
                double d = System.Math.Abs(DVec.Dot(pts[p] - pts[i0], n));
                if (d > bestPlane) { bestPlane = d; i3 = p; }
            }
            return i3 >= 0;
        }

        // Rebuilt per apex rather than maintained incrementally: the hull is a few hundred faces and
        // this runs once at author time, so the simpler thing that cannot go stale wins.
        static Dictionary<(int, int), List<HullFace>> BuildEdgeMap(List<HullFace> faces)
        {
            var byEdge = new Dictionary<(int, int), List<HullFace>>();
            foreach (HullFace f in faces)
            {
                if (!f.Alive) continue;
                foreach ((int a, int b) in FaceEdges(f))
                {
                    (int, int) key = UndirectedEdge(a, b);
                    if (!byEdge.TryGetValue(key, out List<HullFace> shared))
                        byEdge[key] = shared = new List<HullFace>();
                    shared.Add(f);
                }
            }
            return byEdge;
        }

        static (int, int)[] FaceEdges(HullFace f) => new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) };

        static (int, int) UndirectedEdge(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>
        /// Whether these triangles form a closed 2-manifold: every edge shared by exactly two
        /// faces, and Euler's relation for a triangulated sphere, F == 2(V - 2).
        /// </summary>
        static bool IsClosedTriangulation(List<int> tris)
        {
            var edgeUses = new Dictionary<(int, int), int>();
            var vertices = new HashSet<int>();
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                vertices.Add(tris[i]);
                vertices.Add(tris[i + 1]);
                vertices.Add(tris[i + 2]);
                CountEdge(edgeUses, tris[i], tris[i + 1]);
                CountEdge(edgeUses, tris[i + 1], tris[i + 2]);
                CountEdge(edgeUses, tris[i + 2], tris[i]);
            }

            foreach (int uses in edgeUses.Values)
                if (uses != ManifoldEdgeUses) return false;

            return tris.Count / 3 == 2 * (vertices.Count - 2);
        }

        // The check the topology test cannot make: the triangles must BE a convex hull, not merely
        // a closed surface. A folded, self-overlapping crust is combinatorially a perfect sphere -
        // the ski-boat failure passed every edge count and Euler's relation while carrying 2.2x the
        // true volume. Two geometric facts pin it down: (a) every winding normal points away from
        // the hull centroid, and (b) no input point sits meaningfully above any face plane.
        // O(faces x points) - fine for a one-shot editor build on a welded cloud.
        static bool IsGeometricallyConvex(List<DVec> pts, List<int> tris, double eps)
        {
            var used = new HashSet<int>(tris);
            var centroid = new DVec(0.0, 0.0, 0.0);
            foreach (int v in used)
                centroid = new DVec(centroid.X + pts[v].X, centroid.Y + pts[v].Y, centroid.Z + pts[v].Z);
            centroid = new DVec(centroid.X / used.Count, centroid.Y / used.Count, centroid.Z / used.Count);

            var planes = new List<(DVec normal, double planeD)>(tris.Count / 3);
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                DVec a = pts[tris[i]];
                DVec n = DVec.Cross(pts[tris[i + 1]] - a, pts[tris[i + 2]] - a);
                if (n.SqrMagnitude < DegenerateFaceCrossSqr) return false;
                n = n.Normalized;
                if (DVec.Dot(n, a - centroid) <= 0.0) return false; // inward-wound face
                planes.Add((n, DVec.Dot(n, a)));
            }

            double slack = ContainmentSlackEpsilons * eps;
            foreach (DVec p in pts)
                foreach ((DVec normal, double planeD) in planes)
                    if (DVec.Dot(normal, p) - planeD > slack) return false; // hull does not contain its input
            return true;
        }

        static void CountEdge(Dictionary<(int, int), int> edgeUses, int a, int b)
        {
            (int, int) key = UndirectedEdge(a, b);
            edgeUses.TryGetValue(key, out int uses);
            edgeUses[key] = uses + 1;
        }
    }
}
