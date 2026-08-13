// WebGpuWater - shared underwater fog (Beer-Lambert absorption)
// Included by the water surface AND the lit receivers (objects, pool) so fog is
// consistent however you look at the water. Parameters are GLOBAL, published once
// per frame by WaterController, so there is a single place to tune them.
#ifndef WEBGL_WATER_FOG_INCLUDED
#define WEBGL_WATER_FOG_INCLUDED

float4 _WaterFogColor;    // deep-water colour (inscattering target)
float4 _WaterExtinction;  // per-channel extinction (red highest -> dies first)
float  _WaterFogDensity;   // overall multiplier
float  _WaterFogEnabled;   // 0 / 1
float  _WaterOpacity;      // 0..1 depth-independent turbidity (lerp view toward fog colour)

// Absorb 'color' over 'dist' world units of water. No-op when disabled.
// Absorption toward the water's OWN colour over 'dist' metres of water.
//
// THE FIX THIS CARRIES: this used to converge to the flat _WaterFogColor while every other consumer
// in the package - the chunk wall, the exclusion wall, the underwater fog, the particles, the pool
// trace and the surface's own refraction, eight call sites - converged to WaterInscatterColor. That
// broke the package's own stated invariant ("the fog colour seen from below matches the water colour
// seen from above"), and it had a blunt practical consequence: on a body with Volume Scattering ON,
// _ScatterColor is the knob an author reaches for, and it had NO EFFECT WHATSOEVER on the pool floor,
// on receivers, or on terrain - those three converged to a colour nobody was editing.
//
// The in-scatter is passed IN rather than computed here, matching ApplyWaterVolumeClarity below, so
// a caller that already has one (the surface) cannot end up with two subtly different values.
float3 ApplyWaterFog(float3 color, float dist, float3 inscatter)
{
    if (_WaterFogEnabled < 0.5) return color;
    float3 absorb = exp(-_WaterExtinction.rgb * (_WaterFogDensity * max(0.0, dist)));
    return lerp(inscatter, color, absorb);
}

// ---- Underwater view tint (physical; replaces the old hardcoded UNDERWATER_* constants) ----
// The colour the water casts on the reflected/refracted view AT the surface boundary, seen from
// below. Derived from the SAME per-channel extinction that drives the fog, so it follows the
// selected water type instead of a constant. It uses a fixed representative near-surface path on
// purpose: the eye-depth dimming of the whole submerged view is already owned by
// DownwellingAttenuation AND the underwater fog pass (simple or complex), so this must NOT
// re-integrate depth or it would double-darken. Clear water reproduces the old blue-green cast;
// turbid types pull it green, matching their absorption.
#define UNDERWATER_VIEW_DEPTH 1.0 // metres; a boundary-view path, deliberately not the full eye depth
float3 UnderwaterViewTint()
{
    return exp(-_WaterExtinction.rgb * (_WaterFogDensity * UNDERWATER_VIEW_DEPTH));
}

// ---- Volume scattering (lit in-scatter) ---------------------------------
// The flat _WaterFogColor is a picked colour that never responds to the sun. This instead lights a
// picked body colour: _ScatterColor scaled by _ScatterIntensity and modulated by ambient + a sun
// term shaped by a Schlick phase function, so the water glows toward the sun and shifts with the
// lighting while the colour you pick stays predictable. When _ScatterEnabled is 0 the helpers below
// fall back to the exact flat-colour behaviour, so every existing scene is byte-identical until on.
float  _ScatterEnabled;     // 0 -> flat _WaterFogColor in-scatter (unchanged); 1 -> lit volume colour
float4 _ScatterColor;       // the water body colour, shown directly (art-directable), HDR
float  _ScatterIntensity;   // master brightness of the in-scattered colour
float4 _ScatterAmbient;     // ambient light feeding the volume (published from the scene ambient)
float  _ScatterAmbientTerm; // weight of the ambient in-scatter
float  _ScatterSunTerm;     // weight of the sun in-scatter
float  _ScatterAnisotropy;  // Schlick phase g: 0 isotropic .. ~0.9 strong forward glow

