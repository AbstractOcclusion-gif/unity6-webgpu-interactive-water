// WebGpuWater - WaterRiver inspector: the FOAM tab.
// Contact, cascade (slope whitewater) and baked-turbulence foam composition plus its look.
// WaterRiverFoam is optional and requires the fluid component (its RequireComponent chain);
// contact and cascade sources work before any bake exists.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        // Foam property paths (single consumer: this tab).
        const string FoamOverallStrengthPath = "overallStrength";
        const string FoamBakedStrengthPath = "strength";
        const string FoamContactStrengthPath = "contactStrength";
        const string FoamContactDepthPath = "contactDepth";
        const string FoamCascadeStrengthPath = "cascadeStrength";
        const string FoamCascadeStartAnglePath = "cascadeStartAngle";
        const string FoamCascadeFullAnglePath = "cascadeFullAngle";
        const string FoamCascadePersistencePath = "cascadePersistenceMeters";
        const string FoamPatternSizePath = "patternSize";
        const string FoamEdgeFeatherPath = "edgeFeather";
        const string FoamCoreCutPath = "coreCut";

        void DrawFoamTab()
        {
            SerializedObject foam = FoamObject;
            if (foam == null)
            {
                bool hasFluid = River.GetComponent<WaterRiverFluid>() != null;
                EditorGUILayout.HelpBox(hasFluid ? NoFoamHelp : FoamNeedsFluidHelp,
                                        MessageType.None);
                WaterEditorUI.ComponentRow(FoamComponentLabel, null,
                                           AddOptionalComponent<WaterRiverFoam>,
                                           addEnabled: hasFluid);
                return;
            }

            foam.Update();
            _showFoamSources = WaterEditorUI.Section("Sources", _showFoamSources, () =>
            {
                EditorGUILayout.HelpBox(FoamHelp, MessageType.None);
                DrawFields(foam, FoamOverallStrengthPath, FoamContactStrengthPath,
                           FoamContactDepthPath, FoamCascadeStrengthPath,
                           FoamCascadeStartAnglePath, FoamCascadeFullAnglePath,
                           FoamCascadePersistencePath);
                // The field is named for its shader slot; the label says what it multiplies.
                EditorGUILayout.PropertyField(Prop(foam, FoamBakedStrengthPath),
                                              BakedTurbulenceStrengthLabel);
                DrawFoamWarnings();
            });
            _showFoamAppearance = WaterEditorUI.Section("Appearance", _showFoamAppearance, () =>
                DrawFields(foam, FoamPatternSizePath, FoamEdgeFeatherPath, FoamCoreCutPath));
            foam.ApplyModifiedProperties();
        }

        void DrawFoamWarnings()
        {
            var fluid = River.GetComponent<WaterRiverFluid>();
            if (fluid == null || fluid.BakeData == null || !fluid.BakeData.IsValid)
                EditorGUILayout.HelpBox(FoamWithoutBakeHelp, MessageType.Info);
            if (River.ParentVolume == null)
                EditorGUILayout.HelpBox(FoamWithoutVolumeWarning, MessageType.Warning);
        }

        static readonly GUIContent BakedTurbulenceStrengthLabel = new GUIContent(
            "Baked Turbulence Strength",
            "Multiplier on the foam coverage stored in the River Fluid bake.");

        const string FoamComponentLabel = "River Foam";
        const string NoFoamHelp = "No River Foam component: the ribbon renders without foam.";
        const string FoamNeedsFluidHelp =
            "River Foam requires the River Fluid component (Fluid tab) - add that first. A " +
            "completed bake is not required for contact and cascade foam.";
        const string FoamHelp =
            "Combines depth-contact foam, slope-driven whitewater and baked fluid turbulence. " +
            "The parent volume supplies the established foam texture and colour.";
        const string FoamWithoutBakeHelp =
            "No valid fluid bake: contact and whitewater remain active, but obstacle wakes and " +
            "turbulence need Bake Settled Fluid (Fluid tab).";
        const string FoamWithoutVolumeWarning =
            "No parent volume (Wiring tab): foam has no established water look to inherit and " +
            "does not join the after-fog overlay.";
    }
}
#endif
