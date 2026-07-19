// WebGL Water - shared ray-tracing helpers (Unity 6 / URP port)
// Faithful translation of helperFunctions from Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_COMMON_INCLUDED
#define WEBGL_WATER_COMMON_INCLUDED

#include "WaterShared.hlsl" // IOR_*, POOL_*, IntersectCube, ProjectCausticUV, rim consts

// Floor for the pool ambient-occlusion divide, so a point at the pool centre (length(p) -> 0)
// can't drive the result to Inf.
#define POOL_AO_MIN_DIST 1e-4

// Global uniforms (set from C# via Shader.SetGlobalX)
sampler2D   _WaterTex;     // (height, velocity, normal.x, normal.z)
sampler2D   _CausticTex;   // (caustic intensity, rim shadow, -, -)
sampler2D   _Tiles;        // pool wall/floor albedo (REPEAT)
samplerCUBE _Sky;          // sky cubemap

float3 _LightDir;          // normalized direction toward the light
float4 _WaterTexel;        // (1/width, 1/height, width, height) of _WaterTex, pushed from C#
// 1 when the caustic occluder pass drew submerged objects into the green channel this frame, so the
// pool/receiver source the underwater object shadow from that refracted term; 0 = legacy shadow-map path.
float _CausticOccluderActive;

// Manual bilinear sample of the float sim texture. WebGPU does NOT hardware-filter
// RGBA32Float, so a Bilinear sampler silently point-samples there and the normal field
// (and the vertex height) reads blocky -> micro-perturbations on the surface that don't
// appear on desktop. Filtering the four texels ourselves keeps the water smooth on every
// backend while the sim stays full 32-bit. tex2Dlod so it is valid in the vertex stage too.
float4 SampleWaterBilinear(float2 uv)
{
    float2 texel = _WaterTexel.xy;
    float2 st = uv * _WaterTexel.zw - 0.5;
    float2 f = frac(st);
    float2 baseUV = (floor(st) + 0.5) * texel;
    float4 c00 = tex2Dlod(_WaterTex, float4(baseUV, 0, 0));
    float4 c10 = tex2Dlod(_WaterTex, float4(baseUV + float2(texel.x, 0.0), 0, 0));
    float4 c01 = tex2Dlod(_WaterTex, float4(baseUV + float2(0.0, texel.y), 0, 0));
    float4 c11 = tex2Dlod(_WaterTex, float4(baseUV + texel, 0, 0));
    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

// Cubic B-spline weights for one axis (fractional position v in [0,1]). B-spline rather than
// Catmull-Rom because it SMOOTHS the field (its taps are all positive and blur slightly), which is
// what turns the faceted per-texel ripple steps into gentle swells.
float4 CubicBSplineWeights(float v)
{
    float4 n = float4(1.0, 2.0, 3.0, 4.0) - v;
    float4 s = n * n * n;
    float x = s.x;
    float y = s.y - 4.0 * s.x;
    float z = s.z - 4.0 * s.y + 6.0 * s.x;
    float w = 6.0 - x - y - z;
    return float4(x, y, z, w) * (1.0 / 6.0);
}

// Bicubic B-spline sample of the sim state, assembled from four SampleWaterBilinear taps (the classic
// fast-bicubic decomposition). WebGPU can't hardware-filter the RGBAFloat sim texture, so this reuses
// the manual bilinear instead of a hardware sampler. Smooths ripple height + normal so a coarse grid
// reads as soft swells rather than the faceted steps a plain bilinear leaves.
float4 SampleWaterBicubic(float2 uv)
{
    float2 texSize = _WaterTexel.zw;
    float2 invTexSize = _WaterTexel.xy;

    float2 coord = uv * texSize - 0.5;
    float2 fxy = frac(coord);
    coord -= fxy;

    float4 xWeights = CubicBSplineWeights(fxy.x);
    float4 yWeights = CubicBSplineWeights(fxy.y);

    float4 c = coord.xxyy + float4(-0.5, 1.5, -0.5, 1.5);
    float4 s = float4(xWeights.xz + xWeights.yw, yWeights.xz + yWeights.yw);
    float4 offset = c + float4(xWeights.yw, yWeights.yw) / s;
    offset *= invTexSize.xxyy;

    float4 s0 = SampleWaterBilinear(offset.xz);
    float4 s1 = SampleWaterBilinear(offset.yz);
    float4 s2 = SampleWaterBilinear(offset.xw);
    float4 s3 = SampleWaterBilinear(offset.yw);

    float sx = s.x / (s.x + s.y);
    float sy = s.z / (s.z + s.w);
    return lerp(lerp(s3, s2, sx), lerp(s1, s0, sx), sy);
}

// Pick the pool face a POOL-space point lies on: the tile UV, the flat face normal, and a
// tangent frame that matches the UV axes (for optional normal mapping). Shared by GetWallColor
// (the analytic look) and the AnalyticPool geometry pass (which supplies its own texture/normal).
void WallSurface(float3 p, out float2 uv, out float3 normal, out float3 tangent, out float3 bitangent)
{
    if (abs(p.x) > 0.999)
    {
        uv = p.yz * 0.5 + float2(1.0, 0.5);
        normal = float3(-p.x, 0.0, 0.0);
        tangent = float3(0.0, 1.0, 0.0);   // U = pool Y
        bitangent = float3(0.0, 0.0, 1.0); // V = pool Z
    }
    else if (abs(p.z) > 0.999)
    {
        uv = p.yx * 0.5 + float2(1.0, 0.5);
        normal = float3(0.0, 0.0, -p.z);
        tangent = float3(0.0, 1.0, 0.0);   // U = pool Y
        bitangent = float3(1.0, 0.0, 0.0); // V = pool X
    }
    else
    {
        uv = p.xz * 0.5 + 0.5;
        normal = float3(0.0, 1.0, 0.0);
        tangent = float3(1.0, 0.0, 0.0);   // U = pool X
        bitangent = float3(0.0, 0.0, 1.0); // V = pool Z
    }
}

// The pool wall SHADING scalar (no albedo): pool ambient occlusion, refracted-sun diffuse for
// the supplied normal, projected caustics below the waterline and the rim shadow above it. Split
// out so the AnalyticPool geometry pass can reuse it with a normal-mapped normal and its own albedo,
// while GetWallColor keeps the original analytic behaviour byte-for-byte.

// Strength the projected caustics are baked at in the legacy analytic path (GetWallColor). AnalyticPool
// overrides this with its own material Caustic Strength; keeping the legacy value here as a named
// constant preserves GetWallColor's result exactly.
#define WALL_CAUSTIC_LEGACY_STRENGTH 2.0

// Base wall shade (pool AO + refracted diffuse + above-water rim shadow) WITHOUT caustics, plus the
// separated caustic term via 'causticTerm' (0 above the waterline). A geometry pass can apply its
// own strength/tint to the caustic like WaterReceiver, while GetWallShade below re-bakes it at the
// legacy strength so the analytic GetWallColor path is unchanged.
float GetWallShadeSplit(float3 p, float3 normal, out float causticTerm)
{
    causticTerm = 0.0;
    float scale = 0.5;
    scale /= max(length(p), POOL_AO_MIN_DIST);                                 // pool ambient occlusion

    float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float diffuse = max(0.0, dot(refractedLight, normal));
    // Manual bilinear (not tex2D): WebGPU point-samples float32 textures, which turned
    // the above/below-waterline cut into a blocky stair-step in builds.
    float4 info = SampleWaterBilinear(p.xz * 0.5 + 0.5);
    if (p.y < info.r)
    {
        float4 caustic = tex2D(_CausticTex, ProjectCausticUV(p, refractedLight));
        // Caller scales this (diffuse * focus * rim-occluder) by its own caustic strength.
        causticTerm = diffuse * caustic.r * caustic.g;
    }
    else
    {
        // shadow for the rim of the pool
        float2 t = IntersectCube(p, refractedLight, POOL_BOX_MIN, POOL_BOX_MAX);
        diffuse *= 1.0 / (1.0 + exp(-RIM_SHADOW_SHARPNESS / (1.0 + RIM_SHADOW_SPREAD * (t.y - t.x)) * (p.y + refractedLight.y * t.y - POOL_RIM_HEIGHT)));
        scale += diffuse * 0.5;
    }
    return scale;
}

// Full analytic wall shade with caustics baked at the legacy strength. Kept so GetWallColor (and
// its WaterSurface underwater-reflection use) render exactly as before.
float GetWallShade(float3 p, float3 normal)
{
    float causticTerm;
    float scale = GetWallShadeSplit(p, normal, causticTerm);
    return scale + causticTerm * WALL_CAUSTIC_LEGACY_STRENGTH;
}

// Analytic wall colour with the projected caustic gated by an externally supplied main-light shadow
// term (0 = fully shadowed -> no caustic, 1 = lit -> legacy look). The caller (WaterSurface) samples
// the shadow at the floor's world position; a caustic is refracted sun, so it must vanish under a
// caster like the geometry paths (AnalyticPool / WaterReceiver) already do.
float3 GetWallColorShadowed(float3 p, float causticShadow)
{
    float2 uv; float3 normal, tangent, bitangent;
    WallSurface(p, uv, normal, tangent, bitangent);
    float causticTerm;
    float scale = GetWallShadeSplit(p, normal, causticTerm);
    float shade = scale + causticTerm * WALL_CAUSTIC_LEGACY_STRENGTH * causticShadow;
    return tex2D(_Tiles, uv).rgb * shade;
}

// Unshadowed analytic wall colour (causticShadow = 1): byte-identical to the legacy path.
float3 GetWallColor(float3 p) { return GetWallColorShadowed(p, 1.0); }

#endif // WEBGL_WATER_COMMON_INCLUDED
