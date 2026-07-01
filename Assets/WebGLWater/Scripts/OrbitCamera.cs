// WebGL Water - reusable orbit camera (Unity 6 / URP port)
// Orbits a pivot, scroll to zoom. WaterController calls Rotate() when the user
// drags the background; zoom is handled here every frame. Publishes the camera
// world position to the _Eye global used by the water shaders.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace WebGLWater
{
    [RequireComponent(typeof(Camera))]
    public class OrbitCamera : MonoBehaviour
    {
        [Tooltip("World-space point the camera orbits around.")]
        public Vector3 pivot = new Vector3(0f, -0.5f, 0f);
        public Transform pivotTarget;          // optional; overrides 'pivot' if set

        [Header("Orbit")]
        public float yaw = -200.5f;            // degrees around Y
        public float pitch = -25f;             // degrees around X
        public float minPitch = -89.99f;
        public float maxPitch = 89.99f;
        public float rotateSpeed = 0.5f;

        [Header("Zoom")]
        public float distance = 4f;
        public float minDistance = 1.5f;
        public float maxDistance = 12f;
        public float zoomSpeed = 0.5f;
        [Tooltip("Two-finger pinch zoom sensitivity (per pixel of finger spread).")]
        public float pinchZoomSpeed = 0.02f;

        // OS mouse-wheel delta per notch; dividing by it normalizes a notch to ~1 zoom step.
        const float MouseWheelNotchDelta = 120f;
        const float ScrollDeadzone = 0.0001f;       // ignore sub-pixel scroll jitter
        const float NoActivePinch = -1f;            // sentinel: no pinch gesture in progress

        float _lastPinchDist = NoActivePinch;

        static readonly int ID_Eye = Shader.PropertyToID("_Eye");

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
                float d = Vector2.Distance(a, b);
                if (_lastPinchDist > 0f) Zoom((d - _lastPinchDist) * pinchZoomSpeed);
                _lastPinchDist = d;
            }
            else
            {
                _lastPinchDist = NoActivePinch;
            }
        }

        static bool GetTwoTouches(out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var ts = Touchscreen.current;
            if (ts == null) return false;
            int n = 0;
            foreach (var t in ts.touches)
            {
                if (!t.press.isPressed) continue;
                Vector2 pos = t.position.ReadValue();
                if (n == 0) a = pos;
                else if (n == 1) { b = pos; return true; }
                n++;
            }
            return false;
#else
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
            Shader.SetGlobalVector(ID_Eye, eye);
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
