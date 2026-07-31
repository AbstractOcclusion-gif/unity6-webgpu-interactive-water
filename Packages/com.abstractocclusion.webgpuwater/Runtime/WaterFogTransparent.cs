// WebGpuWater - opt-in after-fog reroute for USER transparent renderers (the public fog API's
// sorting half; see WebGpuWaterFogAPI.hlsl's header for the why).
//
// The fullscreen underwater fog runs AFTER every transparent and integrates to the OPAQUE
// depth, so on submerged frames it paints the full water column's fog over anything drawn in
// the transparent queue. The package sprites solved this by skipping their queue-time draw on
// fog-armed frames and re-drawing AFTER the fog (WaterParticlesAfterFogPass); this component
// extends exactly that reroute to a user renderer whose material prices its own fog through
// WebGpuWaterFogAPI.hlsl:
//  * on armed frames it raises Renderer.forceRenderingOff (the queue-time draw stands down,
//    every camera) and WaterParticlesAfterFogPass draws the renderer explicitly - after the
//    fog and the god rays, BEFORE the package sprites so spray reads as the nearest layer;
//  * disarmed frames lower the flag and touch nothing - the scene is byte-identical to a
//    project that never heard of this component.
// The gate is the SAME WaterVolume.UnderwaterFogActive the sprite reroute keys on, read in
// LateUpdate (rendering runs after all updates), so user transparents and package sprites
// flip on exactly the same frames, never one frame apart.
//
// Known trades, same as the sprites: while a fog DEBUG VIEW owns the frame the after-fog pass
// stands down and rerouted renderers vanish for the duration; and on armed frames the
// renderer is absent from reflection/thumbnail cameras (the reroute is global, the re-draw is
// the game camera's fog pass).
//
// Materials are CACHED on enable: Renderer.sharedMaterials allocates a fresh array per call,
// and the after-fog pass must stay GC-free. Swap materials at runtime -> call
// RefreshMaterials() (or toggle the component).
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Fog Transparent")]
    [RequireComponent(typeof(Renderer))]
    [DisallowMultipleComponent]
    public sealed class WaterFogTransparent : MonoBehaviour
    {
        /// <summary>Live rerouted renderers, drawn by WaterParticlesAfterFogPass on armed
        /// frames (the WaterFoamParticles.Live pattern).</summary>
        internal static readonly List<WaterFogTransparent> Live = new List<WaterFogTransparent>();

        Renderer _renderer;
        Material[] _materials;

        internal Renderer TargetRenderer => _renderer;
        internal Material[] Materials => _materials;

        void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            _materials = _renderer != null ? _renderer.sharedMaterials : null;
            Live.Add(this);
        }

        void OnDisable()
        {
            Live.Remove(this);
            // Never leave a renderer suppressed behind us - the flag outlives the reroute.
            if (_renderer != null) _renderer.forceRenderingOff = false;
            _renderer = null;
            _materials = null;
        }

        /// <summary>Re-read the renderer's shared materials after swapping them at runtime
        /// (they are cached so the after-fog draw allocates no garbage).</summary>
        public void RefreshMaterials()
        {
            if (_renderer != null) _materials = _renderer.sharedMaterials;
        }

        void LateUpdate()
        {
#if WEBGPUWATER_URP
            if (_renderer == null) return;
            // Same gate, same frame semantics as the sprite reroute (WaterFoamParticles reads
            // this in its own submit path): armed -> the after-fog pass owns the draw.
            _renderer.forceRenderingOff = WaterVolume.UnderwaterFogActive;
#endif
        }
    }
}
