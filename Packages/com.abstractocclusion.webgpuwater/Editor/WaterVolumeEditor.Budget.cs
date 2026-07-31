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
        // Sits next to the slider rather than in the Caustics section: this is the tab a user reaches
        // for when caustics look blocky, and raising this is the first thing they will try.
        const string CausticResolutionBudgetHelp =
            "Caustic resolution above the SIM resolution adds no detail: the generator writes one focus " +
            "value per sim grid cell, so how fine the pattern can get is set by Ripple Quality - and on an " +
            "ocean by the sim window size. Higher values here only smooth the sampling. One map feeds " +
            "everything that shows caustics: pool walls and floor, water receivers, terrain and other " +
            "foreign surfaces via the screen-space pass, and the light shafts.\n\n" +
            "Assigning a Quality asset replaces this value outright, so the field greys out; clear the " +
            "asset to author it per body.";

        void DrawQualitySection()
        {
            _showQuality = WaterEditorUI.Section("Quality & Culling", _showQuality, () =>
            {
                // The tier already sets sensible resolutions; the explicit ones below override it.
                DrawFields("quality", "rippleQuality", "enableCulling");
                _showQualityAdvanced = WaterEditorUI.SubSection("Advanced", _showQualityAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Resolutions");
                    // A quality asset REPLACES this outright (ApplyQuality -> _causticRes), so the
                    // authored value is dead while one is assigned - grey it rather than let the user
                    // drag a slider that does nothing. With no asset it is the only source, so it stays
                    // live. Greyed, not hidden: the value still ships in the scene and still applies the
                    // moment the asset is cleared.
                    DrawFieldsIf(Prop("quality").objectReferenceValue == null, "causticResolution");
                    // NOT greyed by the quality asset: the lattice density is the artist's, while the
                    // map size above is the tier's. They cap each other, they do not replace each other.
                    DrawFields("causticDetail");
                    EditorGUILayout.HelpBox(CausticResolutionBudgetHelp, MessageType.None);
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
