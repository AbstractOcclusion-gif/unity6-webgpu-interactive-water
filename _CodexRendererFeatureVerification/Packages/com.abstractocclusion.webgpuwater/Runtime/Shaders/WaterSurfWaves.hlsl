// WebGpuWater - surf breaker wavefronts (Layer C-analytic, "P2" of the coastline plan).
//
// Periodic breaking wave fronts whose phase is a function of SHORE DISTANCE + time, so crests are
// shore-parallel on any coastline shape by construction (the HFW / KWS1 / Kelly-Slater family of
// techniques - authored shore waves, not simulation). Each front runs a depth-driven lifecycle:
// grow (Green's law) -> steepen/lean -> break where H exceeds the depth criterion -> collapse into
// a whitewash bore that keeps running shoreward -> hand over to the swash at the waterline.
//
// PURE CLOSED-FORM MATH: every function is a function of (inputs, time) only - no textures, no sim
// state. The hosts sample the Layer A shore field themselves and pass depth/sdf/direction in:
//   - WaterLargeWaves.hlsl (surface vertex + fragment) via WaterShore.hlsl's ShoreData,
//   - WaterSim.compute (foam injection) via its own Texture2D fetches,
//   - LargeWaveField.cs (CPU buoyancy mirror) - kept BYTE-FOR-BYTE with SurfFrontHeight below.
// That makes the layer WebGPU-safe (no readback anywhere) and exactly CPU-mirrorable.
#ifndef WEBGPUWATER_SURF_WAVES_INCLUDED
#define WEBGPUWATER_SURF_WAVES_INCLUDED

// Published as globals for the surface (WaterShoreDepthField.Publish) and set explicitly on the
// ripple-sim compute (WaterSimulation.BindShoreFoam). All-zero (unpublished) is inert.
float _SurfActive;        // 1 = the breaker-front layer runs on this body (bed depth + SDF baked)
float _SurfAmplitude;     // deep-water set-wave amplitude (m) feeding the fronts
float _SurfWavelength;    // front spacing offshore (m)
float _SurfPeriod;        // seconds between fronts arriving at a fixed point
float _SurfBandDepth;     // column depth (m) at which fronts are fully developed (fade in deeper)
float _SurfSetStrength;   // 0..1 amplitude variation between wave sets (waves come in sets)
float _SurfLean;          // forward-lean shear (fraction of local height thrown shoreward at the crest)
float _SurfCompression;   // front-spacing compression toward the waterline (crests bunch as they slow)
float _SurfGreens;        // Green's-law growth cap for the fronts (1 = no growth)
float _SurfAmbientFade;   // 0..1 how much the ambient swell/FFT fades where fronts own the surface
float _SurfSwashAmplitude;// MULTIPLIER on the physical Hunt run-up (1 = physics; 0 = swash off)
float _SurfSwashMaxSlopeTan; // tan of the steepest beach that still holds a swash film
// Fraction of the max slope where the swash starts thinning out. A HARD cut would print a contour
// line straight across the terrain wherever the bed slope crosses the threshold - the one artifact
// this cap must not trade for the one it removes.
#define SURF_SWASH_SLOPE_FEATHER 0.6
float _SurfWaterlineFoam; // standing lace hugging the waterline (fills the last metres to the sand)
float _SurfSmallWaveFoam; // FOAM-7: foam on the CREST + TAIL of gentle waves that never break
                          // (overCap < 1), which the breaking-gated whitewash leaves bare. 0 = off
float _SurfCrestLength;   // alongshore length scale (m) of crest segments (finite crests, not bands)
float _SurfCrestVariation;// 0..1 how deeply the crest noise modulates amplitude (0 = endless bands)
float _SurfCrestPersistence; // 0..1 how anchored the segmentation is across fronts: 0 = a fresh
                          // random pattern per front (foam hot spots wander wave to wave), 1 = the
                          // pattern only drifts a small phase per front, so successive waves break
                          // at nearly the same alongshore spots (bathymetry-anchored read)
float _SurfDirectionality;// 0..1 gate surf by shore exposure to the swell (lee side goes calm)
float4 _SurfWindDirXZ;    // xy = (cos, sin) of the swell/wind heading (the wave travel direction)
// THE MASTER SURF BEAT. The body's wave clock wrapped to SURF_BEAT_WRAP_FRONTS front periods
// (WaterVolume.SurfBeatTime), published every frame. EVERY surf consumer - surface vertex +
// fragment, swash, curl sheet, foam-sim injection (_ShoreFoamTime carries the same value) and
// the CPU buoyancy mirror (ShoreWaveContext.SurfBeatTime) - evaluates the front field on THIS
// clock, never raw _WaveTime: the raw clock grows without bound, so the per-front hash argument
// grows into the range where GPU float32 sin() and CPU double sin() disagree (the render and the
// buoyancy mirror slowly stop agreeing on set amplitudes) and t/T itself loses fractional
// precision (front positions step). The field is EXACTLY periodic in the wrap (see
// SurfWrapIndex), so the wrap instant is seamless by construction.
float _SurfBeatTime;
// THE SHARED WETNESS CLOCK, in seconds, published per body from foamSettings.wetnessDryTime. The surf
// swash and the ripple sim's wet mark both dry on THIS number - two drying laws for one visual (a
// beach receding on the wave period while the ground beside it dried on a seconds knob) is exactly
// the kind of split that shows up as a seam where the two meet.
float _WetDryTimeSeconds;
// Matches WaterVolume.Solver's WetMarkFadeTimeConstants: "dry" means faded to exp(-3) ~ 5%. Both
// sides must agree or the same authored number would mean two different durations.
#define SURF_DRY_TIME_CONSTANTS 3.0
// Used when the uniform has never been published (no live body). Mirrors the C# field default, so an
// unpublished frame looks like a default one rather than drying instantly.
#define SURF_DEFAULT_DRY_SECONDS 3.0
// Dedicated surf-foam LOOK controls (decoupled from BOTH the ripple/pond foam sliders and the
// ocean whitecap sliders - tuning either must never restyle the surf whitewash). The surf renders
// through the ocean-whitecap pipeline (same texture + contrast law: whitewash IS whitecap foam)
// but with these knobs. Consumed by the surface fragment only; published with the _Surf* globals.
float _SurfFoamStrength;  // coverage scale of the whitewash/geometry foam layer
float _SurfFoamFeather;   // dissolve softness at the coverage threshold (0 = hard-edged lace)
float _SurfFoamTileSize;  // metres per foam-pattern tile on the surf
float4 _SurfFoamColor;    // rgb tint, a = master opacity
// Whitewash REPARTITION weights (FOAM-2). ALL RENDER-ONLY: foam never moves the surface, so none
// of these are CPU-mirrored or validator-guarded. _SurfFoamRepartActive gates the whole set - a
// body that never publishes it (0) collapses every weight to the legacy constants, so existing
// scenes stay byte-identical. Published by WaterShoreDepthField (surface) and
// ShoreFoamState.BindTo (sim + particle computes).
float _SurfFoamRepartActive; // 1 = the repartition weights below are live
float _SurfFoamBoreGain;     // whitewash weight of the bore head (1 = legacy)
float _SurfFoamTrailGain;    // whitewash weight of the trailing deposit (1 = legacy)
float _SurfFoamTrailLength;  // trailing-deposit length multiplier (1 = legacy)

