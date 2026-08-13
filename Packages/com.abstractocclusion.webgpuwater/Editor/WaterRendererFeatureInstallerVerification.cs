using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public static class WaterRendererFeatureInstallerVerification
    {
        const string TemporaryAssetPath = "Assets/__WebGpuWaterRendererInstallerVerification.asset";
        const string UniversalRendererDataTypeName =
            "UnityEngine.Rendering.Universal.UniversalRendererData, Unity.RenderPipelines.Universal.Runtime";
        const int ExpectedFeatureCount = 6;
        const int ExpectedShaderBindingCount = 7;

        public static void Run()
        {
            if (AssetDatabase.LoadMainAssetAtPath(TemporaryAssetPath) != null)
                throw new InvalidOperationException("Verification asset already exists: " + TemporaryAssetPath);

            try
            {
                Type rendererType = Type.GetType(UniversalRendererDataTypeName);
                if (rendererType == null) throw new InvalidOperationException("UniversalRendererData was not found.");
                ScriptableObject renderer = ScriptableObject.CreateInstance(rendererType);
                if (renderer == null) throw new InvalidOperationException("Could not create temporary Renderer Data.");
                renderer.name = "WebGpuWater Renderer Installer Verification";
                ClearFeatures(renderer);
                AssetDatabase.CreateAsset(renderer, TemporaryAssetPath);

                VerifyInitialInstall(renderer);
                VerifyRepairPreservesCustomShader(renderer);
                VerifyNoOp(renderer);
                Debug.Log("[WebGpuWater Verification] PASS: install, feature map, shader repair, " +
                          "custom assignment preservation, and idempotence.");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TemporaryAssetPath);
                AssetDatabase.Refresh();
            }
        }

        static void ClearFeatures(Object renderer)
        {
            SerializedObject serializedRenderer = new SerializedObject(renderer);
            SerializedProperty features = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            SerializedProperty featureMap = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeatureMapProperty);
            if (features == null || featureMap == null)
                throw new InvalidOperationException("Cloned asset is not compatible Renderer Data.");
            features.arraySize = 0;
            featureMap.arraySize = 0;
            serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
        }

        static void VerifyInitialInstall(Object renderer)
        {
            RequireInstall(renderer, out WaterRendererFeatureInstaller.Result result);
            Require(result.AddedFeatureCount == ExpectedFeatureCount,
                "Initial install added " + result.AddedFeatureCount + " features.");
            Require(result.RepairedShaderCount == ExpectedShaderBindingCount,
                "Initial install assigned " + result.RepairedShaderCount + " shader fields.");

            SerializedObject serializedRenderer = new SerializedObject(renderer);
            SerializedProperty features = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            SerializedProperty featureMap = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeatureMapProperty);
            Require(features.arraySize == ExpectedFeatureCount, "Feature list size is incorrect.");
            Require(featureMap.arraySize == ExpectedFeatureCount, "Feature map size is incorrect.");

            foreach (WaterRendererFeatureCatalog.Feature specification in WaterRendererFeatureCatalog.Features)
            {
                Object feature = FindFeature(features, specification.TypeName);
                Require(feature != null, "Missing installed feature: " + specification.TypeName);
                Require(AssetDatabase.GetAssetPath(feature) == TemporaryAssetPath,
                    specification.TypeName + " is not a renderer sub-asset.");
                VerifyBindings(feature, specification);
            }
        }

        static void VerifyRepairPreservesCustomShader(Object renderer)
        {
            SerializedProperty features = new SerializedObject(renderer).FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            Object fogFeature = FindFeature(features, "WaterUnderwaterFogFeature");
            Shader customShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterSkyFog.shader");
            Require(fogFeature != null && customShader != null, "Repair test prerequisites are missing.");

            SerializedObject serializedFeature = new SerializedObject(fogFeature);
            SerializedProperty fogShader = serializedFeature.FindProperty("underwaterFogShader");
            SerializedProperty heightShader = serializedFeature.FindProperty("heightRtShader");
            fogShader.objectReferenceValue = customShader;
            heightShader.objectReferenceValue = null;
            serializedFeature.ApplyModifiedPropertiesWithoutUndo();

            RequireInstall(renderer, out WaterRendererFeatureInstaller.Result result);
            Require(result.AddedFeatureCount == 0, "Repair unexpectedly added a feature.");
            Require(result.RepairedShaderCount == 1, "Repair did not fill exactly one empty shader field.");

            serializedFeature.Update();
            Require(fogShader.objectReferenceValue == customShader, "Repair overwrote a custom shader assignment.");
            Require(heightShader.objectReferenceValue != null, "Repair left the empty shader field unassigned.");
        }

        static void VerifyNoOp(Object renderer)
        {
            RequireInstall(renderer, out WaterRendererFeatureInstaller.Result result);
            Require(!result.Changed, "Repeated install was not idempotent.");
        }

        static void VerifyBindings(Object feature, WaterRendererFeatureCatalog.Feature specification)
        {
            SerializedObject serializedFeature = new SerializedObject(feature);
            foreach (WaterRendererFeatureCatalog.ShaderBinding binding in specification.ShaderBindings)
            {
                SerializedProperty property = serializedFeature.FindProperty(binding.PropertyName);
                Require(property != null && property.objectReferenceValue != null,
                    specification.TypeName + " has an empty " + binding.PropertyName + ".");
            }
        }

        static Object FindFeature(SerializedProperty features, string typeName)
        {
            for (int index = 0; index < features.arraySize; index++)
            {
                Object feature = features.GetArrayElementAtIndex(index).objectReferenceValue;
                if (feature != null && feature.GetType().Name == typeName) return feature;
            }
            return null;
        }

        static void RequireInstall(Object renderer, out WaterRendererFeatureInstaller.Result result)
        {
            if (!WaterRendererFeatureInstaller.TryInstallOrRepair(renderer, out result, out string error))
                throw new InvalidOperationException(error);
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
