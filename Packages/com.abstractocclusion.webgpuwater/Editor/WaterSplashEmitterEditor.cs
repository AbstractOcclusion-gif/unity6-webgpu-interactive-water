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
        SerializedProperty _crownParticles, _crownMinStrength, _crownBaseSize, _crownLifetime;
        SerializedProperty _crownTint, _crownOpacity;

        bool _wiringExpanded = true;
        bool _burstExpanded = true;
        bool _driftExpanded;
        bool _crownExpanded = true;

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
            _crownTint = serializedObject.FindProperty("crownTint");
            _crownOpacity = serializedObject.FindProperty("crownOpacity");
        }

        public override void OnInspectorGUI()
        {
            WaterEditorUI.DrawHeader("Water Splash Emitter", "impact droplets + crown ring");
            serializedObject.Update();

            var profile = _profile.objectReferenceValue as WaterFoamProfile;
            _splashDriven = profile != null && profile.splash.drive;

            DrawStatus();

            _wiringExpanded = WaterEditorUI.Section("Wiring & Profile", _wiringExpanded, DrawWiring);
            _burstExpanded = WaterEditorUI.Section("Burst Shaping", _burstExpanded, DrawBurst);
            _driftExpanded = WaterEditorUI.Section("Surface Drift", _driftExpanded, DrawDrift);
            _crownExpanded = WaterEditorUI.Section("Crown Ring", _crownExpanded, DrawCrown);

            serializedObject.ApplyModifiedProperties();
            WaterEditorUI.DrawFooter();
        }

        void DrawStatus()
        {
            EditorGUILayout.HelpBox(
                "Droplets are thrown by the body's GPU foam system when one is active; the " +
                "'Droplet Spray (CPU Fallback)' Shuriken system only bursts on bodies without " +
                "one. The 'Crown Ring' flipbook always plays.",
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

        // One-click jump to the single tweak surface. When no profile exists yet it is created and
        // pointed at BOTH this emitter and the body's foam particles, so foam + splash are configured
        // from one asset instead of two components.
        void DrawFoamProfileLink()
        {
            var linked = _profile.objectReferenceValue as WaterFoamProfile;
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

            var emitter = target as WaterSplashEmitter;
            var body = emitter != null ? emitter.GetComponentInParent<WaterVolume>() : null;
            WaterBuildKit.EnsureGenFolder();
            var created = WaterBuildKit.LoadOrCreateFoamProfile(WaterBuildKit.Gen);
            if (body != null)
            {
                WaterBuildKit.AssignFoamProfileToBody(body, created);
                serializedObject.Update();
            }
            else
            {
                _profile.objectReferenceValue = created;
            }
            Selection.activeObject = created;
            EditorGUIUtility.PingObject(created);
        }

        void DrawWiring()
        {
            EditorGUILayout.PropertyField(_particles,
                new GUIContent("Droplet System", "Shuriken droplet system (CPU fallback). Auto-created if empty."));
            EditorGUILayout.PropertyField(_crownParticles,
                new GUIContent("Crown System", "Flipbook crown system. Leave empty to disable the crown."));
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
                EditorGUILayout.PropertyField(_crownTint);
                EditorGUILayout.PropertyField(_crownOpacity);
            }
        }
    }
}
