// WaterSurface pass: Crest-style crossing scrolling detail normals.
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. The tex2D taps use IMPLICIT derivatives, so
// DetailNormalTilt may only ever be called from UNIFORM control flow (the
// caller's strength/underwater gates) - see the WGSL note on the function.
#ifndef WATER_SURFACE_DETAIL_NORMAL_INCLUDED
#define WATER_SURFACE_DETAIL_NORMAL_INCLUDED

// Crest-style crossing detail normals: the two fixed crossing directions are Crest's
// own (non-orthogonal, non-axis-aligned, so the two scrolls never read as a grid) -
// and the two layers now also sit at DIFFERENT, irrationally-related tile sizes, so
// their repeats can never coincide either.
//
// AN OCTAVE LADDER, not two fixed layers. This used to be a near tile and one far tile
// crossfaded over [BLEND_START, BLEND_START+BLEND_RANGE] - and past that the far tile
// was the ONLY layer and its world size was FIXED. Distance kept growing, the screen
// footprint kept shrinking, and with nothing stepping up to meet it the repeat became
// the most visible tiling left on the surface once the FFT cascades stopped repeating.
// Raising _DetailNormalScale to fix it only traded the problem for a coarse near field,
// because that one number sets both ends.
//
// The tile now climbs continuously with view distance, but only ever TWO octaves are
// sampled - the pair straddling a fractional octave index, blended by its fraction,
// exactly how hardware mip blending works. So:
//   * cost is unchanged at four taps (two crossing directions x two live octaves);
//   * each octave stays WORLD-LOCKED, so nothing swims under the camera - only the
//     blend weight is view-dependent, which is the difference between this and simply
//     scaling the tile with distance;
//   * the climb STOPS at an authored far tile, because "keep growing forever" is not
//     what looks right - past a few times the near tile the pattern stops reading as
//     micro-ripple and starts reading as large blotches. Both ends are authored in
//     metres (Tile Meters / Far Tile Meters), plus the DISTANCE the far one is reached
//     at (Far Tile Distance) - which is what actually sets the climb rate, and without
//     which the far tile appears to top out on its own well below where it was set.
// Octave 0 reproduces the OLD near layer exactly, and the ladder is anchored so that
// octave 1 lands where the old crossfade completed - the 0-120 m look is therefore
// unchanged, and everything new happens where there was nothing before.
#define DETAIL_NORMAL_DIR0            float2(0.94, 0.34)
#define DETAIL_NORMAL_DIR1            float2(-0.85, -0.53)
// Tile ratio is the golden ratio SQUARED - the same
// low-discrepancy constant WaterWaveBank stratifies its wave headings with. Two reasons, not one:
// (1) an irrational ratio never lets the two layers beat back into phase, where the exact octave
// this used to be re-synced constantly and read as a grid; (2) sqrt(tile ratio) IS the deep-water
// dispersion relation c = sqrt(g*lambda / 2pi), so the LONGER far layer now travels FASTER than the
// near one. It previously ran at HALF speed while carrying twice the wavelength - dispersion
// backwards, and the main reason the distance layer read as sludge rather than as moving water.
#define DETAIL_NORMAL_FAR_TILE_MULT   2.6180340
// The two crossing layers straddle the authored tile instead of sharing it: one at tile/S, one at
// tile*S, so the AUTHORED size stays their geometric mean and the look barely shifts. S is the square
// root of the golden ratio, so the two periods are in an IRRATIONAL ratio and the pair has no common
// period at all - layer A repeating every 9.4 m and layer B every 15.3 m never re-align, where two
// layers on one tile repeat together and stack their grids into the one artefact you can still see.
// Splitting the tile was the last axis left aligned; the directions and the octaves were already
// irrational. Costs nothing - same four taps, different constants.
#define DETAIL_NORMAL_CROSS_TILE_SPLIT  1.2720196
// ...and their speeds split by the square root of THAT, because c = sqrt(g*lambda/2pi): the longer
// layer has to travel faster or the pair reads as one sheet sliding over a stationary one.
#define DETAIL_NORMAL_CROSS_SPEED_SPLIT 1.1278865
// Sine of the surface tilt treated as a fully steep wave face by the crest boost (~20 degrees).
#define DETAIL_CREST_REFERENCE_SLOPE  0.35
// Octave 0 is held all the way in to here, so the near field never changes with camera distance.
#define DETAIL_NORMAL_FAR_BLEND_START 30.0
// Guards for the DERIVED climb rate: a far distance at or inside the near band, or a far tile at or
// below the near tile, would otherwise divide by zero. Both degrade to "no climb", which is the
// sensible reading of either input.
#define DETAIL_NORMAL_MIN_FAR_SPAN    1.0
#define DETAIL_NORMAL_MIN_TILE_RATIO  1e-3
// The far fade used to start at 250 m because that is roughly where the FIXED far tile began to
// shimmer. The ladder removes that reason - each octave's screen-space frequency is bounded - so
// this is now only about how far it is worth paying for. Extending it is FREE in tap count: all
// four taps run unconditionally either way (the fade is a multiply, never a branch, because a
// per-pixel branch around texture derivatives is undefined on WGSL), so the samples beyond the old
// 600 m cutoff were already being taken and multiplied by zero.
#define DETAIL_NORMAL_FADE_START      1200.0
#define DETAIL_NORMAL_FADE_RANGE      1800.0
// Guards the tile divide. _DetailNormalScale is authored and can legitimately be dragged to 0.
#define DETAIL_NORMAL_MIN_TILE        1e-3

