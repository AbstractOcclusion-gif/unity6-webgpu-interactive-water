// WebGpuWater - water exclusion volumes (dry interiors): analytic primitives.
// Declares the global exclusion uniforms plus the ONE point test every water consumer
// shares (reuse-never-rewrite: consumers include this file, nobody hand-copies the loop).
// Kept OUT of WaterShared.hlsl on purpose: that header's contract is pure math with no
// global declarations, and these ARE globals.
//
// Published by WaterUniformPublisher.PublishSharedGlobals (global, not per body: a dry
// room is dry in whichever body intersects it). _ExclusionWorldToLocal maps world space
// into each volume's UNIT LOCAL space, so one matrix carries centre + rotation + size and
// the shape test reduces to the origin-centred primitive kernels in WaterPrimitiveShape.hlsl
// (box: abs(local) <= 0.5 per axis; sphere: |local| <= 0.5, an ELLIPSOID in world space
// whenever the matrix scales non-uniformly).
//
#ifndef WEBGL_WATER_EXCLUSION_INCLUDED
#define WEBGL_WATER_EXCLUSION_INCLUDED

#include "WaterPrimitiveShape.hlsl" // the shared box/sphere kernels (+ WaterShared: IntersectCube)
// NOT included here: WaterExclusionMesh.hlsl. This header is pulled in by the foam COMPUTE too, and
// the mesh tier declares depth textures a compute kernel would carry for nothing. The three
// screen-space consumers include it (or its URP-core companion WaterExclusionMeshSpan.hlsl)
// themselves; everything in THIS file needs only the mesh FLAG, which rides in the shape uniform.

// C# pair: WaterExclusionVolume.MaxVolumes (WaterWaveConstantsValidator guards the pair).
#define EXCLUSION_MAX_VOLUMES 4

// Half-extent of the unit local space the world->local matrices map into: the box's half-edge
// and the inscribed sphere's radius (see WaterPrimitiveShape.hlsl).
// C# pair: WaterExclusionVolume.LocalHalfExtent (WaterWaveConstantsValidator guards the pair).
#define EXCLUSION_LOCAL_HALF_EXTENT 0.5

// Selector value a MESH volume carries. The analytic uniform arrays never see it - a mesh volume
// sends its PROXY there and raises the mesh flag instead - but the WALL is drawn per volume and
// does carry the true shape, because it has to shade a real mesh facet rather than an analytic
// surface. C# pair: WaterExclusionVolume.MeshShapeId.
#define EXCLUSION_SHAPE_MESH 2.0

float    _ExclusionCount; // active volumes (float so it binds like _WaveCount); 0 disables
float4x4 _ExclusionWorldToLocal[EXCLUSION_MAX_VOLUMES];
// Per-volume SHAPE in the SAME slot order: x = PRIMITIVE_SHAPE_* selector (box / sphere),
// y = 1 for a MESH volume, z = 1 when the volume does NOT block the sun, w reserved for a
// future shape parameter (a capsule's radius, a wedge's angle). Every lane is polarised the
// same way: a volume that never sets it reads 0 = box, not a mesh, sun-blocking - exactly what
// every pre-flag scene authored.
//
// A MESH volume sends its analytic PROXY in x and raises the flag in y, which splits this
// header cleanly in two. The kernels that answer along the CAMERA RAY - InsideExclusion,
// ExclusionRayLength, the two endpoint pushes, the boundary pane - SKIP mesh volumes, because
// the depth prepass answers those exactly and a proxy would carve a box where the author put a
// silhouette; the screen-space consumers add the mesh path themselves (WaterExclusionMesh.hlsl).
// Every OTHER kernel - interior depth, the particle trio, both sun-visibility traces - keeps
// mesh volumes on their proxy, because those trace directions the prepass never rendered.
float4   _ExclusionShape[EXCLUSION_MAX_VOLUMES];

// True when slot i carves from a mesh rather than from its analytic shape (see above).
bool ExclusionIsMesh(int i)
{
    return _ExclusionShape[i].y >= 0.5;
}

