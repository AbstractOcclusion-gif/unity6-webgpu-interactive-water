// WebGpuWater - read-only multi-body runtime observability window, plus the connected-
// components helper (WaterNetworkComponents) it shares with the wizard's Water System status.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    // Which bodies are joined into one water network: union-find over the wired connections,
    // labelled 1..N in first-seen order. Bodies are addressed by index into the caller's list so
    // the window's snapshot rows and the wizard's scene scan both label without copying.
    internal static class WaterNetworkComponents
    {
        internal const int UnassignedComponent = 0;
        internal const int FirstComponentLabel = 1;
        const int MissingBodyIndex = -1;

        // The live topology (play mode: every enabled WaterConnection registers itself).
        internal static void CollectTopologyConnections(List<WaterConnection> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            for (int i = 0; i < WaterTopology.ConnectionCount; i++)
                into.Add(WaterTopology.GetConnection(i));
        }

        // Edit mode: WaterConnection is not ExecuteAlways, so the topology is empty and the
        // authored connections are read off the open scene instead.
        internal static void CollectSceneConnections(List<WaterConnection> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            into.AddRange(UnityEngine.Object.FindObjectsByType<WaterConnection>(FindObjectsSortMode.None));
        }

        internal static int[] BuildComponentLabels(IReadOnlyList<WaterVolume> bodies,
                                                   IReadOnlyList<WaterConnection> connections)
            => BuildComponentLabels(BuildComponentRoots(bodies, connections));

        internal static int[] BuildComponentRoots(IReadOnlyList<WaterVolume> bodies,
                                                  IReadOnlyList<WaterConnection> connections)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            if (connections == null) throw new ArgumentNullException(nameof(connections));
            int[] roots = new int[bodies.Count];
            for (int i = 0; i < roots.Length; i++) roots[i] = i;
            for (int connectionIndex = 0; connectionIndex < connections.Count; connectionIndex++)
            {
                WaterConnection connection = connections[connectionIndex];
                if (connection == null || !connection.IsWired) continue;
                int indexA = FindBody(bodies, BodyOf(connection.PortA));
                int indexB = FindBody(bodies, BodyOf(connection.PortB));
                if (indexA >= 0 && indexB >= 0) Union(roots, indexA, indexB);
            }
            for (int i = 0; i < roots.Length; i++) roots[i] = FindRoot(roots, i);
            return roots;
        }

        internal static int[] BuildComponentLabels(int[] roots)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            int[] labels = new int[roots.Length];
            int nextLabel = FirstComponentLabel;
            for (int i = 0; i < roots.Length; i++)
            {
                int existingLabel = UnassignedComponent;
                for (int previous = 0; previous < i; previous++)
                {
                    if (roots[previous] != roots[i]) continue;
                    existingLabel = labels[previous];
                    break;
                }
                labels[i] = existingLabel != UnassignedComponent ? existingLabel : nextLabel++;
            }
            return labels;
        }

        // The body behind a port: its live provider while the water is enabled (a river port
        // answers with its parent body), else the serialized body reference - so an edit-mode
        // scan still joins body ports whose water is not live.
        internal static WaterVolume BodyOf(WaterConnectionPort port)
        {
            if (port == null) return null;
            IWaterSurfaceProvider provider = port.ResolveProvider();
            if (provider != null && provider.Body != null) return provider.Body;
            return port.body;
        }

        static int FindBody(IReadOnlyList<WaterVolume> bodies, WaterVolume body)
        {
            if (body == null) return MissingBodyIndex;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] == body) return i;
            return MissingBodyIndex;
        }

        static int FindRoot(int[] roots, int index)
        {
            while (roots[index] != index)
            {
                roots[index] = roots[roots[index]];
                index = roots[index];
            }
            return index;
        }

        static void Union(int[] roots, int left, int right)
        {
            int leftRoot = FindRoot(roots, left);
            int rightRoot = FindRoot(roots, right);
            if (leftRoot != rightRoot) roots[rightRoot] = leftRoot;
        }
    }

    internal sealed class WaterNetworkManagerWindow : EditorWindow
    {
        const string WindowTitle = "Water Network Manager";
        // Same leaf name, now under the shared MenuRoot (it hung off a "Window/Abstract Occlusion/"
        // root of its own - one of three roots this assembly used to spell).
        const string MenuPath = WaterBuildKit.MenuRoot + WindowTitle;
        const string NoCameraLabel = "No camera";
        const string NoBodiesMessage = "No registered WaterVolumes are currently available.";
        const string SelectLabel = "Select";
        const string FrameLabel = "Frame";
        const string GrantedLabel = "Granted";
        const string WantedLabel = "Wanted";
        const string CulledLabel = "Culled";
        const string DrawnLabel = "Drawn";
        const string IneligibleLabel = "Ineligible";
        const string SelectedLabel = "Selected";
        const string NotSelectedLabel = "-";
        const string PrimaryLabel = "Primary";
        const string SecondaryLabel = "Secondary";
        const string CameraLabel = "Camera";
        const string EffectiveBudgetsLabel = "Effective budgets";
        const string BodiesLabel = "Bodies";
        const string RenderGrantsLabel = "Render grants";
        const string UnwiredConnectionsLabel = "Unwired connections";
        const string ActionsLabel = "Actions";
        const string BodyLabel = "Body";
        const string TypeLabel = "Type";
        const string RoleLabel = "Role";
        const string NetworkLabel = "Network";
        const string AssignedQualityLabel = "Assigned quality";
        const string ResolvedTierLabel = "Resolved tier";
        const string RankLabel = "Rank";
        const string VisibilityHeaderLabel = "Visibility";
        const string DistanceLabel = "Distance";
        const string CoverageLabel = "Coverage";
        const string SimulationLabel = "Simulation";
        const string PlanarLabel = "Planar";
        const string CausticLabel = "Caustic";
        const string FoamLabelText = "Foam";
        const string FogLabel = "Fog";
        const string ActivationLabel = "Activation";
        const string GpuMemoryLabel = "GPU memory";
        const string ValidationWarningsLabel = "Validation warnings";
        const string VisibleLabel = "Visible";
        const string VisiblePinnedLabel = "Visible + pin";
        const string ForcedOnLabel = "Forced on";
        const string AscendingSuffix = " ▲";
        const string DescendingSuffix = " ▼";
        const string DistanceFormat = "0.0";
        const string PercentageFormat = "0.0";
        const string MemoryFormat = "0.0";
        const string ConnectedComponentPrefix = "Component ";
        const float MinimumWindowWidth = 900f;
        const float MinimumWindowHeight = 300f;
        const float ToolbarHeight = 22f;
        const float RowHeight = 21f;
        const float ActionsWidth = 112f;
        const float ActionButtonWidth = 54f;
        const float BodyWidth = 180f;
        const float TypeWidth = 70f;
        const float RoleWidth = 75f;
        const float NetworkWidth = 90f;
        const float AssignedQualityWidth = 130f;
        const float ResolvedQualityWidth = 95f;
        const float RankWidth = 50f;
        const float VisibilityWidth = 95f;
        const float DistanceWidth = 75f;
        const float CoverageWidth = 75f;
        const float SimulationWidth = 90f;
        const float PlanarWidth = 80f;
        const float CausticWidth = 80f;
        const float FoamWidth = 80f;
        const float FogWidth = 70f;
        const float ActivationWidth = 80f;
        const float MemoryWidth = 95f;
        const float ValidationWidth = 360f;
        // The scroll content width IS the sum of the columns, so a new column cannot leave the
        // rows narrower (or the scroll wider) than the headers; the hand-summed literal that sat
        // here had already drifted from the columns it claimed to total.
        const float TableWidth = ActionsWidth + BodyWidth + TypeWidth + RoleWidth + NetworkWidth +
                                 AssignedQualityWidth + ResolvedQualityWidth + RankWidth + VisibilityWidth +
                                 DistanceWidth + CoverageWidth + SimulationWidth + PlanarWidth + CausticWidth +
                                 FoamWidth + FogWidth + ActivationWidth + MemoryWidth + ValidationWidth;
        const float PercentageMultiplier = 100f;
        const float MegabyteBytes = 1024f * 1024f;

        readonly List<Row> _rows = new List<Row>();
        readonly List<WaterVolume> _bodies = new List<WaterVolume>();
        readonly List<WaterConnection> _connections = new List<WaterConnection>();
        readonly RowComparer _rowComparer = new RowComparer();
        Vector2 _tableScroll;
        SortColumn _sortColumn = SortColumn.RelevanceRank;
        bool _sortAscending = true;

        [MenuItem(MenuPath)]
        static void Open() => GetWindow<WaterNetworkManagerWindow>(WindowTitle);

        void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle);
            minSize = new Vector2(MinimumWindowWidth, MinimumWindowHeight);
        }

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            Camera camera = ResolveCamera();
            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot =
                WaterRuntimeRelevance.GetSnapshot(camera);
            WaterRuntimeCounters counters = WaterRuntimeRelevance.GetCounters(camera);
            WaterRuntimeBudgets budgets = WaterRuntimeRelevance.GetBudgets(camera);
            BuildRows(snapshot);

            DrawSummary(camera, counters, budgets, CountUnwiredConnections());
            if (_rows.Count == 0)
            {
                EditorGUILayout.HelpBox(NoBodiesMessage, MessageType.Info);
                return;
            }
            DrawTable();
        }

        static Camera ResolveCamera()
        {
            if (Application.isPlaying) return WaterSimScheduler.ScheduleCamera();
            SceneView sceneView = SceneView.lastActiveSceneView;
            return sceneView != null && sceneView.camera != null
                ? sceneView.camera : WaterSimScheduler.ScheduleCamera();
        }

        static void DrawSummary(Camera camera, WaterRuntimeCounters counters,
                                WaterRuntimeBudgets budgets, int unwiredConnectionCount)
        {
            EditorGUILayout.LabelField(WindowTitle, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(CameraLabel, camera != null ? camera.name : NoCameraLabel);
            EditorGUILayout.LabelField(
                EffectiveBudgetsLabel,
                $"Simulation {budgets.SimulationBodies}  |  Planar {budgets.PlanarReflectionBodies}  |  Caustic {budgets.CausticProjectionBodies}");
            EditorGUILayout.LabelField(
                BodiesLabel,
                $"Registered {counters.Registered}  |  Visible {counters.Visible}  |  Simulation {counters.SimulationGranted}/{counters.SimulationEligible}");
            EditorGUILayout.LabelField(
                RenderGrantsLabel,
                $"Planar {counters.PlanarGranted}/{counters.PlanarWanted}  |  Caustic {counters.CausticGranted}/{counters.CausticWanted}  |  Foam drawn {counters.FoamDrawn}, culled {counters.FoamCulled}  |  Fog {counters.FogSources}");
            EditorGUILayout.LabelField(UnwiredConnectionsLabel, unwiredConnectionCount.ToString());
            EditorGUILayout.Space();
        }

        void BuildRows(IReadOnlyList<WaterRuntimeBodySnapshot> snapshot)
        {
            _rows.Clear();
            int primaryCount = CountPrimaries(snapshot);
            CollectBodies(snapshot, _bodies);
            WaterNetworkComponents.CollectTopologyConnections(_connections);
            int[] componentLabels = WaterNetworkComponents.BuildComponentLabels(_bodies, _connections);
            for (int i = 0; i < snapshot.Count; i++)
            {
                WaterRuntimeBodySnapshot state = snapshot[i];
                WaterVolume body = state.Body;
                if (body == null) continue;
                _rows.Add(new Row
                {
                    Body = body,
                    State = state,
                    Component = componentLabels[i],
                    Validation = WaterRuntimeValidation.ValidateBody(body, primaryCount)
                               | ConnectionWarningsForBody(body, _connections),
                    ApproximateGpuBytes = body.ApproximateOwnedGpuBytes,
                });
            }
            _rowComparer.Column = _sortColumn;
            _rowComparer.Ascending = _sortAscending;
            _rows.Sort(_rowComparer);
        }

        // Snapshot rows and body indices stay aligned (a null body keeps its slot) so the
        // component label for row i is labels[i].
        static void CollectBodies(IReadOnlyList<WaterRuntimeBodySnapshot> snapshot, List<WaterVolume> into)
        {
            into.Clear();
            for (int i = 0; i < snapshot.Count; i++) into.Add(snapshot[i].Body);
        }

        static int CountPrimaries(IReadOnlyList<WaterRuntimeBodySnapshot> snapshot)
        {
            int count = 0;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Body != null && snapshot[i].Body.IsPrimary) count++;
            return count;
        }

        static int CountUnwiredConnections()
        {
            int count = 0;
            for (int i = 0; i < WaterTopology.ConnectionCount; i++)
            {
                WaterConnection connection = WaterTopology.GetConnection(i);
                if (connection == null) continue;
                if (WaterRuntimeValidation.ValidateConnection(connection) != WaterRuntimeValidationFlags.None)
                    count++;
            }
            return count;
        }

        // The runtime rule (ValidateConnection) applied to every connection touching this body.
        static WaterRuntimeValidationFlags ConnectionWarningsForBody(WaterVolume body,
                                                                     IReadOnlyList<WaterConnection> connections)
        {
            WaterRuntimeValidationFlags flags = WaterRuntimeValidationFlags.None;
            for (int i = 0; i < connections.Count; i++)
            {
                WaterConnection connection = connections[i];
                if (connection == null) continue;
                WaterVolume bodyA = WaterNetworkComponents.BodyOf(connection.PortA);
                WaterVolume bodyB = WaterNetworkComponents.BodyOf(connection.PortB);
                if (bodyA != body && bodyB != body) continue;
                flags |= WaterRuntimeValidation.ValidateConnection(connection);
            }
            return flags;
        }

        void DrawTable()
        {
            _tableScroll = EditorGUILayout.BeginScrollView(_tableScroll);
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(TableWidth),
                                                        GUILayout.Height(ToolbarHeight)))
            {
                GUILayout.Label(ActionsLabel, EditorStyles.toolbarButton,
                                GUILayout.Width(ActionsWidth));
                DrawHeader(BodyLabel, SortColumn.Name, BodyWidth);
                DrawHeader(TypeLabel, SortColumn.Type, TypeWidth);
                DrawHeader(RoleLabel, SortColumn.Primary, RoleWidth);
                DrawHeader(NetworkLabel, SortColumn.Component, NetworkWidth);
                DrawHeader(AssignedQualityLabel, SortColumn.AssignedQuality,
                           AssignedQualityWidth);
                DrawHeader(ResolvedTierLabel, SortColumn.ResolvedQuality,
                           ResolvedQualityWidth);
                DrawHeader(RankLabel, SortColumn.RelevanceRank, RankWidth);
                DrawHeader(VisibilityHeaderLabel, SortColumn.Visibility, VisibilityWidth);
                DrawHeader(DistanceLabel, SortColumn.Distance, DistanceWidth);
                DrawHeader(CoverageLabel, SortColumn.Coverage, CoverageWidth);
                DrawHeader(SimulationLabel, SortColumn.Simulation, SimulationWidth);
                DrawHeader(PlanarLabel, SortColumn.Planar, PlanarWidth);
                DrawHeader(CausticLabel, SortColumn.Caustic, CausticWidth);
                DrawHeader(FoamLabelText, SortColumn.Foam, FoamWidth);
                DrawHeader(FogLabel, SortColumn.Fog, FogWidth);
                DrawHeader(ActivationLabel, SortColumn.Activation, ActivationWidth);
                DrawHeader(GpuMemoryLabel, SortColumn.Memory, MemoryWidth);
                DrawHeader(ValidationWarningsLabel, SortColumn.Validation, ValidationWidth);
            }

            for (int i = 0; i < _rows.Count; i++) DrawRow(_rows[i]);
            EditorGUILayout.EndScrollView();
        }

        void DrawHeader(string label, SortColumn column, float width)
        {
            string suffix = _sortColumn == column
                ? (_sortAscending ? AscendingSuffix : DescendingSuffix) : string.Empty;
            if (!GUILayout.Button(label + suffix, EditorStyles.toolbarButton,
                                  GUILayout.Width(width))) return;
            if (_sortColumn == column) _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = column;
                _sortAscending = true;
            }
        }

        static void DrawRow(Row row)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Width(TableWidth),
                                                        GUILayout.Height(RowHeight)))
            {
                if (GUILayout.Button(SelectLabel, GUILayout.Width(ActionButtonWidth)))
                    Selection.activeObject = row.Body.gameObject;
                if (GUILayout.Button(FrameLabel, GUILayout.Width(ActionButtonWidth)))
                {
                    Selection.activeObject = row.Body.gameObject;
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
                DrawCell(row.Body.name, BodyWidth);
                DrawCell(row.Body.BodyTypeForDiagnostics.ToString(), TypeWidth);
                DrawCell(row.Body.IsPrimary ? PrimaryLabel : SecondaryLabel, RoleWidth);
                DrawCell(ConnectedComponentPrefix + row.Component, NetworkWidth);
                DrawCell($"{row.Body.AssignedQualityName} ({row.Body.AssignedQualitySelection})",
                         AssignedQualityWidth);
                DrawCell(row.Body.ResolvedQualitySelection.ToString(), ResolvedQualityWidth);
                DrawCell(row.State.RelevanceRank.ToString(), RankWidth);
                DrawCell(VisibilityLabel(row.State), VisibilityWidth);
                DrawCell(row.State.NearestBoundsDistance.ToString(DistanceFormat) + " m",
                         DistanceWidth);
                DrawCell((row.State.ScreenCoverage * PercentageMultiplier)
                         .ToString(PercentageFormat) + "%", CoverageWidth);
                DrawCell(GrantLabel(row.State.SimulationEligible,
                                    row.State.SimulationGranted), SimulationWidth);
                DrawCell(GrantLabel(row.State.PlanarWanted, row.State.PlanarGranted), PlanarWidth);
                DrawCell(GrantLabel(row.State.CausticWanted, row.State.CausticGranted),
                         CausticWidth);
                DrawCell(FoamLabel(row.State), FoamWidth);
                DrawCell(row.State.SelectedFogSource ? SelectedLabel : NotSelectedLabel, FogWidth);
                DrawCell(row.Body.ActivationDistanceForDiagnostics.ToString(DistanceFormat) + " m",
                         ActivationWidth);
                DrawCell("~" + (row.ApproximateGpuBytes / MegabyteBytes).ToString(MemoryFormat)
                         + " MB", MemoryWidth);
                DrawCell(row.Validation == WaterRuntimeValidationFlags.None
                    ? NotSelectedLabel : row.Validation.ToString(), ValidationWidth);
            }
        }

        static string VisibilityLabel(WaterRuntimeBodySnapshot state)
        {
            if (state.FrustumVisible)
                return state.ImportancePinned ? VisiblePinnedLabel : VisibleLabel;
            return state.RenderVisible ? ForcedOnLabel : CulledLabel;
        }

        static string GrantLabel(bool wanted, bool granted)
        {
            if (granted) return GrantedLabel;
            return wanted ? WantedLabel : IneligibleLabel;
        }

        static string FoamLabel(WaterRuntimeBodySnapshot state)
        {
            if (state.FoamDrawn) return DrawnLabel;
            if (state.FoamCulled) return CulledLabel;
            return state.FoamWanted ? WantedLabel : IneligibleLabel;
        }

        static void DrawCell(string value, float width)
            => GUILayout.Label(value, GUILayout.Width(width));

        struct Row
        {
            internal WaterVolume Body;
            internal WaterRuntimeBodySnapshot State;
            internal int Component;
            internal WaterRuntimeValidationFlags Validation;
            internal long ApproximateGpuBytes;
        }

        enum SortColumn
        {
            Name,
            Type,
            Primary,
            Component,
            AssignedQuality,
            ResolvedQuality,
            RelevanceRank,
            Visibility,
            Distance,
            Coverage,
            Simulation,
            Planar,
            Caustic,
            Foam,
            Fog,
            Activation,
            Memory,
            Validation,
        }

        sealed class RowComparer : IComparer<Row>
        {
            internal SortColumn Column;
            internal bool Ascending;

            public int Compare(Row left, Row right)
            {
                int result;
                switch (Column)
                {
                    case SortColumn.Name:
                        result = string.Compare(left.Body.name, right.Body.name,
                                                StringComparison.OrdinalIgnoreCase); break;
                    case SortColumn.Type:
                        result = left.Body.BodyTypeForDiagnostics.CompareTo(
                            right.Body.BodyTypeForDiagnostics); break;
                    case SortColumn.Primary:
                        result = left.Body.IsPrimary.CompareTo(right.Body.IsPrimary); break;
                    case SortColumn.Component:
                        result = left.Component.CompareTo(right.Component); break;
                    case SortColumn.AssignedQuality:
                        result = string.Compare(left.Body.AssignedQualityName,
                            right.Body.AssignedQualityName, StringComparison.OrdinalIgnoreCase); break;
                    case SortColumn.ResolvedQuality:
                        result = left.Body.ResolvedQualitySelection.CompareTo(
                            right.Body.ResolvedQualitySelection); break;
                    case SortColumn.Visibility:
                        result = left.State.FrustumVisible.CompareTo(right.State.FrustumVisible); break;
                    case SortColumn.Distance:
                        result = left.State.NearestBoundsDistance.CompareTo(
                            right.State.NearestBoundsDistance); break;
                    case SortColumn.Coverage:
                        result = left.State.ScreenCoverage.CompareTo(right.State.ScreenCoverage); break;
                    case SortColumn.Simulation:
                        result = left.State.SimulationGranted.CompareTo(
                            right.State.SimulationGranted); break;
                    case SortColumn.Planar:
                        result = left.State.PlanarGranted.CompareTo(right.State.PlanarGranted); break;
                    case SortColumn.Caustic:
                        result = left.State.CausticGranted.CompareTo(right.State.CausticGranted); break;
                    case SortColumn.Foam:
                        result = left.State.FoamDrawn.CompareTo(right.State.FoamDrawn); break;
                    case SortColumn.Fog:
                        result = left.State.SelectedFogSource.CompareTo(
                            right.State.SelectedFogSource); break;
                    case SortColumn.Activation:
                        result = left.Body.ActivationDistanceForDiagnostics.CompareTo(
                            right.Body.ActivationDistanceForDiagnostics); break;
                    case SortColumn.Memory:
                        result = left.ApproximateGpuBytes.CompareTo(right.ApproximateGpuBytes); break;
                    case SortColumn.Validation:
                        result = left.Validation.CompareTo(right.Validation); break;
                    default:
                        result = left.State.RelevanceRank.CompareTo(right.State.RelevanceRank); break;
                }
                if (result == 0)
                    result = left.State.RegistrationOrder.CompareTo(right.State.RegistrationOrder);
                return Ascending ? result : -result;
            }
        }
    }
}
