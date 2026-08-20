// WaterSurface pass: every foam SAMPLING path - surf-foam knobs, foam/whitecap
// constants, the flipbook/tiling pattern samplers, EvaluateFoam, the ocean
// whitecap pattern/tilt, and the shared FoamDissolve / ApplyFoamTiltToNormal
// helpers. (Foam LIGHTING lives in WaterFoamCommon.hlsl, shared with the
// particle shaders.)
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. The hoisted-gradient comments (WGSL derivative
// uniformity) are CONTRACTS - keep them glued to their functions.
#ifndef WATER_SURFACE_FOAM_SAMPLING_INCLUDED
#define WATER_SURFACE_FOAM_SAMPLING_INCLUDED

// The sim-foam mask read and THE coverage formula (_FoamMask/_FoamStrength/_FoamBorderWidth,
// SampleFoamMaskBilinear, FoamWindowFade, SampleFoamMaskWindowed, SimFoamCoverage) moved here
// so the fullscreen underwater fog shares them instead of keeping a second, drifted copy.
#include "WaterFoamMask.hlsl"

// ---- Surf foam enhancement uniforms (FOAM-1/2/3, ALL RENDER-ONLY). Published as
// globals by WaterShoreDepthField beside the _Surf* set; unpublished = every feature
// off and the pass byte-identical. The scalar repartition weights live in
// WaterSurfWaves.hlsl (shared with the computes); these are surface-only. ----
sampler2D _SurfCrestFoamLut;   // R: crest-foam intensity over the lifecycle clock
float _SurfCrestFoamLutActive; // 1 = the artist pop curve replaces the built-in window
float _SurfCrestFoamGain;      // master gain on the curve-driven crest foam
float _SurfFoamCrestCap;       // FOAM-4: crest-cap gain - keeps foam on the breaking crest through
                               // the bore (surface-only, independent of the LUT). 0 = off/byte-identical
float _SurfFoamTrailDissolve;  // seconds an aged deposit takes to rot into holes (0 = off)
float _SurfSwashFoam;          // swash foam strength (0 = feature off)
float _SurfSwashFoamWidth;     // metres of run-up height covered by the foam band
float _SurfSwashFoamDissolve;  // 0..1 how hard reflux age erodes the stranded line
float _ShoreSwashDepositGain;  // FOAM-5: >0 = persistent swash deposits live in the foam buffer;
                               // the surface then lifts + keeps the beach alive under them so they
                               // dissolve on the sand instead of blinking off when the wet line recedes
// How far age can push the pattern-dissolve threshold (a full push leaves only the
// brightest pattern peaks alive - lace filaments, then nothing).
#define SURF_TRAIL_ERODE_MAX   0.6
#define SURF_SWASH_ERODE_MAX   0.7
// Swash-phase at which the stranded deposit line reaches peak brightness (a little past the
// uprush apex SURF_SWASH_UPRUSH). It rises to here, then dissolves to ~0 by the cycle wrap, so
// the deposit fades gradually instead of snapping off when the next uprush begins.
#define SURF_SWASH_DEPOSIT_PEAK 0.45

// Perturb the foam texture UV by the surface tilt so foam rides the ripples.
#define FOAM_NORMAL_NUDGE   0.1
// Skip all foam texture work below this mask level (nothing would be visible).
#define FOAM_MASK_EPSILON   0.005
// Flow-phased pattern drift: how far the foam pattern is dragged along the
// local surface flow (UV units per phase) and how fast the two phases cycle.
// Two half-offset phases cross-faded by a seesaw weight hide the reset jump
// (classic flowmap trick), so the pattern drifts forever without stretching.
#define FOAM_FLOW_DISTANCE  0.35
#define FOAM_FLOW_RATE      0.5
// Two-layer look: mask level where the dense core starts/saturates, softness
// of the lace erosion edge, and how far the core is pushed toward plain white.
// CORE_START sits high: the solid-white core is reserved for genuinely thick
// foam, so everyday ripple foam stays textured lace/flecks instead of big
// white patches (the sqrt-reach dissolve below carries the mid range).
#define FOAM_CORE_START     0.8
#define FOAM_CORE_FULL      0.95
#define FOAM_LACE_SOFTNESS  0.25
#define FOAM_CORE_WHITEN    0.7
// Pattern-erosion band for the core cut: wider than the lace band so the
// core rim breaks into chunkier pieces than the thin filaments.
#define FOAM_CORE_CUT_SOFTNESS 0.35
// Procedural foam relief (replaces the normal-map flipbook, like the whitecap):
// finite-difference tap offset in TILE-UV units (~4 texels of a 128px cell) and
// the gain mapping brightness gradient -> normal tilt.
#define FOAM_PROC_NORMAL_DELTA 0.03
#define FOAM_PROC_NORMAL_GAIN  2.0
// (Residual foam is controlled in the SIM: the Residual Foam slider blends the thin-
// foam survival rate toward the fresh rate, so leftovers decay away uniformly. A
// render-side slope gate was tried and rejected - modulating foam by live wave phase
// makes it pulse in rings, which reads as visually wrong.)
// Foam lighting (FOAM_LIGHT_WRAP / FOAM_AMBIENT) lives in WaterFoamCommon.hlsl,
// shared with FoamParticles/SplashParticles so every foam element shades alike.
// Seen from BELOW, dense foam blocks the sky transmitted through the surface, while thin
// lace scatters a faint sunlit glow through. The two weights are per-body tunables now
// (_FoamUndersideDarken/_FoamUndersideGlow, Underwater Surface block, declared in
// WaterSurfaceSpecular.hlsl); the shipped defaults match the old hard-coded 0.6/0.4.
// Ocean whitecap anti-tiling: a second, rotated, differently-scaled octave of the foam pattern
// is combined with the first so no single texture tile is resolvable toward the horizon. This is
// continuous (unlike a hashed triangle grid it has no cell seams), so it is safe on every
// backend. Contrast then sharpens the dissolve so crests read as crisp whitecaps, not round blobs.
#define OCEAN_WHITECAP_OCTAVE2_SCALE     2.37       // 2nd octave world scale vs the 1st (non-integer so the grids rarely realign)
#define OCEAN_WHITECAP_OCTAVE2_ROT_COS   0.8660254  // cos(30 deg): rotate the 2nd octave so its axes don't line up with the 1st
#define OCEAN_WHITECAP_OCTAVE2_ROT_SIN   0.5        // sin(30 deg)
#define OCEAN_WHITECAP_OCTAVE_BLEND_DIST 60.0       // metres over which the 2nd octave fades in (near water keeps one crisp tile)
#define OCEAN_WHITECAP_CONTRAST          1.6        // >1 sharpens the pattern so foam breaks into crisper shapes, less round
#define OCEAN_WHITECAP_CONTRAST_DENSE    1.0        // contrast relaxes toward this as coverage saturates (KWS), so dense foam goes SOLID instead of staying lacy
// Texture-histogram constants, ONE home: the far-field FoamDissolveExpected model (further
// down) and the variance-preserving octave blend (WhitecapOctaveBlend, below) both read
// them, and this block sits before both use sites.
// ⚠️ Calibrated to the SHIPPED whitecap texture (all three channels identical: mean 0.501,
// stddev 0.189, measured 2026-07-31). A user who swaps in a foam pattern with a very
// different histogram (much flatter, or much more contrasty) will see the near and far
// fields disagree - remeasure and update MEAN/STDDEV, they are the only texture-specific
// constants in the foam path.
#define OCEAN_FOAM_PATTERN_MEAN     0.501
#define OCEAN_FOAM_PATTERN_STDDEV   0.189
#define OCEAN_FOAM_PATTERN_CDF_SPAN 2.45       // +/- sigma over which the smoothstep approximates the normal CDF
#define OCEAN_FOAM_OCTAVE_BLEND_NORM 0.70710678 // 1/sqrt(2): variance normalizer for two decorrelated octaves

// Variance-preserving octave combine - the anti-tiling mix that REPLACED min().
// (a + b - 2*mean)/sqrt(2) + mean keeps the blended pattern's mean AND variance equal to a
// single octave's (measured on the shipped texture: 0.501/0.188 blended vs 0.501/0.189
// raw), so the dissolve threshold - calibrated on the raw histogram, exactly like
// FoamDissolveExpected's constants - keeps the SAME foam density at every distance. The
// old min() combine collapsed the distribution (mean 0.394, stddev 0.158): at coverage 0.2
// the kept fraction fell from 0.086 to 0.009 - TEN TIMES sparser - across the whole
// 60 m+ band, then FoamDissolveExpected (still on raw stats) brought the density BACK at
// the far handover. On screen: "whitecaps sparser inside the sim window than at distance"
// (Bert 2026-07-31). The 60 m octave-blend edge sits at the sim window's visual edge, but
// the window was never involved - this path reads no window state at all. Two decorrelated
// rotated grids share no common repeat, so the anti-tiling job survives the change; only
// the far foam's SHAPES soften, from intersection-clumps to mixed cells.
float3 WhitecapOctaveBlend(float3 a, float3 b)
{
    return saturate((a + b - 2.0 * OCEAN_FOAM_PATTERN_MEAN) * OCEAN_FOAM_OCTAVE_BLEND_NORM
                    + OCEAN_FOAM_PATTERN_MEAN);
}
// Whitecap parallax (SW3-style fake height): the foam pattern is sampled where a layer floating
// PARALLAX_HEIGHT metres above the surface would intersect the view ray, so foam visually sits
// on top of the water instead of being painted into it. The view-ray Y is floored so grazing
// angles can't stretch the offset to infinity.
#define OCEAN_FOAM_PARALLAX_HEIGHT 0.04
#define OCEAN_FOAM_PARALLAX_MIN_VIEW_Y 0.25
// Procedural whitecap relief (Crest MultiScaleFoamNormal): finite-difference the albedo
// tile instead of shipping a normal map. DELTA = tap offset as a fraction of the tile
// (4 texels of the 1024px source); GAIN calibrated so the default tilt is comparable to
// the retired normal map at strength 1.
#define OCEAN_FOAM_NORMAL_DELTA (4.0 / 1024.0)
#define OCEAN_FOAM_NORMAL_GAIN  2.5

// Foam: the _FoamMask sim buffer is declared in WaterFoamMask.hlsl (shared with the fog pass);
// _FoamTex is an optional per-material pattern (defaults white = flat foam).
sampler2D _FoamTex;
// Static river coverage reuses _FoamMask on river renderers: those meshes never consume the
// rectangular simulation field, and avoiding a new sampler is load-bearing for the pass budget.
float _RiverFoamActive;
float _RiverFoamStrength;
float _RiverFluidActive;
float _RiverFluidInvLength;
float _RiverFluidMaxSpeed;
// Dedicated ocean wave-foam (whitecap) slots: a single seamless TILING texture (not a flipbook
// atlas) + its raw-RGB relief normal, sampled only by the FFT-ocean whitecap path. Defaults
// (white / bump) keep the look unchanged when unassigned. Decoupled from _FoamTex so the ocean
// whitecap and the interactive/shoreline foam can be art-directed independently.
sampler2D _OceanWhitecapTex;
// Optional whitecap flipbook: a real grid animates the whitecap texture (the SAME texture the
// deep ocean caps AND the surf whitewash sample), (1,1) = the original seamless tiling. Its own
// auto-populated texel size drives the flipbook cell inset.
float4 _OceanWhitecapFrames; // (cols, rows); (1,1) = single tiling texture, no flipbook
float _OceanWhitecapFPS;     // whitecap flipbook frame rate
float4 _OceanWhitecapTex_TexelSize;
// Auto-populated by Unity as (1/w, 1/h, w, h). Drives the flipbook half-texel inset that
// stops bilinear filtering bleeding across cell/tile edges.
float4 _FoamTex_TexelSize;
float4 _FoamTexFrames; // (cols, rows) of the flipbook grid; (1,1) = plain tiling texture
float  _FoamTexFPS;
float  _FoamNormalStrength;
// WORLD metres per foam-pattern tile (published per body: Foam Pattern Size). The pattern
// is sampled in world space, so its scale is independent of the body extent (no more
// "pattern rides the pool size") and world-anchored on windowed bodies (no more pattern
// swimming with the camera window).
float  _FoamTileSize;
float4 _FoamColor;
float _FoamContactDepth; // _FoamEnabled/_FoamStrength/_FoamBorderWidth: WaterFoamMask.hlsl
// Mask level over which the foam layer fades in from nothing (edge
// feathering). 0 disables: foam clips hard at the mask epsilon.
float _FoamFeather;
// How much the pattern erodes the dense core's alpha (0 = solid core,
// 1 = fully pattern-cut like the lace).
float _FoamCoreCut;


// Flipbook frame pair + crossfade weight for the current time. Both the foam
// pattern and its normal map use this, so their frames can never drift apart.
// A (1,1) grid reduces to a plain tiled lookup (existing materials unaffected).
// Parameterized on (frames, fps) so EVERY flipbook consumer - pond foam AND the ocean whitecap /
// surf whitewash - shares ONE frame-selection implementation instead of a per-texture copy.
void FlipbookFrames(float2 framesXY, float fps,
                    out float2 cellA, out float2 cellB, out float2 grid, out float blend)
{
    grid = max(float2(1.0, 1.0), framesXY);
    float frameCount = grid.x * grid.y;
    float framePos = _Time.y * fps;
    blend = frac(framePos);

    float frameA = fmod(floor(framePos), frameCount);
    float frameB = fmod(frameA + 1.0, frameCount);
    // Flipbooks read left-to-right, top-to-bottom; texture V runs bottom-up.
    cellA = float2(fmod(frameA, grid.x), grid.y - 1.0 - floor(frameA / grid.x));
    cellB = float2(fmod(frameB, grid.x), grid.y - 1.0 - floor(frameB / grid.x));
}

// Seamless flipbook-cell sample. frac(uv) tiles the pattern but spikes ddx/ddy at every tile
// boundary, which snaps the GPU to a coarse mip there - a visible stitch line on the seam - and
// lets bilinear filtering bleed into the neighbouring frame. Fix both: choose the mip from the
// CONTINUOUS pre-frac gradients via tex2Dgrad, and inset the tile by half a texel so a filtered
// tap can't leave the cell. WGSL derivative uniformity: the pre-frac uv gradients (uvDdx/uvDdy)
// are HOISTED by the caller from uniform control flow - computing ddx/ddy here would be undefined,
// since this helper runs inside the non-uniform foam-mask branches.
float4 SampleFlipbookCell(sampler2D tex, float2 uv, float2 uvDdx, float2 uvDdy, float2 cell, float2 grid, float2 invSize)
{
    float2 gradX = uvDdx / grid;
    float2 gradY = uvDdy / grid;
    // Half a texel in tile space, capped so the 1x1 white-fallback texture (no foam assigned,
    // invSize = 1) can't invert the clamp below; a white tap stays white either way.
    float2 inset = min(invSize * 0.5 * grid, 0.49);
    float2 tiled = clamp(frac(uv), inset, 1.0 - inset);
    return tex2Dgrad(tex, (tiled + cell) / grid, gradX, gradY);
}

// Foam pattern with frame advance + crossfade: the foam churns internally
// even where the mask is static. Grid (1,1) = a single seamless TILING texture:
// plain hardware-wrap sample (like the ocean whitecap) - the flipbook cell inset
// would break a seamless tile's edges, and there are no frames to crossfade.
// WGSL derivative uniformity: gradients are passed in (hoisted by the caller in
// uniform control flow), never derived here - this runs inside non-uniform
// foam-mask branches where ddx/ddy would be undefined.
// Generic flipbook/tiling pattern sample: frame-crossfaded when the grid is real, a plain seamless
// tiling tap at (1,1). ONE implementation shared by the pond foam and the ocean whitecap / surf
// whitewash (so their flipbook handling can never drift). Gradients hoisted by the caller.
float3 SampleFlipbookPattern(sampler2D tex, float2 framesXY, float fps, float2 invSize,
                             float2 uv, float2 uvDdx, float2 uvDdy)
{
    float2 cellA, cellB, grid; float blend;
    FlipbookFrames(framesXY, fps, cellA, cellB, grid, blend);
    if (grid.x * grid.y <= 1.0)
        return tex2Dgrad(tex, uv, uvDdx, uvDdy).rgb;
    float3 a = SampleFlipbookCell(tex, uv, uvDdx, uvDdy, cellA, grid, invSize).rgb;
    float3 b = SampleFlipbookCell(tex, uv, uvDdx, uvDdy, cellB, grid, invSize).rgb;
    return lerp(a, b, blend);
}

float3 SampleFoamPattern(float2 uv, float2 uvDdx, float2 uvDdy)
{
    return SampleFlipbookPattern(_FoamTex, _FoamTexFrames.xy, _FoamTexFPS,
                                 _FoamTex_TexelSize.xy, uv, uvDdx, uvDdy);
}

// Shared foam evaluation for BOTH sides of the surface. Pattern: tiled/flipbook
// texture dragged along the local flow; two half-offset phases cross-faded by a
// seesaw weight give endless drift with no visible reset. A rotated, rescaled
// second octave fades in with camera distance (the ocean whitecap's anti-tiling)
// so the pattern's repeat stops reading as a grid. Layers: dense white core
// where the mask is thick; as it thins the pattern's dark regions erode away
// first, so decaying foam breaks into filaments instead of ghosting out.
// Tilt: PROCEDURAL relief from finite differences of the pattern (Crest-style,
// matching the ocean whitecap - no normal map), scaled by the mask so sparse
// foam doesn't dent the shading.
// WGSL derivative uniformity: fuvDdx/fuvDdy are the SCREEN derivatives of fuv, hoisted
// by the caller BEFORE its non-uniform mask branch - every sample below runs in
// non-uniform control flow, where implicit-derivative tex2D/ddx/ddy are undefined.
// The flow/phase/relief offsets are ADDITIVE, so the base gradients stay exact; the
// rotated octave is a linear transform, so its gradients get the same rotation/scale.
void EvaluateFoam(float2 fuv, float2 fuvDdx, float2 fuvDdy,
                  float2 flowXZ, float mask, float camDist,
                  out float3 pattern, out float core, out float lace,
                  out float alpha, out float2 tilt)
{
    float2 flowDir = flowXZ * FOAM_FLOW_DISTANCE;
    float phaseA = frac(_Time.y * FOAM_FLOW_RATE);
    float phaseB = frac(phaseA + 0.5);
    float seesaw = abs(phaseA * 2.0 - 1.0);
    float2 uvA = fuv - flowDir * phaseA;
    float3 baseA = SampleFoamPattern(uvA, fuvDdx, fuvDdy);
    pattern = lerp(baseA, SampleFoamPattern(fuv - flowDir * phaseB, fuvDdx, fuvDdy), seesaw);

    // Distance anti-tiling, same recipe as SampleOceanWhitecapPattern: a rotated second
    // octave, mixed by the variance-preserving WhitecapOctaveBlend so the pattern's
    // histogram - and with it the dissolved foam DENSITY - stays what the threshold was
    // calibrated for at every distance (the old min() starved it; see the helper's header).
    float octaveBlend = saturate(camDist / OCEAN_WHITECAP_OCTAVE_BLEND_DIST);
    if (octaveBlend > 0.0)
    {
        float2 rotated = float2(
            fuv.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuv.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
            fuv.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuv.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
            / OCEAN_WHITECAP_OCTAVE2_SCALE;
        // Same linear transform applied to the hoisted gradients (exact, no new ddx).
        float2 rotDdx = float2(
            fuvDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuvDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
            fuvDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuvDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
            / OCEAN_WHITECAP_OCTAVE2_SCALE;
        float2 rotDdy = float2(
            fuvDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuvDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
            fuvDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuvDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
            / OCEAN_WHITECAP_OCTAVE2_SCALE;
        float3 octave1 = SampleFoamPattern(rotated - flowDir * phaseA, rotDdx, rotDdy);
        pattern = lerp(pattern, WhitecapOctaveBlend(pattern, octave1), octaveBlend);
    }

    core = smoothstep(FOAM_CORE_START, FOAM_CORE_FULL, mask);
    // Dissolve threshold with sqrt REACH (the KWS law the whitecap path already
    // uses): a THIN mask reaches high into the pattern, so light foam shows as a
    // few bright FLECKS tracking the ripple crests instead of nothing-then-blob.
    // (The old linear 1-mask threshold could exceed a midtone texture's maximum,
    // so thin foam vanished entirely and moderate foam jumped to solid patches.)
    float reach = sqrt(saturate(mask));
    float laceThreshold = 1.0 - reach;
    lace = saturate((pattern.r - laceThreshold) / FOAM_LACE_SOFTNESS);

    // Core cut (user-tunable): erode the dense core's alpha by the pattern -
    // same trick as the lace, wider band - so the core rim breaks into
    // texture detail instead of ending in a smooth mask blob. 0 = solid core
    // (original look). Even at full cut the lace term below keeps the
    // saturated centre near-solid; only the darkest pattern texels open up.
    float coreCut = saturate((pattern.r - laceThreshold) / FOAM_CORE_CUT_SOFTNESS);
    float coreAlpha = core * lerp(1.0, coreCut, _FoamCoreCut);

    // Edge feathering (user-tunable): fade the layer out smoothly as the
    // mask thins instead of clipping at the mask epsilon. 0 = off (hard
    // edge, the original look). Core is untouched by construction: it only
    // exists above FOAM_CORE_START, well over any sensible feather band.
    float feather = (_FoamFeather > 0.0) ? smoothstep(0.0, _FoamFeather, mask) : 1.0;
    // The reach term doubles as the fleck weight: thin-mask flecks stay readable
    // without linear dimming forcing the strength slider up into blob territory.
    alpha = max(coreAlpha, lace * reach) * feather;

    // Procedural relief (Crest MultiScaleFoamNormal): brightness reads as bubble
    // height, so the negated finite-difference gradient tilts the shading normal
    // away from raised foam. Taken at phase A of the base octave (relief slightly
    // lagging the crossfade is imperceptible; the offsets stay consistent).
    float rx = SampleFoamPattern(uvA + float2(FOAM_PROC_NORMAL_DELTA, 0.0), fuvDdx, fuvDdy).r;
    float rz = SampleFoamPattern(uvA + float2(0.0, FOAM_PROC_NORMAL_DELTA), fuvDdx, fuvDdy).r;
    tilt = -FOAM_PROC_NORMAL_GAIN * float2(rx - baseA.r, rz - baseA.r)
         * (_FoamNormalStrength * mask);
}

// Wind heading as a direction, shared with the detail-normal family (declared HERE because this
// header is included first in the pass - see the note in WaterSurfaceDetailNormal.hlsl).
float4 _WindDirection;
// Whitecap STREAK stretch: how many times longer the foam TEXTURE reads along the DRIFT (downwind)
// axis than across it. 1 = the original isotropic sampling, byte-identical.
float _OceanFoamStreakStretch;
// How much the foam TEXTURE owns the foam's OUTLINE (Ceto's Ceto_TextureWaveFoam). 1 = the shipped
// behaviour, where the dissolve threshold is applied to the texture so the shape IS the texture's
// iso-contours. Lower hands the outline back to the coverage field. Consumed at the dissolve call
// site in WaterSurfaceFragStages.hlsl, declared here beside the rest of the whitecap family.
float _OceanFoamTextureInfluence;
// Ceto-style foam absorption: how much thin foam takes the WATER'S colour instead of the flat tint.
// Consumed where the whitecap look is built (WaterSurfaceFragStages.hlsl). 0 = flat tint, unchanged.
float _OceanFoamDepthTint;

// Map a world XZ vector into the WIND-ALIGNED, stretched frame the whitecap pattern is sampled in.
//
// Real whitecaps read as STREAKS, not patches: foam is born along the breaking crest and is then
// dragged downwind, so the field is elongated along the wind. Our foam artwork is an isotropic
// cellular pattern, and it was sampled with a plain worldXZ / tileSize - isotropic in, isotropic out,
// which is why caps came out round. Sampling that SAME texture through a stretched wind-aligned frame
// turns its cells into filaments, so the look comes out of the texture already shipped.
//
// The map is LINEAR, so the caller can push the screen-space derivatives through it unchanged and the
// tex2Dgrad footprint stays exact. Across-wind is untouched, so the tile size keeps meaning "metres
// across the streaks" and only the along-wind span is rescaled.
float2 WhitecapStreakFrame(float2 v)
{
    float2 wind = (dot(_WindDirection.xy, _WindDirection.xy) > 1e-6)
                ? normalize(_WindDirection.xy) : float2(1.0, 0.0);
    // ALONG THE WIND, deliberately - this is the DEPOSIT's axis. Foam already laid down is rolled
    // downwind by advection (OceanFoamDriftFraction), so smearing the texture the same way is what
    // makes a trail read as a trail.
    // Crest-aligned STRIPES are a different job and are NOT done here: a texture frame can only
    // stretch DETAIL, it can never change which parts of the sea are foamy. That outline belongs to
    // the coverage field - see OceanFoamAnisotropy (which folds count) and _OceanFoamTextureInfluence
    // (who owns the outline) at the dissolve site.
    // Divide the along-wind axis so a longer world span maps into the same texture span; across-wind
    // is untouched, so the tile size keeps meaning "metres across the streak".
    return float2(dot(v, wind) / max(_OceanFoamStreakStretch, 1e-3),
                  dot(v, float2(-wind.y, wind.x)));
}

// Ocean whitecap pattern with distance anti-tiling. Combines the base foam tile with a rotated,
// differently-scaled second octave that fades in with distance, so the texture's repeat stops
// reading as a grid toward the horizon. The octaves merge through WhitecapOctaveBlend (see its
// header) - a variance-preserving weighted blend, NOT min(): min() kept foam only where both
// octaves agreed, which collapsed the pattern histogram and read as ~10x sparser foam past the
// blend distance (fixed 2026-07-31; do not reintroduce). Returns
// the pattern rgb; .r drives the coverage dissolve.
// tileSize is a PARAMETER so the surf whitewash can reuse this exact pipeline with its
// own dedicated tiling (decoupled from the ocean whitecap knob); the no-arg wrappers
// below keep the ocean call sites unchanged.
// WGSL derivative uniformity: worldDdx/worldDdy are the screen derivatives of the BASE
// (pre-parallax) world XZ, hoisted by the caller in uniform control flow - these taps run
// inside non-uniform coverage branches where implicit-derivative tex2D is undefined. The
// parallax lift is ADDITIVE so the base gradients are exact; the tile divide and the
// rotated octave are linear, so the gradients get the same scale/rotation.
float3 SampleOceanWhitecapPatternTiled(float2 worldXZ, float camDist, float tileSize,
                                       float2 worldDdx, float2 worldDdy)
{
    float tile0 = max(tileSize, 1e-3);
    // Optional flipbook, shared by BOTH the deep ocean caps AND the surf whitewash (they both
    // sample through here). A real grid plays animated frames; the distance anti-tiling OCTAVE is
    // a seamless-tiling trick a flipbook atlas can't use, so it's skipped in flipbook mode. (1,1)
    // grid = the original seamless tiling path below, byte-identical.
    if (_OceanWhitecapFrames.x * _OceanWhitecapFrames.y > 1.0)
        return SampleFlipbookPattern(_OceanWhitecapTex, _OceanWhitecapFrames.xy, _OceanWhitecapFPS,
                                     _OceanWhitecapTex_TexelSize.xy, worldXZ / tile0,
                                     worldDdx / tile0, worldDdy / tile0);
    // Dedicated whitecap: a single seamless tiling texture sampled with hardware Repeat wrap -
    // no frac/flipbook cell, so no atlas mip-bleed and no tile-edge seam. The rotated second
    // octave still hides the texture's own repeat toward the horizon.
    float2 uv0 = worldXZ / tile0;
    float3 octave0 = tex2Dgrad(_OceanWhitecapTex, uv0, worldDdx / tile0, worldDdy / tile0).rgb;

    float2 rotated = float2(
        worldXZ.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldXZ.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
        worldXZ.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldXZ.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS);
    float tile1 = max(tileSize * OCEAN_WHITECAP_OCTAVE2_SCALE, 1e-3);
    float2 rotDdx = float2(
        worldDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
        worldDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS) / tile1;
    float2 rotDdy = float2(
        worldDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
        worldDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS) / tile1;
    float3 octave1 = tex2Dgrad(_OceanWhitecapTex, rotated / tile1, rotDdx, rotDdy).rgb;

    float blend = saturate(camDist / OCEAN_WHITECAP_OCTAVE_BLEND_DIST);
    return lerp(octave0, WhitecapOctaveBlend(octave0, octave1), blend);
}

float3 SampleOceanWhitecapPattern(float2 worldXZ, float camDist,
                                  float2 worldDdx, float2 worldDdy)
{
    // The streak frame is applied HERE, in the deep-ocean wrapper only. The Tiled entry point is
    // shared with the surf whitewash and the swash, whose foam aligns to the SHORE, not the wind -
    // stretching those along the wind would be wrong. Pre-transforming the inputs also means the
    // shared pipeline (tile divide, rotated anti-tiling octave, flipbook) needs no new parameter.
    return SampleOceanWhitecapPatternTiled(WhitecapStreakFrame(worldXZ), camDist, _OceanFoamTileSize,
                                           WhitecapStreakFrame(worldDdx), WhitecapStreakFrame(worldDdy));
}

// Relief tilt (xy) of the whitecap, derived PROCEDURALLY from the albedo tile by finite
// differences (Crest's MultiScaleFoamNormal): brightness reads as bubble height, so the
// negated gradient tilts the shading normal away from raised foam. Self-flattening - where
// there is no foam the gradient is ~0 - and it retires the separate normal-map texture
// (_OceanWhitecapNormalTex kept only as an unused asset on disk).
// WGSL derivative uniformity: same hoisted-gradient contract as the pattern sampler above -
// called inside non-uniform foam branches, so the finite-difference taps use explicit
// gradients (the tap offsets are additive, so all three share the base uv gradients).
float2 SampleOceanWhitecapTiltTiled(float2 worldXZ, float tileSize,
                                    float2 worldDdx, float2 worldDdy)
{
    float tile = max(tileSize, 1e-3);
    float dd = tile * OCEAN_FOAM_NORMAL_DELTA;
    float2 uvDdx = worldDdx / tile;
    float2 uvDdy = worldDdy / tile;
    // Flipbook relief: finite-difference the CURRENT animated frame (same texture path as the
    // albedo) so the bubble bumps match what's actually drawn. Tiling path unchanged at (1,1).
    if (_OceanWhitecapFrames.x * _OceanWhitecapFrames.y > 1.0)
    {
        float fc  = SampleFlipbookPattern(_OceanWhitecapTex, _OceanWhitecapFrames.xy, _OceanWhitecapFPS,
                        _OceanWhitecapTex_TexelSize.xy, worldXZ / tile, uvDdx, uvDdy).r;
        float fcx = SampleFlipbookPattern(_OceanWhitecapTex, _OceanWhitecapFrames.xy, _OceanWhitecapFPS,
                        _OceanWhitecapTex_TexelSize.xy, (worldXZ + float2(dd, 0.0)) / tile, uvDdx, uvDdy).r;
        float fcz = SampleFlipbookPattern(_OceanWhitecapTex, _OceanWhitecapFrames.xy, _OceanWhitecapFPS,
                        _OceanWhitecapTex_TexelSize.xy, (worldXZ + float2(0.0, dd)) / tile, uvDdx, uvDdy).r;
        return -OCEAN_FOAM_NORMAL_GAIN * float2(fcx - fc, fcz - fc);
    }
    float c  = tex2Dgrad(_OceanWhitecapTex, worldXZ / tile, uvDdx, uvDdy).r;
    float cx = tex2Dgrad(_OceanWhitecapTex, (worldXZ + float2(dd, 0.0)) / tile, uvDdx, uvDdy).r;
    float cz = tex2Dgrad(_OceanWhitecapTex, (worldXZ + float2(0.0, dd)) / tile, uvDdx, uvDdy).r;
    return -OCEAN_FOAM_NORMAL_GAIN * float2(cx - c, cz - c);
}

float2 SampleOceanWhitecapTilt(float2 worldXZ, float2 worldDdx, float2 worldDdy)
{
    return SampleOceanWhitecapTiltTiled(worldXZ, _OceanFoamTileSize, worldDdx, worldDdy);
}

// Tilt the shading normal by a foam relief tilt (xy = xz slope) in the surface's
// local tangent frame. ONE shared frame construction for every foam layer (ocean
// whitecap, pond foam, surf whitewash), so their relief shading can never diverge.
// NaN guard: a normal pushed near +/-Z (the large-body tilt path can) makes
// cross(normal, Z) degenerate and normalize() NaNs every foam layer's shading;
// fall back to the X axis there (DEGENERATE_DIR_EPSILON from WaterShared.hlsl).
float3 ApplyFoamTiltToNormal(float3 normal, float2 tilt)
{
    float3 rawTangent = cross(normal, float3(0.0, 0.0, 1.0));
    if (dot(rawTangent, rawTangent) < DEGENERATE_DIR_EPSILON)
        rawTangent = cross(normal, float3(1.0, 0.0, 0.0));
    float3 tangent = normalize(rawTangent);
    float3 bitangent = cross(normal, tangent);
    return normalize(normal + tangent * tilt.x + bitangent * tilt.y);
}

// Shared KWS dissolve law for every whitecap-pipeline foam layer (ocean caps,
// surf whitewash, swash line): dense coverage RELAXES the contrast so heavy foam
// goes solid instead of staying lacy, and the dissolve threshold falls with
// sqrt(coverage) so mid coverage reaches further into the pattern.
// extraThreshold RAISES the cut (age/reflux erosion): aged foam rots into holes,
// then filaments, then nothing. Pass 0 for layers without an erosion term.
// Threshold + contrast for the dissolve. Factored out so the dissolve and its EXPECTED value below
// read from ONE definition - if they ever disagreed, near and far foam would drift apart in exactly
// the way FoamDissolveExpected exists to prevent.
void FoamDissolveTerms(float coverage, float extraThreshold, out float threshold, out float contrast)
{
    float coverageSat = saturate(coverage);
    contrast = lerp(OCEAN_WHITECAP_CONTRAST, OCEAN_WHITECAP_CONTRAST_DENSE, coverageSat);
    threshold = 1.0 - sqrt(coverageSat) + extraThreshold;
}

float FoamDissolve(float patternValue, float coverage, float feather, float extraThreshold)
{
    float threshold, contrast;
    FoamDissolveTerms(coverage, extraThreshold, threshold, contrast);
    float sharpened = pow(saturate(patternValue), contrast);
    return smoothstep(threshold, threshold + max(feather, 1e-3), sharpened);
}

// The dissolve's EXPECTED value, for use once the pattern can no longer be resolved (distance, mips).
//
// The far field has to show the same AVERAGE amount of foam as the near field. Handing it the raw
// COVERAGE does not: the dissolve keeps only the fraction of the pattern that clears its threshold,
// which is always less than the coverage, so distant foam comes out brighter than the near foam it is
// meant to match.
//
// The fraction that clears the threshold is 1 - CDF(pattern value at the band), so this needs the
// pattern's HISTOGRAM. An earlier version assumed the histogram was UNIFORM over 0..1. It is not, and
// the error was not small: measured against the shipped OceanWhitecap.png, the uniform model
// overestimated the foam by 2.7x at coverage 0.2 and 6.3x at 0.05 - i.e. exactly in the sparse-foam
// range an open ocean runs at, which is why distant water carried MORE foam than close water.
//
// The real histogram is tightly clustered - mean 0.501, standard deviation 0.189, deciles running
// 0.26 -> 0.75 - so a GAUSSIAN model of it is both accurate and cheap. Evaluate its complementary CDF
// at the pattern value corresponding to the MIDDLE of the dissolve's feather band (the smoothstep's
// 50% point), using a smoothstep as the CDF surrogate: max absolute error 0.057 across coverage
// 0.02..1 and feather 0.05..1, against 0.2+ for the uniform model.
//
// Feather therefore reaches the far field, which it must: it is the same band the near-field
// smoothstep uses, so raising it dims distant foam exactly as it softens near foam.
//
// The OCEAN_FOAM_PATTERN_* histogram constants live in the whitecap-constants block near
// the top of this file (ONE home): the variance-preserving octave blend reads the mean
// too, and up there the defines precede both use sites. The ⚠️ swap-your-own-texture
// calibration warning lives with them.
float FoamDissolveExpected(float coverage, float feather, float extraThreshold)
{
    float threshold, contrast;
    FoamDissolveTerms(coverage, extraThreshold, threshold, contrast);
    // Pattern value at the 50% point of the dissolve band, undoing the contrast sharpen.
    float midBand = pow(saturate(threshold + 0.5 * max(feather, 1e-3)), 1.0 / max(contrast, 1e-3));
    float z = (midBand - OCEAN_FOAM_PATTERN_MEAN) / OCEAN_FOAM_PATTERN_STDDEV;
    return smoothstep(-OCEAN_FOAM_PATTERN_CDF_SPAN, OCEAN_FOAM_PATTERN_CDF_SPAN, -z);
}

#endif // WATER_SURFACE_FOAM_SAMPLING_INCLUDED