// True when slot i BLOCKS the sun, so its shadow reaches the god-ray shafts and the fog's
// in-scatter (WaterExclusionVolume.castsSunShadow; stored inverted - see the header). ONLY the
// two sun-visibility traces read this: the CARVE is unconditional, or the water would come back
// inside the volume.
bool ExclusionCastsSunShadow(int i)
{
    return _ExclusionShape[i].z < 0.5;
}
// Per-volume carve-boundary edge look + particle handling (WaterExclusionVolume fields,
// published alongside the matrices in the SAME slot order): color rgb = tint the edges
// shade toward (black = pure occlusion), color a = intensity [0..1]; params.x = edge
// spread (band reach in unit-local coords), params.y = affect-particles flag (0 lets
// particles through), params.z = particle fade band (unit-local interior depth of the
// dissolve shell; 0 = hard clip), params.w = particle dissolve-speed multiplier.
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
        if (ExclusionIsMesh(i)) continue; // camera-ray query: the prepass answers it exactly
        float3 local = mul(_ExclusionWorldToLocal[i], float4(worldPos, 1.0)).xyz;
        if (PrimitiveContains(_ExclusionShape[i].x, local, EXCLUSION_LOCAL_HALF_EXTENT))
            return true;
    }
    return false;
}

// ---- Particle culling (foam/spray sprites, splash crown + droplets) ------------------
// The particle consumers respect the per-volume handling in _ExclusionEdgeParams: a
// volume with params.y = 0 does not touch particles at all. Callers gate every use on
// _ExclusionCount > 0.5 (the zero-cost off state, as everywhere else in this header).

// Floor under the fade band so a 0 (hard clip) never divides by zero: sharper than any
// visible band, so "0" still reads as a razor edge on the surface.
#define EXCLUSION_PARTICLE_BAND_MIN 1e-4

// True when worldPos is inside a PARTICLE-AFFECTING volume - the spawn-rejection test
// (InsideExclusion would also veto spawns under volumes that opted their particles out).
bool InsideParticleExclusion(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    [loop]
    for (int i = 0; i < count; i++)
    {
        if (_ExclusionEdgeParams[i].y < 0.5) continue;
        float3 local = mul(_ExclusionWorldToLocal[i], float4(worldPos, 1.0)).xyz;
        if (PrimitiveContains(_ExclusionShape[i].x, local, EXCLUSION_LOCAL_HALF_EXTENT))
            return true;
    }
    return false;
}

// Alpha multiplier for a particle FRAGMENT at worldPos: 1 outside every particle-affecting
// volume (and exactly ON a surface), dissolving to 0 across each volume's fade band just
// inside it. min() across volumes, so overlapping shapes take the strongest cut.
// This is the render-side guarantee the sim's age-boost dissolve cannot give: it clips
// the parts of a big billboard (the Shuriken crown) that PROTRUDE into a dry interior,
// and it hides sim particles the moment they are swept over, however long their life.
float ExclusionParticleAttenuation(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    float atten = 1.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        if (_ExclusionEdgeParams[i].y < 0.5) continue;
        float3 local = mul(_ExclusionWorldToLocal[i], float4(worldPos, 1.0)).xyz;
        float depth = PrimitiveInteriorDepth(_ExclusionShape[i].x, local,
                                             EXCLUSION_LOCAL_HALF_EXTENT); // < 0 outside
        float band = max(_ExclusionEdgeParams[i].z, EXCLUSION_PARTICLE_BAND_MIN);
        atten = min(atten, saturate(1.0 - depth / band));
    }
    return atten;
}

// Deepest interior depth of worldPos across the particle-affecting volumes plus that
// volume's dissolve-speed multiplier: x = depth (0 = outside them all, unit-local coords,
// the PrimitiveInteriorDepth unit-local convention), y = params.w of the deepest volume (1 when
// outside). The compute Update kernel scales its age-boost dissolve by y.
float2 ExclusionParticleInteriorDepth(float3 worldPos)
{
    int count = (int)_ExclusionCount;
    float2 result = float2(0.0, 1.0);
    [loop]
    for (int i = 0; i < count; i++)
    {
        if (_ExclusionEdgeParams[i].y < 0.5) continue;
        float3 local = mul(_ExclusionWorldToLocal[i], float4(worldPos, 1.0)).xyz;
        float depth = PrimitiveInteriorDepth(_ExclusionShape[i].x, local,
                                             EXCLUSION_LOCAL_HALF_EXTENT);
        if (depth > result.x) result = float2(depth, _ExclusionEdgeParams[i].w);
    }
    return result;
}

