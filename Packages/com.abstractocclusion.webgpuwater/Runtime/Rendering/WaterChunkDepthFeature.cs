// WebGpuWater - mesh-chunk depth PREPASS render feature (URP, RenderGraph).
// Renders every active MESH-footprint water chunk's front and back faces into two depth RTs the
// chunk wall reads to bound its water column against an arbitrary closed mesh. Add this feature once
// to the renderer used by the water camera and assign the WaterChunkDepth shader; it self-gates on
// WaterVolume.AnyMeshChunkActive(), so it costs nothing and changes nothing when no mesh chunk is in
// the scene (sphere/box chunks never trigger it - they stay on the analytic path).
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterChunkDepthFeature : ScriptableRendererFeature
    {
        [Tooltip("The AbstractOcclusion/WebGpuWater/WaterChunkDepth shader. Assign the shader asset of that name.")]
        [SerializeField] Shader chunkDepthShader;

        WaterChunkDepthPass _pass;
        Material _material;

        public override void Create()
        {
            if (chunkDepthShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(chunkDepthShader);
            _pass = new WaterChunkDepthPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;                        // shader unassigned / not created
            if (!WaterVolume.AnyMeshChunkActive()) return;    // no mesh chunk: nothing to prepass
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
