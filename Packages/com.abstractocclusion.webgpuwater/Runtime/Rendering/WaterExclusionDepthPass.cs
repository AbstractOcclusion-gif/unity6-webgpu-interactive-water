// WebGpuWater - mesh-exclusion depth PREPASS (RenderGraph).
// For each active MESH-shape exclusion volume, draws its mesh front faces into
// _ExclusionMeshFrontDepth (entry) and back faces into _ExclusionMeshBackDepth (exit), depth only,
// then hands both to the rest of the frame as globals (SetGlobalTextureAfterPass - the project's
// RenderGraph handoff convention). Consumers LOAD them (texel fetch, no sampler) to take the DRY
// column's entry/exit from the mesh instead of from the analytic proxy.
//
// Runs BeforeRenderingTransparents so both depths exist before the water surface and the exclusion
// wall (transparent draws) read them this frame. Unlike the chunk twin, placement comes from the
// DRAW MATRIX - an exclusion volume has a real transform, so the mesh needs no frame block.
#if WEBGPUWATER_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterExclusionDepthPass : ScriptableRenderPass
    {
        // Before transparents: the surface and the wall both render in the transparent queue, so
        // both depths must be written and bound global by the time they draw.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingTransparents;

        const int FrontFaceShaderPass = 0; // Cull Back  -> entry depth
        const int BackFaceShaderPass  = 1; // Cull Front -> exit depth

        static readonly int ID_FrontDepth = Shader.PropertyToID("_ExclusionMeshFrontDepth");
        static readonly int ID_BackDepth  = Shader.PropertyToID("_ExclusionMeshBackDepth");

        readonly Material _material;
        readonly ProfilingSampler _frontSampler = new ProfilingSampler("WaterExclusionDepth.Front");
        readonly ProfilingSampler _backSampler  = new ProfilingSampler("WaterExclusionDepth.Back");

        // Reused each frame so the pass allocates no garbage.
        static readonly List<WaterExclusionVolume> s_MeshVolumes = new List<WaterExclusionVolume>();

        internal WaterExclusionDepthPass(Material material)
        {
            _material = material;
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData
        {
            public Material material;
            public int shaderPass;
            public List<WaterExclusionVolume> volumes;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            WaterExclusionVolume.CollectMeshVolumes(s_MeshVolumes);
            if (s_MeshVolumes.Count == 0) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle sizeSource = resources.activeColorTexture;
            if (!sizeSource.IsValid()) return;

            TextureHandle front = CreateDepthTarget(renderGraph, sizeSource, "_ExclusionMeshFrontDepth");
            TextureHandle back  = CreateDepthTarget(renderGraph, sizeSource, "_ExclusionMeshBackDepth");

            RecordFacePass(renderGraph, front, FrontFaceShaderPass, ID_FrontDepth, _frontSampler);
            RecordFacePass(renderGraph, back,  BackFaceShaderPass,  ID_BackDepth,  _backSampler);
        }

        // A camera-sized depth-only target. Cleared depth reads as FAR ("no mesh volume here"),
        // which every consumer treats as empty - the ExclusionMeshDepthEmpty convention.
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
            data.volumes = s_MeshVolumes;

            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);                    // driven by our own list, not renderer visibility
            builder.SetGlobalTextureAfterPass(depth, globalId); // consumers read it later this frame

            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.volumes.Count; i++)
                {
                    WaterExclusionVolume volume = d.volumes[i];
                    if (volume == null) continue;
                    Mesh mesh = volume.CarveMesh;
                    if (mesh == null) continue;
                    // The volume's own shape-to-world places and sizes the mesh, exactly as it
                    // places the unit cube a Box volume carves with.
                    ctx.cmd.DrawMesh(mesh, volume.ShapeToWorldMatrix(), d.material, 0, d.shaderPass);
                }
            });
        }
    }
}
#endif
