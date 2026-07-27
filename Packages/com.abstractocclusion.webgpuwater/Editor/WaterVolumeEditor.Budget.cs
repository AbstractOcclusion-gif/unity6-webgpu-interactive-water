// WebGpuWater - WaterVolume inspector: the BUDGET tab.
// Everything you pay for: the quality tier, every RT resolution, culling, the camera-following sim
// window, and the horizon clipmap's geometry. If a knob trades frame time for fidelity it is here,
// even when the fidelity it buys is shown in another tab.
//
// The clipmap's HAZE colour/density is NOT here - that is light transport, drawn in Volume. This
// section is only the mesh that carries the ocean to the horizon.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawQualitySection()
        {
            _showQuality = WaterEditorUI.Section("Quality & Culling", _showQuality, () =>
            {
                // The tier already sets sensible resolutions; the explicit ones below override it.
                DrawFields("quality", "rippleQuality", "enableCulling");
                _showQualityAdvanced = WaterEditorUI.SubSection("Advanced", _showQualityAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Resolutions");
                    DrawFields("causticResolution");
                    // The bed bake only happens when a bed terrain drives this body (Body tab).
                    DrawFieldsIf(UsesBedDepth, "bedDepthSettings.bedResolution");
                    WaterEditorUI.SubHeading("Culling");
                    // Activation distance only bites when culling is on; grey it out otherwise.
                    DrawFieldsIf(Prop("enableCulling").boolValue, "activationDistance");
                });
            });
        }

        void DrawWindowSection()
        {
            _showWindow = WaterEditorUI.SectionWithToggle(
                "Large-Water Sim Window", _showWindow, Prop(WaterVolumePropertyPaths.EnableLargeBodyWindow), () =>
            {
                // The window SIZE is the budget decision; where it sits and how it feathers are
                // placement details that the defaults already handle.
                DrawFields("simWindowMeters");
                _showWindowAdvanced = WaterEditorUI.SubSection("Advanced", _showWindowAdvanced, () =>
                    DrawFields(
                        "largeBodyThreshold",
                        "clampWindowToShore",
                        "simWindowFocus",
                        "simWindowOffset",
                        "simWindowEdgeFadeTexels"));
            },
            contentEnabled: LakeOrOcean);
        }

        void DrawClipmapSection()
        {
            _showClipmap = WaterEditorUI.Section("Ocean Clipmap (horizon geometry)", _showClipmap, () =>
            {
                EditorGUILayout.HelpBox(ClipmapHelp, MessageType.None);
                DrawFields("ocean.clipmapOuterRadius");
                _showClipmapAdvanced = WaterEditorUI.SubSection("Advanced", _showClipmapAdvanced, () =>
                    DrawFields(
                        "ocean.clipmapGridResolution",
                        "ocean.oceanDetailFalloff",
                        "ocean.horizonFadeDistance"));
            }, contentEnabled: IsOcean);
        }

        const string ClipmapHelp =
            "Ocean-only. Requires Ocean Swell on to take effect. The horizon HAZE colour + density " +
            "are light transport - they are in the Volume tab.";
    }
}
#endif
