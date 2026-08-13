// WebGpuWater - shared touchscreen plumbing.
// WHY this file exists: FlyCamera, OrbitCamera and WaterInputRouter each carried a near-identical
// private copy of the pressed-touch count, the two-active-touch fetch and the pinch-distance
// state machine, and the three copies were verified to be drifting apart. Only the shared
// MECHANICS live here; per-camera tunables (pinch-to-metres / pinch-to-zoom scaling, tap
// thresholds) stay with each camera so they remain independently tweakable.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterTouchInput
    {
#if ENABLE_INPUT_SYSTEM
        // Number of touches currently pressed. 'touchscreen' must be non-null.
        internal static int PressedCount(Touchscreen touchscreen)
        {
            int pressed = 0;
            foreach (var touch in touchscreen.touches)
                if (touch.press.isPressed) pressed++;
            return pressed;
        }

        // Positions of the first two pressed touches. Tolerates a null touchscreen (-> false)
        // because one historical copy (OrbitCamera) null-checked inside the helper while the
        // other (FlyCamera) checked at the call site; accepting null preserves both behaviors.
        internal static bool TryGetTwoTouches(Touchscreen touchscreen, out Vector2 a, out Vector2 b)
        {
            a = b = Vector2.zero;
            if (touchscreen == null) return false;
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

    // Pinch-distance state machine shared by FlyCamera (altitude) and OrbitCamera (zoom).
    // Deliberately NOT under ENABLE_INPUT_SYSTEM: OrbitCamera also pinch-zooms on the legacy
    // Input.GetTouch path, and this struct is pure math with no InputSystem dependency.
    // The default-constructed value (0) reads as "no active pinch" just like the sentinel,
    // because both fail the '_lastDist > 0f' had-a-previous-sample test - so a field of this
    // type needs no explicit initialization.
    internal struct PinchTracker
    {
        const float NoActivePinch = -1f; // sentinel: no pinch gesture in progress

        float _lastDist;

        // Feed the two touch positions each frame. Returns true - with the change in finger
        // spread (pixels) - once a previous frame's spread exists; the first frame of a gesture
        // returns false with deltaPixels 0 so callers may skip acting on it entirely
        // (OrbitCamera must: even a zero-delta Zoom() call would re-clamp its distance).
        internal bool Update(Vector2 a, Vector2 b, out float deltaPixels)
        {
            float dist = Vector2.Distance(a, b);
            bool hadPrevious = _lastDist > 0f;
            deltaPixels = hadPrevious ? dist - _lastDist : 0f;
            _lastDist = dist;
            return hadPrevious;
        }

        // Call the moment the gesture ends so a new pinch never inherits a stale distance.
        internal void Reset() => _lastDist = NoActivePinch;
    }
}