// Total length of the ray segment [origin, origin + dir * maxDist] that lies inside
// exclusion volumes - the DRY span the fog/god-ray integrals subtract. Per volume:
// transform the ray into unit-local space and intersect the primitive there. The direction
// is transformed WITHOUT normalisation, so the ray parameter t stays in WORLD units and the
// clamped interval length is directly a world-metre length.
// Overlapping volumes double-count their shared span: author dry rooms disjoint (N <= 4
// volumes; per-ray interval merging is not worth its cost in a fullscreen pass).
float ExclusionRayLength(float3 origin, float3 dir, float maxDist)
{
    int count = (int)_ExclusionCount;
    float inside = 0.0;
    [loop]
    for (int i = 0; i < count; i++)
    {
        if (ExclusionIsMesh(i)) continue; // camera-ray query: the prepass answers it exactly
        float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
        float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], dir);
        float2 t = PrimitiveIntersect(_ExclusionShape[i].x, localOrigin, localDir,
                                      EXCLUSION_LOCAL_HALF_EXTENT);
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
        if (ExclusionIsMesh(i)) continue; // camera-ray query: the prepass answers it exactly
        float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
        float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], dir);
        float2 s = PrimitiveIntersect(_ExclusionShape[i].x, localOrigin, localDir,
                                      EXCLUSION_LOCAL_HALF_EXTENT);
        if (s.x < t && t < s.y) t = max(s.x, 0.0);
    }
    return t;
}

// Interval-overlap slack for the blocking tests below: a sample sitting exactly ON a volume
// surface (span endpoints, pushed sun-vis samples) must not read a zero-length graze as a block.
#define EXCLUSION_SHADOW_EPSILON 1e-3
// Minimum sun elevation (dirToSun.y) for the refracted underwater leg: at or below the
// horizon no light enters the water, so the trace falls back to the plain air-direction ray.
#define EXCLUSION_SUN_MIN_ELEVATION 1e-2
// "Whole ray" sentinel for the leg-1 clip when there is no surface crossing to clip at.
#define EXCLUSION_RAY_UNBOUNDED 1e30

// 1 when the sun is visible from p past every exclusion volume, 0 when a volume stands
// between p and the sun. Treats the dry volumes as opaque to the DIRECT sun term only (the
// ambient term is untouched), so a dry room carves a soft shadow column into the
// surrounding water's in-scatter and god rays - the Crest "carved in fog" presence -
// analytically, with no shadow map and no caster mesh. dirToSun points TOWARD the sun
// (the _LightDir convention); waterLevel is the surface plane above p.
//
// REFRACTION-AWARE: sunlight under water travels along the REFRACTED sun direction (steep,
// <= ~49 deg off vertical), exactly as the caustic projection models it. Tracing the raw air
// direction gave a surface-piercing volume a near-horizontal shadow curtain at sunset that
// blacked out all deep water down-sun (god rays "stopped 1m deep"). A submerged sample
// therefore traces TWO legs: up along the refracted direction to the surface, then along
// the air sun direction - each tested against every volume (the air leg catches the volume's
// above-water part shading the entry point).
float ExclusionSunVisibility(float3 p, float3 dirToSun, float waterLevel)
{
    int count = (int)_ExclusionCount;
    // No volumes: nothing can occlude, and the loop below would fall straight through to return 1.0.
    // Worth an explicit early-out because this runs PER MARCHED SAMPLE in both god-ray shaders, so a
    // scene with no carve was paying the refract() setup up to 64 times per pixel for that same 1.0.
    if (count < 1) return 1.0;

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
        if (!ExclusionCastsSunShadow(i)) continue; // authored light-transmitting (see the header)
        float shape = _ExclusionShape[i].x;

        // Leg 1: sample -> surface along the (refracted) travel direction, clipped at tSurf.
        float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(p, 1.0)).xyz;
        float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], upLeg);
        float2 t = PrimitiveIntersect(shape, localOrigin, localDir, EXCLUSION_LOCAL_HALF_EXTENT);
        if (min(t.y, tSurf) - max(t.x, 0.0) > EXCLUSION_SHADOW_EPSILON) return 0.0;

        // Leg 2: surface point -> sun along the air direction (above-water volume parts).
        if (refractedLeg)
        {
            float3 airOrigin = mul(_ExclusionWorldToLocal[i], float4(surfacePoint, 1.0)).xyz;
            float3 airDir    = mul((float3x3)_ExclusionWorldToLocal[i], dirToSun);
            float2 tAir = PrimitiveIntersect(shape, airOrigin, airDir,
                                             EXCLUSION_LOCAL_HALF_EXTENT);
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
        if (ExclusionIsMesh(i)) continue; // camera-ray query: the prepass answers it exactly
        float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
        float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], dir);
        float2 s = PrimitiveIntersect(_ExclusionShape[i].x, localOrigin, localDir,
                                      EXCLUSION_LOCAL_HALF_EXTENT);
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
// computed analytically from the same primitive math the carve uses.
// Wrapped N.L for the pane's sun/shade facet split, and how dark a full-shade facet gets.
// (The edge intensity/spread/colour are PER-VOLUME data - see the uniform arrays above.)
#define EXCLUSION_PANE_SUN_WRAP     0.5
#define EXCLUSION_PANE_FACET_DARKEN 0.25

