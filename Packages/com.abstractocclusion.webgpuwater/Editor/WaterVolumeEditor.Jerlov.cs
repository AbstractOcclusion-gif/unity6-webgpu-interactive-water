// WebGpuWater - WaterVolume inspector: Jerlov physical water-colour preset.
// A water-type dropdown + "Apply" button. The actual writes live in the shared
// WaterJerlovLookWriter (the wizard's default ocean applies the same preset - one copy of the
// tuned numbers, no drift). Mirrors the body-type "Apply defaults" pattern: explicit,
// button-driven, and fully undoable (SerializedProperty writes committed by OnInspectorGUI).
// Editor-only.
#if UNITY_EDITOR
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawJerlovWaterTypeSelector()
        {
            DrawFields(WaterVolumePropertyPaths.JerlovWaterType);
            var type = (JerlovWaterType)Prop(WaterVolumePropertyPaths.JerlovWaterType).enumValueIndex;
            if (GUILayout.Button("Apply " + JerlovWaterTypes.Get(type).DisplayName + " water colour"))
                WaterJerlovLookWriter.Write(serializedObject, type);
        }
    }
}
#endif
