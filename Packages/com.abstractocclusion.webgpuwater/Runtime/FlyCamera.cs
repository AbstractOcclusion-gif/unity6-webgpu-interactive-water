// WebGpuWater - free-fly camera (Unity 6 / URP port).
// Desktop: WASD to move on the view plane, Q/E down/up, hold RIGHT mouse to look, hold Shift to boost.
// Touch (phone / tablet): drag on the LEFT half of the screen to fly (a virtual stick - forward/back and
// strafe, relative to where you look), drag on the RIGHT half to look around, and pinch with two fingers
// to change altitude (spread = ascend). A short tap is left alone so the water can ripple under it.
// An alternative to OrbitCamera for exploring large bodies; supports both the new Input System and
// the legacy Input manager, mirroring OrbitCamera's input abstraction.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Camera))]
    public sealed class FlyCamera : MonoBehaviour
    {
        [Header("Move")]
        [Tooltip("Metres per second at normal speed.")]
        [SerializeField] internal float moveSpeed = 6f;
        [Tooltip("Speed multiplier while Shift is held.")]
        [SerializeField] internal float boostMultiplier = 3f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse movement while the right button is held.")]
        [SerializeField] internal float lookSensitivity = 0.1f;

        [Header("Touch")]
        [Tooltip("Degrees of rotation per pixel of finger movement while dragging on the right half.")]
        [SerializeField] internal float touchLookSensitivity = 0.12f;
        [Tooltip("Finger travel (px) beyond which a drag counts as camera control. Below this it stays a tap, so the water can ripple under it.")]
        [SerializeField] internal float touchTapTravelPixels = 16f;
        [Tooltip("Left-stick finger offset (px) that maps to full move speed.")]
        [SerializeField] internal float touchMoveRangePixels = 140f;
        [Tooltip("Vertical metres per pixel of two-finger pinch spread (spread fingers = ascend).")]
        [SerializeField] internal float pinchVerticalSpeed = 0.01f;

        const float MinPitch = -89.99f;
        const float MaxPitch = 89.99f;
        const float NoActivePinch = -1f; // sentinel: no pinch gesture in progress

        float _yaw;
        float _pitch;
        float _lastPinchDist = NoActivePinch;

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _pitch = NormalizePitch(euler.x);
            _yaw = euler.y;
        }

        void Update()
        {
            // Look: right mouse (desktop) and/or a right-half touch drag. Accumulate degrees, apply once.
            Vector2 lookDegrees = Vector2.zero;
            if (LookHeld()) lookDegrees += MouseDelta() * lookSensitivity;
            lookDegrees += TouchLookDegrees();
            if (lookDegrees != Vector2.zero) ApplyLookDegrees(lookDegrees);

            // Move: keyboard (desktop) and/or a left-half touch stick. Both are zero on the other platform.
            Vector3 move = MoveInput() + TouchMoveInput();
            ApplyMove(move, BoostHeld());

            // Altitude: two-finger pinch (touch only).
            ApplyVertical(TouchPinchVertical());
        }

        void ApplyLookDegrees(Vector2 degrees)
        {
            _yaw += degrees.x;
            _pitch = Mathf.Clamp(_pitch - degrees.y, MinPitch, MaxPitch);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // moveInput: x = right/left, y = up/down (Q/E), z = forward/back, in local axes.
        void ApplyMove(Vector3 moveInput, bool boost)
        {
            if (moveInput == Vector3.zero) return;
            Vector3 local = transform.right * moveInput.x + Vector3.up * moveInput.y + transform.forward * moveInput.z;
            float speed = moveSpeed * (boost ? boostMultiplier : 1f);
            // Unscaled: a free-fly camera should keep its speed regardless of any time scaling or pause
            // (a per-body water timeScale, a paused game, etc.), so movement never "loses speed".
            transform.position += Vector3.ClampMagnitude(local, 1f) * (speed * Time.unscaledDeltaTime);
        }

        void ApplyVertical(float metres)
        {
            if (metres == 0f) return;
            transform.position += Vector3.up * metres;
        }

        // Wrap Unity's 0..360 euler.x into a signed pitch so the clamp is symmetric.
        static float NormalizePitch(float eulerX) => eulerX > 180f ? eulerX - 360f : eulerX;

        // ---- keyboard / mouse (desktop) ----

        static Vector3 MoveInput()
        {
            Vector3 m = Vector3.zero;
#if ENABLE_INPUT_SYSTEM
            var k = Keyboard.current;
            if (k == null) return m;
            if (k.wKey.isPressed) m.z += 1f;
            if (k.sKey.isPressed) m.z -= 1f;
            if (k.dKey.isPressed) m.x += 1f;
            if (k.aKey.isPressed) m.x -= 1f;
            if (k.eKey.isPressed) m.y += 1f;
            if (k.qKey.isPressed) m.y -= 1f;
#else
            if (Input.GetKey(KeyCode.W)) m.z += 1f;
            if (Input.GetKey(KeyCode.S)) m.z -= 1f;
            if (Input.GetKey(KeyCode.D)) m.x += 1f;
            if (Input.GetKey(KeyCode.A)) m.x -= 1f;
            if (Input.GetKey(KeyCode.E)) m.y += 1f;
            if (Input.GetKey(KeyCode.Q)) m.y -= 1f;
#endif
            return m;
        }

        static bool BoostHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed;
#else
            return Input.GetKey(KeyCode.LeftShift);
#endif
        }

        static bool LookHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
