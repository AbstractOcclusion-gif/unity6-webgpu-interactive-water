// WebGpuWater - opt-in after-fog reroute for USER transparent renderers (the public fog API's
// sorting half; see WebGpuWaterFogAPI.hlsl's header for the why).
//
// TWO reasons a queue-time transparent dies around this water (both hit Bert on day one):
// the water sheet renders with ZWrite On, so its depth kills any later draw BEHIND it (a
// submerged prop seen from the air, an above-water prop seen from below - the cross-side
// views); and on submerged frames the fullscreen fog paints the whole column's fog over
// queue-time transparents. This component reroutes a user renderer past both:
//  * whenever ANY water body is active it raises Renderer.forceRenderingOff (the
//    queue-time draw stands down, every camera) and the water feature draws the renderer
//    explicitly AFTER the whole water stack, over depth RESTORED to the opaque-only copy
//    (WaterRestoreOpaqueDepth) - z-tested against walls and terrain, visible through the
//    sheet from either side, fogged by its own material via WebGpuWaterFogAPI.hlsl;
//  * with no water body in the scene the flag stays down and nothing changes.
// The gate (WaterVolume.ActiveBodyCount) is the same one the feature's enqueue reads, in
// LateUpdate (rendering runs after all updates), so suppression and re-draw cannot
// disagree within a frame.
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

        internal static void ResetStaticState()
        {
            for (int index = 0; index < Live.Count; index++)
            {
                WaterFogTransparent transparent = Live[index];
                if (transparent != null && transparent._renderer != null)
                    transparent._renderer.forceRenderingOff = false;
            }
            Live.Clear();
        }

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
            // EVERY water frame, not just fog-armed ones (the cross-side fix): the water
            // sheet renders with ZWrite On, so a queue-time draw of this renderer dies
            // behind the sheet's depth on any cross-side view (submerged prop from the air,
            // above-water prop from below) - and on from-above ocean frames the old armed
            // gate was false, leaving exactly that broken queue-time path. Rerouted, the
            // renderer draws after the whole water stack over RESTORED opaque depth
            // (WaterRestoreOpaqueDepth), so it z-tests against walls but sees through the
            // sheet. Same gate as the feature's enqueue (ActiveBodyCount), read in
            // LateUpdate - rendering runs after all updates, so the two cannot disagree
            // within a frame. No water bodies: flag off, byte-identical legacy scene.
            _renderer.forceRenderingOff = WaterVolume.ActiveBodyCount > 0;
#endif
        }
    }
}
