using System.Text;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Demo Depth Gauge")]
    [DisallowMultipleComponent]
    public sealed class WaterDemoDepthGauge : MonoBehaviour
    {
        enum DepthMotionState { Holding, Descending, Ascending }

        const float DefaultMaximumDepth = 70f;
        const float DefaultGaugeWidth = 170f;
        const float DefaultGaugeHeight = 360f;
        const float DefaultScreenMargin = 14f;
        const float DefaultUiScale = 1f;
        const float DefaultSmoothing = 0.12f;
        const float MinimumMaximumDepth = 1f;
        const float MinimumGaugeWidth = 120f;
        const float MaximumGaugeWidth = 320f;
        const float MinimumGaugeHeight = 220f;
        const float MaximumGaugeHeight = 720f;
        const float MinimumUiScale = 0.5f;
        const float MaximumUiScale = 4f;
        const float MinimumSmoothing = 0.01f;
        const float MaximumSmoothing = 1f;
        const float MotionThresholdMetersPerSecond = 0.05f;
        const float TrackWidth = 12f;
        const float MajorTickWidth = 20f;
        const float MinorTickWidth = 10f;
        const float TickThickness = 2f;
        const float MarkerWidth = 34f;
        const float MarkerHeight = 4f;
        const float HeaderHeight = 76f;
        const float FooterHeight = 34f;
        const float HorizontalPadding = 18f;
        const float TrackLabelGap = 12f;
        const float HeaderTopPadding = 10f;
        const float TitleHeightPadding = 6f;
        const float DepthHeightPadding = 8f;
        const float FooterVerticalPadding = 4f;
        const float TickLabelWidthFraction = 0.45f;
        const float TickLabelHeightMultiplier = 2f;
        const float Half = 0.5f;
        const float UnitScale = 1f;
        const int MajorIntervalCount = 7;
        const int MinorTicksPerMajorInterval = 2;
        const int TitleFontSize = 18;
        const int DepthFontSize = 24;
        const int LabelFontSize = 13;
        const string TitleText = "DEPTH";
        const string DepthFormat = "{0:0.0} m";
        const string TickLabelFormat = "{0:0}";
        const string HoldingText = "HOLDING";
        const string DescendingText = "DESCENDING";
        const string AscendingText = "ASCENDING";

        [SerializeField] Transform depthTarget;
        [SerializeField] Transform surfaceReference;
        [Min(MinimumMaximumDepth)] [SerializeField] float maximumDepth = DefaultMaximumDepth;
        [SerializeField] WaterOverlayCorner corner = WaterOverlayCorner.BottomRight;
        [Range(MinimumGaugeWidth, MaximumGaugeWidth)] [SerializeField] float gaugeWidth = DefaultGaugeWidth;
        [Range(MinimumGaugeHeight, MaximumGaugeHeight)] [SerializeField] float gaugeHeight = DefaultGaugeHeight;
        [Min(0f)] [SerializeField] float screenMargin = DefaultScreenMargin;
        [Range(MinimumUiScale, MaximumUiScale)] [SerializeField] float uiScale = DefaultUiScale;
        [Range(MinimumSmoothing, MaximumSmoothing)] [SerializeField] float smoothing = DefaultSmoothing;
        [SerializeField] Color textColor = Color.white;
        [SerializeField] Color trackColor = new Color(0.12f, 0.55f, 0.72f, 0.9f);
        [SerializeField] Color markerColor = new Color(1f, 0.75f, 0.18f, UnitScale);
        [SerializeField] Color backgroundTint = new Color(UnitScale, UnitScale, UnitScale, 0.85f);

        readonly StringBuilder _builder = new StringBuilder(32);
        GUIStyle _panelStyle;
        GUIStyle _titleStyle;
        GUIStyle _depthStyle;
        GUIStyle _labelStyle;
        float _smoothedDepth;
        float _previousDepth;
        float _verticalSpeed;
        bool _hasSample;

        void Awake()
        {
            if (depthTarget != null && surfaceReference != null) return;
            Debug.LogError("WaterDemoDepthGauge requires a depth target and surface reference.", this);
            enabled = false;
        }

        void Update()
        {
            float rawDepth = MeasureDepth();
            if (!_hasSample)
            {
                _smoothedDepth = rawDepth;
                _previousDepth = rawDepth;
                _hasSample = true;
                return;
            }

            float deltaTime = Mathf.Max(Time.unscaledDeltaTime, Mathf.Epsilon);
            _smoothedDepth = Mathf.Lerp(_smoothedDepth, rawDepth, smoothing);
            _verticalSpeed = (_smoothedDepth - _previousDepth) / deltaTime;
            _previousDepth = _smoothedDepth;
        }

        void OnGUI()
        {
            EnsureStyles();
            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousBackground = GUI.backgroundColor;
            Color previousColor = GUI.color;
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, UnitScale));
            GUI.backgroundColor = backgroundTint;

            Rect panel = GaugeRect();
            GUI.Box(panel, GUIContent.none, _panelStyle);
            DrawHeader(panel);
            DrawTrack(panel);
            DrawFooter(panel);

            GUI.color = previousColor;
            GUI.backgroundColor = previousBackground;
            GUI.matrix = previousMatrix;
        }

        float MeasureDepth()
        {
            Vector3 surfaceUp = surfaceReference.up;
            return Mathf.Max(0f, Vector3.Dot(surfaceReference.position - depthTarget.position, surfaceUp));
        }

        void DrawHeader(Rect panel)
        {
            Rect titleRect = new Rect(panel.x, panel.y + HeaderTopPadding,
                                      panel.width, TitleFontSize + TitleHeightPadding);
            GUI.Label(titleRect, TitleText, _titleStyle);
            _builder.Clear();
            _builder.AppendFormat(DepthFormat, _smoothedDepth);
            Rect depthRect = new Rect(panel.x, titleRect.yMax,
                                      panel.width, DepthFontSize + DepthHeightPadding);
            GUI.Label(depthRect, _builder.ToString(), _depthStyle);
        }

        void DrawTrack(Rect panel)
        {
            float trackTop = panel.y + HeaderHeight;
            float trackBottom = panel.yMax - FooterHeight;
            float trackHeight = trackBottom - trackTop;
            float trackX = panel.x + HorizontalPadding + MajorTickWidth;
            Rect trackRect = new Rect(trackX, trackTop, TrackWidth, trackHeight);
            DrawSolidRect(trackRect, trackColor);

            int totalIntervals = MajorIntervalCount * MinorTicksPerMajorInterval;
            for (int tickIndex = 0; tickIndex <= totalIntervals; tickIndex++)
            {
                float fraction = tickIndex / (float)totalIntervals;
                float tickY = Mathf.Lerp(trackTop, trackBottom, fraction);
                bool isMajor = tickIndex % MinorTicksPerMajorInterval == 0;
                float tickWidth = isMajor ? MajorTickWidth : MinorTickWidth;
                DrawSolidRect(new Rect(trackX - tickWidth, tickY - TickThickness * Half,
                                       tickWidth, TickThickness), textColor);
                if (isMajor) DrawTickLabel(trackRect, tickY, fraction);
            }

            float depthFraction = Mathf.Clamp01(_smoothedDepth / Mathf.Max(maximumDepth, MinimumMaximumDepth));
            float markerY = Mathf.Lerp(trackTop, trackBottom, depthFraction);
            DrawSolidRect(new Rect(trackX - MarkerWidth * Half + TrackWidth * Half,
                                   markerY - MarkerHeight * Half, MarkerWidth, MarkerHeight), markerColor);
        }

        void DrawTickLabel(Rect trackRect, float tickY, float fraction)
        {
            _builder.Clear();
            _builder.AppendFormat(TickLabelFormat, maximumDepth * fraction);
            Rect labelRect = new Rect(trackRect.xMax + TrackLabelGap,
                                      tickY - LabelFontSize, gaugeWidth * TickLabelWidthFraction,
                                      LabelFontSize * TickLabelHeightMultiplier);
            GUI.Label(labelRect, _builder.ToString(), _labelStyle);
        }

        void DrawFooter(Rect panel)
        {
            Rect footer = new Rect(panel.x, panel.yMax - FooterHeight,
                                   panel.width, FooterHeight - FooterVerticalPadding);
            GUI.Label(footer, MotionStateText(), _labelStyle);
        }

        string MotionStateText()
        {
            DepthMotionState state = Mathf.Abs(_verticalSpeed) <= MotionThresholdMetersPerSecond
                ? DepthMotionState.Holding
                : (_verticalSpeed > 0f ? DepthMotionState.Descending : DepthMotionState.Ascending);
            return state switch
            {
                DepthMotionState.Descending => DescendingText,
                DepthMotionState.Ascending => AscendingText,
                _ => HoldingText,
            };
        }

        void EnsureStyles()
        {
            if (_panelStyle != null) return;
            _panelStyle = new GUIStyle(GUI.skin.box);
            _titleStyle = CenteredStyle(TitleFontSize, FontStyle.Bold);
            _depthStyle = CenteredStyle(DepthFontSize, FontStyle.Bold);
            _labelStyle = CenteredStyle(LabelFontSize, FontStyle.Normal);
        }

        GUIStyle CenteredStyle(int fontSize, FontStyle fontStyle)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = fontSize,
                fontStyle = fontStyle,
            };
            style.normal.textColor = textColor;
            return style;
        }

        Rect GaugeRect()
        {
            float scaledScreenWidth = Screen.width / uiScale;
            float scaledScreenHeight = Screen.height / uiScale;
            float x = IsRightAligned(corner) ? scaledScreenWidth - gaugeWidth - screenMargin : screenMargin;
            float y = IsBottomAligned(corner) ? scaledScreenHeight - gaugeHeight - screenMargin : screenMargin;
            return new Rect(x, y, gaugeWidth, gaugeHeight);
        }

        static void DrawSolidRect(Rect rect, Color color)
        {
            Color previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        static bool IsRightAligned(WaterOverlayCorner value) =>
            value == WaterOverlayCorner.TopRight || value == WaterOverlayCorner.BottomRight;

        static bool IsBottomAligned(WaterOverlayCorner value) =>
            value == WaterOverlayCorner.BottomLeft || value == WaterOverlayCorner.BottomRight;

#if UNITY_EDITOR
        void OnValidate()
        {
            maximumDepth = Mathf.Max(maximumDepth, MinimumMaximumDepth);
            _panelStyle = null;
        }
#endif
    }
}
