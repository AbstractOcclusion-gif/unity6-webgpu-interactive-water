// WebGpuWater - the ocean sea state: JONSWAP/TMA spectrum shape, the cascade layout derived from it,
// and the CPU-side normalisation that turns the shape into an authored significant wave height.
//
// WHY THIS FILE EXISTS AT ALL. The spectrum lives in OceanFft.compute, on the GPU, sampled on a
// k-lattice. Nothing on the GPU can integrate that lattice - there is no reduction pass, and a readback
// would land a frame or two after the amplitudes it was supposed to scale. So the same shape is
// evaluated once here, over the identical lattice, to produce a single scalar gain per spectrum change.
// That is the price of "Significant Height" being a number in METRES rather than a calibrated fudge:
// one mirrored function, guarded by WaterWaveConstantsValidator, exactly as LargeWaveField mirrors
// WaterLargeWaves.hlsl.
//
// The mirror is deliberately NARROW - only what the normalisation integral needs. Everything about how
// the field is evolved, transformed and shaded stays on the GPU alone.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Sea-state maths shared by the FFT dispatch and its CPU normalisation. Pure functions.</summary>
    internal static class WaterOceanSpectrum
    {
        internal const float Gravity = 9.81f;

        // HLSL pair: OCEAN_JONSWAP_* / OCEAN_TMA_* in OceanFft.compute (validator-guarded). Same values
        // the analytic bank runs (WaterWaveBank.JonswapSigmaLow/High) - one sea, described once.
        internal const float JonswapPeakDecay = 1.25f;
        internal const float JonswapSigmaLow = 0.07f;
        internal const float JonswapSigmaHigh = 0.09f;
        internal const float TmaSlope = 1.8f;
        internal const float TmaOffset = 1.125f;

        // HLSL pair: OCEAN_SWELL_* (validator-guarded). The directional-spreading constants used to be
        // mirrored here too; they went with the spreading itself once ComputeGains stopped needing it
        // (see there), and a guard on a constant no C# reads is worse than no guard - it implies the two
        // sides are kept in step when only one of them exists.
        internal const float SwellWidth = 0.12f;
        internal const float SwellDirPower = 6f;

        // --- cascade layout -------------------------------------------------------------------------
        // Longest wave the COARSEST cascade carries, as a multiple of the peak wavelength. JONSWAP's
        // low-frequency flank is exp(-1.25 (w_p/w)^4); at twice the peak wavelength that is exp(-5),
        // i.e. under 1% of the peak density, so two peak wavelengths is where the sea genuinely ends.
        // It also sets the coarsest TILE to 2 * oversample = 8 peak wavelengths, comfortably inside the
        // 4-8 periods per tile that keeps the dominant wave from quantising to a handful of bins.
        internal const float TopBandPeakMultiple = 2f;

        // Each finer cascade's band top, as a fraction of the one above. 1/phi^3 - IRRATIONAL, and that
        // is the entire point: the tiles are the band tops times a fixed oversample, so a rational ratio
        // makes the tiles commensurate and the whole ocean repeats at their lowest common multiple. The
        // shipped 5/20/100/600 m bands gave tiles of 20/80/400/2400 m, whose LCM is 2400 m - about eight
        // identical copies of the sea across a 10 km horizon. An irrational ratio has no common multiple,
        // so the summed field never exactly repeats.
        //
        // The magnitude (~1/4.24) is chosen to span roughly four octaves per step, which puts four
        // cascades across the ~6 octaves between the peak wavelength and the shortest wave a 128-texel
        // tile can resolve.
        internal const float CascadeBandRatio = 0.2360679775f;

        // Per-cascade view distance as a multiple of its band top. This is not a new number: the shipped
        // visible areas (40/160/800/4800 m) are EXACTLY eight times the shipped band tops (5/20/100/600 m),
        // so the relationship was always there - it was just frozen into a literal array that silently
        // assumed a 5-600 m sea. Derived, it follows the sea state, and a miniature ocean no longer fades
        // itself out at 40 m.
        internal const float VisibleAreaBandMultiple = 8f;

        const float MinWavelength = 1e-3f;
        const float MinAngularFreq = 1e-6f;
        const float MinVariance = 1e-12f;   // guards the divide when a band set carries no energy at all
        const float SignificantHeightToRms = 4f; // Hs = 4 sqrt(m0)

        // The share of the spectral energy that survives into the RENDERED surface, and the reason it is
        // not 1. Each lattice cell carries a circularly-symmetric complex Gaussian amplitude, so the
        // inverse transform is a complex field whose real and imaginary parts have equal variance - and
        // FftVertical keeps only the real part (gX/gY/gZ .x). Half the energy therefore never reaches the
        // height field, and summing |h|^2 over the lattice overstates the surface variance by exactly 2.
        //
        // Verified numerically against a full replica of the pipeline (spectrum -> H0 -> unnormalised
        // inverse DFT -> measured variance) at 64/128/256 across six seeds: 0.483 / 0.502 / 0.517, i.e.
        // 1/2 with the sampling noise a single tile of this size carries. Without it every authored
        // height would render about 71% of its metres - close enough to look plausible and never be
        // questioned, which is exactly why it is pinned down here rather than absorbed into a fudge.
        const float RealPartVarianceShare = 0.5f;

        /// <summary>Peak angular frequency (rad/s) of a deep-water wave of this wavelength.</summary>
        internal static float PeakAngularFrequency(float peakWavelength)
            => Mathf.Sqrt(Gravity * (2f * Mathf.PI / Mathf.Max(peakWavelength, MinWavelength)));

        /// <summary>Band tops (metres) per cascade, ASCENDING, derived from the peak wavelength.</summary>
        /// <remarks>
        /// Ascending order is load-bearing well beyond this file: slice 0 being the FINEST cascade is
        /// what the turbulence floors, the foam per-cascade damping and the far-field slope floor all
        /// index against. The derivation therefore builds from the coarsest end and fills backwards.
        /// </remarks>
        internal static float[] DeriveCascadeBands(float peakWavelength, int cascades)
        {
            var bands = new float[cascades];
            float top = Mathf.Max(peakWavelength, MinWavelength) * TopBandPeakMultiple;
            for (int i = cascades - 1; i >= 0; i--)
            {
                bands[i] = top;
                top *= CascadeBandRatio;
            }
            return bands;
        }

        /// <summary>JONSWAP/TMA density as a 2D density in k. HLSL pair: OceanJonswapOmni.</summary>
        internal static float JonswapOmni(float waveNumber, float peakAngularFreq, float peakSharpness, float seaDepth)
        {
            if (waveNumber < MinAngularFreq) return 0f;
            float omega = Mathf.Sqrt(Gravity * waveNumber);
            float peakRatio = peakAngularFreq / omega;
            float peakRatio2 = peakRatio * peakRatio;
            float shape = Mathf.Exp(-JonswapPeakDecay * peakRatio2 * peakRatio2) / Mathf.Pow(omega, 5f);

            float sigma = (omega <= peakAngularFreq) ? JonswapSigmaLow : JonswapSigmaHigh;
            float peakOffset = (omega - peakAngularFreq) / Mathf.Max(sigma * peakAngularFreq, MinAngularFreq);
            shape *= Mathf.Pow(Mathf.Max(peakSharpness, 1f), Mathf.Exp(-0.5f * peakOffset * peakOffset));

            if (seaDepth > 0f)
                shape *= 0.5f + 0.5f * (float)System.Math.Tanh(
                    TmaSlope * (omega * Mathf.Sqrt(seaDepth / Gravity) - TmaOffset));

            float dOmegaDk = 0.5f * Mathf.Sqrt(Gravity / waveNumber);
            return shape * dOmegaDk / waveNumber;
        }

        /// <summary>HLSL pair: OceanSwellShape. Unit-less ring shape; the gain carries the height.</summary>
        internal static float SwellShape(Vector2 waveVector, float waveNumber, float swellWavelength, Vector2 windDir)
        {
            if (waveNumber < MinAngularFreq) return 0f;
            float kSwell = 2f * Mathf.PI / Mathf.Max(swellWavelength, MinWavelength);
            float width = kSwell * SwellWidth;
            float offset = waveNumber - kSwell;
            float radial = Mathf.Exp(-(offset * offset) / (2f * width * width));
            float alignment = Mathf.Clamp01(Vector2.Dot(waveVector / waveNumber, windDir));
            return radial * Mathf.Pow(alignment, SwellDirPower);
        }

        /// <summary>The two spectrum gains that make the authored heights come out in metres.</summary>
        /// <remarks>
        /// Walks the exact lattice SpectrumInit walks and accumulates the surface variance each shape
        /// would produce at unit gain, then inverts m0 = (Hs/4)^2. The wind sea and the swell are
        /// summed under one square root in the kernel but enter the VARIANCE additively (|a+b| under
        /// sqrt means the squared amplitude is a+b, with no cross term), so one pass yields both gains
        /// independently - which is what lets Swell Height stay meaningful while the sea state changes.
        ///
        /// WIND IS DELIBERATELY ABSENT, and that is a performance decision as much as a physical one.
        /// The directional spreading is energy-conserving by construction (SpreadingNormalisation
        /// restores the ring integral), so summing spreadNorm * (D(+t) + D(-t)) over a full ring of
        /// lattice directions comes to exactly the omnidirectional density - measured identical to
        /// five decimal places at peak wavelengths from 3 m to 400 m, across turbulence 0 to 0.9 and
        /// four headings. Including it would make every wind change - a heading animated by a hair,
        /// a gust curve - re-run this whole integral for a provably identical answer, which is the
        /// difference between a one-off authoring cost and a per-frame hitch.
        ///
        /// The swell lobe is likewise evaluated along a FIXED reference direction: heading only
        /// rotates it, and a continuous integral over the ring is invariant to that. What survives is
        /// a few percent of lattice-sampling wobble on a narrow ring, which is worth far less than
        /// re-integrating whenever the wind turns.
        ///
        /// Cost is resolution^2 * cascades evaluations, and it now runs ONLY when the spectrum's SHAPE
        /// changes - the same edge that redefines the cascade layout.
        /// </remarks>
        internal static void ComputeGains(in Layout layout, in SeaState sea,
                                          out float windSeaGain, out float swellGain)
        {
            float windSeaVariance = 0f;
            float swellVariance = 0f;
            bool swellActive = sea.SwellHeight > 0f;
            float half = layout.Resolution * 0.5f;

            for (int slice = 0; slice < layout.Cascades; slice++)
            {
                float domain = Mathf.Max(layout.DomainSizes[slice], MinWavelength);
                float dk = 2f * Mathf.PI / domain;
                float measure = dk * dk;
                float bandMin = layout.BandMin[slice];
                float bandMax = layout.BandMax[slice];

                for (int y = 0; y < layout.Resolution; y++)
                for (int x = 0; x < layout.Resolution; x++)
                {
                    var waveVector = new Vector2((x - half) * dk, (y - half) * dk);
                    float waveNumber = waveVector.magnitude;
                    if (waveNumber < MinAngularFreq) continue;
                    float wavelength = 2f * Mathf.PI / waveNumber;
                    if (wavelength <= bandMin || wavelength > bandMax) continue;

                    windSeaVariance += measure
                                     * JonswapOmni(waveNumber, sea.PeakAngularFreq, sea.PeakSharpness, sea.SeaDepth);

                    if (!swellActive) continue;
                    swellVariance += measure
                                   * (SwellShape(waveVector, waveNumber, sea.SwellWavelength, SwellReferenceDirection)
                                      + SwellShape(-waveVector, waveNumber, sea.SwellWavelength, SwellReferenceDirection));
                }
            }

            windSeaGain = GainForHeight(sea.SignificantHeight, windSeaVariance);
            swellGain = GainForHeight(sea.SwellHeight, swellVariance);
        }

        // See ComputeGains: the swell integral is rotation-invariant in the continuum, so the lobe is
        // integrated along one fixed axis rather than following the authored heading.
        static readonly Vector2 SwellReferenceDirection = new Vector2(1f, 0f);

        // A band set that carries no energy for this sea (a swell wavelength outside every band, say)
        // must yield gain 0, NOT a division blow-up that would turn one surviving bin into the ocean.
        static float GainForHeight(float significantHeight, float unitVariance)
        {
            if (significantHeight <= 0f || unitVariance < MinVariance) return 0f;
            float targetRms = significantHeight / SignificantHeightToRms;
            return targetRms * targetRms / (unitVariance * RealPartVarianceShare);
        }

        /// <summary>The k-lattice the cascades are sampled on. Fixed once the body is built.</summary>
        internal readonly struct Layout
        {
            internal readonly int Resolution;
            internal readonly int Cascades;
            internal readonly Vector4 DomainSizes;
            internal readonly Vector4 BandMin;
            internal readonly Vector4 BandMax;
            internal Layout(int resolution, int cascades, Vector4 domainSizes, Vector4 bandMin, Vector4 bandMax)
            {
                Resolution = resolution; Cascades = cascades;
                DomainSizes = domainSizes; BandMin = bandMin; BandMax = bandMax;
            }
        }

        /// <summary>The spectrum SHAPE - the only things the gains and the cascade layout depend on.</summary>
        /// <remarks>Wind is absent by design: see ComputeGains. It steers the field, it does not size it.</remarks>
        internal readonly struct SeaState
        {
            internal readonly float SignificantHeight;
            internal readonly float PeakAngularFreq;
            internal readonly float PeakSharpness;
            internal readonly float SeaDepth;
            internal readonly float SwellHeight;
            internal readonly float SwellWavelength;
            internal SeaState(float significantHeight, float peakAngularFreq, float peakSharpness,
                              float seaDepth, float swellHeight, float swellWavelength)
            {
                SignificantHeight = significantHeight; PeakAngularFreq = peakAngularFreq;
                PeakSharpness = peakSharpness; SeaDepth = seaDepth;
                SwellHeight = swellHeight; SwellWavelength = swellWavelength;
            }
        }
    }
}
