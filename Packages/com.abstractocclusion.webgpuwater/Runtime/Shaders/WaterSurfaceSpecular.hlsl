// WaterSurface pass: the whole specular/reflection family - Fresnel + GGX
// constants, roughness ramp, sky/planar/SSR sampling with the shared vertical
// smear, and the dual-lobe sun specular.
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. Needs WaterCommon (IOR_* for FRESNEL_F0_WATER,
// _Sky, _LightDir) and WaterSurfaceScreen.hlsl (ScreenUV/RawSceneDepth/EyeDepthOf
// for the SSR march). The ANISO_TAP_* kernel MUST stay in this file with all
// three smear loops (sky, planar, SSR) that unroll over it.
#ifndef WATER_SURFACE_SPECULAR_INCLUDED
#define WATER_SURFACE_SPECULAR_INCLUDED

// Look constants local to this surface pass (single-use here).
// Legacy hard sun glint: kept ONLY for the underwater branch (TIR ceiling and
// rays through the Snell window). The above-water sun is the GGX lobe in
// SunSpecular below.
#define SUN_GLINT_TINT          float3(10.0, 8.0, 6.0)
#define SUN_GLINT_SHARPNESS     5000.0
#define UNDERWATER_REFRACT_TINT float3(0.8, 1.0, 1.1)
// Above-water Fresnel is physical Schlick (1994): F0 derived from the air/water
// IOR (~0.02), grazing exponent 5. The old artistic curve (pow 3 lerped from a
// 0.25 floor) reflected a quarter of the sky looking straight DOWN, which
// flattened the surface; _FresnelFloor (default 0) keeps that override
// available as an opt-in. The underwater branch keeps the legacy curve.
#define FRESNEL_F0_WATER        (((IOR_WATER - IOR_AIR) * (IOR_WATER - IOR_AIR)) \
                               / ((IOR_WATER + IOR_AIR) * (IOR_WATER + IOR_AIR)))
#define FRESNEL_SCHLICK_POWER   5.0
#define FRESNEL_POWER           3.0    // legacy curve - underwater branch only
#define FRESNEL_MIN_BELOW       0.5
// GGX sun specular (above water): the lobe's roughness grows with view distance
// (KWS trick, ramp knobs published per body) - far pixels average many unresolved
// wave facets, so widening the lobe is both the physically-motivated filter and the
// specular anti-aliasing; the sun's glitter path broadens toward the horizon instead
// of aliasing into sparkle dust. The clamp stops a mirror-calm lobe blowing out HDR.
#define SUN_SPEC_CLAMP              50.0   // max lobe value (multiplies the sun colour)
// Two SEPARATE guard epsilons - the two denominators live at wildly different
// scales, and sharing one epsilon was a real bug:
// - The NDF denominator at the lobe peak is pi * alpha^4 = pi * r^8, which drops
//   below 1e-5 for EVERY roughness < ~0.2 (covering the 0.08/0.2 defaults). A
//   1e-5 guard therefore CLAMPED the peak, dimming the sun core ~1900x at
//   r = 0.08 and inverting the response (peak grew with roughness instead of
//   sharpening). 1e-9 keeps r >= ~0.06 exact; below that the guard hands over
//   to SUN_SPEC_CLAMP, which is the intended HDR ceiling anyway.
// - The visibility denominator is O(NoL + NoV): order 1, only near zero at
//   pathological grazing. 1e-5 remains the right div-by-zero guard there.
#define GGX_NDF_EPSILON             1e-9
#define GGX_VISIBILITY_EPSILON      1e-5
// Anisotropic (vertically stretched) sky reflection: wave slopes tilt mostly about
// horizontal axes, so a rough water mirror smears what it reflects VERTICALLY - the
// classic elongated ocean reflection (KWS's ReflectionPreFiltering does this as an
// RT blur; here it is extra roughness-mip taps spread along world up, explicit-LOD
// so it is WGSL-safe and costs no extra sampler). Tap offsets/weights ~ gaussian.
#define SKY_ANISO_TAP_COUNT  5
#define SKY_ANISO_SPREAD_MAX 1.0    // max up-offset (ray units) at stretch 1, roughness 1
#define SKY_ANISO_MIN_SPREAD 1e-3   // below this the smear is invisible: single tap
// Screen-space variant of the same stretch, for the PLANAR mirror and the SSR hit
// colour (their vertical axis is simply the screen v axis). UV units at stretch 1,
// roughness 1.
#define SCREEN_ANISO_SPREAD_MAX 0.08
// Shared 5-tap gaussian-ish smear kernel (sky + planar + SSR reflection stretch).
static const float ANISO_TAP_OFFSETS[SKY_ANISO_TAP_COUNT] = { -1.0, -0.5, 0.0, 0.5, 1.0 };
static const float ANISO_TAP_WEIGHTS[SKY_ANISO_TAP_COUNT] = { 0.1, 0.2, 0.4, 0.2, 0.1 };

