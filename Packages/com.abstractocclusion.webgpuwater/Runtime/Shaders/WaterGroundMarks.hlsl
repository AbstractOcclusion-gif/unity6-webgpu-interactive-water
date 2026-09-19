// WebGpuWater - reading the GROUND MARKS field (footprints / keel grooves) from a surface shader.
//
// The one place the field's layout is decoded; WaterTerrain (and any future ground shader)
// includes this and never touches the texture directly, so the window mapping, the edge fade and
// the float32 filtering rule cannot drift between readers - the exact failure WaterFoamMask.hlsl
// exists to prevent for the foam buffer.
//
// FIELD: R = depth 0..1, G = freshness 0..1 (WaterGroundMarks.compute has the full contract).
//
// INCLUDE CONTRACT - no includes; flat globals published by WaterGroundMarks.cs. Uses the URP
// LOAD_TEXTURE2D_LOD macro, so include after Core.hlsl. Loads, not samples: rg32float cannot be
// hardware-filtered on WebGPU, and a Load costs no sampler unit on a shader that is already
// counting them (WaterTerrain's sampler-budget note).
#ifndef WEBGPUWATER_GROUND_MARKS_INCLUDED
#define WEBGPUWATER_GROUND_MARKS_INCLUDED

TEXTURE2D(_GroundMarkTex);
// xy = world XZ of the window's min corner, z = 1 / window size (m), w = window size (m).
float4 _GroundMarkWindow;
// (1/res, 1/res, res, res) - same shape as _WaterTexel.
float4 _GroundMarkTexel;
// 1 while the component is enabled AND the field may hold marks. 0 = skip the whole block.
float  _GroundMarkActive;

// Texels over which a read fades to nothing at the window border, so a print never ends in a
// hard line where the window does (mirrors _SimEdgeFadeTexels on the ripple sim).
#define GROUND_MARK_EDGE_FADE_TEXELS 4.0

float2 GroundMarkUV(float2 worldXZ)
{
    return (worldXZ - _GroundMarkWindow.xy) * _GroundMarkWindow.z;
}

// 0 outside the window, 1 inside, smooth over the edge-fade band.
float GroundMarkWindowWeight(float2 uv)
{
    float2 texelsIn = min(uv, 1.0 - uv) * _GroundMarkTexel.zw;
    float e = min(texelsIn.x, texelsIn.y) / GROUND_MARK_EDGE_FADE_TEXELS;
    return saturate(e);
}

// Manual bilinear read (float32 rule). Returns (depth, fresh), already window-weighted, so a
// consumer can use it in any control flow without a second guard.
float2 SampleGroundMark(float2 uv)
{
    float w = GroundMarkWindowWeight(uv) * _GroundMarkActive;
    if (w <= 0.0) return float2(0.0, 0.0);

    float2 st = uv * _GroundMarkTexel.zw - 0.5;
    float2 f = frac(st);
    int2 b = (int2)floor(st);
    int2 maxT = (int2)_GroundMarkTexel.zw - 1;
    int2 b00 = clamp(b, 0, maxT);
    int2 b11 = clamp(b + 1, 0, maxT);
    float2 c00 = LOAD_TEXTURE2D_LOD(_GroundMarkTex, int2(b00.x, b00.y), 0).rg;
    float2 c10 = LOAD_TEXTURE2D_LOD(_GroundMarkTex, int2(b11.x, b00.y), 0).rg;
    float2 c01 = LOAD_TEXTURE2D_LOD(_GroundMarkTex, int2(b00.x, b11.y), 0).rg;
    float2 c11 = LOAD_TEXTURE2D_LOD(_GroundMarkTex, int2(b11.x, b11.y), 0).rg;
    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y) * w;
}

// Depth gradient in WORLD metres: (d depth / d worldX, d depth / d worldZ) by central differences
// one texel apart. Multiply by the substrate's print depth in metres to get a slope the shader can
// turn into a normal (batch 2). Four extra reads; the caller decides whether it needs them.
float2 GroundMarkGradient(float2 uv)
{
    float2 t = _GroundMarkTexel.xy;
    float dx = SampleGroundMark(uv + float2(t.x, 0.0)).x - SampleGroundMark(uv - float2(t.x, 0.0)).x;
    float dz = SampleGroundMark(uv + float2(0.0, t.y)).x - SampleGroundMark(uv - float2(0.0, t.y)).x;
    float texelMetres = _GroundMarkWindow.w * t.x;
    return float2(dx, dz) / (2.0 * texelMetres);
}

#endif // WEBGPUWATER_GROUND_MARKS_INCLUDED
