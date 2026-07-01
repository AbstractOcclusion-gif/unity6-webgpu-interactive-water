// WebGL Water - wind-driven spectral wave bank (Unity 6 / URP port)
//
// Builds a set of directional sinusoidal components from a JONSWAP-shaped spectrum
// driven entirely by wind. The components are (a) uploaded to the shaders as global
// arrays and (b) evaluated on the CPU here for buoyancy, so the rendered surface and
// the floating-object physics use the exact same wave function.
//
// Coordinate note: the scene's world units ARE the pool units (the pool spans
// [-1, 1]). Wavelengths only make physical sense in metres, so the bank multiplies
// pool positions by metersPerPoolUnit internally and converts amplitudes back to
// pool units before they are used. The shader (WaterWaves.hlsl) does the same.
using UnityEngine;

namespace WebGLWater
{
    public sealed class WaterWaveBank
    {
        // Must match WATER_MAX_WAVES in WaterWaves.hlsl.
        public const int MaxWaves = 16;

        // --- physical constants -------------------------------------------------
        const float Gravity = 9.81f;

        // JONSWAP peak: omega_p = PeakFactor * (g^2 / (U * F))^(1/3).
        const float JonswapPeakFactor = 22f;
        const float JonswapGamma = 3.3f;          // peak enhancement
        const float JonswapSigmaLow = 0.07f;      // left of the peak
        const float JonswapSigmaHigh = 0.09f;     // right of the peak
        const float PhillipsAlpha = 0.0081f;      // spectrum scale (relative use only)

        // Fetch-limited total variance: m0 = Coeff * (gF/U^2) * U^4 / g^2.
        const float FetchEnergyCoeff = 1.6e-7f;

        // Significant-height -> per-component amplitude. For N sinusoids of amplitude
        // A_i the surface variance is sum(A_i^2)/2, and Hs = 4 * sqrt(variance), so
        // Hs = HsToRms * sqrt(sum A_i^2) with HsToRms = 4 / sqrt(2).
        const float HsToRms = 2.8284271f;

        // --- spectral sampling band --------------------------------------------
        const float BandLowFactor = 0.5f;         // shortest sampled wavelength = peak * this
        const float BandHighFactor = 2.5f;        // longest sampled wavelength  = peak * this

        // A light breeze over a small lake physically produces sub-centimetre waves;
        // clamp the dominant wavelength into a range that actually reads on screen.
        const float MinPeakWavelength = 0.6f;     // metres
        const float MaxPeakWavelength = 6f;       // metres

        // Directional spreading. Components are stratified across a wide fan (not
        // randomly clumped) so their crests cross instead of marching in parallel,
        // and a share of them propagate UPWIND so the two trains interfere into
        // choppy, non-directional lake water rather than a one-way "river flow".
        const float DownwindFanRadians = 1.45f;   // ~83 degrees either side of the wind
        const float UpwindFanRadians = 1.2f;      // ~69 degrees around the opposing axis
        const int UpwindEveryNth = 3;             // ~1/3 of components face upwind
        const float UpwindAmplitudeFactor = 0.7f; // upwind train a touch weaker than downwind
        const float GoldenRatio = 0.6180339887f;  // low-discrepancy angular stratification

        const int GenerationSeed = 9173;          // deterministic bank for reproducibility
        const float TwoPi = 2f * Mathf.PI;

        struct Wave
        {
            public Vector2 dir;   // unit direction in the XZ plane (x, z)
            public float k;       // wavenumber (rad / metre)
            public float omega;   // angular speed (rad / s)
            public float amp;     // amplitude in POOL units
            public float phase;   // phase offset (rad)
        }

        readonly Wave[] _waves = new Wave[MaxWaves];
        int _count;

        // Packed for upload: A = (dirX, dirZ, k, omega), B = (amp, phase, 0, 0).
        readonly Vector4[] _packedA = new Vector4[MaxWaves];
        readonly Vector4[] _packedB = new Vector4[MaxWaves];

        public int Count => _count;
        public Vector4[] PackedA => _packedA;
        public Vector4[] PackedB => _packedB;

        /// <summary>
        /// Rebuild the bank for a wind state.
        /// </summary>
        /// <param name="windSpeed">Wind speed in m/s.</param>
        /// <param name="windFromDegrees">Wind heading: 0 = blowing toward +X (i.e. coming from the west).</param>
        /// <param name="fetchMeters">Open-water distance the wind blows over (~ pool extent).</param>
        /// <param name="waveCount">Number of sinusoidal components (clamped to MaxWaves).</param>
        /// <param name="amplitudeScale">Artistic multiplier on the physical wave height.</param>
        /// <param name="directionSpreadExponent">Higher = tighter alignment to the wind.</param>
        /// <param name="metersPerPoolUnit">Pool-unit -> metre conversion used for amplitudes.</param>
        public void Generate(float windSpeed, float windFromDegrees, float fetchMeters,
                             int waveCount, float amplitudeScale, float directionSpreadExponent,
                             float metersPerPoolUnit)
        {
            _count = Mathf.Clamp(waveCount, 1, MaxWaves);

            float wind = Mathf.Max(0.1f, windSpeed);
            float fetch = Mathf.Max(1f, fetchMeters);
            float metersPerUnit = Mathf.Max(1e-3f, metersPerPoolUnit);

            float peakWavelength = ResolvePeakWavelength(wind, fetch);
            float omegaPeak = OmegaFromWavelength(peakWavelength);

            float windRadians = windFromDegrees * Mathf.Deg2Rad;
            var windDir = new Vector2(Mathf.Cos(windRadians), Mathf.Sin(windRadians));

            float logLow = Mathf.Log(peakWavelength * BandLowFactor);
            float logHigh = Mathf.Log(peakWavelength * BandHighFactor);

            var rng = new System.Random(GenerationSeed);
            float sumAmpSquared = 0f;

            for (int i = 0; i < _count; i++)
            {
                float bandT = _count == 1 ? 0.5f : (i + 0.5f) / _count;
                float wavelength = Mathf.Exp(Mathf.Lerp(logLow, logHigh, bandT));
                float k = TwoPi / wavelength;
                float omega = Mathf.Sqrt(Gravity * k);

                // Stratify the heading across the fan with a golden-ratio sequence so the
                // directions are spread evenly rather than clustering on the wind axis.
                bool upwind = _count > 2 && (i % UpwindEveryNth) == 0;
                float stratified = (((i + 1) * GoldenRatio) % 1f) * 2f - 1f; // even in [-1, 1]
                float fan = upwind ? UpwindFanRadians : DownwindFanRadians;
                float offset = stratified * fan;
                float heading = (upwind ? Mathf.PI : 0f) + offset;

                // Weight relative to the subset centre so the fan is actually populated.
                float directionWeight = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(offset)), 2f * directionSpreadExponent);
                float spectral = Mathf.Sqrt(Mathf.Max(0f, Jonswap(omega, omegaPeak)));
                float amp = spectral * directionWeight * (upwind ? UpwindAmplitudeFactor : 1f);

