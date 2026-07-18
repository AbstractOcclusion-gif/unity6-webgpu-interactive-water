// WebGpuWater - scene input router (mouse / touch / pen via Pointer; legacy fallback).
// Extracted from WaterVolume: input is a scene-level concern, so only the PRIMARY body
// owns a router (avoids two bodies fighting over one camera). It routes clicks to
// whichever body's surface the ray hits, throttles drag injection, and handles the
// pause / sun-align keys. Play mode only - the edit-mode preview never routes input.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterInputRouter
    {
        enum PointerMode { None, AddDrops, Orbit }

        // World distance the cursor must travel between injected ripples while dragging.
        // Holding still otherwise re-injects into the same texels every frame, pumping
        // unbounded energy into the explicit solver. The initial press bypasses this.
        const float MinDragWorldSpacing = 0.02f;

        // Drag-splash shaping: cursor speed maps to droplet strength; the burst radius
        // follows the ripple radius so bigger ripples throw wider spray.
        const float DragSplashFullStrengthDistance = 0.08f;
        const float MinDragSplashStrength = 0.1f;
        const float DragSplashStrengthScale = 0.6f;   // drag splashes stay softer than impact splashes
        const float DragSplashRadiusScale = 4f;       // splash radius per ripple radius

        // Touch only: a finger that lifts within this travel is a tap -> one ripple. Anything longer is a
        // drag, which the FlyCamera claims for move/look (it uses the same threshold), so the two never
        // both fire on one finger.
        const float TapMaxTravelPixels = 16f;
        const float TapSplashStrength = 0.5f;

        readonly WaterVolume _owner; // the primary body: camera/orbit/splash wiring + ripple look

        PointerMode _mode = PointerMode.None;
        Vector2 _oldMouse;
        Vector3 _prevWorld;      // last world-space ripple point during a drag
        WaterVolume _dragBody;   // body being rippled this drag
        bool _forceDrop;

        internal WaterInputRouter(WaterVolume owner)
        {
            _owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
        }

        internal void Update()
        {
            HandleKeys();
            HandleMouse();
        }

        Ray PixelRay(Vector2 p) => _owner.targetCamera.ScreenPointToRay(new Vector3(p.x, p.y, 0f));

        // Nearest water body whose surface the ray hits (null = none, so we orbit instead).
        static WaterVolume FindHitBody(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            WaterVolume best = null;
            float bestSqr = float.MaxValue;
            var bodies = WaterVolume.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (!bodies[i].TryRaycastSurface(ray, out Vector3 hit)) continue;
                float sqr = (hit - ray.origin).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = bodies[i]; worldHit = hit; }
            }
            return best;
        }

        void HandleMouse()
        {
            // No camera -> no rays to cast; skip input rather than NRE in PixelRay.
            if (_owner.targetCamera == null) return;

#if ENABLE_INPUT_SYSTEM
            // On touch, a finger drives the fly camera; only a tap (short-travel release) ripples the water.
            // Route to the tap handler whenever a finger is down or was just lifted, so a drag never reaches
            // the mouse path below (which would ripple every frame and fight the camera).
            var touchscreen = Touchscreen.current;
            if (touchscreen != null &&
                (touchscreen.primaryTouch.press.isPressed || touchscreen.primaryTouch.press.wasReleasedThisFrame))
            {
                HandleTouchRipple(touchscreen);
                return;
            }
#endif

            // While pinching (2+ fingers), don't ripple/orbit - let the camera zoom.
            if (MultiTouch()) { _mode = PointerMode.None; return; }

            Vector2 m = MousePos();

            if (MouseDown())
            {
                _oldMouse = m;
                _dragBody = FindHitBody(PixelRay(m), out Vector3 hit);
                if (_dragBody != null)
                {
                    _mode = PointerMode.AddDrops;
                    _prevWorld = hit;
                    _forceDrop = true; // the initial press always injects one ripple
                    DuringDrag(m);
                }
                else
                {
                    _mode = PointerMode.Orbit; // clicked empty space -> orbit the camera
                }
            }
            else if (MouseHeld())
            {
                DuringDrag(m);
            }
            else if (MouseUp())
            {
                _mode = PointerMode.None;
                _dragBody = null;
            }
        }

