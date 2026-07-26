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
float _SurfSwashStreak;        // 0..1 downslope streak stretch during the backwash
float _ShoreSwashDepositGain;  // FOAM-5: >0 = persistent swash deposits live in the foam buffer;
                               // the surface then lifts + keeps the beach alive under them so they
                               // dissolve on the sand instead of blinking off when the wet line recedes
// How far age can push the pattern-dissolve threshold (a full push leaves only the
// brightest pattern peaks alive - lace filaments, then nothing).
#define SURF_TRAIL_ERODE_MAX   0.6
#define SURF_SWASH_ERODE_MAX   0.7
// Full-streak elongation factor of the backwash drain marks at _SurfSwashStreak = 1.
#define SURF_SWASH_STREAK_GAIN 3.0
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

    // Distance anti-tiling, same recipe as SampleOceanWhitecapPattern: min() of a
    // rotated second octave keeps foam only where BOTH octaves agree, breaking the
    // repeat into irregular shapes toward the distance.
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
        pattern = lerp(pattern, min(pattern, octave1), octaveBlend);
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

// Ocean whitecap pattern with distance anti-tiling. Combines the base foam tile with a rotated,
// differently-scaled second octave that fades in with distance, so the texture's repeat stops
// reading as a grid toward the horizon. min() of the two octaves as they blend keeps foam only
// where BOTH agree, which also breaks the round patches into more whitecap-like shapes. Returns
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
    return lerp(octave0, min(octave0, octave1), blend);
}

float3 SampleOceanWhitecapPattern(float2 worldXZ, float camDist,
                                  float2 worldDdx, float2 worldDdy)
{
    return SampleOceanWhitecapPatternTiled(worldXZ, camDist, _OceanFoamTileSize, worldDdx, worldDdy);
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
float FoamDissolve(float patternValue, float coverage, float feather, float extraThreshold)
{
    float coverageSat = saturate(coverage);
    float contrast = lerp(OCEAN_WHITECAP_CONTRAST, OCEAN_WHITECAP_CONTRAST_DENSE, coverageSat);
    float sharpened = pow(saturate(patternValue), contrast);
    float threshold = 1.0 - sqrt(coverageSat) + extraThreshold;
    return smoothstep(threshold, threshold + max(feather, 1e-3), sharpened);
}

#endif // WATER_SURFACE_FOAM_SAMPLING_INCLUDED
