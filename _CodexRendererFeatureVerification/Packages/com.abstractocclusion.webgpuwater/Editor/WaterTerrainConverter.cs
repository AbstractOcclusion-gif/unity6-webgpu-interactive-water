// WebGpuWater - editor utility: put a Unity Terrain onto the dedicated WaterTerrain shader.
//
// WHY THIS EXISTS SEPARATELY FROM WaterReceiverConverter: a Unity Terrain is NOT a Renderer. The
// receiver converter walks GetComponentsInChildren<Renderer>(), so it skips every Terrain in the
// scene silently - no warning, no material swapped, nothing to tell the user why. Terrain also takes
// its material through Terrain.materialTemplate rather than a Renderer's material array, so the swap
// itself is a different operation, not just a different filter.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterTerrainConverter
    {
        const string MenuConvert = MenuRoot + "Convert Selection To Water Terrain";
        const string TerrainShaderName = WaterShaderNames.WaterTerrain;
        const string MaterialSuffix = "_WaterTerrain";

        [MenuItem(MenuConvert, priority = 411)]
        static void ConvertSelection()
        {
            Shader terrainShader = Shader.Find(TerrainShaderName);
            if (terrainShader == null)
            {
                Debug.LogError($"[WebGpuWater] Shader '{TerrainShaderName}' not found - is the package present?");
                return;
            }

            List<Terrain> terrains = CollectSelectedTerrains();
            if (terrains.Count == 0)
            {
                Debug.LogWarning("[WebGpuWater] Select one or more GameObjects with a Terrain component first.");
                return;
            }

            WaterReceiverConverter.EnsureOutputFolder();
            int converted = 0, instancedWarnings = 0;
            foreach (Terrain terrain in terrains)
            {
                // The shader draws the terrain from its ordinary mesh vertices. With Draw Instanced on,
                // Unity feeds a flat patch and expects the shader to displace it from
                // _TerrainHeightmapTexture, which this one does not do - the terrain would render as a
                // flat sheet. Warn rather than silently flipping the user's setting: which one they
                // want is their call, and a converter that quietly changes rendering settings is worse
                // than one that explains the problem.
                if (terrain.drawInstanced)
                {
                    Debug.LogWarning($"[WebGpuWater] Terrain '{terrain.name}' has Draw Instanced ON. " +
                                     "WaterTerrain does not implement the instanced heightmap path; " +
                                     "turn Draw Instanced off on this terrain, or keep it on TerrainLit.",
                                     terrain);
                    instancedWarnings++;
                }

                Undo.RecordObject(terrain, "Convert To Water Terrain");
                terrain.materialTemplate = GetOrCreateTerrainMaterial(terrain, terrainShader);
                EditorUtility.SetDirty(terrain);
                converted++;
            }

            AssetDatabase.SaveAssets();
            string tail = instancedWarnings > 0 ? $" ({instancedWarnings} with Draw Instanced ON - see warnings)" : "";
            Debug.Log($"[WebGpuWater] Converted {converted} terrain(s) to WaterTerrain{tail}. " +
                      "Assign substrate textures on the new material; the shader works from its tints " +
                      "until you do. Wetness follows the baked shore field, so enable Bed Depth on the " +
                      "water body for the beach band and swash to track the real waterline.");
        }

        [MenuItem(MenuConvert, validate = true)]
        static bool ConvertSelectionValidate() => Selection.gameObjects.Length > 0;

        static List<Terrain> CollectSelectedTerrains()
        {
            // A HashSet-backed pass, because selecting both a parent and its child would otherwise
            // convert the same terrain twice and create a second material asset for it.
            var seen = new HashSet<Terrain>();
            var ordered = new List<Terrain>();
            foreach (GameObject root in Selection.gameObjects)
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(includeInactive: true))
                    if (seen.Add(terrain)) ordered.Add(terrain);
            return ordered;
        }

        // One material per terrain, created once and reused on re-run, so a converted scene keeps a
        // real asset reference instead of a leaked runtime instance - and so re-running the menu item
        // does not throw away textures the user has already assigned.
        static Material GetOrCreateTerrainMaterial(Terrain terrain, Shader terrainShader)
        {
            string safeName = WaterReceiverConverter.SanitizeAssetName(terrain.name);
            string path = $"{WaterReceiverConverter.OutputFolder}/{safeName}{MaterialSuffix}.mat";
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                if (existing.shader != terrainShader) existing.shader = terrainShader;
                return existing;
            }
            var material = new Material(terrainShader) { name = safeName + MaterialSuffix };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
#endif
