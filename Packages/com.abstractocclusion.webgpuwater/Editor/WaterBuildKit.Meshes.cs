// WebGpuWater build kit - renderer GameObjects that carry packaged meshes.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- meshes
        internal static GameObject CreateRenderer(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = NewUndoableGameObject(name);
            go.transform.SetParent(parent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        // Put the built surfaces on the "Water" layer so a planar reflection - configured to exclude
        // that layer - never mirrors the water into itself. Done HERE, at author time, so the layer
        // is authored scene data: the runtime pass (WaterVolume.AssignSurfaceLayers) is play-mode
        // only precisely because it must never rewrite a GameObject the user owns. Not folded into
        // CreateRenderer, which also builds the analytic pool and the god-ray box - neither belongs
        // on the Water layer. The objects are freshly created here, so Undo already covers them.
        internal static void AssignWaterLayer(params Renderer[] renderers)
        {
            int layer = LayerMask.NameToLayer(WaterVolume.WaterLayerName);
            if (layer < 0) return; // "Water" is built-in layer 4; defensive only

            foreach (Renderer renderer in renderers)
                if (renderer != null) renderer.gameObject.layer = layer;
        }

    }
}