#if ENABLE_INPUT_SYSTEM
        // Touch: fire one ripple where a finger taps the water. Drags (camera) and multi-touch (pinch) are
        // ignored here so they don't double up with the FlyCamera gestures.
        void HandleTouchRipple(Touchscreen touchscreen)
        {
            if (MultiTouch()) return; // 2+ fingers -> camera pinch owns it

            var primary = touchscreen.primaryTouch;
            if (!primary.press.wasReleasedThisFrame) return; // the ripple lands when the tap lifts

            Vector2 start = primary.startPosition.ReadValue();
            Vector2 pos = primary.position.ReadValue();
            if ((pos - start).magnitude > TapMaxTravelPixels) return; // travelled = a drag, not a tap

            WaterVolume body = FindHitBody(PixelRay(pos), out Vector3 hit);
            if (body == null) return;

            body.AddRipple(hit.x, hit.z, _owner.RippleRadius, _owner.RippleStrength);
            if (_owner.splashEmitter != null)
                _owner.splashEmitter.EmitSplash(hit, TapSplashStrength, _owner.RippleRadius * DragSplashRadiusScale);
        }
#endif

        void DuringDrag(Vector2 m)
        {
            switch (_mode)
            {
                case PointerMode.AddDrops:
                {
                    if (_dragBody == null) break;
                    if (!_dragBody.TryRaycastSurface(PixelRay(m), out Vector3 hit)) break;

                    // Throttle injection by world distance travelled so holding the cursor
                    // still doesn't pump energy into the same texels every frame.
                    float moved = Vector2.Distance(new Vector2(hit.x, hit.z), new Vector2(_prevWorld.x, _prevWorld.z));
                    if (!_forceDrop && moved < MinDragWorldSpacing) break;
                    _forceDrop = false;

                    // Route the ripple to the clicked body (world-space API; it converts).
                    _dragBody.AddRipple(hit.x, hit.z, _owner.RippleRadius, _owner.RippleStrength);

                    if (_owner.splashEmitter != null)
                    {
                        float strength = Mathf.Clamp01(moved / DragSplashFullStrengthDistance);
                        if (strength > MinDragSplashStrength)
                            _owner.splashEmitter.EmitSplash(hit, strength * DragSplashStrengthScale,
                                                            _owner.RippleRadius * DragSplashRadiusScale);
                    }
                    _prevWorld = hit;
                    break;
                }
                case PointerMode.Orbit:
                {
                    if (_owner.orbit != null) _owner.orbit.Rotate(m.x - _oldMouse.x, m.y - _oldMouse.y);
                    break;
                }
            }
            _oldMouse = m;
        }

        void HandleKeys()
        {
            if (KeySpaceDown()) _owner.TogglePause();
            if (KeyLHeld() && _owner.targetCamera != null)
            {
                // Point the real sun along the camera view (or the fallback vector).
                if (_owner.sun != null)
                    _owner.sun.transform.rotation = Quaternion.LookRotation(_owner.targetCamera.transform.forward);
                else
                    _owner.lightDir = -_owner.targetCamera.transform.forward;
            }
        }

        // ---- input abstraction (mouse, touch or pen via Pointer; legacy fallback) ---
        // Pointer.current resolves to the mouse on desktop and the touchscreen on
        // mobile, so the same drag logic drives both.
        static Vector2 MousePos()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null ? Pointer.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
        static bool MouseDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }
        static bool MouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }
        static bool MouseUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Pointer.current != null && Pointer.current.press.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(0);
#endif
        }

        // True while two or more fingers are down, so single-touch ripple/orbit
        // yields to the camera's pinch-zoom.
        static bool MultiTouch()
        {
#if ENABLE_INPUT_SYSTEM
            var touchscreen = Touchscreen.current;
            if (touchscreen == null) return false;
            int pressed = 0;
            foreach (var touch in touchscreen.touches)
                if (touch.press.isPressed) pressed++;
            return pressed >= 2;
#else
            return Input.touchCount >= 2;
#endif
        }
        static bool KeySpaceDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }
        static bool KeyLHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.lKey.isPressed;
#else
            return Input.GetKey(KeyCode.L);
#endif
        }
    }
}
