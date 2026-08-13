// WebGpuWater - THE caustic-map resolver: turns a point into the UV, footprint and sampling scale
// for this body's caustic RT, in whichever frame that RT was actually written.
//
// WHY THIS FILE EXISTS. WaterVolume.RenderCausticsForThisBody picks between TWO generators - the pool
// caustic (projected onto the pool floor, indexed by ProjectCausticUV) and the large-body caustic
// (projected onto a reference plane, indexed in the sim WINDOW's moving world frame) - and publishes
// the choice as _CausticFrameMode. Until now exactly ONE consumer undid that projection correctly:
// WaterCausticProjection.shader, fixed 2026-07-28. WaterReceiver and WaterTerrain still assumed the
// pool frame unconditionally, so under an ocean they sampled a window-frame RT through a pool-frame
// map: wrong origin, wrong scale, and - because _SimCenter tracks the camera - a pattern that churns
// as you move. The fix belongs in ONE place that every consumer calls, not in each shader again.
//
// NOT resolved here: the waterline. Every consumer already computes its own surface height, and the
// receiver/terrain pair are already window-aware (WaterTerrain indexes the sim through WorldToSim).
// Only the caustic MAP was ever frame-blind, so only the map is shared.
//
// Dialect: URP HLSL. The legacy-CG consumers (Caustics.shader, GodRays.shader, the pool wall in
// WaterCommon.hlsl) are pool-box constructs by construction and keep ProjectCausticUV directly.
#ifndef WEBGL_WATER_CAUSTIC_MAP_INCLUDED
#define WEBGL_WATER_CAUSTIC_MAP_INCLUDED

#include "WaterVolume.hlsl" // _CausticFrameMode, _SimCenter/_SimExtent, CAUSTIC_*, FootprintMaskPool
#include "WaterShared.hlsl" // ProjectCausticUV, SafeRefractedLightY, IOR_*

// Hard ceiling on the projection mip bias: past this the pattern averages to a flat wash. Was local
// to WaterCausticProjection.shader; shared now that three shaders sample the same RT the same way.
#define CAUSTIC_PROJECTION_LOD_MAX 4.0

// Mip bias for the screen-space/receiver caustic projection (WaterVolume.LargeCausticProjectionLod:
// the grid floor plus the artist's extra soften). The god rays deliberately do NOT use it - they
// sample the same RT at their own depth-scaled LOD, because the beam banding must reach their march
// sharp. Declared here so the three URP consumers share one declaration.
float _LargeCausticProjectionLod;

struct WaterCausticMap
{
    float2 uv;        // where to sample _CausticTex
    float  footprint; // 1 inside, 0 outside, faded across the window border; 0 when the RT is undefined
    float  gradScale; // multiply ddx(uv) / ddy(uv) by this before a GRAD sample
};

// worldPos and poolPos are THE SAME POINT in the two frames (poolPos == WorldToPool(worldPos)); both
// are passed because every caller already has both and the conversion is not free. lightDirTowardSun
// is the global "toward the light" (_LightDir), taken as a parameter because each consumer declares
// that global itself.
WaterCausticMap ResolveCausticMap(float3 worldPos, float3 poolPos, float3 lightDirTowardSun)
{
    int causticFrame = (int)(_CausticFrameMode + 0.5);
    bool windowFrame = (causticFrame == CAUSTIC_FRAME_WINDOW);

    // POOL frame: ProjectCausticUV's xz/y ratio is only valid in pool space, so a WORLD direction
    // mis-projects on non-uniform (deep) bodies. Uniform extents are byte-identical.
    float3 refractedLight = -refract(-lightDirTowardSun, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float2 poolCuv = ProjectCausticUV(poolPos, WorldDirToPool(refractedLight));

    // WINDOW frame: LargeBodyGodRays' LargeBodyCausticAt expression - same refracted-sun form
    // (NOT negated, unlike the pool ray above), same normalisation - because that is the map
    // proven to register with what LargeBodyCaustics.shader wrote. The REFERENCE PLANE is where
    // the two consumer families deliberately part ways: floor projection (this file) uses the
    // GENERATOR's frame (_SimCenter.y - LARGE_CAUSTIC_REFERENCE_DEPTH, see that shader's vert)
    // so the pattern stays registered to the written map, while the god-ray march uses the LIVE
    // camera-surface plane (camSurfY - ..., see its own comment there) so the shimmer tracks the
    // swell instead of pumping against a stale scalar. The projection expression itself is still
    // hand-mirrored in both files - drift there IS the visible-seam class; the dedupe is tracked
    // (docs/WebGpuWater_Standards_Audit_2026-07-31.md, S3).
    float3 refractedSun = refract(-lightDirTowardSun, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float causticRefPlaneY = _SimCenter.y - LARGE_CAUSTIC_REFERENCE_DEPTH;
    float2 projXZ = worldPos.xz + refractedSun.xz
                  * ((causticRefPlaneY - worldPos.y) / SafeRefractedLightY(refractedSun.y));
    float2 windowNorm = (projXZ - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);

    WaterCausticMap map;
    // Selected WITHOUT a branch: a GRAD sample must stay in uniform control flow - an
    // implicit-derivative sample inside a per-fragment branch is undefined on WebGPU/WGSL.
    map.uv = windowFrame ? (windowNorm * 0.5 + 0.5) : poolCuv;

    // Widen the sampling footprint instead of switching to an explicit LOD: scaling both derivatives
    // by 2^bias raises the mip exactly as adding the bias would, but KEEPS the screen footprint's
    // shape, so a grazing view still filters along the direction it is stretched in - which an
    // isotropic LOD sample would throw away, at the very angle that aliases worst. Window frame only:
    // pool RTs carry no mip chain (WaterCausticsPass allocates mips for ocean clipmaps only) and
    // their samplers were tuned against LOD 0. The scale is a uniform, so control flow stays coherent.
    float projectionLod = clamp(_LargeCausticProjectionLod, 0.0, CAUSTIC_PROJECTION_LOD_MAX);
    map.gradScale = windowFrame ? exp2(projectionLod) : 1.0;

    // Footprint, answered in the frame that owns it. The mode is a uniform, so this branch is
    // coherent across the whole draw and costs nothing per pixel.
    if (windowFrame)
    {
        // The RT holds data only inside the drawn window (cleared transparent outside), so the window
        // IS the footprint - the pool box is an arbitrary rectangle on an unbounded ocean. Faded at
        // the border with the shared constant the shafts use, so caustics and shafts die out together
        // instead of the caustics popping at the edge.
        float2 edge = 1.0 - abs(windowNorm);
        map.footprint = (edge.x <= 0.0 || edge.y <= 0.0)
                      ? 0.0 : saturate(min(edge.x, edge.y) / CAUSTIC_WINDOW_FADE);
    }
    else
    {
        map.footprint = FootprintMaskPool(poolPos);
    }

    // CAUSTIC_FRAME_NONE - a windowed body that is NOT an ocean clipmap. Nothing ever draws into its
    // RT and it is never even cleared, so its contents are undefined: every consumer must contribute
    // its identity rather than project uninitialised memory across the screen.
    if (causticFrame == CAUSTIC_FRAME_NONE) map.footprint = 0.0;

    return map;
}

#endif // WEBGL_WATER_CAUSTIC_MAP_INCLUDED