#define SURF_TWO_PI            6.28318530718
#define SURF_MIN_DEPTH         0.05  // metre floor under every depth divide
// Knob floors: _SurfPeriod / _SurfWavelength are artist-typed, so every consumer (this file,
// WaterSurfaceFragStages' swash foam, and the CPU mirror LargeWaveField.cs) clamps through THESE
// - one named floor per knob, so a zero knob can never divide-by-zero and both sides of the
// CPU/GPU mirror clamp to the exact same number (validator-guarded).
#define SURF_MIN_PERIOD        0.5   // seconds
#define SURF_MIN_WAVELENGTH    1.0   // metres
#define SURF_MIN_GREENS        1.0   // _SurfGreens floor: a growth CAP below 1 would attenuate
// Fronts per master-beat wrap (WaterVolume.SurfBeatTime = waveTime mod period*WRAP). MUST be a
// multiple of SURF_SET_WAVES so the set envelope (sin(frontIndex / SURF_SET_WAVES * 2pi)) is
// exactly periodic across the wrap. 1280 fronts ~= 3.2 h of surf at the default 9 s period.
#define SURF_BEAT_WRAP_FRONTS  1280.0
// Radians the crest-segmentation seed advances per front at FULL persistence: the alongshore
// pattern slides ~5% of a noise cycle each wave, so break spots migrate over minutes like a real
// sandbank instead of teleporting (mirrored in LargeWaveField.cs - segmentation moves the height).
// Both drifts are EXACT multiples of 2pi / SURF_BEAT_WRAP_FRONTS (2pi*71/1280 and 2pi*92/1280,
// the closest such values to the original 0.35 and 0.35*1.3), so the anchored pattern is exactly
// beat-periodic - no teleport at the wrap instant. Persistence 0 (the default) never reads them.
#define SURF_CREST_SEED_DRIFT_A 0.34852044
#define SURF_CREST_SEED_DRIFT_B 0.45160394
// Crest-segmentation noise shaping (SurfCrestFactor - height-affecting, so every value below is
// mirrored in LargeWaveField.cs and validator-guarded):
#define SURF_CREST_MIN_LENGTH   4.0  // metres floor on _SurfCrestLength (keeps invLen finite)
#define SURF_CREST_SEED_FRESH_SCALE 37.0 // spreads the 0..1 per-front hash across many noise cycles
#define SURF_CREST_FRESH_OCTAVE_RATIO 1.3 // octave-B fresh-seed multiplier (decorrelates octaves)
#define SURF_CREST_DIR_A_Z      0.31 // octave-A world direction = (1, this) - irrational-ish tilt
#define SURF_CREST_DIR_B_X      -0.42 // octave-B world direction = (this, 1) - rotated vs octave A
#define SURF_CREST_FREQ_RATIO   1.7  // octave-B spatial frequency multiplier (breaks periodicity)
#define SURF_CREST_OCTAVE_B_WEIGHT 0.5 // octave-B amplitude weight in the two-octave sum
#define SURF_CREST_NOISE_NORM   1.5  // max |sum| of the two octaves (normalizes the noise to 0..1)
#define SURF_FACE_FRACTION     0.10  // steep shoreward face length, as a fraction of front spacing
#define SURF_BACK_FRACTION     0.24  // long offshore back length, as a fraction of front spacing
#define SURF_SET_WAVES         5.0   // pseudo-period (in fronts) of the set envelope
// Set-amplitude shaping (SurfSetAmp - height-affecting, mirrored in LargeWaveField.cs):
#define SURF_SETAMP_HASH_PHASE 2.4   // radians of per-front phase jitter fed into the set sine
#define SURF_SETAMP_FLOOR      0.35  // smallest set-wave amplitude at full _SurfSetStrength
// Per-front hash jitter bounds on the amplitude. The MAX is a published contract: other shaders
// (WaterWaterline.hlsl's SurfaceHeightBand) size against the largest possible front, so the
// name SURF_SETAMP_JITTER_MAX must stay stable for them to re-point at.
#define SURF_SETAMP_JITTER_MIN 0.9
#define SURF_SETAMP_JITTER_MAX 1.1
// Shore-distance compression reach, in front spacings: reach = this * wavelength. The SAME curve
// is published to the ambient swell as _ShoreWarpReach (WaterShoreDepthField.Publish computes
// 2 x spacing with its own copy of this factor), so fronts and swell bunch in lockstep.
#define SURF_WARP_REACH_SPACINGS 2.0
// Per-front amplitude cross-fade: |f - 0.5| (half-cells from the crest) where the blend toward
// the NEIGHBOURING front's amplitude begins, reaching 50/50 exactly at the cell edge. Without it
// the per-front hash steps at f = 0/1 while the bore shape is still ~0.43 there - a shore-parallel
// height + foam seam marching mid-way between bores, and an FD-slope spike (the "sparkle line").
// Mirrored in LargeWaveField.cs (height-affecting).
#define SURF_EDGE_BLEND_START  0.35
#define SURF_NEAR_FADE         0.55  // fraction of _SurfBandDepth where fronts are fully developed
#define SURF_SECH_ARG_MAX      20.0  // cosh overflow clamp (WGSL float overflow is impl-defined)
#define SURF_SLOPE_EPSILON     0.5   // metres, finite-difference step for the front slope
// Field-mask shaping (SurfFieldMask / EvaluateSurfWaves - the mask scales the height, so all
// four are mirrored in LargeWaveField.cs and validator-guarded):
#define SURF_MIN_INFLUENCE     0.001 // influence/mask gate: below this the layer is provably inert
#define SURF_MIN_BAND_DEPTH    0.25  // metres floor on _SurfBandDepth (develop ramp never /0)
#define SURF_WET_FADE_LO       -0.05 // depth (m) where the waterline wet fade starts (on the sand)
#define SURF_WET_FADE_HI       0.1   // depth (m) of full strength - tight, so fronts run nearly
                                     // to the waterline (a wide fade STRANDED the foam ~25 cm deep)
// Shore-exposure gate window (SurfExposure): smoothstep over dot(swell dir, toShore). The soft
// negative lower edge lets waves wrap a little past the tangent point (cheap diffraction stand-in).
#define SURF_EXPOSURE_FACING_LO -0.25
#define SURF_EXPOSURE_FACING_HI 0.5
// Swash timing: fraction of the front period spent on the quick uprush (the rest is the slower
// backwash). Also the apex the wet line decays FROM - shared with the sim's swash deposit and the
// surface's reflux/deposit envelopes, so all four agree on when the film turns around.
#define SURF_SWASH_UPRUSH      0.30
// The swash film rides this far (m) proud of the sand, so the film/glaze fragments WIN the depth
// test against the opaque beach (a flat plane under the terrain would be entirely occluded).
#define SURF_FILM_THICKNESS    0.03
// Vertical band (m) the film's two geometry joins are smoothed over (WaterSurfaceVertStage's
// SmoothMin/SmoothMax): the film-to-wet-line plateau, and the film-to-open-sea junction. Hard
// min/max there left two slope discontinuities running parallel to the beach - visible dihedral
// creases. MUST stay under 4 x SURF_FILM_THICKNESS: SmoothMin dips blend/4 below the true minimum
// where the two arguments meet, and that dip eats into the film's clearance over the sand.
#define SURF_FILM_BLEND        0.05
// Foam-buffer coverage at which a persistent swash deposit HOLDS the beach film fully onto the sand
// (FOAM_MASK_EPSILON is the other end of the ramp). This was a bare `> FOAM_MASK_EPSILON` test, i.e.
// a binary switch on a continuously decaying buffer: the faintest trace of foam snapped the film up
// by whole metres and opened the clip, so beach patches popped in and out as deposits faded through
// 0.005. An epsilon answers "is anything here", which is the wrong question for a HOLD strength.
#define SURF_DEPOSIT_HOLD_FULL 0.15
// Lifecycle x-axis span of the crest-foam pop LUT (FOAM-1): overCap 0..this maps to LUT u 0..1.
// LOCKSTEP with WaterVolume.SurfCrestLutOverCapMax (the C# curve bake) - render-only foam, so it
// is NOT a validator-guarded height pair; the comment is the contract.
#define SURF_CREST_LUT_OVERCAP_MAX 2.0
// Base weight of the trailing deposit inside the whitewash max() (the historic 0.4 - named so the
// FOAM-2 repartition multiplies a constant, not a magic number).
#define SURF_TRAIL_BASE_WEIGHT 0.4

