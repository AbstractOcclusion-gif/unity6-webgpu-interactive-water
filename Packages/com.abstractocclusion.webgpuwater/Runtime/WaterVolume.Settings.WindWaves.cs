// WaterVolume settings - the analytic wind-wave layer that covers the whole body, everywhere,
// independent of the interactive ripple sim's window.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

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
            // Wind lives in this block for serialization reasons only - it is drawn in its own
            // inspector section because it steers EVERY wave layer, not just this one.
            [Tooltip("Wind speed (m/s). ~3 = light breeze. Gates whitecaps and drives foam drift; it " +
                     "no longer sets the size of any wave layer.")]
            [Range(0f, 15f)] public float windSpeed = 3f;
            [Tooltip("Wind heading in degrees: 0 = blowing toward +X (i.e. coming from the west).")]
            [Range(0f, 360f)] public float windFromDegrees = 0f;

            [Tooltip("WAVE LENGTH (metres): crest-to-crest distance of the dominant ripple. Small " +
                     "values are pond fizz; 1-4 m is the chop you get in the middle of a lake. " +
                     "Authored directly - it used to be derived from wind and fetch and then clamped " +
                     "to 0.6 m, which is where every setting landed, so wind appeared to do nothing.")]
            [Min(WaveLengthMetersMin)] public float waveLengthMeters = DefaultWaveLengthMeters;
            [Tooltip("WAVE HEIGHT (metres, significant height): the average height of the biggest " +
                     "third of these ripples. Independent of Wave Length, so the two together set " +
                     "steepness - which is what makes chop read as agitated rather than just big.")]
            [Min(0f)] public float waveHeightMeters = DefaultWaveHeightMeters;
            [Tooltip("SETS: how strongly the ripples gather into travelling groups instead of an even " +
                     "buzz. Real chop arrives in patches of about seven waves that drift at half the " +
                     "wave speed. 0 = uniform (the old look); 1 = strongly patchy. Does not change " +
                     "the wave height.")]
            [Range(0f, 1f)] public float waveGrouping = DefaultWaveGrouping;
            [Tooltip("CREST SHARPNESS: 0 = round sine humps; 1 = sharp crests over flat troughs, the " +
                     "shape a real wave has. The effect grows with steepness, so it is invisible on " +
                     "calm water and strongest exactly where the plain sines looked worst. Height " +
                     "stays a true function of position, so buoyancy is unaffected.")]
            [Range(0f, 1f)] public float waveCrestSharpness = DefaultWaveCrestSharpness;
            [Tooltip("Number of sinusoidal components summed for the wave layer.")]
            [Range(1, WaterWaveBank.MaxWaves)] public int waveCount = 12;
            [Tooltip("Higher = waves cling more tightly to the wind direction (parallel, river-like). " +
                     "Lower = broader, choppier crossing crests.")]
            [Range(1f, 12f)] public float waveDirectionSpread = 2f;
            [Tooltip("Scales how strongly the wind waves tilt the surface normal.")]
            [Range(0f, 3f)] public float waveNormalStrength = 1f;

            // Legacy captures for the pre-v11 (wind, fetch, amplitude scale) rig. They have to live
            // INSIDE this block, not as top-level WaterVolume fields: that is where the values were
            // serialized, and [FormerlySerializedAs] cannot reach across into a nested object. Read
            // once by MigrateWindWaveRigV11 to recover a scene's real look, then never again.
            // Hidden; do not edit.
            [SerializeField, HideInInspector]
            [FormerlySerializedAs("poolHalfExtentMeters")]
            [FormerlySerializedAs("waveScaleMeters")]
            [FormerlySerializedAs("windWaveFetchMeters")]
            internal float legacyFetchMeters = 10f;
            [SerializeField, HideInInspector]
            [FormerlySerializedAs("waveAmplitudeScale")]
            internal float legacyAmplitudeScale = 4f;
        }

        // Sea-state defaults for the ripple layer. 5 cm on a 1.2 m wavelength is a light lake chop:
        // clearly visible in the specular, still small enough to sit under an ocean swell. Both are
        // roughly twenty times the old rendered height, which was 2.7 mm - see WaterWaveBank's header
        // for why that number was so small and so hard to find.
        const float DefaultWaveLengthMeters = 1.2f;
        const float DefaultWaveHeightMeters = 0.05f;
        // Below a centimetre the layer is finer than any mesh or normal map can carry it.
        const float WaveLengthMetersMin = 0.01f;
        // Defaults that ship the new shaping ON: the whole point of adding them is that the plain
        // sine field was the thing that looked simplistic. Set either to 0 for the old behaviour.
        const float DefaultWaveGrouping = 0.4f;
        const float DefaultWaveCrestSharpness = 0.6f;

        // Same-named forwarding accessors keep every reader unchanged. WindWaves stays a public get/set
        // (sample scripting API) targeting the settings; windWaves is the private read for internal use.
        bool windWaves => windWaveSettings.windWaves;
        internal float windSpeed => windWaveSettings.windSpeed;
        internal float windFromDegrees => windWaveSettings.windFromDegrees;

        internal int waveCount => windWaveSettings.waveCount;
        internal float waveLengthMeters => windWaveSettings.waveLengthMeters;
        internal float waveHeightMeters => windWaveSettings.waveHeightMeters;
        internal float waveGrouping => windWaveSettings.waveGrouping;
        internal float waveCrestSharpness => windWaveSettings.waveCrestSharpness;
        internal float waveDirectionSpread => windWaveSettings.waveDirectionSpread;
        internal float waveNormalStrength => windWaveSettings.waveNormalStrength;

        /// <summary>Ambient wind-driven wave layer composited on top of the interactive
        /// ripples. Floating objects ride these waves too.</summary>
        public bool WindWaves { get => windWaveSettings.windWaves; set => windWaveSettings.windWaves = value; }
    }
}
