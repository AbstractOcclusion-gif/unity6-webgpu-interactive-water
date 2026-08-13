// WebGpuWater - inspector for WaterFoamProfile: the ONE friendly config surface for a body's foam.
//
// The profile is a plain ScriptableObject with four serializable sections (shared look, ambient
// foam/spray, density veil, splash), each gated by its own 'drive' bool. This editor renders those
// sections as the package's blue toggle-boxes (WaterEditorUI), adds a small preset row that scales
// the whole thing Small -> Big in one click, and an "Apply to selected body" button that points a
// body's WaterFoamParticles AND WaterSplashEmitter at this profile. Everything is written through
// SerializedProperty/SerializedObject, so Undo and prefab overrides work for free.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterFoamProfile))]
    internal sealed class WaterFoamProfileEditor : UnityEditor.Editor
    {
        // Section field names on WaterFoamProfile (top-level serialized members).
        const string LookField = "look";
        const string AmbientField = "ambient";
        const string MotionField = "motion";
        const string VeilField = "veil";
        const string SplashField = "splash";
        const string BubbleField = "bubbles";
        // The per-section enable flag, surfaced as the section header toggle (drawn once there, not in the body).
        const string DriveField = "drive";
        // The optional profile reference every foam component carries; set by "Apply to selected body".
        const string ProfileRefField = "profile";

        // Plain-language map of which particles each section actually drives. Shown as a HelpBox at the
        // top so the four sections are not guessed at from field names alone.
        const string SectionGuide =
            "What each section drives:\n" +
            "- Shared Look: appearance shared by foam particles (tint, opacity, sprite, flipbook and size bias).\n" +
            "- Floating Foam: surface clumps spawned from the simulated foam mask. Ripples can feed that mask.\n" +
            "- Ripple Crest Flecks: separate small dots emitted directly from moving ripple crests.\n" +
            "- Ambient Airborne Droplets: the Spray Chance fraction of foam-mask spawns launched into the air.\n" +
            "- Splash & Pump Bursts: impact/pump droplets and crowns; these are not mist or crest flecks.\n" +
            "- Landed Foam: the surface patches left when airborne droplets land.\n" +
            "- Motion: gravity, flow/wind drift and drag - how foam and spray ride the water.\n" +
            "- Density Veil: a connected screen-space foam wash, not another particle source.\n" +
            "- Bubbles: the underwater plume injected by splash and pump bursts.";

        // The ambient section is drawn in explicit groups rather than field order, so it reads the same
        // way the component does: the particle you are looking at first, the source that feeds it after.
        // Anything not listed here still gets drawn (see DrawAmbientSection), so a new field cannot go
        // missing just because this list was not updated.
        static readonly (string Field, string Label, string Tooltip)[] FloatingFoamFields =
        {
            ("lifeRange", "Clump Lifetime", "Lifetime in seconds of floating clumps spawned from the simulated foam mask. Longer life increases the visible population even when Spawn Rate is low."),
            ("sizeRange", "Clump Half-Size", "World-space half-size of foam-mask clumps. Approximate visible width is twice this value. This does not resize direct Ripple Crest Flecks."),
        };

        static readonly (string Field, string Label, string Tooltip)[] LandedFoamFields =
        {
            ("depositLifeRange", "Landed Lifetime",
             "Lifetime of the patch left by ANY droplet that lands - mist, surf lip or boat spray."),
            ("depositSizeRange", "Landed Half-Size", "World-space half-size of the landed patch; approximate visible width is twice this value."),
        };

        static readonly (string Field, string Label, string Tooltip)[] AmbientSourceFields =
        {
            ("spawnThreshold", "Mask Spawn Threshold", "Simulated foam-mask level (0-1) below which no floating clumps or ambient airborne droplets spawn. Increase this to keep weak ripple foam from producing particles."),
            ("spawnRate", "Mask Spawn Rate", "Expected foam-mask spawns per second per square world unit of fully foamed water. This does not control direct Ripple Crest Flecks or impact/pump bursts."),
            ("maxSpawnPerFrame", "Mask Max Per Frame", "Per-frame cap for foam-mask spawns only."),
            ("spawnMaxDistance", "Mask Spawn Distance", "Distance LOD for foam-mask spawns, in metres. 0 disables distance thinning."),
            ("sprayChance", "Airborne Droplet Chance", "Fraction of accepted foam-mask spawns launched as airborne droplets instead of remaining floating clumps. These are not impact droplets or ripple flecks."),
            ("sprayLaunchSpeed", "Airborne Launch Speed", "Upward launch speed of foam-mask airborne droplets."),
            ("sprayLifeRange", "Airborne Lifetime", "Foam-mask airborne droplets only. Impact and pump droplets use the Splash section."),
            ("spraySizeRange", "Airborne Half-Size", "World-space half-size of foam-mask airborne droplets. Approximate visible width is twice this value."),
        };

        static readonly (string Field, string Label, string Tooltip)[] RippleCrestFleckFields =
        {
            ("rippleCrestFlecksEnabled", "Enabled", "Emit small floating flecks from moving ripple crests."),
            ("rippleCrestFleckAmount", "Fleck Amount", "Multiplies the number of direct flecks selected from each moving ripple crest. Set to 0, or disable the source, to stop them."),
            ("rippleCrestFleckMaxPerFrame", "Max Per Frame", "Hard per-frame cap for this source."),
            ("rippleCrestFleckLifetimeRange", "Lifetime Range", "Lifetime range for ripple-crest flecks."),
            ("rippleCrestFleckSizeRange", "Fleck Half-Size", "World-space half-size of direct ripple-crest flecks. Approximate visible width is twice this value."),
            ("rippleCrestFleckMotion", "Ripple Motion", "How strongly flecks keep their ripple-propagation motion."),
        };

        // Section expanded state (editor-session only). Open by default so every knob is discoverable.
        bool _lookExpanded = true;
        bool _ambientExpanded = true;
        bool _motionExpanded = true;
        bool _veilExpanded = true;
        bool _splashExpanded = true;
        bool _bubbleExpanded = true;

        public override void OnInspectorGUI()
        {
            WaterEditorUI.DrawHeader("Water Foam Profile", "one asset drives a body's whole foam story");

            EditorGUILayout.HelpBox(SectionGuide, MessageType.None);
            EditorGUILayout.Space();

            DrawPresetRow();

            serializedObject.Update();
            SerializedProperty look = serializedObject.FindProperty(LookField);
            SerializedProperty ambient = serializedObject.FindProperty(AmbientField);
            SerializedProperty motion = serializedObject.FindProperty(MotionField);
            SerializedProperty veil = serializedObject.FindProperty(VeilField);
            SerializedProperty splash = serializedObject.FindProperty(SplashField);
            SerializedProperty bubbles = serializedObject.FindProperty(BubbleField);

            _lookExpanded = WaterEditorUI.SectionWithToggle("Shared Look", _lookExpanded,
                look.FindPropertyRelative(DriveField), () => DrawSectionFields(look));
            _ambientExpanded = WaterEditorUI.SectionWithToggle("Ambient Source & Foam Ranges", _ambientExpanded,
                ambient.FindPropertyRelative(DriveField), () => DrawAmbientSection(ambient));
            _motionExpanded = WaterEditorUI.SectionWithToggle("Motion", _motionExpanded,
                motion.FindPropertyRelative(DriveField), () => DrawSectionFields(motion));
            _veilExpanded = WaterEditorUI.SectionWithToggle("Density Veil", _veilExpanded,
                veil.FindPropertyRelative(DriveField), () => DrawVeilSection(veil));
            _splashExpanded = WaterEditorUI.SectionWithToggle("Splash & Pump Bursts", _splashExpanded,
                splash.FindPropertyRelative(DriveField), () => DrawSectionFields(splash));
            _bubbleExpanded = WaterEditorUI.SectionWithToggle("Bubbles", _bubbleExpanded,
                bubbles.FindPropertyRelative(DriveField), () => DrawSectionFields(bubbles));

            serializedObject.ApplyModifiedProperties();

            DrawApplyToBody();
            WaterEditorUI.DrawFooter();
        }

        // The ambient section, grouped by which particle each field reaches. Any field NOT named in the
        // three tables is drawn afterwards under "Other", so growing the struct can never silently drop a
        // knob out of the inspector - the failure mode of every hand-written field list.
        static void DrawAmbientSection(SerializedProperty ambient)
        {
            var drawn = new System.Collections.Generic.HashSet<string> { DriveField };

            if (ambient.FindPropertyRelative(DriveField).boolValue)
                EditorGUILayout.HelpBox(
                    "Drive is enabled. This profile overwrites the matching Water Foam Particles " +
                    "component values every frame. Tune them here, or turn Drive off to use the " +
                    "component's local values.", MessageType.Info);

            WaterEditorUI.SubHeading("Floating Foam - From Foam Mask");
            DrawGroup(ambient, FloatingFoamFields, drawn);
            WaterEditorUI.SubHeading("Landed foam (every source)");
            DrawGroup(ambient, LandedFoamFields, drawn);
            WaterEditorUI.SubHeading("Foam-Mask Spawn Source");
            EditorGUILayout.HelpBox("Ripples and turbulence can raise the simulated foam mask. These controls " +
                "turn that mask into floating clumps or airborne droplets. Direct ripple flecks and splash / " +
                "pump bursts use their own controls, so lowering Mask Spawn Rate does not stop those sources.",
                MessageType.None);
            DrawGroup(ambient, AmbientSourceFields, drawn);

            WaterEditorUI.SubHeading("Ripple Crest Flecks");
            EditorGUILayout.HelpBox(
                "Best suited to pools and other small-ripple water. Large or fast splash waves can " +
                "produce dense, strongly stretched fleck ribbons that may look oversized. For scenes " +
                "with heavy impacts, reduce Fleck Amount and Fleck Half-Size, or disable this source.",
                MessageType.Warning);
            DrawGroup(ambient, RippleCrestFleckFields, drawn);

            DrawRemainingFields(ambient, drawn);
        }

        static void DrawVeilSection(SerializedProperty veil)
        {
            if (veil.FindPropertyRelative(DriveField).boolValue)
                EditorGUILayout.HelpBox(
                    "Density Veil only renders on Water Foam Particles set to Screen-Space Density. " +
                    "Bodies using Quads ignore these settings and render their foam sprites instead.",
                    MessageType.Warning);
            DrawSectionFields(veil);
        }

        static void DrawGroup(SerializedProperty section,
                              (string Field, string Label, string Tooltip)[] fields,
                              System.Collections.Generic.HashSet<string> drawn)
        {
            foreach ((string field, string label, string tooltip) in fields)
            {
                SerializedProperty property = section.FindPropertyRelative(field);
                if (property == null) continue; // renamed or removed upstream: skip, never throw
                EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip), true);
                drawn.Add(field);
            }
        }

        static void DrawRemainingFields(SerializedProperty section,
                                        System.Collections.Generic.HashSet<string> drawn)
        {
            bool headingDrawn = false;
            SerializedProperty iterator = section.Copy();
            SerializedProperty end = section.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                if (drawn.Contains(iterator.name)) continue;
                if (!headingDrawn)
                {
                    WaterEditorUI.SubHeading("Other");
                    headingDrawn = true;
                }
                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        // Every field of a section EXCEPT 'drive' (which is the section header's toggle already).
        static void DrawSectionFields(SerializedProperty section)
        {
            SerializedProperty iterator = section.Copy();
            SerializedProperty end = section.GetEndProperty();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                if (iterator.name == DriveField) continue;
                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        // ---- presets ----------------------------------------------------------------------------

        void DrawPresetRow()
        {
            WaterEditorUI.SubHeading("Presets");
            EditorGUILayout.BeginHorizontal();
            foreach (FoamPreset preset in Presets)
                if (GUILayout.Button(preset.Name))
                    ApplyPreset(preset);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        // Write a preset's expressive dials into the profile. Structural flags (per-section 'drive',
        // spawn caps, textures) are left as authored - a preset scales the feel, it doesn't reset the asset.
        void ApplyPreset(in FoamPreset preset)
        {
            serializedObject.Update();

            SerializedProperty ambient = serializedObject.FindProperty(AmbientField);
            ambient.FindPropertyRelative("spawnRate").floatValue = preset.SpawnRate;
            ambient.FindPropertyRelative("sprayChance").floatValue = preset.SprayChance;
            ambient.FindPropertyRelative("sprayLaunchSpeed").floatValue = preset.SprayLaunchSpeed;
            ambient.FindPropertyRelative("lifeRange").vector2Value = preset.AmbientLifeRange;
            ambient.FindPropertyRelative("sizeRange").vector2Value = preset.AmbientSizeRange;

            SerializedProperty splash = serializedObject.FindProperty(SplashField);
            splash.FindPropertyRelative("maxParticlesPerBurst").intValue = preset.MaxParticlesPerBurst;
            splash.FindPropertyRelative("upwardBias").floatValue = preset.UpwardBias;
            splash.FindPropertyRelative("outwardSpread").floatValue = preset.OutwardSpread;
            splash.FindPropertyRelative("dropletSize").floatValue = preset.DropletSize;
            splash.FindPropertyRelative("lifetime").vector2Value = preset.SplashLifetime;
            splash.FindPropertyRelative("crownBaseSize").floatValue = preset.CrownBaseSize;
            splash.FindPropertyRelative("crownLifetime").floatValue = preset.CrownLifetime;

            serializedObject.FindProperty(LookField).FindPropertyRelative("sizeHeroPower").floatValue = preset.SizeHeroPower;

            serializedObject.ApplyModifiedProperties(); // registers Undo + marks the asset dirty
        }

        // A preset is a coherent Small..Big scaling of the fields a user actually feels (density, spray,
        // droplet size, lifetimes, hero bias). Named fields, no loose literals.
        readonly struct FoamPreset
        {
            public readonly string Name;
            public readonly float SpawnRate;
            public readonly float SprayChance;
            public readonly float SprayLaunchSpeed;
            public readonly Vector2 AmbientLifeRange;
            public readonly Vector2 AmbientSizeRange;
            public readonly int MaxParticlesPerBurst;
            public readonly float UpwardBias;
            public readonly float OutwardSpread;
            public readonly float DropletSize;
            public readonly Vector2 SplashLifetime;
            public readonly float CrownBaseSize;
            public readonly float CrownLifetime;
            public readonly float SizeHeroPower;

            public FoamPreset(string name, float spawnRate, float sprayChance, float sprayLaunchSpeed,
                Vector2 ambientLifeRange, Vector2 ambientSizeRange, int maxParticlesPerBurst, float upwardBias,
                float outwardSpread, float dropletSize, Vector2 splashLifetime, float crownBaseSize,
                float crownLifetime, float sizeHeroPower)
            {
                Name = name;
                SpawnRate = spawnRate;
                SprayChance = sprayChance;
                SprayLaunchSpeed = sprayLaunchSpeed;
                AmbientLifeRange = ambientLifeRange;
                AmbientSizeRange = ambientSizeRange;
                MaxParticlesPerBurst = maxParticlesPerBurst;
                UpwardBias = upwardBias;
                OutwardSpread = outwardSpread;
                DropletSize = dropletSize;
                SplashLifetime = splashLifetime;
                CrownBaseSize = crownBaseSize;
                CrownLifetime = crownLifetime;
                SizeHeroPower = sizeHeroPower;
            }
        }

        // Small/short -> the component defaults -> big/long. Retune freely; these are starting points.
        static readonly FoamPreset[] Presets =
        {
            new FoamPreset("Calm",    spawnRate: 18f, sprayChance: 0.04f, sprayLaunchSpeed: 0.4f,
                ambientLifeRange: new Vector2(1.0f, 2.5f), ambientSizeRange: new Vector2(0.015f, 0.04f),
                maxParticlesPerBurst: 24, upwardBias: 0.8f, outwardSpread: 1.0f, dropletSize: 0.015f,
                splashLifetime: new Vector2(0.4f, 0.9f), crownBaseSize: 0.3f, crownLifetime: 0.4f, sizeHeroPower: 2.5f),
            new FoamPreset("Default", spawnRate: 30f, sprayChance: 0.08f, sprayLaunchSpeed: 0.6f,
                ambientLifeRange: new Vector2(1.5f, 4.0f), ambientSizeRange: new Vector2(0.02f, 0.06f),
                maxParticlesPerBurst: 48, upwardBias: 1.0f, outwardSpread: 1.3f, dropletSize: 0.02f,
                splashLifetime: new Vector2(0.6f, 1.3f), crownBaseSize: 0.4f, crownLifetime: 0.5f, sizeHeroPower: 2.0f),
            new FoamPreset("Big",     spawnRate: 60f, sprayChance: 0.14f, sprayLaunchSpeed: 0.9f,
                ambientLifeRange: new Vector2(2.5f, 6.0f), ambientSizeRange: new Vector2(0.03f, 0.09f),
                maxParticlesPerBurst: 96, upwardBias: 1.4f, outwardSpread: 1.8f, dropletSize: 0.03f,
                splashLifetime: new Vector2(0.9f, 1.8f), crownBaseSize: 0.6f, crownLifetime: 0.7f, sizeHeroPower: 1.6f),
        };

        // ---- apply to a body --------------------------------------------------------------------

        void DrawApplyToBody()
        {
            WaterEditorUI.SubHeading("Apply");
            WaterVolume body = SelectedBody();
            using (new EditorGUI.DisabledScope(body == null))
                if (GUILayout.Button(body != null
                        ? $"Apply To \"{body.name}\""
                        : "Apply To Selected Body"))
                    ApplyToSelectedBody(body);

            EditorGUILayout.HelpBox(body != null
                ? $"Points {body.name}'s foam particles and splash emitter at this profile."
                : "Select a GameObject with a WaterVolume to apply this profile to its foam + splash.",
                MessageType.None);
        }

        // Point a body's foam AND splash components at this profile in one click. Edit-time safe:
        // resolves the emitter without creating one (runtime auto-create is a play-mode concern).
        void ApplyToSelectedBody(WaterVolume body)
        {
            if (body == null) return;
            var profile = (WaterFoamProfile)target;

            int applied = 0;
            applied += SetProfileReference(body.GetComponent<WaterFoamParticles>(), profile);
            applied += SetProfileReference(EmitterForEdit(body), profile);

            if (applied == 0)
                Debug.LogWarning($"[WebGpuWater] '{body.name}' has no WaterFoamParticles or WaterSplashEmitter " +
                                 "to receive the profile.", body);
            else
                Debug.Log($"[WebGpuWater] Applied '{profile.name}' to '{body.name}' ({applied} component(s)).", body);
        }

        static int SetProfileReference(Object component, WaterFoamProfile profile)
        {
            if (component == null) return 0;
            var serialized = new SerializedObject(component);
            SerializedProperty reference = serialized.FindProperty(ProfileRefField);
            if (reference == null) return 0;
            reference.objectReferenceValue = profile;
            serialized.ApplyModifiedProperties(); // Undo + dirty on the component
            return 1;
        }

        static WaterVolume SelectedBody()
        {
            GameObject selected = Selection.activeGameObject;
            return selected != null ? selected.GetComponentInChildren<WaterVolume>() : null;
        }

        // The body's own emitter for editing: its assigned reference, else one already under it. Never
        // creates one (that is ResolveSplashEmitter's play-mode job), so applying a profile stays inert.
        static WaterSplashEmitter EmitterForEdit(WaterVolume body)
            => body.splashEmitter != null ? body.splashEmitter : body.GetComponentInChildren<WaterSplashEmitter>();
    }
}
