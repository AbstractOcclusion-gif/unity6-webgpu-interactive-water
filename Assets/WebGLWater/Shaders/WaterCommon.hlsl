// WebGL Water - shared ray-tracing helpers (Unity 6 / URP port)
// Faithful translation of helperFunctions from Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_COMMON_INCLUDED
#define WEBGL_WATER_COMMON_INCLUDED

#include "WaterShared.hlsl" // IOR_*, POOL_*, IntersectCube, ProjectCausticUV, rim consts

static const float3 ABOVEWATER_COLOR = float3(0.25, 1.0, 1.25);
static const float3 UNDERWATER_COLOR = float3(0.4, 0.9, 1.0);

// Global uniforms (set from C# via Shader.SetGlobalX)
sampler2D   _WaterTex;     // (height, velocity, normal.x, normal.z)
sampler2D   _CausticTex;   // (caustic intensity, rim shadow, -, -)
sampler2D   _Tiles;        // pool wall/floor albedo (REPEAT)
samplerCUBE _Sky;          // sky cubemap

float3 _LightDir;          // normalized direction toward the light
float3 _Eye;               // camera world position

float3 GetWallColor(float3 p)
{
    float scale = 0.5;

    float3 wallColor;
    float3 normal;
    if (abs(p.x) > 0.999)
    {
        wallColor = tex2D(_Tiles, p.yz * 0.5 + float2(1.0, 0.5)).rgb;
        normal = float3(-p.x, 0.0, 0.0);
    }
    else if (abs(p.z) > 0.999)
    {
        wallColor = tex2D(_Tiles, p.yx * 0.5 + float2(1.0, 0.5)).rgb;
        normal = float3(0.0, 0.0, -p.z);
    }
    else
    {
        wallColor = tex2D(_Tiles, p.xz * 0.5 + 0.5).rgb;
        normal = float3(0.0, 1.0, 0.0);
    }

    scale /= length(p);                                                        // pool ambient occlusion

    float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float diffuse = max(0.0, dot(refractedLight, normal));
    float4 info = tex2D(_WaterTex, p.xz * 0.5 + 0.5);
    if (p.y < info.r)
    {
        float4 caustic = tex2D(_CausticTex, ProjectCausticUV(p, refractedLight));
        scale += diffuse * caustic.r * 2.0 * caustic.g;
    }
    else
    {
        // shadow for the rim of the pool
        float2 t = IntersectCube(p, refractedLight, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
        diffuse *= 1.0 / (1.0 + exp(-RIM_SHADOW_SHARPNESS / (1.0 + RIM_SHADOW_SPREAD * (t.y - t.x)) * (p.y + refractedLight.y * t.y - POOL_RIM_HEIGHT)));
        scale += diffuse * 0.5;
    }

    return wallColor * scale;
}

#endif // WEBGL_WATER_COMMON_INCLUDED
