// WebGL Water - volume placement frame (Unity 6 / URP port)
//
// Maps the normalised pool box (x,z in [-1,1], surface y=0, floor y=-1) into world
// space with a full transform so the volume can be moved, ROTATED (incl. tilt) and
// sized NON-UNIFORMLY (rectangular footprint + independent depth) without scaling any
// GameObject transforms:
//
//   world = center + Rotation * (pool * extent)        (extent applied per-axis)
//   pool  = (Rotation^-1 * (world - center)) / extent
//
// Because extent is non-uniform, directions are NOT angle-preserving under this map,
// so the surface shader does its reflection/refraction in WORLD space and only uses
// these helpers to move points/rays in and out of the unit box. Identity defaults
// (extent 1, rotation I) reproduce the original 1:1 pool exactly.
#ifndef WEBGL_WATER_VOLUME_INCLUDED
#define WEBGL_WATER_VOLUME_INCLUDED

float3   _VolumeCenter; // world position of the pool origin (centre of the surface)
float3   _VolumeExtent; // world half-size per pool unit, per axis (x,y,z)
float4x4 _VolumeRot;    // rotation (upper 3x3 used); identity when unset

// Large-water sim window: the interactive ripple sim covers a camera-following window
// (not the whole body). _SimWindowed = 0 restores the whole-body path exactly.
float    _SimWindowed;       // 0/1 branch flag
float3   _SimCenter;         // world centre of the window (on the surface plane)
float3   _SimExtent;         // world half-size (x,z) and height scale (y) of the window
float    _SimEdgeFadeTexels; // border falloff width, in sim texels

// Depth (world metres) below the surface that the large-body caustic is projected onto - the ocean
// analog of the pool floor. SHARED by LargeBodyCaustics.shader (generation) and LargeBodyGodRays.shader
// (sampling) so both use the exact same projection plane; keep them in lockstep.
#define LARGE_CAUSTIC_REFERENCE_DEPTH 4.0

// Open-water (lake/ocean) path flag. 0 = the original pool / small-body look, unchanged.
// 1 = the surface stands alone with NO pool: the analytic refraction ray-march is bypassed
// (a deep-water colour is returned instead) and the mesh god rays are suppressed. Published
// per-body via the MaterialPropertyBlock, exactly like _SimWindowed above. Defaults to 0 when
// unpublished, so nothing changes for bodies that never set it.
float    _LargeBody;

float3 VolumeExtentSafe()
{
    return float3(_VolumeExtent.x > 1e-5 ? _VolumeExtent.x : 1.0,
                  _VolumeExtent.y > 1e-5 ? _VolumeExtent.y : 1.0,
                  _VolumeExtent.z > 1e-5 ? _VolumeExtent.z : 1.0);
}

// Rotation, guarded to identity when the global hasn't been published yet.
float3x3 VolumeRot()
{
    float3x3 r = (float3x3)_VolumeRot;
    return abs(determinant(r)) < 0.5 ? float3x3(1,0,0, 0,1,0, 0,0,1) : r;
}

float3 PoolToWorld(float3 poolPos)
{
    return _VolumeCenter + mul(VolumeRot(), poolPos * VolumeExtentSafe());
}

float3 WorldToPool(float3 worldPos)
{
    return mul(transpose(VolumeRot()), worldPos - _VolumeCenter) / VolumeExtentSafe();
}

// Keeps the boundary walls (pool edge at |pool.xz| = 1) counted as inside so a pool's
// own rim/wall fragments don't fall just outside the footprint and lose their shading.
#define FOOTPRINT_EDGE_EPSILON 1e-3

// 1 when a POOL-space point lies within the footprint box (|x|,|z| <= 1), else 0.
// Underwater tint, caustics, downwelling and fog gate on this so water shading never bleeds
// onto geometry beside the body (e.g. objects that merely sit below the water plane's Y but
// outside its footprint). Y is intentionally ignored here; submersion is a separate test.
float FootprintMaskPool(float3 poolPos)
{
    return (max(abs(poolPos.x), abs(poolPos.z)) <= 1.0 + FOOTPRINT_EDGE_EPSILON) ? 1.0 : 0.0;
}

// World direction -> pool direction (NOT normalised; valid for box intersection).
float3 WorldDirToPool(float3 worldDir)
{
    return mul(transpose(VolumeRot()), worldDir) / VolumeExtentSafe();
}

// Pool-space surface normal -> world normal (inverse-transpose of the linear map).
float3 PoolNormalToWorld(float3 poolNormal)
{
    return normalize(mul(VolumeRot(), poolNormal / VolumeExtentSafe()));
}

float3 SimExtentSafe()
{
    return float3(_SimExtent.x > 1e-5 ? _SimExtent.x : 1.0,
                  _SimExtent.y > 1e-5 ? _SimExtent.y : 1.0,
                  _SimExtent.z > 1e-5 ? _SimExtent.z : 1.0);
}

// World -> sim-window normalised coords (.xz in [-1,1] inside the window), reusing the
// shared volume rotation. Mirrors WorldToPool but around the scrolling window frame.
float3 WorldToSim(float3 worldPos)
{
    return mul(transpose(VolumeRot()), worldPos - _SimCenter) / SimExtentSafe();
}

#endif // WEBGL_WATER_VOLUME_INCLUDED
