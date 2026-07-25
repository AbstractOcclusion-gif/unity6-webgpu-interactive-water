// WebGpuWater - water exclusion volumes (dry interiors), Phase 1: analytic OBBs.
// Declares the global exclusion uniforms plus the ONE point test every water consumer
// shares (reuse-never-rewrite: consumers include this file, nobody hand-copies the loop).
// Kept OUT of WaterShared.hlsl on purpose: that header's contract is pure math with no
// global declarations, and these ARE globals.
//
// Published by WaterUniformPublisher.PublishSharedGlobals (global, not per body: a dry
// room is dry in whichever body intersects it). _ExclusionWorldToBox maps world space
// into each volume's UNIT box, so one matrix carries centre + rotation + size and the
// inside test is abs(local) <= 0.5 per axis.
//
#ifndef WEBGL_WATER_EXCLUSION_INCLUDED
#define WEBGL_WATER_EXCLUSION_INCLUDED

#include "WaterShared.hlsl" // IntersectCube + RAY_SLAB_EPSILON for ExclusionRayLength

// C# pair: WaterExclusionVolume.MaxVolumes (WaterWaveConstantsValidator guards the pair).
#define EXCLUSION_MAX_VOLUMES 4

// Half-extent of the unit box the world->box matrices map into.
#define EXCLUSION_BOX_HALF_EXTENT 0.5

// The unit box as min/max corners, for the slab test in ExclusionRayLength.
#define EXCLUSION_BOX_MIN float3(-EXCLUSION_BOX_HALF_EXTENT, -EXCLUSION_BOX_HALF_EXTENT, -EXCLUSION_BOX_HALF_EXTENT)
#define EXCLUSION_BOX_MAX float3( EXCLUSION_BOX_HALF_EXTENT,  EXCLUSION_BOX_HALF_EXTENT,  EXCLUSION_BOX_HALF_EXTENT)

float    _ExclusionCount; // active volumes (float so it binds like _WaveCount); 0 disables
float4x4 _ExclusionWorldToBox[EXCLUSION_MAX_VOLUMES];
// Per-volume carve-boundary edge look (WaterExclusionVolume fields, published alongside the
// matrices in the SAME slot order): rgb = tint the edges shade toward (black = pure
// occlusion), a = intensity [0..1]; params.x = spread (band reach in unit-box coords).
float4   _ExclusionEdgeColor[EXCLUSION_MAX_VOLUMES];
float4   _ExclusionEdgeParams[EXCLUSION_MAX_VOLUMES];

// True when world-space worldPos lies inside any active exclusion volume. The trip count
// and matrices are uniforms and no texture is sampled inside, so the loop itself keeps
// uniform control flow - only the boolean RESULT is per-fragment (the caller's discard
// demotes the invocation, which keeps feeding neighbour derivatives; the WGSL contract).
// With zero volumes the loop body never runs: the zero-cost off state.
bool InsideExclusion(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxLocal = mul(_ExclusionWorldToBox[i], float4(worldPos, 1.0)).xyz;
        if (all(abs(boxLocal) <= EXCLUSION_BOX_HALF_EXTENT)) return true;
    }
    return false;
}

// Deepest NORMALISED interior depth of a point across the active volumes: 0 = outside
// every box (or exactly on a face), rising toward EXCLUSION_BOX_HALF_EXTENT at a box
// centre. Per box it is the smallest per-axis inset from the faces, in unit-box coords,
// so a thin shell near the boundary stays thin whatever the authored box size. Lets a
// consumer FADE by intrusion depth instead of the binary InsideExclusion - the foam
// particles use it so a dry volume sweeping into sprites (a moving boat hull) dissolves
// them instead of one-frame popping them.
float ExclusionInteriorDepth(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    float depth = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxLocal = mul(_ExclusionWorldToBox[i], float4(worldPos, 1.0)).xyz;
        float3 inset = EXCLUSION_BOX_HALF_EXTENT - abs(boxLocal);
        float boxDepth = min(inset.x, min(inset.y, inset.z)); // < 0 when outside this box
        depth = max(depth, boxDepth);
    }
    return depth;
}