// Wave-crest subsurface scattering (Crest PinchSSS): boosts the sun in-scatter at steep crests. The
// surface shader supplies the crest "pinch" and view/sun geometry; these are the shared tunables.
float  _SssEnabled;      // 0 -> no crest glow (unchanged); 1 -> add the SSS sun boost at crests
float  _SssIntensity;    // strength of the crest sun boost
float  _SssSunFalloff;   // how tightly the glow concentrates when looking toward the sun
float  _SssPinchMin;     // crest height (normalised) where the glow starts to ramp in
float  _SssPinchMax;     // crest height (normalised) where the glow reaches full strength
float  _SssPinchFalloff; // power curve on the ramp: >1 concentrates the glow onto the sharp peaks

#define SCATTER_PHASE_FLOOR 1e-4  // keeps the phase denominator finite at grazing angles

// Schlick approximation to the Henyey-Greenstein phase, normalised so isotropic (g = 0) returns 1 - a
// RELATIVE directional weight rather than Crest's sphere-normalised value (whose 1/4pi made the sun
// glow vanish here). cosTheta is between the light travel direction and the view; g biases forward.
float VolumeSchlickPhase(float g, float cosTheta)
{
    float k = 1.5 * g - 0.5 * g * g * g;
    float denom = 1.0 + k * cosTheta;
    return (1.0 - k * k) / max(denom * denom, SCATTER_PHASE_FLOOR);
}

// In-scatter colour for the water volume. Falls back to the flat _WaterFogColor when scattering is
// disabled, so callers stay unchanged until opted in. The picked _ScatterColor IS the body colour
// (direct, art-directable), scaled by intensity and lit by ambient + a sun term shaped by the phase.
// viewDirWS points from the surface TOWARD the camera and sunDir from the surface TOWARD the sun
// (Unity _LightDir), so the phase peaks when looking toward the sun. sunBoost is the wave-crest SSS.
// Moved here from WaterSurfaceSpecular.hlsl: the in-scatter is what needs a sun colour, and the three
// "solid geometry seen through water" shaders (AnalyticPool / WaterReceiver / WaterTerrain) include
// this header but not that one. Safe as a MOVE rather than a second declaration - WaterSurface.shader
// is the only consumer of WaterSurfaceSpecular.hlsl and includes WaterFog.hlsl first in all 3 passes.
float3 _SunColor; // Unity directional light color * intensity (global)

float3 WaterInscatterColor(float3 viewDirWS, float3 sunDir, float3 sunColor, float sunBoost)
{
    if (_ScatterEnabled < 0.5) return _WaterFogColor.rgb;

    float phase = VolumeSchlickPhase(_ScatterAnisotropy, dot(sunDir, viewDirWS));
    float3 lighting = _ScatterAmbientTerm * _ScatterAmbient.rgb
                    + _ScatterSunTerm * (1.0 + sunBoost) * phase * sunColor;
    return _ScatterColor.rgb * _ScatterIntensity * lighting;
}

// ---- Depth clarity (auto transparency from the bed depth) ----------------
// Blend a transmitted colour toward the water's in-scatter colour over 'dist' of water, active when
// EITHER the fog or the scattering feature is on - so turning on Volume Scattering alone still tints
// the transmitted scene by depth. Transmittance stays on _WaterExtinction / _WaterFogDensity, which
// are published every frame regardless of the toggles, so with both features off the colour returns
// unchanged. clarity in [0,1]: 1 = clear (identity), 0 = fully murky. A murky pixel gets denser fog
// (shorter reach) and higher turbidity, so shallow/deep water reads as authored by WaterDepthClarity
// (below). Callers pass clarity = 1 wherever the feature is inactive.
#define CLARITY_FOG_DENSITY_MAX 4.0 // deepest murk multiplies the fog density by this at clarity 0
float3 ApplyWaterVolumeClarity(float3 color, float dist, float3 inscatter, float clarity)
{
    if (_WaterFogEnabled < 0.5 && _ScatterEnabled < 0.5) return color;
    float density = _WaterFogDensity * lerp(CLARITY_FOG_DENSITY_MAX, 1.0, saturate(clarity));
    float3 absorb = exp(-_WaterExtinction.rgb * (density * max(0.0, dist)));
    return lerp(inscatter, color, absorb);
}
float3 ApplyWaterOpacityTintedClarity(float3 color, float3 inscatter, float clarity)
{
    // Murk raises turbidity from the body's base _WaterOpacity toward fully opaque as clarity -> 0.
    float opacity = saturate(_WaterOpacity + (1.0 - saturate(clarity)) * (1.0 - _WaterOpacity));
    return lerp(color, inscatter, opacity);
}

