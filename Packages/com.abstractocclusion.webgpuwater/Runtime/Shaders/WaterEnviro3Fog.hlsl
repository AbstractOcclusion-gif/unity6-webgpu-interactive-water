// WebGpuWater - Enviro 3 scene fog, PORTED (not included) from Enviro's FogInclude/SkyInclude.
//
// WHY A PORT: Enviro's own include drags SkyInclude + VolumetricCloudsBlendInclude behind it,
// which declare 8 combined samplers (clouds, cirrus, aurora...) of which the fog path uses ONE.
// WaterSurface Pass 0 already sits at 14 of the 16 ps_4_0 sampler registers, so the include
// overflows it ("maximum ps_4_0 sampler register index (16) exceeded"). This file re-states the
// fog maths against the globals Enviro publishes every frame, declares no textures or samplers,
// and serves the CG and the HLSL passes alike.
//
// PINNED TO: Enviro 3.3.2b FogInclude.cginc / SkyInclude.cginc. The Water Wizard hashes those
// two files and warns when they change, so a formula update upstream is re-checked here rather
// than discovered visually. Deliberate simplifications vs upstream:
//  * ENVIRO_SIMPLEFOG is not a variant: the two-term formula with _EnviroFogParameters2 left
//    at zero (what simple mode publishes) collapses to the one-term one. Edge: switching from
//    advanced to simple fog at runtime leaves stale Parameters2 in the global until a reload.
//  * ENVIRO_SIMPLESKY is not a variant: the full gradient ramp is always used for the fog tint
//    (simple mode only cheapens that ramp; the difference is a slight hue at the horizon).
//  * Effect Removal Zones are not applied (structured buffers; Enviro itself disables them
//    outside D3D11/Metal/Vulkan, so WebGPU never had them).
//  * Enviro volumetric-light shafts are intentionally omitted because WaterSurface has no free
//    D3D11 texture register; Enviro height/distance fog itself is fully matched.
#ifndef WATER_ENVIRO3_FOG_INCLUDED
#define WATER_ENVIRO3_FOG_INCLUDED

// ---- Enviro globals (published by EnviroManager / EnviroFogModule / EnviroSkyModule) ----
uniform float  _EnviroActive;
uniform float4 _EnviroFogParameters;  // x = ray origin, y = falloff, z = density, w = height (term 1)
uniform float4 _EnviroFogParameters2; // same for term 2 (zero in simple-fog mode)
uniform float4 _EnviroFogParameters3; // x = min opacity (1 - max), y = start distance, z = block scattering, w = sky blend
uniform float4 _EnviroFogColor;
uniform float4 _EnviroDirLightColor;
uniform float3 _EnviroCameraPos;
uniform float3 _EnviroWorldOffset;
// Sky ramp (Enviro's, NOT the water's _WaterSunColor)
uniform float4 _SunDir;
uniform float4 _SunColor;
uniform half4  _FrontColor0, _FrontColor1, _FrontColor2, _FrontColor3, _FrontColor4, _FrontColor5;
uniform half4  _BackColor0, _BackColor1, _BackColor2, _BackColor3, _BackColor4, _BackColor5;
uniform float4 _SkyColorTint;
uniform float  _frontBackDistribution0, _frontBackDistribution1, _frontBackDistribution2, _frontBackDistribution3;
uniform float  _Intensity;
uniform float  _SkyColorExponent;
uniform float  _MieScatteringIntensity;

float WaterEnviroMie(float costh, float g)
{
    g = min(g, 0.9381);
    float k = 1.55 * g - 0.55 * g * g * g;
    float kcosth = k * costh;
    return (1.0 - k * k) / ((4.0 * 3.14159265) * (1.0 - kcosth) * (1.0 - kcosth));
}

float WaterEnviroRemap(float v, float lo, float hi)
{
    return saturate((v - lo) / (hi - lo));
}

