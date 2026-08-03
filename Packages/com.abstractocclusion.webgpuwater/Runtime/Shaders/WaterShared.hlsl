// WebGpuWater - shared constants & pure helpers (Unity 6 / URP port)
// Backend-agnostic: ONLY #defines, static consts and pure math here (no sampler or
// global declarations), so both the legacy-CG shaders and the URP HLSL shaders can
// include it without clashing. Faithful to Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_SHARED_INCLUDED
#define WEBGL_WATER_SHARED_INCLUDED

#define IOR_AIR   1.0
#define IOR_WATER 1.333

// Schlick (1994) Fresnel for the air/water interface: F0 derived from the IOR pair (~0.02),
// grazing exponent 5. Lives here, next to the IORs it is derived from, because TWO sides need
// the same number: the surface's own reflection ladder, and the particle sprites that are seen
// THROUGH the surface (a submerged bubble's apparent image is only the transmitted share).
#define FRESNEL_F0_WATER      (((IOR_WATER - IOR_AIR) * (IOR_WATER - IOR_AIR)) \
                             / ((IOR_WATER + IOR_AIR) * (IOR_WATER + IOR_AIR)))
#define FRESNEL_SCHLICK_POWER 5.0

// Squared-length floor under which a direction has cancelled to ~zero and normalize()
// would return NaN (0/0). Shared by every degenerate-direction guard (specular taps,
// foam tangent frames, particle axes); well under any visually meaningful vector.
#define DEGENERATE_DIR_EPSILON 1e-8

// C1 replacements for min()/max(). A hard min/max is continuous in VALUE but not in SLOPE, so
// wherever two surfaces are joined by one the seam prints as a crease - a dihedral "angle" the eye
// reads instantly even when the two sides differ by millimetres. These blend the switch-over across
// a band of `blend` (same units as a and b), which is exactly the width the crease is spread over.
//
// Quilez's polynomial smooth-min: the blend term blend*h*(1-h) is 0 at both ends - so far from the
// crossing the result is EXACTLY min/max, with no bias - and peaks at blend/4 where a == b. A caller
// that needs a floor on the result must keep blend/4 inside the headroom it has there.
//
// blend <= 0 returns the hard min/max, so the smoothing is always switchable off.
float SmoothMin(float a, float b, float blend)
{
    if (blend <= 0.0) return min(a, b);
    float h = saturate(0.5 + 0.5 * (b - a) / blend);
    return lerp(b, a, h) - blend * h * (1.0 - h);
}

float SmoothMax(float a, float b, float blend)
{
    if (blend <= 0.0) return max(a, b);
    float h = saturate(0.5 + 0.5 * (a - b) / blend);
    return lerp(b, a, h) + blend * h * (1.0 - h);
}

#define POOL_HEIGHT     1.0          // pool floor sits at y = -POOL_HEIGHT
#define POOL_RIM_HEIGHT (2.0 / 12.0) // top of the pool walls, in pool units

// Pool interior as an axis-aligned box in pool space, used by every analytic ray march
// (surface refraction, caustics, underwater fog). Floor at -POOL_HEIGHT; the top gives
// headroom above the surface so upward rays don't clip the waterline.
#define POOL_BOX_TOP 2.0
#define POOL_BOX_MIN float3(-1.0, -POOL_HEIGHT, -1.0)
#define POOL_BOX_MAX float3(1.0, POOL_BOX_TOP, 1.0)

// The WATER-ONLY half of the pool box (top at the rest waterline y = 0): the volume the god rays
// and the bounded underwater fog march through. Shared so the two passes always march the same box.
#define POOL_WATER_BOX_MIN float3(-1.0, -POOL_HEIGHT, -1.0)
#define POOL_WATER_BOX_MAX float3(1.0, 0.0, 1.0)

// Wall-face pick threshold on |pool xz|: a point this close to the +/-1 footprint edge is ON that
// wall. Shared by WallSurface (shading) and the pool-trace gradient face pick - if they drifted,
// the gradient path would pick a different face than the shading and the tile mip would break
// silently at the corners.
#define POOL_WALL_FACE_EPS 0.999

#define CAUSTIC_PROJECTION_SCALE 0.75 // fits the projected caustic map into the pool footprint

// Shared by BOTH caustic generators (Caustics.shader pool path, LargeBodyCaustics.shader ocean
// path) so the two can never drift apart:
// - FOCUS_SCALE: brightness of the focused caustic (area-ratio gain).
// - NORMAL_SOFTEN: softens the sampled surface normal before focusing - full-strength slopes
//   over-focus the caustics into hard sparkles (inherited from the original WebGL demo).
#define CAUSTIC_FOCUS_SCALE   0.2
#define CAUSTIC_NORMAL_SOFTEN 0.5

