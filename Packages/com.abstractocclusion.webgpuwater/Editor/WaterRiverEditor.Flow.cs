// WebGpuWater - WaterRiver inspector: the FLOW tab.
// Where the gameplay current comes from (uniform knot speed or the obstacle bake) and how much
// water each end carries. The current field has no tuning of its own - its two references are
// facade-managed (Wiring tab) - so this tab is readouts plus the one status that matters:
// whether the parent samples this field at all.
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        void DrawFlowTab()
        {
            _showCurrent = WaterEditorUI.Section("Current", _showCurrent, () =>
            {
                EditorGUILayout.HelpBox(CurrentHelp, MessageType.None);
                DrawVelocitySourceReadout();
                DrawEndFlowReadout(WaterRiverEndKind.Source, "Source flow");
                DrawEndFlowReadout(WaterRiverEndKind.Mouth, "Mouth flow");
                DrawParentMembershipReadout();
            });
        }

        void DrawVelocitySourceReadout()
        {
            var fluid = River.GetComponent<WaterRiverFluid>();
            bool baked = fluid != null && fluid.isActiveAndEnabled &&
                         fluid.BakeData != null && fluid.BakeData.IsValid;
            WaterEditorUI.Readout("Velocity source",
                                  baked ? VelocityFromBake : VelocityFromKnots);
        }

        void DrawEndFlowReadout(WaterRiverEndKind endKind, string label)
        {
            if (!River.TryGetEndFrame(endKind, out _, out _, out float flowRate)) return;
            WaterEditorUI.Readout(label, string.Format(FlowRateFormat, flowRate));
        }

        // The parent samples river current near the seam only through its currentFields list.
        // The build kit writes that link serialized; the facade appends it on enable. Shown so a
        // missing link is visible instead of a silently still seam.
        void DrawParentMembershipReadout()
        {
            WaterVolume parent = River.ParentVolume;
            if (parent == null) return;
            var field = River.GetComponent<WaterRiverCurrentField>();
            WaterCurrentField[] fields = parent.currentFields ?? Array.Empty<WaterCurrentField>();
            bool listed = field != null && Array.IndexOf(fields, field) >= 0;
            WaterEditorUI.Readout(string.Format(ParentMembershipFormat, parent.name),
                                  listed ? YesLabel : NoLabel);
        }

        const string CurrentHelp =
            "The nearest spline tangent supplies full 3D flow direction, including waterfalls; " +
            "knot Speed sets its magnitude. A valid River Fluid bake replaces that uniform speed " +
            "with the same obstacle-deflected velocity the visible waves use. The parent volume " +
            "(and a connected mouth body) sample this field automatically.";
        const string VelocityFromBake = "River Fluid bake (obstacle-deflected)";
        const string VelocityFromKnots = "Uniform knot speed";
        const string FlowRateFormat = "{0:0.##} m³/s (width × speed)";
        const string ParentMembershipFormat = "Listed in {0}'s current fields";
        const string YesLabel = "yes";
        const string NoLabel = "no (links on enable)";
    }
}
#endif
