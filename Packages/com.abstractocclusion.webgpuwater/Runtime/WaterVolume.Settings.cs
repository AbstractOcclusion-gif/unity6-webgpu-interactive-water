// WebGpuWater - WaterVolume: the serialized CONFIGURATION surface (root).
// Split out of WaterVolume.cs (final-clean E), then split again per feature 2026-07-27 - both
// times a VERBATIM move: any behaviour change in these files is a bug. The runtime ORCHESTRATION
// (lifecycle, update loop, solver) stays in WaterVolume.cs.
//
// This file holds the scene-builder wiring and the body's own frame - what it IS and where it is.
// Each per-feature Settings block lives in WaterVolume.Settings.<Feature>.cs, because one
// 1860-line file made it impossible to see which block a given forwarding accessor belonged to.
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

        [Tooltip("Ocean wave whitecap - a single tiling texture, or a flipbook when the grid below is a " +
                 "real grid. This texture drives BOTH the deep ocean whitecaps AND the shore-wave " +
                 "whitewash. Empty = keep the water material's own whitecap texture (_OceanWhitecapTex).")]
        [SerializeField] internal Texture oceanWhitecapTexture;
        [Tooltip("Whitecap flipbook grid (cols, rows). (1,1) = a single seamless tiling texture, no " +
                 "flipbook. A real grid animates the deep whitecaps AND the shore-wave foam together.")]
        [SerializeField] internal Vector2Int oceanWhitecapGrid = new Vector2Int(1, 1);
        [Tooltip("Whitecap flipbook frame rate (frames/sec). 0 = a static frame.")]
        [Range(0f, 30f)] [SerializeField] internal float oceanWhitecapFps = 10f;

        [Tooltip("Interactive-ripple detail on a bounded body: higher = a denser sim grid (crisper, " +
                 "rounder ripples) with a matched surface mesh, at more GPU cost. No effect on windowed oceans.")]
        [SerializeField] internal RippleQuality rippleQuality = RippleQuality.High;

        [Header("Body type")]
        [Tooltip("Body archetype. Advisory: drives which inspector sections are relevant and the " +
                 "'Apply defaults' action. Pond = small bounded pool; Lake = large / open bounded water; " +
                 "Ocean = unbounded open water to the horizon.")]
        [SerializeField] internal WaterBodyType bodyType = WaterBodyType.Pond;

        [Tooltip("Render this volume's built-in flat surfaces, analytic pool, god rays, and " +
                 "runtime patch/clipmap geometry. Disable for an external surface such as a river " +
                 "ribbon; simulation and per-body shader uniforms remain active. Fullscreen volume " +
                 "fog is controlled separately in the Volume tab.")]
        [SerializeField] internal bool renderBuiltInGeometry = true;

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
