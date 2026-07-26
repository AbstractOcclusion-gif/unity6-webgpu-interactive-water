// WebGpuWater - reusable orbit camera (Unity 6 / URP port)
// Orbits a pivot, scroll to zoom. WaterVolume calls Rotate() when the user
// drags the background; zoom is handled here every frame.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Camera))]
    public class OrbitCamera : MonoBehaviour
    {
        [Tooltip("World-space point the camera orbits around.")]
        [SerializeField] internal Vector3 pivot = new Vector3(0f, -0.5f, 0f);
        [SerializeField] internal Transform pivotTarget; // optional; overrides 'pivot' if set

        [Header("Orbit")]
        [SerializeField] internal float yaw = -200.5f;   // degrees around Y
        [SerializeField] internal float pitch = -25f;    // degrees around X
        [SerializeField] internal float minPitch = -89.99f;
        [SerializeField] internal float maxPitch = 89.99f;
        [SerializeField] internal float rotateSpeed = 0.5f;

        [Header("Zoom")]
        [SerializeField] internal float distance = 4f;
        [SerializeField] internal float minDistance = 1.5f;
        [SerializeField] internal float maxDistance = 12f;
        [SerializeField] internal float zoomSpeed = 0.5f;
        [Tooltip("Two-finger pinch zoom sensitivity (per pixel of finger spread).")]
        [SerializeField] internal float pinchZoomSpeed = 0.02f;

        // OS mouse-wheel delta per notch; dividing by it normalizes a notch to ~1 zoom step.
        const float MouseWheelNotchDelta = 120f;
        const float ScrollDeadzone = 0.0001f;       // ignore sub-pixel scroll jitter

        PinchTracker _pinch; // shared pinch-distance state machine (WaterTouchInput.cs)

        void OnEnable() => Apply();

        public void Rotate(float dx, float dy)
        {
            yaw -= dx * rotateSpeed;
            pitch += dy * rotateSpeed;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        public void Zoom(float delta)
        {
            distance = Mathf.Clamp(distance - delta * zoomSpeed, minDistance, maxDistance);
        }

        public void SetView(float newPitch, float newYaw, float newDistance)
        {
            pitch = Mathf.Clamp(newPitch, minPitch, maxPitch);
            yaw = newYaw;
            distance = Mathf.Clamp(newDistance, minDistance, maxDistance);
            Apply();
        }

        void LateUpdate()
        {
            float scroll = ScrollDelta();
            if (Mathf.Abs(scroll) > ScrollDeadzone) Zoom(scroll);
            HandlePinch();
            Apply();
        }

        // Two-finger pinch -> zoom (spread fingers = zoom in).
        void HandlePinch()
        {
            if (GetTwoTouches(out Vector2 a, out Vector2 b))
            {
                // Zoom only once a previous frame's spread exists (Update returns false on the
                // gesture's first frame - even Zoom(0) would re-clamp distance). The pixels-to-zoom
                // scaling is THIS camera's tunable; only the distance tracking is shared.
                if (_pinch.Update(a, b, out float deltaPixels)) Zoom(deltaPixels * pinchZoomSpeed);
            }
            else
            {
                _pinch.Reset();
            }
        }

        static bool GetTwoTouches(out Vector2 a, out Vector2 b)
        {
#if ENABLE_INPUT_SYSTEM
            // Shared helper null-checks Touchscreen.current itself, matching the old local copy.
            return WaterTouchInput.TryGetTwoTouches(Touchscreen.current, out a, out b);
#else
            a = b = Vector2.zero;
            if (Input.touchCount < 2) return false;
            a = Input.GetTouch(0).position;
            b = Input.GetTouch(1).position;
            return true;
#endif
        }

        void Apply()
        {
            Vector3 p = pivotTarget != null ? pivotTarget.position : pivot;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 eye = rot * new Vector3(0f, 0f, distance) + p;
            transform.position = eye;
            transform.LookAt(p, Vector3.up);
        }

        static float ScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.scroll.ReadValue().y / MouseWheelNotchDelta : 0f;
#else
            return Input.mouseScrollDelta.y;
#endif
        }
    }
}
