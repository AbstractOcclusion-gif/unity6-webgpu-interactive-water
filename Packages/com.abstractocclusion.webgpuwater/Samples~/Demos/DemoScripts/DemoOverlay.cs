// DemoOverlay - on-screen explanation panel for a demo scene.
//
// Drop this on any GameObject in a demo scene, type what the scene shows into Description, and it
// paints a titled panel in a screen corner while playing. Pairs with FlyCamera (it can append the
// fly controls) so a viewer knows both what they're looking at and how to move around. Editor-time
// only aid: it draws through OnGUI, so there is nothing to build into a shipping product.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Demo
{
    [AddComponentMenu("AbstractOcclusion/Demo/Demo Overlay")]
    [DisallowMultipleComponent]
    public sealed class DemoOverlay : MonoBehaviour
    {
        enum ScreenCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        [Header("Content")]
        [Tooltip("Heading shown in bold at the top of the panel.")]
        [SerializeField] string title = "WebGPU Water Demo";
        [Tooltip("What this scene shows. Supports multiple lines and <b>rich text</b>.")]
        [TextArea(3, 12)] [SerializeField] string description =
            "Describe what this demo scene shows here.";
        [Tooltip("Append the FlyCamera controls to the panel.")]
        [SerializeField] bool showCameraControls = true;

        [Header("Placement")]
        [SerializeField] ScreenCorner corner = ScreenCorner.TopLeft;
        [Tooltip("Pixels between the panel and the screen edges.")]
        [Min(0f)] [SerializeField] float margin = 16f;
        [Tooltip("Panel width in pixels.")]
        [Min(120f)] [SerializeField] float panelWidth = 380f;

        [Header("Style")]
        [Range(8, 48)] [SerializeField] int titleFontSize = 18;
        [Range(8, 32)] [SerializeField] int bodyFontSize = 13;
        [Tooltip("Panel backing colour; the alpha shades the dark box behind the text.")]
        [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0.55f);
        [SerializeField] Color textColor = Color.white;

        [Header("Toggle")]
        [Tooltip("Key that shows/hides the panel at runtime.")]
        [SerializeField] KeyCode toggleKey = KeyCode.F1;
        [Tooltip("Start with the panel visible when the scene plays.")]
        [SerializeField] bool visibleOnStart = true;

        // The fly-controls line is fixed text, kept here so it can never drift from FlyCamera's own
        // header comment when someone edits the Description field.
        const string CameraControlsText =
            "Fly Camera - hold RIGHT mouse to look, WASD move, Q/E down/up, Shift sprint.";
        const float PanelPadding = 12f;
        const float TitleBodyGap = 6f;
        const float HintGap = 6f;

        bool _visible;
        Texture2D _backingTexture; // 1x1 white, tinted per-draw by GUI.color
        // Styles cached across OnGUI events (OnGUI fires 2+ times/frame; building GUIStyles there
        // allocates every event). Rebuilt when the inspector changes a style field (OnValidate).
        GUIStyle _titleStyle, _bodyStyle, _hintStyle;

        void OnEnable()
        {
            _visible = visibleOnStart;
            _backingTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _backingTexture.SetPixel(0, 0, Color.white);
            _backingTexture.Apply();
        }

        void OnDisable()
        {
            if (_backingTexture == null) return;
            Destroy(_backingTexture);
            _backingTexture = null;
        }

        void OnValidate() => _titleStyle = _bodyStyle = _hintStyle = null;

        void Update()
        {
            if (TogglePressed()) _visible = !_visible;
        }

        // Toggle key on either input backend: the legacy API throws on Input-System-only projects.
        // KeyCode names map onto InputSystem.Key names for the plain keys a toggle would use (F1..F12,
        // Space, Tab, letters); an unmappable binding just disables the toggle instead of throwing.
        bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return false;
            if (!System.Enum.TryParse(toggleKey.ToString(), true, out UnityEngine.InputSystem.Key key))
                return false;
            return keyboard[key].wasPressedThisFrame;
#else
            return Input.GetKeyDown(toggleKey);
#endif
        }

        void OnGUI()
        {
            if (!_visible) return;

            if (_titleStyle == null || _bodyStyle == null || _hintStyle == null)
            {
                _titleStyle = BuildLabelStyle(titleFontSize, FontStyle.Bold);
                _bodyStyle = BuildLabelStyle(bodyFontSize, FontStyle.Normal);
                _hintStyle = BuildLabelStyle(Mathf.Max(8, bodyFontSize - 2), FontStyle.Italic);
            }
            GUIStyle titleStyle = _titleStyle;
            GUIStyle bodyStyle = _bodyStyle;
            GUIStyle hintStyle = _hintStyle;

            string body = showCameraControls ? description + "\n\n" + CameraControlsText : description;
            string hint = $"[{toggleKey}] hide";

            float contentWidth = panelWidth - PanelPadding * 2f;
            float titleHeight = titleStyle.CalcHeight(new GUIContent(title), contentWidth);
            float bodyHeight = bodyStyle.CalcHeight(new GUIContent(body), contentWidth);
            float hintHeight = hintStyle.CalcHeight(new GUIContent(hint), contentWidth);
            float panelHeight = PanelPadding * 2f + titleHeight + TitleBodyGap + bodyHeight + HintGap + hintHeight;

            Rect panel = PanelRect(panelWidth, panelHeight);
            DrawBacking(panel);

            float x = panel.x + PanelPadding;
            float y = panel.y + PanelPadding;
            GUI.Label(new Rect(x, y, contentWidth, titleHeight), title, titleStyle);
            y += titleHeight + TitleBodyGap;
            GUI.Label(new Rect(x, y, contentWidth, bodyHeight), body, bodyStyle);
            y += bodyHeight + HintGap;
            GUI.Label(new Rect(x, y, contentWidth, hintHeight), hint, hintStyle);
        }

        GUIStyle BuildLabelStyle(int fontSize, FontStyle fontStyle) => new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = fontStyle,
            wordWrap = true,
            richText = true,
            normal = { textColor = textColor }
        };

        Rect PanelRect(float width, float height)
        {
            bool left = corner == ScreenCorner.TopLeft || corner == ScreenCorner.BottomLeft;
            bool top = corner == ScreenCorner.TopLeft || corner == ScreenCorner.TopRight;
            float x = left ? margin : Screen.width - width - margin;
            float y = top ? margin : Screen.height - height - margin;
            return new Rect(x, y, width, height);
        }

        void DrawBacking(Rect rect)
        {
            Color previous = GUI.color;
            GUI.color = backgroundColor;
            GUI.DrawTexture(rect, _backingTexture);
            GUI.color = previous;
        }
    }
}
