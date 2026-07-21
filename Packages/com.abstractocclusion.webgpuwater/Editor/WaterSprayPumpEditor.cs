// WebGpuWater - inspector for WaterSprayPump: an "Auto-place probes" tool that populates the probe
// array from the object's own geometry, plus scene gizmos so the points are visible and nudge-able.
//
// Two layouts:
//   - Bow row : a line of probes across the forward face (chosen axis), at the resting waterline - for
//               spray in front of a boat. Stamped Boat mode.
//   - Rock ring: probes around the footprint silhouette at the waterline - for a rock/pier that sprays
//               where waves hit it. Stamped Rock mode.
//
// Edit-time only: the waterline comes from the resolved WaterVolume's rest plane (its transform Y, the
// same plane the obstacle/caustics code treats as the surface), so no play mode or GPU readback is needed.
// Points are written through SerializedProperty, so Undo and prefab overrides work for free.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterSprayPump))]
    internal sealed class WaterSprayPumpEditor : UnityEditor.Editor
    {
        enum Layout { BowRow, RockRing }
        enum ForwardAxis { PlusX, MinusX, PlusZ, MinusZ }

        const int DefaultProbeCount = 7;
        const int MinProbeCount = 1;
        const int MaxProbeCount = 128;
        const float FallbackHalfExtent = 0.5f;      // when the object has neither collider nor renderer
        const float MinHorizontalAxisLength = 1e-4f; // guard degenerate forward directions
        const float GizmoRadiusFraction = 0.03f;     // probe gizmo size as a fraction of the object's size
        const float GizmoRadiusFloor = 0.02f;

        static readonly Color BoatColor = new Color(0.4f, 0.8f, 1.0f);
        static readonly Color RockColor = new Color(1.0f, 0.75f, 0.35f);
        static readonly Color BothColor = new Color(0.7f, 1.0f, 0.6f);

        // Placement controls (editor-session state; reset when the inspector is rebuilt).
        Layout _layout = Layout.BowRow;
        ForwardAxis _bowAxis = ForwardAxis.PlusZ;
        int _probeCount = DefaultProbeCount;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Auto-place probes", EditorStyles.boldLabel);

            _layout = (Layout)EditorGUILayout.EnumPopup("Layout", _layout);
            if (_layout == Layout.BowRow)
                _bowAxis = (ForwardAxis)EditorGUILayout.EnumPopup("Bow axis (forward)", _bowAxis);
            _probeCount = EditorGUILayout.IntSlider("Probe count", _probeCount, MinProbeCount, MaxProbeCount);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(_layout == Layout.BowRow ? "Place bow row" : "Place rock ring"))
                Place((WaterSprayPump)target);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                _layout == Layout.BowRow
                    ? "Lays a row across the forward face at the waterline (Boat mode). Set the axis to your hull's bow."
                    : "Rings the footprint at the waterline (Rock mode). Raise the count for a smoother ring.",
                MessageType.None);
        }

        void Place(WaterSprayPump pump)
        {
            int count = Mathf.Clamp(_probeCount, MinProbeCount, MaxProbeCount);
            Transform t = pump.transform;
            float waterY = ResolveWaterY(t.position);
            Bounds bounds = ResolveWorldBounds(pump);

            WaterSprayMode mode = _layout == Layout.BowRow ? WaterSprayMode.Boat : WaterSprayMode.Rock;

            var localOffsets = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 world = _layout == Layout.BowRow
                    ? BowRowPoint(t, bounds, waterY, i, count)
                    : RockRingPoint(bounds, waterY, i, count);
                localOffsets[i] = t.InverseTransformPoint(world);
            }

            WriteProbes(localOffsets, mode);
        }

        // A row across the forward face: centre + forward*halfDepth + right*(halfWidth * -1..1), at the waterline.
        Vector3 BowRowPoint(Transform t, Bounds bounds, float waterY, int index, int count)
        {
            Vector3 forward = HorizontalAxis(t, _bowAxis);
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            float halfDepth = SupportRadius(forward, bounds.extents);
            float halfWidth = SupportRadius(right, bounds.extents);
            float across = count == 1 ? 0f : Mathf.Lerp(-1f, 1f, index / (float)(count - 1));

            Vector3 point = bounds.center + forward * halfDepth + right * (halfWidth * across);
            point.y = waterY;
            return point;
        }

        // A point on the footprint's silhouette ellipse at the waterline.
        static Vector3 RockRingPoint(Bounds bounds, float waterY, int index, int count)
        {
            float angle = (index / (float)count) * Mathf.PI * 2f;
            return new Vector3(
                bounds.center.x + Mathf.Cos(angle) * bounds.extents.x,
                waterY,
                bounds.center.z + Mathf.Sin(angle) * bounds.extents.z);
        }

        void WriteProbes(Vector3[] localOffsets, WaterSprayMode mode)
        {
            serializedObject.Update();
            SerializedProperty probes = serializedObject.FindProperty("probes");
            probes.arraySize = localOffsets.Length;
            for (int i = 0; i < localOffsets.Length; i++)
            {
                SerializedProperty element = probes.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("localOffset").vector3Value = localOffsets[i];
                element.FindPropertyRelative("mode").enumValueIndex = (int)mode;
            }
            serializedObject.ApplyModifiedProperties(); // registers Undo + marks the scene/prefab dirty
        }

        // Rest waterline = the resolved body's plane (its transform Y), matching the surface plane the
        // obstacle/caustics code uses. Falls back to the object's own height when there is no water body.
        static float ResolveWaterY(Vector3 worldPosition)
        {
            WaterVolume body = WaterVolume.BodyContaining(worldPosition);
            return body != null ? body.transform.position.y : worldPosition.y;
        }

        static Bounds ResolveWorldBounds(WaterSprayPump pump)
        {
            Collider collider = pump.GetComponent<Collider>();
            if (collider != null) return collider.bounds;

            Renderer renderer = pump.GetComponentInChildren<Renderer>();
            if (renderer != null) return renderer.bounds;

            Debug.LogWarning($"{nameof(WaterSprayPump)} on '{pump.name}' has no Collider or Renderer to size " +
                             "probe placement from; using a default extent. Move the probes by hand if needed.", pump);
            return new Bounds(pump.transform.position, Vector3.one * (FallbackHalfExtent * 2f));
        }

        // The object's chosen local axis, flattened to horizontal and normalised (guarded).
        static Vector3 HorizontalAxis(Transform t, ForwardAxis axis)
        {
            Vector3 local = axis switch
            {
                ForwardAxis.PlusX => Vector3.right,
                ForwardAxis.MinusX => Vector3.left,
                ForwardAxis.MinusZ => Vector3.back,
                _ => Vector3.forward,
            };
            Vector3 world = t.rotation * local;
            world.y = 0f;
            return world.sqrMagnitude > MinHorizontalAxisLength ? world.normalized : Vector3.forward;
        }

        // Half-width of an axis-aligned box (given by its extents) along an arbitrary direction: the box's
        // support distance. Lets the bow row reach the true forward/side faces under any yaw.
        static float SupportRadius(Vector3 direction, Vector3 extents)
            => Mathf.Abs(direction.x) * extents.x
             + Mathf.Abs(direction.y) * extents.y
             + Mathf.Abs(direction.z) * extents.z;

        // Each probe gets a draggable sphere handle: grab it in the Scene view to place the point directly,
        // instead of typing local offsets. Edits go through SerializedProperty, so Undo and prefab
        // overrides work the same as the auto-place button.
        void OnSceneGUI()
        {
            var pump = (WaterSprayPump)target;
            serializedObject.Update(); // reflect the latest points (e.g. right after an Undo) before drawing
            SerializedProperty probes = serializedObject.FindProperty("probes");
            if (probes == null || probes.arraySize == 0) return;

            Transform t = pump.transform;
            float radius = Mathf.Max(GizmoRadiusFloor, ResolveWorldBounds(pump).size.magnitude * GizmoRadiusFraction);

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < probes.arraySize; i++)
            {
                SerializedProperty element = probes.GetArrayElementAtIndex(i);
                SerializedProperty offset = element.FindPropertyRelative("localOffset");
                var mode = (WaterSprayMode)element.FindPropertyRelative("mode").enumValueIndex;

                Vector3 world = t.TransformPoint(offset.vector3Value);
                Handles.color = ModeColor(mode);
                Vector3 moved = Handles.FreeMoveHandle(world, radius, Vector3.zero, Handles.SphereHandleCap);
                if (moved != world)
                    offset.vector3Value = t.InverseTransformPoint(moved); // store back in the object's local space
            }
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties(); // one Undo step + marks the scene/prefab dirty
        }

        static Color ModeColor(WaterSprayMode mode) => mode switch
        {
            WaterSprayMode.Boat => BoatColor,
            WaterSprayMode.Rock => RockColor,
            _ => BothColor,
        };
    }
}
