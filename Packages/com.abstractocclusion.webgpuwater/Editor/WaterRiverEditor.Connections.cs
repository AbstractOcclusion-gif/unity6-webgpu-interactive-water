// WebGpuWater - WaterRiver inspector: the CONNECTIONS tab.
// The two ends. The body (or upstream river) is the ONLY authored input: assigning it
// generates the seam objects in the same Undo step, clearing it removes them, and the facade
// keeps them aimed afterwards (spline events + the change router). The buttons that remain are
// repair verbs. Every authoring warning about an end is drawn here, next to the field that
// fixes it - the facade's console warnings are the play-mode copy of the same checks.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        void DrawConnectionsTab()
        {
            _showSourceEnd = WaterEditorUI.Section("Source End (first knot)", _showSourceEnd, () =>
                DrawEnd(WaterRiverEndKind.Source, Prop(SourceEndPath)));
            _showMouthEnd = WaterEditorUI.Section("Mouth End (last knot)", _showMouthEnd, () =>
                DrawEnd(WaterRiverEndKind.Mouth, Prop(MouthEndPath)));
            _showGenerated = WaterEditorUI.Section("Generated Objects", _showGenerated,
                                                   DrawGeneratedObjectsSection);
        }

        void DrawEnd(WaterRiverEndKind endKind, SerializedProperty endProperty)
        {
            bool isSource = endKind == WaterRiverEndKind.Source;
            EditorGUILayout.HelpBox(isSource ? SourceEndHelp : MouthEndHelp, MessageType.None);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(endProperty.FindPropertyRelative(EndBodyPath));
            if (isSource)
                EditorGUILayout.PropertyField(endProperty.FindPropertyRelative(EndUpstreamRiverPath));
            bool targetChanged = EditorGUI.EndChangeCheck();
            // Radius edits need no gesture: the facade's OnValidate re-aims the generated seam.
            EditorGUILayout.PropertyField(endProperty.FindPropertyRelative(EndTransitionRadiusPath));
            if (targetChanged) SyncEndToTarget(endKind);

            WaterRiverEndConnection end = River.EndFor(endKind);
            DrawEndStatus(end);
            DrawEndWarnings(endKind, end);
            DrawEndButtons(endKind, end);
        }

        // Assignment IS the connect gesture. One Undo step covers the field edit and everything
        // generation touched, so a single Ctrl+Z returns to the unassigned state.
        void SyncEndToTarget(WaterRiverEndKind endKind)
        {
            int undoGroup = Undo.GetCurrentGroup();
            serializedObject.ApplyModifiedProperties();
            WaterRiverEndConnection end = River.EndFor(endKind);
            if (end.WantsConnection && !end.HasAmbiguousTarget)
                GenerateEnd(River, endKind);
            else if (!end.WantsConnection && (end.IsGenerated || end.IsPartiallyGenerated))
                RemoveEnd(River, endKind);
            Undo.CollapseUndoOperations(undoGroup);
            serializedObject.Update();
        }

        static void DrawEndStatus(WaterRiverEndConnection end)
        {
            if (!end.IsGenerated) return;
            WaterEditorUI.Readout("Connected via", string.Format(
                ConnectedFormat, end.connection.name, end.riverPort.PortId));
        }

        void DrawEndWarnings(WaterRiverEndKind endKind, WaterRiverEndConnection end)
        {
            if (end.HasAmbiguousTarget)
                EditorGUILayout.HelpBox(AmbiguousTargetWarning, MessageType.Warning);
            else if (end.IsPartiallyGenerated)
                EditorGUILayout.HelpBox(PartiallyGeneratedWarning, MessageType.Warning);
            else if (end.WantsConnection && !end.IsGenerated)
                EditorGUILayout.HelpBox(NotGeneratedWarning, MessageType.Warning);

            if (end.IsGenerated &&
                (WaterRuntimeValidation.ValidateConnection(end.connection) &
                 WaterRuntimeValidationFlags.UnwiredConnection) != 0)
                EditorGUILayout.HelpBox(UnwiredConnectionWarning, MessageType.Warning);

            if (end.body != null && !end.body.WaterFog)
                EditorGUILayout.HelpBox(string.Format(BodyFogOffFormat, end.body.name),
                                        MessageType.Info);

            if (River.IsEndKnotDeepInsideBody(endKind, out float insetMeters))
                EditorGUILayout.HelpBox(
                    string.Format(KnotInsetFormat, insetMeters, end.body.name),
                    MessageType.Warning);

            if (end.upstreamRiver != null && end.body == null &&
                end.upstreamRiver.ParentVolume != River.ParentVolume)
                EditorGUILayout.HelpBox(StitchParentMismatchWarning, MessageType.Warning);
        }

        void DrawEndButtons(WaterRiverEndKind endKind, WaterRiverEndConnection end)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!end.WantsConnection || end.HasAmbiguousTarget))
                    if (GUILayout.Button(RegenerateConnectionLabel))
                        GenerateEnd(River, endKind);
                using (new EditorGUI.DisabledScope(!end.IsGenerated && !end.IsPartiallyGenerated))
                    if (GUILayout.Button(RemoveConnectionLabel))
                        RemoveEnd(River, endKind);
            }
        }

        // ---- generated objects ---------------------------------------------------------------

        void DrawGeneratedObjectsSection()
        {
            EditorGUILayout.HelpBox(GeneratedObjectsHelp, MessageType.None);
            EditorGUI.BeginChangeCheck();
            SerializedProperty show = Prop(ShowGeneratedObjectsPath);
            EditorGUILayout.PropertyField(show, ShowGeneratedObjectsLabel);
            if (EditorGUI.EndChangeCheck()) ApplyGeneratedObjectVisibility(show.boolValue);

            DrawGeneratedObjectRows("Source", River.SourceEnd);
            DrawGeneratedObjectRows("Mouth", River.MouthEnd);
        }

        void ApplyGeneratedObjectVisibility(bool visible)
        {
            serializedObject.ApplyModifiedProperties();
            Undo.RecordObject(River, ShowGeneratedObjectsUndoLabel);
            RecordGeneratedObjects(River.SourceEnd);
            RecordGeneratedObjects(River.MouthEnd);
            River.SetGeneratedObjectsVisible(visible);
            EditorUtility.SetDirty(River);
            EditorApplication.RepaintHierarchyWindow();
            serializedObject.Update();
        }

        static void RecordGeneratedObjects(WaterRiverEndConnection end)
        {
            RecordIfAlive(end.riverPort, ShowGeneratedObjectsUndoLabel);
            RecordIfAlive(end.targetPort, ShowGeneratedObjectsUndoLabel);
            RecordIfAlive(end.connection, ShowGeneratedObjectsUndoLabel);
        }

        static void DrawGeneratedObjectRows(string endLabel, WaterRiverEndConnection end)
        {
            if (!end.IsGenerated && !end.IsPartiallyGenerated) return;
            WaterEditorUI.SubHeading(endLabel);
            WaterEditorUI.ComponentRow(RiverPortLabel, end.riverPort);
            WaterEditorUI.ComponentRow(TargetPortLabel, end.targetPort);
            WaterEditorUI.ComponentRow(ConnectionLabel, end.connection);
        }

        static readonly GUIContent ShowGeneratedObjectsLabel = new GUIContent(
            "Show generated objects",
            "Reveal the generated port and connection children in the Hierarchy. They are " +
            "derived from the spline and the end bodies and are hidden by default.");

        const string ShowGeneratedObjectsUndoLabel = "Show River Generated Objects";
        const string RegenerateConnectionLabel = "Regenerate Connection";
        const string RemoveConnectionLabel = "Remove Connection";
        const string RiverPortLabel = "River port";
        const string TargetPortLabel = "Target port";
        const string ConnectionLabel = "Connection";
        const string SourceEndHelp =
            "Assign the body that FEEDS this river (or, for a river-to-river stitch, the " +
            "upstream river whose mouth this source continues). Assigning connects it; the " +
            "seam then follows knot and body edits by itself.";
        const string MouthEndHelp =
            "Assign the body this river EMPTIES into. Assigning connects it and registers the " +
            "mouth outflow with that body; the seam then follows knot and body edits by itself.";
        const string GeneratedObjectsHelp =
            "Each connected end owns two Water Connection Ports and one Water Connection as " +
            "hidden children. Their port ids are persistent (saves and streaming key on them) " +
            "and survive regeneration.";
        const string ConnectedFormat = "'{0}' (port id {1})";
        const string AmbiguousTargetWarning =
            "Both a body and an upstream river are assigned. Clear one - an end has exactly one " +
            "target.";
        const string PartiallyGeneratedWarning =
            "The generated connection is incomplete (objects were deleted by hand). Regenerate " +
            "or remove it.";
        const string NotGeneratedWarning =
            "A target is assigned but no connection exists - generation failed (see the " +
            "Console). Fix the cause and press Regenerate Connection.";
        const string UnwiredConnectionWarning =
            "The generated connection lost a port reference and is inert. Regenerate it.";
        const string BodyFogOffFormat =
            "'{0}' has Water Fog off, so it cannot drive underwater fog for a camera in this " +
            "river or hand fog across the seam.";
        const string KnotInsetFormat =
            "The terminal knot sits {0:0.0} m inside '{1}'s footprint. Seams connect at the " +
            "footprint BORDER - move the knot to the bank line (a link that cannot meet a " +
            "border should be a waterfall).";
        const string StitchParentMismatchWarning =
            "Stitched rivers must share the same parent volume so their shared row receives " +
            "identical wave uniforms.";
    }
}
#endif
