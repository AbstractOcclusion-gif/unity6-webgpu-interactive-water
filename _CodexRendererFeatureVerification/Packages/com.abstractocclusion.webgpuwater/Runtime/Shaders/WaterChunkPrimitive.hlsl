// WebGpuWater - water CHUNK primitive intersection, in POOL space.
// The chunk's view of the shared primitive kernels (WaterPrimitiveShape.hlsl): everything works
// in the body's POOL space - the unit shape spanning [-1, 1] per axis (xz in [-1,1] is the
// footprint; the volume frame's PoolToWorld / WorldToPool place and size it), so rotation and
// non-uniform extent are the frame's. This file exists to bind that ONE space convention to the
// shared kernels, so no chunk consumer has to know the half-extent.
#ifndef WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED
#define WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED

#include "WaterPrimitiveShape.hlsl" // PrimitiveIntersect / PrimitiveSurfaceNormal (+ WaterShared)

// C# pair: WaterVolume.ChunkFootprint (published as _ChunkShape). Values are the enum ordinals - 1
// (None isn't drawn), so Box = 0, Sphere = 1 - the shared PRIMITIVE_SHAPE_* values, aliased here
// so chunk code keeps reading in its own vocabulary.
#define CHUNK_SHAPE_BOX              PRIMITIVE_SHAPE_BOX
#define CHUNK_SHAPE_SPHERE           PRIMITIVE_SHAPE_SPHERE
#define CHUNK_SHAPE_SPHERE_THRESHOLD PRIMITIVE_SHAPE_THRESHOLD

// Half-extent of the unit shape in POOL space: the [-1, 1] cube, and the INSCRIBED sphere's radius.
#define CHUNK_POOL_HALF_EXTENT 1.0

// (tNear, tFar) of the selected chunk primitive along a POOL-space ray, in world metres. The
// direction is passed UNNORMALISED (poolDir = WorldDirToPool(worldDir)) - see IntersectLocalSphere
// for why that preserves world-metre t's. Returns an EMPTY interval on a miss.
float2 ChunkIntersect(float shape, float3 origin, float3 dir)
{
    return PrimitiveIntersect(shape, origin, dir, CHUNK_POOL_HALF_EXTENT);
}

// Outward POOL-space surface normal at a point ON the primitive's surface (the ray's entry point),
// for the sun facet + refraction. Map to world with PoolNormalToWorld (inverse-transpose of the
// frame) at the call site.
float3 ChunkSurfaceNormalPool(float shape, float3 surfacePoint)
{
    return PrimitiveSurfaceNormal(shape, surfacePoint);
}

#endif // WEBGPUWATER_CHUNK_PRIMITIVE_INCLUDED
