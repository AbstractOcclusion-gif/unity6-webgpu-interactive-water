#ifndef WEBGPUWATER_SEA_STATE_FETCH_INCLUDED
#define WEBGPUWATER_SEA_STATE_FETCH_INCLUDED

Texture2D<float4> _SeaStateFetchTex;
float4 _SeaStateFetchFrame;  // xy world centre, zw half-size
float4 _SeaStateFetchParams; // x strength, y full fetch, z full wavelength, w valid

static const float SEA_STATE_FETCH_PEAK_EXPONENT = 0.66;
static const float SEA_STATE_FETCH_HEIGHT_EXPONENT = 0.5;
static const float SEA_STATE_FETCH_EPSILON = 0.01;
static const int SEA_STATE_FETCH_RESOLUTION = 256;
static const float SEA_STATE_FETCH_FULL_METERS = 10000.0;
static const float SEA_STATE_FETCH_FULL_WAVELENGTH = 100.0;

float SeaStateFetchBilinear(float2 uv)
{
    float2 texel = clamp(uv * SEA_STATE_FETCH_RESOLUTION - 0.5, 0.0,
                         (float)SEA_STATE_FETCH_RESOLUTION - 1.0);
    int2 p0 = (int2)texel;
    int2 p1 = min(p0 + 1, SEA_STATE_FETCH_RESOLUTION - 1);
    float2 f = texel - p0;
    float bottom = lerp(_SeaStateFetchTex.Load(int3(p0.x, p0.y, 0)).r,
                        _SeaStateFetchTex.Load(int3(p1.x, p0.y, 0)).r, f.x);
    float top = lerp(_SeaStateFetchTex.Load(int3(p0.x, p1.y, 0)).r,
                     _SeaStateFetchTex.Load(int3(p1.x, p1.y, 0)).r, f.x);
    return lerp(bottom, top, f.y);
}

float SeaStateFetchWeight(float2 worldXZ, float wavelength)
{
    if (_SeaStateFetchParams.w <= 0.0 || _SeaStateFetchParams.x <= 0.0) return 1.0;
    float2 uv = (worldXZ - _SeaStateFetchFrame.xy) / (2.0 * _SeaStateFetchFrame.zw) + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;

    float normalizedFetch = SeaStateFetchBilinear(uv);
    // JONSWAP fetch growth: lambda_p ~ F^0.66, while significant height grows as F^0.5.
    float wavelengthRatio = max(wavelength, SEA_STATE_FETCH_EPSILON) / SEA_STATE_FETCH_FULL_WAVELENGTH;
    float requiredFetch = SEA_STATE_FETCH_FULL_METERS
                        * pow(wavelengthRatio, 1.0 / SEA_STATE_FETCH_PEAK_EXPONENT);
    float fetchMeters = saturate(normalizedFetch) * SEA_STATE_FETCH_FULL_METERS;
    float physicalWeight = pow(saturate(fetchMeters / max(requiredFetch, SEA_STATE_FETCH_EPSILON)),
                               SEA_STATE_FETCH_HEIGHT_EXPONENT);
    return lerp(1.0, physicalWeight, saturate(_SeaStateFetchParams.x));
}

#endif