sampler2D _DetailNormalTex; // tiling water normals; default "bump" = flat = feature inert
float _DetailNormalStrength, _DetailNormalScale, _DetailNormalSpeed;
// World tile the ladder tops out at, in metres. The climb is capped rather than open-ended: the
// useful range is a few times the near tile, and beyond that the texture stops being micro-ripple.
// Below the near tile it degenerates to a single fixed octave, which is the pre-ladder behaviour.
float _DetailNormalFarScale;
// View distance (m) at which the far tile is reached - i.e. how FAST the ladder climbs, which the far
// tile alone cannot express: a cap only trims a climb that has already happened.
float _DetailNormalFarDistance;
// Scroll speed at the far tile. Authored rather than derived, for a reason worth knowing: apparent
// motion on screen is world speed over distance, so once the tile caps and the octave freezes, the
// far water's screen motion keeps falling as 1/d and settles into sludge. Dispersion says the far
// speed "should" be near * sqrt(tileRatio) - the inspector prints that value - but the honest
// physical answer is not always the readable one at range, so this is a knob and not a formula.
float _DetailNormalFarSpeed;
// 0 = one tap per layer (the tiling texture repeats on its own grid); 1 = hexagonal stochastic
// tiling, three offset copies blended per layer. UNIFORM across the draw, so the branch below is
// coherent - and branching is only safe here at all because the taps carry EXPLICIT gradients now.
float _DetailNormalHexTiling;

// --- Hexagonal stochastic tiling (Heitz & Neyret, HPG 2018; the operator Ubisoft's HPG 2024 ocean
// paper uses on its FFT output). The texture is resampled on a triangular lattice: each lattice cell
// gets a hash-random offset into the map, and the three cells overlapping a fragment are blended by
// their barycentric weights. Because every cell shows a DIFFERENT part of the texture, the map's own
// repeat stops existing - what remains is the lattice, which the blend hides.
//
// Two shortcuts are deliberate. Heitz & Neyret Gaussianize the exemplar through a histogram LUT
// first; a water normal map's xy are already near zero-mean and symmetric, so that step buys nothing
// here and the operator collapses to the same variance-preserving blend the octave crossfade uses.
// And the cells are OFFSET but not ROTATED - rotation kills the residual lattice alignment but has
// to un-rotate every sampled normal to stay correct, which is a bigger change than this one.
#define DETAIL_HEX_LATTICE_SCALE 3.4641016   // 2*sqrt(3): cells per texture repeat, Heitz's default
// Barycentric weights blend over the whole cell, which softens detail near the boundaries. Raising
// them to a power narrows the transition without reintroducing seams, because the variance restore
// below keeps the contrast up where the blend is widest.
#define DETAIL_HEX_WEIGHT_EXPONENT 3.0

