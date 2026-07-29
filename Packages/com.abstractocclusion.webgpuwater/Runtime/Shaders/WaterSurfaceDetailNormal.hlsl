// WaterSurface pass: Crest-style crossing scrolling detail normals.
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. The tex2D taps use IMPLICIT derivatives, so
// DetailNormalTilt may only ever be called from UNIFORM control flow (the
// caller's strength/underwater gates) - see the WGSL note on the function.
#ifndef WATER_SURFACE_DETAIL_NORMAL_INCLUDED
#define WATER_SURFACE_DETAIL_NORMAL_INCLUDED

// Crest-style crossing detail normals: the two fixed crossing directions are Crest's
// own (non-orthogonal, non-axis-aligned, so the two scrolls never read as a grid).
// The far layer runs at a bigger tile and half the scroll so the layers never sync;
// it crossfades in over [BLEND_START, BLEND_START+BLEND_RANGE] metres and the whole
// effect fades out over [FADE_START, FADE_START+FADE_RANGE] metres (beyond that the
// distance-grown roughness carries the look and per-pixel detail would only shimmer).
#define DETAIL_NORMAL_DIR0            float2(0.94, 0.34)
#define DETAIL_NORMAL_DIR1            float2(-0.85, -0.53)
// Tile ratio and speed ratio are the golden ratio SQUARED and the golden ratio - the same
// low-discrepancy constant WaterWaveBank stratifies its wave headings with. Two reasons, not one:
// (1) an irrational ratio never lets the two layers beat back into phase, where the exact octave
// this used to be re-synced constantly and read as a grid; (2) sqrt(tile ratio) IS the deep-water
// dispersion relation c = sqrt(g*lambda / 2pi), so the LONGER far layer now travels FASTER than the
// near one. It previously ran at HALF speed while carrying twice the wavelength - dispersion
// backwards, and the main reason the distance layer read as sludge rather than as moving water.
#define DETAIL_NORMAL_FAR_TILE_MULT   2.6180340
#define DETAIL_NORMAL_FAR_SPEED_MULT  1.6180340
// Sine of the surface tilt treated as a fully steep wave face by the crest boost (~20 degrees).
#define DETAIL_CREST_REFERENCE_SLOPE  0.35
#define DETAIL_NORMAL_FAR_BLEND_START 30.0
#define DETAIL_NORMAL_FAR_BLEND_RANGE 90.0
#define DETAIL_NORMAL_FADE_START      250.0
#define DETAIL_NORMAL_FADE_RANGE      350.0

sampler2D _DetailNormalTex; // tiling water normals; default "bump" = flat = feature inert
float _DetailNormalStrength, _DetailNormalScale, _DetailNormalSpeed;
// ONE wind for the whole surface: (cos, sin) of the heading in XZ, the same convention
// WaterWaveBank.Generate builds its component directions from. Declared here beside the family that
// reads it - this package has every consumer declare its own published globals. Identity (1, 0) at
// heading 0, which every shipped body uses, so the rotation below is a no-op until wind is turned.
float4 _WindDirection;
float _DetailNormalCrestBoost; // applied by the CALLER (it needs the composed surface normal)

// ---- Crest-style detail normal: two CROSSING, SCROLLING samples of a tiling normal
// map at two world scales, crossfaded by camera distance (see DETAIL_NORMAL_*).
// Returns an xz slope tilt for the world normal. All four taps always run - the
// distance fade is a multiply, not a branch, because a per-pixel branch around
// tex2D's implicit derivatives is undefined on WGSL; the caller's gate (strength
// knob + above-water) is uniform, which IS branch-safe. ----
float2 DetailNormalTilt(float2 worldXZ, float viewDist)
{
    // Rotate the two crossing directions INTO the wind frame (a complex multiply). They stay Crest's
    // non-orthogonal pair: the ANGLE BETWEEN them is what stops the two scrolls reading as a grid,
    // and a rotation preserves it exactly. Guarded so an unpublished uniform cannot collapse both
    // directions to zero and freeze the scroll.
    // No normalize: WindDirectionXZ publishes (cos, sin) and is already unit by construction. The
    // guard stays, because an UNPUBLISHED uniform is the one way this can be non-unit - and that is
    // exactly the case it was written for.
    float2 wind = (dot(_WindDirection.xy, _WindDirection.xy) > 1e-6)
                ? _WindDirection.xy : float2(1.0, 0.0);
    float2 dir0 = float2(DETAIL_NORMAL_DIR0.x * wind.x - DETAIL_NORMAL_DIR0.y * wind.y,
                         DETAIL_NORMAL_DIR0.x * wind.y + DETAIL_NORMAL_DIR0.y * wind.x);
    float2 dir1 = float2(DETAIL_NORMAL_DIR1.x * wind.x - DETAIL_NORMAL_DIR1.y * wind.y,
                         DETAIL_NORMAL_DIR1.x * wind.y + DETAIL_NORMAL_DIR1.y * wind.x);

    float scrollTime = _DetailNormalSpeed * _WaveTime;
    float2 scroll0 = dir0 * scrollTime;
    float2 scroll1 = dir1 * scrollTime;

    float2 tiltNear =
          UnpackNormal(tex2D(_DetailNormalTex, (worldXZ + scroll0) / _DetailNormalScale)).xy
        + UnpackNormal(tex2D(_DetailNormalTex, (worldXZ + scroll1) / _DetailNormalScale)).xy;

    float farTile = _DetailNormalScale * DETAIL_NORMAL_FAR_TILE_MULT;
    float2 tiltFar =
          UnpackNormal(tex2D(_DetailNormalTex,
              (worldXZ + scroll0 * DETAIL_NORMAL_FAR_SPEED_MULT) / farTile)).xy
        + UnpackNormal(tex2D(_DetailNormalTex,
              (worldXZ + scroll1 * DETAIL_NORMAL_FAR_SPEED_MULT) / farTile)).xy;

    float farBlend = saturate((viewDist - DETAIL_NORMAL_FAR_BLEND_START)
                              / DETAIL_NORMAL_FAR_BLEND_RANGE);
    float fade = 1.0 - saturate((viewDist - DETAIL_NORMAL_FADE_START)
                                / DETAIL_NORMAL_FADE_RANGE);
    return lerp(tiltNear, tiltFar, farBlend) * fade;
}

#endif // WATER_SURFACE_DETAIL_NORMAL_INCLUDED
