#ifndef WATER_OCEAN_RENDERED_COVERAGE_INCLUDED
#define WATER_OCEAN_RENDERED_COVERAGE_INCLUDED

// An ownership claim may OVERRIDE the analytic coverage only when both flanks corroborate
// it - in BOTH directions (F7 + F9). Isolated half-resolution silhouette coin tosses at
// the sheet's edge-on folds lie both ways: an isolated AIR texel under a submerged eye
// printed the dark line (F7, 2026-08-11), and an isolated WET texel under an eye in AIR
// armed the mask over open sky - the above-water specks, strongest with the camera just
// above the surface where the silhouette fills the view (F9, 2026-08-11). The prepass is
// ONE two-sided draw, so neither the visible-twin depth bias nor any material state can
// reach these texels - corroboration here is the only gate they pass through.
// A GENUINE region interior corroborates itself by construction (every pixel has same-side
// neighbours); only its one edge row falls back to the analytic feather, which is the
// correct answer at an edge anyway. OceanOwnershipSample is supplied by each consumer
// beside its RT binding.
#define OWNERSHIP_AIR_CORROBORATION_WET_MAX 0.5
// Wet-claim threshold, deliberately the SAME fraction mirrored: a flank whose valid share
// is mostly wet corroborates a wet centre exactly as a mostly-air flank corroborates an
// air centre.
#define OWNERSHIP_WET_CORROBORATION_WET_MIN 0.5

float OceanRenderedCoverage(float2 uv, float analyticCoverage, float2 screenDirection)
{
    float2 prepassTexel = 1.0 / max(_ScaledScreenParams.xy * _OceanSurfacePrepassScale, 1.0);
    float2 offset = screenDirection * prepassTexel;
    float2 center = OceanOwnershipSample(uv);
    float2 flankA = OceanOwnershipSample(uv + offset);
    float2 flankB = OceanOwnershipSample(uv - offset);
    float2 ownership = center * 0.5 + flankA * 0.25 + flankB * 0.25;
    float coverage = saturate(ownership.r + analyticCoverage * (1.0 - ownership.g));
    bool flankAAir = flankA.g > 0.5
                  && flankA.r < OWNERSHIP_AIR_CORROBORATION_WET_MAX * flankA.g;
    bool flankBAir = flankB.g > 0.5
                  && flankB.r < OWNERSHIP_AIR_CORROBORATION_WET_MAX * flankB.g;
    // F7: an uncorroborated AIR claim may not pull coverage BELOW analytic.
    if (!(flankAAir && flankBAir))
        coverage = max(coverage, analyticCoverage);
    bool flankAWet = flankA.g > 0.5
                  && flankA.r > OWNERSHIP_WET_CORROBORATION_WET_MIN * flankA.g;
    bool flankBWet = flankB.g > 0.5
                  && flankB.r > OWNERSHIP_WET_CORROBORATION_WET_MIN * flankB.g;
    // F9: the mirror - an uncorroborated WET claim may not push coverage ABOVE analytic.
    // With BOTH directions uncorroborated (mixed flanks) the two clamps meet at exactly
    // the analytic coverage - the rendered authority stands down entirely.
    if (!(flankAWet && flankBWet))
        coverage = min(coverage, analyticCoverage);
    return coverage;
}

#endif
