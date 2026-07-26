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

// Dry length of the segment [origin, origin + segDir * maxDist] inside the MESH exclusion volumes,
// taken from the prepass at screenUV. 0 when no mesh volume covers the pixel. Callers still gate on
// _ExclusionMeshCount so a scene without mesh volumes never even issues the texel fetches.
//
// Both endpoints are reconstructed to world space and projected onto segDir. That projection is
// EXACT rather than approximate because origin and segDir lie on this pixel's camera ray by
// construction - which is precisely the contract that confines the mesh tier to camera-ray
// queries in the first place (see WaterExclusionMesh.hlsl).
float ExclusionMeshRayLength(float2 screenUV, float3 origin, float3 segDir, float maxDist)
{
    float2 rawSpan = ExclusionMeshRawSpan(int2(screenUV * _ScreenParams.xy));

    // No exit face at this pixel means no mesh volume stands along this ray at all.
    float backEye = LinearEyeDepth(rawSpan.y, _ZBufferParams);
    if (ExclusionMeshDepthEmpty(backEye, _ProjectionParams.z)) return 0.0;
    float3 backWS = ComputeWorldSpacePosition(screenUV, rawSpan.y, UNITY_MATRIX_I_VP);
    float tBack = dot(backWS - origin, segDir);

    // Front empty + back valid = the camera sits INSIDE the mesh (its front faces are behind the
    // eye, so nothing rasterised), and the dry column starts at the ray's own origin - the same
    // rule the chunk wall applies to its missing entry face.
    float tFront = 0.0;
    float frontEye = LinearEyeDepth(rawSpan.x, _ZBufferParams);
    if (!ExclusionMeshDepthEmpty(frontEye, _ProjectionParams.z))
    {
        float3 frontWS = ComputeWorldSpacePosition(screenUV, rawSpan.x, UNITY_MATRIX_I_VP);
        tFront = dot(frontWS - origin, segDir);
    }

    return max(min(tBack, maxDist) - max(tFront, 0.0), 0.0);
}

#endif // WEBGPUWATER_EXCLUSION_MESH_SPAN_INCLUDED