// Cheap integer hash per lattice cell -> a uv offset inside the unit square. Deliberately NOT the
// sin-based hash the reference uses: it runs three times per layer per octave, and two
// transcendentals apiece is a real cost per water pixel for a value that only needs scattering.
float2 DetailHexHash(int2 cell)
{
    uint2 q = (uint2)(cell + 1024);              // bias off negatives before the unsigned mix
    uint h = q.x * 1597334677u ^ q.y * 3812015801u;
    h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
    return float2(h & 0xFFFFu, (h >> 16) & 0xFFFFu) * (1.0 / 65536.0);
}

// Triangular lattice: the three cells overlapping this uv and their barycentric weights.
void DetailHexLattice(float2 uv, out float3 weights, out int2 cellA, out int2 cellB, out int2 cellC)
{
    const float2x2 gridToSkewed = float2x2(1.0, -0.57735027, 0.0, 1.15470054);
    float2 skewed = mul(gridToSkewed, uv * DETAIL_HEX_LATTICE_SCALE);
    int2 baseCell = int2(floor(skewed));
    float3 bary = float3(frac(skewed), 0.0);
    bary.z = 1.0 - bary.x - bary.y;
    // Which half of the skewed cell the fragment fell in decides the winding of the three corners.
    if (bary.z > 0.0)
    {
        weights = float3(bary.z, bary.y, bary.x);
        cellA = baseCell;
        cellB = baseCell + int2(0, 1);
        cellC = baseCell + int2(1, 0);
    }
    else
    {
        weights = float3(-bary.z, 1.0 - bary.y, 1.0 - bary.x);
        cellA = baseCell + int2(1, 1);
        cellB = baseCell + int2(1, 0);
        cellC = baseCell + int2(0, 1);
    }
}

// One tilt sample: a plain tap, or three hash-offset taps blended variance-preservingly.
// The three offsets are CONSTANT per cell, so all three taps share the caller's gradients - the
// footprint is unchanged by an offset, and recomputing it per copy would be both wrong and dearer.
float2 DetailNormalSample(float2 uv, float2 uvDdx, float2 uvDdy)
{
    if (_DetailNormalHexTiling < 0.5)
        return UnpackNormal(tex2Dgrad(_DetailNormalTex, uv, uvDdx, uvDdy)).xy;

    float3 weights;
    int2 cellA, cellB, cellC;
    DetailHexLattice(uv, weights, cellA, cellB, cellC);
    weights = pow(max(weights, 0.0), DETAIL_HEX_WEIGHT_EXPONENT);
    weights /= max(weights.x + weights.y + weights.z, 1e-6);

    float2 tiltA = UnpackNormal(tex2Dgrad(_DetailNormalTex, uv + DetailHexHash(cellA), uvDdx, uvDdy)).xy;
    float2 tiltB = UnpackNormal(tex2Dgrad(_DetailNormalTex, uv + DetailHexHash(cellB), uvDdx, uvDdy)).xy;
    float2 tiltC = UnpackNormal(tex2Dgrad(_DetailNormalTex, uv + DetailHexHash(cellC), uvDdx, uvDdy)).xy;

    // Same operator as the octave crossfade: three independent copies averaged lose variance as
    // sqrt(sum of squared weights), and dividing it back out is what stops the blend zones reading
    // as flat patches between the cells.
    float2 blended = weights.x * tiltA + weights.y * tiltB + weights.z * tiltC;
    return blended / sqrt(max(dot(weights, weights), 1e-6));
}
#define DETAIL_NORMAL_MIN_SPEED       1e-4
#define DETAIL_NORMAL_MIN_OCTAVE_SPAN 1e-3
// Gain that recovers the amplitude the MIP CHAIN eats once the ladder has hit its far tile. Up to
// the cap the tile grows with distance, so the uv footprint - and therefore the mip - stays roughly
// put and the detail holds its strength. Past the cap the tile is frozen while distance keeps
// growing, so each pixel covers more texels, the normal map averages toward flat, and the ripple
// washes out exactly where you are looking when you scan the horizon. Compensating there (and only
// there) is what stops "make it stronger at distance" from also overdriving the water at your feet.
float _DetailNormalDistanceBoost;
// ONE wind for the whole surface: (cos, sin) of the heading in XZ, the same convention
// WaterWaveBank.Generate builds its component directions from. Identity (1, 0) at heading 0, which
// every shipped body uses, so the rotation below is a no-op until wind is turned.
// DECLARED IN WaterSurfaceFoamSampling.hlsl, not here: the whitecap streak frame needs it too and
// that header is included FIRST in this pass (WaterSurface.shader), so one declaration has to serve
// both - HLSL has no way to declare the same uniform twice in one compilation unit. Both headers are
// used only by WaterSurface.shader, so there is no other include order to satisfy.
float _DetailNormalCrestBoost; // applied by the CALLER (it needs the composed surface normal)

