// WebGpuWater - WaterVolume partial: per-body surface material instances, reflection keywords
// and the Water layer.
//
// Bodies routinely share one authored surface material, so reflection keywords set on the shared
// asset would leak across bodies and dirty the asset on disk. Every renderer therefore gets a
// play-mode instance here; OnDisable restores the original (see WaterVolume.Wiring.cs).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // Give the surface renderers per-body material instances and set their reflection
        // keywords + look floats from the tier-capped toggles, so bodies with different reflection
        // settings don't fight over one shared material. A planar body also binds the scene's
        // single planar reflection.
        void ApplyReflections()
        {
            // Play-mode only: an instance assigned to sharedMaterial in edit mode could be saved
            // into the scene as a dead reference. Reflection is uniform-driven and published every
            // frame by WaterUniformPublisher (edit + play), so no keywords are baked here.
            if (!Application.isPlaying) return;

            // Per-body material instances so the ocean clipmap / patch renderers and the low-tier
            // mesh swap share this body's surface material.
            _surfaceAboveInstance = InstanceSurfaceMaterial(surfaceAbove, out _surfaceAboveOriginal);
            _surfaceUnderInstance = InstanceSurfaceMaterial(surfaceUnder, out _surfaceUnderOriginal);

            // Planar reflection is self-driven per body now (see RenderPlanarMirror in OnBeginCameraRender);
            // no hero binding here.
        }

        // Put water surfaces on the built-in "Water" layer so the planar reflection - configured to
        // exclude that layer - never mirrors the water into itself (which reads as a second, independently
        // waving surface). The scene camera still renders the layer, so the water itself is unaffected.
        // internal: the build kit assigns the same layer at author time (WaterBuildKit.AssignWaterLayer)
        // and must not spell the name a second time.
        internal const string WaterLayerName = "Water";

        // PLAY MODE ONLY. surfaceAbove/surfaceUnder are USER-assigned renderers, so writing their
        // layer in edit mode (TryInitialize runs under [ExecuteAlways]) rewrote a customer's own
        // GameObject - permanently, since nothing ever restored it. Author-time assignment is the
        // build kit's job, where it lands as authored data the user can see and undo.
        // The two OTHER ApplyWaterLayer callers - OceanClipmap and SimWindowPatch - stay unguarded
        // on purpose: those GameObjects are created by the package at runtime, not user data.
        void AssignSurfaceLayers()
        {
            if (!Application.isPlaying) return;

            ApplyWaterLayer(surfaceAbove);
            ApplyWaterLayer(surfaceUnder);
        }

        static void ApplyWaterLayer(Renderer r)
        {
            if (r != null) ApplyWaterLayer(r.gameObject);
        }

        static void ApplyWaterLayer(GameObject go)
        {
            int layer = LayerMask.NameToLayer(WaterLayerName);
            if (go != null && layer >= 0 && go.layer != layer) go.layer = layer;
        }

        // ONE factory for the runtime surface renderers this body spawns - the near-field sim-window
        // patch and every ocean clipmap ring. They were two byte-identical methods differing only in
        // which mesh they took; a parameter add to one silently left the other behind. Never-shadowing
        // by construction (the water surface neither casts nor receives), parented beside the authored
        // surface, and on the Water layer like it. DontSave: these are rebuilt every enable and must
        // never be serialized into the user's scene.
        // Their per-renderer flags (_IsPatch / _IsClipmap) ride the property block, set by the caller.
        MeshRenderer CreateSurfaceRenderer(string objectName, Mesh mesh, Material material)
        {
            var go = new GameObject(objectName) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(surfaceAbove.transform.parent, false);
            ApplyWaterLayer(go);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        // Replace the renderer's shared material with a per-body instance (play-mode only, so
        // the scene asset is untouched). The original is captured so OnDisable can restore it
        // before destroying the instance.
        static Material InstanceSurfaceMaterial(Renderer r, out Material original)
        {
            original = null;
            if (r == null || r.sharedMaterial == null) return null;
            original = r.sharedMaterial;
            var instance = new Material(original) { hideFlags = HideFlags.HideAndDontSave };
            r.sharedMaterial = instance;
            return instance;
        }
    }
}
