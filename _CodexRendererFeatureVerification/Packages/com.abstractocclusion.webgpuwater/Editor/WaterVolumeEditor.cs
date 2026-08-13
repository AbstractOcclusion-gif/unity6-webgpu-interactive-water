// WebGpuWater - scene-view editor for WaterVolume (Unity 6 / URP port)
// Draws the oriented water volume as a wire box (floor -> surface) and gives draggable
// scene handles for its extent and rotation, so a body can be sized and oriented in the
// scene view without typing numbers into the inspector. Editor-only; no runtime code.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterVolume))]
    public partial class WaterVolumeEditor : UnityEditor.Editor
    {
        // The volume in POOL space (matches WaterVolume.PoolToWorld): x,z span [-1, 1] and y
        // spans [-1, 0] (floor to surface). As a box that is center (0, -0.5, 0), size (2, 1, 2),
        // scaled by volumeExtent and placed by the GameObject transform.
        static readonly Vector3 PoolBoxCenter = new Vector3(0f, -0.5f, 0f);
        static readonly Vector3 PoolBoxSize = new Vector3(2f, 1f, 2f);
        const float PoolHalfExtent = 1f;  // pool horizontal half-size before extent scaling

        const float MinExtent = 1e-2f;         // keep the box non-degenerate while dragging
        const float HandleCapFactor = 0.15f;   // slider cap size relative to the view handle size
        const float HandleOffsetFactor = 0.5f; // push the cap this far off the face (in handle-size
                                               // units) so it clears Unity's transform gizmo

        static readonly Color BoxColorSelected = new Color(0.35f, 0.75f, 1f, 1f);
        static readonly Color BoxColorIdle = new Color(0.35f, 0.75f, 1f, 0.3f);
        static readonly Color SurfaceColor = new Color(0.6f, 0.9f, 1f, 1f);
        static readonly Color ExtentHandleColor = new Color(0.4f, 0.9f, 1f, 1f);
        static readonly Color SimWindowColor = new Color(1f, 0.85f, 0.3f, 0.9f);

        // ---- gizmo: the oriented volume box + a highlighted surface rectangle ----
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        static void DrawVolumeGizmo(WaterVolume volume, GizmoType gizmoType)
        {
            Vector3 extent = SafeExtent(volume.volumeExtent);
            bool selected = (gizmoType & GizmoType.Selected) != 0;

            Matrix4x4 previous = Gizmos.matrix;
            // extent as the matrix scale, so the fixed pool-space box renders at world size.
            Gizmos.matrix = Matrix4x4.TRS(volume.transform.position, volume.transform.rotation, extent);

            Gizmos.color = selected ? BoxColorSelected : BoxColorIdle;
            Gizmos.DrawWireCube(PoolBoxCenter, PoolBoxSize);

            // Surface rectangle at pool y = 0, drawn brighter so the waterline reads at a glance.
            Gizmos.color = SurfaceColor;
            DrawSurfaceRect();

            Gizmos.matrix = previous;

            // Sim window (large bodies): the camera-following interactive-sim footprint. In play
            // mode it sits at the live (scrolled) centre; at edit time it previews at the body
            // centre as a sizing aid. Drawn in world metres, so it uses a rotation-only matrix.
            if (TryGetSimWindow(volume, out Vector3 windowCenter, out float windowHalf))
            {
                Gizmos.matrix = Matrix4x4.TRS(windowCenter, volume.transform.rotation, Vector3.one);
                Gizmos.color = SimWindowColor;
                DrawWindowRect(windowHalf);
                Gizmos.matrix = previous;
            }
        }

        // The active sim window as a world centre + horizontal half-size, or false when this
        // body is whole-body. Uses the live window at runtime, the authored size at edit time.
        static bool TryGetSimWindow(WaterVolume volume, out Vector3 center, out float half)
        {
            half = Mathf.Max(MinExtent, volume.simWindowMeters);
            if (Application.isPlaying)
            {
                center = volume.SimWindowCenter;
                return volume.IsWindowed;
            }
            center = volume.transform.position;
            if (!volume.enableLargeBodyWindow) return false;
            Vector3 e = SafeExtent(volume.volumeExtent);
            return Mathf.Max(e.x, e.z) > volume.largeBodyThreshold;
        }

        // Wire rectangle at the surface (y = 0), half-size 'half' in world units on x and z.
        static void DrawWindowRect(float half)
        {
            Vector3 backLeft = new Vector3(-half, 0f, -half);
            Vector3 backRight = new Vector3(half, 0f, -half);
            Vector3 frontRight = new Vector3(half, 0f, half);
            Vector3 frontLeft = new Vector3(-half, 0f, half);
            Gizmos.DrawLine(backLeft, backRight);
            Gizmos.DrawLine(backRight, frontRight);
            Gizmos.DrawLine(frontRight, frontLeft);
            Gizmos.DrawLine(frontLeft, backLeft);
        }

        // The four surface edges in pool space (y = 0, corners at +/-PoolHalfExtent).
        static void DrawSurfaceRect()
        {
            float h = PoolHalfExtent;
            Vector3 backLeft = new Vector3(-h, 0f, -h);
            Vector3 backRight = new Vector3(h, 0f, -h);
            Vector3 frontRight = new Vector3(h, 0f, h);
            Vector3 frontLeft = new Vector3(-h, 0f, h);
            Gizmos.DrawLine(backLeft, backRight);
            Gizmos.DrawLine(backRight, frontRight);
            Gizmos.DrawLine(frontRight, frontLeft);
            Gizmos.DrawLine(frontLeft, backLeft);
        }

        // ---- handles: extent (face sliders) + rotation ----
        // Rotation is left to Unity's built-in Rotate tool (E), which drives the same
        // transform.rotation with no Euler typing; a custom ring here would only overlap it.
        void OnSceneGUI()
        {
            var volume = (WaterVolume)target;
            EditExtent(volume);
        }

        // Face-center sliders in the volume's rotated frame, placed on the +X, +Z and floor faces
        // (offset slightly outward) so they clear Unity's transform gizmo at the origin. +X and +Z
        // drag the horizontal half-extents (the box grows both ways, staying centered on the
        // transform); the floor face drags depth downward while the surface stays pinned at y = 0.
        static void EditExtent(WaterVolume volume)
        {
            Vector3 extent = SafeExtent(volume.volumeExtent);
            Transform t = volume.transform;
            Vector3 center = t.position;
            Vector3 right = t.rotation * Vector3.right;
            Vector3 forward = t.rotation * Vector3.forward;
            Vector3 down = t.rotation * Vector3.down;

            // Side handles sit at mid-depth on their face; the floor handle at the floor centre.
            Vector3 midDepth = down * (extent.y * 0.5f);
            Vector3 xFace = center + right * extent.x + midDepth;
            Vector3 zFace = center + forward * extent.z + midDepth;
            Vector3 floorFace = center + down * extent.y;

            Handles.color = ExtentHandleColor;
            EditorGUI.BeginChangeCheck();
            float newX = FaceExtentSlider(xFace, center, right);
            float newZ = FaceExtentSlider(zFace, center, forward);
            float newY = FaceExtentSlider(floorFace, center, down);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(volume, "Edit Water Volume Extent");
            volume.volumeExtent = new Vector3(newX, newY, newZ);
        }

        // Drag one face along its axis and return the new half-extent along that axis. The cap is
        // pushed 'margin' off the face so it doesn't overlap the transform gizmo; that same margin
        // is subtracted back out, and the perpendicular placement drops out of the axis projection.
        static float FaceExtentSlider(Vector3 faceCenter, Vector3 center, Vector3 axis)
        {
            float viewSize = HandleUtility.GetHandleSize(faceCenter);
            float margin = viewSize * HandleOffsetFactor;
            Vector3 capPosition = faceCenter + axis * margin;
            Vector3 moved = Handles.Slider(capPosition, axis, viewSize * HandleCapFactor, Handles.ConeHandleCap, 0f);
            return Mathf.Max(MinExtent, Vector3.Dot(moved - center, axis) - margin);
        }

        static Vector3 SafeExtent(Vector3 extent) => new Vector3(
            Mathf.Max(MinExtent, extent.x),
            Mathf.Max(MinExtent, extent.y),
            Mathf.Max(MinExtent, extent.z));
    }
}
