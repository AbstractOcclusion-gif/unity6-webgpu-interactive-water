// WebGpuWater - real underwater fog pass (RenderGraph).
// When the camera is submerged, fogs the whole camera colour by water-path length using two
// hardware-blend fullscreen passes (per-channel absorb, then inscatter). No scene-colour copy:
// both passes read the destination through the blender, which is why the colour attachment is
// bound ReadWrite (load the scene) rather than Write (which would discard it).
//
// The shader reconstructs the scene from the resolved _CameraDepthTexture and computes the wavy
// waterline ANALYTICALLY (or flat, on Simple tiers) - it does not read a post-transparent depth.
// The former DepthHandoff sub-pass that published one (_WaterFogSceneDepth) was dead weight: the
// shader declared the texture but never sampled it, so the handoff was removed (U3).
//
// Runs before post so bloom/tonemapping treat the fogged scene as the final image.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUnderwaterFogPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        const int AbsorbShaderPass = 0;
        const int InscatterShaderPass = 1;

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterUnderwaterFog");

        internal WaterUnderwaterFogPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData { public Material material; public int shaderPass; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            // Order matters: absorb (scene *= transmittance) then inscatter (scene += fog).
            RecordFogPass(renderGraph, resources, cameraColor, AbsorbShaderPass, "WaterUnderwaterFog.Absorb");
            RecordFogPass(renderGraph, resources, cameraColor, InscatterShaderPass, "WaterUnderwaterFog.Inscatter");
        }

        void RecordFogPass(RenderGraph renderGraph, UniversalResourceData resources,
                           TextureHandle cameraColor, int shaderPass, string passName)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            data.shaderPass = shaderPass;
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // published fog globals (shore field, FFT displacement, ...)
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, d.shaderPass));
        }
    }
}
#endif
