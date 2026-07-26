// WebGpuWater - large-body atmosphere pass (RenderGraph).
// Fullscreen ocean god-ray shafts: a half-res raymarch of the view ray through the main light's
// shadow map (in-scatter with a Henyey-Greenstein phase), then an additive composite over the
// camera colour. Runs before post so bloom/tonemapping treat the shafts as scene light.
//
// Calm additions (KWS-informed): the raymarch uses a per-frame ANIMATED jitter and blends with
// LAST frame's shafts reprojected by scene position. The VISIBLE chain is deliberately the
// original proven one - raymarch into a transient, global handoff, composite - and the temporal
// history rides on the side: after the march, an AddCopyPass snapshots the transient into a
// persistent history RT that next frame's march samples as an ordinary material texture. If the
// history path ever fails, the failure mode is "less smoothing", never "no shafts". (A fancier
// version that rendered straight into imported ping-pong history RTs through a blur chain
// blanked the shafts on this setup and was rolled back to this shape; the shader still carries
// the unused blur passes at indices 1+2 for a future re-attempt.)
//
// Temporal runs for GAME cameras only (a scene-view camera would corrupt the game camera's
// reprojection pairing); other cameras march with temporal blend 0 and just skip the smoothing.
//
// Ocean-only: the feature gates enqueue on an active ocean with god rays on, and the shader reads
// _LargeGodRayDensity (0 for bounded bodies) as a second guard. Pools stay untouched.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class LargeBodyAtmospherePass : ScriptableRenderPass
    {
        // Before post so the additive shafts feed bloom/tonemapping like real in-scattered light -
        // but one slot AFTER the underwater fog (which sits at BeforeRenderingPostProcessing + 0):
        // the raymarch already applies the per-step Beer-Lambert fog extinction itself, so letting
        // the fog's absorb pass multiply the composited shafts a second time double-charged every
        // metre of fog and crushed the shafts as soon as fog density rose above zero. The +1 makes
        // the ordering a code guarantee instead of a renderer-asset feature-order accident.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing + 1;

        const int RaymarchShaderPass = 0;
        const int CompositeShaderPass = 3; // passes 1+2 are the (currently unused) blur pair
        const int HalfResDivisor = 2; // shafts are low-frequency; half res halves the march cost
        // History weight of the temporal accumulation - THE beam-pace dial. KWS ships 0.35, but
        // their volumetric caustic source is a pre-baked slow flipbook; ours is the LIVE wave
        // field, whose focus bands sweep and blink at physical wave speed, so the accumulation
        // has to provide the slowness itself. 0.88 integrates ~8 frames: beams breathe and hold
        // instead of popping. Lower toward 0.5 for snappier beams, raise toward 0.95 for calmer.
        const float TemporalHistoryWeight = 0.88f;

        // The raymarch pass hands its half-res target to the composite pass through this global,
        // via SetGlobalTextureAfterPass (the project's RenderGraph handoff convention).
        static readonly int ID_ShaftTexture = Shader.PropertyToID("_LargeGodRayTex");
        static readonly int ID_History = Shader.PropertyToID("_LargeGodRayHistory");
        static readonly int ID_PrevVP = Shader.PropertyToID("_GodRayPrevVP");
        static readonly int ID_CurrVP = Shader.PropertyToID("_GodRayCurrVP");
        static readonly int ID_TemporalBlend = Shader.PropertyToID("_GodRayTemporalBlend");
        static readonly int ID_Frame = Shader.PropertyToID("_GodRayFrame");

        readonly Material _material;
        readonly ProfilingSampler _raymarchSampler = new ProfilingSampler("LargeBodyGodRays.Raymarch");
        readonly ProfilingSampler _compositeSampler = new ProfilingSampler("LargeBodyGodRays.Composite");

        // Persistent half-res history for the temporal accumulation, filled by a copy AFTER the
        // march (single RT - the march never writes it directly, so there is no read/write hazard).
        RTHandle _history;
        int _historyWidth, _historyHeight;
        bool _historyValid;   // false until a game-camera frame has copied into it (and after resize)
        Matrix4x4 _prevViewProj;
        bool _prevViewProjValid;

        internal LargeBodyAtmospherePass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        internal void Dispose()
        {
            _history?.Release();
            _history = null;
            _historyValid = false;
            _prevViewProjValid = false;
        }

        sealed class RaymarchPassData
        {
            public Material material;
            public TextureHandle history;
            public Matrix4x4 prevViewProj;
            public Matrix4x4 currViewProj;
            public float temporalBlend;
            public float frame;
        }

        sealed class PassData { public Material material; }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;

            TextureHandle shaftTexture = CreateHalfResTarget(renderGraph, cameraColor, out TextureDesc halfDesc);

            bool temporal = cameraData.cameraType == CameraType.Game;
            if (temporal) EnsureHistory(halfDesc);

            Camera cam = cameraData.camera;
            Matrix4x4 viewProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
                                 * cam.worldToCameraMatrix;
            float blend = (temporal && _historyValid && _prevViewProjValid) ? TemporalHistoryWeight : 0f;
            Matrix4x4 prevVP = _prevViewProjValid ? _prevViewProj : viewProj;
            TextureHandle historyRead = (temporal && _historyValid)
                ? renderGraph.ImportTexture(_history)
                : TextureHandle.nullHandle;

            RecordRaymarch(renderGraph, resources, shaftTexture, historyRead, prevVP, viewProj, blend);

            if (temporal)
            {
                // Snapshot this frame's (post-blend) shafts into the persistent history for next
                // frame. Rides AFTER the visible chain: if this copy ever fails, the shafts on
                // screen are untouched - only the smoothing degrades.
                TextureHandle historyWrite = renderGraph.ImportTexture(_history);
                renderGraph.AddCopyPass(shaftTexture, historyWrite,
                                        passName: "LargeBodyGodRays.HistoryCopy");
                _historyValid = true;
                _prevViewProj = viewProj;
                _prevViewProjValid = true;
            }

            RecordComposite(renderGraph, cameraColor);
        }

        TextureHandle CreateHalfResTarget(RenderGraph renderGraph, TextureHandle cameraColor,
                                          out TextureDesc desc)
        {
            desc = renderGraph.GetTextureDesc(cameraColor);
            desc.name = "LargeBodyGodRaysHalfRes";
            desc.width = Mathf.Max(1, desc.width / HalfResDivisor);
            desc.height = Mathf.Max(1, desc.height / HalfResDivisor);
            desc.clearBuffer = true;         // start black so the additive composite adds only shafts
            desc.clearColor = Color.clear;
            desc.msaaSamples = MSAASamples.None; // post-style buffer; also lets AddCopyPass match history
            return renderGraph.CreateTexture(desc);
        }

        void EnsureHistory(in TextureDesc desc)
        {
            if (_history != null && _historyWidth == desc.width && _historyHeight == desc.height)
                return;
            _history?.Release();
            _history = RTHandles.Alloc(desc.width, desc.height, colorFormat: desc.format,
                                       name: "_LargeGodRayHistory");
            _historyWidth = desc.width;
            _historyHeight = desc.height;
            _historyValid = false; // fresh RT holds garbage; blend stays 0 until the first copy
        }

        void RecordRaymarch(RenderGraph renderGraph, UniversalResourceData resources,
                            TextureHandle shaftTexture, TextureHandle historyRead,
                            Matrix4x4 prevVP, Matrix4x4 currVP, float temporalBlend)
        {
            using var builder = renderGraph.AddRasterRenderPass<RaymarchPassData>(
                _raymarchSampler.name, out RaymarchPassData data, _raymarchSampler);

            data.material = _material;
            data.history = historyRead;
            data.prevViewProj = prevVP;
            data.currViewProj = currVP;
            data.temporalBlend = temporalBlend;
            data.frame = Time.frameCount & 1023; // wrapped for float precision in the jitter

            builder.SetRenderAttachment(shaftTexture, 0, AccessFlags.Write);
            if (historyRead.IsValid())
                builder.UseTexture(historyRead, AccessFlags.Read);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            if (resources.mainShadowsTexture.IsValid())
                builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true);                       // scene depth + shadow + shaft globals
            builder.SetGlobalTextureAfterPass(shaftTexture, ID_ShaftTexture); // hand to the composite pass
            builder.SetRenderFunc((RaymarchPassData d, RasterGraphContext ctx) =>
            {
                // Material state set at EXECUTE time, immediately before the draw, so multiple
                // cameras recording in one frame cannot alias each other's values.
                if (d.history.IsValid()) d.material.SetTexture(ID_History, d.history);
                else d.material.SetTexture(ID_History, Texture2D.blackTexture);
                d.material.SetMatrix(ID_PrevVP, d.prevViewProj);
                d.material.SetMatrix(ID_CurrVP, d.currViewProj);
                d.material.SetFloat(ID_TemporalBlend, d.temporalBlend);
                d.material.SetFloat(ID_Frame, d.frame);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, RaymarchShaderPass);
            });
        }

        void RecordComposite(RenderGraph renderGraph, TextureHandle cameraColor)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                _compositeSampler.name, out PassData data, _compositeSampler);

            data.material = _material;
            // ReadWrite (not Write): the Read half forces the rendered scene to be LOADED before the
            // additive Blend One One, instead of discarded (Write alone left the screen black).
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseAllGlobalTextures(true);                             // resolve _LargeGodRayTex
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, CompositeShaderPass));
        }
    }
}
#endif
