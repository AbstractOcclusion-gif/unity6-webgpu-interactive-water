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

        // Frustum-tested variants. An external surface (a river ribbon) can sit far outside this
        // body's own box, so the after-fog foam redraw must cull it by ITS bounds: gating it on the
        // parent's CullBounds dropped the ribbon's foam whenever the parent left the view while
        // Pass 0 had already deferred that foam to the overlay. Null planes = no test.
        internal bool AnyExternalFoamRendererVisible(Plane[] frustumPlanes)
        {
            for (int i = 0; i < _externalFoamRenderers.Count; i++)
                if (IsVisibleExternalRenderer(_externalFoamRenderers[i], frustumPlanes)) return true;
            return false;
        }

        internal void CollectExternalFoamRenderers(List<Renderer> into, Plane[] frustumPlanes)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            for (int i = 0; i < _externalFoamRenderers.Count; i++)
            {
                Renderer renderer = _externalFoamRenderers[i];
                if (IsVisibleExternalRenderer(renderer, frustumPlanes)) into.Add(renderer);
            }
        }

        static bool IsVisibleExternalRenderer(Renderer renderer, Plane[] frustumPlanes)
            => IsLiveRenderer(renderer) &&
               (frustumPlanes == null ||
                GeometryUtility.TestPlanesAABB(frustumPlanes, renderer.bounds));

        static bool IsLiveRenderer(Renderer renderer)
            => renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
    }
}
