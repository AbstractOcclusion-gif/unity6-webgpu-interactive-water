// WebGpuWater - WaterRiver inspector: the PATH tab.
// The authored course: the knot list (position, tangent, width, speed) drawn through the
// spline's nested SerializedObject, the read-only figures derived from it, and the ribbon
// rebuild. Scene-view knot handles stay on WaterRiverSplineEditor (they target the spline).
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterRiverEditor
    {
        void DrawPathTab()
        {
            SerializedObject spline = SplineObject;
            if (spline == null)
            {
                EditorGUILayout.HelpBox(MissingSplineHelp, MessageType.Error);
                return;
            }

            spline.Update();
            _showCourse = WaterEditorUI.Section("Course", _showCourse, () =>
            {
                EditorGUILayout.HelpBox(SceneHandlesHelp, MessageType.None);
                DrawCourseReadouts((WaterRiverSpline)spline.targetObject);
                DrawKnotButtons((WaterRiverSpline)spline.targetObject);
                if (GUILayout.Button(RegenerateLabel)) RegenerateRibbon();
            });
            _showKnots = WaterEditorUI.Section("Knots", _showKnots, () =>
                DrawFields(spline, SplineKnotsPath));
            spline.ApplyModifiedProperties();
        }

        void DrawCourseReadouts(WaterRiverSpline spline)
        {
            WaterEditorUI.Readout("Knots", spline.KnotCount.ToString());
            if (spline.KnotCount < WaterRiverSpline.MinimumKnotCount) return;

            float minimumWidth = float.MaxValue, maximumWidth = 0f;
            float minimumSpeed = float.MaxValue, maximumSpeed = 0f;
            for (int i = 0; i < spline.KnotCount; i++)
            {
                WaterRiverKnot knot = spline.GetKnot(i);
                minimumWidth = Mathf.Min(minimumWidth, knot.Width);
                maximumWidth = Mathf.Max(maximumWidth, knot.Width);
                minimumSpeed = Mathf.Min(minimumSpeed, knot.Speed);
                maximumSpeed = Mathf.Max(maximumSpeed, knot.Speed);
            }
            WaterEditorUI.Readout("Width", string.Format(RangeMetersFormat, minimumWidth, maximumWidth));
            WaterEditorUI.Readout("Speed", string.Format(RangeSpeedFormat, minimumSpeed, maximumSpeed));

            WaterRiverSurface surface = River.Surface;
            int samplesPerSegment = surface != null
                ? surface.samplesPerSegment : WaterRiverSurface.DefaultSamplesPerSegment;
            if (WaterRiverFluidBaker.TryMeasureRiverLength(spline, samplesPerSegment,
                                                           out float riverLength))
                WaterEditorUI.Readout("Length", string.Format(MetersFormat, riverLength));
        }

        void DrawKnotButtons(WaterRiverSpline spline)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(WaterRiverSplineEditor.AddKnotLabel))
                    WaterRiverSplineEditor.AddKnotWithUndo(spline);
                using (new EditorGUI.DisabledScope(
                           spline.KnotCount <= WaterRiverSpline.MinimumKnotCount))
                    if (GUILayout.Button(WaterRiverSplineEditor.RemoveKnotLabel))
                        WaterRiverSplineEditor.RemoveLastKnotWithUndo(spline);
            }
        }

        // The mesh is DontSave and rebuilt from events; this is the repair path when an event
        // was missed (a rebuild error is logged by the surface itself).
        void RegenerateRibbon()
        {
            WaterRiverSurface surface = River.Surface;
            if (surface != null) surface.RequestRebuild();
            River.SyncGeneratedConnections();
        }

        const string MissingSplineHelp =
            "No River Spline on this object. Water River requires one - re-add the component.";
        const string SceneHandlesHelp =
            "Edit knots in the Scene view: blue spheres move knots, cyan spheres shape mirrored " +
            "tangents, yellow sliders set bank-to-bank width. Descending paths make waterfalls. " +
            "Ports and aprons follow knot edits automatically.";
        const string RegenerateLabel = "Regenerate Ribbon";
        const string RangeMetersFormat = "{0:0.##} – {1:0.##} m";
        const string RangeSpeedFormat = "{0:0.##} – {1:0.##} m/s";
        const string MetersFormat = "{0:0.##} m";
    }
}
#endif