                _waves[i] = new Wave
                {
                    dir = Rotate(windDir, heading),
                    k = k,
                    omega = omega,
                    amp = amp,
                    phase = (float)(rng.NextDouble() * TwoPi)
                };
                sumAmpSquared += amp * amp;
            }

            NormalizeAmplitudes(sumAmpSquared, wind, fetch, amplitudeScale, metersPerUnit);
            Pack();
        }

        // Scale every component so the surface's significant wave height matches the
        // (exaggerated) physical target, then convert metre amplitudes to pool units.
        void NormalizeAmplitudes(float sumAmpSquared, float wind, float fetch,
                                 float amplitudeScale, float metersPerUnit)
        {
            if (sumAmpSquared <= 0f) return;

            float dimensionlessFetch = Gravity * fetch / (wind * wind);
            float variance = FetchEnergyCoeff * dimensionlessFetch * Mathf.Pow(wind, 4f) / (Gravity * Gravity);
            float hsMeters = 4f * Mathf.Sqrt(Mathf.Max(0f, variance)) * Mathf.Max(0f, amplitudeScale);

            float currentRms = Mathf.Sqrt(sumAmpSquared);
            float targetRms = hsMeters / HsToRms;             // metres
            float metersToPool = 1f / metersPerUnit;
            float scale = (targetRms / currentRms) * metersToPool;

            for (int i = 0; i < _count; i++)
                _waves[i].amp *= scale;
        }

        void Pack()
        {
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                _packedA[i] = new Vector4(w.dir.x, w.dir.y, w.k, w.omega);
                _packedB[i] = new Vector4(w.amp, w.phase, 0f, 0f);
            }
        }

        /// <summary>Height (pool/world units) of the wave layer at pool xz in [-1, 1].</summary>
        public float SampleHeight(float poolX, float poolZ, float time, float metersPerPoolUnit)
        {
            float mx = poolX * metersPerPoolUnit, mz = poolZ * metersPerPoolUnit;
            float height = 0f;
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                height += w.amp * Mathf.Sin((w.dir.x * mx + w.dir.y * mz) * w.k - w.omega * time + w.phase);
            }
            return height;
        }

        /// <summary>Gradient d(height)/d(poolXZ) of the wave layer (pool units).</summary>
        public Vector2 SampleSlope(float poolX, float poolZ, float time, float metersPerPoolUnit)
        {
            float mx = poolX * metersPerPoolUnit, mz = poolZ * metersPerPoolUnit;
            float gx = 0f, gz = 0f;
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                float c = Mathf.Cos((w.dir.x * mx + w.dir.y * mz) * w.k - w.omega * time + w.phase);
                float common = w.amp * c * w.k * metersPerPoolUnit;
                gx += common * w.dir.x;
                gz += common * w.dir.y;
            }
            return new Vector2(gx, gz);
        }

        // --- spectrum helpers ---------------------------------------------------
        static float ResolvePeakWavelength(float wind, float fetch)
        {
            float omegaPeak = JonswapPeakFactor * Mathf.Pow(Gravity * Gravity / (wind * fetch), 1f / 3f);
            float wavelength = WavelengthFromOmega(omegaPeak);
            return Mathf.Clamp(wavelength, MinPeakWavelength, MaxPeakWavelength);
        }

        // Unnormalised JONSWAP spectral density at omega (relative weight only).
        static float Jonswap(float omega, float omegaPeak)
        {
            if (omega <= 0f) return 0f;
            float pm = PhillipsAlpha * Gravity * Gravity / Mathf.Pow(omega, 5f)
                       * Mathf.Exp(-1.25f * Mathf.Pow(omegaPeak / omega, 4f));
            float sigma = omega <= omegaPeak ? JonswapSigmaLow : JonswapSigmaHigh;
            float r = Mathf.Exp(-Mathf.Pow(omega - omegaPeak, 2f) / (2f * sigma * sigma * omegaPeak * omegaPeak));
            return pm * Mathf.Pow(JonswapGamma, r);
        }

        // Deep-water dispersion: omega^2 = g * k, k = 2*pi / wavelength.
        static float OmegaFromWavelength(float wavelength) => Mathf.Sqrt(Gravity * TwoPi / wavelength);
        static float WavelengthFromOmega(float omega) => TwoPi * Gravity / (omega * omega);

        static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }
    }
}
