// WebGpuWater - WaterRiver inspector: the FLUID tab.
// The settled obstacle-aware fluid bake: the asset and whether it is still current, the grid
// and solve knobs, and the Bake button (WaterRiverFluidBaker does the work). Without a
// WaterRiverFluid component the tab offers to add one; it is optional because most rivers
// never need obstacle-deflected flow.
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        // Fluid property paths (single consumer: this tab).
        const string FluidBakeDataPath = "bakeData";
        const string FluidLateralResolutionPath = "lateralResolution";
        const string FluidLongitudinalResolutionPath = "longitudinalResolution";
        const string FluidIterationsPath = "iterations";
        const string FluidObstacleLayersPath = "obstacleLayers";
        const string FluidObstacleContactRadiusPath = "obstacleContactRadius";
        const string FluidDeltaTimePath = "deltaTime";
        const string FluidViscosityPath = "viscosity";
        const string FluidPressurePath = "pressure";
        const string FluidFlowForcePath = "flowForce";
        const string FluidVelocityDecayPath = "velocityDecay";
        const string FluidVorticityPath = "vorticity";
        const string FluidFoamThresholdPath = "foamThreshold";
        const string FluidFoamStrengthPath = "foamStrength";
        const string FluidObstacleFoamTrailLengthPath = "obstacleFoamTrailLengthMeters";
        const string FluidBankFoamStrengthPath = "bankFoamStrength";

        // The staleness check measures the spline with the bake's own sampling, so the two
        // lengths agree to float noise when nothing moved; anything beyond this is a real edit.
        const float BakeLengthStaleToleranceFraction = 0.001f;

        void DrawFluidTab()
        {
            SerializedObject fluid = FluidObject;
            if (fluid == null)
            {
                EditorGUILayout.HelpBox(NoFluidHelp, MessageType.None);
                WaterEditorUI.ComponentRow(FluidComponentLabel, null,
                                           AddOptionalComponent<WaterRiverFluid>);
                return;
            }

            var fluidComponent = (WaterRiverFluid)fluid.targetObject;
            fluid.Update();
            _showBakeAsset = WaterEditorUI.Section("Bake Asset", _showBakeAsset, () =>
            {
                EditorGUILayout.HelpBox(FluidHelp, MessageType.None);
                DrawFields(fluid, FluidBakeDataPath);
                DrawBakeStatus(fluidComponent);
                DrawBakeWarnings(fluidComponent);
                using (new EditorGUI.DisabledScope(fluidComponent.Spline == null))
                    if (GUILayout.Button(BakeButtonLabel)) BakeFluid(fluidComponent);
            });
            _showBakeGrid = WaterEditorUI.Section("Bake Grid", _showBakeGrid, () =>
                DrawFields(fluid, FluidLateralResolutionPath, FluidLongitudinalResolutionPath,
                           FluidIterationsPath));
            _showObstacles = WaterEditorUI.Section("Obstacle Rasterization", _showObstacles, () =>
                DrawFields(fluid, FluidObstacleLayersPath, FluidObstacleContactRadiusPath));
            _showSolve = WaterEditorUI.Section("Fluid Solve", _showSolve, () =>
                DrawFields(fluid, FluidDeltaTimePath, FluidViscosityPath, FluidPressurePath,
                           FluidFlowForcePath, FluidVelocityDecayPath, FluidVorticityPath));
            _showBakedFoam = WaterEditorUI.Section("Generated Foam", _showBakedFoam, () =>
                DrawFields(fluid, FluidFoamThresholdPath, FluidFoamStrengthPath,
                           FluidObstacleFoamTrailLengthPath, FluidBankFoamStrengthPath));
            fluid.ApplyModifiedProperties();
        }

        void DrawBakeStatus(WaterRiverFluid fluid)
        {
            WaterRiverFluidBakeData data = fluid.BakeData;
            if (data == null || !data.IsValid)
            {
                WaterEditorUI.Readout("Bake", NoBakeReadout);
                return;
            }
            WaterEditorUI.Readout("Bake", string.Format(
                BakeStatusFormat, data.LateralResolution, data.LongitudinalResolution,
                data.RiverLength, data.MaximumSpeed));
            string staleReason = DescribeBakeStaleness(fluid, data);
            if (staleReason != null)
                EditorGUILayout.HelpBox(string.Format(StaleBakeFormat, staleReason),
                                        MessageType.Warning);
        }

        // Null when the asset still matches the settings and the spline it was baked from.
        static string DescribeBakeStaleness(WaterRiverFluid fluid, WaterRiverFluidBakeData data)
        {
            if (data.LateralResolution != fluid.lateralResolution ||
                data.LongitudinalResolution != fluid.longitudinalResolution)
                return StaleResolutionReason;
            if (!WaterRiverFluidBaker.TryMeasureRiverLength(
                    fluid.Spline, fluid.SamplesPerSegment, out float currentLength))
                return null;
            float drift = Mathf.Abs(currentLength - data.RiverLength);
            return drift > data.RiverLength * BakeLengthStaleToleranceFraction
                ? string.Format(StaleLengthReasonFormat, data.RiverLength, currentLength)
                : null;
        }

        static void DrawBakeWarnings(WaterRiverFluid fluid)
        {
            if (fluid.Spline == null)
                EditorGUILayout.HelpBox(MissingSplineForBakeWarning, MessageType.Warning);
            if (fluid.obstacleLayers.value == 0)
                EditorGUILayout.HelpBox(NoObstacleLayersWarning, MessageType.Warning);
        }

        static void BakeFluid(WaterRiverFluid fluid)
        {
            try
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName(BakeUndoName);
                WaterRiverFluidBaker.Bake(fluid);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, fluid);
                EditorUtility.DisplayDialog(BakeFailureTitle, exception.Message, DialogOkLabel);
            }
        }

        const string FluidComponentLabel = "River Fluid";
        const string NoFluidHelp =
            "No River Fluid component: the current field uses the uniform knot speed. Add one " +
            "to bake obstacle-deflected flow (and to enable River Foam); only obstacle " +
            "turbulence requires an actual bake.";
        const string FluidHelp =
            "Bakes a settled 2D fluid simulation in river-ribbon space. Knot Speed drives the " +
            "flow; colliders on Obstacle Layers become solids. The packed result supplies the " +
            "same obstacle-deflected velocity to visible waves and gameplay currents, plus foam.";
        const string MissingSplineForBakeWarning =
            "The surface has no spline, so there is nothing to bake (see the Wiring tab).";
        const string NoObstacleLayersWarning =
            "Obstacle Layers is empty, so the bake cannot detect rocks or other solid colliders.";
        const string BakeButtonLabel = "Bake Settled Fluid";
        const string BakeUndoName = "Bake River Fluid";
        const string BakeFailureTitle = "River Fluid Bake Failed";
        const string DialogOkLabel = "OK";
        const string BakeStatusFormat =
            "{0} x {1}, river length {2:0.##} m, maximum speed {3:0.##} m/s";
        const string NoBakeReadout = "none - contact and cascade foam still work";
        const string StaleBakeFormat = "The bake is stale: {0}. Re-bake to match.";
        const string StaleResolutionReason = "the grid resolution changed since it was baked";
        const string StaleLengthReasonFormat =
            "the spline changed since it was baked ({0:0.##} m then, {1:0.##} m now)";
    }
}
#endif
