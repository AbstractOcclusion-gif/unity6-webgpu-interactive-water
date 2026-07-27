// WebGpuWater - WaterVolume inspector: the BODY tab.
// What the body IS and WHERE it is: placement, the driven renderers, the chunk footprint, the bed
// terrain that forms its floor, the asset wiring, and the camera. Nothing here changes how the
// water looks or moves - those are the Surface / Volume / Motion tabs.
//
// The bed TERRAIN SOURCE lives here (not with the colours it feeds) because the bed is the body's
// floor, the same kind of fact as its extent. Its colour + clarity knobs are in Volume, its surf
// fronts in Motion, its bake resolution in Budget; each of those greys out until this toggle is on.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawPlacementSection()
        {
            _showPlacement = WaterEditorUI.Section("Placement", _showPlacement, () =>
            {
                EditorGUILayout.HelpBox(PlacementHelp, MessageType.Info);
                DrawFields("volumeExtent");
            });
        }

        void DrawBodySection()
        {
            _showBody = WaterEditorUI.Section("Water Body (multi-instance)", _showBody, () =>
            {
                DrawFields("isPrimary", "autoLinkReceivers");
                // The renderers are wired by the wizard / scene builder and then never touched.
                _showBodyAdvanced = WaterEditorUI.SubSection("Advanced", _showBodyAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Driven renderers");
                    DrawFields("surfaceAbove", "surfaceUnder", "poolRenderer", "godRayRenderer");
                });
            });
        }

        // The terrain that forms the floor. Only the SOURCE is here; everything derived from it is
        // drawn in the tab that owns the derivation, and each of those blocks greys on this toggle.
        void DrawBedSourceSection()
        {
            _showBedSource = WaterEditorUI.SectionWithToggle(
                "Bed Depth (terrain floor)", _showBedSource, Prop(WaterVolumePropertyPaths.UseBedDepth), () =>
            {
                DrawFields(WaterVolumePropertyPaths.BedTerrain);
                EditorGUILayout.HelpBox(BedSourceHelp, MessageType.None);
            });
        }

        void DrawWiringSection()
        {
            _showWiring = WaterEditorUI.Section("Wiring & References (scene builder)", _showWiring, () =>
            {
                EditorGUILayout.HelpBox(WiringHelp, MessageType.None);
                WaterEditorUI.SubHeading("Sun & light");
                DrawFields(WaterVolumePropertyPaths.Sun);
                // lightDir is auto-driven from the assigned sun every tick (WaterUniformPublisher),
                // so it is read-only while a sun drives it - editable only when no sun is set.
                DrawFieldsIf(!HasSun, "lightDir");
                WaterEditorUI.SubHeading("Assets");
                // NOTE: never list a path here without its serialized field on WaterVolume -
                // Prop() returns null for a missing path and PropertyField(null) throws the
                // moment the section unfolds ("sweCompute" lingered here after the SWE removal).
                DrawFields(
                    "simCompute", "oceanFftCompute", "causticsShader",
                    "largeBodyCausticsShader", "obstacleShader", "occluderShader", "waterMesh",
                    "targetCamera");
            });
        }

        void DrawCameraSection()
        {
            _showCamera = WaterEditorUI.Section("Camera", _showCamera, () =>
                DrawFields("orbit", "configureCamera"));
        }

        const string PlacementHelp =
            "Position and rotation come from this GameObject's Transform - move/rotate it to place " +
            "the water. Extent is the world half-size per pool unit (X width, Y depth, Z length).";
        const string WiringHelp =
            "Assigned by the scene builder / Water Wizard. Leave as-is unless you know a reference changed.";
        const string BedSourceHelp =
            "The terrain read as this body's floor. What the bed DRIVES is drawn where it belongs: " +
            "deep colour + clarity in Volume, surf fronts in Motion, bake resolution in Budget - each " +
            "greyed out until this is on.";
    }
}
#endif
