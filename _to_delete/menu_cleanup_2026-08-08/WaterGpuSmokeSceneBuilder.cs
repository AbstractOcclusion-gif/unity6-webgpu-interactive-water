using System;
using System.Reflection;
using AbstractOcclusion.WebGpuWater;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AbstractOcclusion.WebGpuWater.Tests.Editor
{
    internal static class WaterGpuSmokeSceneBuilder
    {
        const string MenuPath = "Tools/AbstractOcclusion/WebGpuWater/Create GPU Smoke Scene";
        const string SceneDirectory = "Assets/WebGpuWater/Tests";
        const string GeneratedDirectory = SceneDirectory + "/Generated";
        const string ScenePath = SceneDirectory + "/WaterGpuSmoke.unity";
        const string RootName = "WebGpuWater GPU Smoke";
        const string OceanName = "Ocean - FFT + Clipmap";
        const string ExclusionName = "Exclusion - Dry Interior";
        const string FloaterName = "Floater - Buoyancy";
        const string BuilderAssemblyName = "AbstractOcclusion.WebGpuWater.Editor";
        const string BuilderTypeName = "AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit";
        const string CreateContextMethodName = "CreateContext";
        const string CreateWaterBodyMethodName = "CreateWaterBody";
        const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
        const float OceanHalfExtent = 50f;
        const float OceanDepth = 10f;
        const float FloaterOffsetX = 10f;
        const float FloaterHeight = 2f;
        const float ExclusionSize = 6f;

        [MenuItem(MenuPath)]
        static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EnsureFolder(SceneDirectory);
            EnsureFolder(GeneratedDirectory);
            SceneAsset existingScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (existingScene != null)
            {
                Selection.activeObject = existingScene;
                Debug.LogWarning("[WebGpuWater] GPU smoke scene already exists at '" + ScenePath + "'.");
                return;
            }
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(RootName);

            try
            {
                object buildContext = CreateBuildContext(root.transform);
                WaterVolume ocean = CreateOcean(buildContext, root.transform);
                ConfigureOcean(ocean);
                CreateExclusion(root.transform);
                CreateFloater(root.transform);
                EditorSceneManager.SaveScene(scene, ScenePath);
                Selection.activeObject = ocean.gameObject;
                Debug.Log("[WebGpuWater] GPU smoke scene created at '" + ScenePath + "'.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[WebGpuWater] GPU smoke scene creation failed: " + exception.Message);
                throw;
            }
        }

        static object CreateBuildContext(Transform root)
        {
            Type builderType = Type.GetType(BuilderTypeName + ", " + BuilderAssemblyName);
            MethodInfo createContext = builderType?.GetMethod(CreateContextMethodName, StaticNonPublic);
            if (createContext == null)
                throw new InvalidOperationException("Could not resolve the package water build kit.");

            object[] arguments = { root, null, GeneratedDirectory, true };
            bool succeeded = (bool)createContext.Invoke(null, arguments);
            if (!succeeded || arguments[1] == null)
                throw new InvalidOperationException("The package water build kit could not create its shared assets.");
            return arguments[1];
        }

        static WaterVolume CreateOcean(object buildContext, Transform root)
        {
            Type builderType = Type.GetType(BuilderTypeName + ", " + BuilderAssemblyName);
            MethodInfo createWaterBody = builderType?.GetMethod(CreateWaterBodyMethodName, StaticNonPublic);
            if (createWaterBody == null)
                throw new InvalidOperationException("Could not resolve the package water-body builder.");

            var extent = new Vector3(OceanHalfExtent, OceanDepth, OceanHalfExtent);
            object[] arguments = { buildContext, root, OceanName, Vector3.zero, extent, true, false, false, true, true };
            return (WaterVolume)createWaterBody.Invoke(null, arguments);
        }

        static void ConfigureOcean(WaterVolume ocean)
        {
            var serializedOcean = new SerializedObject(ocean);
            SerializedProperty oceanSettings = serializedOcean.FindProperty("ocean");
            if (oceanSettings == null)
                throw new InvalidOperationException("WaterVolume ocean settings were not found.");

            oceanSettings.FindPropertyRelative("openWater").boolValue = true;
            oceanSettings.FindPropertyRelative("unboundedOcean").boolValue = true;
            serializedOcean.ApplyModifiedPropertiesWithoutUndo();
        }

        static void CreateExclusion(Transform root)
        {
            var exclusion = new GameObject(ExclusionName);
            exclusion.transform.SetParent(root);
            exclusion.transform.position = Vector3.zero;
            WaterExclusionVolume volume = exclusion.AddComponent<WaterExclusionVolume>();
            volume.shape = WaterExclusionVolume.Shape.Box;
            volume.size = Vector3.one * ExclusionSize;
        }

        static void CreateFloater(Transform root)
        {
            GameObject floater = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floater.name = FloaterName;
            floater.transform.SetParent(root);
            floater.transform.position = new Vector3(FloaterOffsetX, FloaterHeight, 0f);
            Rigidbody rigidbody = floater.AddComponent<Rigidbody>();
            rigidbody.mass = 1f;
            floater.AddComponent<WaterBuoyancy>();
            floater.AddComponent<WaterInteractable>();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException("Invalid asset folder path: " + path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
