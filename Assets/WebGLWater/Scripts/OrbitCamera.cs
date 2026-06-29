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
            if (Mathf.Abs(scroll) > 0.0001f) Zoom(scroll);
            Apply();
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
            return Mouse.current != null ? Mouse.current.scroll.ReadValue().y / 120f : 0f;
#else
            return Input.mouseScrollDelta.y;
#endif
        }
    }
}
