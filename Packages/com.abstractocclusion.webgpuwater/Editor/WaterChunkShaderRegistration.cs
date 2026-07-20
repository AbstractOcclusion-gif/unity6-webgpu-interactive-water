// WebGpuWater - ensure the chunk shaders ship in player builds.
// The chunk resolves WaterChunkWall by name at runtime (Shader.Find), and the mesh-chunk depth
// prepass resolves WaterChunkDepth the same way (via its render feature material). A packaged shader
// used solely from code is NOT pulled into a build automatically, so without this the chunk would
// render in the editor but vanish in a player. One idempotent add per shader to GraphicsSettings'
// Always Included Shaders (only when missing - no VCS churn), so no slot ever has to be filled by hand.
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [InitializeOnLoad]
    internal static class WaterChunkShaderRegistration
    {
        const string AlwaysIncludedProperty = "m_AlwaysIncludedShaders";

        static readonly string[] RequiredShaders =
        {
            WaterShaderNames.WaterChunkWall,
            WaterShaderNames.WaterChunkDepth,
        };

        // delayCall: mutating a settings asset during the InitializeOnLoad callback itself is flaky;
        // deferring one tick runs it on a settled editor.
        static WaterChunkShaderRegistration() => EditorApplication.delayCall += EnsureIncluded;

        static void EnsureIncluded()
        {
            var settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty shaders = settings.FindProperty(AlwaysIncludedProperty);
            if (shaders == null) return;

            bool changed = false;
            foreach (string shaderName in RequiredShaders)
                changed |= EnsureShader(shaders, shaderName);

            if (changed) settings.ApplyModifiedProperties();
        }

        // Adds one shader to the Always Included list if it is imported and not already listed.
        // Returns whether it was added.
        static bool EnsureShader(SerializedProperty shaders, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) return false; // not imported yet; the next domain reload retries

            for (int i = 0; i < shaders.arraySize; i++)
                if (shaders.GetArrayElementAtIndex(i).objectReferenceValue == shader) return false; // already listed

            int index = shaders.arraySize;
            shaders.InsertArrayElementAtIndex(index);
            shaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            Debug.Log($"[WebGpuWater] Added '{shaderName}' to Always Included Shaders so it ships in player builds.");
            return true;
        }
    }
}
