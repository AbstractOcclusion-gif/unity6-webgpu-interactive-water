// WebGL Water - the single authoring entry point.
//
// Menu: Window > AbstractOcclusion > WebGpuWater > Water Wizard
//
// One window with two independent jobs:
//   1. Build a water body from a base type (legacy analytic pool, surface only, or surface + fog),
//      layering the shared look options (reflection, foam, splash) on top of whichever type is chosen.
//   2. Turn scene objects into buoyant floaters or static interactables - on its own, so props can be
//      configured without (or before) any water in the scene.
// Scene composition is delegated to WaterBuildKit; this file only maps UI state onto those generators.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed class WaterWizardWindow : EditorWindow
    {
        const string MenuPath = "Window/AbstractOcclusion/WebGpuWater/Water Wizard";
        const string WindowTitle = "Water Wizard";

        const string RootObjectName = "WebGL Water";
        const string WaterBodyName = "Water Body";

        static readonly Vector3 DefaultExtent = new Vector3(2f, 1f, 2f);
        const float MinExtentComponent = 0.05f;

        // WaterVolume.foamBorderWidth default; applied when edge foam is enabled, zeroed otherwise.
        const float EdgeFoamBorderWidth = 0.08f;

        // Floor collider sizing, expressed relative to the water extent so props always land inside.
        const float FloorThickness = 0.1f;
        const float FloorDropBelowFloorMargin = 0.05f;
        const float FloorHorizontalScale = 2f;

        // Serialized paths into WaterVolume's private reflection + ocean blocks. Set via SerializedObject
        // so the wizard doesn't force a wider public runtime API just to flip these flags.
        const string SsrPropertyPath = "reflectionSettings.useScreenSpaceReflection";
        const string PlanarPropertyPath = "reflectionSettings.usePlanarReflection";
        const string OpenWaterPropertyPath = "ocean.openWater";
        const string UnboundedOceanPropertyPath = "ocean.unboundedOcean";

        // The base water type. Fog is a property of the type: only SurfaceWithFog turns it on, so the pool
        // and plain surface start fog-free. OpenWaterOcean drives the experimental large-body/clipmap path.
        enum WaterKind { LegacyAnalyticPool, SurfaceOnly, SurfaceWithFog, OpenWaterOcean }

        enum InteractionMode { Floatable, InteractableStatic }
        enum BuoyancyPreset { Light, Normal, Heavy }

        // Buoyancy tuning per preset. Normal mirrors WaterBuoyancy's own field defaults; Light rides
        // higher, Heavy sits lower. Damping is shared - only the float strength changes between presets.
        readonly struct FloaterTuning
        {
            public readonly float Buoyancy;
            public readonly float LinearDamping;
            public readonly float AngularDamping;

            public FloaterTuning(float buoyancy, float linearDamping, float angularDamping)
            {
                Buoyancy = buoyancy;
                LinearDamping = linearDamping;
                AngularDamping = angularDamping;
            }
        }

        static readonly Dictionary<BuoyancyPreset, FloaterTuning> BuoyancyPresets =
            new Dictionary<BuoyancyPreset, FloaterTuning>
            {
                { BuoyancyPreset.Light, new FloaterTuning(4.0f, 2.0f, 1.0f) },
                { BuoyancyPreset.Normal, new FloaterTuning(2.5f, 2.0f, 1.0f) },
                { BuoyancyPreset.Heavy, new FloaterTuning(1.2f, 2.0f, 1.0f) },
            };

        WaterKind _kind = WaterKind.LegacyAnalyticPool;
        Vector3 _extent = DefaultExtent;
        bool _useCustomPoolTexture;
        Texture _poolTexture;
        bool _unboundedOcean;

        WaterVolume.RippleQuality _rippleQuality = WaterVolume.RippleQuality.High;
        bool _reflections = true;
        WaterVolume.ReflectionMode _reflectionMode = WaterVolume.ReflectionMode.SSR;
        bool _foam;
        bool _edgeFoam;
        bool _splash = true;
        bool _godRays = true;
        bool _addFloorCollider = true;

        InteractionMode _objectMode = InteractionMode.Floatable;
        BuoyancyPreset _buoyancyPreset = BuoyancyPreset.Normal;

        // Advanced floater tuning (big / complex objects). Defaults match WaterBuoyancy's own field defaults,
        // so leaving the foldout untouched wires a floater identically to the preset-only path.
        bool _advancedFloater;
        float _floaterObjectWidth;                       // 0 = sample every ripple
        float _floaterMaxBuoyancyForce;                  // 0 = uncapped
        bool _floaterSurfaceRelativeDrag;
        Vector3 _floaterDragCoefficients = Vector3.one;
        bool _floaterIgnoreRipples;

        readonly List<GameObject> _objects = new List<GameObject>();
        Vector2 _scroll;

        bool _createExpanded = true;
        bool _objectsExpanded = true;
        bool _utilitiesExpanded;

        [MenuItem(MenuPath)]
        static void Open()
        {
            var window = GetWindow<WaterWizardWindow>(utility: false, title: WindowTitle, focus: true);
            window.minSize = new Vector2(340f, 520f);
            window.Show();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            WaterEditorUI.DrawHeader(WindowTitle, "Build a water body & its floaters");
            _createExpanded = WaterEditorUI.Section("Create Water", _createExpanded, DrawCreateSection);
            _objectsExpanded = WaterEditorUI.Section("Floating Objects", _objectsExpanded, DrawFloatingObjectsSection);
            _utilitiesExpanded = WaterEditorUI.Section("Utilities", _utilitiesExpanded, DrawUtilitiesSection);
            WaterEditorUI.DrawFooter();
            EditorGUILayout.EndScrollView();
        }

        // ---- create section --------------------------------------------------
        void DrawCreateSection()
        {
            _kind = (WaterKind)EditorGUILayout.EnumPopup(
                new GUIContent("Type", "Legacy analytic pool (walls/floor/caustics, no fog), a bare water " +
                                       "surface, a surface with underwater fog, or experimental open-water " +
                                       "ocean (large-body + horizon clipmap)."),
                _kind);

            _extent = EditorGUILayout.Vector3Field(
                new GUIContent("Size (extent)", "Half-extents of the water volume: X/Z horizontal, Y depth. " +
                                                "Large horizontal extents auto-enable open water."),
                _extent);

            DrawPoolTexturePicker();
            DrawOceanOptions();
            EditorGUILayout.Space(6f);
            DrawSharedOptions();

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(!ExtentIsValid()))
            {
                if (GUILayout.Button("Create Water", GUILayout.Height(30f)))
                    CreateWater();
            }
            if (!ExtentIsValid())
                EditorGUILayout.HelpBox($"Every size component must be at least {MinExtentComponent}.", MessageType.Warning);
        }

        // The pool tiles only exist on the analytic pool; hide the picker for surface types. The custom
        // texture field stays collapsed behind a button so the default look needs no interaction.
        void DrawPoolTexturePicker()
        {
            if (_kind != WaterKind.LegacyAnalyticPool)
                return;

            if (!_useCustomPoolTexture)
            {
                if (GUILayout.Button("Custom pool texture..."))
                    _useCustomPoolTexture = true;
                return;
            }

            EditorGUILayout.BeginHorizontal();
            _poolTexture = (Texture)EditorGUILayout.ObjectField(
                new GUIContent("Pool texture", "Tile albedo for the pool walls/floor. Leave to fall back to the default."),
                _poolTexture, typeof(Texture), allowSceneObjects: false);
            if (GUILayout.Button("Default", GUILayout.Width(64f)))
            {
                _poolTexture = null;
                _useCustomPoolTexture = false;
            }
            EditorGUILayout.EndHorizontal();
        }

        // Open-water settings live only on the Ocean type. The horizon clipmap is the "infinite" look;
        // it's experimental, so it's flagged rather than silently promising a full ocean.
        void DrawOceanOptions()
        {
            if (_kind != WaterKind.OpenWaterOcean)
                return;

            EditorGUILayout.HelpBox("Open water is experimental: analytic large waves by default; the horizon " +
                                    "clipmap needs the WEBGPUWATER_LARGE_BODY define.", MessageType.Info);
            EditorGUI.indentLevel++;
            _unboundedOcean = EditorGUILayout.Toggle(
                new GUIContent("Unbounded (to horizon)", "Extend the surface to the horizon with a camera-following clipmap."),
                _unboundedOcean);
            EditorGUI.indentLevel--;
        }

        void DrawSharedOptions()
        {
            WaterEditorUI.SubHeading("Options (applied to any type)");

            _rippleQuality = (WaterVolume.RippleQuality)EditorGUILayout.EnumPopup(
                new GUIContent("Ripple quality", "Sim grid density + matched surface mesh for interactive " +
                                                 "ripples. Higher = rounder ripples at more GPU cost."),
                _rippleQuality);

            _reflections = EditorGUILayout.Toggle(
                new GUIContent("Reflection", "Rich reflection on top of the sky base."), _reflections);
            using (new EditorGUI.DisabledScope(!_reflections))
            {
                EditorGUI.indentLevel++;
                _reflectionMode = (WaterVolume.ReflectionMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Mode", "SkyOnly = sky base only; SSR = screen-space; Planar = mirror render."),
                    _reflectionMode);
                EditorGUI.indentLevel--;
            }

            _foam = EditorGUILayout.Toggle(
                new GUIContent("Foam", "Turbulence-driven surface foam plus its GPU particle system."), _foam);
            using (new EditorGUI.DisabledScope(!_foam))
            {
                EditorGUI.indentLevel++;
                _edgeFoam = EditorGUILayout.Toggle(
                    new GUIContent("Edge foam", "Foam band along the pool walls (only with foam)."),
                    _foam && _edgeFoam);
                EditorGUI.indentLevel--;
            }

            _splash = EditorGUILayout.Toggle(
                new GUIContent("Splash", "Give buoyant props a splash trigger when they punch the surface."), _splash);
            _godRays = EditorGUILayout.Toggle(
                new GUIContent("God rays", "Underwater caustic-masked light shafts."), _godRays);
            _addFloorCollider = EditorGUILayout.Toggle(
                new GUIContent("Floor collider", "Thin collider under the water so sinking props have something to rest on."),
                _addFloorCollider);
        }

        bool ExtentIsValid()
        {
            return _extent.x >= MinExtentComponent
                && _extent.y >= MinExtentComponent
                && _extent.z >= MinExtentComponent;
        }

        void CreateWater()
        {
            if (!ExtentIsValid())
            {
                Debug.LogError("[WebGL Water] Water not created: every size component must be positive.");
                return;
            }

            bool withPool = _kind == WaterKind.LegacyAnalyticPool;

            var root = new GameObject(RootObjectName);
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildPoolMaterial: withPool))
            {
                Object.DestroyImmediate(root);
                return;
            }

            var body = CreateWaterBody(ctx, root.transform, WaterBodyName, Vector3.zero, _extent,
                                       primary: true, withPool: withPool, withGodRays: _godRays,
                                       withFoamParticles: _foam);

            body.rippleQuality = _rippleQuality;
            ApplyBaseType(body);
            ApplyCustomPoolTexture(body, withPool);
            ApplyReflection(body);
            ApplyFoam(body);
            bool openWater = ApplyOpenWater(body);
            EditorUtility.SetDirty(body);

            if (_addFloorCollider)
                CreateFloorForExtent(root.transform, _extent);

            WireObjects();

            Selection.activeObject = root;
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGL Water] Water built ({RootObjectName}, {_kind}, openWater={openWater}). Press Play.");
        }

        void ApplyBaseType(WaterVolume body)
        {
            body.WaterFog = _kind == WaterKind.SurfaceWithFog;
        }

        // Open water turns on for the Ocean type, or for any bounded type whose footprint outgrows the
        // body's own large-body threshold (the same value that arms the runtime sim window). The horizon
        // clipmap only comes on when the user asks for it on the Ocean type. Returns the resolved flag.
        bool ApplyOpenWater(WaterVolume body)
        {
            bool bigExtent = Mathf.Max(_extent.x, _extent.z) > body.largeBodyThreshold;
            bool openWater = _kind == WaterKind.OpenWaterOcean || bigExtent;
            bool unbounded = _kind == WaterKind.OpenWaterOcean && _unboundedOcean;

            var serialized = new SerializedObject(body);
            serialized.FindProperty(OpenWaterPropertyPath).boolValue = openWater;
            serialized.FindProperty(UnboundedOceanPropertyPath).boolValue = unbounded;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return openWater;
        }

        void ApplyCustomPoolTexture(WaterVolume body, bool withPool)
        {
            if (!withPool || !_useCustomPoolTexture || _poolTexture == null)
                return;

            body.tiles = _poolTexture;
        }

        // reflectionSettings is a private serialized block; SerializedProperty is the access path that
        // doesn't require exposing new runtime API. SkyOnly = both flags off.
        void ApplyReflection(WaterVolume body)
        {
            bool useSsr = _reflections && _reflectionMode == WaterVolume.ReflectionMode.SSR;
            bool usePlanar = _reflections && _reflectionMode == WaterVolume.ReflectionMode.Planar;

            var serialized = new SerializedObject(body);
            serialized.FindProperty(SsrPropertyPath).boolValue = useSsr;
            serialized.FindProperty(PlanarPropertyPath).boolValue = usePlanar;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        void ApplyFoam(WaterVolume body)
        {
            body.Foam = _foam;
            body.foamBorderWidth = (_foam && _edgeFoam) ? EdgeFoamBorderWidth : 0f;
        }

        void CreateFloorForExtent(Transform parent, Vector3 extent)
        {
            var center = new Vector3(0f, -(extent.y + FloorDropBelowFloorMargin), 0f);
            var size = new Vector3(extent.x * FloorHorizontalScale, FloorThickness, extent.z * FloorHorizontalScale);
            CreateFloorCollider(parent, center, size);
        }

        // ---- floating objects section ---------------------------------------
        void DrawFloatingObjectsSection()
        {
            EditorGUILayout.HelpBox("Configure props on their own - they attach to whatever water hosts them at " +
                                    "runtime, so no water needs to exist yet.", MessageType.None);

            _objectMode = (InteractionMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode", "Floatable = buoyant rigidbody prop; Interactable = static object that displaces the surface."),
                _objectMode);

            using (new EditorGUI.DisabledScope(_objectMode != InteractionMode.Floatable))
            {
                EditorGUI.indentLevel++;
                _buoyancyPreset = (BuoyancyPreset)EditorGUILayout.EnumPopup(
                    new GUIContent("Buoyancy", "Light rides high, Heavy sits low. Splash follows the Options toggle above."),
                    _buoyancyPreset);
                DrawAdvancedFloaterOptions();
                EditorGUI.indentLevel--;
            }

            DrawObjectSlots();

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(!HasAnyObject()))
            {
                if (GUILayout.Button("Apply To Listed Objects", GUILayout.Height(24f)))
                {
                    WireObjects();
                    Debug.Log($"[WebGL Water] Configured listed objects as {_objectMode}.");
                }
            }
        }

        void DrawObjectSlots()
        {
            for (int i = 0; i < _objects.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _objects[i] = (GameObject)EditorGUILayout.ObjectField(_objects[i], typeof(GameObject), allowSceneObjects: true);
                if (GUILayout.Button("-", GUILayout.Width(24f)))
                {
                    _objects.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("Add object slot"))
                _objects.Add(null);
        }

        bool HasAnyObject()
        {
            foreach (GameObject go in _objects)
                if (go != null) return true;
            return false;
        }

        void WireObjects()
        {
            foreach (GameObject go in _objects)
            {
                if (go == null) continue;
                if (_objectMode == InteractionMode.Floatable) MakeFloatable(go);
                else MakeInteractable(go);
                EditorUtility.SetDirty(go);
            }
        }

        // A buoyant prop's full component set (floats, displaces, optionally splashes, lit by its lake),
        // applied in place so the user's own mesh/material/transform are preserved.
        void MakeFloatable(GameObject go)
        {
            EnsureComponent<Rigidbody>(go);
            EnsureComponent<WaterInteractable>(go);
            WaterBuoyancy buoyancy = EnsureComponent<WaterBuoyancy>(go);
            ApplyBuoyancyPreset(buoyancy);
            ApplyAdvancedBuoyancy(buoyancy);
            if (_splash) EnsureComponent<WaterSplash>(go);
            EnsureComponent<WaterMembership>(go);
        }

        void ApplyBuoyancyPreset(WaterBuoyancy buoyancy)
        {
            FloaterTuning tuning = BuoyancyPresets[_buoyancyPreset];
            buoyancy.buoyancy = tuning.Buoyancy;
            buoyancy.waterLinearDamping = tuning.LinearDamping;
            buoyancy.waterAngularDamping = tuning.AngularDamping;
        }

        // The opt-in tuning for large / complex floaters. Values default to WaterBuoyancy's own defaults, so
        // this is a no-op unless the user opened the Advanced foldout and changed something.
        void ApplyAdvancedBuoyancy(WaterBuoyancy buoyancy)
        {
            buoyancy.objectWidth = Mathf.Max(0f, _floaterObjectWidth);
            buoyancy.maxBuoyancyForce = Mathf.Max(0f, _floaterMaxBuoyancyForce);
            buoyancy.surfaceRelativeDrag = _floaterSurfaceRelativeDrag;
            buoyancy.dragCoefficients = _floaterDragCoefficients;
            buoyancy.ignoreInteractiveRipples = _floaterIgnoreRipples;
        }

        // Advanced foldout for big / complex objects: ripple LOD, force cap, and wave-carrying drag.
        void DrawAdvancedFloaterOptions()
        {
            _advancedFloater = EditorGUILayout.Foldout(_advancedFloater, "Advanced (big / complex objects)", toggleOnLabelClick: true);
            if (!_advancedFloater)
                return;

            EditorGUI.indentLevel++;
            _floaterObjectWidth = EditorGUILayout.FloatField(
                new GUIContent("Object width (LOD)", "Ignore wind-wave ripples shorter than this width (m) so a " +
                                                     "large float rides the swell without buzzing. 0 = sample every wave."),
                _floaterObjectWidth);
            _floaterMaxBuoyancyForce = EditorGUILayout.FloatField(
                new GUIContent("Max buoyant accel", "Cap on per-point buoyant acceleration so a deeply plunged " +
                                                    "float doesn't erupt. 0 = uncapped."),
                _floaterMaxBuoyancyForce);
            _floaterSurfaceRelativeDrag = EditorGUILayout.Toggle(
                new GUIContent("Surface-relative drag", "Drag against the water's own velocity (waves carry the " +
                                                        "object) instead of braking toward world rest."),
                _floaterSurfaceRelativeDrag);
            using (new EditorGUI.DisabledScope(!_floaterSurfaceRelativeDrag))
            {
                EditorGUI.indentLevel++;
                _floaterDragCoefficients = EditorGUILayout.Vector3Field(
                    new GUIContent("Drag coefficients", "Per-axis (object-local) drag scale for surface-relative drag."),
                    _floaterDragCoefficients);
                EditorGUI.indentLevel--;
            }
            _floaterIgnoreRipples = EditorGUILayout.Toggle(
                new GUIContent("Ignore interactive ripples", "Sample the analytic surface (rest + wind + swell) only, " +
                                                             "so a self-emitting body (e.g. a boat) isn't carried by its " +
                                                             "own wake. Leave off so pool floaters bob on ripples."),
                _floaterIgnoreRipples);
            EditorGUI.indentLevel--;
        }

        // A static interactable displaces the surface but stays put (no Rigidbody).
        static void MakeInteractable(GameObject go)
        {
            EnsureComponent<WaterInteractable>(go);
            EnsureComponent<WaterMembership>(go);
        }

        static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();
        }

        // ---- utilities section ----------------------------------------------
        void DrawUtilitiesSection()
        {
            if (GUILayout.Button(new GUIContent("Create WaterVolume Prefab",
                "Save a reusable single-body water prefab that resolves camera/sun at runtime.")))
                WaterSceneBuilder.CreateWaterVolumePrefab();

            if (GUILayout.Button(new GUIContent("Add Foam Particles To Selected",
                "Retrofit GPU foam particles onto the selected WaterVolume.")))
                WaterSceneBuilder.AddFoamParticlesToSelection();

            if (GUILayout.Button(new GUIContent("Assign Foam Textures To Scene Water",
                "Assign the foam flipbook + normal map to every water material in the open scene.")))
                WaterSceneBuilder.AssignFoamTexturesToSceneWater();

            if (GUILayout.Button(new GUIContent("Upgrade Splash Materials (lit)",
                "Upgrade the shared splash materials to the lit splash shader in place.")))
                WaterSceneBuilder.UpgradeSplashMaterialsMenu();

            if (GUILayout.Button(new GUIContent("Add Water Body (secondary)",
                "Add a second, independent water body next to the primary one.")))
                WaterSceneBuilder.AddSecondaryBody();
        }
    }
}
