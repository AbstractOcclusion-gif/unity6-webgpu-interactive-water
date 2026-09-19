// WebGpuWater - WaterRiver inspector: the DISTURBANCE ("Wakes") tab.
// Analytic wake / pile-up / impact displacement from WaterRiverInteractors riding the river.
// Optional: rivers nothing floats on never need it. The interactors themselves live on the
// floating objects and keep their default inspectors.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        // Disturbance property paths (single consumer: this tab).
        const string DisturbanceMaximumDisplacementPath = "maximumDisplacement";
        const string DisturbanceMinimumRelativeSpeedPath = "minimumRelativeSpeed";
        const string DisturbanceFullRelativeSpeedPath = "fullRelativeSpeed";
        const string DisturbanceWakeLengthScalePath = "wakeLengthScale";
        const string DisturbanceFoamStrengthPath = "foamStrength";
        const string DisturbanceNormalSampleDistancePath = "normalSampleDistance";
        const string DisturbanceMinimumSurfaceUpPath = "minimumSurfaceUp";
        const string DisturbanceFullSurfaceUpPath = "fullSurfaceUp";

        void DrawDisturbanceTab()
        {
            SerializedObject disturbance = DisturbanceObject;
            if (disturbance == null)
            {
                EditorGUILayout.HelpBox(NoDisturbanceHelp, MessageType.None);
                WaterEditorUI.ComponentRow(DisturbanceComponentLabel, null,
                                           AddOptionalComponent<WaterRiverDisturbance>);
                return;
            }

            disturbance.Update();
            _showDisturbance = WaterEditorUI.Section("Wakes & Impacts", _showDisturbance, () =>
            {
                EditorGUILayout.HelpBox(DisturbanceHelp, MessageType.None);
                DrawFields(disturbance, DisturbanceMaximumDisplacementPath,
                           DisturbanceMinimumRelativeSpeedPath, DisturbanceFullRelativeSpeedPath,
                           DisturbanceWakeLengthScalePath, DisturbanceFoamStrengthPath,
                           DisturbanceNormalSampleDistancePath, DisturbanceMinimumSurfaceUpPath,
                           DisturbanceFullSurfaceUpPath);
                WaterEditorUI.Readout("Interactors in range",
                    ((WaterRiverDisturbance)disturbance.targetObject).SourceCount.ToString());
            });
            disturbance.ApplyModifiedProperties();
        }

        const string DisturbanceComponentLabel = "River Disturbance";
        const string NoDisturbanceHelp =
            "No River Disturbance component: objects with a River Interactor leave no wake or " +
            "impact ring on this ribbon.";
        const string DisturbanceHelp =
            "Objects carrying a River Interactor (boats, crates) displace the ribbon with a " +
            "pile-up, a downstream wake and impact rings; the same sources feed gameplay queries.";
    }
}
#endif
