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
        [Tooltip("The Hidden/AbstractOcclusion/WebGpuWater/WaterHeightRT shader. Required in player builds because Shader.Find assets can be stripped.")]
        [SerializeField] Shader heightRtShader;

        WaterUnderwaterFogPass _pass;
        WaterParticlesAfterFogPass _particlePass;
        Material _material;
        Material _heightRtMaterial;

        public override void Create()
        {
        // Release BEFORE (re)creating. URP calls Create() on OnEnable, on OnValidate and on every
        // domain reload, but Dispose() only when the feature asset is destroyed - so allocating here
        // without releasing first leaked one engine Material (and, where the pass owns RTHandles, the
        // pass's history targets) per inspector tweak. Create and Dispose now share ONE teardown, so
        // they cannot drift.
            ReleaseResources();
            _particlePass = new WaterParticlesAfterFogPass(); // sprite half is material-free
            if (underwaterFogShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(underwaterFogShader);
            if (heightRtShader != null)
                _heightRtMaterial = CoreUtils.CreateEngineMaterial(heightRtShader);
            _pass = new WaterUnderwaterFogPass(_material, _heightRtMaterial);
            // The user-transparent half needs the fog material for its depth-restore draw
            // (WaterRestoreOpaqueDepth - see the cross-side fix). Null (shader unassigned)
            // degrades to drawing without the restore: cross-side props stay hidden behind
            // the sheet's depth, exactly the pre-fix behaviour, never worse.
            _particlePass.FogMaterial = _material;
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
            //
            // TWO independent halves ride this one pass since the cross-side fix:
            //  * sprites/foam - armed frames only, exactly the original condition;
            //  * user transparents (WaterFogTransparent) - EVERY frame a water body is
            //    active, armed or not: the component suppresses their queue-time draw on the
            //    same ActiveBodyCount gate, so this pass is their only draw whenever water
            //    exists (the sheet's ZWrite On depth would eat a queue-time draw on any
            //    cross-side view - see DrawUserTransparents).
            bool spritesNeeded = WaterVolume.UnderwaterFogActive
                              && (WaterFoamParticles.Live.Count > 0 || WaterSplashEmitter.Live.Count > 0
                                  || foamOverlayNeeded);
            bool userTransparentsNeeded = WaterFogTransparent.Live.Count > 0
                                       && WaterVolume.ActiveBodyCount > 0;
            if (!WaterDebugView.FogViewActive && _particlePass != null
                && (spritesNeeded || userTransparentsNeeded))
            {
                _particlePass.SpritesThisFrame = spritesNeeded;
                _particlePass.UserTransparentsThisFrame = userTransparentsNeeded;
                renderer.EnqueuePass(_particlePass);
            }

            if (_pass == null) return; // shader unassigned / not created
            // Fog: ocean = submerged only, pond = whenever fog is on. Waterline: the near plane
            // straddles the surface (partial submersion) - it arms BEFORE the eye submerges, so
            // the crossing shows a meniscus line instead of a hard pop. The pass records only
            // the sub-passes whose gate is set.
            if (!WaterVolume.UnderwaterFogActive && !WaterVolume.WaterlineActive) return;
            // PER-CAMERA pond cull: a bounded fog volume entirely outside THIS camera's frustum
            // can put nothing on its screen, so the whole recorded chain (prepass, height RTs,
            // classify, absorb, inscatter, waterline) is skipped for this camera - the pond arm
            // is otherwise unconditional ("murk from any angle"), so a pool BEHIND the camera
            // used to pay it all. The particle/transparent pass above deliberately does NOT take
            // this cull (transparents exist outside the volume); each scene view runs its own
            // test, so authoring keeps its fog while the game camera looks away. Oceans always
            // pass (infinite fog), and a null fog source fails ARMED.
            WaterVolume fogSource = WaterVolume.FogSource;
            if (fogSource != null && !fogSource.FogVolumeVisibleTo(renderingData.cameraData.camera))
                return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
        {
            CoreUtils.Destroy(_material);
            CoreUtils.Destroy(_heightRtMaterial);
            _material = null;
            _heightRtMaterial = null;
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
        readonly ProfilingSampler _userSampler = new ProfilingSampler("WaterTransparentsAfterFog");

        // Set by the feature each enqueue (see AddRenderPasses): which of the two halves run.
        internal bool SpritesThisFrame;
        internal bool UserTransparentsThisFrame;
        // The fog material, for the WaterRestoreOpaqueDepth draw in the user half. Null when
        // the fog shader is unassigned - the user draws then run without the restore.
        internal Material FogMaterial;

        // WaterSurface.shader's "PondFoamOverlay" pass, drawn per above-surface renderer below.
        const int FoamOverlayShaderPass = 2;
        static readonly List<Renderer> s_FoamRenderers = new List<Renderer>();
        // Reused each frame so the overlay draws allocate no garbage (the prepass recipe).
        readonly MaterialPropertyBlock _scratchBlock = new MaterialPropertyBlock();

        sealed class PassData { public Camera camera; public MaterialPropertyBlock block; }
        sealed class UserPassData { public Material fogMaterial; }

        internal WaterParticlesAfterFogPass()
        {
            renderPassEvent = WaterUnderwaterFogPass.InjectionPoint + 2;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (!resources.activeColorTexture.IsValid()) return;

            // ---- Half 1: sprites + pond-foam overlay (armed frames only, the original pass) --
            if (SpritesThisFrame)
            {
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

            // ---- Half 2: user transparents (every water frame - the cross-side fix) ----------
            // Recorded AFTER the sprite pass so the props composite over fog, god rays, foam
            // and spray. Its FIRST draw rewrites the depth attachment from the opaque-only
            // _CameraDepthTexture (WaterRestoreOpaqueDepth): the water sheet renders with
            // ZWrite On, so without the restore any prop on the FAR side of the sheet
            // (submerged prop from the air, above-water prop from below) z-failed against the
            // sheet's depth and vanished - while walls and terrain must keep occluding, which
            // is why the depth is restored rather than the test dropped. Depth ReadWrite: the
            // restore writes it, the prop draws then test against the restored values.
            if (UserTransparentsThisFrame)
            {
                using (var builder = renderGraph.AddRasterRenderPass("WaterTransparentsAfterFog",
                                                                     out UserPassData data, _userSampler))
                {
                    data.fogMaterial = FogMaterial;
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    if (resources.activeDepthTexture.IsValid())
                        builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                    if (resources.cameraDepthTexture.IsValid())
                        builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.AllowPassCulling(false); // driven by our own list, not renderer visibility
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderFunc((UserPassData d, RasterGraphContext ctx) =>
                    {
                        if (d.fogMaterial != null)
                            CoreUtils.DrawFullScreen(ctx.cmd, d.fogMaterial, null,
                                                     WaterUnderwaterFogPass.RestoreDepthShaderPass);
                        DrawUserTransparents(ctx.cmd);
                    });
                }
            }
        }

        // Draws the USER transparents that opted into the after-water reroute via the
        // WaterFogTransparent component (the public fog API's sorting half). Their
        // queue-time draw is suppressed on EVERY water frame (forceRenderingOff, set by the
        // component on the SAME ActiveBodyCount gate that enqueues this half), so this
        // explicit draw is their only submission - after the whole water stack, over the
        // restored opaque depth. Materials come from the component's CACHE, never
        // Renderer.sharedMaterials here - that property allocates a fresh array per call
        // and this pass stays GC-free (swap materials at runtime ->
        // WaterFogTransparent.RefreshMaterials). Shader pass 0 per submesh: the forward
        // pass of URP shaders and of every Shader Graph output. List order, no depth sort
        // between multiple props - overlapping user transparents may layer by registration
        // order (v1 trade, same as the sprite emitters).
        static void DrawUserTransparents(RasterCommandBuffer cmd)
        {
            var live = WaterFogTransparent.Live;
            for (int i = 0; i < live.Count; i++)
            {
                WaterFogTransparent entry = live[i];
                if (entry == null) continue;
                Renderer target = entry.TargetRenderer;
                Material[] materials = entry.Materials;
                if (target == null || materials == null) continue;
                for (int m = 0; m < materials.Length; m++)
                    if (materials[m] != null)
                        cmd.DrawRenderer(target, materials[m], m, 0);
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
