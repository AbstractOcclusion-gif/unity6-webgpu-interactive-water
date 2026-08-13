// WaterVolume settings - the bed: terrain-baked depth, the shore/surf field it feeds, and the
// depth-driven clarity ramp.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        [Header("Bed depth (real terrain depth - EXPERIMENTAL)")]
        [SerializeField] BedDepthSettings bedDepthSettings = new BedDepthSettings();

        /// <summary>Real water-column depth from a baked terrain bed (shoreline gradient) vs flat-floor.
        /// Migrated off the flat WaterVolume fields into this block (Phase 2); the same-named accessors
        /// keep every reader (WaterBedBaker, the publisher) unchanged.</summary>
        [System.Serializable]
        public sealed class BedDepthSettings
        {
            [Tooltip("Use the baked terrain bed height for real water-column depth (shoreline " +
                     "gradient). Off = flat-floor behaviour.")]
            public bool useBedDepth = false;
            [Tooltip("Terrain whose heightmap defines the lake bed. Auto-resolves to the active " +
                     "Terrain if empty. Baked once at startup; call RebakeBed() (or the context-menu " +
                     "item) if the terrain changes.")]
            public Terrain bedTerrain;
            [Tooltip("Resolution of the baked pool-space bed-height map.")]
            [Range(WaterBedBaker.MinResolution, WaterBedBaker.MaxResolution)] public int bedResolution = 256;
            [Tooltip("Colour the surface tints toward over deep water.")]
            public Color deepWaterColor = new Color(0.02f, 0.10f, 0.15f);
            [Tooltip("World-unit depth at which the deep-water tint reaches ~63% toward the deep " +
                     "colour. Smaller = the water darkens in shallower depth.")]
            [Range(0.1f, 50f)] [FormerlySerializedAs("shorelineFadeDepth")] public float bedFadeDepth = 6f;
            [Tooltip("Maximum tint toward the deep-water colour.")]
            [Range(0f, 1f)] [FormerlySerializedAs("shorelineStrength")] public float bedTintStrength = 0.8f;

            [Header("Depth clarity (auto water transparency from the bed depth)")]
            [Tooltip("Drive water clarity from the baked bed depth: turbidity, underwater-fog reach and " +
                     "the deep-water tint all follow ONE depth curve. Off = the flat per-body look. Needs " +
                     "a baked bed (Use Bed Depth on).")]
            public bool clarityFromDepth = false;
            [Tooltip("Column depth (m) treated as fully SHALLOW on the clarity curve.")]
            [Range(0f, 50f)] public float clarityShallowDepth = 0.5f;
            [Tooltip("Column depth (m) treated as fully DEEP on the clarity curve.")]
            [Range(0f, 50f)] public float clarityDeepDepth = 8f;
            [Tooltip("Clarity at the SHALLOW end (1 = clear/see-through, 0 = murky).")]
            [Range(0f, 1f)] public float clarityShallow = 1f;
            [Tooltip("Clarity at the DEEP end (1 = clear, 0 = murky). Default: deep water reads murkier.")]
            [Range(0f, 1f)] public float clarityDeep = 0f;
            [Tooltip("How strongly the depth curve pushes the look vs the flat per-body turbidity/fog. 0 = off.")]
            [Range(0f, 1f)] public float clarityStrength = 1f;
            [Tooltip("Depth (world metres) over which the open-water swell shoals toward shore. Waves keep " +
                     "full height in water deeper than this and calm within it toward the waterline; larger " +
                     "reaches the calming further out into deeper water. 0 = no shoaling.")]
            [Range(0f, 30f)] public float shoreShoalDepth = 4f;

            [Header("Shore waves (shoal transform + surf breaker fronts)")]
            [Tooltip("Bend shoaling waves toward the shore so crests swing parallel to the beach. " +
                     "0 = waves keep the wind heading everywhere.")]
            [Range(0f, 1f)] public float shoreRefraction = 0.7f;
            [Tooltip("Crest bunching near the waterline (waves slow down in the shallows, so the " +
                     "spacing compresses). 0 = off; above ~1.5 crests start looking glued together.")]
            [Range(0f, 1.5f)] public float shoreCompression = 0.6f;
            [Tooltip("Green's-law growth cap: how much shoaling waves are allowed to GROW before " +
                     "breaking/attenuation takes them. 1 = no growth (old behaviour).")]
            [Range(1f, 2f)] public float shoreGreens = 1.35f;
            [Tooltip("Run automatic surf breaker fronts along the shoreline (needs the bed depth + " +
                     "SDF baked). Shore-parallel wave fronts shoal, break and run whitewash in.")]
            public bool surfEnabled = true;
            [Tooltip("Deep-water amplitude (metres) of the surf sets feeding the fronts.")]
            [Range(0f, 3f)] public float surfAmplitude = 0.8f;
            [Tooltip("Derive the front spacing from the period by deep-water dispersion " +
                     "(L = 0.2 x 1.56 x T^2), so one Period knob drives both the rhythm and the " +
                     "spacing and fronts move at a physically-linked speed. At the default 9 s " +
                     "period the derived spacing (~25 m) matches the old 26 m default. Off = tune " +
                     "the spacing by hand below.")]
            public bool surfWavelengthAuto = true;
            [Tooltip("Spacing (metres) between surf fronts offshore. Manual - only read when the " +
                     "Auto toggle above is off.")]
            [Range(SurfWavelengthMin, SurfWavelengthMax)] public float surfWavelength = 26f;
            // Slider bounds, shared with the auto-derived clamp in SurfWavelengthEffective.
            public const float SurfWavelengthMin = 4f;
            public const float SurfWavelengthMax = 120f;
            [Tooltip("Seconds between fronts arriving at a fixed point (the surf rhythm).")]
            [Range(2f, 30f)] public float surfPeriod = 9f;
            [Tooltip("Column depth (metres) at which fronts are fully developed; they fade in from " +
                     "deeper water and break where the depth criterion says.")]
            [Range(0.5f, 20f)] public float surfBandDepth = 6f;
            [Tooltip("Amplitude variation between wave sets (waves come in sets). 0 = every front " +
                     "identical; 1 = strong lulls between sets.")]
            [Range(0f, 1f)] public float surfSetStrength = 0.55f;
            [Tooltip("Alongshore length (metres) of individual crest segments. Long bands break " +
                     "into finite crests of roughly this size, with calm gaps between them.")]
            [Range(10f, 300f)] public float surfCrestLength = 60f;
            [Tooltip("How deeply the crest segmentation modulates the fronts. 0 = endless " +
                     "shore-long bands (old look); 1 = strongly broken-up individual crests.")]
            [Range(0f, 1f)] public float surfCrestVariation = 0.6f;
            [Tooltip("How anchored the crest segmentation is across waves. 0 = every front gets a " +
                     "fresh random pattern (foam hot spots wander wave to wave); 1 = successive " +
                     "waves break at nearly the same alongshore spots, migrating slowly like a " +
                     "real sandbank - the right feel for visible breaking lips.")]
            [Range(0f, 1f)] public float surfCrestPersistence = 0f;
            [Tooltip("Gate surf by shore exposure to the swell direction: the coast facing the " +
                     "wind gets the surf, the lee side calms down. 0 = surf everywhere.")]
            [Range(0f, 1f)] public float surfDirectionality = 0.7f;
            [Tooltip("Forward lean of the cresting front (fraction of local height thrown shoreward).")]
            [Range(0f, 1f)] public float surfLean = 0.35f;
            [Tooltip("How much the ambient swell/FFT fades where the surf fronts own the surface " +
                     "(prevents double crests). 1 = fronts fully replace the ambient waves near shore.")]
            [Range(0f, 1f)] public float surfAmbientFade = 0.8f;
            [Tooltip("Multiplier on the physical Hunt run-up (swash height = Iribarren x deep-water " +
                     "set height, from the baked beach slope). 1 = physics; 0 = classic hard " +
                     "waterline. Pre-SURF-PHYS scenes tuned in metres should reset this to 1.")]
            [Range(0f, 3f)] public float surfSwashAmplitude = 1f;
            [Tooltip("Beach slope (degrees) above which swash stops. Swash is a BEACH process - a " +
                     "film running up a slope gentle enough to hold it - and the physical run-up " +
                     "below GROWS with slope, so on a cliff the model surges instead of stopping. " +
                     "Faded in over the approach to this angle, never a hard line. 89 = uncapped, " +
                     "0 = no swash anywhere.")]
            [Range(0f, 89f)] public float surfSwashMaxSlopeDegrees = 35f;
            [Tooltip("Whitewash + breaker foam injected into the interactive foam sim near shore.")]
            [Range(0f, 4f)] public float surfFoamGain = 1.2f;
            [Tooltip("Standing foam lace hugging the waterline, independent of the front rhythm.")]
            [Range(0f, 2f)] public float surfWaterlineFoam = 0.5f;
            [Tooltip("Small-wave foam: puts foam on the CREST and a short TAIL of gentle shore waves " +
                     "that never break (the whitewash/crest-curve foam only fire on breaking waves, so " +
                     "small waves render bare). Fades out on waves that DO break (their whitewash takes " +
                     "over). 0 = off.")]
            [Range(0f, 2f)] public float surfSmallWaveFoam = 0f;

            [Header("Surf foam look (dedicated - decoupled from ripple & ocean foam)")]
            [Tooltip("Coverage scale of the surf whitewash layer (bores, trails, geometry foam).")]
            [Range(0f, 2f)] public float surfFoamStrength = 1f;
            [Tooltip("Dissolve softness of the whitewash lace at its coverage threshold. Small = " +
                     "crisp hard-edged foam shapes; larger = softer, mistier edges.")]
            [Range(0.01f, 1f)] public float surfFoamFeather = 0.2f;
            [Tooltip("Metres per foam-pattern tile on the surf whitewash.")]
            [Range(0.5f, 30f)] public float surfFoamTileSize = 8f;
            [Tooltip("Whitewash tint (RGB) and master opacity (A).")]
            public Color surfFoamColor = Color.white;

            [Header("Surf foam enhancement (pop curve / repartition / swash) - all render-only")]
            [Tooltip("Drive WHEN crest foam pops with the artist curve below instead of the " +
                     "built-in window. Off = legacy look, byte-identical.")]
            public bool surfCrestFoamCurveEnabled = false;
            [Tooltip("Crest-foam intensity over the front's lifecycle clock (x = H over the " +
                     "breaking limit, 0..2; breaking starts at ~1). The default bump reproduces " +
                     "the built-in pop window - drag keys to pop earlier/later, add a small " +
                     "early bump for pre-break spume, hold the tail for lingering lip foam.")]
            public AnimationCurve surfCrestFoamCurve = new AnimationCurve(
                new Keyframe(0.75f, 0f), new Keyframe(1.05f, 1f),
                new Keyframe(1.5f, 0f), new Keyframe(2f, 0f));
            [Tooltip("Master gain on the curve-driven crest foam.")]
            [Range(0f, 3f)] public float surfCrestFoamGain = 1f;
            [Tooltip("Crest cap: keeps bright foam ON the breaking crest through the bore, so a " +
                     "broken wave doesn't go bald on top with all the foam piling at its base. " +
                     "Fires even without the pop curve. 0 = off (legacy: crest foam only from the " +
                     "pop curve, if enabled).")]
            [Range(0f, 2f)] public float surfFoamCrestCap = 0f;
            [Tooltip("Whitewash weight of the BORE HEAD (the churned mound riding the broken " +
                     "front). 1 = legacy balance.")]
            [Range(0f, 2f)] public float surfFoamBoreGain = 1f;
            [Tooltip("Whitewash weight of the TRAILING DEPOSIT left behind the front. 1 = " +
                     "legacy balance.")]
            [Range(0f, 2f)] public float surfFoamTrailGain = 1f;
            [Tooltip("Length multiplier of the trailing deposit (1 = legacy). Longer trails " +
                     "read as heavier churn; keep below ~2 so neighbouring fronts' foam never " +
                     "merges into one static carpet.")]
            [Range(0.2f, 3f)] public float surfFoamTrailLength = 1f;
            [Tooltip("Seconds an aged deposit takes to rot into holes behind the bore (real " +
                     "foam dies by holes opening, not by fading). 0 = off (legacy uniform look).")]
            [Range(0f, 20f)] public float surfFoamTrailDissolve = 0f;
            [Tooltip("Swash foam strength: a foamy line rides the uprush film, strands at the " +
                     "wash border, then dissolves through the reflux. 0 = off.")]
            [Range(0f, 2f)] public float surfSwashFoam = 0.8f;
            [Tooltip("Metres of run-up height the swash foam band covers around the film edge " +
                     "and the stranded line.")]
            [Range(0.02f, 1f)] public float surfSwashFoamWidth = 0.25f;
            [Tooltip("How hard reflux age erodes the stranded foam line into lace holes (0 = " +
                     "the line only drains with the next uprush).")]
            [Range(0f, 1f)] public float surfSwashFoamDissolve = 0.6f;
            [Tooltip("Persistent swash deposits: the backwash strands foam into the interactive " +
                     "foam BUFFER, so deposits LINGER across waves and fade over real time instead " +
                     "of the per-cycle analytic fade. How LONG they last is set by the body's Foam " +
                     "Decay (thin-foam residual). Needs interactive Foam enabled on the body. " +
                     "0 = off (analytic swash only).")]
            [Range(0f, 2f)] public float surfSwashDepositGain = 0f;
        }

        // Same-named forwarding accessors keep every reader unchanged (WaterBedBaker, the publisher).
        internal bool useBedDepth => bedDepthSettings.useBedDepth;
        internal Terrain bedTerrain => bedDepthSettings.bedTerrain;
        internal int bedResolution => bedDepthSettings.bedResolution;
        internal Color deepWaterColor => bedDepthSettings.deepWaterColor;
        internal float bedFadeDepth => bedDepthSettings.bedFadeDepth;
        internal float bedTintStrength => bedDepthSettings.bedTintStrength;
        internal bool clarityFromDepth => bedDepthSettings.clarityFromDepth;
        internal float clarityShallowDepth => bedDepthSettings.clarityShallowDepth;
        internal float clarityDeepDepth => bedDepthSettings.clarityDeepDepth;
        internal float clarityShallow => bedDepthSettings.clarityShallow;
        internal float clarityDeep => bedDepthSettings.clarityDeep;
        internal float clarityStrength => bedDepthSettings.clarityStrength;
        internal float shoreShoalDepth => bedDepthSettings.shoreShoalDepth;
        internal float shoreRefraction => bedDepthSettings.shoreRefraction;
        internal float shoreCompression => bedDepthSettings.shoreCompression;
        internal float shoreGreens => bedDepthSettings.shoreGreens;
        internal bool surfEnabled => bedDepthSettings.surfEnabled;
        internal float surfAmplitude => bedDepthSettings.surfAmplitude;
        /// <summary>Front amplitude actually fed to the surf layer: floored at the body's swell
        /// height, so the fronts never carry LESS energy than the ambient swell they replace at
        /// the hand-over line - otherwise waves visibly "grow then shrink" at the surf-band edge
        /// instead of continuing in. One definition for the publisher, foam push and CPU mirror.</summary>
        // REVERTED to the swell-only floor. Flooring this on the whole offshore field was correct in
        // spirit - a bigger sea should arrive as bigger surf - and wrong in practice: surfAmplitude is
        // authored on a 0-3 m slider, so an 8 m sea state drove the fronts to 8 m, nearly three times
        // the largest value the front renderer was ever built for, and overrode a hand-tuned coast
        // outright. Coupling the two is still worth doing, but as a proportional term the artist keeps
        // control of, not as a hard floor that silently wins.
        internal float SurfAmplitudeEffective => Mathf.Max(surfAmplitude, SwellHeight);

        // Depth (metres) over which the open-water field shoals, floored so that it always starts
        // FURTHER OUT than the sea can survive. A wave breaks at roughly H = 0.78 * depth, so a wave of
        // height H needs about 1.3 H of water to still exist; the largest waves in a sea run near twice
        // the significant height, which puts the last surviving crest at somewhere over 2 Hs of depth.
        // Flooring the band there means "shoaling flattens the sea before it reaches the beach" holds at
        // ANY sea state, instead of only at the ~2 m sea the fixed 4 m default was tuned against - which
        // is how a 3 m ocean ended up standing at full height in 3 m of water.
        //
        // A FLOOR, not a replacement: a hand-authored deeper band still wins, so a gently shelving coast
        // can start its shoaling as far out as it likes.
        internal float ShoreShoalDepthEffective
            => Mathf.Max(shoreShoalDepth, ShoalBandSignificantHeightMultiple * OffshoreSignificantHeight);

        // See ShoreShoalDepthEffective: 2 x Hs is where the biggest waves of a sea have broken.
        const float ShoalBandSignificantHeightMultiple = 2f;
        internal float surfWavelength => bedDepthSettings.surfWavelength;
        internal float surfPeriod => bedDepthSettings.surfPeriod;

        // Deep-water dispersion: L0 = g/(2 pi) * T^2 = 1.56 * T^2 - LOCKSTEP with
        // SURF_DEEPWATER_LENGTH_COEF in WaterSurfWaves.hlsl / SurfDeepwaterLengthCoef in
        // LargeWaveField.cs. The auto spacing is this fraction of L0 (0.2 lands the default
        // 9 s period on ~25 m, matching the historical hand default of 26 m).
        // Aliases the validator-guarded LargeWaveField mirror of SURF_DEEPWATER_LENGTH_COEF (the
        // deep-water dispersion coefficient, L0 = coef * T^2) rather than re-authoring 1.56 a third
        // time - same pattern as SurfBeatWrapFronts just below. It cannot drift.
        const float SurfDispersionLengthCoef = LargeWaveField.SurfDeepwaterLengthCoef;
        const float SurfAutoWavelengthFraction = 0.2f;
        // Fronts per master-beat wrap - aliases the validator-guarded LargeWaveField mirror of
        // SURF_BEAT_WRAP_FRONTS (must stay a multiple of SURF_SET_WAVES for beat periodicity).
        internal const float SurfBeatWrapFronts = LargeWaveField.SurfBeatWrapFronts;
        // Same period floor as max(_SurfPeriod, SURF_MIN_PERIOD) in the shader - one definition.
        internal float SurfPeriodFloored => Mathf.Max(surfPeriod, LargeWaveField.SurfMinPeriod);

        /// <summary>THE MASTER SURF BEAT: the body's wave clock wrapped to SurfBeatWrapFronts
        /// front periods. Every surf consumer - the _SurfBeatTime global (surface, swash, curl
        /// sheet), the foam state's Time (_ShoreFoamTime: sim injection + particle spray) and the
        /// CPU buoyancy mirror (ShoreWaveContext.SurfBeatTime) - runs on this one clock. Wrapping
        /// keeps the per-front hash argument and the t/T fraction inside float32 precision forever
        /// (the unwrapped clock slowly desynced the render from the CPU mirror); the front field
        /// is exactly periodic in the wrap, so the rollover is seamless.</summary>
        internal float SurfBeatTime => Mathf.Repeat(_waveTime, SurfPeriodFloored * SurfBeatWrapFronts);

        /// <summary>Front spacing actually fed to the surf layer: dispersion-derived from the
        /// period when Auto is on (clamped to the manual slider's bounds), the hand-tuned value
        /// otherwise. One definition for the publisher, warp reach, foam push and CPU mirror.</summary>
        internal float SurfWavelengthEffective
            => bedDepthSettings.surfWavelengthAuto
                ? Mathf.Clamp(SurfAutoWavelengthFraction * SurfDispersionLengthCoef
                              * SurfPeriodFloored * SurfPeriodFloored,
                              BedDepthSettings.SurfWavelengthMin, BedDepthSettings.SurfWavelengthMax)
                : surfWavelength;
        internal float surfBandDepth => bedDepthSettings.surfBandDepth;
        internal float surfSetStrength => bedDepthSettings.surfSetStrength;
        internal float surfCrestLength => bedDepthSettings.surfCrestLength;
        internal float surfCrestVariation => bedDepthSettings.surfCrestVariation;
        internal float surfCrestPersistence => bedDepthSettings.surfCrestPersistence;
        internal float surfDirectionality => bedDepthSettings.surfDirectionality;
        internal float surfLean => bedDepthSettings.surfLean;
        internal float surfAmbientFade => bedDepthSettings.surfAmbientFade;
        internal float surfSwashAmplitude => bedDepthSettings.surfSwashAmplitude;
        // Authored in DEGREES (the only readable unit for a slope) but consumed as a tangent, so the
        // shader and the compute never pay a trig call per pixel or per cell. Clamped rather than
        // left to reach tan(90) = infinity.
        internal float surfSwashMaxSlopeTan
            => Mathf.Min(Mathf.Tan(bedDepthSettings.surfSwashMaxSlopeDegrees * Mathf.Deg2Rad),
                         SurfSwashMaxSlopeTanCeiling);
        const float SurfSwashMaxSlopeTanCeiling = 1000f;
        internal float surfFoamGain => bedDepthSettings.surfFoamGain;
        internal float surfWaterlineFoam => bedDepthSettings.surfWaterlineFoam;
        internal float surfSmallWaveFoam => bedDepthSettings.surfSmallWaveFoam;
        internal float surfFoamStrength => bedDepthSettings.surfFoamStrength;
        internal float surfFoamFeather => bedDepthSettings.surfFoamFeather;
        internal float surfFoamTileSize => bedDepthSettings.surfFoamTileSize;
        internal Color surfFoamColor => bedDepthSettings.surfFoamColor;
        internal float surfCrestFoamGain => bedDepthSettings.surfCrestFoamGain;
        internal float surfFoamCrestCap => bedDepthSettings.surfFoamCrestCap;
        internal float surfFoamBoreGain => bedDepthSettings.surfFoamBoreGain;
        internal float surfFoamTrailGain => bedDepthSettings.surfFoamTrailGain;
        internal float surfFoamTrailLength => bedDepthSettings.surfFoamTrailLength;
        internal float surfFoamTrailDissolve => bedDepthSettings.surfFoamTrailDissolve;
        internal float surfSwashFoam => bedDepthSettings.surfSwashFoam;
        internal float surfSwashFoamWidth => bedDepthSettings.surfSwashFoamWidth;
        internal float surfSwashFoamDissolve => bedDepthSettings.surfSwashFoamDissolve;
        internal float surfSwashDepositGain => bedDepthSettings.surfSwashDepositGain;
    }
}
