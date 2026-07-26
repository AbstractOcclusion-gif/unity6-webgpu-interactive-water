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
                DrawFields("refractShadows");
                if (!Prop("refractShadows").boolValue)
                    EditorGUILayout.HelpBox(
                        "Refract Underwater Shadows is OFF: every material (incl. Standard Lit) shows one " +
                        "consistent shadow from URP's straight shadow map - but the shadow and the caustics " +
                        "drift apart on a deep pool. On = shadows line up with the caustics (Water Receiver " +
                        "shader on submerged objects).", MessageType.None);
                WaterEditorUI.SubHeading("Look");
                DrawFields(
                    "reflectionSettings.reflectionStrength",
                    "reflectionSettings.envReflectionIntensity",
                    "reflectionSettings.reflectionDistortion",
                    "reflectionSettings.fresnelFloor",
                    "reflectionSettings.fresnelPower");
                WaterEditorUI.SubHeading("Roughness (sun lobe + sky blur)");
                DrawFields(
                    "reflectionSettings.sunRoughness",
                    "reflectionSettings.roughnessFar",
                    "reflectionSettings.roughnessFarDistance",
                    "reflectionSettings.roughnessFalloff",
                    "reflectionSettings.reflectionAnisoStretch",
                    "reflectionSettings.sunSheen",
                    "reflectionSettings.sunSheenRoughness",
                    "reflectionSettings.sunGrazeBoost");
                WaterEditorUI.SubHeading("Screen-space");
                DrawFields(
                    "reflectionSettings.ssrStrength",
                    "reflectionSettings.ssrStepSize",
                    "reflectionSettings.ssrMaxSteps",
                    "reflectionSettings.ssrThickness",
                    "reflectionSettings.refractionDistortion");
            });
        }

        // The surface seen FROM BELOW + the camera-crossing waterline. Its own section (not a
        // Reflections sub-block): the underside used to run on hard-coded constants, and artists
        // looking for "why is it milky underwater" should find one obvious foldout.
        void DrawUnderwaterSurfaceSection()
        {
            _showUnderwaterSurface = WaterEditorUI.Section("Underwater Surface (seen from below)",
                _showUnderwaterSurface, () =>
            {
                DrawFields("underwaterSurfaceSettings.physicalFresnel");
                DrawFieldsIf(Prop("underwaterSurfaceSettings.physicalFresnel").boolValue,
                    "underwaterSurfaceSettings.tirEdgeSoftness",
                    "underwaterSurfaceSettings.fresnelFloor");
                DrawFields(
                    "underwaterSurfaceSettings.reflectionStrength",
                    "underwaterSurfaceSettings.mirrorWaterBlend",
                    "underwaterSurfaceSettings.detailNormalStrength");
                WaterEditorUI.SubHeading("Foam seen from below");
                DrawFields(
                    "underwaterSurfaceSettings.foamSilhouetteDarken",
                    "underwaterSurfaceSettings.foamSunGlow");
                WaterEditorUI.SubHeading("Waterline (partial submersion)");
                DrawFields("underwaterSurfaceSettings.meniscus");
                DrawFieldsIf(Prop("underwaterSurfaceSettings.meniscus").boolValue,
                    "underwaterSurfaceSettings.meniscusWidthPixels",
                    "underwaterSurfaceSettings.meniscusStrength",
                    "underwaterSurfaceSettings.meniscusWarp");
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
                    "depthAttenuation.screenSpaceCaustics",
                    "depthAttenuation.screenCausticIntensity",
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
                        "bedDepthSettings.surfSmallWaveFoam",
                        "bedDepthSettings.surfFoamStrength",
                        "bedDepthSettings.surfFoamFeather",
                        "bedDepthSettings.surfFoamTileSize",
                        "bedDepthSettings.surfFoamColor");
                    WaterEditorUI.SubHeading("Crest foam pop curve");
                    DrawFields("bedDepthSettings.surfCrestFoamCurveEnabled");
                    DrawFieldsIf(Prop("bedDepthSettings.surfCrestFoamCurveEnabled").boolValue,
                        "bedDepthSettings.surfCrestFoamCurve",
                        "bedDepthSettings.surfCrestFoamGain");
                    // FOAM-4 crest cap: independent of the pop curve, so always shown.
                    DrawFields("bedDepthSettings.surfFoamCrestCap");
                    WaterEditorUI.SubHeading("Whitewash repartition");
                    DrawFields(
                        "bedDepthSettings.surfFoamBoreGain",
                        "bedDepthSettings.surfFoamTrailGain",
                        "bedDepthSettings.surfFoamTrailLength",
                        "bedDepthSettings.surfFoamTrailDissolve");
                    WaterEditorUI.SubHeading("Swash foam");
                    DrawFields(
                        "bedDepthSettings.surfSwashFoam",
                        "bedDepthSettings.surfSwashFoamWidth",
                        "bedDepthSettings.surfSwashFoamDissolve",
                        "bedDepthSettings.surfSwashStreak",
                        "bedDepthSettings.surfSwashDepositGain");
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
                    "foamSettings.foamFromCurvature",
                    "foamSettings.foamDeposit",
                    "foamSettings.foamBreakStrength",
                    "foamSettings.foamBreakRange",
                    "foamSettings.foamCrestBias",
                    "foamSettings.foamWakeStrength",
                    "foamSettings.foamWakeRadiusScale");
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
