// WebGpuWater - the shared depth-only RenderGraph target used by the geometry prepasses.
//
// Both mesh prepasses (WaterExclusionDepthPass, WaterChunkDepthPass) want the same thing: a
// camera-sized, depth-only, cleared target whose FAR value reads as "nothing here". They carried a
// byte-identical private copy of the descriptor each, so a format change to one silently left the
// other on the old one. One definition, two callers.
#if WEBGPUWATER_URP
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterDepthTarget
    {
        /// <summary>A camera-sized depth-only target matching <paramref name="sizeSource"/>. Cleared,
        /// so an untouched texel reads as FAR - which every consumer treats as "no geometry here"
        /// (the ExclusionMeshDepthEmpty / Crest "raw == 0 -> not in view" convention).</summary>
        internal static TextureHandle Create(RenderGraph renderGraph, TextureHandle sizeSource, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(sizeSource);
            desc.name = name;
            desc.colorFormat = GraphicsFormat.None;   // depth only
            desc.depthBufferBits = DepthBits.Depth32;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = true;
            return renderGraph.CreateTexture(desc);
        }
    }
}
#endif
