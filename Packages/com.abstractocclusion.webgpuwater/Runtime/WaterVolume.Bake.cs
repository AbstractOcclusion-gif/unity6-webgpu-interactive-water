// WebGpuWater - WaterVolume partial: the terrain bake entry points.
//
// Context-menu hooks for the two lazily-built terrain fields (pool-space bed height, world-frame
// shore depth) plus their debug toggles. Authoring-time actions, not part of any frame path -
// they exist because a terrain or a volume placement can change after the field was baked.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // ---- terrain bed-height bake (WaterBedBaker) --------------------------

        /// <summary>Re-sample the terrain heightmap into the pool-space bed map. Call after
        /// the terrain or the volume placement changes.</summary>
        [ContextMenu("Rebake Bed")]
        public void RebakeBed() => BedBaker.Rebake();

        [ContextMenu("Rebake Shore Depth (Layer A)")]
        public void RebakeShoreDepth() => ShoreDepth.Rebake();

        [ContextMenu("Toggle Shore Depth Debug (Layer A)")]
        public void ToggleShoreDepthDebug()
        {
            WaterShoreDepthField.ToggleDepthDebug();
            ShoreDepth.EnsureBaked(); // the next body-property publish carries the debug flag
        }

        [ContextMenu("Toggle Shore SDF Debug (Layer A)")]
        public void ToggleShoreSdfDebug()
        {
            WaterShoreDepthField.ToggleSdfDebug();
            ShoreDepth.EnsureBaked(); // the next body-property publish carries the debug flag
        }
    }
}
