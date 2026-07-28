// WebGpuWater build kit - procedural meshes (surface grid, pool shell, god-ray box) and the
// renderer GameObjects that carry them.
using System.IO;
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

        // XY-plane grid in [-1,1], z = 0. Shared with the runtime (the Low tier rebuilds a
        // coarser grid on weak devices), so the actual builder lives in WaterMeshBuilder.
        internal static Mesh BuildGrid(int detail) => WaterMeshBuilder.BuildGrid(detail);

        // Open-top box: floor at y=-1, walls up to y=2/12, spanning x,z in [-1,1]. Faces inward.
        internal static Mesh BuildPool()
        {
            const float top = 2f / 12f;
            const float lo = -1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, lo, -1), new Vector3(-1, lo, 1), new Vector3(1, lo, 1), new Vector3(1, lo, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, top, -1), new Vector3(-1, top, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(-1, lo, 1), new Vector3(-1, top, 1), new Vector3(1, top, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, lo, -1), new Vector3(-1, top, -1), new Vector3(-1, top, 1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(1, top, 1), new Vector3(1, top, -1));

            var mesh = new Mesh { name = "Pool" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeMeshBoundsSize);
            return mesh;
        }

        // Closed box in POOL space: y in [-1,0], x,z in [-1,1]. Outward-wound (like a primitive
        // cube) so the GodRays pass's Cull Front renders the back faces.
        internal static Mesh BuildGodRayBox()
        {
            const float lo = -1f, hi = 0f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, hi, -1), new Vector3(-1, hi, 1), new Vector3(1, hi, 1), new Vector3(1, hi, -1));
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, -1), new Vector3(-1, hi, -1), new Vector3(1, hi, -1), new Vector3(1, lo, -1));
            Quad(new Vector3(1, lo, 1), new Vector3(1, hi, 1), new Vector3(-1, hi, 1), new Vector3(-1, lo, 1));
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, hi, 1), new Vector3(-1, hi, -1), new Vector3(-1, lo, -1));
            Quad(new Vector3(1, lo, -1), new Vector3(1, hi, -1), new Vector3(1, hi, 1), new Vector3(1, lo, 1));

            var mesh = new Mesh { name = "GodRayBox" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * HugeMeshBoundsSize);
            return mesh;
        }

    }
}
