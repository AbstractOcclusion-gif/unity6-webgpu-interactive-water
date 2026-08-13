// WaterSurface pass: manual URP main-light shadow tap (both keyword variants).
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. Needs WaterSurfaceScreen.hlsl first
// (sampler_PointClamp); the _MAIN_LIGHT_SHADOWS* keywords stay a pass-level
// multi_compile in the .shader.
#ifndef WATER_SURFACE_SHADOW_INCLUDED
#define WATER_SURFACE_SHADOW_INCLUDED

// ---- Manual URP main-light shadow tap (this pass is CGPROGRAM and cannot include URP's
// Shadows.hlsl). Mirrors URP's cascade select + a single hard depth compare - enough to GATE
// the analytic floor caustic (a soft multiply), NOT to draw crisp shadows. Returns 1 (lit)
// when shadows are off/unsupported, so the caustic falls back to its legacy look. ----
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
// The shadow map is a DEPTH texture; URP binds a COMPARISON sampler for it. This pass does its
// own hard depth compare (below), so it needs the raw depth, not hardware comparison. Declaring
// it as a plain sampler2D made WebGPU bind URP's comparison sampler to a non-comparison slot
// (validation error -> the whole WaterSurface bind group is invalid -> black screen in builds).
// Read it as a Texture2D with an explicit NON-comparison point sampler instead
// (sampler_PointClamp, declared unconditionally with the scene depth texture above -
// the depth reads need it in every variant, not just the shadow ones).
Texture2D _MainLightShadowmapTexture;
float4x4  _MainLightWorldToShadow[5];
float4    _CascadeShadowSplitSpheres0;
float4    _CascadeShadowSplitSpheres1;
float4    _CascadeShadowSplitSpheres2;
float4    _CascadeShadowSplitSpheres3;
float4    _CascadeShadowSplitSphereRadii;
float4    _MainLightShadowParams; // x = shadow strength

float WaterMainLightShadow(float3 worldPos)
{
    // Cascade index from distance to the four split spheres (URP ComputeCascadeIndex).
    float3 f0 = worldPos - _CascadeShadowSplitSpheres0.xyz;
    float3 f1 = worldPos - _CascadeShadowSplitSpheres1.xyz;
    float3 f2 = worldPos - _CascadeShadowSplitSpheres2.xyz;
    float3 f3 = worldPos - _CascadeShadowSplitSpheres3.xyz;
    float4 d2 = float4(dot(f0, f0), dot(f1, f1), dot(f2, f2), dot(f3, f3));
    float4 w  = float4(d2 < _CascadeShadowSplitSphereRadii);
    w.yzw = saturate(w.yzw - w.xyz);
    int cascade = min(3, (int)(4.0 - dot(w, float4(4.0, 3.0, 2.0, 1.0))));

    float4 c = mul(_MainLightWorldToShadow[cascade], float4(worldPos, 1.0));
    c.xyz /= c.w;
    if (c.z <= 0.0 || c.z >= 1.0) return 1.0; // outside the atlas -> treat as lit

    float occluder = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, c.xy, 0.0).r;
    // In shadow when the fragment is FARTHER from the light than the stored occluder.
#if defined(UNITY_REVERSED_Z)
    float lit = c.z < occluder ? 0.0 : 1.0;
#else
    float lit = c.z > occluder ? 0.0 : 1.0;
#endif
    return lerp(1.0, lit, _MainLightShadowParams.x); // fold in shadow strength
}
#else
float WaterMainLightShadow(float3 worldPos) { return 1.0; }
#endif

#endif // WATER_SURFACE_SHADOW_INCLUDED
