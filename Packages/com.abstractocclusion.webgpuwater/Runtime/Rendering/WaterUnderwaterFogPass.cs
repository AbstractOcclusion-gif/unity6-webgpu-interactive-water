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
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUnderwaterFogPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        const int AbsorbShaderPass = 0;
        const int InscatterShaderPass = 1;
        const int WaterlineShaderPass = 2;
        // WaterSurface.shader's "OceanSurfaceEyeDepth" pass, drawn per surface renderer below.
        const int SurfaceDepthShaderPass = 1;

        static readonly int ID_OceanSurfaceEyeDepth = Shader.PropertyToID("_OceanSurfaceEyeDepth");
        static readonly int ID_OceanSurfaceDepthValid = Shader.PropertyToID("_OceanSurfaceDepthValid");
        static readonly int ID_WaterlineSceneTex = Shader.PropertyToID("_WaterlineSceneTex");

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterUnderwaterFog");
        readonly ProfilingSampler _prepassSampler = new ProfilingSampler("WaterUnderwaterFog.SurfaceDepth");
        // Reused each frame so the prepass allocates no garbage.
        readonly MaterialPropertyBlock _scratchBlock = new MaterialPropertyBlock();
        static readonly List<Renderer> s_SurfaceRenderers = new List<Renderer>();

        internal WaterUnderwaterFogPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData { public Material material; }

        sealed class PrepassData
        {
            public List<Renderer> renderers;
            public MaterialPropertyBlock block;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            // Rendered-surface waterline prepass (KWS trick): draw the primary ocean's DISPLACED
            // surface into an eye-depth target the fog samples per pixel, so its waterline is the
            // rendered surface itself - exact at any distance, replacing the bounded crossing
            // march. The validity global is refreshed EVERY record (globals persist across frames,
            // so a stale 1 after the ocean disappears would leave the fog reading a dead RT).
            bool prepassRecorded = false;
            WaterVolume primary = WaterVolume.Primary;
            // NOT ON THE SIMPLE TIER - it has no reader there. UnderwaterSegment tests
            // _UnderwaterFogSimple BEFORE _OceanSurfaceDepthValid (WaterUnderwaterFog.shader), so a
            // Simple frame takes OceanFlatPath and _OceanSurfaceEyeDepth is sampled NOWHERE: its
            // only consumer in the package is OceanPrepassPath. Recording it anyway re-drew every
            // ocean surface renderer a second time - base + under + near-field patch + patch under +
            // two per clipmap level, each through the full displacement vertex stage - into a
            // camera-sized R32F plus its own Depth32, and threw the result away. It also forced a
            // mid-frame render-target switch, which costs far more on the WebGPU backend than
            // native. Leaving the validity global at 0 is the state a pond or a non-ocean primary
            // already ships every frame, so this adds no new case for the shader to handle.
            if (primary != null && primary.IsOceanClipmap && !primary.UnderwaterFogSimple)
            {
                s_SurfaceRenderers.Clear();
                primary.CollectOceanSurfaceRenderers(s_SurfaceRenderers);
                if (s_SurfaceRenderers.Count > 0)
                {
                    RecordSurfaceDepthPrepass(renderGraph, cameraColor);
                    prepassRecorded = true;
                }
            }
            Shader.SetGlobalFloat(ID_OceanSurfaceDepthValid, prepassRecorded ? 1f : 0f);

            // Order matters: absorb (scene *= transmittance) then inscatter (scene += fog),
            // then the waterline meniscus ON TOP of the fogged scene (it darkens the final
            // crossing band, whichever side of it is fogged). The same per-frame gates the
            // feature enqueued on decide which sub-passes record - fog and waterline arm
            // independently (a straddling near plane arms the line before the eye submerges).
            if (WaterVolume.UnderwaterFogActive)
            {
                RecordFogPass(renderGraph, resources, cameraColor, "WaterUnderwaterFog");
            }
            // The meniscus darkens the finished frame along the crossing - the exact band a fog
            // debug view exists to show - so it stands down while one is selected. The absorb and
            // inscatter passes above are NOT gated: they ARE the view (absorb wipes, inscatter
            // writes), which is also why a view only appears while the fog is armed.
            if (WaterVolume.WaterlineActive && !WaterDebugView.FogViewActive)
                RecordWaterlinePass(renderGraph, resources, cameraColor);
        }

        // The waterline meniscus draws over the fogged scene AND (for the KWS-style lens tension)
        // re-samples it at a warped UV - a raster pass cannot read its own colour target, so the
        // scene is copied to a transient first and handed to the material. The copy costs one
        // camera-sized blit only during the few straddle frames the waterline is armed.
        void RecordWaterlinePass(RenderGraph renderGraph, UniversalResourceData resources,
                                 TextureHandle cameraColor)
        {
            TextureDesc copyDesc = renderGraph.GetTextureDesc(cameraColor);
            copyDesc.name = "_WaterlineSceneTex";
            copyDesc.clearBuffer = false;
            TextureHandle sceneCopy = renderGraph.CreateTexture(copyDesc);
            renderGraph.AddCopyPass(cameraColor, sceneCopy, passName: "WaterUnderwaterFog.WaterlineCopy");

            using var builder = renderGraph.AddRasterRenderPass<WaterlinePassData>(
                "WaterUnderwaterFog.Waterline", out WaterlinePassData data, _sampler);
            data.material = _material;
            data.sceneCopy = sceneCopy;
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(sceneCopy, AccessFlags.Read);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true);
            builder.SetRenderFunc((WaterlinePassData d, RasterGraphContext ctx) =>
            {
                d.material.SetTexture(ID_WaterlineSceneTex, d.sceneCopy);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, WaterlineShaderPass);
            });
        }

        sealed class WaterlinePassData
        {
            public Material material;
            public TextureHandle sceneCopy;
        }

        // Draw every live ocean-surface renderer's mesh with its OWN matrix, material and property
        // block through WaterSurface.shader's depth pass, so the prepass displacement matches the
        // visible surface exactly (whatever uniforms reach the real draw reach this one).
        void RecordSurfaceDepthPrepass(RenderGraph renderGraph, TextureHandle sizeSource)
        {
            // Camera-sized R32F colour (linear eye depth; clear 0 = "no surface") + its own depth
            // buffer so the nearest sheet wins where above/under overlap on screen.
            TextureDesc colorDesc = renderGraph.GetTextureDesc(sizeSource);
            colorDesc.name = "_OceanSurfaceEyeDepth";
            colorDesc.colorFormat = GraphicsFormat.R32_SFloat;
            colorDesc.depthBufferBits = DepthBits.None;
            colorDesc.msaaSamples = MSAASamples.None;
            colorDesc.clearBuffer = true;
            colorDesc.clearColor = Color.clear;
            TextureHandle color = renderGraph.CreateTexture(colorDesc);

            TextureDesc depthDesc = renderGraph.GetTextureDesc(sizeSource);
            depthDesc.name = "OceanSurfaceDepthBuffer";
            depthDesc.colorFormat = GraphicsFormat.None;
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.msaaSamples = MSAASamples.None;
            depthDesc.clearBuffer = true;
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            using var builder = renderGraph.AddRasterRenderPass<PrepassData>(_prepassSampler.name,
                out PrepassData data, _prepassSampler);
            data.renderers = s_SurfaceRenderers;
            data.block = _scratchBlock;
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);                          // driven by our own list
            builder.SetGlobalTextureAfterPass(color, ID_OceanSurfaceEyeDepth); // fog reads it later this frame
            builder.SetRenderFunc((PrepassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.renderers.Count; i++)
                {
                    Renderer renderer = d.renderers[i];
                    if (renderer == null || renderer.sharedMaterial == null) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    renderer.GetPropertyBlock(d.block); // the renderer's live per-body/per-level uniforms
                    ctx.cmd.DrawMesh(filter.sharedMesh, renderer.localToWorldMatrix,
                                     renderer.sharedMaterial, 0, SurfaceDepthShaderPass, d.block);
                }
            });
        }

        void RecordFogPass(RenderGraph renderGraph, UniversalResourceData resources,
                           TextureHandle cameraColor, string passName)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // published fog globals (shore field, FFT displacement, ...)
            // Two draws, ONE raster pass. Absorb multiplies the destination (Blend Zero SrcColor) and
            // inscatter adds to it (Blend One One) - both composite through the fixed-function blender,
            // and NEITHER shader samples the colour target, so this is ordinary blend accumulation in
            // submission order, not a read-after-write on the attachment. (Where a self-read IS needed,
            // RecordWaterlinePass copies to a transient first - deliberately, for exactly that reason.)
            // Same shape as WaterCausticProjectionPass, which already accumulates N fullscreen draws
            // with this very pair of blend modes into one ReadWrite colour attachment.
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, AbsorbShaderPass);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, InscatterShaderPass);
            });
        }
    }
}
#endif
