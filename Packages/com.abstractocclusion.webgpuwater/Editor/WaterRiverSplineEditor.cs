// WebGpuWater - Scene-view authoring for WaterRiverSpline: knot / tangent / width handles and
// the bank gizmo. The inspector body is a stub - knots are listed and edited on the Water River
// inspector's Path tab, which also calls the knot add/remove helpers below.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverSpline))]
    internal sealed class WaterRiverSplineEditor : UnityEditor.Editor
    {
        const string KnotsPropertyName = "knots";
        const string LocalPositionPropertyName = "localPosition";
        const string LocalTangentPropertyName = "localTangent";
        const string WidthPropertyName = "width";
        const string SpeedPropertyName = "speed";
        internal const string AddKnotLabel = "Add Knot";
        internal const string RemoveKnotLabel = "Remove Last";
        const string AddKnotUndoName = "Add River Knot";
        const string RemoveKnotUndoName = "Remove River Knot";
        const string ComponentLabel = "River Spline";
        const int GizmoSamplesPerSegment = 12;
        const float KnotHandleSizeFactor = 0.07f;
        const float TangentHandleSizeFactor = 0.055f;
        const float WidthHandleSizeFactor = 0.08f;
        const float MinimumHandleSize = 0.02f;
        const float FullWidth = 2f;
        const float CurveThickness = 3f;
        const string SpeedLabelFormat = "{0:0.##} m/s";

        static readonly Color SelectedCurveColor = new Color(0.2f, 0.8f, 1f, 1f);
        static readonly Color IdleCurveColor = new Color(0.2f, 0.65f, 0.9f, 0.35f);
        static readonly Color KnotColor = new Color(0.15f, 0.65f, 1f, 1f);
        static readonly Color TangentColor = new Color(0.2f, 1f, 0.95f, 1f);
        static readonly Color WidthColor = new Color(1f, 0.8f, 0.2f, 1f);

        public override void OnInspectorGUI()
            => WaterRiverEditor.DrawSubComponentStub(
                (Component)target, WaterRiverEditor.InspectorTab.Path, ComponentLabel);

        internal static void AddKnotWithUndo(WaterRiverSpline spline)
        {
            Undo.RecordObject(spline, AddKnotUndoName);
            spline.AddKnot();
            EditorUtility.SetDirty(spline);
        }

        internal static void RemoveLastKnotWithUndo(WaterRiverSpline spline)
        {
            Undo.RecordObject(spline, RemoveKnotUndoName);
            spline.RemoveLastKnot();
            EditorUtility.SetDirty(spline);
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
            float halfWidth = Mathf.Max(WaterRiverSpline.MinimumWidth, widthProperty.floatValue) *
                             WaterRiverSpline.HalfWidthFraction;
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
                Vector3 previousLeft = previous.Position - previous.Right * previous.HalfWidth;
                Vector3 previousRight = previous.Position + previous.Right * previous.HalfWidth;
                for (int step = 1; step <= GizmoSamplesPerSegment; step++)
                {
                    float segmentT = step / (float)GizmoSamplesPerSegment;
                    if (!spline.TryEvaluateSegment(
                            segmentIndex, segmentT, out WaterRiverSplineSample current))
                        continue;
                    Vector3 currentLeft = current.Position - current.Right * current.HalfWidth;
                    Vector3 currentRight = current.Position + current.Right * current.HalfWidth;
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
