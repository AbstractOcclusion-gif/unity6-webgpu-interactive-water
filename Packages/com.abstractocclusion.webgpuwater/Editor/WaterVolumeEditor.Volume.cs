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
            {
                DrawFields(
                    "waterFogSettings.fullscreenVolumeFog",
                    WaterVolumePropertyPaths.FogColor,
                    WaterVolumePropertyPaths.FogExtinction,
                    WaterVolumePropertyPaths.FogDensity,
                    WaterVolumePropertyPaths.WaterOpacity,
                    "waterFogSettings.lightScatter");
                EditorGUILayout.HelpBox(
                    WaterFogReachSummary(Prop(WaterVolumePropertyPaths.FogExtinction).colorValue,
                                         Prop(WaterVolumePropertyPaths.FogDensity).floatValue),
                    MessageType.None);
            });
        }

        // Extinction and density multiply, so neither number means anything on its own and no slider
        // position can be read as "how murky is this". What an author actually wants is a DISTANCE:
        // ln(2) / (extinction * density) is where that channel reaches half brightness. Red goes
        // first at every shipped preset, which is the whole reason deep water reads blue. Recomputed
        // from the serialized values every repaint, so it tracks whichever of the two knobs moved.
        static string WaterFogReachSummary(Color extinction, float density)
        {
            return "Half-brightness distance:   R " + HalfBrightnessDistanceLabel(extinction.r * density)
                 + "    G " + HalfBrightnessDistanceLabel(extinction.g * density)
                 + "    B " + HalfBrightnessDistanceLabel(extinction.b * density);
        }

        const float NaturalLogOfTwo = 0.6931472f;
        const float FogReachClearMetres = 999f; // beyond this the channel is effectively unattenuated
        const string FogReachClearLabel = "clear";
        static string HalfBrightnessDistanceLabel(float coefficientPerMetre)
        {
            if (coefficientPerMetre <= 0f) return FogReachClearLabel;
            float metres = NaturalLogOfTwo / coefficientPerMetre;
            if (metres > FogReachClearMetres) return FogReachClearLabel;
            return metres.ToString(metres < 10f ? "0.00" : "0.#") + " m";
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
            {
                bool linked = Prop("depthAttenuation.linkDepthToFog").boolValue;
                // The colour row greys out while Link mirrors the fog extinction over it every
                // frame - editing an overridden field silently did nothing (Bert 2026-07-31,
                // "color depth extinction look to have no effect": the link was the effect).
                DrawFieldsIf(!linked, "depthAttenuation.depthExtinction");
                DrawFields(
                    "depthAttenuation.depthDarkenStrength",
                    "depthAttenuation.linkDepthToFog");
                // Same treatment as the Water Fog readout above (the confirmed MaxFogDensity
                // lesson): an exponential dial is only controllable next to the DISTANCE it
                // implies, and hue only exists while the channels still differ.
                Color depthExt = linked
                    ? Prop(WaterVolumePropertyPaths.FogExtinction).colorValue
                    : Prop("depthAttenuation.depthExtinction").colorValue;
                EditorGUILayout.HelpBox(
                    DepthReachSummary(depthExt, Prop("depthAttenuation.depthDarkenStrength").floatValue),
                    MessageType.None);
            });
        }

        // Half-brightness DEPTHS per channel for the downwelling term (exp(-ext * strength * d)),
        // so the dial reads in metres instead of guesswork - and so it is obvious when all three
        // channels crush within a metre and the colour can no longer show (past the point where
        // every channel has halved several times, black is black whatever the hue).
        static string DepthReachSummary(Color extinction, float strength)
        {
            const float Ln2 = 0.6931472f;
            const float MinCoeff = 1e-4f;
            float r = Ln2 / Mathf.Max(extinction.r * strength, MinCoeff);
            float g = Ln2 / Mathf.Max(extinction.g * strength, MinCoeff);
            float b = Ln2 / Mathf.Max(extinction.b * strength, MinCoeff);
            return $"Half-brightness depth  R {r:0.0} m   G {g:0.0} m   B {b:0.0} m — " +
                   "the colour shift lives between these depths; once all three have passed, " +
                   "deeper just reads black.";
        }

        void DrawCausticsSection()
        {
            _showCaustics = WaterEditorUI.Section("Caustics", _showCaustics, () =>
            {
                DrawFields(
                    "depthAttenuation.causticDepthFade",
                    "depthAttenuation.screenSpaceCaustics",
                    "depthAttenuation.screenCausticIntensity",
                    "depthAttenuation.causticWindWaveStrength");
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
                        "ocean.largeCausticProjectionSoften",
                        "ocean.largeGodRayCausticSmooth");
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
                    // Lives here, not under Ocean caustics: it is read ONLY by the shaft march
                    // (LargeBodyGodRays), never by the caustics painted on the ground.
                    DrawFieldsIf(IsOcean,
                        "ocean.largeGodRayCausticDepthSoften",
                        "ocean.largeGodRayFromAir",
                        // A2: lamp halos in the march. Sits by From Air - both are opt-in
                        // extensions of the same shafts; the fog's own Light Scatter row stays
                        // in the Water Fog block (two layers, two homes, independent keywords).
                        "ocean.largeGodRayLightScatter",
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
