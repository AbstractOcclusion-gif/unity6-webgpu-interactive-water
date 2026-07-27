// WebGpuWater build kit - AssetDatabase load-or-create for every asset type the kit persists.
// One save/load idiom per type, so a build re-run reuses assets instead of duplicating them.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {

        internal static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // Create-once mesh asset: reuse what's on disk (the builders' meshes are deterministic
        // functions of named constants), build only when missing. Delete the asset to regenerate
        // after changing the constants.
        static Mesh LoadOrSaveMesh(string path, System.Func<Mesh> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            return existing != null ? existing : SaveAsset(build(), path);
        }

        static Cubemap LoadOrSaveCubemap(string path, System.Func<Cubemap> build)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            return existing != null ? existing : SaveCubemap(build(), path);
        }

        // Write a generated PNG and configure its importer in one guarded path: the old inline
        // copies cast AssetImporter.GetAtPath unchecked right after an unchecked File.WriteAllBytes,
        // so a failed write/import NRE'd halfway through a build.
        static Texture2D SavePngAsset(string path, Texture2D tex, System.Action<TextureImporter> configure)
        {
            try
            {
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            catch (System.IO.IOException ioException)
            {
                Debug.LogError($"[WebGpuWater] Could not write '{path}': {ioException.Message}");
                return null;
            }
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                configure(importer);
                importer.SaveAndReimport();
            }
            else
            {
                Debug.LogError($"[WebGpuWater] '{path}' imported without a TextureImporter; texture settings not applied.");
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Create-once: reuse the material already at 'path' (preserving any hand-tuning) instead of
        // overwriting it, so rebuilding a scene - or building a different one - never resets it.
        internal static Material LoadOrCreateMaterial(string path, Shader shader, System.Action<Material> configure = null)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var m = new Material(shader);
            configure?.Invoke(m);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        internal static Cubemap SaveCubemap(Cubemap c, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (existing != null) { EditorUtility.CopySerialized(c, existing); return existing; }
            AssetDatabase.CreateAsset(c, path);
            return c;
        }

        internal static WaterQuality LoadOrCreateWaterQuality(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
            if (existing != null) return existing;
            var q = ScriptableObject.CreateInstance<WaterQuality>();
            AssetDatabase.CreateAsset(q, path);
            return q;
        }
    }
}
