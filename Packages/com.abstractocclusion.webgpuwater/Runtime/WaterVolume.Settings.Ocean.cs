// WaterVolume settings - open water: the ocean spectrum, the FFT cascades and the geometry
// clipmap, including the derived clipmap dimensions (pure functions of the two authored knobs).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [Range(0f, 32f)] [SerializeField] internal float simWindowEdgeFadeTexels = 8f;

        [Header("Ocean (open water, clipmap, god rays, whitecaps)")]
        [SerializeField] OceanSettings ocean = new OceanSettings();

        /// <summary>Open-water / ocean look: the standalone surface, its horizon clipmap, large-body god
        /// rays and FFT whitecap foam. All ocean-only - inert on pools / bounded lakes. Migrated off the
        /// flat WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// and the derived helpers below unchanged. (Consts and derived helpers stay on WaterVolume.)</summary>
        [System.Serializable]
        public sealed class OceanSettings
        {
            [Header("Open water (lake / ocean) - EXPERIMENTAL")]
            [Tooltip("Render this body as open water: the surface stands alone with NO analytic pool. " +
                     "The refracted view falls back to the deep-water colour where there is no scene " +
                     "geometry, and the mesh god rays are suppressed (the large-body render feature " +
                     "replaces them). OFF = the original pool / small-body look, byte-for-byte unchanged. " +
                     "Publishes the _LargeBody shader flag; the clipmap + FFT modules read the same flag.")]
            public bool openWater = false;
            [Tooltip("Open-water SWELL height multiplier. The big waves' scale and direction come from " +
                     "the Wind Waves section (wind speed scales the swell, wind heading steers it); " +
                     "this is an artistic multiplier on top, like Wave Amplitude Scale is for the small " +
                     "waves. 0 = no big swell (small wind waves remain).")]
            [Min(0f)] public float largeWaveAmplitude = 1f;
            [Tooltip("Open-water CHOPPINESS: horizontal Gerstner displacement that sharpens wave crests. " +
                     "0 = smooth sine swell (byte-for-byte the previous look); higher = sharper, more " +
                     "ocean-like peaks. Buoyancy inverts it, so floaters still ride the visible crest.")]
            [Range(0f, LargeWaveChoppinessMax)] public float largeWaveChoppiness = 0f;
            [Tooltip("Long-period SWELL height (metres): tall, slow, rolling waves that keep the open sea " +
                     "moving toward the horizon, layered on top of the wind chop. 0 = no long swell.")]
            [Min(0f)] public float swellHeight = 0f;
            [Tooltip("Wavelength (metres) of the longest swell component. Bigger = longer, slower rolls.")]
            [Min(1f)] public float swellWavelength = DefaultSwellWavelength;
            [Tooltip("Extend this open-water body's surface to the HORIZON with a camera-following clipmap " +
                     "mesh (an OCEAN, not a bounded lake). Requires Open Water ON and the large-body sim " +
                     "window (near-field ripples fade to flat past it). OFF = the surface stays the bounded " +
                     "footprint plane, unchanged. Drawing water past the shore would be wrong for a lake, so " +
                     "this is opt-in.")]
            public bool unboundedOcean = false;
            [Tooltip("BOUNDED open water only: metres over which the whole wave field (swell, chop, FFT, " +
                     "surf, whitecaps) feathers to the rest level toward the footprint border, so the " +
                     "surface never ends mid-wave as a standing wall of water. Ignored on an Unbounded " +
                     "ocean (its clipmap has no border). 0 = off.")]
            [Range(0f, EdgeFeatherMetersMax)] public float edgeFeatherMeters = DefaultEdgeFeatherMeters;

            [Header("Ocean clipmap (unbounded open water)")]
            [Tooltip("Cells per side of each geometry-clipmap LOD level (even). Higher = finer far-field " +
                     "tessellation and less wave 'swim' when the camera moves, at more vertices.")]
            [Min(ClipmapMinGridResolution)] public int clipmapGridResolution = DefaultClipmapGridResolution;
            [Tooltip("Target horizon reach (metres) of the outermost LOD level: the number of levels is " +
                     "derived so the ocean reaches at least this far. Drives the camera far plane too.")]
            [Min(ClipmapMinRadius)] public float clipmapOuterRadius = DefaultClipmapOuterRadius;
            [Tooltip("Far-field band-limit: how fast the shortest DRAWN wavelength grows with camera distance " +
                     "(metres of wavelength per metre of distance). Keeps the long rolling swell out to the " +
                     "horizon while dropping short chop the coarse far mesh can't resolve (which would crawl). " +
                     "Lower = waves reach further (needs denser Clipmap Rings); higher = calms sooner.")]
            [Min(0f)] public float oceanDetailFalloff = DefaultOceanDetailFalloff;
            [Tooltip("Distance (metres) at which the ocean surface fully dissolves into the horizon sky, so " +
                     "the far mesh edge has no hard line. 0 = off. A light stopgap - real horizon softening " +
                     "is the future fog pass. Set near the Clipmap Outer Radius to try it.")]
            [Min(0f)] public float horizonFadeDistance = 0f;
            [Tooltip("Atmosphere colour the far ocean dissolves toward at the horizon. Alpha controls how much " +
                     "it overrides the reflected sky: 0 = pure sky (seamless, the natural default), 1 = fully " +
                     "this colour (a coloured haze band). Only used when Horizon Haze Density > 0.")]
            public Color horizonHazeColor = DefaultHorizonHazeColor;
            [Tooltip("Horizon haze AMOUNT (0 = off, 1 = strongest) - the far ocean dissolves toward the " +
                     "horizon sky colour. Mapped internally to a gentle distance-haze so the whole 0..1 range " +
                     "is usable; ~0.3-0.5 reads as a light atmospheric haze. (Previously a raw per-metre " +
                     "density where anything over ~0.001 saturated instantly - re-enter as a 0..1 amount.)")]
            [Range(0f, 1f)] public float horizonHazeDensity = 0f;

            [Header("Ocean god rays (large-body light shafts)")]
            [Tooltip("Shaft colour, multiplied by the sun colour. Only used when God Ray Density > 0.")]
            public Color largeGodRayColor = DefaultLargeGodRayColor;
            [Tooltip("Master intensity of the ocean god-ray shafts. 0 = off (also the gate: the fullscreen " +
                     "shaft pass is skipped entirely). Raise for brighter volumetric beams.")]
            [Min(0f)] public float largeGodRayDensity = 0f;
            [Tooltip("Shafts seen from ABOVE the water, THROUGH AN EXCLUSION VOLUME'S WINDOW - a " +
                     "sunken room's pane, a hull opening. 0 = underwater only (the default, and the " +
                     "look this asset shipped with). Above water the shafts draw ONLY where the view " +
                     "ray crosses the waterline INSIDE a carve: over open sea the surface shader owns " +
                     "the view and beams there would be painted onto water the viewer is not inside, " +
                     "but looking through a pane genuinely IS looking into a lit water volume. Scales " +
                     "the shafts relative to the submerged view, which always renders full strength.")]
            [Range(0f, 1f)] public float largeGodRayFromAir = 0f;
            [Tooltip("Raymarch samples per pixel for the ocean shafts - SEPARATE from the pool god-ray steps. " +
                     "More = smoother beams, higher cost.")]
            [Range(LargeGodRayMinSteps, LargeGodRayMaxSteps)] public int largeGodRaySteps = DefaultLargeGodRaySteps;
            [Tooltip("Forward-scattering (Mie / Henyey-Greenstein g): 0 = even glow, higher = beams brighten " +
                     "sharply when looking toward the sun, like real shafts through haze.")]
            [Range(0f, LargeGodRayMaxAnisotropy)] public float largeGodRayAnisotropy = DefaultLargeGodRayAnisotropy;
            [Tooltip("Distance extinction (per metre) that thins the shafts as they recede, so the far ocean " +
                     "does not over-glow. 0 = no distance falloff.")]
            [Min(0f)] public float largeGodRayExtinction = 0f;
            [Tooltip("How strongly the near-field surface caustics brighten and flicker the shafts (the shimmer " +
                     "close to the camera, inside the sim window). 0 = plain shadow shafts. Needs the Large Body " +
                     "Caustics Shader assigned.")]
            [Min(0f)] public float largeGodRayCausticStrength = DefaultLargeGodRayCausticStrength;
            [Tooltip("Shaft caustic smoothing radius (metres): the beams focus only through waves LONGER than " +
                     "about twice this, so the shimmer rides the slow swell instead of fast wind ripple. The " +
                     "rendered surface keeps its full detail. 0 = full spectrum (fast, harsh pinpoint flicker).")]
            [Range(0f, 10f)] public float largeGodRayCausticSmooth = 2f;
            [Tooltip("How quickly the shaft shimmer blurs and calms with the sample's depth below the surface " +
                     "(softening per metre): deep beams read broad and slow instead of razor sharp, like real " +
                     "light losing focus. 0 = sharp at any depth.")]
            [Range(0f, 1f)] public float largeGodRayCausticDepthSoften = 0.25f;
            [Tooltip("Speed of the caustic's OWN ripple field (1 = physical wave speed for its wavelength). " +
                     "The caustic runs a dedicated small-wave layer decoupled from the surface (the surface's " +
                     "small content is FFT-driven and cannot be slowed), so beam/shimmer pace is a direct dial.")]
            [Range(0.05f, 1f)] public float largeCausticTimeScale = 0.5f;
            [Tooltip("Dominant wavelength (metres) of the caustic's own ripple field - the small waves that " +
                     "trigger the shafts. Smaller = finer, denser beams; larger = broad slow bands.")]
            [Range(0.5f, 10f)] public float largeCausticRippleScale = 3f;
            [Tooltip("Strength of the dedicated caustic ripples. 0 = caustic from the smoothed swell only.")]
            [Range(0f, 2f)] public float largeCausticRippleStrength = 1f;
            [Tooltip("EXTRA softening (in mip levels) for the SCREEN-SPACE caustics painted on the seabed - " +
                     "the light shafts are never affected, they keep reading the sharp map. A floor is applied " +
                     "automatically so the projection never samples finer than the generator grid actually " +
                     "resolves; raise this only if you want the pattern softer than physically necessary.")]
            [Range(0f, ProjectionSoftenMax)] public float largeCausticProjectionSoften = 0f;

            [Header("Ocean foam (whitecaps)")]
            [Tooltip("Wind speed (m/s) below which the FFT ocean grows NO whitecaps (KWS foams above ~4). Tie " +
                     "to the same Wind Speed that drives the swell: calmer seas stay foam-free. Ocean-only.")]
            [Min(0f)] public float oceanFoamWindThreshold = DefaultOceanFoamWindThreshold;
            [Tooltip("How readily a folding wave crest turns to foam. 1 = only where the surface actually pinches " +
                     "(the natural default); higher spreads foam onto gentler folds; lower needs sharper breaks. " +
                     "Needs Large Wave Choppiness above 0 for crests to fold at all.")]
            [Range(0f, OceanFoamCoverageMax)] public float oceanFoamCoverage = DefaultOceanFoamCoverage;
            [Tooltip("How fast foam builds up on breaking crests. Higher = denser whitecaps sooner.")]
            [Range(0f, OceanFoamStrengthMax)] public float oceanFoamStrength = DefaultOceanFoamStrength;
            [Tooltip("How fast foam fades once a crest passes (per second). Lower = foam lingers and streaks; " +
                     "higher = it dies back quickly. This is what stops whitecaps flickering frame to frame.")]
            [Range(0f, OceanFoamFadeRateMax)] public float oceanFoamFadeRate = DefaultOceanFoamFadeRate;
            [Tooltip("Whitecap tint (RGB) and overall opacity (alpha) where foam sits on the surface. White is " +
                     "the natural default; alpha 0 hides the surface foam entirely (accumulation still runs).")]
            public Color oceanFoamColor = Color.white;
            [Tooltip("Metres per tile of the Foam Pattern texture on the ocean surface. Smaller = finer, more " +
                     "repeated lace; larger = broader foam shapes. Uses the material's Foam Pattern slot.")]
            [Min(OceanFoamTileSizeMin)] public float oceanFoamTileSize = DefaultOceanFoamTileSize;
            [Tooltip("How softly the foam texture dissolves in as coverage rises. 0 = hard edges; higher = a " +
                     "gentle feathered fade from water to foam.")]
            [Range(0f, 1f)] public float oceanFoamFeather = DefaultOceanFoamFeather;
            [Tooltip("How much foam is left behind (deposited) after a crest passes. Higher = dense whitecaps " +
                     "linger and streak into trails; 0 = foam fades as fast as it forms. This is the main " +
                     "'deposit' control.")]
            [Range(0f, 1f)] public float oceanFoamDeposit = DefaultOceanFoamDeposit;
            [Tooltip("How fast deposited foam rolls downwind, streaking into windrows (as a fraction of wind " +
                     "speed). 0 = foam stays where it formed.")]
            [Range(0f, OceanFoamDriftMax)] public float oceanFoamDrift = DefaultOceanFoamDrift;
            [Tooltip("Ceiling on how dense foam can pile up before accumulation stops. Higher = thicker, " +
                     "longer-lasting deposits (1 = the original ceiling).")]
            [Range(OceanFoamMaxBuildupMin, OceanFoamMaxBuildupMax)] public float oceanFoamMaxBuildup = DefaultOceanFoamMaxBuildup;
        }

        // Same-named forwarding accessors so every reader (WaterUniformPublisher, the derived helpers
        // below, the clipmap/FFT setup, ShouldWindow/IsOceanClipmap) is unchanged. Names are the exact
        // former field names; the derived helpers (PascalCase, e.g. LargeWaveChoppiness) read these.
        internal bool openWater => ocean.openWater;
        internal float largeWaveAmplitude => ocean.largeWaveAmplitude;
        internal float largeWaveChoppiness => ocean.largeWaveChoppiness;
        internal float swellHeight => ocean.swellHeight;
        internal float swellWavelength => ocean.swellWavelength;
        internal bool unboundedOcean => ocean.unboundedOcean;
        internal float edgeFeatherMeters => ocean.edgeFeatherMeters;
        internal int clipmapGridResolution => ocean.clipmapGridResolution;
        internal float clipmapOuterRadius => ocean.clipmapOuterRadius;
        internal float oceanDetailFalloff => ocean.oceanDetailFalloff;
        internal float horizonFadeDistance => ocean.horizonFadeDistance;
        internal Color horizonHazeColor => ocean.horizonHazeColor;
        internal float horizonHazeDensity => ocean.horizonHazeDensity;
        internal Color largeGodRayColor => ocean.largeGodRayColor;
        internal float largeGodRayDensity => ocean.largeGodRayDensity;
        internal int largeGodRaySteps => ocean.largeGodRaySteps;
        internal float largeGodRayAnisotropy => ocean.largeGodRayAnisotropy;
        internal float largeGodRayExtinction => ocean.largeGodRayExtinction;
        internal float largeGodRayCausticStrength => ocean.largeGodRayCausticStrength;
        internal float oceanFoamWindThreshold => ocean.oceanFoamWindThreshold;
        internal float oceanFoamCoverage => ocean.oceanFoamCoverage;
        internal float oceanFoamStrength => ocean.oceanFoamStrength;
        internal float oceanFoamFadeRate => ocean.oceanFoamFadeRate;
        internal Color oceanFoamColor => ocean.oceanFoamColor;
        internal float oceanFoamTileSize => ocean.oceanFoamTileSize;
        internal float oceanFoamFeather => ocean.oceanFoamFeather;
        internal float oceanFoamDeposit => ocean.oceanFoamDeposit;
        internal float oceanFoamDrift => ocean.oceanFoamDrift;
        internal float oceanFoamMaxBuildup => ocean.oceanFoamMaxBuildup;

        // Legacy capture (scenes/prefabs from before this migration) -> copied once by MigrateOceanV2.
        // Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("openWater")] bool _legacyOpenWater = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveAmplitude")] float _legacyLargeWaveAmplitude = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveChoppiness")] float _legacyLargeWaveChoppiness = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellHeight")] float _legacySwellHeight = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellWavelength")] float _legacySwellWavelength = DefaultSwellWavelength;
        [SerializeField, HideInInspector, FormerlySerializedAs("unboundedOcean")] bool _legacyUnboundedOcean = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("clipmapOuterRadius")] float _legacyClipmapOuterRadius = DefaultClipmapOuterRadius;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanDetailFalloff")] float _legacyOceanDetailFalloff = DefaultOceanDetailFalloff;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonFadeDistance")] float _legacyHorizonFadeDistance = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonHazeColor")] Color _legacyHorizonHazeColor = DefaultHorizonHazeColor;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonHazeDensity")] float _legacyHorizonHazeDensity = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayColor")] Color _legacyLargeGodRayColor = DefaultLargeGodRayColor;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayDensity")] float _legacyLargeGodRayDensity = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRaySteps")] int _legacyLargeGodRaySteps = DefaultLargeGodRaySteps;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayAnisotropy")] float _legacyLargeGodRayAnisotropy = DefaultLargeGodRayAnisotropy;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayExtinction")] float _legacyLargeGodRayExtinction = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayCausticStrength")] float _legacyLargeGodRayCausticStrength = DefaultLargeGodRayCausticStrength;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamWindThreshold")] float _legacyOceanFoamWindThreshold = DefaultOceanFoamWindThreshold;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamCoverage")] float _legacyOceanFoamCoverage = DefaultOceanFoamCoverage;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamStrength")] float _legacyOceanFoamStrength = DefaultOceanFoamStrength;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamFadeRate")] float _legacyOceanFoamFadeRate = DefaultOceanFoamFadeRate;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamColor")] Color _legacyOceanFoamColor = Color.white;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamTileSize")] float _legacyOceanFoamTileSize = DefaultOceanFoamTileSize;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamFeather")] float _legacyOceanFoamFeather = DefaultOceanFoamFeather;

        // The open-water swell shares the body's wind settings so one wind drives both wave scales.
        // ReferenceWind maps the default breeze (windSpeed 3) to a x1 swell; stronger wind grows it,
        // calm flattens it. Both the shader publisher and the CPU buoyancy read these, so they match.
        const float LargeWaveReferenceWind = 3f;
        // Crest's _Chop range; beyond this the Gerstner surface self-intersects (pinch-through) and the
        // buoyancy inversion stops converging, so the knob is clamped here.
        const float LargeWaveChoppinessMax = 2f;
        // Edge guard defaults: 10 m rides out the default swell without visibly shrinking a lake;
        // the slider cap keeps the feather from eating a small bounded body whole.
        const float DefaultEdgeFeatherMeters = 10f;
        const float EdgeFeatherMetersMax = 50f;
        // Ocean whitecap foam defaults - subtle + wind-gated so the current look is unchanged until dialed.
        const float DefaultOceanFoamWindThreshold = 4f; // KWS FOAM_MIN_WIND: no whitecaps below ~4 m/s
        const float DefaultOceanFoamCoverage = 1f;      // fold threshold; 1 == the original saturate(1 - jacobian)
        const float DefaultOceanFoamStrength = 1f;      // accumulation gain per unit fold
        const float DefaultOceanFoamFadeRate = 0.5f;    // exponential decay per second (lower = foam lingers)
        const float OceanFoamCoverageMax = 2f;          // beyond ~2 the whole surface foams; clamp the knob
        const float OceanFoamStrengthMax = 4f;          // sane upper bound for the build-up gain slider
        const float OceanFoamFadeRateMax = 4f;          // fastest useful decay; higher just flickers
        const float DefaultOceanFoamTileSize = 8f;      // metres per foam-pattern tile on the surface
        const float OceanFoamTileSizeMin = 0.5f;        // guard the divide + keep the pattern from collapsing
        const float DefaultOceanFoamFeather = 0.25f;    // dissolve softness of the foam texture black point
        // Deposit knobs (promoted from OceanFft.compute #defines so they're art-tweakable). Defaults lean
        // toward MORE deposit than the old constants (slow-fade 0.25 -> deposit 0.85 = slow-fade 0.15).
        const float DefaultOceanFoamDeposit = 0.85f;    // dense-foam persistence; slowFadeFraction = 1 - this
        const float DefaultOceanFoamDrift = 0.08f;      // downwind roll speed as a fraction of wind speed
        const float OceanFoamDriftMax = 0.3f;           // fastest useful roll before foam smears across the tile
        const float DefaultOceanFoamMaxBuildup = 1f;    // accumulation ceiling (1 = the original FoamMax)
        const float OceanFoamMaxBuildupMin = 0.25f;
        const float OceanFoamMaxBuildupMax = 3f;
        internal float LargeWaveHeadingRad => windFromDegrees * Mathf.Deg2Rad;
        internal float LargeWaveAmplitudeEffective => largeWaveAmplitude * (windSpeed / LargeWaveReferenceWind);
        internal float LargeWaveChoppiness => largeWaveChoppiness;
        // Edge guard is a BOUNDED-body concept: an unbounded ocean's clipmap has no footprint border,
        // so the feather is forced off there (and pools never read it - _LargeBody gates the field).
        internal float LargeWaveEdgeFeatherEffective => (openWater && !unboundedOcean) ? edgeFeatherMeters : 0f;
        internal float SwellHeight => swellHeight;
        internal float SwellWavelength => swellWavelength;
        internal float OceanFoamWindThreshold => oceanFoamWindThreshold;
        internal float OceanFoamCoverage => oceanFoamCoverage;
        internal float OceanFoamStrength => oceanFoamStrength;
        internal float OceanFoamFadeRate => oceanFoamFadeRate;
        internal Color OceanFoamColor => oceanFoamColor;
        internal float OceanFoamTileSize => oceanFoamTileSize;
        internal float OceanFoamFeather => oceanFoamFeather;
        internal float OceanFoamDeposit => oceanFoamDeposit;
        internal float OceanFoamDrift => oceanFoamDrift;
        internal float OceanFoamMaxBuildup => oceanFoamMaxBuildup;
        const float DefaultSwellWavelength = 140f;
        // Default horizon haze target: pale sky-blue, but alpha 0 so out of the box the far ocean
        // dissolves into the REAL reflected sky (seamless). The rgb only matters once alpha is raised.
        static readonly Color DefaultHorizonHazeColor = new Color(0.7f, 0.8f, 0.9f, 0f);
        // Ocean god-ray defaults + guard rails. Density 0 keeps the whole shaft pass off out of the box.
        static readonly Color DefaultLargeGodRayColor = new Color(1f, 0.97f, 0.85f, 1f);
        const int LargeGodRayMinSteps = 8;
        const int LargeGodRayMaxSteps = 96;
        const int DefaultLargeGodRaySteps = 24;
        const float LargeGodRayMaxAnisotropy = 0.95f;
        const float DefaultLargeGodRayAnisotropy = 0.6f;
        const float DefaultLargeGodRayCausticStrength = 4f;

        // Geometry-clipmap authoring + guard rails. Grid resolution = cells per side of each LOD level;
        // the level count is derived so the outermost reaches clipmapOuterRadius (the horizon target).
        const int DefaultClipmapGridResolution = 64;
        // Beyond this the projected pattern is averaged away to a flat wash - the same "flattened to
        // near-DC" failure the shaft caustic term is documented against.
        internal const float ProjectionSoftenMax = 4f;
        const int ClipmapMinGridResolution = 8;
        const int ClipmapMaxLevels = 12;
        const int ClipmapMinLevels = 2;
        const int ClipmapSnapCellMultiple = 2;    // each level snaps to 2*cell so its even cells align with the coarser level
        const int ClipmapHoleMarginCells = 2;     // shrink each level's hole so it overlaps the finer level (no seam gap)
        const float ClipmapMorphBandFraction = 0.5f; // fraction of the annulus half-width used for the edge geomorph
        const float DefaultClipmapOuterRadius = 10000f;
        const float DefaultOceanDetailFalloff = 0.03f; // low: the clipmap resolves waves far out, so the
                                                       // swell rolls near to the horizon before band-limiting
        const float ClipmapMinRadius = 1e-3f;
        // The clipmap's central hole is set a little INSIDE the near-field patch so the patch (which
        // carries a depth bias) covers the seam; beyond the patch, only the clipmap draws.
        const float ClipmapPatchOverlap = 0.9f;
        // Frustum-cull AABB size for an ocean body: large enough to always intersect the frustum
        // (the ocean is everywhere), matching the clipmap mesh's own huge bounds.
        const float OceanCullBoundsSize = 1_000_000f;

        // True when this body renders its surface as an unbounded ocean clipmap: needs open water, the
        // opt-in flag, AND the sim window (its ripple fade is what keeps the far field clean). Bounded
        // lakes / pools are always false, so their render path is untouched.
        internal bool IsOceanClipmap => openWater && unboundedOcean && _windowed;

        // --- Derived geometry-clipmap dimensions (all pure functions of the two authored knobs:
        //     clipmapGridResolution and clipmapOuterRadius, plus the shared patch extent). ---
        // Cells per side, clamped and forced even (the annulus needs a symmetric hole).
        int ClipmapGridRes { get { int m = Mathf.Max(ClipmapMinGridResolution, clipmapGridResolution); return m + (m & 1); } }
        // Hole half-width in cells, shrunk by the overlap margin so each level overlaps the finer one.
        int ClipmapHoleHalfCells => Mathf.Max(1, ClipmapGridRes / 4 - ClipmapHoleMarginCells);
        // Finest cell size (metres) so the innermost level's hole sits just inside the near-field patch.
        float ClipmapBaseCell => (ClipmapPatchOverlap * SimHorizontalExtent) / ClipmapHoleHalfCells;
        // Level 0's outer reach (metres); each further level doubles it.
        float ClipmapLevel0Reach => (ClipmapGridRes / 2f) * ClipmapBaseCell;
        // Levels needed for the outermost to reach at least the horizon target.
        int ClipmapLevelCount
        {
            get
            {
                float ratio = Mathf.Max(1f, clipmapOuterRadius / Mathf.Max(ClipmapLevel0Reach, 1e-3f));
                int levels = 1 + Mathf.CeilToInt(Mathf.Log(ratio, 2f));
                return Mathf.Clamp(levels, ClipmapMinLevels, ClipmapMaxLevels);
            }
        }
        // World reach of the outermost level - drives the camera far plane so the horizon isn't clipped.
        float ClipmapOuterReach => ClipmapLevel0Reach * Mathf.Pow(2f, ClipmapLevelCount - 1);

        // Band-limit slope for the shader. 0 for non-ocean bodies -> no band-limit -> the bounded
        // open-water surface keeps its full spectrum everywhere (unchanged).
        internal float OceanDetailSlope => IsOceanClipmap ? oceanDetailFalloff : 0f;
        // Horizon fade distance for the shader. 0 for non-ocean bodies -> no fade (unchanged).
        internal float HorizonFadeDistance => IsOceanClipmap ? horizonFadeDistance : 0f;
        // Horizon haze for the shader: density gated to 0 for non-ocean bodies so pools/lakes are never
        // hazed; the colour passes through (inert while density is 0).
        internal float HorizonHazeDensity => IsOceanClipmap ? horizonHazeDensity : 0f;
        internal Color HorizonHazeColor => horizonHazeColor;
        // Ocean god rays for the shader: density gated to 0 for non-ocean bodies (pools/lakes never get
        // shafts from this pass); the rest pass through (inert while density is 0).
        internal Color LargeGodRayColor => largeGodRayColor;
        // THE TIER GATE, and it had been missing here entirely. _godRaysAllowed was read in exactly
        // ONE place (WaterVolume.Update.cs, for godRayRenderer) and only when !_windowed - but
        // IsOceanClipmap REQUIRES _windowed, so an ocean body never reached it and a tier that turns
        // god rays off could not switch the ocean shafts off at all. Folding it in HERE fixes both
        // halves at once, because this one property is what LargeBodyAtmosphereGate tests to decide
        // whether to enqueue the raymarch pass AND what WriteBodyUniforms publishes as the density -
        // so the pass stops being recorded and the uniform reads 0 from a single line.
        internal float LargeGodRayDensity
            => (IsOceanClipmap && _godRaysAllowed) ? largeGodRayDensity : 0f;
        // THE TIER IS A CEILING, NOT AN OVERRIDE. This used to return the authored field raw, so the
        // tier's step count never reached the ocean shader and Low marched the authored 24 exactly
        // like High (the pool shafts never had this bug - they read _godRaySteps directly, because
        // there is no authored per-body step count on that path). Min() keeps the author's intent
        // wherever the budget allows it: at High, authored 24 under a 32-step ceiling is still 24.
        internal float LargeGodRaySteps => Mathf.Min(largeGodRaySteps, _godRaySteps);
        internal float LargeGodRayAnisotropy => largeGodRayAnisotropy;
        internal float LargeGodRayExtinction => largeGodRayExtinction;
        internal float LargeGodRayCausticStrength => IsOceanClipmap ? largeGodRayCausticStrength : 0f;
        internal float LargeGodRayCausticSmooth => ocean.largeGodRayCausticSmooth;
        internal float LargeGodRayCausticDepthSoften => ocean.largeGodRayCausticDepthSoften;
        /// <summary>Strength of the from-air (through-a-carve-pane) shafts relative to the
        /// submerged view. Ocean-only, like every other shaft term.</summary>
        internal float LargeGodRayFromAir => IsOceanClipmap ? ocean.largeGodRayFromAir : 0f;
        internal float LargeCausticTimeScale => ocean.largeCausticTimeScale;
        internal float LargeCausticRippleScale => ocean.largeCausticRippleScale;
        internal float LargeCausticRippleStrength => ocean.largeCausticRippleStrength;

        /// <summary>Mip bias the screen-space caustic projection samples the caustic RT at.
        /// The generator flat-shades ONE value per grid cell (the focus term is an area Jacobian
        /// across the projected triangle, and ddx/ddy of a linearly interpolated attribute is
        /// constant over a triangle), so a caustic RT larger than the grid stores each cell as a
        /// block of identical texels - visible as hard pixelation, and NOT fixable by changing the
        /// RT resolution because the information content is set by the grid. Sampling at
        /// log2(rt / grid) puts one texel back on one cell, which is the finest level that carries
        /// real detail; the artist term adds softness beyond that. The shafts do not use this - they
        /// keep their own LOD, so the beam banding stays sharp.</summary>
        internal float LargeCausticProjectionLod
        {
            get
            {
                float cellsPerTexel = EffectiveCausticResolution
                                    / (float)Mathf.Max(1, SimResolution);
                float gridFloor = Mathf.Max(0f, Mathf.Log(Mathf.Max(1f, cellsPerTexel), 2f));
                return gridFloor + ocean.largeCausticProjectionSoften;
            }
        }

        [Header("Water body (multi-instance)")]
        [Tooltip("Renderers driven by THIS body via a MaterialPropertyBlock (surface above/under, " +
                 "pool, god rays). Assigned by the scene builder.")]
        [SerializeField] internal Renderer surfaceAbove;
        [SerializeField] internal Renderer surfaceUnder;
        [SerializeField] internal Renderer poolRenderer;
        [SerializeField] internal Renderer godRayRenderer;

        // True when this body draws the analytic/procedural pool (tiles). Surface-only bodies have no
        // pool renderer, so the surface shader must not sample pool tiles in their refraction.
        internal bool HasProceduralPool => poolRenderer != null;
        [Tooltip("The primary body also mirrors its data to global shader state, the fallback " +
                 "for objects that don't carry a WaterMembership (which otherwise resolves each " +
                 "object's own containing body). Exactly one body should be primary.")]
        [SerializeField] private bool isPrimary = true;
        [Tooltip("On Play, automatically add a WaterMembership to any scene renderer that uses a " +
                 "water material (receiver / pool wall) and doesn't already have one, so a crate " +
                 "or custom pool is lit and fogged by the body it actually sits in - no manual " +
                 "wiring. Only the primary body runs the one-time scan.")]
        [SerializeField] private bool autoLinkReceivers = true;

        /// <summary>Whether this body is the primary one (mirrors its data to global shader
        /// state and acts as the fallback for objects without a WaterMembership).</summary>
        public bool IsPrimary { get => isPrimary; set => isPrimary = value; }

        [Header("Performance (Phase 3)")]
        [Tooltip("Quality tier asset scaling sim/caustic resolution and god-ray steps. Leave " +
                 "empty for the default (256/1024/24) look. Assigned by the scene builder.")]
        [SerializeField] private WaterQuality quality;
        [Tooltip("Pause a body's simulation, caustics and height readback - and stop drawing it - " +
                 "when it is off-screen OR beyond Activation Distance, and let only the nearest few " +
                 "bodies simulate at once. A single visible body is unaffected. Turn off to force " +
                 "this body to always simulate and render.")]
        [SerializeField] private bool enableCulling = true;
        [Tooltip("Bodies whose centre is farther than this from the camera pause their simulation " +
                 "(they hold their last state). Matches the camera far clip by default.")]
        [SerializeField] internal float activationDistance = CameraFarClip;

        /// <summary>Quality tier asset scaling sim/caustic resolution and god-ray steps.
        /// Read at startup; assign before the body enables.</summary>
        public WaterQuality Quality { get => quality; set => quality = value; }

        /// <summary>Pause this body's simulation and rendering when off-screen or beyond the
        /// activation distance.</summary>
        public bool EnableCulling { get => enableCulling; set => enableCulling = value; }
    }
}
