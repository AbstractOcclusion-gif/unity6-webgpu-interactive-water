// WebGL Water - one water body: identity, lifecycle and public facade (Unity 6 / URP port).
// Port of main.js / renderer.js by Evan Wallace (MIT).
//
// WaterVolume is the single scene component; each responsibility lives in a collaborator
// it owns and orchestrates from Update:
//   WaterSimulation      - GPU heightfield sim (ping-pong RTs, compute dispatch)
//   WaterObstacle        - rasterized submerged-footprint pass (FootprintDelta mode)
//   WaterCausticsPass    - per-body caustic material/RT/command buffer
//   WaterSurfaceSampler  - async height readback + CPU bilinear surface queries
//   WaterSimWindow       - camera-following scrolling sim window for large bodies
//   WaterBedBaker        - terrain -> pool-space bed-height bake (lazy)
//   WaterUniformPublisher- per-body shader uniforms (property block + global mirror)
//   WaterInputRouter     - scene input (primary body only, play mode only)
//   WaterSimScheduler    - static per-frame visibility / sim-budget schedule
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [DefaultExecutionOrder(-50)]
    public class WaterVolume : MonoBehaviour, ISerializationCallbackReceiver
    {
        /// <summary>How WaterInteractable objects disturb the surface.</summary>
        public enum ObjectInteraction
        {
            /// <summary>Analytic cosine drops from bobbing/drift, cloned from the mouse
            /// interaction (WaterInteractable emits via AddRipple).</summary>
            MouseLikeDrops,
            /// <summary>Rasterized submerged-footprint displacement (prev - curr delta).</summary>
            FootprintDelta
        }

        [Header("Assigned by the scene builder")]
        [SerializeField] internal ComputeShader simCompute;
        // Optional, ocean-only: the FFT-cascade wave compute. Unassigned, or on non-ocean bodies, the
        // analytic large-wave path (WaterLargeWaves.hlsl) is used unchanged. Deliberately NOT part of
        // HasRequiredWiring - a body must run without it so pools/bounded bodies are unaffected.
        [SerializeField] internal ComputeShader oceanFftCompute;
        [SerializeField] internal Shader causticsShader;
        [SerializeField] internal Shader largeBodyCausticsShader; // WebGpuWater/LargeBodyCaustics - near-field ocean caustics in the sim-window frame (optional; oceans only)
        [SerializeField] internal Shader obstacleShader; // WebGLWater/ObstacleDepth - footprint of interactable objects
        [SerializeField] internal Mesh waterMesh;        // XY grid plane, [-1,1], shared with the water surface renderers
        [SerializeField] internal Camera targetCamera;
        [SerializeField] internal Light sun;             // directional light: drives water, caustics AND real shadows

        [Header("Look / surfaces")]
        [SerializeField] internal Texture tiles;         // pool tile albedo sampled by the water reflection (assign your own)
        [SerializeField] internal Cubemap sky;           // sky cubemap for above-water reflections

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
                     "the Wind waves settings below (wind speed scales the swell, wind heading steers it); " +
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

            [Header("Ocean clipmap (unbounded open water)")]
            [Tooltip("Concentric rings in the radial clipmap mesh. More = denser far-field / smoother swell, " +
                     "at more vertices.")]
            [Min(ClipmapMinRings)] public int clipmapRings = DefaultClipmapRings;
            [Tooltip("Vertices per ring. More = rounder rings, at more vertices.")]
            [Min(ClipmapMinSegments)] public int clipmapSegments = DefaultClipmapSegments;
            [Tooltip("Radius (metres) of the outermost ring: push near the camera far plane to reach the horizon.")]
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
        internal int clipmapRings => ocean.clipmapRings;
        internal int clipmapSegments => ocean.clipmapSegments;
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

        // Legacy capture (scenes/prefabs from before this migration) -> copied once by MigrateOceanV2.
        // Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("openWater")] bool _legacyOpenWater = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveAmplitude")] float _legacyLargeWaveAmplitude = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveChoppiness")] float _legacyLargeWaveChoppiness = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellHeight")] float _legacySwellHeight = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellWavelength")] float _legacySwellWavelength = DefaultSwellWavelength;
        [SerializeField, HideInInspector, FormerlySerializedAs("unboundedOcean")] bool _legacyUnboundedOcean = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("clipmapRings")] int _legacyClipmapRings = DefaultClipmapRings;
        [SerializeField, HideInInspector, FormerlySerializedAs("clipmapSegments")] int _legacyClipmapSegments = DefaultClipmapSegments;
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
        internal float LargeWaveHeadingRad => windFromDegrees * Mathf.Deg2Rad;
        internal float LargeWaveAmplitudeEffective => largeWaveAmplitude * (windSpeed / LargeWaveReferenceWind);
        internal float LargeWaveChoppiness => largeWaveChoppiness;
        internal float SwellHeight => swellHeight;
        internal float SwellWavelength => swellWavelength;
        internal float OceanFoamWindThreshold => oceanFoamWindThreshold;
        internal float OceanFoamCoverage => oceanFoamCoverage;
        internal float OceanFoamStrength => oceanFoamStrength;
        internal float OceanFoamFadeRate => oceanFoamFadeRate;
        internal Color OceanFoamColor => oceanFoamColor;
        internal float OceanFoamTileSize => oceanFoamTileSize;
        internal float OceanFoamFeather => oceanFoamFeather;
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

        // Ocean clipmap guard rails (mirror LargeWaterClipmap's) + defaults. Inner ring dense for
        // near-field chop/ripples; outer ring near the far plane to reach the horizon.
        const int DefaultClipmapRings = 48;
        const int DefaultClipmapSegments = 96;
        const float DefaultClipmapOuterRadius = 10000f;
        const float DefaultOceanDetailFalloff = 0.03f; // low: the clipmap resolves waves far out, so the
                                                       // swell rolls near to the horizon before band-limiting
        const int ClipmapMinRings = 2;
        const int ClipmapMinSegments = 3;
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

        /// <summary>How this body reflects (mode) and what it reflects (base environment). Migrated off the
        /// flat WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// unchanged.</summary>
        [System.Serializable]
        public sealed class ReflectionSettings
        {
            [Tooltip("How THIS body reflects. SSR (screen-space over the procedural sky) scales to many " +
                     "bodies. Planar is a full extra scene render across this body's plane - use it for at " +
                     "most ONE 'hero' body. SkyOnly is cheapest (procedural sky only). SSR needs Depth + " +
                     "Opaque Texture enabled on the active URP asset.")]
            public ReflectionMode reflectionMode = ReflectionMode.SSR;
            [Tooltip("Reflection base environment. ProceduralSky uses the generated sky cubemap (the demo " +
                     "look). UrpProbe reflects the scene's active reflection probe / skybox so the water " +
                     "matches your lit environment. Orthogonal to the mode above and unaffected by the tier.")]
            public EnvironmentSource environmentSource = EnvironmentSource.ProceduralSky;
        }

        // Same-named forwarding accessors keep every reader unchanged. Reflections stays a public get/set
        // (sample scripting API) targeting the settings; the two enums are read by ApplyReflections.
        ReflectionMode reflectionMode => reflectionSettings.reflectionMode;
        internal EnvironmentSource environmentSource => reflectionSettings.environmentSource;

        /// <summary>How this body reflects (SkyOnly, SSR or Planar). Named 'Reflections'
        /// because the nested <see cref="ReflectionMode"/> enum owns that identifier.</summary>
        public ReflectionMode Reflections { get => reflectionSettings.reflectionMode; set => reflectionSettings.reflectionMode = value; }

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
            "WebGLWater/WaterReceiver",
            "WebGLWater/PoolWall",
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
        }

        [Header("Simulation")]
        [Tooltip("Direction TOWARD the light. Auto-driven from 'sun' when one is assigned.")]
        [SerializeField] internal Vector3 lightDir = new Vector3(2f, 2f, -1f);
        [SerializeField] internal int causticResolution = 1024;

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
        const int CurrentSettingsVersion = 7;
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
            ocean.clipmapRings = _legacyClipmapRings;
            ocean.clipmapSegments = _legacyClipmapSegments;
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
            windWaveSettings.poolHalfExtentMeters = _legacyPoolHalfExtentMeters;
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
            foamSettings.foamDecayResidual = _legacyFoamDecayResidual;
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
            reflectionSettings.reflectionMode = _legacyReflectionMode;
            reflectionSettings.environmentSource = _legacyEnvironmentSource;
        }

        // Editor-only: a freshly added component starts already-migrated, so the one-time copy never runs
        // on new bodies. Only assets serialized before a feature existed (no _settingsVersion -> 0) migrate.
        // (Distinguishing new from pre-migration data is exactly what a field initializer cannot do.)
        void Reset() => _settingsVersion = CurrentSettingsVersion;

        [Header("Bed depth (real terrain depth - EXPERIMENTAL)")]
        [Tooltip("Use the baked terrain bed height for real water-column depth (shoreline " +
                 "gradient). Off = flat-floor behaviour.")]
        [SerializeField] internal bool useBedDepth = false;
        [Tooltip("Terrain whose heightmap defines the lake bed. Auto-resolves to the active " +
                 "Terrain if empty. Baked once at startup; call RebakeBed() (or the context-menu " +
                 "item) if the terrain changes.")]
        [SerializeField] internal Terrain bedTerrain;
        [Tooltip("Resolution of the baked pool-space bed-height map.")]
        [Range(WaterBedBaker.MinResolution, WaterBedBaker.MaxResolution)] [SerializeField] internal int bedResolution = 256;
        [Tooltip("Colour the surface tints toward over deep water.")]
        [SerializeField] internal Color deepWaterColor = new Color(0.02f, 0.10f, 0.15f);
        [Tooltip("World-unit depth at which the shoreline gradient reaches ~63% toward the deep " +
                 "colour. Smaller = the water darkens in shallower depth.")]
        [Range(0.1f, 50f)] [SerializeField] internal float shorelineFadeDepth = 6f;
        [Tooltip("Maximum tint toward the deep-water colour.")]
        [Range(0f, 1f)] [SerializeField] internal float shorelineStrength = 0.8f;

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
            [Tooltip("Physical size the pool half-extent ([-1,1] -> +/-this) represents, in metres. " +
                     "Sets wave scale; fetch is twice this.")]
            [Range(1f, 50f)] public float poolHalfExtentMeters = 10f;
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
        internal float poolHalfExtentMeters => windWaveSettings.poolHalfExtentMeters;
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
            [Tooltip("How far foam is carried along the surface flow each step (texels). 0 = old isotropic spread.")]
            [Range(0f, 8f)] public float foamAdvect = 3f;
            public float foamFromSpeed = 6f;
            public float foamFromCurvature = 30f;
            [Space]
            public Color foamColor = Color.white;
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
        internal float foamDecayResidual => foamSettings.foamDecayResidual;
        internal float foamDecayRate => foamSettings.foamDecayRate;
        internal float foamSpread => foamSettings.foamSpread;
        internal float foamAdvect => foamSettings.foamAdvect;
        internal float foamFromSpeed => foamSettings.foamFromSpeed;
        internal float foamFromCurvature => foamSettings.foamFromCurvature;
        internal Color foamColor => foamSettings.foamColor;
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
        [SerializeField, HideInInspector, FormerlySerializedAs("foamDecayResidual")] float _legacyFoamDecayResidual = 0.993f;
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
        [Tooltip("Shared splash emitter used for mouse interaction (and objects).")]
        [SerializeField] internal WaterSplashEmitter splashEmitter;

        // runtime collaborators (see the header comment for the responsibility map)
        //
        // The eagerly-owned collaborators are formalised as IWaterModule lifecycle modules (see
        // WaterCollaboratorModules.cs): the master constructs and disposes them through the module
        // registry instead of by hand. The typed accessors below keep the rest of the class - Update,
        // the sampling/ripple facade, the caustics render - reading them exactly as before.
        SimulationModule _simulationModule;
        ObstacleModule _obstacleModule;
        CausticsModule _causticsModule;
        SurfaceSamplerModule _surfaceSamplerModule;
        OceanFftModule _oceanFftModule;
        SimWindowModule _simWindowModule;
        IWaterModule[] _modules;   // ordered registry over the six modules above
        WaterContext _context;     // shared seam handed to the modules at Initialize

        WaterSimulation _water => _simulationModule?.Simulation;
        WaterObstacle _obstacle => _obstacleModule?.Obstacle;
        WaterCausticsPass _caustics => _causticsModule?.Caustics;
        WaterSurfaceSampler _sampler => _surfaceSamplerModule?.Sampler;
        WaterOceanFft _oceanFft => _oceanFftModule?.OceanFft; // ocean-only FFT wave pass; null on pools/bounded bodies
        WaterSimWindow _simWindow => _simWindowModule?.SimWindow;

        // The lazy trio stays as-is: each already uses a clean lazy pattern and serves even an
        // uninitialized body (context-menu rebake, defensive uniform writes), so it is not part of
        // the eager registry.
        WaterBedBaker _bedBaker;
        WaterUniformPublisher _publisher;
        WaterInputRouter _inputRouter;

        // Camera-following high-detail surface over the sim window (windowed bodies, play mode).
        // Its grid is built at the SIM resolution so the near field is sampled ~one vertex per
        // texel - the far plane's fixed grid stretched over a large volume samples the ripple
        // heightfield too sparsely and aliases into false, bobbing ripples.
        Renderer _patchRenderer;
        Mesh _patchGrid;
        MaterialPropertyBlock _patchMpb;
        static readonly int ID_IsPatch = Shader.PropertyToID("_IsPatch");
        static readonly int ID_PatchPoolCenter = Shader.PropertyToID("_PatchPoolCenter");
        static readonly int ID_PatchPoolHalf = Shader.PropertyToID("_PatchPoolHalf");
        static readonly int ID_PatchDepthBias = Shader.PropertyToID("_PatchDepthBias");
        const float PatchDepthBiasNdc = 5e-4f;      // nudge toward the camera so the dense patch wins the overlap
                                                    // (beats the coplanar far plane AND the coarser ocean clipmap)
        const string PatchObjectName = "Sim Window Patch";
        // Underside twin of the near-field patch: the SAME dense grid drawn with the under-water
        // material, so the submerged near field is sampled as finely as the above one and the two line
        // up vertex-for-vertex at the waterline (a coarse underside would show through the fine top).
        // Ocean-clipmap bodies only: it fills the under-clipmap's centre hole, and the bounded
        // under-plane it would otherwise fight is already switched off there.
        Renderer _patchUnderRenderer;
        MaterialPropertyBlock _patchUnderMpb;
        const string PatchUnderObjectName = "Sim Window Patch (under)";

        // Camera-following clipmap surface for unbounded open-water (ocean) bodies: a radial ring mesh
        // authored in world metres that reaches the horizon. Reuses THIS body's surface material; the
        // _IsClipmap flag rides its own property block (so it never leaks onto the pool-grid renderers).
        Renderer _clipmapRenderer;
        Mesh _clipmapMesh;
        MaterialPropertyBlock _clipmapMpb;
        static readonly int ID_IsClipmap = Shader.PropertyToID("_IsClipmap");
        const string ClipmapObjectName = "Ocean Clipmap";
        // Underside twin of the ocean clipmap: the same camera-following radial mesh (an annulus,
        // identical to the above one) drawn with the under-water surface material, so the submerged
        // view of the surface reaches the horizon too. Its centre hole is filled by the under-patch,
        // exactly as the above clipmap's hole is filled by the near-field patch.
        Renderer _clipmapUnderRenderer;
        Mesh _clipmapUnderMesh;
        MaterialPropertyBlock _clipmapUnderMpb;
        const string ClipmapUnderObjectName = "Ocean Clipmap (under)";

        // Lazy: the bed baker serves the context-menu RebakeBed even on an uninitialized
        // body, and the publisher serves WriteBodyProps callers defensively.
        WaterBedBaker BedBaker => _bedBaker ??= new WaterBedBaker(this);
        WaterUniformPublisher Publisher => _publisher ??= new WaterUniformPublisher(this);
        WaterInputRouter InputRouter => _inputRouter ??= new WaterInputRouter(this);

        // Internal collaborator surface (same assembly only).
        internal WaterSimulation Simulation => _water;
        internal WaterWaveBank WaveBank => _waveBank;
        internal float WaveTime => _waveTime;
        internal RenderTexture CausticTexture => _caustics?.Texture;
        // Ocean FFT displacement cascade array (null on non-ocean bodies / before init) - for the debug view.
        internal RenderTexture OceanFftTexture => _oceanFft?.DisplacementTexture;
        // True only when this body is an unbounded ocean whose FFT pass is producing cascades. Drives the
        // per-body _OceanFftActive flag so the surface samples the FFT instead of the analytic generator.
        internal bool OceanFftActive => _oceanFft != null && _oceanFft.Ready;
        // Cascade whitecap data for the foam-particle spawn compute (crest foam source).
        internal RenderTexture OceanFftNormalTexture => _oceanFft?.NormalTexture;
        internal Vector4 OceanFftDomainSizes => _oceanFft != null ? _oceanFft.DomainSizes : Vector4.one;
        internal float OceanFftCascadeCount => _oceanFft != null ? _oceanFft.CascadeCount : 0f;
        internal Texture2D BedTexture => _bedBaker?.Texture;
        internal bool IsBedBaked => _bedBaker != null && _bedBaker.IsBaked;
        internal int GodRaySteps => _godRaySteps;
        internal int PeakedRefineSteps => _peakedRefineSteps;
        internal void TogglePause() => _paused = !_paused;

        // wind-wave layer (shared by the surface shader and CPU buoyancy)
        readonly WaterWaveBank _waveBank = new WaterWaveBank();
        float _waveTime;
        // Bank-generation inputs baked into the current bank, compared field-by-field. (A
        // packed signature could alias two distinct states and silently keep stale amplitudes.)
        float _waveGenWindSpeed = float.NaN;
        float _waveGenWindFrom;
        float _waveGenExtentMeters;
        int _waveGenCount;
        float _waveGenAmpScale;
        float _waveGenSpread = float.NaN;
        float _waveGenVerticalExtent = float.NaN; // volume y-extent baked into the current bank
        bool _waveGenEnabled;

        int _simRes = WaterQuality.Default.SimResolution; // grid resolution, set from the quality tier at OnEnable
        bool _godRaysAllowed = true;                       // false when the tier turns god rays off
        bool _richReflectionsAllowed = true;               // false when the tier caps reflections to SkyOnly
        // Tier cost knobs delivered per-body through the property block (never by writing the
        // shared god-ray/surface material, which dirties the asset and lets bodies stomp each other).
        int _godRaySteps = WaterQuality.Default.GodRaySteps;
        int _maxWaveCount = WaterQuality.Default.MaxWaveCount;
        int _peakedRefineSteps = WaterQuality.Default.RefineSteps;
        // Low-end tier knobs (see WaterQuality): at their defaults every one is a no-op.
        float _renderScale = WaterQuality.Default.RenderScale;
        bool _realRefractionAllowed = true;
        int _meshDetail = WaterQuality.Default.MeshDetail;
        int _causticInterval = WaterQuality.Default.CausticInterval;
        int _readbackInterval = WaterQuality.Default.ReadbackInterval;
        int _maxFoamParticles = WaterQuality.Default.MaxFoamParticles;
        /// <summary>Tier cap on the GPU foam-particle pool (WaterFoamParticles clamps to it).</summary>
        internal int FoamParticleBudget => _maxFoamParticles;
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable (play mode only) and destroyed at
        // OnDisable, which also restores the renderer's original shared material so an
        // enable/disable cycle never leaves a renderer pointing at a destroyed instance.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        Material _surfaceAboveOriginal, _surfaceUnderOriginal;
        const string KW_SSR = "_USE_SSR";
        const string KW_PLANAR = "_USE_PLANAR";
        const string KW_URP_PROBE = "_USE_URP_PROBE";
        const string KW_REAL_REFRACTION = "_REAL_REFRACTION";
        // Low-tier coarse grid swapped onto the surface renderers at init (play mode only);
        // the originals are restored on disable, mirroring the material-instance pattern.
        Mesh _lowDetailGrid;
        Mesh _surfaceAboveOriginalMesh, _surfaceUnderOriginalMesh;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        bool _paused;
        float _stepDebt;     // fractional solver steps owed (frame-rate-independent stepping)
        float _foamTimeDebt; // reference steps elapsed since the last foam pass (foam runs once per frame, not per solver step)

        bool _windowed; // this body runs the camera-following windowed sim (decided at OnEnable)

        // Per-frame schedule flags, written for every body by WaterSimScheduler (frame-guarded,
        // so the result is independent of the arbitrary order in which the bodies Update).
        const float WaveHeightMargin = 0.1f;  // pool-space headroom above y=0 for wind-wave crests in the cull box
        internal bool _visible = true;   // inside the camera frustum -> its renderers draw
        internal bool _simulate = true;  // visible AND in range AND within the sim budget -> runs the GPU sim

        // Camera framing. activationDistance defaults to the far clip so "beyond the far clip"
        // is exactly what pauses a distant body - the two stay coupled, not coincidentally equal.
        // Internal so the editor build kit frames its demo camera from the same constants.
        internal const float CameraFieldOfView = 45f;
        internal const float CameraNearClip = 0.01f;
        internal const float CameraFarClip = 100f;

        // Large-water sim-window defaults (world metres). Threshold sits above the window
        // half-size so a body only marginally larger than the window stays whole-body
        // (windowing it would scroll for near-zero detail gain).
        const float DefaultLargeBodyThreshold = 48f;
        const float DefaultSimWindowMeters = 32f;

        // Upper bound on fog density; high enough that (with extinction) water can read fully
        // opaque even on short view paths.
        const float MaxFogDensity = 50f;

        // Startup pool seeding: a few random ripples so the surface isn't dead-flat on load.
        const int SeedRippleCount = 20;
        const float SeedRippleRadius = 0.03f;
        const float SeedRippleStrength = 0.01f;

        // Skip a sim step after an editor hitch/breakpoint: integrating one huge dt would
        // slam the explicit solver with energy in a single step.
        const float MaxStepSeconds = 1f;

        // Frame-rate-independent stepping: 'stepsPerFrame' is authored against this frame
        // rate; the solver runs stepsPerFrame * ReferenceFrameRate steps per SECOND at any
        // fps. The per-frame cap bounds the catch-up burst on slow devices/hitches - beyond
        // it the debt is dropped, so waves degrade to "slightly slower" instead of bursting.
        const float ReferenceFrameRate = 60f;
        const int MaxSolverStepsPerFrame = 8;
        // Cap on the foam time debt (reference steps), mirroring MaxSolverStepsPerFrame:
        // after a long pause foam catches up at most this much instead of vanishing in one pass.
        const float MaxFoamTimeDebtSteps = 8f;

        // Numeric guards.
        const float MinVolumeExtent = 1e-5f;        // a zero extent would collapse the pool-space transforms
        const float MinWindowHalfExtent = 1e-3f;    // same guard for the scrolling sim window
        const float RayParallelEpsilon = 1e-6f;     // surface picking: treat near-parallel rays as a miss
        internal const float MinShorelineFadeDepth = 0.01f; // keeps the shoreline depth scale finite (publisher)
        const float MinWaveMetersPerUnit = 1e-3f;   // keeps wave-space conversions finite

        // Edit-mode preview: Update ticks come from the editor loop at an uneven cadence, so
        // the sim integrates real elapsed time, clamped so a pause between repaints doesn't
        // feed one huge step into the solver.
        const float MaxEditorDeltaSeconds = 1f / 30f;

        // True once the GPU resources exist and the body is registered; guards teardown and
        // the edit-mode lazy-init retry (see TryInitialize).
        bool _initialized;

        void OnEnable()
        {
            TryInitialize();
        }

        // Full setup, run once per enable. In edit mode ([ExecuteAlways]) missing wiring is
        // NOT an error yet: the scene builders AddComponent first and wire fields afterwards,
        // and Update retries, so a hand-wired body starts previewing the moment the last
        // reference lands. In play mode missing wiring fails fast and loud.
        void TryInitialize()
        {
            if (_initialized || !enabled) return;

            if (!HasRequiredWiring())
            {
                if (Application.isPlaying) FailMissingWiring();
                return;
            }

            // Hard capability guard: the sim needs compute shaders + a float random-write RT. On a
            // backend without them, disable this body cleanly instead of dispatching into a crash.
            // (The quality tier already scales cost; this handles the total absence of support.)
            if (!SystemInfo.supportsComputeShaders ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Debug.LogWarning("WaterVolume: device lacks compute shaders or float render textures; " +
                                 "water simulation disabled on this body.", this);
                enabled = false;
                return;
            }

            ResolveSceneRefs(); // let a dropped-in prefab find the scene's camera/sun without manual wiring

            ApplyQuality();     // sets _simRes, causticResolution, _godRaysAllowed + per-body cost knobs

            _lastEditorTick = 0d;
            _stepDebt = 0f;
            _foamTimeDebt = 0f;
            _windowed = ShouldWindow(); // decided once; volumeExtent is fixed before Play

            // Construct the eagerly-owned collaborators through the module registry. Ordered here (after
            // _windowed, which the ocean-FFT module gates on; before ApplySimAnisotropy, which needs the
            // simulation to already exist) so the sequence and the Enabled gates match the former inline
            // construction byte-for-byte.
            BuildAndInitializeModules();

            ApplySimAnisotropy();       // round ripples on a rectangular pool (no-op for square/windowed)
#if UNITY_EDITOR
            WarnIfLargeBody();           // editor-only heads-up: large bodies are experimental in this POC
            WarnIfExperimentalTerrain(); // editor-only heads-up: terrain bed-depth is experimental
#endif

            // seed the pool with a few ripples. Compensate the strength for extent.y (like
            // AddRipple) so seed splashes keep a fixed world height on a deep pool - PoolToWorld
            // multiplies surface height by extent.y.
            if (seedRipplesOnStart)
            {
                float seedStrength = SeedRippleStrength / VolumeExtentSafe.y;
                for (int i = 0; i < SeedRippleCount; i++)
                    _water.AddDrop(Random.value * 2f - 1f, Random.value * 2f - 1f, SeedRippleRadius,
                                   (i & 1) == 1 ? seedStrength : -seedStrength);
            }

            // Opt-in only: a package component must not silently hijack the game's camera.
            if (configureCamera && targetCamera != null)
            {
                targetCamera.fieldOfView = CameraFieldOfView;
                targetCamera.nearClipPlane = CameraNearClip;
                // An unbounded ocean's clipmap reaches clipmapOuterRadius; the 100 m pool far-plane would
                // clip the horizon surface (and the fog that fills it), which reads as fog "popping" out
                // there. Bounded bodies keep the pool default.
                targetCamera.farClipPlane = IsOceanClipmap ? clipmapOuterRadius : CameraFarClip;
            }

            if (isPrimary)
            {
                if (Primary != null && Primary != this)
                    Debug.LogWarning("WaterVolume: multiple bodies are marked Is Primary; the last " +
                                     "one enabled wins. Exactly one body should be primary.", this);
                Primary = this;
            }
            if (!Bodies.Contains(this)) Bodies.Add(this);
            _mpb = new MaterialPropertyBlock();
            ApplyReflections();
            ApplyMeshDetail();   // Low tier: coarse surface grid (play mode only)
            ApplyPipelineTier(); // Low tier: render scale / opaque-copy release (primary, play mode only)
            CreateSimWindowPatch(); // windowed bodies: dense near-field surface over the sim window
            CreateOceanClipmap();   // unbounded-ocean bodies: horizon-reaching camera-following surface

            BedBaker.EnsureBaked(); // lazy terrain -> pool-space bed bake, only when useBedDepth is on

            Publisher.PublishSharedGlobals();
            EnsureWaveBank();
            if (_windowed) _simWindow.Track();  // prime the window centre before first publish
            RenderCausticsForThisBody();        // pool caustic (bounded), or the window-frame ocean caustic
            ApplyBodyBlock();
            if (isPrimary) Publisher.PublishBodyGlobals();

            _initialized = true;
        }

        void OnDisable()
        {
            if (!_initialized) return; // never initialized (missing wiring / capability guard)

            _initialized = false;
            if (Primary == this) Primary = FindNextPrimary(this);
            Bodies.Remove(this);
            DisposeModules();      // disposes the six eager collaborator modules (sim, obstacle, caustics,
                                   // surface sampler, ocean FFT, sim window) - releases the same GPU
                                   // resources the inline disposal did, and clears the sampler/window refs.
            _bedBaker?.Dispose();  // also re-arms the lazy bake gate for the next enable
            DestroySimWindowPatch(); // before restoring the surface material it borrows
            DestroyOceanClipmap();   // ditto - it borrows the same surface material
            RestoreSurfaceMaterial(surfaceAbove, ref _surfaceAboveInstance, ref _surfaceAboveOriginal);
            RestoreSurfaceMaterial(surfaceUnder, ref _surfaceUnderInstance, ref _surfaceUnderOriginal);
            RestoreMeshDetail();
            RestorePipelineTier();
            // Fresh per-enable state: a re-enable must not float objects on a stale height
            // field, and the window centre re-primes from the camera. (The sampler and sim-window
            // refs are cleared by DisposeModules above; the lazy input router is cleared here.)
            _inputRouter = null;
        }

        // Build the ordered collaborator registry for this enable and initialize each enabled module.
        // Order mirrors the original construction sequence (sim, sampler, sim window, obstacle, caustics,
        // ocean FFT); the context is the shared seam the modules will read from as their per-frame tick
        // moves onto IWaterModule.
        void BuildAndInitializeModules()
        {
            _context = new WaterContext(this);
            _simulationModule = new SimulationModule(this);
            _surfaceSamplerModule = new SurfaceSamplerModule(this);
            _simWindowModule = new SimWindowModule(this);
            _obstacleModule = new ObstacleModule(this);
            _causticsModule = new CausticsModule(this);
            _oceanFftModule = new OceanFftModule(this);
            _modules = new IWaterModule[]
            {
                _simulationModule, _surfaceSamplerModule, _simWindowModule,
                _obstacleModule, _causticsModule, _oceanFftModule,
            };

            for (int i = 0; i < _modules.Length; i++)
                if (_modules[i].Enabled) _modules[i].Initialize(_context);
        }

        // Dispose every collaborator module. Safe on modules that were disabled or never initialized.
        void DisposeModules()
        {
            if (_modules == null) return;
            for (int i = 0; i < _modules.Length; i++) _modules[i].Dispose();
        }

        // ---- Low-tier surface grid swap ----------------------------------------
        // The authored grid is 200x200 and the vertex shader runs 4 fetches + the wave sines
        // per vertex; a 128 sim doesn't need that tessellation. Play mode only (an edit-mode
        // swap could serialize the runtime mesh reference into the scene), mirroring the
        // material-instance pattern: originals restored on disable.
        void ApplyMeshDetail()
        {
            if (!Application.isPlaying || _meshDetail <= 0) return;

            _lowDetailGrid = WaterMeshBuilder.BuildGrid(_meshDetail);
            _lowDetailGrid.hideFlags = HideFlags.HideAndDontSave;
            SwapRendererMesh(surfaceAbove, _lowDetailGrid, ref _surfaceAboveOriginalMesh);
            SwapRendererMesh(surfaceUnder, _lowDetailGrid, ref _surfaceUnderOriginalMesh);
        }

        void RestoreMeshDetail()
        {
            RestoreRendererMesh(surfaceAbove, ref _surfaceAboveOriginalMesh);
            RestoreRendererMesh(surfaceUnder, ref _surfaceUnderOriginalMesh);
            if (_lowDetailGrid != null) { DestroyRuntimeObject(_lowDetailGrid); _lowDetailGrid = null; }
        }

        // The caustic pass shares whichever grid the surface uses this session.
        Mesh EffectiveWaterMesh => _lowDetailGrid != null ? _lowDetailGrid : waterMesh;

        static void SwapRendererMesh(Renderer r, Mesh replacement, ref Mesh original)
        {
            original = null;
            if (r == null) return;
            var filter = r.GetComponent<MeshFilter>();
            if (filter == null) return;
            original = filter.sharedMesh;
            filter.sharedMesh = replacement;
        }

        static void RestoreRendererMesh(Renderer r, ref Mesh original)
        {
            if (original == null) return;
            var filter = r != null ? r.GetComponent<MeshFilter>() : null;
            if (filter != null) filter.sharedMesh = original;
            original = null;
        }

        // ---- Low-tier global URP knobs ------------------------------------------
        // Render scale and the opaque-texture copy are PIPELINE-wide, so the primary body
        // applies them once (play mode only) and restores the authored values on disable -
        // the asset never keeps a tier's values.
