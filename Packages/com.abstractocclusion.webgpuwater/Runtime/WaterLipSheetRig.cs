// WebGpuWater - shared rig for lip-sheet strip renderers (hero wave + surf curl).
//
// A "lip sheet" is a dense strip mesh that renders an OVERTURNING water surface (a heightfield
// cannot overhang), riding the body's own surface material through the clipmap world-metre vertex
// mapping and discarding every fragment outside its curl region. WaterHeroWave introduced the
// pattern; WaterSurfCurl reuses it verbatim - this rig is the ONE copy of the mesh builder and the
// renderer recipe, so the two sheets can never drift apart.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterLipSheetRig
    {
        // KEEP IN SYNC with WaterVolume.WaterLayerName (private there); same layer as every water renderer.
        const string WaterLayerName = "Water";

        /// <summary>Dense XZ-plane grid in local [-1,1] (x = along the crest, z = across the wave),
        /// same triangle pattern as WaterMeshBuilder.BuildGrid so the winding matches every other
        /// water surface. Huge bounds like the other generated water meshes (culling is never
        /// mesh-tight).</summary>
        internal static Mesh BuildStripGrid(string meshName, int alongSegments, int acrossSegments)
        {
            if (alongSegments < 1 || acrossSegments < 1)
                throw new System.ArgumentException(
                    $"Strip segments must be >= 1, got {alongSegments}x{acrossSegments}.");

            int alongVerts = alongSegments + 1;
            int acrossVerts = acrossSegments + 1;
            var verts = new Vector3[alongVerts * acrossVerts];
            for (int i = 0; i < alongVerts; i++)
                for (int j = 0; j < acrossVerts; j++)
                    verts[i * acrossVerts + j] = new Vector3(
                        i / (float)alongSegments * 2f - 1f, 0f, j / (float)acrossSegments * 2f - 1f);

            var tris = new int[alongSegments * acrossSegments * 6];
            int t = 0;
            for (int i = 0; i < alongSegments; i++)
                for (int j = 0; j < acrossSegments; j++)
                {
                    int a = i * acrossVerts + j;
                    int b = (i + 1) * acrossVerts + j;
                    int c = i * acrossVerts + (j + 1);
                    int d = (i + 1) * acrossVerts + (j + 1);
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }

            var mesh = new Mesh
            {
                name = meshName,
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * WaterMeshBuilder.HugeBoundsSize);
            return mesh;
        }

        /// <summary>Mirror of the volume's patch/clipmap renderer recipe: never-shadowing
        /// MeshRenderer on the Water layer, parented beside the surface, reusing THIS body's
        /// material instance so the sheet inherits reflections/fog/foam; the sheet flags ride its
        /// property block (set by the owning component each frame).</summary>
        internal static MeshRenderer CreateStripRenderer(WaterVolume volume, Mesh stripMesh,
                                                         string objectName, Material material)
        {
            var go = new GameObject(objectName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(volume.surfaceAbove.transform.parent, false);
            int layer = LayerMask.NameToLayer(WaterLayerName);
            if (layer >= 0) go.layer = layer;
            go.AddComponent<MeshFilter>().sharedMesh = stripMesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            return meshRenderer;
        }

        /// <summary>Destroy a runtime-generated object in either play or edit mode.</summary>
        internal static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
