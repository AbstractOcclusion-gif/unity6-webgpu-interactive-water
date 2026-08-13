// WebGpuWater - runtime cost probe (diagnostics only).
// Flips the expensive per-frame water features and shows the frame time, so ONE build answers many
// "is it this?" questions instead of one build per hypothesis. Written for the underwater FPS work:
// the browser build takes ~15 minutes, and the two suspects (the fullscreen underwater fog and the
// ocean god-ray march) are both switchable without a restart.
//
// WHY IT ONLY TOUCHES RUNTIME STATE. Both knobs below write fields that ApplyQuality already owns
// and that nothing serialises - _underwaterFogMode and _godRaysAllowed. It deliberately does NOT
// write the authored settings (largeGodRayDensity, waterFog, meniscus): ApplyQuality records that
// writing a serialized field from code baked a device-probed value into saved scene data, and a
// debug component must never be able to do that. It is also play-mode only, so there is no
// [ExecuteAlways] path that could run while the scene is being authored.
//
// It is ALSO the test for the ocean god-ray tier gate: toggling god rays here goes through
// _godRaysAllowed -> LargeGodRayDensity, which is exactly the gate that used to be inert on an
// ocean body. If G does nothing, that gate is broken again.
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Cost Probe")]
    public sealed class WaterCostProbe : MonoBehaviour
    {
        // Frame-time smoothing: an exponential average for the headline number, plus the worst
        // frame over a short window - a perf change that only shows up in the spikes is still a
        // perf change, and an average alone hides it.
        const float SmoothingHalfLifeSeconds = 0.25f;
        const float WorstWindowSeconds = 1f;
        // The readout is rebuilt at this rate rather than every OnGUI call (which fires twice per
        // frame): string building is the one thing in here that would allocate per frame, and GC
        // pressure in a WebGL build is exactly what we are trying to measure around.
        const float ReadoutRefreshSeconds = 0.25f;
        const int FontSize = 18;
        const int MarginPixels = 12;
        const int PanelWidth = 470;
        const int PanelHeight = 142; // 5 readout lines at FontSize, with slack - GUI.Label CLIPS to its rect

        [Tooltip("Cycle the underwater fog mode: Off -> Simple -> Full.")]
        [SerializeField] KeyCode fogKey = KeyCode.F;
        [Tooltip("Toggle the ocean god-ray shafts (through the tier gate, so it also tests it).")]
        [SerializeField] KeyCode godRayKey = KeyCode.G;
        [Tooltip("Show or hide the readout.")]
        [SerializeField] KeyCode visibilityKey = KeyCode.H;
        float _smoothedMs;
        float _worstMs;
        float _worstWindowEndsAt;
        float _readoutRefreshesAt;
        string _readout = "";
        bool _visible = true;
        GUIStyle _style;

        void Update()
        {
            if (!Application.isPlaying) return;
            SampleFrameTime();
            ReadInput();
            RefreshReadout();
        }

        void SampleFrameTime()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            // Half-life -> per-frame blend weight, so the smoothing feels the same at any frame rate.
            float blend = 1f - Mathf.Pow(0.5f, Time.unscaledDeltaTime / SmoothingHalfLifeSeconds);
            _smoothedMs = (_smoothedMs <= 0f) ? ms : Mathf.Lerp(_smoothedMs, ms, blend);

            if (Time.unscaledTime >= _worstWindowEndsAt)
            {
                _worstMs = ms;
                _worstWindowEndsAt = Time.unscaledTime + WorstWindowSeconds;
                return;
            }
            _worstMs = Mathf.Max(_worstMs, ms);
        }

        void ReadInput()
        {
            if (Pressed(visibilityKey)) _visible = !_visible;

            WaterVolume primary = WaterVolume.Primary;
            if (primary == null) return;
            if (Pressed(fogKey)) primary.UnderwaterFogMode = NextFogMode(primary.UnderwaterFogMode);
            if (Pressed(godRayKey)) primary.GodRaysAllowed = !primary.GodRaysAllowed;
        }

        // Off -> Simple -> Full -> Off. Ordered by cost so repeated presses walk the budget upward.
        static WaterQuality.UnderwaterMode NextFogMode(WaterQuality.UnderwaterMode current)
        {
            if (current == WaterQuality.UnderwaterMode.Off) return WaterQuality.UnderwaterMode.Simple;
            if (current == WaterQuality.UnderwaterMode.Simple) return WaterQuality.UnderwaterMode.Full;
            return WaterQuality.UnderwaterMode.Off;
        }

        static bool Pressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return false;
            Key mapped = MapToInputSystemKey(key);
            return mapped != Key.None && keyboard[mapped].wasPressedThisFrame;
#else
            return Input.GetKeyDown(key);
#endif
        }

#if ENABLE_INPUT_SYSTEM
        // Only the letter keys are mapped: this is a debug component with letter-key defaults, and a
        // full KeyCode -> Key table would be a hundred lines of surface for nobody. A key outside the
        // range reports None and the binding is inert rather than silently wrong.
        static Key MapToInputSystemKey(KeyCode key)
        {
            if (key < KeyCode.A || key > KeyCode.Z) return Key.None;
            return Key.A + (int)(key - KeyCode.A);
        }
#endif

        void RefreshReadout()
        {
            if (!_visible || Time.unscaledTime < _readoutRefreshesAt) return;
            _readoutRefreshesAt = Time.unscaledTime + ReadoutRefreshSeconds;

            WaterVolume primary = WaterVolume.Primary;
            string fog = primary != null ? primary.UnderwaterFogMode.ToString() : "no primary";
            string godRays = primary != null ? (primary.GodRaysAllowed ? "ON" : "off") : "-";
            float fps = _smoothedMs > 0f ? 1000f / _smoothedMs : 0f;

            _readout =
                $"{_smoothedMs:0.0} ms  ({fps:0} fps)   worst {_worstMs:0.0} ms\n" +
                $"[{fogKey}] underwater fog : {fog}\n" +
                $"[{godRayKey}] ocean god rays : {godRays}\n" +
                $"fog pass armed {WaterVolume.UnderwaterFogActive}   submerged {WaterVolume.CameraSubmerged}\n" +
                $"[{visibilityKey}] hide";
        }

        void OnGUI()
        {
            if (!_visible || !Application.isPlaying || _readout.Length == 0) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = FontSize, richText = false };
            // Drawn twice, offset by one pixel in black first, so the text stays readable over both
            // bright sky and dark underwater murk without a background texture asset.
            var shadow = new Rect(MarginPixels + 1, MarginPixels + 1, PanelWidth, PanelHeight);
            var face = new Rect(MarginPixels, MarginPixels, PanelWidth, PanelHeight);
            Color previous = _style.normal.textColor;
            _style.normal.textColor = Color.black;
            GUI.Label(shadow, _readout, _style);
            _style.normal.textColor = Color.white;
            GUI.Label(face, _readout, _style);
            _style.normal.textColor = previous;
        }
    }
}
