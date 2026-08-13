using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterSplashPrefabMigration
    {
        const string DemoScenesRelativePath = "Samples/Demos/Scenes";
        const string MenuPath = WaterBuildKit.MenuRoot +
            "Maintenance/Rebuild Splash Prefab and Migrate Demo Scenes";

        [MenuItem(MenuPath)]
        internal static void RebuildPrefabAndMigrateDemoScenes()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            try
            {
                GameObject prefab = WaterBuildKit.CreateOrReplaceSplashEmitterPrefab();
                int convertedEmitterCount = ConvertDemoScenes(prefab);
                AssetDatabase.SaveAssets();
                Debug.Log(WaterBuildKit.LogPrefix +
                    $"splash prefab rebuilt and {convertedEmitterCount} demo emitter(s) linked to it.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
            finally
            {
                if (activeScene.IsValid() && activeScene.isLoaded)
                    SceneManager.SetActiveScene(activeScene);
                EditorUtility.ClearProgressBar();
            }
        }

        static int ConvertDemoScenes(GameObject prefab)
        {
            if (prefab == null)
                throw new ArgumentNullException(nameof(prefab));

            string demoScenesRoot = WaterPackagePaths.Asset(DemoScenesRelativePath);
            string[] sceneGuids = AssetDatabase.FindAssets("t:Scene", new[] { demoScenesRoot });
            Array.Sort(sceneGuids, StringComparer.Ordinal);

            int convertedEmitterCount = 0;
            for (int sceneIndex = 0; sceneIndex < sceneGuids.Length; sceneIndex++)
            {
                string scenePath = AssetDatabase.GUIDToAssetPath(sceneGuids[sceneIndex]);
                EditorUtility.DisplayProgressBar(
                    "WebGPU Water Splash Prefab Migration",
                    scenePath,
                    sceneGuids.Length > 0 ? (float)sceneIndex / sceneGuids.Length : 1f);
                convertedEmitterCount += ConvertScene(scenePath, prefab);
            }
            return convertedEmitterCount;
        }

        static int ConvertScene(string scenePath, GameObject prefab)
        {
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool openedForMigration = !scene.IsValid() || !scene.isLoaded;
            if (openedForMigration)
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

            try
            {
                List<WaterSplashEmitter> emitters = FindSceneEmitters(scene);
                int convertedEmitterCount = 0;
                foreach (WaterSplashEmitter emitter in emitters)
                {
                    if (IsInstanceOf(emitter.gameObject, prefab)) continue;

                    WaterBuildKit.EnsureSplashPrefabHierarchy(emitter);
                    ConvertEmitter(emitter.gameObject, prefab);
                    ValidateParticleSystems(emitter.gameObject, scenePath);
                    convertedEmitterCount++;
                }

                if (convertedEmitterCount > 0 && !EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Could not save migrated scene '{scenePath}'.");
                return convertedEmitterCount;
            }
            finally
            {
                if (openedForMigration && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        static List<WaterSplashEmitter> FindSceneEmitters(Scene scene)
        {
            var emitters = new List<WaterSplashEmitter>();
            foreach (GameObject root in scene.GetRootGameObjects())
                emitters.AddRange(root.GetComponentsInChildren<WaterSplashEmitter>(true));
            return emitters;
        }

        static bool IsInstanceOf(GameObject instanceRoot, GameObject prefab)
            => PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot) == prefab;

        static void ConvertEmitter(GameObject emitterRoot, GameObject prefab)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(emitterRoot))
            {
                GameObject outermostRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(emitterRoot);
                if (outermostRoot != emitterRoot)
                    throw new InvalidOperationException(
                        $"Splash emitter '{emitterRoot.name}' is nested inside another Prefab instance.");

                var replacingSettings = new PrefabReplacingSettings
                {
                    changeRootNameToAssetName = false,
                    logInfo = false,
                    objectMatchMode = ObjectMatchMode.ByHierarchy,
                    prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides
                };
                PrefabUtility.ReplacePrefabAssetOfPrefabInstance(
                    emitterRoot, prefab, replacingSettings, InteractionMode.AutomatedAction);
                return;
            }

            var conversionSettings = new ConvertToPrefabInstanceSettings
            {
                changeRootNameToAssetName = false,
                componentsNotMatchedBecomesOverride = true,
                gameObjectsNotMatchedBecomesOverride = true,
                logInfo = false,
                objectMatchMode = ObjectMatchMode.ByHierarchy,
                recordPropertyOverridesOfMatches = true
            };
            PrefabUtility.ConvertToPrefabInstance(
                emitterRoot, prefab, conversionSettings, InteractionMode.AutomatedAction);
        }

        static void ValidateParticleSystems(GameObject emitterRoot, string scenePath)
        {
            ParticleSystem[] particleSystems = emitterRoot.GetComponentsInChildren<ParticleSystem>(true);
            foreach (ParticleSystem particleSystem in particleSystems)
            {
                if (!PrefabUtility.IsPartOfPrefabInstance(particleSystem.gameObject))
                    throw new InvalidOperationException(
                        $"ParticleSystem '{particleSystem.name}' in '{scenePath}' was not linked to the prefab.");
            }
        }
    }
}
