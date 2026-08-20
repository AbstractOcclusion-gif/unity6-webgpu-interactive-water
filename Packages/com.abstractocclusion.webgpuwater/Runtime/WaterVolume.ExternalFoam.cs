// WebGpuWater - external surface registration for the after-fog foam redraw.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        readonly List<Renderer> _externalFoamRenderers = new();

        internal void RegisterExternalFoamRenderer(Renderer renderer)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (!_externalFoamRenderers.Contains(renderer)) _externalFoamRenderers.Add(renderer);
        }

        internal void UnregisterExternalFoamRenderer(Renderer renderer)
        {
            if (renderer != null) _externalFoamRenderers.Remove(renderer);
        }

        internal bool HasLiveExternalFoamRenderer
        {
            get
            {
                for (int i = 0; i < _externalFoamRenderers.Count; i++)
                    if (IsLiveRenderer(_externalFoamRenderers[i])) return true;
                return false;
            }
        }

        internal void CollectExternalFoamRenderers(List<Renderer> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            for (int i = 0; i < _externalFoamRenderers.Count; i++)
            {
                Renderer renderer = _externalFoamRenderers[i];
                if (IsLiveRenderer(renderer)) into.Add(renderer);
            }
        }

        static bool IsLiveRenderer(Renderer renderer)
            => renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
    }
}
