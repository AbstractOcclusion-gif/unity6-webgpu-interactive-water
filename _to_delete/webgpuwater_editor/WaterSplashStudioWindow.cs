// WebGpuWater - Splash Studio: a window for authoring a body's splash/foam feel by SEEING it.
//
// It hosts the active WaterFoamProfile (the Move-2 inspector, presets and all) and, in one click,
// builds a small "stage" into the open scene - a water body with a rock and a boat that glides a
// circle past it, each carrying a WaterSprayPump. The water renders live in edit mode (WaterVolume
// is [ExecuteAlways] + the Live Water Preview driver), but the spray TRIGGERS (pump, movement) are
// play-mode systems, so the workflow is: build the stage, press Play, then scrub the profile below
// and watch the rock-splash and bow-spray react (the profile re-applies on every emit).
//
// The stage is a kinematic demo, not a physics scene: the boat is driven by WaterShowcaseMover
// (deterministic circular path), so there is no rigidbody to fight and the visual is repeatable.
// Everything is parented under one stage root, so teardown is a single destroy. All scene edits go
// through Undo; menu lives under the package's Window root (Asset Store policy - no top-level menus).
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed class WaterSplashStudioWindow : EditorWindow
    {
        const string MenuPath = WaterBuildKit.MenuRoot + "Splash Studio";
        const string WindowTitle = "Splash Studio";

        // One root holds the whole stage, so building refuses/replaces a stale one and clearing is a
        // single destroy. The bracketed name keeps it obvious (and findable) in the hierarchy.
        const string StageRootName = "— Splash Studio Stage —";
        const string StudioWaterName = "Studio Water";
        const string StudioRockName = "Studio Rock";
        const string StudioBoatName = "Studio Boat";
        const string NewProfileAssetName = "/StudioFoamProfile.asset";

        // Stage geometry (metres / seconds). Starting points - retune on the built objects.
        static readonly Vector3 StageExtent = new Vector3(10f, 3f, 10f);
        const float RockSize = 1.6f;
        const float BoatSize = 1.2f;
        static readonly Vector3 BoatHullScale = new Vector3(0.6f, 0.4f, 1.6f); // long, low - reads as "driving"
        const float BoatLapRadius = 4f;   // < stage extent, so the boat stays over water
        const float BoatLapSeconds = 10f;
        const float SceneFramePadding = 2f;

        // The optional profile reference field every foam component carries (set on the stage body).
        const string ProfileRefField = "profile";

        [SerializeField] WaterFoamProfile _profile;
        UnityEditor.Editor _profileEditor;
        Vector2 _scroll;

        [MenuItem(MenuPath)]
        static void Open()
        {
            var window = GetWindow<WaterSplashStudioWindow>(WindowTitle);
            window.minSize = new Vector2(360f, 480f);
            window.Show();
        }

        void OnDisable()
        {
            if (_profileEditor != null) DestroyImmediate(_profileEditor);
            _profileEditor = null;
        }

        void OnGUI()
        {
            WaterEditorUI.DrawHeader(WindowTitle, "build a live stage, scrub a profile, watch it splash");

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawProfilePicker();
            DrawStageControls();
            DrawProfileEditor();

            EditorGUILayout.EndScrollView();
            WaterEditorUI.DrawFooter();
        }

        // ---- active profile ---------------------------------------------------------------------

        void DrawProfilePicker()
        {
            WaterEditorUI.SubHeading("Active Profile");
            var picked = (WaterFoamProfile)EditorGUILayout.ObjectField(
                "Profile", _profile, typeof(WaterFoamProfile), allowSceneObjects: false);
            if (picked != _profile) SetProfile(picked);

            if (_profile == null && GUILayout.Button("New Profile"))
                CreateProfile();

            EditorGUILayout.Space();
        }

        void SetProfile(WaterFoamProfile profile)
        {
            _profile = profile;
            if (_profileEditor != null) DestroyImmediate(_profileEditor);
            _profileEditor = null; // rebuilt lazily for the new target
        }

        void CreateProfile()
        {
            WaterBuildKit.EnsureGenFolder();
            var profile = CreateInstance<WaterFoamProfile>();
            string path = AssetDatabase.GenerateUniqueAssetPath(WaterBuildKit.Gen + NewProfileAssetName);
            AssetDatabase.CreateAsset(profile, path);
            AssetDatabase.SaveAssets();
            SetProfile(profile);
            Selection.activeObject = profile;
        }

        // ---- stage ------------------------------------------------------------------------------

        void DrawStageControls()
        {
            WaterEditorUI.SubHeading("Stage");
            EditorGUILayout.HelpBox(
                "Builds a water body with a rock and a gliding boat, each with a spray pump. Enter Play to " +
                "see the spray, then scrub the profile below - it updates the splash live.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Stage")) BuildStage();
            using (new EditorGUI.DisabledScope(FindStageRoot() == null))
                if (GUILayout.Button("Clear Stage")) ClearStage();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
        }

        void BuildStage()
        {
            if (FindStageRoot() != null &&
                !EditorUtility.DisplayDialog(WindowTitle, "A stage already exists in this scene. Replace it?",
                                             "Replace", "Cancel"))
                return;
            ClearStage(); // no-op when there is none

            Undo.SetCurrentGroupName("Build Splash Studio Stage");
            int undoGroup = Undo.GetCurrentGroup();

            if (!WaterBuildKit.TryBuildSharedAssets(WaterBuildKit.Gen, buildPoolMaterial: false, out var ctx))
                return; // TryBuildSharedAssets already reported the missing shader/asset

            var root = new GameObject(StageRootName);
            Undo.RegisterCreatedObjectUndo(root, "Splash Studio Stage");

            // Primary only if the scene has none, so the studio never demotes the user's own water.
            bool sceneHasPrimary = System.Array.Exists(
                Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None), body => body.IsPrimary);
            WaterVolume water = WaterBuildKit.CreateWaterBody(ctx, root.transform, StudioWaterName,
                Vector3.zero, StageExtent, primary: !sceneHasPrimary, withPool: false, withGodRays: false,
                withFoamParticles: true, withSplash: true);

            Vector3 center = water.VolumeCenter;
            Vector3 waterline = new Vector3(center.x, water.transform.position.y, center.z);
            BuildRock(root.transform, waterline);
            BuildBoat(root.transform, waterline);

            if (_profile != null) ApplyProfileToBody(water, _profile);

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeObject = root;
            FrameSceneView(center);
            Debug.Log("[WebGL Water] Splash Studio stage built. Enter Play to watch the spray; scrub the " +
                      "profile in the Splash Studio window to tune it live.");
        }

        // A static rock straddling the waterline, sprayed by the boat's wake / passing waves (Rock/Both).
        void BuildRock(Transform parent, Vector3 waterline)
        {
            GameObject rock = WaterBuildKit.CreateBuoyantObjectBody(WaterBuildKit.FloaterShape.Cube, null, RockSize);
            Undo.SetTransformParent(rock.transform, parent, "Studio Rock");
            rock.name = StudioRockName;
            rock.transform.position = waterline;
            Undo.AddComponent<WaterSprayPump>(rock); // default single Both-mode probe: fires on rising water
        }

        // A boat gliding a circle past the rock: kinematic mover (no physics), a wake interactor so its
        // pass lifts the water at the rock, and a spray pump for the bow plow (Both mode covers both).
        void BuildBoat(Transform parent, Vector3 waterline)
        {
            GameObject boat = WaterBuildKit.CreateBuoyantObjectBody(WaterBuildKit.FloaterShape.Cube, null, BoatSize);
            Undo.SetTransformParent(boat.transform, parent, "Studio Boat");
            boat.name = StudioBoatName;
            boat.transform.localScale = BoatHullScale * BoatSize;
            boat.transform.position = waterline + new Vector3(BoatLapRadius, 0f, 0f); // start on the circle

            var mover = Undo.AddComponent<WaterShowcaseMover>(boat);
            mover.pathCenter = waterline;
            mover.pathRadius = BoatLapRadius;
            mover.lapSeconds = BoatLapSeconds;

            Undo.AddComponent<WaterInteractable>(boat); // wake ripples that reach the rock
            Undo.AddComponent<WaterSprayPump>(boat);    // bow/plow spray as it drives
        }

        void ClearStage()
        {
            GameObject root = FindStageRoot();
            if (root != null) Undo.DestroyObjectImmediate(root);
        }

        static GameObject FindStageRoot() => GameObject.Find(StageRootName);

        void FrameSceneView(Vector3 center)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;
            var bounds = new Bounds(center, StageExtent * 2f + Vector3.one * SceneFramePadding);
            view.Frame(bounds, instant: false);
        }

        // Point the stage body's foam particles AND its splash emitter at the profile, so both the
        // ambient foam and the splash burst read from this one asset. SerializedObject so Undo + dirty
        // are handled and the internal 'profile' field is reachable without widening the runtime API.
        static void ApplyProfileToBody(WaterVolume body, WaterFoamProfile profile)
        {
            SetProfileReference(body.GetComponent<WaterFoamParticles>(), profile);
            SetProfileReference(body.splashEmitter, profile); // set by CreateWaterBody(withSplash: true)
        }

        static void SetProfileReference(Object component, WaterFoamProfile profile)
        {
            if (component == null) return;
            var serialized = new SerializedObject(component);
            SerializedProperty reference = serialized.FindProperty(ProfileRefField);
            if (reference == null) return;
            reference.objectReferenceValue = profile;
            serialized.ApplyModifiedProperties();
        }

        // ---- embedded profile inspector ---------------------------------------------------------

        void DrawProfileEditor()
        {
            if (_profile == null)
            {
                EditorGUILayout.HelpBox("Assign or create a Water Foam Profile to tune it here.", MessageType.Info);
                return;
            }

            if (_profileEditor == null || _profileEditor.target != _profile)
            {
                if (_profileEditor != null) DestroyImmediate(_profileEditor);
                _profileEditor = UnityEditor.Editor.CreateEditor(_profile);
            }
            _profileEditor.OnInspectorGUI();
        }
    }
}
