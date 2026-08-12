// WebGpuWater - screen-space caustic projection + refracted object shadow pass (RenderGraph).
// Reconstructs each opaque pixel's world position from the resolved _CameraDepthTexture, reprojects it through
// the water's pool-space caustic projection, and paints two things onto submerged surfaces:
//   * a MULTIPLY pass (shader pass 1) that darkens pixels under a submerged occluder - the refracted object
//     shadow FOREIGN shaders (terrain, Standard Lit) can't produce themselves; and
//   * an ADD pass (shader pass 0) for the refracted caustic pattern.
// The shadow runs first (darken the base) then the caustics add on top. Both are optional-additive identities
// outside real coverage, so armed-but-empty pixels are untouched.
//
// Injection point is AfterRenderingSkybox, which URP records IMMEDIATELY BEFORE it copies the camera colour
// into _CameraOpaqueTexture (UniversalRendererRenderGraph: custom AfterRenderingSkybox passes then
// m_CopyColorPass). That ordering is essential: the transparent water surface refracts by sampling
// _CameraOpaqueTexture, so these effects must be composited into the opaque scene BEFORE that copy or they are
// invisible THROUGH the surface. Running here they land in the opaque texture, so the refraction sees them, and
// direct (above-surface) views of the floor still show them. ConfigureInput(Depth) guarantees
// _CameraDepthTexture is produced before this early pass.
//
// Attachments: camera colour (ReadWrite, so the hardware blends composite onto the scene) and the camera
// DEPTH-STENCIL (Read), whose stencil holds bit 3 written by WaterReceiver / AnalyticPool during the opaque
// pass - the shader's NotEqual stencil test uses it to skip those already-shaded surfaces. The resolved
// _CameraDepthTexture is bound separately as a sampled texture for the world reconstruction.
#if WEBGPUWATER_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterCausticProjectionPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.AfterRenderingSkybox;

        const int CausticShaderPass = 0;
        const int ShadowShaderPass = 1;

        readonly Material _material;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterCausticProjection");

        // Reused each frame so the per-body loop allocates no garbage (mirrors WaterChunkDepthPass).
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        static readonly List<WaterVolume> s_CausticBodies = new List<WaterVolume>();
        static readonly List<WaterVolume> s_RefractedShadowBodies = new List<WaterVolume>();
        static readonly int ID_ScreenCausticIntensity = Shader.PropertyToID("_ScreenCausticIntensity");

        // Set by the feature each frame before enqueue: whether to run the refracted-shadow multiply pass.
        internal bool renderCaustics = true;
        internal bool renderRefractedShadow = true;

        internal WaterCausticProjectionPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
            // Force _CameraDepthTexture to be produced before this pass runs (it injects earlier than the fog
            // pass, which ran late enough to always find it ready). Needed for the world reconstruction.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        sealed class PassData
        {
            public Material material;
            public int shaderPass;
            public List<WaterVolume> bodies;
            public MaterialPropertyBlock block;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            // Build independent body sets because caustic light and the refracted occluder channel
            // have independent strengths. A zero-light ocean must not pay either fullscreen pass,
            // while a pool may still need its shadow with caustic intensity at zero.
            WaterVolume.CollectCausticProjectionBodies(s_CausticBodies, s_RefractedShadowBodies,
                                                        renderCaustics, renderRefractedShadow);
            if (s_CausticBodies.Count == 0 && s_RefractedShadowBodies.Count == 0) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            // Shadow first, then caustic light, preserving the established global composite order.
            if (s_RefractedShadowBodies.Count > 0)
                RecordProjectionPass(renderGraph, resources, cameraColor, ShadowShaderPass,
                                     "WaterRefractedShadow", s_RefractedShadowBodies);
            if (s_CausticBodies.Count > 0)
                RecordProjectionPass(renderGraph, resources, cameraColor, CausticShaderPass,
                                     "WaterCausticProjection", s_CausticBodies);
        }

        void RecordProjectionPass(RenderGraph renderGraph, UniversalResourceData resources,
                                  TextureHandle cameraColor, int shaderPass, string passName,
                                  List<WaterVolume> bodies)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            data.shaderPass = shaderPass;
            data.bodies = bodies;
            data.block = _block;
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            // Bind the depth-stencil target (Read only: the shader tests the stencil bit, it never writes depth).
            if (resources.activeDepthTexture.IsValid())
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
            // Resolved scene depth for the world reconstruction (SampleSceneDepth in the shader).
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // _LightDir + any global left unset; per-body _CausticTex/_WaterTex come from the block
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.bodies.Count; i++)
                {
                    WaterVolume body = d.bodies[i];
                    if (body == null) continue;
                    // Overwrite the block with THIS body's uniforms (frame + _CausticTex + _WaterTex +
                    // _CausticDepthFade + occluder flag), so the fullscreen projection reprojects through this
                    // body's caustics - exactly how WaterMembership relights a floater with its own lake.
                    body.WriteBodyProps(d.block);
                    // Per-body intensity: scales the feature's global Caustic Strength for THIS
                    // body's projection (the slider next to the Screen-Space Caustics opt-in).
                    d.block.SetFloat(ID_ScreenCausticIntensity, body.screenCausticIntensity);
                    CoreUtils.DrawFullScreen(ctx.cmd, d.material, d.block, d.shaderPass);
                }
            });
        }
    }
}
#endif
