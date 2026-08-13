// WebGpuWater - Water Wizard partial: "Fit Spray To Object".
//
// Fits a waterline outline for boats, rocks and other complex objects, then applies one global probe
// behaviour. Per-probe overrides remain possible in the component inspector, but are never required.
//
// Why a drawing and not a preview render: a draft is read from a side elevation, and a side elevation
// is 2D. PreviewRenderUtility would mean a second camera, its own lighting rig, and gizmo picking
// rebuilt from scratch because Handles do not work in a preview rect - all to show something a filled
// polygon and a horizontal line already say. Checking the result in context has its own home: applied
// probes are ordinary Scene-view gizmos drawn by WaterSprayPumpEditor.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterWizardWindow
    {
        const float PreviewHeight = 190f;
        // Margin around the hull inside the preview, as a fraction of the drawn span - so a long boat
        // and a small buoy both sit off the edges by the same visual amount.
        const float PreviewPaddingFraction = 0.08f;
        // Floor for that margin (and for the fitted span) in metres, so a degenerate hull cannot
        // collapse the view to zero width and divide by it.
        const float MinPreviewSpanMeters = 0.01f;
        // Half-height of the draft line's grab band, in pixels. Wide enough to catch without hunting.
        const float DraftGrabHalfHeight = 7f;
        const float DraftHandleWidth = 26f;
        const float DraftHandleHeight = 16f;
        const int LeftMouseButton = 0;

        const int DefaultHullProbeCount = 24;
        // An outline needs at least a triangle's worth of probes to read as a ring rather than a line.
        const int MinHullProbeCount = 3;
        const float DefaultOutwardInsetMeters = 0.03f;
        const float MaxOutwardInsetMeters = 0.5f;
        const float ProbeDotRadius = 3.5f;
        // Narrow enough that the petals read as petals the moment they are applied. Leaving the pump's
        // own 360 default here would apply directions the user cannot see any effect from.
        const float DefaultPetalArcDegrees = 70f;
        const float PetalArrowPixels = 16f;
        // Below this a projected direction is edge-on to the view and has no arrow to draw.
        const float MinArrowLengthSquared = 1e-8f;

        /// <summary>Where the draft line starts before the offset slider nudges it.</summary>
        /// <remarks>v2 §1.4's third source, "use current pose", is absent because it is already the
        /// behaviour: the silhouette and the slice both read the hull's live world transform, so a heeled
        /// hull is sliced heeled without a mode for it.</remarks>
        enum HullDraftSource { SolveFromBuoyancy, RestPlane }

        [SerializeField] bool _hullSprayExpanded;
        [SerializeField] HullDraftSource _hullDraftSource = HullDraftSource.SolveFromBuoyancy;
        [SerializeField] WaterHullOutline2D.View _hullPreviewView = WaterHullOutline2D.View.Side;
        [SerializeField] GameObject _hullSprayTarget;
        [SerializeField] MeshFilter _hullSprayMeshFilter;
        [SerializeField] float _hullDraftOffset;
        [SerializeField] int _hullProbeCount = DefaultHullProbeCount;
        [SerializeField] float _hullOutwardInset = DefaultOutwardInsetMeters;
        [SerializeField] float _hullPetalArcDegrees = DefaultPetalArcDegrees;
        [SerializeField] float _hullPetalElevationDegrees;
        [SerializeField] WaterSprayMode _hullSprayMode = WaterSprayMode.Boat;
        [SerializeField] bool _hullContinuousEmission;
        [SerializeField] WaterSprayWaterMotion _hullWaterMotion = WaterSprayWaterMotion.Rising;
        [SerializeField] bool _hullIgnoreSurfaceBand;

        // Preview state is window state: EditorWindow serializes its fields across domain reloads, so a
        // script recompile mid-drag does not wipe the draft the user just picked. Nothing reaches a
        // component until Apply.
        // Rebuilt whenever its inputs move. Not serialized: after a domain reload the cache keys come
        // back null, which fails the match below and rebuilds - cheaper than trusting a stale outline.
        // The SIDE silhouette is always built, whichever view is on screen, because the draft slider's
        // range is read from its world-Y bounds. The plan silhouette is built only while it is shown.
        [System.NonSerialized] WaterHullOutline2D.Silhouette _hullSilhouette;
        [System.NonSerialized] WaterHullOutline2D.Silhouette _hullPlanSilhouette;
        [System.NonSerialized] GameObject _hullSilhouetteSource;
        [System.NonSerialized] MeshFilter _hullSilhouetteFilter;
        [System.NonSerialized] Matrix4x4 _hullSilhouetteMatrix;
        [System.NonSerialized] WaterHullOutline2D.View _hullSilhouetteView;

        [System.NonSerialized] WaterHullSlice.Result _hullSlice;
        [System.NonSerialized] SliceKey _hullSliceKey;

        // ---- section ---------------------------------------------------------

        void DrawFitSprayToHullSection()
        {
            EditorGUILayout.HelpBox("Rings a boat, rock or other object's real waterline with spray probes. " +
                                    "Drag the amber draft line, choose one behaviour for every probe, then Apply. Check " +
                                    "the result in place afterwards: applied probes are ordinary gizmos on " +
                                    "the object's Water Spray Pump.", MessageType.None);

            DrawHullSourceFields();

            GameObject hull = ResolveHullObject();
            if (hull == null)
            {
                EditorGUILayout.HelpBox("Select a scene object, or assign a target above.",
                                        MessageType.Info);
                return;
            }

            DrawGlobalProbeBehaviour(hull);

            RefreshSilhouette(hull);
            if (!_hullSilhouette.Ok)
            {
                EditorGUILayout.HelpBox(_hullSilhouette.Error, MessageType.Warning);
                return;
            }

            float restWorldY = WaterSprayPumpEditor.ResolveWaterY(hull.transform.position);
            WaterHullDraftSolver.Solution draft = ResolveDraftBase(hull, restWorldY);
            float baseWorldY = draft.DraftWorldY;

            var view = DraftView.Fit(_hullSilhouette, restWorldY, baseWorldY);
            _hullDraftOffset = Mathf.Clamp(_hullDraftOffset, view.MinOffset, view.MaxOffset);

            float draftWorldY = baseWorldY + _hullDraftOffset;
            RefreshSlice(hull, draftWorldY);

            _hullPreviewView = (WaterHullOutline2D.View)GUILayout.Toolbar(
                (int)_hullPreviewView, HullSprayStyle.ViewLabels);

            Rect previewRect = GUILayoutUtility.GetRect(0f, PreviewHeight, GUILayout.ExpandWidth(true));
            bool sideView = _hullPreviewView == WaterHullOutline2D.View.Side;
            var sideMap = new PreviewMap(previewRect, view.World);

            if (sideView) DrawSideElevation(previewRect, sideMap, hull, restWorldY, draftWorldY);
            else DrawPlanView(previewRect, hull);

            // Called on BOTH views on purpose: it claims a control ID, and claiming one only sometimes
            // would desynchronise IMGUI's per-frame control numbering on the frame the view is switched.
            HandleDraftDrag(previewRect, sideMap, baseWorldY, view, draggable: sideView);

            DrawDraftControls(view, restWorldY, draftWorldY, draft);
            DrawSliceControls(hull);
        }

        // Solving reads the hull's own WaterBuoyancy, so the line starts where the boat will actually
        // float rather than on the flat rest plane.
        WaterHullDraftSolver.Solution ResolveDraftBase(GameObject hull, float restWorldY)
            => _hullDraftSource == HullDraftSource.SolveFromBuoyancy
                ? WaterHullDraftSolver.Solve(hull, restWorldY)
                : WaterHullDraftSolver.Solution.RestPlane(restWorldY, null);

        void DrawHullSourceFields()
        {
            _hullSprayTarget = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target object", "The boat, rock or other object in the scene. Leave empty " +
                                                "to follow the current selection."),
                _hullSprayTarget, typeof(GameObject), allowSceneObjects: true);

            _hullSprayMeshFilter = (MeshFilter)EditorGUILayout.ObjectField(
                new GUIContent("Slice mesh (optional)", "The one mesh that touches water. Leave empty to use " +
                                                        "every mesh under the target - assign this when the " +
                                                        "visual root also carries masts, cabins, vegetation or " +
                                                        "other geometry that must not count."),
                _hullSprayMeshFilter, typeof(MeshFilter), allowSceneObjects: true);
        }

        void DrawGlobalProbeBehaviour(GameObject targetObject)
        {
            EditorGUILayout.Space();
            WaterEditorUI.SubHeading("Global Probe Behaviour");
            _hullSprayMode = (WaterSprayMode)EditorGUILayout.EnumPopup(
                new GUIContent("Trigger mode", "Boat reacts to object movement, Rock reacts to water movement, " +
                                               "Both reacts to either source."),
                _hullSprayMode);
            _hullContinuousEmission = EditorGUILayout.Toggle(
                new GUIContent("Continuous", "Off creates paced impact bursts. On creates a steady stream while " +
                                             "the selected trigger remains active."),
                _hullContinuousEmission);

            if (_hullSprayMode != WaterSprayMode.Boat)
            {
                _hullWaterMotion = (WaterSprayWaterMotion)EditorGUILayout.EnumPopup(
                    new GUIContent("Water motion", "For Rock and Both modes: react to rising water, falling " +
                                                    "water, or either direction."),
                    _hullWaterMotion);
            }

            _hullIgnoreSurfaceBand = EditorGUILayout.Toggle(
                new GUIContent("Ignore surface band", "Keep probes active even when the object moves above or " +
                                                     "below the configured surface band."),
                _hullIgnoreSurfaceBand);

            WaterSprayPump existingPump = targetObject.GetComponent<WaterSprayPump>();
            using (new EditorGUI.DisabledScope(existingPump == null))
            {
                if (GUILayout.Button(new GUIContent("Apply Behaviour To Existing Probes",
                        "Changes every existing probe without rebuilding its position or direction.")))
                    ApplyGlobalProbeBehaviour(existingPump);
            }

            if (existingPump == null)
                EditorGUILayout.HelpBox("This object has no Water Spray Pump yet. The main Apply button below " +
                                        "will create it and use these settings.", MessageType.Info);
        }

        void ApplyGlobalProbeBehaviour(WaterSprayPump pump)
        {
            var pumpObject = new SerializedObject(pump);
            WaterSprayEmission emission = ResolveHullSprayEmission();
            int changedCount = WaterSprayPumpEditor.ApplyProbeBehaviour(
                pumpObject, _hullSprayMode, emission, _hullWaterMotion, _hullIgnoreSurfaceBand);
            Debug.Log($"[WebGpuWater] Applied {_hullSprayMode} / {emission} behaviour to " +
                      $"{changedCount} probe(s) on '{pump.name}'.", pump);
        }

        WaterSprayEmission ResolveHullSprayEmission() => _hullContinuousEmission
            ? WaterSprayEmission.Continuous
            : WaterSprayEmission.Burst;

        // The hull an action targets: the explicit field, else whatever is selected. Mirrors
        // SelectedBody()'s idiom so the section behaves like the rest of the wizard.
        GameObject ResolveHullObject()
            => _hullSprayTarget != null ? _hullSprayTarget : Selection.activeGameObject;

        // The projection depends on the hull's world transform, so a moved or rotated boat must
        // re-project. Comparing the matrix catches that without polling the mesh itself.
        void RefreshSilhouette(GameObject hull)
        {
            Matrix4x4 matrix = hull.transform.localToWorldMatrix;
            bool current = _hullSilhouetteSource == hull
                           && _hullSilhouetteFilter == _hullSprayMeshFilter
                           && _hullSilhouetteMatrix == matrix
                           && _hullSilhouetteView == _hullPreviewView;
            if (current) return;

            _hullSilhouette = WaterHullOutline2D.Build(hull, _hullSprayMeshFilter, WaterHullOutline2D.View.Side);
            _hullPlanSilhouette = _hullPreviewView == WaterHullOutline2D.View.Top
                ? WaterHullOutline2D.Build(hull, _hullSprayMeshFilter, WaterHullOutline2D.View.Top)
                : default;
            _hullSilhouetteSource = hull;
            _hullSilhouetteFilter = _hullSprayMeshFilter;
            _hullSilhouetteMatrix = matrix;
            _hullSilhouetteView = _hullPreviewView;
        }

        // The slice depends on the draft, so it is rebuilt when the draft moves - and on the same
        // transform key as the silhouette, so the dots can never be drawn against a stale outline.
        void RefreshSlice(GameObject hull, float draftWorldY)
        {
            var key = new SliceKey(hull, _hullSprayMeshFilter, hull.transform.localToWorldMatrix,
                                   draftWorldY, _hullProbeCount, _hullOutwardInset);
            if (key.Equals(_hullSliceKey)) return;

            _hullSlice = WaterHullSlice.Build(hull, _hullSprayMeshFilter, draftWorldY,
                                              _hullProbeCount, _hullOutwardInset);
            _hullSliceKey = key;
        }

        // ---- drawing ---------------------------------------------------------

        void DrawSideElevation(Rect rect, in PreviewMap map, GameObject hull, float restWorldY, float draftWorldY)
        {
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, HullSprayStyle.Background);
            DrawSilhouetteFill(map, _hullSilhouette);

            float restPixelY = map.ToPixelY(restWorldY);
            DrawLevelLine(rect, restPixelY, HullSprayStyle.RestPlaneColor, HullSprayStyle.RestLineThickness);
            GUI.Label(LabelRectAbove(rect, restPixelY), "rest plane", HullSprayStyle.RestLabel);

            float draftPixelY = map.ToPixelY(draftWorldY);
            DrawLevelLine(rect, draftPixelY, HullSprayStyle.DraftColor, HullSprayStyle.DraftLineThickness);
            GUI.Label(LabelRectAbove(rect, draftPixelY), "draft - drag me", HullSprayStyle.DraftLabel);
            EditorGUI.DrawRect(DraftHandleRect(rect, draftPixelY), HullSprayStyle.DraftColor);

            DrawProbeDots(map, WaterHullOutline2D.Frame.For(hull.transform, WaterHullOutline2D.View.Side));
        }

        // Looking down: no draft line (a horizontal plane seen from above IS the whole view) and no drag.
        // What this view is for is the petal directions - the one thing the side elevation cannot show.
        void DrawPlanView(Rect rect, GameObject hull)
        {
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, HullSprayStyle.Background);
            if (!_hullPlanSilhouette.Ok)
            {
                GUI.Label(rect, "  no plan outline", HullSprayStyle.RestLabel);
                return;
            }

            var map = new PreviewMap(rect, Pad(_hullPlanSilhouette.Bounds));
            DrawSilhouetteFill(map, _hullPlanSilhouette);

            var frame = WaterHullOutline2D.Frame.For(hull.transform, WaterHullOutline2D.View.Top);
            DrawPetalArrows(map, frame);
            DrawProbeDots(map, frame);
        }

        // The same margin rule DraftView.Fit applies, so both views sit off their edges identically.
        static Rect Pad(Rect bounds)
        {
            float padX = Mathf.Max(MinPreviewSpanMeters, bounds.width * PreviewPaddingFraction);
            float padY = Mathf.Max(MinPreviewSpanMeters, bounds.height * PreviewPaddingFraction);
            return Rect.MinMaxRect(bounds.xMin - padX, bounds.yMin - padY,
                                   bounds.xMax + padX, bounds.yMax + padY);
        }

        // Probes are projected through the SAME frame the silhouette used, so a dot sitting on the bow in
        // the drawing is a probe sitting on the bow in the world.
        void DrawProbeDots(in PreviewMap map, in WaterHullOutline2D.Frame frame)
        {
            if (!_hullSlice.Ok) return;

            Handles.color = HullSprayStyle.ProbeColor;
            foreach (WaterHullSlice.Probe probe in _hullSlice.Probes)
                Handles.DrawSolidDisc(map.ToPixel(frame.Project(probe.WorldPosition)), Vector3.forward,
                                      ProbeDotRadius);
        }

        // One arrow per probe, out of the hull. Drawn in PIXELS rather than metres so the arrows stay
        // readable on a 30 m hull and do not swamp a 2 m one.
        void DrawPetalArrows(in PreviewMap map, in WaterHullOutline2D.Frame frame)
        {
            if (!_hullSlice.Ok) return;

            Handles.color = HullSprayStyle.PetalArrowColor;
            foreach (WaterHullSlice.Probe probe in _hullSlice.Probes)
            {
                Vector3 outwardWorld = new Vector3(probe.OutwardXZ.x, 0f, probe.OutwardXZ.y);
                Vector2 direction = frame.ProjectDirection(outwardWorld);
                if (direction.sqrMagnitude < MinArrowLengthSquared) continue;

                Vector3 from = map.ToPixel(frame.Project(probe.WorldPosition));
                // Pixel Y grows downward, so the projected Y is negated to keep the arrow pointing the
                // same way the dot's own projection does.
                Vector2 pixelDirection = new Vector2(direction.x, -direction.y).normalized;
                Vector3 to = from + new Vector3(pixelDirection.x, pixelDirection.y, 0f) * PetalArrowPixels;
                Handles.DrawAAPolyLine(HullSprayStyle.PetalArrowThickness, from, to);
            }
        }

        void DrawSilhouetteFill(in PreviewMap map, in WaterHullOutline2D.Silhouette silhouette)
        {
            Vector2[] outline = silhouette.Outline;
            var polygon = new Vector3[outline.Length];
            for (int i = 0; i < outline.Length; i++)
                polygon[i] = map.ToPixel(outline[i]);

            Handles.color = HullSprayStyle.HullFillColor;
            Handles.DrawAAConvexPolygon(polygon);

            // DrawAAPolyLine does not close itself, so the first point is repeated to seal the outline.
            var closed = new Vector3[polygon.Length + 1];
            System.Array.Copy(polygon, closed, polygon.Length);
            closed[polygon.Length] = polygon[0];
            Handles.color = HullSprayStyle.HullEdgeColor;
            Handles.DrawAAPolyLine(HullSprayStyle.HullEdgeThickness, closed);
        }

        static void DrawLevelLine(Rect rect, float pixelY, Color color, float thickness)
            => EditorGUI.DrawRect(new Rect(rect.x, pixelY - thickness * 0.5f, rect.width, thickness), color);

        static Rect DraftHandleRect(Rect rect, float pixelY)
            => new Rect(rect.xMax - DraftHandleWidth - HullSprayStyle.LabelInset,
                        pixelY - DraftHandleHeight * 0.5f, DraftHandleWidth, DraftHandleHeight);

        static Rect LabelRectAbove(Rect rect, float pixelY)
            => new Rect(rect.x + HullSprayStyle.LabelInset, pixelY - HullSprayStyle.LabelHeight,
                        rect.width, HullSprayStyle.LabelHeight);

        // ---- interaction -----------------------------------------------------

        void HandleDraftDrag(Rect rect, in PreviewMap map, float baseWorldY, in DraftView view, bool draggable)
        {
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (!draggable) return;

            float pixelY = map.ToPixelY(baseWorldY + _hullDraftOffset);
            var grabBand = new Rect(rect.x, pixelY - DraftGrabHalfHeight, rect.width, DraftGrabHalfHeight * 2f);
            EditorGUIUtility.AddCursorRect(grabBand, MouseCursor.ResizeVertical);

            Event evt = Event.current;
            switch (evt.GetTypeForControl(controlId))
            {
                case EventType.MouseDown:
                    if (evt.button != LeftMouseButton || !grabBand.Contains(evt.mousePosition)) break;
                    GUIUtility.hotControl = controlId;
                    evt.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId) break;
                    _hullDraftOffset = Mathf.Clamp(map.ToWorldY(evt.mousePosition.y) - baseWorldY,
                                                   view.MinOffset, view.MaxOffset);
                    evt.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    evt.Use();
                    break;
            }
        }

        void DrawDraftControls(in DraftView view, float restWorldY, float draftWorldY,
                               in WaterHullDraftSolver.Solution draft)
        {
            _hullDraftSource = (HullDraftSource)EditorGUILayout.EnumPopup(
                new GUIContent("Draft source", "Solve from buoyancy reads the hull's own WaterBuoyancy and " +
                                               "puts the line where the boat will actually settle. Rest plane " +
                                               "starts from the water's flat surface instead."),
                _hullDraftSource);

            if (draft.Error != null)
                EditorGUILayout.HelpBox(draft.Error, MessageType.Warning);
            else if (draft.Warning != null)
                EditorGUILayout.HelpBox(draft.Warning, MessageType.Warning);
            else if (_hullDraftSource == HullDraftSource.SolveFromBuoyancy && !draft.Solved)
                EditorGUILayout.HelpBox("No WaterBuoyancy on this object, so the draft starts from the rest " +
                                        "plane. That is the normal case for a kinematic boat.", MessageType.Info);
            else if (draft.Solved)
                EditorGUILayout.HelpBox("Solved from the hull's WaterBuoyancy, which lays its lattice across " +
                                        "the COLLIDER - so this is where the boat really floats, which can " +
                                        "differ from where the hull mesh alone suggests.", MessageType.Info);

            _hullDraftOffset = EditorGUILayout.Slider(
                new GUIContent("Draft offset", "Nudge the draft plane off its source, in metres. Negative " +
                                               "sinks the hull deeper."),
                _hullDraftOffset, view.MinOffset, view.MaxOffset);

            EditorGUILayout.LabelField("Draft world Y",
                $"{draftWorldY:0.###}   (rest plane {restWorldY:0.###})");
        }

        // ---- slice, budget and apply ------------------------------------------

        void DrawSliceControls(GameObject hull)
        {
            _hullProbeCount = EditorGUILayout.IntSlider(
                new GUIContent("Probe count", "How many spray probes to spread around the waterline, " +
                                              "evenly by distance along the outline rather than by vertex."),
                _hullProbeCount, MinHullProbeCount, WaterSprayPumpEditor.MaxProbeCount);

            _hullOutwardInset = EditorGUILayout.Slider(
                new GUIContent("Outward inset", "Push each probe this far clear of the plating, in metres, " +
                                                "so it sprays just off the hull instead of inside it."),
                _hullOutwardInset, 0f, MaxOutwardInsetMeters);

            _hullPetalArcDegrees = EditorGUILayout.Slider(
                new GUIContent("Petal arc", "Width of each probe's wedge, in degrees. 360 is the full " +
                                            "ring every splash threw before hull fitting; narrower throws " +
                                            "a petal. Written onto the pump by Apply, and tunable there " +
                                            "afterwards along with the rake."),
                _hullPetalArcDegrees, WaterSprayPump.MinPetalArcDegrees, WaterSprayPump.FullRingDegrees);

            _hullPetalElevationDegrees = EditorGUILayout.Slider(
                new GUIContent("Petal elevation", "Lift every burst toward vertical, on top of the launch " +
                                                  "angle the splash emitter's Upward Bias already gives it. " +
                                                  "0 leaves it alone; 90 tops out straight up. This one " +
                                                  "needs no probe direction, so it lifts full rings too."),
                _hullPetalElevationDegrees, WaterSprayPump.MinPetalElevationDegrees,
                WaterSprayPump.MaxPetalElevationDegrees);

            if (!_hullSlice.Ok)
            {
                EditorGUILayout.HelpBox(_hullSlice.Error, MessageType.Warning);
                EditorGUILayout.HelpBox("Assign the mesh that actually intersects the waterline, or adjust " +
                                        "the draft until it crosses the target geometry.", MessageType.Info);
                return;
            }

            if (_hullSlice.Warning != null)
                EditorGUILayout.HelpBox(_hullSlice.Warning, MessageType.Warning);
            if (_hullSlice.LoopCount > 1)
                EditorGUILayout.HelpBox($"The hull slices into {_hullSlice.LoopCount} separate outlines at this " +
                                        "draft (a catamaran, or a hull split above its keel). Probes are shared " +
                                        "between them by outline length.", MessageType.Info);

            DrawBurstBudgetWarning();

            if (GUILayout.Button(new GUIContent($"Apply To \"{hull.name}\"",
                    "Write these probes onto the object's Water Spray Pump, adding one if it has none."),
                    GUILayout.Height(26f)))
                ApplyHullProbes(hull);
        }

        // Trap the plan calls out: WaterFoamParticles drops the surplus bursts in a frame rather than
        // deferring them, and the drops land on whichever probes sit late in the array - one side of the
        // hull. Surfacing the arithmetic here is what stops it being met later as mysterious one-sided spray.
        void DrawBurstBudgetWarning()
        {
            int probeCount = _hullSlice.Probes.Length;
            if (probeCount <= WaterFoamParticles.MaxBurstsPerFrame) return;

            EditorGUILayout.HelpBox(
                $"{probeCount} probes can trigger in the same frame, and the burst cap is " +
                $"{WaterFoamParticles.MaxBurstsPerFrame} - the surplus is dropped, always the probes late in " +
                "the array, so one side of the hull goes quiet at speed. Cooldown staggering lands in a later " +
                "increment; until then, keep the count at or under the cap for even spray.",
                MessageType.Warning);
        }

        void ApplyHullProbes(GameObject hull)
        {
            Undo.SetCurrentGroupName("Fit Spray To Object");
            int undoGroup = Undo.GetCurrentGroup();

            // GetComponent returns a fake-null on a missing component, so this cannot use ?? here.
            var pump = hull.GetComponent<WaterSprayPump>();
            if (pump == null) pump = Undo.AddComponent<WaterSprayPump>(hull);

            WaterHullSlice.Probe[] probes = _hullSlice.Probes;
            var localOffsets = new Vector3[probes.Length];
            var outwardLocals = new Vector3[probes.Length];
            for (int i = 0; i < probes.Length; i++)
            {
                localOffsets[i] = hull.transform.InverseTransformPoint(probes[i].WorldPosition);
                // Directions are transformed as DIRECTIONS, not points: an InverseTransformPoint here would
                // fold the hull's position into the vector and aim every probe at the world origin.
                Vector2 outward = probes[i].OutwardXZ;
                outwardLocals[i] = hull.transform.InverseTransformDirection(
                    new Vector3(outward.x, 0f, outward.y));
            }

            var pumpObject = new SerializedObject(pump);
            WaterSprayEmission emission = ResolveHullSprayEmission();
            WaterSprayPumpEditor.WriteProbes(
                pumpObject, localOffsets, outwardLocals, _hullSprayMode, emission,
                _hullWaterMotion, _hullIgnoreSurfaceBand);

            // Written after the probes, because WriteProbes re-reads the object and would discard it.
            // Both edits land in the one Undo group collapsed below.
            pumpObject.Update();
            pumpObject.FindProperty("petalArcDegrees").floatValue = _hullPetalArcDegrees;
            pumpObject.FindProperty("petalElevationDegrees").floatValue = _hullPetalElevationDegrees;
            pumpObject.ApplyModifiedProperties();

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeObject = hull;
            Debug.Log($"[WebGpuWater] Fitted {probes.Length} {_hullSprayMode} / {emission} spray probe(s) " +
                      $"to '{hull.name}' " +
                      $"across {_hullSlice.LoopCount} waterline outline(s), arc {_hullPetalArcDegrees:0}°, " +
                      $"elevation {_hullPetalElevationDegrees:0}°.",
                      hull);
        }

        /// <summary>Everything the slice depends on, so it is recomputed exactly when one of them moves.</summary>
        readonly struct SliceKey : System.IEquatable<SliceKey>
        {
            readonly GameObject _hull;
            readonly MeshFilter _filter;
            readonly Matrix4x4 _hullToWorld;
            readonly float _draftWorldY;
            readonly int _probeCount;
            readonly float _inset;

            public SliceKey(GameObject hull, MeshFilter filter, Matrix4x4 hullToWorld,
                            float draftWorldY, int probeCount, float inset)
            {
                _hull = hull;
                _filter = filter;
                _hullToWorld = hullToWorld;
                _draftWorldY = draftWorldY;
                _probeCount = probeCount;
                _inset = inset;
            }

            public bool Equals(SliceKey other)
                => _hull == other._hull
                   && _filter == other._filter
                   && _hullToWorld == other._hullToWorld
                   && _draftWorldY == other._draftWorldY
                   && _probeCount == other._probeCount
                   && _inset == other._inset;
        }

        // ---- view maths ------------------------------------------------------

        /// <summary>The metres shown in the preview, and the draft range that implies.</summary>
        readonly struct DraftView
        {
            /// <summary>Padded (along-hull, world-Y) window the preview draws, in metres.</summary>
            public readonly Rect World;

            readonly float _baseWorldY;

            public float MinOffset => World.yMin - _baseWorldY;
            public float MaxOffset => World.yMax - _baseWorldY;

            DraftView(Rect world, float baseWorldY)
            {
                World = world;
                _baseWorldY = baseWorldY;
            }

            // The rest plane and the solved base are both included in the fit even when they sit clear of
            // the hull: seeing how far the boat floats above (or below) its own water is the whole point
            // of the elevation.
            public static DraftView Fit(in WaterHullOutline2D.Silhouette silhouette, float restWorldY,
                                        float baseWorldY)
            {
                Rect hull = silhouette.Bounds;
                float minY = Mathf.Min(Mathf.Min(hull.yMin, restWorldY), baseWorldY);
                float maxY = Mathf.Max(Mathf.Max(hull.yMax, restWorldY), baseWorldY);
                float padX = Mathf.Max(MinPreviewSpanMeters, hull.width * PreviewPaddingFraction);
                float padY = Mathf.Max(MinPreviewSpanMeters, (maxY - minY) * PreviewPaddingFraction);
                Rect world = Rect.MinMaxRect(hull.xMin - padX, minY - padY, hull.xMax + padX, maxY + padY);
                return new DraftView(world, baseWorldY);
            }
        }

        /// <summary>Metres to pixels at a single uniform scale, so the hull keeps its proportions.</summary>
        readonly struct PreviewMap
        {
            readonly Vector2 _pixelCenter;
            readonly Vector2 _worldCenter;
            readonly float _pixelsPerMeter;

            public PreviewMap(Rect pixels, Rect world)
            {
                _pixelCenter = pixels.center;
                _worldCenter = world.center;
                // One scale for both axes: a stretched fit would make a shallow hull look deep-draughted,
                // and "does the silhouette look like the boat" is exactly what this view is judged on.
                _pixelsPerMeter = Mathf.Min(pixels.width / Mathf.Max(world.width, MinPreviewSpanMeters),
                                            pixels.height / Mathf.Max(world.height, MinPreviewSpanMeters));
            }

            public Vector3 ToPixel(Vector2 world) => new Vector3(
                _pixelCenter.x + (world.x - _worldCenter.x) * _pixelsPerMeter,
                ToPixelY(world.y),
                0f);

            // Pixel Y grows downward; world Y grows upward.
            public float ToPixelY(float worldY) => _pixelCenter.y - (worldY - _worldCenter.y) * _pixelsPerMeter;

            public float ToWorldY(float pixelY) => _worldCenter.y - (pixelY - _pixelCenter.y) / _pixelsPerMeter;
        }

        // ---- palette + metrics (no inline literals in the drawing code above) ----

        static class HullSprayStyle
        {
            public static readonly Color Background = new Color(0.04f, 0.08f, 0.13f, 1f);
            public static readonly Color HullFillColor = new Color(0.13f, 0.19f, 0.24f, 1f);
            public static readonly Color HullEdgeColor = new Color(0.56f, 0.71f, 0.79f, 1f);
            public static readonly Color RestPlaneColor = new Color(0.18f, 0.37f, 0.47f, 1f);
            public static readonly Color DraftColor = new Color(0.94f, 0.71f, 0.35f, 1f);
            public static readonly Color ProbeColor = new Color(0.30f, 0.85f, 0.91f, 1f);
            public static readonly Color PetalArrowColor = new Color(0.72f, 0.55f, 0.92f, 1f);

            // Index-matched to WaterHullOutline2D.View, which the toolbar casts through.
            public static readonly string[] ViewLabels = { "Side", "Top" };

            public const float PetalArrowThickness = 1.6f;

            public const float HullEdgeThickness = 2f;
            public const float RestLineThickness = 1f;
            public const float DraftLineThickness = 2f;
            public const float LabelInset = 6f;
            public const float LabelHeight = 14f;

            static GUIStyle _restLabel;
            static GUIStyle _draftLabel;

            public static GUIStyle RestLabel => _restLabel ??= MiniLabel(RestPlaneColor);
            public static GUIStyle DraftLabel => _draftLabel ??= MiniLabel(DraftColor);

            static GUIStyle MiniLabel(Color color)
                => new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = color } };
        }
    }
}