// Roughness -> sky-cube mip: Unity's perceptual remap (the UNITY_SPECCUBE_LOD_STEPS
// convention, mip = r * (1.7 - 0.7r) * steps) so the blur ramp behaves like probe
// reflections artists already know. Needs the bound cube to HAVE mips: the wizard's
// BuildSky now bakes them (regenerate the sky asset!) and imported skybox cubemaps
// have them by default; without mips the lod sample just clamps to the sharp top.
#define SKY_MIP_CURVE_SCALE 1.7
#define SKY_MIP_CURVE_BIAS  0.7
#define SKY_MIP_STEPS       6.0

// Horizon clamp for the SKY reflection ray (Crest's minimum-reflection-Y idea): a
// reflected ray that dips below the horizon - common on wave flanks at grazing
// angles, exactly where fresnel is strongest - would sample the cubemap's ground
// hemisphere and draw a dark fringe on the swell. Lift the ray's y to this minimum
// so it lands on the horizon ring instead (which is what grazing water reflects).
#define REFLECTION_MIN_UP_Y 0.02

float _ReflectionStrength;
float _FresnelFloor;  // artistic minimum reflectance on the Schlick curve (0 = physical)
float _FresnelPower;  // Schlick grazing exponent; 5 physical, lower = overall shinier
float _SunRoughness;          // perceptual roughness at the camera (near end of the ramp)
float _RoughnessFar;          // perceptual roughness at/beyond the far distance
float _RoughnessFarDistance;  // metres over which the near->far ramp runs
float _RoughnessFalloff;      // ramp curve: 1 linear, >1 stays sharp longer
float _ReflectionAnisoStretch; // vertical smear of the sky reflection (0 = off)
float _SunSheen;          // weight of the broad second sun lobe (0 = single lobe)
float _SunSheenRoughness; // breadth of that sheen lobe (its roughness floor)
float _SunGrazeBoost;     // NoL wrap for the sun lobes; 0 = physical, higher keeps
                          // the glitter alive when the sun sits at the horizon

float3 _SunColor; // Unity directional light color * intensity (global)

sampler2D _PlanarReflectionTex;
float     _ReflectionDistortion;

float _SSRStrength, _SSRStepSize, _SSRMaxSteps, _SSRThickness;

// Reflection mode flags (0/1), driven per body via the property block.
// (_UseUrpProbe has NO uniform: its Property is read by C# only - see Properties.)
float _UsePlanar, _UseSSR, _RealRefraction;

float _EnvReflectionIntensity; // brightness of the reflected sky / URP probe (not the sun glint)

