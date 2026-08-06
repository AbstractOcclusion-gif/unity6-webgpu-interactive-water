// WebGpuWater - inspector for the showcase controller: Previous / Next buttons that work in BOTH
// edit and play mode. Edit-mode preview clones carry HideFlags.DontSave (set by the controller),
// so previewing stations can never bake a duplicate station into the saved scene.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterShowcaseController))]
    internal sealed class WaterShowcaseControllerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var controller = (WaterShowcaseController)target;

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous")) controller.Cycle(reverse: true);
            if (GUILayout.Button("Next")) controller.Cycle(reverse: false);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                $"Station {controller.CurrentIndex + 1} / {controller.StationCount}",
                EditorStyles.miniLabel);
        }
    }
}
