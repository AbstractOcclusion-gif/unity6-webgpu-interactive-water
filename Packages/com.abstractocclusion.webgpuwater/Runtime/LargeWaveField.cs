// LargeWaveField - CPU mirror of the open-water wave field (Runtime/Shaders/WaterLargeWaves.hlsl).
//
// Kept BYTE-FOR-BYTE in lockstep with the shader's LargeBodyWave() so open-water buoyancy matches
// the rendered surface without a GPU readback - the same CPU/GPU-mirror pattern the package already
// uses for WaterWaveBank <-> WaterWaves.hlsl. If you change the wave constants or the hash in the
// HLSL, change them here too (and vice versa). Two bands are summed: the wind CHOP band and the
// long-period SWELL band, exactly as LbwAccumulateBand does in the shader.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>World-space open-water wave height, matching the large-body surface shader.</summary>
    internal static class LargeWaveField
    {
        // Chop band - these MUST match the LBW_* defines in WaterLargeWaves.hlsl.
        const int WaveCount = 12;
        const float BaseWavelength = 9.0f;
        const float WavelengthFalloff = 0.82f;
        const float BaseAmplitude = 0.14f;
        const float AmplitudeFalloff = 0.76f;
        const float DirectionSpread = 1.05f;
        const float ChopPhaseSeed = 1.0f;

        // Long-period swell band - must match the LBW_SWELL_* defines. Base wavelength/height are
        // passed in (the art knobs); base amplitude is 1 so the height knob reads as metres.
        const int SwellCount = 4;
        const float SwellBaseAmplitude = 1.0f;
        const float SwellWavelengthFalloff = 0.68f;
        const float SwellAmplitudeFalloff = 0.85f;
        const float SwellDirectionSpread = 0.5f;
        const float SwellPhaseSeed = 101.0f;

        const float Gravity = 9.81f;
        const float TwoPi = 6.28318530718f;

        // Matches LBW_INVERSION_ITERATIONS in WaterLargeWaves.hlsl (Crest's SampleInvertedDisplacement
        // uses 4). Inverting the horizontal Gerstner displacement is how a fixed world xz maps back to
        // the wave's SOURCE point, so buoyancy samples the height under the crest the eye sees.
        const int InversionIterations = 4;

        // Matches LbwHash() in the shader: frac(sin(n * 12.9898) * 43758.5453).
        static float Hash(float n) => Fract(Mathf.Sin(n * 12.9898f) * 43758.5453f);
        static float Fract(float x) => x - Mathf.Floor(x);

        // Height + slope + horizontal displacement accumulated across the wave components. Mirrors the
        // shader's LargeBodyWaveField, minus the Jacobian derivatives (buoyancy needs no normals).
        struct BandAccum
        {
            public float Height;
            public float SlopeX;
            public float SlopeZ;
            public float DisplacementX;
            public float DisplacementZ;
        }

        // Sum one band of directional Gerstner components. Mirrors LbwAccumulateBand() in the shader.
        static void AccumulateBand(ref BandAccum a, float x, float z, float time, int count,
            float baseWavelength, float wavelengthFalloff, float baseAmplitude, float amplitudeFalloff,
            float dirSpread, float phaseSeed, float amplitudeScale, float windHeadingRadians)
        {
            float wavelength = baseWavelength;
            float amplitude = baseAmplitude;

            for (int n = 0; n < count; n++)
            {
                float fn = n;
                float headingJitter = (Hash(fn + phaseSeed) * 2f - 1f) * dirSpread;
                float heading = windHeadingRadians + headingJitter;
                float directionX = Mathf.Cos(heading);
                float directionZ = Mathf.Sin(heading);
                float phaseOffset = Hash(fn + phaseSeed + 16f) * TwoPi;

                float wavenumber = TwoPi / Mathf.Max(wavelength, 1e-3f);
                float angularSpeed = Mathf.Sqrt(Gravity * wavenumber);
                float phase = (directionX * x + directionZ * z) * wavenumber - angularSpeed * time + phaseOffset;
                float sinP = Mathf.Sin(phase);
                float cosP = Mathf.Cos(phase);
                float amp = amplitudeScale * amplitude;

                a.Height += amp * sinP;
                float slopeMagnitude = amp * wavenumber * cosP;
                a.SlopeX += slopeMagnitude * directionX;
                a.SlopeZ += slopeMagnitude * directionZ;
                a.DisplacementX += amp * directionX * cosP;
                a.DisplacementZ += amp * directionZ * cosP;

                wavelength *= wavelengthFalloff;
                amplitude *= amplitudeFalloff;
            }
        }

        static BandAccum EvaluateBands(float x, float z, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight)
        {
            BandAccum a = default;
            AccumulateBand(ref a, x, z, time, WaveCount, BaseWavelength, WavelengthFalloff,
                BaseAmplitude, AmplitudeFalloff, DirectionSpread, ChopPhaseSeed, amplitudeScale, windHeadingRadians);
            AccumulateBand(ref a, x, z, time, SwellCount, swellWavelength, SwellWavelengthFalloff,
                SwellBaseAmplitude, SwellAmplitudeFalloff, SwellDirectionSpread, SwellPhaseSeed, swellHeight, windHeadingRadians);
            return a;
        }

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at world (x, z). Mirrors
        /// EvaluateLargeBodyWave() in WaterLargeWaves.hlsl. <paramref name="time"/> is the body's
        /// WaveTime. Height drives buoyancy depth; the slope drives wave-carried drift.
        /// </summary>
        internal static Vector3 Evaluate(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight)
        {
            BandAccum a = EvaluateBands(worldX, worldZ, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight);
            return new Vector3(a.Height, a.SlopeX, a.SlopeZ);
        }

        /// <summary>
        /// Horizontal Gerstner offset (metres) at a SOURCE (x, z), choppiness baked in. Mirrors
        /// LargeBodyWaveDisplacement() in WaterLargeWaves.hlsl. Zero when <paramref name="choppiness"/>
        /// is 0, so the field collapses to the pure vertical swell.
        /// </summary>
        static Vector2 Displacement(float sourceX, float sourceZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness)
        {
            BandAccum a = EvaluateBands(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight);
            return new Vector2(a.DisplacementX * choppiness, a.DisplacementZ * choppiness);
        }

        /// <summary>
        /// Invert the horizontal displacement: find the SOURCE (x, z) that Gerstner chop displaces
        /// onto the query world (x, z). Fixed-point iteration mirroring Crest's SampleInvertedDisplacement.
        /// With choppiness 0 the displacement is zero, so this returns the query point on the first pass.
        /// </summary>
        static Vector2 InvertToSource(float queryX, float queryZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness)
        {
            float sourceX = queryX;
            float sourceZ = queryZ;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector2 displacement = Displacement(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight, choppiness);
                sourceX -= (sourceX + displacement.x) - queryX;
                sourceZ -= (sourceZ + displacement.y) - queryZ;
            }
            return new Vector2(sourceX, sourceZ);
        }

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at a QUERY world (x, z), accounting for
        /// horizontal chop by first inverting to the source point. This is what buoyancy needs: the
        /// surface value directly above a fixed world position. Matches the rendered (displaced) crest.
        /// </summary>
        internal static Vector3 EvaluateAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight, choppiness);
            return Evaluate(source.x, source.y, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight);
        }

        /// <summary>Wave height (metres) at a QUERY world (x, z), chop-inverted. See EvaluateAtQuery.</summary>
        internal static float HeightAtQuery(float worldX, float worldZ, float time, float amplitudeScale,
            float windHeadingRadians, float swellWavelength, float swellHeight, float choppiness)
            => EvaluateAtQuery(worldX, worldZ, time, amplitudeScale, windHeadingRadians, swellWavelength, swellHeight, choppiness).x;
    }
}
