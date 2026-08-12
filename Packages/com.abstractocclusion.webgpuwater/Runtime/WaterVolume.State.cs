// WebGpuWater - WaterVolume partial: non-serialized runtime state and tuning constants.
//
// Everything the body owns for the duration of an enable but never serializes: the wind-wave
// bank and its generation inputs, the quality-tier cost knobs, the per-body material/mesh
// instances, the frame-schedule flags and the numeric guards the rest of the class reads.
// Split out of WaterVolume.cs so the orchestration partial reads as lifecycle, not as a field wall.
// The SERIALIZED configuration surface lives in WaterVolume.Settings.cs - this is its opposite.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // wind-wave layer (shared by the surface shader and CPU buoyancy)
        readonly WaterWaveBank _waveBank = new WaterWaveBank();
        float _waveTime;
        // Bank-generation inputs baked into the current bank, compared field-by-field. (A
        // packed signature could alias two distinct states and silently keep stale amplitudes.)
        float _waveGenWindFrom = float.NaN;
        float _waveGenExtentMeters;
        int _waveGenCount;
        float _waveGenLength = float.NaN;
        float _waveGenHeight = float.NaN;
        float _waveGenGrouping = float.NaN;
        float _waveGenSharpness = float.NaN;
        float _waveGenAnimationSpeed = float.NaN;
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
        int _oceanFftInterval = WaterQuality.Default.OceanFftInterval;
        int _maxFoamParticles = WaterQuality.Default.MaxFoamParticles;
        WaterQuality.UnderwaterMode _underwaterFogMode = WaterQuality.Default.UnderwaterFog;
        /// <summary>Tier cap on the GPU foam-particle pool (WaterFoamParticles clamps to it).</summary>
        internal int FoamParticleBudget => _maxFoamParticles;
        // Per-body surface material instances so reflection keywords don't leak across bodies
        // that share the source material. Created at OnEnable (play mode only) and destroyed at
        // OnDisable, which also restores the renderer's original shared material so an
        // enable/disable cycle never leaves a renderer pointing at a destroyed instance.
        Material _surfaceAboveInstance, _surfaceUnderInstance;
        Material _surfaceAboveOriginal, _surfaceUnderOriginal;
        // Low-tier coarse grid swapped onto the surface renderers at init (play mode only);
        // the originals are restored on disable, mirroring the material-instance pattern.
        Mesh _lowDetailGrid;
        Mesh _surfaceAboveOriginalMesh, _surfaceUnderOriginalMesh;
        MaterialPropertyBlock _mpb; // per-body uniforms pushed to this body's renderers

        // Round (disc) surface footprint for a CHUNK body: ApplyMeshDetail rebuilds the play-mode
        // surface as a disc instead of the square grid, so a sphere/round chunk reads circular.
        // Default false = the square footprint every existing body uses. Serialized so it survives
        // play mode / domain reload (ApplyMeshDetail runs from the serialized state).
        [SerializeField, HideInInspector] internal bool discSurface;
        const int DiscSurfaceMinSegments = 24; // angular floor so a low sim res still reads round

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

        // Interactive-ripple density (bounded bodies): the ripple sim is a grid stretched over the
        // footprint, so a fixed resolution blurs as the plane grows (fine at ~5 m, coarse by ~40 m).
        // Scale the grid with the footprint at a per-quality texel density, clamped between a per-quality
        // floor and cap. The floor keeps SMALL pools dense (High/Ultra hold the pre-scaling 256 grid so a
        // small pool stays crisp); the cap bounds the cost on big planes. Both are multiples of the
        // compute thread-group size. The surface mesh is matched to the result (see SurfaceMeshDetail)
        // so displaced ripples are round.
        readonly struct RippleQualitySetting
        {
            public readonly float TexelsPerMeter;
            public readonly int MinResolution; // small-pool floor; multiple of WaterSimulation.ThreadGroupSize
            public readonly int MaxResolution; // big-plane cap; multiple of WaterSimulation.ThreadGroupSize

            public RippleQualitySetting(float texelsPerMeter, int minResolution, int maxResolution)
            {
                TexelsPerMeter = texelsPerMeter;
                MinResolution = minResolution;
                MaxResolution = maxResolution;
            }
        }

        static readonly System.Collections.Generic.Dictionary<RippleQuality, RippleQualitySetting> RippleQualityTable =
            new System.Collections.Generic.Dictionary<RippleQuality, RippleQualitySetting>
            {
                { RippleQuality.Low,    new RippleQualitySetting(8f, 128, 192) },
                { RippleQuality.Medium, new RippleQualitySetting(12f, 192, 256) },
                { RippleQuality.High,   new RippleQualitySetting(16f, 256, 320) },
                { RippleQuality.Ultra,  new RippleQualitySetting(24f, 256, 384) },
            };

        // Upper bound on fog density. Was 50, where the top ~85% of the slider was indistinguishable
        // pea soup and the band artists actually use had ~6% of the travel: density MULTIPLIES the
        // per-channel extinction, so at the shipped red extinction of 0.45 even density 10 already
        // puts half-brightness at 15 cm, and density 50 kills red inside a millimetre. The highest
        // value authored across every shipped demo body is 7.2, so 10 keeps real headroom. Denser
        // water comes from the extinction colour, which is HDR and deliberately unbounded.
        const float MaxFogDensity = 10f;

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
        // LOWERED 8 -> 3 (perf audit 2026-08-11): the cap was a positive-feedback amplifier on
        // exactly the frames that could least afford it. A frame slowed by something ELSE (the
        // first-use PSO compile of an 8000-line surface variant is the reproducible case) owes
        // proportionally more debt, so it dispatched 4x the sim compute, which kept the NEXT
        // frame slow - a compile hitch of a few frames stretched into seconds of low fps before
        // the scene "settled". Three still absorbs an ordinary 20 fps dip at the authored
        // stepsPerFrame = 2; past that the excess is dropped, which is the documented trade
        // above (slightly slower waves, never a burst).
        const int MaxSolverStepsPerFrame = 3;
        // Cap on the foam time debt (reference steps). Deliberately NOT lowered with
        // MaxSolverStepsPerFrame: foam runs ONE dispatch per frame whatever the debt, so the
        // number only scales how much decay that dispatch applies - it costs nothing to catch up.
        const float MaxFoamTimeDebtSteps = 8f;

        // Numeric guards.
        const float MinVolumeExtent = 1e-5f;        // a zero extent would collapse the pool-space transforms
        const float MinWindowHalfExtent = 1e-3f;    // same guard for the scrolling sim window
        const float RayParallelEpsilon = 1e-6f;     // surface picking: treat near-parallel rays as a miss
        internal const float MinBedFadeDepth = 0.01f; // keeps the bed depth scale finite (publisher)
        const float MinWaveMetersPerUnit = 1e-3f;   // keeps wave-space conversions finite

        // Edit-mode preview: Update ticks come from the editor loop at an uneven cadence, so
        // the sim integrates real elapsed time, clamped so a pause between repaints doesn't
        // feed one huge step into the solver.
        const float MaxEditorDeltaSeconds = 1f / 30f;
    }
}