// Rim band width as a multiple of the authored edge spread. A BOX measures its band across
// the two tangential unit-local axes; a SPHERE has no edges at all, so its band is measured
// across |dot(normal, view)| instead - 0 exactly on the silhouette, 1 head-on. Those two
// measures are not in the same units, and this factor is what makes ONE authored Edge Spread
// read as a comparable visual width on both, so switching Box -> Sphere never forces a retune.
#define EXCLUSION_RIM_SPREAD_SCALE 2.0

// Boundary occlusion AMOUNT [0..1] at a point ON a volume's surface, from its unit-local
// coords. BOX: per axis, closeness to the +-halfExtent boundary; the surface's OWN axis is
// always at the boundary (dropped as the largest), so the two tangential axes drive edges and
// corners - 0 on a face interior, 1 in a full corner. SPHERE: the silhouette RIM, because a
// sphere's only visible outline is where its surface turns away from the viewer - 0 head-on,
// 1 on the silhouette. Both are 0 on the open surface and 1 on the carve's visible OUTLINE,
// which is exactly what the per-volume tint/intensity knobs are authored against.
// 'spread' is the band reach in unit-local coords. viewDirWS may point either way along the
// view line: only its alignment with the normal matters.
float ExclusionBoundaryOcclusion(float shape, float3 local, float3 normalWS, float3 viewDirWS,
                                 float spread)
{
    if (PrimitiveIsSphere(shape))
    {
        float facing = abs(dot(normalWS, viewDirWS)); // 0 on the silhouette, 1 head-on
        return smoothstep(spread * EXCLUSION_RIM_SPREAD_SCALE, 0.0, facing);
    }
    float3 edge = smoothstep(EXCLUSION_LOCAL_HALF_EXTENT,
                             EXCLUSION_LOCAL_HALF_EXTENT - spread, abs(local));
    float largest = max(edge.x, max(edge.y, edge.z));
    float smallest = min(edge.x, min(edge.y, edge.z));
    float middle = edge.x + edge.y + edge.z - largest - smallest;
    return 1.0 - largest * middle;
}

// Per-channel edge tint: 1 on the open surface, shading toward edgeColor.rgb at a full
// corner (or on the sphere's rim) scaled by the intensity in edgeColor.a. Black = the
// classic pure occlusion.
float3 ExclusionEdgeTint(float occlusion, float4 edgeColor)
{
    return lerp(float3(1.0, 1.0, 1.0), edgeColor.rgb, saturate(occlusion * edgeColor.a));
}

// Sun-side vs shade-side darkening for a pane with world normal flipped toward the viewer:
// gives the volume its 3D read without adding any scatter (multiplicative only).
float ExclusionFacetFactor(float3 normalWS, float3 dirToSun)
{
    float wrap = saturate((dot(normalWS, dirToSun) + EXCLUSION_PANE_SUN_WRAP)
                        / (1.0 + EXCLUSION_PANE_SUN_WRAP));
    return lerp(1.0 - EXCLUSION_PANE_FACET_DARKEN, 1.0, wrap);
}

