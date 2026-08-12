// WebGpuWater - on-screen caption for the sample scenes.
//
// Draws one IMGUI panel naming the scene and saying, in a short paragraph, what it is meant to show, so
// a demo opened straight out of the Samples folder explains itself without the README next to it. An
// optional smoothed frame-rate line rides along; the deeper numbers (buoyancy batch cost, floater count)
// stay in WaterMetricsOverlay, which is a separate, development-only component.
//
// Unlike WaterMetricsOverlay this is NOT gated on DEVELOPMENT_BUILD: the caption is demo content, so a
// release WebGL build of a sample scene still names itself.
using System.Text;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Screen corner a <see cref="WaterDemoOverlay"/> panel is pinned to.</summary>
    public enum WaterOverlayCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Demo Overlay")]
    [DisallowMultipleComponent]
    public sealed class WaterDemoOverlay : MonoBehaviour
    {
        // Inspector group labels.
        const string CaptionHeader = "Caption";
        const string LayoutHeader = "Layout";
        const string AppearanceHeader = "Appearance";
        const string FrameRateHeader = "Frame Rate";

        // Defaults. Every tweakable below starts from one of these, so the shipped look lives in one place.
        const float DefaultPanelWidth = 460f;
        const float DefaultScreenMargin = 14f;
        const int DefaultPaddingHorizontal = 12;
        const int DefaultPaddingVertical = 10;
        const int DefaultTitleFontSize = 15;
        const int DefaultBodyFontSize = 12;
        const float DefaultUiScale = 1f;
        const float DefaultFrameSmoothing = 0.05f;

        // Inspector slider bounds.
        const float MinPanelWidth = 160f;
        const float MaxPanelWidth = 1200f;
        const float MinScreenMargin = 0f;
        const float MaxScreenMargin = 120f;
        const float MinPadding = 0f;
        const float MaxPadding = 48f;
        const float MinFontSize = 8f;
        const float MaxFontSize = 48f;
        const float MinUiScale = 0.5f;
        const float MaxUiScale = 4f;
        const float MinFrameSmoothing = 0.01f;
        const float MaxFrameSmoothing = 1f;

        const float MillisecondsPerSecond = 1000f;
        const float FrameRateRefreshSeconds = 0.25f;
        const string TitleFormat = "<size={0}><b>{1}</b></size>";
        const string FrameRateFormat = "{0:0} fps  ({1:0.0} ms)";
        const string ParagraphSeparator = "\n\n";

        [Header(CaptionHeader)]
        [Tooltip("Scene name, drawn bold on the first line.")]
        [SerializeField] string title = string.Empty;

        [Tooltip("One short paragraph on what this scene demonstrates.")]
        [TextArea(2, 6)]
        [SerializeField] string description = string.Empty;

        [Header(LayoutHeader)]
        [Tooltip("Screen corner the panel is pinned to.")]
        [SerializeField] WaterOverlayCorner corner = WaterOverlayCorner.BottomLeft;

        [Tooltip("Panel width in scaled pixels; the height follows the wrapped text.")]
        [Range(MinPanelWidth, MaxPanelWidth)]
        [SerializeField] float panelWidth = DefaultPanelWidth;

        [Tooltip("Gap between the panel and the two screen edges it hugs.")]
        [Range(MinScreenMargin, MaxScreenMargin)]
        [SerializeField] float screenMargin = DefaultScreenMargin;

        [Tooltip("Inner padding, left and right.")]
        [Range(MinPadding, MaxPadding)]
        [SerializeField] int paddingHorizontal = DefaultPaddingHorizontal;

        [Tooltip("Inner padding, top and bottom.")]
        [Range(MinPadding, MaxPadding)]
        [SerializeField] int paddingVertical = DefaultPaddingVertical;

        [Header(AppearanceHeader)]
        [Tooltip("Point size of the bold title line.")]
        [Range(MinFontSize, MaxFontSize)]
        [SerializeField] int titleFontSize = DefaultTitleFontSize;

        [Tooltip("Point size of the description and the frame-rate line.")]
        [Range(MinFontSize, MaxFontSize)]
        [SerializeField] int bodyFontSize = DefaultBodyFontSize;

        [Tooltip("Text colour.")]
        [SerializeField] Color textColor = Color.white;

        [Tooltip("Multiplied into the panel background; drop the alpha to fade the box out.")]
        [SerializeField] Color backgroundTint = Color.white;

        [Tooltip("Whole-panel scale. Raise it on high-DPI and mobile screens, where the panel reads tiny.")]
        [Range(MinUiScale, MaxUiScale)]
        [SerializeField] float uiScale = DefaultUiScale;

        [Header(FrameRateHeader)]
        [Tooltip("Append a smoothed frame rate. WaterMetricsOverlay carries the deeper profiler numbers.")]
        [SerializeField] bool showFrameRate = true;

        [Tooltip("Exponential smoothing on the frame time. Higher reacts faster and reads noisier.")]
        [Range(MinFrameSmoothing, MaxFrameSmoothing)]
        [SerializeField] float frameSmoothing = DefaultFrameSmoothing;

        readonly StringBuilder _builder = new StringBuilder(256);
        readonly GUIContent _panelContent = new GUIContent();
        GUIStyle _panelStyle;   // built lazily in OnGUI (GUI.skin only exists there)
        float _smoothedFrameMs;
        float _nextFrameRateRefreshTime;
        bool _textDirty = true;

        /// <summary>Retitles the panel at runtime, e.g. from a scene cycler.</summary>
        public void SetCaption(string newTitle, string newDescription)
        {
            title = newTitle;
            description = newDescription;
            _textDirty = true;
        }

        void OnEnable()
        {
            _textDirty = true;
            _panelStyle = null;
            _nextFrameRateRefreshTime = 0f;
        }

        void Update()
        {
            // The caption never changes on its own, so it is built once. Smooth frame time every frame,
            // but refresh the allocating formatted string at a human-readable rate instead of at render FPS.
            if (!showFrameRate)
            {
                RebuildPanelTextIfDirty();
                return;
            }

            SmoothFrameTime();
            if (Time.unscaledTime < _nextFrameRateRefreshTime) return;
            _nextFrameRateRefreshTime = Time.unscaledTime + FrameRateRefreshSeconds;
            RebuildPanelText();
        }

        void OnGUI()
        {
            RebuildPanelTextIfDirty();
            if (string.IsNullOrEmpty(_panelContent.text)) return;

            EnsureStyle();

            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousBackground = GUI.backgroundColor;
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
            GUI.backgroundColor = backgroundTint;

            GUI.Box(MeasurePanel(), _panelContent, _panelStyle);

            GUI.backgroundColor = previousBackground;
            GUI.matrix = previousMatrix;
        }

        void SmoothFrameTime()
        {
            float frameMs = Time.unscaledDeltaTime * MillisecondsPerSecond;
            _smoothedFrameMs = _smoothedFrameMs <= 0f ? frameMs : Mathf.Lerp(_smoothedFrameMs, frameMs, frameSmoothing);
        }

        void RebuildPanelTextIfDirty()
        {
            if (!_textDirty) return;
            RebuildPanelText();
        }

        void RebuildPanelText()
        {
            _builder.Clear();

            if (!string.IsNullOrEmpty(title)) _builder.AppendFormat(TitleFormat, titleFontSize, title);
            if (!string.IsNullOrEmpty(description)) AppendParagraph(description);
            if (showFrameRate) AppendFrameRate();

            _panelContent.text = _builder.ToString();
            _textDirty = false;
        }

        void AppendParagraph(string paragraph)
        {
            AppendSeparatorIfNeeded();
            _builder.Append(paragraph);
        }

        void AppendFrameRate()
        {
            float framesPerSecond = _smoothedFrameMs > 0f ? MillisecondsPerSecond / _smoothedFrameMs : 0f;
            AppendSeparatorIfNeeded();
            _builder.AppendFormat(FrameRateFormat, framesPerSecond, _smoothedFrameMs);
        }

        void AppendSeparatorIfNeeded()
        {
            if (_builder.Length > 0) _builder.Append(ParagraphSeparator);
        }

        void EnsureStyle()
        {
            if (_panelStyle != null) return;
            _panelStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                richText = true,
                wordWrap = true,
                fontSize = bodyFontSize,
                padding = new RectOffset(paddingHorizontal, paddingHorizontal, paddingVertical, paddingVertical),
            };
            _panelStyle.normal.textColor = textColor;
        }

        Rect MeasurePanel()
        {
            // GUI.matrix scales everything drawn, so the layout works in scaled pixels: the usable screen
            // shrinks by the same factor the panel grows.
            float scaledScreenWidth = Screen.width / uiScale;
            float scaledScreenHeight = Screen.height / uiScale;
            float height = _panelStyle.CalcHeight(_panelContent, panelWidth);

            float x = IsRightAligned(corner) ? scaledScreenWidth - panelWidth - screenMargin : screenMargin;
            float y = IsBottomAligned(corner) ? scaledScreenHeight - height - screenMargin : screenMargin;
            return new Rect(x, y, panelWidth, height);
        }

        static bool IsRightAligned(WaterOverlayCorner value) =>
            value == WaterOverlayCorner.TopRight || value == WaterOverlayCorner.BottomRight;

        static bool IsBottomAligned(WaterOverlayCorner value) =>
            value == WaterOverlayCorner.BottomLeft || value == WaterOverlayCorner.BottomRight;

#if UNITY_EDITOR
        // Style fields bake into the cached GUIStyle, so a tweak has to drop it as well as the text.
        void OnValidate()
        {
            _textDirty = true;
            _panelStyle = null;
        }
#endif
    }
}
