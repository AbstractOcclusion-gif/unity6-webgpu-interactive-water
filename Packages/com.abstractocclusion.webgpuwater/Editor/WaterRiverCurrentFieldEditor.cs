// WebGpuWater - focused authoring guidance for spline-backed physical current.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverCurrentField))]
    internal sealed class WaterRiverCurrentFieldEditor : UnityEditor.Editor
    {
        const string SplinePropertyName = "spline";
        const string FluidPropertyName = "fluid";
        const string InspectorHelp =
            "The nearest spline tangent supplies full 3D flow direction, including waterfalls. " +
            "A valid River Fluid bake replaces uniform knot Speed with the same obstacle-deflected " +
            "velocity used by visible waves. Add this field to the Water Volume's Motion > " +
            "Currents list to include it in water queries.";
        const string MissingSplineWarning =
            "Assign a River Spline before this current field can return velocity.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(InspectorHelp, MessageType.None);
            SerializedProperty splineProperty = serializedObject.FindProperty(SplinePropertyName);
            EditorGUILayout.PropertyField(splineProperty);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(FluidPropertyName));
            if (splineProperty.objectReferenceValue == null)
                EditorGUILayout.HelpBox(MissingSplineWarning, MessageType.Warning);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
