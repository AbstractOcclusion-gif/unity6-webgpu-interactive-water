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
    }
}
