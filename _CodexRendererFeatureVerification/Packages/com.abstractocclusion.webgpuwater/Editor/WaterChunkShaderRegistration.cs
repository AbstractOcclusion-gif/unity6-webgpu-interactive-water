// WebGpuWater - opt-in registration of the code-resolved chunk and exclusion shaders for player builds.
// Both systems resolve their wall shader by name at runtime (Shader.Find) and their mesh depth prepass
// the same way (via the render feature's material). A packaged shader used solely from code is NOT
// pulled into a build automatically, so without registration they render in the editor and vanish in a
// player.
//
// WHY THIS IS NOT AUTOMATIC: the fix lives in GraphicsSettings' Always Included Shaders, which is the
// USER'S project setting, and every entry there compiles ALL of that shader's variants into EVERY
// build - including builds by users who never place a chunk or an exclusion volume. A package silently
// editing project settings on import is both a build-size cost they did not ask for and an Asset Store
// review flag, so this is an explicit, idempotent command instead.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterChunkShaderRegistration
    {
        const string AlwaysIncludedProperty = "m_AlwaysIncludedShaders";
        const string MenuPath = WaterBuildKit.MenuRoot + "Register Chunk Shaders For Builds";
        const string DialogTitle = WaterBuildKit.ProductName;

        static readonly string[] RequiredShaders =
        {
            WaterShaderNames.WaterChunkWall,
            WaterShaderNames.WaterChunkDepth,
            WaterShaderNames.WaterExclusionWall,
            WaterShaderNames.WaterExclusionDepth,
        };

        /// <summary>True when at least one code-resolved shader is missing from Always Included
        /// Shaders, i.e. chunks/exclusion volumes would vanish in a player build. The wizard surfaces
        /// this so the user is told BEFORE they build, not after.</summary>
        internal static bool AnyShaderMissing()
        {
            SerializedProperty shaders = FindAlwaysIncludedList(out _);
            if (shaders == null) return false; // cannot tell - never nag on a broken lookup

            foreach (string shaderName in RequiredShaders)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader != null && !IsListed(shaders, shader)) return true;
            }
            return false;
        }

        [MenuItem(MenuPath)]
        internal static void RegisterAll()
        {
            SerializedProperty shaders = FindAlwaysIncludedList(out SerializedObject settings);
            if (shaders == null)
            {
                EditorUtility.DisplayDialog(DialogTitle,
                    $"Could not read Graphics Settings ('{AlwaysIncludedProperty}'). Register the chunk " +
                    "and exclusion wall shaders by hand under Project Settings > Graphics > Always " +
                    "Included Shaders.", "OK");
                return;
            }

            int added = 0;
            int missingFromProject = 0;
            foreach (string shaderName in RequiredShaders)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null) { missingFromProject++; continue; }
                if (IsListed(shaders, shader)) continue;

                int index = shaders.arraySize;
                shaders.InsertArrayElementAtIndex(index);
                shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
                added++;
            }

            if (added > 0)
            {
                settings.ApplyModifiedProperties();
                AssetDatabase.SaveAssets(); // ApplyModifiedProperties only dirties the asset
            }

            EditorUtility.DisplayDialog(DialogTitle, BuildReport(added, missingFromProject), "OK");
        }

        static string BuildReport(int added, int missingFromProject)
        {
            if (missingFromProject > 0)
                return $"{missingFromProject} of the {RequiredShaders.Length} shaders could not be found " +
                       "in this project - reimport the package, then run this again.";
            if (added == 0)
                return "All chunk and exclusion shaders are already registered. Nothing to do.";
            return $"Added {added} shader(s) to Always Included Shaders. Water chunks and exclusion " +
                   "volumes will now render in player builds.\n\nRemove them under Project Settings > " +
                   "Graphics if you stop using those features.";
        }

        static SerializedProperty FindAlwaysIncludedList(out SerializedObject settings)
        {
            settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            return settings.FindProperty(AlwaysIncludedProperty);
        }

        static bool IsListed(SerializedProperty shaders, Shader shader)
        {
            for (int i = 0; i < shaders.arraySize; i++)
                if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) return true;
            return false;
        }
    }
}