// FFT ocean cascade layout, shared by every consumer (WaterLargeWaves.hlsl sampling,
// OceanFft.compute generation, WaterFoamParticles.compute crest-foam spawning) - three files used
// to carry their own copies. MAX_CASCADES also mirrors WaterOceanFft.cs MaxCascades
// (WaterWaveConstantsValidator guards the pair). A tiled cascade has no per-component wavelength at
// sample time, so shore attenuation uses one REPRESENTATIVE wavelength per cascade: the dominant
// energy of a tile sits around a QUARTER OF THE CASCADE'S BAND TOP.
//
// The band top is no longer the tile: WaterOceanFft.CascadeTileOversample makes each FFT tile 4x longer
// than the longest wave it carries (see the header there for why). So the fraction OF THE DOMAIN that
// lands on the same metres is 0.25 / 4. Written out rather than divided so it stays a literal the
// shader compiler folds, and so changing the oversample forces a look at this line.
#define OCEAN_FFT_MAX_CASCADES 4
#define OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION 0.0625

// ONE definition of the per-cascade distance fade: full near the camera, 0 past the cascade's visible
// range, cubic so the taper is gentle where it matters. Shared by the fragment normal/foam sum, the
// VERTEX displacement and the CPU buoyancy height bake. They MUST agree - a cascade that is faded out
// of the shading but still displacing the mesh aliases on the far clipmap cells, and one that is faded
// out of the render but not the bake floats objects on a surface that is not the one drawn.
float OceanCascadeDistanceFade(float camDist, float visibleArea)
{
    float f = saturate(camDist / max(visibleArea, 1e-3));
    return 1.0 - f * f * f;
}

// Rim-shadow sigmoid shaping (softens the pool-wall shadow edge in the caustic/wall passes).
#define RIM_SHADOW_SHARPNESS 200.0
#define RIM_SHADOW_SPREAD    10.0

// Caustic projection divides by the refracted light's downward component; keep it away
// from zero so a near-horizontal sun can't blow the projection up to infinity. The
// refracted light points DOWN (negative y is carried by the callers' conventions), so
// clamp the magnitude, preserving sign.
#define MIN_REFRACTED_LIGHT_Y 0.05

// Floor on a slab-divide ray component: an exactly-zero component with the origin ON that slab
// plane produced 0 * inf = NaN through the min/max chain below. At the floor the slab reads as
// effectively parallel (huge |t|), which the min/max chain handles fine.
#define RAY_SLAB_EPSILON 1e-6

// Slab intersection of a ray with an axis-aligned box; returns (tNear, tFar).
float2 IntersectCube(float3 origin, float3 ray, float3 cubeMin, float3 cubeMax)
{
    // NaN-guard the per-axis divides (see RAY_SLAB_EPSILON). A zero component gets +eps: the
    // sign is irrelevant at parallel - both slab t's land at +/-huge either way.
    float3 safeRay = float3(abs(ray.x) < RAY_SLAB_EPSILON ? RAY_SLAB_EPSILON : ray.x,
                            abs(ray.y) < RAY_SLAB_EPSILON ? RAY_SLAB_EPSILON : ray.y,
                            abs(ray.z) < RAY_SLAB_EPSILON ? RAY_SLAB_EPSILON : ray.z);
    float3 tMin = (cubeMin - origin) / safeRay;
    float3 tMax = (cubeMax - origin) / safeRay;
    float3 t1 = min(tMin, tMax);
    float3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar  = min(min(t2.x, t2.y), t2.z);
    return float2(tNear, tFar);
}

// Soft shadow the pool RIM casts on a point ABOVE the waterline. A sigmoid rather than a step, and
// its softness widens with the chord the ray cuts through the pool box (t.y - t.x), so a grazing sun
// gives a broad penumbra and an overhead one a tight edge.
//
// toLight MUST point TOWARD the sun - the '-refract(...)' convention used by WaterCommon,
// CausticOccluder, GodRays, WaterReceiver and WaterCausticProjection. Caustics.shader and
// LargeBodyCaustics instead keep the DOWNWARD propagation ray in their own 'refractedLight', so those
// callers negate at the call site. Passing the wrong one INVERTS the shadow and nothing complains -
// which is why the two former copies of this expression looked sign-mirrored and were in fact
// identical: each flipped the ray AND the term, and the flips cancelled.
float PoolRimShadow(float3 p, float3 toLight)
{
    float2 t = IntersectCube(p, toLight, POOL_BOX_MIN, POOL_BOX_MAX);
    return 1.0 / (1.0 + exp(-RIM_SHADOW_SHARPNESS / (1.0 + RIM_SHADOW_SPREAD * (t.y - t.x))
                            * (p.y + toLight.y * t.y - POOL_RIM_HEIGHT)));
}

// Signed clamp away from zero for the caustic-projection divides.
float SafeRefractedLightY(float y)
{
    return sign(y) * max(abs(y), MIN_REFRACTED_LIGHT_Y);
}

