// WebGL Water - WaterVolume inspector: surface look + light-transport sections (reflections,
// Beer-Lambert water fog, depth attenuation, real-bed depth, turbulence foam). Toggle-gated blocks
// grey their body when the feature is off. Draws serialized properties by exact path.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawReflectionsSection()
        {
            _showReflections = WaterEditorUI.Section("Reflections", _showReflections, () =>
            {
                DrawFields(
                    "reflectionSettings.useScreenSpaceReflection",
                    "reflectionSettings.usePlanarReflection",
                    "reflectionSettings.reflectUrpProbe",
                    "reflectionSettings.realRefraction");
                WaterEditorUI.SubHeading("Look");
                DrawFields(
                    "reflectionSettings.reflectionStrength",
                    "reflectionSettings.envReflectionIntensity",
                    "reflectionSettings.reflectionDistortion",
                    "reflectionSettings.ssrStrength",
                    "reflectionSettings.ssrStepSize",
                    "reflectionSettings.ssrMaxSteps",
                    "reflectionSettings.ssrThickness",
                    "reflectionSettings.refractionDistortion");
            });
        }

        void DrawWaterFogSection()
        {
            _showWaterFog = WaterEditorUI.SectionWithToggle(
                "Water Fog (Beer-Lambert)", _showWaterFog, Prop("waterFogSettings.waterFog"), () =>
                DrawFields(
                    "waterFogSettings.fogColor",
                    "waterFogSettings.fogExtinction",
                    "waterFogSettings.fogDensity",
                    "waterFogSettings.waterOpacity"));
        }

        void DrawVolumeScatterSection()
        {
            _showScatter = WaterEditorUI.SectionWithToggle(
                "Volume Scattering", _showScatter, Prop("volumeScatterSettings.volumeScatter"), () =>
            {
                DrawFields(
                    "volumeScatterSettings.scatterColor",
                    "volumeScatterSettings.scatterIntensity",
                    "volumeScatterSettings.scatterAnisotropy",
                    "volumeScatterSettings.scatterAmbientTerm",
                    "volumeScatterSettings.scatterSunTerm");
                _showCrestGlow = WaterEditorUI.SubSection("Wave-crest subsurface glow (ocean)", _showCrestGlow, () =>
                    DrawFields(
                        "volumeScatterSettings.crestScatter",
                        "volumeScatterSettings.sssIntensity",
                        "volumeScatterSettings.sssSunFalloff",
                        "volumeScatterSettings.sssPinchMin",
                        "volumeScatterSettings.sssPinchMax",
                        "volumeScatterSettings.sssPinchFalloff"),
                    contentEnabled: IsOcean);
            });
        }

        void DrawDepthAttenuationSection()
        {
            _showDepth = WaterEditorUI.SectionWithToggle(
                "Depth Attenuation (downwelling)", _showDepth, Prop("depthAttenuation.depthDarken"), () =>
                DrawFields(
                    "depthAttenuation.depthExtinction",
                    "depthAttenuation.depthDarkenStrength",
                    "depthAttenuation.causticDepthFade",
                    "depthAttenuation.godRayDepthFade",
                    "depthAttenuation.linkDepthToFog"));
        }

        void DrawBedDepthSection()
        {
            _showBedDepth = WaterEditorUI.SectionWithToggle(
                "Bed Depth (real terrain depth)", _showBedDepth, Prop("bedDepthSettings.useBedDepth"), () =>
            {
                DrawFields(
                    "bedDepthSettings.bedTerrain",
                    "bedDepthSettings.bedResolution",
                    "bedDepthSettings.deepWaterColor",
                    "bedDepthSettings.bedFadeDepth",
                    "bedDepthSettings.bedTintStrength",
                    "bedDepthSettings.shoreShoalDepth");
                WaterEditorUI.SubHeading("Depth clarity (auto transparency)");
                DrawFields("bedDepthSettings.clarityFromDepth");
                DrawFieldsIf(Prop("bedDepthSettings.clarityFromDepth").boolValue,
                    "bedDepthSettings.clarityShallowDepth",
                    "bedDepthSettings.clarityDeepDepth",
                    "bedDepthSettings.clarityShallow",
                    "bedDepthSettings.clarityDeep",
                    "bedDepthSettings.clarityStrength");
                WaterEditorUI.SubHeading("Surf breaker fronts");
                DrawFields(
                    "bedDepthSettings.surfEnabled",
                    "bedDepthSettings.surfAmplitude");
                // Runtime silently floors the surf amplitude at the swell height; surface the
                // effective value here whenever that floor is actually raising it.
                if (target is WaterVolume floorVolume &&
                    floorVolume.SwellHeight > Prop("bedDepthSettings.surfAmplitude").floatValue)
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
                DrawFields("bedDepthSettings.surfPeriod");
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
                    DrawFields("bedDepthSettings.surfSwashAmplitude");
                    WaterEditorUI.SubHeading("Foam");
                    DrawFields(
                        "bedDepthSettings.surfFoamGain",
                        "bedDepthSettings.surfWaterlineFoam",
                        "bedDepthSettings.surfFoamStrength",
                        "bedDepthSettings.surfFoamFeather",
                        "bedDepthSettings.surfFoamTileSize",
                        "bedDepthSettings.surfFoamColor");
                });
            });
        }

        void DrawFoamSection()
        {
            _showFoam = WaterEditorUI.SectionWithToggle(
                "Foam (turbulence)", _showFoam, Prop("foamSettings.foam"), () =>
            {
                WaterEditorUI.SubHeading("Generation & decay");
                DrawFields(
                    "foamSettings.foamGenRate",
                    "foamSettings.foamGenThreshold",
                    "foamSettings.foamMinWaveHeight",
                    "foamSettings.foamDecay",
                    "foamSettings.foamDecayResidual",
                    "foamSettings.foamDecayRate",
                    "foamSettings.foamSpread",
                    "foamSettings.foamAdvect",
                    "foamSettings.foamFromSpeed",
                    "foamSettings.foamFromCurvature");
                WaterEditorUI.SubHeading("Shading");
                DrawFields(
                    "foamSettings.foamColor",
                    "foamSettings.foamPatternSize",
                    "foamSettings.foamStrength",
                    "foamSettings.foamFeather",
                    "foamSettings.foamCoreCut");
                // Pool-wall border foam + geometry contact foam are bounded-only.
                DrawFieldsIf(Bounded,
                    "foamSettings.foamBorderWidth",
                    "foamSettings.foamContactDepth");
            });
        }
    }
}
#endif