// Screen-space ray march along 'dir' from world 'p0'. On a depth hit it
// returns the scene colour and sets hit=1; otherwise hit=0 (caller falls
// back to planar / analytic). Kept deliberately simple + linear; tune the
// step size / thickness in the material.
// Scene-colour fetch for the SSR hit, vertically smeared by the shared stretch.
// The opaque texture has no mip chain, so unlike the planar/sky paths roughness
// cannot pick a blur level here - the smear is the only softening (KWS blurs its
// SSR result the same way, vertically). Explicit-LOD: loop/WGSL-safe.
float3 SampleOpaqueSmeared(float2 uv, float roughness)
{
    float spread = _ReflectionAnisoStretch * roughness * SCREEN_ANISO_SPREAD_MAX;
    float3 color = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int tap = 0; tap < SKY_ANISO_TAP_COUNT; tap++)
    {
        float2 tapUV = saturate(uv + float2(0.0, spread * ANISO_TAP_OFFSETS[tap]));
        color += tex2Dlod(_CameraOpaqueTexture, float4(tapUV, 0.0, 0.0)).rgb
               * ANISO_TAP_WEIGHTS[tap];
    }
    return color;
}

float3 MarchSSR(float3 p0, float3 dir, float roughness, out float hit)
{
    hit = 0.0;
    float3 p = p0;
    int maxSteps = (int)_SSRMaxSteps;
    [loop]
    for (int s = 0; s < maxSteps; s++)
    {
        p += dir * _SSRStepSize;
        float4 clip = mul(UNITY_MATRIX_VP, float4(p, 1.0));
        if (clip.w <= 0.0) break;
        // Platform-correct screen UV (handles the WebGPU/GL vs D3D V-flip),
        // matching the refraction / planar paths that use ComputeScreenPos.
        // A hand-rolled clip.xy/clip.w*0.5+0.5 samples the mirrored row in a
        // build and makes SSR reflections look screen-locked.
        float4 sp = ComputeScreenPos(clip);
        float2 uv = ScreenUV(sp);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

        // explicit-LOD samples: safe inside a divergent loop (WebGPU)
        float sceneDepth = LinearEyeDepth(RawSceneDepth(uv));
        float rayDepth   = EyeDepthOf(p);
        if (rayDepth > sceneDepth && (rayDepth - sceneDepth) < _SSRThickness)
        {
            hit = 1.0;
            return SampleOpaqueSmeared(uv, roughness);
        }
    }
    return 0.0;
}

// Roughness -> blur mip on the perceptual UNITY_SPECCUBE_LOD_STEPS-style curve
// (see SKY_MIP_*). ONE formula for the sky cube AND the planar RT, so both blur
// ramps stay in lockstep with the shared roughness knobs.
float RoughnessToSkyMip(float roughness)
{
    return roughness * (SKY_MIP_CURVE_SCALE - SKY_MIP_CURVE_BIAS * roughness)
         * SKY_MIP_STEPS;
}

// Sample the planar reflection RT at the fragment's screen UV, nudged by the
// surface normal so ripples wobble the mirror image. Roughness picks the RT mip
// (PlanarMirror now renders with a mip chain) and the shared vertical smear
// stretches it, so the planar mirror obeys the SAME roughness knobs as the sky -
// without this a planar body ignored them entirely and stayed razor sharp.
// Explicit-LOD taps: WGSL-safe, no extra sampler.
float3 SamplePlanarReflection(float4 screenPos, float3 normal, float roughness)
{
    float2 uv = ScreenUV(screenPos);
    uv += normal.xz * _ReflectionDistortion;
    float mip = RoughnessToSkyMip(roughness);
    float spread = _ReflectionAnisoStretch * roughness * SCREEN_ANISO_SPREAD_MAX;
    float3 color = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int tap = 0; tap < SKY_ANISO_TAP_COUNT; tap++)
    {
        float2 tapUV = saturate(uv + float2(0.0, spread * ANISO_TAP_OFFSETS[tap]));
        color += tex2Dlod(_PlanarReflectionTex, float4(tapUV, 0.0, mip)).rgb
               * ANISO_TAP_WEIGHTS[tap];
    }
    return color;
}

// Legacy hard sun glint (pow-5000 disc). Underwater paths only - the above-water
// sun is the roughness-driven GGX lobe in SunSpecular, so folding this fixed disc
// into the above-water mirror would double the sun.
float3 LegacySunGlint(float3 worldRay)
{
    return SUN_GLINT_TINT * _SunColor * pow(max(0.0, dot(_LightDir, worldRay)), SUN_GLINT_SHARPNESS);
}