// Total length of the ray segment [origin, origin + dir * maxDist] that lies inside
// exclusion volumes - the DRY span the fog/god-ray integrals subtract. Per box: transform
// the ray into unit-box space and slab-test there (IntersectCube). The direction is
// transformed WITHOUT normalisation, so the ray parameter t stays in WORLD units and the
// clamped interval length is directly a world-metre length.
// Overlapping volumes double-count their shared span: author dry rooms disjoint (N <= 4
// boxes; per-ray interval merging is not worth its cost in a fullscreen pass).
float ExclusionRayLength(float3 origin, float3 dir, float maxDist)
{
    int count = (int)_ExclusionCount;
    float inside = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(origin, 1.0)).xyz;
        float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], dir);
        float2 t = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
        inside += max(min(t.y, maxDist) - max(t.x, 0.0), 0.0);
    }
    return inside;
}

// Pull the ray parameter tAt out of any exclusion volume containing it, toward the ORIGIN
// (landing on that volume's entry). Used to move a span endpoint onto dry-of-volume water -
// e.g. the fog pass's depth-darkening reference, so a dry room at the deep end of a ray
// doesn't darken the water wall seen through its window. One pass over the volumes: a
// chained pull (an entry sitting inside ANOTHER volume) is as unsupported as overlapping
// rooms are elsewhere - author them disjoint.
float ExclusionPullToEntry(float3 origin, float3 dir, float tAt)
{
    int count = (int)_ExclusionCount;
    float t = tAt;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(origin, 1.0)).xyz;
        float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], dir);
        float2 s = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
        if (s.x < t && t < s.y) t = max(s.x, 0.0);
    }
    return t;
}

// Interval-overlap slack for the blocking tests below: a sample sitting exactly ON a box
// face (span endpoints, pushed sun-vis samples) must not read a zero-length graze as a block.
#define EXCLUSION_SHADOW_EPSILON 1e-3
// Minimum sun elevation (dirToSun.y) for the refracted underwater leg: at or below the
// horizon no light enters the water, so the trace falls back to the plain air-direction ray.
#define EXCLUSION_SUN_MIN_ELEVATION 1e-2
// "Whole ray" sentinel for the leg-1 clip when there is no surface crossing to clip at.
#define EXCLUSION_RAY_UNBOUNDED 1e30

// 1 when the sun is visible from p past every exclusion volume, 0 when a volume stands
// between p and the sun. Treats the dry boxes as opaque to the DIRECT sun term only (the
// ambient term is untouched), so a dry room carves a soft shadow column into the
// surrounding water's in-scatter and god rays - the Crest "carved in fog" presence -
// analytically, with no shadow map and no caster mesh. dirToSun points TOWARD the sun
// (the _LightDir convention); waterLevel is the surface plane above p.
//
// REFRACTION-AWARE: sunlight under water travels along the REFRACTED sun direction (steep,
// <= ~49 deg off vertical), exactly as the caustic projection models it. Tracing the raw air
// direction gave a surface-piercing box a near-horizontal shadow curtain at sunset that
// blacked out all deep water down-sun (god rays "stopped 1m deep"). A submerged sample
// therefore traces TWO legs: up along the refracted direction to the surface, then along
// the air sun direction - each tested against every box (the air leg catches the volume's
// above-water part shading the entry point).
float ExclusionSunVisibility(float3 p, float3 dirToSun, float waterLevel)
{
    int count = (int)_ExclusionCount;

    // Refracted underwater leg setup. Above-water samples (or a horizon/below-horizon sun)
    // degrade to a single air-direction ray: tSurf covers the whole ray, no second leg.
    bool refractedLeg = (p.y < waterLevel) && (dirToSun.y > EXCLUSION_SUN_MIN_ELEVATION);
    float3 upLeg = dirToSun; // sample -> sun travel direction of the (first) leg
    float tSurf = EXCLUSION_RAY_UNBOUNDED;
    float3 surfacePoint = p;
    if (refractedLeg)
    {
        // Downward light travel refracted at the flat surface, reversed into the up-leg.
        float3 refractedDown = refract(-dirToSun, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
        upLeg = -refractedDown;
        tSurf = (waterLevel - p.y) / max(upLeg.y, EXCLUSION_SUN_MIN_ELEVATION);
        surfacePoint = p + upLeg * tSurf;
    }

    [loop]
    for (int i = 0; i < count; i++)
    {
        // Leg 1: sample -> surface along the (refracted) travel direction, clipped at tSurf.
        float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(p, 1.0)).xyz;
        float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], upLeg);
        float2 t = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
        if (min(t.y, tSurf) - max(t.x, 0.0) > EXCLUSION_SHADOW_EPSILON) return 0.0;

        // Leg 2: surface point -> sun along the air direction (above-water box parts).
        if (refractedLeg)
        {
            float3 airOrigin = mul(_ExclusionWorldToBox[i], float4(surfacePoint, 1.0)).xyz;
            float3 airDir    = mul((float3x3)_ExclusionWorldToBox[i], dirToSun);
            float2 tAir = IntersectCube(airOrigin, airDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
            if (tAir.y - max(tAir.x, 0.0) > EXCLUSION_SHADOW_EPSILON) return 0.0;
        }
    }
    return 1.0;
}

