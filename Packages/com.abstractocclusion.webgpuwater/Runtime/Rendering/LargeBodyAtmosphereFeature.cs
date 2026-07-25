// WebGpuWater - large-body atmosphere render feature (URP, RenderGraph).
// Adds the ocean-scale god-ray shafts to a URP renderer. Add this feature once to the renderer
// used by the ocean camera and assign the LargeBodyGodRays shader; it self-gates, so it costs
// nothing and changes nothing on scenes without an unbounded ocean with god rays enabled.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class LargeBodyAtmosphereFeature : ScriptableRendererFeature
    {
        [Tooltip("The AbstractOcclusion/WebGpuWater/LargeBodyGodRays shader. Assign the shader asset of that name.")]
        [SerializeField] Shader godRayShader;

        LargeBodyAtmospherePass _pass;
        Material _material;

        public override void Create()
        {
            if (godRayShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(godRayShader);
            _pass = new LargeBodyAtmospherePass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null) return;                                // shader unassigned / not created
            if (!LargeBodyAtmosphereGate.HasActiveGodRayOcean) return; // ocean-only, and only when shafts are on
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            _pass?.Dispose(); // releases the persistent temporal-history RTs
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
