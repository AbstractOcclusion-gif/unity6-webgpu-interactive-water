// WebGpuWater build kit - repeatable 1/5/15/30-body relevance and render-budget stress scene.
// The first five bodies form a geometric stepped basin chain with real river ribbons and banks;
// the remaining bodies stay deliberately plain so water scaling is not buried under prop cost.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string StressRigMenuPath = GameObjectMenuRoot + "Multi-Body Water Stress Test Rig";
        // Same leaf name as before, now under the one Window/ MenuRoot (it lived on a Tools/ root
        // of its own, the third menu root in this assembly).
        const string StressSceneMenuPath = MenuRoot + "Create Multi-Body Stress Test Scene";
        const int StressRigMenuPriority = 13;
        const int StressSceneMenuPriority = 40;
        const string StressRigRootName = "Multi-Body Water Stress Test Rig";
        const string StressSceneAssetPath = "Assets/Multi-Body Water Stress Test.unity";
        const string StressUndoName = "Rebuild Multi-Body Water Stress Test Rig";
        const string StressWaterNamePrefix = "Stress Water ";
        const string StressWaterPrimarySuffix = " (Primary)";
        const string StressRiverNamePrefix = "Stress River ";
        const string StressConcreteMaterialName = "/Stress Concrete.mat";
        const string StressConcreteShaderName = "Universal Render Pipeline/Lit";
        const string StressConcreteMissingMessage =
            LogPrefix + "URP Lit shader is required for stress-test basin geometry.";
        const string StressBuildFailureMessage =
            LogPrefix + "multi-body stress-test scene could not be built.";
        const string StressSettingsMissingMessage =
            "Water stress builder could not find reflection/caustic settings.";
        const string StressBuildSuccessPrefix = LogPrefix + "Multi-body stress test built at ";
        const string StressBuildInstructions =
            ". Play, use 1/2/3/4 for 1/5/15/30 bodies, Space to pause the deterministic camera, " +
            "R to restart its route, and profile the same route for every tier.";
        const string BodyNumberFormat = "00";
        const string TierOneRootName = "Tier 1 — first body";
        const string TierFiveRootName = "Tier 5 — add four bodies and rivers";
        const string TierFifteenRootName = "Tier 15 — add ten bodies";
        const string TierThirtyRootName = "Tier 30 — add fifteen bodies";
        const string GroundName = "Geometric Ground";
        const string BasinFloorName = "Basin Floor";
        const string BasinNorthBankName = "Basin North Bank";
        const string BasinSouthBankName = "Basin South Bank";
        const string BasinEastBankName = "Basin East Bank";
        const string BasinWestBankName = "Basin West Bank";
        const string RiverNorthBankName = "River North Bank";
        const string RiverSouthBankName = "River South Bank";
        const string ReflectionPlanarPropertyPath =
            "reflectionSettings.usePlanarReflection";
        const string ScreenCausticsPropertyPath =
            "depthAttenuation.screenSpaceCaustics";
        const int TotalBodyCount = 30;
        const int FiveBodyThreshold = 5;
        const int FifteenBodyThreshold = 15;
        const int GridColumns = 6;
        const int GridRows = 5;
        const int FirstBodyIndex = 0;
        const int FirstFiveBodyLastIndex = FiveBodyThreshold - 1;
        const int TierRootCount = 4;
        const int TierOneIndex = 0;
        const int TierFiveIndex = 1;
        const int TierFifteenIndex = 2;
        const int TierThirtyIndex = 3;
        const int InitialScenarioIndex = TierThirtyIndex;
        const int BodyNumberOffset = 1;
        const int NextBodyOffset = 1;
        const float Half = 0.5f;
        const float GridSpacingMeters = 18f;
        const float HalfGridColumnSpan = (GridColumns - NextBodyOffset) * Half;
        const float HalfGridRowSpan = (GridRows - NextBodyOffset) * Half;
        const float FeaturedWaterLevel = 3f;
        const float FeaturedWaterDropPerBody = 0.75f;
        const float PlainWaterLevel = 0f;
        const float StressActivationDistance = 75f;
        const float BasinHalfWidth = 6f;
        const float BasinHalfDepth = 6f;
        const float BasinWaterColumnHalfHeight = 2f;
        const float BasinBankThickness = 0.8f;
        const float BasinBankHeight = 3f;
        const float BasinBankTopAboveWater = 0.6f;
        const float BasinFloorThickness = 0.5f;
        const float BasinFloorDepth = 3f;
        const float StressRiverWidthMeters = 3f;
        const float StressRiverSpeedMetersPerSecond = 1.5f;
        const float StressRiverTangentMeters = 2f;
        const float RiverBankThickness = 0.8f;
        const float RiverBankHeight = 2f;
        const float RiverBankVerticalOffset = -0.7f;
        const float GroundMarginMeters = 20f;
        const float GroundThickness = 0.5f;
        const float GroundTop = -3.5f;
        const float RouteExtraRadiusX = 25f;
        const float RouteExtraRadiusZ = 22f;
        const float RouteHeight = 32f;
        const float RouteHeightVariation = 6f;
        const float RouteDurationSeconds = 24f;
        const float ConcreteRed = 0.38f;
        const float ConcreteGreen = 0.34f;
        const float ConcreteBlue = 0.28f;
        const float ConcreteAlpha = 1f;

        static readonly string[] StressTierRootNames =
        {
            TierOneRootName,
            TierFiveRootName,
            TierFifteenRootName,
            TierThirtyRootName,
        };
        static readonly Vector3 StressBodyExtent = new Vector3(
            BasinHalfWidth, BasinWaterColumnHalfHeight, BasinHalfDepth);

        [MenuItem(StressRigMenuPath, false, StressRigMenuPriority)]
        static void CreateStressTestRigInOpenScene()
        {
            int undoGroup = Undo.GetCurrentGroup();
            GameObject root = BuildStressTestRig(undoGroup);
            if (root == null) return;
            Selection.activeGameObject = root;
            Undo.CollapseUndoOperations(undoGroup);
        }

        [MenuItem(StressSceneMenuPath, false, StressSceneMenuPriority)]
        public static void CreateMultiBodyStressTestSceneAsset()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            int undoGroup = Undo.GetCurrentGroup();
            GameObject root = BuildStressTestRig(undoGroup);
            if (root == null)
            {
                Debug.LogError(StressBuildFailureMessage);
                return;
            }

            string scenePath = AssetDatabase.GenerateUniqueAssetPath(StressSceneAssetPath);
            if (!EditorSceneManager.SaveScene(scene, scenePath))
                throw new InvalidOperationException(StressBuildFailureMessage);
            AssetDatabase.SaveAssets();
            Selection.activeGameObject = root;
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log(StressBuildSuccessPrefix + scenePath + StressBuildInstructions, root);
        }

        static GameObject BuildStressTestRig(int undoGroup)
        {
            Undo.SetCurrentGroupName(StressUndoName);
            GameObject existingRoot = GameObject.Find(StressRigRootName);
            if (existingRoot != null && existingRoot.scene.IsValid())
                Undo.DestroyObjectImmediate(existingRoot);

            GameObject root = NewUndoableGameObject(StressRigRootName);
            if (!CreateContext(root.transform, out BuildContext context, CreateUniqueWaterFolder(),
                               buildPoolMaterial: false))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return null;
            }

            Material concreteMaterial = CreateStressConcreteMaterial(context.MaterialsFolder);
            if (concreteMaterial == null)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return null;
            }

            GameObject[] tierRoots = CreateTierRoots(root.transform);
            WaterVolume[] bodies = CreateStressBodies(context, tierRoots, concreteMaterial);
            CreateFeaturedRivers(context, tierRoots[TierFiveIndex].transform, bodies,
                                 concreteMaterial);
            CreateStressGround(root.transform, concreteMaterial);
            ConfigureStressController(root, context, tierRoots);
            return root;
        }

        static Material CreateStressConcreteMaterial(string materialsFolder)
        {
            Shader shader = Shader.Find(StressConcreteShaderName);
            if (shader == null)
            {
                Debug.LogError(StressConcreteMissingMessage);
                return null;
            }
            return LoadOrCreateMaterial(materialsFolder + StressConcreteMaterialName, shader,
                material => material.SetColor(PropBaseColor, new Color(
                    ConcreteRed, ConcreteGreen, ConcreteBlue, ConcreteAlpha)));
        }

        static GameObject[] CreateTierRoots(Transform parent)
        {
            var roots = new GameObject[TierRootCount];
            for (int i = 0; i < roots.Length; i++)
            {
                roots[i] = NewUndoableGameObject(StressTierRootNames[i]);
                roots[i].transform.SetParent(parent);
            }
            return roots;
        }

        static WaterVolume[] CreateStressBodies(BuildContext context, GameObject[] tierRoots,
                                                Material concreteMaterial)
        {
            var bodies = new WaterVolume[TotalBodyCount];
            for (int bodyIndex = 0; bodyIndex < bodies.Length; bodyIndex++)
            {
                int tierIndex = TierIndexForBody(bodyIndex);
                Vector3 position = BodyPosition(bodyIndex);
                bool primary = bodyIndex == FirstBodyIndex;
                string bodyName = StressWaterNamePrefix
                                + (bodyIndex + BodyNumberOffset).ToString(BodyNumberFormat)
                                + (primary ? StressWaterPrimarySuffix : string.Empty);
                WaterVolume body = CreateWaterBody(
                    context, tierRoots[tierIndex].transform, bodyName, position, StressBodyExtent,
                    primary, withPool: false, withGodRays: false,
                    withFoamParticles: false, withSplash: false);
                body.activationDistance = StressActivationDistance;
                ConfigureStressRenderFeatures(body, bodyIndex <= FirstFiveBodyLastIndex);
                bodies[bodyIndex] = body;

                if (bodyIndex <= FirstFiveBodyLastIndex)
                    CreateBasinGeometry(tierRoots[tierIndex].transform, position,
                                        concreteMaterial);
            }
            return bodies;
        }

        static void ConfigureStressRenderFeatures(WaterVolume body, bool planarReflection)
        {
            var serializedBody = new SerializedObject(body);
            SerializedProperty planarProperty =
                serializedBody.FindProperty(ReflectionPlanarPropertyPath);
            SerializedProperty causticProperty =
                serializedBody.FindProperty(ScreenCausticsPropertyPath);
            if (planarProperty == null || causticProperty == null)
                throw new InvalidOperationException(StressSettingsMissingMessage);
            planarProperty.boolValue = planarReflection;
            causticProperty.boolValue = true;
            serializedBody.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(body);
        }

        static void CreateFeaturedRivers(BuildContext context, Transform parent,
                                         WaterVolume[] bodies, Material concreteMaterial)
        {
            for (int sourceIndex = FirstBodyIndex;
                 sourceIndex < FirstFiveBodyLastIndex; sourceIndex++)
            {
                WaterVolume sourceBody = bodies[sourceIndex];
                WaterVolume mouthBody = bodies[sourceIndex + NextBodyOffset];
                Vector3 source = sourceBody.transform.position
                               + Vector3.right * BasinHalfWidth;
                Vector3 mouth = mouthBody.transform.position
                              - Vector3.right * BasinHalfWidth;
                Vector3 midpoint = Vector3.Lerp(source, mouth, Half);
                Vector3 tangent = (mouth - source).normalized * StressRiverTangentMeters;
                string riverName = StressRiverNamePrefix
                                 + (sourceIndex + BodyNumberOffset).ToString(BodyNumberFormat);
                // The demo rig's seam radius and procedural foam, as before the recipe took
                // them as parameters.
                CreateConnectedRiver(parent, riverName,
                    new List<WaterRiverKnot>
                    {
                        new WaterRiverKnot(source, tangent, StressRiverWidthMeters,
                                           StressRiverSpeedMetersPerSecond),
                        new WaterRiverKnot(midpoint, tangent, StressRiverWidthMeters,
                                           StressRiverSpeedMetersPerSecond),
                        new WaterRiverKnot(mouth, tangent, StressRiverWidthMeters,
                                           StressRiverSpeedMetersPerSecond),
                    },
                    sourceBody, context.MatAbove, context.MatUnder,
                    sourceBody: sourceBody, mouthBody: mouthBody, upstreamRiver: null,
                    transitionRadiusMeters: SeamTransitionRadiusMeters, withProceduralFoam: true);
                CreateRiverBanks(parent, source, mouth, concreteMaterial);
            }
        }

        static void CreateBasinGeometry(Transform parent, Vector3 waterPosition,
                                        Material material)
        {
            float bankCenterY = waterPosition.y + BasinBankTopAboveWater
                              - BasinBankHeight * Half;
            float fullWidth = BasinHalfWidth * 2f + BasinBankThickness * 2f;
            float fullDepth = BasinHalfDepth * 2f;
            CreateStressCube(BasinFloorName, parent,
                new Vector3(waterPosition.x, waterPosition.y - BasinFloorDepth,
                            waterPosition.z),
                new Vector3(BasinHalfWidth * 2f, BasinFloorThickness,
                            BasinHalfDepth * 2f), material);
            CreateStressCube(BasinNorthBankName, parent,
                new Vector3(waterPosition.x, bankCenterY,
                            waterPosition.z + BasinHalfDepth + BasinBankThickness * Half),
                new Vector3(fullWidth, BasinBankHeight, BasinBankThickness), material);
            CreateStressCube(BasinSouthBankName, parent,
                new Vector3(waterPosition.x, bankCenterY,
                            waterPosition.z - BasinHalfDepth - BasinBankThickness * Half),
                new Vector3(fullWidth, BasinBankHeight, BasinBankThickness), material);
            CreateStressCube(BasinEastBankName, parent,
                new Vector3(waterPosition.x + BasinHalfWidth + BasinBankThickness * Half,
                            bankCenterY, waterPosition.z),
                new Vector3(BasinBankThickness, BasinBankHeight, fullDepth), material);
            CreateStressCube(BasinWestBankName, parent,
                new Vector3(waterPosition.x - BasinHalfWidth - BasinBankThickness * Half,
                            bankCenterY, waterPosition.z),
                new Vector3(BasinBankThickness, BasinBankHeight, fullDepth), material);
        }

        static void CreateRiverBanks(Transform parent, Vector3 source, Vector3 mouth,
                                     Material material)
        {
            Vector3 bankOffset = Vector3.forward
                               * (StressRiverWidthMeters * Half + RiverBankThickness * Half);
            Vector3 verticalOffset = Vector3.up * RiverBankVerticalOffset;
            CreateStressSegment(RiverNorthBankName, parent, source + bankOffset + verticalOffset,
                                mouth + bankOffset + verticalOffset, RiverBankHeight,
                                RiverBankThickness, material);
            CreateStressSegment(RiverSouthBankName, parent, source - bankOffset + verticalOffset,
                                mouth - bankOffset + verticalOffset, RiverBankHeight,
                                RiverBankThickness, material);
        }

        static void CreateStressSegment(string name, Transform parent, Vector3 start, Vector3 end,
                                        float height, float width, Material material)
        {
            Vector3 direction = end - start;
            GameObject segment = CreateStressCube(name, parent, Vector3.Lerp(start, end, Half),
                new Vector3(direction.magnitude, height, width), material);
            segment.transform.rotation = Quaternion.FromToRotation(Vector3.right,
                                                                   direction.normalized);
        }

        static void CreateStressGround(Transform parent, Material material)
        {
            float width = (GridColumns - 1) * GridSpacingMeters
                        + BasinHalfWidth * 2f + GroundMarginMeters;
            float depth = (GridRows - 1) * GridSpacingMeters
                        + BasinHalfDepth * 2f + GroundMarginMeters;
            CreateStressCube(GroundName, parent,
                new Vector3(0f, GroundTop - GroundThickness * Half, 0f),
                new Vector3(width, GroundThickness, depth), material);
        }

        static GameObject CreateStressCube(string name, Transform parent, Vector3 position,
                                           Vector3 scale, Material material)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            Undo.RegisterCreatedObjectUndo(cube, name);
            cube.transform.SetParent(parent);
            cube.transform.position = position;
            cube.transform.localScale = scale;
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            return cube;
        }

        static void ConfigureStressController(GameObject root, BuildContext context,
                                              GameObject[] tierRoots)
        {
            if (context.Orbit != null)
            {
                Undo.RecordObject(context.Orbit, StressUndoName);
                context.Orbit.enabled = false;
            }
            var controller = Undo.AddComponent<WaterStressScenarioController>(root);
            float gridHalfWidth = HalfGridColumnSpan * GridSpacingMeters + BasinHalfWidth;
            float gridHalfDepth = HalfGridRowSpan * GridSpacingMeters + BasinHalfDepth;
            controller.Configure(context.Camera, tierRoots, Vector3.zero,
                new Vector2(gridHalfWidth + RouteExtraRadiusX,
                            gridHalfDepth + RouteExtraRadiusZ),
                RouteHeight, RouteHeightVariation, RouteDurationSeconds,
                InitialScenarioIndex);
            EditorUtility.SetDirty(controller);
        }

        static int TierIndexForBody(int bodyIndex)
        {
            if (bodyIndex == FirstBodyIndex) return TierOneIndex;
            if (bodyIndex < FiveBodyThreshold) return TierFiveIndex;
            if (bodyIndex < FifteenBodyThreshold) return TierFifteenIndex;
            return TierThirtyIndex;
        }

        static Vector3 BodyPosition(int bodyIndex)
        {
            int column = bodyIndex % GridColumns;
            int row = bodyIndex / GridColumns;
            float x = (column - HalfGridColumnSpan) * GridSpacingMeters;
            float z = (row - HalfGridRowSpan) * GridSpacingMeters;
            float y = bodyIndex <= FirstFiveBodyLastIndex
                ? FeaturedWaterLevel - bodyIndex * FeaturedWaterDropPerBody
                : PlainWaterLevel;
            return new Vector3(x, y, z);
        }
    }
}