// --- Slope-aware breaker physics (SURF-PHYS) -----------------------------------------------------
// The bathymetry now diversifies the coastline by itself: the baked beach slope (SDF texture A
// channel, tan(beta) = |grad depth|) drives the Iribarren surf-similarity classification, the
// slope-dependent breaker index and the Hunt run-up. Height-affecting constants below are mirrored
// in LargeWaveField.cs (lockstep) and guarded by WaterWaveConstantsValidator.
//
// Iribarren number xi0 = tanBeta / sqrt(H0/L0) (Battjes 1974, "surf similarity"): classifies the
// breaker type. xi < ~0.5 spilling, ~0.5..~3.3 plunging, > ~3.3 surging/collapsing. The bands blend
// over smoothstep windows (never branches) so the coastline morphs continuously with the slope.
#define SURF_XI_SPILL_END_LO   0.45  // spilling fades out / plunging fades in across this window
#define SURF_XI_SPILL_END_HI   0.60
#define SURF_XI_SURGE_START_LO 2.8   // plunging fades out / surging fades in across this window
#define SURF_XI_SURGE_START_HI 3.6
// Deep-water wavelength L0 = g/(2 pi) * T^2 = 1.56 * T^2 (linear dispersion, metres).
#define SURF_DEEPWATER_LENGTH_COEF 1.56
#define SURF_XI_HEIGHT_EPSILON 1e-3  // H0 floor inside the Iribarren sqrt (crest-segment gaps)
#define SURF_XI_LENGTH_EPSILON 1e-3  // L0 floor inside the Iribarren sqrt (degenerate period)
// Slope-dependent breaker index gamma = Hb/db, replacing the fixed McCowan 0.78. Simplified from
// Weggel (1972) gamma = b(m) - a(m)*Hb/(g T^2): the linear ramp approximates b(m)'s initial rise
// (db/dm ~ 7.6 at m = 0, softened to 5.0 over the playable slope range) and the sub-McCowan base
// 0.6 consciously folds in the finite-steepness a(m) reduction, so flat shelves break EARLY and
// soft while steep beaches hold on later (relatively shallower, more violent). Cap 1.1 spans the
// measured field range (~0.6..1.2).
#define SURF_GAMMA_BASE        0.6
#define SURF_GAMMA_SLOPE_GAIN  5.0
#define SURF_GAMMA_MAX         1.1
// Broken-bore decay target: Dally, Dean & Dalrymple (1985) - a broken bore decays toward a STABLE
// height ~0.4 * local depth, it does not keep a fixed fraction of its break height. The reference
// form is an ODE in travel distance; a stateless field cannot integrate it, so the bore amplitude
// RELAXES onto the 0.4*d envelope with the existing 'broken' blend (documented approximation:
// matches the reference behaviour's direction and end state, skips the exponential transient).
#define SURF_BORE_STABLE_GAMMA 0.40
// Front lifecycle shaping (SurfComputeFrontTerms - ALL height-affecting, mirrored in
// LargeWaveField.cs and validator-guarded; the bore width factor is the exact constant whose
// unnamed twin once drifted to 2.0 in the CPU mirror):
#define SURF_GREEN_EXPONENT    0.25  // Green's law: amplitude ~ depth^(-1/4)
#define SURF_CAP_EPSILON       1e-3  // breaking-cap floor under the overCap divide
#define SURF_CRESTING_START    0.75  // overCap window where the front reads as cresting
#define SURF_CRESTING_END      1.05
#define SURF_BROKEN_START      1.05  // overCap window of the break -> whitewash-bore hand-over
#define SURF_BROKEN_END        1.5
#define SURF_LEAN_REACH_FRACTION 0.25 // lean-shear e-folding reach, fraction of front spacing
#define SURF_BORE_WIDTH_FACTOR 1.4   // bore sech width = backLen * this (wider than the crest)
// Hunt (1959) run-up R = xi * H0, valid up to xi ~ 2.3 where measured run-up saturates - the cap
// doubles as the cliff guard (xi is unbounded on near-vertical shores). Swash is render-only.
#define SURF_RUNUP_XI_CAP      2.3
#define SURF_SURGE_RUNUP_BOOST 1.35  // surging waves route their unbroken energy into the swash
// Plunging face steepening (CURL/P2, HEIGHT-AFFECTING - mirrored in LargeWaveField.cs): as a
// plunging front crests, the base wave's FACE stands up (profile length shrinks to this factor),
// so the lip sheet is visibly thrown BY the wave instead of floating over an unchanged swell.
// Spilling/surging keep today's profile.
#define SURF_PLUNGE_FACE_SHARPEN   0.6
// Breaker-type foam shaping (render-only - foam never moves the surface, so no CPU mirror):
// plunging breaks throw a NARROWER, more intense whitewash and a stronger, wider cresting lip.
// The SPLASH-DOWN line: a plunging jet lands about a face length shoreward of the crest and
// churns the water THERE before the bore proper arrives - a dedicated whitewash lobe at the
// landing point (CURL-3; it also seeds the sim's foam injection, so spray particles that land
// convert into churn that is already visually anchored).
#define SURF_PLUNGE_LANDING_AHEAD  1.0  // landing distance shoreward of the crest (x face length)
#define SURF_PLUNGE_LANDING_WIDTH  0.5  // landing lobe width (x face length)
#define SURF_PLUNGE_LANDING_FOAM   0.8  // whitewash intensity of the landing lobe at full plunge
#define SURF_PLUNGE_TRAIL_NARROW   0.55 // trail length factor at full plunge (1 = spilling width)
#define SURF_PLUNGE_WHITEWASH_GAIN 0.5  // extra whitewash intensity at full plunge
#define SURF_PLUNGE_BREAKER_GAIN   0.8  // extra cresting-lip signal at full plunge
#define SURF_PLUNGE_BREAKER_WIDEN  0.6  // lip profile exponent at full plunge (< 1 widens the lobe)

// Matches LbwHash in WaterLargeWaves.hlsl / Hash in LargeWaveField.cs (same constants, so the CPU
// mirror stays byte-for-byte) - validator-guarded against that mirror, so the match is a console
// warning instead of hand-discipline.
#define SURF_HASH_SINE_FREQ 12.9898
#define SURF_HASH_SINE_SCALE 43758.5453
float SurfHash(float n)
{
    return frac(sin(n * SURF_HASH_SINE_FREQ) * SURF_HASH_SINE_SCALE);
}

