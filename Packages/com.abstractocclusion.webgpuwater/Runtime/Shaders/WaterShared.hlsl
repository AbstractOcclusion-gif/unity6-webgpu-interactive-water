// WebGL Water - shared constants & pure helpers (Unity 6 / URP port)
// Backend-agnostic: ONLY #defines, static consts and pure math here (no sampler or
// global declarations), so both the legacy-CG shaders and the URP HLSL shaders can
// include it without clashing. Faithful to Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_SHARED_INCLUDED
#define WEBGL_WATER_SHARED_INCLUDED

#define IOR_AIR   1.0
#define IOR_WATER 1.333

// Squared-length floor under which a direction has cancelled to ~zero and normalize()
// would return NaN (0/0). Shared by every degenerate-direction guard (specular taps,
// foam tangent frames, particle axes); well under any visually meaningful vector.
#define DEGENERATE_DIR_EPSILON 1e-8

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
// to carry their own copies. MAX_CASCADES also mirrors WaterOceanFft.cs MaxCascades (C#, not
// validator-parsed - keep lockstep by hand). A tiled cascade has no per-component wavelength at
// sample time, so shore attenuation uses one REPRESENTATIVE wavelength per cascade: the dominant
// energy of a tile sits around this fraction of its domain.
#define OCEAN_FFT_MAX_CASCADES 4
#define OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION 0.25

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

// Soft depth band (normalised pool depth) over which the occluder shadow fades in just below the
// occluder, so its top edge isn't a hard step.
#define OCCLUDER_SHADOW_SOFTEN 0.03

// The caustic RT's GREEN channel encodes the NORMALISED DEPTH (0 at the surface, 1 at the floor) of the
// SHALLOWEST submerged occluder along this refracted ray - min-blended by CausticOccluder, and 1 (floor,
// = no occluder) where nothing is submerged. A point is in shadow ONLY where it lies BELOW that occluder,
// i.e. its own depth exceeds the stored one; above the occluder it is lit. This gives the shadow a top,
// so a shaft/wall is no longer darkened both above AND below the object. Returns the LIT factor (1 lit,
// 0 shadowed). poolPosY is the point's pool-space Y (surface 0, floor -POOL_HEIGHT).
float OccluderLitFromGreen(float poolPosY, float greenDepth)
{
    float pointDepth = saturate(-poolPosY / POOL_HEIGHT); // 0 surface .. 1 floor
    return 1.0 - saturate((pointDepth - greenDepth) / OCCLUDER_SHADOW_SOFTEN);
}

#endif // WEBGL_WATER_SHARED_INCLUDED
