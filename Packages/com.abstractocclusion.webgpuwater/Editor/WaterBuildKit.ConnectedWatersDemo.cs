// WebGpuWater build kit - one-click CONNECTED WATERS test rig (2026-08-29 domain/topology round).
//
// Builds, into the open scene, the exact situations the new 3D-domain layer exists for, so they
// can be eyeballed in one Play press:
//   - a sloped RIVER ribbon flowing into a LAKE through a ported WaterConnection (seam blend),
//   - a POND stacked directly above a SEWER at the same XZ (elevation-resolved domains),
//   - an EXCLUSION box carved into the lake (no float/splash/ripples inside),
//   - buoyant crates dropped over each case.
// A scene-object creator, so it lives on the GameObject menu beside the exclusion-volume
// creator (the Window/ MenuRoot hosts tool windows). Reuses the wizard's generators end to end.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string ConnectedDemoMenuPath = "GameObject/AbstractOcclusion/Connected Waters Test Rig";
        const int ConnectedDemoMenuPriority = 11; // right under the exclusion-volume creator
        const string ConnectedDemoRootName = "Connected Waters Test Rig";

        // ---- lake (primary, the river's receiving body) ----
        static readonly Vector3 LakeCenter = Vector3.zero;
        static readonly Vector3 LakeExtent = new Vector3(8f, 2f, 8f);

        // ---- stacked pair: same XZ, different elevations (the founding 3D-domain case) ----
        static readonly Vector3 PondCenter = new Vector3(14f, 6f, 0f);
        static readonly Vector3 SewerCenter = new Vector3(14f, 0f, 0f);
        static readonly Vector3 StackedExtent = new Vector3(4f, 1.5f, 4f);

        // ---- river: three knots descending onto the lake's -Z edge ----
        static readonly Vector3 RiverKnotHigh = new Vector3(-3f, 4.2f, -24f);
        static readonly Vector3 RiverKnotMid = new Vector3(-1f, 2.2f, -16f);
        static readonly Vector3 RiverKnotMouth = new Vector3(0f, 0.1f, -9f);
        static readonly Vector3 RiverKnotTangent = new Vector3(0.6f, -0.6f, 2.6f);
        const float RiverWidthMeters = 3f;
        const float RiverSpeedMetersPerSecond = 1.5f;

        // ---- the seam: one port on the river mouth, one on the lake edge ----
        static readonly Vector3 RiverPortPosition = new Vector3(0f, 0.1f, -9.5f);
        static readonly Vector3 LakePortPosition = new Vector3(0f, 0f, -8f);
        const float SeamTransitionRadiusMeters = 3f;

        // ---- exclusion carve inside the lake ----
        static readonly Vector3 CarvePosition = new Vector3(2f, -0.5f, 2f);
        static readonly Vector3 CarveSize = new Vector3(2f, 2f, 2f);

        // ---- crates: open lake / inside the carve / into the pond / the air gap ----
        const float CrateSize = 0.5f;
        static readonly Vector3[] CrateDropPositions =
        {
            new Vector3(-2f, 1f, 0f),   // open lake: floats at 0
            new Vector3(2f, 1f, 2f),    // over the carve: must NOT float or splash - sinks to the floor
            new Vector3(14f, 7.5f, 0f), // over the pond: floats at 6, never at the sewer's 0
            new Vector3(14f, 3f, 0f),   // the air gap: no water until it falls into the sewer domain
        };

        // Floor under everything so sunk props (the carve crate) rest instead of falling forever.
        static readonly Vector3 FloorCenter = new Vector3(4f, -2.2f, -4f);
        static readonly Vector3 FloorSize = new Vector3(44f, 0.2f, 44f);

        [MenuItem(ConnectedDemoMenuPath, false, ConnectedDemoMenuPriority)]
        static void CreateConnectedWatersRig()
        {
            int undoGroup = Undo.GetCurrentGroup();
            var root = NewUndoableGameObject(ConnectedDemoRootName);

            if (!CreateContext(root.transform, out BuildContext ctx, CreateUniqueWaterFolder()))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            // Bodies: lean (no pool walls / god rays / foam) - this rig is for QUERY behaviour.
            WaterVolume lake = CreateWaterBody(ctx, root.transform, "Lake", LakeCenter, LakeExtent,
                                               primary: true, withPool: false, withGodRays: false,
                                               withFoamParticles: false, withSplash: true);
            WaterVolume pond = CreateWaterBody(ctx, root.transform, "Pond (stacked above)", PondCenter,
                                               StackedExtent, primary: false, withPool: false,
                                               withGodRays: false, withFoamParticles: false,
                                               withSplash: true);
            CreateWaterBody(ctx, root.transform, "Sewer (stacked below)", SewerCenter,
                            StackedExtent, primary: false, withPool: false, withGodRays: false,
                            withFoamParticles: false, withSplash: true);

            WaterRiverSurface river = CreateDemoRiver(root.transform, lake, ctx);
            CreateDemoSeam(root.transform, river, lake);

            var carve = NewUndoableGameObject("Lake Carve (dry room)");
            carve.transform.SetParent(root.transform);
            carve.transform.position = CarvePosition;
            carve.AddComponent<WaterExclusionVolume>().size = CarveSize;

            CreateFloorCollider(root.transform, FloorCenter, FloorSize);
            for (int i = 0; i < CrateDropPositions.Length; i++)
                CreateDemoCrate(root.transform, CrateDropPositions[i], i);

            Selection.activeGameObject = root;
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[WebGpuWater] Connected Waters Test Rig built. Press Play and check: " +
                      "(1) the river ribbon slopes into the lake and the crossing has no height/flow step, " +
                      "(2) the pond crate floats at the POND's surface, never the sewer's, " +
                      "(3) the air-gap crate falls until it reaches the sewer's water, " +
                      "(4) the crate over the carve sinks silently - no splash, no ripples, no lift.");
        }

        // River rig: spline + current field beside it, ribbon surface driven by the lake's
        // animated uniforms. Wired exactly as a hand-authored river would be, so the rig also
        // documents the authoring recipe.
        static WaterRiverSurface CreateDemoRiver(Transform parent, WaterVolume lake, BuildContext ctx)
        {
            var riverGO = NewUndoableGameObject("River (spline ribbon)");
            riverGO.transform.SetParent(parent);

            var spline = riverGO.AddComponent<WaterRiverSpline>();
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(RiverKnotHigh, RiverKnotTangent, RiverWidthMeters,
                                   RiverSpeedMetersPerSecond),
                new WaterRiverKnot(RiverKnotMid, RiverKnotTangent, RiverWidthMeters,
                                   RiverSpeedMetersPerSecond),
                new WaterRiverKnot(RiverKnotMouth, RiverKnotTangent, RiverWidthMeters,
                                   RiverSpeedMetersPerSecond),
            };

            var currentField = riverGO.AddComponent<WaterRiverCurrentField>();
            currentField.Configure(spline);
            // The lake reads the river's current near the mouth through its own field list, so
            // in-lake samples by the seam inherit the inflow push.
            lake.currentFields = new WaterCurrentField[] { currentField };

            var surface = riverGO.AddComponent<WaterRiverSurface>();
            surface.Configure(spline, lake, ctx.MatAbove, WaterRiverSurface.DefaultSamplesPerSegment);
            EditorUtility.SetDirty(lake);
            return surface;
        }

        // The seam: a port on each side + the connection. Port forwards point downstream (+Z,
        // river into lake) so the authored flow direction reads correctly in gizmos/queries.
        static void CreateDemoSeam(Transform parent, WaterRiverSurface river, WaterVolume lake)
        {
            var riverPortGO = NewUndoableGameObject("Port - River Mouth");
            riverPortGO.transform.SetParent(parent);
            riverPortGO.transform.SetPositionAndRotation(RiverPortPosition, Quaternion.identity);
            var riverPort = riverPortGO.AddComponent<WaterConnectionPort>();
            riverPort.river = river;
            riverPort.authoredFlowRate = RiverWidthMeters * RiverSpeedMetersPerSecond;

            var lakePortGO = NewUndoableGameObject("Port - Lake Edge");
            lakePortGO.transform.SetParent(parent);
            lakePortGO.transform.SetPositionAndRotation(LakePortPosition, Quaternion.identity);
            var lakePort = lakePortGO.AddComponent<WaterConnectionPort>();
            lakePort.body = lake;

            var connectionGO = NewUndoableGameObject("Connection - River to Lake");
            connectionGO.transform.SetParent(parent);
            var connection = connectionGO.AddComponent<WaterConnection>();
            connection.portA = riverPort;
            connection.portB = lakePort;
            connection.transitionRadiusMeters = SeamTransitionRadiusMeters;
            connection.authoredFlowRate = riverPort.authoredFlowRate;
        }

        // A crate with the wizard's full floatable set (defaults; no preset table needed here):
        // it floats, displaces, splashes and is lit by whichever body's domain claims it.
        static void CreateDemoCrate(Transform parent, Vector3 position, int index)
        {
            GameObject crate = CreateBuoyantObjectBody(FloaterShape.Cube, null, CrateSize);
            if (crate == null) return;
            crate.name = $"Crate {index + 1}";
            crate.transform.SetParent(parent);
            crate.transform.position = position;
            crate.AddComponent<Rigidbody>();
            crate.AddComponent<WaterInteractable>();
            crate.AddComponent<WaterBuoyancy>();
            crate.AddComponent<WaterSplash>();
            crate.AddComponent<WaterMembership>();
        }
    }
}
