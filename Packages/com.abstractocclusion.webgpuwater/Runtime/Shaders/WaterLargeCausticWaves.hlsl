// WebGpuWater - compile-bounded ocean wave sampling for LargeBodyCaustics.
//
// The visible surface needs the complete shore, surf, analytic fallback and FFT composition in
// WaterLargeWaves.hlsl. The caustic generator evaluates its projection at five positions per
// vertex, so importing that full graph here makes the D3D11 compiler expand the graph five times
// and time out on a cold package import. This focused path reads the already-generated FFT arrays
// directly. It preserves the live ocean height and normal that focus the caustic while leaving
// shore/surf ownership to the visible surface and the dedicated caustic ripple field.
#ifndef WEBGPUWATER_LARGE_CAUSTIC_WAVES_INCLUDED
#define WEBGPUWATER_LARGE_CAUSTIC_WAVES_INCLUDED

#include "WaterShared.hlsl"

#define LARGE_CAUSTIC_FFT_ACTIVE_THRESHOLD 0.5
#define LARGE_CAUSTIC_FFT_MIN_DOMAIN        1e-3
#define LARGE_CAUSTIC_FFT_MIN_CASCADE_COUNT 1.0

Texture2DArray _OceanFftDisplacement;
SamplerState sampler_OceanFftDisplacement;
Texture2DArray _OceanFftNormal;
SamplerState sampler_OceanFftNormal;

float4 _OceanFftDomainSizes;
float4 _OceanFftVisibleAreas;
float _OceanFftCascadeCount;
float _OceanFftActive;
// Surface-current drift (full rationale: WaterWaves.hlsl). Guarded: whichever include lands
// first in a chain defines it; zero offset is bit-identical.
#ifndef WEBGPUWATER_OCEAN_CURRENT_INCLUDED
#define WEBGPUWATER_OCEAN_CURRENT_INCLUDED
float4 _OceanCurrentOffset;

float2 OceanCurrentDrift(float2 worldXZ)
{
    return worldXZ - _OceanCurrentOffset.xy;
}
#endif
float _LargeWaveAmplitude;

static const float4 LargeCausticFftFarSlopeFloor = float4(0.15, 0.20, 0.25, 0.25);

void SampleLargeCausticOcean(float2 worldXZ, float smoothRadius,
                             out float waveHeight, out float2 normalTilt)
{
    waveHeight = 0.0;
    normalTilt = float2(0.0, 0.0);
    if (_OceanFftActive <= LARGE_CAUSTIC_FFT_ACTIVE_THRESHOLD) return;

    uint normalWidth;
    uint normalHeight;
    uint normalLayers;
    uint normalMipCount;
    _OceanFftNormal.GetDimensions(0, normalWidth, normalHeight, normalLayers, normalMipCount);

    float cameraDistance = distance(worldXZ, _WorldSpaceCameraPos.xz);
    float cascadeCount = max(_OceanFftCascadeCount, LARGE_CAUSTIC_FFT_MIN_CASCADE_COUNT);
    [loop]
    for (int cascadeIndex = 0; cascadeIndex < OCEAN_FFT_MAX_CASCADES; cascadeIndex++)
    {
        float active = cascadeIndex < (int)_OceanFftCascadeCount ? 1.0 : 0.0;
        float slice = min((float)cascadeIndex, cascadeCount - 1.0);
        float domain = max(_OceanFftDomainSizes[cascadeIndex], LARGE_CAUSTIC_FFT_MIN_DOMAIN);
        // Same current drift as the surface samplers, so caustics track the drifting crests.
        float2 uv = OceanCurrentDrift(worldXZ) / domain;
        float distanceFade = OceanCascadeDistanceFade(
            cameraDistance, _OceanFftVisibleAreas[cascadeIndex]);

        float displacementHeight = _OceanFftDisplacement.SampleLevel(
            sampler_OceanFftDisplacement, float3(uv, slice), 0).y;
        waveHeight += active * distanceFade * displacementHeight * _LargeWaveAmplitude;

        // The normal texture owns mips, unlike the displacement array. Convert the authored
        // smoothing radius from world metres to cascade texels so deeper, broader shafts read a
        // stable swell without paying four full surface-height evaluations at every projection.
        float distanceLod = log2(1.0 + cameraDistance / domain);
        float smoothLod = log2(1.0 + max(smoothRadius, 0.0) * (float)normalWidth / domain);
        float maxLod = (float)max((int)normalMipCount - 1, 0);
        float normalLod = min(max(distanceLod, smoothLod), maxLod);
        float2 cascadeTilt = _OceanFftNormal.SampleLevel(
            sampler_OceanFftNormal, float3(uv, slice), normalLod).xz;
        float slopeWeight = active * max(distanceFade, LargeCausticFftFarSlopeFloor[cascadeIndex]);
        normalTilt += slopeWeight * cascadeTilt;
    }
}

#endif // WEBGPUWATER_LARGE_CAUSTIC_WAVES_INCLUDED
