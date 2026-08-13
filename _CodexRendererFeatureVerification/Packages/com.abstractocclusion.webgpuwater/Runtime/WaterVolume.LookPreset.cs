// WebGpuWater - WaterVolume: runtime look-preset apply (phase 2).
//
// Runtime twin of the editor's WaterLookPresetSync: the same six domains, the same include
// flags, the same preserved fields - but through plain field copies instead of
// SerializedProperties, so a weather system or gameplay code can swap looks in a build.
// THE PAIRING RULE: a domain or preserved field added on either side must be added to the
// other - WaterLookPresetSync.Domains is the authoritative list; this file mirrors it.
//
// Blocks are deep-copied via JsonUtility overwrite (preset assets are shared objects; the
// volume must never alias them). JsonUtility round-trips UnityEngine.Object references by
// instanceID, which is stable within a running session - texture references survive.
// Live effect: the per-frame uniform publisher picks up colour/surface values on the next
// frame; the wind-wave bank and the ocean FFT spectrum both change-detect their inputs
// (WaterVolume.Waves dirty set, WaterOceanFft.SeaParams) and rebuild when the values move.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        /// <summary>Applies a look preset at runtime, honouring its per-domain include flags.
        /// Topology (open water), budget (clipmap resolution, god-ray steps) and the planar
        /// exclusion layer mask keep the body's own values, exactly like the editor apply.
        /// In the editor, prefer the inspector's Look Presets section (undoable).</summary>
        public void ApplyLookPreset(WaterLookPreset preset)
        {
            if (preset == null)
                throw new System.ArgumentNullException(nameof(preset));

            if (preset.includeWaves)
                ApplyWavesDomain(preset);
            if (preset.includeAppearance)
                ApplyAppearanceDomain(preset);
            if (preset.includeSurface)
                ApplySurfaceDomain(preset);
            if (preset.includeFoam)
                Overwrite(preset.foamSettings, foamSettings);
            if (preset.includeUnderwater)
                Overwrite(preset.underwaterSurfaceSettings, underwaterSurfaceSettings);
            if (preset.includeRipples)
                Overwrite(preset.rippleSettings, rippleSettings);
        }

        void ApplyWavesDomain(WaterLookPreset preset)
        {
            // Preserved: topology and budget stay the body's own (see the pairing rule above).
            bool keepOpenWater = ocean.openWater;
            bool keepUnboundedOcean = ocean.unboundedOcean;
            int keepClipmapGridResolution = ocean.clipmapGridResolution;
            int keepLargeGodRaySteps = ocean.largeGodRaySteps;

            Overwrite(preset.ocean, ocean);

            ocean.openWater = keepOpenWater;
            ocean.unboundedOcean = keepUnboundedOcean;
            ocean.clipmapGridResolution = keepClipmapGridResolution;
            ocean.largeGodRaySteps = keepLargeGodRaySteps;

            Overwrite(preset.windWaveSettings, windWaveSettings);
        }

        void ApplyAppearanceDomain(WaterLookPreset preset)
        {
            jerlovWaterType = preset.jerlovWaterType;
            Overwrite(preset.waterFogSettings, waterFogSettings);
            Overwrite(preset.volumeScatterSettings, volumeScatterSettings);
            Overwrite(preset.depthAttenuation, depthAttenuation);
            refractShadows = preset.refractShadows;
            refractShadowSoftness = preset.refractShadowSoftness;
        }

        void ApplySurfaceDomain(WaterLookPreset preset)
        {
            // Preserved: planar project wiring and render budgets are not look.
            LayerMask keepPlanarExcludeLayers = reflectionSettings.planarExcludeLayers;
            float keepPlanarResolutionScale = reflectionSettings.planarResolutionScale;
            int keepPlanarUpdateInterval = reflectionSettings.planarUpdateInterval;
            bool keepPlanarRenderShadows = reflectionSettings.planarRenderShadows;
            float keepPlanarFarClipDistance = reflectionSettings.planarFarClipDistance;
            Overwrite(preset.reflectionSettings, reflectionSettings);
            reflectionSettings.planarExcludeLayers = keepPlanarExcludeLayers;
            reflectionSettings.planarResolutionScale = keepPlanarResolutionScale;
            reflectionSettings.planarUpdateInterval = keepPlanarUpdateInterval;
            reflectionSettings.planarRenderShadows = keepPlanarRenderShadows;
            reflectionSettings.planarFarClipDistance = keepPlanarFarClipDistance;

            Overwrite(preset.detailNormalSettings, detailNormalSettings);
            foamPatternTexture = preset.foamPatternTexture;
            foamPatternGrid = preset.foamPatternGrid;
            foamPatternFps = preset.foamPatternFps;
            foamReliefStrength = preset.foamReliefStrength;
            oceanWhitecapTexture = preset.oceanWhitecapTexture;
            oceanWhitecapGrid = preset.oceanWhitecapGrid;
            oceanWhitecapFps = preset.oceanWhitecapFps;
        }

        // Deep field copy INTO the existing instance, never aliasing the preset's own object.
        static void Overwrite<T>(T source, T target) where T : class
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), target);
        }
    }
}