// Wrap a front index onto the master beat: every per-front quantity (hash, set envelope,
// segmentation seed) derives from the WRAPPED index, so (a) the frac-sin hash argument stays
// small enough that GPU float32 sin() and the CPU mirror's double sin() agree forever, and
// (b) the whole field is exactly periodic in the beat wrap - the _SurfBeatTime rollover lands on
// an identical field. Mirrored in LargeWaveField.SurfWrapIndex.
float SurfWrapIndex(float frontIndex)
{
    return frontIndex - SURF_BEAT_WRAP_FRONTS * floor(frontIndex / SURF_BEAT_WRAP_FRONTS);
}

// Per-front set amplitude: a slow sine over SURF_SET_WAVES fronts (waves arrive in sets) plus a
// per-front hash jitter. _SurfSetStrength 0 = every front identical.
float SurfSetAmp(float frontIndex)
{
    float wrapped = SurfWrapIndex(frontIndex);
    float h = SurfHash(wrapped);
    float setWave = 0.5 + 0.5 * sin((wrapped / SURF_SET_WAVES) * SURF_TWO_PI
                                    + h * SURF_SETAMP_HASH_PHASE);
    return lerp(1.0, lerp(SURF_SETAMP_FLOOR, 1.0, setWave), _SurfSetStrength)
         * lerp(SURF_SETAMP_JITTER_MIN, SURF_SETAMP_JITTER_MAX, h);
}

// Shore-distance warp: compresses front spacing toward the waterline (waves slow down, crests
// bunch). Monotonic for gains up to ~2 so fronts never fold back on themselves.
float SurfWarpDistance(float s)
{
    float reach = SURF_WARP_REACH_SPACINGS * max(_SurfWavelength, SURF_MIN_WAVELENGTH);
    return s * (1.0 + _SurfCompression * exp(-max(s, 0.0) / reach));
}

// Alongshore crest modulation: a slow world-space noise (two rotated sine octaves - cheap, smooth,
// non-repeating at shore scale), seeded per front so segment gaps never align between consecutive
// fronts. Crests are locally shore-parallel, so world-position noise naturally varies ALONG the
// crest - long bands break into finite crest segments with calm water between them. Returns an
// amplitude factor in [1 - variation, 1]; where it dips, the front stays under the breaking
// criterion and produces no bore/foam at all - the gaps read as real lulls, not faded foam.
float SurfCrestFactor(float2 worldXZ, float frontIndex)
{
    if (_SurfCrestVariation <= 0.0) return 1.0;
    float invLen = 1.0 / max(_SurfCrestLength, SURF_CREST_MIN_LENGTH);
    float wrapped = SurfWrapIndex(frontIndex);
    // Seed persistence: lerp between a fresh hash per front (0 - the classic wandering hot spots)
    // and a slow constant drift per front (1 - anchored break spots). Continuous in the knob, and
    // byte-identical to the original at 0. The two octaves carry separate drift constants (both
    // exact multiples of 2pi/SURF_BEAT_WRAP_FRONTS - see the constants block) instead of the old
    // seed * 1.3, so the anchored pattern is exactly beat-periodic.
    float persistence = saturate(_SurfCrestPersistence);
    float seedFresh = SurfHash(wrapped) * SURF_CREST_SEED_FRESH_SCALE;
    float seedA = lerp(seedFresh, wrapped * SURF_CREST_SEED_DRIFT_A, persistence);
    float seedB = lerp(seedFresh * SURF_CREST_FRESH_OCTAVE_RATIO,
                       wrapped * SURF_CREST_SEED_DRIFT_B, persistence);
    float n = sin(dot(worldXZ, float2(1.0, SURF_CREST_DIR_A_Z)) * (SURF_TWO_PI * invLen) + seedA)
            + SURF_CREST_OCTAVE_B_WEIGHT
            * sin(dot(worldXZ, float2(SURF_CREST_DIR_B_X, 1.0))
                  * (SURF_TWO_PI * invLen * SURF_CREST_FREQ_RATIO) + seedB);
    float n01 = saturate(n / SURF_CREST_NOISE_NORM * 0.5 + 0.5);
    return 1.0 - _SurfCrestVariation * (1.0 - n01);
}

// Shore-exposure gate: surf only pounds coasts that FACE the swell. The soft negative lower edge
// lets waves wrap a little past the tangent point (cheap stand-in for diffraction) instead of
// cutting off knife-sharp at 90 degrees. 1 everywhere when _SurfDirectionality = 0.
float SurfExposure(float2 toShore)
{
    float facing = smoothstep(SURF_EXPOSURE_FACING_LO, SURF_EXPOSURE_FACING_HI,
                              dot(_SurfWindDirXZ.xy, toShore));
    return lerp(1.0, facing, saturate(_SurfDirectionality));
}

// Iribarren / surf-similarity number xi0 for a deep-water set height H0 on a beach of slope
// tanBeta (Battjes 1974). L0 from linear deep-water dispersion. Mirrored in LargeWaveField.cs.
float SurfIribarren(float tanBeta, float deepHeight)
{
    float T = max(_SurfPeriod, SURF_MIN_PERIOD);
    float deepLength = SURF_DEEPWATER_LENGTH_COEF * T * T;
    return tanBeta / sqrt(max(deepHeight, SURF_XI_HEIGHT_EPSILON)
                          / max(deepLength, SURF_XI_LENGTH_EPSILON));
}

// Breaker-type weights from the Iribarren number: x = spilling, y = plunging, z = surging.
// Smooth partition of unity (the three always sum to 1), so every lifecycle SHAPE term below can
// blend by them with no branches. Mirrored in LargeWaveField.cs.
float3 SurfBreakerWeights(float xi)
{
    float pastSpill = smoothstep(SURF_XI_SPILL_END_LO, SURF_XI_SPILL_END_HI, xi);
    float surge = smoothstep(SURF_XI_SURGE_START_LO, SURF_XI_SURGE_START_HI, xi);
    return float3(1.0 - pastSpill, pastSpill * (1.0 - surge), surge);
}

// Slope-dependent breaker index gamma = Hb/db (Weggel-simplified, see the constants block):
// steep beaches break later, in relatively shallower water, more violently; flat beaches break
// early and soft. Mirrored in LargeWaveField.cs.
float SurfGamma(float tanBeta)
{
    return clamp(SURF_GAMMA_BASE + SURF_GAMMA_SLOPE_GAIN * tanBeta, SURF_GAMMA_BASE, SURF_GAMMA_MAX);
}

