// WebGL Water - WaterVolume inspector: motion sections (simulation, interactive ripples,
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
                DrawFields("timeScale", "lightDir", "causticResolution"));
        }

        void DrawRippleSection()
        {
            _showRipple = WaterEditorUI.Section("Ripple Tuning", _showRipple, () =>
                DrawFields(
                    "rippleSettings.waveSpeed",
                    "rippleSettings.damping",
                    "rippleSettings.stepsPerFrame",
                    "rippleSettings.rippleStrength",
                    "rippleSettings.rippleRadius",
                    "rippleSettings.seedRipplesOnStart",
                    "rippleSettings.conserveVolume",
                    "rippleSettings.conserveMaxCorrection"));
        }

        void DrawWindWavesSection()
        {
            _showWindWaves = WaterEditorUI.SectionWithToggle(
                "Wind Waves (spectral)", _showWindWaves, Prop("windWaveSettings.windWaves"), () =>
                DrawFields(
                    "windWaveSettings.windSpeed",
                    "windWaveSettings.windFromDegrees",
                    "windWaveSettings.poolHalfExtentMeters",
                    "windWaveSettings.waveCount",
                    "windWaveSettings.waveAmplitudeScale",
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