// One octave: the two crossing scrolling taps at a given world tile and scroll speed.
//
// tex2Dgrad, NOT tex2D, and that is load-bearing. The tile is a function of floor(octave), so two
// pixels either side of an octave boundary map the SAME world position to uv values a factor of
// 2.618 apart. Hardware derivatives are differences across the quad, so an implicit-derivative tap
// would read that jump as an enormous uv gradient, drop to the coarsest mip and draw a hard seam
// along every octave boundary. Passing gradients derived analytically from the WORLD-space
// derivatives - which are continuous - gives each pixel the right footprint for its own tile.
//
// The blended RESULT stays continuous across the boundary on its own: at fraction 1 this pair
// resolves to octave n+1, and the neighbouring pixel at fraction 0 resolves to that same octave.
float2 DetailNormalOctave(float2 worldXZ, float2 scroll0, float2 scroll1, float scrollSpeed,
                          float tile, float2 worldDdx, float2 worldDdy)
{
    // The two layers straddle the authored tile in an irrational ratio (see the CROSS_ constants), so
    // each needs its own scale, its own speed and - because the gradients are analytic - its own
    // footprint. The authored size remains their geometric mean.
    float safeTile = max(tile, DETAIL_NORMAL_MIN_TILE);
    float invTile0 = DETAIL_NORMAL_CROSS_TILE_SPLIT / safeTile;
    float invTile1 = 1.0 / (safeTile * DETAIL_NORMAL_CROSS_TILE_SPLIT);
    float speed0 = scrollSpeed / DETAIL_NORMAL_CROSS_SPEED_SPLIT;
    float speed1 = scrollSpeed * DETAIL_NORMAL_CROSS_SPEED_SPLIT;
    float2 uv0 = (worldXZ + scroll0 * speed0) * invTile0;
    float2 uv1 = (worldXZ + scroll1 * speed1) * invTile1;
    return DetailNormalSample(uv0, worldDdx * invTile0, worldDdy * invTile0)
         + DetailNormalSample(uv1, worldDdx * invTile1, worldDdy * invTile1);
}

