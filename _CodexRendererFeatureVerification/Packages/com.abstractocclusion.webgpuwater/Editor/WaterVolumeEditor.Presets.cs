// WebGpuWater - WaterVolume inspector: look presets (capture / apply / save-as-new).
//
// A WaterLookPreset stores the body's LOOK domains as the same nested Settings classes the
// volume serializes; WaterLookPresetSync does the generic copying. Apply honours the preset's
// per-domain include flags and preserves topology/budget/project fields, and its writes ride
// this inspector's serializedObject - committed (undoably) by OnInspectorGUI exactly like the
// Jerlov apply. Capture/save write the ASSET, committed here for the same undo behaviour.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        const string PresetSaveTitle = "Save Water Look Preset";
        const string PresetSaveDefaultName = "WaterLookPreset";
        const string PresetSaveMessage = "Where to save the captured look";
        const string PresetSaveExtension = "asset";

        bool _showPresets = false;
        WaterLookPreset _presetSlot; // editor-side slot only - the volume stores no preset link

        void DrawPresetSection()
        {
            _showPresets = WaterEditorUI.Section("Look Presets", _showPresets, DrawPresetControls);
        }

        void DrawPresetControls()
        {
            EditorGUILayout.HelpBox("A preset moves the LOOK only (waves, colour, surface, foam, underwater, " +
                                    "ripples - per its include flags). Wiring, size, shore and quality never move.",
                                    MessageType.None);
            _presetSlot = (WaterLookPreset)EditorGUILayout.ObjectField(
                new GUIContent("Preset", "The WaterLookPreset asset to apply from or capture into."),
                _presetSlot, typeof(WaterLookPreset), allowSceneObjects: false);

            using (new EditorGUI.DisabledScope(_presetSlot == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Apply Preset",
                        "Write the preset's included domains into this body. Undoable.")))
                    ApplyPreset();
                if (GUILayout.Button(new GUIContent("Capture Current Look",
                        "Overwrite the preset asset with this body's full look. Undoable.")))
                    CapturePreset();
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button(new GUIContent("Save Current Look As New Preset...",
                    "Create a new WaterLookPreset asset from this body's full look.")))
                SaveNewPreset();
        }

        // Preset -> volume, included domains only. The writes join serializedObject and are
        // committed by OnInspectorGUI.
        void ApplyPreset()
        {
            var presetSerialized = new SerializedObject(_presetSlot);
            int applied = WaterLookPresetSync.ApplyIncluded(presetSerialized, serializedObject, _presetSlot);
            Debug.Log("[WebGpuWater] Applied " + applied + " look domain(s) from '" + _presetSlot.name + "'.", target);
        }

        // Volume -> preset, ALL domains (include flags gate apply, not capture). Committed here
        // because the preset asset is not this inspector's serializedObject.
        void CapturePreset()
        {
            var presetSerialized = new SerializedObject(_presetSlot);
            WaterLookPresetSync.CaptureAll(serializedObject, presetSerialized);
            presetSerialized.ApplyModifiedProperties();
            Debug.Log("[WebGpuWater] Captured the current look into '" + _presetSlot.name + "'.", _presetSlot);
        }

        void SaveNewPreset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                PresetSaveTitle, PresetSaveDefaultName, PresetSaveExtension, PresetSaveMessage);
            if (string.IsNullOrEmpty(path))
                return; // user cancelled - nothing created

            var preset = ScriptableObject.CreateInstance<WaterLookPreset>();
            AssetDatabase.CreateAsset(preset, path);
            var presetSerialized = new SerializedObject(preset);
            WaterLookPresetSync.CaptureAll(serializedObject, presetSerialized);
            presetSerialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            _presetSlot = preset;
            EditorGUIUtility.PingObject(preset);
        }
    }
}
#endif
