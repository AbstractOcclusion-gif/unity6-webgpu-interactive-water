// WebGpuWater - WaterVolume custom inspector (orchestration).
// Draws the cyan header, the body-type + water-colour presets, the category tab bar, and the footer.
// The scene-view gizmos/handles live in WaterVolumeEditor.cs; the per-section drawing lives in one
// partial PER TAB (Body / Motion / Surface / Volume / Interaction / Budget), plus Chunk and Jerlov
// which are single sections large enough to own their file. Editor-only.
//
// TABS ARE NAMED FOR WHAT THEY DO, NOT FOR A FEATURE. A field belongs to the tab whose charter
// covers it, even when the feature it serves is shown elsewhere - that is what stops the drift this
// layout replaced (a caustic RESOLUTION filed under waves, ocean knobs scattered over four tabs,
// foam split three ways). The charters, in one line each:
//   Body        - what the body IS and where: extent, renderers, chunk, bed floor, wiring, camera.
//   Motion      - every source of surface height, ordered by scale.
//   Surface     - the film itself: textures, reflections, the underside, foam.
//   Volume      - what light does THROUGH the water: fog, scatter, caustics, shafts, depth colour.
//   Interaction - what the world does to the water: obstacles, splashes, FX components.
//   Budget      - anything that trades frame time for fidelity, wherever that fidelity shows.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Foldout state. Only the blocks a user reaches for first start open; the rest stay collapsed
        // so the inspector opens compact. Persisted through SessionState (see OnEnable/OnDisable):
        // per-instance fields reset on every selection change, which re-collapsed whatever section
        // the user was working in each time they clicked away.
        // Every "Advanced" fold below holds the second-order knobs of the section above it: solver
        // and numerical parameters, refinements of a primary knob, and wizard-set-once values. They
        // all default closed - the visible fields are the ones that answer "make it more like X".
        bool _showTopology = true;
        bool _showPlacement = true;
        bool _showBody = true;
        bool _showBodyAdvanced = false;
        bool _showBedSource = false;
        bool _showChunk = false;
        bool _showWiring = false;
        bool _showCamera = false;

        bool _showRipple = false;
        bool _showRippleAdvanced = false;
        bool _showWakeSafety = false;
        bool _showCurrent = true;
        bool _showWind = true;
        bool _showWindWaves = false;
        bool _showWindWavesAdvanced = false;
        bool _showOceanSwell = false;
        bool _showSwell = false;
        bool _showSurfaceCurrent = false;
        bool _showSeaState = false;
        bool _showOceanAperiodic = false;
        bool _showWindFetch = false;
        bool _showOceanSwellAdvanced = false;
        bool _showSurf = false;
        bool _showSurfAdvanced = false;

        bool _showTextures = true;
        bool _showTexturesAdvanced = false;
        bool _showReflections = false;
        bool _showReflectFresnel = false;
        bool _showReflectRoughness = false;
        bool _showReflectScreenSpace = false;
        bool _showUnderwaterSurface = false;
        bool _showUnderwaterAdvanced = false;
        bool _showFoam = false;
        bool _showFoamTurbulence = true;
        bool _showFoamTurbulenceAdvanced = false;
        bool _showFoamWhitecaps = false;
        bool _showFoamShore = false;
        bool _showFoamShading = false;
        bool _showWetness = false;

        bool _showWaterFog = false;
        bool _showScatter = false;
        bool _showScatterAdvanced = false;
        bool _showCrestGlow = true;
        bool _showDepth = false;
        bool _showCaustics = false;
        bool _showCausticsAdvanced = false;
        bool _showGodRays = false;
        bool _showGodRaysAdvanced = false;
        bool _showBedColour = false;
        bool _showBedColourAdvanced = false;
        bool _showHorizonHaze = false;

        bool _showObjectInteraction = false;
        bool _showObjectInteractionAdvanced = false;
        bool _showPointerInteraction = true;
        bool _showSplash = false;

        bool _showQuality = false;
        bool _showQualityAdvanced = false;
        bool _showWindow = false;
        bool _showWindowAdvanced = false;
        bool _showClipmap = false;
        bool _showClipmapAdvanced = false;

        const string FoldoutKeyPrefix = "WebGpuWater.WaterVolumeEditor.";
        const string TabSessionKey = FoldoutKeyPrefix + "_tab";

        void OnEnable()
        {
            SyncFoldouts(load: true);
            _tab = (InspectorTab)SessionState.GetInt(TabSessionKey, (int)_tab);
        }

        void OnDisable()
        {
            SyncFoldouts(load: false);
            SessionState.SetInt(TabSessionKey, (int)_tab);
        }

        // ONE list drives both directions, so a new foldout can never be persisted in only one
        // of load/save. The field initializers above remain the first-session defaults.
        void SyncFoldouts(bool load)
        {
            Sync(ref _showTopology, nameof(_showTopology), load);
            Sync(ref _showPlacement, nameof(_showPlacement), load);
            Sync(ref _showBody, nameof(_showBody), load);
            Sync(ref _showBodyAdvanced, nameof(_showBodyAdvanced), load);
            Sync(ref _showBedSource, nameof(_showBedSource), load);
            Sync(ref _showChunk, nameof(_showChunk), load);
            // _showWiring is DELIBERATELY not persisted: it is the one foldout that turns on
            // RequiresConstantRepaint (the live sun-driven lightDir readout), and a SessionState
            // -persisted "open" latched EVERY later WaterVolume inspector into continuous repaint
            // until editor restart - SessionState survives selection changes and domain reloads.
            // It now opens closed on each selection; live repaint runs only while it is open.
            Sync(ref _showCamera, nameof(_showCamera), load);
            Sync(ref _showPointerInteraction, nameof(_showPointerInteraction), load);

            Sync(ref _showRipple, nameof(_showRipple), load);
            Sync(ref _showRippleAdvanced, nameof(_showRippleAdvanced), load);
            Sync(ref _showWakeSafety, nameof(_showWakeSafety), load);
            Sync(ref _showCurrent, nameof(_showCurrent), load);
            Sync(ref _showWind, nameof(_showWind), load);
            Sync(ref _showWindWaves, nameof(_showWindWaves), load);
            Sync(ref _showWindWavesAdvanced, nameof(_showWindWavesAdvanced), load);
            Sync(ref _showOceanSwell, nameof(_showOceanSwell), load);
            Sync(ref _showSwell, nameof(_showSwell), load);
            Sync(ref _showSurfaceCurrent, nameof(_showSurfaceCurrent), load);
            Sync(ref _showSeaState, nameof(_showSeaState), load);
            Sync(ref _showOceanAperiodic, nameof(_showOceanAperiodic), load);
            Sync(ref _showWindFetch, nameof(_showWindFetch), load);
            Sync(ref _showOceanSwellAdvanced, nameof(_showOceanSwellAdvanced), load);
            Sync(ref _showSurf, nameof(_showSurf), load);
            Sync(ref _showSurfAdvanced, nameof(_showSurfAdvanced), load);

            Sync(ref _showTextures, nameof(_showTextures), load);
            Sync(ref _showTexturesAdvanced, nameof(_showTexturesAdvanced), load);
            Sync(ref _showReflections, nameof(_showReflections), load);
            Sync(ref _showReflectFresnel, nameof(_showReflectFresnel), load);
            Sync(ref _showReflectRoughness, nameof(_showReflectRoughness), load);
            Sync(ref _showReflectScreenSpace, nameof(_showReflectScreenSpace), load);
            Sync(ref _showUnderwaterSurface, nameof(_showUnderwaterSurface), load);
            Sync(ref _showUnderwaterAdvanced, nameof(_showUnderwaterAdvanced), load);
            Sync(ref _showFoam, nameof(_showFoam), load);
            Sync(ref _showFoamTurbulence, nameof(_showFoamTurbulence), load);
            Sync(ref _showFoamTurbulenceAdvanced, nameof(_showFoamTurbulenceAdvanced), load);
            Sync(ref _showFoamWhitecaps, nameof(_showFoamWhitecaps), load);
            Sync(ref _showFoamShore, nameof(_showFoamShore), load);
            Sync(ref _showFoamShading, nameof(_showFoamShading), load);
            Sync(ref _showWetness, nameof(_showWetness), load);

            Sync(ref _showWaterFog, nameof(_showWaterFog), load);
            Sync(ref _showScatter, nameof(_showScatter), load);
            Sync(ref _showScatterAdvanced, nameof(_showScatterAdvanced), load);
            Sync(ref _showCrestGlow, nameof(_showCrestGlow), load);
            Sync(ref _showDepth, nameof(_showDepth), load);
            Sync(ref _showCaustics, nameof(_showCaustics), load);
            Sync(ref _showCausticsAdvanced, nameof(_showCausticsAdvanced), load);
            Sync(ref _showGodRays, nameof(_showGodRays), load);
            Sync(ref _showGodRaysAdvanced, nameof(_showGodRaysAdvanced), load);
            Sync(ref _showBedColour, nameof(_showBedColour), load);
            Sync(ref _showBedColourAdvanced, nameof(_showBedColourAdvanced), load);
            Sync(ref _showHorizonHaze, nameof(_showHorizonHaze), load);

            Sync(ref _showObjectInteraction, nameof(_showObjectInteraction), load);
            Sync(ref _showObjectInteractionAdvanced, nameof(_showObjectInteractionAdvanced), load);
            Sync(ref _showPresets, nameof(_showPresets), load);
            Sync(ref _showSplash, nameof(_showSplash), load);

            Sync(ref _showQuality, nameof(_showQuality), load);
            Sync(ref _showQualityAdvanced, nameof(_showQualityAdvanced), load);
            Sync(ref _showWindow, nameof(_showWindow), load);
            Sync(ref _showWindowAdvanced, nameof(_showWindowAdvanced), load);
            Sync(ref _showClipmap, nameof(_showClipmap), load);
            Sync(ref _showClipmapAdvanced, nameof(_showClipmapAdvanced), load);
        }

        static void Sync(ref bool value, string key, bool load)
        {
            if (load) value = SessionState.GetBool(FoldoutKeyPrefix + key, value);
            else SessionState.SetBool(FoldoutKeyPrefix + key, value);
        }

        // The sun-driven lightDir is shown read-only; repaint live only while the Wiring section is
        // VISIBLE (its tab active + open) AND a sun drives it, so the greyed value tracks the sun
        // instead of showing a stale vector - and idle inspectors pay no continuous-repaint cost.
        public override bool RequiresConstantRepaint() =>
            _tab == InspectorTab.Body && _showWiring && HasSun;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            WaterEditorUI.DrawHeader(InspectorTitle, BodySubtitle());

            // Body type selector + one-click defaults for the chosen archetype (advisory).
            WaterEditorUI.BodyTypeSelector(Prop(WaterVolumePropertyPaths.BodyType));
            if (GUILayout.Button("Apply " + CurrentType + " defaults"))
                ApplyBodyTypeDefaults(CurrentType);

            // Physically-based Jerlov water colour: writes Fog Extinction + body/scatter colour.
            DrawJerlovWaterTypeSelector();

            // Look presets: capture this body's look, or apply a saved one (per its domain flags).
            DrawPresetSection();

            _tab = (InspectorTab)WaterEditorUI.TabBar((int)_tab, TabLabels);
            switch (_tab)
            {
                case InspectorTab.Body:
                    DrawTopologySection();
                    DrawPlacementSection();
                    DrawBodySection();
                    DrawBedSourceSection();
                    DrawChunkSection();
                    DrawWiringSection();
                    DrawCameraSection();
                    break;

                case InspectorTab.Motion:
                    DrawMotionGlobals();
                    DrawCurrentSection();
                    DrawWindSection();
                    DrawRippleSection();
                    DrawOceanSwellSection();
                    DrawWindWavesSection();
                    DrawSurfFrontsSection();
                    break;

                case InspectorTab.Surface:
                    DrawTexturesSection();
                    DrawReflectionsSection();
                    DrawUnderwaterSurfaceSection();
                    DrawFoamSection();
                    DrawWetnessSection();
                    break;

                case InspectorTab.Volume:
                    DrawWaterFogSection();
                    DrawVolumeScatterSection();
                    DrawDepthAttenuationSection();
                    DrawCausticsSection();
                    DrawGodRaysSection();
                    DrawBedColourSection();
                    DrawHorizonHazeSection();
                    break;

                case InspectorTab.Interaction:
                    DrawPointerInteractionSection();
                    DrawObjectInteractionSection();
                    DrawSplashSection();
                    break;

                case InspectorTab.Budget:
                    DrawQualitySection();
                    DrawWindowSection();
                    DrawClipmapSection();
                    break;
            }

            WaterEditorUI.DrawFooter();

            serializedObject.ApplyModifiedProperties();
        }

        // Category tabs, ordered as a user's journey: make it exist, make it move, make it look
        // right at the surface, then through the water, then let the world hit it, then pay for it.
        enum InspectorTab { Body, Motion, Surface, Volume, Interaction, Budget }

        // "Interact" rather than "Interaction": GUILayout.Toolbar splits its width evenly and clips
        // the longest label first, which a narrow inspector would do to this one.
        static readonly string[] TabLabels =
            { "Body", "Motion", "Surface", "Volume", "Interact", "Budget" };

        InspectorTab _tab = InspectorTab.Body;

        // Shorthand for a serialized property by path; nested Settings blocks use dotted paths
        // (e.g. "ocean.openWater"). Kept single-sourced so no section invents a raw string twice.
        SerializedProperty Prop(string path) => serializedObject.FindProperty(path);

        // True when a directional light is wired into the body's sun slot (which then auto-drives lightDir).
        bool HasSun => Prop(WaterVolumePropertyPaths.Sun).objectReferenceValue != null;

        // Draws every named property field of a nested block, honouring its [Range]/[Min]/[Tooltip]
        // attributes automatically (PropertyField reads them), so this editor holds no range literals.
        void DrawFields(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
                EditorGUILayout.PropertyField(Prop(paths[i]), true);
        }

        // ---- applicability (advisory) --------------------------------------------------------
        // The bodyType enum drives which sections are relevant; sections grey their body when a
        // feature doesn't apply to the chosen archetype. Advisory only - it never changes runtime
        // behaviour by itself (the functional flags still gate the actual paths).
        WaterVolume.WaterBodyType CurrentType =>
            (WaterVolume.WaterBodyType)Prop(WaterVolumePropertyPaths.BodyType).enumValueIndex;
        bool IsOcean => CurrentType == WaterVolume.WaterBodyType.Ocean;
        bool LakeOrOcean => CurrentType != WaterVolume.WaterBodyType.Pond;
        bool Bounded => CurrentType != WaterVolume.WaterBodyType.Ocean; // pond + lake have real walls / finite volume

        // The bed terrain is authored in the Body tab; everything derived from it (surf motion, deep
        // colour + clarity, bake resolution) greys out on this one flag from its own tab.
        bool UsesBedDepth => Prop(WaterVolumePropertyPaths.UseBedDepth).boolValue;

        // True when this body draws the analytic pool, i.e. when a Pool Renderer is wired. The pool
        // tile albedo is only ever sampled on that path, so it greys on this rather than on bodyType.
        bool HasProceduralPool => target is WaterVolume volume && volume.HasProceduralPool;

        // Draw the given fields greyed unless the applicability condition holds (fine-grained, in-section).
        void DrawFieldsIf(bool enabled, params string[] paths)
        {
            EditorGUI.BeginDisabledGroup(!enabled);
            DrawFields(paths);
            EditorGUI.EndDisabledGroup();
        }

        // "Apply {type} defaults": set the functional flags that make the chosen archetype behave as
        // expected. Explicit (button-driven) so selecting a type never silently clobbers tuning.
        void ApplyBodyTypeDefaults(WaterVolume.WaterBodyType type)
        {
            bool openWater = type != WaterVolume.WaterBodyType.Pond;
            Prop(WaterVolumePropertyPaths.OpenWater).boolValue = openWater;
            Prop(WaterVolumePropertyPaths.UnboundedOcean).boolValue = type == WaterVolume.WaterBodyType.Ocean;
            Prop(WaterVolumePropertyPaths.EnableLargeBodyWindow).boolValue = openWater;
        }

        string BodySubtitle()
        {
            var volume = (WaterVolume)target;
            return volume.IsPrimary ? SubtitlePrimary : SubtitleSecondary;
        }

        const string InspectorTitle = "WATER VOLUME";
        const string SubtitlePrimary = "Primary body  —  drives global water state";
        const string SubtitleSecondary = "Secondary body";
    }
}
#endif
