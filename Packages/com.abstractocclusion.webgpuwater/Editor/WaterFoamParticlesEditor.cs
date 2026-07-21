// WebGpuWater - inspector for WaterFoamParticles: turns the flat wall of "assign me" slots into a
// guided, sectioned panel. It surfaces what each asset slot wants, offers a one-click Wire / Repair
// that reuses the wizard's asset logic (WaterBuildKit.WireFoamAssets), greys the Density Material out
// unless Screen-Space Density is selected, and warns when a Foam Profile is overriding the fields
// below (the #1 "why does nothing change" trap). Fields are edited through SerializedProperty, so
// Undo and multi-object editing keep working.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterFoamParticles))]
    [CanEditMultipleObjects]
    internal sealed class WaterFoamParticlesEditor : UnityEditor.Editor
    {
        // Screen-Space Density is enum index 0 (FoamRenderMode.ScreenSpaceDensity); Quads is 1.
        const int DensityModeIndex = (int)WaterFoamParticles.FoamRenderMode.ScreenSpaceDensity;

        SerializedProperty _volume, _compute, _material, _renderMode, _densityMaterial, _profile;
        SerializedProperty _capacity;
        SerializedProperty _spawnThreshold, _spawnRate, _maxSpawnPerFrame, _sprayChance, _sprayLaunchSpeed;
        SerializedProperty _lifeRange, _sizeRange, _sizeHeroPower, _spawnMaxDistance;
        SerializedProperty _sprayMaterial, _sprayLifeRange, _spraySizeRange, _sprayFlipbookGrid, _sprayFlipbookFps;
        SerializedProperty _depositLifeRange, _depositSizeRange;
        SerializedProperty _gravity, _flowDrift, _windDriftSpeed, _drag;
        SerializedProperty _crestRollSpeed, _flipbookGrid, _flipbookFps;

        // Profile-driven state, refreshed each GUI pass: driven fields are DISABLED (not
        // just warned about) so users can't type into values the profile overwrites next frame.
        bool _ambientDriven;
        bool _lookDriven;

        bool _wiringExpanded = true;
        bool _poolExpanded;
        bool _spawnExpanded = true;
        bool _lookExpanded = true;
        bool _sprayExpanded = true;
        bool _motionExpanded;
        bool _oceanExpanded;

        void OnEnable()
        {
            _volume = serializedObject.FindProperty("volume");
            _compute = serializedObject.FindProperty("particleCompute");
            _material = serializedObject.FindProperty("particleMaterial");
            _renderMode = serializedObject.FindProperty("renderMode");
            _densityMaterial = serializedObject.FindProperty("densityMaterial");
            _profile = serializedObject.FindProperty("profile");
            _capacity = serializedObject.FindProperty("capacity");
            _spawnThreshold = serializedObject.FindProperty("spawnThreshold");
            _spawnRate = serializedObject.FindProperty("spawnRate");
            _maxSpawnPerFrame = serializedObject.FindProperty("maxSpawnPerFrame");
            _sprayChance = serializedObject.FindProperty("sprayChance");
            _sprayLaunchSpeed = serializedObject.FindProperty("sprayLaunchSpeed");
            _lifeRange = serializedObject.FindProperty("lifeRange");
            _sizeRange = serializedObject.FindProperty("sizeRange");
            _sizeHeroPower = serializedObject.FindProperty("sizeHeroPower");
            _spawnMaxDistance = serializedObject.FindProperty("spawnMaxDistance");
            _sprayMaterial = serializedObject.FindProperty("sprayMaterial");
            _sprayLifeRange = serializedObject.FindProperty("sprayLifeRange");
            _spraySizeRange = serializedObject.FindProperty("spraySizeRange");
            _sprayFlipbookGrid = serializedObject.FindProperty("sprayFlipbookGrid");
            _sprayFlipbookFps = serializedObject.FindProperty("sprayFlipbookFps");
            _depositLifeRange = serializedObject.FindProperty("depositLifeRange");
            _depositSizeRange = serializedObject.FindProperty("depositSizeRange");
            _gravity = serializedObject.FindProperty("gravity");
            _flowDrift = serializedObject.FindProperty("flowDrift");
            _windDriftSpeed = serializedObject.FindProperty("windDriftSpeed");
            _drag = serializedObject.FindProperty("drag");
            _crestRollSpeed = serializedObject.FindProperty("crestRollSpeed");
            _flipbookGrid = serializedObject.FindProperty("flipbookGrid");
            _flipbookFps = serializedObject.FindProperty("flipbookFps");
        }

        public override void OnInspectorGUI()
        {
            WaterEditorUI.DrawHeader("Water Foam Particles", "GPU foam + spray pool");
            serializedObject.Update();

            var profile = _profile.objectReferenceValue as WaterFoamProfile;
            _ambientDriven = profile != null && profile.ambient.drive;
            _lookDriven = profile != null && profile.look.drive;

            DrawStatusAndRepair();

            _wiringExpanded = WaterEditorUI.Section("Wiring & Assets", _wiringExpanded, DrawWiring);
            _poolExpanded = WaterEditorUI.Section("Pool", _poolExpanded, DrawPool);
            _spawnExpanded = WaterEditorUI.Section("Spawning", _spawnExpanded, DrawSpawning);
            _lookExpanded = WaterEditorUI.Section("Look & Life", _lookExpanded, DrawLookLife);
            _sprayExpanded = WaterEditorUI.Section("Ambient Mist", _sprayExpanded, DrawSpray);
            _motionExpanded = WaterEditorUI.Section("Motion", _motionExpanded, DrawMotion);
            _oceanExpanded = WaterEditorUI.Section("Ocean Crest & Flipbook", _oceanExpanded, DrawOceanFlipbook);

            serializedObject.ApplyModifiedProperties();
            WaterEditorUI.DrawFooter();
        }

        // ---- status, repair, and the two "why nothing changes" gotchas --------------------------

        void DrawStatusAndRepair()
        {
            bool densityMode = _renderMode.enumValueIndex == DensityModeIndex;
            bool missingCompute = _compute.objectReferenceValue == null;
            bool missingMaterial = _material.objectReferenceValue == null;
            bool missingDensity = densityMode && _densityMaterial.objectReferenceValue == null;

            if (missingCompute || missingMaterial || missingDensity)
                EditorGUILayout.HelpBox(
                    "Missing " + MissingList(missingCompute, missingMaterial, missingDensity) +
                    ". Click Wire / Repair Assets to load and assign the package defaults.",
                    MessageType.Warning);

            if (GUILayout.Button("Wire / Repair Assets"))
                WireSelected();

            if (_profile.objectReferenceValue != null)
                EditorGUILayout.HelpBox(
                    "A Foam Profile is assigned: its driven sections OVERRIDE the matching fields below " +
                    "every frame, so those fields are greyed out here. Tune the profile - or clear it, " +
                    "or turn off that section's Drive toggle - to edit them on this component.",
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    "No Foam Profile assigned. These foam controls and the body's Splash Emitter are then " +
                    "two SEPARATE control points on two components. To configure both from ONE place, " +
                    "assign a Water Foam Profile: its 'Apply To Selected Body' button points this and the " +
                    "splash emitter at the same asset in one click.",
                    MessageType.Warning);

            DrawFoamProfileLink();

            if (!DeviceSupportsDensity())
                EditorGUILayout.HelpBox(
                    "This device can't read structured buffers in the fragment stage, so Screen-Space " +
                    "Density falls back to Quads at runtime.", MessageType.None);

            EditorGUILayout.Space();
        }

        // One-click jump to the single tweak surface. When no profile exists yet it is created and
        // pointed at BOTH this component and the body's splash emitter, so foam + splash are
        // configured from one asset instead of two components.
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

            var particles = target as WaterFoamParticles;
            var body = particles != null
                ? (particles.volume != null ? particles.volume : particles.GetComponentInParent<WaterVolume>())
                : null;
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

        static string MissingList(bool compute, bool material, bool density)
        {
            var parts = new System.Collections.Generic.List<string>(3);
            if (compute) parts.Add("Particle Compute");
            if (material) parts.Add("Particle Material");
            if (density) parts.Add("Density Material");
            return string.Join(", ", parts);
        }

        void WireSelected()
        {
            WaterBuildKit.EnsureGenFolder();
            foreach (Object obj in targets)
            {
                var particles = obj as WaterFoamParticles;
                if (particles == null) continue;
                Undo.RecordObject(particles, "Wire Foam Assets");
                WaterBuildKit.WireFoamAssets(particles, WaterBuildKit.Gen);
            }
            serializedObject.Update();
        }

        // maxComputeBufferInputsFragment >= 2 mirrors WaterFoamParticles' own density-support gate.
        static bool DeviceSupportsDensity() => SystemInfo.maxComputeBufferInputsFragment >= 2;

        // ---- sections ---------------------------------------------------------------------------

        void DrawWiring()
        {
            EditorGUILayout.PropertyField(_volume,
                new GUIContent("Water Body", "The WaterVolume this system spawns from. Auto-found on the parent."));
            EditorGUILayout.PropertyField(_compute,
                new GUIContent("Particle Compute", "The package's WaterFoamParticles.compute (fixed asset). Required."));
            EditorGUILayout.PropertyField(_material,
                new GUIContent("Particle Material", "Material on the FoamParticles shader (quad/spray look). Required."));
            EditorGUILayout.PropertyField(_renderMode,
                new GUIContent("Render Mode", "Screen-Space Density = KWS connected-foam veil; Quads = per-particle billboards."));

            using (new EditorGUI.DisabledScope(_renderMode.enumValueIndex != DensityModeIndex))
                EditorGUILayout.PropertyField(_densityMaterial,
                    new GUIContent("Density Material",
                        "Material on the FoamDensityComposite shader. Only used in Screen-Space Density mode."));

            EditorGUILayout.PropertyField(_profile,
                new GUIContent("Foam Profile",
                    "Optional master profile. When set, its driven sections override the fields below every frame."));
        }

        void DrawPool()
        {
            EditorGUILayout.HelpBox(
                "Live pool = min(Capacity, quality-tier cap). The Low tier caps foam at 1024, so raising " +
                "Capacity above the cap does nothing - check the Console 'foamCap' log for the active cap, " +
                "and set the WaterQuality asset's tier to Force High to lift it. The pool is a ring buffer: " +
                "when it fills, the oldest particle is recycled (which can look like foam 'popping' if the " +
                "cap is small and spawn is high).",
                MessageType.None);
            EditorGUILayout.PropertyField(_capacity,
                new GUIContent("Capacity", "Requested pool size (rounded to a power of two, clamped to the tier cap)."));
        }

        void DrawSpawning()
        {
            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_spawnThreshold);
                EditorGUILayout.PropertyField(_spawnRate);
                EditorGUILayout.PropertyField(_maxSpawnPerFrame);
                EditorGUILayout.PropertyField(_sprayChance,
                    new GUIContent("Mist Chance",
                        "Chance a foam spawn launches as an airborne mist droplet instead of floating foam."));
                EditorGUILayout.PropertyField(_sprayLaunchSpeed,
                    new GUIContent("Mist Launch Speed", "Upward launch speed of ambient mist droplets."));
            }
        }

        void DrawLookLife()
        {
            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_lifeRange);
                EditorGUILayout.PropertyField(_sizeRange);
            }
            using (new EditorGUI.DisabledScope(_lookDriven))
                EditorGUILayout.PropertyField(_sizeHeroPower);
            using (new EditorGUI.DisabledScope(_ambientDriven))
                EditorGUILayout.PropertyField(_spawnMaxDistance);
        }

        void DrawSpray()
        {
            EditorGUILayout.HelpBox("Ambient mist: airborne droplets thrown off the floating foam " +
                "(Mist Chance above). Pump/impact SPLASH droplets are tuned on the WaterSplashEmitter " +
                "instead - these values do NOT affect them. Leave the material empty to draw the mist " +
                "with the foam Particle Material.", MessageType.None);
            EditorGUILayout.PropertyField(_sprayMaterial, new GUIContent("Mist Material"));
            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_sprayLifeRange, new GUIContent("Mist Lifetime"));
                EditorGUILayout.PropertyField(_spraySizeRange, new GUIContent("Mist Size"));
            }
            EditorGUILayout.PropertyField(_sprayFlipbookGrid, new GUIContent("Flipbook Grid"));
            EditorGUILayout.PropertyField(_sprayFlipbookFps, new GUIContent("Flipbook FPS"));

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Deposited foam: when ANY airborne droplet (mist or pump splash) " +
                "lands, it becomes a surface foam patch re-rolled from these ranges - tuned " +
                "independently of the droplet that made it.", MessageType.None);
            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_depositLifeRange, new GUIContent("Deposit Lifetime"));
                EditorGUILayout.PropertyField(_depositSizeRange, new GUIContent("Deposit Size"));
            }
        }

        void DrawMotion()
        {
            EditorGUILayout.PropertyField(_gravity);
            EditorGUILayout.PropertyField(_flowDrift);
            EditorGUILayout.PropertyField(_windDriftSpeed);
            EditorGUILayout.PropertyField(_drag);
        }

        void DrawOceanFlipbook()
        {
            EditorGUILayout.PropertyField(_crestRollSpeed);
            using (new EditorGUI.DisabledScope(_lookDriven))
            {
                EditorGUILayout.PropertyField(_flipbookGrid);
                EditorGUILayout.PropertyField(_flipbookFps);
            }
        }
    }
}
