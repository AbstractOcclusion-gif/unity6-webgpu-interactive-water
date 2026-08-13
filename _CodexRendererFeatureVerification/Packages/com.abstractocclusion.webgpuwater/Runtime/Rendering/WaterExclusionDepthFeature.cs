// WebGpuWater - mesh-exclusion depth PREPASS render feature (URP, RenderGraph).
// Renders every active exclusion volume's front and back faces into two depth RTs the carve
// consumers read to bound the dry column against the volume's real silhouette. Add this feature
// once to the renderer used by the water camera and assign the WaterExclusionDepth shader; it
// self-gates on WaterExclusionVolume.AnyPrepassVolumeActive(), so it costs nothing and changes
// nothing when no exclusion volume is in the scene. Twin of WaterChunkDepthFeature.
//
// EVERY shape is drawn, but only MESH volumes are read back today (the _ExclusionMeshCount gate) -
// so a Box/Sphere scene still carves correctly with this feature absent, exactly as before. See
// WaterExclusionDepthPass for why the two were separated.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterExclusionDepthFeature : ScriptableRendererFeature
    {
        [Tooltip("The AbstractOcclusion/WebGpuWater/WaterExclusionDepth shader. Assign the shader " +
                 "asset of that name.")]
        [SerializeField] Shader exclusionDepthShader;

        WaterExclusionDepthPass _pass;
        Material _material;

        public override void Create()
        {
        // Release BEFORE (re)creating. URP calls Create() on OnEnable, on OnValidate and on every
        // domain reload, but Dispose() only when the feature asset is destroyed - so allocating here
        // without releasing first leaked one engine Material (and, where the pass owns RTHandles, the
        // pass's history targets) per inspector tweak. Create and Dispose now share ONE teardown, so
        // they cannot drift.
            ReleaseResources();
            if (exclusionDepthShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(exclusionDepthShader);
            _pass = new WaterExclusionDepthPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Never for material/prefab thumbnails - see WaterPassCameraGate.
            if (WaterPassCameraGate.SkipCamera(renderingData.cameraData.cameraType)) return;
            if (_pass == null) return;                                 // shader unassigned / not created
            if (!WaterExclusionVolume.AnyPrepassVolumeActive()) return; // no volume: nothing to prepass
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
