// WebGpuWater - explicit obstacle-aware river-fluid bake controls.
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverFluid))]
    [CanEditMultipleObjects]
    internal sealed class WaterRiverFluidEditor : UnityEditor.Editor
    {
        const string LateralResolutionPropertyName = "lateralResolution";
        const string LongitudinalResolutionPropertyName = "longitudinalResolution";
        const string IterationsPropertyName = "iterations";
        const string ObstacleLayersPropertyName = "obstacleLayers";
        const string ObstacleContactRadiusPropertyName = "obstacleContactRadius";
        const string DeltaTimePropertyName = "deltaTime";
        const string ViscosityPropertyName = "viscosity";
        const string PressurePropertyName = "pressure";
        const string FlowForcePropertyName = "flowForce";
        const string VelocityDecayPropertyName = "velocityDecay";
        const string VorticityPropertyName = "vorticity";
        const string FoamThresholdPropertyName = "foamThreshold";
        const string FoamStrengthPropertyName = "foamStrength";
        const string BakeButtonLabel = "Bake Settled Fluid";
        const string InspectorHelp =
            "Bakes a settled 2D fluid simulation in river-ribbon space. Spline Speed drives the " +
            "flow; colliders on Obstacle Layers become solids. The packed result supplies the " +
            "same obstacle-deflected velocity to visible waves and gameplay currents, plus foam.";
        const string MissingSplineWarning =
            "Assign a spline on River Surface before baking fluid.";
        const string NoObstacleLayersWarning =
            "Obstacle Layers is empty, so the bake cannot detect rocks or other solid colliders.";
        const string BakeFailureTitle = "River Fluid Bake Failed";
        const string BakeUndoName = "Bake River Fluid";
        const string BakeStatusFormat =
            "Baked {0} x {1}, river length {2:0.##} m, maximum speed {3:0.##} m/s";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(InspectorHelp, MessageType.Info);

            WaterEditorUI.SubHeading("Bake Grid");
            DrawProperty(LateralResolutionPropertyName);
            DrawProperty(LongitudinalResolutionPropertyName);
            DrawProperty(IterationsPropertyName);

            WaterEditorUI.SubHeading("Obstacle Rasterization");
            DrawProperty(ObstacleLayersPropertyName);
            DrawProperty(ObstacleContactRadiusPropertyName);

            WaterEditorUI.SubHeading("Fluid Solve");
            DrawProperty(DeltaTimePropertyName);
            DrawProperty(ViscosityPropertyName);
            DrawProperty(PressurePropertyName);
            DrawProperty(FlowForcePropertyName);
            DrawProperty(VelocityDecayPropertyName);
            DrawProperty(VorticityPropertyName);

            WaterEditorUI.SubHeading("Generated Foam");
            DrawProperty(FoamThresholdPropertyName);
            DrawProperty(FoamStrengthPropertyName);
            serializedObject.ApplyModifiedProperties();

            DrawWarningsAndStatus();
            using (new EditorGUI.DisabledScope(targets.Length != 1 || !CanBake()))
            {
                if (GUILayout.Button(BakeButtonLabel)) BakeTarget();
            }
        }

        void DrawProperty(string propertyName)
            => EditorGUILayout.PropertyField(serializedObject.FindProperty(propertyName));

        void DrawWarningsAndStatus()
        {
            if (targets.Length != 1) return;
            var fluid = (WaterRiverFluid)target;
            if (fluid.Spline == null)
                EditorGUILayout.HelpBox(MissingSplineWarning, MessageType.Warning);
            if (fluid.obstacleLayers.value == 0)
                EditorGUILayout.HelpBox(NoObstacleLayersWarning, MessageType.Warning);
            WaterRiverFluidBakeData data = fluid.BakeData;
            if (data == null || !data.IsValid) return;
            EditorGUILayout.LabelField(
                string.Format(BakeStatusFormat,
                              data.LateralResolution, data.LongitudinalResolution,
                              data.RiverLength, data.MaximumSpeed),
                EditorStyles.miniLabel);
        }

        bool CanBake()
            => target is WaterRiverFluid fluid && fluid.Spline != null;

        void BakeTarget()
        {
            var fluid = (WaterRiverFluid)target;
            try
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName(BakeUndoName);
                WaterRiverFluidBaker.Bake(fluid);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, fluid);
                EditorUtility.DisplayDialog(BakeFailureTitle, exception.Message, "OK");
            }
        }
    }
}
#endif
