// WebGpuWater - Water Wizard partial: "Water System".
//
// Plans a MULTI-BODY water (bodies, the rivers joining them, dry rooms, one quality asset) as
// data the window remembers, validates it against the facade's rules before the button is
// enabled, and builds it in ONE undo step through WaterBuildKit.BuildWaterSystem - the same
// executor the Connected Waters test rig runs, so "Load Connected Waters demo plan" + Build
// yields that rig's waters. Lists are drawn through the window's own SerializedObject, which is
// how the river spline inspector draws its knots (a reorderable list per Unity's default drawer)
// and keeps every edit undoable without a hand-rolled row editor.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterWizardWindow
    {
        const string WaterSystemSectionTitle = "Water System";
        const string WaterSystemUndoName = "Build Water System";
        const string WaterSystemStatusTitle = "Status (scene)";
        const string LoadDemoPlanLabel = "Load Connected Waters demo plan";
        const string BuildSystemLabel = "Build Water System";
        const string RendererRepairProblem =
            "The default URP renderer is missing WebGpuWater features - run Utilities > Renderer Setup " +
            "> Install / Repair first.";
        const string BodiesHeading = "Bodies";
        const string RiversHeading = "Rivers";
        const string ExclusionsHeading = "Exclusions (dry rooms)";
        const string ComponentLabelPrefix = "network ";
        const string PrimaryStatusSuffix = " (primary)";
        const string FogOnStatus = "fog on";
        const string FogOffStatus = "fog off";
        const string StatusSeparator = "  |  ";
        const float BuildButtonHeight = 30f;

        [SerializeField] bool _systemExpanded;
        [SerializeField] bool _systemStatusExpanded;
        // The plan is window state like every other field: EditorWindow serializes it across
        // domain reloads, so a half-authored system survives a script recompile.
        [SerializeField] WaterSystemPlan _systemPlan = new WaterSystemPlan();

        // Rebuilt every repaint from the plan; never serialized (a stale readout is worse than none).
        [System.NonSerialized] readonly List<string> _systemProblems = new List<string>();
        [System.NonSerialized] readonly List<WaterVolume> _statusBodies = new List<WaterVolume>();
        [System.NonSerialized] readonly List<WaterConnection> _statusConnections = new List<WaterConnection>();

        void DrawWaterSystemSection()
        {
            EditorGUILayout.HelpBox("Plan several bodies, the rivers that join them and any dry rooms, then " +
                                    "build them together: one scene root, one undo step, one asset folder " +
                                    "(Assets/WebGpuWater/Waters/<system name>). Rivers reference bodies by " +
                                    "their index in the Bodies list.", MessageType.None);

            DrawWaterSystemPlanFields();

            bool planValid = ValidateSystemPlanForUi();
            DrawWaterSystemProblems();

            EditorGUILayout.Space(4f);
            if (GUILayout.Button(new GUIContent(LoadDemoPlanLabel,
                "Replace the plan with the Connected Waters test rig's waters (5 bodies, 4 rivers, " +
                "7 connections, 1 carve). Building it here yields the same waters as the GameObject menu " +
                "rig; only that rig's coastal Terrain, floor and crates are not part of a plan.")))
                _systemPlan = ConnectedWatersDemoPlan();

            using (new EditorGUI.DisabledScope(!planValid))
            {
                if (GUILayout.Button(BuildSystemLabel, GUILayout.Height(BuildButtonHeight)))
                    BuildWaterSystemFromPlan();
            }

            _systemStatusExpanded = WaterEditorUI.SubSection(WaterSystemStatusTitle, _systemStatusExpanded,
                                                             DrawWaterSystemStatus);
        }

        // Every plan field through the window's SerializedObject: strings, enums, vectors and the
        // three lists get Unity's default drawers (tooltips from the plan's [Tooltip]s), and each
        // edit lands in the undo stack.
        void DrawWaterSystemPlanFields()
        {
            var serialized = new SerializedObject(this);
            serialized.Update();
            SerializedProperty plan = serialized.FindProperty(nameof(_systemPlan));

            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.systemName)));
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.primaryBodyIndex)));
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.fogOnAllBodies)));
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.splash)));

            SerializedProperty qualitySource = plan.FindPropertyRelative(nameof(WaterSystemPlan.qualitySource));
            EditorGUILayout.PropertyField(qualitySource);
            if (qualitySource.enumValueIndex == (int)WaterQualitySource.Asset)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.quality)));
                EditorGUI.indentLevel--;
            }

            WaterEditorUI.SubHeading(BodiesHeading);
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.bodies)), true);
            WaterEditorUI.SubHeading(RiversHeading);
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.rivers)), true);
            WaterEditorUI.SubHeading(ExclusionsHeading);
            EditorGUILayout.PropertyField(plan.FindPropertyRelative(nameof(WaterSystemPlan.exclusions)), true);

            serialized.ApplyModifiedProperties();
        }

        // The plan's own rules plus the one scene precondition a build must not paper over: a
        // renderer without the water features renders nothing useful, and installing them edits
        // a project asset behind a confirm dialog that belongs to Utilities, not to Build.
        bool ValidateSystemPlanForUi()
        {
            ValidatePlan(_systemPlan, _systemProblems);
            WaterRendererFeatureCheck.Report report = WaterRendererFeatureCheck.Inspect();
            if (report.RendererAsset != null && report.NeedsRepair)
                _systemProblems.Add(RendererRepairProblem);
            return _systemProblems.Count == 0;
        }

        void DrawWaterSystemProblems()
        {
            if (_systemProblems.Count == 0) return;
            EditorGUILayout.HelpBox(string.Join("\n", _systemProblems), MessageType.Warning);
        }

        // One undo step for the entire system (root, context rig, bodies, rivers + seams, carves,
        // quality) - the Create Water shape, with the same root-exists guard and the same
        // aborted-build cleanup (nothing persists, the fresh folder is deleted).
        void BuildWaterSystemFromPlan()
        {
            if (!ValidateSystemPlanForUi())
            {
                Debug.LogError(LogPrefix + "Water system not built: " + string.Join(" ", _systemProblems));
                return;
            }

            string rootName = _systemPlan.systemName;
            var existingRoot = GameObject.Find(rootName);
            if (existingRoot != null &&
                !EditorUtility.DisplayDialog(ProductName,
                    $"The scene already has a '{rootName}' root. Build another water system anyway?",
                    "Build Another", "Cancel"))
            {
                Selection.activeObject = existingRoot;
                return;
            }

            Undo.SetCurrentGroupName(WaterSystemUndoName);
            int undoGroup = Undo.GetCurrentGroup();

            string systemFolder = _systemPlan.SystemFolder;
            bool folderExisted = AssetDatabase.IsValidFolder(systemFolder);
            EnsureFolder(systemFolder);
            var root = NewUndoableGameObject(rootName);
            if (!CreateContext(root.transform, out BuildContext ctx, systemFolder,
                               buildPoolMaterial: PlanHasAnalyticPool(_systemPlan)))
            {
                Undo.RevertAllDownToGroup(undoGroup); // nothing persists from an aborted build
                if (!folderExisted) AssetDatabase.DeleteAsset(systemFolder);
                return;
            }

            WaterSystemBuildResult result;
            try
            {
                result = BuildWaterSystem(_systemPlan, ctx, root.transform);
            }
            catch (System.Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                if (!folderExisted) AssetDatabase.DeleteAsset(systemFolder);
                Debug.LogException(exception);
                return;
            }

            Selection.activeObject = result.Primary.gameObject;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log(LogPrefix + $"Water system '{rootName}' built: {result.Bodies.Count} bodies, " +
                      $"{result.Rivers.Count} rivers, {result.Exclusions.Count} exclusions; assets under " +
                      $"'{systemFolder}'. Press Play.");
            if (rootName == ConnectedDemoRootName)
                Debug.Log(LogPrefix + ConnectedDemoBuiltMessage + ConnectedDemoChecklist);
        }

        // The pool material only exists for a system that has an analytic-pool body; the demo rig
        // (no pool bodies) built one anyway through CreateContext's default, and still does.
        static bool PlanHasAnalyticPool(WaterSystemPlan plan)
        {
            for (int i = 0; i < plan.bodies.Count; i++)
                if (plan.bodies[i].kind == WaterKind.LegacyAnalyticPool) return true;
            return false;
        }

        // Read-only view of the OPEN SCENE's waters (not the plan): which bodies exist, how the
        // wired connections join them into networks, and each body's quality - the Network
        // Manager's component computation over an edit-mode scan (the live topology in play).
        void DrawWaterSystemStatus()
        {
            _statusBodies.Clear();
            _statusBodies.AddRange(Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None));
            if (_statusBodies.Count == 0)
            {
                EditorGUILayout.HelpBox("No WaterVolume in the open scene.", MessageType.None);
                return;
            }

            if (Application.isPlaying) WaterNetworkComponents.CollectTopologyConnections(_statusConnections);
            else WaterNetworkComponents.CollectSceneConnections(_statusConnections);
            int[] labels = WaterNetworkComponents.BuildComponentLabels(_statusBodies, _statusConnections);

            EditorGUILayout.LabelField("Bodies", _statusBodies.Count.ToString());
            EditorGUILayout.LabelField("Networks", CountComponents(labels).ToString());
            EditorGUILayout.LabelField("Connections",
                $"{_statusConnections.Count} ({CountUnwired(_statusConnections)} unwired)");
            for (int i = 0; i < _statusBodies.Count; i++)
                EditorGUILayout.LabelField(_statusBodies[i].name, BodyStatusLine(_statusBodies[i], labels[i]));
        }

        static string BodyStatusLine(WaterVolume body, int componentLabel)
        {
            return ComponentLabelPrefix + componentLabel + StatusSeparator +
                   body.AssignedQualityName + StatusSeparator +
                   (body.WaterFog ? FogOnStatus : FogOffStatus) +
                   (body.IsPrimary ? PrimaryStatusSuffix : string.Empty);
        }

        // Labels are dense from FirstComponentLabel, so the highest label IS the network count.
        static int CountComponents(int[] labels)
        {
            int highest = WaterNetworkComponents.UnassignedComponent;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] > highest) highest = labels[i];
            return highest;
        }

        static int CountUnwired(IReadOnlyList<WaterConnection> connections)
        {
            int count = 0;
            for (int i = 0; i < connections.Count; i++)
            {
                if (connections[i] == null) continue;
                if (WaterRuntimeValidation.ValidateConnection(connections[i]) != WaterRuntimeValidationFlags.None)
                    count++;
            }
            return count;
        }
    }
}
