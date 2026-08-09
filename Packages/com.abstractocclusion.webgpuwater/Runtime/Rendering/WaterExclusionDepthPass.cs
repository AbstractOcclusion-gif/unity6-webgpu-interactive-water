// WebGpuWater - exclusion depth PREPASS (RenderGraph).
// For each active exclusion volume - EVERY shape, not just Mesh - draws its mesh front faces into
// _ExclusionMeshFrontDepth (entry) and back faces into _ExclusionMeshBackDepth (exit), depth only,
// then hands both to the rest of the frame as globals (SetGlobalTextureAfterPass - the project's
// RenderGraph handoff convention). Consumers LOAD them (texel fetch, no sampler) to take the DRY
// column's entry/exit from the mesh instead of from the analytic proxy.
//
// Runs after opaque geometry and before the skybox. This makes the span available to the
// screen-space caustic projection as well as the later transparent water/wall draws. Unlike the
// chunk twin, placement comes from the DRAW MATRIX - an exclusion volume has a real transform, so
// the mesh needs no frame block.
//
// WHY EVERY SHAPE, when only Mesh volumes are read back today. A Box or Sphere is not mesh-less -
// the wall draws its unit cube/sphere every frame - so the analytic shapes were absent from these
// RTs by omission, not by cost. Filling them gives the carve ONE complete rasterised silhouette,
// which is what a consumer needs before it can take the carve boundary from raster instead of
// re-deriving it analytically. The consumer gate (_ExclusionMeshCount) is deliberately NOT widened
// here: switching Box/Sphere onto these RTs would make an existing scene depend on this feature
// being installed on the renderer, which is a manual step. Draws now, reads later.
#if WEBGPUWATER_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterExclusionDepthPass : ScriptableRenderPass
    {
        // Caustic projection follows the skybox. Publish the carve span before it so dry mesh
        // interiors cannot receive screen-space underwater lighting.
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.AfterRenderingOpaques;

        const int FrontFaceShaderPass = 0; // Cull Back  -> entry depth
        const int BackFaceShaderPass  = 1; // Cull Front -> exit depth

        static readonly int ID_FrontDepth = Shader.PropertyToID("_ExclusionMeshFrontDepth");
        static readonly int ID_BackDepth  = Shader.PropertyToID("_ExclusionMeshBackDepth");
        static readonly int ID_PrepassValid = Shader.PropertyToID("_ExclusionPrepassValid");

        readonly Material _material;
        readonly ProfilingSampler _frontSampler = new ProfilingSampler("WaterExclusionDepth.Front");
        readonly ProfilingSampler _backSampler  = new ProfilingSampler("WaterExclusionDepth.Back");

        // Reused each frame so the pass allocates no garbage.
        static readonly List<WaterExclusionVolume> s_PrepassVolumes = new List<WaterExclusionVolume>();

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

            WaterExclusionVolume.CollectPrepassVolumes(s_PrepassVolumes);
            if (s_PrepassVolumes.Count == 0) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            TextureHandle sizeSource = resources.activeColorTexture;
            if (!sizeSource.IsValid()) return;

            TextureHandle front = WaterDepthTarget.Create(renderGraph, sizeSource, "_ExclusionMeshFrontDepth");
            TextureHandle back  = WaterDepthTarget.Create(renderGraph, sizeSource, "_ExclusionMeshBackDepth");

            RecordFacePass(renderGraph, front, FrontFaceShaderPass, ID_FrontDepth, _frontSampler);
            RecordFacePass(renderGraph, back,  BackFaceShaderPass,  ID_BackDepth,  _backSampler);

            // Both targets are written for this frame, so consumers may trust them. Raised only
            // after every early-out above, and lowered again next frame by WaterUniformPublisher, so
            // this reads "the prepass RAN" and never "a volume exists" - the two differ exactly when
            // this feature is missing from the renderer, which is a manual setup step.
            Shader.SetGlobalFloat(ID_PrepassValid, 1f);
        }

        void RecordFacePass(RenderGraph renderGraph, TextureHandle depth, int shaderPass, int globalId,
                            ProfilingSampler sampler)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(sampler.name, out PassData data, sampler);

            data.material = _material;
            data.shaderPass = shaderPass;
            data.volumes = s_PrepassVolumes;

            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);                    // driven by our own list, not renderer visibility
            builder.SetGlobalTextureAfterPass(depth, globalId); // consumers read it later this frame

            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.volumes.Count; i++)
                {
                    WaterExclusionVolume volume = d.volumes[i];
                    if (volume == null) continue;
                    Mesh mesh = volume.PrepassMesh;
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
