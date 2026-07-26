// WebGpuWater - the ONE analytic primitive kernel set (box + sphere/ellipsoid).
// Two systems carve or fill space with the same handful of shapes - water CHUNKS (a finite body
// of water standing in dry air) and water EXCLUSION VOLUMES (a dry interior carved out of water) -
// and both need the same four answers about a shape: does it contain a point, where does a ray
// enter and leave it, how deep inside is a point, and which way does its surface face. Written
// once here (reuse, never rewrite); WaterChunkPrimitive.hlsl and WaterExclusion.hlsl are thin
// per-system wrappers that supply their own local space and uniform layout.
//
// EVERYTHING IS LOCAL. Each caller maps world space into its own unit local space with one
// matrix, so the shape here is always centred on the origin and axis-aligned: rotation, position
// and (non-uniform) size are the matrix's job. `halfExtent` is the caller's convention for that
// space - the chunk's POOL space uses 1 (the [-1, 1] cube), the exclusion volume's unit box uses
// 0.5 - and it is the box half-edge AND the inscribed sphere's radius, so the sphere is exactly
// the box's inscribed ball and a non-uniform matrix turns it into an ELLIPSOID for free.
//
// The next shapes (capsule, cylinder, wedge) slot in here and every consumer of both systems
// inherits them at once.
#ifndef WEBGPUWATER_PRIMITIVE_SHAPE_INCLUDED
#define WEBGPUWATER_PRIMITIVE_SHAPE_INCLUDED

#include "WaterShared.hlsl" // IntersectCube + RAY_SLAB_EPSILON

// Shape selector. Carried as a FLOAT because that is what uniform arrays and
// MaterialPropertyBlocks hold, and compared against the threshold rather than tested for
// equality so a value that arrives slightly off can never select "no shape at all".
// C# pairs: WaterExclusionVolume.Shape and WaterVolume.ChunkFootprint ordinals
// (WaterWaveConstantsValidator guards PRIMITIVE_SHAPE_SPHERE against the C# side).
#define PRIMITIVE_SHAPE_BOX       0.0
#define PRIMITIVE_SHAPE_SPHERE    1.0
#define PRIMITIVE_SHAPE_THRESHOLD 0.5

// Empty interval (tNear > tFar) returned on a miss, so a caller that subtracts
// max(0, min(tFar, cap) - max(tNear, 0)) gets a zero-length span without a special case.
#define PRIMITIVE_MISS_INTERVAL float2(1.0, -1.0)

// Fallback normal for the degenerate "surface point at the exact centre" case: a real surface
// point can never be there, so this only ever guards a caller that passed garbage - and it must
// guard it, because normalising a zero vector yields NaN that then spreads through the shading.
#define PRIMITIVE_DEGENERATE_NORMAL float3(0.0, 1.0, 0.0)

bool PrimitiveIsSphere(float shape)
{
    return shape >= PRIMITIVE_SHAPE_THRESHOLD;
}

// Ray vs the sphere of radius `halfExtent` centred on the local origin, as (tNear, tFar).
// `dir` is passed UNNORMALISED (it is the local image of a unit WORLD direction): because local
// space is an affine image of world space the ray parameter t is preserved by the map, so the
// t's come back in WORLD metres - the same convention IntersectCube uses, which is what lets
// every caller mix box and sphere spans in one length accumulation.
float2 IntersectLocalSphere(float3 origin, float3 dir, float halfExtent)
{
    float a = dot(dir, dir);
    float b = dot(origin, dir);
    float c = dot(origin, origin) - halfExtent * halfExtent;
    float discriminant = b * b - a * c;
    if (discriminant < 0.0) return PRIMITIVE_MISS_INTERVAL;
    float root = sqrt(discriminant);
    // A degenerate (zero-length) direction pushes both roots to +-huge, which is the right
    // answer for a ray that never moves: inside stays inside, outside stays outside.
    float invA = 1.0 / max(a, RAY_SLAB_EPSILON);
    return float2((-b - root) * invA, (-b + root) * invA);
}

// (tNear, tFar) of the selected primitive along a LOCAL-space ray, in world metres.
float2 PrimitiveIntersect(float shape, float3 origin, float3 dir, float halfExtent)
{
    if (PrimitiveIsSphere(shape)) return IntersectLocalSphere(origin, dir, halfExtent);
    return IntersectCube(origin, dir,
                         float3(-halfExtent, -halfExtent, -halfExtent),
                         float3( halfExtent,  halfExtent,  halfExtent));
}

// True when the LOCAL-space point lies inside (or exactly on) the selected primitive.
bool PrimitiveContains(float shape, float3 local, float halfExtent)
{
    if (PrimitiveIsSphere(shape)) return dot(local, local) <= halfExtent * halfExtent;
    return all(abs(local) <= halfExtent);
}

// Inset of a LOCAL-space point from the primitive's surface, in local units: 0 exactly ON the
// surface, `halfExtent` at the centre, NEGATIVE outside. Box = the smallest per-axis inset (a
// point near one face is shallow however far it sits from the others), sphere = the radial
// inset. Lets a consumer fade by intrusion depth instead of by a binary inside test.
float PrimitiveInteriorDepth(float shape, float3 local, float halfExtent)
{
    if (PrimitiveIsSphere(shape)) return halfExtent - length(local);
    float3 inset = halfExtent - abs(local);
    return min(inset.x, min(inset.y, inset.z));
}

// Outward LOCAL-space normal at a point ON the primitive's surface (typically a ray's entry
// point). Sphere: the radial direction. Box: the dominant-axis face. Map it to world with the
// inverse-transpose of the local->world matrix at the call site - which, for callers that hold
// the world->local matrix, is just mul(normal, (float3x3)worldToLocal).
float3 PrimitiveSurfaceNormal(float shape, float3 surfaceLocal)
{
    if (PrimitiveIsSphere(shape))
    {
        float lengthSquared = dot(surfaceLocal, surfaceLocal);
        if (lengthSquared <= RAY_SLAB_EPSILON) return PRIMITIVE_DEGENERATE_NORMAL;
        return surfaceLocal * rsqrt(lengthSquared);
    }
    float3 a = abs(surfaceLocal);
    if (a.x >= a.y && a.x >= a.z) return float3(sign(surfaceLocal.x), 0.0, 0.0);
    if (a.y >= a.z)               return float3(0.0, sign(surfaceLocal.y), 0.0);
    return float3(0.0, 0.0, sign(surfaceLocal.z));
}

#endif // WEBGPUWATER_PRIMITIVE_SHAPE_INCLUDED
