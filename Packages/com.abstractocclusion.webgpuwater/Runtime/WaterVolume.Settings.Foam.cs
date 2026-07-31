// WaterVolume settings - foam. Four families share this block but NOT their knobs: turbulence,
// ocean whitecaps, shore swash and shading each keep their own gate, so retuning one cannot
// silently retune another.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

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
            [Tooltip("Keep the wet-ground memory running even when Foam itself is off. The sim tracks " +
                     "the highest recent waterline per column in the foam buffer's second channel; " +
                     "WaterReceiver (and the terrain shader) read it so ground stays wet AFTER the wave " +
                     "that wetted it has moved on. Leave off if nothing in the scene uses wetness - it " +
                     "costs a sim dispatch per frame on a body that would otherwise skip it. When Foam " +
                     "IS on the memory is maintained anyway, for free.")]
            public bool wetnessMemory = false;
            [Tooltip("How long wet ground takes to dry, in SECONDS - the time for a wet mark to fade " +
                     "to roughly 5% of the level it was wetted to. Height-independent on purpose: a " +
                     "small ripple and a big splash both dry in this time, so the number means what it " +
                     "says. Raise for long dark tide marks that linger behind the water; lower for " +
                     "wetness that vanishes almost with the wave.")]
            [Range(0.1f, 30f)] public float wetnessDryTime = 3f;
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
            [Tooltip("Yield to existing foam: how strongly ripple/turbulence generation is scaled " +
                     "down by the foam ALREADY on a column. 0 = every foam source stacks into one " +
                     "saturating channel, which is why a breaking shore - already white from the " +
                     "surf engine - clips to a flat slab the moment ripple foam is switched on. " +
                     "1 = ripple foam only fills water that is still clear, so the shore keeps the " +
                     "surf's own structure. This is a SPATIAL limit, not a strength cut: it never " +
                     "weakens foam where there is room for it, so splashes and wakes in open water " +
                     "are untouched - which is exactly what raising the activity gates above " +
                     "cannot do.")]
            [Range(0f, 1f)] public float foamHeadroom = 0f;
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
        internal bool wetnessMemory => foamSettings.wetnessMemory;
        internal float wetnessDryTime => foamSettings.wetnessDryTime;
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
        internal float foamHeadroom => foamSettings.foamHeadroom;
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
    }
}
