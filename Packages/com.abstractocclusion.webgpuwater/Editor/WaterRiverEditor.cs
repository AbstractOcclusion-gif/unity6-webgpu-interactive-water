// WebGpuWater - WaterRiver consolidated inspector (orchestration).
// ONE inspector for the whole river: the facade's own fields plus every sibling component's
// (spline, current field, surface, fluid, foam, disturbance), drawn through nested
// SerializedObjects so no field moves and no scene migrates - Undo and dirty land on the
// component that owns the field. The per-tab drawing lives in one partial PER TAB; this file
// holds the header, the tab bar, foldout persistence, the nested-object cache and the
// connection generation/removal entry points the build kit also calls.
//
// All object creation/destruction lives HERE (with Undo), never in the facade's OnValidate -
// Unity forbids lifecycle operations there. The facade owns the math and the references; this
// editor owns the user gesture.
//
// TABS ARE NAMED FOR WHAT THEY DO (mirrors WaterVolumeEditor). The charters, one line each:
//   Path        - the authored course: knots, their widths and speeds, the ribbon rebuild.
//   Flow        - where the current comes from and how strong it is at each end.
//   Surface     - the ribbon itself: sampling, depth, materials, the body-border aprons, the
//                 mouth plume.
//   Fluid       - the settled obstacle bake: grid, solve, the asset and its staleness.
//   Foam        - contact, cascade and baked-turbulence foam composition.
//   Disturbance - analytic wakes/impacts from interactors riding the river.
//   Connections - the two ends: which body/river they meet, the generated seam objects, and
//                 every authoring warning about them.
//   Wiring      - the parent link, facade-managed references (read-only) and the component set.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiver))]
    internal sealed partial class WaterRiverEditor : UnityEditor.Editor
    {
        // Category tabs, ordered as a user's journey: draw the course, make it flow, dress the
        // surface, refine with bakes/foam/wakes, connect it to its waters, then check the wiring.
        internal enum InspectorTab
        {
            Path, Flow, Surface, Fluid, Foam, Disturbance, Connections, Wiring
        }

        // Short labels: GUILayout.Toolbar splits its width evenly and clips the longest first,
        // and eight tabs leave little room in a narrow inspector.
        static readonly string[] TabLabels =
            { "Path", "Flow", "Surface", "Fluid", "Foam", "Wakes", "Connect", "Wiring" };

        const string InspectorTitle = "WATER RIVER";
        const string SubtitleStandalone = "Standalone ribbon  —  no parent volume";
        const string SubtitleParentedFormat = "Parented to {0}{1}";
        const string SubtitleConnectedSuffix = "  —  {0} connected end(s)";

        const string FoldoutKeyPrefix = "WebGpuWater.WaterRiverEditor.";
        const string TabSessionKey = FoldoutKeyPrefix + "_tab";

        // ---- facade property paths ------------------------------------------------------------
        const string ParentVolumePath = "parentVolume";
        const string SourceEndPath = "sourceEnd";
        const string MouthEndPath = "mouthEnd";
        const string EndBodyPath = "body";
        const string EndUpstreamRiverPath = "upstreamRiver";
        const string EndTransitionRadiusPath = "transitionRadiusMeters";
        const string ShowGeneratedObjectsPath = "showGeneratedObjects";
        const string MouthOutflowLengthPath = "mouthOutflowLengthMeters";
        const string MouthOutflowSpreadPath = "mouthOutflowSpreadPerMeter";
        const string MouthOutflowCurrentStrengthPath = "mouthOutflowCurrentStrength";
        const string MouthOutflowFoamLengthPath = "mouthOutflowFoamLengthMeters";
        const string MouthOutflowFoamStrengthPath = "mouthOutflowFoamStrength";

        // ---- sibling property paths (drawn through nested SerializedObjects) ------------------
        const string SplineKnotsPath = "knots";
        const string SplineReferencePath = "spline";      // on the surface AND the current field
        const string SurfaceWaterVolumePath = "waterVolume";
        const string SurfaceSamplesPerSegmentPath = "samplesPerSegment";
        const string SurfaceGameplayDepthPath = "gameplayDepthMeters";
        const string SurfaceUnderMaterialPath = "underSurfaceMaterial";
        const string SurfaceReflectionProbePath = "reflectionProbe";
        const string CurrentFieldFluidPath = "fluid";

        // Foldout state; persisted through SessionState (per-instance fields reset on every
        // selection change). Only the first-reach blocks start open.
        bool _showCourse = true;
        bool _showKnots = false;
        bool _showCurrent = true;
        bool _showRibbon = true;
        bool _showSeams = false;
        bool _showMouthOutflow = false;
        bool _showBakeAsset = true;
        bool _showBakeGrid = false;
        bool _showObstacles = false;
        bool _showSolve = false;
        bool _showBakedFoam = false;
        bool _showFoamSources = true;
        bool _showFoamAppearance = false;
        bool _showDisturbance = true;
        bool _showSourceEnd = true;
        bool _showMouthEnd = true;
        bool _showGenerated = false;
        bool _showParent = true;
        bool _showManagedReferences = false;
        bool _showComponents = true;

        InspectorTab _tab = InspectorTab.Path;

        // Nested SerializedObjects, one per sibling. Re-created when the sibling appears,
        // disappears or is replaced (Undo); bracketed by Update/ApplyModifiedProperties per draw.
        SerializedObject _splineObject;
        SerializedObject _currentFieldObject;
        SerializedObject _surfaceObject;
        SerializedObject _fluidObject;
        SerializedObject _foamObject;
        SerializedObject _disturbanceObject;

        // A sub-component stub inspector (same GameObject, drawn below this one) asks for a tab
        // by setting these; the next repaint adopts it. Statics because the stub has no handle on
        // this editor instance.
        static WaterRiver s_pendingFocusRiver;
        static InspectorTab s_pendingFocusTab;

        WaterRiver River => (WaterRiver)target;

        void OnEnable()
        {
            SyncFoldouts(load: true);
            _tab = (InspectorTab)SessionState.GetInt(TabSessionKey, (int)_tab);
        }

        void OnDisable()
        {
            SyncFoldouts(load: false);
            SessionState.SetInt(TabSessionKey, (int)_tab);
            _splineObject = null;
            _currentFieldObject = null;
            _surfaceObject = null;
            _fluidObject = null;
            _foamObject = null;
            _disturbanceObject = null;
        }

        // ONE list drives both directions, so a new foldout can never be persisted in only one
        // of load/save. The field initializers above remain the first-session defaults.
        void SyncFoldouts(bool load)
        {
            Sync(ref _showCourse, nameof(_showCourse), load);
            Sync(ref _showKnots, nameof(_showKnots), load);
            Sync(ref _showCurrent, nameof(_showCurrent), load);
            Sync(ref _showRibbon, nameof(_showRibbon), load);
            Sync(ref _showSeams, nameof(_showSeams), load);
            Sync(ref _showMouthOutflow, nameof(_showMouthOutflow), load);
            Sync(ref _showBakeAsset, nameof(_showBakeAsset), load);
            Sync(ref _showBakeGrid, nameof(_showBakeGrid), load);
            Sync(ref _showObstacles, nameof(_showObstacles), load);
            Sync(ref _showSolve, nameof(_showSolve), load);
            Sync(ref _showBakedFoam, nameof(_showBakedFoam), load);
            Sync(ref _showFoamSources, nameof(_showFoamSources), load);
            Sync(ref _showFoamAppearance, nameof(_showFoamAppearance), load);
            Sync(ref _showDisturbance, nameof(_showDisturbance), load);
            Sync(ref _showSourceEnd, nameof(_showSourceEnd), load);
            Sync(ref _showMouthEnd, nameof(_showMouthEnd), load);
            Sync(ref _showGenerated, nameof(_showGenerated), load);
            Sync(ref _showParent, nameof(_showParent), load);
            Sync(ref _showManagedReferences, nameof(_showManagedReferences), load);
            Sync(ref _showComponents, nameof(_showComponents), load);
        }

        static void Sync(ref bool value, string key, bool load)
        {
            if (load) value = SessionState.GetBool(FoldoutKeyPrefix + key, value);
            else SessionState.SetBool(FoldoutKeyPrefix + key, value);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            AdoptPendingFocus();

            WaterEditorUI.DrawHeader(InspectorTitle, Subtitle());
            _tab = (InspectorTab)WaterEditorUI.TabBar((int)_tab, TabLabels);
            switch (_tab)
            {
                case InspectorTab.Path: DrawPathTab(); break;
                case InspectorTab.Flow: DrawFlowTab(); break;
                case InspectorTab.Surface: DrawSurfaceTab(); break;
                case InspectorTab.Fluid: DrawFluidTab(); break;
                case InspectorTab.Foam: DrawFoamTab(); break;
                case InspectorTab.Disturbance: DrawDisturbanceTab(); break;
                case InspectorTab.Connections: DrawConnectionsTab(); break;
                case InspectorTab.Wiring: DrawWiringTab(); break;
            }
            WaterEditorUI.DrawFooter();

            serializedObject.ApplyModifiedProperties();
        }

        string Subtitle()
        {
            WaterRiver river = River;
            if (river.ParentVolume == null) return SubtitleStandalone;
            int connected = (river.SourceEnd.IsGenerated ? 1 : 0) +
                            (river.MouthEnd.IsGenerated ? 1 : 0);
            string suffix = connected > 0
                ? string.Format(SubtitleConnectedSuffix, connected) : string.Empty;
            return string.Format(SubtitleParentedFormat, river.ParentVolume.name, suffix);
        }

        // ---- sub-component stubs -------------------------------------------------------------

        /// <summary>The whole inspector body of a river sub-component (spline, current field,
        /// fluid, foam): a pointer to the tab on this inspector that edits it. Sub-components
        /// without a facade get an Add button instead, so a hand-built rig can opt in.</summary>
        internal static void DrawSubComponentStub(Component component, InspectorTab tab,
                                                  string componentLabel)
        {
            var river = component.GetComponent<WaterRiver>();
            if (river == null)
            {
                EditorGUILayout.HelpBox(string.Format(StubNoFacadeFormat, componentLabel),
                                        MessageType.Info);
                if (GUILayout.Button(StubAddFacadeLabel))
                    Undo.AddComponent<WaterRiver>(component.gameObject);
                return;
            }

            EditorGUILayout.HelpBox(string.Format(StubFormat, componentLabel, TabLabels[(int)tab]),
                                    MessageType.None);
            if (!GUILayout.Button(StubEditLabel)) return;
            s_pendingFocusRiver = river;
            s_pendingFocusTab = tab;
            EditorGUIUtility.PingObject(river);
        }

        void AdoptPendingFocus()
        {
            if (s_pendingFocusRiver != target) return;
            _tab = s_pendingFocusTab;
            s_pendingFocusRiver = null;
        }

        const string StubFormat =
            "{0} is tuned on the Water River component above ({1} tab).";
        const string StubNoFacadeFormat =
            "{0} is tuned through a Water River component, which this object does not have. " +
            "Add one for the consolidated river inspector.";
        const string StubEditLabel = "Edit on Water River";
        const string StubAddFacadeLabel = "Add Water River";

        // ---- property helpers ----------------------------------------------------------------

        // A missing path throws with the path in the message instead of a NullReference on
        // unfold (the volume inspector's "sweCompute lingered" lesson).
        static SerializedProperty Prop(SerializedObject owner, string path)
        {
            SerializedProperty property = owner.FindProperty(path);
            if (property == null)
                throw new System.InvalidOperationException(
                    $"WaterRiverEditor: '{owner.targetObject.GetType().Name}' has no serialized " +
                    $"field '{path}'.");
            return property;
        }

        SerializedProperty Prop(string path) => Prop(serializedObject, path);

        // Draws every named property of an owner, honouring its [Range]/[Min]/[Tooltip] so this
        // editor holds no range literals.
        static void DrawFields(SerializedObject owner, params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
                EditorGUILayout.PropertyField(Prop(owner, paths[i]), true);
        }

        void DrawFields(params string[] paths) => DrawFields(serializedObject, paths);

        // Greyed rather than hidden, so a reader can see settings that don't apply right now.
        static void DrawFieldsIf(bool enabled, SerializedObject owner, params string[] paths)
        {
            EditorGUI.BeginDisabledGroup(!enabled);
            DrawFields(owner, paths);
            EditorGUI.EndDisabledGroup();
        }

        void DrawFieldsIf(bool enabled, params string[] paths)
            => DrawFieldsIf(enabled, serializedObject, paths);

        // The nested object for one sibling type, or null when the sibling is absent. Cached per
        // component instance so Undo-driven add/remove swaps it correctly.
        SerializedObject Nested<T>(ref SerializedObject cached) where T : Component
        {
            T component = River.GetComponent<T>();
            if (component == null)
            {
                cached = null;
                return null;
            }
            if (cached == null || cached.targetObject != component)
                cached = new SerializedObject(component);
            return cached;
        }

        SerializedObject SplineObject => Nested<WaterRiverSpline>(ref _splineObject);
        SerializedObject CurrentFieldObject => Nested<WaterRiverCurrentField>(ref _currentFieldObject);
        SerializedObject SurfaceObject => Nested<WaterRiverSurface>(ref _surfaceObject);
        SerializedObject FluidObject => Nested<WaterRiverFluid>(ref _fluidObject);
        SerializedObject FoamObject => Nested<WaterRiverFoam>(ref _foamObject);
        SerializedObject DisturbanceObject => Nested<WaterRiverDisturbance>(ref _disturbanceObject);

        // Optional siblings are added with Undo and never silently; the facade re-resolves its
        // wiring straight away so the current field sees a freshly added fluid component.
        void AddOptionalComponent<T>() where T : Component
        {
            Undo.AddComponent<T>(River.gameObject);
            River.ApplyWiring();
        }

        // ---- connection generation / removal (shared with the build kit) ---------------------

        const string GenerateUndoLabel = "Generate River Connection";
        const string RemoveUndoLabel = "Remove River Connection";

        internal static void GenerateEnd(WaterRiver river, WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = river.EndFor(endKind);
            WaterConnectionPort priorRiverPort = end.riverPort;
            WaterConnectionPort priorTargetPort = end.targetPort;
            WaterConnection priorConnection = end.connection;

            Undo.RecordObject(river, GenerateUndoLabel);
            RecordIfAlive(priorRiverPort, GenerateUndoLabel);
            RecordIfAlive(priorTargetPort, GenerateUndoLabel);
            RecordIfAlive(priorConnection, GenerateUndoLabel);

            try
            {
                river.RegenerateConnection(endKind);
            }
            catch (System.InvalidOperationException exception)
            {
                Debug.LogError($"WaterRiver: {exception.Message}", river);
                return;
            }

            // Only what regeneration actually CREATED gets a created-undo; reused objects were
            // recorded above so their rewiring undoes in the same step.
            RegisterIfCreated(end.riverPort, priorRiverPort);
            RegisterIfCreated(end.targetPort, priorTargetPort);
            RegisterIfCreated(end.connection, priorConnection);
            EditorUtility.SetDirty(river);
        }

        internal static void RemoveEnd(WaterRiver river, WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = river.EndFor(endKind);
            Undo.RecordObject(river, RemoveUndoLabel);
            DestroyIfAlive(end.connection);
            DestroyIfAlive(end.targetPort);
            DestroyIfAlive(end.riverPort);
            river.ClearConnectionRefs(endKind);
            EditorUtility.SetDirty(river);
        }

        static void RecordIfAlive(Component component, string undoLabel)
        {
            if (component == null) return;
            Undo.RecordObject(component, undoLabel);
            Undo.RecordObject(component.transform, undoLabel);
            // hideFlags live on the GameObject (generated-object visibility).
            Undo.RecordObject(component.gameObject, undoLabel);
        }

        static void RegisterIfCreated(Component current, Component prior)
        {
            if (current == null || current == prior) return;
            Undo.RegisterCreatedObjectUndo(current.gameObject, GenerateUndoLabel);
        }

        static void DestroyIfAlive(Component component)
        {
            if (component == null) return;
            Undo.DestroyObjectImmediate(component.gameObject);
        }
    }
}
#endif
