// WebGpuWater - MESH exclusion span along the view ray (URP-core dialect).
// The world-space half of WaterExclusionMesh.hlsl: turns the prepass's raw front/back depths into
// the DRY LENGTH a view ray spends inside the mesh volumes, in the same world metres the analytic
// ExclusionRayLength returns - so a consumer subtracts both the same way and never has to know
// which tier a volume came from.
//
// Kept OUT of WaterExclusionMesh.hlsl because it needs URP-core-only helpers
// (ComputeWorldSpacePosition, UNITY_MATRIX_I_VP, the two-argument LinearEyeDepth) that
// WaterSurface.shader - a CGPROGRAM / UnityCG shader - cannot compile. The surface's own carve
// needs nothing more than a depth compare, which the dialect-free header already provides; only
// the fog and the wall, both URP-core, need real world-space lengths.
#ifndef WEBGPUWATER_EXCLUSION_MESH_SPAN_INCLUDED
#define WEBGPUWATER_EXCLUSION_MESH_SPAN_INCLUDED

#include "WaterExclusionMesh.hlsl" // the raw prepass fetch + the far-plane emptiness convention

// Distance along the ray at which it LEAVES the rasterised exclusion silhouette at this pixel.
// False when no carve covers the pixel, when the exit lies behind the ray's own origin, or when the
// prepass did not run this frame - in which case the depth RTs hold nothing this frame may trust and
// every caller falls back to its analytic path.
//
// `rawSpan` is handed back rather than re-fetched by the caller: ExclusionMeshRawSpan LOADs BOTH
// depth textures, so a second call would double this function's texel traffic for one number the
// first call already had. Callers that only want the exit pass a dummy and ignore it.
//
// This is the ONE place a back-face depth becomes a world distance. ExclusionMeshRayLength below
// uses it instead of reconstructing the exit a second time.
bool ExclusionPrepassExitDistance(float2 screenUV, float3 origin, float3 segDir,
                                  out float2 rawSpan, out float exitDist)
{
    rawSpan = float2(0.0, 0.0);
    exitDist = 0.0;
    if (_ExclusionPrepassValid < 0.5) return false;

    rawSpan = ExclusionMeshRawSpan(int2(screenUV * _ScreenParams.xy));

    // No exit face at this pixel means no exclusion volume stands along this ray at all.
    float backEye = LinearEyeDepth(rawSpan.y, _ZBufferParams);
    if (ExclusionMeshDepthEmpty(backEye, _ProjectionParams.z)) return false;

    float3 backWS = ComputeWorldSpacePosition(screenUV, rawSpan.y, UNITY_MATRIX_I_VP);
    exitDist = dot(backWS - origin, segDir);
    return exitDist > 0.0;
}

// Distance along the ray at which it ENTERS the rasterised silhouette at this pixel, read from the
// SAME rawSpan a prior ExclusionPrepassExitDistance call handed back - so an entry costs arithmetic
// only, never a second pair of depth LOADs.
//
// 0 when the front face is empty, which by the prepass's own convention means the eye sits INSIDE
// the mesh (its front faces are behind the camera): the dry column then starts at the ray's own
// origin, the same rule the chunk wall applies to its missing entry face.
//
// Only meaningful when that exit call returned true - an empty back face means no silhouette covers
// the pixel at all, and there is no span for an entry to belong to.
float ExclusionPrepassEntryDistance(float2 rawSpan, float2 screenUV, float3 origin, float3 segDir)
{
    float frontEye = LinearEyeDepth(rawSpan.x, _ZBufferParams);
    if (ExclusionMeshDepthEmpty(frontEye, _ProjectionParams.z)) return 0.0;

    float3 frontWS = ComputeWorldSpacePosition(screenUV, rawSpan.x, UNITY_MATRIX_I_VP);
    return dot(frontWS - origin, segDir);
}

// Dry length of the segment [origin, origin + segDir * maxDist] inside the MESH exclusion volumes,
// taken from the prepass at screenUV. 0 when no mesh volume covers the pixel. Callers still gate on
// _ExclusionMeshCount so a scene without mesh volumes never even issues the texel fetches.
//
// Both endpoints are reconstructed to world space and projected onto segDir. That projection is
// EXACT rather than approximate because origin and segDir lie on this pixel's camera ray by
// construction - which is precisely the contract that confines the mesh tier to camera-ray
// queries in the first place (see WaterExclusionMesh.hlsl).
//
// Behaviour is unchanged by routing the exit through the helper above: a non-positive tBack already
// produced 0 through the final max(), and an unwritten RT already read as empty. The only new
// refusal is the explicit _ExclusionPrepassValid gate, which now says so instead of relying on a
// cleared target to mean the same thing by accident. The ENTRY routes through its own helper for
// the same reason - one home per endpoint - and computes the identical value it used to inline.
float ExclusionMeshRayLength(float2 screenUV, float3 origin, float3 segDir, float maxDist)
{
    float2 rawSpan;
    float tBack;
    if (!ExclusionPrepassExitDistance(screenUV, origin, segDir, rawSpan, tBack)) return 0.0;

    float tFront = ExclusionPrepassEntryDistance(rawSpan, screenUV, origin, segDir);
    return max(min(tBack, maxDist) - max(tFront, 0.0), 0.0);
}

#endif // WEBGPUWATER_EXCLUSION_MESH_SPAN_INCLUDED