// ---- Crest-style detail normal: two CROSSING, SCROLLING samples of a tiling normal
// map, taken at the two octaves straddling a distance-driven fractional octave index
// and blended by its fraction (see the header block). Returns an xz slope tilt for the
// world normal. All four taps always run - the distance fade is a multiply, not a
// branch, because a per-pixel branch around texture derivatives is undefined on WGSL;
// the caller's gate (strength knob + above-water) is uniform, which IS branch-safe. ----
float2 DetailNormalTiltScrolled(float2 surfaceCoordinates, float2 scroll0, float2 scroll1,
                                float nearSpeed, float farSpeed, float viewDist)
{
    // Surface-space derivatives, taken ONCE on the continuous quantity. Every octave scales these by
    // its own tile rather than letting the hardware difference a discontinuous uv (see the octave
    // helper). Valid because this is fragment-only - the single caller passes a varying.
    float2 worldDdx = ddx(surfaceCoordinates);
    float2 worldDdy = ddy(surfaceCoordinates);

    // THREE authored numbers describe the whole ladder: the near tile, the far tile, and the DISTANCE
    // at which the far tile is reached. The climb rate is then DERIVED rather than guessed - solving
    // "the ladder equals maxOctave at farDistance" for the reference gives the expression below.
    //
    // The distance is not optional convenience. A cap can only ever make the tile SMALLER than the
    // ladder would have produced, so beyond the point where the natural climb has already overtaken
    // the cap, raising the far tile does nothing at all - which reads as the far tile silently
    // topping out somewhere it was never set to.
    float tileRatio = max(_DetailNormalFarScale, DETAIL_NORMAL_MIN_TILE)
                    / max(_DetailNormalScale, DETAIL_NORMAL_MIN_TILE);
    float maxOctave = max(0.0, log2(max(tileRatio, DETAIL_NORMAL_MIN_TILE))
                               / log2(DETAIL_NORMAL_FAR_TILE_MULT));
    float octaveReference = max(_DetailNormalFarDistance - DETAIL_NORMAL_FAR_BLEND_START,
                                DETAIL_NORMAL_MIN_FAR_SPAN)
                          / max(tileRatio - 1.0, DETAIL_NORMAL_MIN_TILE_RATIO);

    // Fractional octave. Logarithmic in distance because the ladder is geometric: each step
    // MULTIPLIES the tile, so equal screen-space detail costs equal steps in log space. The max()
    // pins octave 0 all the way in to the camera, which is what keeps the near field as it was.
    float ladderOctave = log2(1.0 + max(0.0, viewDist - DETAIL_NORMAL_FAR_BLEND_START)
                                    / octaveReference)
                       / log2(DETAIL_NORMAL_FAR_TILE_MULT);
    // The cap is what HOLDS the tile once it arrives; a fractional cap simply parks the crossfade
    // part-way between two octaves and keeps it there.
    float octave = min(ladderOctave, maxOctave);

    float octaveIndex = floor(octave);
    float octaveFrac = octave - octaveIndex;

    float tileNear = _DetailNormalScale * pow(DETAIL_NORMAL_FAR_TILE_MULT, octaveIndex);
    // Speed climbs per octave too, on a ratio DERIVED from the authored near/far pair so that the
    // far speed lands exactly when the far tile does. Leaving it on a fixed multiplier would have
    // meant the one number that fights distance-induced sludge was the one number not exposed.
    float speedRatio = max(farSpeed, DETAIL_NORMAL_MIN_SPEED)
                     / max(nearSpeed, DETAIL_NORMAL_MIN_SPEED);
    float speedPerOctave = (maxOctave > DETAIL_NORMAL_MIN_OCTAVE_SPAN)
                         ? pow(speedRatio, 1.0 / maxOctave) : 1.0;
    float speedNear = nearSpeed * pow(speedPerOctave, octaveIndex);

    float2 tiltNear = DetailNormalOctave(surfaceCoordinates, scroll0, scroll1, speedNear,
                                         tileNear, worldDdx, worldDdy);
    float2 tiltFar = DetailNormalOctave(surfaceCoordinates, scroll0, scroll1,
                                        speedNear * speedPerOctave,
                                        tileNear * DETAIL_NORMAL_FAR_TILE_MULT,
                                        worldDdx, worldDdy);

    // VARIANCE-PRESERVING crossfade, not a plain lerp. The two octaves are statistically independent
    // fields, so lerp(a, b, f) carries variance (1-f)^2 + f^2 - which bottoms out at HALF mid-blend,
    // i.e. the detail visibly weakens by ~29% every time the ladder passes between octaves and
    // recovers at the boundaries. Climbing or descending, that reads as the micro-ripple pulsing.
    // Dividing by the blend's own RMS restores it: 1 at either end, sqrt(2) in the middle. Same
    // operator as Burley's histogram-preserving tile blend, and the same reason the ocean cascades
    // needed their measure fixing - averaging independent fields loses energy unless you put it back.
    float blendRms = sqrt(max((1.0 - octaveFrac) * (1.0 - octaveFrac) + octaveFrac * octaveFrac, 1e-4));
    float2 tilt = lerp(tiltNear, tiltFar, octaveFrac) / blendRms;

    // How many octaves of ladder the cap denied us - i.e. how far the mip has had to coarsen instead
    // of the tile growing. Zero until the cap bites, then it climbs at the same log rate the ladder
    // would have. Applied as a gain, so at boost 0 this is inert and the layer is exactly as before.
    float cappedOctaves = max(0.0, ladderOctave - maxOctave);
    tilt *= 1.0 + _DetailNormalDistanceBoost * cappedOctaves;

    float fade = 1.0 - saturate((viewDist - DETAIL_NORMAL_FADE_START)
                                / DETAIL_NORMAL_FADE_RANGE);
    return tilt * fade;
}

