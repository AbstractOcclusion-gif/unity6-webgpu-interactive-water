// WebGpuWater - the SIM foam mask: one read, one coverage formula, shared by every consumer.
//
// WHY THIS FILE EXISTS: every consumer of "how much sim foam covers this spot" - the surface's
// above/underside foam layers and the after-fog PondFoamOverlay pass - reads THIS one formula,
// after per-consumer copies drifted (bilinear vs point sampling, border term present vs absent).
// The fullscreen underwater fog no longer reads it at all: its mask-based foam exemption could
// never match the DRAWN foam (mask x pattern texture) and was replaced by the overlay pass,
// which re-draws the real foam AFTER the fog (WaterSurface's PondFoamOverlay).
//
// INCLUDE CONTRACT - this header does NOT include its dependencies, matching the style of the
// rest of Runtime/Shaders (flat globals, include order owned by the .shader):
//   * WaterVolume.hlsl must precede it: _SimWindowed, _SimEdgeFadeTexels.
//   * _WaterTexel must be in scope. WaterCommon.hlsl declares it for the surface/receiver
//     shaders; the fallback declaration below covers any consumer without WaterCommon. Keep
//     this include AFTER WaterCommon.hlsl wherever both are present.
#ifndef WEBGPUWATER_FOAM_MASK_INCLUDED
#define WEBGPUWATER_FOAM_MASK_INCLUDED

#ifndef WEBGL_WATER_COMMON_INCLUDED
float4 _WaterTexel;        // (1/width, 1/height, width, height) of _WaterTex, pushed from C#
#endif

// Sim foam buffer + the two globals its coverage formula needs. Published per body through the
// uniform sink, and mirrored to globals by the primary body - which is what the fullscreen fog
// pass reads (WaterUniformPublisher.WriteBodyUniforms).
sampler2D _FoamMask;
float _FoamEnabled, _FoamStrength, _FoamBorderWidth;

// Manual bilinear sample of the float foam mask - same fix as SampleWaterBilinear:
// WebGPU cannot hardware-filter float32, so a plain tex2D point-samples there and
// the foam edges go blocky in builds only. The foam RT matches the sim resolution,
// so _WaterTexel applies. tex2Dlod keeps it valid in any control flow.
float SampleFoamMaskBilinear(float2 uv)
{
    float2 texel = _WaterTexel.xy;
    float2 st = uv * _WaterTexel.zw - 0.5;
    float2 f = frac(st);
    float2 baseUV = (floor(st) + 0.5) * texel;
    float c00 = tex2Dlod(_FoamMask, float4(baseUV, 0, 0)).r;
    float c10 = tex2Dlod(_FoamMask, float4(baseUV + float2(texel.x, 0.0), 0, 0)).r;
    float c01 = tex2Dlod(_FoamMask, float4(baseUV + float2(0.0, texel.y), 0, 0)).r;
    float c11 = tex2Dlod(_FoamMask, float4(baseUV + texel, 0, 0)).r;
    return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

// Window-edge fade for foam-mask reads (mirrors SampleRipple's out-of-window guard).
// The foam RT clamps, so an unguarded windowed read past the border repeats the edge
// texels - any foam at the window edge smears into horizon-length bands (visible as
// vertical/horizontal streaks at distance). The sim deliberately does NOT fade its
// own edges (WaterSim.compute: "Edge softening is a render-side concern"), so the
// render-side read owns the border: 0 at/beyond the window edge, ramping in over the
// same _SimEdgeFadeTexels band the ripple fade uses. Whole-body bodies
// (_SimWindowed = 0) return 1, keeping the bounded-pool path byte-identical.
float FoamWindowFade(float2 uv)
{
    if (_SimWindowed < 0.5) return 1.0;
    if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
    float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
    float2 edgeDist = min(uv, 1.0 - uv);
    return saturate(min(edgeDist.x, edgeDist.y) / max(band, 1e-5));
}

// Foam-mask read for every pool/window foam coord: the bilinear sample scaled by the
// window fade. The early return skips the four mask taps everywhere beyond the
// window. tex2Dlod-based, so (like SampleFoamMaskBilinear) it stays valid in any
// control flow.
float SampleFoamMaskWindowed(float2 uv)
{
    float fade = FoamWindowFade(uv);
    if (fade <= 0.0) return 0.0;
    return SampleFoamMaskBilinear(uv) * fade;
}

// THE coverage formula - 0 = open water, 1 = fully foamed. Every consumer goes through here.
//   poolXZ   pool-space xz in [-1,1]; only the wall-border term uses it.
//   simUV    the advection lookup, already mapped by the caller (pool xz for a bounded body,
//            WorldToSim for a scrolling window - and the two surface call sites deliberately
//            map DIFFERENT world points, which is why this is a parameter and not derived).
//   extraAdd caller-side additive coverage before the strength scale. The surface's above-water
//            path passes its contact foam here; everything else passes 0.
// The wall border is whole-body only - a scrolling window has no walls to foam against.
float SimFoamCoverage(float2 poolXZ, float2 simUV, float extraAdd)
{
    float advected = SampleFoamMaskWindowed(simUV);
    float edge = min(1.0 - abs(poolXZ.x), 1.0 - abs(poolXZ.y));
    float border = (_SimWindowed < 0.5) ? (1.0 - smoothstep(0.0, _FoamBorderWidth, edge)) : 0.0;
    return saturate((advected + border + extraAdd) * _FoamStrength);
}

#endif // WEBGPUWATER_FOAM_MASK_INCLUDED
