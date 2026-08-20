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
        const string CurrentFieldsPath = "currentFields";
        const string CurrentFieldsHelp =
            "Current fields add physical world-space velocity without changing the surface normal. " +
            "Use a Constant Current Field for a whole-body stream or a River Current Field for " +
            "spline width, speed and waterfall direction. Sources in this list compose additively.";

        // Drawn before the first foldout: one knob that scales every motion source below it, so it
        // reads as the tab's master rather than as a section of its own.
        void DrawMotionGlobals()
        {
            DrawFields("timeScale");
            EditorGUILayout.Space();
        }

        void DrawCurrentSection()
        {
            _showCurrent = WaterEditorUI.Section("Currents", _showCurrent, () =>
            {
                EditorGUILayout.HelpBox(CurrentFieldsHelp, MessageType.None);
                DrawFields(CurrentFieldsPath);
            });
        }

        void DrawRippleSection()
        {
            _showRipple = WaterEditorUI.Section("Interactive Ripples", _showRipple, () =>
            {
                DrawFields(
                    "rippleSettings.waveSpeed",
                    "rippleSettings.damping",
                    "rippleSettings.rippleViscosity",
                    "rippleSettings.rippleStrength",
                    "rippleSettings.rippleRadius",
                    "rippleSettings.rippleChoppiness",
                    "rippleSettings.splashImpactRippleCap");
                _showWakeSafety = WaterEditorUI.SubSection("Wake Safety", _showWakeSafety, () =>
                {
                    EditorGUILayout.HelpBox("This cap applies to every wake interactor on this water body. " +
                                            "To cap only a boat's plunge/heave wave, set Vertical Force Cap " +
                                            "on that boat's Water Sphere Interactor.", MessageType.Info);
                    DrawFields("rippleSettings.wakeStartForceCap");
                });
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
                DrawFields(WaterVolumePropertyPaths.WaveHeightMeters, WaterVolumePropertyPaths.WaveLengthMeters);
                bool ambientWindDriven = target is WaterVolume windWaveVolume
                                         && windWaveVolume.WindDrivesAmbientSeaState;
                if (ambientWindDriven)
                    EditorGUILayout.LabelField("Wind Response", "Driven by Ambient Sea State", EditorStyles.miniLabel);
                else
                    DrawFields("windWaveSettings.windResponse");
                DrawFields("windWaveSettings.waveAnimationSpeed", WaterVolumePropertyPaths.WaveGrouping,
                           WaterVolumePropertyPaths.WaveCrestSharpness);
                // The authored metres describe the reference breeze; show what the wind is actually
                // making of them, so "my pond ignores the wind" and "why is it bigger than I typed"
                // are both answered on the spot.
                if (target is WaterVolume rippleVolume && rippleVolume.WindWaveResponseActive)
                    EditorGUILayout.LabelField(" ",
                        $"At this wind: {rippleVolume.WaveHeightEffective:0.###} m high, "
                        + $"{rippleVolume.WaveLengthEffective:0.##} m long",
                        EditorStyles.miniLabel);
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
                    DrawFields(WaterVolumePropertyPaths.WindDrivesAmbientSeaState);
                    bool ambientWindDriven = target is WaterVolume oceanVolume
                                             && oceanVolume.WindDrivesAmbientSeaState;
                    if (ambientWindDriven)
                        DrawFields(WaterVolumePropertyPaths.AmbientWindReferenceSpeed);
                    DrawFields(
                        WaterVolumePropertyPaths.SignificantWaveHeight,
                        WaterVolumePropertyPaths.PeakWavelength,
                        WaterVolumePropertyPaths.PeakSharpness,
                        WaterVolumePropertyPaths.LargeWaveChoppiness,
                        WaterVolumePropertyPaths.WaveScale);
                    // Steepness is the whole point of splitting height from wavelength, so show the one
                    // number the two sliders exist to control rather than making it mental arithmetic.
                    // The sea-state line then answers the question the metres alone do not: what size
                    // of wave will actually be seen out there.
                    if (target is WaterVolume seaVolume)
                    {
                        if (seaVolume.WindDrivesAmbientSeaState)
                            EditorGUILayout.LabelField(" ",
                                $"At this wind: {seaVolume.SignificantWaveHeight:0.##} m high, " +
                                $"{seaVolume.PeakWavelengthEffective:0.#} m peak wavelength",
                                EditorStyles.miniLabel);
                        DrawSteepnessReadout(seaVolume);
                        DrawSeaSizeReadout(seaVolume);
                    }
                    DrawRetiredAmplitudeWarning();

                    _showSwell = WaterEditorUI.SubSection("Swell", _showSwell, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.SwellHeight,
                            WaterVolumePropertyPaths.SwellWavelength,
                            WaterVolumePropertyPaths.SwellHeadingOffset));
                    // Uniform surface current: drifts the whole sampled wave field (crests,
                    // whitecaps, waterline, caustics together) - shader pair OceanCurrentDrift,
                    // WaterWaves.hlsl. Speed 0 (default) is inert.
                    _showSurfaceCurrent = WaterEditorUI.SubSection("Surface Current",
                        _showSurfaceCurrent, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.CurrentHeadingDegrees,
                            WaterVolumePropertyPaths.CurrentSpeed));
                    // Shading-only spatial variation (gusts/slicks) - see _SeaStateParams in
                    // WaterLargeWaves.hlsl for what each slider drives.
                    _showSeaState = WaterEditorUI.SubSection("Sea State Variation", _showSeaState, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.SeaStateGusts,
                            WaterVolumePropertyPaths.SeaStateSlicks));
                    _showOceanAperiodic = WaterEditorUI.SubSection("Aperiodic Direction Field",
                        _showOceanAperiodic, () =>
                        {
                            DrawFields(
                                WaterVolumePropertyPaths.OceanAperiodicEnabled,
                                WaterVolumePropertyPaths.OceanDirectionMap,
                                WaterVolumePropertyPaths.OceanDirectionMapSize,
                                WaterVolumePropertyPaths.OceanDirectionMapStrength,
                                WaterVolumePropertyPaths.OceanAperiodicTileScale);
                            if (ShouldWarnAboutAperiodicGodRays())
                                EditorGUILayout.HelpBox(AperiodicGodRaysWarning, MessageType.Warning);
                        });
                    _showWindFetch = WaterEditorUI.SubSection("Wind Fetch", _showWindFetch, () =>
                    {
                        DrawFields(
                            WaterVolumePropertyPaths.SeaStateFetchEnabled,
                            WaterVolumePropertyPaths.SeaStateFetchStrength);
                        if (target is WaterVolume fetchVolume)
                        {
                            string state = !fetchVolume.seaStateFetchEnabled ? "Disabled"
                                         : fetchVolume.unboundedOcean ? "Inert on unbounded ocean"
                                         : fetchVolume.SeaStateFetchBaked ? "Baked"
                                         : "Waiting for shore field";
                            EditorGUILayout.LabelField("Bake state", state, EditorStyles.miniLabel);
                        }
                    });
                    // Topology and water body, not feel: all decided once when the body is authored.
                    _showOceanSwellAdvanced = WaterEditorUI.SubSection("Advanced", _showOceanSwellAdvanced, () =>
                        DrawFields(
                            WaterVolumePropertyPaths.SeaDepth,
                            "ocean.cascadeReach",
                            // Unbounded Ocean moved to the Body tab's Topology section: it decides what
                            // the body IS, it must stay tickable when the type says Pond, and this fold
                            // greys out exactly when someone would be reaching for it.
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
            float significantHeight = volume.SignificantWaveHeight;
            if (peak <= 0f || significantHeight <= 0f) return;
            float steepness = significantHeight / peak;
            string character = steepness >= BreakingSteepness ? "breaking - expect heavy whitecaps"
                             : steepness >= AgitatedSteepness ? "steep, agitated"
                             : steepness >= SwellSteepness ? "ordinary sea"
                             : "lazy swell";
            EditorGUILayout.LabelField(" ", $"Steepness: 1/{1f / steepness:0} ({character})",
                                       EditorStyles.miniLabel);
        }

        // Real wave SIZE, which the authored metres do not state on their own.
        //
        // Significant Height is the mean of the highest THIRD of the waves. That definition exists
        // because it matches what an observer at sea reports as "the wave height" - so it is the honest
        // answer to "how big is my sea", but it is emphatically NOT the biggest wave anyone will meet.
        // For a narrow-banded sea the crest-to-trough heights are Rayleigh distributed, so the largest
        // wave in a run of N is Hs * sqrt(ln(N) / 2) - about 1.9x Hs over a storm's worth of waves.
        // Wind sea and swell are independent, so their heights combine in QUADRATURE (energies add),
        // which is the same rule LargeWaveHeightMeters uses on the runtime side.
        // Peak period comes from the deep-water dispersion relation via the coefficient the surf
        // breakers already use, so this readout and the breaker physics cannot disagree about how fast
        // a given wavelength travels.
        void DrawSeaSizeReadout(WaterVolume volume)
        {
            float peakWavelength = volume.PeakWavelengthEffective;
            if (peakWavelength <= 0f) return;

            float windSea = volume.SignificantWaveHeight;
            float swell = volume.SwellHeight;
            float significant = Mathf.Sqrt(windSea * windSea + swell * swell);
            if (significant <= 0f) return;

            float prominentCrestElevation = significant * SignificantHeightToCrestElevation;
            float largest = significant * Mathf.Sqrt(Mathf.Log(ObservedWaveCount) / 2f);
            float peakPeriod = Mathf.Sqrt(peakWavelength / LargeWaveField.SurfDeepwaterLengthCoef);
            EditorGUILayout.LabelField(" ",
                $"Visible crest ~{prominentCrestElevation:0.#} m above mean; Hs {significant:0.#} m "
                + $"crest-to-trough (highest-third average) - {DescribeSeaState(significant)}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(" ",
                $"Rare wave ~{largest:0.#} m crest-to-trough; peak period {peakPeriod:0.#} s",
                                       EditorStyles.miniLabel);
        }

        // WMO sea-state descriptions, by significant height in metres. Plain words rather than a code
        // number, because the point of the line is to tell an artist what they have built.
        static string DescribeSeaState(float significantHeight)
        {
            for (int i = 0; i < SeaStateCeilings.Length; i++)
                if (significantHeight < SeaStateCeilings[i]) return SeaStateNames[i];
            return PhenomenalSeaName;
        }

        static readonly float[] SeaStateCeilings = { 0.1f, 0.5f, 1.25f, 2.5f, 4f, 6f, 9f, 14f };
        static readonly string[] SeaStateNames =
            { "calm", "smooth", "slight", "moderate", "rough", "very rough", "high", "very high" };
        const string PhenomenalSeaName = "phenomenal";
        // Waves in the run the "biggest" figure is quoted over - roughly a storm's duration, which is
        // the window the textbook 1.86x Hs figure assumes.
        const float ObservedWaveCount = 1000f;
        const float SignificantHeightToCrestElevation = 0.5f;

        // `largeWaveAmplitude` is a retired whole-field multiplier (see
        // MigrateOceanAmplitudeIntoMetresV12). Unbounded oceans are folded back to 1 on load, so this
        // can only fire if that migration was bypassed - and if it ever does, every metre in this
        // section is a lie by exactly this factor. Expose the field only while broken: it gives the
        // author a normal-inspector repair path without resurrecting it as a sea-state control.
        void DrawRetiredAmplitudeWarning()
        {
            if (!Prop(WaterVolumePropertyPaths.UnboundedOcean).boolValue) return;
            SerializedProperty amplitudeProperty = Prop(WaterVolumePropertyPaths.LargeWaveAmplitude);
            float amplitude = amplitudeProperty.floatValue;
            if (Mathf.Approximately(amplitude, NeutralLargeWaveAmplitude)) return;
            EditorGUILayout.HelpBox(
                $"This ocean carries a retired wave-height multiplier of {amplitude:0.###}, so the sea "
                + "is rendered at that fraction of every height above. Set the repair field below to 1 and "
                + "re-author the heights in metres.", MessageType.Warning);
            EditorGUILayout.PropertyField(amplitudeProperty, RetiredAmplitudeRepairLabel);
        }

        const float NeutralLargeWaveAmplitude = 1f;
        static readonly GUIContent RetiredAmplitudeRepairLabel = new GUIContent(
            "Legacy Height Multiplier",
            "Compatibility value left by an older or flat-water setup. Set it to 1 so Significant "
            + "Wave Height and Swell Height render in metres. This field disappears once repaired.");

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

        bool ShouldWarnAboutAperiodicGodRays()
        {
            if (!IsOcean) return false;
            SerializedProperty enabled = Prop(WaterVolumePropertyPaths.OceanAperiodicEnabled);
            SerializedProperty godRayDensity = Prop(WaterVolumePropertyPaths.LargeGodRayDensity);
            return enabled != null && enabled.boolValue &&
                   godRayDensity != null && godRayDensity.floatValue > Mathf.Epsilon;
        }

        const string AperiodicGodRaysWarning =
            "God rays and large-body caustics use the original FFT direction. The runtime direction " +
            "map affects the visible surface, foam and buoyancy, but not their caustic projection; " +
            "including the three-tile synthesis there exceeds supported shader-compiler limits.";
        const string SeaStateHelp =
            "Significant Height is HOW MUCH water the sea carries; Peak Wavelength is HOW FAR APART the " +
            "waves are. Together they set steepness, so a short peak with a tall height is a small " +
            "agitated chop and a long peak with the same height is a lazy ocean swell. Peak Sharpness " +
            "sets the character (1 = confused, 7 = organised corduroy) without changing the height. " +
            "Wave Scale multiplies the wavelength alone, for a miniature or a giant sea at the same " +
            "steepness. Turn on Wind Drives Ambient Sea State to make Wind Speed scale the local wind sea; " +
            "Swell remains independent.";
        const string WindHelp =
            "Wind steers every wave layer and gates the whitecaps. Turn on Wind Drives Ambient Sea State " +
            "in Ocean Sea State when this one speed should also take the local sea from flat to rough. " +
            "Remote swell, wakes and impact ripples stay independent.";
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