// Mirror of ExclusionPullToEntry: push tAt out of a containing volume AWAY from the origin
// (landing on the exit, capped at tMax). For a span START inside a volume - a camera in a
// dry room looking up: the darkening reference moves to where the ray re-enters water.
float ExclusionPushToExit(float3 origin, float3 dir, float tAt, float tMax)
{
    int count = (int)_ExclusionCount;
    float t = tAt;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(origin, 1.0)).xyz;
        float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], dir);
        float2 s = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
        if (s.x < t && t < s.y) t = min(s.y, tMax);
    }
    return t;
}

// ---- Shared carved-presence shadow terms (fog pass + exclusion wall) ----------------
// ONE definition on purpose: the underwater fog pass and the wall's above-water fog
// reconstruction must shade the shadow column identically, or the hole reads differently
// from outside vs when diving in.
// In-scatter multiplier at full sun block: the exclusion shadow column's darkest value.
// Applied on TOP of the sun-term attenuation so the carve stays visible when Volume
// Scatter is off (the flat fog colour has no sun term to lose).
#define EXCLUSION_SHADOW_FLOOR 0.65

// ---- Carve-boundary "pane" shading (the edges of the exclusion zone) -----------------
// Crest draws its cutout edge by darkening the COMPOSITED underwater result at the mask
// boundary (portals Meniscus.hlsl, weight *= 0.9 per boundary hit) - never by shading the
// volume geometry, because anything drawn before the underwater pass is buried under its
// additive in-scatter. Same constraint here: the fullscreen fog runs AFTER the transparent
// walls, so the boundary shading lives in the fog (and the wall's own reconstruction path),
// computed analytically from the same box math the carve uses.
// Wrapped N.L for the pane's sun/shade facet split, and how dark a full-shade facet gets.
// (The edge intensity/spread/colour are PER-VOLUME data - see the uniform arrays above.)
#define EXCLUSION_PANE_SUN_WRAP     0.5
#define EXCLUSION_PANE_FACET_DARKEN 0.25

// Edge/corner occlusion AMOUNT [0..1] for a point ON a box face, from its unit-box coords:
// per axis, closeness to the +-0.5 boundary; the face's OWN axis is always at the boundary
// (dropped as the largest), so the two tangential axes drive edges and corners. 'spread' is
// the band reach in unit-box coords. 0 on the face interior, 1 in a full corner.
float ExclusionEdgeOcclusion(float3 boxLocal, float spread)
{
    float3 edge = smoothstep(0.5, 0.5 - spread, abs(boxLocal));
    float largest = max(edge.x, max(edge.y, edge.z));
    float smallest = min(edge.x, min(edge.y, edge.z));
    float middle = edge.x + edge.y + edge.z - largest - smallest;
    return 1.0 - largest * middle;
}

