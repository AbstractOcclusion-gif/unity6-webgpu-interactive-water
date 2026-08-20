// WebGpuWater - WaterVolume inspector: the SURFACE tab.
// The film itself: its textures, what it reflects and refracts, how it reads from below, and its
// foam. Light travelling THROUGH the water is the Volume tab; what moves the surface is Motion.
//
// FOAM is one section here, but four independent engines. They were previously split across three
// tabs, which made them impossible to compare; they are now side by side WITHOUT being wired
// together - each sub-block keeps its own enable gate and its own applicability greying, and no
// knob is shared between families.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // One dedicated section for every author-time SURFACE texture slot + its tweaks. The foam pattern
        // and ocean whitecap used to be reachable only on the water material; they live on the body now, so
        // all surface textures are configured from one place. Empty foam/whitecap slots keep the material's.
        void DrawTexturesSection()
        {
            _showTextures = WaterEditorUI.Section("Textures", _showTextures, () =>
            {
                // Greyed on the REAL gate (a pool renderer exists), not on body type: a bounded body
                // with no analytic pool ignores this texture just as much as an ocean does.
                WaterEditorUI.SubHeading("Pool tiles (analytic pool only)");
                DrawFieldsIf(HasProceduralPool, "tiles");
                EditorGUILayout.HelpBox(TilesHelp, MessageType.Info);

                WaterEditorUI.SubHeading("Sky (reflection base)");
                DrawFields("sky");
                EditorGUILayout.HelpBox(SkyHelp, MessageType.Info);

                // Crest-style crossing scrolling detail normals: off (flat) until a tiling
                // water-normal texture is assigned; the sliders shape the layer once it is.
                bool hasDetailNormal =
                    Prop(WaterVolumePropertyPaths.DetailNormalTexture).objectReferenceValue != null;
                WaterEditorUI.SubHeading("Detail normals (micro ripples)");
                DrawFields(WaterVolumePropertyPaths.DetailNormalTexture);
                DrawFieldsIf(hasDetailNormal,
                    "detailNormalSettings.strength",
                    "detailNormalSettings.windResponse",
                    "detailNormalSettings.crestBoost",
                    "detailNormalSettings.distanceBoost",
                    "detailNormalSettings.hexTiling");

                WaterEditorUI.SubHeading("Surface foam pattern");
                EditorGUILayout.HelpBox("Empty keeps the water material's own foam texture. Assign here to " +
                    "drive it from this body; the flipbook grid/rate and relief apply once a texture is set.",
                    MessageType.None);
                DrawFields("foamPatternTexture");

                WaterEditorUI.SubHeading("Ocean whitecap");
                DrawFields("oceanWhitecapTexture");

                WaterEditorUI.SubHeading("Foam relief (foam pattern + whitecap)");
                DrawFields("foamReliefStrength");

                // The flipbook grid/rate DESCRIBE the assigned sheet rather than tune the look, and
                // the detail-normal tiling/scroll are set once with the texture.
                _showTexturesAdvanced = WaterEditorUI.SubSection("Advanced", _showTexturesAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Detail normal layout");
                    // Both ends of the octave ladder, together: the tile climbs from the near size
                    // toward the far one with view distance and stops there. Judging one without the
                    // other is guesswork, so they are never drawn apart.
                    DrawFieldsIf(hasDetailNormal,
                        "detailNormalSettings.tileMeters",
                        "detailNormalSettings.farTileMeters",
                        "detailNormalSettings.farTileDistance",
                        "detailNormalSettings.scrollSpeed",
                        "detailNormalSettings.farScrollSpeed");
                    // What deep-water dispersion says the far speed should be for the authored tile
                    // step. Printed rather than enforced: past the tile cap the far water's screen
                    // motion falls off as 1/distance, so outrunning dispersion is often the readable
                    // choice - but it should be a decision, not an accident.
                    if (hasDetailNormal && target is WaterVolume detailVolume)
                        EditorGUILayout.LabelField(" ",
                            $"Dispersion-correct far speed: {detailVolume.DetailNormalDispersionFarSpeed:0.##} m/s",
                            EditorStyles.miniLabel);
                    WaterEditorUI.SubHeading("Foam pattern flipbook");
                    DrawFieldsIf(Prop("foamPatternTexture").objectReferenceValue != null,
                        "foamPatternGrid", "foamPatternFps");
                    WaterEditorUI.SubHeading("Whitecap flipbook");
                    DrawFieldsIf(Prop("oceanWhitecapTexture").objectReferenceValue != null,
                        "oceanWhitecapGrid", "oceanWhitecapFps");
                });
            });
        }

        void DrawReflectionsSection()
        {
            _showReflections = WaterEditorUI.Section("Reflections", _showReflections, () =>
            {
                DrawFields(
                    WaterVolumePropertyPaths.ScreenSpaceReflection,
                    WaterVolumePropertyPaths.PlanarReflection,
                    "reflectionSettings.reflectUrpProbe");
                DrawFieldsIf(Prop("reflectionSettings.reflectUrpProbe").boolValue,
                    "reflectionSettings.reflectionProbe");
                // Greyed unless planar is on: the culling mask and the crop depth both belong to the
                // planar mirror and do nothing to SSR or the environment base.
                DrawFieldsIf(Prop(WaterVolumePropertyPaths.PlanarReflection).boolValue,
                    "reflectionSettings.planarExcludeLayers",
                    "reflectionSettings.planarClipDepth",
                    "reflectionSettings.planarResolutionScale",
                    "reflectionSettings.planarUpdateInterval",
                    "reflectionSettings.planarRenderShadows",
                    "reflectionSettings.planarFarClipDistance");

                // Refraction gets its own heading rather than one line buried in the SSR foldout,
                // where nobody looking for "how do I tune refraction" would ever find it. The two
                // knobs are mutually exclusive by construction - Real Refraction selects the path,
                // and only that path's knob does anything - so each is greyed on its dead side.
                WaterEditorUI.SubHeading("Refraction");
                DrawFields(WaterVolumePropertyPaths.RealRefraction);
                bool realRefraction = Prop(WaterVolumePropertyPaths.RealRefraction).boolValue;
                DrawFieldsIf(!realRefraction, "reflectionSettings.refractionStrength");
                DrawFieldsIf(realRefraction, "reflectionSettings.refractionDistortion");

                WaterEditorUI.SubHeading("Underwater shadows");
                DrawFields("refractShadows");
                if (Prop("refractShadows").boolValue)
                    DrawFields("refractShadowSoftness", "refractShadowLayers");
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
                    "reflectionSettings.reflectSunlight");
                DrawFieldsIf(Prop("reflectionSettings.reflectSunlight").boolValue,
                    "reflectionSettings.sunReflectionIntensity");
                DrawFields(
                    "reflectionSettings.reflectionDistortion",
                    "reflectionSettings.sunRoughness",
                    "reflectionSettings.ssrStrength");

                // Three refinement families, each a second-order shaping of a knob above. The five
                // fields left visible are the ones that answer "more/less reflective, sharper/softer".
                _showReflectFresnel = WaterEditorUI.SubSection("Advanced · Fresnel", _showReflectFresnel, () =>
                    DrawFields(
                        "reflectionSettings.fresnelFloor",
                        "reflectionSettings.fresnelPower"));

                _showReflectRoughness = WaterEditorUI.SubSection("Advanced · Roughness ramp + sun lobe",
                    _showReflectRoughness, () =>
                    DrawFields(
                        "reflectionSettings.roughnessFar",
                        "reflectionSettings.roughnessFarDistance",
                        "reflectionSettings.roughnessFalloff",
                        "reflectionSettings.reflectionAnisoStretch",
                        "reflectionSettings.sunSheen",
                        "reflectionSettings.sunSheenRoughness",
                        "reflectionSettings.sunGrazeBoost"));

                _showReflectScreenSpace = WaterEditorUI.SubSection("Advanced · Screen-space tracing",
                    _showReflectScreenSpace, () =>
                    DrawFields(
                        "reflectionSettings.ssrStepSize",
                        "reflectionSettings.ssrMaxSteps",
                        "reflectionSettings.ssrThickness"));
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
                bool physicalFresnel = Prop("underwaterSurfaceSettings.physicalFresnel").boolValue;
                bool meniscus = Prop("underwaterSurfaceSettings.meniscus").boolValue;
                DrawFields(
                    "underwaterSurfaceSettings.physicalFresnel",
                    "underwaterSurfaceSettings.reflectionStrength",
                    "underwaterSurfaceSettings.mirrorWaterBlend",
                    "underwaterSurfaceSettings.mirrorShafts");
                WaterEditorUI.SubHeading("Foam seen from below");
                DrawFields(
                    "underwaterSurfaceSettings.foamSilhouetteDarken",
                    "underwaterSurfaceSettings.foamSunGlow");
                WaterEditorUI.SubHeading("Waterline (partial submersion)");
                DrawFields("underwaterSurfaceSettings.meniscus");
                DrawFieldsIf(meniscus, "underwaterSurfaceSettings.meniscusStrength");

                _showUnderwaterAdvanced = WaterEditorUI.SubSection("Advanced", _showUnderwaterAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Snell window edge");
                    DrawFieldsIf(physicalFresnel,
                        "underwaterSurfaceSettings.tirEdgeSoftness",
                        "underwaterSurfaceSettings.fresnelFloor");
                    WaterEditorUI.SubHeading("Detail + meniscus shaping");
                    DrawFields("underwaterSurfaceSettings.detailNormalStrength");
                    DrawFieldsIf(meniscus,
                        "underwaterSurfaceSettings.meniscusWidthPixels",
                        "underwaterSurfaceSettings.meniscusWarp");
                });
            });
        }

        // ---- foam: one section, three independent engines -------------------------------------
        void DrawFoamSection()
        {
            _showFoam = WaterEditorUI.Section("Foam", _showFoam, () =>
            {
                EditorGUILayout.HelpBox(FoamFamiliesHelp, MessageType.None);
                DrawFoamTurbulenceBlock();
                DrawFoamWhitecapsBlock();
                DrawFoamShoreBlock();
            });
        }

        // Engine 1: the interactive sim's turbulence foam (advect + generate + decay), with the
        // shading of its mask nested inside. The mask and the look of that mask are ONE engine;
        // side by side at the same level they read as two, and the shading block's fields are
        // dead without the mask above them.
        void DrawFoamTurbulenceBlock()
        {
            _showFoamTurbulence = WaterEditorUI.SubSection("Turbulence (generation & decay)",
                _showFoamTurbulence, () =>
            {
                SerializedProperty foamEnabled = Prop("foamSettings.foam");
                EditorGUILayout.PropertyField(foamEnabled, true);
                DrawFieldsIf(foamEnabled.boolValue,
                    WaterVolumePropertyPaths.FoamGenRate,
                    "foamSettings.foamDecay",
                    "foamSettings.foamSpread",
                    "foamSettings.foamAdvect");

                // Look before deep tuning: colour and pattern are reached for far more often than
                // the generation-source knobs, so Shading sits above Advanced.
                DrawFoamShadingBlock();

                // Everything below tunes WHICH water generates foam rather than how much or how
                // long it lasts - the four above are the ones reached for first.
                _showFoamTurbulenceAdvanced = WaterEditorUI.SubSection("Advanced",
                    _showFoamTurbulenceAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Generation sources");
                    DrawFields(
                        "foamSettings.foamGenThreshold",
                        "foamSettings.foamMinWaveHeight",
                        "foamSettings.foamFromSpeed",
                        "foamSettings.foamFromCurvature",
                        "foamSettings.foamCrestBias",
                        "foamSettings.foamHeadroom");
                    WaterEditorUI.SubHeading("Breaking + deposit");
                    DrawFields(
                        "foamSettings.foamDeposit",
                        "foamSettings.foamBreakStrength",
                        "foamSettings.foamBreakRange");
                    WaterEditorUI.SubHeading("Wake stamp");
                    DrawFields(
                        "foamSettings.foamWakeStrength",
                        "foamSettings.foamWakeRadiusScale");
                    WaterEditorUI.SubHeading("Decay shaping");
                    DrawFields(
                        "foamSettings.foamDecayResidual",
                        "foamSettings.foamDecayRate");
                },
                contentEnabled: foamEnabled.boolValue);
            });
        }

        // Wetness is its OWN section, not a foam sub-block. It only STORES its mark in the foam
        // buffer; conceptually it is "how long does ground stay wet", which is a different question
        // from "where is there foam" - and burying the one control that lets it run without foam
        // inside the foam section is how a feature gets reported as broken.
        //
        // ONE CLOCK: Dry Time drives the sim's wet mark AND the surf swash wet line. Before, the beach
        // receded on the wave period while the ground beside it dried on this slider, and the two
        // disagreed wherever they met.
        void DrawWetnessSection()
        {
            _showWetness = WaterEditorUI.Section("Wetness", _showWetness, () =>
            {
                EditorGUILayout.HelpBox(
                    "How long ground stays wet after the water leaves. Dry Time drives BOTH the " +
                    "ripple sim's wet mark and the beach swash line, so terrain, props and sand all " +
                    "dry together.\n\n" +
                    "Per-surface strength lives on the MATERIAL: raise Wetness on a WaterReceiver or " +
                    "WaterTerrain material. Wetness Memory keeps the sim pass running when Foam is " +
                    "off - with Foam on, the mark is maintained for free.", MessageType.None);
                DrawFields(
                    "foamSettings.wetnessMemory",
                    "foamSettings.wetnessDryTime");
            });
        }

        // Engine 2: ocean whitecaps, driven by the FFT wave field. Ocean-only, own colour + tiling
        // (deliberately NOT shared with the turbulence shading nested in Engine 1 above - different
        // look, different source, and merging them would silently retune every existing ocean).
        void DrawFoamWhitecapsBlock()
        {
            _showFoamWhitecaps = WaterEditorUI.SubSection("Whitecaps (ocean)", _showFoamWhitecaps, () =>
                DrawFields(
                    "ocean.oceanFoamWindThreshold",
                    "ocean.oceanFoamCoverage",
                    "ocean.oceanFoamStrength",
                    "ocean.oceanFoamFadeRate",
                    "ocean.oceanFoamColor",
                    "ocean.oceanFoamTileSize",
                    "ocean.oceanFoamFeather",
                    "ocean.oceanFoamStreakStretch",
                    "ocean.oceanFoamCrestAnisotropy",
                    "ocean.oceanFoamCrestGate",
                    "ocean.oceanFoamFaceBias",
                    "ocean.oceanFoamTextureInfluence",
                    "ocean.oceanFoamDepthTint",
                    "ocean.oceanFoamCascadeMix",
                    "ocean.oceanFoamDeposit",
                    "ocean.oceanFoamDrift",
                    "ocean.oceanFoamMaxBuildup"),
                contentEnabled: IsOcean);
        }

        // Engine 3: whitewash + swash foam laid down by the surf fronts (Motion tab).
        void DrawFoamShoreBlock()
        {
            _showFoamShore = WaterEditorUI.SubSection("Shore & Swash", _showFoamShore, () =>
            {
                WaterEditorUI.SubHeading("Breaker foam");
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
                    "bedDepthSettings.surfSwashDepositGain");
            },
            contentEnabled: UsesBedDepth && Prop(WaterVolumePropertyPaths.SurfEnabled).boolValue);
        }

        // How the turbulence foam mask is SHADED. Nested inside block 1 because the mask it
        // shades is generated there - the title needs no qualifier at that depth.
        void DrawFoamShadingBlock()
        {
            bool externalFoamUsesLook = target is WaterVolume volume &&
                                        volume.HasLiveExternalFoamRenderer;
            _showFoamShading = WaterEditorUI.SubSection("Shading", _showFoamShading, () =>
            {
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
            },
            contentEnabled: Prop("foamSettings.foam").boolValue || externalFoamUsesLook);
        }

        const string TilesHelp =
            "LEGACY analytic-pool path. This albedo is sampled by the pool walls/floor and by the " +
            "surface's own trace into the pool - it is what the water reflects and refracts on a " +
            "procedural pool. A body with no Pool Renderer (Body tab) never reads it: open water " +
            "refracts the real scene instead.";
        const string SkyHelp =
            "Reflection BASE only - SSR and Planar Reflection layer on top of it. With Reflect URP " +
            "Probe on, an explicitly assigned Reflection Probe is used first. If it is empty or not " +
            "ready, a Skybox/Cubemap scene sky is used; panoramic, 6-sided and procedural skyboxes " +
            "cannot provide a cube directly, so this slot remains the final fallback.";
        const string FoamFamiliesHelp =
            "Three independent foam engines, grouped so they can be compared - not merged. Each has " +
            "its own switch and its own source: the sim's turbulence, the ocean's FFT crests, and " +
            "the surf fronts' whitewash. How the turbulence mask is SHADED lives inside its own " +
            "engine; the ocean and the surf each carry their own colour and tiling on purpose.";
    }
}
#endif