// Core front shape at a warped shore distance + local depth. Returns (height m, whitewash,
// breaker) as a plain float3 - NO out parameters: FXC's inliner is fragile around out-params in
// deeply nested calls (the editor's shader-compiler process died on the first build of this file),
// and a value return is also simply cleaner. This is THE canonical evaluation - the CPU mirror
// (LargeWaveField.SurfFrontHeight) reproduces exactly the .x math.
//   .y whitewash: 0..1 broken-bore coverage (foam fuel, trails behind the moving front)
//   .z breaker:   0..1 "cresting/about to break" signal (thin line at the lip - foam + SSS fuel)
// Everything the front lifecycle knows at one evaluation point - shared by the height/whitewash/
// breaker composition (SurfFrontHeight below) and the foam-side extras (SurfFrontFoamFromTerms),
// so no consumer can drift from the surface it decorates. The CPU mirror reproduces the HEIGHT
// composition only; splitting terms out changes no math.
struct SurfFrontTerms
{
    float dAcross;    // metres from the crest along the shore-distance axis (+ offshore), lean applied
    float height;     // capped local front height H (m)
    float profile;    // asymmetric sech^2 across-front shape (0..1, 1 at the crest)
    float boreSech;   // wide bore mound shape (0..1)
    float boreAmp;    // Dally-Dean-Dalrymple bore amplitude envelope (m)
    float overCap;    // raw break-criterion ratio H/(gamma*d) - the lifecycle's monotonic clock
    float cresting;   // approaching/at the breaking limit (0..1)
    float broken;     // fully broken -> whitewash bore (0..1, surge-suppressed)
    float setAmp;     // set envelope x crest segmentation
    float3 breakType; // Iribarren breaker weights: x = spilling, y = plunging, z = surging
    float faceLen;    // steep shoreward face length (m)
    float backLen;    // long offshore back length (m)
};

SurfFrontTerms SurfComputeFrontTerms(float2 worldXZ, float sWarp, float depth, float tanBeta,
                                     float time)
{
    SurfFrontTerms t;
    float L = max(_SurfWavelength, SURF_MIN_WAVELENGTH);
    float T = max(_SurfPeriod, SURF_MIN_PERIOD);
    // Phase grows with time at fixed distance, so an iso-phase crest moves TOWARD the shore
    // (smaller s) as time advances; speed drops where the warp has compressed the spacing.
    float phase = sWarp / L + time / T;
    float frontIndex = floor(phase);
    float f = phase - frontIndex;              // 0..1 across the front cell, crest at 0.5

    // Set envelope (in time) x crest segmentation (alongshore): both fold into the amplitude, so
    // the breaking criterion, the bore, the whitewash and the crest glow all follow them for free.
    // C0 continuity across cell edges: near f = 0/1 the amplitude cross-fades toward the
    // NEIGHBOURING front's, reaching 50/50 exactly at the edge - both cells agree there, so the
    // per-front hash can never print a step into the height/foam mid-way between bores (see
    // SURF_EDGE_BLEND_START). At the crest (f = 0.5) the blend is 0: each wave keeps its own size.
    float ampThis = SurfSetAmp(frontIndex) * SurfCrestFactor(worldXZ, frontIndex);
    float halfCell = f - 0.5;                        // -0.5..0.5, 0 at the crest
    float neighborIndex = frontIndex + ((halfCell > 0.0) ? 1.0 : -1.0);
    float ampNeighbor = SurfSetAmp(neighborIndex) * SurfCrestFactor(worldXZ, neighborIndex);
    float edgeBlend = 0.5 * smoothstep(SURF_EDGE_BLEND_START, 0.5, abs(halfCell));
    t.setAmp = lerp(ampThis, ampNeighbor, edgeBlend);
    float d = max(depth, SURF_MIN_DEPTH);

    // Breaker-type classification for THIS front on THIS beach (SURF-PHYS): the deep-water set
    // height (pre-Green) feeds the Iribarren number; the local slope picks the breaking regime.
    float deepHeight = _SurfAmplitude * t.setAmp;
    t.breakType = SurfBreakerWeights(SurfIribarren(tanBeta, deepHeight)); // spill/plunge/surge

    // Local height: Green's-law growth toward the shore, capped by the breaking criterion -
    // the slope-dependent Weggel gamma now, not the fixed McCowan 0.78.
    float green = min(pow(max(_SurfBandDepth, d) / d, SURF_GREEN_EXPONENT),
                      max(_SurfGreens, SURF_MIN_GREENS));
    float H = _SurfAmplitude * t.setAmp * green;
    float capH = SurfGamma(tanBeta) * d;
    t.overCap = H / max(capH, SURF_CAP_EPSILON);
    t.cresting = smoothstep(SURF_CRESTING_START, SURF_CRESTING_END, t.overCap); // at/near the limit
    // Fully broken -> whitewash bore. Surging waves SKIP the bore entirely (their weight kills the
    // hand-over): the unbroken face runs to the waterline and hands its energy to the swash.
    t.broken = smoothstep(SURF_BROKEN_START, SURF_BROKEN_END, t.overCap) * (1.0 - t.breakType.z);
    // (Later hand-over than v1: the cresting face stays tall further in, so the wave is
    // still VISIBLY a wave when it arrives instead of collapsing to a flat foam smear.)
    t.height = min(H, capH);

    // Asymmetric solitary profile across the front (sech^2, Fournier-Reeves family): crest at
    // f = 0.5, SHORT steep face on the shoreward side (f < 0.5 = smaller s), long offshore back.
    // The lean shear throws the crest top shoreward as it steepens (phase-advance forward lean).
    t.dAcross = (f - 0.5) * L;                       // metres from the crest, + = offshore side
    float lean = _SurfLean * t.height * t.cresting;  // lean grows as the front approaches breaking
    t.dAcross += lean * exp(-abs(t.dAcross) / (SURF_LEAN_REACH_FRACTION * L));
    t.faceLen = SURF_FACE_FRACTION * L;
    t.backLen = SURF_BACK_FRACTION * L;
    // Plunging face steepening: see SURF_PLUNGE_FACE_SHARPEN (height-affecting, CPU-mirrored).
    float faceSharpen = lerp(1.0, SURF_PLUNGE_FACE_SHARPEN, t.breakType.y * t.cresting);
    float profLen = (t.dAcross < 0.0) ? t.faceLen * faceSharpen : t.backLen;
    float sechTerm = 1.0 / cosh(min(abs(t.dAcross) / profLen, SURF_SECH_ARG_MAX));
    t.profile = sechTerm * sechTerm;

    // Broken front: collapse toward a LOWER, WIDER whitewash bore (rounded step of churned water
    // that keeps running shoreward). sech (not sech^2) at SURF_BORE_WIDTH_FACTOR x the back length
    // reads as the mound. The bore amplitude relaxes onto the Dally-Dean-Dalrymple STABLE height
    // (0.4 * depth) instead of a fixed fraction of its break height - see SURF_BORE_STABLE_GAMMA.
    t.boreSech = 1.0 / cosh(min(abs(t.dAcross) / (t.backLen * SURF_BORE_WIDTH_FACTOR),
                                SURF_SECH_ARG_MAX));
    t.boreAmp = lerp(t.height, SURF_BORE_STABLE_GAMMA * d, t.broken);
    return t;
}

// Height (m) of the un-curled front surface from its terms - THE height every consumer composes
// (the CPU mirror reproduces exactly this).
float SurfFrontHeightFromTerms(SurfFrontTerms t)
{
    return lerp(t.height * t.profile, t.boreAmp * t.boreSech, t.broken);
}