// World-space outward normal at a LOCAL-space surface point of volume i. Normals transform by
// the INVERSE-TRANSPOSE of the local->world matrix, which is exactly the transpose of the
// world->local matrix we hold - and mul(vector, matrix) IS that transposed product. (For a box
// this reproduces the older "the row of the axis sitting at the boundary is the face normal"
// derivation identically, because the local normal is then a signed unit axis.)
float3 ExclusionSurfaceNormalWorld(int i, float shape, float3 surfaceLocal)
{
    float3 localNormal = PrimitiveSurfaceNormal(shape, surfaceLocal);
    return normalize(mul(localNormal, (float3x3)_ExclusionWorldToLocal[i]));
}

// Shading of the nearest carve boundary the ray pierces within [0, spanLen]: boundary occlusion
// (per-volume colour/intensity/spread) + sun facet of the surface being looked through. A
// camera inside a volume shades by its EXIT surface (the aquarium pane), an outside view by the
// ENTRY surface (the carve silhouette - at the rim the entry point sits on an edge, so the zone
// outline falls out for free). Returns 1 when no volume is pierced. Callers fold this into the
// term both fog passes share.
float3 ExclusionBoundaryPaneShade(float3 origin, float3 segDir, float spanLen, float3 dirToSun)
{
    int count = (int)_ExclusionCount;
    float3 shade = float3(1.0, 1.0, 1.0);
    float nearest = EXCLUSION_RAY_UNBOUNDED;
    [loop]
    for (int i = 0; i < count; i++)
    {
        // Camera-ray query: a mesh volume's own boundary is drawn by its wall, which shades the
        // fragment it lands on with this same occlusion + facet pair.
        if (ExclusionIsMesh(i)) continue;
        float shape = _ExclusionShape[i].x;
        float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
        float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], segDir);
        float2 t = PrimitiveIntersect(shape, localOrigin, localDir, EXCLUSION_LOCAL_HALF_EXTENT);
        if (t.y <= max(t.x, 0.0)) continue;                   // no pierce ahead of the origin
        float tFace = (t.x > 0.0) ? t.x : t.y;                // entry surface; inside -> exit
        if (tFace >= nearest || tFace > spanLen) continue;    // farther than best, or past span
        nearest = tFace;
        float3 faceLocal = localOrigin + localDir * tFace;
        float3 normalWS = ExclusionSurfaceNormalWorld(i, shape, faceLocal);
        // Flip toward the viewer (matches the wall's double-sided flip).
        if (dot(normalWS, segDir) > 0.0) normalWS = -normalWS;
        float occlusion = ExclusionBoundaryOcclusion(shape, faceLocal, normalWS, segDir,
                                                     _ExclusionEdgeParams[i].x);
        shade = ExclusionEdgeTint(occlusion, _ExclusionEdgeColor[i])
              * ExclusionFacetFactor(normalWS, dirToSun);
    }
    return shade;
}

// ---- Analytic span sun visibility (the shadow column, band-free) ---------------------
// The previous 3-fixed-sample average quantised the shadow column to {0, 1/3, 2/3, 1} and
// painted polygon-edged contour BANDS on down-sun views from inside a carve. Closed form
// instead: a CONVEX volume's shadow along a fixed light direction is that volume SWEPT
// down-light, which stays convex, so its intersection with a straight view ray is ONE
// t-interval. Visibility = 1 - shadowedWetLength / wetLength - continuous by construction, so
// no step can ever show. Each shape gets the closed form of its own swept solid: a box sweeps
// into a PRISM, a sphere into a CAPSULE. The WHOLE volume sweeps along the refracted underwater
// direction (a semi-immersed volume's emergent part therefore also shadows along it - a slight
// horizontal shift against the exact two-leg trace, still the same steep column).

// Degeneracy guards for the sweep math: an axis the sweep barely moves along, and a
// constraint whose slope in t vanishes. Local-space values, hence tighter than the
// world-space EXCLUSION_SHADOW_EPSILON.
#define EXCLUSION_PRISM_AXIS_EPSILON  1e-5
#define EXCLUSION_PRISM_SLOPE_EPSILON 1e-6

