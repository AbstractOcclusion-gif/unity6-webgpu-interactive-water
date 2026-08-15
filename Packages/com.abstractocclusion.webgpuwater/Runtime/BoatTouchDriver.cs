// WebGpuWater - on-screen touch drive for the boat demos (phone / tablet / WebGPU in browser).
//
// LEFT half of the screen: a FLOATING virtual joystick - it spawns under the thumb on touch
// down, so idle frames show no UI over the water. The stick vector feeds BoatController
// through the SAME camera-relative path the keyboard uses (SetDriveReference), so stick-up
// drives where the camera looks and the hull carves toward the thumb.
// RIGHT half: chase-camera orbit (per-frame finger delta -> SimpleFollowCamera.OrbitBy, the
// same math and clamps as its right-mouse path). TWO fingers on the right half pinch-zoom;
// a right-half pinch never orbits (the two gestures are exclusive, like every map app).
// The halves are PER FINGER, classified by each touch's START position (the FlyCamera
// scheme), so two thumbs drive and orbit SIMULTANEOUSLY - the reason the screen is split.
// Taps still ripple the water: WaterInputRouter's tap-travel threshold owns short touches,
// and this driver only reads input - it never consumes or blocks anything.
//
// Spawned at runtime by WaterDemoBoatSwitcher, so every boat demo scene gains touch with no
// scene edit. New Input System only, like FlyCamera's touch section: without
// ENABLE_INPUT_SYSTEM (or without a touchscreen) the component is inert and desktop input is
// untouched.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Boat Touch Driver")]
    [DisallowMultipleComponent]
    internal sealed class BoatTouchDriver : MonoBehaviour
    {
        const float DefaultStickRangePixels = 120f;        // drag (px) for a full-lock stick
        const float DefaultOrbitSensitivity = 0.15f;       // matches SimpleFollowCamera's mouse feel
        const float DefaultPinchZoomStepsPerPixel = 0.02f; // ~50 px of finger spread = one wheel notch
        const float StickBaseRadiusPixels = 70f;
        const float StickKnobRadiusPixels = 30f;
        const float StickBaseAlpha = 0.25f;
        const float StickKnobAlpha = 0.5f;
        const float StickRangeMinPixels = 1f;   // guards the drag division on a degenerate range
        const float DiscEdgeFeatherFraction = 0.15f; // rim softness of the generated disc sprite
        const int DiscTexturePixels = 64;
        const int NoTouch = -1; // sentinel touchId (real InputSystem ids are positive)

        [Header("Stick (left half)")]
        [SerializeField] float stickRangePixels = DefaultStickRangePixels;

        [Header("Camera (right half)")]
        [SerializeField] float orbitSensitivity = DefaultOrbitSensitivity;
        [SerializeField] float pinchZoomStepsPerPixel = DefaultPinchZoomStepsPerPixel;

        BoatController _boat;
        SimpleFollowCamera _followCamera;

        // The switcher retargets this on every boat change; releasing the stick here means a
        // mid-drag switch can never leave throttle latched on the previous hull.
        internal void SetTargets(BoatController boat, SimpleFollowCamera followCamera)
        {
            ReleaseStick();
            _boat = boat;
            _followCamera = followCamera;
        }

        void OnDisable() => ReleaseStick();

        void ReleaseStick()
        {
            if (_boat != null) _boat.SetTouchInput(Vector2.zero);
#if ENABLE_INPUT_SYSTEM
            _stickTouchId = NoTouch;
            _stickVector = Vector2.zero;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        PinchTracker _pinch; // shared pinch-distance state machine (WaterTouchInput.cs)
        int _stickTouchId = NoTouch;
        int _orbitTouchId = NoTouch;
        Vector2 _stickOrigin;         // screen px, where the owning finger touched down
        Vector2 _stickVector;         // -1..1, screen-aligned (x right, y up)
        Texture2D _discTexture;

        void Update()
        {
            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen == null)
            {
                if (_stickTouchId != NoTouch) ReleaseStick();
                _orbitTouchId = NoTouch;
                _pinch.Reset();
                return;
            }

            bool stickAlive = false;
            bool orbitAlive = false;
            Vector2 orbitDelta = Vector2.zero;
            int rightCount = 0;
            Vector2 rightA = Vector2.zero;
            Vector2 rightB = Vector2.zero;
            float splitX = Screen.width * 0.5f;

            foreach (var touch in touchscreen.touches)
            {
                if (!touch.press.isPressed) continue;
                int id = touch.touchId.ReadValue();

                // Classified by START position, so a drive-drag that crosses the middle of the
                // screen keeps driving instead of suddenly orbiting (the FlyCamera rule).
                if (touch.startPosition.ReadValue().x < splitX)
                {
                    if (_stickTouchId == NoTouch)
                    {
                        _stickTouchId = id;
                        _stickOrigin = touch.position.ReadValue(); // floating stick: born under the thumb
                    }
                    if (id != _stickTouchId) continue; // one stick; extra left fingers are ignored

                    stickAlive = true;
                    Vector2 drag = touch.position.ReadValue() - _stickOrigin;
                    _stickVector = Vector2.ClampMagnitude(
                        drag / Mathf.Max(stickRangePixels, StickRangeMinPixels), 1f);
                }
                else
                {
                    if (rightCount == 0) rightA = touch.position.ReadValue();
                    else if (rightCount == 1) rightB = touch.position.ReadValue();
                    rightCount++;

                    if (_orbitTouchId == NoTouch) _orbitTouchId = id;
                    if (id == _orbitTouchId)
                    {
                        orbitAlive = true;
                        orbitDelta = touch.delta.ReadValue();
                    }
                }
            }

            if (!stickAlive && _stickTouchId != NoTouch) ReleaseStick(); // finger lifted
            if (stickAlive && _boat != null) _boat.SetTouchInput(_stickVector);

            if (rightCount >= 2)
            {
                // Two right-half fingers = pinch zoom, never orbit. First gesture frame returns
                // false (no previous spread) and is deliberately skipped - see PinchTracker.
                if (_pinch.Update(rightA, rightB, out float spreadDelta) && _followCamera != null)
                    _followCamera.ZoomSteps(spreadDelta * pinchZoomStepsPerPixel);
            }
            else
            {
                _pinch.Reset();
                if (orbitAlive && _followCamera != null)
                    _followCamera.OrbitBy(orbitDelta * orbitSensitivity);
            }

            if (!orbitAlive) _orbitTouchId = NoTouch;
        }

        // Minimal feedback: a soft base disc where the finger landed and a knob at the stick
        // position, drawn only while a stick finger is down. IMGUI keeps this scene-free (no
        // canvas asset, nothing to wire in existing demos); the one texture is generated once.
        void OnGUI()
        {
            if (_stickTouchId == NoTouch) return;
            if (_discTexture == null) _discTexture = BuildDiscTexture();
            DrawDisc(_stickOrigin, StickBaseRadiusPixels, StickBaseAlpha);
            DrawDisc(_stickOrigin + _stickVector * stickRangePixels, StickKnobRadiusPixels, StickKnobAlpha);
        }

        void DrawDisc(Vector2 screenPosition, float radius, float alpha)
        {
            // Touch coordinates are y-up; IMGUI rects are y-down.
            Rect rect = new Rect(screenPosition.x - radius, Screen.height - screenPosition.y - radius,
                                 radius * 2f, radius * 2f);
            Color previous = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(rect, _discTexture);
            GUI.color = previous;
        }

        static Texture2D BuildDiscTexture()
        {
            var texture = new Texture2D(DiscTexturePixels, DiscTexturePixels, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            float half = (DiscTexturePixels - 1) * 0.5f;
            var center = new Vector2(half, half);
            for (int y = 0; y < DiscTexturePixels; y++)
            {
                for (int x = 0; x < DiscTexturePixels; x++)
                {
                    float normalizedDistance = Vector2.Distance(new Vector2(x, y), center) / half;
                    float alpha = Mathf.Clamp01((1f - normalizedDistance) / DiscEdgeFeatherFraction);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return texture;
        }

        void OnDestroy()
        {
            if (_discTexture != null) Destroy(_discTexture);
        }
#endif
    }
}
