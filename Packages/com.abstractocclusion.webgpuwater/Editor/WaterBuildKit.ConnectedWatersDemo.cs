// WebGpuWater build kit - one-click CONNECTED WATERS test rig (2026-08-29 domain/topology round;
// v2 same day: a MULTI-BODY CONNECTED CHAIN; v3 same day: REAL GAPS between the bodies, so both
// rivers span open air and the underwater connection - gameplay, stitch and fog - is exercised
// end to end, not hidden by overlapping footprints; v4 adds a real coastal Terrain whose incised
// estuary shoals the unbounded ocean before its waves reach the elevated inland water).
//
// Builds, into the open scene, the exact situations the domain/topology layer exists for, so
// they can be eyeballed in one Play press:
//   - a CHAIN of connected waters: RESERVOIR -> upper river -> lower river -> LAKE -> coastal
//     river -> unbounded OCEAN, plus LAKE <- spillway <- POND. Every seam is a facade-generated
//     ported WaterConnection (7 connections; the lake touches 3 - the topology enumeration case),
//   - a POND stacked directly above a SEWER at the same XZ (elevation-resolved domains),
//   - an EXCLUSION box carved into the lake (no float/splash/ripples inside),
//   - buoyant crates dropped over each case.
// A scene-object creator, so it lives on the GameObject menu beside the exclusion-volume
// creator (the Window/ MenuRoot hosts tool windows). Reuses the wizard's generators end to end.
using System;
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

        // ---- lake (primary, the chain's receiving body) ----
        static readonly Vector3 LakeCenter = Vector3.zero;
        static readonly Vector3 LakeExtent = new Vector3(8f, 2f, 8f);

        // ---- stacked pair: same XZ, different elevations (the founding 3D-domain case).
        // v3: moved OUT to x = 22 so a ~10 m air gap separates the pond from the lake and the
        // spillway genuinely bridges two waters instead of grazing overlapping footprints.
        static readonly Vector3 PondCenter = new Vector3(22f, 6f, 0f);
        static readonly Vector3 SewerCenter = new Vector3(22f, 0f, 0f);
        static readonly Vector3 StackedExtent = new Vector3(4f, 1.5f, 4f);

        // ---- reservoir: the uplands body FEEDING the main river's source (v3: pushed further
        // upstream so the river crosses ~19 m of open air before entering the lake) ----
        static readonly Vector3 ReservoirCenter = new Vector3(-3f, 4.2f, -32f);
        static readonly Vector3 ReservoirExtent = new Vector3(5f, 1.5f, 5f);

        // ---- ocean: deliberately lower than the inland lake. The coastal terrain keeps its
        // infinite clipmap out of the elevated channel; this rectangular extent is bookkeeping
        // only, and the river mouth must follow the authored tangent, never it.
        const float OceanWaterLevel = -2f;
        static readonly Vector3 OceanCenter = new Vector3(0f, OceanWaterLevel, 32f);
        static readonly Vector3 OceanExtent = new Vector3(16f, 3f, 16f);

        // ---- coast: a real Unity Terrain because the shore field bakes Terrain.SampleHeight,
        // not colliders or display meshes. Dry banks keep the infinite ocean out of the elevated
        // river reach; the incised channel opens into a shallow fan, then the bed drops offshore.
        const string CoastTerrainName = "Coastal Terrain (ocean shoal)";
        const string CoastTerrainAssetFileName = "Connected Coast Terrain.asset";
        const int CoastTerrainHeightmapResolution = 129;
        static readonly Vector3 CoastTerrainOrigin = new Vector3(-28f, -8f, 6f);
        static readonly Vector3 CoastTerrainSize = new Vector3(56f, 10f, 50f);
        const float CoastLandHeight = 0.4f;
        const float CoastShelfDescentStartZ = 25f;
        const float CoastShelfDescentEndZ = 44f;
        const float CoastDeepBedHeight = -7.5f;
        const float EstuaryUpstreamClearance = 0.45f;
        const float EstuaryOceanExclusionClearance = 0.05f;
        const float EstuaryDryBedHeight = OceanWaterLevel + EstuaryOceanExclusionClearance;
        const float EstuaryDropStartZ = 21.5f;
        const float EstuaryMouthDepth = 0.7f;
        const float EstuaryMouthBedHeight = OceanWaterLevel - EstuaryMouthDepth;
        const float EstuaryFanEndZ = 42f;
        const float EstuaryFanHalfWidth = 12f;
        const float EstuaryBankFeather = 2.5f;
        const float EstuaryFanFeather = 4f;

        // ---- main river: split at an open-air junction so the rig exercises a TRUE shared-row
        // river-to-river stitch as well as the two river-to-body border seams. THE BORDER
        // AUTHORING RULE (v5): a terminal knot sits ON its body's footprint border AND rest
        // plane. The generator makes that exact even if authored data drifts, then conforms the
        // preceding rows to the border frame. No receiving-body pixels are carved.
        static readonly Vector3 RiverKnotHigh = new Vector3(-3f, 4.2f, -27f); // reservoir z border
        static readonly Vector3 RiverKnotUpperMid = new Vector3(-2f, 3.1f, -22f);
        static readonly Vector3 RiverKnotJunction = new Vector3(-1f, 2.2f, -17f);
        static readonly Vector3 RiverKnotLowerMid = new Vector3(-0.5f, 1.0f, -12f);
        static readonly Vector3 RiverKnotMouth = new Vector3(0f, 0f, -8f); // lake z border
        static readonly Vector3 RiverKnotTangent = new Vector3(0.6f, -0.6f, 2.6f);
        const float RiverWidthMeters = 3f;
        const float RiverSpeedMetersPerSecond = 1.5f;

        // ---- coastal outlet: lake border to an authored point in the unbounded ocean. The last
        // transition radius becomes a visible river apron over the camera-following ocean sheet.
        static readonly Vector3 CoastKnotSource = new Vector3(0f, 0f, 8f);
        static readonly Vector3 CoastKnotMid = new Vector3(0.5f, OceanWaterLevel * 0.5f, 16f);
        static readonly Vector3 CoastKnotMouth = new Vector3(0f, OceanWaterLevel, 24f);
        static readonly Vector3 CoastKnotTangent = new Vector3(0f, -0.35f, 3f);
        const float CoastRiverWidthMeters = 4f;
        const float CoastRiverSpeedMetersPerSecond = 1.25f;

        // ---- spillway: the chute draining the POND into the lake's NE corner - the second
        // inflow that makes the lake a multi-connection body. Terminal knots follow the same
        // border rule (top on the pond's x border under its 6, mouth on the lake's x border
        // under its 0); v3: the run crosses the ~10 m air gap, so mid-chute there is river with
        // NO body underneath.
        static readonly Vector3 SpillKnotTop = new Vector3(18f, 6f, 1f);
        static readonly Vector3 SpillKnotMid = new Vector3(13f, 3.0f, 4f);
        static readonly Vector3 SpillKnotMouth = new Vector3(8f, 0f, 6.5f);
        static readonly Vector3 SpillKnotTangent = new Vector3(-2f, -1f, 1f);
        const float SpillwayWidthMeters = 2f;
        const float SpillwaySpeedMetersPerSecond = 2.5f;

        // ---- seams: generated by the WaterRiver facade at every connected end ----
        const float SeamTransitionRadiusMeters = 3f;

        // ---- exclusion carve inside the lake ----
        static readonly Vector3 CarvePosition = new Vector3(2f, -0.5f, 2f);
        static readonly Vector3 CarveSize = new Vector3(2f, 2f, 2f);

        // ---- crates, one per case ----
        const float CrateSize = 0.5f;
        static readonly Vector3[] CrateDropPositions =
        {
            new Vector3(-2f, 1f, 0f),      // open lake: floats at 0
            new Vector3(2f, 1f, 2f),       // over the carve: must NOT float or splash - sinks to the floor
            new Vector3(22f, 7.5f, 0f),    // over the pond: floats at 6, never at the sewer's 0
            new Vector3(22f, 3f, 0f),      // the air gap: no water until it falls into the sewer domain
            new Vector3(-3f, 5.5f, -32f),  // over the reservoir: floats at 4.2 (a THIRD elevation)
            new Vector3(-1f, 3.4f, -17f),  // over the shared-row river junction
            new Vector3(13f, 4.5f, 4f),    // onto the spillway MID-GAP: rides the ribbon over open air
            new Vector3(0f, 0.5f, 25f),    // over the ocean mouth apron and downstream outflow
        };

        // Inland floor for sunk props. It stops at the lake/coastal source: the real coastal
        // Terrain owns collisions from there onward and must remain the ocean's actual seabed.
        static readonly Vector3 FloorCenter = new Vector3(4f, -2.2f, -16.5f);
        static readonly Vector3 FloorSize = new Vector3(56f, 0.2f, 49f);

        [MenuItem(ConnectedDemoMenuPath, false, ConnectedDemoMenuPriority)]
        static void CreateConnectedWatersRig()
        {
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Rebuild Connected Waters Test Rig");
            GameObject existingRoot = GameObject.Find(ConnectedDemoRootName);
            if (existingRoot != null && existingRoot.scene.IsValid())
                Undo.DestroyObjectImmediate(existingRoot);
            var root = NewUndoableGameObject(ConnectedDemoRootName);

            if (!CreateContext(root.transform, out BuildContext ctx, CreateUniqueWaterFolder()))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            // Bodies stay lean (no pool walls, god rays or particle foam). Each river receives
            // procedural surface foam below so this rig also exercises the stitched render path.
            WaterVolume lake = CreateWaterBody(ctx, root.transform, "Lake", LakeCenter, LakeExtent,
                                               primary: true, withPool: false, withGodRays: false,
                                               withFoamParticles: false, withSplash: true);
            WaterVolume pond = CreateWaterBody(ctx, root.transform, "Pond (stacked above)", PondCenter,
                                               StackedExtent, primary: false, withPool: false,
                                               withGodRays: false, withFoamParticles: false,
                                               withSplash: true);
            WaterVolume sewer = CreateWaterBody(ctx, root.transform, "Sewer (stacked below)",
                            SewerCenter, StackedExtent, primary: false, withPool: false,
                            withGodRays: false, withFoamParticles: false, withSplash: true);
            WaterVolume reservoir = CreateWaterBody(ctx, root.transform, "Reservoir (uplands)",
                                                    ReservoirCenter, ReservoirExtent,
                                                    primary: false, withPool: false,
                                                    withGodRays: false, withFoamParticles: false,
                                                    withSplash: true);
            WaterVolume ocean = CreateWaterBody(ctx, root.transform, "Ocean (unbounded)",
                                                OceanCenter, OceanExtent, primary: false,
                                                withPool: false, withGodRays: false,
                                                withFoamParticles: false, withSplash: true);
            Terrain coastTerrain = CreateCoastalTerrain(root.transform, ctx.WaterFolder);
            ConfigureUnboundedOcean(ocean, coastTerrain);

            // Freshly created bodies default Water Fog OFF; this rig demos the underwater
            // CONNECTION (check 8), so every body is a fog medium. Fullscreen volume fog is
            // already on by default - only the master toggle needs flipping.
            EnableUnderwaterFog(lake, pond, sewer, reservoir, ocean);

            // The main run is deliberately two meshes. The lower source copies the upper mouth
            // row verbatim, so this is the regression case for RAM-style river sewing rather
            // than two ribbons hidden under one another.
            WaterRiver upperRiver = CreateConnectedRiver(
                root.transform, ctx, "Upper River (reservoir to junction)",
                new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(RiverKnotHigh, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                    new WaterRiverKnot(RiverKnotUpperMid, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                    new WaterRiverKnot(RiverKnotJunction, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                },
                parentBody: lake, sourceBody: reservoir, mouthBody: null);
            WaterRiver lowerRiver = CreateConnectedRiver(
                root.transform, ctx, "Lower River (junction to lake)",
                new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(RiverKnotJunction, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                    new WaterRiverKnot(RiverKnotLowerMid, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                    new WaterRiverKnot(RiverKnotMouth, RiverKnotTangent, RiverWidthMeters,
                                       RiverSpeedMetersPerSecond),
                },
                parentBody: lake, sourceBody: null, mouthBody: lake);
            lowerRiver.sourceEnd.upstreamRiver = upperRiver;
            lowerRiver.sourceEnd.transitionRadiusMeters = SeamTransitionRadiusMeters;
            WaterRiverEditor.GenerateEnd(lowerRiver, WaterRiverEndKind.Source);

            CreateConnectedRiver(root.transform, ctx, "Spillway (pond to lake)",
                new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(SpillKnotTop, SpillKnotTangent, SpillwayWidthMeters,
                                       SpillwaySpeedMetersPerSecond),
                    new WaterRiverKnot(SpillKnotMid, SpillKnotTangent, SpillwayWidthMeters,
                                       SpillwaySpeedMetersPerSecond),
                    new WaterRiverKnot(SpillKnotMouth, SpillKnotTangent, SpillwayWidthMeters,
                                       SpillwaySpeedMetersPerSecond),
                },
                parentBody: lake, sourceBody: pond, mouthBody: lake);

            CreateConnectedRiver(root.transform, ctx, "Coastal River (lake to ocean)",
                new List<WaterRiverKnot>
                {
                    new WaterRiverKnot(CoastKnotSource, CoastKnotTangent,
                                       CoastRiverWidthMeters,
                                       CoastRiverSpeedMetersPerSecond),
                    new WaterRiverKnot(CoastKnotMid, CoastKnotTangent,
                                       CoastRiverWidthMeters,
                                       CoastRiverSpeedMetersPerSecond),
                    new WaterRiverKnot(CoastKnotMouth, CoastKnotTangent,
                                       CoastRiverWidthMeters,
                                       CoastRiverSpeedMetersPerSecond),
                },
                // The ribbon inherits its upstream inland water. Parenting it to the ocean would
                // publish _LargeBody without a river shore field, applying unshoaled FFT waves to
                // the entire channel; the ocean remains the authored receiving body at the mouth.
                parentBody: lake, sourceBody: lake, mouthBody: ocean);

            var carve = NewUndoableGameObject("Lake Carve (dry room)");
            carve.transform.SetParent(root.transform);
            carve.transform.position = CarvePosition;
            carve.AddComponent<WaterExclusionVolume>().size = CarveSize;

            CreateFloorCollider(root.transform, FloorCenter, FloorSize);
            for (int i = 0; i < CrateDropPositions.Length; i++)
                CreateDemoCrate(root.transform, CrateDropPositions[i], i);

            Selection.activeGameObject = root;
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[WebGpuWater] Connected Waters Test Rig built (multi-body chain). Press " +
                      "Play and check: " +
                      "(1) the main river has no height/flow step at its flush reservoir border, " +
                      "exact shared-row mid-air junction, or flush lake border, " +
                      "(2) the pond crate floats at the POND's surface, never the sewer's, " +
                      "(3) the air-gap crate falls until it reaches the sewer's water, " +
                      "(4) the crate over the carve sinks silently - no splash, no ripples, no lift, " +
                      "(5) the reservoir crate floats at 4.2 - a third elevation, " +
                      "(6) the spillway crate lands ON the chute and rides its slope into the lake, " +
                      "(7) WaterTopology.ConnectionsOf(lake) enumerates 3 connections (7 total), " +
                      "(8) dive INTO the mid-gap river: the fog must ride the river, not cut off " +
                      "where the parent body's box ends, " +
                      "(9) from a DRY camera, look across and below both river ribbons: their " +
                      "water columns are foggy, terrain truncates the fog, and no glass wall or " +
                      "plain shell is visible, " +
                      "(10) river banks show contact foam and sloped reaches show whitewater; " +
                      "both remain continuous through the sewn junctions, " +
                      "(11) the coastal river crosses into the unbounded ocean without a rectangular " +
                      "ocean-border seam, double surface, or wave wall; its current and foam continue " +
                      "downstream over the ocean, " +
                      "(12) the coastal Terrain leaves the estuary open, clips the infinite ocean from " +
                      "the dry banks, and shoals ocean waves before they can run up the river.");
        }

        // One connected river: THE recipe (facade-owned wiring), then connect to bodies at each
        // named end. The explicit parent body supplies animated uniforms and the underwater
        // medium; both halves of a river-to-river stitch intentionally share it.
        static WaterRiver CreateConnectedRiver(Transform parent, BuildContext ctx, string riverName,
                                               List<WaterRiverKnot> knots, WaterVolume parentBody,
                                               WaterVolume sourceBody,
                                               WaterVolume mouthBody)
        {
            WaterRiver river = CreateRiverRig(parent, parentBody, ctx.MatAbove, ctx.MatUnder,
                                              knots);
            river.gameObject.name = riverName;
            AddProceduralRiverFoam(river);

            if (sourceBody != null)
            {
                river.sourceEnd.body = sourceBody;
                river.sourceEnd.transitionRadiusMeters = SeamTransitionRadiusMeters;
                WaterRiverEditor.GenerateEnd(river, WaterRiverEndKind.Source);
            }
            if (mouthBody != null)
            {
                river.mouthEnd.body = mouthBody;
                river.mouthEnd.transitionRadiusMeters = SeamTransitionRadiusMeters;
                WaterRiverEditor.GenerateEnd(river, WaterRiverEndKind.Mouth);
            }
            return river;
        }

        static void AddProceduralRiverFoam(WaterRiver river)
        {
            if (river == null) throw new ArgumentNullException(nameof(river));
            if (river.GetComponent<WaterRiverFluid>() == null)
                Undo.AddComponent<WaterRiverFluid>(river.gameObject);
            if (river.GetComponent<WaterRiverFoam>() == null)
                Undo.AddComponent<WaterRiverFoam>(river.gameObject);
        }

        static Terrain CreateCoastalTerrain(Transform parent, string waterFolder)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (string.IsNullOrEmpty(waterFolder))
                throw new ArgumentException("A water asset folder is required.", nameof(waterFolder));

            var terrainData = new TerrainData
            {
                name = CoastTerrainName,
                heightmapResolution = CoastTerrainHeightmapResolution,
                size = CoastTerrainSize
            };
            terrainData.SetHeights(0, 0, BuildCoastalTerrainHeights());

            string terrainAssetPath = waterFolder + "/" + CoastTerrainAssetFileName;
            AssetDatabase.CreateAsset(terrainData, terrainAssetPath);
            AssetDatabase.SaveAssets();

            GameObject terrainObject = Terrain.CreateTerrainGameObject(terrainData);
            terrainObject.name = CoastTerrainName;
            Undo.RegisterCreatedObjectUndo(terrainObject, CoastTerrainName);
            terrainObject.transform.SetParent(parent);
            terrainObject.transform.position = CoastTerrainOrigin;
            return terrainObject.GetComponent<Terrain>();
        }

        static float[,] BuildCoastalTerrainHeights()
        {
            int resolution = CoastTerrainHeightmapResolution;
            var heights = new float[resolution, resolution];
            int lastSample = resolution - 1;
            for (int z = 0; z < resolution; z++)
            {
                float worldZ = CoastTerrainOrigin.z + CoastTerrainSize.z * z / lastSample;
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = CoastTerrainOrigin.x + CoastTerrainSize.x * x / lastSample;
                    float worldHeight = CoastalTerrainHeight(worldX, worldZ);
                    heights[z, x] = Mathf.InverseLerp(
                        CoastTerrainOrigin.y,
                        CoastTerrainOrigin.y + CoastTerrainSize.y,
                        worldHeight);
                }
            }
            return heights;
        }

        static float CoastalTerrainHeight(float worldX, float worldZ)
        {
            float shelfHeight = CoastShelfHeight(worldZ);
            float channelCenterX = EstuaryCenterX(worldZ);
            float lateralDistance = Mathf.Abs(worldX - channelCenterX);
            float halfWidth = EstuaryHalfWidth(worldZ);
            float feather = EstuaryFeather(worldZ);
            float bankWeight = Mathf.SmoothStep(0f, 1f,
                Mathf.InverseLerp(halfWidth, halfWidth + feather, lateralDistance));
            return Mathf.Lerp(EstuaryBedHeight(worldZ), shelfHeight, bankWeight);
        }

        static float CoastShelfHeight(float worldZ)
        {
            float descent = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                CoastShelfDescentStartZ, CoastShelfDescentEndZ, worldZ));
            return Mathf.Lerp(CoastLandHeight, CoastDeepBedHeight, descent);
        }

        static float EstuaryBedHeight(float worldZ)
        {
            if (worldZ >= CoastKnotMouth.z)
            {
                float fanDescent = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                    CoastKnotMouth.z, EstuaryFanEndZ, worldZ));
                return Mathf.Lerp(EstuaryMouthBedHeight, CoastDeepBedHeight, fanDescent);
            }

            if (worldZ >= EstuaryDropStartZ)
            {
                float mouthDrop = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                    EstuaryDropStartZ, CoastKnotMouth.z, worldZ));
                return Mathf.Lerp(EstuaryDryBedHeight, EstuaryMouthBedHeight, mouthDrop);
            }

            float riverBed = CoastalRiverSurfaceHeight(worldZ) - EstuaryUpstreamClearance;
            return Mathf.Max(riverBed, EstuaryDryBedHeight);
        }

        static float CoastalRiverSurfaceHeight(float worldZ)
        {
            if (worldZ <= CoastKnotMid.z)
            {
                float upstream = Mathf.InverseLerp(CoastKnotSource.z, CoastKnotMid.z, worldZ);
                return Mathf.Lerp(CoastKnotSource.y, CoastKnotMid.y, upstream);
            }

            float downstream = Mathf.InverseLerp(CoastKnotMid.z, CoastKnotMouth.z, worldZ);
            return Mathf.Lerp(CoastKnotMid.y, CoastKnotMouth.y, downstream);
        }

        static float EstuaryCenterX(float worldZ)
        {
            if (worldZ <= CoastKnotMid.z)
            {
                float upstream = Mathf.InverseLerp(CoastKnotSource.z, CoastKnotMid.z, worldZ);
                return Mathf.Lerp(CoastKnotSource.x, CoastKnotMid.x, upstream);
            }

            float downstream = Mathf.InverseLerp(CoastKnotMid.z, CoastKnotMouth.z, worldZ);
            return Mathf.Lerp(CoastKnotMid.x, CoastKnotMouth.x, downstream);
        }

        static float EstuaryHalfWidth(float worldZ)
        {
            float riverHalfWidth = CoastRiverWidthMeters * 0.5f;
            if (worldZ <= CoastKnotMouth.z) return riverHalfWidth;
            float fan = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                CoastKnotMouth.z, EstuaryFanEndZ, worldZ));
            return Mathf.Lerp(riverHalfWidth, EstuaryFanHalfWidth, fan);
        }

        static float EstuaryFeather(float worldZ)
        {
            if (worldZ <= CoastKnotMouth.z) return EstuaryBankFeather;
            float fan = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                CoastKnotMouth.z, EstuaryFanEndZ, worldZ));
            return Mathf.Lerp(EstuaryBankFeather, EstuaryFanFeather, fan);
        }

        static void ConfigureUnboundedOcean(WaterVolume ocean, Terrain coastTerrain)
        {
            if (ocean == null) throw new ArgumentNullException(nameof(ocean));
            if (coastTerrain == null) throw new ArgumentNullException(nameof(coastTerrain));
            var serialized = new SerializedObject(ocean);
            serialized.FindProperty(WaterVolumePropertyPaths.BodyType).enumValueIndex =
                (int)WaterVolume.WaterBodyType.Ocean;
            serialized.FindProperty(WaterVolumePropertyPaths.OpenWater).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.UnboundedOcean).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.EnableLargeBodyWindow).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.UseBedDepth).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.BedTerrain).objectReferenceValue =
                coastTerrain;
            // This diagnostic needs calm shoaling, not an added breaker train obscuring the seam.
            serialized.FindProperty(WaterVolumePropertyPaths.SurfEnabled).boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ocean);
        }

        // The wizard's tuned baseline, not the class default: WaterFogSettings ships
        // fogDensity = 2, which reads as a solid murk WALL right at the surface of a bounded
        // body (their fog volume deliberately renders from any angle). 0.2 is the value the
        // full wizard has authored since 2026-07-25.
        const float RigFogDensity = 0.2f;

        static void EnableUnderwaterFog(params WaterVolume[] bodies)
        {
            foreach (WaterVolume body in bodies)
            {
                if (body == null) continue;
                body.FogSettings.waterFog = true;
                body.FogSettings.fogDensity = RigFogDensity;
            }
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
