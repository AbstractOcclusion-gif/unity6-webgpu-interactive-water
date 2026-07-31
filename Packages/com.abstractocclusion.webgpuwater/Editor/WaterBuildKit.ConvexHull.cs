// WebGpuWater build kit - convex hull approximation for the dry-interior carve.
// Quickhull over the hull model's combined render vertices, in the visual root's local frame.
// Editor-only and one-shot at create time, so the priorities are robustness over speed:
// welded input cloud, relative epsilon, and every degenerate case returns null so the caller
// falls back to the fitted box LOUDLY instead of carving with a broken hull.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // Weld grid for the input cloud: coincident/near-coincident vertices collapse so the
        // hull works from unique positions (a dense model drops to a few thousand candidates).
        const float ConvexWeldGridMeters = 0.005f;
        // Face-distance epsilon as a fraction of the cloud's bounds diagonal: a point within
        // this of a face is ON it and never spawns a new face - what terminates quickhull.
        const float ConvexEpsilonFraction = 1e-4f;
        // Absolute floor for a face's cross-product square: below this the three points are
        // colinear at the weld grid's own scale and the face is degenerate.
        const float DegenerateFaceSqr = 1e-12f;
        const string ConvexHullSuffix = "_ConvexHull";

        /// <summary>Convex hull of every render vertex under <paramref name="visualRoot"/>, as
        /// a mesh in the root's local frame - or null when the cloud is degenerate (fewer than
        /// four non-coplanar points). The caller owns the returned mesh's lifetime; it is
        /// SCRATCH for the dry-interior normalisation, never itself saved.</summary>
        internal static Mesh BuildConvexHullMesh(Transform visualRoot, string baseName)
        {
            List<Vector3> points = CollectWeldedLocalVertices(visualRoot);
            if (points.Count < 4) return null;
            List<int> tris = QuickHull(points);
            if (tris == null) return null;

            // Compact to the referenced vertices only (the hull touches a tiny subset).
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var outTris = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                if (!remap.TryGetValue(tris[i], out int idx))
                {
                    idx = verts.Count;
                    verts.Add(points[tris[i]]);
                    remap.Add(tris[i], idx);
                }
                outTris[i] = idx;
            }
            var mesh = new Mesh { name = baseName + ConvexHullSuffix };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.triangles = outTris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static List<Vector3> CollectWeldedLocalVertices(Transform visualRoot)
        {
            var seen = new HashSet<Vector3Int>();
            var points = new List<Vector3>();
            Matrix4x4 toRoot = visualRoot.worldToLocalMatrix;
            foreach (MeshFilter filter in visualRoot.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null) continue;
                Matrix4x4 toLocal = toRoot * filter.transform.localToWorldMatrix;
                foreach (Vector3 v in filter.sharedMesh.vertices)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(v);
                    var cell = new Vector3Int(
                        Mathf.RoundToInt(p.x / ConvexWeldGridMeters),
                        Mathf.RoundToInt(p.y / ConvexWeldGridMeters),
                        Mathf.RoundToInt(p.z / ConvexWeldGridMeters));
                    if (seen.Add(cell)) points.Add(p);
                }
            }
            return points;
        }

        sealed class HullFace
        {
            public int A, B, C;
            public Vector3 Normal;                        // unit, outward
            public float PlaneD;                          // dot(Normal, vertex A)
            public List<int> Outside = new List<int>();   // points strictly outside this face
            public bool Alive = true;
            public float Dist(Vector3 p) => Vector3.Dot(Normal, p) - PlaneD;
        }

        // Face (a,b,c) wound so its normal points AWAY from a point known to be inside the
        // hull - the initial simplex centroid stays inside forever (hulls only grow outward),
        // so one reference point orients every face the algorithm ever makes.
        static HullFace MakeFace(List<Vector3> pts, int a, int b, int c, Vector3 inside)
        {
            Vector3 normal = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
            if (normal.sqrMagnitude < DegenerateFaceSqr) return null;
            normal.Normalize();
            if (Vector3.Dot(normal, inside - pts[a]) > 0f) { (b, c) = (c, b); normal = -normal; }
            return new HullFace { A = a, B = b, C = c, Normal = normal, PlaneD = Vector3.Dot(normal, pts[a]) };
        }

        static float BoundsDiagonal(List<Vector3> pts)
        {
            Vector3 mn = pts[0], mx = pts[0];
            foreach (Vector3 p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            return (mx - mn).magnitude;
        }

        // The farthest pair among the six axis-extreme points seeds the hull; then the point
        // farthest from that line, then the point farthest from that plane. Any stage failing
        // its epsilon means the cloud is degenerate (a point/segment/plate) - return false.
        static bool InitialSimplex(List<Vector3> pts, float eps,
                                   out int i0, out int i1, out int i2, out int i3)
        {
            i0 = i1 = i2 = i3 = -1;
            int[] ext = { 0, 0, 0, 0, 0, 0 };
            for (int p = 1; p < pts.Count; p++)
            {
                if (pts[p].x < pts[ext[0]].x) ext[0] = p;
                if (pts[p].x > pts[ext[1]].x) ext[1] = p;
                if (pts[p].y < pts[ext[2]].y) ext[2] = p;
                if (pts[p].y > pts[ext[3]].y) ext[3] = p;
                if (pts[p].z < pts[ext[4]].z) ext[4] = p;
                if (pts[p].z > pts[ext[5]].z) ext[5] = p;
            }
            float bestSq = 0f;
            for (int a = 0; a < 6; a++)
                for (int b = a + 1; b < 6; b++)
                {
                    float dSq = (pts[ext[a]] - pts[ext[b]]).sqrMagnitude;
                    if (dSq > bestSq) { bestSq = dSq; i0 = ext[a]; i1 = ext[b]; }
                }
            if (bestSq < eps * eps) return false;

            Vector3 dir = (pts[i1] - pts[i0]).normalized;
            float bestLine = eps;
            for (int p = 0; p < pts.Count; p++)
            {
                Vector3 rel = pts[p] - pts[i0];
                float d = (rel - Vector3.Dot(rel, dir) * dir).magnitude;
                if (d > bestLine) { bestLine = d; i2 = p; }
            }
            if (i2 < 0) return false;

            Vector3 n = Vector3.Cross(pts[i1] - pts[i0], pts[i2] - pts[i0]).normalized;
            float bestPlane = eps;
            for (int p = 0; p < pts.Count; p++)
            {
                float d = Mathf.Abs(Vector3.Dot(pts[p] - pts[i0], n));
                if (d > bestPlane) { bestPlane = d; i3 = p; }
            }
            return i3 >= 0;
        }

        static List<int> QuickHull(List<Vector3> pts)
        {
            float eps = Mathf.Max(BoundsDiagonal(pts) * ConvexEpsilonFraction, 1e-6f);
            if (!InitialSimplex(pts, eps, out int i0, out int i1, out int i2, out int i3)) return null;

            Vector3 inside = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25f;
            var faces = new List<HullFace>
            {
                MakeFace(pts, i0, i1, i2, inside), MakeFace(pts, i0, i1, i3, inside),
                MakeFace(pts, i0, i2, i3, inside), MakeFace(pts, i1, i2, i3, inside),
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
                float best = eps;
                foreach (int p in work.Outside)
                {
                    float d = work.Dist(pts[p]);
                    if (d > best) { best = d; apex = p; }
                }
                if (apex < 0) { work.Outside.Clear(); continue; }

                var visible = new List<HullFace>();
                var orphans = new List<int>();
                foreach (HullFace f in faces)
                {
                    if (!f.Alive || f.Dist(pts[apex]) <= eps) continue;
                    f.Alive = false;
                    visible.Add(f);
                    orphans.AddRange(f.Outside);
                }

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
                    HullFace made = MakeFace(pts, a, b, apex, inside);
                    if (made == null) return null;           // degenerate horizon: box fallback
                    fresh.Add(made);
                }

                foreach (int p in orphans)
                {
                    if (p == apex) continue;
                    foreach (HullFace f in fresh)
                        if (f.Dist(pts[p]) > eps) { f.Outside.Add(p); break; }
                }
                faces.AddRange(fresh);
            }

            var tris = new List<int>();
            foreach (HullFace f in faces)
                if (f.Alive) { tris.Add(f.A); tris.Add(f.B); tris.Add(f.C); }
            return tris.Count >= 12 ? tris : null; // a closed hull is at least a tetrahedron
        }
    }
}