#endif
        }

        static Vector2 MouseDelta()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
#else
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#endif
        }

        // ---- touch (phone / tablet, new Input System only) ----
        // One finger drives the camera by the half of the screen it started in; two fingers pinch to
        // change altitude. A finger that hasn't travelled past the tap threshold does nothing here, so a
        // tap is free to ripple the water (WaterInputRouter owns that, using the same threshold).

        // Left-half drag -> a virtual move stick. x = strafe, z = forward (screen-up = forward).
        Vector3 TouchMoveInput()
        {
#if ENABLE_INPUT_SYSTEM
            var touchscreen = Touchscreen.current;
            if (touchscreen == null || PressedCount(touchscreen) != 1) return Vector3.zero;

            var touch = touchscreen.primaryTouch;
            Vector2 start = touch.startPosition.ReadValue();
            if (start.x >= Screen.width * 0.5f) return Vector3.zero; // right half is Look, not Move

            Vector2 offset = touch.position.ReadValue() - start;
            float travel = offset.magnitude;
            if (travel <= touchTapTravelPixels) return Vector3.zero; // dead zone / still a tap

            float range = Mathf.Max(1f, touchMoveRangePixels - touchTapTravelPixels);
            float amount = Mathf.Clamp01((travel - touchTapTravelPixels) / range);
            Vector2 dir = offset / travel;
            return new Vector3(dir.x * amount, 0f, dir.y * amount);
#else
            return Vector3.zero;
#endif
        }

        // Right-half drag -> look. Returns per-frame rotation already scaled to degrees.
        Vector2 TouchLookDegrees()
        {
#if ENABLE_INPUT_SYSTEM
            var touchscreen = Touchscreen.current;
            if (touchscreen == null || PressedCount(touchscreen) != 1) return Vector2.zero;

            var touch = touchscreen.primaryTouch;
            Vector2 start = touch.startPosition.ReadValue();
            if (start.x < Screen.width * 0.5f) return Vector2.zero; // left half is Move, not Look

            Vector2 pos = touch.position.ReadValue();
            if ((pos - start).magnitude <= touchTapTravelPixels) return Vector2.zero; // still a tap

            return touch.delta.ReadValue() * touchLookSensitivity;
#else
            return Vector2.zero;
#endif
        }

        // Two-finger pinch -> altitude (spread fingers = ascend). Metres to move up this frame.
        float TouchPinchVertical()
        {
#if ENABLE_INPUT_SYSTEM
            var touchscreen = Touchscreen.current;
            if (touchscreen == null || PressedCount(touchscreen) < 2 || !TwoTouches(touchscreen, out Vector2 a, out Vector2 b))
            {
                _lastPinchDist = NoActivePinch;
                return 0f;
            }

            float dist = Vector2.Distance(a, b);
            float metres = _lastPinchDist > 0f ? (dist - _lastPinchDist) * pinchVerticalSpeed : 0f;
            _lastPinchDist = dist;
            return metres;
#else
            return 0f;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        static int PressedCount(Touchscreen touchscreen)
        {
            int pressed = 0;
            foreach (var touch in touchscreen.touches)
                if (touch.press.isPressed) pressed++;
            return pressed;
        }

        static bool TwoTouches(Touchscreen touchscreen, out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
            int n = 0;
            foreach (var touch in touchscreen.touches)
            {
                if (!touch.press.isPressed) continue;
                Vector2 pos = touch.position.ReadValue();
                if (n == 0) a = pos;
                else if (n == 1) { b = pos; return true; }
                n++;
            }
            return false;
        }
#endif
    }
}
