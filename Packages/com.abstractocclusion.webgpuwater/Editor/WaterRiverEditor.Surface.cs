// WebGpuWater - WaterRiver inspector: the SURFACE tab.
// The ribbon itself (sampling density, gameplay depth, materials, mesh readouts), the
// body-border aprons the generated seams carve into it, and the mouth plume the facade pushes
// into the receiving body. Surface fields are drawn through the surface's nested
// SerializedObject; the above material is the MeshRenderer's and is shown as a readout.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        void DrawSurfaceTab()
        {
            SerializedObject surface = SurfaceObject;
            if (surface == null)
            {
                EditorGUILayout.HelpBox(MissingSurfaceHelp, MessageType.Error);
                return;
            }

            surface.Update();
            _showRibbon = WaterEditorUI.Section("Ribbon", _showRibbon, () =>
            {
                DrawFields(surface, SurfaceSamplesPerSegmentPath, SurfaceGameplayDepthPath,
                           SurfaceUnderMaterialPath, SurfaceReflectionProbePath);
                DrawRibbonReadouts((WaterRiverSurface)surface.targetObject);
            });
            surface.ApplyModifiedProperties();

            _showSeams = WaterEditorUI.Section("Seams (body aprons)", _showSeams, () =>
            {
                EditorGUILayout.HelpBox(SeamsHelp, MessageType.None);
                DrawSeamReadout(WaterRiverEndKind.Source, "Source apron");
                DrawSeamReadout(WaterRiverEndKind.Mouth, "Mouth apron");
            });

            DrawMouthOutflowSection();
        }

        void DrawRibbonReadouts(WaterRiverSurface surface)
        {
            Renderer renderer = surface.SurfaceRenderer;
            Material above = renderer != null ? renderer.sharedMaterial : null;
            WaterEditorUI.Readout("Above material (MeshRenderer)",
                                  above != null ? above.name : NoneReadout);
            // vertexCount only: reading the index array copies it every repaint.
            Mesh mesh = surface.GeneratedMesh;
            WaterEditorUI.Readout("Generated mesh", mesh != null
                ? string.Format(MeshReadoutFormat, mesh.vertexCount) : NotBuiltReadout);
            if (surface.underSurfaceMaterial == null)
                EditorGUILayout.HelpBox(NoUnderMaterialHelp, MessageType.None);
        }

        // Seam length IS the end's transition radius (WaterRiver.BodySeamFor), authored in the
        // Connections tab beside the body it belongs to - so this is a readout, not a second knob.
        void DrawSeamReadout(WaterRiverEndKind endKind, string label)
        {
            WaterRiverEndConnection end = River.EndFor(endKind);
            if (!end.IsGenerated)
            {
                WaterEditorUI.Readout(label, NotConnectedReadout);
                return;
            }
            WaterEditorUI.Readout(label, end.body != null
                ? string.Format(ApronReadoutFormat, end.transitionRadiusMeters, end.body.name)
                : string.Format(StitchReadoutFormat, end.upstreamRiver.name));
        }

        // Greyed until the mouth is generated into a body: every knob here is consumed only by
        // the outflow the facade registers with that body.
        void DrawMouthOutflowSection()
        {
            _showMouthOutflow = WaterEditorUI.Section("Mouth Outflow", _showMouthOutflow, () =>
            {
                WaterRiverEndConnection mouth = River.MouthEnd;
                bool active = mouth.IsGenerated && mouth.body != null;
                if (!active) EditorGUILayout.HelpBox(MouthOutflowInactiveHelp, MessageType.None);
                DrawFieldsIf(active, MouthOutflowLengthPath, MouthOutflowSpreadPath,
                             MouthOutflowCurrentStrengthPath, MouthOutflowFoamLengthPath,
                             MouthOutflowFoamStrengthPath);
            });
        }

        const string MissingSurfaceHelp =
            "No River Surface on this object. Water River requires one - re-add the component.";
        const string SeamsHelp =
            "Where an end meets a body, the terminal band conforms to that body's footprint " +
            "border and fades the river's own waves across the transition radius set per end in " +
            "the Connections tab.";
        const string NoUnderMaterialHelp =
            "No underside material: a submerged camera looking up will not see this ribbon. " +
            "Assign the body's cull-front (Underwater) material to get the runtime underside twin.";
        const string MouthOutflowInactiveHelp =
            "Applies once the Mouth end is connected to a body (Connections tab).";
        const string MeshReadoutFormat = "{0} vertices";
        const string ApronReadoutFormat = "{0:0.##} m into {1}";
        const string StitchReadoutFormat = "stitched to {0}'s mouth";
        const string NotConnectedReadout = "not connected";
        const string NotBuiltReadout = "not built";
        const string NoneReadout = "none";
    }
}
#endif