// Finite shadow reach, in volume light-axis THICKNESSES (the world metres the sweep needs to
// cross the volume, ~1/|localUp|): full shadow out to NEAR thicknesses down-light of the
// volume, refilled to nothing by FAR. The unbounded sweep painted a near-black dot with a
// small halo at the exact anti-(refracted-)sun direction - the vanishing point of an
// INFINITE shadow column: the one view ray parallel to the sweep axis stayed inside the
// swept solid for its whole wet span, so sunVisibility hit 0 however long the span. Physically
// the column refills with ambient in-scatter within a few thicknesses anyway. The near/far
// pair is averaged into a linear ramp (two extra interval clips per volume), so the closed
// form - and its band-free guarantee - stays intact.
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
    // The guard above makes c1 non-zero on this path, but some shader compilers do not carry that
    // proof through control flow. Clamp without changing any live value or its sign.
    float safeSlope = c1 > 0.0 ? max(c1, EXCLUSION_PRISM_SLOPE_EPSILON)
                               : min(c1, -EXCLUSION_PRISM_SLOPE_EPSILON);
    float tCross = -c0 / safeSlope;
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
    float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
    float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], segDir);
    float3 localUp     = mul((float3x3)_ExclusionWorldToLocal[i], upLeg);

    float tMin = 0.0;
    float tMax = spanLen;
    float loIntercept[3]; // lo_j(t) = loIntercept + slope * t (feasible s lower bound)
    float hiIntercept[3]; // hi_j(t) = hiIntercept + slope * t (feasible s upper bound)
    float slope[3];       // shared: both bounds move with -localDir_j / localUp_j
    bool  axisSweeps[3];
    [unroll]
    for (int j = 0; j < 3; j++)
    {
        if (abs(localUp[j]) <= EXCLUSION_PRISM_AXIS_EPSILON)
        {
            // The sweep cannot move this axis: the ray point itself must be in the slab.
            axisSweeps[j] = false;
            ExclusionConstrainInterval(localOrigin[j] - EXCLUSION_LOCAL_HALF_EXTENT, localDir[j],
                                       tMin, tMax);
            ExclusionConstrainInterval(-localOrigin[j] - EXCLUSION_LOCAL_HALF_EXTENT, -localDir[j],
                                       tMin, tMax);
            continue;
        }
        axisSweeps[j] = true;
        float invUp = 1.0 / localUp[j];
        float nearFace = (localUp[j] > 0.0) ? -EXCLUSION_LOCAL_HALF_EXTENT
                                            :  EXCLUSION_LOCAL_HALF_EXTENT;
        loIntercept[j] = (nearFace - localOrigin[j]) * invUp;
        hiIntercept[j] = (-nearFace - localOrigin[j]) * invUp;
        slope[j] = -localDir[j] * invUp;
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
    // thicknesses to metres (the sweep crosses the unit shape in ~1/|localUp| metres).
    // The ray's own dry chord through the box lies inside the prism (s -> 0) but is
    // carved air, not shadowed water: its overlap leaves each clipped interval.
    float sThickness = 1.0 / max(length(localUp), EXCLUSION_PRISM_AXIS_EPSILON);
    float2 chord = PrimitiveIntersect(PRIMITIVE_SHAPE_BOX, localOrigin, localDir,
                                      EXCLUSION_LOCAL_HALF_EXTENT);
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

// Interval of the LOCAL-space ray [origin, +dir*t] against the CAPSULE swept by the local
// sphere of radius `radius` from the local origin along `axis` (a zero-length axis degrades to
// the sphere itself). The capsule is CONVEX, so the union of its three pieces - the two end
// spheres and the barrel (the axis cylinder clipped to the slab between the cap centres) - is
// ONE contiguous interval: the min of the pieces' entries and the max of their exits. Working
// in LOCAL space is what keeps this exact for an ELLIPSOID volume too: there the swept solid is
// a sheared capsule, but its local pre-image is a true one.
float2 ExclusionCapsuleInterval(float3 origin, float3 dir, float3 axis, float radius)
{
    float tEnter =  EXCLUSION_RAY_UNBOUNDED;
    float tExit  = -EXCLUSION_RAY_UNBOUNDED;

    // Cap A: the volume itself. Cap B: the sphere at the far end of the sweep.
    float2 capA = IntersectLocalSphere(origin, dir, radius);
    if (capA.y > capA.x) { tEnter = min(tEnter, capA.x); tExit = max(tExit, capA.y); }
    float2 capB = IntersectLocalSphere(origin - axis, dir, radius);
    if (capB.y > capB.x) { tEnter = min(tEnter, capB.x); tExit = max(tExit, capB.y); }

    float axisLengthSquared = dot(axis, axis);
    if (axisLengthSquared > EXCLUSION_PRISM_AXIS_EPSILON)
    {
        // Barrel: the same quadratic as a sphere's, run on the components PERPENDICULAR to the
        // axis (an infinite cylinder), then clipped to the slab between the two cap centres.
        float invAxisLengthSquared = 1.0 / axisLengthSquared;
        float originAlong = dot(origin, axis) * invAxisLengthSquared;
        float dirAlong    = dot(dir,    axis) * invAxisLengthSquared;
        float3 perpOrigin = origin - axis * originAlong;
        float3 perpDir    = dir    - axis * dirAlong;
        float2 barrel = IntersectLocalSphere(perpOrigin, perpDir, radius);
        if (barrel.y > barrel.x)
        {
            float slabMin = barrel.x;
            float slabMax = barrel.y;
            ExclusionConstrainInterval(-originAlong, -dirAlong, slabMin, slabMax);     // >= cap A
            ExclusionConstrainInterval(originAlong - 1.0, dirAlong, slabMin, slabMax); // <= cap B
            if (slabMax > slabMin) { tEnter = min(tEnter, slabMin); tExit = max(tExit, slabMax); }
        }
    }

    if (tExit <= tEnter) return PRIMITIVE_MISS_INTERVAL;
    return float2(tEnter, tExit);
}

// Sphere twin of ExclusionBoxShadowedLength: a sphere swept down-light is a CAPSULE, so the
// same recipe applies - clip the ray interval at the NEAR and FAR reach, average the two for
// the linear falloff, and subtract the ray's own dry chord through the sphere (carved air is
// not shadowed water). Closed form throughout, so it inherits the box path's band-free
// guarantee. The sweep runs from the volume AWAY from the sun: a point is shadowed when moving
// toward the sun from it enters the volume.
float ExclusionSphereShadowedLength(int i, float3 origin, float3 segDir, float spanLen,
                                    float3 upLeg)
{
    float3 localOrigin = mul(_ExclusionWorldToLocal[i], float4(origin, 1.0)).xyz;
    float3 localDir    = mul((float3x3)_ExclusionWorldToLocal[i], segDir);
    float3 localUp     = mul((float3x3)_ExclusionWorldToLocal[i], upLeg);

    // World metres the sweep needs to cross the volume - the same thickness conversion the
    // box path makes, because reach is authored in volume thicknesses for both shapes.
    float sThickness = 1.0 / max(length(localUp), EXCLUSION_PRISM_AXIS_EPSILON);
    float2 chord = IntersectLocalSphere(localOrigin, localDir, EXCLUSION_LOCAL_HALF_EXTENT);

    float shadowed = 0.0;
    [unroll]
    for (int c = 0; c < 2; c++)
    {
        float reach = ((c == 0) ? EXCLUSION_SHADOW_REACH_NEAR : EXCLUSION_SHADOW_REACH_FAR)
                    * sThickness;
        float2 span = ExclusionCapsuleInterval(localOrigin, localDir, -localUp * reach,
                                               EXCLUSION_LOCAL_HALF_EXTENT);
        float tMinC = max(span.x, 0.0);
        float tMaxC = min(span.y, spanLen);
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
    {
        if (!ExclusionCastsSunShadow(i)) continue; // authored light-transmitting (see the header)
        // The shape is a uniform, so this branch stays uniform across the wave: each volume
        // pays for ITS swept solid only, never for both.
        if (PrimitiveIsSphere(_ExclusionShape[i].x))
            shadowed += ExclusionSphereShadowedLength(i, wetStart, segDir, spanLen, upLeg);
        else
            shadowed += ExclusionBoxShadowedLength(i, wetStart, segDir, spanLen, upLeg);
    }
    return 1.0 - saturate(shadowed / max(wetLen, EXCLUSION_SHADOW_EPSILON));
}

#endif // WEBGL_WATER_EXCLUSION_INCLUDED
