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

        /// <summary>The provider a resolve picked for a point, before its surface is sampled
        /// (see <see cref="TrySelectProvider"/>). Carries the containment answer the selection
        /// already computed and - for the vertical-search intents, which must sample every
        /// candidate to rank it - the winner's height sample, so <see cref="TryFillSample"/>
        /// never asks the same provider the same question twice.</summary>
        internal struct SelectedProvider
        {
            public IWaterSurfaceProvider Provider;
            public bool InsideDomain;
            /// <summary>True when HeightSample holds a WaterQueryFields.Height sample of Provider
            /// at the query point (taken with the options' wavelength/ripple settings).</summary>
            public bool HasHeightSample;
            public WaterSample HeightSample;
        }

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

                if (TrySelectProvider(worldPoint, in options, out SelectedProvider selected,
                                      out WaterDomainValidity failure))
                    return TryFillSample(in selected, worldPoint, in options, out sample);

                // Rule 6 reproduces the legacy Primary fallback for an EMPTY SEARCH only: a bad
                // explicit hint is a caller bug, and handing it Primary would hide the bug.
                if (failure == WaterDomainValidity.MissingHint)
                {
                    sample.Validity = failure;
                    return false;
                }
                return ApplyFallback(worldPoint, in options, failure, ref sample);
            }
        }

        /// <summary>The selection half of <see cref="Resolve"/>: which provider answers for the
        /// point under the intent (rules 1-4 and 7), WITHOUT sampling its surface or applying
        /// the exclusion veto and seam blend - those are <see cref="TryFillSample"/>. Split out for
        /// the caller that wants the domain decision but its own surface read (WaterSplashEmitter:
        /// a volume body's droplets ride the legacy TryGetSurface drift, so the resolver's full
        /// sample would only be thrown away). The point must be finite. On false, failure says
        /// why and no fallback has been applied.</summary>
        internal static bool TrySelectProvider(Vector3 worldPoint, in WaterDomainQueryOptions options,
                                               out SelectedProvider selected,
                                               out WaterDomainValidity failure)
        {
            selected = default;
            failure = WaterDomainValidity.Valid;
            // Same boundary guard as Resolve: a NaN/Inf point must never reach a provider's
            // containment test, and direct callers of this entry bypass Resolve's check.
            if (!WaterSurfaceKinematics.IsFinite(worldPoint))
            {
                failure = WaterDomainValidity.NoContainingBody;
                return false;
            }
            switch (options.Intent)
            {
                case WaterQueryIntent.ExplicitBody:
                    return SelectExplicit(worldPoint, in options, ref selected, ref failure);
                case WaterQueryIntent.BuoyancySurface:
                    return SelectBuoyancy(worldPoint, in options, ref selected, ref failure);
                case WaterQueryIntent.RayInteraction:
                case WaterQueryIntent.NearestWithinVerticalLimits:
                    return SelectNearestSurface(worldPoint, in options, ref selected, ref failure);
                default:
                    return SelectContainingVolume(worldPoint, in options, ref selected, ref failure);
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

        static bool SelectExplicit(Vector3 point, in WaterDomainQueryOptions options,
                                   ref SelectedProvider selected, ref WaterDomainValidity failure)
        {
            // The interface compare cannot see a destroyed component; the typed field can.
            if (options.BodyHint == null)
            {
                failure = WaterDomainValidity.MissingHint;
                return false;
            }
            IWaterSurfaceProvider provider = options.BodyHint;
            selected.Provider = provider;
            selected.InsideDomain = provider.ContainsPoint(point);
            return true;
        }

        static bool SelectContainingVolume(Vector3 point, in WaterDomainQueryOptions options,
                                           ref SelectedProvider selected,
                                           ref WaterDomainValidity failure)
        {
            IWaterSurfaceProvider winner = SelectContaining(point, options.PreviousBodyId);
            if (winner == null)
            {
                failure = WaterDomainValidity.NoContainingBody;
                return false;
            }
            selected.Provider = winner;
            selected.InsideDomain = true;
            return true;
        }

        static bool SelectBuoyancy(Vector3 point, in WaterDomainQueryOptions options,
                                   ref SelectedProvider selected, ref WaterDomainValidity failure)
        {
            IWaterSurfaceProvider winner = SelectContaining(point, options.PreviousBodyId);
            if (winner != null)
            {
                selected.Provider = winner;
                selected.InsideDomain = true;
                return true;
            }

            // Not inside any water: a floater above the surface still needs the surface BELOW
            // it - and ONLY below: an object buried under a body's floor must not snap upward.
            // Every candidate above just answered "does not contain", so InsideDomain is known.
            if (!SelectSurfaceInRange(point, in options, surfaceBelowPointOnly: true, ref selected))
            {
                failure = WaterDomainValidity.NoSurfaceInRange;
                return false;
            }
            selected.InsideDomain = false;
            return true;
        }

        static bool SelectNearestSurface(Vector3 point, in WaterDomainQueryOptions options,
                                         ref SelectedProvider selected,
                                         ref WaterDomainValidity failure)
        {
            if (!SelectSurfaceInRange(point, in options, surfaceBelowPointOnly: false, ref selected))
            {
                failure = WaterDomainValidity.NoSurfaceInRange;
                return false;
            }
            selected.InsideDomain = selected.Provider.ContainsPoint(point);
            return true;
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
        // more than the switch margin (rule 4 for surface intents). Ranking needs every
        // candidate's height, so the winner's sample travels out in selected.HeightSample
        // rather than being taken a second time by TryFillSample.
        static bool SelectSurfaceInRange(Vector3 point, in WaterDomainQueryOptions options,
                                         bool surfaceBelowPointOnly, ref SelectedProvider selected)
        {
            float reach = options.MaxVerticalDistance > 0f
                ? options.MaxVerticalDistance : DefaultVerticalSearchMeters;
            IWaterSurfaceProvider best = null;
            WaterSample bestSurface = default;
            float bestGap = float.PositiveInfinity;
            IWaterSurfaceProvider previous = null;
            WaterSample previousSurface = default;
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
                    previousSurface = surface;
                    previousGap = gap;
                }
                if (gap < bestGap || (gap == bestGap && best != null && candidate.BodyId < best.BodyId))
                {
                    best = candidate;
                    bestSurface = surface;
                    bestGap = gap;
                }
            }

            if (best == null) return false;
            if (previous != null && best != previous && previousGap - bestGap < DomainSwitchMarginMeters)
            {
                best = previous;
                bestSurface = previousSurface;
            }
            selected.Provider = best;
            selected.HasHeightSample = true;
            selected.HeightSample = bestSurface;
            return true;
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

        /// <summary>The sampling half of <see cref="Resolve"/>: the selected provider's surface at
        /// the point (rules 5 and the seam handoff), the exact fields the options ask for. False
        /// with the Validity set when the provider cannot answer or the point is carved dry.</summary>
        internal static bool TryFillSample(in SelectedProvider selected, Vector3 point,
                                           in WaterDomainQueryOptions options,
                                           out WaterDomainSample sample)
        {
            IWaterSurfaceProvider provider = selected.Provider;
            sample = default;
            sample.BodyId = provider.BodyId;
            sample.Body = provider.Body;
            sample.Provider = provider;
            sample.InsideDomain = selected.InsideDomain;

            // The ranking search's height sample IS the requested sample when the caller wants
            // nothing more than height (GameplayBodyAt and every membership-style caller); any
            // other field set has to be asked for.
            WaterSample surface;
            if (selected.HasHeightSample && options.Fields == WaterQueryFields.Height)
                surface = selected.HeightSample;
            else if (!provider.TrySampleSurface(point, options.Fields, options.MinimumWaveLength,
                                                options.ExcludeInteractiveRipples, out surface))
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
            var selected = new SelectedProvider { Provider = fallback, InsideDomain = false };
            return TryFillSample(in selected, point, in options, out sample);
        }
    }
}
