// WebGpuWater - editor-side presence check for the package's URP renderer features.
// Five of the package's effects live on the URP RENDERER asset, not on a water body: underwater fog,
// screen-space caustics, the mesh-chunk and mesh-exclusion depth prepasses, and the ocean god-ray
// atmosphere. Every one of them SELF-GATES, which means a missing feature is silent - the effect is
// simply absent, and that reads as "this feature is broken" rather than "this feature was never
// installed". Listing the absences at authoring time is the whole point of this file.
//
// PRESENCE ONLY, deliberately. A need-aware check would have to answer "does THIS scene want fog?",
// and only three of the five runtime gates have an authoring-time predicate at all
// (WaterVolume.UnderwaterFogActive is camera-submerged, which means nothing while editing). Inventing
// the missing two would trade a missing warning for a WRONG one, so this reports absence as
// information and leaves the judgement to the user - the same honesty as the
// "only needed if you use those features" wording on the Always Included Shaders warning.
//
// The renderer list is read through SerializedObject instead of URP's typed API on purpose: this
// assembly references only the package runtime, so touching UniversalRenderPipelineAsset would mean
// adding a URP assembly reference AND duplicating the WEBGPUWATER_URP versionDefine here. The
// serialized route needs neither, and it is the same idiom WaterChunkShaderRegistration already uses
// for m_AlwaysIncludedShaders. Every lookup fails soft: anything this cannot resolve, it stays quiet
// about rather than nagging.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterRendererFeatureCheck
    {
        // Serialized field names on UniversalRenderPipelineAsset and ScriptableRendererData
        // (both [SerializeField] internal in URP 17). A rename in a future URP surfaces as a null
        // property, which Inspect treats as "cannot tell" - never as "the feature is missing".
        const string RendererDataListProperty = "m_RendererDataList";
        const string RendererFeaturesProperty = "m_RendererFeatures";
        const string FeatureNamespacePrefix = "AbstractOcclusion.WebGpuWater.";

        // Feature type name -> what the user loses without it. Type NAMES rather than typeof(): the
        // feature classes compile only under WEBGPUWATER_URP, which this assembly does not define, so a
        // typeof() reference would fail to build in a project without URP. Each name is resolved against
        // the runtime assembly before it is trusted - see ResolveFeatureType.
        static readonly (string TypeName, string Purpose)[] Features =
        {
            ("WaterUnderwaterFogFeature",     "Underwater fog while the camera is submerged"),
            ("WaterCausticProjectionFeature", "Screen-space caustics on terrain and other non-water surfaces"),
            ("WaterChunkDepthFeature",        "Mesh-footprint water chunks"),
            ("WaterExclusionDepthFeature",    "Mesh-shaped exclusion volumes"),
            ("LargeBodyAtmosphereFeature",    "Ocean god-ray shafts"),
        };

        /// <summary>What the ACTIVE render pipeline is missing, plus the renderer asset to point at.</summary>
        internal readonly struct Report
        {
            internal readonly Object RendererAsset;
            internal readonly List<string> MissingPurposes;

            internal Report(Object rendererAsset, List<string> missingPurposes)
            {
                RendererAsset = rendererAsset;
                MissingPurposes = missingPurposes;
            }

            internal bool AnyMissing => MissingPurposes != null && MissingPurposes.Count > 0;
        }

        /// <summary>
        /// Which package renderer features are absent from the pipeline asset in use for the CURRENT
        /// quality level. Returns an empty report - never a warning - when the answer cannot be trusted.
        /// </summary>
        internal static Report Inspect()
        {
            // The asset for the current quality level, falling back to the default: exactly what
            // UniversalRenderPipeline.asset resolves to, without needing the URP type to say so.
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null) return default; // Built-in pipeline, or none: nothing to check against

            SerializedProperty renderers = new SerializedObject(pipeline).FindProperty(RendererDataListProperty);
            if (renderers == null || !renderers.isArray || renderers.arraySize == 0) return default;

            HashSet<string> present = CollectFeatureTypeNames(renderers);
            List<string> missing = new List<string>();
            foreach ((string typeName, string purpose) in Features)
            {
                if (ResolveFeatureType(typeName) == null) continue; // renamed, or no URP: cannot tell
                if (!present.Contains(typeName)) missing.Add(purpose);
            }
            return new Report(FirstRenderer(renderers), missing);
        }

        /// <summary>Select and ping a renderer asset so the user can add the features by hand.</summary>
        internal static void Reveal(Object rendererAsset)
        {
            if (rendererAsset == null) return;
            Selection.activeObject = rendererAsset;
            EditorGUIUtility.PingObject(rendererAsset);
        }

        // Union across EVERY renderer in the asset, not only the default one. A feature has to sit on
        // the renderer the water camera actually uses, and which renderer that is depends on the camera -
        // so "wired somewhere in this asset" is the strongest claim a presence-only check can honestly
        // make, and the only one that raises no false alarm on a multi-renderer setup.
        static HashSet<string> CollectFeatureTypeNames(SerializedProperty renderers)
        {
            HashSet<string> names = new HashSet<string>();
            for (int i = 0; i < renderers.arraySize; i++)
            {
                Object rendererData = renderers.GetArrayElementAtIndex(i).objectReferenceValue;
                if (rendererData == null) continue;

                SerializedProperty features =
                    new SerializedObject(rendererData).FindProperty(RendererFeaturesProperty);
                if (features == null || !features.isArray) continue;

                for (int f = 0; f < features.arraySize; f++)
                {
                    Object feature = features.GetArrayElementAtIndex(f).objectReferenceValue;
                    if (feature != null) names.Add(feature.GetType().Name);
                }
            }
            return names;
        }

        // The feature classes live in the runtime assembly; WaterVolume is the unconditional anchor into
        // it (public, and compiled with or without URP). An unresolvable name means the class was renamed
        // or URP is absent - either way this must not claim the feature is missing from a renderer.
        static System.Type ResolveFeatureType(string typeName) =>
            typeof(WaterVolume).Assembly.GetType(FeatureNamespacePrefix + typeName);

        // The first non-null renderer is what a user means by "my renderer" and what URP's
        // Add Renderer Feature button lives on.
        static Object FirstRenderer(SerializedProperty renderers)
        {
            for (int i = 0; i < renderers.arraySize; i++)
            {
                Object rendererData = renderers.GetArrayElementAtIndex(i).objectReferenceValue;
                if (rendererData != null) return rendererData;
            }
            return null;
        }
    }
}
