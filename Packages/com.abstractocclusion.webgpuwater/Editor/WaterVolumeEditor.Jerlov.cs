// WebGpuWater - WaterVolume inspector: Jerlov physical water-colour preset.
// A water-type dropdown + "Apply" button that writes the validated per-channel absorption into
// Fog Extinction (at density 1) and the single-scattering-albedo body colour into the Scatter / Fog
// colour. Mirrors the body-type "Apply defaults" pattern: explicit, button-driven, and fully
// undoable (values are set through SerializedProperties, committed by OnInspectorGUI). Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Applying a preset sets brightness together with colour. The body colour already carries the
        // physical cross-type magnitude, so a unit intensity reads correctly; the old default of 2
        // over-brightened the derived colours (and a stored 0 would have rendered the body black).
        const float JerlovScatterIntensity = 1f;

        void DrawJerlovWaterTypeSelector()
        {
            DrawFields("jerlovWaterType");
            var type = (JerlovWaterType)Prop("jerlovWaterType").enumValueIndex;
            if (GUILayout.Button("Apply " + JerlovWaterTypes.Get(type).DisplayName + " water colour"))
                ApplyJerlovWaterType(type);
        }

        // Writes the preset into the existing appearance fields. Reversible via Undo; enables Water Fog
        // so the transmission tint (the part that removes the old constant cyan) is visible immediately.
        void ApplyJerlovWaterType(JerlovWaterType type)
        {
            JerlovPreset preset = JerlovWaterTypes.Get(type);

            Prop(WaterVolumePropertyPaths.FogExtinction).colorValue = preset.Extinction;
            Prop(WaterVolumePropertyPaths.FogDensity).floatValue = JerlovWaterTypes.PhysicalDensity;
            Prop(WaterVolumePropertyPaths.FogColor).colorValue = preset.BodyColor;
            Prop(WaterVolumePropertyPaths.WaterFog).boolValue = true;

            Prop(WaterVolumePropertyPaths.ScatterColor).colorValue = preset.BodyColor;
            Prop(WaterVolumePropertyPaths.ScatterIntensity).floatValue = JerlovScatterIntensity;
        }
    }
}
#endif
