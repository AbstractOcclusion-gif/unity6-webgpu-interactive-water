// WebGpuWater - WaterVolume inspector: the BODY tab.
// What the body IS and WHERE it is: placement, the driven renderers, the chunk footprint, the bed
// terrain that forms its floor, the asset wiring, and the camera. Nothing here changes how the
// water looks or moves - those are the Surface / Volume / Motion tabs.
//
// The bed TERRAIN SOURCE lives here (not with the colours it feeds) because the bed is the body's
// floor, the same kind of fact as its extent. Its colour + clarity knobs are in Volume, its surf
// fronts in Motion, its bake resolution in Budget; each of those greys out until this toggle is on.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        // Topology: the two flags that decide WHAT KIND of body this is, as plain tick boxes and
        // never greyed. They used to be reachable only as the enable toggle on a Motion section and a
        // field inside its Advanced fold - and both of those grey out with the body-type selector, so
        // a Pond could not be turned into open water at all without first pressing "Apply defaults".
        // A checkbox that cannot be ticked reads as a missing feature, which is exactly how it read.
        void DrawTopologySection()
        {
            _showTopology = WaterEditorUI.Section("Topology", _showTopology, () =>
            {
                EditorGUILayout.HelpBox(TopologyHelp, MessageType.Info);

                SerializedProperty openWater = Prop(WaterVolumePropertyPaths.OpenWater);
                SerializedProperty unbounded = Prop(WaterVolumePropertyPaths.UnboundedOcean);

                EditorGUILayout.PropertyField(openWater, OpenWaterLabel);

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(unbounded, InfiniteLabel);
                // Turning INFINITE on writes its two prerequisites. That is not the "never silently
                // clobber tuning" case the Apply-defaults button guards against: ShouldWindow() only
                // windows an unbounded body when the large-body window is enabled, and IsOceanClipmap
                // needs open water as well - so ticking this alone would leave a body that is still
                // bounded and looks exactly like a lake, with nothing on screen to say why.
                // Turning it OFF touches nothing: a bounded open-water lake is a valid body.
                if (EditorGUI.EndChangeCheck() && unbounded.boolValue)
                {
                    openWater.boolValue = true;
                    Prop(WaterVolumePropertyPaths.EnableLargeBodyWindow).boolValue = true;
                }

                DrawMakeInfiniteFlat();
            });
        }

        // One click from "pond" to an endless flat pond: the three topology flags, plus a zeroed sea
        // state so the surface carries only wind waves and interactive ripples. Button-driven and
        // explicit, like "Apply defaults" - a preset should never fire off a tick box.
        //
        // Gated on the analytic pool because the two cannot coexist: WaterSurfacePoolTrace early-outs
        // on _LargeBody, since the analytic pool is a finite BOX traced in pool space and an endless
        // surface has no box to trace. A wired pool renderer would keep drawing its tile box adrift in
        // the middle of the sea, so say that instead of clearing someone's wiring behind their back.
        void DrawMakeInfiniteFlat()
        {
            if (HasProceduralPool)
            {
                EditorGUILayout.HelpBox(InfiniteNeedsNoPoolHelp, MessageType.Warning);
                return;
            }

            if (!GUILayout.Button(MakeInfiniteFlatLabel)) return;

            Prop(WaterVolumePropertyPaths.OpenWater).boolValue = true;
            Prop(WaterVolumePropertyPaths.UnboundedOcean).boolValue = true;
            Prop(WaterVolumePropertyPaths.EnableLargeBodyWindow).boolValue = true;
            // Flat: the analytic swell off at both knobs. Wind waves and the ripple sim are untouched -
            // they are what makes it read as a pond rather than as a dead mirror.
            Prop(WaterVolumePropertyPaths.SignificantWaveHeight).floatValue = 0f;
            Prop(WaterVolumePropertyPaths.LargeWaveAmplitude).floatValue = 0f;
        }

        void DrawPlacementSection()
        {
            _showPlacement = WaterEditorUI.Section("Placement", _showPlacement, () =>
            {
                EditorGUILayout.HelpBox(PlacementHelp, MessageType.Info);
                DrawFields("volumeExtent");
            });
        }

        void DrawBodySection()
        {
            _showBody = WaterEditorUI.Section("Water Body (multi-instance)", _showBody, () =>
            {
                DrawFields("isPrimary", "autoLinkReceivers", "renderBuiltInGeometry");
                // The renderers are wired by the wizard / scene builder and then never touched.
                _showBodyAdvanced = WaterEditorUI.SubSection("Advanced", _showBodyAdvanced, () =>
                {
                    WaterEditorUI.SubHeading("Driven renderers");
                    DrawFields("surfaceAbove", "surfaceUnder", "poolRenderer", "godRayRenderer");
                });
            });
        }

        // The terrain that forms the floor. Only the SOURCE is here; everything derived from it is
        // drawn in the tab that owns the derivation, and each of those blocks greys on this toggle.
        void DrawBedSourceSection()
        {
            _showBedSource = WaterEditorUI.SectionWithToggle(
                "Bed Depth (terrain floor)", _showBedSource, Prop(WaterVolumePropertyPaths.UseBedDepth), () =>
            {
                DrawFields(WaterVolumePropertyPaths.BedTerrain);
                EditorGUILayout.HelpBox(BedSourceHelp, MessageType.None);
            });
        }

        void DrawWiringSection()
        {
            _showWiring = WaterEditorUI.Section("Wiring & References (scene builder)", _showWiring, () =>
            {
                EditorGUILayout.HelpBox(WiringHelp, MessageType.None);
                WaterEditorUI.SubHeading("Sun & light");
                DrawFields(WaterVolumePropertyPaths.Sun);
                // lightDir is auto-driven from the assigned sun every tick (WaterUniformPublisher),
                // so it is read-only while a sun drives it - editable only when no sun is set.
                DrawFieldsIf(!HasSun, "lightDir");
                WaterEditorUI.SubHeading("Assets");
                // NOTE: never list a path here without its serialized field on WaterVolume -
                // Prop() returns null for a missing path and PropertyField(null) throws the
                // moment the section unfolds ("sweCompute" lingered here after the SWE removal).
                DrawFields(
                    "simCompute", "oceanFftCompute", "causticsShader",
                    "largeBodyCausticsShader", "obstacleShader", "occluderShader", "waterMesh",
                    "targetCamera");
            });
        }

        void DrawCameraSection()
        {
            _showCamera = WaterEditorUI.Section("Camera", _showCamera, () =>
                DrawFields("orbit", "configureCamera"));
        }

        static readonly GUIContent OpenWaterLabel = new GUIContent(
            "Open Water",
            "The surface stands alone with no analytic pool: the refracted view falls back to the " +
            "deep-water colour where there is no scene geometry. Off = the pool / small-body look.");

        static readonly GUIContent InfiniteLabel = new GUIContent(
            "Infinite Surface",
            "Unbounded lake or ocean: the surface spans everywhere instead of ending at the volume " +
            "extent, drawn through the horizon clipmap and simulated in a camera-following window. " +
            "Ticking this also enables Open Water and the large-water sim window, which it needs.");

        static readonly GUIContent MakeInfiniteFlatLabel = new GUIContent(
            "Make Infinite (flat pond)",
            "Sets Open Water + Infinite Surface + the sim window, and zeroes the sea state, leaving " +
            "an endless surface carrying only wind waves and interactive ripples. An Ocean FFT " +
            "compute, if one is wired, still adds its spectral waves on top - clear that slot too " +
            "for a truly flat body.");

        const string InfiniteNeedsNoPoolHelp =
            "This body draws the analytic pool, which an infinite surface cannot keep: the pool is a " +
            "finite box traced in pool space, and the surface shader skips that trace entirely on an " +
            "open-water body. Unwire Pool Renderer (Water Body > Advanced) to make this body infinite; " +
            "a visible bottom then has to be real geometry or terrain.";

        const string TopologyHelp =
            "What kind of body this is. Infinite needs Open Water and the sim window, and ticking it " +
            "turns both on. The volume extent still bounds an infinite body's DEPTH and its footprint " +
            "for buoyancy queries.";

        const string PlacementHelp =
            "Position and rotation come from this GameObject's Transform - move/rotate it to place " +
            "the water. Extent is the world half-size per pool unit (X width, Y depth, Z length).";
        const string WiringHelp =
            "Assigned by the scene builder / Water Wizard. Leave as-is unless you know a reference changed.";
        const string BedSourceHelp =
            "The terrain read as this body's floor. What the bed DRIVES is drawn where it belongs: " +
            "deep colour + clarity in Volume, surf fronts in Motion, bake resolution in Budget - each " +
            "greyed out until this is on.";
    }
}
#endif