// Sample the SKY environment (reflection probe / procedural sky) for a WORLD-space
// ray - no sun term. This is what the water REFLECTS - never the analytic pool tiles.
// WGSL derivative uniformity: the grad variant exists for call sites inside NON-UNIFORM
// control flow (GetSurfaceRayColor's per-fragment up/down ray split), where texCUBE's
// implicit derivatives are undefined - the caller hoists ddx/ddy of the ray beforehand.
float3 SampleSkyEnvironmentGrad(float3 worldRay, float3 rayDdx, float3 rayDdy)
{
    // Reflection base is ALWAYS a plain cubemap in _Sky: the assigned Sky slot for procedural
    // sky, or the scene's skybox cubemap when Reflect URP Probe is on (WaterUniformPublisher
    // picks which). Sampling a cubemap works in EVERY render path - unlike unity_SpecCube0,
    // which URP Forward+ (used on WebGPU) does not bind per-object, so the old probe path read
    // the default/skybox and the plane showed no reflection.
    float3 color = texCUBEgrad(_Sky, worldRay, rayDdx, rayDdy).rgb;
    // Art-directed brightness of the reflected environment (sky OR probe). Applied before any
    // sun term so the sun stays a fixed specular regardless of the mirror intensity.
    color *= _EnvReflectionIntensity;
    return color;
}

// Environment WITH the legacy sun glint - the underwater branch and the shared
// ray tracer (GetSurfaceRayColor) keep this so their look is unchanged by the
// above-water GGX rework.
float3 SampleEnvironmentGrad(float3 worldRay, float3 rayDdx, float3 rayDdy)
{
    return SampleSkyEnvironmentGrad(worldRay, rayDdx, rayDdy) + LegacySunGlint(worldRay);
}

// Implicit-derivative conveniences for UNIFORM control flow (the reflection paths and
// the horizon haze, all gated on uniforms) - identical results, no hoisting needed there.
float3 SampleEnvironment(float3 worldRay)
{
    return SampleEnvironmentGrad(worldRay, ddx(worldRay), ddy(worldRay));
}

// Sky environment at a roughness-selected mip: a rough surface reflects a BLURRED
// sky. Explicit LOD, so it is WGSL-safe in any control flow; if the bound cube has
// no mips the lod clamps to 0 and this degrades to the old sharp mirror.
float3 SampleSkyEnvironmentRough(float3 worldRay, float roughness)
{
    return texCUBElod(_Sky, float4(worldRay, RoughnessToSkyMip(roughness))).rgb
         * _EnvReflectionIntensity;
}

// Shared surface roughness for the whole specular family: Crest's smoothness-far
// ramp, near->far over a distance with a curve, all artist knobs. Drives the GGX
// sun lobe, the sky-reflection mip AND the vertical smear, so the sun and the
// mirror around it roughen together. Raise _RoughnessFar to calm shiny far waves.
float EffectiveWaterRoughness(float viewDist)
{
    float ramp = pow(saturate(viewDist / max(_RoughnessFarDistance, 1.0)),
                     _RoughnessFalloff);
    return lerp(_SunRoughness, _RoughnessFar, ramp);
}

