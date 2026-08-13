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
        const string ExperimentalSpawningLabel = "Simulation Driven Spawning (Experimental)";
        const string ExperimentalSpawningWarning =
            "EXPERIMENTAL · ACTIVE DEVELOPMENT\n\nThis autonomous ripple / foam-mask source is " +
            "not production-ready. Keep it off to render only event splash particles, surfaced " +
            "bubbles and their landed foam deposits.";

        const string ExperimentalDensityWarning =
            "EXPERIMENTAL - ACTIVE DEVELOPMENT\n\nScreen-Space Density is not production-ready. " +
            "Use it only for testing while its foam shape and size controls are being reworked.";

        SerializedProperty _useParticles;
        SerializedProperty _volume, _compute, _material, _renderMode, _densityMaterial, _profile;
        SerializedProperty _capacity;
        SerializedProperty _simulationDrivenSpawning, _spawnThreshold, _spawnRate, _maxSpawnPerFrame,
            _sprayChance, _sprayLaunchSpeed;
        SerializedProperty _rippleCrestFlecksEnabled, _rippleCrestFleckAmount,
            _rippleCrestFleckMaxPerFrame, _rippleCrestFleckLifetimeRange, _rippleCrestFleckSizeRange,
            _rippleCrestFleckMotion;
        SerializedProperty _lifeRange, _sizeRange, _sizeHeroPower, _spawnMaxDistance;
        SerializedProperty _sprayMaterial, _sprayLifeRange, _spraySizeRange, _sprayFlipbookGrid, _sprayFlipbookFps;
        SerializedProperty _surfaceFoamOpacity, _sprayOpacity, _bubbleOpacity;
        SerializedProperty _depositLifeRange, _depositSizeRange, _densitySurfaceSizeScale;
        SerializedProperty _gravity, _flowDrift, _windDriftSpeed, _drag;
        SerializedProperty _bubbleAmount, _bubbleRiseSpeed, _bubbleLifeRange, _bubbleSizeRange,
            _bubbleWobble;
        SerializedProperty _flipbookGrid, _flipbookFps;

        // Profile-driven state, refreshed each GUI pass: driven fields are DISABLED (not
        // just warned about) so users can't type into values the profile overwrites next frame.
        bool _ambientDriven;
        bool _lookDriven;
        bool _motionDriven;
        bool _veilDriven;
        bool _bubbleDriven;

        bool _wiringExpanded = true;
        bool _poolExpanded;
        bool _motionExpanded = true;
        bool _foamExpanded = true;
        bool _dropletExpanded = true;
        bool _landedExpanded;
        bool _bubbleExpanded;
        bool _ambientSourceExpanded = true;
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
            _simulationDrivenSpawning = serializedObject.FindProperty("simulationDrivenSpawning");
            _spawnThreshold = serializedObject.FindProperty("spawnThreshold");
            _spawnRate = serializedObject.FindProperty("spawnRate");
            _maxSpawnPerFrame = serializedObject.FindProperty("maxSpawnPerFrame");
            _sprayChance = serializedObject.FindProperty("sprayChance");
            _sprayLaunchSpeed = serializedObject.FindProperty("sprayLaunchSpeed");
            _rippleCrestFlecksEnabled = serializedObject.FindProperty("rippleCrestFlecksEnabled");
            _rippleCrestFleckAmount = serializedObject.FindProperty("rippleCrestFleckAmount");
            _rippleCrestFleckMaxPerFrame = serializedObject.FindProperty("rippleCrestFleckMaxPerFrame");
            _rippleCrestFleckLifetimeRange = serializedObject.FindProperty("rippleCrestFleckLifetimeRange");
            _rippleCrestFleckSizeRange = serializedObject.FindProperty("rippleCrestFleckSizeRange");
            _rippleCrestFleckMotion = serializedObject.FindProperty("rippleCrestFleckMotion");
            _lifeRange = serializedObject.FindProperty("lifeRange");
            _sizeRange = serializedObject.FindProperty("sizeRange");
            _sizeHeroPower = serializedObject.FindProperty("sizeHeroPower");
            _spawnMaxDistance = serializedObject.FindProperty("spawnMaxDistance");
            _sprayMaterial = serializedObject.FindProperty("sprayMaterial");
            _sprayLifeRange = serializedObject.FindProperty("sprayLifeRange");
            _spraySizeRange = serializedObject.FindProperty("spraySizeRange");
            _sprayFlipbookGrid = serializedObject.FindProperty("sprayFlipbookGrid");
            _sprayFlipbookFps = serializedObject.FindProperty("sprayFlipbookFps");
            _surfaceFoamOpacity = serializedObject.FindProperty("surfaceFoamOpacity");
            _sprayOpacity = serializedObject.FindProperty("sprayOpacity");
            _bubbleOpacity = serializedObject.FindProperty("bubbleOpacity");
            _depositLifeRange = serializedObject.FindProperty("depositLifeRange");
            _depositSizeRange = serializedObject.FindProperty("depositSizeRange");
            _densitySurfaceSizeScale = serializedObject.FindProperty("densitySurfaceSizeScale");
            _gravity = serializedObject.FindProperty("gravity");
            _flowDrift = serializedObject.FindProperty("flowDrift");
            _windDriftSpeed = serializedObject.FindProperty("windDriftSpeed");
            _drag = serializedObject.FindProperty("drag");
            _bubbleAmount = serializedObject.FindProperty("bubbleAmount");
            _bubbleRiseSpeed = serializedObject.FindProperty("bubbleRiseSpeed");
            _bubbleLifeRange = serializedObject.FindProperty("bubbleLifeRange");
            _bubbleSizeRange = serializedObject.FindProperty("bubbleSizeRange");
            _bubbleWobble = serializedObject.FindProperty("bubbleWobble");
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
            _motionDriven = profile != null && profile.motion.drive;
            _veilDriven = profile != null && profile.veil.drive;
            _bubbleDriven = profile != null && profile.bubbles.drive;

            DrawStatusAndRepair();

            _wiringExpanded = WaterEditorUI.Section("Wiring & Assets", _wiringExpanded, DrawWiring);
            _poolExpanded = WaterEditorUI.Section("Pool (shared by everything)", _poolExpanded, DrawPool);
            _motionExpanded = WaterEditorUI.Section("Motion (all particles)", _motionExpanded, DrawMotion);

            _foamExpanded = WaterEditorUI.Section("1 - Floating Foam", _foamExpanded, DrawFloatingFoam);
            _dropletExpanded = WaterEditorUI.Section("2 - Airborne Droplets", _dropletExpanded, DrawAirborneDroplets);
            _landedExpanded = WaterEditorUI.Section("3 - Landed Foam", _landedExpanded, DrawLandedFoam);
            _bubbleExpanded = WaterEditorUI.Section("4 - Bubbles", _bubbleExpanded, DrawBubbles);

            _ambientSourceExpanded = WaterEditorUI.Section("Source - Ambient Turbulence",
                _ambientSourceExpanded, DrawAmbientSource);
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

            var profile = _profile.objectReferenceValue as WaterFoamProfile;
            if (!densityMode && profile != null && profile.veil.drive)
                EditorGUILayout.HelpBox(
                    "Density Veil is enabled in the assigned Foam Profile, but this component is set to " +
                    "Quads. The veil is inactive until Render Mode is changed to Screen-Space Density.",
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
            foreach (Object obj in targets)
            {
                var particles = obj as WaterFoamParticles;
                if (particles == null) continue;
                Undo.RecordObject(particles, "Wire Foam Assets");
                WaterVolume volume = particles.volume != null
                    ? particles.volume
                    : particles.GetComponentInParent<WaterVolume>();
                WaterBuildKit.WireFoamAssets(
                    particles, WaterBuildKit.ResolveOrCreateMaterialsFolder(volume));
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
                new GUIContent("Render Mode",
                    "Screen-Space Density is experimental and under active development. Quads uses " +
                    "per-particle billboards."));

            if (_renderMode.enumValueIndex == DensityModeIndex)
                EditorGUILayout.HelpBox(ExperimentalDensityWarning, MessageType.Warning);

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
                WaterEditorUI.SubHeading("Layer Opacity");
                EditorGUILayout.PropertyField(_surfaceFoamOpacity, new GUIContent("Foam Opacity"));
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

        }

        // ---- motion shared across the pool ---------------------------------------------------------

        void DrawMotion()
        {
            EditorGUILayout.HelpBox(_motionDriven
                    ? "Motion is overridden by the assigned Foam Profile. Tune its Motion section, " +
                      "or turn that section's Drive toggle off to use these local values."
                    : "Physics shared across the pool. Gravity pulls every airborne droplet " +
                      "(mist, splash, pump, lip, cascade); drift and damping steer floating foam " +
                      "and the sideways motion of bubbles.",
                _motionDriven ? MessageType.Info : MessageType.None);

            using (new EditorGUI.DisabledScope(_motionDriven))
            {
                EditorGUILayout.PropertyField(_gravity, new GUIContent("Gravity",
                    "Downward acceleration on every airborne droplet, whatever threw it. Floating foam " +
                    "sits on the surface and bubbles use their own buoyancy, so neither falls."));
                EditorGUILayout.PropertyField(_flowDrift, new GUIContent("Flow Drift",
                    "Speed floating foam (and bubbles, sideways) are carried along the surface flow, " +
                    "per unit of surface slope."));
                EditorGUILayout.PropertyField(_windDriftSpeed, new GUIContent("Wind Drift",
                    "Constant downwind drift of floating foam, in world units per second."));
                EditorGUILayout.PropertyField(_drag, new GUIContent("Drift Damping",
                    "How quickly a particle's velocity relaxes to the driven flow (floating foam and " +
                    "bubble sideways motion)."));
            }
        }

        // ---- 2. everything airborne (KIND_SPRAY), whatever threw it -------------------------------

        void DrawAirborneDroplets()
        {
            EditorGUILayout.HelpBox("Every airborne droplet on this body shares these: ambient mist, shore " +
                "surf lip spray, AND splash / spray-pump bursts. They are one draw pass, so this material " +
                "and flipbook are what a BOAT'S spray looks like too.\n\n" +
                "How MUCH each source throws, and how long those droplets live, belongs to the source " +
                "sections below.", MessageType.None);

            using (new EditorGUI.DisabledScope(_ambientDriven))
            {
                WaterEditorUI.SubHeading("Layer Opacity");
                EditorGUILayout.PropertyField(_sprayOpacity, new GUIContent("Droplet Opacity"));
            }
            EditorGUILayout.PropertyField(_sprayMaterial, new GUIContent("Droplet Material",
                "Material for ALL airborne droplets. Empty = draw them with the foam Particle Material above."));
            EditorGUILayout.PropertyField(_sprayFlipbookGrid, new GUIContent("Droplet Flipbook Grid",
                "Sprite atlas layout (columns, rows) for droplets. Kept separate from the foam atlas so a " +
                "sheet authored for foam is never forced onto the spray."));
            EditorGUILayout.PropertyField(_sprayFlipbookFps, new GUIContent("Droplet Flipbook FPS",
                "Droplet flipbook speed. 0 = a static droplet sprite."));
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

            if (_renderMode.enumValueIndex == DensityModeIndex)
            {
                using (new EditorGUI.DisabledScope(_veilDriven))
                    EditorGUILayout.PropertyField(_densitySurfaceSizeScale,
                        new GUIContent("Screen Density Size Scale",
                            "Immediate render-time multiplier for existing and new landed foam. " +
                            "Tune this while playing; 1 uses the authored Landed Size."));
            }
        }

        // ---- sources ------------------------------------------------------------------------------

        void DrawAmbientSource()
        {
            EditorGUILayout.PropertyField(_simulationDrivenSpawning,
                new GUIContent(ExperimentalSpawningLabel,
                    "Off keeps only event splash droplets, surfaced bubbles and their landed foam. " +
                    "On also permits the foam mask and ripple crests to generate particles."));
            EditorGUILayout.HelpBox(ExperimentalSpawningWarning, MessageType.Warning);

            EditorGUILayout.HelpBox("The always-on foam the water makes for itself: wakes, interactor rims " +
                "and shore whitewash raise a foam mask, and these decide how much of it becomes particles.\n\n" +
                "ONLY this source reads them. Splash / pump bursts spawn regardless, so zeroing Spawn Rate " +
                "does NOT stop a boat spraying. On FFT ocean bodies this ambient source is fully OFF " +
                "(the surface shader owns the whitecap look) - only the breaking surf lip throws.", MessageType.None);

            bool sourceDisabled = !_simulationDrivenSpawning.hasMultipleDifferentValues
                               && !_simulationDrivenSpawning.boolValue;
            using (new EditorGUI.DisabledScope(_ambientDriven || sourceDisabled))
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

                WaterEditorUI.SubHeading("Ripple Crest Flecks");
                EditorGUILayout.PropertyField(_rippleCrestFlecksEnabled,
                    new GUIContent("Enabled",
                        "Emit small floating flecks from moving ripple crests, independently of foam-mask spawning."));
                EditorGUILayout.PropertyField(_rippleCrestFleckAmount, new GUIContent("Density"));
                EditorGUILayout.PropertyField(_rippleCrestFleckMaxPerFrame,
                    new GUIContent("Max Per Frame"));
                EditorGUILayout.PropertyField(_rippleCrestFleckLifetimeRange,
                    new GUIContent("Lifetime Range"));
                EditorGUILayout.PropertyField(_rippleCrestFleckSizeRange,
                    new GUIContent("Size Range"));
                EditorGUILayout.PropertyField(_rippleCrestFleckMotion,
                    new GUIContent("Ripple Motion",
                        "How strongly flecks retain their outward ripple-propagation motion."));

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

        void DrawBubbles()
        {
            EditorGUILayout.HelpBox("Underwater bubble plumes: every splash / pump burst also injects " +
                "bubbles DOWNWARD under the impact; buoyancy rises them back and they pop into landed " +
                "foam at the waterline. Drawn as analytic rim circles - no texture slot to wire.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(_bubbleDriven))
            {
                EditorGUILayout.PropertyField(_bubbleAmount, new GUIContent("Bubble Amount",
                    "Bubbles injected per droplet thrown (0 = no bubbles, and the bubble pass is skipped)."));
                EditorGUILayout.PropertyField(_bubbleRiseSpeed, new GUIContent("Rise Speed",
                    "Terminal rise of the LARGEST bubbles (world units/sec). Physical band: 0.20-0.30."));
                EditorGUILayout.PropertyField(_bubbleLifeRange, new GUIContent("Lifetime",
                    "Seconds. Surfacing pops a bubble first; ageing out dissolves it underwater."));
                EditorGUILayout.PropertyField(_bubbleSizeRange, new GUIContent("Size",
                    "World half-size range, skewed toward small."));
                EditorGUILayout.PropertyField(_bubbleWobble, new GUIContent("Wobble",
                    "Sideways zigzag while rising; amplitude scales with bubble size (only mm+ bubbles " +
                    "wobble in reality)."));
                WaterEditorUI.SubHeading("Layer Opacity");
                EditorGUILayout.PropertyField(_bubbleOpacity, new GUIContent("Bubble Opacity"));
            }
            if (_bubbleDriven)
                EditorGUILayout.HelpBox("Driven by the Foam Profile's Bubbles section.", MessageType.None);
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
