// WebGpuWater - procedural water grid builder.
// Runtime (not editor-only) because it serves two callers: the editor build kit bakes
// the authored grid asset from it, and the Low quality tier rebuilds a coarser grid at
// startup on weak devices - the vertex shader runs 4 texture fetches plus the wave-bank
// sines PER VERTEX, so grid density is a first-order cost on mobile GPUs.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterMeshBuilder
    {
        // Generated meshes keep huge bounds so Unity's renderer culling can never wrongly
        // cull a surface placed by the volume frame; real frustum culling is
        // WaterVolume.CullBounds.
        internal const float HugeBoundsSize = 1000f;

        // XY-plane grid in [-1,1], z = 0 (matches the original lightgl plane mesh).
        internal static Mesh BuildGrid(int detail)
        {
            if (detail < 1) throw new System.ArgumentException($"Grid detail must be >= 1, got {detail}.", nameof(detail));

            int n = detail + 1;
            var verts = new Vector3[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    verts[i * n + j] = new Vector3(i / (float)detail * 2f - 1f, j / (float)detail * 2f - 1f, 0f);

            var tris = new int[detail * detail * 6];
            int t = 0;
            for (int i = 0; i < detail; i++)
                for (int j = 0; j < detail; j++)
                {
                    int a = i * n + j;
                    int b = (i + 1) * n + j;
                    int c = i * n + (j + 1);
                    int d = (i + 1) * n + (j + 1);
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }

            var mesh = new Mesh { name = "WaterGrid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeBoundsSize);
            return mesh;
        }

        // XY-plane DISC in the unit circle (radius 1), z = 0 - the SAME convention as BuildGrid, so
        // the volume frame and the surface shader place and wave-displace it exactly like the square
        // grid; it just happens to be round. The footprint of a SPHERE chunk's flat water surface.
        //
        // Built as an (i = ring, j = segment) polar grid with j wrapping, using BuildGrid's exact
        // triangle pattern so the winding matches by construction. Ring 0's vertices all coincide at
        // the centre, so its cells collapse to the centre fan through zero-area (degenerate) tris -
        // no special-casing, and no seam.
        internal static Mesh BuildDisc(int radialDetail, int angularSegments)
        {
            if (radialDetail < 1)
                throw new System.ArgumentException($"Disc radial detail must be >= 1, got {radialDetail}.", nameof(radialDetail));
            if (angularSegments < 3)
                throw new System.ArgumentException($"Disc angular segments must be >= 3, got {angularSegments}.", nameof(angularSegments));

            int ringCount = radialDetail + 1; // ring 0 is the duplicated centre
            var verts = new Vector3[ringCount * angularSegments];
            for (int r = 0; r < ringCount; r++)
            {
                float radius = r / (float)radialDetail; // 0 at the centre ring, 1 at the rim
                for (int s = 0; s < angularSegments; s++)
                {
                    float angle = s / (float)angularSegments * (2f * Mathf.PI);
                    verts[r * angularSegments + s] =
                        new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                }
            }

            var tris = new int[radialDetail * angularSegments * 6];
            int t = 0;
            for (int r = 0; r < radialDetail; r++)
                for (int s = 0; s < angularSegments; s++)
                {
                    int sNext = (s + 1) % angularSegments; // wrap the last segment back to the first
                    int a = r * angularSegments + s;
                    int b = (r + 1) * angularSegments + s;
                    int c = r * angularSegments + sNext;
                    int d = (r + 1) * angularSegments + sNext;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }

            var mesh = new Mesh { name = "WaterDisc", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeBoundsSize);
            return mesh;
        }

        // Unit sphere of radius 0.5: the SPHERE exclusion volume's water-wall mesh, drawn with the
        // volume's shape-to-world matrix (which turns it into an ellipsoid under a non-uniform
        // size). It is the CUBE's inscribed ball, exactly like the shader's primitive pair, so the
        // drawn boundary and the carved boundary are the same surface. Positions only - the wall
        // shader derives its normal analytically from the fragment's own object-space position
        // (a per-vertex normal would face the FACET, not the sphere) and renders Cull Off.
        // A UV sphere rather than an icosphere: the pole seam costs nothing here (no UVs, no
        // normals, no lighting seam) and the ring/segment counts stay readable knobs.
        internal static Mesh BuildUnitSphere()
        {
            const float Radius = 0.5f;
            // Enough that the SILHOUETTE reads round at close range - the rim is what the edge
            // occlusion shades, so a coarse outline would be visible exactly where it matters.
            const int RingCount = 24;    // latitude divisions, pole to pole
            const int SegmentCount = 32; // longitude divisions around the equator

            int vertexCount = (RingCount + 1) * (SegmentCount + 1);
            var verts = new Vector3[vertexCount];
            for (int ring = 0; ring <= RingCount; ring++)
            {
                float polar = Mathf.PI * ring / RingCount;
                float sinPolar = Mathf.Sin(polar);
                float cosPolar = Mathf.Cos(polar);
                for (int segment = 0; segment <= SegmentCount; segment++)
                {
                    float azimuth = 2f * Mathf.PI * segment / SegmentCount;
                    verts[ring * (SegmentCount + 1) + segment] = new Vector3(
                        Radius * sinPolar * Mathf.Cos(azimuth),
                        Radius * cosPolar,
                        Radius * sinPolar * Mathf.Sin(azimuth));
                }
            }

            var tris = new int[RingCount * SegmentCount * 6];
            int index = 0;
            for (int ring = 0; ring < RingCount; ring++)
            {
                for (int segment = 0; segment < SegmentCount; segment++)
                {
                    int current = ring * (SegmentCount + 1) + segment;
                    int below = current + SegmentCount + 1;
                    tris[index++] = current;
                    tris[index++] = below;
                    tris[index++] = current + 1;
                    tris[index++] = current + 1;
                    tris[index++] = below;
                    tris[index++] = below + 1;
                }
            }

            var mesh = new Mesh { name = "WaterExclusionWallSphere" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return mesh;
        }

        // Unit cube spanning [-0.5, 0.5] per axis: the exclusion volume's water-wall mesh, drawn
        // with the volume's box-to-world matrix. Positions only (8 verts / 12 tris) - the wall
        // shader shades from world position and renders Cull Off, so normals/uvs/winding senses
        // are irrelevant. Unit bounds: DrawMesh culls with the real box size via the matrix.
        internal static Mesh BuildUnitCube()
        {
            const float Half = 0.5f;
            var verts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                verts[i] = new Vector3((i & 1) != 0 ? Half : -Half,
                                       (i & 2) != 0 ? Half : -Half,
                                       (i & 4) != 0 ? Half : -Half);
            // Two triangles per face; vertex index bits are (x, y, z).
            int[] tris =
            {
                0, 2, 1,  2, 3, 1, // -z
                4, 5, 6,  5, 7, 6, // +z
                0, 4, 2,  4, 6, 2, // -x
                1, 3, 5,  3, 7, 5, // +x
                0, 1, 4,  1, 5, 4, // -y
                2, 6, 3,  6, 7, 3, // +y
            };
            var mesh = new Mesh { name = "WaterExclusionWallCube" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return mesh;
        }

        // POOL-space box spanning [-1, 1] per axis: the chunk shell's bounding proxy, placed by the
        // volume frame in the shader (PoolToWorld), so its transform stays identity. Huge bounds like
        // the surface grid, because the frame - not the transform - is where it really sits, and unit
        // bounds at the origin would frustum-cull it whenever the origin is off-screen. Cull Front +
        // world-position shading make winding irrelevant.
        internal static Mesh BuildChunkShellBox()
        {
            const float Edge = 1f;
            var verts = new Vector3[8];
            for (int i = 0; i < 8; i++)
                verts[i] = new Vector3((i & 1) != 0 ? Edge : -Edge,
                                       (i & 2) != 0 ? Edge : -Edge,
                                       (i & 4) != 0 ? Edge : -Edge);
            int[] tris =
            {
                0, 2, 1,  2, 3, 1, // -z
                4, 5, 6,  5, 7, 6, // +z
                0, 4, 2,  4, 6, 2, // -x
                1, 3, 5,  3, 7, 5, // +x
                0, 1, 4,  1, 5, 4, // -y
                2, 6, 3,  6, 7, 3, // +y
            };
            var mesh = new Mesh { name = "WaterChunkShellBox" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeBoundsSize);
            return mesh;
        }
    }
}
