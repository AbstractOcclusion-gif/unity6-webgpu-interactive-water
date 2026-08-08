// WebGpuWater - WaterLookPreset custom inspector.
//
// The default inspector drew every mirrored Settings block fully expanded - a wall of ~190
// fields. This one shows the preset as the user thinks of it: the notes, then one row per look
// domain with its include toggle (what Apply writes) and a foldout for the stored values. The
// domain list comes from WaterLookPresetSync.Domains - the ONE table - so a new domain shows
// up here automatically.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterLookPreset))]
    public sealed class WaterLookPresetEditor : UnityEditor.Editor
    {
        const string NotesPath = "notes";

        bool[] _expanded;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(serializedObject.FindProperty(NotesPath));
            EditorGUILayout.HelpBox("Capture and apply happen on a WaterVolume - select a body and use its " +
                                    "Look Presets section. Ticked domains are the ones Apply writes.",
                                    MessageType.None);

            WaterLookPresetSync.LookDomain[] domains = WaterLookPresetSync.Domains;
            if (_expanded == null || _expanded.Length != domains.Length)
                _expanded = new bool[domains.Length];

            for (int i = 0; i < domains.Length; i++)
                DrawDomain(domains[i], ref _expanded[i]);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawDomain(WaterLookPresetSync.LookDomain domain, ref bool expanded)
        {
            EditorGUILayout.Space(4f);
            SerializedProperty include = serializedObject.FindProperty(domain.IncludeFlagPath);
            include.boolValue = EditorGUILayout.ToggleLeft(domain.DisplayName, include.boolValue,
                                                           EditorStyles.boldLabel);

            EditorGUI.indentLevel++;
            expanded = EditorGUILayout.Foldout(expanded, "Stored values", toggleOnLabelClick: true);
            if (expanded)
            {
                using (new EditorGUI.DisabledScope(!include.boolValue))
                {
                    foreach (string path in domain.Paths)
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(path), includeChildren: true);
                }
            }
            EditorGUI.indentLevel--;
        }
    }
}
#endif