// Per-channel edge tint: 1 on the face interior, shading toward edgeColor.rgb at a full
// corner scaled by the intensity in edgeColor.a. Black = the classic pure occlusion.
float3 ExclusionEdgeTint(float occlusion, float4 edgeColor)
{
    return lerp(float3(1.0, 1.0, 1.0), edgeColor.rgb, saturate(occlusion * edgeColor.a));
}

// Sun-side vs shade-side darkening for a pane with world normal flipped toward the viewer:
// gives the box its 3D read without adding any scatter (multiplicative only).
float ExclusionFacetFactor(float3 normalWS, float3 dirToSun)
{
    float wrap = saturate((dot(normalWS, dirToSun) + EXCLUSION_PANE_SUN_WRAP)
                        / (1.0 + EXCLUSION_PANE_SUN_WRAP));
    return lerp(1.0 - EXCLUSION_PANE_FACET_DARKEN, 1.0, wrap);
}

// Shading of the nearest carve boundary the ray pierces within [0, spanLen]: edge occlusion
// (per-volume colour/intensity/spread) + sun facet of the box face being looked through. A
// camera inside a box shades by its EXIT face (the aquarium pane), an outside view by the
// ENTRY face (the carve silhouette - at the rim the entry point sits on an edge, so the zone
// outline falls out for free). Returns 1 when no box is pierced. Callers fold this into the
// term both fog passes share.
float3 ExclusionBoundaryPaneShade(float3 origin, float3 segDir, float spanLen, float3 dirToSun)
{
    int count = (int)_ExclusionCount;
    float3 shade = float3(1.0, 1.0, 1.0);
    float nearest = EXCLUSION_RAY_UNBOUNDED;
    [loop]
    for (int i = 0; i < count; i++)
    {
        float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(origin, 1.0)).xyz;
        float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], segDir);
        float2 t = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
        if (t.y <= max(t.x, 0.0)) continue;                   // no pierce ahead of the origin
        float tFace = (t.x > 0.0) ? t.x : t.y;                // entry face; inside -> exit face
        if (tFace >= nearest || tFace > spanLen) continue;    // farther than best, or past span
        nearest = tFace;
        float3 faceLocal = boxOrigin + boxDir * tFace;
        // Face normal in world space: the world->box matrix rows are the gradients of the
        // box-local axes, so the row of the axis sitting at the +-0.5 boundary IS the face
        // plane normal; flip it toward the viewer (matches the wall's double-sided flip).
        float3 a = abs(faceLocal);
        float3 row = (a.x >= a.y && a.x >= a.z) ? _ExclusionWorldToBox[i][0].xyz
                   : (a.y >= a.z)               ? _ExclusionWorldToBox[i][1].xyz
                                                : _ExclusionWorldToBox[i][2].xyz;
        float3 normalWS = normalize(row);
        if (dot(normalWS, segDir) > 0.0) normalWS = -normalWS;
        float occlusion = ExclusionEdgeOcclusion(faceLocal, _ExclusionEdgeParams[i].x);
        shade = ExclusionEdgeTint(occlusion, _ExclusionEdgeColor[i])
              * ExclusionFacetFactor(normalWS, dirToSun);
    }
    return shade;
}

// ---- Analytic span sun visibility (the shadow column, band-free) ---------------------
// The previous 3-fixed-sample average quantised the shadow column to {0, 1/3, 2/3, 1} and
// painted polygon-edged contour BANDS on down-sun views from inside a carve. Closed form
// instead: a box's shadow volume along a fixed light direction is a CONVEX prism (the box
// swept down-light), so its intersection with a straight view ray is ONE t-interval.
// Visibility = 1 - shadowedWetLength / wetLength - continuous by construction, so no step
// can ever show. The WHOLE box sweeps along the refracted underwater direction (a
// semi-immersed box's emergent part therefore also shadows along it - a slight horizontal
// shift against the exact two-leg trace, still the same steep column).

