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
        internal static T LoadRequiredDefault<T>(string path, string description) where T : Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                Debug.LogError(LogPrefix + $"required packaged {description} is missing at '{path}'.");
            return asset;
        }

        // Overwrite-in-place via CopySerialized so scene references keep their GUID/fileID - but
        // that copy lands IN MEMORY only, and nothing marked the asset dirty: the next asset
        // refresh silently reloaded the STALE file and the "regenerated" mesh reverted to the old
        // one (2026-08-01: a rebuilt boat hull kept carving with the broken morning asset while
        // the preview showed the new vertex counts). SetDirty + SaveAssets persists immediately.
        internal static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(m, existing);
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                return existing;
            }
            AssetDatabase.CreateAsset(m, path);
            AssetDatabase.SaveAssets();
            return m;
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

    }
}
