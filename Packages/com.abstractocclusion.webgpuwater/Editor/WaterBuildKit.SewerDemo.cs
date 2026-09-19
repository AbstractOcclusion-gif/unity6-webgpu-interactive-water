// WebGpuWater build kit - one-click SEWER water system for the TB_ForgottenSewers Demo_FP scene
// (2026-09-19). Project-local helper: NOT part of the shipped package - do not copy it back to
// the package repo.
//
// Builds, into the open scene, the connected waters measured from the scene geometry:
//   - HALL  : the flooded column hall (Module_02_FP), a 0.31 m film over the flat floor at Y 0,
//   - BASIN : the Module_03_FP basin (floor Y -3, surface Y -1.79, 1.2 m deep) - the primary body,
//   - SHAFTS: one body under the Module_01_FP floor slab, visible through its three grated shafts,
//   - ROOM  : the north half of the Tunnel_B room the hall opens into (same 0.31 m film); a plain
//     pond that also parents the stream, so the ribbon gets the dense film fog,
//   - STREAM: tunnel room east opening -> the Z=43 corridor -> the 2 m drain that drops into the
//     basin's north border (a facade-generated ported connection at each end), with its OWN
//     reflection probe (WaterRiverSurface.reflectionProbe),
//   - hand-authored port-to-port WaterConnections: SHAFTS -> BASIN (underground, topology only)
//     and HALL -> ROOM (one film split at Z 38 because a footprint is a rectangle),
//   - one baked reflection probe per body, linked as its reflection base.
// It composes the kit's EXISTING recipes (BuildWaterSystem, CreateConnectedRiver); only the
// numbers below are sewer-specific. The TB flat water planes are deactivated, not deleted.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string SewerDemoMenuPath = GameObjectMenuRoot + "Sewer Water System (Demo_FP)";
        const int SewerDemoMenuPriority = 14; // after the river creator and the stress rig
        const string SewerRootName = "Sewer Water System";
        const string SewerUndoName = "Build Sewer Water System";

        // ---- bodies (centre = rest plane, extent = half-size X/Z + depth Y; world metres) ----
        const int SewerBasinIndex = 0;
        const int SewerHallIndex = 1;
        const int SewerShaftsIndex = 2;
        const int SewerRoomIndex = 3;
        const string SewerBasinName = "Sewer Basin (Module_03)";
        const string SewerHallName = "Sewer Hall Film (Module_02)";
        const string SewerShaftsName = "Sewer Shafts (Module_01)";
        const string SewerRoomName = "Sewer Tunnel Room (Tunnel_B)";
        // Basin: open water X -47..-23, Z 27..38, floor -3, TB surface -1.79.
        static readonly Vector3 SewerBasinCenter = new Vector3(-35f, -1.79f, 32.5f);
        static readonly Vector3 SewerBasinExtent = new Vector3(12f, 1.2f, 5.5f);
        // Hall: the TB plane's footprint trimmed to the walls, X -128..-78, Z -18..38, floor 0.
        static readonly Vector3 SewerHallCenter = new Vector3(-103f, 0.31f, 10f);
        static readonly Vector3 SewerHallExtent = new Vector3(25f, 0.31f, 28f);
        // Shafts: three 4.5 m shafts at X -79/-71/-63, Z 62 through a 1 m slab; TB surface -0.26.
        static readonly Vector3 SewerShaftsCenter = new Vector3(-71f, -0.26f, 62f);
        static readonly Vector3 SewerShaftsExtent = new Vector3(11.5f, 1f, 2.6f);

        // Tunnel room: the north half of the Tunnel_B room (wall to wall X -99.5..-92.5, hall border
        // Z 38 to the rubble end Z 53.5), same 0.31 film as the hall it opens into. A plain small
        // pond, so it is also the stream's PARENT: the ribbon takes its medium (dense sewer fog)
        // from here instead of from the far basin or the windowed hall.
        static readonly Vector3 SewerRoomCenter = new Vector3(-96f, 0.31f, 45.75f);
        static readonly Vector3 SewerRoomExtent = new Vector3(3.5f, 0.31f, 7.75f);
        // Film-depth water only reads as murky with a dense medium: a 0.3 m column at the wizard's
        // 0.2 absorbs a few percent. Hall + room (and so the stream) share this; the basin keeps 0.2.
        const float SewerFilmFogDensity = 2f;

        // ---- hall sim window (Bert's tuning, 2026-09-19): the 50 x 56 m film runs the camera-following
        // ripple window (Lake archetype, threshold 20 m, 8 m half-size). The hall is ENCLOSED, so the
        // window is clamped to the footprint: unclamped, its centre follows the camera to the border
        // and half the patch (8 m) draws water past the walls, over Tunnel_B and the stream.
        const float SewerHallWindowThresholdMeters = 20f;
        const float SewerHallWindowHalfSizeMeters = 8f;

        // ---- stream: per-knot tangents (mirrored Bezier handles), so the plan's one-tangent
        // river row cannot express it - the knots go straight to CreateConnectedRiver.
        const string SewerStreamName = "Sewer Stream (tunnel room to basin)";
        const float SewerStreamSeamRadiusMeters = 1.5f;
        const float SewerCorridorWidthMeters = 2.5f; // kerb-to-kerb clear width is ~3 m
        const float SewerDrainWidthMeters = 1.9f;    // the drain slot is 2 m wide
        const float SewerCorridorSpeed = 0.8f;
        const float SewerChuteSpeed = 2.5f;
        static List<WaterRiverKnot> SewerStreamKnots() => new List<WaterRiverKnot>
        {
            // tunnel room EAST border (X -92.5) ON its rest plane, centred in the wall opening
            // (Z 41.5..44.5) that leads into the Z=43 corridor
            new WaterRiverKnot(new Vector3(-92.5f, 0.31f, 43f), new Vector3(3f, 0f, 0f),
                               SewerCorridorWidthMeters, SewerCorridorSpeed),
            new WaterRiverKnot(new Vector3(-62f, 0.2f, 43f), new Vector3(8f, 0f, 0f),
                               SewerCorridorWidthMeters, SewerCorridorSpeed),
            // Module_03 walkway is open here (Z 39..47): swing north so the right turn into the
            // drain is a 2.2 m-radius quarter circle (handle = 0.5523 x R) - a tighter bend folds
            // the ribbon. Stays clear of the column at (-34, 45.5).
            new WaterRiverKnot(new Vector3(-40f, 0.14f, 43f), new Vector3(2.2f, 0f, 0f),
                               SewerDrainWidthMeters, SewerCorridorSpeed),
            new WaterRiverKnot(new Vector3(-32.2f, 0.12f, 44.2f), new Vector3(1.2f, 0f, 0f),
                               SewerDrainWidthMeters, SewerCorridorSpeed),
            // lip of the drain slot (X -31..-29, Z 38..42), now heading -Z
            new WaterRiverKnot(new Vector3(-30f, 0.08f, 42f), new Vector3(0f, -0.1f, -1.2f),
                               SewerDrainWidthMeters, SewerChuteSpeed),
            // basin north border (Z 38) ON its rest plane - the border authoring rule
            new WaterRiverKnot(new Vector3(-30f, -1.79f, 38f), new Vector3(0f, -0.4f, -1.2f),
                               SewerDrainWidthMeters, SewerChuteSpeed),
        };

        // ---- underground link: shafts east border -> basin north-west border. The seam slab sits
        // at the port midpoint (dry rock), so the blend never touches either body: topology only.
        const string SewerUndergroundName = "Underground Link (shafts to basin)";
        const string SewerUndergroundPortAName = "Port (shafts)";
        const string SewerUndergroundPortBName = "Port (basin)";
        const float SewerUndergroundSeamRadiusMeters = 0.5f;
        const float SewerUndergroundFlowRate = 0.5f; // m3/s, advisory
        static readonly Vector3 SewerShaftsPortPosition = new Vector3(-59.5f, -0.26f, 62f);
        static readonly Vector3 SewerBasinPortPosition = new Vector3(-47f, -1.79f, 36f);
        // ---- hall <-> tunnel room: one continuous film split at Z 38 only because a body footprint is
        // a rectangle. Coincident ports on the shared border (the seam normal then comes from the
        // ports' authored flow direction); both surfaces rest at 0.31, so the blend is a no-op.
        const string SewerRoomLinkName = "Film Link (hall to tunnel room)";
        const string SewerRoomLinkPortAName = "Port (hall)";
        const string SewerRoomLinkPortBName = "Port (tunnel room)";
        const float SewerRoomLinkSeamRadiusMeters = 1f;
        const float SewerRoomLinkFlowRate = 2f; // m3/s, advisory (= stream width x speed)
        static readonly Vector3 SewerRoomLinkPortPosition = new Vector3(-96f, 0.31f, 38f);
        static readonly Vector3 SewerRoomLinkFlow = Vector3.forward;

        // ---- reflection probes: one per water zone, baked once at build time (the sewer is static
        // and Custom/baked probes work on every quality tier, unlike realtime ones), then linked as
        // the body's reflection BASE (Reflect URP Probe + explicit probe). The stream inherits the
        // basin's through its parent body. Probes sit in open air above the surface, clear of
        // columns/grates; the Water layer is culled so no water bakes into its own reflection.
        const string SewerProbeFolderName = "ReflectionProbes";
        const string SewerProbeNameSuffix = " Reflection Probe";
        const int SewerProbeResolution = 256;
        const float SewerProbeNearClip = 0.1f;
        const float SewerProbeFarClip = 120f;
        static readonly Vector3 SewerBasinProbePosition = new Vector3(-35f, -0.3f, 32.5f);
        static readonly Vector3 SewerBasinProbeSize = new Vector3(26f, 10f, 14f);
        static readonly Vector3 SewerHallProbePosition = new Vector3(-103f, 2.5f, 15f);
        static readonly Vector3 SewerHallProbeSize = new Vector3(52f, 8f, 58f);
        static readonly Vector3 SewerShaftsProbePosition = new Vector3(-71f, 1.5f, 62f);
        static readonly Vector3 SewerShaftsProbeSize = new Vector3(24f, 8f, 8f);
        static readonly Vector3 SewerRoomProbePosition = new Vector3(-96f, 2f, 45f);
        static readonly Vector3 SewerRoomProbeSize = new Vector3(7f, 8f, 16f);
        // The stream's OWN probe (WaterRiverSurface.reflectionProbe): mid Z=43 corridor, so the
        // ribbon reflects the tunnel it runs through instead of its parent's room.
        static readonly Vector3 SewerStreamProbePosition = new Vector3(-75f, 1.5f, 43f);
        static readonly Vector3 SewerStreamProbeSize = new Vector3(64f, 6f, 4f);
        const string ReflectUrpProbePropertyPath = "reflectionSettings.reflectUrpProbe";
        const string ReflectionProbePropertyPath = "reflectionSettings.reflectionProbe";

        // ---- TB water planes this system replaces (horizontal quads only: Module_01 also has a
        // VERTICAL quad with the Water material, which is left alone).
        static readonly string[] SewerLegacyWaterMaterialNames = { "Water", "Water_FP1", "Water_FP2" };
        const float SewerHorizontalDotThreshold = 0.9f;

        [MenuItem(SewerDemoMenuPath, false, SewerDemoMenuPriority)]
        static void CreateSewerWaterSystem()
        {
            Undo.SetCurrentGroupName(SewerUndoName);
            int undoGroup = Undo.GetCurrentGroup();

            GameObject existingRoot = GameObject.Find(SewerRootName);
            if (existingRoot != null && existingRoot.scene.IsValid())
                Undo.DestroyObjectImmediate(existingRoot);

            WaterSystemPlan plan = SewerWaterPlan();
            string systemFolder = plan.SystemFolder;
            EnsureFolder(systemFolder);
            var root = NewUndoableGameObject(SewerRootName);
            // Asset half only: an existing gameplay scene keeps its own camera and lights
            // (bodies resolve Camera.main and the sun at play time).
            if (!TryBuildSharedAssets(systemFolder, buildPoolMaterial: false, out BuildContext ctx))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            try
            {
                WaterSystemBuildResult built = BuildWaterSystem(plan, ctx, root.transform);
                WaterVolume basin = built.Bodies[SewerBasinIndex];
                WaterVolume hall = built.Bodies[SewerHallIndex];
                WaterVolume shafts = built.Bodies[SewerShaftsIndex];

                WaterVolume room = built.Bodies[SewerRoomIndex];

                ConfigureSewerHallWindow(hall);
                ApplySewerFilmFog(hall);
                ApplySewerFilmFog(room);
                WaterRiver stream = CreateConnectedRiver(
                    root.transform, SewerStreamName, SewerStreamKnots(), parentBody: room,
                    ctx.MatAbove, ctx.MatUnder, sourceBody: room, mouthBody: basin,
                    upstreamRiver: null, transitionRadiusMeters: SewerStreamSeamRadiusMeters,
                    withProceduralFoam: true);
                CreateSewerBodyLink(root.transform, SewerUndergroundName,
                                    SewerUndergroundPortAName, SewerShaftsPortPosition, shafts,
                                    SewerUndergroundPortBName, SewerBasinPortPosition, basin,
                                    SewerBasinPortPosition - SewerShaftsPortPosition,
                                    SewerUndergroundSeamRadiusMeters, SewerUndergroundFlowRate);
                CreateSewerBodyLink(root.transform, SewerRoomLinkName,
                                    SewerRoomLinkPortAName, SewerRoomLinkPortPosition, hall,
                                    SewerRoomLinkPortBName, SewerRoomLinkPortPosition, room,
                                    SewerRoomLinkFlow,
                                    SewerRoomLinkSeamRadiusMeters, SewerRoomLinkFlowRate);
                int hidden = DeactivateLegacySewerWater();
                // After the TB planes are hidden, so they never bake into the cubemaps.
                LinkSewerBodyProbe(basin, SewerBasinProbePosition, SewerBasinProbeSize, systemFolder);
                LinkSewerBodyProbe(hall, SewerHallProbePosition, SewerHallProbeSize, systemFolder);
                LinkSewerBodyProbe(shafts, SewerShaftsProbePosition, SewerShaftsProbeSize, systemFolder);
                LinkSewerBodyProbe(room, SewerRoomProbePosition, SewerRoomProbeSize, systemFolder);
                WaterRiverSurface streamSurface = stream.GetComponent<WaterRiverSurface>();
                streamSurface.reflectionProbe = CreateSewerReflectionProbe(
                    stream.transform, SewerStreamName, SewerStreamProbePosition, SewerStreamProbeSize,
                    systemFolder);
                EditorUtility.SetDirty(streamSurface);

                Selection.activeGameObject = built.Primary.gameObject;
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log(LogPrefix + $"'{SewerRootName}' built: 4 bodies, 1 stream, 2 body " +
                          $"links, 5 linked reflection probes; {hidden} TB water plane(s) deactivated; assets under '{systemFolder}'.");
            }
            catch (System.Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                Debug.LogException(exception);
            }
        }

        static WaterSystemPlan SewerWaterPlan()
        {
            var plan = new WaterSystemPlan
            {
                systemName = SewerRootName,
                primaryBodyIndex = SewerBasinIndex,
                fogOnAllBodies = false,
                splash = true,
                qualitySource = WaterQualitySource.PackageDefault,
            };
            plan.bodies.Add(DemoBody(SewerBasinName, WaterKind.SurfaceWithFog, SewerBasinCenter, SewerBasinExtent));
            plan.bodies.Add(DemoBody(SewerHallName, WaterKind.SurfaceOnly, SewerHallCenter, SewerHallExtent));
            plan.bodies.Add(DemoBody(SewerShaftsName, WaterKind.SurfaceOnly, SewerShaftsCenter, SewerShaftsExtent));
            plan.bodies.Add(DemoBody(SewerRoomName, WaterKind.SurfaceOnly, SewerRoomCenter, SewerRoomExtent));
            return plan;
        }

        static void ConfigureSewerHallWindow(WaterVolume hall)
        {
            var serialized = new SerializedObject(hall);
            serialized.FindProperty(WaterVolumePropertyPaths.BodyType).enumValueIndex =
                (int)WaterVolume.WaterBodyType.Lake;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            hall.enableLargeBodyWindow = true;
            hall.largeBodyThreshold = SewerHallWindowThresholdMeters;
            hall.simWindowMeters = SewerHallWindowHalfSizeMeters;
            hall.clampWindowToShore = true;
            EditorUtility.SetDirty(hall);
        }

        // Film-depth bodies: fog on at the dense sewer value (EnsureUnderwaterFog's two fields).
        static void ApplySewerFilmFog(WaterVolume body)
        {
            body.FogSettings.waterFog = true;
            body.FogSettings.fogDensity = SewerFilmFogDensity;
            EditorUtility.SetDirty(body);
        }

        // A hand-authored (body-to-body) connection: two ports + the connection, flow A -> B.
        static void CreateSewerBodyLink(Transform root, string linkName,
                                        string portAName, Vector3 portAPosition, WaterVolume upstream,
                                        string portBName, Vector3 portBPosition, WaterVolume downstream,
                                        Vector3 flow, float seamRadiusMeters, float flowRate)
        {
            var linkGO = NewUndoableGameObject(linkName);
            linkGO.transform.SetParent(root);

            WaterConnectionPort portA = CreateSewerPort(linkGO.transform, portAName, portAPosition, flow, upstream);
            WaterConnectionPort portB = CreateSewerPort(linkGO.transform, portBName, portBPosition, flow, downstream);
            // Wire BEFORE the connection's OnEnable runs its half-wired warning.
            linkGO.SetActive(false);
            var connection = linkGO.AddComponent<WaterConnection>();
            connection.portA = portA;
            connection.portB = portB;
            connection.transitionRadiusMeters = seamRadiusMeters;
            connection.authoredFlowRate = flowRate;
            linkGO.SetActive(true);
            EditorUtility.SetDirty(connection);
        }

        static WaterConnectionPort CreateSewerPort(Transform parent, string name, Vector3 position,
                                                   Vector3 flowDirection, WaterVolume body)
        {
            var portGO = NewUndoableGameObject(name);
            portGO.transform.SetParent(parent);
            portGO.transform.SetPositionAndRotation(
                position, Quaternion.LookRotation(flowDirection.normalized, Vector3.up));
            var port = portGO.AddComponent<WaterConnectionPort>();
            port.body = body;
            port.EnsurePortId();
            EditorUtility.SetDirty(port);
            return port;
        }

        // One probe under the body's root object, baked to the system folder and linked on the body.
        // A failed bake degrades to a realtime OnAwake probe (needs Realtime Reflection Probes on
        // the active quality level) instead of leaving the body on the sky cubemap.
        static ReflectionProbe CreateSewerReflectionProbe(Transform owner, string ownerName,
                                                          Vector3 position, Vector3 size,
                                                          string systemFolder)
        {
            var probeGO = NewUndoableGameObject(ownerName + SewerProbeNameSuffix);
            probeGO.transform.SetParent(owner);
            probeGO.transform.position = position;

            var probe = probeGO.AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
            probe.renderDynamicObjects = true; // the TB meshes are not flagged static
            probe.size = size;
            probe.resolution = SewerProbeResolution;
            probe.hdr = true;
            probe.nearClipPlane = SewerProbeNearClip;
            probe.farClipPlane = SewerProbeFarClip;
            int waterLayer = LayerMask.NameToLayer(WaterVolume.WaterLayerName);
            if (waterLayer >= 0) probe.cullingMask = ~(1 << waterLayer);

            string probeFolder = systemFolder + "/" + SewerProbeFolderName;
            EnsureFolder(probeFolder);
            string cubemapPath = probeFolder + "/" + ownerName + ".exr";
            Cubemap baked = null;
            if (Lightmapping.BakeReflectionProbe(probe, cubemapPath))
            {
                AssetDatabase.ImportAsset(cubemapPath, ImportAssetOptions.ForceUpdate);
                baked = AssetDatabase.LoadAssetAtPath<Cubemap>(cubemapPath);
            }
            if (baked != null) probe.customBakedTexture = baked;
            else
            {
                Debug.LogWarning(LogPrefix + $"reflection probe bake failed for '{ownerName}'; " +
                                 "falling back to a realtime OnAwake probe.", probe);
                probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.OnAwake;
            }
            EditorUtility.SetDirty(probe);
            return probe;
        }

        // A body's probe: created under the body's root object, then linked as its reflection base.
        static void LinkSewerBodyProbe(WaterVolume body, Vector3 position, Vector3 size, string systemFolder)
        {
            Transform bodyRoot = body.transform.parent;
            ReflectionProbe probe = CreateSewerReflectionProbe(bodyRoot, bodyRoot.name, position, size,
                                                               systemFolder);
            var serialized = new SerializedObject(body);
            serialized.FindProperty(ReflectUrpProbePropertyPath).boolValue = true;
            serialized.FindProperty(ReflectionProbePropertyPath).objectReferenceValue = probe;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(body);
        }

        // Deactivate (never delete) the TB flat water quads the bodies replace. Undoable; on a
        // prefab instance this is a plain m_IsActive scene override.
        static int DeactivateLegacySewerWater()
        {
            int hidden = 0;
            MeshRenderer[] renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                Material material = renderer.sharedMaterial;
                if (material == null ||
                    System.Array.IndexOf(SewerLegacyWaterMaterialNames, material.name) < 0)
                    continue;
                if (Mathf.Abs(Vector3.Dot(renderer.transform.up, Vector3.up)) < SewerHorizontalDotThreshold)
                    continue;
                Undo.RecordObject(renderer.gameObject, SewerUndoName);
                renderer.gameObject.SetActive(false);
                hidden++;
            }
            return hidden;
        }
    }
}
