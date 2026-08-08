using System;
using System.Reflection;
using AbstractOcclusion.WebGpuWater;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AbstractOcclusion.WebGpuWater.Tests.Editor
{
    internal static class WaterGpuMultiOceanSceneBuilder
    {
        const string MenuPath = "Tools/AbstractOcclusion/WebGpuWater/Create Multi-Ocean Isolation Scene";
        const string SceneDirectory = "Assets/WebGpuWater/Tests";
        const string GeneratedDirectory = SceneDirectory + "/Generated";
        const string ScenePath = SceneDirectory + "/WaterGpuMultiOcean.unity";
        const string RootName = "WebGpuWater Multi-Ocean Isolation";
        const string FirstOceanName = "Ocean A - Short Sea";
        const string SecondOceanName = "Ocean B - Long Swell";
        const string FirstFloaterName = "Floater A";
        const string SecondFloaterName = "Floater B";
        const string BuilderAssemblyName = "AbstractOcclusion.WebGpuWater.Editor";
        const string BuilderTypeName = "AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit";
        const string CreateContextMethodName = "CreateContext";
        const string CreateWaterBodyMethodName = "CreateWaterBody";
        const BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
        const float OceanCenterOffset = 20f;
        const float OceanHalfExtent = 15f;
        const float OceanDepth = 6f;
        const float FloaterHeight = 2f;
        const float FirstWaveHeight = 0.75f;
        const float FirstPeakWavelength = 12f;
        const float SecondWaveHeight = 2.5f;
        const float SecondPeakWavelength = 40f;

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
                Debug.LogWarning("[WebGpuWater] Multi-ocean isolation scene already exists at '" + ScenePath + "'.");
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(RootName);
            try
            {
                object buildContext = CreateBuildContext(root.transform);
                WaterVolume first = CreateOcean(buildContext, root.transform, FirstOceanName, -OceanCenterOffset, true);
                WaterVolume second = CreateOcean(buildContext, root.transform, SecondOceanName, OceanCenterOffset, false);
                ConfigureOcean(first, FirstWaveHeight, FirstPeakWavelength);
                ConfigureOcean(second, SecondWaveHeight, SecondPeakWavelength);
                CreateFloater(root.transform, FirstFloaterName, -OceanCenterOffset);
                CreateFloater(root.transform, SecondFloaterName, OceanCenterOffset);
                EditorSceneManager.SaveScene(scene, ScenePath);
                Selection.activeObject = first.gameObject;
                Debug.Log("[WebGpuWater] Multi-ocean isolation scene created at '" + ScenePath + "'.");
            }
            catch (Exception exception)
            {
                Debug.LogError("[WebGpuWater] Multi-ocean isolation scene creation failed: " + exception.Message);
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

        static WaterVolume CreateOcean(object buildContext, Transform root, string name, float xPosition, bool primary)
        {
            Type builderType = Type.GetType(BuilderTypeName + ", " + BuilderAssemblyName);
            MethodInfo createWaterBody = builderType?.GetMethod(CreateWaterBodyMethodName, StaticNonPublic);
            if (createWaterBody == null)
                throw new InvalidOperationException("Could not resolve the package water-body builder.");

            var extent = new Vector3(OceanHalfExtent, OceanDepth, OceanHalfExtent);
            object[] arguments =
            {
                buildContext,
                root,
                name,
                new Vector3(xPosition, 0f, 0f),
                extent,
                primary,
                false,
                false,
                false,
                false,
            };
            return (WaterVolume)createWaterBody.Invoke(null, arguments);
        }

        static void ConfigureOcean(WaterVolume ocean, float significantWaveHeight, float peakWavelength)
        {
            var serializedOcean = new SerializedObject(ocean);
            SerializedProperty oceanSettings = serializedOcean.FindProperty("ocean");
            if (oceanSettings == null)
                throw new InvalidOperationException("WaterVolume ocean settings were not found.");

            oceanSettings.FindPropertyRelative("openWater").boolValue = true;
            oceanSettings.FindPropertyRelative("unboundedOcean").boolValue = false;
            oceanSettings.FindPropertyRelative("significantWaveHeight").floatValue = significantWaveHeight;
            oceanSettings.FindPropertyRelative("peakWavelength").floatValue = peakWavelength;
            serializedOcean.ApplyModifiedPropertiesWithoutUndo();
        }

        static void CreateFloater(Transform root, string name, float xPosition)
        {
            GameObject floater = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floater.name = name;
            floater.transform.SetParent(root);
            floater.transform.position = new Vector3(xPosition, FloaterHeight, 0f);
            Rigidbody rigidbody = floater.AddComponent<Rigidbody>();
            rigidbody.mass = 1f;
            floater.AddComponent<WaterBuoyancy>();
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
