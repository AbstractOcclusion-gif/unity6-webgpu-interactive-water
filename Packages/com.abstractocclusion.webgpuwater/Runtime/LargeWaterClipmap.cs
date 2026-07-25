// LargeWaterClipmap - camera-following open-water surface geometry (world-locked geometry clipmap).
//
// Pure mesh builder (no side effects): always compiled, but only USED when a WaterVolume has
// Open Water + Unbounded Ocean enabled (see WaterVolume.IsOceanClipmap). Bounded lakes and pools
// never build one, so the shipped small-body build is unaffected.
//
// Technique (Losasso/Hoppe "Geometry Clipmaps"): the ocean is drawn as a set
// of NESTED SQUARE LOD levels. Each level is one shared UNIFORM square-annulus grid authored in
// INTEGER CELL UNITS; the driver scales it by that level's cell size and places it at a follow point
// SNAPPED to that level's own world lattice. Because a uniform grid snapped to its own cell always
// re-lands its vertices on a fixed world lattice, the wave field (a pure function of world XZ) is
// sampled at stable world points no matter how the camera moves - which is what kills the "geometry
// swim" the old radial ring mesh suffered (a ring-and-spoke mesh has no repeating lattice to snap to,
// so its vertices slid through the waves as the camera followed).
//
// The mesh here is authored flat in the XZ plane (y = 0), centred on the origin, in cell units
// (vertex xz are integer cell indices). Level L's transform scales by cellSize*2^L and the surface
// shader (a) offsets the vertex Y by the world-space wave height and (b) morphs the outermost band of
// each level onto the next-coarser lattice to stitch the levels without T-junction cracks.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Builds the shared uniform square-annulus grid used by every ocean clipmap LOD level.</summary>
    internal static class LargeWaterClipmap
    {
        // Guard rails so a mis-authored inspector value fails loudly instead of producing a
        // degenerate (zero-area / NaN) mesh that the surface shader would then sample.
        const int MinGridResolution = 8;

        // The mesh is recentred on the camera every frame, so it must never frustum-cull. A
        // deliberately huge local bounds keeps it drawn from any view angle (mirrors the
        // oversized bounds the small-body grid uses).
        const float HugeBoundsSize = 1_000_000f;

        /// <summary>
        /// Uniform square-annulus grid, authored in INTEGER CELL UNITS: vertices at (i, 0, j) for
        /// i,j in [-M/2, M/2], with the central square hole (|i| and |j| both within
        /// <paramref name="holeHalfCells"/>) left untriangulated. The hole is filled by the next-finer
        /// LOD level (or, for the innermost level, by the near-field patch). One template serves every
        /// level; levels differ only by the transform scale (cell size) and snapped position.
        /// </summary>
        internal static Mesh BuildAnnulusTemplate(int gridResolution, int holeHalfCells)
        {
            ValidateOrThrow(gridResolution, holeHalfCells);

            int half = gridResolution / 2;
            int stride = gridResolution + 1;                       // vertices per side
            var vertices = new Vector3[stride * stride];
            var uvs = new Vector2[stride * stride];

            for (int i = -half; i <= half; i++)
            {
                for (int j = -half; j <= half; j++)
                {
                    int index = VertexIndex(i, j, half, stride);
                    vertices[index] = new Vector3(i, 0f, j);       // cell units; scaled to metres by the transform
                    uvs[index] = new Vector2((i + half) / (float)gridResolution, (j + half) / (float)gridResolution);
                }
            }

            int[] triangles = BuildAnnulusTriangles(gridResolution, holeHalfCells, half, stride);
            return Assemble(vertices, uvs, triangles);
        }

        static int VertexIndex(int i, int j, int half, int stride) => (i + half) * stride + (j + half);

        // One quad (two upward-facing triangles) per grid cell, skipping cells that lie wholly inside
        // the central hole square. A quad occupies [i, i+1] x [j, j+1]; it is inside the hole when both
        // axis spans stay within +/- holeHalfCells.
        static int[] BuildAnnulusTriangles(int gridResolution, int holeHalfCells, int half, int stride)
        {
            var triangles = new System.Collections.Generic.List<int>(gridResolution * gridResolution * 6);
            for (int i = -half; i < half; i++)
            {
                for (int j = -half; j < half; j++)
                {
                    if (IsQuadInsideHole(i, j, holeHalfCells)) continue;

                    int a = VertexIndex(i, j, half, stride);
                    int b = VertexIndex(i + 1, j, half, stride);
                    int c = VertexIndex(i, j + 1, half, stride);
                    int d = VertexIndex(i + 1, j + 1, half, stride);

                    // Wound so the surface faces +Y (see the header): (a, c, b) and (b, c, d).
                    triangles.Add(a); triangles.Add(c); triangles.Add(b);
                    triangles.Add(b); triangles.Add(c); triangles.Add(d);
                }
            }
            return triangles.ToArray();
        }

        static bool IsQuadInsideHole(int i, int j, int holeHalfCells)
        {
            bool insideX = i >= -holeHalfCells && (i + 1) <= holeHalfCells;
            bool insideZ = j >= -holeHalfCells && (j + 1) <= holeHalfCells;
            return insideX && insideZ;
        }

        static Mesh Assemble(Vector3[] vertices, Vector2[] uvs, int[] triangles)
        {
            var mesh = new Mesh
            {
                name = "LargeWaterClipmapLevel",
                // Grid resolutions routinely exceed the 16-bit vertex limit across the annulus.
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

        static void ValidateOrThrow(int gridResolution, int holeHalfCells)
        {
            if (gridResolution < MinGridResolution)
                throw new System.ArgumentOutOfRangeException(nameof(gridResolution), gridResolution,
                    $"needs >= {MinGridResolution} cells per side");
            if ((gridResolution & 1) != 0)
                throw new System.ArgumentException($"gridResolution must be even (got {gridResolution})", nameof(gridResolution));
            if (holeHalfCells < 0 || holeHalfCells >= gridResolution / 2)
                throw new System.ArgumentOutOfRangeException(nameof(holeHalfCells), holeHalfCells,
                    "must be in [0, gridResolution/2)");
        }
    }
}