// ---- Downwelling depth attenuation --------------------------------------
// Separate from the view-path fog above: this models the light LOST travelling straight
// DOWN from the surface, so a point reads darker the DEEPER it sits, independent of how
// far the camera looks through the water. Per-channel (red dies first) so the deep also
// shifts blue. Depth is measured in WORLD units against the surface plane y=level, the
// same convention the fog uses, so arbitrary floor/terrain geometry darkens for free.
// These are independent of the fog extinction by default; WaterVolume can mirror the fog
// values in when its "link" toggle is on.
float4 _DepthExtinction;     // per-channel downwelling coefficient (rgb used; float4 to match SetColor)
float  _DepthDarkenStrength; // master multiplier (density) on the depth term
float  _DepthDarkenEnabled;  // 0 / 1 master switch for the whole depth feature
float  _CausticDepthFade;    // extra depth softening for projected caustics (objects)
float  _GodRayDepthFade;     // how fast god-ray shafts fade with depth

// Per-channel transmittance for a point at world height 'pointY' beneath surface 'level'.
// Returns 1 (no darkening) above the surface or when the feature is disabled.
float3 DownwellingAttenuation(float pointY, float level)
{
    if (_DepthDarkenEnabled < 0.5) return float3(1.0, 1.0, 1.0);
    float depth = max(0.0, level - pointY);
    return exp(-_DepthExtinction.rgb * (_DepthDarkenStrength * depth));
}

// Scalar depth fade for intensity-only effects (caustics, god-ray shafts). Gated by the
// same master switch so one toggle governs every depth effect. 'coeff' is the per-effect rate.
float DepthFadeScalar(float pointY, float level, float coeff)
{
    if (_DepthDarkenEnabled < 0.5) return 1.0;
    float depth = max(0.0, level - pointY);
    return exp(-coeff * depth);
}

// ---- Bed depth (real water-column depth from the baked terrain height) ------
// _BedTex (pool-space, R = bed height in pool units) is baked once from the terrain by
// WaterVolume. Shaders sample it in their own sampler style and pass bedPoolY here; with no
// bake we fall back to the flat floor at pool y = -1, so nothing breaks before a bed is set.
float  _BedValid;            // 1 when a bed-height map is baked
float  _UseBedDepth;         // master toggle for real bed depth
float4 _DeepWaterColor;      // shoreline gradient target colour (deep water)
float  _ShorelineDepthScale; // gradient rate (1 / fade-depth, per world unit)
float  _ShorelineStrength;   // 0..1 max tint toward the deep colour

// Depth clarity curve: ONE mapping from the bed column depth to a clarity in [0,1] that drives
// turbidity, underwater-fog reach and the deep-water tint together (no painted mask). Inert at
// strength 0, so a body that never opts in is byte-identical to before.
float4 _DepthClarityRange;    // x = shallow depth (m), y = deep depth (m), z = clarity@shallow, w = clarity@deep
float  _DepthClarityStrength; // 0 = off (flat per-body look); 1 = full depth-driven clarity

// The RAW clarity curve at a world column depth - the range mapping alone, WITHOUT the strength
// fold. For consumers that need "how murky is this column at full clarity" and blend by the
// strength themselves (the deep-water tint): folding the strength in first and then inverting
// (1 - clarity) collapses BELOW both endpoints at partial strength - the shipped "water turns
// transparent between 0 and 1" dial bug. Density/turbidity consumers keep WaterDepthClarity
// below, whose correct strength-0 limit is identity.
float WaterDepthClarityCurve(float columnDepthWorld)
{
    float span = max(_DepthClarityRange.y - _DepthClarityRange.x, 1e-3);
    float t = saturate((columnDepthWorld - _DepthClarityRange.x) / span);
    return lerp(_DepthClarityRange.z, _DepthClarityRange.w, t);
}

// Clarity at a world column depth. 1 = clear (identity), 0 = murky. Returns 1 when the feature is
// off (strength 0) so every caller is a no-op until a body opts in.
float WaterDepthClarity(float columnDepthWorld)
{
    if (_DepthClarityStrength <= 0.0) return 1.0;
    return lerp(1.0, WaterDepthClarityCurve(columnDepthWorld), saturate(_DepthClarityStrength));
}

