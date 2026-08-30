// WebGpuWater - source and appearance controls for river foam.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverFoam))]
    [CanEditMultipleObjects]
    internal sealed class WaterRiverFoamEditor : UnityEditor.Editor
    {
        const string StrengthPropertyName = "strength";
        const string OverallStrengthPropertyName = "overallStrength";
        const string ContactStrengthPropertyName = "contactStrength";
        const string ContactDepthPropertyName = "contactDepth";
        const string CascadeStrengthPropertyName = "cascadeStrength";
        const string CascadeStartAnglePropertyName = "cascadeStartAngle";
        const string CascadeFullAnglePropertyName = "cascadeFullAngle";
        const string CascadePersistencePropertyName = "cascadePersistenceMeters";
        const string PatternSizePropertyName = "patternSize";
        const string EdgeFeatherPropertyName = "edgeFeather";
        const string CoreCutPropertyName = "coreCut";
        const string InspectorHelp =
            "Combines depth-contact foam, slope-driven whitewater and baked fluid turbulence. " +
            "The assigned Water Volume supplies the established foam texture and colour.";
        const string MissingFluidWarning =
            "River Foam requires a sibling River Fluid component.";
        const string MissingBakeWarning =
            "River Fluid has no valid bake. Contact and whitewater remain active, but obstacle " +
            "wakes and turbulence require Bake Settled Fluid.";
        const string MissingVolumeWarning =
            "Assign a Water Volume on River Surface so foam receives the established water look " +
            "and participates in the after-fog overlay.";
        const string BakeStatusFormat = "Using fluid bake {0} x {1}, river length {2:0.##} m";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(InspectorHelp, MessageType.None);
            WaterEditorUI.SubHeading("Sources");
            DrawProperty(OverallStrengthPropertyName);
            DrawProperty(ContactStrengthPropertyName);
            DrawProperty(ContactDepthPropertyName);
            DrawProperty(CascadeStrengthPropertyName);
            DrawProperty(CascadeStartAnglePropertyName);
            DrawProperty(CascadeFullAnglePropertyName);
            DrawProperty(CascadePersistencePropertyName);
            DrawProperty(StrengthPropertyName, "Baked Turbulence Strength");
            WaterEditorUI.SubHeading("Appearance");
            DrawProperty(PatternSizePropertyName);
            DrawProperty(EdgeFeatherPropertyName);
            DrawProperty(CoreCutPropertyName);
            serializedObject.ApplyModifiedProperties();
            DrawWarningsAndStatus();
        }

        void DrawProperty(string propertyName)
            => EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName));

        void DrawProperty(string propertyName, string label)
            => EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName),
                                             new GUIContent(label));

        void DrawWarningsAndStatus()
        {
            if (targets.Length != 1) return;
            var foam = (WaterRiverFoam)target;
            WaterRiverSurface surface = foam.GetComponent<WaterRiverSurface>();
            WaterRiverFluid fluid = foam.GetComponent<WaterRiverFluid>();
            if (fluid == null)
                EditorGUILayout.HelpBox(MissingFluidWarning, MessageType.Warning);
            else if (fluid.BakeData == null || !fluid.BakeData.IsValid)
                EditorGUILayout.HelpBox(MissingBakeWarning, MessageType.Info);
            if (surface == null || surface.WaterVolume == null)
                EditorGUILayout.HelpBox(MissingVolumeWarning, MessageType.Warning);
            if (fluid?.BakeData == null || !fluid.BakeData.IsValid) return;
            EditorGUILayout.LabelField(
                string.Format(BakeStatusFormat,
                              fluid.BakeData.LateralResolution,
                              fluid.BakeData.LongitudinalResolution,
                              fluid.BakeData.RiverLength),
                EditorStyles.miniLabel);
        }
    }
}
#endif
