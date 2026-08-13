// WebGpuWater build kit - water surface/pool material creation and the textures bound into them.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        internal static string ResolveOrCreateMaterialsFolder(WaterVolume volume)
        {
            Material material = volume != null && volume.surfaceAbove != null
                ? volume.surfaceAbove.sharedMaterial
                : null;
            string materialPath = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(materialPath))
            {
                string existingFolder = Path.GetDirectoryName(materialPath).Replace('\\', '/');
                if (existingFolder.StartsWith(WatersRoot + "/")) return existingFolder;
            }

            string materialsFolder = MaterialsFolder(CreateUniqueWaterFolder());
            EnsureFolder(materialsFolder);
            return materialsFolder;
        }

        // ---------------------------------------------------------------- materials
        // The above-water pass culls BACK faces; the underwater pass culls FRONT faces (inverted
        // from the shader's own defaults, which reads better here). The pool interior culls back
        // faces (_Cull maps to UnityEngine.Rendering.CullMode). Both surface materials enable REAL
        // screen-space refraction by default, so the water is transparent without hand-tweaking
        // (needs Opaque Texture + Depth Texture on the active URP asset).
        internal static (Material above, Material under, Material pool) CreateWaterMaterials(
            Shader sfWater, Shader sfPool, bool buildAnalyticPool, string folder)
        {
            float cullFront = (float)UnityEngine.Rendering.CullMode.Front;
            float cullBack = (float)UnityEngine.Rendering.CullMode.Back;
            var above = LoadOrCreateMaterial(folder + "/WaterAbove.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 0f); m.SetFloat(PropCull, cullBack); EnableRealRefraction(m); });
            var under = LoadOrCreateMaterial(folder + "/WaterUnder.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 1f); m.SetFloat(PropCull, cullFront); EnableRealRefraction(m); });
            // OUTSIDE the create-once lambda: LoadOrCreateMaterial only runs 'configure' when it
            // actually creates the asset, so a material built before the sprite existed could never
            // be healed - not by a rebuild, not by the inspector's Repair button. Slot-empty guards
            // (below) make this safe to re-run over a hand-tuned material.
            AssignFoamFlipbook(above);
            Material pool = null;
            if (buildAnalyticPool && sfPool != null)
            {
                pool = LoadOrCreateMaterial(folder + "/Pool.mat", sfPool, m => m.SetFloat(PropCull, cullBack));
                AssignPackagedSpriteIfEmpty(pool, PropBumpMap, TilesNormalTextureFile);
            }
            return (above, under, pool);
        }

        // Turn on the surface shader's real (screen-space) refraction toggle. The mode is
        // UNIFORM-driven: no shader in the package declares a _REAL_REFRACTION keyword, so the
        // EnableKeyword call that used to sit here only ever wrote a keyword nothing read (it is
        // still baked into the demo materials, harmlessly).
        static void EnableRealRefraction(Material m)
        {
            m.SetFloat(PropRealRefraction, 1f);
        }

        // Give a water surface material the animated foam pattern, and the grid that decodes it.
        // The two MUST move together: the sheet without _FoamTexFrames plays as one frozen frame.
        // Relief is procedural (finite differences of the pattern, like the ocean whitecap), so no
        // normal-map assignment; the generated FoamFlipbookNormal asset stays on disk for old
        // materials that still serialize it.
        internal static void AssignFoamFlipbook(Material material)
        {
            if (material == null || material.GetTexture(PropFoamTex) != null) return;

            var flipbook = LoadDefaultTexture(FoamFlipbookFile);
            if (flipbook == null) return; // LoadDefaultTexture already named the missing file

            material.SetTexture(PropFoamTex, flipbook);
            material.SetVector(PropFoamTexFrames, new Vector4(FoamFlipbookCols, FoamFlipbookRows, 0f, 0f));
            EditorUtility.SetDirty(material);
        }

        // Fill ONE sprite slot from the package's shipped art, and only when it is empty. Every
        // wiring path routes through here so a re-run or a Repair heals a missing sprite without
        // ever clobbering one the user picked by hand.
        internal static void AssignPackagedSpriteIfEmpty(Material material, string property, string textureFile)
        {
            if (material == null || material.GetTexture(property) != null) return;

            var texture = LoadDefaultTexture(textureFile);
            if (texture == null) return; // LoadDefaultTexture already named the missing file

            material.SetTexture(property, texture);
            EditorUtility.SetDirty(material);
        }

        // Shipped art from the package's imported Runtime/Defaults/Textures folder. Loaded through the
        // AssetDatabase with NO importer rewrite: on a registry or tarball install the package
        // folder is IMMUTABLE, so a SaveAndReimport here would fail - and the authored .meta files
        // already carry the right settings. Null (with a loud warning) when the copy is missing, so
        // a broken install fails visibly instead of silently building an untextured body.
        internal static Texture2D LoadDefaultTexture(string fileName)
        {
            string path = DefaultTexturesRoot + "/" + fileName;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
                Debug.LogWarning(LogPrefix + $"packaged texture '{fileName}' not found at '{path}'; " +
                                 "the corresponding slot stays empty and the shader falls back to flat white.");
            return texture;
        }

        // Underwater god-ray volume (caustic-masked light shafts). Returns null if the shader is
        // missing (the feature is simply absent then).
        internal static GameObject CreateGodRays(Transform parent, string folder)
        {
            var sfGodRays = Shader.Find(ShaderGodRays);
            if (sfGodRays == null) return null;

            var godRayMat = LoadOrCreateMaterial(folder + "/GodRays.mat", sfGodRays,
                                                 m =>
                                                 {
                                                     m.SetColor(PropGodRayColor, DefaultGodRayColor);
                                                     m.SetFloat(PropGodRayDensity, DefaultGodRayDensity);
                                                 });
            Mesh godRayMesh = LoadRequiredDefault<Mesh>(GodRayBoxMeshPath, "god-ray box mesh");
            if (godRayMesh == null) return null;
            var go = CreateRenderer(GodRaysObjectName, godRayMesh, godRayMat, parent);
            var gmr = go.GetComponent<MeshRenderer>();
            gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gmr.receiveShadows = false;
            return go;
        }

    }
}