// SkyInclude.cginc GetSkyColor: gradient ramp + Mie glow. Pure ALU.
float3 WaterEnviroSkyColor(float3 viewDir, float depth)
{
    float cosTheta = smoothstep(0.0, 2.0, saturate(dot(-_SunDir.xyz, viewDir)));
    float3 fb0 = lerp(max(_FrontColor0.rgb, 0), max(_BackColor0.rgb, 0), cosTheta);
    float3 fb1 = lerp(max(_FrontColor1.rgb, 0), max(_BackColor1.rgb, 0), cosTheta);
    float3 fb2 = lerp(max(_FrontColor2.rgb, 0), max(_BackColor2.rgb, 0), cosTheta);
    float3 fb3 = lerp(max(_FrontColor3.rgb, 0), max(_BackColor3.rgb, 0), cosTheta);
    float3 fb4 = lerp(max(_FrontColor4.rgb, 0), max(_BackColor4.rgb, 0), cosTheta);
    float3 fb5 = lerp(max(_FrontColor5.rgb, 0), max(_BackColor5.rgb, 0), cosTheta);
    float h1 = WaterEnviroRemap(viewDir.y, -0.75, _frontBackDistribution0);
    float h2 = WaterEnviroRemap(viewDir.y, _frontBackDistribution0, _frontBackDistribution1);
    float h3 = WaterEnviroRemap(viewDir.y, _frontBackDistribution1, _frontBackDistribution2);
    float h4 = WaterEnviroRemap(viewDir.y, _frontBackDistribution2, _frontBackDistribution3);
    float h5 = WaterEnviroRemap(viewDir.y, _frontBackDistribution3, 1.0);
    float3 sky = lerp(fb0, fb1, h1);
    sky = lerp(sky, fb2, h2);
    sky = lerp(sky, fb3, h3);
    sky = lerp(sky, fb4, h4);
    sky = lerp(sky, fb5, h5);
    sky *= _Intensity;
    float eyeCos = dot(_SunDir.xyz, viewDir);
    float fade = saturate(eyeCos);
    float mie = WaterEnviroMie(eyeCos, 0.7) * _MieScatteringIntensity * fade * depth;
    sky += (mie * sky) * _SunColor.rgb;
    return pow(max(sky * _SkyColorTint.rgb, 0.0), _SkyColorExponent);
}

float WaterEnviroLineIntegral(float heightFalloff, float rayDirY, float rayOriginTerms)
{
    float falloff = heightFalloff * rayDirY;
    float lineIntegral = (1.0 - exp2(-falloff)) / falloff;
    float taylor = log(2.0) - (0.5 * log(2.0) * log(2.0)) * falloff;
    return rayOriginTerms * (abs(falloff) > 0.01 ? lineIntegral : taylor);
}

// FogInclude.cginc GetExponentialHeightFog: rgb = in-scattered fog colour, a = transmittance.
float4 WaterEnviroHeightFog(float3 worldPos, float linear01Depth)
{
    float3 wPos = worldPos - _EnviroWorldOffset;
    float minFogOpacity = _EnviroFogParameters3.x;

    float3 toReceiver = wPos - _EnviroCameraPos;
    float camHeight = min(2000.0, _EnviroCameraPos.y - _EnviroWorldOffset.y);
    float rayDirY = wPos.y - camHeight;
    float viewLength = length(toReceiver);
    float3 viewDir = toReceiver / max(viewLength, 1e-5);

    float rot1 = _EnviroFogParameters.z  * exp2(-_EnviroFogParameters.y  * (camHeight - _EnviroFogParameters.w));
    float rot2 = _EnviroFogParameters2.z * exp2(-_EnviroFogParameters2.y * (camHeight - _EnviroFogParameters2.w));
    float fogAmount = (WaterEnviroLineIntegral(_EnviroFogParameters.y,  rayDirY, rot1)
                     + WaterEnviroLineIntegral(_EnviroFogParameters2.y, rayDirY, rot2)) * viewLength;

    // Start distance: sixth-power ease-in below it.
    if (viewLength <= _EnviroFogParameters3.y)
    {
        float t = saturate(viewLength / max(_EnviroFogParameters3.y, 1e-5));
        fogAmount *= pow(t, 6.0);
    }
    fogAmount = clamp(fogAmount, 0.0, 10.0);

    float transmittance = max(exp2(-fogAmount), minFogOpacity);
    float scattering = saturate(linear01Depth + _EnviroFogParameters3.z);
    float3 sky = WaterEnviroSkyColor(viewDir, scattering);
    float3 inscatter = lerp(_EnviroFogColor.rgb, sky, _EnviroFogParameters3.w);
    return float4(inscatter * saturate(1.0 - transmittance), transmittance);
}

// Enviro height/distance fog for transparent water. 'screenUV' is retained in the contract so
// callers remain source-compatible, but is intentionally unused: WaterSurface has no free D3D11
// texture register for Enviro's volumetric-light buffer.
float3 WaterEnviroApplyFog(float3 color, float2 screenUV, float3 worldPos, float linear01Depth)
{
    if (_EnviroActive <= 0.0) return color;
    float4 fog = WaterEnviroHeightFog(worldPos, linear01Depth);
    return color * fog.a + fog.rgb;
}

#endif // WATER_ENVIRO3_FOG_INCLUDED
