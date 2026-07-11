// WebGL Water - shared underwater fog (Beer-Lambert absorption)
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
float3 ApplyWaterFog(float3 color, float dist)
{
    if (_WaterFogEnabled < 0.5) return color;
    float3 absorb = exp(-_WaterExtinction.rgb * (_WaterFogDensity * max(0.0, dist)));
    return lerp(_WaterFogColor.rgb, color, absorb);
}

// Depth-independent turbidity: pull a transmitted colour toward the fog colour by
// _WaterOpacity, so water can be made non-transparent regardless of what's behind it.
// Active whenever opacity > 0 (independent of the Beer-Lambert fog toggle).
float3 ApplyWaterOpacity(float3 color)
{
    return lerp(color, _WaterFogColor.rgb, saturate(_WaterOpacity));
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
float3 WaterInscatterColor(float3 viewDirWS, float3 sunDir, float3 sunColor, float sunBoost)
{
    if (_ScatterEnabled < 0.5) return _WaterFogColor.rgb;

    float phase = VolumeSchlickPhase(_ScatterAnisotropy, dot(sunDir, viewDirWS));
    float3 lighting = _ScatterAmbientTerm * _ScatterAmbient.rgb
                    + _ScatterSunTerm * (1.0 + sunBoost) * phase * sunColor;
    return _ScatterColor.rgb * _ScatterIntensity * lighting;
}

// Blend a transmitted colour toward the water's in-scatter colour over 'dist' of water. Like
// ApplyWaterFog but (a) toward a supplied lit in-scatter colour and (b) active when EITHER the fog or
// the scattering feature is on - so turning on Volume Scattering alone still tints the transmitted
// scene by depth (the whole point of a scattering volume). Transmittance stays on _WaterExtinction /
// _WaterFogDensity, which are published every frame regardless of the toggles, so with both features
// off this returns the colour unchanged - identical to the old ApplyWaterFog no-op.
float3 ApplyWaterVolume(float3 color, float dist, float3 inscatter)
{
    if (_WaterFogEnabled < 0.5 && _ScatterEnabled < 0.5) return color;
    float3 absorb = exp(-_WaterExtinction.rgb * (_WaterFogDensity * max(0.0, dist)));
    return lerp(inscatter, color, absorb);
}

// Turbidity floor toward the water's in-scatter colour (the lit body colour when scattering is on,
// else the flat fog colour, since 'inscatter' already carries that fallback). Mirrors ApplyWaterOpacity.
float3 ApplyWaterOpacityTinted(float3 color, float3 inscatter)
{
    return lerp(color, inscatter, saturate(_WaterOpacity));
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

#endif // WEBGL_WATER_FOG_INCLUDED
