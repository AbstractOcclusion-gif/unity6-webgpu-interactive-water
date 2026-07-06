// LargeWaveField - CPU mirror of the open-water wave field (Runtime/Shaders/WaterLargeWaves.hlsl).
//
// Kept BYTE-FOR-BYTE in lockstep with the shader's LargeBodyWave() so open-water buoyancy matches
// the rendered surface without a GPU readback - the same CPU/GPU-mirror pattern the package already
// uses for WaterWaveBank <-> WaterWaves.hlsl. If you change the wave constants or the hash in the
// HLSL, change them here too (and vice versa).
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>World-space open-water wave height, matching the large-body surface shader.</summary>
    internal static class LargeWaveField
    {
        // These MUST match the LBW_* defines in WaterLargeWaves.hlsl.
        const int WaveCount = 12;
        const float BaseWavelength = 9.0f;
        const float WavelengthFalloff = 0.82f;
        const float BaseAmplitude = 0.14f;
        const float AmplitudeFalloff = 0.76f;
        const float DirectionSpread = 1.05f;
        const float Gravity = 9.81f;
        const float TwoPi = 6.28318530718f;

        // Matches LBW_INVERSION_ITERATIONS in WaterLargeWaves.hlsl (Crest's SampleInvertedDisplacement
        // uses 4). Inverting the horizontal Gerstner displacement is how a fixed world xz maps back to
        // the wave's SOURCE point, so buoyancy samples the height under the crest the eye sees.
        const int InversionIterations = 4;

        // Matches LbwHash() in the shader: frac(sin(n * 12.9898) * 43758.5453).
        static float Hash(float n) => Fract(Mathf.Sin(n * 12.9898f) * 43758.5453f);
        static float Fract(float x) => x - Mathf.Floor(x);

        /// <summary>
        /// Wave (height, dHeight/dx, dHeight/dz) in metres at world (x, z). Mirrors
        /// LargeBodyWave() in WaterLargeWaves.hlsl. <paramref name="time"/> is the body's WaveTime
        /// (same clock the shader's _WaveTime carries). Height drives buoyancy depth; the slope
        /// drives wave-carried drift.
        /// </summary>
        internal static Vector3 Evaluate(float worldX, float worldZ, float time, float amplitudeScale, float windHeadingRadians)
        {
            float height = 0f;
            float slopeX = 0f;
            float slopeZ = 0f;
            float wavelength = BaseWavelength;
            float amplitude = BaseAmplitude;

            for (int n = 0; n < WaveCount; n++)
            {
                float fn = n;
                float headingJitter = (Hash(fn + 1f) * 2f - 1f) * DirectionSpread;
                float heading = windHeadingRadians + headingJitter;
                float directionX = Mathf.Cos(heading);
                float directionZ = Mathf.Sin(heading);
                float phaseOffset = Hash(fn + 17f) * TwoPi;

                float wavenumber = TwoPi / Mathf.Max(wavelength, 1e-3f);
                float angularSpeed = Mathf.Sqrt(Gravity * wavenumber);
                float phase = (directionX * worldX + directionZ * worldZ) * wavenumber - angularSpeed * time + phaseOffset;
                height += amplitude * Mathf.Sin(phase);
                float slopeMagnitude = amplitude * wavenumber * Mathf.Cos(phase);
                slopeX += slopeMagnitude * directionX;
                slopeZ += slopeMagnitude * directionZ;

                wavelength *= WavelengthFalloff;
                amplitude *= AmplitudeFalloff;
            }

            return new Vector3(height * amplitudeScale, slopeX * amplitudeScale, slopeZ * amplitudeScale);
        }

        /// <summary>
        /// Horizontal Gerstner offset (metres) at a SOURCE (x, z), choppiness baked in. Mirrors
        /// LargeBodyWaveDisplacement() in WaterLargeWaves.hlsl. Zero when <paramref name="choppiness"/>
        /// is 0, so the field collapses to the pure vertical swell.
        /// </summary>
        static Vector2 Displacement(float sourceX, float sourceZ, float time, float amplitudeScale, float windHeadingRadians, float choppiness)
        {
            float displacementX = 0f;
            float displacementZ = 0f;
            float wavelength = BaseWavelength;
            float amplitude = BaseAmplitude;

            for (int n = 0; n < WaveCount; n++)
            {
                float fn = n;
                float headingJitter = (Hash(fn + 1f) * 2f - 1f) * DirectionSpread;
                float heading = windHeadingRadians + headingJitter;
                float directionX = Mathf.Cos(heading);
                float directionZ = Mathf.Sin(heading);
                float phaseOffset = Hash(fn + 17f) * TwoPi;

                float wavenumber = TwoPi / Mathf.Max(wavelength, 1e-3f);
                float angularSpeed = Mathf.Sqrt(Gravity * wavenumber);
                float phase = (directionX * sourceX + directionZ * sourceZ) * wavenumber - angularSpeed * time + phaseOffset;
                float cosP = Mathf.Cos(phase);
                displacementX += amplitude * directionX * cosP;
                displacementZ += amplitude * directionZ * cosP;

                wavelength *= WavelengthFalloff;
                amplitude *= AmplitudeFalloff;
            }

            float scale = amplitudeScale * choppiness;
            return new Vector2(displacementX * scale, displacementZ * scale);
        }

        /// <summary>
        /// Invert the horizontal displacement: find the SOURCE (x, z) that Gerstner chop displaces
        /// onto the query world (x, z). Fixed-point iteration mirroring Crest's SampleInvertedDisplacement.
        /// With choppiness 0 the displacement is zero, so this returns the query point on the first pass.
        /// </summary>
        static Vector2 InvertToSource(float queryX, float queryZ, float time, float amplitudeScale, float windHeadingRadians, float choppiness)
        {
            float sourceX = queryX;
            float sourceZ = queryZ;
            for (int i = 0; i < InversionIterations; i++)
            {
                Vector2 displacement = Displacement(sourceX, sourceZ, time, amplitudeScale, windHeadingRadians, choppiness);
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
        internal static Vector3 EvaluateAtQuery(float worldX, float worldZ, float time, float amplitudeScale, float windHeadingRadians, float choppiness)
        {
            Vector2 source = InvertToSource(worldX, worldZ, time, amplitudeScale, windHeadingRadians, choppiness);
            return Evaluate(source.x, source.y, time, amplitudeScale, windHeadingRadians);
        }

        /// <summary>Wave height (metres) at a QUERY world (x, z), chop-inverted. See EvaluateAtQuery.</summary>
        internal static float HeightAtQuery(float worldX, float worldZ, float time, float amplitudeScale, float windHeadingRadians, float choppiness)
            => EvaluateAtQuery(worldX, worldZ, time, amplitudeScale, windHeadingRadians, choppiness).x;
    }
}
