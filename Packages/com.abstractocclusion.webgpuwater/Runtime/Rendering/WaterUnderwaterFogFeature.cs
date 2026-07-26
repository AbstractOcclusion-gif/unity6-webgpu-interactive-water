// WebGpuWater - real underwater fog render feature (URP, RenderGraph).
// Fogs the whole view when the camera is submerged in ANY water body, replacing the per-object
// trick for the camera-underwater case. Add this feature once to the renderer used by the water
// camera and assign the WaterUnderwaterFog shader; it self-gates on WaterVolume.UnderwaterFogActive,
// so above water it never enqueues and nothing changes.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterUnderwaterFogFeature : ScriptableRendererFeature
    {
        [Tooltip("The AbstractOcclusion/WebGpuWater/WaterUnderwaterFog shader. Assign the shader asset of that name.")]
        [SerializeField] Shader underwaterFogShader;

        WaterUnderwaterFogPass _pass;
        WaterParticlesAfterFogPass _particlePass;
        Material _material;

        public override void Create()
        {
            _particlePass = new WaterParticlesAfterFogPass(); // material-free: needs no shader
            if (underwaterFogShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(underwaterFogShader);
            _pass = new WaterUnderwaterFogPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // After-fog particle reroute: WaterFoamParticles/WaterSplashEmitter SKIP their
            // queue-time draws whenever the fullscreen fog is armed (the fog would paint the
            // water column's fog over the sprites), so this pass must enqueue on EXACTLY that
            // gate - independent of the fog shader being assigned, or the reroute would eat
            // the particles entirely on a misconfigured renderer.
            if (WaterVolume.UnderwaterFogActive && _particlePass != null
                && (WaterFoamParticles.Live.Count > 0 || WaterSplashEmitter.Live.Count > 0))
                renderer.EnqueuePass(_particlePass);

            if (_pass == null) return; // shader unassigned / not created
            // Fog: ocean = submerged only, pond = whenever fog is on. Waterline: the near plane
            // straddles the surface (partial submersion) - it arms BEFORE the eye submerges, so
            // the crossing shows a meniscus line instead of a hard pop. The pass records only
            // the sub-passes whose gate is set.
            if (!WaterVolume.UnderwaterFogActive && !WaterVolume.WaterlineActive) return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
            _particlePass = null;
        }
    }

    // Draws the water particle sprites AFTER the fullscreen underwater fog and the god-ray
    // composite (fog +0, god rays +1, sprites +2): the fog integrates to OPAQUE depth, so
    // sprites drawn in the transparent queue got the full water column's fog painted over
    // them - near droplets read as flat fog colour (the particle/fog SORTING fix). The
    // sprite shaders price their own camera->particle fog instead (WaterParticleFog.hlsl).
    // Spray in front of shafts: physically the shafts are IN the water behind the spray.
    internal sealed class WaterParticlesAfterFogPass : ScriptableRenderPass
    {
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterParticlesAfterFog");

        sealed class PassData { public Camera camera; }

        internal WaterParticlesAfterFogPass()
        {
            renderPassEvent = WaterUnderwaterFogPass.InjectionPoint + 2;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (!resources.activeColorTexture.IsValid()) return;

            using (var builder = renderGraph.AddRasterRenderPass("WaterParticlesAfterFog",
                                                                 out PassData data, _sampler))
            {
                data.camera = cameraData.camera;
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
                // Depth READ: the sprites keep their hardware ZTest against the scene (and the
                // soft-fade depth sample rides the global _CameraDepthTexture).
                if (resources.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false); // driven by our own lists, not renderer visibility
                builder.UseAllGlobalTextures(true);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    var quads = WaterFoamParticles.Live;
                    for (int i = 0; i < quads.Count; i++)
                        if (quads[i] != null) quads[i].RenderAfterFog(ctx.cmd, d.camera);
                    var emitters = WaterSplashEmitter.Live;
                    for (int i = 0; i < emitters.Count; i++)
                        if (emitters[i] != null) emitters[i].DrawAfterFog(ctx.cmd);
                });
            }
        }
    }
}
#endif
