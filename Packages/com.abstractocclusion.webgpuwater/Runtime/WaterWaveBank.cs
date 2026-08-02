// WebGpuWater - wind-driven spectral wave bank (Unity 6 / URP port)
//
// Builds a set of directional sinusoidal components spread around an AUTHORED wavelength and scaled
// to an AUTHORED significant height, then shapes the sum into something that reads as water rather
// than as a sum of sines. The components are (a) uploaded to the shaders as global arrays and (b)
// evaluated on the CPU here for buoyancy, so the rendered surface and the floating-object physics
// use the exact same wave function.
//
// WHY THIS WAS REWRITTEN. The original derived its peak wavelength from wind and fetch through a
// JONSWAP growth law, then clamped the result into [0.6, 6] m - and at every wind speed the slider
// allows (1-15 m/s) the raw peak came out between 0.04 m and 0.27 m, so the clamp was ALWAYS active
// and wind changed the wavelength not at all. It then normalised the height to a fetch-limited
// variance and immediately divided by sqrt(ScaleReferenceMeters * metersPerUnit), which cancels the
// sqrt(fetch) growth EXACTLY: sweeping the fetch field from 2 m to 500 m moved the rendered height
// by nothing (0.002741 m throughout). So the spectrum computed a wavelength that was thrown away and
// a height that was cancelled, and what actually reached the screen was twelve fixed-wavelength sines
// scaled by wind - which is why mid-lake chop was unreachable and two of the four knobs did not do
// what their names said.
//
// The rig is now the same one the FFT ocean uses: WAVELENGTH and HEIGHT in metres, orthogonal, no
// derivation and no compensation. Wind steers direction only. JONSWAP survives where it is actually
// useful - as the relative WEIGHTING across the band around the authored wavelength.
//
// Coordinate note: the scene's world units ARE the pool units (the pool spans [-1, 1]). Wavelengths
// only make physical sense in metres, so the bank multiplies pool positions by metersPerPoolUnit
// internally and converts amplitudes back to pool units before they are used. The shader
// (WaterWaves.hlsl) does the same.
//
// Vertical-only displacement (no Gerstner horizontal pinch) is still deliberate: it keeps height a
// true function of (x,z), which both the buoyancy sampler and the _WaterTex normal lookup rely on.
// Crest sharpening is therefore done in the VERTICAL (see the Stokes term) rather than horizontally.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterWaveBank
    {
        // Must match WATER_MAX_WAVES in WaterWaves.hlsl.
        public const int MaxWaves = 16;

        // --- physical constants -------------------------------------------------
        const float Gravity = 9.81f;

        // JONSWAP band weighting. The peak enhancement and its asymmetric widths are the standard
        // Hasselmann values, shared with the ocean spectrum (WaterOceanSpectrum.JonswapSigma*).
        const float JonswapGamma = 3.3f;          // peak enhancement
        const float JonswapSigmaLow = 0.07f;      // left of the peak
        const float JonswapSigmaHigh = 0.09f;     // right of the peak

        // Significant-height -> per-component amplitude. For N sinusoids of amplitude
        // A_i the surface variance is sum(A_i^2)/2, and Hs = 4 * sqrt(variance), so
        // Hs = HsToRms * sqrt(sum A_i^2) with HsToRms = 4 / sqrt(2).
        const float HsToRms = 2.8284271f;
        const float SignificantHeightToRms = 4f;  // Hs = 4 * sqrt(variance)

        // --- spectral sampling band --------------------------------------------
        const float BandLowFactor = 0.5f;         // shortest sampled wavelength = authored * this
        const float BandHighFactor = 2.5f;        // longest sampled wavelength  = authored * this

        // Directional spreading. Components are stratified across a wide fan (not
        // randomly clumped) so their crests cross instead of marching in parallel,
        // and a share of them propagate UPWIND so the two trains interfere into
        // choppy, non-directional lake water rather than a one-way "river flow".
        const float DownwindFanRadians = 1.45f;   // ~83 degrees either side of the wind
        const float UpwindFanRadians = 1.2f;      // ~69 degrees around the opposing axis
        const int UpwindEveryNth = 3;             // ~1/3 of components face upwind
        const float UpwindAmplitudeFactor = 0.7f; // upwind train a touch weaker than downwind
        const float GoldenRatio = 0.6180339887f;  // low-discrepancy angular stratification

        // --- wave groups ("sets") ----------------------------------------------
        // Real chop arrives in groups, not as a uniform buzz: an envelope of roughly seven waves
        // rides over the carrier and travels at the GROUP velocity, which in deep water is half the
        // phase velocity. Twelve sines with fixed phases do beat against each other, but quickly and
        // regularly; an explicit envelope is both slower and controllable.
        //
        // TWO crossing envelopes, not one: a single envelope makes the whole surface breathe in
        // unison, which reads as pulsing rather than as sets. Two at different lengths and angles
        // interfere into patches.
        //
        // ⚠️ THE LENGTHS MUST BE IRRATIONALLY RELATED, and 7 and 11 were not - being integers, they
        // share a common period at 77 wavelengths, so the envelope pattern REPEATS on that grid. The
        // carrier's own motion normally hides it; slow the layer down and that static structure is
        // exactly what surfaces, as lines. B is now A times the golden ratio, which has no common
        // period with A at all, so there is nothing left to re-align.
        // HLSL pair: none - the envelopes are precomputed here and uploaded as _WaveGroupA/B.
        const float GroupLengthWavesA = 7f;
        const float GroupLengthWavesB = 11.3262379f;   // 7 * golden ratio
        const float GroupAngleARadians = 0.35f;   // ~20 deg off the wind, one either side
        const float GroupAngleBRadians = -0.52f;  // ~30 deg the other way
        // Deep-water group velocity is half the phase velocity: cg = 0.5 * sqrt(g / k_carrier).
        const float GroupVelocityFraction = 0.5f;

        // --- crest shaping -------------------------------------------------------
        // Second-order Stokes: a real wave of amplitude A and wavenumber k is not A*cos(theta) but
        // A*cos(theta) + (k*A^2/2)*cos(2*theta), and that second term is exactly what sharpens the
        // crest and flattens the trough. Applied to the SUMMED height as (k/2)*(h^2 - variance) it
        // generalises to a random sea, keeps the mean at zero, stays a pure function of (x,z) - so
        // buoyancy and the normal lookup are untouched - and has a trivial exact derivative.
        //
        // It also self-scales with steepness: the correction is proportional to k*h, so it does
        // nothing on flat water and bites exactly when the layer is pushed hard, which is when the
        // pure sines looked worst.
        const float StokesSecondOrderFactor = 0.5f;   // the 1/2 in k*A^2/2
        // Variance growth from the quadratic term, for a Gaussian sum: Var(h + a*h^2) = var*(1 + 2*a^2*var).
        // Dividing it back out keeps the authored significant height honest as sharpness rises.
        const float StokesVarianceGrowth = 2f;

        const int GenerationSeed = 9173;          // deterministic bank for reproducibility
        const float TwoPi = 2f * Mathf.PI;
        const float MinWavelength = 1e-3f;
        const float MinRms = 1e-9f;
        const float MinVerticalExtent = 1e-3f;

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

        /// <summary>Group envelope A: (dirX, dirZ, wavenumber, angular speed). HLSL pair: _WaveGroupA.</summary>
        public Vector4 GroupA { get; private set; }
        /// <summary>Group envelope B, crossing A. HLSL pair: _WaveGroupB.</summary>
        public Vector4 GroupB { get; private set; }
        /// <summary>(envelope base, envelope amplitude, Stokes coefficient, Stokes DC offset). HLSL pair: _WaveShape.</summary>
        /// <remarks>Defaults to a pass-through (envelope 1, no crest term) so a bank that has not been
        /// generated yet cannot flatten the surface through an all-zero envelope.</remarks>
        public Vector4 Shape { get; private set; } = new Vector4(1f, 0f, 0f, 0f);
        /// <summary>Variance rescale that keeps Hs honest as sharpening rises. HLSL pair: _WaveStokesNorm.</summary>
        public float StokesNorm { get; private set; } = 1f;

        /// <summary>
        /// Rebuild the bank for an authored wave state.
        /// </summary>
        /// <param name="windFromDegrees">Wind heading: 0 = blowing toward +X (i.e. coming from the west).
        /// DIRECTION ONLY - wind no longer sets the size of these waves.</param>
        /// <param name="waveLengthMeters">Dominant crest-to-crest distance, in metres.</param>
        /// <param name="significantHeightMeters">Significant wave height (metres) of the whole layer.</param>
        /// <param name="waveCount">Number of sinusoidal components (clamped to MaxWaves).</param>
        /// <param name="directionSpreadExponent">Higher = tighter alignment to the wind.</param>
        /// <param name="grouping">0 = a uniform field, 1 = strongly grouped into sets.</param>
        /// <param name="crestSharpness">0 = pure sines, 1 = full second-order Stokes crests.</param>
        /// <param name="animationSpeed">Multiplier on every component's angular speed AND on the group
        /// envelope, so the whole layer keeps its internal timing and only its overall pace changes.
        /// 1 = the physical rate for the authored wavelength; below that is a deliberate cheat.</param>
        /// <param name="metersPerPoolUnit">Pool-unit -> metre conversion for the phase.</param>
        /// <param name="verticalWorldPerUnit">World units per pool unit VERTICALLY (the volume's
        /// y extent). Crest height is pre-divided by this so a deeper pool doesn't render taller
        /// waves: PoolToWorld later multiplies surface height by it.</param>
        public void Generate(float windFromDegrees, float waveLengthMeters, float significantHeightMeters,
                             int waveCount, float directionSpreadExponent, float grouping,
                             float crestSharpness, float animationSpeed,
                             float metersPerPoolUnit, float verticalWorldPerUnit)
        {
            _count = Mathf.Clamp(waveCount, 1, MaxWaves);

            float peakWavelength = Mathf.Max(waveLengthMeters, MinWavelength);
            float significantHeight = Mathf.Max(significantHeightMeters, 0f);
            float verticalExtent = Mathf.Max(MinVerticalExtent, verticalWorldPerUnit);
            float timeScale = Mathf.Max(0f, animationSpeed);
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
                // The dispersion frequency and the CLOCK are two different things. Jonswap below must
                // weight the band by the PHYSICAL frequency: omegaPeak is derived from the authored
                // wavelength and is not scaled, so feeding it a timeScale'd omega slides the whole band
                // off the peak - and the Pierson-Moskowitz factor exp(-1.25*(omegaPeak/omega)^4) is a
                // QUARTIC in that ratio, so the slide is violent: at animation speed 0.5 one component
                // carried 95% of the energy and at 0.3 the other eleven had underflowed to zero. A single
                // surviving sinusoid is a set of perfectly straight parallel crests, which is exactly what
                // slowing the layer used to produce. The cheat belongs on the phase clock only.
                float omegaPhysical = Mathf.Sqrt(Gravity * k);
                float omega = omegaPhysical * timeScale;

                // Stratify the heading across the fan with a golden-ratio sequence so the
                // directions are spread evenly rather than clustering on the wind axis.
                bool upwind = _count > 2 && (i % UpwindEveryNth) == 0;
                float stratified = (((i + 1) * GoldenRatio) % 1f) * 2f - 1f; // even in [-1, 1]
                float fan = upwind ? UpwindFanRadians : DownwindFanRadians;
                float offset = stratified * fan;
                float heading = (upwind ? Mathf.PI : 0f) + offset;

                // Weight relative to the subset centre so the fan is actually populated.
                float directionWeight = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(offset)), 2f * directionSpreadExponent);
                float spectral = Mathf.Sqrt(Mathf.Max(0f, Jonswap(omegaPhysical, omegaPeak)));
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

            float poolVariance = NormalizeAmplitudes(sumAmpSquared, significantHeight, verticalExtent);
            BuildShaping(peakWavelength, windDir, grouping, crestSharpness, poolVariance, verticalExtent,
                         timeScale);
            Pack();
        }

        // Scale every component so the layer's significant height matches the authored metres, then
        // convert to pool units. Returns the resulting surface variance in POOL units, which the
        // Stokes term needs - its correction is quadratic in height, so it is not scale-free.
        //
        // No horizontal compensation here any more. The old sqrt(ScaleReferenceMeters * metersPerUnit)
        // divisor existed to make "a given wind read as the same chop on a 10 m pond or a 500 m plane",
        // but it cancelled the fetch term it was compensating for and left the rendered height a
        // function of wind alone - about 2.7 mm at the shipped defaults. An authored height in metres
        // is already scale-independent, so the compensation has nothing left to do.
        float NormalizeAmplitudes(float sumAmpSquared, float significantHeight, float verticalExtent)
        {
            if (sumAmpSquared <= 0f || significantHeight <= 0f)
            {
                for (int i = 0; i < _count; i++) _waves[i].amp = 0f;
                return 0f;
            }

            float currentRms = Mathf.Max(Mathf.Sqrt(sumAmpSquared), MinRms);
            float targetRms = significantHeight / HsToRms;      // metres
            // Pre-divide by the vertical extent so PoolToWorld's height * extent.y leaves the world
            // crest height fixed as the pool deepens. This mirrors how click-ripples compensate on
            // injection.
            float scale = (targetRms / currentRms) / verticalExtent;

            for (int i = 0; i < _count; i++) _waves[i].amp *= scale;

            float poolRms = significantHeight / verticalExtent / SignificantHeightToRms;
            return poolRms * poolRms;
        }

        // Group envelopes and the Stokes crest term, in the units the samplers work in: phase in
        // metres, height in pool units. Precomputed here because they are pure functions of the
        // authored state - the per-sample cost is two sines for the envelope and one multiply-add
        // for the crest, whatever the component count.
        void BuildShaping(float peakWavelength, Vector2 windDir, float grouping, float crestSharpness,
                          float poolVariance, float verticalExtent, float timeScale)
        {
            float depth = Mathf.Clamp01(grouping);
            float carrierK = TwoPi / peakWavelength;
            // The envelope of a group travels at the CARRIER's group velocity, not its own phase
            // speed - that is what makes individual crests appear at the back of a set and die at
            // the front instead of the whole pattern sliding rigidly.
            // Scaled by the same time factor as the components: slowing the waves without slowing the
            // sets would leave the envelope sliding through a field that is no longer keeping up with it.
            float groupSpeed = GroupVelocityFraction * Mathf.Sqrt(Gravity / carrierK) * timeScale;
            GroupA = BuildGroup(windDir, GroupAngleARadians, peakWavelength * GroupLengthWavesA, groupSpeed);
            GroupB = BuildGroup(windDir, GroupAngleBRadians, peakWavelength * GroupLengthWavesB, groupSpeed);

            // env = base + amplitude * (sinA + sinB). Mean is 'base'; the two sines are independent
            // with variance 1/2 each, so E[env^2] = base^2 * (1 + depth^2 / 4). Normalising by that
            // keeps the authored height fixed as grouping rises - otherwise the sets slider would
            // double as a height slider.
            float envelopeNorm = 1f / Mathf.Sqrt(1f + depth * depth * 0.25f);
            float envelopeAmplitude = envelopeNorm * depth * 0.5f;

            // Stokes coefficient in POOL height units. The physical term is (k/2) * h_world^2; with
            // h_world = h_pool * verticalExtent that becomes (k * verticalExtent / 2) * h_pool^2.
            float stokes = Mathf.Max(0f, crestSharpness) * StokesSecondOrderFactor * carrierK * verticalExtent;
            float stokesOffset = stokes * poolVariance;     // removes the DC the quadratic would add
            StokesNorm = 1f / Mathf.Sqrt(1f + StokesVarianceGrowth * stokes * stokes * poolVariance);
            Shape = new Vector4(envelopeNorm, envelopeAmplitude, stokes, stokesOffset);
        }

        static Vector4 BuildGroup(Vector2 windDir, float angleRadians, float envelopeWavelength, float groupSpeed)
        {
            Vector2 dir = Rotate(windDir, angleRadians);
            float k = TwoPi / Mathf.Max(envelopeWavelength, MinWavelength);
            return new Vector4(dir.x, dir.y, k, k * groupSpeed);
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

        // ---- shaping, mirrored exactly by WaterWaves.hlsl ------------------------------------------
        // Every sampler below runs the same three stages: sum the linear components, multiply by the
        // group envelope, then apply the Stokes crest term. They are kept as small named helpers
        // rather than inlined three times so the CPU and the shader can be read side by side.

        float GroupEnvelope(Vector2 meters, float time, out float envelopeRate, out Vector2 envelopeGradient)
        {
            float argA = (GroupA.x * meters.x + GroupA.y * meters.y) * GroupA.z - GroupA.w * time;
            float argB = (GroupB.x * meters.x + GroupB.y * meters.y) * GroupB.z - GroupB.w * time;
            float cosA = Mathf.Cos(argA), cosB = Mathf.Cos(argB);
            float amplitude = Shape.y;
            envelopeRate = amplitude * (-GroupA.w * cosA - GroupB.w * cosB);
            envelopeGradient = amplitude * (new Vector2(GroupA.x, GroupA.y) * (GroupA.z * cosA)
                                            + new Vector2(GroupB.x, GroupB.y) * (GroupB.z * cosB));
            return Shape.x + amplitude * (Mathf.Sin(argA) + Mathf.Sin(argB));
        }

        // h -> norm * (h + stokes * h^2 - stokesOffset). Derivative factor is norm * (1 + 2*stokes*h).
        float StokesSharpen(float height) => StokesNorm * (height + Shape.z * height * height - Shape.w);
        float StokesDerivativeFactor(float height) => StokesNorm * (1f + 2f * Shape.z * height);

        /// <summary>Height (pool/world units) of the wave layer at pool xz in [-1, 1].
        /// <paramref name="minWavelengthMeters"/> > 0 skips components shorter than it, so a large
        /// floater rides the swell without buzzing on ripples finer than the object (LOD filtering).</summary>
        public float SampleHeight(float poolX, float poolZ, float time, float metersPerPoolUnit, float minWavelengthMeters = 0f)
        {
            var meters = new Vector2(poolX * metersPerPoolUnit, poolZ * metersPerPoolUnit);
            float linear = 0f;
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                if (IsFilteredOut(w, minWavelengthMeters)) continue;
                linear += w.amp * Mathf.Sin(Phase(w, meters, time));
            }
            return StokesSharpen(linear * GroupEnvelope(meters, time, out _, out _));
        }

        /// <summary>Vertical surface velocity d(height)/dt (pool units / s) of the wave layer at pool
        /// xz. Closed-form time derivative of <see cref="SampleHeight"/>, so buoyancy gets an exact
        /// wave velocity with no cross-frame state. Not uploaded to the shader (velocity is
        /// physics-only, never rendered), so there is no HLSL mirror to keep in lockstep.</summary>
        public float SampleVerticalVelocity(float poolX, float poolZ, float time, float metersPerPoolUnit, float minWavelengthMeters = 0f)
        {
            var meters = new Vector2(poolX * metersPerPoolUnit, poolZ * metersPerPoolUnit);
            float linear = 0f, linearRate = 0f;
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                if (IsFilteredOut(w, minWavelengthMeters)) continue;
                float arg = Phase(w, meters, time);
                linear += w.amp * Mathf.Sin(arg);
                linearRate += w.amp * -w.omega * Mathf.Cos(arg);
            }
            float envelope = GroupEnvelope(meters, time, out float envelopeRate, out _);
            // Product rule through the envelope, then the chain rule through the Stokes term.
            return StokesDerivativeFactor(linear * envelope) * (linearRate * envelope + linear * envelopeRate);
        }

        /// <summary>Gradient d(height)/d(poolXZ) of the wave layer (pool units).
        /// <paramref name="minWavelengthMeters"/> > 0 skips components shorter than it (LOD filtering).</summary>
        public Vector2 SampleSlope(float poolX, float poolZ, float time, float metersPerPoolUnit, float minWavelengthMeters = 0f)
        {
            var meters = new Vector2(poolX * metersPerPoolUnit, poolZ * metersPerPoolUnit);
            float linear = 0f, gx = 0f, gz = 0f;
            for (int i = 0; i < _count; i++)
            {
                Wave w = _waves[i];
                if (IsFilteredOut(w, minWavelengthMeters)) continue;
                float arg = Phase(w, meters, time);
                linear += w.amp * Mathf.Sin(arg);
                // d/d(poolXZ) introduces a factor k * dir * d(m)/d(poolXZ) = k * dir * metersPerUnit.
                float common = w.amp * Mathf.Cos(arg) * w.k * metersPerPoolUnit;
                gx += common * w.dir.x;
                gz += common * w.dir.y;
            }
            float envelope = GroupEnvelope(meters, time, out _, out Vector2 envelopeGradient);
            envelopeGradient *= metersPerPoolUnit;   // the envelope's phase is in metres too
            return StokesDerivativeFactor(linear * envelope)
                   * (new Vector2(gx, gz) * envelope + envelopeGradient * linear);
        }

        static float Phase(Wave w, Vector2 meters, float time)
            => (w.dir.x * meters.x + w.dir.y * meters.y) * w.k - w.omega * time + w.phase;

        // A component is dropped when its wavelength (2*pi / wavenumber) is shorter than the requested
        // minimum - the LOD cut that lets big objects ignore small ripples. 0 keeps every component.
        static bool IsFilteredOut(Wave w, float minWavelengthMeters)
            => minWavelengthMeters > 0f && TwoPi / w.k < minWavelengthMeters;

        // --- spectrum helpers ---------------------------------------------------
        // Unnormalised JONSWAP density at omega (relative weight only). Only the SHAPE across the band
        // matters now - the absolute level comes from the authored significant height - so the alpha
        // scale the original carried had no job and is gone.
        static float Jonswap(float omega, float omegaPeak)
        {
            if (omega <= 0f) return 0f;
            float pm = Mathf.Pow(omega, -5f) * Mathf.Exp(-1.25f * Mathf.Pow(omegaPeak / omega, 4f));
            float sigma = omega <= omegaPeak ? JonswapSigmaLow : JonswapSigmaHigh;
            float r = Mathf.Exp(-Mathf.Pow(omega - omegaPeak, 2f) / (2f * sigma * sigma * omegaPeak * omegaPeak));
            return pm * Mathf.Pow(JonswapGamma, r);
        }

        // Deep-water dispersion: omega^2 = g * k, k = 2*pi / wavelength.
        static float OmegaFromWavelength(float wavelength) => Mathf.Sqrt(Gravity * TwoPi / wavelength);

        static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
        }

        // ---- legacy migration ----------------------------------------------------------------------
        // The pre-rewrite layer's ACTUAL rendered output, so MigrateWindWaveRigV11 can carry a scene's
        // look across to the new authored fields instead of guessing at it. These live here rather than
        // in the migration because they are this class's own retired constants, and nothing else should
        // ever call them.
        //
        // Height: the fetch growth law and the sqrt(reference * metersPerUnit) divisor cancel to a
        // constant, leaving Hs = 4 * sqrt(coeff * 2 / (reference * g)) * wind * amplitudeScale -
        // independent of the fetch field entirely. (Checked against the shipped defaults: wind 3,
        // amplitude scale 4 gives 2.741 mm, matching a direct evaluation of the old code path.)
        const float LegacyFetchEnergyCoeff = 1.6e-7f;
        const float LegacyScaleReferenceMeters = 10f;
        const float LegacyJonswapPeakFactor = 22f;
        const float LegacyMinPeakWavelength = 0.6f;
        const float LegacyMaxPeakWavelength = 6f;
        const float LegacyMinWind = 0.1f;
        const float LegacyMinFetch = 1f;

        internal static float LegacySignificantHeight(float windSpeed, float amplitudeScale)
            => SignificantHeightToRms
               * Mathf.Sqrt(LegacyFetchEnergyCoeff * 2f / (LegacyScaleReferenceMeters * Gravity))
               * Mathf.Max(0f, windSpeed) * Mathf.Max(0f, amplitudeScale);

        internal static float LegacyWavelength(float windSpeed, float fetchFieldMeters)
        {
            float wind = Mathf.Max(LegacyMinWind, windSpeed);
            float fetch = Mathf.Max(LegacyMinFetch, 2f * fetchFieldMeters);
            float omegaPeak = LegacyJonswapPeakFactor * Mathf.Pow(Gravity * Gravity / (wind * fetch), 1f / 3f);
            float wavelength = TwoPi * Gravity / (omegaPeak * omegaPeak);
            return Mathf.Clamp(wavelength, LegacyMinPeakWavelength, LegacyMaxPeakWavelength);
        }
    }
}
