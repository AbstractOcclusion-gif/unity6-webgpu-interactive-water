// WebGpuWater - shoreline substrate uniforms + sampling helpers (Layer A/B).
//
// The world-frame seabed-depth field and its jump-flood SDF are baked by WaterShoreDepthField and
// published as globals. This header is the single place that declares those uniforms and the helpers
// that read them, so the vertex wave code (shoaling), the fragment (debug), and any other shader can
// share one definition. tex2Dlod everywhere so the samplers are valid in the vertex stage.
#ifndef WEBGPUWATER_SHORE_INCLUDED
#define WEBGPUWATER_SHORE_INCLUDED

// Seabed field: R = seabed WORLD height (metres). SDF field: RG = toward-shore direction (0..1),
// B = signed distance to shore (m, + in water / - on land), A = mask. Both share one world frame.
sampler2D _ShoreDepthTex;
float4 _ShoreDepthCenter; // world XZ centre of the field (.xy)
float4 _ShoreDepthSize;   // world XZ half-extent of the field (.xy)
float _ShoreDepthValid;   // 1 = a seabed field is baked
float _ShoreDepthDebug;   // 1 = visualize seabed depth on the surface (debug only)
float _ShoreWaterLevel;   // still-water plane world Y used when the field was baked
float _ShoreShoalDepth;   // depth (m) over which waves shoal; full strength beyond it (0 = no shoaling)
sampler2D _ShoreSDFTex;   // RG = toward-shore dir (0..1), B = signed distance (m), A = mask
float _ShoreSDFValid;     // 1 = a shoreline SDF is baked
float _ShoreSDFDebug;     // 1 = visualize the SDF on the surface (debug only)

// Deep-water sentinel: a depth this large attenuates nothing (used off-field / when no field is baked).
#define SHORE_DEEP_SENTINEL 1e9

// World XZ -> shore-field UV. The field is axis-aligned in world space.
float2 ShoreFieldUV(float2 worldXZ)
{
    return (worldXZ - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
}

// Still-water column depth (metres) under a world xz: surface plane minus the seabed. Returns a deep
// sentinel where no field is baked or the point is outside the field, so those places never shoal.
float ShoreShoalDepth(float2 worldXZ)
{
    if (_ShoreDepthValid < 0.5) return SHORE_DEEP_SENTINEL;
    float2 uv = ShoreFieldUV(worldXZ);
    if (any(uv != saturate(uv))) return SHORE_DEEP_SENTINEL;
    float seabedY = tex2Dlod(_ShoreDepthTex, float4(uv, 0, 0)).r;
    return _ShoreWaterLevel - seabedY;
}

// Depth-based shoaling weight for one wave component: 0 at the waterline, ramping to 1 within the
// near-shore band. Short waves recover shallower than long ones (Crest's saturate(2*depth/L)), but the
// _ShoreShoalDepth clamp forces full strength once past that depth so ONLY the near-shore band
// attenuates - without it a long swell shoals away across a whole lake, not just at the beach. Negative
// depth (dry land) clamps to 0, so waves vanish rather than punch through the seabed. _ShoreShoalDepth
// of 0 (or unpublished) disables shoaling entirely (weight 1 everywhere).
float ShoalWeight(float depth, float wavelength)
{
    float clamped = max(depth, 0.0);
    float raw = saturate(2.0 * clamped / max(wavelength, 1e-3));
    return lerp(raw, 1.0, saturate(clamped / max(_ShoreShoalDepth, 1e-3)));
}

#endif // WEBGPUWATER_SHORE_INCLUDED
