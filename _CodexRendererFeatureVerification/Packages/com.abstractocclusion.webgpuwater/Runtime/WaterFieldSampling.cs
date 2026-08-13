// WebGpuWater - shared CPU bilinear sampling over row-major square fields.
//
// Three CPU consumers (WaterOceanFft's buoyancy height field, WaterSurfaceSampler's ripple
// readback, WaterShoreDepthField's shore mirror for LargeWaveField) each carried their own
// bilinear filter with identical semantics: half-texel offset (UV addresses texel centres, the
// GPU convention), texel coordinate clamped to [0, res-1], +1 neighbour clamped to the edge -
// so out-of-range UVs collapse to the edge texel, matching a GPU bilinear read with Clamp wrap.
// One implementation here so the filters can never drift apart.
// (WaterShoreDepthField's copy was written as clamp-the-UV-first + clamped floor/fraction; that
// form is output-identical to this one for every input - both collapse out-of-range coordinates
// to the edge texel - so it was unified rather than kept as a variant.)
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterFieldSampling
    {
        // Texel indices + lerp weights for one bilinear tap: the SINGLE definition of the
        // half-texel/clamp maths that both typed overloads below share.
        static void BilinearTexels(int res, float u, float v,
                                   out int x0, out int x1, out int z0, out int z1,
                                   out float tx, out float tz)
        {
            float sx = Mathf.Clamp(u * res - 0.5f, 0f, res - 1f);
            float sz = Mathf.Clamp(v * res - 0.5f, 0f, res - 1f);
            x0 = (int)sx; z0 = (int)sz;
            x1 = Mathf.Min(x0 + 1, res - 1);
            z1 = Mathf.Min(z0 + 1, res - 1);
            tx = sx - x0; tz = sz - z0;
        }

        /// <summary>Bilinear sample of a row-major res*res float field at UV in 0..1
        /// (texel-centre convention, edge-clamped outside).</summary>
        internal static float SampleBilinear(float[] field, int res, float u, float v)
        {
            BilinearTexels(res, u, v, out int x0, out int x1, out int z0, out int z1,
                           out float tx, out float tz);
            float bottom = Mathf.Lerp(field[z0 * res + x0], field[z0 * res + x1], tx);
            float top    = Mathf.Lerp(field[z1 * res + x0], field[z1 * res + x1], tx);
            return Mathf.Lerp(bottom, top, tz);
        }

        /// <summary>Bilinear sample of a row-major res*res Color field. Same texel maths as the
        /// float overload (shared via BilinearTexels); only the channel type differs.</summary>
        internal static Color SampleBilinear(Color[] field, int res, float u, float v)
        {
            BilinearTexels(res, u, v, out int x0, out int x1, out int z0, out int z1,
                           out float tx, out float tz);
            Color bottom = Color.Lerp(field[z0 * res + x0], field[z0 * res + x1], tx);
            Color top    = Color.Lerp(field[z1 * res + x0], field[z1 * res + x1], tx);
            return Color.Lerp(bottom, top, tz);
        }
    }
}
