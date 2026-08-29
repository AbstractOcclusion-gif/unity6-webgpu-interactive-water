// WebGpuWater - the 3D water-domain resolver: one deterministic rule set for "which water".
//
// Resolution rules, in order (see the 2026-08-29 research doc, section 3.2):
//   1. ExplicitBody short-circuits to the hint.
//   2. Candidates are the providers whose FULL-XYZ domain contains the point - never the
//      XZ footprint alone, so two bodies stacked at different elevations resolve correctly.
//   3. Overlaps rank by specificity (ribbon over plane), then smaller domain volume (a pond
//      inside an ocean's box wins), then registration id - every step deterministic.
//   4. Hysteresis: the caller's previous domain keeps the point while it stays within
//      DomainSwitchMarginMeters of its boundary, so an edge probe cannot chatter.
//   5. Exclusions veto gameplay validity (dry wins); the sample still reports what it found.
//   6. No candidate NEVER falls back to Primary unless the caller asked (WaterFallbackPolicy).
//   7. The vertical-search intents find the nearest surface within explicit limits.
//
// Allocation-free in steady state: struct options/samples, index-based candidate iteration.
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Entry point for 3D water-domain queries (see WaterDomainSample). Static like the
    /// body registry it reads; all state lives in the caller's options (hysteresis hint).</summary>
    public static class WaterDomainResolver
    {
        /// <summary>Boundary band a caller's PREVIOUS domain keeps claiming (rule 4). Also the
        /// switch margin for the vertical-search intents: a new surface must beat the previous
        /// one by this much before the caller is handed over.</summary>
        public const float DomainSwitchMarginMeters = 0.5f;

        /// <summary>Vertical reach used when options.MaxVerticalDistance is unset (metres).</summary>
        public const float DefaultVerticalSearchMeters = 10f;

        internal const string ResolveMarkerName = "WaterDomainResolver.Resolve";
        static readonly ProfilerMarker ResolveMarker =
            new ProfilerMarker(ProfilerCategory.Scripts, ResolveMarkerName);

        /// <summary>Resolve one point. True only when the sample is Valid; on false the sample's
        /// Validity says why (dry land, excluded space, bad hint, out of range).</summary>
        public static bool Resolve(Vector3 worldPoint, in WaterDomainQueryOptions options,
                                   out WaterDomainSample sample)
        {
            using (ResolveMarker.Auto())
            {
                sample = default;
                if (!WaterSurfaceKinematics.IsFinite(worldPoint))
                {
                    sample.Validity = WaterDomainValidity.NoContainingBody;
                    return false;
                }

                switch (options.Intent)
                {
                    case WaterQueryIntent.ExplicitBody:
                        return ResolveExplicit(worldPoint, in options, ref sample);
                    case WaterQueryIntent.BuoyancySurface:
                        return ResolveBuoyancy(worldPoint, in options, ref sample);
                    case WaterQueryIntent.RayInteraction:
                    case WaterQueryIntent.NearestWithinVerticalLimits:
                        return ResolveNearestSurface(worldPoint, in options, ref sample);
                    default:
                        return ResolveContaining(worldPoint, in options, ref sample);
                }
            }
        }

        /// <summary>Migration shim for gameplay callers that keyed on WaterVolume.BodyContaining:
        /// the body whose domain answers for this point under the given intent - full-XYZ,
        /// exclusion-aware, hysteresis through the caller's stored id, and never a Primary
        /// fallback unless the intent options say so via the overload below. Null = no valid
        /// water here; previousBodyId is updated either way.</summary>
        public static WaterVolume GameplayBodyAt(Vector3 worldPoint, WaterQueryIntent intent,
                                                 ref int previousBodyId)
            => GameplayBodyAt(worldPoint, intent, WaterFallbackPolicy.None, ref previousBodyId);

        public static WaterVolume GameplayBodyAt(Vector3 worldPoint, WaterQueryIntent intent,
                                                 WaterFallbackPolicy fallback, ref int previousBodyId)
        {
            WaterDomainQueryOptions options = WaterDomainQueryOptions.ForIntent(intent);
            options.PreviousBodyId = previousBodyId;
            options.Fallback = fallback;
            options.Fields = WaterQueryFields.Height;
            if (!Resolve(worldPoint, in options, out WaterDomainSample sample) || sample.Body == null)
            {
                previousBodyId = 0;
                return null;
            }
            previousBodyId = sample.BodyId;
            return sample.Body;
        }

        /// <summary>Batched form: fill results[i] for points[i] under one options set (one owner,
        /// one hysteresis hint - a hull's probes belong to one body decision).</summary>
        public static void ResolveBatch(IReadOnlyList<Vector3> points, WaterDomainSample[] results,
                                        in WaterDomainQueryOptions options)
        {
            if (points == null) throw new System.ArgumentNullException(nameof(points));
            if (results == null) throw new System.ArgumentNullException(nameof(results));
            if (results.Length < points.Count)
                throw new System.ArgumentException(
                    $"results length ({results.Length}) is smaller than points ({points.Count}).",
                    nameof(results));

            for (int i = 0; i < points.Count; i++)
                Resolve(points[i], in options, out results[i]);
        }

        // ---- intent implementations --------------------------------------------------------

        static bool ResolveExplicit(Vector3 point, in WaterDomainQueryOptions options,
                                    ref WaterDomainSample sample)
        {
            IWaterSurfaceProvider provider = options.BodyHint;
            if (provider == null || options.BodyHint == null) // Unity fake-null on the component
            {
                sample.Validity = WaterDomainValidity.MissingHint;
                return false;
            }
            return FillSample(provider, point, in options, provider.ContainsPoint(point), ref sample);
        }

        static bool ResolveContaining(Vector3 point, in WaterDomainQueryOptions options,
                                      ref WaterDomainSample sample)
        {
            IWaterSurfaceProvider winner = SelectContaining(point, options.PreviousBodyId);
            if (winner != null)
                return FillSample(winner, point, in options, insideDomain: true, ref sample);
            return ApplyFallback(point, in options, WaterDomainValidity.NoContainingBody, ref sample);
        }

        static bool ResolveBuoyancy(Vector3 point, in WaterDomainQueryOptions options,
                                    ref WaterDomainSample sample)
        {
            IWaterSurfaceProvider winner = SelectContaining(point, options.PreviousBodyId);
            if (winner != null)
                return FillSample(winner, point, in options, insideDomain: true, ref sample);

            // Not inside any water: a floater above the surface still needs the surface BELOW
            // it - and ONLY below: an object buried under a body's floor must not snap upward.
            winner = SelectSurfaceInRange(point, in options, surfaceBelowPointOnly: true, out _);
            if (winner != null)
                return FillSample(winner, point, in options, insideDomain: false, ref sample);
            return ApplyFallback(point, in options, WaterDomainValidity.NoSurfaceInRange, ref sample);
        }

        static bool ResolveNearestSurface(Vector3 point, in WaterDomainQueryOptions options,
                                          ref WaterDomainSample sample)
        {
            IWaterSurfaceProvider winner = SelectSurfaceInRange(point, in options, surfaceBelowPointOnly: false, out _);
            if (winner != null)
                return FillSample(winner, point, in options, winner.ContainsPoint(point), ref sample);
            return ApplyFallback(point, in options, WaterDomainValidity.NoSurfaceInRange, ref sample);
        }

        // ---- candidate selection -----------------------------------------------------------

        // Rule 3 ordering for containment: specificity desc, domain volume asc, BodyId asc.
        // Rule 4 hysteresis: when nothing strictly contains the point, the previous domain keeps
        // it while its widened boundary still does - and when the previous domain DOES contain
        // the point, an equally-ranked rival cannot take it (ties stick to the incumbent).
        static IWaterSurfaceProvider SelectContaining(Vector3 point, int previousBodyId)
        {
            IWaterSurfaceProvider best = null;
            float bestVolume = float.PositiveInfinity;
            IWaterSurfaceProvider previous = null;
            bool previousContains = false;

            int count = WaterSurfaceProviders.CandidateCount;
            for (int i = 0; i < count; i++)
            {
                IWaterSurfaceProvider candidate = WaterSurfaceProviders.Candidate(i);
                if (candidate == null) continue;
                bool isPrevious = previousBodyId != 0 && candidate.BodyId == previousBodyId;
                if (isPrevious) previous = candidate;
                if (!candidate.ContainsPoint(point)) continue;
                if (isPrevious) previousContains = true;

                float volume = DomainVolume(candidate);
                if (best == null || RanksAbove(candidate, volume, best, bestVolume))
                {
                    best = candidate;
                    bestVolume = volume;
                }
            }

            if (best != null)
            {
                // Incumbent keeps a tie: equal specificity and no smaller domain means switching
                // would be arbitrary - and arbitrary switches at an overlap boundary are chatter.
                if (previousContains && previous != best &&
                    previous.Specificity == best.Specificity &&
                    DomainVolume(previous) <= bestVolume)
                    return previous;
                return best;
            }

            // Nothing strict: the widened previous boundary is the hysteresis band (rule 4).
            if (previous != null && previous.ContainsPointWithin(point, DomainSwitchMarginMeters))
                return previous;
            return null;
        }

        static bool RanksAbove(IWaterSurfaceProvider candidate, float candidateVolume,
                               IWaterSurfaceProvider incumbent, float incumbentVolume)
        {
            if (candidate.Specificity != incumbent.Specificity)
                return candidate.Specificity > incumbent.Specificity;
            if (candidateVolume != incumbentVolume) return candidateVolume < incumbentVolume;
            return candidate.BodyId < incumbent.BodyId;
        }

        // Nearest surface by vertical gap within the caller's reach. surfaceBelowPointOnly
        // restricts to surfaces at or below the point (the buoyancy fall-through: the water a
        // falling object is heading for). The previous body wins unless a rival is closer by
        // more than the switch margin (rule 4 for surface intents).
        static IWaterSurfaceProvider SelectSurfaceInRange(Vector3 point,
                                                          in WaterDomainQueryOptions options,
                                                          bool surfaceBelowPointOnly, out float bestGap)
        {
            float reach = options.MaxVerticalDistance > 0f
                ? options.MaxVerticalDistance : DefaultVerticalSearchMeters;
            IWaterSurfaceProvider best = null;
            bestGap = float.PositiveInfinity;
            IWaterSurfaceProvider previous = null;
            float previousGap = float.PositiveInfinity;

            int count = WaterSurfaceProviders.CandidateCount;
            for (int i = 0; i < count; i++)
            {
                IWaterSurfaceProvider candidate = WaterSurfaceProviders.Candidate(i);
                if (candidate == null || !candidate.ContainsXZ(point)) continue;
                if (!candidate.TrySampleSurface(point, WaterQueryFields.Height, options.MinimumWaveLength,
                                                options.ExcludeInteractiveRipples, out WaterSample surface))
                    continue;

                float signed = surface.Height - point.y;
                if (surfaceBelowPointOnly && signed > 0f) continue;
                float gap = Mathf.Abs(signed);
                if (gap > reach) continue;

                if (options.PreviousBodyId != 0 && candidate.BodyId == options.PreviousBodyId)
                {
                    previous = candidate;
                    previousGap = gap;
                }
                if (gap < bestGap || (gap == bestGap && best != null && candidate.BodyId < best.BodyId))
                {
                    best = candidate;
                    bestGap = gap;
                }
            }

            if (previous != null && best != previous && previousGap - bestGap < DomainSwitchMarginMeters)
            {
                bestGap = previousGap;
                return previous;
            }
            return best;
        }

        // Bounded providers rank by their axis-aligned bounds volume; an unbounded ocean reports
        // infinite bounds and therefore loses every overlap against bounded water (rule 3).
        static float DomainVolume(IWaterSurfaceProvider provider)
        {
            Vector3 size = provider.DomainBounds.size;
            if (float.IsInfinity(size.x) || float.IsInfinity(size.y) || float.IsInfinity(size.z))
                return float.PositiveInfinity;
            return size.x * size.y * size.z;
        }

        // ---- sample assembly ---------------------------------------------------------------

        static bool FillSample(IWaterSurfaceProvider provider, Vector3 point,
                               in WaterDomainQueryOptions options, bool insideDomain,
                               ref WaterDomainSample sample)
        {
            sample.BodyId = provider.BodyId;
            sample.Body = provider.Body;
            sample.Provider = provider;
            sample.InsideDomain = insideDomain;

            if (!provider.TrySampleSurface(point, options.Fields, options.MinimumWaveLength,
                                           options.ExcludeInteractiveRipples, out WaterSample surface))
            {
                sample.Validity = WaterDomainValidity.NoContainingBody;
                return false;
            }
            sample.SurfaceHeight = surface.Height;
            sample.SurfaceNormal = surface.Normal;
            sample.Velocity = surface.Velocity;
            sample.SignedDepth = surface.Height - point.y;

            // Rule 5: dry wins. Box/sphere are exact analytic; mesh volumes answer through their
            // authored proxy (WaterExclusionVolume.meshProxy) - the documented CPU policy.
            sample.Excluded = WaterExclusionVolume.ContainsPoint(point);
            if (sample.Excluded && !options.IncludeExcludedSpace)
            {
                sample.Validity = WaterDomainValidity.Excluded;
                return false;
            }

            WaterTopology.ApplySeamBlend(provider, point, in options, ref sample);
            sample.Validity = WaterDomainValidity.Valid;
            return true;
        }

        // Rule 6: Primary only when the caller explicitly asked. Reproduces the legacy shape
        // (Primary, else any live body) but records that the point is OUTSIDE the domain.
        static bool ApplyFallback(Vector3 point, in WaterDomainQueryOptions options,
                                  WaterDomainValidity failure, ref WaterDomainSample sample)
        {
            if (options.Fallback != WaterFallbackPolicy.PrimaryBody)
            {
                sample.Validity = failure;
                return false;
            }

            WaterVolume fallback = WaterVolume.Resolve();
            if (fallback == null)
            {
                sample.Validity = failure;
                return false;
            }
            return FillSample(fallback, point, in options, insideDomain: false, ref sample);
        }
    }
}
