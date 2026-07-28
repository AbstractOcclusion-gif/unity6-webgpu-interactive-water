// WebGpuWater build kit - water surface/pool material creation and the textures bound into them.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
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
                                             m => { m.SetFloat(PropUnderwater, 0f); m.SetFloat(PropCull, cullBack); EnableRealRefraction(m); AssignFoamFlipbook(m); });
            var under = LoadOrCreateMaterial(folder + "/WaterUnder.mat", sfWater,
                                             m => { m.SetFloat(PropUnderwater, 1f); m.SetFloat(PropCull, cullFront); EnableRealRefraction(m); });
            Material pool = null;
            if (buildAnalyticPool && sfPool != null)
                pool = LoadOrCreateMaterial(folder + "/Pool.mat", sfPool, m => m.SetFloat(PropCull, cullBack));
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

        // Give a water surface material the animated foam pattern. Skipped silently when the
        // flipbook asset is absent: the shader's white default degrades to flat foam. Relief
        // is procedural now (finite differences of the pattern, like the ocean whitecap), so
        // no normal-map assignment; the generated FoamFlipbookNormal asset stays on disk for
        // old materials that still serialize it.
        internal static void AssignFoamFlipbook(Material m)
        {
            var flipbook = LoadFlipbook(FoamFlipbookPath, TextureWrapMode.Repeat, true);
            if (flipbook == null) return;
            m.SetTexture(PropFoamTex, flipbook);
            m.SetVector(PropFoamTexFrames, new Vector4(FoamFlipbookCols, FoamFlipbookRows, 0f, 0f));
        }

        // A default surface texture from the package's imported Runtime/Textures folder. Null (with
        // a loud warning) if the package copy is missing, so a broken install fails visibly instead
        // of silently building an untextured body.
        internal static Texture LoadDefaultTexture(string fileName)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(DefaultTexturesRoot + "/" + fileName);
            if (texture == null)
                Debug.LogWarning($"[WebGpuWater] Default texture '{fileName}' not found under {DefaultTexturesRoot}; the corresponding slot stays empty.");
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
            var go = CreateRenderer(GodRaysObjectName, SaveAsset(BuildGodRayBox(), GodRayBoxMeshPath),
                                    godRayMat, parent);
            var gmr = go.GetComponent<MeshRenderer>();
            gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            gmr.receiveShadows = false;
            return go;
        }

    }
}
