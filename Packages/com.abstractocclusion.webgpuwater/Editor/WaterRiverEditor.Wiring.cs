// WebGpuWater - WaterRiver inspector: the WIRING tab.
// The parent link (the facade's own field), the references the facade WRITES on the siblings
// (drawn read-only: editing them would be overwritten on the next enable/validate), and the
// component set - required trio discovered, optional add-ons offered with an explicit Add.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        void DrawWiringTab()
        {
            _showParent = WaterEditorUI.Section("Parent Volume", _showParent, () =>
            {
                DrawFields(ParentVolumePath);
                if (River.ParentVolume == null)
                    EditorGUILayout.HelpBox(StandaloneHelp, MessageType.Info);
                else if (!River.ParentVolume.fullscreenVolumeFog)
                    EditorGUILayout.HelpBox(ParentFogOffWarning, MessageType.Warning);
            });
            _showManagedReferences = WaterEditorUI.Section(
                "Facade-managed References", _showManagedReferences, DrawManagedReferences);
            _showComponents = WaterEditorUI.Section("Components", _showComponents, DrawComponentRows);
        }

        // Read-only on purpose: WaterRiver.ApplyWiring assigns every one of these on enable and
        // validate, so a hand edit here would be a lie that lasts one tick.
        void DrawManagedReferences()
        {
            EditorGUILayout.HelpBox(ManagedReferencesHelp, MessageType.None);
            SerializedObject surface = SurfaceObject;
            if (surface != null)
            {
                surface.Update();
                WaterEditorUI.SubHeading("River Surface");
                DrawFieldsIf(false, surface, SplineReferencePath, SurfaceWaterVolumePath);
            }
            SerializedObject currentField = CurrentFieldObject;
            if (currentField != null)
            {
                currentField.Update();
                WaterEditorUI.SubHeading("River Current Field");
                DrawFieldsIf(false, currentField, SplineReferencePath, CurrentFieldFluidPath);
            }
        }

        void DrawComponentRows()
        {
            EditorGUILayout.HelpBox(ComponentsHelp, MessageType.None);
            WaterRiver river = River;
            WaterEditorUI.ComponentRow("River Spline", river.GetComponent<WaterRiverSpline>());
            WaterEditorUI.ComponentRow("River Current Field",
                                       river.GetComponent<WaterRiverCurrentField>());
            WaterEditorUI.ComponentRow("River Surface", river.GetComponent<WaterRiverSurface>());
            var fluid = river.GetComponent<WaterRiverFluid>();
            WaterEditorUI.ComponentRow(FluidComponentLabel, fluid,
                                       AddOptionalComponent<WaterRiverFluid>);
            // Foam's RequireComponent chain needs the fluid first.
            WaterEditorUI.ComponentRow(FoamComponentLabel, river.GetComponent<WaterRiverFoam>(),
                                       AddOptionalComponent<WaterRiverFoam>,
                                       addEnabled: fluid != null);
            WaterEditorUI.ComponentRow(DisturbanceComponentLabel,
                                       river.GetComponent<WaterRiverDisturbance>(),
                                       AddOptionalComponent<WaterRiverDisturbance>);
        }

        const string StandaloneHelp =
            "Standalone ribbon: gameplay queries work, but there are no animated waves and no " +
            "underwater fog until a parent volume is assigned. The edit-mode live preview only " +
            "ticks while a Water Volume exists in the scene.";
        const string ParentFogOffWarning =
            "The parent volume has Fullscreen Volume Fog disabled, so a camera inside this river " +
            "gets no underwater fog.";
        const string ManagedReferencesHelp =
            "Written by Water River on every enable and validate (the sibling spline, this " +
            "parent volume, the sibling fluid). Shown for diagnosis only.";
        const string ComponentsHelp =
            "The spline, current field and surface are required. Fluid, Foam and Disturbance are " +
            "optional add-ons; each has its own tab once added.";
    }
}
#endif
