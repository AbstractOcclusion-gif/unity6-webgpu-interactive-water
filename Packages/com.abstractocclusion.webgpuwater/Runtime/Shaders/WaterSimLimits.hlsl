// WebGpuWater - the simulated field's hard bounds, in ONE place.
//
// WaterSim.compute's Sanitize() clamps every texel to these and resets any non-finite one to flat.
// The surface's headroom debug view (WaterSurfaceDebug.hlsl) has to test against the SAME numbers:
// a view that draws the clamp somewhere other than where it actually fires would be worse than no
// view at all, and that is precisely the hunt these bounds show up in.
//
// Dialect-free on purpose (plain static consts, nothing else), so the compute pass and the UnityCG
// surface shader can both include it - the same rule WaterDebugMode.hlsl follows.
#ifndef WEBGPUWATER_SIM_LIMITS_INCLUDED
#define WEBGPUWATER_SIM_LIMITS_INCLUDED

// POOL units (world metres / extent.y). The explicit wave integrator is only conditionally stable,
// so a violent enough disturbance can overshoot and diverge to Inf/NaN; clamping height and
// velocity keeps the field bounded, and resetting a non-finite texel stops one bad value from being
// averaged into the conserved mean and poisoning the whole surface (on WebGPU that made the plane
// and the floating objects vanish). Bounds sit far above normal ripple amplitude.
static const float WATER_SIM_MAX_ABS_HEIGHT   = 1.0;
static const float WATER_SIM_MAX_ABS_VELOCITY = 0.5;

#endif // WEBGPUWATER_SIM_LIMITS_INCLUDED
