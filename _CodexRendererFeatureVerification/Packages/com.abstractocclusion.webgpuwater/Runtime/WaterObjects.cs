// WebGpuWater - destroying runtime-created UnityEngine.Objects under [ExecuteAlways].
//
// Every collaborator that creates a material, mesh or RenderTexture at runtime must tear it down on
// the matching disable, and the correct call differs by mode: Destroy is deferred and does nothing
// in edit mode, DestroyImmediate is illegal from most play-mode callbacks. This package runs under
// [ExecuteAlways], so BOTH modes are live and the choice cannot be hard-coded at the call site.
// The four-line decision was copy-pasted into three classes before it was shared here.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterObjects
    {
        /// <summary>Destroy an object created at runtime, picking the call the current mode allows.
        /// Null-safe, so a caller tearing down a collaborator that never initialized needs no guard.</summary>
        internal static void DestroyRuntime(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