// Foam-side signals of one front evaluation (ALL RENDER-ONLY - nothing here feeds height, so no
// CPU mirror). Split out of SurfFrontHeight so consumers that also want the artist pop-curve
// inputs (lipShape + the overCap already in the terms) get them from the SAME evaluation.
struct SurfFrontFoam
{
    float whitewash; // 0..1 broken-bore + trailing-deposit + splash-down coverage
    float breaker;   // 0..1 legacy cresting-lip signal (= lipShape x the built-in pop window)
    float lipShape;  // WHERE crest foam can live (lip profile, plunge-widened, surge-killed) with
                     // NO timing window - the FOAM-1 artist curve owns WHEN via overCap
    float trailAge;  // seconds since the crest passed this point (0 at/ahead of the crest, grows
                     // offshore through the deposit trail; bore-gated so unbroken fronts stay 0)
};

SurfFrontFoam SurfFrontFoamFromTerms(SurfFrontTerms t)
{
    SurfFrontFoam foam;
    // FOAM-2 repartition: bore-head vs trailing-deposit weights + trail length. All lerped from
    // the legacy constants by the publish gate, so unpublished bodies are byte-identical.
    float repart = saturate(_SurfFoamRepartActive);
    float boreWeight = lerp(1.0, _SurfFoamBoreGain, repart);
    float trailWeight = lerp(1.0, _SurfFoamTrailGain, repart);
    float trailLenMul = lerp(1.0, max(_SurfFoamTrailLength, 0.05), repart);

    // Whitewash: rides the bore and TRAILS OFFSHORE behind the shoreward-moving front (the churned
    // water is left behind as the front travels on; the shoreward side gets its foam from the sim
    // injection + waterline lace instead). NARROW on purpose: each front's foam footprint must be
    // clearly smaller than the compressed front spacing (~L/(1+c)), or neighbouring bores' foam
    // overlaps into one solid static-looking carpet and the march toward shore becomes invisible -
    // exactly the "big slow band" failure. Gated by the set amplitude so lulls stay clean.
    // Breaker-type shaping (render-only): plunging throws a narrower, more intense whitewash;
    // surging produces none at all (its 'broken' is already killed above).
    float trailLen = t.backLen * lerp(1.0, SURF_PLUNGE_TRAIL_NARROW, t.breakType.y) * trailLenMul;
    float trail = (t.dAcross > 0.0) ? exp(-t.dAcross / trailLen) : 0.0;
    float whitewash = t.broken
                    * max(t.boreSech * boreWeight, SURF_TRAIL_BASE_WEIGHT * trail * trailWeight)
                    * saturate(t.setAmp)
                    * (1.0 + SURF_PLUNGE_WHITEWASH_GAIN * t.breakType.y);
    // Splash-down lobe: the plunging jet churns its LANDING point (shoreward of the crest, where
    // dAcross is negative) while the front is still cresting - see SURF_PLUNGE_LANDING_*.
    float landing = t.breakType.y * t.cresting * (1.0 - t.broken) * saturate(t.setAmp)
                  * exp(-abs(t.dAcross + SURF_PLUNGE_LANDING_AHEAD * t.faceLen)
                        / max(SURF_PLUNGE_LANDING_WIDTH * t.faceLen, 1e-3));
    foam.whitewash = saturate(whitewash + landing * SURF_PLUNGE_LANDING_FOAM);
    // FOAM-7 small-wave / pre-break foam: gentle shore waves carry foam on the CREST and a short
    // TAIL even when they never reach the breaking criterion (overCap < 1), where the broken-gated
    // whitewash above leaves them bare. An un-gated crest+tail lobe, faded OUT once the wave has
    // actually broken (its own whitewash owns the foam then) and scaled by the set envelope so
    // lulls stay clean. 0 = off (byte-identical). Render-only, like the rest of this foam struct.
    float smallCrest = t.profile;                                          // top: peaks at the crest
    float smallTail  = (t.dAcross > 0.0) ? exp(-t.dAcross / max(t.backLen, 1e-3)) : 0.0; // trailing tail
    float smallWaveFoam = saturate(t.setAmp) * max(smallCrest, smallTail)
                        * (1.0 - t.broken) * _SurfSmallWaveFoam;
    foam.whitewash = saturate(foam.whitewash + smallWaveFoam);
    // Thin cresting line right at the lip while the front is breaking (not yet fully broken).
    // Plunging amplifies AND widens the lip; surging has no lip at all - the face never overturns.
    // lipShape is the timing-free part; the legacy breaker keeps its built-in cresting window.
    float lipProfile = pow(t.profile, lerp(1.0, SURF_PLUNGE_BREAKER_WIDEN, t.breakType.y));
    foam.lipShape = lipProfile * (1.0 + SURF_PLUNGE_BREAKER_GAIN * t.breakType.y)
                  * (1.0 - t.breakType.z);
    foam.breaker = t.cresting * (1.0 - t.broken) * foam.lipShape;
    // Deposit age: the front travels ~one wavelength per period, so metres behind the crest map
    // to seconds since it passed. A render-only heuristic (the warp compresses true speed near
    // the waterline) - good enough to drive the FOAM-2 hole-dissolve, never height.
    float L = max(_SurfWavelength, SURF_MIN_WAVELENGTH);
    float T = max(_SurfPeriod, SURF_MIN_PERIOD);
    foam.trailAge = t.broken * max(t.dAcross, 0.0) * (T / L);
    return foam;
}

float3 SurfFrontHeight(float2 worldXZ, float sWarp, float depth, float tanBeta, float time)
{
    SurfFrontTerms t = SurfComputeFrontTerms(worldXZ, sWarp, depth, tanBeta, time);
    SurfFrontFoam foam = SurfFrontFoamFromTerms(t);
    return float3(SurfFrontHeightFromTerms(t), foam.whitewash, foam.breaker);
}

// Everything the surface / foam / CPU mirror needs from the front layer at one world xz.
struct SurfWaveSample
{
    float height;     // metres added to the surface (0 outside the surf band)
    float2 slopeXZ;   // d(height)/d(world xz) - drives the normal
    float whitewash;  // 0..1 whitewash coverage (broken bore + trail)
    float breaker;    // 0..1 cresting-lip signal (foam + subsurface glow fuel)
    float mask;       // 0..1 where the front layer owns the surface (ambient-fade weight)
    // FOAM-1/2 render-only extras (all 0 when inert, so non-surf consumers see no foam):
    float overCap;    // the front's lifecycle clock H/(gamma*d) - x input of the artist pop LUT
    float lipShape;   // mask-weighted timing-free lip footprint (multiply by the LUT sample)
    float trailAge;   // seconds since the crest passed (drives the deposit hole-dissolve)
};

// The inert (all-zero) sample: one definition, so every early-out and every caller's default is
// provably fully-initialized for the compiler's definite-assignment analysis.
SurfWaveSample SurfWaveSampleInert()
{
    SurfWaveSample o;
    o.height = 0.0;
    o.slopeXZ = float2(0.0, 0.0);
    o.whitewash = 0.0;
    o.breaker = 0.0;
    o.mask = 0.0;
    o.overCap = 0.0;
    o.lipShape = 0.0;
    o.trailAge = 0.0;
    return o;
}