// Project a pool-space point down the refracted light onto the caustic map's UV.
float2 ProjectCausticUV(float3 poolPos, float3 refractedLight)
{
    return CAUSTIC_PROJECTION_SCALE
           * (poolPos.xz - poolPos.y * refractedLight.xz / SafeRefractedLightY(refractedLight.y))
           * 0.5 + 0.5;
}

// Refract-shadow look (published per body with _CausticOccluderActive by WaterUniformPublisher):
// the softness knob widens the vertical fade band below the occluder AND drives the lateral PCF
// penumbra; the sun strength is the directional light's own Shadow Strength, so this refracted
// path dims its shadows exactly like URP's shadow map does on the fallback path
// (shadowAttenuation folds the same value in) - toggling Refract Shadows no longer jumps from
// tuned shadows to pitch black.
float _OccluderShadowSoftness;  // 0 = legacy hard silhouette .. 1 = widest band + penumbra
float _SunShadowStrength;       // the sun's Light.shadowStrength (1 when no sun is wired)

// Soft depth band (normalised pool depth) over which the occluder shadow fades in just below the
// occluder, so its top edge isn't a hard step. The BASE keeps softness 0 at the legacy look; the
// softness knob widens it up to the MAX on top.
#define OCCLUDER_SHADOW_SOFTEN     0.03
#define OCCLUDER_SHADOW_SOFTEN_MAX 0.25

// The caustic RT's GREEN channel encodes the NORMALISED DEPTH (0 at the surface, 1 at the floor) of the
// SHALLOWEST submerged occluder along this refracted ray - min-blended by CausticOccluder, and 1 (floor,
// = no occluder) where nothing is submerged. A point is in shadow ONLY where it lies BELOW that occluder,
// i.e. its own depth exceeds the stored one; above the occluder it is lit. This gives the shadow a top,
// so a shaft/wall is no longer darkened both above AND below the object. Returns the LIT factor (1 lit,
// 0 shadowed). poolPosY is the point's pool-space Y (surface 0, floor -POOL_HEIGHT).
float OccluderLitFromGreen(float poolPosY, float greenDepth)
{
    float pointDepth = saturate(-poolPosY / POOL_HEIGHT); // 0 surface .. 1 floor
    float band = OCCLUDER_SHADOW_SOFTEN + _OccluderShadowSoftness * OCCLUDER_SHADOW_SOFTEN_MAX;
    float lit = 1.0 - saturate((pointDepth - greenDepth) / band);
    // The sun's Shadow Strength caps how dark ANY refracted shadow gets (identity at strength 1),
    // exactly like shadowAttenuation caps the shadow-map path.
    return lerp(1.0, lit, _SunShadowStrength);
}

// ---- Lateral penumbra: 4-tap PCF around the green silhouette ----
// A real shadow softens with distance below its caster. Green stores DEPTH, so blurring it would
// MIX depths and shift the edge instead of softening the shadow - the taps therefore COMPARE
// first (OccluderLitFromGreen) and average after, classic PCF. The radius grows with the
// receiving point's own depth (a proxy for distance below the occluder - most occluders float
// near the surface), so shallow points stay crisp and the floor goes soft, on BOTH sides of the
// silhouette edge. Callers fetch the four taps themselves - the two shader families bind
// _CausticTex differently (sampler2D vs TEXTURE2D) - but the offsets, radius and combine all
// live HERE so the pattern can never drift between consumers. Tap fetches are explicit-LOD, so
// every call site stays WGSL-safe in any control flow. Radius 0 (softness 0, or at the surface)
// collapses every tap onto the centre = the legacy single-sample look.
#define OCCLUDER_PCF_RADIUS_MAX 0.025 // caustic-UV penumbra radius at softness 1, floor depth
#define OCCLUDER_PCF_TAP0 float2( 0.7,  0.3)
#define OCCLUDER_PCF_TAP1 float2(-0.3,  0.7)
#define OCCLUDER_PCF_TAP2 float2(-0.7, -0.3)
#define OCCLUDER_PCF_TAP3 float2( 0.3, -0.7)

float OccluderPenumbraRadiusUV(float poolPosY)
{
    float pointDepth = saturate(-poolPosY / POOL_HEIGHT);
    return _OccluderShadowSoftness * OCCLUDER_PCF_RADIUS_MAX * pointDepth;
}

// Centre + the four taps, compared individually then averaged (see the PCF note above).
float OccluderLitFromGreenPCF(float poolPosY, float centerGreen, float4 tapGreens)
{
    return 0.2 * (OccluderLitFromGreen(poolPosY, centerGreen)
                + OccluderLitFromGreen(poolPosY, tapGreens.x)
                + OccluderLitFromGreen(poolPosY, tapGreens.y)
                + OccluderLitFromGreen(poolPosY, tapGreens.z)
                + OccluderLitFromGreen(poolPosY, tapGreens.w));
}

#endif // WEBGL_WATER_SHARED_INCLUDED