// Degeneracy guards for the prism math: an axis the sweep barely moves along, and a
// constraint whose slope in t vanishes. Box-space values, hence tighter than the
// world-space EXCLUSION_SHADOW_EPSILON.
#define EXCLUSION_PRISM_AXIS_EPSILON  1e-5
#define EXCLUSION_PRISM_SLOPE_EPSILON 1e-6

// Finite shadow reach, in box light-axis THICKNESSES (the world metres the sweep needs to
// cross the box, ~1/|boxUp|): full shadow out to NEAR box-thicknesses down-light of the
// box, refilled to nothing by FAR. The unbounded prism painted a near-black dot with a
// small halo at the exact anti-(refracted-)sun direction - the vanishing point of an
// INFINITE shadow column: the one view ray parallel to the sweep axis stayed inside the
// prism for its whole wet span, so sunVisibility hit 0 however long the span. Physically
// the column refills with ambient in-scatter within a few box-thicknesses anyway. The
// near/far pair is averaged into a linear ramp (two extra interval clips per box), so the
// closed form - and its band-free guarantee - stays intact.
#define EXCLUSION_SHADOW_REACH_NEAR 2.0
#define EXCLUSION_SHADOW_REACH_FAR  6.0

// Clip the interval [tMin, tMax] by the half-line of the linear constraint c0 + c1*t <= 0.
void ExclusionConstrainInterval(float c0, float c1, inout float tMin, inout float tMax)
{
    if (abs(c1) <= EXCLUSION_PRISM_SLOPE_EPSILON)
    {
        if (c0 > 0.0) tMax = tMin - 1.0; // constant and violated -> empty interval
        return;
    }
    float tCross = -c0 / c1;
    if (c1 > 0.0) tMax = min(tMax, tCross);
    else          tMin = max(tMin, tCross);
}

