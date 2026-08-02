// WebGpuWater - WaterVolume inspector: the MOTION tab.
// Every source of surface height, in one place, ordered LARGEST FIRST below the things that steer
// them all:
//   global clock -> wind -> interactive ripples -> ocean sea state -> small wind waves -> surf fronts.
// Reading the tab top-down is reading the wave stack. Nothing here decides how the water LOOKS.
//
// Wind is its own section, above the wave sources rather than inside one. It used to live inside
// "Wind Waves" while also setting the OCEAN's wave scale and swell amplitude, so the sea's size was
// authored from the ripple section - the two are now genuinely independent (Peak Wavelength owns the
// ocean's scale, Fetch owns the ripples'), and the layout says so.
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

        // Wind is drawn ABOVE the wave sections and outside both, because it is not a wave source: it
        // steers direction and spreading for every layer, and gates the whitecaps. It used to sit inside
        // "Wind Waves" while quietly setting the OCEAN's wave scale as well, which is exactly why the two
        // scales could not be authored apart.
        void DrawWindSection()
        {
            _showWind = WaterEditorUI.Section("Wind", _showWind, () =>
            {
                EditorGUILayout.HelpBox(WindHelp, MessageType.None);
                DrawFields(
                    WaterVolumePropertyPaths.WindSpeed,
                    "windWaveSettings.windFromDegrees");
                DrawFieldsIf(LakeOrOcean, WaterVolumePropertyPaths.OceanWindTurbulence);
            });
        }

        void DrawWindWavesSection()
        {
            _showWindWaves = WaterEditorUI.SectionWithToggle(
                "Small Wind Waves (ripple layer)", _showWindWaves, Prop("windWaveSettings.windWaves"), () =>
            {
                EditorGUILayout.HelpBox(SmallWindWaveHelp, MessageType.None);
                DrawFields(
                    WaterVolumePropertyPaths.WaveHeightMeters,
                    WaterVolumePropertyPaths.WaveLengthMeters,
                    WaterVolumePropertyPaths.WaveGrouping,
                    WaterVolumePropertyPaths.WaveCrestSharpness);
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
                "Ocean Sea State (open water)", _showOceanSwell, Prop(WaterVolumePropertyPaths.OpenWater), () =>
                {
                    EditorGUILayout.HelpBox(SeaStateHelp, MessageType.None);
                    DrawFields(
                        WaterVolumePropertyPaths.SignificantWaveHeight,
                        WaterVolumePropertyPaths.PeakWavelength,
                        WaterVolumePropertyPaths.PeakSharpness,
                        WaterVolumePropertyPaths.LargeWaveChoppiness,
                        WaterVolumePropertyPaths.WaveScale);
                    // Steepness is the whole point of splitting height from wavelength, so show the one
                    // number the two sliders exist to control rather than making it mental arithmetic.
                    if (target is WaterVolume seaVolume) DrawSteepnessReadout(seaVolume);

                    _showSwell = WaterEditorUI.SubSection("Swell", _showSwell, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.SwellHeight,
                            WaterVolumePropertyPaths.SwellWavelength));
                    // Topology and water body, not feel: all decided once when the body is authored.
                    _showOceanSwellAdvanced = WaterEditorUI.SubSection("Advanced", _showOceanSwellAdvanced, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.SeaDepth,
                            "ocean.cascadeReach",
                            WaterVolumePropertyPaths.UnboundedOcean,
                            WaterVolumePropertyPaths.EdgeFeatherMeters));
                },
                contentEnabled: LakeOrOcean);
        }

        // Wave steepness Hs / lambda_p, with the two thresholds that actually mean something: real seas
        // sit around 1/30, and a Stokes wave breaks near 1/7. Read-only - it is the ratio of two sliders,
        // not a third one, and making it editable would just raise "which of the two moved?".
        void DrawSteepnessReadout(WaterVolume volume)
        {
            float peak = volume.PeakWavelengthEffective;
            if (peak <= 0f) return;
            float steepness = volume.SignificantWaveHeight / peak;
            string character = steepness >= BreakingSteepness ? "breaking - expect heavy whitecaps"
                             : steepness >= AgitatedSteepness ? "steep, agitated"
                             : steepness >= SwellSteepness ? "ordinary sea"
                             : "lazy swell";
            EditorGUILayout.LabelField(" ", $"Steepness: 1/{1f / steepness:0} ({character})",
                                       EditorStyles.miniLabel);
        }

        // Stokes' limiting steepness is 1/7; the other two are where a sea stops reading as one thing and
        // starts reading as another, taken from the fetch-limited steepness law (Hs/lambda ~ 1/14 at very
        // short fetch, ~1/35 at ocean fetches).
        const float BreakingSteepness = 1f / 7f;
        const float AgitatedSteepness = 1f / 20f;
        const float SwellSteepness = 1f / 60f;
        // Float slop, so the readout does not flicker on when the floor merely ties the authored value.
        const float ShoalBandReadoutEpsilon = 1e-3f;

        void DrawSurfFrontsSection()
        {
            _showSurf = WaterEditorUI.SectionWithToggle(
                "Surf Fronts (shoaling breakers)", _showSurf, Prop(WaterVolumePropertyPaths.SurfEnabled), () =>
            {
                DrawFields(WaterVolumePropertyPaths.SurfAmplitude);
                // Runtime silently floors the surf amplitude at the swell height; surface the effective
                // value here whenever that floor is actually raising it.
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
                // Same treatment as the surf amplitude above: the band is floored so that shoaling
                // always begins outside the depth the sea can survive in, and a big sea moves that
                // floor well past whatever is typed here.
                if (target is WaterVolume bandVolume &&
                    bandVolume.ShoreShoalDepthEffective > Prop("bedDepthSettings.shoreShoalDepth").floatValue + ShoalBandReadoutEpsilon)
                    EditorGUILayout.LabelField(" ",
                        $"Effective: {bandVolume.ShoreShoalDepthEffective:0.##} m (floored at twice the offshore sea height)",
                        EditorStyles.miniLabel);

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

        const string SeaStateHelp =
            "Significant Height is HOW MUCH water the sea carries; Peak Wavelength is HOW FAR APART the " +
            "waves are. Together they set steepness, so a short peak with a tall height is a small " +
            "agitated chop and a long peak with the same height is a lazy ocean swell. Peak Sharpness " +
            "sets the character (1 = confused, 7 = organised corduroy) without changing the height. " +
            "Wave Scale multiplies the wavelength alone, for a miniature or a giant sea at the same " +
            "steepness. Wind no longer touches any of this - it only steers direction and whitecaps.";
        const string WindHelp =
            "Wind steers every wave layer and gates the whitecaps. It does NOT set wave size any more: " +
            "the ocean's scale is Peak Wavelength (Ocean Sea State) and the ripple layer's is Fetch " +
            "(Small Wind Waves).";
        const string SmallWindWaveHelp =
            "The fine ripple layer that rides on top of everything else, everywhere on the body - " +
            "independent of the ocean's FFT sea state, and what a pool or a pond has instead. Height " +
            "and Length are in metres and orthogonal, exactly like the ocean's: a short length with a " +
            "tall height is mid-lake chop, a long one is a lazy ripple. Sets and Crest Sharpness are " +
            "shape only and leave the height alone.";
        const string SurfFoamPointerHelp =
            "Whitewash, swash foam and the crest pop curve are in Surface > Foam > Shore & Swash.";
    }
}
#endif