// Evaluate the breaker-front layer. The caller provides the Layer A samples: still-water column
// depth (m), signed shore distance (m, + in water), unit direction toward shore, the local beach
// slope tan(beta) (SDF texture A channel), and the feathered in-field influence. Inert (all
// zeros) when inactive, off-field, offshore of the band, or on land.
// Where the front layer owns the surface, from the Layer A samples alone: develop (fronts grow as
// the water shallows into the band - the tight wet fade keeps them running almost to the
// waterline; a wide fade STRANDED the foam ~25 cm deep) x wet x field influence x shore exposure
// (the swell-facing coast gets the surf; the lee side calms down). Shared by EvaluateSurfWaves and
// every other surf consumer, so they all mask exactly like the surface.
// WHO OWNS THIS WATER - the SUPPRESSION contour, deliberately NOT SurfFieldMask.
//
// SurfFieldMask carries a 'wet' term whose job is to let the FRONTS run almost onto the sand
// (SURF_WET_FADE_LO/HI, see its comment: a wide fade STRANDED the foam ~25 cm deep). Reusing that
// same expression as the weight that tells OTHER foam engines to stand down made ownership die in
// the last ~10 cm of water - so accumulated ocean whitecaps, and the ripple/turbulence foam, both
// switched back on exactly AT the waterline. "How strong are the fronts here" and "who owns this
// water" are two different questions and they need two contours.
//
// This keeps develop x influence x exposure. Keeping influence and exposure is deliberate and is
// what stops the 2026-07-28 "barren strip" regression: a depth-only window killed whitecaps on the
// lee side of an island and in the outer band ring, where no whitewash ever appears. It simply does
// not fade out at the waterline.
//
// SUPERSET OF SurfFieldMask BY CONSTRUCTION (wet <= 1), so it can never suppress LESS than the
// shipped behaviour did - only more, and only inside the surf field.
float SurfOwnershipMask(float depth, float2 toShore, float influence)
{
    float band = max(_SurfBandDepth, SURF_MIN_BAND_DEPTH);
    float develop = 1.0 - smoothstep(SURF_NEAR_FADE * band, band, max(depth, 0.0));
    return develop * influence * SurfExposure(toShore);
}

float SurfFieldMask(float depth, float2 toShore, float influence)
{
    float band = max(_SurfBandDepth, SURF_MIN_BAND_DEPTH);
    float develop = 1.0 - smoothstep(SURF_NEAR_FADE * band, band, max(depth, 0.0));
    float wet = smoothstep(SURF_WET_FADE_LO, SURF_WET_FADE_HI, depth);
    return develop * wet * influence * SurfExposure(toShore);
}

SurfWaveSample EvaluateSurfWaves(float2 worldXZ, float depth, float sdfDist, float2 toShore,
                                 float tanBeta, float influence, float time)
{
    SurfWaveSample o = SurfWaveSampleInert();
#ifdef WATER_STRIP_SHORE
    // Compile-time strip - see ShoreSample (WaterShore.hlsl). With the shore stripped, influence
    // is provably 0 here anyway; this makes the whole surf cosh chain dead code so it folds out
    // of the shoreless fog variants instead of being compiled into every one of them.
    return o;
#endif
    if (_SurfActive < 0.5 || influence <= SURF_MIN_INFLUENCE) return o;

    float exposure = SurfExposure(toShore); // the lace below reuses it beyond the mask
    float mask = SurfFieldMask(depth, toShore, influence);

    // Standing waterline lace: foam hugging the last metres of water and a hint onto the swash
    // zone - it bridges the gap between the final bore and the sand so the run-out never reads
    // as flat open water. Exposure-gated AND segmented alongshore like the fronts (seeded by the
    // most recent arrival, so the segmentation drifts with each wave instead of forming one
    // endless static ribbon around the island).
    // Reading a single floor()'d arrival index made the alongshore segmentation RESEED in one
    // step at each period boundary, so the standing lace popped in patches every wave (with the
    // default crest variation 0.6 and persistence 0, consecutive seeds are uncorrelated). Crossfade
    // the segmentation from THIS arrival to the NEXT across the whole period so the pattern MORPHS
    // smoothly instead of stepping. Continuous AND C1 at the boundary (smoothstep's zero end-slopes):
    // at frac -> 1 it reads arrival+1, and next period at frac -> 0 the index has advanced so it
    // reads the same segmentation - no jump. Render-only (lace feeds whitewash/foam, never height).
    float lacePhase = time / max(_SurfPeriod, SURF_MIN_PERIOD) - 0.5;
    float laceIndex = floor(lacePhase);
    float laceSeg = lerp(SurfCrestFactor(worldXZ, laceIndex),
                         SurfCrestFactor(worldXZ, laceIndex + 1.0),
                         smoothstep(0.0, 1.0, lacePhase - laceIndex));
    float lace = (1.0 - smoothstep(0.2, 1.8, max(depth, 0.0)))
               * smoothstep(-0.35, -0.05, depth)
               * _SurfWaterlineFoam * influence * exposure * laceSeg;

    if (mask <= SURF_MIN_INFLUENCE && lace <= SURF_MIN_INFLUENCE) return o;

    float s = max(sdfDist, 0.0);
    // ONE terms evaluation shared by height, whitewash/breaker AND the FOAM-1/2 extras (overCap /
    // lipShape / trailAge) - same math SurfFrontHeight composes, just not thrown away.
    SurfFrontTerms terms = SurfComputeFrontTerms(worldXZ, SurfWarpDistance(s), depth, tanBeta, time);
    SurfFrontFoam frontFoam = SurfFrontFoamFromTerms(terms);
    float frontHeight = SurfFrontHeightFromTerms(terms);

    // Slope by finite difference ALONG the shore-distance axis (the front varies along it by
    // construction). The FD step may straddle a cell edge and re-derive the front index - safe,
    // because the edge cross-fade (SURF_EDGE_BLEND_START) makes the field C0 there, so the
    // difference stays bounded instead of spiking the normal.
    // grad(sdfDist) = -toShore, since distance grows offshore.
    float h1 = SurfFrontHeight(worldXZ, SurfWarpDistance(s + SURF_SLOPE_EPSILON), depth, tanBeta, time).x;
    float dhds = (h1 - frontHeight) / SURF_SLOPE_EPSILON;

    o.height = frontHeight * mask;
    o.slopeXZ = -toShore * (dhds * mask);
    o.whitewash = saturate(frontFoam.whitewash * mask + lace);
    o.breaker = saturate(frontFoam.breaker * mask);
    o.mask = mask;
    o.overCap = terms.overCap;
    o.lipShape = frontFoam.lipShape * mask;
    o.trailAge = frontFoam.trailAge;
    return o;
}

// Ambient-wave fade where the fronts own the surface (the anti-double-crest "replace" rule from
// Crest's spline Blend mode / HFW): multiply the ambient swell/FFT amplitude by this.
float SurfAmbientWeight(float surfMask)
{
    return 1.0 - surfMask * saturate(_SurfAmbientFade);
}