float2 DetailNormalTilt(float2 worldXZ, float viewDist)
{
    // Rotate the two crossing directions INTO the wind frame (a complex multiply). They stay Crest's
    // non-orthogonal pair: the ANGLE BETWEEN them is what stops the two scrolls reading as a grid,
    // and a rotation preserves it exactly. Guarded so an unpublished uniform cannot collapse both
    // directions to zero and freeze the scroll.
    float2 wind = (dot(_WindDirection.xy, _WindDirection.xy) > 1e-6)
                ? _WindDirection.xy : float2(1.0, 0.0);
    float2 dir0 = float2(DETAIL_NORMAL_DIR0.x * wind.x - DETAIL_NORMAL_DIR0.y * wind.y,
                         DETAIL_NORMAL_DIR0.x * wind.y + DETAIL_NORMAL_DIR0.y * wind.x);
    float2 dir1 = float2(DETAIL_NORMAL_DIR1.x * wind.x - DETAIL_NORMAL_DIR1.y * wind.y,
                         DETAIL_NORMAL_DIR1.x * wind.y + DETAIL_NORMAL_DIR1.y * wind.x);
    return DetailNormalTiltScrolled(
        worldXZ, dir0 * _WaveTime, dir1 * _WaveTime,
        _DetailNormalSpeed, _DetailNormalFarSpeed, viewDist);
}

#define RIVER_DETAIL_CROSSING_X 0.18
#define RIVER_DETAIL_DOWNSTREAM_Y -0.983666

float2 RiverDetailNormalTilt(float4 currentData, float viewDist)
{
    // Sampling travels upstream in river UV space, which makes the visible pattern move downstream.
    // The slight crossing angle keeps the two normal taps organic without letting wind steer flow
    // across a bend. Velocity comes from the settled bake (or spline speed before a bake exists),
    // in the same metres/second contract used by WaterRiverCurrentField.
    float2 riverDirection0 = float2(
        RIVER_DETAIL_CROSSING_X, RIVER_DETAIL_DOWNSTREAM_Y);
    float2 riverDirection1 = float2(
        -RIVER_DETAIL_CROSSING_X, RIVER_DETAIL_DOWNSTREAM_Y);
    float farSpeedRatio = max(_DetailNormalFarSpeed, DETAIL_NORMAL_MIN_SPEED)
                        / max(_DetailNormalSpeed, DETAIL_NORMAL_MIN_SPEED);
    float riverSpeed = length(currentData.zw);
    return DetailNormalTiltScrolled(
        currentData.xy,
        riverDirection0 * _WaveTime,
        riverDirection1 * _WaveTime,
        riverSpeed,
        riverSpeed * farSpeedRatio,
        viewDist);
}

#endif // WATER_SURFACE_DETAIL_NORMAL_INCLUDED
