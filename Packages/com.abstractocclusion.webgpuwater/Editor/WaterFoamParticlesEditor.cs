// WebGpuWater - inspector for WaterFoamParticles: turns the flat wall of "assign me" slots into a
// guided, sectioned panel.
//
// SECTIONS ARE GROUPED BY THE PARTICLE YOU ARE LOOKING AT, not by parameter type. One component feeds
// three populations from four sources, and grouping by type hid which was which:
//
//   Floating foam    (KIND_SURFACE) - the sheet on the water
//   Airborne droplets(KIND_SPRAY)   - ambient mist, surf lip spray AND splash/pump bursts, one draw pass
//   Landed foam                     - what ANY droplet becomes when it touches down
//
// then the SOURCES that feed them, because "which knob moves my boat spray" is a question about the
// source, not the look. Three fields used to sit under headings that actively lied about their reach:
// the droplet material and its flipbook are the whole spray pass (not "Ambient Mist"), the deposit
// ranges catch every landed droplet (not "Ambient Mist"), and the foam flipbook is every foam particle
// (not "Ocean Crest"). Verified against the compute kernel, not the field names. It surfaces what each asset slot wants, offers a one-click Wire / Repair
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

        SerializedProperty _useParticles;
        SerializedProperty _volume, _compute, _material, _renderMode, _densityMaterial, _profile;
        SerializedProperty _capacity;
        SerializedProperty _spawnThreshold, _spawnRate, _maxSpawnPerFrame, _sprayChance, _sprayLaunchSpeed;
        SerializedProperty _lifeRange, _sizeRange, _sizeHeroPower, _spawnMaxDistance;
        SerializedProperty _sprayMaterial, _sprayLifeRange, _spraySizeRange, _sprayFlipbookGrid, _sprayFlipbookFps;
        SerializedProperty _depositLifeRange, _depositSizeRange;
        SerializedProperty _gravity, _flowDrift, _windDriftSpeed, _drag;
        SerializedProperty _crestRollSpeed, _crestFoamSpawn, _flipbookGrid, _flipbookFps;

        // Profile-driven state, refreshed each GUI pass: driven fields are DISABLED (not
        // just warned about) so users can't type into values the profile overwrites next frame.
        bool _ambientDriven;
        bool _lookDriven;

        bool _wiringExpanded = true;
        bool _poolExpanded;
        bool _foamExpanded = true;
        bool _dropletExpanded = true;
        bool _landedExpanded;
        bool _ambientSourceExpanded = true;
        bool _crestSourceExpanded;
        bool _burstSourceExpanded;

        void OnEnable()
        {
            _useParticles = serializedObject.FindProperty("useParticles");
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
            _crestFoamSpawn = serializedObject.FindProperty("crestFoamSpawn");
            _flipbookGrid = serializedObject.FindProperty("flipbookGrid");
            _flipbookFps = serializedObject.FindProperty("flipbookFps");
        }

        public override void OnInspectorGUI()
        {
            WaterEditorUI.DrawHeader("Water Foam Particles", "GPU foam + spray pool");
            serializedObject.Update();

            EditorGUILayout.PropertyField(_useParticles,
                new GUIContent("Use Particles",
                    "Master switch: off skips ALL particles on this body - no simulation, no compute dispatch, " +
                    "no draw (ambient foam and event splashes both stop)."));
            EditorGUILayout.Space();

            var profile = _profile.objectReferenceValue as WaterFoamProfile;
            _ambientDriven = profile != null && profile.ambient.drive;
            _lookDriven = profile != null && profile.look.drive;

            DrawStatusAndRepair();

            _wiringExpanded = WaterEditorUI.Section("Wiring & Assets", _wiringExpanded, DrawWiring);
            _poolExpanded = WaterEditorUI.Section("Pool (shared by everything)", _poolExpanded, DrawPool);

            _foamExpanded = WaterEditorUI.Section("1 - Floating Foam", _foamExpanded, DrawFloatingFoam);
            _dropletExpanded = WaterEditorUI.Section("2 - Airborne Droplets", _dropletExpanded, DrawAirborneDroplets);
            _landedExpanded = WaterEditorUI.Section("3 - Landed Foam", _landedExpanded, DrawLandedFoam);

            _ambientSourceExpanded = WaterEditorUI.Section("Source - Ambient Turbulence",
                _ambientSourceExpanded, DrawAmbientSource);
            _crestSourceExpanded = WaterEditorUI.Section("Source - Ocean Crests",
                _crestSourceExpanded, DrawCrestSource);
            _burstSourceExpanded = WaterEditorUI.Section("Source - Splash & Pump Bursts",
                _burstSourceExpanded, DrawBurstSource);

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

        // The control itself is shared (WaterEditorUI); only finding the owning body is local.
        void DrawFoamProfileLink()
        {
            var particles = target as WaterFoamParticles;
            var body = particles != null
                ? (particles.volume != null ? particles.volume : particles.GetComponentInParent<WaterVolume>())
                : null;
            WaterEditorUI.DrawFoamProfileLink(serializedObject, _profile, body);
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
                new GUIContent("Render Mode", "Screen-Space Density = connected foam veil; Quads = per-particle billboards."));

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

        // ---- 1. the foam sheet on the water (KIND_SURFACE) ---------------------------------------

        void DrawFloatingFoam()
        {
            EditorGUILayout.HelpBox("The foam SHEET lying on the water - every source feeds it: ambient " +
                "turbulence, ocean crests, shore surf, and droplets that have landed. These are its look " +
                "and how it drifts.", MessageType.None);

            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_lifeRange, new GUIContent("Foam Lifetime",
                    "How long a floating foam particle lives, in seconds. Airborne droplets have their own " +
                    "lifetime under Airborne Droplets."));
                EditorGUILayout.PropertyField(_sizeRange, new GUIContent("Foam Size",
                    "World half-size range of a floating foam particle."));
            }
            using (new EditorGUI.DisabledScope(_lookDriven))
            {
                EditorGUILayout.PropertyField(_sizeHeroPower, new GUIContent("Hero Size Bias",
                    "1 = sizes spread evenly across the range; higher = mostly small particles with rare " +
                    "large 'hero' ones. Variety without new art."));
                EditorGUILayout.PropertyField(_flipbookGrid, new GUIContent("Foam Flipbook Grid",
                    "Sprite atlas layout (columns, rows) for FOAM. (1,1) = a plain texture. This is the " +
                    "foam sheet's atlas for every source - it is not ocean-specific."));
                EditorGUILayout.PropertyField(_flipbookFps, new GUIContent("Foam Flipbook FPS",
                    "How fast a foam particle churns through its atlas over its life. 0 = one fixed cell."));
            }

            WaterEditorUI.SubHeading("Drift");
            EditorGUILayout.PropertyField(_flowDrift, new GUIContent("Flow Drift",
                "Speed foam is carried along the surface flow, per unit of surface slope. Floating foam only."));
            EditorGUILayout.PropertyField(_windDriftSpeed, new GUIContent("Wind Drift",
                "Constant downwind drift of floating foam, in world units per second."));
            EditorGUILayout.PropertyField(_drag, new GUIContent("Drift Damping",
                "How quickly a foam particle's velocity relaxes to the driven flow. Floating foam only."));
        }

        // ---- 2. everything airborne (KIND_SPRAY), whatever threw it -------------------------------

        void DrawAirborneDroplets()
        {
            EditorGUILayout.HelpBox("Every airborne droplet on this body shares these: ambient mist, shore " +
                "surf lip spray, AND splash / spray-pump bursts. They are one draw pass, so this material " +
                "and flipbook are what a BOAT'S spray looks like too.\n\n" +
                "How MUCH each source throws, and how long those droplets live, belongs to the source " +
                "sections below.", MessageType.None);

            EditorGUILayout.PropertyField(_sprayMaterial, new GUIContent("Droplet Material",
                "Material for ALL airborne droplets. Empty = draw them with the foam Particle Material above."));
            EditorGUILayout.PropertyField(_sprayFlipbookGrid, new GUIContent("Droplet Flipbook Grid",
                "Sprite atlas layout (columns, rows) for droplets. Kept separate from the foam atlas so a " +
                "sheet authored for foam is never forced onto the spray."));
            EditorGUILayout.PropertyField(_sprayFlipbookFps, new GUIContent("Droplet Flipbook FPS",
                "Droplet flipbook speed. 0 = a static droplet sprite."));
            EditorGUILayout.PropertyField(_gravity, new GUIContent("Droplet Gravity",
                "Downward acceleration on airborne droplets. Applies to every droplet, whatever threw it; " +
                "floating foam is unaffected."));
        }

        // ---- 3. what a droplet becomes when it lands ----------------------------------------------

        void DrawLandedFoam()
        {
            EditorGUILayout.HelpBox("When ANY airborne droplet touches down - mist, surf lip or a boat's " +
                "spray - it converts to floating foam and re-rolls its life and size from these ranges. " +
                "Tuned independently of the droplet that made it.", MessageType.None);

            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_depositLifeRange, new GUIContent("Landed Lifetime",
                    "Lifetime of the foam patch a landed droplet leaves behind, in seconds."));
                EditorGUILayout.PropertyField(_depositSizeRange, new GUIContent("Landed Size",
                    "World half-size range of that patch."));
            }
        }

        // ---- sources ------------------------------------------------------------------------------

        void DrawAmbientSource()
        {
            EditorGUILayout.HelpBox("The always-on foam the water makes for itself: wakes, interactor rims " +
                "and shore whitewash raise a foam mask, and these decide how much of it becomes particles.\n\n" +
                "ONLY this source reads them. Ocean crests and splash / pump bursts spawn regardless, so " +
                "zeroing Spawn Rate does NOT stop a boat spraying.", MessageType.None);

            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                EditorGUILayout.PropertyField(_spawnThreshold, new GUIContent("Foam Threshold",
                    "Foam level (0-1) below which this source spawns nothing."));
                EditorGUILayout.PropertyField(_spawnRate, new GUIContent("Spawn Rate",
                    "Expected spawns per second per square world unit of fully-foamed water."));
                EditorGUILayout.PropertyField(_maxSpawnPerFrame, new GUIContent("Max Spawn Per Frame",
                    "Hard per-frame cap on THIS source, spreading a sudden bloom over a few frames."));
                EditorGUILayout.PropertyField(_spawnMaxDistance, new GUIContent("Spawn Distance",
                    "Distance LOD in metres: full density to ~60% of this, then thinning to a dusting. " +
                    "0 = no thinning. Applies to this source only."));

                WaterEditorUI.SubHeading("Mist thrown off the foam");
                EditorGUILayout.PropertyField(_sprayChance, new GUIContent("Mist Chance",
                    "Fraction of this source's spawns launched as airborne mist instead of floating foam."));
                EditorGUILayout.PropertyField(_sprayLaunchSpeed, new GUIContent("Mist Launch Speed",
                    "Upward launch speed of those mist droplets."));
                EditorGUILayout.PropertyField(_sprayLifeRange, new GUIContent("Mist Lifetime",
                    "Lifetime of AMBIENT MIST droplets only. Splash and pump droplets carry their own, set " +
                    "on the Water Splash Emitter."));
                EditorGUILayout.PropertyField(_spraySizeRange, new GUIContent("Mist Size",
                    "Size of ambient mist droplets only, for the same reason."));
            }
        }

        void DrawCrestSource()
        {
            EditorGUILayout.HelpBox("Breaking ocean crests, from the FFT whitecap channel. Ocean bodies " +
                "only - ignored on pools. Never overridden by a Foam Profile.", MessageType.None);

            EditorGUILayout.PropertyField(_crestFoamSpawn, new GUIContent("Crest Foam Spawn",
                "0 = crests spawn no particles at all and the surface shader keeps the whitecap look; " +
                "1 = as before. Wakes, interactions and shore surf are unaffected either way."));
            EditorGUILayout.PropertyField(_crestRollSpeed, new GUIContent("Crest Roll Speed",
                "How fast whitecap foam rolls forward along the wave-travel direction. 0 = it sits still."));
        }

        void DrawBurstSource()
        {
            EditorGUILayout.HelpBox("Impact splashes and spray-pump bursts - a boat's bow spray, objects " +
                "hitting the water, mouse and touch splashes.\n\n" +
                "THEIR KNOBS ARE NOT ON THIS COMPONENT. How many droplets, how hard they are thrown and " +
                "how long they live are set on the body's Water Splash Emitter; where and when they fire " +
                "is set on each Water Spray Pump. What they LOOK like is Airborne Droplets above, which " +
                "they share with the mist.", MessageType.Info);

            var particles = target as WaterFoamParticles;
            var body = particles != null
                ? (particles.volume != null ? particles.volume : particles.GetComponentInParent<WaterVolume>())
                : null;
            WaterSplashEmitter emitter = body != null ? body.splashEmitter : null;

            using (new EditorGUI.DisabledScope(emitter == null))
                if (GUILayout.Button(emitter != null ? $"Select \"{emitter.name}\"" : "No Splash Emitter On This Body"))
                {
                    Selection.activeObject = emitter.gameObject;
                    EditorGUIUtility.PingObject(emitter);
                }
        }
    }
}