// Length of the ray segment [origin, origin + segDir * spanLen] inside box i's shadow
// prism along upLeg, MINUS the ray's own dry chord through the box (that part is carved
// out of the fog and must not count as shadowed water). All world space; t stays metres.
// Per axis j the sweep parameter s of p(t) + s*upLeg must sit in a slab range whose two
// bounds are LINEAR in t; "in shadow" = max(0, max_j lo_j(t)) <= min_j hi_j(t), and every
// comparison is one linear constraint clipping the t-interval by a half-line.
float ExclusionBoxShadowedLength(int i, float3 origin, float3 segDir, float spanLen, float3 upLeg)
{
    float3 boxOrigin = mul(_ExclusionWorldToBox[i], float4(origin, 1.0)).xyz;
    float3 boxDir    = mul((float3x3)_ExclusionWorldToBox[i], segDir);
    float3 boxUp     = mul((float3x3)_ExclusionWorldToBox[i], upLeg);

    float tMin = 0.0;
    float tMax = spanLen;
    float loIntercept[3]; // lo_j(t) = loIntercept + slope * t (feasible s lower bound)
    float hiIntercept[3]; // hi_j(t) = hiIntercept + slope * t (feasible s upper bound)
    float slope[3];       // shared: both bounds move with -boxDir_j / boxUp_j
    bool  axisSweeps[3];
    [unroll]
    for (int j = 0; j < 3; j++)
    {
        if (abs(boxUp[j]) <= EXCLUSION_PRISM_AXIS_EPSILON)
        {
            // The sweep cannot move this axis: the ray point itself must be in the slab.
            axisSweeps[j] = false;
            ExclusionConstrainInterval(boxOrigin[j] - EXCLUSION_BOX_HALF_EXTENT, boxDir[j],
                                       tMin, tMax);
            ExclusionConstrainInterval(-boxOrigin[j] - EXCLUSION_BOX_HALF_EXTENT, -boxDir[j],
                                       tMin, tMax);
            continue;
        }
        axisSweeps[j] = true;
        float invUp = 1.0 / boxUp[j];
        float nearFace = (boxUp[j] > 0.0) ? -EXCLUSION_BOX_HALF_EXTENT : EXCLUSION_BOX_HALF_EXTENT;
        loIntercept[j] = (nearFace - boxOrigin[j]) * invUp;
        hiIntercept[j] = (-nearFace - boxOrigin[j]) * invUp;
        slope[j] = -boxDir[j] * invUp;
    }
    [unroll]
    for (int j2 = 0; j2 < 3; j2++)
    {
        if (!axisSweeps[j2]) continue;
        // s >= 0 must be reachable: hi_j(t) >= 0.
        ExclusionConstrainInterval(-hiIntercept[j2], -slope[j2], tMin, tMax);
        // Cross-axis: lo_j(t) <= hi_k(t) (same-axis is true by construction).
        [unroll]
        for (int k = 0; k < 3; k++)
        {
            if (k == j2 || !axisSweeps[k]) continue;
            ExclusionConstrainInterval(loIntercept[j2] - hiIntercept[k], slope[j2] - slope[k],
                                       tMin, tMax);
        }
    }

    if (tMax - tMin <= 0.0) return 0.0;

    // Finite reach: the sweep distance s a shadowed point needs to reach the box is
    // max_j lo_j(t) (world metres, upLeg is unit length), so "within reach R" is one more
    // linear constraint per sweeping axis: lo_j(t) - R <= 0. Clip a COPY of the prism
    // interval at the NEAR and FAR reach and average the two lengths - a linear falloff
    // of the shadow between them, still closed-form. sThickness converts reach from
    // box-thicknesses to metres (the sweep crosses the unit box in ~1/|boxUp| metres).
    // The ray's own dry chord through the box lies inside the prism (s -> 0) but is
    // carved air, not shadowed water: its overlap leaves each clipped interval.
    float sThickness = 1.0 / max(length(boxUp), EXCLUSION_PRISM_AXIS_EPSILON);
    float2 chord = IntersectCube(boxOrigin, boxDir, EXCLUSION_BOX_MIN, EXCLUSION_BOX_MAX);
    float shadowed = 0.0;
    [unroll]
    for (int c = 0; c < 2; c++)
    {
        float reach = ((c == 0) ? EXCLUSION_SHADOW_REACH_NEAR : EXCLUSION_SHADOW_REACH_FAR)
                    * sThickness;
        float tMinC = tMin;
        float tMaxC = tMax;
        [unroll]
        for (int j3 = 0; j3 < 3; j3++)
        {
            if (!axisSweeps[j3]) continue;
            ExclusionConstrainInterval(loIntercept[j3] - reach, slope[j3], tMinC, tMaxC);
        }
        float len = max(tMaxC - tMinC, 0.0);
        float chordOverlap = max(min(chord.y, tMaxC) - max(chord.x, tMinC), 0.0);
        shadowed += 0.5 * max(len - chordOverlap, 0.0);
    }
    return shadowed;
}

// Sun visibility of a wet span: the continuous fraction of its WET length (wetLen, the
// post-carve path) left unshadowed by the volumes. spanLen is the pre-carve span the
// interval is clipped to. Callers gate on _ExclusionCount / wetLen (zero-cost off state).
// Overlapping volumes double-count their shared shadow, as everywhere else: author disjoint.
float ExclusionSpanSunVisibility(float3 wetStart, float3 segDir, float spanLen, float wetLen,
                                 float3 dirToSun)
{
    // Underwater light travels along the refracted sun direction (leg 1 of
    // ExclusionSunVisibility); at or below the horizon fall back to the air ray.
    float3 upLeg = dirToSun;
    if (dirToSun.y > EXCLUSION_SUN_MIN_ELEVATION)
        upLeg = -refract(-dirToSun, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

    int count = (int)_ExclusionCount;
    float shadowed = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
        shadowed += ExclusionBoxShadowedLength(i, wetStart, segDir, spanLen, upLeg);
    return 1.0 - saturate(shadowed / max(wetLen, EXCLUSION_SHADOW_EPSILON));
}

#endif // WEBGL_WATER_EXCLUSION_INCLUDED
