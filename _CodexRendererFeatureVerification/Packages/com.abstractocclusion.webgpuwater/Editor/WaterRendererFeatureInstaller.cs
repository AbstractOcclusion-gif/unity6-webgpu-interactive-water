using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterRendererFeatureInstaller
    {
        const string UndoName = "Install WebGpuWater Renderer Features";

        internal readonly struct Result
        {
            internal readonly int AddedFeatureCount;
            internal readonly int RepairedShaderCount;

            internal Result(int addedFeatureCount, int repairedShaderCount)
            {
                AddedFeatureCount = addedFeatureCount;
                RepairedShaderCount = repairedShaderCount;
            }

            internal bool Changed => AddedFeatureCount > 0 || RepairedShaderCount > 0;
        }

        readonly struct ValidatedFeature
        {
            internal readonly WaterRendererFeatureCatalog.Feature Specification;
            internal readonly Type FeatureType;

            internal ValidatedFeature(WaterRendererFeatureCatalog.Feature specification, Type featureType)
            {
                Specification = specification;
                FeatureType = featureType;
            }
        }

        internal static bool TryInstallOrRepair(Object rendererAsset, out Result result, out string error)
        {
            result = default;
            error = null;

            if (!TryValidate(rendererAsset, out List<ValidatedFeature> features,
                    out Dictionary<string, Shader> shaders, out error))
                return false;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            try
            {
                result = InstallOrRepair(rendererAsset, features, shaders);
                Undo.CollapseUndoOperations(undoGroup);
                return true;
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                AssetDatabase.SaveAssetIfDirty(rendererAsset);
                error = "WebGpuWater could not update the renderer asset. All changes were reverted.\n\n" +
                        exception.Message;
                return false;
            }
        }

        static bool TryValidate(
            Object rendererAsset,
            out List<ValidatedFeature> features,
            out Dictionary<string, Shader> shaders,
            out string error)
        {
            features = new List<ValidatedFeature>();
            shaders = new Dictionary<string, Shader>();
            error = null;

            if (rendererAsset == null)
                return Fail("No default URP Renderer Data asset is assigned to the active pipeline.", out error);

            string rendererPath = AssetDatabase.GetAssetPath(rendererAsset);
            if (string.IsNullOrEmpty(rendererPath))
                return Fail("The default URP Renderer Data must be a saved project asset.", out error);
            if (!AssetDatabase.IsOpenForEdit(rendererAsset))
                return Fail("The default URP Renderer Data asset is read-only: " + rendererPath, out error);

            SerializedObject serializedRenderer = new SerializedObject(rendererAsset);
            SerializedProperty rendererFeatures = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            SerializedProperty rendererFeatureMap = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeatureMapProperty);
            if (rendererFeatures == null || !rendererFeatures.isArray ||
                rendererFeatureMap == null || !rendererFeatureMap.isArray)
                return Fail("The selected asset is not compatible URP Renderer Data.", out error);
            if (rendererFeatures.arraySize != rendererFeatureMap.arraySize)
                return Fail("The URP Renderer Data feature list and feature map are inconsistent. " +
                            "Open the renderer asset in the Inspector so URP can repair it, then retry.", out error);

            foreach (WaterRendererFeatureCatalog.Feature specification in WaterRendererFeatureCatalog.Features)
            {
                Type featureType = WaterRendererFeatureCatalog.ResolveFeatureType(specification.TypeName);
                if (featureType == null)
                    return Fail("Renderer feature type was not found: " + specification.TypeName, out error);
                if (!typeof(ScriptableObject).IsAssignableFrom(featureType))
                    return Fail("Renderer feature is not a ScriptableObject: " + specification.TypeName, out error);
                if (!TryValidateBindings(specification, featureType, shaders, out error))
                    return false;
                features.Add(new ValidatedFeature(specification, featureType));
            }
            return true;
        }

        static bool TryValidateBindings(
            WaterRendererFeatureCatalog.Feature specification,
            Type featureType,
            Dictionary<string, Shader> shaders,
            out string error)
        {
            error = null;
            foreach (WaterRendererFeatureCatalog.ShaderBinding binding in specification.ShaderBindings)
            {
                FieldInfo field = featureType.GetField(
                    binding.PropertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null || !typeof(Shader).IsAssignableFrom(field.FieldType))
                    return Fail("Shader field was not found on " + specification.TypeName + ": " +
                                binding.PropertyName, out error);

                if (shaders.ContainsKey(binding.ShaderPath)) continue;
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(binding.ShaderPath);
                if (shader == null)
                    return Fail("Required WebGpuWater shader was not found: " + binding.ShaderPath, out error);
                shaders.Add(binding.ShaderPath, shader);
            }
            return true;
        }

        static Result InstallOrRepair(
            Object rendererAsset,
            List<ValidatedFeature> features,
            Dictionary<string, Shader> shaders)
        {
            SerializedObject serializedRenderer = new SerializedObject(rendererAsset);
            SerializedProperty rendererFeatures = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            SerializedProperty rendererFeatureMap = serializedRenderer.FindProperty(
                WaterRendererFeatureCatalog.RendererFeatureMapProperty);
            Dictionary<string, Object> existing = CollectExistingFeatures(rendererFeatures);

            Undo.RegisterCompleteObjectUndo(rendererAsset, UndoName);
            int addedFeatureCount = 0;
            int repairedShaderCount = 0;
            foreach (ValidatedFeature feature in features)
            {
                if (existing.TryGetValue(feature.Specification.TypeName, out Object existingFeature))
                {
                    repairedShaderCount += RepairEmptyBindings(existingFeature, feature.Specification, shaders);
                    continue;
                }

                Object createdFeature = CreateFeature(rendererAsset, feature, shaders, out int assignedShaderCount);
                AppendFeature(rendererFeatures, rendererFeatureMap, createdFeature);
                existing.Add(feature.Specification.TypeName, createdFeature);
                addedFeatureCount++;
                repairedShaderCount += assignedShaderCount;
            }

            serializedRenderer.ApplyModifiedProperties();
            EditorUtility.SetDirty(rendererAsset);
            AssetDatabase.SaveAssetIfDirty(rendererAsset);
            return new Result(addedFeatureCount, repairedShaderCount);
        }

        static Dictionary<string, Object> CollectExistingFeatures(SerializedProperty rendererFeatures)
        {
            Dictionary<string, Object> existing = new Dictionary<string, Object>();
            for (int index = 0; index < rendererFeatures.arraySize; index++)
            {
                Object feature = rendererFeatures.GetArrayElementAtIndex(index).objectReferenceValue;
                if (feature != null && !existing.ContainsKey(feature.GetType().Name))
                    existing.Add(feature.GetType().Name, feature);
            }
            return existing;
        }

        static Object CreateFeature(
            Object rendererAsset,
            ValidatedFeature feature,
            Dictionary<string, Shader> shaders,
            out int assignedShaderCount)
        {
            ScriptableObject createdFeature = ScriptableObject.CreateInstance(feature.FeatureType);
            if (createdFeature == null)
                throw new InvalidOperationException("Could not create " + feature.Specification.TypeName + ".");

            createdFeature.name = feature.Specification.TypeName;
            Undo.RegisterCreatedObjectUndo(createdFeature, UndoName);
            assignedShaderCount = AssignEmptyBindings(createdFeature, feature.Specification, shaders);
            AssetDatabase.AddObjectToAsset(createdFeature, rendererAsset);
            EditorUtility.SetDirty(createdFeature);
            return createdFeature;
        }

        static int RepairEmptyBindings(
            Object feature,
            WaterRendererFeatureCatalog.Feature specification,
            Dictionary<string, Shader> shaders)
        {
            int missingCount = CountEmptyBindings(feature, specification);
            if (missingCount == 0) return 0;

            Undo.RecordObject(feature, UndoName);
            int assignedCount = AssignEmptyBindings(feature, specification, shaders);
            EditorUtility.SetDirty(feature);
            return assignedCount;
        }

        static int CountEmptyBindings(Object feature, WaterRendererFeatureCatalog.Feature specification)
        {
            SerializedObject serializedFeature = new SerializedObject(feature);
            int emptyCount = 0;
            foreach (WaterRendererFeatureCatalog.ShaderBinding binding in specification.ShaderBindings)
            {
                SerializedProperty shaderProperty = serializedFeature.FindProperty(binding.PropertyName);
                if (shaderProperty == null)
                    throw new InvalidOperationException(
                        "Shader field was not found on " + specification.TypeName + ": " + binding.PropertyName);
                if (shaderProperty.objectReferenceValue == null) emptyCount++;
            }
            return emptyCount;
        }

        static int AssignEmptyBindings(
            Object feature,
            WaterRendererFeatureCatalog.Feature specification,
            Dictionary<string, Shader> shaders)
        {
            SerializedObject serializedFeature = new SerializedObject(feature);
            int assignedCount = 0;
            foreach (WaterRendererFeatureCatalog.ShaderBinding binding in specification.ShaderBindings)
            {
                SerializedProperty shaderProperty = serializedFeature.FindProperty(binding.PropertyName);
                if (shaderProperty == null)
                    throw new InvalidOperationException(
                        "Shader field was not found on " + specification.TypeName + ": " + binding.PropertyName);
                if (shaderProperty.objectReferenceValue != null) continue;

                shaderProperty.objectReferenceValue = shaders[binding.ShaderPath];
                assignedCount++;
            }
            serializedFeature.ApplyModifiedPropertiesWithoutUndo();
            return assignedCount;
        }

        static void AppendFeature(
            SerializedProperty rendererFeatures,
            SerializedProperty rendererFeatureMap,
            Object feature)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId) || localId == 0)
                throw new InvalidOperationException("URP could not obtain a local asset ID for " + feature.name + ".");

            int featureIndex = rendererFeatures.arraySize;
            rendererFeatures.arraySize++;
            rendererFeatures.GetArrayElementAtIndex(featureIndex).objectReferenceValue = feature;

            int mapIndex = rendererFeatureMap.arraySize;
            rendererFeatureMap.arraySize++;
            rendererFeatureMap.GetArrayElementAtIndex(mapIndex).longValue = localId;
        }

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
