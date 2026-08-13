// WebGpuWater build kit - demo props: the floor collider and the buoyant objects the sample
// scenes drop into the water. Spawn geometry only; nothing here knows how water is built.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- demo props
        // A thin box collider under the water so sinking props have something to rest on.
        internal static GameObject CreateFloorCollider(Transform parent, Vector3 center, Vector3 size)
        {
            var go = NewUndoableGameObject("Floor Collider");
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.AddComponent<BoxCollider>().size = size;
            return go;
        }

        // ---------------------------------------------------------------- buoyant props
        // Built-in shapes for the wizard's one-click floater. CustomMesh takes a user mesh instead.
        internal enum FloaterShape { Cube, Sphere, Capsule, CustomMesh }

        const string BuoyantObjectName = "Buoyant Object";
        // Metres above the resolved water surface a new prop spawns, so it visibly drops in and
        // settles rather than popping half-submerged.
        const float PropSpawnHeightAboveWater = 1f;

        // The GEOMETRY of a one-click floater: primitive (mesh + fitting collider for free) or the
        // user's mesh with a convex MeshCollider + the pipeline's default material. The caller wires
        // the buoyancy component set on top (the wizard owns the preset/advanced tuning).
        internal static GameObject CreateBuoyantObjectBody(FloaterShape shape, Mesh customMesh, float size)
        {
            GameObject go;
            if (shape == FloaterShape.CustomMesh)
            {
                if (customMesh == null)
                {
                    Debug.LogError("[WebGpuWater] Assign a mesh to create a custom-mesh floater.");
                    return null;
                }
                go = NewUndoableGameObject(BuoyantObjectName);
                go.AddComponent<MeshFilter>().sharedMesh = customMesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = DefaultPipelineMaterial();
                // Convex: a floater is a rigidbody, and non-convex MeshColliders can't collide as one.
                var meshCollider = go.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = customMesh;
                meshCollider.convex = true;
            }
            else
            {
                var primitive = shape == FloaterShape.Sphere ? PrimitiveType.Sphere
                              : shape == FloaterShape.Capsule ? PrimitiveType.Capsule
                                                              : PrimitiveType.Cube;
                go = GameObject.CreatePrimitive(primitive);
                go.name = BuoyantObjectName;
                Undo.RegisterCreatedObjectUndo(go, BuoyantObjectName);
            }
            go.transform.localScale = Vector3.one * Mathf.Max(size, MinPropSize);
            go.transform.position = PropSpawnPosition();
            return go;
        }

        const float MinPropSize = 0.01f;

        // Spawn above the primary body's surface when one exists (the prop drops in and floats);
        // else in front of the origin so it's still findable in an empty scene.
        internal static Vector3 PropSpawnPosition()
        {
            var bodies = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            WaterVolume primary = System.Array.Find(bodies, b => b.IsPrimary) ?? (bodies.Length > 0 ? bodies[0] : null);
            if (primary != null)
                return primary.VolumeCenter + Vector3.up * PropSpawnHeightAboveWater;
            return Vector3.up * PropSpawnHeightAboveWater;
        }

        // The active render pipeline's default lit material (URP here), so a custom-mesh prop
        // isn't magenta. Built-in fallback kept for safety. Internal: the showcase builder derives
        // its tinted prop materials from this material's shader.
        internal static Material DefaultPipelineMaterial()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultMaterial != null) return pipeline.defaultMaterial;
            return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Diffuse.mat");
        }

    }
}
