// WebGpuWater - editor utility: convert selected object(s) to the WaterReceiver shader so ANY mesh
// (Standard Lit, URP Lit, whatever) receives the REFRACTED underwater shadow + projected caustics.
//
// Why a converter and not a "script that patches Standard Lit": the refracted-shadow logic lives in
// shader code (sampling the caustic occluder + the depth gate). A MonoBehaviour / MaterialPropertyBlock
// can only push property VALUES, it cannot add that logic to a shader it doesn't own, and URP exposes no
// per-shader hook to bend its shadow. So the only way to give an arbitrary object the trick is to move it
// onto a shader that does it - WaterReceiver, which is itself a full lit shader (albedo, normal, smoothness,
// spec) plus the water-column effects. This tool swaps the material and carries those inputs over.
//
// Caveats surfaced to the user: WaterReceiver is Blinn-Phong lit (no metalness - a metal source looks
// simpler), and the object must sit in a body's footprint; a WaterMembership is added so multi-body scenes
// resolve the containing body (a single-body scene works off the primary's globals regardless).
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterReceiverConverter
    {
        const string MenuConvert = MenuRoot + "Convert Selection To Water Receiver";
        // From the shared registry, not retyped: this was the ONE shader name in the package still
        // written out by hand, so a rename in WaterShaderNames would have missed it silently.
        const string ReceiverShaderName = WaterShaderNames.WaterReceiver;
        // Converted materials are written here (create-once, reused on re-run) so scenes keep a real asset
        // reference instead of a leaked runtime instance.
        internal const string OutputFolder = "Assets/WebGpuWaterConverted";

        // Target (WaterReceiver) property names.
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

        [MenuItem(MenuConvert, priority = 410)]
        static void ConvertSelection()
        {
            Shader receiver = Shader.Find(ReceiverShaderName);
            if (receiver == null)
            {
                Debug.LogError($"[WebGpuWater] Shader '{ReceiverShaderName}' not found - is the package present?");
                return;
            }

            GameObject[] roots = Selection.gameObjects;
            if (roots.Length == 0)
            {
                Debug.LogWarning("[WebGpuWater] Select one or more objects to convert first.");
                return;
            }

            EnsureOutputFolder();
            // One converted material per SOURCE material, so shared source materials map to one asset.
            var converted = new Dictionary<Material, Material>();
            int renderers = 0, membershipsAdded = 0;

            foreach (GameObject root in roots)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
                {
                    if (ConvertRenderer(renderer, receiver, converted)) renderers++;
                    if (EnsureMembership(renderer.gameObject)) membershipsAdded++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Converted {renderers} renderer(s) to WaterReceiver " +
                      $"({converted.Count} material(s) created/reused, {membershipsAdded} WaterMembership added).");
        }

        [MenuItem(MenuConvert, validate = true)]
        static bool ConvertSelectionValidate() => Selection.gameObjects.Length > 0;

        // Swap every material slot on this renderer that isn't already a WaterReceiver. Returns true if
        // the renderer was touched.
        static bool ConvertRenderer(Renderer renderer, Shader receiver, Dictionary<Material, Material> cache)
        {
            Material[] slots = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < slots.Length; i++)
            {
                Material source = slots[i];
                if (source == null || source.shader == receiver) continue;
                slots[i] = GetOrCreateReceiverMaterial(source, receiver, cache);
                changed = true;
            }
            if (!changed) return false;

            Undo.RecordObject(renderer, "Convert To Water Receiver");
            renderer.sharedMaterials = slots;
            EditorUtility.SetDirty(renderer);
            return true;
        }

        static Material GetOrCreateReceiverMaterial(Material source, Shader receiver,
                                                    Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(source, out Material existingInRun)) return existingInRun;

            // Sanitise: a Material's name is free text and may contain path separators or other
            // characters illegal in a file name. Unsanitised, a material called "Wood/Oak" produced
            // the path "<OutputFolder>/Wood/Oak_WaterReceiver.mat" - a subfolder that does not exist,
            // so CreateAsset failed silently mid-loop and left the renderer on a null material.
            string safeName = SanitizeAssetName(source.name);
            string path = $"{OutputFolder}/{safeName}_WaterReceiver.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(receiver) { name = safeName + "_WaterReceiver" };
                AssetDatabase.CreateAsset(material, path);
            }
            CopyLitInputs(source, material);
            EditorUtility.SetDirty(material);
            cache[source] = material;
            return material;
        }

        // Material names are arbitrary user text; asset paths are not. Replace every character the
        // filesystem rejects (this includes '/' and '\', so a "grouped" material name can no longer
        // silently target a non-existent subfolder) and fall back for an empty/whitespace name.
        const string UnnamedMaterialFallback = "Material";
        internal static string SanitizeAssetName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return UnnamedMaterialFallback;
            string cleaned = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars())).Trim();
            return string.IsNullOrEmpty(cleaned) ? UnnamedMaterialFallback : cleaned;
        }

        // Carry the standard lit inputs across by name (with built-in Standard fallbacks). Water-specific
        // fields (caustic tint/strength, underwater tint, tiling) are left at the shader defaults.
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

        // A WaterReceiver object reads the sim/caustic/volume uniforms as GLOBALS from the primary body;
        // WaterMembership republishes the CONTAINING body's uniforms so it also works in secondary bodies.
        // Additive and idempotent - only added when missing.
        static bool EnsureMembership(GameObject go)
        {
            if (go.GetComponent<WaterMembership>() != null) return false;
            Undo.AddComponent<WaterMembership>(go);
            return true;
        }

        internal static void EnsureOutputFolder()
        {
            if (AssetDatabase.IsValidFolder(OutputFolder)) return;
            string parent = Path.GetDirectoryName(OutputFolder).Replace('\\', '/');
            string leaf = Path.GetFileName(OutputFolder);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
