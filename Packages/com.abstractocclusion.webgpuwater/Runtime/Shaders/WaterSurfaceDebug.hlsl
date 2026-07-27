// WebGpuWater - surface false-colour debug view (WaterSurfaceDebug.hlsl).
// Answers, per pixel and from inside the shader itself, the questions that screenshots of the final
// image cannot: which reflection path this fragment was allowed to take, WHICH RENDERER owns it
// (base sheet / near-field patch / which clipmap ring), and what the planar mirror is actually
// being sampled at. Reading the C# gates is not the same as reading what the GPU received - a
// renderer that never gets a MaterialPropertyBlock silently falls back to the MATERIAL ASSET's
// values, and that difference is invisible in a beauty shot.
//
// Driven by the _WaterDebugMode global (WaterDebugView.cs). 0 = off, and every consumer is behind
// a UNIFORM branch, so a shipped build with the component absent pays one comparison per pixel.
// Both references ship an equivalent (Crest _DEBUG_VISUALIZE_MASK, KWS its debug modes).
#ifndef WATER_SURFACE_DEBUG_INCLUDED
#define WATER_SURFACE_DEBUG_INCLUDED

// _WaterDebugMode and every WATER_DEBUG_* ordinal live in WaterDebugMode.hlsl: the fullscreen fog
// ships its own views off the same selector, and two private copies of the list would be two
// places for it to drift from WaterDebugView.Mode.
#include "WaterDebugMode.hlsl"

// Summed-RGB below which a mirror texel counts as "nothing was rendered here" (view 6). Low, so a
// genuinely dark reflection is not mistaken for an empty one.
#define MIRROR_EMPTY_THRESHOLD 0.02

// Distinct hue per renderer so overlapping sheets are obvious: coincident draws that should be
// resolved to ONE owner show as two colours interleaved across the same water.
float3 WaterDebugRendererColor()
{
    // The base sheet and the near-field patch carry no clipmap flag; each clipmap ring carries a
    // distinct _PatchDepthBias (WaterVolume.OceanClipmap.cs derives it per level), so the bias
    // doubles as a level id without any new uniform.
    if (_IsClipmap < 0.5) return (_IsPatch > 0.5) ? float3(0.0, 0.6, 1.0)   // near-field patch
                                                  : float3(0.35, 0.35, 0.35); // base sheet
    float level = saturate(_PatchDepthBias * 4.0);
    return float3(level, 1.0 - level, 0.5 * frac(_PatchDepthBias * 16.0));
}

// Returns true when the debug view owns this pixel; 'color' is then the final output.
bool WaterDebugColor(float4 screenPos, float3 normalWS, out float3 color)
{
    color = float3(0.0, 0.0, 0.0);
    if (_WaterDebugMode < 0.5) return false;

    int mode = (int)(_WaterDebugMode + 0.5);
    // Fog ordinals belong to the fullscreen pass (WaterFogDebug.hlsl), which replaces the whole
    // frame - the surface must leave those pixels alone or both would paint the same mode.
    if (mode >= WATER_DEBUG_FOG_FIRST) return false;
    if (mode == WATER_DEBUG_REFLECTION_GATE)
    {
        // RED = SSR on, GREEN = planar on, BLUE = real refraction on - AS THE SHADER READS THEM.
        // Any water that stays red after unticking SSR is a renderer missing its property block.
        color = float3(_UseSSR, _UsePlanar, _RealRefraction);
        return true;
    }
    if (mode == WATER_DEBUG_RENDERER_ID)
    {
        color = WaterDebugRendererColor();
        return true;
    }
    if (mode == WATER_DEBUG_PLANAR_UV)
    {
        // The exact UV the planar mirror is sampled at. A band or a discontinuity here IS the
        // artifact; smooth means the sampler is innocent and the cause is upstream.
        float2 uv = ScreenUV(screenPos);
        uv += mul((float3x3)UNITY_MATRIX_V, normalWS).xy * _ReflectionDistortion;
        color = float3(frac(uv * 8.0), 0.0); // x8 so a sub-percent shift is still visible
        return true;
    }
    if (mode == WATER_DEBUG_VIEW_NORMAL)
    {
        color = mul((float3x3)UNITY_MATRIX_V, normalWS) * 0.5 + 0.5;
        return true;
    }
    if (mode == WATER_DEBUG_RAW_MIRROR)
    {
        // The mirror RT itself, undecorated: no wave nudge, no roughness mip, no aniso smear, no
        // parallax. Whatever is wrong here was rendered wrong by PlanarMirror.cs and no amount of
        // sampler work can repair it. Read it as an image: the scene should appear upside-down,
        // filling the frame, with the horizon at the same screen height as the real one.
        color = tex2Dlod(_PlanarReflectionTex, float4(ScreenUV(screenPos), 0.0, 0.0)).rgb;
        return true;
    }
    if (mode == WATER_DEBUG_MIRROR_EMPTY)
    {
        // MAGENTA wherever the mirror holds (near) nothing - the reflection camera rendered no
        // geometry and no sky there. Large magenta regions mean the RT is the problem: wrong
        // frustum, wrong oblique clip plane, or culling that removed the scene.
        float3 mirror = tex2Dlod(_PlanarReflectionTex, float4(ScreenUV(screenPos), 0.0, 0.0)).rgb;
        color = (dot(mirror, float3(1.0, 1.0, 1.0)) < MIRROR_EMPTY_THRESHOLD)
              ? float3(1.0, 0.0, 1.0) : mirror;
        return true;
    }
    return false;
}

#endif // WATER_SURFACE_DEBUG_INCLUDED
