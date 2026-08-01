// WebGpuWater - inspector for WaterFogTransparent.
//
// WHY THIS EXISTS: the component is the SORTING half of the public fog API and nothing else. Its
// silent failure mode is that it works perfectly - the prop stops dying behind the water sheet -
// while the material ignores every water uniform, because the medium is shader code the material
// has to include (WebGpuWaterFogAPI.hlsl). "Visible in the water but not reacting to depth or
// Water Opacity" is that state, and with no inspector it reads as a broken feature rather than as
// a missing include. This editor names the state and offers the one-click way out.
//
// The check is deliberately CONSERVATIVE. Unity exposes no way to ask a Shader which files it
// included, so a shader that is not the package's own WaterTransparent cannot be proven either
// way - a hand-written shader or a Shader Graph with the WebGpuWaterFog Custom Function node is
// perfectly valid. So a non-package shader raises "cannot verify", never "wrong".
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterFogTransparent))]
    [CanEditMultipleObjects]
    internal sealed class WaterFogTransparentEditor : UnityEditor.Editor
    {
        const string RoleHelp =
            "SORTING ONLY. This component suppresses the queue-time draw and has the water feature " +
            "re-draw this renderer after the whole water stack, over restored opaque depth - that is " +
            "what keeps it visible through the sheet from either side.\n\n" +
            "It cannot tint anything. Depth attenuation, Water Opacity, path extinction and the lamp " +
            "glow come from the MATERIAL, via WebGpuWaterFogAPI.hlsl.";
        const string PlayModeHelp =
            "The reroute is play-mode only by design: LateUpdate does not run in edit mode, so the " +
            "Scene view shows this renderer un-rerouted. Enter play mode to judge it.";
        const string NoRendererHelp =
            "No Renderer on this GameObject - the component is inert.";
        const string UnverifiedHelpFormat =
            "Cannot verify that this material carries the water medium.\n\n" +
            "Material '{0}' uses shader '{1}'. If that shader does NOT include " +
            "WebGpuWaterFogAPI.hlsl and apply the WebGpuWaterFogTransparent mul/add pair to its " +
            "final colour, this prop will be VISIBLE in the water but will not react to depth or " +
            "Water Opacity - from above or from under.\n\n" +
            "Hand-written shader: add the include, then 'rgb = rgb * fogMul + fogAdd' after your " +
            "albedo multiply. Shader Graph: a Custom Function node in File mode pointing at that " +
            "header, function name 'WebGpuWaterFog'.";
        const string ConvertButtonLabel = "Convert to Water Transparent";
        const string ConvertButtonHelp =
            "Swaps every unverified material on this renderer onto the package's WaterTransparent " +
            "shader, carrying the lit inputs over. Same action as the menu item.";
        // The converter's own const, not a retyped copy - see its note.
        const string ConvertMenuPath = WaterTransparentConverter.MenuConvert;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(RoleHelp, MessageType.Info);
            EditorGUILayout.HelpBox(PlayModeHelp, MessageType.Info);

            // Multi-edit: the per-material verdict is per-object, so report on the single target only
            // rather than showing a verdict that is true for one of the selection.
            if (targets.Length > 1) return;

            var component = (WaterFogTransparent)target;
            var renderer = component.GetComponent<Renderer>();
            if (renderer == null)
            {
                EditorGUILayout.HelpBox(NoRendererHelp, MessageType.Warning);
                return;
            }

            if (!TryFindUnverifiedMaterial(renderer, out Material unverified)) return;

            EditorGUILayout.HelpBox(
                string.Format(UnverifiedHelpFormat, unverified.name, unverified.shader.name),
                MessageType.Warning);
            EditorGUILayout.HelpBox(ConvertButtonHelp, MessageType.None);
            if (GUILayout.Button(ConvertButtonLabel))
                EditorApplication.ExecuteMenuItem(ConvertMenuPath);
        }

        // The FIRST material whose shader is not the package's own WaterTransparent. Null shaders and
        // empty slots are skipped: they are a different problem and Unity already flags them.
        static bool TryFindUnverifiedMaterial(Renderer renderer, out Material unverified)
        {
            unverified = null;
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.shader == null) continue;
                if (material.shader.name == WaterShaderNames.WaterTransparent) continue;
                unverified = material;
                return true;
            }
            return false;
        }
    }
}
#endif
