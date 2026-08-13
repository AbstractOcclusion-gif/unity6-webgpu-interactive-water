using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterRendererFeatureCheck
    {
        internal readonly struct Report
        {
            internal readonly Object RendererAsset;
            internal readonly List<string> MissingPurposes;
            internal readonly List<string> MissingShaderBindings;

            internal Report(
                Object rendererAsset,
                List<string> missingPurposes,
                List<string> missingShaderBindings)
            {
                RendererAsset = rendererAsset;
                MissingPurposes = missingPurposes;
                MissingShaderBindings = missingShaderBindings;
            }

            internal bool NeedsRepair =>
                MissingPurposes != null && MissingPurposes.Count > 0 ||
                MissingShaderBindings != null && MissingShaderBindings.Count > 0;
        }

        internal static Report Inspect()
        {
            Object rendererAsset = ResolveDefaultRendererAsset();
            if (rendererAsset == null) return default;

            Dictionary<string, Object> features = CollectFeatures(rendererAsset);
            if (features == null) return default;

            List<string> missingPurposes = new List<string>();
            List<string> missingShaderBindings = new List<string>();
            foreach (WaterRendererFeatureCatalog.Feature specification in WaterRendererFeatureCatalog.Features)
                InspectFeature(specification, features, missingPurposes, missingShaderBindings);

            return new Report(rendererAsset, missingPurposes, missingShaderBindings);
        }

        internal static Object ResolveDefaultRendererAsset()
        {
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return null;

            SerializedObject serializedPipeline = new SerializedObject(pipeline);
            SerializedProperty renderers = serializedPipeline.FindProperty(
                WaterRendererFeatureCatalog.RendererDataListProperty);
            SerializedProperty defaultIndex = serializedPipeline.FindProperty(
                WaterRendererFeatureCatalog.DefaultRendererIndexProperty);
            if (renderers == null || !renderers.isArray || renderers.arraySize == 0 || defaultIndex == null)
                return null;

            int index = defaultIndex.intValue;
            if (index < 0 || index >= renderers.arraySize) return null;
            return renderers.GetArrayElementAtIndex(index).objectReferenceValue;
        }

        internal static void Reveal(Object rendererAsset)
        {
            if (rendererAsset == null) return;
            Selection.activeObject = rendererAsset;
            EditorGUIUtility.PingObject(rendererAsset);
        }

        static Dictionary<string, Object> CollectFeatures(Object rendererAsset)
        {
            SerializedProperty features = new SerializedObject(rendererAsset).FindProperty(
                WaterRendererFeatureCatalog.RendererFeaturesProperty);
            if (features == null || !features.isArray) return null;

            Dictionary<string, Object> byTypeName = new Dictionary<string, Object>();
            for (int index = 0; index < features.arraySize; index++)
            {
                Object feature = features.GetArrayElementAtIndex(index).objectReferenceValue;
                if (feature != null && !byTypeName.ContainsKey(feature.GetType().Name))
                    byTypeName.Add(feature.GetType().Name, feature);
            }
            return byTypeName;
        }

        static void InspectFeature(
            WaterRendererFeatureCatalog.Feature specification,
            Dictionary<string, Object> features,
            List<string> missingPurposes,
            List<string> missingShaderBindings)
        {
            if (WaterRendererFeatureCatalog.ResolveFeatureType(specification.TypeName) == null) return;
            if (!features.TryGetValue(specification.TypeName, out Object feature))
            {
                missingPurposes.Add(specification.Purpose);
                return;
            }

            SerializedObject serializedFeature = new SerializedObject(feature);
            foreach (WaterRendererFeatureCatalog.ShaderBinding binding in specification.ShaderBindings)
            {
                SerializedProperty shaderProperty = serializedFeature.FindProperty(binding.PropertyName);
                if (shaderProperty != null && shaderProperty.objectReferenceValue == null)
                    missingShaderBindings.Add(specification.TypeName + ": " + binding.DisplayName);
            }
        }
    }
}
