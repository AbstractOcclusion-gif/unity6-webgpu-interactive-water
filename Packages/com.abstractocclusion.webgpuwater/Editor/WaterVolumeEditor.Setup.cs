// WebGL Water - WaterVolume inspector: setup + wiring sections (placement, look, body, scene
// wiring, performance, camera, splash). Draws serialized properties by exact path. Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawPlacementSection()
        {
            _showPlacement = WaterEditorUI.Section("Water Volume (placement)", _showPlacement, () =>
            {
                EditorGUILayout.HelpBox(PlacementHelp, MessageType.Info);
                DrawFields("volumeExtent");
            });
        }

        void DrawLookSection()
        {
            _showLook = WaterEditorUI.Section("Look / Surfaces", _showLook, () =>
            {
                DrawFieldsIf(Bounded, "tiles"); // pool tile albedo - bounded bodies only
                DrawFields("sky");
            });
        }

        void DrawBodySection()
        {
            _showBody = WaterEditorUI.Section("Water Body (multi-instance)", _showBody, () =>
            {
                WaterEditorUI.SubHeading("Driven renderers");
                DrawFields("surfaceAbove", "surfaceUnder", "poolRenderer", "godRayRenderer");
                WaterEditorUI.SubHeading("Body role");
                DrawFields("isPrimary", "autoLinkReceivers");
            });
        }

        void DrawWiringSection()
        {
            _showWiring = WaterEditorUI.Section("Wiring & References (scene builder)", _showWiring, () =>
            {
                EditorGUILayout.HelpBox(WiringHelp, MessageType.None);
                // NOTE: never list a path here without its serialized field on WaterVolume -
                // Prop() returns null for a missing path and PropertyField(null) throws the
                // moment the section unfolds ("sweCompute" lingered here after the SWE removal).
                DrawFields(
                    "simCompute", "oceanFftCompute", "causticsShader",
                    "largeBodyCausticsShader", "obstacleShader", "waterMesh",
                    "targetCamera", "sun");
            });
        }

        void DrawPerformanceSection()
        {
            _showPerformance = WaterEditorUI.Section("Performance", _showPerformance, () =>
            {
                DrawFields("quality", "rippleQuality", "enableCulling");
                // Activation distance only bites when culling is on; grey it out otherwise.
                EditorGUI.BeginDisabledGroup(!Prop("enableCulling").boolValue);
                DrawFields("activationDistance");
                EditorGUI.EndDisabledGroup();
            });
        }

        void DrawCameraSection()
        {
            _showCamera = WaterEditorUI.Section("Camera", _showCamera, () =>
                DrawFields("orbit", "configureCamera"));
        }

        void DrawSplashSection()
        {
            _showSplash = WaterEditorUI.Section("Splash", _showSplash, () =>
                DrawFields("splashEmitter"));
        }

        const string PlacementHelp =
            "Position and rotation come from this GameObject's Transform - move/rotate it to place " +
            "the water. Extent is the world half-size per pool unit (X width, Y depth, Z length).";
        const string WiringHelp =
            "Assigned by the scene builder / Water Wizard. Leave as-is unless you know a reference changed.";
    }
}
#endif
