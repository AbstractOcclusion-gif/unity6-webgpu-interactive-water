// WebGpuWater build kit - loading the shader set and wiring it onto a WaterVolume, plus the one
// undo-registered GameObject factory every generator in the kit creates objects through.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- helpers
        // Load + validate the water shaders. Fails fast (dialog + false) if a REQUIRED shader
        // (surface, caustics, compute) is missing; optional shaders only warn.
        internal static bool TryLoadShaders(out ShaderSet shaders)
        {
            shaders = new ShaderSet
            {
                Water = Shader.Find(ShaderWaterSurface),
                Pool = Shader.Find(ShaderAnalyticPool),
                Caustics = Shader.Find(ShaderCaustics),
                Obstacle = Shader.Find(ShaderObstacle),
                Compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(SimComputePath)
            };

            if (shaders.Water == null || shaders.Caustics == null || shaders.Compute == null)
            {
                // Point at where the shaders ACTUALLY live. The old text named a "WebGLWater/Shaders"
                // folder that has not existed since the move into the package, so the one error a
                // broken import produces sent the user hunting for a directory that cannot exist.
                EditorUtility.DisplayDialog(ProductName,
                    "Could not find the water shaders / compute shader. Make sure " +
                    $"'{PackageShadersRoot}' imported without errors (check the Console for shader " +
                    "compile errors), then try again.",
                    "OK");
                return false;
            }

            if (shaders.Obstacle == null) Debug.LogWarning($"[WebGpuWater] Shader '{ShaderObstacle}' not found; object->water displacement will be disabled.");
            return true;
        }

        // ONE source for a body's asset wiring. This block existed as three hand-synced copies
        // (here, the prefab builder, the secondary-body cloner) whose "parity" comments prove it
        // had already drifted once - a new serialized slot now lands everywhere by construction.
        internal static void WireWaterVolumeAssets(WaterVolume volume, in ShaderSet shaders,
                                                   Mesh grid, Texture tiles, Cubemap sky, WaterQuality quality)
        {
            volume.simCompute = shaders.Compute;
            volume.causticsShader = shaders.Caustics;
            volume.obstacleShader = shaders.Obstacle;
            // Optional (oceans only): near-field caustics in the sim-window frame. Non-fatal if
            // absent - Shader.Find just leaves the field null and the pass no-ops.
            volume.largeBodyCausticsShader = Shader.Find(ShaderLargeBodyCaustics);
            // Optional: refracted-light object shadow into the caustic RT. Non-fatal if absent (the
            // occluder pass no-ops and object shadows stay on the un-refracted shadow map).
            volume.occluderShader = Shader.Find(ShaderCausticOccluder);
            // Optional (oceans only): the FFT-cascade wave compute. The runtime module only arms on
            // an ocean clipmap body AND with this assigned, so wiring it everywhere is inert for
            // pools/lakes. Non-fatal if absent (analytic large-wave fallback).
            volume.oceanFftCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(OceanFftComputePath);
            volume.waterMesh = grid;
            volume.tiles = tiles;
            volume.sky = sky;
            volume.Quality = quality;
        }

        // Copy-wiring for a body cloned NEXT TO an existing one (secondary bodies): same slots as
        // WireWaterVolumeAssets plus the shared scene refs, sourced from the live body so a scene
        // whose primary was hand-rewired clones faithfully.
        internal static void WireWaterVolumeFrom(WaterVolume target, WaterVolume source)
        {
            target.simCompute = source.simCompute;
            target.causticsShader = source.causticsShader;
            target.obstacleShader = source.obstacleShader;
            target.largeBodyCausticsShader = source.largeBodyCausticsShader;
            target.occluderShader = source.occluderShader;
            target.oceanFftCompute = source.oceanFftCompute;
            target.waterMesh = source.waterMesh;
            target.tiles = source.tiles;
            target.sky = source.sky;
            target.Quality = source.Quality;
            target.targetCamera = source.targetCamera;
            target.sun = source.sun;
        }

        // Every scene object the builders create goes through here, so a single Undo step
        // (grouped by the caller) removes an entire build - the editor assembly previously had
        // NO undo for creation at all.
        internal static GameObject NewUndoableGameObject(string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            return go;
        }
    }
}
