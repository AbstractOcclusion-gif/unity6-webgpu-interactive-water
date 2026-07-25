// WebGL Water - WaterVolume custom inspector (orchestration).
// Draws the cyan header, every feature section in a readable top-down order, and the footer.
// The scene-view gizmos/handles live in WaterVolumeEditor.cs; the per-section drawing lives in
// the WaterVolumeEditor.Setup/Dynamics/Ocean/Appearance partials. Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Foldout state. Only the placement/look/body blocks a user reaches for first start open;
        // the rest stay collapsed so the inspector opens compact. Persisted through SessionState
        // (see OnEnable/OnDisable): per-instance fields reset on every selection change, which
        // re-collapsed whatever section the user was working in each time they clicked away.
        bool _showWiring = false;
        bool _showLook = true;
        bool _showPlacement = true;
        bool _showBody = true;
        bool _showChunk = false;
        bool _showPerformance = false;
        bool _showReflections = false;
        bool _showUnderwaterSurface = false;
        bool _showSimulation = false;
        bool _showRipple = false;
        bool _showWindWaves = false;
        bool _showWindow = false;
        bool _showOceanOpenWater = false;
        bool _showOceanClipmap = false;
        bool _showOceanGodRays = false;
        bool _showOceanFoam = false;
        bool _showObjectInteraction = false;
        bool _showWaterFog = false;
        bool _showScatter = false;
        bool _showDepth = false;
        bool _showBedDepth = false;
        bool _showFoam = false;
        bool _showCrestGlow = true;
        bool _showSurfAdvanced = false;
        bool _showCamera = false;
        bool _showSplash = false;

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
            Sync(ref _showWiring, nameof(_showWiring), load);
            Sync(ref _showLook, nameof(_showLook), load);
            Sync(ref _showPlacement, nameof(_showPlacement), load);
            Sync(ref _showBody, nameof(_showBody), load);
            Sync(ref _showChunk, nameof(_showChunk), load);
            Sync(ref _showPerformance, nameof(_showPerformance), load);
            Sync(ref _showReflections, nameof(_showReflections), load);
            Sync(ref _showUnderwaterSurface, nameof(_showUnderwaterSurface), load);
            Sync(ref _showSimulation, nameof(_showSimulation), load);
            Sync(ref _showRipple, nameof(_showRipple), load);
            Sync(ref _showWindWaves, nameof(_showWindWaves), load);
            Sync(ref _showWindow, nameof(_showWindow), load);
            Sync(ref _showOceanOpenWater, nameof(_showOceanOpenWater), load);
            Sync(ref _showOceanClipmap, nameof(_showOceanClipmap), load);
            Sync(ref _showOceanGodRays, nameof(_showOceanGodRays), load);
            Sync(ref _showOceanFoam, nameof(_showOceanFoam), load);
            Sync(ref _showObjectInteraction, nameof(_showObjectInteraction), load);
            Sync(ref _showWaterFog, nameof(_showWaterFog), load);
            Sync(ref _showScatter, nameof(_showScatter), load);
            Sync(ref _showDepth, nameof(_showDepth), load);
            Sync(ref _showBedDepth, nameof(_showBedDepth), load);
            Sync(ref _showFoam, nameof(_showFoam), load);
            Sync(ref _showCrestGlow, nameof(_showCrestGlow), load);
            Sync(ref _showSurfAdvanced, nameof(_showSurfAdvanced), load);
            Sync(ref _showCamera, nameof(_showCamera), load);
            Sync(ref _showSplash, nameof(_showSplash), load);
        }

        static void Sync(ref bool value, string key, bool load)
        {
            if (load) value = SessionState.GetBool(FoldoutKeyPrefix + key, value);
            else SessionState.SetBool(FoldoutKeyPrefix + key, value);
        }

        // The sun-driven lightDir is shown read-only; repaint live only while the Simulation section
        // is VISIBLE (its tab active + open) AND a sun drives it, so the greyed value tracks the sun
        // instead of showing a stale vector - and idle inspectors pay no continuous-repaint cost.
        public override bool RequiresConstantRepaint() =>
            _tab == InspectorTab.WavesWind && _showSimulation && HasSun;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            WaterEditorUI.DrawHeader(InspectorTitle, BodySubtitle());

            // Body type selector + one-click defaults for the chosen archetype (advisory).
            WaterEditorUI.BodyTypeSelector(Prop("bodyType"));
            if (GUILayout.Button("Apply " + CurrentType + " defaults"))
                ApplyBodyTypeDefaults(CurrentType);

            // Physically-based Jerlov water colour: writes Fog Extinction + body/scatter colour.
            DrawJerlovWaterTypeSelector();

            // Top-level category tabs: one category's sections render at a time instead of the
            // former flat scroll of 20+ foldouts. The mapping mirrors the runtime partial split
            // (Settings / Look / Waves / Shore / Performance), which is what makes it honest.
            _tab = (InspectorTab)WaterEditorUI.TabBar((int)_tab, TabLabels);
            switch (_tab)
            {
                case InspectorTab.Core:
                    // Make it exist + hook it up: placement, wiring, cameras, interaction plumbing.
                    DrawPlacementSection();
                    DrawBodySection();
                    DrawChunkSection();
                    DrawWiringSection();
                    DrawObjectInteractionSection();
                    DrawCameraSection();
                    DrawSplashSection();
                    break;

                case InspectorTab.Look:
                    // Everything that changes pixels but not motion.
                    DrawLookSection();
                    DrawReflectionsSection();
                    DrawUnderwaterSurfaceSection();
                    DrawWaterFogSection();
                    DrawVolumeScatterSection();
                    DrawDepthAttenuationSection();
                    DrawOceanGodRaysSection();
                    DrawFoamSection();
                    break;

                case InspectorTab.WavesWind:
                    // Everything that moves the surface.
                    DrawSimulationSection();
                    DrawRippleSection();
                    DrawWindWavesSection();
                    DrawOceanOpenWaterSection();
                    DrawOceanFoamSection();
                    break;

                case InspectorTab.ShoreSurf:
                    // The coastline family (bed depth carries the shoal/surf/swash UI).
                    DrawBedDepthSection();
                    break;

                case InspectorTab.Performance:
                    // The budget levers: quality tier, sim window, horizon clipmap.
                    DrawPerformanceSection();
                    DrawWindowSection();
                    DrawOceanClipmapSection();
                    break;
            }

            WaterEditorUI.DrawFooter();

            serializedObject.ApplyModifiedProperties();
        }

        // Category tabs. Order = a user's journey: create/wire it, make it pretty, make it move,
        // shape the coast, then pay for it.
        enum InspectorTab { Core, Look, WavesWind, ShoreSurf, Performance }

        static readonly string[] TabLabels = { "Core", "Look", "Waves & Wind", "Shore & Surf", "Performance" };

        InspectorTab _tab = InspectorTab.Core;

        // Shorthand for a serialized property by path; nested Settings blocks use dotted paths
        // (e.g. "ocean.openWater"). Kept single-sourced so no section invents a raw string twice.
        SerializedProperty Prop(string path) => serializedObject.FindProperty(path);

        // True when a directional light is wired into the body's sun slot (which then auto-drives lightDir).
        bool HasSun => Prop("sun").objectReferenceValue != null;

        // Draws every named property field of a nested block, honouring its [Range]/[Min]/[Tooltip]
        // attributes automatically (PropertyField reads them), so this editor holds no range literals.
        void DrawFields(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
                EditorGUILayout.PropertyField(Prop(paths[i]), true);
        }

        // ---- body-type applicability (advisory) --------------------------------------------
        // The bodyType enum drives which sections are relevant; sections grey their body when a
        // feature doesn't apply to the chosen archetype. Advisory only - it never changes runtime
        // behaviour by itself (the functional flags still gate the actual paths).
        WaterVolume.WaterBodyType CurrentType => (WaterVolume.WaterBodyType)Prop("bodyType").enumValueIndex;
        bool IsOcean => CurrentType == WaterVolume.WaterBodyType.Ocean;
        bool LakeOrOcean => CurrentType != WaterVolume.WaterBodyType.Pond;
        bool Bounded => CurrentType != WaterVolume.WaterBodyType.Ocean; // pond + lake have real walls / finite volume

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
