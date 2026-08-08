// WebGpuWater - RenderGraph pass for WaterSkyFogFeature.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSkyFogPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.AfterRenderingSkybox;

        static readonly int SkyFogOpacityId = Shader.PropertyToID("_SkyFogOpacity");

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterSkyFog");

        internal float skyFogOpacity;

        internal WaterSkyFogPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData
        {
            public Material material;
            public float opacity;
            public MaterialPropertyBlock block;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null || skyFogOpacity <= 0f) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                _sampler.name, out PassData data, _sampler);
            data.material = _material;
            data.opacity = skyFogOpacity;
            data.block = new MaterialPropertyBlock();
            // The existing skybox must be loaded before alpha blending the fog colour over it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                d.block.SetFloat(SkyFogOpacityId, d.opacity);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, d.block);
            });
        }
    }
}
#endif
