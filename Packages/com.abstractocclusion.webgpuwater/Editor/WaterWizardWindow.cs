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
        const string MenuPath = MenuRoot + "Water Wizard";
        const string WindowTitle = "Water Wizard";

        const string RootObjectName = "WebGL Water";
        const string WaterBodyName = "Water Body";

        static readonly Vector3 DefaultExtent = new Vector3(2f, 1f, 2f);
        const float MinExtentComponent = 0.05f;
        const float WindowMinWidth = 340f;
        const float WindowMinHeight = 520f;

        // WaterVolume.foamBorderWidth default; applied when edge foam is enabled, zeroed otherwise.
        const float EdgeFoamBorderWidth = 0.08f;

        // Floor collider sizing, expressed relative to the water extent so props always land inside.
        const float FloorThickness = 0.1f;
        const float FloorDropBelowFloorMargin = 0.05f;
        const float FloorHorizontalScale = 2f;

        // Serialized paths into WaterVolume's private reflection + ocean blocks. Set via SerializedObject
        // so the wizard doesn't force a wider public runtime API just to flip these flags.
        // Aliases of the shared WaterVolumePropertyPaths registry (also used by WaterVolumeEditor).
        const string SsrPropertyPath = WaterVolumePropertyPaths.ScreenSpaceReflection;
        const string PlanarPropertyPath = WaterVolumePropertyPaths.PlanarReflection;
        const string OpenWaterPropertyPath = WaterVolumePropertyPaths.OpenWater;
        const string UnboundedOceanPropertyPath = WaterVolumePropertyPaths.UnboundedOcean;
        const string BodyTypePropertyPath = WaterVolumePropertyPaths.BodyType;

        // The base water type. Fog is a property of the type: only SurfaceWithFog turns it on, so the pool
        // and plain surface start fog-free. OpenWaterOcean drives the experimental large-body/clipmap path.
        enum WaterKind { LegacyAnalyticPool, SurfaceOnly, SurfaceWithFog, OpenWaterOcean }

        // Camera controller wired onto the scene camera: orbit around the water, or free-fly it.
        enum CameraMode { Orbit, Fly }

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

        // Window state is [SerializeField] throughout: EditorWindow serializes its fields across
        // domain reloads, so a script recompile no longer wipes the user's half-configured wizard
        // (extent, options, the object list).
        [SerializeField] WaterKind _kind = WaterKind.LegacyAnalyticPool;
        [SerializeField] Vector3 _extent = DefaultExtent;
        [SerializeField] bool _useCustomPoolTexture;
        [SerializeField] Texture _poolTexture;
        [SerializeField] bool _unboundedOcean;

        [SerializeField] WaterVolume.RippleQuality _rippleQuality = WaterVolume.RippleQuality.High;
        [SerializeField] bool _reflections = true;
        [SerializeField] WaterVolume.ReflectionMode _reflectionMode = WaterVolume.ReflectionMode.SSR;
        [SerializeField] bool _foam;
        [SerializeField] bool _edgeFoam;
        [SerializeField] bool _splash = true;
        [SerializeField] bool _godRays = true;
        [SerializeField] bool _addFloorCollider = true;
        [SerializeField] CameraMode _cameraMode = CameraMode.Orbit;

        [SerializeField] InteractionMode _objectMode = InteractionMode.Floatable;
        [SerializeField] BuoyancyPreset _buoyancyPreset = BuoyancyPreset.Normal;

        // Advanced floater tuning (big / complex objects). Defaults match WaterBuoyancy's own field defaults,
        // so leaving the foldout untouched wires a floater identically to the preset-only path.
        [SerializeField] bool _advancedFloater;
        [SerializeField] float _floaterObjectWidth;      // 0 = sample every ripple
        [SerializeField] float _floaterMaxBuoyancyForce; // 0 = uncapped
        [SerializeField] bool _floaterSurfaceRelativeDrag;
        [SerializeField] Vector3 _floaterDragCoefficients = Vector3.one;
        [SerializeField] bool _floaterIgnoreRipples;

        [SerializeField] List<GameObject> _objects = new List<GameObject>();
        [SerializeField] Vector2 _scroll;

        // Standalone floater creation (no water required - it attaches to whatever body hosts
        // it at runtime). Shares the buoyancy preset/advanced tuning of the section above it.
        [SerializeField] FloaterShape _newFloaterShape = FloaterShape.Cube;
        [SerializeField] Mesh _newFloaterMesh;
        [SerializeField] float _newFloaterSize = DefaultFloaterSize;

        // Boat creator (primitive hull by default; drop a model prefab in for a custom hull).
        [SerializeField] GameObject _boatHullModel;
        [SerializeField] bool _boatChaseCamera = true;

        [SerializeField] bool _createExpanded = true;
        [SerializeField] bool _objectsExpanded = true;
        [SerializeField] bool _boatExpanded = true;
        [SerializeField] bool _utilitiesExpanded;

        const float DefaultFloaterSize = 0.4f; // metres - reads clearly on the default 2x1x2 body

        [MenuItem(MenuPath)]
        static void Open()
        {
            var window = GetWindow<WaterWizardWindow>(utility: false, title: WindowTitle, focus: true);
            window.minSize = new Vector2(WindowMinWidth, WindowMinHeight);
            window.Show();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            WaterEditorUI.DrawHeader(WindowTitle, "Build a water body & its floaters");
            _createExpanded = WaterEditorUI.Section("Create Water", _createExpanded, DrawCreateSection);
            _objectsExpanded = WaterEditorUI.Section("Floating Objects", _objectsExpanded, DrawFloatingObjectsSection);
            _boatExpanded = WaterEditorUI.Section("Boat", _boatExpanded, DrawBoatSection);
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

            EditorGUILayout.HelpBox("Open water is experimental: analytic large waves by default; enable " +
                                    "Unbounded below for the camera-following horizon clipmap.", MessageType.Info);
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

            _cameraMode = (CameraMode)EditorGUILayout.EnumPopup(
                new GUIContent("Camera", "Orbit: rotate/zoom around the water. Fly: free WASD movement, " +
                                         "Q/E down/up, hold right-mouse to look, Shift to boost."),
                _cameraMode);

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
                // Show the stored choice as-is: gating happens at apply time (ApplyFoam does
                // _foam && _edgeFoam). The old `_foam && _edgeFoam` HERE wrote the gated value
                // back, permanently wiping the user's edge-foam choice every repaint while
                // Foam was off.
                _edgeFoam = EditorGUILayout.Toggle(
                    new GUIContent("Edge foam", "Foam band along the pool walls (only with foam)."),
                    _edgeFoam);
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
                Debug.LogError($"[WebGL Water] Water not created: every size component must be at least {MinExtentComponent}.");
                return;
            }

            // Idempotency guard: repeated clicks used to silently stack identical "WebGL Water"
            // roots (duplicate primaries defeat the render-scale/globals logic). Creating a second
            // root stays possible, but only deliberately.
            var existingRoot = GameObject.Find(RootObjectName);
            if (existingRoot != null &&
                !EditorUtility.DisplayDialog("WebGL Water",
                    $"The scene already has a '{RootObjectName}' root. Create another water setup anyway?",
                    "Create Another", "Cancel"))
            {
                Selection.activeObject = existingRoot;
                return;
            }

            bool withPool = _kind == WaterKind.LegacyAnalyticPool;

            // One undo step for the entire build (root, body, rig, floor, object wiring).
            Undo.SetCurrentGroupName("Create Water");
            int undoGroup = Undo.GetCurrentGroup();

            var root = NewUndoableGameObject(RootObjectName);
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildPoolMaterial: withPool))
            {
                Undo.RevertAllDownToGroup(undoGroup); // nothing persists from an aborted build
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

            // Splash unticked: the build context always creates a shared splash emitter, so remove it and
            // unwire the body when the user opted out - otherwise props still splash despite the toggle.
            // The crown system lives on its own sibling object, so it must go too (it used to be
            // silently left behind as an orphan).
            if (!_splash && ctx.Splash != null)
            {
                body.splashEmitter = null;
                if (ctx.Splash.crownParticles != null)
                    Undo.DestroyObjectImmediate(ctx.Splash.crownParticles.gameObject);
                Undo.DestroyObjectImmediate(ctx.Splash.gameObject);
            }

            ApplyCameraMode(body);

            EditorUtility.SetDirty(body);

            if (_addFloorCollider)
                CreateFloorForExtent(root.transform, _extent);

            WireObjects();

            Selection.activeObject = root;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
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

            // Label the archetype so the inspector shows the right sections/defaults: an Ocean if the
            // user picked open water, a Lake for a large bounded body (same size cut-off that arms open
            // water above), else a Pond. Advisory only - it matches the behaviour set here.
            WaterVolume.WaterBodyType bodyType =
                _kind == WaterKind.OpenWaterOcean ? WaterVolume.WaterBodyType.Ocean
                : bigExtent                       ? WaterVolume.WaterBodyType.Lake
                                                  : WaterVolume.WaterBodyType.Pond;

            var serialized = new SerializedObject(body);
            serialized.FindProperty(OpenWaterPropertyPath).boolValue = openWater;
            serialized.FindProperty(UnboundedOceanPropertyPath).boolValue = unbounded;
            serialized.FindProperty(BodyTypePropertyPath).enumValueIndex = (int)bodyType;
            serialized.ApplyModifiedProperties(); // rides the Create Water undo group
            return openWater;
        }

        // Swap the scene camera's controller to match the chosen mode. SetUpCamera always rigs an
        // OrbitCamera; for Fly we remove it and add a FlyCamera (and drop the body's orbit reference so
        // its background-drag rotation is inert). Only one controller drives the transform at a time.
        void ApplyCameraMode(WaterVolume body)
        {
            Camera cam = body.targetCamera;
            if (cam == null)
                return;

            if (_cameraMode == CameraMode.Fly)
            {
                OrbitCamera orbit = cam.GetComponent<OrbitCamera>();
                if (orbit != null) Undo.DestroyObjectImmediate(orbit);
                body.orbit = null;
                if (cam.GetComponent<FlyCamera>() == null) Undo.AddComponent<FlyCamera>(cam.gameObject);
                return;
            }

            FlyCamera fly = cam.GetComponent<FlyCamera>();
            if (fly != null) Undo.DestroyObjectImmediate(fly);
            if (body.orbit == null) body.orbit = cam.GetComponent<OrbitCamera>();
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
            serialized.ApplyModifiedProperties(); // rides the Create Water undo group
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
                    Undo.SetCurrentGroupName("Configure Water Objects");
                    WireObjects();
                    Debug.Log($"[WebGL Water] Configured listed objects as {_objectMode}.");
                }
            }

            DrawCreateFloater();
        }

        // One-click floater from scratch (vs the slots above, which retrofit EXISTING objects):
        // spawns a shape above the water (or the origin), fully wired with the section's buoyancy
        // preset + advanced tuning, in one undo step. No water needs to exist yet.
        void DrawCreateFloater()
        {
            WaterEditorUI.SubHeading("Create New Floater");
            _newFloaterShape = (FloaterShape)EditorGUILayout.EnumPopup(
                new GUIContent("Shape", "Built-in primitive, or Custom Mesh to use your own model."),
                _newFloaterShape);
            if (_newFloaterShape == FloaterShape.CustomMesh)
                _newFloaterMesh = (Mesh)EditorGUILayout.ObjectField(
                    new GUIContent("Mesh", "Hull mesh; gets a convex MeshCollider + the pipeline's default material."),
                    _newFloaterMesh, typeof(Mesh), allowSceneObjects: false);
            _newFloaterSize = EditorGUILayout.FloatField(
                new GUIContent("Size (m)", "Uniform scale of the spawned object."), _newFloaterSize);

            if (GUILayout.Button("Create Buoyant Object", GUILayout.Height(24f)))
            {
                Undo.SetCurrentGroupName("Create Buoyant Object");
                int undoGroup = Undo.GetCurrentGroup();
                GameObject prop = CreateBuoyantObjectBody(_newFloaterShape, _newFloaterMesh, _newFloaterSize);
                if (prop == null) return;
                MakeFloatable(prop); // same wiring + preset path as the retrofit slots
                Selection.activeObject = prop;
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log("[WebGL Water] Buoyant object created - it drops in and floats on whatever " +
                          "water body hosts it (no wiring needed).");
            }
        }

        // ---- boat section ----------------------------------------------------
        // Resurrects the retired boat-demo rig as a first-class creator: primitive hull (works
        // with zero assets) or a custom hull mesh, probe buoyancy + BoatController drive, and an
        // optional yaw-only chase camera. Drive with W/S (throttle) and A/D (steer).
        void DrawBoatSection()
        {
            EditorGUILayout.HelpBox("Creates a drivable boat (probe buoyancy + BoatController). It floats on " +
                                    "whatever water hosts it - build an ocean first for the full experience. " +
                                    "Drive with W/S and A/D.", MessageType.None);

            _boatHullModel = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Hull model (optional)", "Leave empty for the built-in primitive hull; assign a model " +
                                                        "prefab (multi-mesh/material is fine) for a custom hull. The boat " +
                                                        "ROOT stays at scale (1,1,1) with a collider auto-fitted to the " +
                                                        "model's bounds, so nothing gets stretched."),
                _boatHullModel, typeof(GameObject), allowSceneObjects: false);
            _boatChaseCamera = EditorGUILayout.Toggle(
                new GUIContent("Chase camera", "Swap the scene camera's controller for a yaw-only follow camera " +
                                               "locked to the boat (orbit/fly are disabled, not removed)."),
                _boatChaseCamera);

            if (GUILayout.Button("Create Boat", GUILayout.Height(26f)))
            {
                Undo.SetCurrentGroupName("Create Boat");
                int undoGroup = Undo.GetCurrentGroup();
                GameObject boat = CreateBoat(_boatHullModel, withSplash: _splash);
                if (boat == null) return;
                if (_boatChaseCamera) FocusSceneOnBoat(boat);
                Selection.activeObject = boat;
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log("[WebGL Water] Boat created. Press Play - drive with W/S (throttle) and A/D (steer).");
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

        // Components are added through Undo so wiring a USER'S prop is reversible like the
        // rest of the build (these are pre-existing scene objects, not ours).
        static T EnsureComponentUndoable<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : Undo.AddComponent<T>(go);
        }

        // A buoyant prop's full component set (floats, displaces, optionally splashes, lit by its lake),
        // applied in place so the user's own mesh/material/transform are preserved.
        void MakeFloatable(GameObject go)
        {
            EnsureComponentUndoable<Rigidbody>(go);
            EnsureComponentUndoable<WaterInteractable>(go);
            WaterBuoyancy buoyancy = EnsureComponentUndoable<WaterBuoyancy>(go);
            Undo.RecordObject(buoyancy, "Configure Floater"); // may be a pre-existing, hand-tuned component
            ApplyBuoyancyPreset(buoyancy);
            ApplyAdvancedBuoyancy(buoyancy);
            if (_splash) EnsureComponentUndoable<WaterSplash>(go);
            EnsureComponentUndoable<WaterMembership>(go);
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
            EnsureComponentUndoable<WaterInteractable>(go);
            EnsureComponentUndoable<WaterMembership>(go);
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
