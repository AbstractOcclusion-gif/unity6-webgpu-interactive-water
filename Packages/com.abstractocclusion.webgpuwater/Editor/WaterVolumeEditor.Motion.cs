// WebGpuWater - WaterVolume inspector: the MOTION tab.
// Every source of surface height, in one place, ordered by the scale it acts on:
//   global clock -> interactive ripples -> spectral wind waves -> ocean swell -> surf fronts.
// Reading the tab top-down is reading the wave stack. Nothing here decides how the water LOOKS.
//
// The surf block was extracted from the old 45-field "Bed Depth" section: its motion (shoal,
// fronts, crests, swash) is here, its foam is in Surface > Foam > Shore & Swash, its colour in
// Volume > Bed Colour & Clarity. It greys out until Bed Depth is on in the Body tab.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Drawn before the first foldout: one knob that scales every motion source below it, so it
        // reads as the tab's master rather than as a section of its own.
        void DrawMotionGlobals()
        {
            DrawFields("timeScale");
            EditorGUILayout.Space();
        }

        void DrawRippleSection()
        {
            _showRipple = WaterEditorUI.Section("Interactive Ripples", _showRipple, () =>
            {
                DrawFields(
                    "rippleSettings.waveSpeed",
                    "rippleSettings.damping",
                    "rippleSettings.rippleStrength",
                    "rippleSettings.rippleRadius",
                    "rippleSettings.rippleChoppiness");
                _showRippleAdvanced = WaterEditorUI.SubSection("Advanced", _showRippleAdvanced, () =>
                {
                    DrawFields("rippleSettings.stepsPerFrame", "rippleSettings.seedRipplesOnStart");
                    // Volume conservation is meaningless on an unbounded ocean (no finite volume to conserve).
                    DrawFieldsIf(Bounded,
                        "rippleSettings.conserveVolume",
                        "rippleSettings.conserveMaxCorrection");
                });
            });
        }

        void DrawWindWavesSection()
        {
            _showWindWaves = WaterEditorUI.SectionWithToggle(
                "Wind Waves (spectral)", _showWindWaves, Prop("windWaveSettings.windWaves"), () =>
            {
                DrawFields(
                    WaterVolumePropertyPaths.WindSpeed,
                    "windWaveSettings.windFromDegrees",
                    WaterVolumePropertyPaths.WaveScaleMeters,
                    WaterVolumePropertyPaths.WaveAmplitudeScale);
                // waveCount is a cost/quality trade, the other two shape the spectrum once.
                _showWindWavesAdvanced = WaterEditorUI.SubSection("Advanced", _showWindWavesAdvanced, () =>
                    DrawFields(
                        "windWaveSettings.waveCount",
                        "windWaveSettings.waveDirectionSpread",
                        "windWaveSettings.waveNormalStrength"));
            });
        }

        void DrawOceanSwellSection()
        {
            _showOceanSwell = WaterEditorUI.SectionWithToggle(
                "Ocean Swell (open water)", _showOceanSwell, Prop(WaterVolumePropertyPaths.OpenWater), () =>
                {
                    EditorGUILayout.HelpBox(SwellHelp, MessageType.None);
                    DrawFields(
                        WaterVolumePropertyPaths.LargeWaveAmplitude,
                        WaterVolumePropertyPaths.LargeWaveChoppiness,
                        WaterVolumePropertyPaths.SwellHeight,
                        WaterVolumePropertyPaths.SwellWavelength);
                    // Topology, not feel: both are decided once when the body is authored.
                    _showOceanSwellAdvanced = WaterEditorUI.SubSection("Advanced", _showOceanSwellAdvanced, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.UnboundedOcean,
                            WaterVolumePropertyPaths.EdgeFeatherMeters));
                },
                contentEnabled: LakeOrOcean);
        }

        void DrawSurfFrontsSection()
        {
            _showSurf = WaterEditorUI.SectionWithToggle(
                "Surf Fronts (shoaling breakers)", _showSurf, Prop(WaterVolumePropertyPaths.SurfEnabled), () =>
            {
                DrawFields(WaterVolumePropertyPaths.SurfAmplitude);
                // Runtime silently floors the surf amplitude at the swell height; surface the
                // effective value here whenever that floor is actually raising it.
                if (target is WaterVolume floorVolume &&
                    floorVolume.SwellHeight > Prop(WaterVolumePropertyPaths.SurfAmplitude).floatValue)
                    EditorGUILayout.LabelField(" ",
                        $"Effective: {floorVolume.SurfAmplitudeEffective:0.##} m (floored at the swell height)",
                        EditorStyles.miniLabel);
                DrawFields("bedDepthSettings.surfWavelengthAuto");
                // Manual spacing only applies with Auto off; greyed (not hidden) so the stored
                // hand-tuned value stays visible. With Auto on, show the derived spacing readout.
                bool wavelengthAuto = Prop("bedDepthSettings.surfWavelengthAuto").boolValue;
                DrawFieldsIf(!wavelengthAuto, "bedDepthSettings.surfWavelength");
                if (wavelengthAuto && target is WaterVolume surfVolume)
                    EditorGUILayout.LabelField(" ",
                        $"Derived spacing: {surfVolume.SurfWavelengthEffective:0.#} m",
                        EditorStyles.miniLabel);
                DrawFields("bedDepthSettings.surfPeriod", "bedDepthSettings.shoreShoalDepth");

                _showSurfAdvanced = WaterEditorUI.SubSection("Advanced", _showSurfAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Shoal transform");
                    DrawFields(
                        "bedDepthSettings.shoreRefraction",
                        "bedDepthSettings.shoreCompression",
                        "bedDepthSettings.shoreGreens");
                    WaterEditorUI.SubHeading("Front shaping");
                    DrawFields(
                        "bedDepthSettings.surfBandDepth",
                        "bedDepthSettings.surfSetStrength",
                        "bedDepthSettings.surfLean",
                        "bedDepthSettings.surfAmbientFade",
                        "bedDepthSettings.surfDirectionality");
                    WaterEditorUI.SubHeading("Crest segmentation");
                    DrawFields(
                        "bedDepthSettings.surfCrestLength",
                        "bedDepthSettings.surfCrestVariation",
                        "bedDepthSettings.surfCrestPersistence");
                    WaterEditorUI.SubHeading("Swash");
                    DrawFields("bedDepthSettings.surfSwashAmplitude",
                               "bedDepthSettings.surfSwashMaxSlopeDegrees");
                });

                EditorGUILayout.HelpBox(SurfFoamPointerHelp, MessageType.None);
            },
            contentEnabled: UsesBedDepth);
        }

        const string SwellHelp =
            "Large Wave Amplitude scales the wind-driven swell (steered by the Wind Waves section). " +
            "Swell Height adds an independent long-period roll on top. Unbounded Ocean extends the " +
            "surface to the horizon (an ocean, not a bounded lake). Edge Feather flattens the wave " +
            "field toward a BOUNDED body's border so the surface never ends as a wall of water.";
        const string SurfFoamPointerHelp =
            "Whitewash, swash foam and the crest pop curve are in Surface > Foam > Shore & Swash.";
    }
}
#endif
