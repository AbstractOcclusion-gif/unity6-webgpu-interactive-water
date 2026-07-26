// WebGpuWater - WaterVolume inspector: motion sections (simulation, interactive ripples,
// ambient wind waves, floating-object interaction). Draws serialized properties by exact path.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawSimulationSection()
        {
            _showSimulation = WaterEditorUI.Section("Simulation", _showSimulation, () =>
            {
                DrawFields("timeScale");
                // lightDir is auto-driven from the assigned sun every tick (WaterUniformPublisher),
                // so it is read-only while a sun drives it - editable only when no sun is set.
                DrawFieldsIf(!HasSun, "lightDir");
                DrawFields("causticResolution");
            });
        }

        void DrawRippleSection()
        {
            _showRipple = WaterEditorUI.Section("Ripple Tuning", _showRipple, () =>
            {
                DrawFields(
                    "rippleSettings.waveSpeed",
                    "rippleSettings.damping",
                    "rippleSettings.stepsPerFrame",
                    "rippleSettings.rippleStrength",
                    "rippleSettings.rippleRadius",
                    "rippleSettings.rippleChoppiness",
                    "rippleSettings.seedRipplesOnStart");
                // Volume conservation is meaningless on an unbounded ocean (no finite volume to conserve).
                DrawFieldsIf(Bounded,
                    "rippleSettings.conserveVolume",
                    "rippleSettings.conserveMaxCorrection");
            });
        }

        void DrawWindWavesSection()
        {
            _showWindWaves = WaterEditorUI.SectionWithToggle(
                "Wind Waves (spectral)", _showWindWaves, Prop("windWaveSettings.windWaves"), () =>
                DrawFields(
                    WaterVolumePropertyPaths.WindSpeed,
                    "windWaveSettings.windFromDegrees",
                    WaterVolumePropertyPaths.WaveScaleMeters,
                    "windWaveSettings.waveCount",
                    WaterVolumePropertyPaths.WaveAmplitudeScale,
                    "windWaveSettings.waveDirectionSpread",
                    "windWaveSettings.waveNormalStrength"));
        }

        void DrawObjectInteractionSection()
        {
            _showObjectInteraction = WaterEditorUI.Section("Object Interaction", _showObjectInteraction, () =>
                DrawFields(
                    "objectInteractionSettings.objectInteraction",
                    "objectInteractionSettings.obstacleStrength",
                    "objectInteractionSettings.obstacleDeadband",
                    "objectInteractionSettings.obstacleSmoothing",
                    "objectInteractionSettings.obstacleFlipY"));
        }
    }
}
#endif