// Water-column depth (world units) from the bed's pool height up to the surface's pool height.
float BedColumnDepthWorld(float bedPoolY, float surfacePoolY, float extentY)
{
    float bed = (_BedValid > 0.5) ? bedPoolY : -1.0;
    return max(0.0, (surfacePoolY - bed) * extentY);
}

// Length of the camera->fragment segment that lies below the water plane y=level.
// Handles camera above or below the surface; returns 0 for fully-above segments.
float WaterPathLength(float3 fragWS, float3 camWS, float level)
{
    float len = length(fragWS - camWS);
    float yC = camWS.y, yF = fragWS.y;
    bool camUnder  = yC <= level;
    bool fragUnder = yF <= level;
    if (camUnder && fragUnder) return len;     // whole segment underwater
    if (!camUnder && !fragUnder) return 0.0;   // whole segment above water
    float t = (level - yC) / (yF - yC);        // crossing fraction in [0,1]
    return (fragUnder ? (1.0 - t) : t) * len;  // underwater portion only
}

// ---- Scene point/spot light scattering (the WATER_FOG_POINT_LIGHTS family) ------------------
// The package's OWN capped light list, published per frame by WaterUniformPublisher
// (PublishSceneLights): position+range, colour+outer-cone, spot dir + inverse cone range, and
// the true count. Deliberately NOT URP's additional-light arrays: those need URP's
// Lighting.hlsl (impossible in the CGPROGRAM surface pass), their count global is the
// PER-OBJECT cap (a different number), their UBO entries past the visible count are STALE, and
// GetAdditionalLight(i) maps through per-object indices no fullscreen draw has. One published
// list + one integral, consumed identically by the fullscreen fog (submerged view) and the
// surface's from-above transmitted term - the two views of one glow cannot drift.
// This header stays SRP-MACRO-FREE and the function is pure ALU (no textures, no derivatives):
// safe in CG and HLSL, in divergent flow, and inside the 16-sampler-capped surface pass.
#define WATER_SCENE_LIGHT_MAX 8              // KEEP IN SYNC with WaterUniformPublisher.MaxSceneLights
#define WATER_SCENE_LIGHT_MIN_DIST_SQ 0.0025 // closest-approach floor squared (integral divides by it)
#define WATER_SCENE_LIGHT_GAIN 0.25          // art gain folding the scattering coefficient: knob 1
                                             // on a default-density body reads as a strong lamp glow
float4 _WaterSceneLightPosRange[WATER_SCENE_LIGHT_MAX];  // xyz world pos, w = range (metres)
float4 _WaterSceneLightColorCone[WATER_SCENE_LIGHT_MAX]; // rgb colour * intensity, w = cos(outerCone); points publish -2 (cone factor saturates to 1)
float4 _WaterSceneLightSpotDir[WATER_SCENE_LIGHT_MAX];   // xyz spot direction, w = 1 / (cosInner - cosOuter)
float _WaterSceneLightCount;   // TRUE published count - never a URP global (see above)
float _UnderwaterLightScatter; // the per-body Light Scatter knob (Water Fog block)

// Point-local attenuation of ONE published light at sample point 'p': smooth range window
// (URP's (1-(d^2/r^2)^2)^2), squared spot cone, inverse-square falloff. These are the
// geometry factors every lamp-glow consumer must agree on, extracted here so the closed-form
// integral below and the god-ray march (per-sample) evaluate ONE implementation and can never
// drift apart in tuning - the same rule that gave the fog and the surface one integral.
// Colour, depth mood and extinction are deliberately NOT folded in: they multiply outside so
// each caller keeps its own policy - the integral applies its single exp over view+light legs
// exactly as before, while the march pairs the returned RAW 'lightDist' with the running
// per-step view transmittance it already carries (folding an exp here would double-apply it).
// Pure ALU, like everything in this header.
float WaterSceneLightPointAtten(float4 posRange, float4 colorCone, float4 spotDir,
                                float3 p, out float lightDist)
{
    float3 toLight = posRange.xyz - p;
    float distSq = max(dot(toLight, toLight), WATER_SCENE_LIGHT_MIN_DIST_SQ);
    float rangeSq = max(posRange.w * posRange.w, WATER_SCENE_LIGHT_MIN_DIST_SQ);
    float window = saturate(1.0 - (distSq * distSq) / (rangeSq * rangeSq));
    window *= window;
    float3 lightDir = toLight * rsqrt(distSq);
    float cone = saturate((dot(-lightDir, spotDir.xyz) - colorCone.w) * spotDir.w);
    cone *= cone; // URP squares its angle attenuation; matched so lamps agree with geometry
    lightDist = sqrt(distSq);
    return window * cone / distSq;
}

