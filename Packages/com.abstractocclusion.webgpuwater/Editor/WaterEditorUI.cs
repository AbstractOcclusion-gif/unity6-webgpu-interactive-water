// WebGpuWater - shared blue-themed editor UI helpers for the WaterVolume inspector.
// Mirrors the Luminex EditorUIUtility structure (header, footer, foldout sections,
// toggle-gated sections) but carries the water palette: bright cyan accents on a
// deep-blue bar. Editor-only; no runtime code. All literals live in Style below.
#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    /// <summary>
    /// Centralised UI language for the WaterVolume inspector: a cyan header/footer and
    /// consistent foldout sections. Kept separate from the inspector so the look is defined
    /// in one place and every section reads identically.
    /// </summary>
    internal static class WaterEditorUI
    {
        // ---- header --------------------------------------------------------------------------

        /// <summary>Cyan title bar with an optional subtitle and an accent underline.</summary>
        public static void DrawHeader(string title, string subtitle = null)
        {
            EditorGUILayout.Space(Style.HeaderTopSpacing);

            Rect bar = GUILayoutUtility.GetRect(0f, Style.HeaderBarHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(bar, Style.HeaderBarColor);
            GUI.Label(bar, title, TitleStyle);

            if (!string.IsNullOrEmpty(subtitle))
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(subtitle, SubtitleStyle);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(Style.AccentTopSpacing);
            Rect accent = EditorGUILayout.GetControlRect(false, Style.AccentHeight);
            EditorGUI.DrawRect(accent, Style.AccentColor);
            EditorGUILayout.Space(Style.HeaderBottomSpacing);
        }

        /// <summary>Centered grey footer showing the package name and its resolved version.</summary>
        public static void DrawFooter()
        {
            EditorGUILayout.Space(Style.FooterTopSpacing);
            Rect accent = EditorGUILayout.GetControlRect(false, Style.AccentHeight);
            EditorGUI.DrawRect(accent, Style.AccentColor);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label(FooterText, FooterStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ---- sections ------------------------------------------------------------------------

        /// <summary>A boxed foldout section. When <paramref name="contentEnabled"/> is false the body is
        /// greyed (disabled) but the header still folds, so the reader can see settings that don't apply
        /// to the current body type. Returns the new expanded state.</summary>
        public static bool Section(string title, bool expanded, Action drawContent, bool contentEnabled = true)
        {
            if (!contentEnabled) EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            expanded = EditorGUILayout.Foldout(expanded, title, true, FoldoutStyle);

            if (expanded && drawContent != null)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(Style.ContentTopSpacing);
                drawContent.Invoke();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(Style.SectionBottomSpacing);
            if (!contentEnabled) EditorGUI.EndDisabledGroup();
            return expanded;
        }

        /// <summary>
        /// A boxed foldout section with an enable toggle in the header row; the body is disabled
        /// (greyed) while the toggle is off. Returns the new expanded state.
        /// </summary>
        public static bool SectionWithToggle(string title, bool expanded, SerializedProperty enabled, Action drawContent, bool contentEnabled = true)
        {
            if (enabled == null) throw new ArgumentNullException(nameof(enabled));

            if (!contentEnabled) EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            expanded = EditorGUILayout.Foldout(expanded, title, true, FoldoutStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.PropertyField(enabled, GUIContent.none, GUILayout.Width(Style.ToggleWidth));
            EditorGUILayout.EndHorizontal();

            if (expanded && drawContent != null)
            {
                EditorGUI.BeginDisabledGroup(!enabled.boolValue);
                EditorGUI.indentLevel++;
                EditorGUILayout.Space(Style.ContentTopSpacing);
                drawContent.Invoke();
                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(Style.SectionBottomSpacing);
            if (!contentEnabled) EditorGUI.EndDisabledGroup();
            return expanded;
        }

        /// <summary>A cyan sub-heading used to group fields inside a single section.</summary>
        public static void SubHeading(string text)
        {
            EditorGUILayout.Space(Style.SubHeadingTopSpacing);
            GUILayout.Label(text, SubHeadingStyle);
        }

        /// <summary>A nested collapsible sub-group inside a Section (its own expanded state). When
        /// <paramref name="contentEnabled"/> is false the body is greyed. Returns the new expanded state.</summary>
        public static bool SubSection(string title, bool expanded, Action drawContent, bool contentEnabled = true)
        {
            if (!contentEnabled) EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Space(Style.SubHeadingTopSpacing);
            expanded = EditorGUILayout.Foldout(expanded, title, true, SubSectionFoldoutStyle);

            if (expanded && drawContent != null)
            {
                EditorGUI.indentLevel++;
                drawContent.Invoke();
                EditorGUI.indentLevel--;
            }

            if (!contentEnabled) EditorGUI.EndDisabledGroup();
            return expanded;
        }

        /// <summary>The inspector's top-level category tab bar (Core / Look / ...). One tab's
        /// sections render at a time, so the 20+ foldouts stop being one flat scroll. Returns
        /// the new selected index.</summary>
        public static int TabBar(int selected, string[] labels)
        {
            EditorGUILayout.Space(Style.TabTopSpacing);
            int picked = GUILayout.Toolbar(selected, labels, TabStyle, GUILayout.Height(Style.TabHeight));
            EditorGUILayout.Space(Style.TabBottomSpacing);
            return picked;
        }

        /// <summary>A segmented Pond / Lake / Ocean selector bound to the bodyType enum property.</summary>
        public static void BodyTypeSelector(SerializedProperty bodyType)
        {
            if (bodyType == null) throw new ArgumentNullException(nameof(bodyType));

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Body Type", SubHeadingStyle, GUILayout.Width(Style.BodyTypeLabelWidth));
            int current = bodyType.enumValueIndex;
            int picked = GUILayout.Toolbar(current, bodyType.enumDisplayNames);
            if (picked != current) bodyType.enumValueIndex = picked;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(Style.SectionBottomSpacing);
        }

        // ---- lazy styles ---------------------------------------------------------------------

        private static GUIStyle _titleStyle;
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _footerStyle;
        private static GUIStyle _foldoutStyle;
        private static GUIStyle _subHeadingStyle;

        private static GUIStyle TitleStyle => _titleStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = Style.TitleFontSize,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Style.TitleColor }
        };

        private static GUIStyle SubtitleStyle => _subtitleStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Style.SubtitleColor }
        };

        private static GUIStyle FooterStyle => _footerStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Style.FooterColor }
        };

        private static GUIStyle FoldoutStyle => _foldoutStyle ??= new GUIStyle(EditorStyles.foldoutHeader)
        {
            fontSize = Style.SectionFontSize,
            normal = { textColor = Style.SectionTitleColor },
            onNormal = { textColor = Style.SectionTitleColor }
        };

        private static GUIStyle SubHeadingStyle => _subHeadingStyle ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            normal = { textColor = Style.SubHeadingColor }
        };

        private static GUIStyle _subSectionFoldoutStyle;
        private static GUIStyle SubSectionFoldoutStyle => _subSectionFoldoutStyle ??= new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Style.SubHeadingColor },
            onNormal = { textColor = Style.SubHeadingColor }
        };

        private static GUIStyle _tabStyle;
        private static GUIStyle TabStyle => _tabStyle ??= new GUIStyle(EditorStyles.toolbarButton)
        {
            fontSize = Style.TabFontSize,
            fontStyle = FontStyle.Bold,
            // Selected tab reads in the section-title cyan so the active category matches the
            // rest of the water palette; idle tabs keep the toolbar's neutral grey.
            onNormal = { textColor = Style.SectionTitleColor },
            onHover = { textColor = Style.SectionTitleColor },
            onFocused = { textColor = Style.SectionTitleColor }
        };

        /// <summary>The "one place for foam + splash" button, shared by the splash-emitter and
        /// foam-particles inspectors. Both drew the identical control; only how they find the owning
        /// body differed, so that stays with the caller. With a profile linked this pings it; with
        /// none it creates one and assigns it to the whole body (foam AND splash) so the two are
        /// never configured from two separate assets.</summary>
        internal static void DrawFoamProfileLink(SerializedObject serializedObject,
                                                 SerializedProperty profile, WaterVolume body)
        {
            var linked = profile.objectReferenceValue as WaterFoamProfile;
            if (linked != null)
            {
                if (GUILayout.Button("Edit Foam Profile"))
                {
                    Selection.activeObject = linked;
                    EditorGUIUtility.PingObject(linked);
                }
                return;
            }

            if (!GUILayout.Button("Create & Assign Foam Profile (one place for foam + splash)"))
                return;

            var created = WaterBuildKit.LoadOrCreateFoamProfile();
            if (body != null)
            {
                WaterBuildKit.AssignFoamProfileToBody(body, created);
                serializedObject.Update();
            }
            else
            {
                profile.objectReferenceValue = created;
            }
            Selection.activeObject = created;
            EditorGUIUtility.PingObject(created);
        }

        // ---- footer text (resolved package version, no hardcoded number) ---------------------

        private static string _footerText;
        private static string FooterText
        {
            get
            {
                if (_footerText != null) return _footerText;
                // Null off a package install (the Asset Store path has no manifest to read a
                // version from) - the footer simply drops the version rather than reading "v".
                string version = WaterPackagePaths.Version;
                _footerText = version != null
                    ? Style.FooterPrefix + "  v" + version
                    : Style.FooterPrefix;
                return _footerText;
            }
        }

        // ---- palette + metrics (single source of truth; no inline literals elsewhere) --------

        private static class Style
        {
            // Palette - bright cyan accents on a deep-blue bar (water spirit).
            public static readonly Color TitleColor = new Color(0.55f, 0.87f, 1f);
            public static readonly Color SubtitleColor = new Color(0.55f, 0.66f, 0.74f);
            public static readonly Color FooterColor = new Color(0.45f, 0.55f, 0.62f);
            public static readonly Color HeaderBarColor = new Color(0.16f, 0.42f, 0.62f, 0.30f);
            public static readonly Color AccentColor = new Color(0.30f, 0.64f, 0.92f, 1f);
            public static readonly Color SectionTitleColor = new Color(0.62f, 0.86f, 1f);
            public static readonly Color SubHeadingColor = new Color(0.50f, 0.80f, 0.98f);

            // Metrics.
            public const int TitleFontSize = 15;
            public const int SectionFontSize = 12;
            public const float HeaderTopSpacing = 4f;
            public const float HeaderBarHeight = 26f;
            public const float AccentTopSpacing = 3f;
            public const float AccentHeight = 2f;
            public const float HeaderBottomSpacing = 6f;
            public const float FooterTopSpacing = 12f;
            public const float ContentTopSpacing = 2f;
            public const float SectionBottomSpacing = 3f;
            public const float SubHeadingTopSpacing = 4f;
            public const float ToggleWidth = 20f;
            public const float BodyTypeLabelWidth = 70f;
            public const int TabFontSize = 11;
            public const float TabHeight = 22f;
            public const float TabTopSpacing = 2f;
            public const float TabBottomSpacing = 6f;

            public const string FooterPrefix = "AbstractOcclusion  ·  WebGPU Water";
        }
    }
}
#endif
