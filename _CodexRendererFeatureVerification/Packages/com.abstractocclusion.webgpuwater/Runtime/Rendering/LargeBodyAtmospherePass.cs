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
using System.Collections.Generic;
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
        // LAST frame's post-blend shafts, bound as a REAL global (Shader.SetGlobalTexture) so the
        // WATER SURFACE - which draws long before this pass - can add the shaft light into its
        // underside TIR mirror (_UnderMirrorShafts). See the binding site for why it is the
        // HISTORY and not this frame's transient.
        static readonly int ID_ShaftsLastFrame = Shader.PropertyToID("_LargeGodRayLastFrame");
        static readonly int ID_PrevVP = Shader.PropertyToID("_GodRayPrevVP");
        static readonly int ID_CurrVP = Shader.PropertyToID("_GodRayCurrVP");
        static readonly int ID_TemporalBlend = Shader.PropertyToID("_GodRayTemporalBlend");
        static readonly int ID_Frame = Shader.PropertyToID("_GodRayFrame");

        readonly Material _material;
        readonly ProfilingSampler _raymarchSampler = new ProfilingSampler("LargeBodyGodRays.Raymarch");
        readonly ProfilingSampler _compositeSampler = new ProfilingSampler("LargeBodyGodRays.Composite");
        readonly Dictionary<Camera, MaterialPropertyBlock> _sourceBlocks =
            new Dictionary<Camera, MaterialPropertyBlock>();

        // Persistent half-res history for the temporal accumulation, filled by a copy AFTER the
        // march (single RT - the march never writes it directly, so there is no read/write hazard).
        // PER GAME CAMERA (audit H-4): keyed on size alone, two game cameras of equal resolution
        // used to share one RT and one prevVP - each frame the second camera reprojected against
        // the first one's matrix and history, corrupting both accumulations.
        sealed class CameraHistory
        {
            public RTHandle Rt;
            public int Width, Height;
            public WaterVolume SourceOcean;
            public bool Valid;   // false until this camera has copied into it (and after resize)
            public Matrix4x4 PrevViewProj;
            public bool PrevValid;
        }
        readonly Dictionary<Camera, CameraHistory> _histories = new Dictionary<Camera, CameraHistory>();
        // Destroyed cameras leave dictionary entries behind (a Camera key never hashes away).
        // Swept only when the count passes this bound, so the common one-camera case never scans.
        const int HistorySweepThreshold = 4;

        internal LargeBodyAtmospherePass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        internal void Dispose()
        {
            foreach (CameraHistory entry in _histories.Values)
                entry.Rt?.Release();
            _histories.Clear();
            _sourceBlocks.Clear();
            // Last-body-out reset (the stale-global trap): the surface's mirror term must never
            // sample a released RT. Black is also what the term multiplies to nothing.
            Shader.SetGlobalTexture(ID_ShaftsLastFrame, Texture2D.blackTexture);
        }

        sealed class RaymarchPassData
        {
            public Material material;
            public TextureHandle history;
            public Matrix4x4 prevViewProj;
            public Matrix4x4 currViewProj;
            public float temporalBlend;
            public float frame;
            public MaterialPropertyBlock block;
        }

        sealed class PassData
        {
            public Material material;
            public MaterialPropertyBlock block;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;
            WaterVolume sourceOcean = LargeBodyAtmosphereGate.SourceOcean;
            if (sourceOcean == null) return;
            // Graphs for two cameras may both be recorded before either executes. Cache one block
            // per camera so their source data cannot alias, without allocating a native-backed
            // MaterialPropertyBlock every rendered frame.
            Camera cam = cameraData.camera;
            MaterialPropertyBlock sourceBlock = SourceBlockFor(cam);
            sourceOcean.WriteBodyProps(sourceBlock);

            TextureHandle shaftTexture = CreateHalfResTarget(renderGraph, cameraColor, out TextureDesc halfDesc);

            bool temporal = cameraData.cameraType == CameraType.Game;
            CameraHistory entry = temporal ? EnsureHistory(cam, halfDesc, sourceOcean) : null;

            // VOLUMETRIC COUPLING (KWS increment, phase 1): bind LAST frame's post-blend shafts
            // as a real global so the water surface - drawn long before this pass, outside the
            // graph's dependency tracking - can add the shaft light into its underside TIR mirror.
            // Deliberately the HISTORY, not this frame's transient: rescheduling the march before
            // transparents was tried in this pass's past and blanked the shafts (see the file
            // header's rollback note), while the 0.88 blend already integrates ~8 frames, so one
            // frame of lag is invisible where a scheduling regression is not. Black when no valid
            // history exists (first frames, resize, non-game camera), so the mirror term adds
            // nothing - and the strength itself is gated CPU-side on an active god-ray ocean
            // (WaterVolume.UnderwaterMirrorShafts). Set at record time: all of this camera's
            // graph executes after every record, so the binding reaches its transparents.
            Shader.SetGlobalTexture(ID_ShaftsLastFrame,
                (temporal && entry.Valid) ? (Texture)entry.Rt : Texture2D.blackTexture);

            Matrix4x4 viewProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
                                 * cam.worldToCameraMatrix;
            bool historyUsable = temporal && entry.Valid && entry.PrevValid;
            float blend = historyUsable ? TemporalHistoryWeight : 0f;
            Matrix4x4 prevVP = (temporal && entry.PrevValid) ? entry.PrevViewProj : viewProj;
            TextureHandle historyRead = (temporal && entry.Valid)
                ? renderGraph.ImportTexture(entry.Rt)
                : TextureHandle.nullHandle;

            RecordRaymarch(renderGraph, resources, shaftTexture, historyRead, prevVP, viewProj, blend,
                           sourceBlock);

            if (temporal)
            {
                // Snapshot this frame's (post-blend) shafts into the persistent history for next
                // frame. Rides AFTER the visible chain: if this copy ever fails, the shafts on
                // screen are untouched - only the smoothing degrades.
                TextureHandle historyWrite = renderGraph.ImportTexture(entry.Rt);
                renderGraph.AddCopyPass(shaftTexture, historyWrite,
                                        passName: "LargeBodyGodRays.HistoryCopy");
                entry.Valid = true;
                entry.PrevViewProj = viewProj;
                entry.PrevValid = true;
            }

            RecordComposite(renderGraph, cameraColor, sourceBlock);
        }

        MaterialPropertyBlock SourceBlockFor(Camera camera)
        {
            if (!_sourceBlocks.TryGetValue(camera, out MaterialPropertyBlock block))
            {
                if (_sourceBlocks.Count >= HistorySweepThreshold) SweepDeadSourceBlocks();
                block = new MaterialPropertyBlock();
                _sourceBlocks.Add(camera, block);
            }
            return block;
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

        CameraHistory EnsureHistory(Camera cam, in TextureDesc desc, WaterVolume sourceOcean)
        {
            if (!_histories.TryGetValue(cam, out CameraHistory entry))
            {
                if (_histories.Count >= HistorySweepThreshold) SweepDeadCameras();
                entry = new CameraHistory();
                _histories.Add(cam, entry);
            }
            if (entry.Rt != null && entry.Width == desc.width && entry.Height == desc.height)
            {
                if (entry.SourceOcean != sourceOcean)
                {
                    entry.SourceOcean = sourceOcean;
                    entry.Valid = false;
                    entry.PrevValid = false;
                }
                return entry;
            }
            entry.Rt?.Release();
            entry.Rt = RTHandles.Alloc(desc.width, desc.height, colorFormat: desc.format,
                                       name: "_LargeGodRayHistory");
            entry.Width = desc.width;
            entry.Height = desc.height;
            entry.SourceOcean = sourceOcean;
            entry.Valid = false; // fresh RT holds garbage; blend stays 0 until the first copy
            entry.PrevValid = false;
            return entry;
        }

        // Release entries whose camera has been destroyed. Called only when the map outgrows the
        // sweep threshold, so the common one-game-camera case never pays the scan.
        void SweepDeadCameras()
        {
            List<Camera> dead = null;
            foreach (KeyValuePair<Camera, CameraHistory> pair in _histories)
            {
                if (pair.Key != null) continue; // Unity fake-null: destroyed Camera compares == null
                (dead ??= new List<Camera>()).Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
            {
                _histories[dead[i]].Rt?.Release();
                _histories.Remove(dead[i]);
            }
        }

        void SweepDeadSourceBlocks()
        {
            List<Camera> dead = null;
            foreach (KeyValuePair<Camera, MaterialPropertyBlock> pair in _sourceBlocks)
            {
                if (pair.Key != null) continue;
                (dead ??= new List<Camera>()).Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _sourceBlocks.Remove(dead[i]);
        }

        void RecordRaymarch(RenderGraph renderGraph, UniversalResourceData resources,
                            TextureHandle shaftTexture, TextureHandle historyRead,
                            Matrix4x4 prevVP, Matrix4x4 currVP, float temporalBlend,
                            MaterialPropertyBlock sourceBlock)
        {
            using var builder = renderGraph.AddRasterRenderPass<RaymarchPassData>(
                _raymarchSampler.name, out RaymarchPassData data, _raymarchSampler);

            data.material = _material;
            data.history = historyRead;
            data.prevViewProj = prevVP;
            data.currViewProj = currVP;
            data.temporalBlend = temporalBlend;
            data.frame = Time.frameCount & 1023; // wrapped for float precision in the jitter
            data.block = sourceBlock;

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
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, d.block, RaymarchShaderPass);
            });
        }

        void RecordComposite(RenderGraph renderGraph, TextureHandle cameraColor,
                             MaterialPropertyBlock sourceBlock)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(
                _compositeSampler.name, out PassData data, _compositeSampler);

            data.material = _material;
            data.block = sourceBlock;
            // ReadWrite (not Write): the Read half forces the rendered scene to be LOADED before the
            // additive Blend One One, instead of discarded (Write alone left the screen black).
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseAllGlobalTextures(true);                             // resolve _LargeGodRayTex
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, d.block, CompositeShaderPass));
        }
    }
}
#endif