// Closed-form single scatter of the published lights over the wet segment [tStart, tEnd] of
// the unit ray cam + dir*t. 'tWaterStart' is where WATER begins on that ray: extinction is
// measured from there, so an above-water eye does not extinguish its glow through AIR.
// For a light at closest-approach distance h to the ray (closest at parameter tc), the
// inverse-square in-scatter integral is analytic:
//   I = INTEGRAL dt / (h^2 + (t - tc)^2) = (atan((tEnd - tc)/h) - atan((tStart - tc)/h)) / h
// The range window (URP's smooth (1-(d^2/r^2)^2)^2) and the spot cone are evaluated at the
// nearest IN-SPAN point and treated as constant across the span - exact for h << range, and
// they fade the same direction elsewhere, so the approximation only ever dims.
// TWO depth terms give a sunk lamp its depth feeling (Bert 2026-07-31, "no depth feeling"):
//  * the LIGHT LEG - the lamp's photons are absorbed per channel over their distance THROUGH
//    WATER to the scattering point (the first ship only extinguished the view leg, so a lamp
//    at 30 m glowed like a lamp at 1 m);
//  * DownwellingAttenuation at the LIGHT's depth against 'surfaceY' (callers pass the rest
//    plane). Physically a local light owes the sun's downwelling nothing - but the Depth
//    Attenuation knob is the depth-mood dial this package already has, and a lamp that
//    ignores it reads wrong next to everything that obeys it. Bert's call; enabled/scaled by
//    the existing _DepthDarken* family, so no new knob and off-by-default stays off.
float3 WaterSceneLightsInscatter(float3 cam, float3 dir, float tStart, float tEnd,
                                 float tWaterStart, float surfaceY)
{
    float3 scatter = float3(0.0, 0.0, 0.0);
    int sceneLightCount = min((int)_WaterSceneLightCount, WATER_SCENE_LIGHT_MAX);
    [loop]
    for (int li = 0; li < sceneLightCount; li++)
    {
        float4 posRange = _WaterSceneLightPosRange[li];
        float4 colorCone = _WaterSceneLightColorCone[li];
        float4 spotDir = _WaterSceneLightSpotDir[li];
        float tc = dot(posRange.xyz - cam, dir);        // closest approach (unclamped: the
                                                        // atan pair handles out-of-span tc)
        float3 hVec = posRange.xyz - (cam + dir * tc);
        float h = sqrt(max(dot(hVec, hVec), WATER_SCENE_LIGHT_MIN_DIST_SQ));
        // Window/cone/1-over-d^2 sampled at the nearest point the SPAN can actually see, so a
        // light behind the camera or beyond the scene contributes what the water between the
        // bounds receives, not what its own closest point would. The factors themselves live
        // in WaterSceneLightPointAtten (shared with the god-ray march - see its header).
        float tAtten = clamp(tc, tStart, tEnd);
        float lightDist;
        float atten = WaterSceneLightPointAtten(posRange, colorCone, spotDir,
                                                cam + dir * tAtten, lightDist);
        float deltaAtan = atan((tEnd - tc) / h) - atan((tStart - tc) / h);
        // View leg (camera side, from where the water starts) PLUS light leg (lamp to the
        // sample point, all water by construction - the lamp is submerged if it glows at
        // all): both per channel, so a deep or distant lamp reddens then dies exactly like
        // every other path through this medium.
        float3 extinct = exp(-_WaterExtinction.rgb
                             * (_WaterFogDensity
                                * (max(tAtten - tWaterStart, 0.0) + lightDist)));
        // The depth-mood dial (see header): the lamp's own depth against the rest plane,
        // through the SAME DownwellingAttenuation everything else obeys. Identity while the
        // Depth Attenuation feature is off.
        float3 depthMood = DownwellingAttenuation(posRange.y, surfaceY);
        // (window * cone / d^2) at the sample point, times h * deltaAtan = the span integral.
        scatter += colorCone.rgb * (atten * h * deltaAtan)
                 * extinct * depthMood;
    }
    return scatter * (_WaterFogDensity * WATER_SCENE_LIGHT_GAIN);
}

#endif // WEBGL_WATER_FOG_INCLUDED
