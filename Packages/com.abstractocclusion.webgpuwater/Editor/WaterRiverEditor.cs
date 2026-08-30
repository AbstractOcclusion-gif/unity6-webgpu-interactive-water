// WebGpuWater - inspector for the WaterRiver facade: component status, per-end connections
// with Generate/Update/Remove, and the optional fluid/foam add-ons.
//
// All object creation/destruction lives HERE (with Undo), never in the facade's OnValidate -
// Unity forbids lifecycle operations there. The facade owns the math and the references; this
// editor owns the user gesture.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiver))]
    internal sealed class WaterRiverEditor : UnityEditor.Editor
    {
        const string GenerateUndoLabel = "Generate River Connection";
        const string RemoveUndoLabel = "Remove River Connection";
        const string MouthOutflowLengthPropertyName = "mouthOutflowLengthMeters";
        const string MouthOutflowSpreadPropertyName = "mouthOutflowSpreadPerMeter";
        const string MouthOutflowCurrentStrengthPropertyName = "mouthOutflowCurrentStrength";
        const string MouthOutflowFoamLengthPropertyName = "mouthOutflowFoamLengthMeters";
        const string MouthOutflowFoamStrengthPropertyName = "mouthOutflowFoamStrength";
        const string MouthOutflowSectionTitle = "Mouth Outflow";

        SerializedProperty _parentVolume;
        SerializedProperty _sourceEnd;
        SerializedProperty _mouthEnd;
        SerializedProperty _mouthOutflowLength;
        SerializedProperty _mouthOutflowSpread;
        SerializedProperty _mouthOutflowCurrentStrength;
        SerializedProperty _mouthOutflowFoamLength;
        SerializedProperty _mouthOutflowFoamStrength;

        void OnEnable()
        {
            _parentVolume = serializedObject.FindProperty("parentVolume");
            _sourceEnd = serializedObject.FindProperty("sourceEnd");
            _mouthEnd = serializedObject.FindProperty("mouthEnd");
            _mouthOutflowLength = serializedObject.FindProperty(MouthOutflowLengthPropertyName);
            _mouthOutflowSpread = serializedObject.FindProperty(MouthOutflowSpreadPropertyName);
            _mouthOutflowCurrentStrength = serializedObject.FindProperty(
                MouthOutflowCurrentStrengthPropertyName);
            _mouthOutflowFoamLength = serializedObject.FindProperty(
                MouthOutflowFoamLengthPropertyName);
            _mouthOutflowFoamStrength = serializedObject.FindProperty(
                MouthOutflowFoamStrengthPropertyName);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var river = (WaterRiver)target;

            EditorGUILayout.PropertyField(_parentVolume);
            if (_parentVolume.objectReferenceValue == null)
                EditorGUILayout.HelpBox(
                    "Standalone ribbon: gameplay queries work, but there are no animated waves " +
                    "and no underwater fog until a parent volume is assigned.", MessageType.Info);

            DrawOptionalComponents(river);
            DrawMouthOutflow();
            DrawEnd(river, WaterRiverEndKind.Source, _sourceEnd, "Source End (first knot)");
            DrawEnd(river, WaterRiverEndKind.Mouth, _mouthEnd, "Mouth End (last knot)");

            serializedObject.ApplyModifiedProperties();
        }

        void DrawOptionalComponents(WaterRiver river)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Optional Components", EditorStyles.boldLabel);
            var fluid = river.GetComponent<WaterRiverFluid>();
            var foam = river.GetComponent<WaterRiverFoam>();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(fluid != null))
                    if (GUILayout.Button("Add Fluid Bake"))
                        Undo.AddComponent<WaterRiverFluid>(river.gameObject);
                // Foam requires the fluid component, but its contact and cascade sources work
                // before that component has a completed turbulence bake.
                using (new EditorGUI.DisabledScope(foam != null || fluid == null))
                    if (GUILayout.Button("Add Foam"))
                        Undo.AddComponent<WaterRiverFoam>(river.gameObject);
            }
            if (fluid == null)
                EditorGUILayout.HelpBox("No fluid component: the current field uses the uniform " +
                                        "spline speed. Add Fluid Bake before adding river foam; " +
                                        "only obstacle-deflected turbulence requires an actual bake.",
                                        MessageType.None);
        }

        void DrawMouthOutflow()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(MouthOutflowSectionTitle, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_mouthOutflowLength);
            EditorGUILayout.PropertyField(_mouthOutflowSpread);
            EditorGUILayout.PropertyField(_mouthOutflowCurrentStrength);
            EditorGUILayout.PropertyField(_mouthOutflowFoamLength);
            EditorGUILayout.PropertyField(_mouthOutflowFoamStrength);
        }

        void DrawEnd(WaterRiver river, WaterRiverEndKind endKind, SerializedProperty endProperty,
                     string title)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(endProperty.FindPropertyRelative("body"));
            if (endKind == WaterRiverEndKind.Source)
                EditorGUILayout.PropertyField(endProperty.FindPropertyRelative("upstreamRiver"));
            EditorGUILayout.PropertyField(endProperty.FindPropertyRelative("transitionRadiusMeters"));

            WaterRiverEndConnection end = river.EndFor(endKind);
            if (end.IsGenerated)
                EditorGUILayout.HelpBox($"Connected via '{end.connection.name}' " +
                                        $"(port id {end.riverPort.PortId}).", MessageType.None);
            else if (end.IsPartiallyGenerated)
                EditorGUILayout.HelpBox("Generated connection is incomplete (objects deleted by " +
                                        "hand). Regenerate or remove.", MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool hasBody = endProperty.FindPropertyRelative("body").objectReferenceValue != null;
                bool hasRiver = endKind == WaterRiverEndKind.Source &&
                    endProperty.FindPropertyRelative("upstreamRiver").objectReferenceValue != null;
                using (new EditorGUI.DisabledScope(hasBody == hasRiver))
                {
                    string label = end.IsGenerated ? "Update Connection" : "Generate Connection";
                    if (GUILayout.Button(label))
                    {
                        // Apply pending inspector edits (body/radius) before the facade reads them.
                        serializedObject.ApplyModifiedProperties();
                        GenerateEnd(river, endKind);
                    }
                }
                using (new EditorGUI.DisabledScope(!end.IsGenerated && !end.IsPartiallyGenerated))
                    if (GUILayout.Button("Remove Connection"))
                        RemoveEnd(river, endKind);
            }
        }

        internal static void GenerateEnd(WaterRiver river, WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = river.EndFor(endKind);
            WaterConnectionPort priorRiverPort = end.riverPort;
            WaterConnectionPort priorTargetPort = end.targetPort;
            WaterConnection priorConnection = end.connection;

            Undo.RecordObject(river, GenerateUndoLabel);
            RecordIfAlive(priorRiverPort, GenerateUndoLabel);
            RecordIfAlive(priorTargetPort, GenerateUndoLabel);
            RecordIfAlive(priorConnection, GenerateUndoLabel);

            try
            {
                river.RegenerateConnection(endKind);
            }
            catch (System.InvalidOperationException exception)
            {
                Debug.LogError($"WaterRiver: {exception.Message}", river);
                return;
            }

            // Only what regeneration actually CREATED gets a created-undo; reused objects were
            // recorded above so their rewiring undoes in the same step.
            RegisterIfCreated(end.riverPort, priorRiverPort);
            RegisterIfCreated(end.targetPort, priorTargetPort);
            RegisterIfCreated(end.connection, priorConnection);
            EditorUtility.SetDirty(river);
        }

        internal static void RemoveEnd(WaterRiver river, WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = river.EndFor(endKind);
            Undo.RecordObject(river, RemoveUndoLabel);
            DestroyIfAlive(end.connection);
            DestroyIfAlive(end.targetPort);
            DestroyIfAlive(end.riverPort);
            river.ClearConnectionRefs(endKind);
            EditorUtility.SetDirty(river);
        }

        static void RecordIfAlive(Component component, string undoLabel)
        {
            if (component == null) return;
            Undo.RecordObject(component, undoLabel);
            Undo.RecordObject(component.transform, undoLabel);
        }

        static void RegisterIfCreated(Component current, Component prior)
        {
            if (current == null || current == prior) return;
            Undo.RegisterCreatedObjectUndo(current.gameObject, GenerateUndoLabel);
        }

        static void DestroyIfAlive(Component component)
        {
            if (component == null) return;
            Undo.DestroyObjectImmediate(component.gameObject);
        }
    }
}
