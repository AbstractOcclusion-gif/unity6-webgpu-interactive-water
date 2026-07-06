// LargeWaterClipmap - camera-following open-water surface geometry (Phase 2).
//
// EXPERIMENTAL, opt-in: this whole file compiles only when the WEBGPUWATER_LARGE_BODY
// scripting define is set (Player Settings > Scripting Define Symbols). With the define
// absent it is inert, so it can never affect the shipped pool / small-body build.
//
// Technique (reused, not copied, from Crest's single-mesh clipmap and KWS's infinite
// ocean): instead of stretching one fixed grid over a whole ocean (which goes blocky),
// render a radial grid whose triangles grow geometrically with distance from the viewer,
// recentred on the camera each frame. Near the camera the mesh is dense (fine FFT waves
// resolve); far away triangles are large (only long swell remains, which is all the eye
// can see there). One continuous mesh means no LOD-ring seams to stitch - the trade vs a
// full tiled geometry-clipmap is slightly less even texel density, which reads fine for a
// first open-water pass and is far simpler to drive.
//
// The mesh is authored flat in the XZ plane (y = 0) in LOCAL space, centred on the origin.
// A driver places its Transform at the camera's XZ each frame; the surface shader adds FFT
// displacement per vertex in world space. Height is a pure function of world XZ, so the
// buoyancy sampler stays valid (see the analytic-spectrum bridge planned for Phase 3).
#if WEBGPUWATER_LARGE_BODY
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Builds the camera-following radial clipmap mesh for open-water bodies.</summary>
    internal static class LargeWaterClipmap
    {
        // Guard rails so a mis-authored inspector value fails loudly instead of producing a
        // degenerate (zero-area / NaN) mesh that the surface shader would then sample.
        const int MinRings = 2;
        const int MinSegments = 3;
        const float MinRadius = 1e-3f;

        // The mesh is recentred on the camera every frame, so it must never frustum-cull. A
        // deliberately huge local bounds keeps it drawn from any view angle (mirrors the
        // oversized bounds the small-body grid uses).
        const float HugeBoundsSize = 1_000_000f;

        /// <summary>
        /// Radial clipmap grid: <paramref name="rings"/> concentric rings of
        /// <paramref name="segments"/> vertices each, plus a centre vertex. Ring radii grow
        /// GEOMETRICALLY from <paramref name="innerRadius"/> to <paramref name="outerRadius"/>
        /// so triangle size scales with distance (roughly constant screen-space density).
        /// </summary>
        internal static Mesh BuildRadialGrid(int rings, int segments, float innerRadius, float outerRadius)
        {
            ValidateOrThrow(rings, segments, innerRadius, outerRadius);

            int vertexCount = 1 + rings * segments;                 // centre + one vertex per ring/segment
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];

            vertices[0] = Vector3.zero;                             // centre
            uvs[0] = new Vector2(0.5f, 0.5f);

            float radiusRatio = Mathf.Pow(outerRadius / innerRadius, 1f / (rings - 1));
            for (int ring = 0; ring < rings; ring++)
            {
                float radius = innerRadius * Mathf.Pow(radiusRatio, ring);
                WriteRing(vertices, uvs, ring, segments, radius, outerRadius);
            }

            int[] triangles = BuildTriangles(rings, segments);

            return Assemble(vertices, uvs, triangles);
        }

        // One ring of 'segments' vertices evenly spaced around a circle of the given radius.
        static void WriteRing(Vector3[] vertices, Vector2[] uvs, int ring, int segments, float radius, float outerRadius)
        {
            int ringStart = 1 + ring * segments;
            float angleStep = 2f * Mathf.PI / segments;
            for (int seg = 0; seg < segments; seg++)
            {
                float angle = seg * angleStep;
                float x = Mathf.Cos(angle) * radius;
                float z = Mathf.Sin(angle) * radius;
                int index = ringStart + seg;
                vertices[index] = new Vector3(x, 0f, z);
                // UV in [0,1] from local XZ (only used if a material wants a planar UV; the
                // surface shader derives everything it needs from world position instead).
                uvs[index] = new Vector2(x, z) * (0.5f / outerRadius) + new Vector2(0.5f, 0.5f);
            }
        }

        // Centre fan (centre -> ring 0) plus a quad strip between each pair of consecutive rings,
        // each quad split into two triangles. Wound clockwise so the surface faces +Y (up).
        static int[] BuildTriangles(int rings, int segments)
        {
            int fanTriangles = segments;
            int stripTriangles = (rings - 1) * segments * 2;
            var triangles = new int[(fanTriangles + stripTriangles) * 3];
            int t = 0;

            // Centre fan.
            for (int seg = 0; seg < segments; seg++)
            {
                int a = 1 + seg;
                int b = 1 + NextSeg(seg, segments);
                t = EmitTriangle(triangles, t, 0, b, a);
            }

            // Ring-to-ring strips.
            for (int ring = 0; ring < rings - 1; ring++)
            {
                int inner = 1 + ring * segments;
                int outer = 1 + (ring + 1) * segments;
                for (int seg = 0; seg < segments; seg++)
                {
                    int segNext = NextSeg(seg, segments);
                    int i0 = inner + seg;
                    int i1 = inner + segNext;
                    int o0 = outer + seg;
                    int o1 = outer + segNext;
                    t = EmitTriangle(triangles, t, i0, o1, o0);
                    t = EmitTriangle(triangles, t, i0, i1, o1);
                }
            }

            return triangles;
        }

        static int NextSeg(int seg, int segments) => (seg + 1) % segments;

        static int EmitTriangle(int[] triangles, int cursor, int a, int b, int c)
        {
            triangles[cursor] = a;
            triangles[cursor + 1] = b;
            triangles[cursor + 2] = c;
            return cursor + 3;
        }

        static Mesh Assemble(Vector3[] vertices, Vector2[] uvs, int[] triangles)
        {
            var mesh = new Mesh
            {
                name = "LargeWaterClipmap",
                // Ring * segment counts routinely exceed the 16-bit vertex limit.
                indexFormat = IndexFormat.UInt32,
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.SetNormals(BuildUpNormals(vertices.Length));
            // Never cull: the mesh follows the camera, so its authored-space bounds are huge.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeBoundsSize);
            return mesh;
        }

        static Vector3[] BuildUpNormals(int count)
        {
            var normals = new Vector3[count];
            for (int i = 0; i < count; i++) normals[i] = Vector3.up;
            return normals;
        }

        static void ValidateOrThrow(int rings, int segments, float innerRadius, float outerRadius)
        {
            if (rings < MinRings)
                throw new System.ArgumentOutOfRangeException(nameof(rings), rings, $"needs >= {MinRings} rings");
            if (segments < MinSegments)
                throw new System.ArgumentOutOfRangeException(nameof(segments), segments, $"needs >= {MinSegments} segments");
            if (innerRadius < MinRadius)
                throw new System.ArgumentOutOfRangeException(nameof(innerRadius), innerRadius, "must be positive");
            if (outerRadius <= innerRadius)
                throw new System.ArgumentOutOfRangeException(nameof(outerRadius), outerRadius, "must exceed innerRadius");
        }
    }
}
#endif // WEBGPUWATER_LARGE_BODY
