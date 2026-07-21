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

        // One dedicated section for every author-time SURFACE texture slot + its tweaks. The foam pattern
        // and ocean whitecap used to be reachable only on the water material; they live on the body now, so
        // all surface textures are configured from one place. Empty foam/whitecap slots keep the material's.
        void DrawLookSection()
        {
            _showLook = WaterEditorUI.Section("Textures", _showLook, () =>
            {
                DrawFieldsIf(Bounded, "tiles"); // pool tile albedo - bounded bodies only
                DrawFields("sky");

                // Crest-style crossing scrolling detail normals: off (flat) until a tiling
                // water-normal texture is assigned; the sliders shape the layer once it is.
                WaterEditorUI.SubHeading("Detail normals (micro ripples)");
                DrawFields("detailNormalSettings.texture");
                DrawFieldsIf(Prop("detailNormalSettings.texture").objectReferenceValue != null,
                    "detailNormalSettings.strength",
                    "detailNormalSettings.tileMeters",
                    "detailNormalSettings.scrollSpeed");

                WaterEditorUI.SubHeading("Surface foam pattern");
                EditorGUILayout.HelpBox("Empty keeps the water material's own foam texture. Assign here to " +
                    "drive it from this body; the flipbook grid/rate and relief apply once a texture is set.",
                    MessageType.None);
                DrawFields("foamPatternTexture");
                DrawFieldsIf(Prop("foamPatternTexture").objectReferenceValue != null,
                    "foamPatternGrid", "foamPatternFps");

                WaterEditorUI.SubHeading("Ocean whitecap");
                DrawFields("oceanWhitecapTexture");

                WaterEditorUI.SubHeading("Foam relief (foam pattern + whitecap)");
                DrawFields("foamReliefStrength");
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
                    "largeBodyCausticsShader", "obstacleShader", "occluderShader", "waterMesh",
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
