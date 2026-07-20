// WebGpuWater - mesh-chunk depth PREPASS (RenderGraph).
// For each active MESH-footprint chunk, draws its mesh front faces into _ChunkFogFrontDepth (entry)
// and back faces into _ChunkFogBackDepth (exit), depth only, then hands both to the rest of the
// frame as globals (SetGlobalTextureAfterPass - the project's RenderGraph handoff convention). The
// wall LOADs them (texel fetch, no sampler) to take the water column's entry/exit from the mesh.
//
// Runs BeforeRenderingTransparents so both depths exist before the wall (a transparent draw) reads
// them this frame. The chunk mesh is placed by the frame in-shader (PoolToWorld), fed the chunk's
// own block via WriteBodyProps - the same block the wall uses, so placement matches exactly.
#if WEBGPUWATER_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterChunkDepthPass : ScriptableRenderPass
    {
        // Before transparents: the wall renders in the transparent queue, so both depths must be
        // written and bound global by the time it draws.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingTransparents;

        const int FrontFaceShaderPass = 0; // Cull Back  -> entry depth
        const int BackFaceShaderPass  = 1; // Cull Front -> exit depth

        static readonly int ID_FrontDepth = Shader.PropertyToID("_ChunkFogFrontDepth");
        static readonly int ID_BackDepth  = Shader.PropertyToID("_ChunkFogBackDepth");

        readonly Material _material;
        readonly ProfilingSampler _frontSampler = new ProfilingSampler("WaterChunkDepth.Front");
        readonly ProfilingSampler _backSampler  = new ProfilingSampler("WaterChunkDepth.Back");

        // Reused each frame so the pass allocates no garbage.
        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        static readonly List<WaterVolume> s_MeshChunks = new List<WaterVolume>();

        internal WaterChunkDepthPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData
        {
            public Material material;
            public int shaderPass;
            public List<WaterVolume> chunks;
            public MaterialPropertyBlock block;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            WaterVolume.CollectMeshChunks(s_MeshChunks);
            if (s_MeshChunks.Count == 0) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle sizeSource = resources.activeColorTexture;
            if (!sizeSource.IsValid()) return;

            TextureHandle front = CreateDepthTarget(renderGraph, sizeSource, "_ChunkFogFrontDepth");
            TextureHandle back  = CreateDepthTarget(renderGraph, sizeSource, "_ChunkFogBackDepth");

            RecordFacePass(renderGraph, front, FrontFaceShaderPass, ID_FrontDepth, _frontSampler);
            RecordFacePass(renderGraph, back,  BackFaceShaderPass,  ID_BackDepth,  _backSampler);
        }

        // A camera-sized depth-only target. Cleared depth reads as FAR ("no chunk here"), which the
        // wall treats as empty, matching Crest's "raw == 0 -> not in view" convention.
        TextureHandle CreateDepthTarget(RenderGraph renderGraph, TextureHandle sizeSource, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(sizeSource);
            desc.name = name;
            desc.colorFormat = GraphicsFormat.None;   // depth only
            desc.depthBufferBits = DepthBits.Depth32;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            return renderGraph.CreateTexture(desc);
        }

        void RecordFacePass(RenderGraph renderGraph, TextureHandle depth, int shaderPass, int globalId,
                            ProfilingSampler sampler)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(sampler.name, out PassData data, sampler);

            data.material = _material;
            data.shaderPass = shaderPass;
            data.chunks = s_MeshChunks;
            data.block = _block;

            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);                    // driven by our own list, not renderer visibility
            builder.SetGlobalTextureAfterPass(depth, globalId); // wall reads it later this frame

            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.chunks.Count; i++)
                {
                    WaterVolume chunk = d.chunks[i];
                    if (chunk == null) continue;
                    Mesh mesh = chunk.ChunkDepthMesh;
                    if (mesh == null) continue;
                    chunk.WriteBodyProps(d.block);              // frame (_VolumeCenter/Extent/Rot) for PoolToWorld
                    ctx.cmd.DrawMesh(mesh, Matrix4x4.identity, d.material, 0, d.shaderPass, d.block);
                }
            });
        }
    }
}
#endif
