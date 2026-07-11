// WebGpuWater - on-screen performance overlay for the buoyancy stress test.
//
// Shows the frame time / FPS, the batched buoyancy query cost (read from the "WaterVolume.SampleHeights"
// ProfilerMarker via a ProfilerRecorder), and the live floater count, so the probe-buoyancy budget can be
// read at a glance while dialling the stress grid. A best-effort physics-step line is shown when the
// player exposes that marker. Add it to any GameObject; it draws with OnGUI.
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Metrics Overlay")]
    [DisallowMultipleComponent]
    public sealed class WaterMetricsOverlay : MonoBehaviour
    {
        const float FrameSmoothing = 0.05f;      // exponential smoothing on the frame time
        const float NanosecondsToMilliseconds = 1e-6f;
        const float PanelWidth = 260f;
        const float PanelMargin = 12f;

        [SerializeField] string title = "Buoyancy Stress Test";

        ProfilerRecorder _queryRecorder;
        ProfilerRecorder _physicsRecorder;
        float _smoothedFrameMs;
        int _floaterCount;
        GUIStyle _panelStyle;
        readonly StringBuilder _text = new StringBuilder(128);

        /// <summary>Set by the spawner once the grid is built.</summary>
        public void SetFloaterCount(int count) => _floaterCount = count;

        void OnEnable()
        {
            // The buoyancy batch marker lives in the Scripts category (see WaterVolume.Query.cs). Physics is
            // best-effort: the marker name varies by version, so the line is hidden when the recorder is invalid.
            _queryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "WaterVolume.SampleHeights");
            _physicsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Physics.Simulate");
        }

        void OnDisable()
        {
            _queryRecorder.Dispose();
            _physicsRecorder.Dispose();
        }

        void Update()
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            _smoothedFrameMs = _smoothedFrameMs <= 0f ? frameMs : Mathf.Lerp(_smoothedFrameMs, frameMs, FrameSmoothing);
        }

        void OnGUI()
        {
            EnsureStyle();
            BuildText();
            var rect = new Rect(PanelMargin, PanelMargin, PanelWidth,
                                _panelStyle.CalcHeight(new GUIContent(_text.ToString()), PanelWidth));
            GUI.Box(rect, _text.ToString(), _panelStyle);
        }

        void BuildText()
        {
            float fps = _smoothedFrameMs > 0f ? 1000f / _smoothedFrameMs : 0f;
            _text.Clear();
            _text.Append(title).Append('\n');
            _text.AppendFormat("Floaters: {0}\n", _floaterCount);
            _text.AppendFormat("Frame: {0:0.0} ms  ({1:0} fps)\n", _smoothedFrameMs, fps);
            _text.AppendFormat("Buoyancy query: {0:0.00} ms", QueryMilliseconds());
            if (_physicsRecorder.Valid && _physicsRecorder.LastValue > 0)
                _text.AppendFormat("\nPhysics: {0:0.00} ms", _physicsRecorder.LastValue * NanosecondsToMilliseconds);
        }

        float QueryMilliseconds()
            => _queryRecorder.Valid ? _queryRecorder.LastValue * NanosecondsToMilliseconds : 0f;

        void EnsureStyle()
        {
            if (_panelStyle != null) return;
            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 13,
                padding = new RectOffset(10, 10, 8, 8),
                richText = false,
            };
            _panelStyle.normal.textColor = Color.white;
        }
    }
}
