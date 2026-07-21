// WebGpuWater - WaterVolume: the serialized CONFIGURATION surface.
// Split out of WaterVolume.cs (final-clean E, verbatim move - any behavior change here is a bug):
// scene-builder wiring fields, the nested per-feature Settings blocks with their forwarding
// accessors, the body registry / receiver autolink statics, the legacy-field capture and the
// versioned settings migrations, and the crest-foam LUT bake. The runtime ORCHESTRATION
// (lifecycle, update loop, solver) stays in WaterVolume.cs.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [Header("Assigned by the scene builder")]
        [SerializeField] internal ComputeShader simCompute;
        // Optional, ocean-only: the FFT-cascade wave compute. Unassigned, or on non-ocean bodies, the
        // analytic large-wave path (WaterLargeWaves.hlsl) is used unchanged. Deliberately NOT part of
        // HasRequiredWiring - a body must run without it so pools/bounded bodies are unaffected.
        [SerializeField] internal ComputeShader oceanFftCompute;
        [SerializeField] internal Shader causticsShader;
        [SerializeField] internal Shader largeBodyCausticsShader; // AbstractOcclusion/WebGpuWater/LargeBodyCaustics - near-field ocean caustics in the sim-window frame (optional; oceans only)
        [SerializeField] internal Shader obstacleShader; // AbstractOcclusion/WebGpuWater/ObstacleDepth - footprint of interactable objects
        [SerializeField] internal Shader occluderShader; // AbstractOcclusion/WebGpuWater/CausticOccluder - refracted-light object shadow into the caustic RT (optional; Shader.Find fallback)
        [SerializeField] internal Mesh waterMesh;        // XY grid plane, [-1,1], shared with the water surface renderers
        [SerializeField] internal Camera targetCamera;
        [SerializeField] internal Light sun;             // directional light: drives water, caustics AND real shadows

        [Header("Textures")]
        // All author-time texture inputs for the water SURFACE look live under this one section (the
        // inspector's "Textures" section gathers these plus the detailNormalSettings map below). The foam
        // pattern and ocean whitecap were previously authored only on the water material; when left empty
        // here that material value is kept untouched, so existing scenes are unchanged.
        [SerializeField] internal Texture tiles;         // pool tile albedo sampled by the water reflection (assign your own)
        [SerializeField] internal Cubemap sky;           // sky cubemap for above-water reflections

        [Tooltip("Surface foam pattern - a single seamless tile, or a flipbook when the grid below is a real " +
                 "grid. Empty = keep the water material's own foam texture (_FoamTex).")]
        [SerializeField] internal Texture foamPatternTexture;
        [Tooltip("Foam flipbook grid (cols, rows). (1,1) = a single seamless tiling texture, no flipbook.")]
        [SerializeField] internal Vector2Int foamPatternGrid = new Vector2Int(1, 1);
        [Tooltip("Foam flipbook frame rate (frames/sec). 0 = a static tile.")]
        [Range(0f, 30f)] [SerializeField] internal float foamPatternFps = 10f;
        [Tooltip("Procedural relief strength derived from the foam pattern (and shared by the ocean whitecap).")]
        [Range(0f, 3f)] [SerializeField] internal float foamReliefStrength = 1f;

        [Tooltip("Ocean wave whitecap - a single tiling texture. Empty = keep the water material's own " +
                 "whitecap texture (_OceanWhitecapTex).")]
        [SerializeField] internal Texture oceanWhitecapTexture;

        [Tooltip("Interactive-ripple detail on a bounded body: higher = a denser sim grid (crisper, " +
                 "rounder ripples) with a matched surface mesh, at more GPU cost. No effect on windowed oceans.")]
        [SerializeField] internal RippleQuality rippleQuality = RippleQuality.High;

        [Header("Body type")]
        [Tooltip("Body archetype. Advisory: drives which inspector sections are relevant and the " +
                 "'Apply defaults' action. Pond = small bounded pool; Lake = large / open bounded water; " +
                 "Ocean = unbounded open water to the horizon.")]
        [SerializeField] internal WaterBodyType bodyType = WaterBodyType.Pond;

        [Header("Water volume (placement)")]
        [Tooltip("World half-size per pool unit, per axis: X = half width, Y = depth to the " +
                 "floor, Z = half length. (1,1,1) is the original 1:1 pool. X != Z gives a " +
                 "rectangular footprint; Y alone makes it shallow/deep. The volume's POSITION " +
                 "and ROTATION come from THIS GameObject's Transform - move/rotate it to place " +
                 "the water. Set extent/transform before Play; the obstacle map reads them at startup.")]
        [SerializeField] internal Vector3 volumeExtent = Vector3.one;

        [Header("Large-water sim window")]
        [Tooltip("For bodies larger than the threshold, run the interactive ripple sim in a " +
                 "camera-following window instead of stretching the fixed grid over the whole " +
                 "surface (which goes blocky on big water). Analytic wind waves still cover " +
                 "everywhere. Small/medium bodies are unaffected.")]
        [SerializeField] internal bool enableLargeBodyWindow = true;
        [Tooltip("World half-extent (max of X,Z) above which windowing turns on. At/below this " +
                 "the whole-body sim is used exactly as before.")]
        [Min(1f)] [SerializeField] internal float largeBodyThreshold = DefaultLargeBodyThreshold;
        [Tooltip("Half-size (world metres) of the camera-following sim window. Ripple detail is " +
                 "2 * this / sim resolution per texel.")]
        [Min(1f)] [SerializeField] internal float simWindowMeters = DefaultSimWindowMeters;
        [Tooltip("On: keep the window fully inside the body footprint (enclosed bodies). Off: the " +
                 "window may overhang the edge and water beyond the footprint is analytic-only " +
                 "(natural for open water).")]
        [SerializeField] internal bool clampWindowToShore = false;
        [Tooltip("Optional: the sim window follows THIS transform instead of the target camera (e.g. the " +
                 "boat), so the interactive ripples centre on it. Leave empty to follow the camera.")]
        [SerializeField] internal Transform simWindowFocus;
        [Tooltip("Optional offset for the sim window centre, in the follow target's horizontal frame: " +
                 "X = right, Y = forward. Use it to lead the window ahead of the camera/boat.")]
        [SerializeField] internal Vector2 simWindowOffset = Vector2.zero;
        [Tooltip("Width, in sim texels, over which the window's ripple fades to analytic-only at " +
                 "its border so there is no seam.")]
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
            [Tooltip("Exponential distance-haze density (per metre) that dissolves the far ocean surface into " +
                     "the sky - the real replacement for Horizon Fade Distance. 0 = off. Tiny values fade over " +
                     "kilometres; raise it to pull the haze nearer.")]
            [Min(0f)] public float horizonHazeDensity = 0f;

            [Header("Ocean god rays (large-body light shafts)")]
            [Tooltip("Shaft colour, multiplied by the sun colour. Only used when God Ray Density > 0.")]
            public Color largeGodRayColor = DefaultLargeGodRayColor;
            [Tooltip("Master intensity of the ocean god-ray shafts. 0 = off (also the gate: the fullscreen " +
                     "shaft pass is skipped entirely). Raise for brighter volumetric beams.")]
            [Min(0f)] public float largeGodRayDensity = 0f;
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
        internal float LargeGodRayDensity => IsOceanClipmap ? largeGodRayDensity : 0f;
        internal float LargeGodRaySteps => largeGodRaySteps;
        internal float LargeGodRayAnisotropy => largeGodRayAnisotropy;
        internal float LargeGodRayExtinction => largeGodRayExtinction;
        internal float LargeGodRayCausticStrength => IsOceanClipmap ? largeGodRayCausticStrength : 0f;

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

        public enum ReflectionMode { SkyOnly, SSR, Planar }

        // The reflection BASE (what SkyOnly shows and what SSR/Planar layer over): the built-in
        // procedural sky cubemap, or the scene's URP reflection probe / skybox (unity_SpecCube0).
        public enum EnvironmentSource { ProceduralSky, UrpProbe }

        [Header("Reflections (Phase 3c)")]
        [SerializeField] ReflectionSettings reflectionSettings = new ReflectionSettings();

        [SerializeField] DetailNormalSettings detailNormalSettings = new DetailNormalSettings();

        /// <summary>Crest-style crossing scrolling detail normals: micro-ripple detail finer than the
        /// FFT cascades resolve. Off (flat) until a tiling water-normal texture is assigned; the
        /// publisher forces the strength to 0 with no texture so the shader skips the taps.</summary>
        [System.Serializable]
        public sealed class DetailNormalSettings
        {
            [Tooltip("Tiling water-normal texture, sampled as two crossing scrolling layers at two " +
                     "world scales. None = feature off (surface unchanged).")]
            public Texture2D texture = null;
            [Tooltip("Tilt strength of the detail layer on the surface normal.")]
            [Range(0f, 2f)] public float strength = 0.6f;
            [Tooltip("World size of one texture tile, metres (the far layer runs at twice this).")]
            [Range(1f, 100f)] public float tileMeters = 18f;
            [Tooltip("Scroll speed of the crossing layers, metres per second.")]
            [Range(0f, 2f)] public float scrollSpeed = 0.25f;
        }

        internal Texture2D DetailNormalTexture => detailNormalSettings.texture;
        // No texture -> strength 0: the shader's uniform gate then skips all four detail taps.
        internal float DetailNormalStrength
            => detailNormalSettings.texture != null ? detailNormalSettings.strength : 0f;
        internal float DetailNormalScale => detailNormalSettings.tileMeters;
        internal float DetailNormalSpeed => detailNormalSettings.scrollSpeed;

        /// <summary>How this body reflects (mode) and what it reflects (base environment). Migrated off the
        /// flat WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// unchanged.</summary>
        [System.Serializable]
        public sealed class ReflectionSettings
        {
            [Tooltip("Screen-space reflection: reflect the on-screen scene. Scales to many bodies; needs " +
                     "Depth + Opaque Texture on the active URP asset. Mixable with Planar (layered).")]
            public bool useScreenSpaceReflection = true;
            [Tooltip("Planar reflection: a full extra scene render across this body's plane. Use for at " +
                     "most ONE 'hero' body. Mixable with SSR (planar layers under SSR).")]
            public bool usePlanarReflection = false;
            [Tooltip("Reflect the scene's active URP reflection probe / skybox instead of the built-in " +
                     "procedural sky. The reflection BASE that SSR and Planar layer over.")]
            public bool reflectUrpProbe = false;
            [Tooltip("Real (screen-space) refraction: see the actual scene through the water instead of " +
                     "the analytic approximation. Needs the URP opaque texture; a tier may force it off.")]
            public bool realRefraction = false;

            // Look (drives the above-water surface; the under-water surface uses the same strength /
            // distortion for its total-internal-reflection view). Ranges mirror the shader.
            [Tooltip("Overall strength of the reflected term (0 = none, 1 = full).")]
            [Range(0f, 1f)] public float reflectionStrength = 1f;
            [Tooltip("Brightness of the reflected environment - the procedural sky OR the URP reflection " +
                     "probe (whichever is active). Boost to make a dim baked probe / dark skybox read on " +
                     "the water; lower to calm a bright reflection. Does not affect the sun glint.")]
            [Range(0f, 4f)] public float envReflectionIntensity = 1f;
            [Tooltip("Minimum Fresnel reflectance regardless of view angle. 0 = physical (~2% looking " +
                     "straight down, full mirror at grazing). Raise toward the legacy uniformly-mirrored " +
                     "look (the old curve behaved like ~0.25).")]
            [Range(0f, 1f)] public float fresnelFloor = 0f;
            [Tooltip("OVERALL SHININESS: the Fresnel grazing exponent. 5 = physical water; LOWER makes " +
                     "reflectivity rise faster on tilted wave faces, so the whole surface reads " +
                     "glossier with contrast (unlike the floor, which mirrors uniformly).")]
            [Range(1f, 5f)] public float fresnelPower = 5f;
            [Tooltip("Surface roughness at the camera: width of the sun's specular lobe AND blur of the " +
                     "sky reflection. Low = tight glints on calm water; high = broad soft glitter.")]
            [Range(0.01f, 1f)] public float sunRoughness = 0.08f;
            [Tooltip("Roughness far away. RAISE THIS to calm shiny mid/long-range waves: the sun path " +
                     "widens and the sky mirror blurs toward the horizon.")]
            [Range(0.01f, 1f)] public float roughnessFar = 0.2f;
            [Tooltip("Distance (metres) over which roughness ramps from the near value to Far.")]
            [Range(50f, 5000f)] public float roughnessFarDistance = 1000f;
            [Tooltip("Curve of the near-to-far roughness ramp: 1 = linear, above 1 keeps the water " +
                     "sharp for longer, below 1 roughens sooner.")]
            [Range(0.25f, 4f)] public float roughnessFalloff = 1f;
            [Tooltip("Vertical stretching of the blurred sky reflection - rough water smears what it " +
                     "reflects vertically (the classic elongated ocean streaks). 0 = off.")]
            [Range(0f, 1f)] public float reflectionAnisoStretch = 0.5f;
            [Tooltip("Sun sheen: weight of a second, much broader specular lobe, so wave faces far " +
                     "outside the direct sun reflection still catch a soft highlight. 0 = off.")]
            [Range(0f, 1f)] public float sunSheen = 0f;
            [Tooltip("Breadth of the sheen lobe (its roughness). Higher = softer, wider sheen.")]
            [Range(0.2f, 1f)] public float sunSheenRoughness = 0.6f;
            [Tooltip("Keeps the sun glitter alive when the sun sits at/near the horizon (wrapped " +
                     "lighting on the sun lobes). 0 = physical; raise for stronger low-sun sparkle.")]
            [Range(0f, 1f)] public float sunGrazeBoost = 0f;
            [Tooltip("Wave-normal distortion of the reflection.")]
            [Range(0f, 0.2f)] public float reflectionDistortion = 0.05f;
            [Tooltip("Screen-space reflection strength (used when SSR is on).")]
            [Range(0f, 1f)] public float ssrStrength = 1f;
            [Tooltip("SSR ray-march step size, world units.")]
            [Range(0.005f, 0.2f)] public float ssrStepSize = 0.03f;
            [Tooltip("SSR maximum ray-march steps.")]
            [Range(8, 64)] public int ssrMaxSteps = 24;
            [Tooltip("SSR depth thickness tolerance for a hit.")]
            [Range(0.01f, 1f)] public float ssrThickness = 0.2f;
            [Tooltip("Wave-normal distortion of the screen-space refraction (Real Refraction).")]
            [Range(0f, 0.2f)] public float refractionDistortion = 0.05f;
        }

        // Tier-capped effective reflection toggles + look, published per body every frame by
        // WaterUniformPublisher (uniform-driven, so they update live). SSR / Planar / real refraction
        // are the priciest paths, so a tier that disallows them (Low) forces them off; the URP-probe
        // base is never capped.
        internal bool EffectiveUseSSR => _richReflectionsAllowed && reflectionSettings.useScreenSpaceReflection;
        // Planar is split in two: WantsPlanar is the body's own opt-in (tier-capped); EffectiveUsePlanar
        // adds the per-frame budget grant (WaterReflections) so only the nearest few pools actually render
        // a mirror and the rest degrade to SSR / sky. Both the _UsePlanar publish and the mirror pass read
        // EffectiveUsePlanar, so they can never disagree within a frame.
        internal bool WantsPlanar => _richReflectionsAllowed && reflectionSettings.usePlanarReflection;
        internal bool EffectiveUsePlanar => WantsPlanar && WaterReflections.IsPlanarGranted(this);
        internal bool EffectiveRealRefraction => _realRefractionAllowed && reflectionSettings.realRefraction;
        internal bool ReflectUrpProbe => reflectionSettings.reflectUrpProbe;
        internal float ReflectionStrength => reflectionSettings.reflectionStrength;
        internal float EnvReflectionIntensity => reflectionSettings.envReflectionIntensity;
        internal float FresnelFloor => reflectionSettings.fresnelFloor;
        internal float FresnelPower => reflectionSettings.fresnelPower;
        internal float SunRoughness => reflectionSettings.sunRoughness;
        internal float RoughnessFar => reflectionSettings.roughnessFar;
        internal float RoughnessFarDistance => reflectionSettings.roughnessFarDistance;
        internal float RoughnessFalloff => reflectionSettings.roughnessFalloff;
        internal float ReflectionAnisoStretch => reflectionSettings.reflectionAnisoStretch;
        internal float SunSheen => reflectionSettings.sunSheen;
        internal float SunSheenRoughness => reflectionSettings.sunSheenRoughness;
        internal float SunGrazeBoost => reflectionSettings.sunGrazeBoost;
        internal float ReflectionDistortion => reflectionSettings.reflectionDistortion;
        internal float SSRStrength => reflectionSettings.ssrStrength;
        internal float SSRStepSize => reflectionSettings.ssrStepSize;
        internal float SSRMaxSteps => reflectionSettings.ssrMaxSteps;
        internal float SSRThickness => reflectionSettings.ssrThickness;
        internal float RefractionDistortion => reflectionSettings.refractionDistortion;

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateReflectionsV7. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("reflectionMode")] ReflectionMode _legacyReflectionMode = ReflectionMode.SSR;
        [SerializeField, HideInInspector, FormerlySerializedAs("environmentSource")] EnvironmentSource _legacyEnvironmentSource = EnvironmentSource.ProceduralSky;

        /// <summary>The primary water body: the global fallback for objects without a
        /// <see cref="WaterMembership"/>. Per-object association goes through
        /// <see cref="BodyContaining"/>.</summary>
        public static WaterVolume Primary { get; private set; }

        /// <summary>Resolve the body an object should use when it isn't inside any specific
        /// one: the primary body, or any found body as a fallback. Prefer
        /// <see cref="BodyContaining"/> for objects that have a world position.</summary>
        public static WaterVolume Resolve()
        {
            if (Primary != null) return Primary;
            // Frame-cache the scene search: per-particle callers (splash drift) would
            // otherwise degrade to a whole-scene FindFirstObjectByType per particle per frame.
            if (_fallbackBodyFrame != Time.frameCount || _fallbackBody == null)
            {
                _fallbackBodyFrame = Time.frameCount;
                _fallbackBody = FindFirstObjectByType<WaterVolume>();
            }
            return _fallbackBody;
        }
        static WaterVolume _fallbackBody;
        static int _fallbackBodyFrame = -1;

        /// <summary>The water body a world point belongs to: the body whose horizontal
        /// footprint contains the point, nearest-centre wins when several overlap, and the
        /// primary body as a fallback when the point is outside every footprint. Objects call
        /// this each frame so they float on, and are lit by, the lake they are actually in.</summary>
        public static WaterVolume BodyContaining(Vector3 worldPoint)
        {
            WaterVolume best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (!body.WorldToPoolXZ(worldPoint, out _, out _)) continue;

                // Tiebreak on HORIZONTAL distance to centre; the footprint ignores height,
                // so a vertical gap between the point and a body must not sway the choice.
                Vector3 toCenter = body.VolumeCenter - worldPoint;
                float sqr = toCenter.x * toCenter.x + toCenter.z * toCenter.z;
                if (sqr < bestSqr) { bestSqr = sqr; best = body; }
            }
            return best != null ? best : Resolve();
        }

        /// <summary>All live water bodies. Used by the input router to send a click to
        /// whichever body's surface the ray hits, by the sim scheduler, and by
        /// <see cref="BodyContaining"/>.</summary>
        internal static readonly List<WaterVolume> Bodies = new List<WaterVolume>();

        // Set true after the primary body's one-time autolink scan (reset per play session).
        static bool _receiversAutoLinked;

        // Water shaders whose user renderers should be per-body. Named here so the autolink
        // scan can spot a loose crate/pool that uses one and give it a WaterMembership.
        static readonly string[] WaterMaterialShaderNames =
        {
            WaterShaderNames.WaterReceiver,
            WaterShaderNames.AnalyticPool,
        };

        /// <summary>One-time play-mode scan (primary body only): give every scene renderer that
        /// uses a water material - and isn't already driven by a body - a WaterMembership, so it
        /// is lit and fogged by the body it sits in without manual wiring. Idempotent: skips
        /// renderers that already carry the component or belong to a body's own surface/pool.</summary>
        static void AutoLinkReceivers()
        {
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r.GetComponent<WaterMembership>() != null) continue;
                if (IsBodyOwnedRenderer(r)) continue;   // driven by ApplyBodyBlock already
                if (!UsesWaterMaterial(r)) continue;
                r.gameObject.AddComponent<WaterMembership>();
            }
        }

        // True when a renderer is one this-or-another body drives directly (surface/pool/god
        // rays), so the autolink scan must not also attach a membership and double-write its MPB.
        static bool IsBodyOwnedRenderer(Renderer r)
        {
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume b = Bodies[i];
                if (r == b.surfaceAbove || r == b.surfaceUnder || r == b.poolRenderer || r == b.godRayRenderer)
                    return true;
            }
            return false;
        }

        static bool UsesWaterMaterial(Renderer r)
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) continue;
                for (int s = 0; s < WaterMaterialShaderNames.Length; s++)
                    if (m.shader.name == WaterMaterialShaderNames[s]) return true;
            }
            return false;
        }

        // Fast Enter Play Mode (the Unity 6.6 default) skips the domain reload, so statics
        // survive between play sessions. Reset every piece of scene-lifetime static state
        // before each session; OnEnable/OnDisable rebuild it for the new one.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticState()
        {
            Primary = null;
            Bodies.Clear();
            _fallbackBody = null;
            _fallbackBodyFrame = -1;
            _receiversAutoLinked = false;
#if WEBGPUWATER_URP
            _pipelineOwner = null;
            _savedRenderScale = 0f;
            _savedOpaqueTexture = false;
#endif
            WaterSimScheduler.ResetStaticState();
            WaterInteractable.ResetStaticState();
            WaterExclusionVolume.ResetStaticState();
        }

        [Header("Simulation")]
        [Tooltip("Master animation speed for THIS body's surface: multiplies the wave clock and the " +
                 "ripple solver timestep. 1 = real time, 0 = frozen, 2 = double speed. Foam and splash " +
                 "particles keep real time (surface only).")]
        [Range(0f, MaxTimeScale)] [SerializeField] float timeScale = 1f;

        // Upper bound for timeScale + the inspector slider max. Kept modest so the CFL-bounded ripple
        // solver (waveSpeed is stable only to ~2) still integrates sanely when time is sped up.
        const float MaxTimeScale = 8f;

        /// <summary>Per-body master animation speed (wave clock + ripple timestep). Clamped to [0, MaxTimeScale].</summary>
        public float TimeScale { get => timeScale; set => timeScale = Mathf.Clamp(value, 0f, MaxTimeScale); }

        [Tooltip("Direction TOWARD the light. Used when no 'sun' is assigned (a sun overrides it).")]
        [SerializeField] internal Vector3 lightDir = new Vector3(2f, 2f, -1f);
        [SerializeField] internal int causticResolution = 1024;
        // Tier override for the caustic RT resolution; 0 = no tier applied -> the authored
        // causticResolution above (see ApplyQuality for why the serialized field is never written).
        [System.NonSerialized] int _causticRes;
        internal int EffectiveCausticResolution => _causticRes > 0 ? _causticRes : causticResolution;

        // Direction TOWARD the light: the assigned sun wins, the serialized vector is the manual
        // fallback. Derived (not written back to the field): the old per-frame write-back silently
        // dirtied the authored value under [ExecuteAlways] in edit mode.
        internal Vector3 EffectiveLightDir => sun != null ? -sun.transform.forward : lightDir;

        [Header("Object interaction")]
        [SerializeField] ObjectInteractionSettings objectInteractionSettings = new ObjectInteractionSettings();

        /// <summary>How floating objects disturb the surface (mouse-like drops vs rasterized footprint).
        /// Migrated off the flat WaterVolume fields into this block (Phase 2); the same-named accessors
        /// keep every reader unchanged.</summary>
        [System.Serializable]
        public sealed class ObjectInteractionSettings
        {
            [Tooltip("How floating objects disturb the water. MouseLikeDrops clones the mouse " +
                     "interaction: analytic cosine drops from bobbing and drift (uses Ripple " +
                     "Radius/Strength below; smooth, zero rasterization noise, slow rotation is " +
                     "silent). FootprintDelta displaces by the rasterized submerged footprint " +
                     "(shaped wakes for large hulls; costlier and noisier).")]
            public ObjectInteraction objectInteraction = ObjectInteraction.MouseLikeDrops;
            [Tooltip("FootprintDelta mode: MASTER strength for how strongly submerged objects " +
                     "displace the water. Multiplies the per-frame submerged-thickness DELTA " +
                     "(a much smaller quantity than a mouse drop's unit push), so it reads " +
                     "higher than Ripple Strength for a comparable wake. " +
                     "Per-object weighting is WaterInteractable.displaceScale.")]
            [Range(0f, 1f)] public float obstacleStrength = 0.25f;
            [Tooltip("FootprintDelta mode: soft dead-band (in submerged-thickness world units) " +
                     "that swallows tiny footprint deltas from drift/rotation rasterization " +
                     "noise. Raise to kill jitter; LOWER if a slowly moving float's wake is " +
                     "invisible (its genuine per-frame delta is sub-millimetre).")]
            [Range(0f, 0.005f)] public float obstacleDeadband = 0.0006f;
            [Tooltip("Temporal smoothing of the object footprint (0 = off). Low-pass filters " +
                     "the displacement a floater injects, so continuous bobbing/rotation emits " +
                     "a few long clean waves instead of a dense packet of tight rings. The " +
                     "total displaced volume is unchanged; higher = calmer but lazier response.")]
            [Range(0f, 0.95f)] public float obstacleSmoothing = 0.65f;
            [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
            public bool obstacleFlipY = true;
        }

        // Same-named forwarding accessors keep every reader unchanged (objectInteraction is read by Step).
        internal ObjectInteraction objectInteraction => objectInteractionSettings.objectInteraction;
        internal float obstacleStrength => objectInteractionSettings.obstacleStrength;
        internal float obstacleDeadband => objectInteractionSettings.obstacleDeadband;
        internal float obstacleSmoothing => objectInteractionSettings.obstacleSmoothing;
        internal bool obstacleFlipY => objectInteractionSettings.obstacleFlipY;

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateInteractionAndRippleV6. Hidden.
        [SerializeField, HideInInspector, FormerlySerializedAs("objectInteraction")] ObjectInteraction _legacyObjectInteraction = ObjectInteraction.MouseLikeDrops;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleStrength")] float _legacyObstacleStrength = 0.25f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleDeadband")] float _legacyObstacleDeadband = 0.0006f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleSmoothing")] float _legacyObstacleSmoothing = 0.65f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleFlipY")] bool _legacyObstacleFlipY = true;

        [Header("Water fog (Beer-Lambert)")]
        [SerializeField] WaterFogSettings waterFogSettings = new WaterFogSettings();

        /// <summary>Beer-Lambert depth fog plus art-directed turbidity, shared by the surface, objects
        /// and pool. Migrated off the flat WaterVolume fields into this block (Phase 2); the same-named
        /// accessors keep every reader unchanged. (MaxFogDensity const stays on WaterVolume.)</summary>
        [System.Serializable]
        public sealed class WaterFogSettings
        {
            [Tooltip("Global depth absorption, shared by the surface, objects and pool.")]
            public bool waterFog = false;
            public Color fogColor = new Color(0.10f, 0.30f, 0.40f);
            [Tooltip("Per-channel extinction; red highest so it absorbs first. HDR: push a channel " +
                     "above 1 for very heavy absorption (fully opaque water on short paths).")]
            [ColorUsage(false, true)] public Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
            [Tooltip("Overall fog multiplier. Higher = thicker; crank it (with extinction) for pea-soup water.")]
            [Range(0f, MaxFogDensity)] public float fogDensity = 2f;
            [Tooltip("Art-directed turbidity independent of depth: lerp the view THROUGH the surface " +
                     "toward the fog colour. 0 = clear, 1 = fully non-transparent water. Reflections " +
                     "still show on top (tune with the material's Reflection Strength).")]
            [Range(0f, 1f)] public float waterOpacity = 0f;
        }

        // Same-named forwarding accessors keep every reader unchanged. WaterFog stays a public get/set
        // (used by the sample scripting API) but now targets the settings; the rest are read-only.
        bool waterFog => waterFogSettings.waterFog;
        internal Color fogColor => waterFogSettings.fogColor;
        internal Color fogExtinction => waterFogSettings.fogExtinction;
        internal float fogDensity => waterFogSettings.fogDensity;
        internal float waterOpacity => waterFogSettings.waterOpacity;

        /// <summary>Beer-Lambert depth fog, shared by the surface, objects and pool.</summary>
        public bool WaterFog { get => waterFogSettings.waterFog; set => waterFogSettings.waterFog = value; }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateWaterFogV3. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("waterFog")] bool _legacyWaterFog = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("fogColor")] Color _legacyFogColor = new Color(0.10f, 0.30f, 0.40f);
        [SerializeField, HideInInspector, FormerlySerializedAs("fogExtinction")] Color _legacyFogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [SerializeField, HideInInspector, FormerlySerializedAs("fogDensity")] float _legacyFogDensity = 2f;
        [SerializeField, HideInInspector, FormerlySerializedAs("waterOpacity")] float _legacyWaterOpacity = 0f;

        [Header("Volume scattering")]
        [SerializeField] VolumeScatterSettings volumeScatterSettings = new VolumeScatterSettings();

        /// <summary>Lit in-scatter colour layered on top of the Beer-Lambert fog. When off, the fog
        /// in-scatters the flat fog colour exactly as before, so this is opt-in per body. Absorption
        /// authoring converts a transmission colour to per-channel extinction; the crest SSS boosts sun
        /// scatter at steep wave peaks.</summary>
        [System.Serializable]
        public sealed class VolumeScatterSettings
        {
            [Tooltip("Light the water volume (a body colour scaled by intensity and lit by sun + ambient " +
                     "through a phase function) instead of in-scattering a flat picked colour. Makes the " +
                     "open ocean respond to the sun. Off = unchanged flat fog colour.")]
            public bool volumeScatter = false;
            [Tooltip("The water body colour, shown directly. HDR.")]
            [ColorUsage(false, true)] public Color scatterColor = new Color(0.05f, 0.22f, 0.32f);
            [Tooltip("Master brightness of the in-scattered colour. Raise this if the water reads too dark.")]
            [Range(0f, 8f)] public float scatterIntensity = 2f;
            [Tooltip("Phase anisotropy g: 0 scatters evenly, higher concentrates a forward glow toward the " +
                     "sun (Schlick/Henyey-Greenstein).")]
            [Range(0f, 0.95f)] public float scatterAnisotropy = 0.5f;
            [Tooltip("Weight of the ambient (sky) contribution to the in-scattered colour.")]
            [Range(0f, 4f)] public float scatterAmbientTerm = 1f;
            [Tooltip("Weight of the direct sun contribution to the in-scattered colour.")]
            [Range(0f, 4f)] public float scatterSunTerm = 1f;

            [Tooltip("Add a subsurface glow at steep wave crests, brightest when looking toward the sun. " +
                     "Ocean bodies only.")]
            public bool crestScatter = false;
            [Tooltip("Strength of the crest subsurface glow.")]
            [Range(0f, 8f)] public float sssIntensity = 3f;
            [Tooltip("How tightly the crest glow concentrates toward the sun (higher = tighter highlight).")]
            [Range(0.5f, 8f)] public float sssSunFalloff = 2f;
            [Tooltip("Crest fold amount (0 = flat water, 1 = breaking) where the glow starts to ramp in. " +
                     "Raise to keep the glow off the gentler swell and onto steeper crests.")]
            [Range(0f, 1f)] public float sssPinchMin = 0.1f;
            [Tooltip("Fold amount where the glow reaches full strength (folds seed the whitecaps, so keep " +
                     "this below full break to let foam take over the very tips).")]
            [Range(0f, 1f)] public float sssPinchMax = 0.6f;
            [Tooltip("Power curve on the fold ramp: >1 concentrates the glow onto the sharpest folds.")]
            [Range(0.5f, 6f)] public float sssPinchFalloff = 1.5f;
        }

        // Same-named forwarding accessors keep the publisher readable and every reader stable.
        internal bool volumeScatter => volumeScatterSettings.volumeScatter;
        internal Color scatterColor => volumeScatterSettings.scatterColor;
        internal float scatterIntensity => volumeScatterSettings.scatterIntensity;
        internal float scatterAnisotropy => volumeScatterSettings.scatterAnisotropy;
        internal float scatterAmbientTerm => volumeScatterSettings.scatterAmbientTerm;
        internal float scatterSunTerm => volumeScatterSettings.scatterSunTerm;
        internal bool crestScatter => volumeScatterSettings.crestScatter;
        internal float sssIntensity => volumeScatterSettings.sssIntensity;
        internal float sssSunFalloff => volumeScatterSettings.sssSunFalloff;
        internal float sssPinchMin => volumeScatterSettings.sssPinchMin;
        internal float sssPinchMax => volumeScatterSettings.sssPinchMax;
        internal float sssPinchFalloff => volumeScatterSettings.sssPinchFalloff;

        [Header("Depth attenuation (downwelling)")]
        [SerializeField] DepthAttenuationSettings depthAttenuation = new DepthAttenuationSettings();

        /// <summary>Downwelling depth attenuation: darken submerged surfaces, caustics and god rays with
        /// depth, independent of the view-path water fog. First feature migrated off the flat WaterVolume
        /// fields into a nested Settings block (Phase 2).</summary>
        [System.Serializable]
        public sealed class DepthAttenuationSettings
        {
            [Tooltip("Darken submerged surfaces, caustics and god rays the DEEPER they sit, " +
                     "independent of view distance. Separate from the view-path fog above.")]
            public bool depthDarken = false;
            [Tooltip("Per-channel downwelling extinction (red highest so deep water shifts blue). " +
                     "Applied as exp(-extinction * strength * depth).")]
            public Color depthExtinction = new Color(0.45f, 0.15f, 0.08f);
            [Tooltip("Master multiplier on the depth term (acts like the fog density).")]
            [Range(0f, 8f)] public float depthDarkenStrength = 1f;
            [Tooltip("Extra softening of projected caustics on objects, per world unit of depth.")]
            [Range(0f, 8f)] public float causticDepthFade = 0.5f;
            [Tooltip("How fast god-ray shafts fade with depth, per world unit of depth.")]
            [Range(0f, 8f)] public float godRayDepthFade = 0.5f;
            [Tooltip("Mirror the fog extinction into the depth extinction each frame, so one dial " +
                     "drives fog + depth darkening. Off = the depth colour is fully independent.")]
            public bool linkDepthToFog = false;
        }

        // Same-named forwarding accessors so every reader (WaterUniformPublisher, ...) is unchanged.
        internal bool depthDarken => depthAttenuation.depthDarken;
        internal Color depthExtinction => depthAttenuation.depthExtinction;
        internal float depthDarkenStrength => depthAttenuation.depthDarkenStrength;
        internal float causticDepthFade => depthAttenuation.causticDepthFade;
        internal float godRayDepthFade => depthAttenuation.godRayDepthFade;
        internal bool linkDepthToFog => depthAttenuation.linkDepthToFog;

        // Legacy capture: scenes/prefabs authored before this migration serialized these under the old
        // top-level names. [FormerlySerializedAs] IS valid here - the fields are still top-level on
        // WaterVolume (only a C# rename), so the old values land here and are copied into the block above
        // exactly once by MigrateDepthAttenuationV1 (see OnAfterDeserialize). Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("depthDarken")] bool _legacyDepthDarken = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("depthExtinction")] Color _legacyDepthExtinction = new Color(0.45f, 0.15f, 0.08f);
        [SerializeField, HideInInspector, FormerlySerializedAs("depthDarkenStrength")] float _legacyDepthDarkenStrength = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("causticDepthFade")] float _legacyCausticDepthFade = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("godRayDepthFade")] float _legacyGodRayDepthFade = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("linkDepthToFog")] bool _legacyLinkDepthToFog = false;

        // ---- settings migration (god-class -> per-feature nested Settings blocks) ------------------
        // Bumped by one for each feature whose flat fields move into a nested Settings block. A scene
        // serialized before a given version has its old (FormerlySerializedAs) legacy fields copied into
        // the new block once, on load, so tuned values are never lost. The copies are idempotent.
        const int CurrentSettingsVersion = 9;
        [SerializeField, HideInInspector] int _settingsVersion = 0;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_settingsVersion >= CurrentSettingsVersion) return; // new or already-migrated asset
            if (_settingsVersion < 1) MigrateDepthAttenuationV1();
            if (_settingsVersion < 2) MigrateOceanV2();
            if (_settingsVersion < 3) MigrateWaterFogV3();
            if (_settingsVersion < 4) MigrateWindWavesV4();
            if (_settingsVersion < 5) MigrateFoamV5();
            if (_settingsVersion < 6) MigrateInteractionAndRippleV6();
            if (_settingsVersion < 7) MigrateReflectionsV7();
            if (_settingsVersion < 8) MigrateBedDepthV8();
            if (_settingsVersion < 9) MigrateBodyTypeV9();
            _settingsVersion = CurrentSettingsVersion;
        }

        // v1: the "Depth attenuation (downwelling)" fields moved into DepthAttenuationSettings.
        void MigrateDepthAttenuationV1()
        {
            depthAttenuation.depthDarken = _legacyDepthDarken;
            depthAttenuation.depthExtinction = _legacyDepthExtinction;
            depthAttenuation.depthDarkenStrength = _legacyDepthDarkenStrength;
            depthAttenuation.causticDepthFade = _legacyCausticDepthFade;
            depthAttenuation.godRayDepthFade = _legacyGodRayDepthFade;
            depthAttenuation.linkDepthToFog = _legacyLinkDepthToFog;
        }

        // v2: the four "Ocean ..." headers (open water, clipmap, god rays, whitecaps) moved into OceanSettings.
        void MigrateOceanV2()
        {
            ocean.openWater = _legacyOpenWater;
            ocean.largeWaveAmplitude = _legacyLargeWaveAmplitude;
            ocean.largeWaveChoppiness = _legacyLargeWaveChoppiness;
            ocean.swellHeight = _legacySwellHeight;
            ocean.swellWavelength = _legacySwellWavelength;
            ocean.unboundedOcean = _legacyUnboundedOcean;
            ocean.clipmapOuterRadius = _legacyClipmapOuterRadius;
            ocean.oceanDetailFalloff = _legacyOceanDetailFalloff;
            ocean.horizonFadeDistance = _legacyHorizonFadeDistance;
            ocean.horizonHazeColor = _legacyHorizonHazeColor;
            ocean.horizonHazeDensity = _legacyHorizonHazeDensity;
            ocean.largeGodRayColor = _legacyLargeGodRayColor;
            ocean.largeGodRayDensity = _legacyLargeGodRayDensity;
            ocean.largeGodRaySteps = _legacyLargeGodRaySteps;
            ocean.largeGodRayAnisotropy = _legacyLargeGodRayAnisotropy;
            ocean.largeGodRayExtinction = _legacyLargeGodRayExtinction;
            ocean.largeGodRayCausticStrength = _legacyLargeGodRayCausticStrength;
            ocean.oceanFoamWindThreshold = _legacyOceanFoamWindThreshold;
            ocean.oceanFoamCoverage = _legacyOceanFoamCoverage;
            ocean.oceanFoamStrength = _legacyOceanFoamStrength;
            ocean.oceanFoamFadeRate = _legacyOceanFoamFadeRate;
            ocean.oceanFoamColor = _legacyOceanFoamColor;
            ocean.oceanFoamTileSize = _legacyOceanFoamTileSize;
            ocean.oceanFoamFeather = _legacyOceanFoamFeather;
        }

        // v3: the "Water fog (Beer-Lambert)" fields moved into WaterFogSettings.
        void MigrateWaterFogV3()
        {
            waterFogSettings.waterFog = _legacyWaterFog;
            waterFogSettings.fogColor = _legacyFogColor;
            waterFogSettings.fogExtinction = _legacyFogExtinction;
            waterFogSettings.fogDensity = _legacyFogDensity;
            waterFogSettings.waterOpacity = _legacyWaterOpacity;
        }

        // v4: the "Wind waves (spectral)" fields moved into WindWaveSettings.
        void MigrateWindWavesV4()
        {
            windWaveSettings.windWaves = _legacyWindWaves;
            windWaveSettings.windSpeed = _legacyWindSpeed;
            windWaveSettings.windFromDegrees = _legacyWindFromDegrees;
            windWaveSettings.waveScaleMeters = _legacyPoolHalfExtentMeters;
            windWaveSettings.waveCount = _legacyWaveCount;
            windWaveSettings.waveAmplitudeScale = _legacyWaveAmplitudeScale;
            windWaveSettings.waveDirectionSpread = _legacyWaveDirectionSpread;
            windWaveSettings.waveNormalStrength = _legacyWaveNormalStrength;
        }

        // v5: the "Foam" fields (pool/interactive surface foam) moved into FoamSettings.
        void MigrateFoamV5()
        {
            foamSettings.foam = _legacyFoam;
            foamSettings.foamGenRate = _legacyFoamGenRate;
            foamSettings.foamDecay = _legacyFoamDecay;
            foamSettings.foamDecayRate = _legacyFoamDecayRate;
            foamSettings.foamSpread = _legacyFoamSpread;
            foamSettings.foamAdvect = _legacyFoamAdvect;
            foamSettings.foamFromSpeed = _legacyFoamFromSpeed;
            foamSettings.foamFromCurvature = _legacyFoamFromCurvature;
            foamSettings.foamColor = _legacyFoamColor;
            foamSettings.foamStrength = _legacyFoamStrength;
            foamSettings.foamFeather = _legacyFoamFeather;
            foamSettings.foamCoreCut = _legacyFoamCoreCut;
            foamSettings.foamBorderWidth = _legacyFoamBorderWidth;
            foamSettings.foamContactDepth = _legacyFoamContactDepth;
        }

        // v6: the "Object interaction" and "Ripple tuning" fields moved into their nested Settings blocks.
        void MigrateInteractionAndRippleV6()
        {
            objectInteractionSettings.objectInteraction = _legacyObjectInteraction;
            objectInteractionSettings.obstacleStrength = _legacyObstacleStrength;
            objectInteractionSettings.obstacleDeadband = _legacyObstacleDeadband;
            objectInteractionSettings.obstacleSmoothing = _legacyObstacleSmoothing;
            objectInteractionSettings.obstacleFlipY = _legacyObstacleFlipY;

            rippleSettings.waveSpeed = _legacyWaveSpeed;
            rippleSettings.damping = _legacyDamping;
            rippleSettings.stepsPerFrame = _legacyStepsPerFrame;
            rippleSettings.rippleStrength = _legacyRippleStrength;
            rippleSettings.rippleRadius = _legacyRippleRadius;
            rippleSettings.seedRipplesOnStart = _legacySeedRipplesOnStart;
            rippleSettings.conserveVolume = _legacyConserveVolume;
            rippleSettings.conserveMaxCorrection = _legacyConserveMaxCorrection;
        }

        // v7: the "Reflections" fields (reflection mode + base environment) moved into ReflectionSettings.
        void MigrateReflectionsV7()
        {
            // Map the retired SkyOnly/SSR/Planar enum onto the independent toggles.
            reflectionSettings.useScreenSpaceReflection = _legacyReflectionMode == ReflectionMode.SSR;
            reflectionSettings.usePlanarReflection = _legacyReflectionMode == ReflectionMode.Planar;
            reflectionSettings.reflectUrpProbe = _legacyEnvironmentSource == EnvironmentSource.UrpProbe;
        }

        // v8: the "Bed depth (real terrain depth)" fields moved into BedDepthSettings.
        void MigrateBedDepthV8()
        {
            bedDepthSettings.useBedDepth = _legacyUseBedDepth;
            bedDepthSettings.bedTerrain = _legacyBedTerrain;
            bedDepthSettings.bedResolution = _legacyBedResolution;
            bedDepthSettings.deepWaterColor = _legacyDeepWaterColor;
            bedDepthSettings.bedFadeDepth = _legacyShorelineFadeDepth;
            bedDepthSettings.bedTintStrength = _legacyShorelineStrength;
        }

        // v9: infer the advisory body archetype for bodies authored before the WaterBodyType field
        // existed, so their inspector opens on the right type. Unbounded = Ocean, open water = Lake,
        // else Pond. Advisory only; the user can re-pick.
        void MigrateBodyTypeV9()
        {
            bodyType = ocean.unboundedOcean ? WaterBodyType.Ocean
                     : ocean.openWater      ? WaterBodyType.Lake
                     :                         WaterBodyType.Pond;
        }

        // Editor-only: a freshly added component starts already-migrated, so the one-time copy never runs
        // on new bodies. Only assets serialized before a feature existed (no _settingsVersion -> 0) migrate.
        // (Distinguishing new from pre-migration data is exactly what a field initializer cannot do.)
        void Reset() => _settingsVersion = CurrentSettingsVersion;

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
            [Tooltip("Whitewash + breaker foam injected into the interactive foam sim near shore.")]
            [Range(0f, 4f)] public float surfFoamGain = 1.2f;
            [Tooltip("Standing foam lace hugging the waterline, independent of the front rhythm.")]
            [Range(0f, 2f)] public float surfWaterlineFoam = 0.5f;

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
            [Tooltip("Downslope streak stretch of the swash foam during the backwash - drain " +
                     "marks running toward the waterline.")]
            [Range(0f, 1f)] public float surfSwashStreak = 0.5f;
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
        internal float SurfAmplitudeEffective => Mathf.Max(surfAmplitude, SwellHeight);
        internal float surfWavelength => bedDepthSettings.surfWavelength;
        internal float surfPeriod => bedDepthSettings.surfPeriod;

        // Deep-water dispersion: L0 = g/(2 pi) * T^2 = 1.56 * T^2 - LOCKSTEP with
        // SURF_DEEPWATER_LENGTH_COEF in WaterSurfWaves.hlsl / SurfDeepwaterLengthCoef in
        // LargeWaveField.cs. The auto spacing is this fraction of L0 (0.2 lands the default
        // 9 s period on ~25 m, matching the historical hand default of 26 m).
        const float SurfDispersionLengthCoef = 1.56f;
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
        internal float surfFoamGain => bedDepthSettings.surfFoamGain;
        internal float surfWaterlineFoam => bedDepthSettings.surfWaterlineFoam;
        internal float surfFoamStrength => bedDepthSettings.surfFoamStrength;
        internal float surfFoamFeather => bedDepthSettings.surfFoamFeather;
        internal float surfFoamTileSize => bedDepthSettings.surfFoamTileSize;
        internal Color surfFoamColor => bedDepthSettings.surfFoamColor;
        internal float surfCrestFoamGain => bedDepthSettings.surfCrestFoamGain;
        internal float surfFoamBoreGain => bedDepthSettings.surfFoamBoreGain;
        internal float surfFoamTrailGain => bedDepthSettings.surfFoamTrailGain;
        internal float surfFoamTrailLength => bedDepthSettings.surfFoamTrailLength;
        internal float surfFoamTrailDissolve => bedDepthSettings.surfFoamTrailDissolve;
        internal float surfSwashFoam => bedDepthSettings.surfSwashFoam;
        internal float surfSwashFoamWidth => bedDepthSettings.surfSwashFoamWidth;
        internal float surfSwashFoamDissolve => bedDepthSettings.surfSwashFoamDissolve;
        internal float surfSwashStreak => bedDepthSettings.surfSwashStreak;

        // ---- FOAM-1: crest-foam pop curve -> 1D LUT bake -----------------------------------
        // The AnimationCurve is baked to a tiny R8 LUT the surface (tex2Dlod) and the foam sim
        // (SampleLevel) both read. Rebaked whenever the curve's key signature changes, so play-
        // mode curve tuning is live without any editor-side hook. Render-only foam - the max
        // below is a LOCKSTEP comment contract with the shader, not a validator height pair.
        internal const float SurfCrestLutOverCapMax = 2f; // LOCKSTEP: SURF_CREST_LUT_OVERCAP_MAX (WaterSurfWaves.hlsl)
        const int SurfCrestLutResolution = 128;
        [System.NonSerialized] Texture2D _surfCrestFoamLut;
        [System.NonSerialized] float _surfCrestFoamLutSignature = float.NaN;

        internal bool SurfCrestFoamLutActive
            => bedDepthSettings.surfCrestFoamCurveEnabled
               && bedDepthSettings.surfCrestFoamCurve != null
               && bedDepthSettings.surfCrestFoamCurve.length > 0;

        /// <summary>The baked pop-curve LUT (null when the curve is disabled/empty). Lazily
        /// (re)baked on access - callers must gate on SurfCrestFoamLutActive.</summary>
        internal Texture2D SurfCrestFoamLutTexture
        {
            get
            {
                if (!SurfCrestFoamLutActive) return null;
                EnsureSurfCrestFoamLutBaked();
                return _surfCrestFoamLut;
            }
        }

        // Cheap per-frame change detection: fold every key's shape into one float. The indexer
        // (curve[i]) does not allocate, unlike the .keys array property.
        static float SurfCrestFoamCurveSignature(AnimationCurve curve)
        {
            float signature = curve.length;
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
                signature = signature * 31f + key.time;
                signature = signature * 31f + key.value;
                signature = signature * 31f + key.inTangent;
                signature = signature * 31f + key.outTangent;
            }
            return signature;
        }

        void EnsureSurfCrestFoamLutBaked()
        {
            AnimationCurve curve = bedDepthSettings.surfCrestFoamCurve;
            float signature = SurfCrestFoamCurveSignature(curve);
            if (_surfCrestFoamLut != null && signature == _surfCrestFoamLutSignature) return;

            if (_surfCrestFoamLut == null)
            {
                _surfCrestFoamLut = new Texture2D(SurfCrestLutResolution, 1, TextureFormat.R8,
                                                  mipChain: false, linear: true)
                {
                    name = "SurfCrestFoamLut",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            var texels = new byte[SurfCrestLutResolution];
            for (int i = 0; i < SurfCrestLutResolution; i++)
            {
                float overCap = (i / (float)(SurfCrestLutResolution - 1)) * SurfCrestLutOverCapMax;
                texels[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(curve.Evaluate(overCap)) * 255f);
            }
            _surfCrestFoamLut.SetPixelData(texels, 0);
            _surfCrestFoamLut.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _surfCrestFoamLutSignature = signature;
        }

        void DestroySurfCrestFoamLut()
        {
            if (_surfCrestFoamLut == null) return;
            if (Application.isPlaying) Destroy(_surfCrestFoamLut);
            else DestroyImmediate(_surfCrestFoamLut);
            _surfCrestFoamLut = null;
            _surfCrestFoamLutSignature = float.NaN;
        }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateBedDepthV8. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("useBedDepth")] bool _legacyUseBedDepth = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("bedTerrain")] Terrain _legacyBedTerrain;
        [SerializeField, HideInInspector, FormerlySerializedAs("bedResolution")] int _legacyBedResolution = 256;
        [SerializeField, HideInInspector, FormerlySerializedAs("deepWaterColor")] Color _legacyDeepWaterColor = new Color(0.02f, 0.10f, 0.15f);
        [SerializeField, HideInInspector, FormerlySerializedAs("shorelineFadeDepth")] float _legacyShorelineFadeDepth = 6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("shorelineStrength")] float _legacyShorelineStrength = 0.8f;

        [Header("Wind waves (spectral)")]
        [SerializeField] WindWaveSettings windWaveSettings = new WindWaveSettings();

        /// <summary>Ambient wind-driven wave layer composited on top of the interactive ripples (floating
        /// objects ride these too). Migrated off the flat WaterVolume fields into this block (Phase 2);
        /// the same-named accessors keep every reader (buoyancy, the wave bank, the ocean swell) unchanged.</summary>
        [System.Serializable]
        public sealed class WindWaveSettings
        {
            [Tooltip("Ambient wind-driven wave layer composited on top of the interactive ripples. " +
                     "Floating objects ride these waves too.")]
            public bool windWaves = true;
            [Tooltip("Wind speed (m/s). ~3 = light breeze.")]
            [Range(0f, 15f)] public float windSpeed = 3f;
            [Tooltip("Wind heading in degrees: 0 = blowing toward +X (i.e. coming from the west).")]
            [Range(0f, 360f)] public float windFromDegrees = 0f;
            [Tooltip("Physical size the body half-extent ([-1,1] -> +/-this) represents, in metres. " +
                     "Sets wind-wave scale; fetch is twice this.")]
            [Range(1f, 500f)] [FormerlySerializedAs("poolHalfExtentMeters")] public float waveScaleMeters = 10f;
            [Tooltip("Number of sinusoidal components summed for the wave layer.")]
            [Range(1, WaterWaveBank.MaxWaves)] public int waveCount = 12;
            [Tooltip("Artistic multiplier on the physically-derived wave height (a light breeze " +
                     "on a small lake is physically sub-cm, so some exaggeration reads better).")]
            [Range(0f, 12f)] public float waveAmplitudeScale = 4f;
            [Tooltip("Higher = waves cling more tightly to the wind direction (parallel, river-like). " +
                     "Lower = broader, choppier crossing crests.")]
            [Range(1f, 12f)] public float waveDirectionSpread = 2f;
            [Tooltip("Scales how strongly the wind waves tilt the surface normal.")]
            [Range(0f, 3f)] public float waveNormalStrength = 1f;
        }

        // Same-named forwarding accessors keep every reader unchanged. WindWaves stays a public get/set
        // (sample scripting API) targeting the settings; windWaves is the private read for internal use.
        bool windWaves => windWaveSettings.windWaves;
        internal float windSpeed => windWaveSettings.windSpeed;
        internal float windFromDegrees => windWaveSettings.windFromDegrees;
        internal float waveScaleMeters => windWaveSettings.waveScaleMeters;
        internal int waveCount => windWaveSettings.waveCount;
        internal float waveAmplitudeScale => windWaveSettings.waveAmplitudeScale;
        internal float waveDirectionSpread => windWaveSettings.waveDirectionSpread;
        internal float waveNormalStrength => windWaveSettings.waveNormalStrength;

        /// <summary>Ambient wind-driven wave layer composited on top of the interactive
        /// ripples. Floating objects ride these waves too.</summary>
        public bool WindWaves { get => windWaveSettings.windWaves; set => windWaveSettings.windWaves = value; }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateWindWavesV4. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("windWaves")] bool _legacyWindWaves = true;
        [SerializeField, HideInInspector, FormerlySerializedAs("windSpeed")] float _legacyWindSpeed = 3f;
        [SerializeField, HideInInspector, FormerlySerializedAs("windFromDegrees")] float _legacyWindFromDegrees = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("poolHalfExtentMeters")] float _legacyPoolHalfExtentMeters = 10f;
        [SerializeField, HideInInspector, FormerlySerializedAs("waveCount")] int _legacyWaveCount = 12;
        [SerializeField, HideInInspector, FormerlySerializedAs("waveAmplitudeScale")] float _legacyWaveAmplitudeScale = 4f;
        [SerializeField, HideInInspector, FormerlySerializedAs("waveDirectionSpread")] float _legacyWaveDirectionSpread = 2f;
        [SerializeField, HideInInspector, FormerlySerializedAs("waveNormalStrength")] float _legacyWaveNormalStrength = 1f;

        [Header("Foam")]
        [SerializeField] FoamSettings foamSettings = new FoamSettings();

        /// <summary>Turbulence-driven surface foam simulation and shading (the pool/interactive foam,
        /// distinct from the ocean whitecaps above). Migrated off the flat WaterVolume fields into this
        /// block (Phase 2); the same-named accessors keep every reader unchanged.</summary>
        [System.Serializable]
        public sealed class FoamSettings
        {
            [Tooltip("Turbulence-driven surface foam simulation and shading (on/off).")]
            public bool foam = false;
            [Tooltip("How fast turbulence creates foam.")]
            [Range(0f, 2f)] public float foamGenRate = 0.6f;
            [Tooltip("SURVIVAL factor per step of thick, fresh foam (not a decay rate: HIGHER = foam lasts longer). Lower = bursts collapse faster.")]
            [Range(0.80f, 1f)] public float foamDecay = 0.96f;
            [Tooltip("SURVIVAL factor per step of thin residual lace. Must sit above the fresh value (clamped at runtime if not). Higher = lace lingers longer after the burst.")]
            [Range(0.90f, 1f)] public float foamDecayResidual = 0.993f;
            [Tooltip("Time scale of foam decay, frame-rate independent: 1 = authored speed, 2 = fades twice as fast, 0.5 = half. Tune fade SPEED here; the survival sliders above compound ~60x per second, so tiny changes there swing the look violently.")]
            [Range(0.05f, 4f)] public float foamDecayRate = 1f;
            [Tooltip("Diffusion of foam toward neighbours.")]
            [Range(0f, 1f)] public float foamSpread = 0.2f;
            [Tooltip("Activity level below which NO foam forms: small waves are too weak to break, " +
                     "so they pass without leaving foam. Raise until gentle ripples stay clean and " +
                     "only wakes/splashes/breaking waves foam. 0 = every motion foams (old look).")]
            [Range(0f, 1f)] public float foamGenThreshold = 0.15f;
            [Tooltip("WORLD wave height (metres) below which NO foam forms. Kills the noise foam: " +
                     "short interference wavelets between real wavefronts have high curvature but no " +
                     "height, so without this gate they out-foam the actual waves. Raise until only " +
                     "waves of real size foam.")]
            [Range(0f, 0.2f)] public float foamMinWaveHeight = 0.01f;
            [Tooltip("How far foam is carried along the surface flow each step (texels). 0 = old isotropic spread.")]
            [Range(0f, 8f)] public float foamAdvect = 3f;
            [Tooltip("How strongly moving/agitated water (surface speed + shear) generates foam.")]
            [Range(0f, 20f)] public float foamFromSpeed = 6f;
            [Tooltip("How strongly surface curvature (crests, chop, sharp folds) generates foam.")]
            [Range(0f, 100f)] public float foamFromCurvature = 30f;
            [Tooltip("Foam DEPOSIT: how much lasting foam a burst of turbulence lays down instantly, " +
                     "instead of only trickling in at the generation rate. Raise this so a fast wake or " +
                     "churn leaves a real deposit/trail that lingers and dissolves, rather than fading as " +
                     "the boat passes. 0 = off (rate-only, old look).")]
            [Range(0f, 1f)] public float foamDeposit = 0.5f;
            [Tooltip("Shallow-water breaking boost: where a baked bed (shore/beach/shelf) makes the " +
                     "water shallow, waves shoal and break sooner, so foam generation is boosted there " +
                     "- foam gathers over shelves and on the approach to shore (the selective, " +
                     "over-the-shelf whitecapping Crest/KWS gate on the Froude number). 0 = off " +
                     "(deep-water foam unchanged); needs a body with a baked bed. Never suppresses foam.")]
            [Range(0f, 1f)] public float foamBreakStrength = 0f;
            [Tooltip("WORLD depth (metres) below which the breaking boost above applies - the column " +
                     "depth over which 'shallow' ramps to 'deep'. Only used when Break Strength > 0 and " +
                     "the body has a baked bed.")]
            [Range(0.1f, 8f)] public float foamBreakRange = 1.5f;
            [Tooltip("Crest-selective foam: 0 = foam forms wherever the surface is agitated (crests AND " +
                     "the equally-tall troughs a fast wake/chop leaves); 1 = foam forms only on wave " +
                     "CRESTS (rise above the local average), the KWS/Crest whitecap rule. Raise to stop " +
                     "foam filling troughs and read as proper whitecaps.")]
            [Range(0f, 1f)] public float foamCrestBias = 0f;
            [Tooltip("Wake foam: how strongly a moving interactor (boat/sphere) stamps foam at the hull, " +
                     "which then advects and decays into the trail. 0 = off (wake foam comes only from " +
                     "the emergent churn, which reads thin). This is the crisp bow/stern foam.")]
            [Range(0f, 2f)] public float foamWakeStrength = 0f;
            [Tooltip("Wake foam stamp radius as a multiple of the interactor radius - how far past the " +
                     "hull the deposited foam reaches before it advects into the trail.")]
            [Range(0.5f, 4f)] public float foamWakeRadiusScale = 1.5f;
            [Space]
            public Color foamColor = Color.white;
            [Tooltip("WORLD size (metres) of one foam-pattern tile. The pattern is sampled in world " +
                     "space (like the ocean whitecap), so its scale no longer rides the body extent " +
                     "and stays put on windowed bodies. A rotated second octave fades in with " +
                     "distance to hide the repeat.")]
            [Min(0.25f)] public float foamPatternSize = 2f;
            [Range(0f, 2f)] public float foamStrength = 1f;
            [Tooltip("Softness of the foam edges: mask level over which foam fades in from nothing. 0 = hard edges (no feathering).")]
            [Range(0f, 0.5f)] public float foamFeather = 0.15f;
            [Tooltip("How much the foam pattern erodes the dense core: 0 = solid white core, 1 = core breaks into pattern detail like the residual lace.")]
            [Range(0f, 1f)] public float foamCoreCut = 0.5f;
            [Tooltip("Width of the foam band along the pool walls (pool units).")]
            [Range(0f, 0.5f)] public float foamBorderWidth = 0.08f;
            [Tooltip("Depth band for contact foam where objects meet the waterline.")]
            [Range(0f, 0.5f)] public float foamContactDepth = 0.06f;
        }

        // Same-named forwarding accessors keep every reader unchanged. Foam stays a public get/set (sample
        // + Water Wizard API) targeting the settings; foam is the private read for the internal gate;
        // foamBorderWidth stays writable (the Water Wizard sets it). The rest are read-only.
        bool foam => foamSettings.foam;
        internal float foamGenRate => foamSettings.foamGenRate;
        internal float foamDecay => foamSettings.foamDecay;
        internal float foamDecayRate => foamSettings.foamDecayRate;
        internal float foamGenThreshold => foamSettings.foamGenThreshold;
        internal float foamMinWaveHeight => foamSettings.foamMinWaveHeight;
        internal float foamDecayResidual => foamSettings.foamDecayResidual;
        internal float foamSpread => foamSettings.foamSpread;
        internal float foamAdvect => foamSettings.foamAdvect;
        internal float foamFromSpeed => foamSettings.foamFromSpeed;
        internal float foamFromCurvature => foamSettings.foamFromCurvature;
        internal float foamDeposit => foamSettings.foamDeposit;
        internal float foamBreakStrength => foamSettings.foamBreakStrength;
        internal float foamBreakRange => foamSettings.foamBreakRange;
        internal float foamCrestBias => foamSettings.foamCrestBias;
        internal float foamWakeStrength => foamSettings.foamWakeStrength;
        internal float foamWakeRadiusScale => foamSettings.foamWakeRadiusScale;
        internal Color foamColor => foamSettings.foamColor;
        internal float foamPatternSize => foamSettings.foamPatternSize;
        internal float foamStrength => foamSettings.foamStrength;
        internal float foamFeather => foamSettings.foamFeather;
        internal float foamCoreCut => foamSettings.foamCoreCut;
        internal float foamBorderWidth { get => foamSettings.foamBorderWidth; set => foamSettings.foamBorderWidth = value; }
        internal float foamContactDepth => foamSettings.foamContactDepth;

        /// <summary>Turbulence-driven surface foam simulation and shading.</summary>
        public bool Foam { get => foamSettings.foam; set => foamSettings.foam = value; }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateFoamV5. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("foam")] bool _legacyFoam = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamGenRate")] float _legacyFoamGenRate = 0.6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamDecay")] float _legacyFoamDecay = 0.96f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamDecayRate")] float _legacyFoamDecayRate = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamSpread")] float _legacyFoamSpread = 0.2f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamAdvect")] float _legacyFoamAdvect = 3f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFromSpeed")] float _legacyFoamFromSpeed = 6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFromCurvature")] float _legacyFoamFromCurvature = 30f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamColor")] Color _legacyFoamColor = Color.white;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamStrength")] float _legacyFoamStrength = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFeather")] float _legacyFoamFeather = 0.15f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamCoreCut")] float _legacyFoamCoreCut = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamBorderWidth")] float _legacyFoamBorderWidth = 0.08f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamContactDepth")] float _legacyFoamContactDepth = 0.06f;

        [Header("Ripple tuning")]
        [SerializeField] RippleSettings rippleSettings = new RippleSettings();

        /// <summary>Interactive ripple solver + click/drag ripple tuning. Migrated off the flat
        /// WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// unchanged.</summary>
        [System.Serializable]
        public sealed class RippleSettings
        {
            [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
            [Range(0.1f, 2.0f)] public float waveSpeed = 0.6f;
            [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
            [Range(0.90f, 1.0f)] public float damping = 0.99f;
            [Tooltip("Solver steps per frame AT THE 60 FPS REFERENCE - the sim accumulates real " +
                     "time and runs this rate regardless of frame rate, so wave speed is identical " +
                     "in a 30 fps build and a 144 fps editor. More = faster, smoother propagation.")]
            [Range(1, 8)] public int stepsPerFrame = 2;
            [Tooltip("Height added by a click/drag ripple (world units; volume-scale independent).")]
            [Range(0.001f, 0.08f)] public float rippleStrength = 0.025f;
            [Tooltip("Radius of a click/drag ripple (world units; volume-scale independent).")]
            [Range(0.005f, 0.2f)] public float rippleRadius = 0.05f;
            [Tooltip("Horizontal choppiness of the interactive ripple + WAKE field (Crest-style pinch): " +
                     "sharpens ripple/wake crests horizontally so a boat wake reads crisp instead of soft " +
                     "and round. 0 = off (height-only, unchanged). Raise for a sharp V-wake; also sharpens " +
                     "ambient interactive ripples. On the ocean the wake rides the camera-following sim window.")]
            [Range(0f, 1.5f)] public float rippleChoppiness = 0f;
            [Tooltip("Seed the pool with random ripples on start.")]
            public bool seedRipplesOnStart = true;
            [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
            public bool conserveVolume = true;
            [Tooltip("Safety cap on how far Conserve Volume can shift the whole surface per step " +
                     "(pool units). The mean is computed exactly, so this only guards against a " +
                     "diverged transient moving the plane in one step.")]
            [Range(0.005f, 0.5f)] public float conserveMaxCorrection = 0.05f;
        }

        // Same-named forwarding accessors keep every reader unchanged. RippleStrength/RippleRadius stay
        // public get/set (sample scripting API) targeting the settings; the rest are read-only.
        internal float waveSpeed => rippleSettings.waveSpeed;
        internal float damping => rippleSettings.damping;
        internal int stepsPerFrame => rippleSettings.stepsPerFrame;
        internal float rippleChoppiness => rippleSettings.rippleChoppiness;
        internal bool seedRipplesOnStart => rippleSettings.seedRipplesOnStart;
        internal bool conserveVolume => rippleSettings.conserveVolume;
        internal float conserveMaxCorrection => rippleSettings.conserveMaxCorrection;

        /// <summary>Height added by a click/drag ripple (world units).</summary>
        public float RippleStrength { get => rippleSettings.rippleStrength; set => rippleSettings.rippleStrength = value; }

        /// <summary>Radius of a click/drag ripple (world units).</summary>
        public float RippleRadius { get => rippleSettings.rippleRadius; set => rippleSettings.rippleRadius = value; }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateInteractionAndRippleV6. Hidden.
        [SerializeField, HideInInspector, FormerlySerializedAs("waveSpeed")] float _legacyWaveSpeed = 0.6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("damping")] float _legacyDamping = 0.99f;
        [SerializeField, HideInInspector, FormerlySerializedAs("stepsPerFrame")] int _legacyStepsPerFrame = 2;
        [SerializeField, HideInInspector, FormerlySerializedAs("rippleStrength")] float _legacyRippleStrength = 0.025f;
        [SerializeField, HideInInspector, FormerlySerializedAs("rippleRadius")] float _legacyRippleRadius = 0.05f;
        [SerializeField, HideInInspector, FormerlySerializedAs("seedRipplesOnStart")] bool _legacySeedRipplesOnStart = true;
        [SerializeField, HideInInspector, FormerlySerializedAs("conserveVolume")] bool _legacyConserveVolume = true;
        [SerializeField, HideInInspector, FormerlySerializedAs("conserveMaxCorrection")] float _legacyConserveMaxCorrection = 0.05f;

        [Header("Camera")]
        [SerializeField] internal OrbitCamera orbit;
        [Tooltip("Apply the package's default framing (FOV, near/far clip) to the target camera " +
                 "at enable. Off by default: a drop-in water body must not silently overwrite a " +
                 "game's camera setup. The demo scene builder frames its camera at build time.")]
        [SerializeField] internal bool configureCamera = false;

        [Header("Splash")]
        [Tooltip("Splash emitter this body routes impacts through (object splashes, the spray pump, " +
                 "mouse interaction). Left empty, one is resolved or created on demand.")]
        [SerializeField] internal WaterSplashEmitter splashEmitter;
        [Tooltip("Supply a splash emitter to triggers over this body. When none is assigned or found, " +
                 "one is created on demand. Untick to keep this body silent (no object/pump/mouse splashes).")]
        [SerializeField] internal bool provideSplashEmitter = true;
    }
}
