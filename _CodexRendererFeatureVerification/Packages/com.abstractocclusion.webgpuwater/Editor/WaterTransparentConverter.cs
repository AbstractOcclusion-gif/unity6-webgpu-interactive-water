// WebGpuWater - editor utility: convert selected object(s) to the WaterTransparent shader so an
// alpha-blended prop actually REACTS to the water it sits in (depth attenuation, Water Opacity,
// path extinction, lamp glow) instead of merely being visible in it.
//
// Why a converter and not a "script that patches URP Lit Transparent": the same wall
// WaterReceiverConverter already documents for opaques. WaterFogTransparent.cs is the SORTING half
// of the public API - it suppresses the queue-time draw and has the water feature re-draw the
// renderer after the whole water stack, which is what stops a transparent dying behind the sheet's
// ZWrite On depth. It cannot tint anything: a MonoBehaviour can only push property VALUES, and the
// medium is shader CODE (WebGpuWaterFogAPI.hlsl). So the material has to move onto a shader that
// owns that code - WaterTransparent, which is a full lit transparent (albedo, normal, smoothness,
// spec) plus the water medium.
//
// This converter does BOTH halves in one action, which is the whole point: swapping the shader
// without adding WaterFogTransparent leaves the prop tinted but dying behind the sheet on
// cross-side views, and adding the component without swapping the shader is exactly the
// "displaying but not reacting" state this tool exists to end.
//
// Caveats surfaced to the user: WaterTransparent is Blinn-Phong lit (no metalness, matching
// WaterReceiver so a transparent prop and an opaque one agree at the waterline); it writes no
// depth and casts no shadow by design (see the shader's tail comment); and the reroute is
// PLAY-MODE ONLY - the component's LateUpdate does not run in edit mode, so the Scene view still
// shows the un-rerouted prop.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterTransparentConverter
    {
        // Internal so WaterFogTransparentEditor's "Convert" button drives the SAME menu path rather
        // than retyping it - a rename here must not leave a dead button behind.
        internal const string MenuConvert = MenuRoot + "Convert Selection To Water Transparent";
        // From the shared registry, not retyped - the same rule WaterReceiverConverter follows.
        const string TransparentShaderName = WaterShaderNames.WaterTransparent;
        const string UndoLabel = "Convert To Water Transparent";
        const string MaterialSuffix = "_WaterTransparent";
        // Converted materials share WaterReceiverConverter's output folder so a scene keeps ALL its
        // converted assets in one place rather than growing a second parallel directory.
        const string OutputFolder = WaterReceiverConverter.OutputFolder;

        // Target (WaterTransparent) property names.
        const string PropBaseColor = "_BaseColor";
        const string PropBaseMap = "_BaseMap";
        const string PropBumpMap = "_BumpMap";
        const string PropBumpScale = "_BumpScale";
        const string PropSmoothness = "_Smoothness";
        const string PropSpecColor = "_SpecColor";
        // Source fallbacks (built-in Standard uses _Color/_MainTex/_Glossiness; URP Lit matches the target).
        const string SrcColorLegacy = "_Color";
        const string SrcMainTexLegacy = "_MainTex";
        const string SrcGlossinessLegacy = "_Glossiness";

        [MenuItem(MenuConvert, priority = 412)]
        static void ConvertSelection()
        {
            Shader transparent = Shader.Find(TransparentShaderName);
            if (transparent == null)
            {
                Debug.LogError($"[WebGpuWater] Shader '{TransparentShaderName}' not found - is the package present?");
                return;
            }

            GameObject[] roots = Selection.gameObjects;
            if (roots.Length == 0)
            {
                Debug.LogWarning("[WebGpuWater] Select one or more objects to convert first.");
                return;
            }

            WaterReceiverConverter.EnsureOutputFolder();
            // One converted material per SOURCE material, so shared source materials map to one asset.
            var converted = new Dictionary<Material, Material>();
            int renderers = 0, componentsAdded = 0, membershipsAdded = 0;

            foreach (GameObject root in roots)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (ConvertRenderer(renderer, transparent, converted)) renderers++;
                    if (EnsureReroute(renderer.gameObject)) componentsAdded++;
                    if (EnsureMembership(renderer.gameObject)) membershipsAdded++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Converted {renderers} renderer(s) to WaterTransparent " +
                      $"({converted.Count} material(s) created/reused, {componentsAdded} WaterFogTransparent " +
                      $"added, {membershipsAdded} WaterMembership added). The reroute is play-mode only: " +
                      "the Scene view still shows the un-rerouted prop.");
        }

        [MenuItem(MenuConvert, validate = true)]
        static bool ConvertSelectionValidate() => Selection.gameObjects.Length > 0;

        // Swap every material slot on this renderer that isn't already a WaterTransparent. Returns true
        // if the renderer was touched.
        static bool ConvertRenderer(Renderer renderer, Shader transparent, Dictionary<Material, Material> cache)
        {
            Material[] slots = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                Material source = slots[i];
                if (source == null || source.shader == transparent) continue;
                slots[i] = GetOrCreateTransparentMaterial(source, transparent, cache);
                changed = true;
            }
            if (!changed) return false;

            Undo.RecordObject(renderer, UndoLabel);
            renderer.sharedMaterials = slots;
            EditorUtility.SetDirty(renderer);
            return true;
        }

        static Material GetOrCreateTransparentMaterial(Material source, Shader transparent,
                                                       Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(source, out Material existingInRun)) return existingInRun;

            // Sanitised for the same reason WaterReceiverConverter sanitises: a material name is free
            // text and may carry path separators, which silently targeted a non-existent subfolder.
            string safeName = WaterReceiverConverter.SanitizeAssetName(source.name);
            string path = $"{OutputFolder}/{safeName}{MaterialSuffix}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(transparent) { name = safeName + MaterialSuffix };
                AssetDatabase.CreateAsset(material, path);
            }
            CopyLitInputs(source, material);
            EditorUtility.SetDirty(material);
            cache[source] = material;
            return material;
        }

        // Carry the standard lit inputs across by name (with built-in Standard fallbacks). The ALPHA of
        // the source colour comes with it, so a material already authored as half-transparent keeps
        // its opacity; water-specific fields are left at the shader defaults.
        static void CopyLitInputs(Material src, Material dst)
        {
            dst.SetColor(PropBaseColor, FirstColor(src, dst.GetColor(PropBaseColor), PropBaseColor, SrcColorLegacy));

            Texture baseMap = FirstTexture(src, PropBaseMap, SrcMainTexLegacy);
            if (baseMap != null) dst.SetTexture(PropBaseMap, baseMap);

            if (src.HasProperty(PropBumpMap))
            {
                Texture bump = src.GetTexture(PropBumpMap);
                if (bump != null) dst.SetTexture(PropBumpMap, bump);
            }
            if (src.HasProperty(PropBumpScale)) dst.SetFloat(PropBumpScale, src.GetFloat(PropBumpScale));

            dst.SetFloat(PropSmoothness,
                FirstFloat(src, dst.GetFloat(PropSmoothness), PropSmoothness, SrcGlossinessLegacy));

            if (src.HasProperty(PropSpecColor)) dst.SetColor(PropSpecColor, src.GetColor(PropSpecColor));
        }

        static Color FirstColor(Material src, Color fallback, params string[] names)
        {
            foreach (string n in names)
                if (src.HasProperty(n)) return src.GetColor(n);
            return fallback;
        }

        static Texture FirstTexture(Material src, params string[] names)
        {
            foreach (string n in names)
                if (src.HasProperty(n) && src.GetTexture(n) != null) return src.GetTexture(n);
            return null;
        }

        static float FirstFloat(Material src, float fallback, params string[] names)
        {
            foreach (string n in names)
                if (src.HasProperty(n)) return src.GetFloat(n);
            return fallback;
        }

        // The SORTING half. Without it the shader tints correctly but the prop still dies behind the
        // sheet's ZWrite On depth on any cross-side view, which reads as "the converter broke it".
        // Additive and idempotent - only added when missing.
        static bool EnsureReroute(GameObject go)
        {
            if (go.GetComponent<WaterFogTransparent>() != null) return false;
            Undo.AddComponent<WaterFogTransparent>(go);
            return true;
        }

        // WaterTransparent reads the fog/volume uniforms as GLOBALS from the primary body;
        // WaterMembership republishes the CONTAINING body's uniforms so it also works in secondary
        // bodies. Additive and idempotent - only added when missing.
        static bool EnsureMembership(GameObject go)
        {
            if (go.GetComponent<WaterMembership>() != null) return false;
            Undo.AddComponent<WaterMembership>(go);
            return true;
        }
    }
}
#endif
