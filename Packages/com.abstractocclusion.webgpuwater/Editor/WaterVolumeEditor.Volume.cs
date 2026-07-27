// WebGpuWater - WaterVolume inspector: the VOLUME tab.
// Everything light does THROUGH the water rather than at its surface: Beer-Lambert fog, in-scatter,
// downwelling attenuation, caustics, god rays, the deep colour read off the bed, and horizon haze.
//
// Caustics and god rays each had their knobs in three different places; both are single sections
// here. The two exceptions are deliberate: the CHUNK shafts stay in Body > Chunk (a different code
// path - marched in the shell wall, shaped by the fill level - and the chunk's only inspector), and
// the caustic/bed RESOLUTIONS are budget knobs, so they live in the Budget tab.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawWaterFogSection()
        {
            _showWaterFog = WaterEditorUI.SectionWithToggle(
                "Water Fog (Beer-Lambert)", _showWaterFog, Prop(WaterVolumePropertyPaths.WaterFog), () =>
                DrawFields(
                    WaterVolumePropertyPaths.FogColor,
                    WaterVolumePropertyPaths.FogExtinction,
                    WaterVolumePropertyPaths.FogDensity,
                    "waterFogSettings.waterOpacity"));
        }

        void DrawVolumeScatterSection()
        {
            _showScatter = WaterEditorUI.SectionWithToggle(
                "Volume Scattering", _showScatter, Prop(WaterVolumePropertyPaths.VolumeScatter), () =>
            {
                DrawFields(
                    WaterVolumePropertyPaths.ScatterColor,
                    WaterVolumePropertyPaths.ScatterIntensity);
                _showScatterAdvanced = WaterEditorUI.SubSection("Advanced", _showScatterAdvanced, () =>
                    DrawFields(
                        "volumeScatterSettings.scatterAnisotropy",
                        "volumeScatterSettings.scatterAmbientTerm",
                        "volumeScatterSettings.scatterSunTerm"));
                _showCrestGlow = WaterEditorUI.SubSection("Wave-crest subsurface glow (ocean)", _showCrestGlow, () =>
                    DrawFields(
                        WaterVolumePropertyPaths.CrestScatter,
                        "volumeScatterSettings.sssIntensity",
                        "volumeScatterSettings.sssSunFalloff",
                        "volumeScatterSettings.sssPinchMin",
                        "volumeScatterSettings.sssPinchMax",
                        "volumeScatterSettings.sssPinchFalloff"),
                    contentEnabled: IsOcean);
            });
        }

        // Downwelling only: how much light is left at depth. What that light then PAINTS (caustics,
        // shafts) moved to its own section below.
        void DrawDepthAttenuationSection()
        {
            _showDepth = WaterEditorUI.SectionWithToggle(
                "Depth Attenuation (downwelling)", _showDepth, Prop("depthAttenuation.depthDarken"), () =>
                DrawFields(
                    "depthAttenuation.depthExtinction",
                    "depthAttenuation.depthDarkenStrength",
                    "depthAttenuation.linkDepthToFog"));
        }

        void DrawCausticsSection()
        {
            _showCaustics = WaterEditorUI.Section("Caustics", _showCaustics, () =>
            {
                DrawFields(
                    "depthAttenuation.causticDepthFade",
                    "depthAttenuation.screenSpaceCaustics",
                    "depthAttenuation.screenCausticIntensity");
                WaterEditorUI.SubHeading("Ocean caustics");
                DrawFieldsIf(IsOcean, "ocean.largeGodRayCausticStrength");
                _showCausticsAdvanced = WaterEditorUI.SubSection("Advanced", _showCausticsAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Ripple shaping");
                    DrawFields(
                        "ocean.largeCausticTimeScale",
                        "ocean.largeCausticRippleScale",
                        "ocean.largeCausticRippleStrength");
                    WaterEditorUI.SubHeading("Softening");
                    DrawFields(
                        "ocean.largeGodRayCausticSmooth",
                        "ocean.largeGodRayCausticDepthSoften");
                }, contentEnabled: IsOcean);
                EditorGUILayout.HelpBox(CausticResolutionHelp, MessageType.None);
            });
        }

        void DrawGodRaysSection()
        {
            _showGodRays = WaterEditorUI.Section("God Rays (volumetric shafts)", _showGodRays, () =>
            {
                WaterEditorUI.SubHeading("Ocean shafts");
                DrawFieldsIf(IsOcean,
                    "ocean.largeGodRayColor",
                    WaterVolumePropertyPaths.LargeGodRayDensity);
                // Step count is a cost knob; the rest are second-order shaping of the same shafts.
                _showGodRaysAdvanced = WaterEditorUI.SubSection("Advanced", _showGodRaysAdvanced, () =>
                {
                    DrawFields(WaterVolumePropertyPaths.GodRayDepthFade);
                    DrawFieldsIf(IsOcean,
                        "ocean.largeGodRayFromAir",
                        "ocean.largeGodRaySteps",
                        "ocean.largeGodRayAnisotropy",
                        "ocean.largeGodRayExtinction");
                });
                EditorGUILayout.HelpBox(ChunkGodRayPointerHelp, MessageType.None);
            });
        }

        // The colour the water takes from a real bed. Source toggle + terrain are in the Body tab.
        void DrawBedColourSection()
        {
            _showBedColour = WaterEditorUI.Section("Bed Colour & Clarity", _showBedColour, () =>
            {
                DrawFields(
                    "bedDepthSettings.deepWaterColor",
                    "bedDepthSettings.bedTintStrength");
                WaterEditorUI.SubHeading("Depth clarity (auto transparency)");
                DrawFields(WaterVolumePropertyPaths.ClarityFromDepth);
                // The clarity CURVE is five numbers that shape one behaviour; the switch above is
                // the decision, these are the calibration.
                _showBedColourAdvanced = WaterEditorUI.SubSection("Advanced", _showBedColourAdvanced, () =>
                {
                    DrawFields("bedDepthSettings.bedFadeDepth");
                    WaterEditorUI.SubHeading("Clarity curve");
                    DrawFieldsIf(Prop(WaterVolumePropertyPaths.ClarityFromDepth).boolValue,
                        WaterVolumePropertyPaths.ClarityShallowDepth,
                        WaterVolumePropertyPaths.ClarityDeepDepth,
                        "bedDepthSettings.clarityShallow",
                        "bedDepthSettings.clarityDeep",
                        "bedDepthSettings.clarityStrength");
                });
            },
            contentEnabled: UsesBedDepth);
        }

        // Atmospheric transmission at the far edge of the surface. It sat inside the clipmap block,
        // which is geometry - the haze is a light-transport term and belongs here.
        void DrawHorizonHazeSection()
        {
            _showHorizonHaze = WaterEditorUI.Section("Horizon Haze", _showHorizonHaze, () =>
            {
                EditorGUILayout.HelpBox(OceanOnlyHelp, MessageType.None);
                DrawFields(
                    "ocean.horizonHazeColor",
                    WaterVolumePropertyPaths.HorizonHazeDensity);
            }, contentEnabled: IsOcean);
        }

        const string OceanOnlyHelp = "Ocean-only. Requires Ocean Swell on to take effect.";
        const string CausticResolutionHelp =
            "Caustic RT resolution is a budget knob - it is in the Budget tab with the other resolutions.";
        const string ChunkGodRayPointerHelp =
            "A CHUNK's shafts are a separate path (marched in its shell wall, shaped by its fill level) " +
            "with their own strength + colour - see Body > Chunk.";
    }
}
#endif
