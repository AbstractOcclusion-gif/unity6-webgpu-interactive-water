// WebGpuWater - the surface-provider seam: any surface type answers the same domain queries.
//
// WaterVolume's rectangular pool plane, the unbounded ocean clipmap and the river ribbon are
// different SURFACES of the same query contract. The resolver ranks whichever providers claim a
// point; a more SPECIFIC provider (the ribbon) beats the broad plane it is authored inside, so a
// river point finally answers with real spline elevation instead of the parent rectangle.
//
// Everything here is CPU-analytic (WebGPU: no readback on the query path), so a provider keeps
// answering when its body holds no GPU simulation lease.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>A water surface that participates in domain resolution. Implemented by
    /// WaterVolume (pool plane / ocean clipmap) and WaterRiverSurfaceProvider (spline ribbon).</summary>
    public interface IWaterSurfaceProvider
    {
        /// <summary>Stable per-session identity (see WaterSurfaceProviders.NextBodyId). Stable
        /// across frames and enable/disable within a session; NOT persistent across sessions -
        /// persistent identity belongs to WaterConnectionPort's authored string id.</summary>
        int BodyId { get; }

        /// <summary>The owning WaterVolume, when one exists (a ribbon may stand alone).</summary>
        WaterVolume Body { get; }

        /// <summary>Overlap rank: when several providers contain a point, the highest specificity
        /// wins before any volume comparison (WaterSurfaceProviders.RibbonSpecificity beats
        /// VolumeSpecificity, so the ribbon owns its own footprint inside a parent volume).</summary>
        int Specificity { get; }

        /// <summary>Horizontal footprint test (ignores height).</summary>
        bool ContainsXZ(Vector3 worldPoint);

        /// <summary>Full-XYZ domain containment.</summary>
        bool ContainsPoint(Vector3 worldPoint);

        /// <summary>ContainsPoint widened by a boundary margin in metres - the hysteresis band a
        /// caller's previous domain keeps claiming (see WaterDomainResolver). The default keeps
        /// the strict test for providers with no cheap widened form.</summary>
        bool ContainsPointWithin(Vector3 worldPoint, float boundaryMarginMeters)
            => ContainsPoint(worldPoint);

        /// <summary>Surface height/normal/velocity at the point's XZ, same contract as
        /// IWaterHeightSampler (false = outside footprint or not ready).</summary>
        bool TrySampleSurface(Vector3 worldPoint, WaterQueryFields fields, float minimumWaveLength,
                              bool excludeInteractiveRipples, out WaterSample sample);

        /// <summary>Axis-aligned domain bounds, for volume ranking (smaller domain wins overlap
        /// ties). An unbounded ocean reports infinite size so any bounded body beats it.</summary>
        Bounds DomainBounds { get; }
    }

    /// <summary>Registry of the providers that are NOT WaterVolume bodies (the bodies register
    /// through WaterVolume.Bodies already; duplicating them here would double-count candidates).
    /// Also owns the session-stable BodyId counter both kinds draw from.</summary>
    internal static class WaterSurfaceProviders
    {
        internal const int VolumeSpecificity = 0;
        internal const int RibbonSpecificity = 1;

        static readonly List<IWaterSurfaceProvider> _registered = new List<IWaterSurfaceProvider>();
        static int _nextBodyId;

        /// <summary>Next stable body id. Ids start at 1 so 0 stays the "no previous body"
        /// sentinel in WaterDomainQueryOptions.PreviousBodyId.</summary>
        internal static int NextBodyId() => ++_nextBodyId;

        internal static void Register(IWaterSurfaceProvider provider)
        {
            if (provider == null) throw new System.ArgumentNullException(nameof(provider));
            if (!_registered.Contains(provider)) _registered.Add(provider);
        }

        internal static void Unregister(IWaterSurfaceProvider provider)
            => _registered.Remove(provider);

        // Candidate enumeration folds the body registry and the extra providers into one index
        // space so the resolver iterates without building a merged list per query.
        internal static int CandidateCount => WaterVolume.Bodies.Count + _registered.Count;

        internal static IWaterSurfaceProvider Candidate(int index)
            => index < WaterVolume.Bodies.Count
                ? WaterVolume.Bodies[index]
                : _registered[index - WaterVolume.Bodies.Count];

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        // The id counter deliberately survives: with domain reload off, surviving components
        // keep their lazily-assigned ids, so resetting the counter would reissue them.
        internal static void ResetStaticState()
        {
            _registered.Clear();
        }
    }
}