// ---- Anisotropic sky reflection: the roughness-mip sample smeared VERTICALLY by
// extra taps offset along world up (see SKY_ANISO_*). Spread scales with roughness
// and the stretch knob, so near water stays a clean mirror and the far/rough surface
// gets the elongated streaks. Every tap re-applies the horizon lift so a downward
// offset can't dive into the cubemap's ground hemisphere. Explicit-LOD taps only, so
// the early-out branch is WGSL-safe even though the spread varies per pixel. ----
float3 SampleSkyEnvironmentAniso(float3 worldRay, float roughness)
{
    float spread = _ReflectionAnisoStretch * roughness * SKY_ANISO_SPREAD_MAX;
    if (spread < SKY_ANISO_MIN_SPREAD)
        return SampleSkyEnvironmentRough(worldRay, roughness);

    float3 color = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int tap = 0; tap < SKY_ANISO_TAP_COUNT; tap++)
    {
        float3 tapRay = normalize(worldRay + float3(0.0, spread * ANISO_TAP_OFFSETS[tap], 0.0));
        tapRay.y = max(tapRay.y, REFLECTION_MIN_UP_Y);
        color += SampleSkyEnvironmentRough(normalize(tapRay), roughness) * ANISO_TAP_WEIGHTS[tap];
    }
    return color;
}

// GGX distribution term: Trowbridge-Reitz NDF * Smith-joint visibility (Karis
// approximation) * NoL - everything EXCEPT Fresnel. Scalar; callers apply Fresnel
// and colour as fits their lobe.
float GgxLobeDistribution(float noh, float nol, float nov, float roughness)
{
    float alpha  = roughness * roughness;
    float alpha2 = alpha * alpha;
    float ndfDenom = noh * noh * (alpha2 - 1.0) + 1.0;
    float ndf = alpha2 / max(UNITY_PI * ndfDenom * ndfDenom, GGX_NDF_EPSILON);
    float visibility = 0.5 / max(lerp(2.0 * nol * nov, nol + nov, alpha), GGX_VISIBILITY_EPSILON);
    return ndf * visibility * nol;
}

// Full GGX lobe: the distribution above * Schlick Fresnel at the half-vector.
float GgxLobe(float noh, float nol, float nov, float loh, float roughness)
{
    float fresnelSpec = FRESNEL_F0_WATER
        + (1.0 - FRESNEL_F0_WATER) * pow(1.0 - loh, FRESNEL_SCHLICK_POWER);
    return GgxLobeDistribution(noh, nol, nov, roughness) * fresnelSpec;
}

// ---- Dual-lobe GGX sun specular (above water). The tight lobe (roughness from
// EffectiveWaterRoughness) is the glitter path; the optional SHEEN lobe re-runs the
// same GGX at a much broader roughness so wave faces far outside the mirror
// direction still catch a soft highlight - a single lobe leaves them dead, which
// reads as flat water away from the sun path. _SunSheen 0 = single-lobe (legacy). ----
float3 SunSpecular(float3 normal, float3 viewDir, float roughness)
{
    float3 halfDir = normalize(viewDir + _LightDir);
    float noh = saturate(dot(normal, halfDir));
    float nov = saturate(dot(normal, viewDir));
    float loh = saturate(dot(_LightDir, halfDir));
    // Wrapped NoL (fed to BOTH lobes, distribution and visibility alike, so the
    // response stays bounded): plain NoL goes to zero when the sun sits at the
    // horizon, killing the rough/far lobes exactly when a real sea shows its most
    // blinding glitter - low sun raking across tilted wave faces. 0 = physical.
    float nol = saturate((dot(normal, _LightDir) + _SunGrazeBoost)
                         / (1.0 + _SunGrazeBoost));

    float lobe = GgxLobe(noh, nol, nov, loh, roughness);
    // Sheen = distribution WITHOUT the Fresnel factor: water's F0 (~0.02) crushed
    // the broad lobe to invisibility at any usable knob value. Rough-sea sheen is
    // multi-bounce light, not single-surface-Fresnel-bound, so the weight knob is
    // the honest artistic dial (typical 0.1-0.3 now that the lobe has real energy).
    // max(): the sheen lobe may only ever be BROADER than the main lobe, so the
    // distance-roughened far surface never gets a sheen SHARPER than its mirror.
    lobe += _SunSheen * GgxLobeDistribution(noh, nol, nov,
                                            max(roughness, _SunSheenRoughness));

    return min(lobe, SUN_SPEC_CLAMP) * _SunColor;
}

#endif // WATER_SURFACE_SPECULAR_INCLUDED
