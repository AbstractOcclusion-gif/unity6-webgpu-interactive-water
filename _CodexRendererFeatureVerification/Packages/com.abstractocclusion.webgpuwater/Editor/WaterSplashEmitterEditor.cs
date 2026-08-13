// WebGpuWater - inspector for WaterSplashEmitter. Mirrors the honest-inspector rule of
// WaterFoamParticlesEditor: when a Foam Profile drives the Splash section, the driven
// fields are DISABLED (not just warned about), so users can't type into values the
// profile overwrites on the next emit. Drift fields stay editable - the profile does
// not drive them. Fields go through SerializedProperty so Undo/multi-object edit work.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterSplashEmitter))]
    [CanEditMultipleObjects]
    internal sealed class WaterSplashEmitterEditor : UnityEditor.Editor
    {
        SerializedProperty _particles, _profile;
        SerializedProperty _maxParticlesPerBurst, _upwardBias, _outwardSpread, _dropletSize, _lifetime;
        SerializedProperty _popDuration, _driftStrength, _driftDamping, _surfaceRideHeight;
        SerializedProperty _crownParticles, _crownMinStrength, _crownBaseSize, _crownLifetime,
            _crownLaunchHeight, _crownLaunchSpread;
        SerializedProperty _crownTint, _crownOpacity, _dropletOpacity;
        SerializedProperty _jetParticles, _entryStreaksEnabled, _entryStreakAmount, _entryStreakHeight,
            _entryStreakWidth, _entryStreakGravity, _entryStreakOpacity, _entryStreakMinStrength,
            _entryStreakLifetimeRange, _entryStreakSizeRange, _entryStreakTint;

        bool _wiringExpanded = true;
        bool _burstExpanded = true;
        bool _driftExpanded;
        bool _crownExpanded = true;
        bool _streaksExpanded = true;

        // Refreshed each GUI pass: true while the assigned profile's Splash section drives
        // the burst/crown fields below.
        bool _splashDriven;

        void OnEnable()
        {
            _particles = serializedObject.FindProperty("particles");
            _profile = serializedObject.FindProperty("profile");
            _maxParticlesPerBurst = serializedObject.FindProperty("maxParticlesPerBurst");
            _upwardBias = serializedObject.FindProperty("upwardBias");
            _outwardSpread = serializedObject.FindProperty("outwardSpread");
            _dropletSize = serializedObject.FindProperty("dropletSize");
            _lifetime = serializedObject.FindProperty("lifetime");
            _popDuration = serializedObject.FindProperty("popDuration");
            _driftStrength = serializedObject.FindProperty("driftStrength");
            _driftDamping = serializedObject.FindProperty("driftDamping");
            _surfaceRideHeight = serializedObject.FindProperty("surfaceRideHeight");
            _crownParticles = serializedObject.FindProperty("crownParticles");
            _crownMinStrength = serializedObject.FindProperty("crownMinStrength");
            _crownBaseSize = serializedObject.FindProperty("crownBaseSize");
            _crownLifetime = serializedObject.FindProperty("crownLifetime");
            _crownLaunchHeight = serializedObject.FindProperty("crownLaunchHeight");
            _crownLaunchSpread = serializedObject.FindProperty("crownLaunchSpread");
            _crownTint = serializedObject.FindProperty("crownTint");
            _crownOpacity = serializedObject.FindProperty("crownOpacity");
            _dropletOpacity = serializedObject.FindProperty("dropletOpacity");
            _jetParticles = serializedObject.FindProperty("jetParticles");
            _entryStreaksEnabled = serializedObject.FindProperty("entryStreaksEnabled");
            _entryStreakAmount = serializedObject.FindProperty("entryStreakAmount");
            _entryStreakHeight = serializedObject.FindProperty("entryStreakHeight");
            _entryStreakWidth = serializedObject.FindProperty("entryStreakWidth");
            _entryStreakGravity = serializedObject.FindProperty("entryStreakGravity");
            _entryStreakOpacity = serializedObject.FindProperty("entryStreakOpacity");
            _entryStreakMinStrength = serializedObject.FindProperty("entryStreakMinStrength");
            _entryStreakLifetimeRange = serializedObject.FindProperty("entryStreakLifetimeRange");
            _entryStreakSizeRange = serializedObject.FindProperty("entryStreakSizeRange");
            _entryStreakTint = serializedObject.FindProperty("entryStreakTint");
        }

        public override void OnInspectorGUI()
        {
            WaterEditorUI.DrawHeader("Water Splash Emitter", "impact droplets + entry streaks + crown ring");
            serializedObject.Update();

            var profile = _profile.objectReferenceValue as WaterFoamProfile;
            _splashDriven = profile != null && profile.splash.drive;

            DrawStatus();

            _wiringExpanded = WaterEditorUI.Section("Wiring & Profile", _wiringExpanded, DrawWiring);
            _burstExpanded = WaterEditorUI.Section("Burst Shaping", _burstExpanded, DrawBurst);
            _driftExpanded = WaterEditorUI.Section("Surface Drift", _driftExpanded, DrawDrift);
            _crownExpanded = WaterEditorUI.Section("Crown Ring", _crownExpanded, DrawCrown);
            _streaksExpanded = WaterEditorUI.Section("Entry Streaks", _streaksExpanded, DrawStreaks);

            serializedObject.ApplyModifiedProperties();
            WaterEditorUI.DrawFooter();
        }

        void DrawStatus()
        {
            EditorGUILayout.HelpBox(
                "Droplets are thrown by the body's GPU foam system when one is active; the " +
                "'Droplet Spray (CPU Fallback)' Shuriken system only bursts on bodies without " +
                "one. Entry streaks and the 'Crown Ring' flipbook always play.",
                MessageType.None);
            if (_splashDriven)
                EditorGUILayout.HelpBox(
                    "The assigned Foam Profile's Splash section drives the burst and crown fields, " +
                    "so they are greyed out here. Tune the profile - or clear it, or turn off its " +
                    "Splash Drive toggle - to edit them on this component.",
                    MessageType.Info);
            else if (_profile.objectReferenceValue == null)
                EditorGUILayout.HelpBox(
                    "No Foam Profile assigned. These splash controls and the body's Foam Particles are " +
                    "then two SEPARATE control points on two components. To configure both from ONE place, " +
                    "assign a Water Foam Profile: its 'Apply To Selected Body' button points the foam " +
                    "particles AND this emitter at the same asset in one click.",
                    MessageType.Warning);

            DrawFoamProfileLink();
            EditorGUILayout.Space();
        }

        // The control itself is shared (WaterEditorUI); only finding the owning body is local.
        void DrawFoamProfileLink()
        {
            var emitter = target as WaterSplashEmitter;
            var body = emitter != null ? emitter.GetComponentInParent<WaterVolume>() : null;
            WaterEditorUI.DrawFoamProfileLink(serializedObject, _profile, body);
        }

        void DrawWiring()
        {
            EditorGUILayout.PropertyField(_particles,
                new GUIContent("Droplet System", "Shuriken droplet system (CPU fallback). Auto-created if empty."));
            EditorGUILayout.PropertyField(_crownParticles,
                new GUIContent("Crown System", "Flipbook crown system. Leave empty to disable the crown."));
            EditorGUILayout.PropertyField(_jetParticles,
                new GUIContent("Entry Jet System", "Stretched vertical splash columns. Leave empty to disable them."));
            EditorGUILayout.PropertyField(_profile,
                new GUIContent("Foam Profile",
                    "Optional master profile. When set, its Splash section overrides the burst/crown fields on every emit."));
        }

        void DrawBurst()
        {
            using (new EditorGUI.DisabledScope(_splashDriven))
            {
                EditorGUILayout.PropertyField(_maxParticlesPerBurst);
                EditorGUILayout.PropertyField(_upwardBias);
                EditorGUILayout.PropertyField(_outwardSpread);
                EditorGUILayout.PropertyField(_dropletSize);
                EditorGUILayout.PropertyField(_lifetime);
                EditorGUILayout.PropertyField(_dropletOpacity,
                    new GUIContent("Splash Droplet Opacity",
                        "Opacity of impact droplets in both the GPU and CPU-fallback paths. " +
                        "Ambient water spray has its own control in the Foam Profile."));
            }
        }

        void DrawDrift()
        {
            EditorGUILayout.PropertyField(_popDuration);
            EditorGUILayout.PropertyField(_driftStrength);
            EditorGUILayout.PropertyField(_driftDamping);
            EditorGUILayout.PropertyField(_surfaceRideHeight);
        }

        void DrawCrown()
        {
            using (new EditorGUI.DisabledScope(_splashDriven))
            {
                EditorGUILayout.PropertyField(_crownMinStrength);
                EditorGUILayout.PropertyField(_crownBaseSize);
                EditorGUILayout.PropertyField(_crownLifetime);
                WaterEditorUI.SubHeading("Motion");
                EditorGUILayout.PropertyField(_crownLaunchHeight,
                    new GUIContent("Launch Height"));
                EditorGUILayout.PropertyField(_crownLaunchSpread,
                    new GUIContent("Launch Spread"));
                WaterEditorUI.SubHeading("Look");
                EditorGUILayout.PropertyField(_crownTint);
                EditorGUILayout.PropertyField(_crownOpacity);
            }
        }

        void DrawStreaks()
        {
            using (new EditorGUI.DisabledScope(_splashDriven))
            {
                WaterEditorUI.SubHeading("Emission");
                EditorGUILayout.PropertyField(_entryStreaksEnabled);
                EditorGUILayout.PropertyField(_entryStreakMinStrength);
                EditorGUILayout.PropertyField(_entryStreakAmount);

                WaterEditorUI.SubHeading("Motion");
                EditorGUILayout.PropertyField(_entryStreakHeight);
                EditorGUILayout.PropertyField(_entryStreakWidth);
                EditorGUILayout.PropertyField(_entryStreakGravity);
                EditorGUILayout.PropertyField(_entryStreakLifetimeRange,
                    new GUIContent("Lifetime Range"));
                EditorGUILayout.PropertyField(_entryStreakSizeRange,
                    new GUIContent("Size Range"));

                WaterEditorUI.SubHeading("Look");
                EditorGUILayout.PropertyField(_entryStreakTint);
                EditorGUILayout.PropertyField(_entryStreakOpacity);
            }
        }
    }
}
