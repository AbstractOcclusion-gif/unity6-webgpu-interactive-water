// WebGpuWater - a saved water LOOK: the volume's appearance domains as one shareable asset.
//
// Mirrors the exact nested Settings classes (and field names) WaterVolume serializes, so the
// editor can capture/apply by generic subtree copy (WaterLookPresetSync) with zero per-field
// code - a field added to any Settings block becomes preset data automatically, and a rename
// covered by FormerlySerializedAs migrates both sides together. The include flags gate APPLY
// per domain (a "storm waves" preset ships with only Waves on); capture always stores the full
// look. Deliberately absent: scene wiring, body size/topology, shore/bed data, and every
// quality/budget knob - a preset changes how water LOOKS, never what it IS or what it costs.
// Runtime class (not editor-only) so a future runtime-apply phase can load the same assets.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [CreateAssetMenu(fileName = "WaterLookPreset",
                     menuName = "AbstractOcclusion/WebGpuWater/Water Look Preset")]
    public sealed class WaterLookPreset : ScriptableObject
    {
        [Tooltip("What this look is for - purely a note to your future self.")]
        [TextArea] [SerializeField] internal string notes = "";

        [Header("Domains applied by this preset")]
        [Tooltip("Ocean spectrum/swell/whitecaps/god rays + the small wind-wave layer.")]
        public bool includeWaves = true;
        [Tooltip("Water colour: Jerlov type, fog, volume scattering, depth attenuation, refracted shadows.")]
        public bool includeAppearance = true;
        [Tooltip("Surface film: reflections, detail normals, foam/whitecap textures.")]
        public bool includeSurface = true;
        [Tooltip("Interactive sim foam (wake/turbulence generation and look).")]
        public bool includeFoam = true;
        [Tooltip("The underside of the surface seen from below (mirror, meniscus).")]
        public bool includeUnderwater = true;
        [Tooltip("Interactive ripple simulation feel.")]
        public bool includeRipples = true;

        // ---- mirrored look blocks - field names MUST match WaterVolume's (generic path copy;
        // WaterLookPresetSync fails loudly if either side drifts). Defaults mirror the volume's.
        [SerializeField] internal JerlovWaterType jerlovWaterType = JerlovWaterType.OceanII;
        [SerializeField] internal WaterVolume.OceanSettings ocean = new WaterVolume.OceanSettings();
        [SerializeField] internal WaterVolume.WindWaveSettings windWaveSettings = new WaterVolume.WindWaveSettings();
        [SerializeField] internal WaterVolume.WaterFogSettings waterFogSettings = new WaterVolume.WaterFogSettings();
        [SerializeField] internal WaterVolume.VolumeScatterSettings volumeScatterSettings = new WaterVolume.VolumeScatterSettings();
        [SerializeField] internal WaterVolume.DepthAttenuationSettings depthAttenuation = new WaterVolume.DepthAttenuationSettings();
        [SerializeField] internal bool refractShadows = true;
        [SerializeField, Range(0f, 1f)] internal float refractShadowSoftness = 0.5f;
        [SerializeField] internal WaterVolume.ReflectionSettings reflectionSettings = new WaterVolume.ReflectionSettings();
        [SerializeField] internal WaterVolume.DetailNormalSettings detailNormalSettings = new WaterVolume.DetailNormalSettings();
        [SerializeField] internal Texture foamPatternTexture;
        [SerializeField] internal Vector2Int foamPatternGrid = new Vector2Int(1, 1);
        [SerializeField] internal float foamPatternFps = 10f;
        [SerializeField] internal float foamReliefStrength = 1f;
        [SerializeField] internal Texture oceanWhitecapTexture;
        [SerializeField] internal Vector2Int oceanWhitecapGrid = new Vector2Int(1, 1);
        [SerializeField] internal float oceanWhitecapFps = 10f;
        [SerializeField] internal WaterVolume.FoamSettings foamSettings = new WaterVolume.FoamSettings();
        [SerializeField] internal WaterVolume.UnderwaterSurfaceSettings underwaterSurfaceSettings = new WaterVolume.UnderwaterSurfaceSettings();
        [SerializeField] internal WaterVolume.RippleSettings rippleSettings = new WaterVolume.RippleSettings();
    }
}
