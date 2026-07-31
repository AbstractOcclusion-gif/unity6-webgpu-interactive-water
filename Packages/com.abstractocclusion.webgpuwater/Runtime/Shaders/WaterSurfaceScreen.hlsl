// WaterSurface pass: URP screen-texture access (opaque colour + raw scene depth)
// and the shared screen-space helpers (ScreenUV, EyeDepthOf).
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. First of the WaterSurface* includes: the shadow
// tap and the SSR march need sampler_PointClamp / RawSceneDepth from here.
#ifndef WATER_SURFACE_SCREEN_INCLUDED
#define WATER_SURFACE_SCREEN_INCLUDED

// URP scene textures (enable Opaque Texture + Depth Texture in the URP asset).
// UNITY_DECLARE_TEX2D, not sampler2D (2026-07-31): ps_4_0 caps sampler registers at 16 and
// this pass sits EXACTLY at the cap (the comment below already records the last combined slot
// going to the detail normal). The macro family keeps the opaque texture's one sampler
// NAMEABLE (sampler_CameraOpaqueTexture), so the god-ray shaft history the underside mirror
// samples (_LargeGodRayLastFrame, WaterSurfaceSpecular.hlsl) can BORROW it via
// UNITY_SAMPLE_TEX2D_SAMPLER instead of costing a 17th register - which is exactly how the
// compile failed when it was declared sampler2D. The sampler state is the texture's own,
// identical to what tex2D/tex2Dlod resolved to; every call site swapped to the matching
// UNITY_SAMPLE_TEX2D / _LOD macro in the same change (all in this TU).
UNITY_DECLARE_TEX2D(_CameraOpaqueTexture);
// Depth as a separate Texture2D + the shared point sampler, NOT a sampler2D: depth
// must be point-sampled anyway (filtering depth values is meaningless), and ps_4_0
// caps sampler registers at 16 - the detail-normal texture took the last combined
// slot, so depth shares the inline point sampler instead of owning a register.
// (Same Texture2D + explicit-sampler pattern as the WebGPU-safe shadow tap below.)
Texture2D _CameraDepthTexture;
SamplerState sampler_PointClamp; // Unity inline sampler: point filter, clamp wrap (non-comparison)
// Every read is explicit-LOD (loop-safe, WGSL-safe): LinearEyeDepth(RawSceneDepth(uv)).
float RawSceneDepth(float2 uv)
{
    return _CameraDepthTexture.SampleLevel(sampler_PointClamp, uv, 0.0).r;
}

// Perspective divide of a ComputeScreenPos-style position -> [0,1] screen UV.
// ONE helper for every screen-space consumer (SSR march, planar mirror,
// refraction, contact foam) so the w-guard can never drift between them.
#define SCREEN_UV_MIN_W 1e-5   // guards the divide at/behind the camera plane
float2 ScreenUV(float4 screenPos)
{
    return screenPos.xy / max(screenPos.w, SCREEN_UV_MIN_W);
}

// Positive view-space (eye) depth of a world point (view forward is -Z, so the
// negation yields metres in front of the camera). ONE helper for the SSR march
// and the refraction/contact-foam thickness tests, so the sign convention can
// never drift between them.
float EyeDepthOf(float3 worldPos)
{
    return -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z;
}

#endif // WATER_SURFACE_SCREEN_INCLUDED