// --- Swash + wet sand (P4, fully analytic - no simulation, no persistent state) -----------------
// The swash is the thin film running up and back over the beach with each arriving front. At the
// waterline the front field's phase is time / period (s = 0), so the film's rhythm, set variation
// and drying can all be closed-form. Returns (no out-params - see SurfFrontHeight note):
//   .x swashLevel: metres of extra water level RIGHT NOW above the still plane (uprush/backwash)
//   .y wetLevel:   metres up to which the sand still glistens - the recent run-up maximum,
//                  decaying on the SHARED wetness clock (_WetDryTimeSeconds), not on the
//                  wave cycle, so sand and the ground beside it dry at one rate
// The surface shader keeps beach fragments alive up to max(.x, .y) and renders the zone above the
// current film as the dark wet-sand glaze - wet sand with zero extra state.
// The swash's beach-steepness cap as a plain 0..1 weight: 1 on a beach gentle enough to hold a
// film, falling to 0 past the authored ceiling.
//
// Swash is a BEACH process: a thin film runs up a slope only while the slope is gentle enough to
// hold it. The Hunt/Iribarren run-up GROWS with tanBeta - correct for a beach, where a steeper face
// really does surge further, and exactly wrong past the point where the "beach" is a cliff and the
// water simply hits it and falls. The physical model cannot know where that is, so the ceiling is
// authored (_SurfSwashMaxSlopeTan).
//
// A SEPARATE FUNCTION, not an inline fold, because the cap has to be visible to callers. It used to
// live inside EvaluateSurfSwash, applied to that function's own local copy of `influence`, where no
// caller could see it. Any caller deriving a STRENGTH from the swash - rather than reading the
// levels it returns - therefore skipped the cap silently. That is exactly how the foam sim came to
// lay full-strength swash deposits along CLIFF waterlines, where the swash itself is switched off:
// EvaluateSurfSwash dutifully returned 0, and the sim then built its deposit from the uncapped
// influence it had passed in. Anything gating on "is there swash here" must multiply by THIS.
//
// The divide is guarded and the ramp is built from a saturate rather than smoothstep's two edges,
// so a max slope of 0 degenerates cleanly to "no swash" instead of to undefined behaviour.
float SurfSwashSlopeGate(float tanBeta)
{
    float slopeFalloff = max(_SurfSwashMaxSlopeTan * (1.0 - SURF_SWASH_SLOPE_FEATHER), 1e-4);
    float slopeT = saturate((_SurfSwashMaxSlopeTan - tanBeta) / slopeFalloff);
    return smoothstep(0.0, 1.0, slopeT);
}

float2 EvaluateSurfSwash(float2 worldXZ, float2 toShore, float tanBeta, float influence, float time)
{
    // Folded into `influence` rather than applied at the return: influence already multiplies BOTH
    // `run` and `runPrev` below, so the current film AND the drying wet line inherit the cap from
    // one multiply and can never disagree about where the beach ends.
    influence *= SurfSwashSlopeGate(tanBeta);

    if (_SurfActive < 0.5 || influence <= SURF_MIN_INFLUENCE || _SurfSwashAmplitude <= 0.0)
        return float2(0.0, 0.0);

    float T = max(_SurfPeriod, SURF_MIN_PERIOD);
    // SYNC: a front's CREST reaches the waterline when its cell phase f hits 0.5 at s = 0, i.e.
    // when frac(time/T) = 0.5. Shifting the swash cycle by that half-cell makes the uprush START
    // exactly as the bore arrives (v1 peaked ~0.15 T BEFORE the wave hit - the "swash pops out of
    // nowhere, out of sync" read). The arriving front's index is floor(phase - 0.5), so the swash
    // also inherits THAT front's set amplitude + crest segmentation - the film runs up exactly
    // where and exactly as hard as the wave that just broke.
    float phase = time / T;                    // the front field evaluated at the waterline (s = 0)
    float arrivalIndex = floor(phase - 0.5);   // the front whose crest last hit the waterline
    float f = frac(phase - 0.5);               // 0 = crest arrival at the waterline

    float exposure = SurfExposure(toShore);
    // Hunt (1959) run-up from physics: R = xi * H0 (capped at Hunt's validity bound), where H0 is
    // the arriving front's own deep-water set height - so a flat dissipative beach gets a short
    // swash and a steep beach a long surge, per point, from the bathymetry. Surging waves put
    // their whole unbroken energy into the run-up (the boost). _SurfSwashAmplitude is a
    // MULTIPLIER on the physical result (1 = physics) so scenes stay tunable.
    float deepHeight = _SurfAmplitude * SurfSetAmp(arrivalIndex)
                     * SurfCrestFactor(worldXZ, arrivalIndex);
    float xi = SurfIribarren(tanBeta, deepHeight);
    float surge = SurfBreakerWeights(xi).z;
    float run = min(xi, SURF_RUNUP_XI_CAP) * deepHeight * _SurfSwashAmplitude
              * lerp(1.0, SURF_SURGE_RUNUP_BOOST, surge) * influence * exposure;
    // Quick uprush, slower backwash (real swash is strongly asymmetric).
    float upDown = (f < SURF_SWASH_UPRUSH)
        ? smoothstep(0.0, SURF_SWASH_UPRUSH, f)
        : 1.0 - smoothstep(SURF_SWASH_UPRUSH, 1.0, f);
    float swashLevel = run * upDown;
    // Wet line as a CONTINUOUS two-front envelope. The naive form referenced the NEW arrival's
    // full run-up the instant the cycle rolled over, so the wet/clip line teleported up the beach
    // in one frame ~a quarter-period before the water got there - the visible "pop" between the
    // last small wave and the swash. Instead:
    //  - THIS cycle's wet line can only be as high as the film has actually reached (it rises
    //    WITH the uprush), then dries toward the floor through the backwash;
    //  - the PREVIOUS front's wet line keeps drying through this cycle (second-stage dry-out);
    //  - the sand shows whichever is higher. Continuous everywhere, including cycle rollover:
    //    at f->1 this cycle ends at run*FLOOR, and at f=0 the next cycle's "previous" term
    //    starts at exactly run*FLOOR.
    float deepHeightPrev = _SurfAmplitude * SurfSetAmp(arrivalIndex - 1.0)
                         * SurfCrestFactor(worldXZ, arrivalIndex - 1.0);
    float xiPrev = SurfIribarren(tanBeta, deepHeightPrev);
    float runPrev = min(xiPrev, SURF_RUNUP_XI_CAP) * deepHeightPrev * _SurfSwashAmplitude
                  * lerp(1.0, SURF_SURGE_RUNUP_BOOST, SurfBreakerWeights(xiPrev).z)
                  * influence * exposure;
    // Decay on the SHARED wetness clock rather than on fractions of the wave cycle. The old form
    // faded to a hand-tuned floor over the backwash and carried a second hand-tuned term for the
    // previous front, with the two values matched by hand so the cycle rollover did not jump.
    //
    // Measuring the decay against AGE IN SECONDS makes that continuity automatic: at f -> 1 this
    // cycle has aged (1 - UPRUSH) * T, and at f -> 0 the next cycle's "previous" term starts at
    // exactly that same age. It also joins the uprush exactly, since the exponent is 0 at the apex.
    // Nothing is hand-matched any more, and the beach now dries in the seconds the user authored.
    float dryTime = (_WetDryTimeSeconds > 0.0) ? _WetDryTimeSeconds : SURF_DEFAULT_DRY_SECONDS;
    float tau = dryTime / SURF_DRY_TIME_CONSTANTS;
    float thisCycleWet = (f < SURF_SWASH_UPRUSH)
        ? swashLevel
        : run * exp(-((f - SURF_SWASH_UPRUSH) * T) / tau);
    float prevCycleWet = runPrev * exp(-((f + 1.0 - SURF_SWASH_UPRUSH) * T) / tau);
    float wetLevel = max(thisCycleWet, prevCycleWet);
    return float2(swashLevel, wetLevel);
}

#endif // WEBGPUWATER_SURF_WAVES_INCLUDED