#if WEBGPUWATER_URP
        static WaterVolume _pipelineOwner; // the body that applied the tweaks (and must restore them)
        static float _savedRenderScale;
        static bool _savedOpaqueTexture;
#endif

        void ApplyPipelineTier()
        {
#if WEBGPUWATER_URP
            if (!Application.isPlaying || !isPrimary || _pipelineOwner != null) return;
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (pipeline == null) return;

            bool wantScale = _renderScale < 1f;
            bool wantOpaqueOff = !_realRefractionAllowed; // nothing else in the package reads the opaque copy
            if (!wantScale && !wantOpaqueOff) return;

            _savedRenderScale = pipeline.renderScale;
            _savedOpaqueTexture = pipeline.supportsCameraOpaqueTexture;
            if (wantScale) pipeline.renderScale = _renderScale;
            if (wantOpaqueOff) pipeline.supportsCameraOpaqueTexture = false;
            _pipelineOwner = this;
#endif
        }

        void RestorePipelineTier()
        {
#if WEBGPUWATER_URP
            if (_pipelineOwner != this) return; // only the body that applied restores
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (pipeline != null)
            {
                pipeline.renderScale = _savedRenderScale;
                pipeline.supportsCameraOpaqueTexture = _savedOpaqueTexture;
            }
            _pipelineOwner = null;
#endif
        }

        bool HasRequiredWiring() => simCompute != null && causticsShader != null && waterMesh != null;

        // Fail fast on the required wiring (play mode); a missing piece would otherwise surface
        // later as a confusing downstream error (broken caustic material, per-frame DrawMesh errors).
        void FailMissingWiring()
        {
            if (simCompute == null) Debug.LogError("WaterVolume: simCompute not assigned.", this);
            else if (causticsShader == null) Debug.LogError("WaterVolume: causticsShader not assigned.", this);
            else Debug.LogError("WaterVolume: waterMesh not assigned.", this);
            enabled = false;
        }

        // Hand the primary role to another live body flagged isPrimary, so disabling one of two
        // (misconfigured) primaries doesn't strand Primary at null while a candidate is alive -
        // that would send every Resolve() into a per-call whole-scene search.
        static WaterVolume FindNextPrimary(WaterVolume leaving)
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i] != leaving && Bodies[i].isPrimary) return Bodies[i];
            return null;
        }

        // Restore the renderer's authored material before destroying the per-body instance, so
        // a disable/enable cycle never leaves the renderer pointing at a destroyed material.
        static void RestoreSurfaceMaterial(Renderer r, ref Material instance, ref Material original)
        {
            if (instance == null) { original = null; return; }
            if (r != null && original != null) r.sharedMaterial = original;
            DestroyRuntimeObject(instance);
            instance = null;
            original = null;
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj); else DestroyImmediate(obj);
        }

        // Fill in the scene-level references a prefab can't carry, so dropping the WaterVolume
        // prefab into a fresh scene "just works". Only unset fields are touched, so an explicitly
        // wired scene (e.g. the demo builder) is left exactly as authored.
        void ResolveSceneRefs()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (sun == null) sun = ResolveSun();
            if (orbit == null && targetCamera != null) orbit = targetCamera.GetComponent<OrbitCamera>();
            if (splashEmitter == null) splashEmitter = FindFirstObjectByType<WaterSplashEmitter>();
        }

        // The scene's key light: the lighting-settings sun if set, else the first directional light.
        static Light ResolveSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i].type == LightType.Directional) return lights[i];
            return null;
        }

        // Apply the quality tier's cost knobs. Called once at startup, before the sim/caustic
        // RTs are created, so the resolutions are fixed for the session (a tier change takes
        // effect on restart). With no asset assigned the inspector defaults are left untouched
        // (_simRes stays at its default), so existing scenes are unaffected.
        void ApplyQuality()
        {
            if (quality == null) return; // keep the inspector defaults / Default-tier cost knobs

            WaterQuality.Tier tier = quality.Resolve();
            _simRes = tier.SimResolution;
            causticResolution = tier.CausticResolution;
            _godRaysAllowed = tier.GodRays;
            _richReflectionsAllowed = tier.RichReflections;
            // Delivered per-body through WriteBodyUniforms (property block), never by writing
            // the shared god-ray material - which dirtied the asset in the editor and let
            // multiple bodies stomp each other's step count. Clamped >= 1 so a "god rays off"
            // tier (0 steps) can't bake a divide-by-zero; the renderer is disabled separately.
            _godRaySteps = Mathf.Max(1, tier.GodRaySteps);
            _maxWaveCount = tier.MaxWaveCount;
            _peakedRefineSteps = tier.RefineSteps;
            _renderScale = tier.RenderScale;
            _realRefractionAllowed = tier.RealRefraction;
            _meshDetail = tier.MeshDetail;
            _causticInterval = tier.CausticInterval;
            _readbackInterval = tier.ReadbackInterval;
            _maxFoamParticles = tier.MaxFoamParticles;

            // One line per enable so a build's console shows exactly which knobs landed -
            // tier mismatches (stale build cache, wrong asset, missing serialized fields)
            // are otherwise near-impossible to diagnose on a device.
            Debug.Log($"WaterVolume '{name}': quality tier applied - sim {_simRes}, caustics {causticResolution}, " +
                      $"mesh {(_meshDetail > 0 ? _meshDetail.ToString() : "authored")}, renderScale {_renderScale:0.##}, " +
                      $"realRefraction {_realRefractionAllowed}, godRays {_godRaysAllowed} ({_godRaySteps} steps), " +
                      $"waves {_maxWaveCount}, refine {_peakedRefineSteps}, foamCap {_maxFoamParticles}", this);
        }

        // Give the surface renderers per-body material instances and set their reflection
        // keywords from the tier-capped EffectiveReflectionMode, so bodies in different modes
        // don't fight over one shared material. A Planar body also binds the scene's single
        // planar reflection.
        void ApplyReflections()
        {
            // Edit-mode preview leaves the authored shared materials untouched: an instance
            // assigned to sharedMaterial could be saved into the scene as a dead reference.
            // Preview reflections therefore follow the material's authored keywords.
            if (!Application.isPlaying) return;

            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove, out _surfaceAboveOriginal);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder, out _surfaceUnderOriginal);
            ApplyReflectionKeywords(_surfaceAboveInstance);
            ApplyReflectionKeywords(_surfaceUnderInstance);

            if (EffectiveReflectionMode == ReflectionMode.Planar) BindHeroPlanar();
        }

        // The authored reflectionMode capped by the active quality tier: SSR/Planar collapse to
        // SkyOnly when the tier disallows rich reflections (e.g. Low). Keeps the field's intent
        // intact so raising the tier restores the authored mode without re-editing the body.
        ReflectionMode EffectiveReflectionMode =>
            _richReflectionsAllowed ? reflectionMode : ReflectionMode.SkyOnly;

        // Replace the renderer's shared material with a per-body instance (play-mode only, so
        // the scene asset is untouched). The original is captured so OnDisable can restore it
        // before destroying the instance.
        static Material InstanceSurfaceMaterial(Renderer r, out Material original)
        {
            original = null;
            if (r == null || r.sharedMaterial == null) return null;
            original = r.sharedMaterial;
            var instance = new Material(original) { hideFlags = HideFlags.HideAndDontSave };
            r.sharedMaterial = instance;
            return instance;
        }

        void ApplyReflectionKeywords(Material m)
        {
            if (m == null) return;
            SetKeyword(m, KW_SSR, EffectiveReflectionMode == ReflectionMode.SSR);
            SetKeyword(m, KW_PLANAR, EffectiveReflectionMode == ReflectionMode.Planar);
            // Base environment is independent of the mode above and of the tier: a single cube
            // sample either way, so it is not capped on Low.
            SetKeyword(m, KW_URP_PROBE, environmentSource == EnvironmentSource.UrpProbe);
            // A tier can DISABLE real refraction (it needs the URP opaque-texture copy, a big
            // bandwidth cost on mobile tilers) but never force it on over the authored material.
            if (!_realRefractionAllowed) SetKeyword(m, KW_REAL_REFRACTION, false);
        }

        static void SetKeyword(Material m, string keyword, bool on)
        {
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        // Point the scene's planar reflection at THIS body's plane and turn it on. The planar
        // texture is a single global plane, so only one hero body should use Planar mode; with
        // several, the last to enable at OnEnable wins.
        void BindHeroPlanar()
        {
            if (targetCamera == null) return;
            var planar = targetCamera.GetComponent<PlanarReflection>();
            if (planar == null) return;
            planar.enableReflection = true;
            planar.waterHeight = transform.position.y;
        }

        void Update()
        {
            // Edit-mode lazy init: a body whose wiring was assigned after AddComponent (the
            // builders' order) starts up here on the next editor tick.
            if (!_initialized)
            {
                TryInitialize();
                if (!_initialized) return;
            }

            // Input is a scene-level concern (and play-mode only): the primary body's router
            // handles mouse/keys and routes clicks to whichever body's surface the ray hits
            // (avoids two controllers fighting over one camera).
            if (Application.isPlaying && isPrimary) InputRouter.Update();

            // One-time autolink, deferred to Update (not OnEnable) so every body has registered
            // first - a body's own pool also uses a water material, and IsBodyOwnedRenderer can
            // only skip it once that body is in the registry.
            if (Application.isPlaying && isPrimary && autoLinkReceivers && !_receiversAutoLinked)
            {
                _receiversAutoLinked = true;
                AutoLinkReceivers();
            }

            // Decide (once per frame, for every body) which bodies draw and which run the
            // heavy GPU sim, then stop drawing this one if it is off-screen.
            WaterSimScheduler.EnsureSchedule();
            SetRenderersEnabled(_visible);

            // Edit-mode ticks arrive from the editor loop, so the preview integrates real
            // elapsed (clamped) time instead of the play-mode frame delta.
            float dt = Application.isPlaying ? Time.deltaTime : EditorDeltaSeconds();
            if (!_paused)
            {
                // The analytic wind waves are driven by the shared clock, so they keep moving
                // even on a budget-paused (but visible) body; only the GPU sim is gated.
                _waveTime += dt;
                if (_simulate) Step(dt);
            }

            Publisher.PublishSharedGlobals(); // sun, sky, tiles, camera-independent shared clock
            EnsureWaveBank();
            BedBaker.EnsureBaked();           // picks up useBedDepth being toggled on at runtime
            // Bounded bodies render the pool caustic; the windowed OCEAN renders the large-body caustic
            // in the sim-window's world frame (other windowed bodies still skip - see RenderCausticsForThisBody).
            // The tier can amortise the pass over N frames (the caustic RT simply holds).
            // Ocean FFT cascades refresh on the shared wave clock (NOT gated on _simulate: like the analytic
            // large waves they must animate whenever the body is live, or the surface would sample stale
            // cascades and render differently in edit vs play, where _simulate follows game-camera culling).
            // The surface only reads them when _OceanFftActive is published, so this stays ocean-only.
            if (IsOceanClipmap && !_paused)
            {
                Vector2 camXZ = targetCamera != null
                    ? new Vector2(targetCamera.transform.position.x, targetCamera.transform.position.z)
                    : new Vector2(VolumeCenter.x, VolumeCenter.z);
                var foam = new WaterOceanFft.FoamParams(OceanFoamWindThreshold, OceanFoamCoverage,
                                                        OceanFoamStrength, OceanFoamFadeRate);
                _oceanFft?.Dispatch(_waveTime, windSpeed, LargeWaveHeadingRad, LargeWaveAmplitudeEffective,
                                    SwellWavelength, SwellHeight, camXZ, foam);
            }
            if (_simulate && Time.frameCount % _causticInterval == 0)
                RenderCausticsForThisBody();

            ApplyBodyBlock();           // per-body uniforms -> this body's renderers (MPB)
            // Primary bridge: mirror this body's data to globals as the fallback for objects
            // without a WaterMembership (those resolve their own containing body instead).
            if (isPrimary) Publisher.PublishBodyGlobals();
            if (isPrimary) UpdateUnderwaterState(); // camera-submerged flag + globals for the underwater fog pass

            // Tier-amortised readback: buoyancy already tolerates async latency, so weak
            // devices can trade a few frames of it for GPU->CPU bandwidth.
            if (_simulate && Time.frameCount % _readbackInterval == 0)
            {
                _sampler.RequestReadback();  // paused bodies keep their last height (objects still float)
                if (IsOceanClipmap) _oceanFft?.RequestHeightReadback(); // FFT swell height for buoyancy
            }
        }

        // Per-body uniforms pushed to THIS body's own renderers via a property block, so
        // multiple water bodies never fight over global state.
        void ApplyBodyBlock()
        {
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            WriteBodyProps(_mpb);

            ApplyBlockTo(surfaceAbove);
            ApplyBlockTo(surfaceUnder);
            ApplyBlockTo(poolRenderer);
            ApplyBlockTo(godRayRenderer);
            ApplyPatchBlock();
            ApplyClipmapBlock();
        }

        // Refresh both near-field patches (the above one, and the under twin on ocean bodies).
        void ApplyPatchBlock()
        {
            PositionPatch(_patchRenderer, ref _patchMpb);
            PositionPatch(_patchUnderRenderer, ref _patchUnderMpb);
        }

        // Feed one patch renderer this body's per-body uniforms PLUS the window remap it needs, and park
        // it on the window centre so it culls with the window. The remap rides its own block so _IsPatch
        // never leaks onto the flat surface renderers. The transform is cosmetic (the shader places the
        // verts via PoolToWorld); it only sizes the culling bounds.
        void PositionPatch(Renderer patch, ref MaterialPropertyBlock block)
        {
            if (patch == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            WriteBodyProps(block);

            Vector3 poolCenter = WorldToPool(SimWindowCenter);
            block.SetFloat(ID_IsPatch, 1f);
            block.SetFloat(ID_PatchDepthBias, PatchDepthBiasNdc);
            block.SetVector(ID_PatchPoolCenter, new Vector4(poolCenter.x, poolCenter.z, 0f, 0f));
            block.SetVector(ID_PatchPoolHalf, new Vector4(
                SimHorizontalExtent / VolumeExtentSafe.x, SimHorizontalExtent / VolumeExtentSafe.z, 0f, 0f));
            patch.SetPropertyBlock(block);

            Transform t = patch.transform;
            t.position = SimWindowCenter;
            t.localScale = SimHalfExtent;
        }

        // Build the windowed near-field patch: a grid at the sim resolution, remapped by the
        // shader into the window's pool sub-region. Reuses THIS body's surface material instance
        // (so it inherits reflections/fog) with _IsPatch riding its property block. Play mode
        // only - it depends on the per-body material instance created in ApplyReflections.
        void CreateSimWindowPatch()
        {
            if (!Application.isPlaying || !_windowed) return;
            if (_patchRenderer != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            _patchGrid = WaterMeshBuilder.BuildGrid(Mathf.Max(1, _simRes));
            _patchGrid.hideFlags = HideFlags.HideAndDontSave;
            _patchRenderer = CreatePatchRenderer(PatchObjectName, surfaceAbove.sharedMaterial);

            // Underside twin (ocean clipmap only): the same dense grid drawn with the under-water
            // material fills the under-clipmap's centre hole and matches the top vertex-for-vertex, so
            // the two never show through each other at the waterline. Bounded and non-ocean windowed
            // bodies keep their single bounded under-plane (no twin), so they stay unchanged.
            if (IsOceanClipmap && surfaceUnder != null && surfaceUnder.sharedMaterial != null)
                _patchUnderRenderer = CreatePatchRenderer(PatchUnderObjectName, surfaceUnder.sharedMaterial);
        }

        // Build one near-field patch renderer over the shared sim-resolution grid using the given
        // per-body surface material instance. The _IsPatch window remap rides its property block.
        MeshRenderer CreatePatchRenderer(string objectName, Material material)
        {
            var go = new GameObject(objectName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(surfaceAbove.transform.parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _patchGrid;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        void DestroySimWindowPatch()
        {
            if (_patchRenderer != null)
            {
                DestroyRuntimeObject(_patchRenderer.gameObject);
                _patchRenderer = null;
            }
            if (_patchUnderRenderer != null)
            {
                DestroyRuntimeObject(_patchUnderRenderer.gameObject);
                _patchUnderRenderer = null;
            }
            DestroyRuntimeObject(_patchGrid);
            _patchGrid = null;
            _patchMpb = null;
            _patchUnderMpb = null;
        }

        // Feed the ocean clipmap the same per-body uniforms PLUS the _IsClipmap flag so its vertex path
        // reads world-metre positions. Increment 1 parks it at the body centre (static); the camera
        // follow + texel snap arrives in the next increment.
        void ApplyClipmapBlock()
        {
            PositionClipmap(_clipmapRenderer, ref _clipmapMpb);
            PositionClipmap(_clipmapUnderRenderer, ref _clipmapUnderMpb);
        }

        // Feed one clipmap renderer this body's per-body uniforms plus the _IsClipmap flag, and ride the
        // SAME camera-following, texel-snapped centre as the near-field patch so the meshes never slide
        // against each other (the clipmap's hole stays locked over the patch). The ocean is unbounded, so
        // the window is not clamped to the footprint. No depth bias: the far rings sit near the far plane
        // and any push would clip them (the horizon would vanish); the near-field patch is nudged toward
        // the camera instead (PatchDepthBiasNdc), and being always near it never clips.
        void PositionClipmap(Renderer clipmap, ref MaterialPropertyBlock block)
        {
            if (clipmap == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            WriteBodyProps(block);
            block.SetFloat(ID_IsClipmap, 1f);
            clipmap.SetPropertyBlock(block);

            Transform t = clipmap.transform;
            t.SetPositionAndRotation(SimWindowCenter, VolumeRotation);
            t.localScale = Vector3.one; // verts are already in world metres
        }

        // Build the unbounded-ocean clipmap: a radial ring mesh in world metres, reusing THIS body's
        // surface material with _IsClipmap on its block. Play mode only, and only when the body is a
        // true ocean (open water + opt-in + sim window). Fails loudly if the sim window is missing,
        // because without it the near-field ripple fade can't keep the far field clean.
        void CreateOceanClipmap()
        {
            if (!Application.isPlaying) return;
            if (openWater && unboundedOcean && !_windowed)
            {
                Debug.LogWarning("WaterVolume: Unbounded Ocean needs the large-body sim window " +
                                 "(Enable Large Body Window) for near-field ripples; the surface stays " +
                                 "the bounded plane until it is enabled.", this);
                return;
            }
            if (!IsOceanClipmap) return;
            if (_clipmapRenderer != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            // Hollow the clipmap centre to sit just inside the near-field patch, so the dense patch owns
            // the near field and the two meshes never overlap / z-fight (which showed as stitches + a
            // colour seam once the swell was tall). The patch's depth bias covers the overlap ring.
            float holeRadius = Mathf.Max(ClipmapMinRadius, SimHorizontalExtent * ClipmapPatchOverlap);
            _clipmapMesh = LargeWaterClipmap.BuildRadialGrid(clipmapRings, clipmapSegments,
                                                             holeRadius, clipmapOuterRadius, solidCenter: false);
            _clipmapMesh.hideFlags = HideFlags.HideAndDontSave;
            _clipmapRenderer = CreateClipmapRenderer(ClipmapObjectName, _clipmapMesh, surfaceAbove.sharedMaterial);

            // Underside twin: the SAME annulus mesh drawn with the under-water material, so its ring
            // region is byte-identical to the above clipmap (perfectly coincident, opposite cull - the
            // proven bounded above/under pairing). Its centre hole is filled by the under-patch, exactly
            // as the above clipmap's hole is filled by the near-field patch.
            if (surfaceUnder != null && surfaceUnder.sharedMaterial != null)
            {
                _clipmapUnderMesh = LargeWaterClipmap.BuildRadialGrid(clipmapRings, clipmapSegments,
                                                                      holeRadius, clipmapOuterRadius, solidCenter: false);
                _clipmapUnderMesh.hideFlags = HideFlags.HideAndDontSave;
                _clipmapUnderRenderer = CreateClipmapRenderer(ClipmapUnderObjectName, _clipmapUnderMesh,
                                                              surfaceUnder.sharedMaterial);
            }
        }

        // Build one clipmap renderer: a never-shadowing MeshRenderer over 'mesh' using the given per-body
        // surface material instance, parented beside the surface. The _IsClipmap flag rides its property
        // block (written in ApplyClipmapBlock), so it never leaks onto the pool-grid renderers.
        MeshRenderer CreateClipmapRenderer(string objectName, Mesh mesh, Material material)
        {
            var go = new GameObject(objectName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(surfaceAbove.transform.parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        void DestroyOceanClipmap()
        {
            if (_clipmapRenderer != null)
            {
                DestroyRuntimeObject(_clipmapRenderer.gameObject);
                _clipmapRenderer = null;
            }
            if (_clipmapUnderRenderer != null)
            {
                DestroyRuntimeObject(_clipmapUnderRenderer.gameObject);
                _clipmapUnderRenderer = null;
            }
            DestroyRuntimeObject(_clipmapMesh);
            _clipmapMesh = null;
            DestroyRuntimeObject(_clipmapUnderMesh);
            _clipmapUnderMesh = null;
            _clipmapMpb = null;
            _clipmapUnderMpb = null;
        }

        // (1/res, 1/res, res, res) of the sim texture, so shaders can bilinear-filter it manually
        // (WebGPU won't hardware-filter the RGBAFloat sim RT). Paired with every _WaterTex bind.
        internal Vector4 WaterTexel => new Vector4(1f / _simRes, 1f / _simRes, _simRes, _simRes);

        /// <summary>Overwrite <paramref name="mpb"/> with this body's per-renderer uniforms
        /// (sim + caustic textures, volume frame, waves, fog, foam). Used for this body's own
        /// renderers and by <see cref="WaterMembership"/> to light a floating object with the
        /// lake it is in. The block is cleared, so any per-object look must live in the material.</summary>
        public void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            if (mpb == null) throw new System.ArgumentNullException(nameof(mpb));
            Publisher.WriteBodyProps(mpb);
        }

        void ApplyBlockTo(Renderer r) { if (r != null) r.SetPropertyBlock(_mpb); }

        // World-space AABB of this body's volume (pool box x,z in [-1,1], y in [-1,0]) plus a
        // little headroom for wind-wave crests. The renderers keep huge bounds to avoid wrong
        // culling under the volume transform, so frustum culling tests this real box instead.
        internal Bounds CullBounds()
        {
            // An unbounded ocean follows the camera and is drawn everywhere, so it must never be
            // frustum-culled by its (small) footprint - that is what made the horizon surface vanish
            // once the camera left the volume bounds. Report effectively-infinite bounds instead.
            if (IsOceanClipmap)
                return new Bounds(VolumeCenter, Vector3.one * OceanCullBoundsSize);

            Bounds b = new Bounds(PoolToWorld(new Vector3(-1f, -1f, -1f)), Vector3.zero);
            b.Encapsulate(PoolToWorld(new Vector3( 1f, -1f, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, -1f,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, -1f,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, WaveHeightMargin, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, WaveHeightMargin, -1f)));
            b.Encapsulate(PoolToWorld(new Vector3(-1f, WaveHeightMargin,  1f)));
            b.Encapsulate(PoolToWorld(new Vector3( 1f, WaveHeightMargin,  1f)));
            return b;
        }

        void SetRenderersEnabled(bool on)
        {
            // An ocean body draws the horizon-reaching clipmaps INSTEAD of the bounded surface planes,
            // so the two never double-draw (z-fight). Above and under each have their own twin; the
            // clipmaps only exist in play mode, so gate on their ACTUAL presence - otherwise edit mode
            // hides a plane with nothing to replace it (the surface looks cut).
            bool clipmapActive = _clipmapRenderer != null;
            bool underClipmapActive = _clipmapUnderRenderer != null;
            SetRendererEnabled(surfaceAbove, on && !clipmapActive);
            SetRendererEnabled(surfaceUnder, on && !underClipmapActive);
            SetRendererEnabled(poolRenderer, on);
            SetRendererEnabled(_patchRenderer, on && _windowed);
            SetRendererEnabled(_patchUnderRenderer, on && IsOceanClipmap);
            SetRendererEnabled(_clipmapRenderer, on && IsOceanClipmap);
            SetRendererEnabled(_clipmapUnderRenderer, on && IsOceanClipmap);
            // God rays obey the quality tier as well as culling: a tier that disables them
            // keeps the renderer off even when the body is on-screen. Windowed bodies also
            // suppress god rays (out of scope, same reason as caustics).
            SetRendererEnabled(godRayRenderer, on && _godRaysAllowed && !_windowed);
        }

        static void SetRendererEnabled(Renderer r, bool on) { if (r != null && r.enabled != on) r.enabled = on; }

        /// <summary>Inject a ripple at a WORLD position (x,z). Converted into the pool
        /// footprint via the volume frame; out-of-footprint calls are ignored. Radius is
        /// in world units (kept round via the average horizontal extent).</summary>
        public void AddRipple(float worldX, float worldZ, float radius, float strength)
        {
            if (_water == null) return;

            // Windowed bodies inject into the sim WINDOW frame; ripples outside it are dropped.
            if (_windowed)
            {
                Vector3 sim = WorldToSim(new Vector3(worldX, SimWindowCenter.y, worldZ));
                if (sim.x < -1f || sim.x > 1f || sim.z < -1f || sim.z > 1f) return;
                _water.AddDrop(sim.x, sim.z, radius / SimHorizontalExtent, strength / VolumeExtentSafe.y);
                return;
            }

            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return;
            _water.AddDrop(px, pz, radius / VolumeHorizontalExtent, strength / VolumeExtentSafe.y);
        }

        /// <summary>World-space height (Y) of the water surface above WORLD (x,z).
        /// Returns false until the first readback lands or if outside the footprint.</summary>
        public bool TryGetWaterHeight(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight, out _)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y; // pool -> world Y
            return true;
        }

        /// <summary>World surface height (Y) plus the horizontal surface-flow (world x,z)
        /// above WORLD (x,z). For surface effects that ride the waterline (splash drift).
        /// Approximate under steep tilt; exact for rotation/rectangular/depth.</summary>
        public bool TryGetSurface(float worldX, float worldZ, out float height, out Vector2 flow)
        {
            height = 0f;
            flow = Vector2.zero;
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight, out Vector2 poolFlow)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            Vector3 worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            if (openWater)
            {
                Vector3 wave = SampleLargeWaveField(worldX, worldZ);
                height += wave.x;
                worldFlow += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
            flow = new Vector2(worldFlow.x, worldFlow.z);
            return true;
        }

        /// <summary>Sample submersion for a buoyancy point at an arbitrary WORLD point.
        /// Works under rotation/tilt/non-uniform extent because it is evaluated in pool
        /// space. Returns the world-space depth below the surface (negative = above),
        /// the volume's up direction, and the world-space surface-flow push.</summary>
        public bool TrySampleSubmersion(Vector3 worldPoint, out float depthWorld, out Vector3 up, out Vector3 worldFlow)
        {
            depthWorld = 0f;
            up = VolumeUp;
            worldFlow = Vector3.zero;
            if (_sampler == null) return false; // not initialized yet

            Vector3 pool = WorldToPool(worldPoint);
            if (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f) return false;
            if (!_sampler.TrySamplePoolSurface(worldPoint, pool.x, pool.z, out float surfaceH, out Vector2 poolFlow)) return false;

            depthWorld = (surfaceH - pool.y) * VolumeExtentSafe.y; // pool depth -> world depth along up
            worldFlow = VolumeRotation * new Vector3(poolFlow.x, 0f, poolFlow.y);
            // Open water: the world-space swell is the wind-wave source (the pool wavebank is
            // suppressed for these bodies). Raise the surface by the wave height so the point sits
            // deeper on a crest, and push along the wave slope so the swell carries the object.
            if (openWater)
            {
                Vector3 wave = SampleLargeWaveField(worldPoint.x, worldPoint.z);
                depthWorld += wave.x;
                worldFlow += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
            return true;
        }

        // ---- gameplay façade -----------------------------------------------
        // World-position-first wrappers over the sim primitives, so gameplay code (swimming,
        // audio, VFX, projectiles) queries the water without touching x/z or internals. The
        // static *At variants resolve the body that contains the point via BodyContaining.

        /// <summary>World-space surface height (Y) at a world position's x,z on THIS body.
        /// False until the first readback lands or if the point is outside the footprint.</summary>
        public bool TrySampleHeight(Vector3 worldPos, out float worldY)
            => TryGetWaterHeight(worldPos.x, worldPos.z, out worldY);

        /// <summary>True if the world point is below THIS body's surface.</summary>
        public bool IsSubmerged(Vector3 worldPos)
            => TrySampleSubmersion(worldPos, out float depth, out _, out _) && depth > 0f;

        /// <summary>Inject a ripple at a world position on THIS body (footsteps, projectiles,
        /// boats). Radius/strength are world units; out-of-footprint calls are ignored.</summary>
        public void SpawnRipple(Vector3 worldPos, float radius, float strength)
            => AddRipple(worldPos.x, worldPos.z, radius, strength);

        /// <summary>Surface height (Y) at a world position, resolving the body that contains it.
        /// False if there is no water or the readback isn't ready / point is out of footprint.</summary>
        public static bool TrySampleHeightAt(Vector3 worldPos, out float worldY)
        {
            worldY = 0f;
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.TrySampleHeight(worldPos, out worldY);
        }

        /// <summary>True if the world point is below the surface of whichever body contains it.</summary>
        public static bool IsSubmergedAt(Vector3 worldPos)
        {
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.IsSubmerged(worldPos);
        }

        /// <summary>Spawn a ripple at a world position on whichever body contains it. Returns
        /// false if there is no water body to receive it.</summary>
        public static bool TrySpawnRippleAt(Vector3 worldPos, float radius, float strength)
        {
            WaterVolume body = BodyContaining(worldPos);
            if (body == null) return false;
            body.SpawnRipple(worldPos, radius, strength);
            return true;
        }

        /// <summary>Waterline for the obstacle footprint: the ANALYTIC surface only (rest
        /// plane + wind waves), deliberately EXCLUDING the interactive ripples. Including
        /// them fed an object's own displacement back into its footprint through the stale
        /// async readback - a delayed feedback loop that kept re-exciting micro-ripples
        /// around every floater. Wind waves stay in, so a wave-riding float keeps a constant
        /// submerged depth against its waterline and injects nothing; scattering off passing
        /// ripples becomes a small, damped, open-loop effect (like the mouse, which injects
        /// without ever being influenced by the water). No readback needed: valid from frame 0.</summary>
        public bool TryGetAnalyticWaterline(float worldX, float worldZ, out float height)
        {
            height = 0f;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return false;

            // Oceans sample the wind-wave layer in WORLD metres (extent-independent) to match the shader.
            float mpu = WaveMetersPerUnit;
            float waveX = IsOceanClipmap ? worldX / mpu : px;
            float waveZ = IsOceanClipmap ? worldZ / mpu : pz;
            float poolHeight = windWaves ? _waveBank.SampleHeight(waveX, waveZ, _waveTime, mpu) : 0f;
            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            // Open water layers the big world-space swell on top of the small wind waves, mirroring
            // the shader (CPU copy of WaterLargeWaves.hlsl) so floaters ride the rendered surface.
            if (openWater)
                height += SampleLargeWaveField(worldX, worldZ).x;
            return true;
        }

        // Large-body wave field (height, dHeight/dx, dHeight/dz) at a world xz. Prefers the FFT ocean's
        // async height-field readback (so floaters ride the exact rendered swell) and falls back to the
        // analytic CPU mirror before the first readback lands or on non-FFT bodies - matching the shader's
        // own gated fallback in WaterLargeWaves.hlsl.
        Vector3 SampleLargeWaveField(float worldX, float worldZ)
        {
            if (OceanFftActive && _oceanFft.TrySampleField(worldX, worldZ, out Vector3 fft))
                return fft;
            return LargeWaveField.EvaluateAtQuery(worldX, worldZ, _waveTime, LargeWaveAmplitudeEffective,
                LargeWaveHeadingRad, SwellWavelength, SwellHeight, LargeWaveChoppiness);
        }

        void Step(float seconds)
        {
            if (seconds > MaxStepSeconds) return; // hitch/breakpoint guard, see the const
            if (seconds <= 0f) return;            // first edit-mode tick: no elapsed time yet

            // Foam runs once per frame (not per solver step), so it tracks its own elapsed
            // time in reference steps. Accumulated BEFORE the whole-step early-return below,
            // or high-fps frames that owe no solver step would be lost and foam would decay
            // slower the higher the frame rate.
            _foamTimeDebt = Mathf.Min(_foamTimeDebt + seconds * ReferenceFrameRate, MaxFoamTimeDebtSteps);

            // Frame-rate-independent stepping: the explicit solver advances a fixed amount
            // per STEP, so stepping per rendered frame made wave speed scale with fps (a
            // 120 fps editor ran ripples 4x faster than a 30 fps build). Accumulate real
            // time and pay it out in whole steps at the authored rate instead.
            _stepDebt += seconds * ReferenceFrameRate * Mathf.Max(1, stepsPerFrame);
            int steps = (int)_stepDebt;
            if (steps <= 0) return; // very high fps: no full step owed yet, field unchanged
            if (steps > MaxSolverStepsPerFrame)
            {
                steps = MaxSolverStepsPerFrame;
                _stepDebt = 0f; // drop the excess: degrade to slightly-slower waves, never a burst
            }
            else
            {
                _stepDebt -= steps;
            }

            // Scroll the sim window to track the camera before injecting/stepping, so ripples
            // stay world-anchored. No-op for whole-body bodies.
            if (_windowed) _simWindow.Track();

            // FootprintDelta mode only: push the surface with the temporally-smoothed
            // submerged footprint. In MouseLikeDrops mode the WaterInteractables emit
            // analytic drops themselves (via AddRipple) and this pass is skipped entirely.
            if (_obstacle != null && objectInteraction == ObjectInteraction.FootprintDelta)
            {
                // Windowed bodies re-frame the footprint onto the scrolling window each frame.
                if (_windowed) _obstacle.SetFrame(SimWindowCenter, VolumeRotation, SimHalfExtent);
                _obstacle.Render(VolumeCenter.y, 1f - obstacleSmoothing);
                // Compensate for extent.y so an object's displacement is a fixed world height
                // regardless of pool depth (PoolToWorld scales surface height by extent.y).
                _water.ApplyObstacle(_obstacle.Prev, _obstacle.Curr,
                                     obstacleStrength / VolumeExtentSafe.y, obstacleFlipY,
                                     obstacleDeadband);
            }

            for (int i = 0; i < steps; i++)
                _water.StepSimulation(waveSpeed, damping);

            // Exact GPU-reduced mean (no more Blit + GenerateMips: the float-mip mean silently
            // point-sampled in WebGPU builds and popped the plane; see WaterSim.compute).
            if (conserveVolume) _water.ConserveVolume(conserveMaxCorrection);

            _water.UpdateNormals();

            if (foam)
            {
                // Bi-exponential contract: thin residual lace must SURVIVE LONGER than
                // thick fresh foam (residual >= fresh), or the blend inverts and foam
                // pops off as hard-edged blobs. Scene data can't be trusted to keep the
                // ordering (the sliders' ranges overlap), so enforce it here.
                float residualSurvival = Mathf.Max(foamDecayResidual, foamDecay);
                _water.StepFoam(foamGenRate, foamDecay, residualSurvival,
                                foamSpread, foamFromSpeed, foamFromCurvature, foamAdvect,
                                _foamTimeDebt, foamDecayRate);
                _foamTimeDebt = 0f;
            }
        }

        // Choose the caustic path for this body: bounded bodies use the pool caustic (projected onto
        // the pool floor); the windowed OCEAN uses the large-body caustic (projected in the sim-window's
        // world frame, since a moving window has no fixed floor). Other windowed bodies still skip
        // caustics - the pool projection would be mismapped over their scrolling window.
        void RenderCausticsForThisBody()
        {
            if (!_windowed) { RenderCaustics(); return; }
            if (IsOceanClipmap) RenderLargeBodyCaustics();
        }

        // Render this body's own sim into its own caustic RT. The RT reaches the renderers
        // via the MPB; the primary also mirrors it to the _CausticTex global for objects.
        void RenderCaustics() => _caustics.Render(EffectiveWaterMesh, _water?.Texture);

        // Project the ocean's near-field window sim into the caustic RT via the large-body (world-frame)
        // caustic, so the underwater god rays can sample real surface-focused shimmer near the camera.
        void RenderLargeBodyCaustics() =>
            _caustics.RenderLargeBody(_patchGrid, _water?.Texture, SimWindowCenter, SimHalfExtent);

        // ---- volume placement frame (center + rotation + non-uniform extent) ----
        internal Vector3 VolumeExtentSafe => new Vector3(
            Mathf.Max(volumeExtent.x, MinVolumeExtent),
            Mathf.Max(volumeExtent.y, MinVolumeExtent),
            Mathf.Max(volumeExtent.z, MinVolumeExtent));
        // Position + rotation come from this GameObject's transform (move it to place water).
        internal Vector3 VolumeCenter => transform.position;
        internal Quaternion VolumeRotation => transform.rotation;
        internal Vector3 VolumeUp => VolumeRotation * Vector3.up;
        // Average horizontal extent, used to keep a click ripple round in world units.
        float VolumeHorizontalExtent => 0.5f * (VolumeExtentSafe.x + VolumeExtentSafe.z);

        // Tell the sim how to keep ripples ROUND in world on a rectangular (non-square) pool. The
        // heightfield runs on a square grid over pool space, so on a body with extent.x != extent.z
        // both the drop stamp and the wavefront would stretch to that ratio. We weight the wave
        // Laplacian per axis by ~1/extent^2 (equal WORLD propagation speed; normalised by the
        // smaller extent so the max weight stays at the stable 0.25) and squash the drop stamp by
        // extent/avg (matching the average-extent radius normalisation used by AddRipple). Windowed
        // bodies sim over a SQUARE world window already, so they use the identity values.
        void ApplySimAnisotropy()
        {
            if (_water == null) return;
            if (_windowed) { _water.SetAnisotropy(new Vector2(0.25f, 0.25f), Vector2.one); return; }

            float ex = VolumeExtentSafe.x;
            float ez = VolumeExtentSafe.z;
            float minExtent = Mathf.Min(ex, ez);
            float minSq = minExtent * minExtent;
            float avg = VolumeHorizontalExtent;
            var waveWeight = new Vector2(0.25f * minSq / (ex * ex), 0.25f * minSq / (ez * ez));
            var dropScale = new Vector2(ex / avg, ez / avg);
            _water.SetAnisotropy(waveWeight, dropScale);
        }

#if UNITY_EDITOR
        // One-time editor notice: large bodies (big lakes / oceans) are experimental in this
        // proof-of-concept. The interactive ripple sim is a POOL solver on a fixed grid, so past
        // ~20 m of extent the ripples go coarse and the analytic wind waves aren't ocean-scale.
        // Editor-only so a shipped build never logs it. See the README "Scope" notes.
        const float LargeBodyWarnExtent = 20f; // world half-extent (metres) where the pool solver frays
        bool _largeBodyWarned;

        void WarnIfLargeBody()
        {
            if (_largeBodyWarned) return;
            Vector3 e = VolumeExtentSafe;
            float maxExtent = Mathf.Max(e.x, e.z);
            if (maxExtent <= LargeBodyWarnExtent) return;

            _largeBodyWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' is a large water body (extent ~{maxExtent:0} m). Large bodies " +
                "(big lakes / oceans) are experimental in this version: the interactive ripple sim is a " +
                "pool solver, so its ripples get coarse and the wind waves aren't ocean-scale. This asset " +
                "targets small-to-mid bodies - see the README \"Scope\" notes.", this);
        }

        // One-time editor notice: Unity Terrain integration (the bed-depth bake) is experimental in
        // this proof-of-concept - it approximates a shoreline depth gradient, not full terrain support.
        bool _terrainWarned;

        void WarnIfExperimentalTerrain()
        {
            if (_terrainWarned || !useBedDepth) return;
            _terrainWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' uses terrain bed-depth (Use Bed Depth). Unity Terrain integration " +
                "is experimental in this version - the baked shoreline depth is a basic approximation, not " +
                "full terrain support. See the README \"Scope\" notes.", this);
        }
#endif

        internal Vector3 PoolToWorld(Vector3 pool) => VolumeCenter + VolumeRotation * Vector3.Scale(pool, VolumeExtentSafe);

        internal Vector3 WorldToPool(Vector3 world)
        {
            Vector3 e = VolumeExtentSafe;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (world - VolumeCenter);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        /// <summary>True when the underwater fog pass should run this frame (set each frame by the
        /// primary body). Ocean fog is infinite, so it runs only when the camera is submerged; a bounded
        /// pond is a finite volume the shader clips to its box, so its fog runs from ANY angle whenever
        /// Water Fog is on (circle the pond and see the murk inside). The feature reads this to gate.</summary>
        internal static bool UnderwaterFogActive { get; private set; }

        // Detect whether the camera is submerged in THIS (primary) body and publish the globals the
        // underwater fog shader needs. U1 uses the flat surface plane (VolumeCenter.y); a wave-accurate
        // waterline is a later step. Bounded bodies require the camera inside their footprint; an ocean
        // clipmap spans everywhere, so only the height test applies.
        void UpdateUnderwaterState()
        {
            bool submerged = ComputeCameraSubmerged(out float surfaceY);
            // Ocean fog is infinite, so it only matters when the camera is submerged. A bounded pond is a
            // finite fog volume clipped to its box, so it should render from ANY angle (circle it and see
            // the murk inside) whenever Water Fog is on.
            UnderwaterFogActive = waterFog && (IsOceanClipmap ? submerged : true);
            // The unbounded flag tells the shader to fog the whole below-surface half-space (ocean) vs
            // clip the fog to this body's box (pond / bounded lake = a finite fog volume).
            Publisher.PublishUnderwater(submerged ? 1f : 0f, surfaceY, IsOceanClipmap ? 1f : 0f);
        }

        // A little beyond the [-1,1] footprint so an edge-on view of a pond still triggers; the shader
        // box-clips the fog per pixel, so this CPU gate only has to be roughly right.
        const float UnderwaterFootprintMargin = 1.25f;

        // Water intersects the view as soon as the camera's NEAR PLANE dips below the surface (partial
        // submersion, KWS-style), not only when the whole camera is under - otherwise a shallow pond
        // never triggers. Sample the four near-plane corners (plus the eye) and run on the lowest.
        bool ComputeCameraSubmerged(out float surfaceY)
        {
            surfaceY = VolumeCenter.y;
            if (!waterFog) return false; // one Water Fog toggle drives both looking-in and submerged fog
            Camera cam = targetCamera;
            if (cam == null) return false;

            float near = cam.nearClipPlane;
            float lowestY = cam.transform.position.y;
            lowestY = Mathf.Min(lowestY, cam.ViewportToWorldPoint(new Vector3(0f, 0f, near)).y);
            lowestY = Mathf.Min(lowestY, cam.ViewportToWorldPoint(new Vector3(1f, 0f, near)).y);
            lowestY = Mathf.Min(lowestY, cam.ViewportToWorldPoint(new Vector3(0f, 1f, near)).y);
            lowestY = Mathf.Min(lowestY, cam.ViewportToWorldPoint(new Vector3(1f, 1f, near)).y);
            if (lowestY >= surfaceY) return false; // the near plane is entirely above the surface

            if (IsOceanClipmap) return true; // the ocean spans everywhere
            Vector3 pool = WorldToPool(cam.transform.position);
            return Mathf.Abs(pool.x) <= UnderwaterFootprintMargin && Mathf.Abs(pool.z) <= UnderwaterFootprintMargin;
        }

        // ---- large-water sim window frame ----------------------------------
        // Half-size (world) of the window: simWindowMeters horizontally, the body's depth
        // scale vertically (ripple height stays coupled to extent.y like the whole-body sim).
        internal Vector3 SimHalfExtent => new Vector3(
            Mathf.Max(simWindowMeters, MinWindowHalfExtent),
            VolumeExtentSafe.y,
            Mathf.Max(simWindowMeters, MinWindowHalfExtent));

        // Average horizontal window half-size, keeping an injected ripple round in world units.
        float SimHorizontalExtent => Mathf.Max(simWindowMeters, MinWindowHalfExtent);

        // ---- GPU consumer API (foam particles and similar per-body effects) ----

        /// <summary>Sim state texture (height, velocity, normal.xz) for GPU consumers.</summary>
        public RenderTexture SimStateTexture => _water?.Texture;
        /// <summary>Current foam-amount texture (R channel) for GPU consumers.</summary>
        public RenderTexture FoamMaskTexture => _water?.FoamTexture;
        /// <summary>Grid resolution of the active sim (per side), fixed at startup.</summary>
        public int SimResolution => _simRes;
        /// <summary>True when this body runs its GPU sim this frame (visible, in range,
        /// within the sim budget, not paused). GPU consumers should idle when false.</summary>
        public bool IsSimulating => _simulate && !_paused;
        /// <summary>True when this body's renderers draw this frame (frustum cull).</summary>
        public bool IsVisibleToCamera => _visible;

        /// <summary>Push this body's placement-frame uniforms (volume + sim window) onto a
        /// compute shader so GPU consumers can include WaterVolume.hlsl and share the exact
        /// same pool/window/world transforms as the render side.</summary>
        public void WriteSimFrameUniforms(ComputeShader cs)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            Publisher.WriteSimFrameUniforms(cs);
        }

        /// <summary>World-space area covered by one sim texel (m^2), for density-normalised
        /// GPU spawning. Uses the window frame when windowed, else the whole volume.</summary>
        public float SimTexelWorldArea
        {
            get
            {
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                float texelX = 2f * half.x / _simRes;
                float texelZ = 2f * half.z / _simRes;
                return texelX * texelZ;
            }
        }

        /// <summary>Loose world bounds of the active sim frame (surface plane plus wave
        /// headroom), for culling GPU-driven draws that follow this body.</summary>
        public Bounds SimWorldBounds
        {
            get
            {
                Vector3 center = _windowed ? SimWindowCenter : VolumeCenter;
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                // Rotation-safe: expand horizontally by the diagonal, vertically by the
                // depth plus wave headroom.
                float horizontal = Mathf.Sqrt(half.x * half.x + half.z * half.z);
                float vertical = half.y * (1f + WaveHeightMargin);
                return new Bounds(center, 2f * new Vector3(horizontal, vertical, horizontal));
            }
        }

        /// <summary>True if this body runs the camera-following windowed sim (decided at
        /// startup from its size and the threshold).</summary>
        public bool IsWindowed => _windowed;
        /// <summary>World centre of the active sim window (follows the camera at runtime).
        /// The volume centre until the window exists.</summary>
        public Vector3 SimWindowCenter => _simWindow != null ? _simWindow.Center : VolumeCenter;
        /// <summary>World half-size (x,z) and depth scale (y) of the sim window.</summary>
        public Vector3 SimWindowHalfExtent => SimHalfExtent;

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window).
        internal Vector3 WorldToSim(Vector3 world) => _simWindow.WorldToSim(world);

        // Windowing turns on for bodies whose horizontal half-extent exceeds the threshold.
        bool ShouldWindow()
        {
            if (!enableLargeBodyWindow) return false;
            // An unbounded ocean is infinite by definition, so the footprint-size threshold does not
            // apply - it always needs the camera-following window for its near-field ripples.
            if (openWater && unboundedOcean) return true;
            Vector3 e = VolumeExtentSafe;
            return Mathf.Max(e.x, e.z) > largeBodyThreshold;
        }

        // World point -> pool. Returns false if outside the [-1,1] horizontal footprint.
        bool WorldToPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f;
        }

        // Intersect a camera ray with the (possibly tilted) surface plane through the
        // volume centre. Returns the world hit and its pool x,z (which may fall outside
        // [-1,1]); false only if the ray is parallel to or points away from the plane.
        bool TryPickSurface(Vector3 eye, Vector3 dir, out Vector3 worldHit, out float poolX, out float poolZ)
        {
            worldHit = Vector3.zero; poolX = 0f; poolZ = 0f;
            Vector3 n = VolumeUp;
            float denom = Vector3.Dot(dir, n);
            if (Mathf.Abs(denom) < RayParallelEpsilon) return false;
            float t = Vector3.Dot(VolumeCenter - eye, n) / denom;
            if (t < 0f) return false;
            worldHit = eye + dir * t;
            Vector3 pool = WorldToPool(worldHit);
            poolX = pool.x; poolZ = pool.z;
            return true;
        }

        // ---- wind-wave layer -----------------------------------------------
        internal float WaveMetersPerUnit => Mathf.Max(MinWaveMetersPerUnit, poolHalfExtentMeters);

        // Regenerate the bank only when a wind/scale parameter actually changes, so
        // the phases stay stable frame-to-frame (a fresh bank would pop the surface).
        void EnsureWaveBank()
        {
            int count = EffectiveWaveCount;
            float verticalExtent = VolumeExtentSafe.y;
            bool dirty = windWaves != _waveGenEnabled
                         || windSpeed != _waveGenWindSpeed
                         || windFromDegrees != _waveGenWindFrom
                         || poolHalfExtentMeters != _waveGenExtentMeters
                         || count != _waveGenCount
                         || waveAmplitudeScale != _waveGenAmpScale
                         || waveDirectionSpread != _waveGenSpread
                         || verticalExtent != _waveGenVerticalExtent;
            if (!dirty) return;

            _waveBank.Generate(windSpeed, windFromDegrees, 2f * poolHalfExtentMeters,
                               count, waveAmplitudeScale, waveDirectionSpread, WaveMetersPerUnit,
                               verticalExtent);
            _waveGenWindSpeed = windSpeed;
            _waveGenWindFrom = windFromDegrees;
            _waveGenExtentMeters = poolHalfExtentMeters;
            _waveGenCount = count;
            _waveGenAmpScale = waveAmplitudeScale;
            _waveGenSpread = waveDirectionSpread;
            _waveGenVerticalExtent = verticalExtent;
            _waveGenEnabled = windWaves;
        }

        // The authored component count capped by the quality tier (mobile tiers sum fewer
        // sinusoids per vertex/pixel/buoyancy query).
        int EffectiveWaveCount => Mathf.Min(waveCount, _maxWaveCount);

        // Wave arrays are per-body, mirrored to globals only by the primary (see WriteBodyUniforms).
        // The wave CLOCK (_WaveTime) is genuinely shared and published in PublishSharedGlobals.

        // With the link on, the depth colour tracks the fog extinction so a single dial drives
        // both; off, the depth colour is authored independently.
        internal Color EffectiveDepthExtinction => linkDepthToFog ? fogExtinction : depthExtinction;

        // ---- terrain bed-height bake (WaterBedBaker) --------------------------

        /// <summary>Re-sample the terrain heightmap into the pool-space bed map. Call after
        /// the terrain or the volume placement changes.</summary>
        [ContextMenu("Rebake Bed")]
        public void RebakeBed() => BedBaker.Rebake();

        // ---- edit-mode preview ------------------------------------------------
        // The editor preview driver (Editor/WaterEditorPreviewDriver) pumps the player loop
        // while any body is alive so Update runs without Play; these support it.

        /// <summary>Number of live (enabled) water bodies. Editor-preview driver hook.</summary>
        internal static int ActiveBodyCount => Bodies.Count;

        double _lastEditorTick;

        // Real elapsed time between edit-mode ticks, clamped (see MaxEditorDeltaSeconds).
        // First tick after enable returns 0 so no time is invented.
        float EditorDeltaSeconds()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float dt = _lastEditorTick > 0d ? (float)(now - _lastEditorTick) : 0f;
            _lastEditorTick = now;
            return Mathf.Min(dt, MaxEditorDeltaSeconds);
        }

        // ---- interaction (WaterInputRouter drives this) -----------------------

        /// <summary>Does this body's surface plane lie under the ray, within its footprint?
        /// Returns the world hit point. Lets the input router pick which lake was clicked.</summary>
        public bool TryRaycastSurface(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            if (!TryPickSurface(ray.origin, ray.direction, out Vector3 hit, out float px, out float pz)) return false;
            if (Mathf.Abs(px) > 1f || Mathf.Abs(pz) > 1f) return false;
            worldHit = hit;
            return true;
        }
    }
}
