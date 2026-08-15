// WebGpuWater - focused Scene authoring for WaterRiverSpline.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverSpline))]
    public sealed class WaterRiverSplineEditor : UnityEditor.Editor
    {
        const string KnotsPropertyName = "knots";
        const string LocalPositionPropertyName = "localPosition";
        const string LocalTangentPropertyName = "localTangent";
        const string WidthPropertyName = "width";
        const string SpeedPropertyName = "speed";
        const string AddKnotLabel = "Add Knot";
        const string RemoveKnotLabel = "Remove Last";
        const string AddKnotUndoName = "Add River Knot";
        const string RemoveKnotUndoName = "Remove River Knot";
        const string InspectorHelp =
            "Blue spheres move knots, cyan spheres shape mirrored tangents, and yellow sliders edit " +
            "bank-to-bank width. The spline supports descending 3D paths for waterfalls.";
        const int GizmoSamplesPerSegment = 12;
        const float KnotHandleSizeFactor = 0.07f;
        const float TangentHandleSizeFactor = 0.055f;
        const float WidthHandleSizeFactor = 0.08f;
        const float MinimumHandleSize = 0.02f;
        const float HalfWidth = 0.5f;
        const float FullWidth = 2f;
        const float CurveThickness = 3f;
        const string SpeedLabelFormat = "{0:0.##} m/s";

        static readonly Color SelectedCurveColor = new Color(0.2f, 0.8f, 1f, 1f);
        static readonly Color IdleCurveColor = new Color(0.2f, 0.65f, 0.9f, 0.35f);
        static readonly Color KnotColor = new Color(0.15f, 0.65f, 1f, 1f);
        static readonly Color TangentColor = new Color(0.2f, 1f, 0.95f, 1f);
        static readonly Color WidthColor = new Color(1f, 0.8f, 0.2f, 1f);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox(InspectorHelp, MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(KnotsPropertyName), true);
            serializedObject.ApplyModifiedProperties();

            var spline = (WaterRiverSpline)target;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(AddKnotLabel))
            {
                Undo.RecordObject(spline, AddKnotUndoName);
                spline.AddKnot();
                EditorUtility.SetDirty(spline);
            }
            EditorGUI.BeginDisabledGroup(spline.KnotCount <= WaterRiverSpline.MinimumKnotCount);
            if (GUILayout.Button(RemoveKnotLabel))
            {
                Undo.RecordObject(spline, RemoveKnotUndoName);
                spline.RemoveLastKnot();
                EditorUtility.SetDirty(spline);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }

        void OnSceneGUI()
        {
            var spline = (WaterRiverSpline)target;
            serializedObject.Update();
            SerializedProperty knots = serializedObject.FindProperty(KnotsPropertyName);
            if (knots == null || knots.arraySize < WaterRiverSpline.MinimumKnotCount) return;

            DrawBezierSpans(spline, knots);
            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < knots.arraySize; i++)
                EditKnot(spline, knots.GetArrayElementAtIndex(i));
            if (EditorGUI.EndChangeCheck()) serializedObject.ApplyModifiedProperties();
        }

        static void DrawBezierSpans(WaterRiverSpline spline, SerializedProperty knots)
        {
            Handles.color = SelectedCurveColor;
            for (int i = 0; i < knots.arraySize - 1; i++)
            {
                SerializedProperty start = knots.GetArrayElementAtIndex(i);
                SerializedProperty end = knots.GetArrayElementAtIndex(i + 1);
                Vector3 startPosition = spline.LocalPointToWorld(
                    start.FindPropertyRelative(LocalPositionPropertyName).vector3Value);
                Vector3 endPosition = spline.LocalPointToWorld(
                    end.FindPropertyRelative(LocalPositionPropertyName).vector3Value);
                Vector3 startControl = startPosition + spline.LocalDirectionToWorld(
                    start.FindPropertyRelative(LocalTangentPropertyName).vector3Value);
                Vector3 endControl = endPosition - spline.LocalDirectionToWorld(
                    end.FindPropertyRelative(LocalTangentPropertyName).vector3Value);
                Handles.DrawBezier(startPosition, endPosition, startControl, endControl,
                    SelectedCurveColor, null, CurveThickness);
            }
        }

        static void EditKnot(WaterRiverSpline spline, SerializedProperty knot)
        {
            SerializedProperty positionProperty = knot.FindPropertyRelative(LocalPositionPropertyName);
            SerializedProperty tangentProperty = knot.FindPropertyRelative(LocalTangentPropertyName);
            SerializedProperty widthProperty = knot.FindPropertyRelative(WidthPropertyName);
            SerializedProperty speedProperty = knot.FindPropertyRelative(SpeedPropertyName);

            Vector3 worldPosition = spline.LocalPointToWorld(positionProperty.vector3Value);
            float viewSize = HandleUtility.GetHandleSize(worldPosition);
            float knotHandleSize = Mathf.Max(MinimumHandleSize, viewSize * KnotHandleSizeFactor);
            Handles.color = KnotColor;
            Vector3 movedPosition = Handles.FreeMoveHandle(
                worldPosition, knotHandleSize, Vector3.zero, Handles.SphereHandleCap);
            if (movedPosition != worldPosition)
            {
                positionProperty.vector3Value = spline.WorldPointToLocal(movedPosition);
                worldPosition = movedPosition;
            }

            EditTangent(spline, worldPosition, viewSize, tangentProperty);
            EditWidth(spline, worldPosition, viewSize, tangentProperty, widthProperty);
            Handles.Label(worldPosition, string.Format(SpeedLabelFormat, speedProperty.floatValue));
        }

        static void EditTangent(WaterRiverSpline spline, Vector3 worldPosition, float viewSize,
                                SerializedProperty tangentProperty)
        {
            Vector3 worldTangent = spline.LocalDirectionToWorld(tangentProperty.vector3Value);
            Vector3 outgoingHandle = worldPosition + worldTangent;
            Vector3 incomingHandle = worldPosition - worldTangent;
            Handles.color = TangentColor;
            Handles.DrawLine(incomingHandle, outgoingHandle);
            float handleSize = Mathf.Max(MinimumHandleSize, viewSize * TangentHandleSizeFactor);
            Vector3 movedHandle = Handles.FreeMoveHandle(
                outgoingHandle, handleSize, Vector3.zero, Handles.SphereHandleCap);
            if (movedHandle != outgoingHandle)
                tangentProperty.vector3Value = spline.WorldDirectionToLocal(movedHandle - worldPosition);
        }

        static void EditWidth(WaterRiverSpline spline, Vector3 worldPosition, float viewSize,
                              SerializedProperty tangentProperty, SerializedProperty widthProperty)
        {
            Vector3 tangent = spline.LocalDirectionToWorld(tangentProperty.vector3Value);
            Vector3 right = WaterRiverSplineEvaluator.CalculateRight(
                tangent.normalized, spline.transform.rotation * Vector3.forward);
            float halfWidth = Mathf.Max(WaterRiverSpline.MinimumWidth, widthProperty.floatValue) * HalfWidth;
            Vector3 rightBank = worldPosition + right * halfWidth;
            Vector3 leftBank = worldPosition - right * halfWidth;
            Handles.color = WidthColor;
            Handles.DrawLine(leftBank, rightBank);
            float handleSize = Mathf.Max(MinimumHandleSize, viewSize * WidthHandleSizeFactor);
            Vector3 movedBank = Handles.Slider(
                rightBank, right, handleSize, Handles.CubeHandleCap, 0f);
            float movedHalfWidth = Vector3.Dot(movedBank - worldPosition, right);
            widthProperty.floatValue = Mathf.Max(
                WaterRiverSpline.MinimumWidth, movedHalfWidth * FullWidth);
        }

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        static void DrawSplineGizmo(WaterRiverSpline spline, GizmoType gizmoType)
        {
            bool selected = (gizmoType & GizmoType.Selected) != 0;
            Gizmos.color = selected ? SelectedCurveColor : IdleCurveColor;
            for (int segmentIndex = 0; segmentIndex < spline.SegmentCount; segmentIndex++)
            {
                if (!spline.TryEvaluateSegment(segmentIndex, 0f, out WaterRiverSplineSample previous))
                    continue;
                Vector3 previousLeft = previous.Position - previous.Right * (previous.Width * HalfWidth);
                Vector3 previousRight = previous.Position + previous.Right * (previous.Width * HalfWidth);
                for (int step = 1; step <= GizmoSamplesPerSegment; step++)
                {
                    float segmentT = step / (float)GizmoSamplesPerSegment;
                    if (!spline.TryEvaluateSegment(
                            segmentIndex, segmentT, out WaterRiverSplineSample current))
                        continue;
                    Vector3 currentLeft = current.Position - current.Right * (current.Width * HalfWidth);
                    Vector3 currentRight = current.Position + current.Right * (current.Width * HalfWidth);
                    Gizmos.DrawLine(previous.Position, current.Position);
                    Gizmos.DrawLine(previousLeft, currentLeft);
                    Gizmos.DrawLine(previousRight, currentRight);
                    previous = current;
                    previousLeft = currentLeft;
                    previousRight = currentRight;
                }
            }
        }
    }
}
#endif
