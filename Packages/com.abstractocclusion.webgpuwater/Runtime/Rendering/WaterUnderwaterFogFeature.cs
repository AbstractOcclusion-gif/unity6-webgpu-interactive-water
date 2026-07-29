// WebGpuWater - real underwater fog render feature (URP, RenderGraph).
// Fogs the whole view when the camera is submerged in ANY water body, replacing the per-object
// trick for the camera-underwater case. Add this feature once to the renderer used by the water
// camera and assign the WaterUnderwaterFog shader; it self-gates on WaterVolume.UnderwaterFogActive,
// so above water it never enqueues and nothing changes.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using System.Collections.Generic;
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
        // Release BEFORE (re)creating. URP calls Create() on OnEnable, on OnValidate and on every
        // domain reload, but Dispose() only when the feature asset is destroyed - so allocating here
        // without releasing first leaked one engine Material (and, where the pass owns RTHandles, the
        // pass's history targets) per inspector tweak. Create and Dispose now share ONE teardown, so
        // they cannot drift.
            ReleaseResources();
            _particlePass = new WaterParticlesAfterFogPass(); // material-free: needs no shader
            if (underwaterFogShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(underwaterFogShader);
            _pass = new WaterUnderwaterFogPass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Never for material/prefab thumbnails, and never into a REFLECTION: this pass paints
            // the camera colour, and a mirror rendered from below the surface would come back with
            // the water's own fog painted over it. See WaterPassCameraGate.
            if (WaterPassCameraGate.SkipCameraFullscreen(renderingData.cameraData.cameraType)) return;
            // After-fog reroute: WaterFoamParticles/WaterSplashEmitter SKIP their queue-time
            // draws whenever the fullscreen fog is armed (the fog would paint the water
            // column's fog over the sprites), and the water surface skips its POND FOAM on
            // armed camera-in-air frames for the same reason - so this pass must enqueue on
            // EXACTLY those gates, independent of the fog shader being assigned, or the
            // reroute would eat the particles/foam entirely on a misconfigured renderer.
            bool foamOverlayNeeded = !WaterVolume.CameraSubmerged && WaterVolume.AnyFoamOverlayBody();
            // A fullscreen-fog debug view owns the frame, so the sprites and the foam overlay
            // stand down rather than paint over it. They vanish entirely for the duration - their
            // queue-time draw is already skipped while the fog is armed - which is the right
            // trade for a view whose whole job is to show what the FOG did. See WaterDebugView.
            if (!WaterDebugView.FogViewActive
                && WaterVolume.UnderwaterFogActive && _particlePass != null
                && (WaterFoamParticles.Live.Count > 0 || WaterSplashEmitter.Live.Count > 0
                    || foamOverlayNeeded))
                renderer.EnqueuePass(_particlePass);

            if (_pass == null) return; // shader unassigned / not created
            // Fog: ocean = submerged only, pond = whenever fog is on. Waterline: the near plane
            // straddles the surface (partial submersion) - it arms BEFORE the eye submerges, so
            // the crossing shows a meniscus line instead of a hard pop. The pass records only
            // the sub-passes whose gate is set.
            if (!WaterVolume.UnderwaterFogActive && !WaterVolume.WaterlineActive) return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
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

        // WaterSurface.shader's "PondFoamOverlay" pass, drawn per above-surface renderer below.
        const int FoamOverlayShaderPass = 2;
        static readonly List<Renderer> s_FoamRenderers = new List<Renderer>();
        // Reused each frame so the overlay draws allocate no garbage (the prepass recipe).
        readonly MaterialPropertyBlock _scratchBlock = new MaterialPropertyBlock();

        sealed class PassData { public Camera camera; public MaterialPropertyBlock block; }

        internal WaterParticlesAfterFogPass()
        {
            renderPassEvent = WaterUnderwaterFogPass.InjectionPoint + 2;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (!resources.activeColorTexture.IsValid()) return;

            // Pond-foam overlay (the surface-foam half of the particle/fog sorting fix): the
            // queue-time surface pass skipped its pond foam this frame, so collect the live
            // above-surface renderers to re-draw it here - after the fog and the god rays,
            // before the sprites (spray lands ON the foam). Submerged frames collect nothing:
            // the fog is in front of the foam there and Pass 0 kept its own draw.
            s_FoamRenderers.Clear();
            if (!WaterVolume.CameraSubmerged)
                WaterVolume.CollectFoamOverlayRenderers(s_FoamRenderers);

            using (var builder = renderGraph.AddRasterRenderPass("WaterParticlesAfterFog",
                                                                 out PassData data, _sampler))
            {
                data.camera = cameraData.camera;
                data.block = _scratchBlock;
                // ReadWrite (not Write): the sprites and the pond-foam overlay are alpha-blended, so the
                // rendered scene must be LOADED, not discarded. Write alone left the screen black on a
                // load-action-honouring backend - the same trap LargeBodyAtmospherePass.cs already records.
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                // Depth READ: the sprites keep their hardware ZTest against the scene (and the
                // soft-fade depth sample rides the global _CameraDepthTexture).
                if (resources.activeDepthTexture.IsValid())
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowPassCulling(false); // driven by our own lists, not renderer visibility
                builder.UseAllGlobalTextures(true);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                {
                    DrawFoamOverlays(ctx.cmd, d.block);
                    var quads = WaterFoamParticles.Live;
                    for (int i = 0; i < quads.Count; i++)
                        if (quads[i] != null) quads[i].RenderAfterFog(ctx.cmd, d.camera);
                    var emitters = WaterSplashEmitter.Live;
                    for (int i = 0; i < emitters.Count; i++)
                        if (emitters[i] != null) emitters[i].DrawAfterFog(ctx.cmd);
                });
            }
        }

        // Draw each collected above-surface renderer through WaterSurface's PondFoamOverlay
        // pass with its OWN mesh, matrix, material and live property block - the eye-depth
        // prepass recipe, so the overlay displaces exactly like the visible surface.
        static void DrawFoamOverlays(RasterCommandBuffer cmd, MaterialPropertyBlock block)
        {
            for (int i = 0; i < s_FoamRenderers.Count; i++)
            {
                Renderer renderer = s_FoamRenderers[i];
                if (renderer == null || renderer.sharedMaterial == null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                renderer.GetPropertyBlock(block);
                cmd.DrawMesh(filter.sharedMesh, renderer.localToWorldMatrix,
                             renderer.sharedMaterial, 0, FoamOverlayShaderPass, block);
            }
        }
    }
}
#endif
