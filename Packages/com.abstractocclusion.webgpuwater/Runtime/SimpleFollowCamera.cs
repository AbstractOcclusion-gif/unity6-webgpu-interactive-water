// WebGpuWater - an orbiting chase camera for the boat demos.
//
// Follows only the target's YAW, so the view doesn't roll or pitch as the boat rocks on the waves.
// Right-drag or the gamepad right stick orbits; the mouse wheel zooms. The serialized local offset
// remains the initial view, preserving existing scenes that used the original fixed chase camera.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Simple Follow Camera")]
    [DisallowMultipleComponent]
    public sealed class SimpleFollowCamera : MonoBehaviour
    {
        const float DefaultMouseOrbitSensitivity = 0.15f;
        const float DefaultGamepadOrbitSpeed = 120f;
        const float DefaultMinimumPitch = -10f;
        const float DefaultMaximumPitch = 75f;
        const float DefaultMinimumDistance = 3f;
        const float DefaultMaximumDistance = 40f;
        const float DefaultZoomSpeed = 1.5f;
        const float MinimumDistanceLimit = 0.01f;
        const float MouseWheelNotchDelta = 120f;
        const float GamepadDeadzone = 0.125f;
        const string MouseXAxis = "Mouse X";
        const string MouseYAxis = "Mouse Y";

        [Header("Follow")]
        [SerializeField] internal Transform target;
        [SerializeField] Vector3 localOffset = new Vector3(0f, 4.5f, -11f); // behind and above, in target yaw space
        [SerializeField] float lookHeight = 1f;                             // aim a little above the target origin
        [SerializeField] float followSharpness = 4f;                        // higher = snappier

        [Header("Orbit")]
        [SerializeField] bool allowOrbit = true;
        [SerializeField] float mouseOrbitSensitivity = DefaultMouseOrbitSensitivity;
        [SerializeField] float gamepadOrbitSpeed = DefaultGamepadOrbitSpeed;
        [SerializeField] float minimumPitch = DefaultMinimumPitch;
        [SerializeField] float maximumPitch = DefaultMaximumPitch;

        [Header("Zoom")]
        [SerializeField] float minimumDistance = DefaultMinimumDistance;
        [SerializeField] float maximumDistance = DefaultMaximumDistance;
        [SerializeField] float zoomSpeed = DefaultZoomSpeed;

        float _orbitYaw;
        float _orbitPitch;
        float _defaultDistance;
        float _distance;
        bool _orbitInitialized;

        void OnEnable() => InitializeOrbit();

        void LateUpdate()
        {
            if (target == null) return;
            if (!_orbitInitialized) InitializeOrbit();

            ReadOrbitInput();

            // Yaw-only frame: ignore the boat's roll/pitch so the camera stays level.
            Vector3 lookPoint = target.position + Vector3.up * lookHeight;
            Quaternion targetYaw = Quaternion.Euler(0f, target.eulerAngles.y, 0f);
            Quaternion orbit = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 desired = lookPoint + targetYaw * orbit * (Vector3.back * _distance);

            float followAmount = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, followAmount);
            transform.LookAt(lookPoint, Vector3.up);
        }

        internal void SetTarget(Transform newTarget, float framingDistance)
        {
            target = newTarget;
            if (!_orbitInitialized) InitializeOrbit();
            _distance = ClampDistance(Mathf.Max(_defaultDistance, framingDistance));
        }

        // Touch entry points (BoatTouchDriver). Same math and clamps as the mouse-orbit and
        // scroll-zoom paths in ReadOrbitInput; the pixels-to-degrees / pixels-to-steps scaling
        // stays with the CALLER (the driver's serialized tunables), so touch feel can be tuned
        // without touching this camera's mouse feel.
        internal void OrbitBy(Vector2 delta)
        {
            if (!allowOrbit) return;
            if (!_orbitInitialized) InitializeOrbit();
            _orbitYaw += delta.x;
            _orbitPitch = Mathf.Clamp(_orbitPitch + delta.y, minimumPitch, maximumPitch);
        }

        internal void ZoomSteps(float steps)
        {
            if (steps == 0f) return;
            if (!_orbitInitialized) InitializeOrbit();
            _distance = ClampDistance(_distance - steps * zoomSpeed);
        }

        void InitializeOrbit()
        {
            Vector3 lookRelativeOffset = localOffset - Vector3.up * lookHeight;
            _defaultDistance = Mathf.Max(lookRelativeOffset.magnitude, MinimumDistanceLimit);
            _distance = ClampDistance(_defaultDistance);

            float horizontalDistance = new Vector2(lookRelativeOffset.x, lookRelativeOffset.z).magnitude;
            _orbitYaw = Mathf.Atan2(-lookRelativeOffset.x, -lookRelativeOffset.z) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Atan2(lookRelativeOffset.y, horizontalDistance) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Clamp(_orbitPitch, minimumPitch, maximumPitch);
            _orbitInitialized = true;
        }

        void ReadOrbitInput()
        {
            if (allowOrbit)
            {
                Vector2 mouseDelta = MouseOrbitDelta();
                Vector2 gamepadInput = GamepadOrbitInput();
                float unscaledDeltaTime = Time.unscaledDeltaTime;

                _orbitYaw += mouseDelta.x * mouseOrbitSensitivity;
                _orbitPitch += mouseDelta.y * mouseOrbitSensitivity;
                _orbitYaw += gamepadInput.x * gamepadOrbitSpeed * unscaledDeltaTime;
                _orbitPitch += gamepadInput.y * gamepadOrbitSpeed * unscaledDeltaTime;
                _orbitPitch = Mathf.Clamp(_orbitPitch, minimumPitch, maximumPitch);
            }

            float scroll = ScrollDelta();
            if (scroll == 0f) return;
            _distance = ClampDistance(_distance - scroll * zoomSpeed);
        }

        float ClampDistance(float distance) => Mathf.Clamp(
            distance,
            Mathf.Max(minimumDistance, MinimumDistanceLimit),
            Mathf.Max(maximumDistance, minimumDistance));

        static Vector2 MouseOrbitDelta()
        {
#if ENABLE_INPUT_SYSTEM
            Mouse mouse = Mouse.current;
            return mouse != null && mouse.rightButton.isPressed ? mouse.delta.ReadValue() : Vector2.zero;
#else
            if (!Input.GetMouseButton(1)) return Vector2.zero;
            return new Vector2(Input.GetAxisRaw(MouseXAxis), Input.GetAxisRaw(MouseYAxis));
#endif
        }

        static Vector2 GamepadOrbitInput()
        {
#if ENABLE_INPUT_SYSTEM
            Gamepad gamepad = Gamepad.current;
            if (gamepad == null) return Vector2.zero;
            Vector2 input = gamepad.rightStick.ReadValue();
            return input.sqrMagnitude >= GamepadDeadzone * GamepadDeadzone ? input : Vector2.zero;
#else
            return Vector2.zero;
#endif
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
