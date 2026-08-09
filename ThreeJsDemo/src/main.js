/* =====================================================================================
   WebGPU Ocean demo — a faithful Three.js/WebGPU port of the math inside
   AbstractOcclusion.WebGpuWater (Unity) + the island generator package.

   Ported subsystems (constants and equations mirrored from the Unity HLSL/C#):
     - Spectral FFT ocean:  OceanFft.compute / WaterOceanSpectrum.cs / WaterOceanFft.cs
     - Surface shading:     WaterSurfaceFragStages / Specular / DetailNormal / FoamSampling
     - Shore & surf:        WaterShoreMath.hlsl / WaterSurfWaves.hlsl / WaterShoreDepthField.cs
     - Underwater:          WaterFog.hlsl / WaterUnderwaterFog.shader / WaterWaterline.hlsl
                            LargeBodyGodRays.shader / LargeBodyCaustics.shader / JerlovWaterTypes.cs
     - Island:              island generator Runtime (ShelteredSandyArchipelago preset)
     - Fly camera:          FlyCamera.cs
   Deliberate substitutions (no Unity scene infra here): planar/SSR reflection -> analytic
   sky; screen-space refraction -> bed-terrain sample (per the porting note in the source
   spec); shadowmap term in god rays -> 1 (open ocean); no temporal reprojection history.
   ===================================================================================== */

import * as THREE from 'three/webgpu';
import {
  Fn, If, Loop, Return, float, int, uint, bool, vec2, vec3, vec4, ivec2, uvec2,
  uniform, uniformArray, texture, textureLoad, textureStore, instanceIndex,
  attribute, varying, vertexStage, positionLocal, cameraPosition, screenCoordinate,
  uv as tslUV, positionWorld, mix, clamp, saturate, abs, sign, floor, fract, ceil, round, min, max, pow, exp, log2, sqrt,
  sin, cos, tan, atan, acos, cosh, normalize, length, lengthSq, dot, cross, reflect, refract,
  smoothstep, step, select, mat3, mat4, dFdx, dFdy, fwidth, frontFacing, time as tslTime,
  screenUV, viewportUV, getViewPosition, property, varyingProperty, Discard, pass, Break
} from 'three/tsl';
import GUI from 'lil-gui';

/* asset URLs — the build step may replace these with data: URLs for a single-file bundle */
const ASSET_WHITECAP = 'assets/whitecap.webp';
const ASSET_DETAIL_NORMAL = 'assets/waterdetail.webp';

const overlay = document.getElementById('overlay');
const overlayBar = document.querySelector('#bar > div');
const overlayStage = document.getElementById('stage');
const overlayErr = document.getElementById('err');
function setStage(text, frac) {
  overlayStage.textContent = text;
  if (frac !== undefined) overlayBar.style.width = `${Math.round(frac * 100)}%`;
}
function fatal(message) {
  overlay.classList.remove('hidden');
  overlayErr.style.display = 'block';
  overlayErr.textContent = message;
  setStage('failed');
  throw new Error(message);
}
const nextFrame = () => new Promise(requestAnimationFrame);

/* mirror console errors/warnings into an on-page panel (F8 toggles) — the demo is often
   opened from file:// where devtools access is awkward */
(function consoleMirror() {
  const box = document.createElement('div');
  box.id = 'console-mirror';
  box.style.cssText = 'position:fixed;left:0;right:0;bottom:0;max-height:45vh;overflow:auto;' +
    'background:#000c;color:#ff9d8f;font:11px/1.4 monospace;padding:6px 10px;z-index:99;display:none;white-space:pre-wrap;';
  document.body.appendChild(box);
  let shown = false, count = 0;
  const push = (kind, args) => {
    count++;
    if (count > 400) return;
    const line = document.createElement('div');
    line.style.color = kind === 'warn' ? '#ffd98f' : '#ff9d8f';
    line.textContent = '[' + kind + '] ' + args.map((a) => {
      try { return typeof a === 'string' ? a : JSON.stringify(a); } catch { return String(a); }
    }).join(' ');
    box.appendChild(line);
    if (kind === 'error' && !shown) { shown = true; box.style.display = 'block'; }
  };
  const origError = console.error.bind(console), origWarn = console.warn.bind(console);
  console.error = (...a) => { push('error', a); origError(...a); };
  console.warn = (...a) => { push('warn', a); origWarn(...a); };
  window.addEventListener('error', (e) => push('error', [e.message, e.filename, e.lineno]));
  window.addEventListener('unhandledrejection', (e) => push('error', [String(e.reason && e.reason.stack || e.reason)]));
  window.addEventListener('keydown', (e) => {
    if (e.code === 'F8') box.style.display = box.style.display === 'none' ? 'block' : 'none';
  });
})();

/* =====================================================================================
   SECTION 1 — Ported constants (verbatim from the Unity sources)
   ===================================================================================== */

const OCEAN = {
  FFT_SIZE: 128,
  FFT_STAGES: 7,
  MAX_CASCADES: 4,
  TWO_PI: 6.28318530718,
  GRAVITY: 9.81,
  KMIN: 1e-4,
  SPECTRUM_SEED: 1337,
  MAX_FOAM_DT: 0.1,
  TOP_BAND_PEAK_MULTIPLE: 2,
  CASCADE_BAND_RATIO: 0.2360679775,          // 1/phi^3 — irrational so tiles never repeat together
  VISIBLE_AREA_BAND_MULTIPLE: 8,
  CASCADE_TILE_OVERSAMPLE: 4,
  CASCADE_WAVELENGTH_FRACTION: 0.0625,       // 0.25 / oversample
  JONSWAP_PEAK_DECAY: 1.25,
  JONSWAP_SIGMA_LOW: 0.07,
  JONSWAP_SIGMA_HIGH: 0.09,
  TMA_SLOPE: 1.8,
  TMA_OFFSET: 1.125,
  SPREAD_PEDESTAL: 0.5287611,
  SWELL_WIDTH: 0.12,
  SWELL_DIR_POWER: 6.0,
  TURBULENCE_FLOOR_FINEST: 0.5,
  TURBULENCE_FLOOR_FINE: 0.25,
  SIG_HEIGHT_TO_RMS: 4,
  REAL_PART_VARIANCE_SHARE: 0.5,
  FOAM_ACCUM_RATE: 5.0,
  FOAM_DENSE_REFERENCE: 0.6,
  FOAM_CASCADE1_DAMP: 0.25,
  FOAM_FINEST_DAMP: 0.5,
  FOAM_CREST_GATE_LOW: 0.5,
  FAR_SLOPE_FLOOR: [0.15, 0.20, 0.25, 0.25],
};

/* "Ocean - Beach Demo.asset" preset + wizard ocean defaults (the shipped ocean look) */
const SEA_DEFAULTS = {
  significantWaveHeight: 3.0,
  peakWavelength: 90.0,
  peakSharpness: 3.3,
  waveScale: 1.0,
  seaDepth: 0.0,
  cascadeReach: 8.0,
  swellHeight: 0.5,
  swellWavelength: 140.0,
  windTurbulence: 0.15,
  choppiness: 1.0,
  amplitude: 1.0,
  windSpeed: 6.0,
  windFromDegrees: 0.0,        // set at scene build so waves travel toward the beach
  foamWindThreshold: 3.0,
  foamCoverage: 0.9,
  foamStrength: 1.5,
  foamFadeRate: 0.5,
  foamDeposit: 0.85,           // slowFadeFraction = 1 - deposit
  foamDrift: 0.2,
  foamMax: 1.0,
  foamAnisotropy: 1.0,
  foamCrestGate: 0.8,
  foamFaceBias: 0.85,
  foamCascadeMix: 1.0,
};

const LOOK = {
  reflectionStrength: 0.15,
  envReflectionIntensity: 0.3,
  fresnelFloor: 0.25,
  fresnelPower: 3.3,
  fresnelF0: 0.020373,
  sunRoughness: 0.08,
  roughnessFar: 0.2,
  roughnessFarDistance: 1000.0,
  roughnessFalloff: 1.0,
  reflectionAnisoStretch: 0.5,
  sunSheen: 0.0,
  sunSheenRoughness: 0.6,
  sunGrazeBoost: 0.0,
  refractionStrength: 1.0,
  refractionDistortion: 0.05,
  waveNormalStrength: 1.0,
  detailNormalStrength: 0.5,
  detailNormalScale: 8.0,
  detailNormalFarScale: 250.0,
  detailNormalFarDistance: 700.0,
  detailNormalSpeed: 0.25,
  detailNormalFarSpeed: 3.0,
  detailNormalCrestBoost: 0.6,
  detailNormalDistanceBoost: 0.0,
  horizonHazeDensity: 0.85,
  horizonHazeColor: [0.7, 0.8, 0.9], horizonHazeAlpha: 0.0,
  oceanFoamColor: [1, 1, 1], oceanFoamOpacity: 1.0,
  oceanFoamTileSize: 8.0,
  oceanFoamFeather: 0.7,
  oceanFoamStreakStretch: 1.7,
  oceanFoamTextureInfluence: 0.8,
  oceanFoamDepthTint: 0.45,
  foamNormalStrength: 1.0,
  scatterIntensity: 0.1,
  scatterAmbientTerm: 1.0,
  scatterSunTerm: 1.0,
  scatterAnisotropy: 0.5,
  waterOpacity: 0.2,
  fogDensity: 0.3,
  sssIntensity: 1.0,
  sssSunFalloff: 4.0,
  sssPinchMin: 0.2,
  sssPinchMax: 0.8,
  sssPinchFalloff: 1.5,
};

/* JerlovWaterTypes.cs — verbatim tables (extinction 1/m, body colour linear) */
const JERLOV = {
  'Open ocean I':   { ext: [0.6189, 0.0582, 0.0331], body: [0.0000, 0.0323, 0.1223] },
  'Open ocean IA':  { ext: [0.6189, 0.0587, 0.0373], body: [0.0000, 0.0348, 0.1144] },
  'Open ocean IB':  { ext: [0.6189, 0.0584, 0.0384], body: [0.0000, 0.0721, 0.1741] },
  'Open ocean II':  { ext: [0.6189, 0.0583, 0.0380], body: [0.0000, 0.2805, 0.5495] },
  'Open ocean III': { ext: [0.7124, 0.0612, 0.0514], body: [0.0335, 0.5801, 0.8500] },
  'Coastal 1C':     { ext: [0.8125, 0.0667, 0.1043], body: [0.0132, 0.2439, 0.2391] },
  'Coastal 3C':     { ext: [0.6587, 0.0712, 0.1508], body: [0.0775, 0.5448, 0.4060] },
  'Coastal 5C':     { ext: [0.3035, 0.0849, 0.2280], body: [0.1295, 0.5584, 0.3404] },
  'Coastal 7C':     { ext: [0.2790, 0.1154, 0.3611], body: [0.2564, 0.6742, 0.3732] },
  'Coastal 9C':     { ext: [0.2865, 0.1779, 0.6630], body: [0.3349, 0.6065, 0.2968] },
  'Legacy':         { ext: [0.9000, 0.3000, 0.1600], body: [0.0250, 0.3000, 0.5000] },
};

/* Shore / surf — WaterShoreMath.hlsl + WaterSurfWaves.hlsl constants (verbatim) */
const SHORE = {
  DEEP_SENTINEL: 1e9,
  BORDER_FEATHER: 0.08,
  SHOAL_WAVELENGTH_FACTOR: 2.0,
  BAND_INNER_FRACTION: 0.35,
  GREEN_MIN_DEPTH: 0.05,
  GREEN_EXPONENT: 0.25,
  WARP_REACH_SPACINGS: 2.0,
};
const SURF = {
  MIN_DEPTH: 0.05, MIN_PERIOD: 0.5, MIN_WAVELENGTH: 1.0, MIN_GREENS: 1.0,
  BEAT_WRAP_FRONTS: 1280.0,
  CREST_SEED_DRIFT_A: 0.34852044, CREST_SEED_DRIFT_B: 0.45160394,
  CREST_MIN_LENGTH: 4.0, CREST_SEED_FRESH_SCALE: 37.0, CREST_FRESH_OCTAVE_RATIO: 1.3,
  CREST_DIR_A_Z: 0.31, CREST_DIR_B_X: -0.42, CREST_FREQ_RATIO: 1.7,
  CREST_OCTAVE_B_WEIGHT: 0.5, CREST_NOISE_NORM: 1.5,
  FACE_FRACTION: 0.10, BACK_FRACTION: 0.24,
  SET_WAVES: 5.0, SETAMP_HASH_PHASE: 2.4, SETAMP_FLOOR: 0.35,
  SETAMP_JITTER_MIN: 0.9, SETAMP_JITTER_MAX: 1.1,
  EDGE_BLEND_START: 0.35, SECH_ARG_MAX: 20.0, SLOPE_EPSILON: 0.5,
  MIN_INFLUENCE: 0.001, MIN_BAND_DEPTH: 0.25,
  WET_FADE_LO: -0.05, WET_FADE_HI: 0.1,
  EXPOSURE_FACING_LO: -0.25, EXPOSURE_FACING_HI: 0.5,
  SWASH_UPRUSH: 0.30, FILM_THICKNESS: 0.03, FILM_BLEND: 0.05,
  TRAIL_BASE_WEIGHT: 0.4, SWASH_SLOPE_FEATHER: 0.6,
  DRY_TIME_CONSTANTS: 3.0, DEFAULT_DRY_SECONDS: 3.0,
  XI_SPILL_END_LO: 0.45, XI_SPILL_END_HI: 0.60,
  XI_SURGE_START_LO: 2.8, XI_SURGE_START_HI: 3.6,
  DEEPWATER_LENGTH_COEF: 1.56,
  GAMMA_BASE: 0.6, GAMMA_SLOPE_GAIN: 5.0, GAMMA_MAX: 1.1,
  BORE_STABLE_GAMMA: 0.40,
  CRESTING_START: 0.75, CRESTING_END: 1.05, BROKEN_START: 1.05, BROKEN_END: 1.5,
  LEAN_REACH_FRACTION: 0.25, BORE_WIDTH_FACTOR: 1.4,
  RUNUP_XI_CAP: 2.3, SURGE_RUNUP_BOOST: 1.35,
  PLUNGE_FACE_SHARPEN: 0.6, PLUNGE_LANDING_AHEAD: 1.0, PLUNGE_LANDING_WIDTH: 0.5,
  PLUNGE_LANDING_FOAM: 0.8, PLUNGE_TRAIL_NARROW: 0.55,
  PLUNGE_WHITEWASH_GAIN: 0.5, PLUNGE_BREAKER_GAIN: 0.8, PLUNGE_BREAKER_WIDEN: 0.6,
  HASH_SINE_FREQ: 12.9898, HASH_SINE_SCALE: 43758.5453,
};

/* Surf authoring values (the WaterVolume partial with authored defaults is not in the
   sources; documented defaults are used where known — see spec §7 — the rest are
   artist values chosen for the sandy-beach preset, all exposed in the GUI). */
const SURF_DEFAULTS = {
  enabled: true,
  amplitude: 1.15,            // deep-water set height H0 (m)
  wavelength: 55.0,
  period: 9.0,                // documented default
  bandDepth: 10.0,
  setStrength: 0.65,
  lean: 0.45,
  compression: 0.35,
  greens: 1.6,
  refraction: 0.6,
  ambientFade: 0.85,
  swashAmplitude: 1.0,        // 1 = physics (Hunt run-up)
  swashMaxSlopeTan: 0.45,
  waterlineFoam: 0.55,
  smallWaveFoam: 0.0,         // documented default (off)
  crestLength: 60.0,
  crestVariation: 0.6,        // documented default
  crestPersistence: 0.0,      // documented default
  directionality: 0.45,
  foamStrength: 1.0,
  foamFeather: 0.5,
  foamTileSize: 6.0,
  foamColor: [1, 1, 1], foamOpacity: 1.0,
  foamBoreGain: 1.0, foamTrailGain: 1.0, foamTrailLength: 1.0,
  foamTrailDissolve: 4.0,
  foamCrestCap: 0.35,
  swashFoam: 0.8,
  swashFoamWidth: 0.25,       // documented default
  swashFoamDissolve: 0.6,
  shoalDepth: 8.0,            // authored attenuation/amplification band
  wetDryTime: 3.0,
};

/* Underwater / god rays / caustics — Beach Demo values */
const UW = {
  godRayColor: [0.0, 0.75, 1.0],
  godRayDensity: 0.3,
  godRaySteps: 24,
  godRayAnisotropy: 0.6,
  godRayCausticStrength: 4.0,
  godRayCausticSmooth: 2.0,
  godRayCausticDepthSoften: 0.25,
  godRayDepthFade: 0.5,          // wizard: beams die by ~10 m
  causticDepthFade: 0.12,
  causticStrength: 3.0,
  causticTimeScale: 0.5,
  causticRippleScale: 3.0,
  causticRippleStrength: 1.0,
  depthDarkenStrength: 1.0,
  waterlineWidthPx: 5.0,
  waterlineStrength: 0.7,
  waterlineWarp: 0.35,
  CAUSTIC_REFERENCE_DEPTH: 4.0,
  CAUSTIC_WINDOW_FADE: 0.15,
  CAUSTIC_FOCUS_SCALE: 0.2,
  MIN_REFRACTED_LIGHT_Y: 0.05,
  SHAFT_MAX_DISTANCE: 100.0,
  GODRAY_CAUSTIC_DISTANCE_FADE: 0.005,
  GODRAY_BASE_CALM_DEPTH: 1.0,
  GODRAY_BASE_CALM_LOD: 1.0,
  GODRAY_BASE_CALM_GAIN: 0.3,
  GODRAY_SUBMERGE_FADE_METERS: 0.25,
  CROSS_STEP: 1.5, CROSS_MAX_STEPS: 24, CROSS_REFINE_ITERS: 12, SEAM_BLEND_START: 0.75,
  SURFACE_BAND_AMPLITUDES: 3.0, SURFACE_BAND_PAD: 2.0, SURFACE_BAND_CREST_REACH: 1.2,
  WATERLINE_FEATHER_PIXELS: 6.0,
  IOR_RATIO: 1.0 / 1.333,
};

/* Foam sampling constants — WaterSurfaceFoamSampling.hlsl (verbatim) */
const FOAMC = {
  MASK_EPSILON: 0.005,
  PARALLAX_HEIGHT: 0.04, PARALLAX_MIN_VIEW_Y: 0.25,
  TEXTURE_FADE_START: 120.0, TEXTURE_FADE_RANGE: 400.0,
  OCTAVE2_SCALE: 2.37, OCTAVE2_COS: 0.8660254, OCTAVE2_SIN: 0.5, OCTAVE_BLEND_DIST: 60.0,
  PATTERN_MEAN: 0.501, PATTERN_STDDEV: 0.189, PATTERN_CDF_SPAN: 2.45, OCTAVE_BLEND_NORM: 0.70710678,
  CONTRAST: 1.6, CONTRAST_DENSE: 1.0,
  NORMAL_DELTA: 4 / 1024, NORMAL_GAIN: 2.5,
  LIGHT_WRAP: 0.4, AMBIENT: 0.35,
  TRAIL_ERODE_MAX: 0.6, SWASH_ERODE_MAX: 0.7, SWASH_DEPOSIT_PEAK: 0.45,
};

/* Detail normal constants — WaterSurfaceDetailNormal.hlsl (verbatim) */
const DETC = {
  DIR0: [0.94, 0.34], DIR1: [-0.85, -0.53],
  FAR_TILE_MULT: 2.6180340, CROSS_TILE_SPLIT: 1.2720196, CROSS_SPEED_SPLIT: 1.1278865,
  CREST_REFERENCE_SLOPE: 0.35, FAR_BLEND_START: 30.0,
  FADE_START: 1200.0, FADE_RANGE: 1800.0,
  HEX_LATTICE_SCALE: 3.4641016, HEX_WEIGHT_EXPONENT: 3.0,
};

/* Island generator — ShelteredSandyArchipelago.json (verbatim dump) */
const ISLAND_PRESET = {
  resolution: 513,                 // preset authored at 1025; 513 for startup speed —
                                   // erosion auto-rescales (ScaleErosionWithResolution)
  widthMeters: 1000, heightMeters: 60,
  seaLevel: 0.23,
  seeds: { terrain: 8675309, mountain: 12345, voronoi: 67890, warp: 24680,
           continental: 13579, erosion: 112233, beach: 445566, detail: 778899 },
  perlin:      { enabled: true, weight: 1.15, fbm: { scale: 690, octaves: 5, persistence: 0.52, lacunarity: 1.95 } },
  ridged:      { enabled: true, weight: 0.30, sharpness: 1.65, fbm: { scale: 900, octaves: 4, persistence: 0.40, lacunarity: 2.0 } },
  continental: { enabled: true, height: 0.34, fbm: { scale: 1700, octaves: 2, persistence: 0.5, lacunarity: 2.0 } },
  warp:        { enabled: true, strength: 145, scale: 610 },
  falloff:     { enabled: true, innerRadius: 0.4, outerRadius: 1.0, edgeSharpness: 0.9,
                 edgeNoiseStrength: 0.3, edgeNoiseScale: 330, seaFloorHeight: 0.035, interiorLift: 0.045 },
  mountainBase:{ enabled: true, height: 0.065, shoreBlendRange: 0.2, fbm: { scale: 390, octaves: 3, persistence: 0.55, lacunarity: 2.0 } },
  hydraulic:   { enabled: true, dropletCount: 280000, rainAmount: 0.82, inertia: 0.1, sedimentCapacity: 3.2,
                 evaporation: 0.03, erodeSpeed: 0.2, depositSpeed: 0.42, gravity: 3.5,
                 dropletLifetime: 46, erosionRadius: 4 },
  thermal:     { enabled: true, iterations: 10, talusAngleDegrees: 30, strength: 0.36 },
  depressionFill: { enabled: true, fillStrength: 1.0, maxFillDepth: 0.06 },
  beach:       { enabled: true, inlandWidthMeters: 85, submergedWidthMeters: 135, bermHeightMeters: 1.25,
                 foreshoreDepthMeters: 4.8, profileExponent: 0.667, strength: 0.94,
                 maxCoastSlopeDegrees: 20, coastSlopeBlendDegrees: 12, maxShapedReliefMeters: 14,
                 edgeNoiseAmplitudeMeters: 48,
                 edgeNoiseFbm: { scale: 390, octaves: 3, persistence: 0.52, lacunarity: 2.0 },
                 sandCoverageWidthScale: 1.3 },
  coastal:     { enabled: true, sedimentAvailability: 0.92, bayPreference: 0.66, exposureStrength: 0.45,
                 windDirectionDegrees: 235, alongshoreCoherenceMeters: 120,
                 minimumWidthScale: 0.55, maximumWidthScale: 1.5,
                 sedimentFbm: { scale: 540, octaves: 3, persistence: 0.52, lacunarity: 2.0 } },
  blur:        { enabled: true, radius: 2, sigma: 1.2, iterations: 1 },
  detail:      { enabled: true, strength: 0.003, fbm: { scale: 34, octaves: 3, persistence: 0.5, lacunarity: 2.3 } },
  cliff:       { slopeStartDegrees: 42, slopeEndDegrees: 58 },
  coverage:    { rockSlopeStartDegrees: 32, rockSlopeEndDegrees: 48 },
};

/* IslandTerrain.shader substrate constants (verbatim) */
const SUBSTRATE = {
  seabed: { tint: [0.34, 0.33, 0.28], tiling: 0.35, smoothness: 0.30, wetDarken: 0.55, wetSmoothness: 0.60 },
  beach:  { tint: [0.80, 0.72, 0.55], tiling: 0.50, smoothness: 0.12, wetDarken: 0.85, wetSmoothness: 0.85 },
  rock:   { tint: [0.42, 0.40, 0.38], tiling: 0.25, smoothness: 0.25, wetDarken: 0.65, wetSmoothness: 0.80 },
  grass:  { tint: [0.28, 0.38, 0.18], tiling: 0.60, smoothness: 0.15, wetDarken: 0.25, wetSmoothness: 0.45 },
  wetBandHeight: 0.25, wetNormalFlatten: 0.6, wetStrength: 1.0,
  triplanarSharpness: 4.0, underwaterTint: [0.55, 0.85, 0.95], specColor: [0.2, 0.2, 0.2],
};

/* =====================================================================================
   SECTION 2 — CPU math primitives (DotNetRandom, Mathf, Perlin/Fbm/Ridged/Warp)
   Ported from island generator Runtime/Noise + .NET System.Random (Knuth subtractive)
   ===================================================================================== */

class DotNetRandom {
  constructor(seed) {
    const MBIG = 2147483647, MSEED = 161803398;
    const subtraction = (seed === -2147483648) ? 2147483647 : Math.abs(seed);
    let mj = MSEED - subtraction;
    this.arr = new Array(56).fill(0);
    this.arr[55] = mj;
    let mk = 1;
    for (let i = 1; i < 55; i++) {
      const ii = (21 * i) % 55;
      this.arr[ii] = mk;
      mk = mj - mk;
      if (mk < 0) mk += MBIG;
      mj = this.arr[ii];
    }
    for (let k = 1; k < 5; k++)
      for (let i = 1; i < 56; i++) {
        this.arr[i] -= this.arr[1 + (i + 30) % 55];
        if (this.arr[i] < 0) this.arr[i] += MBIG;
      }
    this.inext = 0; this.inextp = 21;
  }
  _sample() {
    let i = this.inext + 1; if (i >= 56) i = 1;
    let j = this.inextp + 1; if (j >= 56) j = 1;
    let r = this.arr[i] - this.arr[j];
    if (r === 2147483647) r--;
    if (r < 0) r += 2147483647;
    this.arr[i] = r;
    this.inext = i; this.inextp = j;
    return r;
  }
  sample() { return this._sample() * (1.0 / 2147483647); }
  next(maxValue) { return Math.trunc(this.sample() * maxValue); }
  nextDouble() { return this.sample(); }
}

const clamp01 = (t) => Math.min(Math.max(t, 0), 1);
const mLerp = (a, b, t) => a + (b - a) * clamp01(t);          // Unity Mathf.Lerp clamps t
const uLerp = (a, b, t) => a + (b - a) * t;                    // unclamped
const mInverseLerp = (a, b, v) => clamp01((v - a) / (b - a));
const smooth01 = (t) => { t = clamp01(t); return t * t * (3 - 2 * t); };

class PerlinNoise {
  constructor(seed) {
    this.perm = new Int32Array(512);
    for (let i = 0; i < 256; i++) this.perm[i] = i;
    const rng = new DotNetRandom(seed);
    for (let i = 255; i >= 1; i--) {
      const s = rng.next(i + 1);
      const t = this.perm[i]; this.perm[i] = this.perm[s]; this.perm[s] = t;
    }
    for (let i = 0; i < 256; i++) this.perm[256 + i] = this.perm[i];
  }
  static grad(hash, x, y) {
    switch (hash & 7) {
      case 0: return x + y; case 1: return -x + y; case 2: return x - y; case 3: return -x - y;
      case 4: return x; case 5: return -x; case 6: return y; default: return -y;
    }
  }
  sample(x, y) {
    const fx = Math.floor(x), fy = Math.floor(y);
    const cellX = fx & 255, cellY = fy & 255;
    const dx = x - fx, dy = y - fy;
    const fade = (t) => t * t * t * (t * (t * 6 - 15) + 10);
    const u = fade(dx), v = fade(dy);
    const p = this.perm;
    const aa = p[p[cellX] + cellY], ba = p[p[cellX + 1] + cellY];
    const ab = p[p[cellX] + cellY + 1], bb = p[p[cellX + 1] + cellY + 1];
    const bottom = uLerp(PerlinNoise.grad(aa, dx, dy), PerlinNoise.grad(ba, dx - 1, dy), u);
    const top = uLerp(PerlinNoise.grad(ab, dx, dy - 1), PerlinNoise.grad(bb, dx - 1, dy - 1), u);
    return uLerp(bottom, top, v) * 1.4142136;
  }
}

class RidgedNoise {
  constructor(source, sharpness) { this.source = source; this.sharpness = Math.max(0.01, sharpness); }
  sample(x, y) {
    let ridge = 1 - Math.abs(this.source.sample(x, y));
    if (ridge < 0) ridge = 0;
    return Math.pow(ridge, this.sharpness) * 2 - 1;
  }
}

class DomainWarp {
  constructor(seed, strength, scale) {
    this.nx = new PerlinNoise(seed);
    this.ny = new PerlinNoise(seed + 7919);
    this.strength = strength;
    this.inv = 1 / Math.max(1, scale);
  }
  apply(x, y) {
    const sx = x * this.inv, sy = y * this.inv;
    return [x + this.nx.sample(sx, sy) * this.strength, y + this.ny.sample(sx, sy) * this.strength];
  }
}

function fbmSample(noise, x, y, s) {
  let frequency = 1 / Math.max(1, s.scale);
  let amplitude = 1, total = 0, norm = 0;
  for (let o = 0; o < s.octaves; o++) {
    total += noise.sample(x * frequency, y * frequency) * amplitude;
    norm += amplitude;
    amplitude *= s.persistence;
    frequency *= s.lacunarity;
  }
  return norm > 0 ? total / norm : 0;
}
const fbmSampleUnit = (n, x, y, s) => fbmSample(n, x, y, s) * 0.5 + 0.5;

/* =====================================================================================
   SECTION 3 — Island generation pipeline (HeightmapGenerator.Generate, exact order)
   ===================================================================================== */

function scaleErosion(preset) {
  const f = (preset.resolution - 1) / 1024;                     // LinearFactor vs reference 1025
  const areaCount = (n) => n <= 0 ? 0 : Math.max(1, Math.round(n * f * f));
  const cellDist = (n) => n <= 0 ? 0 : Math.max(1, Math.round(n * f));
  const iter = (n) => n <= 0 ? 0 : Math.max(1, Math.round(n * f));
  return {
    hydraulic: { ...preset.hydraulic,
      dropletCount: areaCount(preset.hydraulic.dropletCount),
      dropletLifetime: cellDist(preset.hydraulic.dropletLifetime),
      erosionRadius: cellDist(preset.hydraulic.erosionRadius) },
    thermal: { ...preset.thermal, iterations: iter(preset.thermal.iterations) },
    blur: { ...preset.blur, radius: cellDist(preset.blur.radius), sigma: preset.blur.sigma * f },
  };
}

function normalizeToUnitRange(v) {
  let mn = Infinity, mx = -Infinity;
  for (let i = 0; i < v.length; i++) { if (v[i] < mn) mn = v[i]; if (v[i] > mx) mx = v[i]; }
  if (mx - mn <= 1.1920929e-7) { v.fill(0); return; }
  const inv = 1 / (mx - mn);
  for (let i = 0; i < v.length; i++) v[i] = (v[i] - mn) * inv;
}

async function generateIsland(preset, progress) {
  const res = preset.resolution;
  const n = res * res;
  const map = new Float32Array(n);
  const scaled = scaleErosion(preset);
  const S = preset.seeds;

  /* 1. base shape */
  {
    const perlin = new PerlinNoise(S.terrain);
    const ridged = new RidgedNoise(new PerlinNoise(S.mountain), preset.ridged.sharpness);
    const continental = new PerlinNoise(S.continental);
    const warp = preset.warp.enabled ? new DomainWarp(S.warp, preset.warp.strength, preset.warp.scale) : null;
    for (let y = 0; y < res; y++) {
      for (let x = 0; x < res; x++) {
        let sx = x, sy = y;
        if (warp) { const w = warp.apply(x, y); sx = w[0]; sy = w[1]; }
        let h = 0;
        if (preset.perlin.enabled) h += preset.perlin.weight * fbmSampleUnit(perlin, sx, sy, preset.perlin.fbm);
        if (preset.ridged.enabled) h += preset.ridged.weight * fbmSampleUnit(ridged, sx, sy, preset.ridged.fbm);
        if (preset.continental.enabled) h += preset.continental.height * fbmSampleUnit(continental, sx, sy, preset.continental.fbm);
        map[y * res + x] = h;
      }
      if ((y & 63) === 0) { progress('island: base shape', y / res * 0.25); await nextFrame(); }
    }
    normalizeToUnitRange(map);
  }

  /* 2. island falloff */
  if (preset.falloff.enabled) {
    const F = preset.falloff;
    const edgeNoise = new PerlinNoise(S.terrain);
    const half = (res - 1) * 0.5;
    const invNoiseScale = 1 / Math.max(1, F.edgeNoiseScale);
    const innerRadius = Math.min(F.innerRadius, F.outerRadius - 0.001);
    const interiorFloor = clamp01(preset.seaLevel + F.interiorLift);
    for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
      const nx = (x - half) / half, ny = (y - half) / half;
      let radius = Math.sqrt(nx * nx + ny * ny);
      radius += edgeNoise.sample(x * invNoiseScale, y * invNoiseScale) * F.edgeNoiseStrength;
      let falloff = smooth01(mInverseLerp(innerRadius, F.outerRadius, radius));
      falloff = Math.pow(falloff, F.edgeSharpness);
      const i = y * res + x, v = map[i];
      const compressed = mLerp(interiorFloor, 1, v);
      const lifted = mLerp(v, compressed, 1 - falloff);
      map[i] = mLerp(lifted, F.seaFloorHeight, falloff);
    }
  }

  /* 3. mountain base variation */
  if (preset.mountainBase.enabled) {
    const M = preset.mountainBase;
    const noise = new PerlinNoise(S.mountain);
    const blendTop = preset.seaLevel + M.shoreBlendRange;
    for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
      const i = y * res + x, h = map[i];
      const landMask = smooth01(mInverseLerp(preset.seaLevel, blendTop, h));
      if (landMask <= 0) continue;
      map[i] = h + fbmSample(noise, x, y, M.fbm) * M.height * landMask;
    }
  }
  progress('island: erosion', 0.3); await nextFrame();

  /* 4. hydraulic erosion (single RNG stream — droplet order matters) */
  if (scaled.hydraulic.enabled && scaled.hydraulic.dropletCount > 0) {
    const H = scaled.hydraulic;
    const rng = new DotNetRandom(S.erosion);
    const R = H.erosionRadius;
    const brush = [];
    for (let oy = -R; oy <= R; oy++) for (let ox = -R; ox <= R; ox++) {
      const d = Math.sqrt(ox * ox + oy * oy);
      if (d > R) continue;
      brush.push([ox, oy, 1 - d / R]);
    }
    let wsum = 0; for (const b of brush) wsum += b[2];
    for (const b of brush) b[2] /= wsum;

    const sampleHG = (nodeX, nodeY, ox, oy) => {
      const i = nodeY * res + nodeX;
      const BL = map[i], BR = map[i + 1], TL = map[i + res], TR = map[i + res + 1];
      return [
        BL * (1 - ox) * (1 - oy) + BR * ox * (1 - oy) + TL * (1 - ox) * oy + TR * ox * oy,
        (BR - BL) * (1 - oy) + (TR - TL) * oy,
        (TL - BL) * (1 - ox) + (TR - BR) * ox,
      ];
    };
    const depositBilinear = (nodeX, nodeY, ox, oy, amount) => {
      if (amount <= 0) return;
      const i = nodeY * res + nodeX;
      map[i] += amount * (1 - ox) * (1 - oy);
      map[i + 1] += amount * ox * (1 - oy);
      map[i + res] += amount * (1 - ox) * oy;
      map[i + res + 1] += amount * ox * oy;
    };
    const chunk = Math.max(1, Math.floor(H.dropletCount / 24));
    for (let dIdx = 0; dIdx < H.dropletCount; dIdx++) {
      let posX = rng.nextDouble() * (res - 2) + 0.5;
      let posY = rng.nextDouble() * (res - 2) + 0.5;
      let dirX = 0, dirY = 0, speed = 1, water = H.rainAmount, sediment = 0;
      let nodeX = Math.trunc(posX), nodeY = Math.trunc(posY);
      let cellOffX = posX - nodeX, cellOffY = posY - nodeY;
      for (let stepI = 0; stepI < H.dropletLifetime; stepI++) {
        const [height, gradX, gradY] = sampleHG(nodeX, nodeY, cellOffX, cellOffY);
        dirX = dirX * H.inertia - gradX * (1 - H.inertia);
        dirY = dirY * H.inertia - gradY * (1 - H.inertia);
        const len = Math.sqrt(dirX * dirX + dirY * dirY);
        if (len <= 1.1920929e-7) break;
        dirX /= len; dirY /= len;
        posX += dirX; posY += dirY;
        if (posX < 1 || posX >= res - 2 || posY < 1 || posY >= res - 2) break;
        const newNodeX = Math.trunc(posX), newNodeY = Math.trunc(posY);
        const newHeight = sampleHG(newNodeX, newNodeY, posX - newNodeX, posY - newNodeY)[0];
        const heightDelta = newHeight - height;
        const capacity = Math.max(-heightDelta * speed * water * H.sedimentCapacity, 0.0001);
        if (sediment > capacity || heightDelta > 0) {
          const deposit = heightDelta > 0 ? Math.min(heightDelta, sediment) : (sediment - capacity) * H.depositSpeed;
          sediment -= deposit;
          depositBilinear(nodeX, nodeY, cellOffX, cellOffY, deposit);
        } else {
          const erosion = Math.min((capacity - sediment) * H.erodeSpeed, -heightDelta);
          if (erosion > 0) {
            let visibleWeight = 0;
            for (const [ox, oy, w] of brush) {
              const bx = nodeX + ox, by = nodeY + oy;
              if (bx >= 0 && bx < res && by >= 0 && by < res) visibleWeight += w;
            }
            if (visibleWeight > 0) {
              let removed = 0;
              for (const [ox, oy, w] of brush) {
                const bx = nodeX + ox, by = nodeY + oy;
                if (bx < 0 || bx >= res || by < 0 || by >= res) continue;
                const idx = by * res + bx;
                const share = erosion * w / visibleWeight;
                const available = Math.min(map[idx], share);
                map[idx] -= available;
                removed += available;
              }
              sediment += removed;
            }
          }
        }
        nodeX = newNodeX; nodeY = newNodeY;
        cellOffX = posX - newNodeX; cellOffY = posY - newNodeY;
        speed = Math.sqrt(Math.max(0, speed * speed - heightDelta * H.gravity));
        water *= 1 - H.evaporation;
        if (water <= 1.1920929e-7) break;
      }
      depositBilinear(nodeX, nodeY, cellOffX, cellOffY, sediment);
      if (dIdx % chunk === 0) { progress('island: hydraulic erosion', 0.3 + 0.35 * dIdx / H.dropletCount); await nextFrame(); }
    }
  }

  /* 5. thermal erosion */
  if (scaled.thermal.enabled && scaled.thermal.iterations > 0) {
    const T = scaled.thermal;
    const offX = [-1, 0, 1, -1, 1, -1, 0, 1], offY = [-1, -1, -1, 0, 0, 1, 1, 1];
    const cellWorldSize = preset.widthMeters / (res - 1);
    const verticalScale = Math.max(0.001, preset.heightMeters);
    const straight = Math.tan(T.talusAngleDegrees * Math.PI / 180) * cellWorldSize / verticalScale;
    const thr = offX.map((_, k) => (offX[k] !== 0 && offY[k] !== 0) ? straight * Math.SQRT2 : straight);
    const deltas = new Float32Array(n);
    const diff = new Float32Array(8);
    for (let it = 0; it < T.iterations; it++) {
      deltas.fill(0);
      for (let y = 1; y < res - 1; y++) for (let x = 1; x < res - 1; x++) {
        const i = y * res + x, h = map[i];
        let excessSum = 0, largest = 0;
        for (let k = 0; k < 8; k++) {
          const d = Math.max(0, h - map[(y + offY[k]) * res + (x + offX[k])] - thr[k]);
          diff[k] = d; excessSum += d; if (d > largest) largest = d;
        }
        if (excessSum <= 0) continue;
        const moved = largest * T.strength * 0.5;
        deltas[i] -= moved;
        for (let k = 0; k < 8; k++) if (diff[k] > 0)
          deltas[(y + offY[k]) * res + (x + offX[k])] += moved * diff[k] / excessSum;
      }
      for (let i = 0; i < n; i++) map[i] += deltas[i];
      progress('island: thermal erosion', 0.65 + 0.04 * (it + 1) / T.iterations); await nextFrame();
    }
  }

  /* 6. gaussian blur */
  if (scaled.blur.enabled && scaled.blur.radius >= 1 && scaled.blur.iterations >= 1) {
    const B = scaled.blur, R = B.radius;
    const kernel = new Float32Array(2 * R + 1);
    let ksum = 0;
    for (let o = -R; o <= R; o++) { kernel[o + R] = Math.exp(-(o * o) / (2 * B.sigma * B.sigma)); ksum += kernel[o + R]; }
    for (let k = 0; k < kernel.length; k++) kernel[k] /= ksum;
    const tmp = new Float32Array(n);
    for (let it = 0; it < B.iterations; it++) {
      for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
        let acc = 0;
        for (let o = -R; o <= R; o++) acc += kernel[o + R] * map[y * res + Math.min(Math.max(x + o, 0), res - 1)];
        tmp[y * res + x] = acc;
      }
      for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
        let acc = 0;
        for (let o = -R; o <= R; o++) acc += kernel[o + R] * tmp[Math.min(Math.max(y + o, 0), res - 1) * res + x];
        map[y * res + x] = acc;
      }
    }
  }
  progress('island: shoreline analysis', 0.72); await nextFrame();

  /* 7. shoreline distance (Felzenszwalb EDT, metres, + inland / - offshore) + coast slope */
  const shorelineDistance = buildShorelineDistance(map, res, preset.widthMeters, preset.seaLevel);
  const coastSlope = buildSlopeMap(map, res, preset.widthMeters, preset.heightMeters);

  /* 8. coastal morphology suitability + beach shaping */
  const suitability = buildCoastalSuitability(map, res, preset, shorelineDistance, coastSlope);
  const beach = applyBeachShaper(map, res, preset, shorelineDistance, coastSlope, suitability);
  progress('island: beach + detail', 0.8); await nextFrame();

  /* 9. detail noise + clamp */
  if (preset.detail.enabled && preset.detail.strength > 0) {
    const noise = new PerlinNoise(S.detail);
    for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
      const i = y * res + x, h = map[i];
      if (h <= preset.seaLevel) continue;
      const exposure = 1 - clamp01(beach.coverage[i]);
      if (exposure <= 0) continue;
      map[i] = h + fbmSample(noise, x, y, preset.detail.fbm) * preset.detail.strength * exposure;
    }
  }
  for (let i = 0; i < n; i++) map[i] = clamp01(map[i]);

  /* 10. depression fill (priority flood) */
  if (preset.depressionFill.enabled && preset.depressionFill.fillStrength > 0)
    applyDepressionFill(map, res, preset.depressionFill);
  progress('island: masks', 0.86); await nextFrame();

  /* 11-13. final slope + cliff + coverage masks */
  const slope = buildSlopeMap(map, res, preset.widthMeters, preset.heightMeters);
  const cliffStart = preset.cliff.slopeStartDegrees / 90, cliffEnd = preset.cliff.slopeEndDegrees / 90;
  const cliff = new Float32Array(n);
  for (let i = 0; i < n; i++) cliff[i] = smooth01(mInverseLerp(cliffStart, cliffEnd, slope[i]));
  const rockStart = preset.coverage.rockSlopeStartDegrees / 90;
  const rockEnd = Math.max(rockStart + 0.001, preset.coverage.rockSlopeEndDegrees / 90);
  const seabed = new Float32Array(n), rock = new Float32Array(n), grass = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const b = clamp01(beach.coverage[i]);
    const remaining = 1 - b;
    if (map[i] <= preset.seaLevel) { seabed[i] = remaining; }
    else {
      const rockShare = Math.max(cliff[i], smooth01(mInverseLerp(rockStart, rockEnd, slope[i])));
      rock[i] = remaining * rockShare;
      grass[i] = remaining - rock[i];
    }
  }
  return { map, res, slope, shorelineDistance, beachCoverage: beach.coverage, beachZone: beach.zone,
           seabed, rock, grass };
}

function buildShorelineDistance(map, res, widthMeters, seaLevel) {
  const n = res * res;
  const unreachable = 2 * res * res;
  const land = (i) => map[i] > seaLevel;
  const edt = (seedIsWater) => {
    const f = new Float64Array(n);
    for (let i = 0; i < n; i++) f[i] = (land(i) !== seedIsWater) ? 0 : unreachable;
    const line = new Float64Array(res), d = new Float64Array(res);
    const v = new Int32Array(res), z = new Float64Array(res + 1);
    const transform = (get, set) => {
      for (let q = 0; q < res; q++) line[q] = get(q);
      let k = 0; v[0] = 0; z[0] = -Infinity; z[1] = Infinity;
      for (let q = 1; q < res; q++) {
        let s = ((line[q] + q * q) - (line[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
        while (s <= z[k]) {
          k--;
          s = ((line[q] + q * q) - (line[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
        }
        k++; v[k] = q; z[k] = s; z[k + 1] = Infinity;
      }
      k = 0;
      for (let q = 0; q < res; q++) {
        while (z[k + 1] < q) k++;
        d[q] = (q - v[k]) * (q - v[k]) + line[v[k]];
      }
      for (let q = 0; q < res; q++) set(q, d[q]);
    };
    for (let x = 0; x < res; x++) transform((q) => f[q * res + x], (q, val) => { f[q * res + x] = val; });
    for (let y = 0; y < res; y++) transform((q) => f[y * res + q], (q, val) => { f[y * res + q] = val; });
    return f;
  };
  const squaredToSea = edt(true), squaredToLand = edt(false);
  const cellWorldSize = widthMeters / (res - 1);
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const isLand = land(i);
    const dCells = Math.sqrt(isLand ? squaredToSea[i] : squaredToLand[i]) - 0.5;
    out[i] = (isLand ? dCells : -dCells) * cellWorldSize;
  }
  return out;
}

function buildSlopeMap(map, res, widthMeters, heightMeters) {
  const cellWorldSize = widthMeters / (res - 1);
  const invSpan = 1 / (2 * cellWorldSize);
  const out = new Float32Array(res * res);
  const gc = (x, y) => map[Math.min(Math.max(y, 0), res - 1) * res + Math.min(Math.max(x, 0), res - 1)];
  for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
    const dX = (gc(x + 1, y) - gc(x - 1, y)) * heightMeters * invSpan;
    const dY = (gc(x, y + 1) - gc(x, y - 1)) * heightMeters * invSpan;
    out[y * res + x] = Math.atan(Math.sqrt(dX * dX + dY * dY)) * (180 / Math.PI) / 90;
  }
  return out;
}

function buildCoastalSuitability(map, res, preset, dist, coastSlope) {
  const n = res * res;
  const C = preset.coastal, B = preset.beach;
  if (!C.enabled) return new Float32Array(n).fill(1);
  const cellSize = preset.widthMeters / (res - 1);
  const maxBeachReach = Math.max(B.inlandWidthMeters, B.submergedWidthMeters) * C.maximumWidthScale;
  const coastSlopeEnd = B.maxCoastSlopeDegrees + Math.max(0.001, B.coastSlopeBlendDegrees);
  const wind = [Math.cos(C.windDirectionDegrees * Math.PI / 180), Math.sin(C.windDirectionDegrees * Math.PI / 180)];
  const sedimentNoise = new PerlinNoise(preset.seeds.beach);
  const raw = new Float32Array(n);
  const S = (f, x, y) => f[Math.min(Math.max(y, 0), res - 1) * res + Math.min(Math.max(x, 0), res - 1)];
  for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
    const i = y * res + x;
    if (Math.abs(dist[i]) > maxBeachReach) continue;
    const slopeDeg = coastSlope[i] * 90;
    const slopeGate = 1 - smooth01(mInverseLerp(B.maxCoastSlopeDegrees, coastSlopeEnd, slopeDeg));
    if (slopeGate <= 0) continue;
    const gradX = (S(dist, x + 1, y) - S(dist, x - 1, y)) / (2 * cellSize);
    const gradY = (S(dist, x, y + 1) - S(dist, x, y - 1)) / (2 * cellSize);
    const g2 = gradX * gradX + gradY * gradY;
    let exposure = 0;
    if (g2 > 1e-4) {
      const gl = Math.sqrt(g2);
      exposure = Math.max(0, (-gradX / gl) * wind[0] + (-gradY / gl) * wind[1]);
    }
    const leeWeight = 1 - exposure * C.exposureStrength;
    const lap = S(dist, x - 1, y) + S(dist, x + 1, y) + S(dist, x, y - 1) + S(dist, x, y + 1) - 4 * S(dist, x, y);
    const curvature = lap / (cellSize * cellSize);
    const bayWeight = clamp01(0.5 + curvature * 90);
    const geology = mLerp(1 - C.sedimentAvailability, 1, fbmSampleUnit(sedimentNoise, x * cellSize, y * cellSize, C.sedimentFbm));
    const planform = mLerp(1, bayWeight, C.bayPreference);
    raw[i] = slopeGate * leeWeight * geology * planform;
  }
  /* alongshore smoothing */
  const radius = Math.min(Math.max(Math.round(C.alongshoreCoherenceMeters / Math.max(cellSize, 0.01)), 1), 32);
  const smoothed = new Float32Array(n);
  for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
    const i = y * res + x;
    if (Math.abs(dist[i]) > maxBeachReach) continue;
    const gradX = (S(dist, x + 1, y) - S(dist, x - 1, y)) / (2 * cellSize);
    const gradY = (S(dist, x, y + 1) - S(dist, x, y - 1)) / (2 * cellSize);
    const g2 = gradX * gradX + gradY * gradY;
    if (g2 <= 1e-4) { smoothed[i] = raw[i]; continue; }
    const gl = Math.sqrt(g2);
    const tx = -gradY / gl, ty = gradX / gl;
    let sum = raw[i], weight = 1;
    for (let stepI = 1; stepI <= radius; stepI++) {
      const w = 1 - stepI / (radius + 1);
      for (const s of [1, -1]) {
        const sx = Math.min(Math.max(Math.round(x + tx * stepI * s), 0), res - 1);
        const sy = Math.min(Math.max(Math.round(y + ty * stepI * s), 0), res - 1);
        const j = sy * res + sx;
        if (Math.abs(dist[j]) > maxBeachReach) continue;
        sum += raw[j] * w; weight += w;
      }
    }
    smoothed[i] = sum / weight;
  }
  return smoothed;
}

function applyBeachShaper(map, res, preset, dist, coastSlope, suitability) {
  const n = res * res;
  const B = preset.beach, C = preset.coastal;
  const coverage = new Float32Array(n), zone = new Float32Array(n);
  if (!B.enabled) return { coverage, zone };
  const edgeNoise = new PerlinNoise(preset.seeds.beach);
  const verticalScale = preset.heightMeters;
  const cellWorldSize = preset.widthMeters / (res - 1);
  const coastGateEnd = B.maxCoastSlopeDegrees + Math.max(0.001, B.coastSlopeBlendDegrees);
  const widthScale = (s) => C.enabled ? mLerp(C.minimumWidthScale, C.maximumWidthScale, clamp01(s)) : 1;
  const bandWeight = (d, width) => {
    const nd = Math.abs(d) / Math.max(0.01, width);
    return nd >= 1 ? 0 : 1 - smooth01(nd);
  };
  const offshoreProfile = (profile, foreshoreDepth) => {
    const barDistance = Math.abs(profile - 0.58);
    const barWeight = 1 - smooth01(clamp01(barDistance / 0.18));
    const depth = foreshoreDepth * profile;
    const barLift = foreshoreDepth * 0.16 * barWeight;
    return -Math.max(0.001, depth - barLift);
  };
  const profileHeight = (d, width) => {
    const nd = clamp01(Math.abs(d) / width);
    const profile = Math.pow(nd, B.profileExponent);
    const vertical = d >= 0 ? B.bermHeightMeters * profile : offshoreProfile(profile, B.foreshoreDepthMeters);
    return preset.seaLevel + vertical / verticalScale;
  };
  const zoneCode = (d, width) => {
    const nd = clamp01(Math.abs(d) / width);
    return d < 0 ? mLerp(0.20, 0.45, nd) : mLerp(0.55, 1.00, nd);
  };
  const reliefGate = (reliefMeters, maxRelief) => {
    if (maxRelief <= 0) return 1;
    return 1 - smooth01(mInverseLerp(maxRelief, maxRelief * 1.5, reliefMeters));
  };
  for (let y = 0; y < res; y++) for (let x = 0; x < res; x++) {
    const i = y * res + x;
    const coastGate = 1 - smooth01(mInverseLerp(B.maxCoastSlopeDegrees, coastGateEnd, coastSlope[i] * 90));
    if (coastGate <= 0) continue;
    const d = dist[i];
    const ws = widthScale(suitability[i]);
    const bandWidth = (d >= 0 ? B.inlandWidthMeters : B.submergedWidthMeters) * ws;
    if (bandWidth < 0.01) continue;
    const edgeShift = fbmSample(edgeNoise, x * cellWorldSize, y * cellWorldSize, B.edgeNoiseFbm) * B.edgeNoiseAmplitudeMeters;
    const bandDistance = d + edgeShift;
    const coverageWeight = bandWeight(bandDistance, bandWidth * B.sandCoverageWidthScale);
    const geometryWeight = bandWeight(bandDistance, bandWidth);
    if (coverageWeight <= 0 && geometryWeight <= 0) continue;
    const h = map[i];
    const rg = reliefGate(Math.abs(h - preset.seaLevel) * verticalScale, B.maxShapedReliefMeters);
    if (rg <= 0) continue;
    const gate = coastGate * rg * suitability[i];
    coverage[i] = coverageWeight * gate;
    if (geometryWeight <= 0) continue;
    const target = profileHeight(d, bandWidth);
    map[i] = mLerp(h, target, B.strength * geometryWeight * gate);
    zone[i] = zoneCode(d, bandWidth) * geometryWeight * gate;
  }
  return { coverage, zone };
}

function applyDepressionFill(map, res, cfg) {
  const n = res * res;
  const offX = [-1, 0, 1, -1, 1, -1, 0, 1], offY = [-1, -1, -1, 0, 0, 1, 1, 1];
  const visited = new Uint8Array(n);
  /* binary min-heap over (floodLevel, index) */
  const heapLevel = new Float64Array(n * 2), heapIndex = new Int32Array(n * 2);
  let heapSize = 0;
  const push = (idx, level) => {
    let i = heapSize++;
    heapLevel[i] = level; heapIndex[i] = idx;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (heapLevel[p] <= heapLevel[i]) break;
      const tl = heapLevel[p], ti = heapIndex[p];
      heapLevel[p] = heapLevel[i]; heapIndex[p] = heapIndex[i];
      heapLevel[i] = tl; heapIndex[i] = ti;
      i = p;
    }
  };
  const pop = () => {
    const outIdx = heapIndex[0], outLevel = heapLevel[0];
    heapSize--;
    heapLevel[0] = heapLevel[heapSize]; heapIndex[0] = heapIndex[heapSize];
    let i = 0;
    for (;;) {
      const l = 2 * i + 1, r = l + 1;
      let s = i;
      if (l < heapSize && heapLevel[l] < heapLevel[s]) s = l;
      if (r < heapSize && heapLevel[r] < heapLevel[s]) s = r;
      if (s === i) break;
      const tl = heapLevel[s], ti = heapIndex[s];
      heapLevel[s] = heapLevel[i]; heapIndex[s] = heapIndex[i];
      heapLevel[i] = tl; heapIndex[i] = ti;
      i = s;
    }
    return [outIdx, outLevel];
  };
  for (let x = 0; x < res; x++) {
    visited[x] = 1; push(x, map[x]);
    const b = (res - 1) * res + x;
    if (!visited[b]) { visited[b] = 1; push(b, map[b]); }
  }
  for (let y = 1; y < res - 1; y++) {
    const l = y * res, r = y * res + res - 1;
    if (!visited[l]) { visited[l] = 1; push(l, map[l]); }
    if (!visited[r]) { visited[r] = 1; push(r, map[r]); }
  }
  while (heapSize > 0) {
    const [index, floodLevel] = pop();
    const spillLevel = floodLevel + 1e-6;
    const cx = index % res, cy = (index / res) | 0;
    for (let k = 0; k < 8; k++) {
      const nx2 = cx + offX[k], ny2 = cy + offY[k];
      if (nx2 < 0 || nx2 >= res || ny2 < 0 || ny2 >= res) continue;
      const ni = ny2 * res + nx2;
      if (visited[ni]) continue;
      visited[ni] = 1;
      const original = map[ni];
      const neighborFloodLevel = Math.max(original, spillLevel);
      const fillDepth = neighborFloodLevel - original;
      if (fillDepth > 0) {
        const allowed = (fillDepth > cfg.maxFillDepth) ? 0 : fillDepth * cfg.fillStrength;
        map[ni] = original + allowed;
      }
      push(ni, neighborFloodLevel);
    }
  }
}

/* =====================================================================================
   SECTION 4 — Shore fields (WaterShoreDepthField.cs: depth + jump-flood SDF + slope)
   and terrain-side textures
   ===================================================================================== */

function buildShoreFields(island, widthMeters, heightMeters, waterLevel, fieldRes) {
  const res = island.res;
  const sampleHeight = (wx, wz) => {                       // world -> heightmap bilinear (metres)
    const fx = (wx / widthMeters + 0.5) * (res - 1);
    const fz = (wz / widthMeters + 0.5) * (res - 1);
    const x0 = Math.min(Math.max(Math.floor(fx), 0), res - 2);
    const z0 = Math.min(Math.max(Math.floor(fz), 0), res - 2);
    const tx = Math.min(Math.max(fx - x0, 0), 1), tz = Math.min(Math.max(fz - z0, 0), 1);
    const i = z0 * res + x0;
    const h = island.map[i] * (1 - tx) * (1 - tz) + island.map[i + 1] * tx * (1 - tz)
            + island.map[i + res] * (1 - tx) * tz + island.map[i + res + 1] * tx * tz;
    return h * heightMeters;
  };
  const n = fieldRes * fieldRes;
  const half = widthMeters / 2;
  const texel = widthMeters / fieldRes;
  const worldOf = (i) => {
    const x = i % fieldRes, z = (i / fieldRes) | 0;
    return [((x + 0.5) / fieldRes) * 2 * half - half, ((z + 0.5) / fieldRes) * 2 * half - half];
  };
  const depth = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const [wx, wz] = worldOf(i);
    depth[i] = waterLevel - sampleHeight(wx, wz);          // + water column, - dry land
  }

  /* jump flood SDF (world metric), CPU — WaterShoreDepthField §1.3 */
  const seed = new Int32Array(n).fill(-1);
  let seedCount = 0;
  for (let z = 0; z < fieldRes; z++) for (let x = 0; x < fieldRes; x++) {
    const i = z * fieldRes + x;
    const under = depth[i] > 0;
    let isSeed = false;
    if (x > 0 && (depth[i - 1] > 0) !== under) isSeed = true;
    else if (x < fieldRes - 1 && (depth[i + 1] > 0) !== under) isSeed = true;
    else if (z > 0 && (depth[i - fieldRes] > 0) !== under) isSeed = true;
    else if (z < fieldRes - 1 && (depth[i + fieldRes] > 0) !== under) isSeed = true;
    if (isSeed) { seed[i] = i; seedCount++; }
  }
  const dist = new Float32Array(n), dirX = new Float32Array(n), dirZ = new Float32Array(n);
  const slopeTan = new Float32Array(n);
  if (seedCount > 0) {
    let src = seed, dst = new Int32Array(n);
    for (let stepC = fieldRes >> 1; stepC >= 1; stepC >>= 1) {
      for (let z = 0; z < fieldRes; z++) for (let x = 0; x < fieldRes; x++) {
        const i = z * fieldRes + x;
        let best = src[i];
        const [wx, wz] = worldOf(i);
        let bestD = Infinity;
        if (best >= 0) {
          const [sx, sz] = worldOf(best);
          bestD = (sx - wx) * (sx - wx) + (sz - wz) * (sz - wz);
        }
        for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) {
          if (dx === 0 && dz === 0) continue;
          const nx2 = x + dx * stepC, nz2 = z + dz * stepC;
          if (nx2 < 0 || nx2 >= fieldRes || nz2 < 0 || nz2 >= fieldRes) continue;
          const cand = src[nz2 * fieldRes + nx2];
          if (cand < 0) continue;
          const [sx, sz] = worldOf(cand);
          const d2 = (sx - wx) * (sx - wx) + (sz - wz) * (sz - wz);
          if (d2 < bestD) { bestD = d2; best = cand; }
        }
        dst[i] = best;
      }
      const t = src; src = dst; dst = t;
    }
    for (let i = 0; i < n; i++) {
      const s = src[i];
      const [wx, wz] = worldOf(i);
      const [sx, sz] = worldOf(s);
      const d = Math.sqrt((sx - wx) * (sx - wx) + (sz - wz) * (sz - wz));
      dist[i] = (depth[i] > 0 ? 1 : -1) * d;
      if (d > 1e-4) { dirX[i] = (sx - wx) / d; dirZ[i] = (sz - wz) / d; }
    }
    /* direction smoothing: 2 passes of 3x3 box blur on UNNORMALIZED dirs, then renormalize */
    const blur3 = (f) => {
      const out = new Float32Array(n);
      for (let z = 0; z < fieldRes; z++) for (let x = 0; x < fieldRes; x++) {
        let acc = 0;
        for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++) {
          const cx = Math.min(Math.max(x + dx, 0), fieldRes - 1);
          const cz = Math.min(Math.max(z + dz, 0), fieldRes - 1);
          acc += f[cz * fieldRes + cx];
        }
        out[z * fieldRes + x] = acc / 9;
      }
      return out;
    };
    let bx = dirX, bz = dirZ;
    for (let p = 0; p < 2; p++) { bx = blur3(bx); bz = blur3(bz); }
    for (let i = 0; i < n; i++) {
      const l = Math.sqrt(bx[i] * bx[i] + bz[i] * bz[i]);
      if (l > 1e-4) { dirX[i] = bx[i] / l; dirZ[i] = bz[i] / l; } else { dirX[i] = 0; dirZ[i] = 0; }
    }
  }
  /* beach slope tan(beta) = |grad depth|, central differences + 2x blur (spec 1.3d) */
  {
    const raw = new Float32Array(n);
    for (let z = 0; z < fieldRes; z++) for (let x = 0; x < fieldRes; x++) {
      const xm = Math.max(x - 1, 0), xp = Math.min(x + 1, fieldRes - 1);
      const zm = Math.max(z - 1, 0), zp = Math.min(z + 1, fieldRes - 1);
      const ddx2 = (depth[z * fieldRes + xp] - depth[z * fieldRes + xm]) / ((xp - xm) * texel);
      const ddz2 = (depth[zp * fieldRes + x] - depth[zm * fieldRes + x]) / ((zp - zm) * texel);
      raw[z * fieldRes + x] = Math.sqrt(ddx2 * ddx2 + ddz2 * ddz2);
    }
    let b = raw;
    const blur3s = (f) => {
      const out = new Float32Array(n);
      for (let z = 0; z < fieldRes; z++) for (let x = 0; x < fieldRes; x++) {
        let acc = 0;
        for (let dz = -1; dz <= 1; dz++) for (let dx = -1; dx <= 1; dx++)
          acc += f[Math.min(Math.max(z + dz, 0), fieldRes - 1) * fieldRes + Math.min(Math.max(x + dx, 0), fieldRes - 1)];
        out[z * fieldRes + x] = acc / 9;
      }
      return out;
    };
    for (let p = 0; p < 2; p++) b = blur3s(b);
    slopeTan.set(b);
  }
  return { fieldRes, depth, dist, dirX, dirZ, slopeTan, half };
}

const toHalf = THREE.DataUtils.toHalfFloat;

function makeHalfTexture(fieldRes, fill) {
  const data = new Uint16Array(fieldRes * fieldRes * 4);
  fill(data);
  const tex = new THREE.DataTexture(data, fieldRes, fieldRes, THREE.RGBAFormat, THREE.HalfFloatType);
  tex.wrapS = tex.wrapT = THREE.ClampToEdgeWrapping;
  tex.minFilter = tex.magFilter = THREE.LinearFilter;
  tex.colorSpace = THREE.NoColorSpace;
  tex.needsUpdate = true;
  return tex;
}

/* =====================================================================================
   SECTION 5 — FFT ocean, CPU side (WaterOceanSpectrum.cs / OceanFft.compute SpectrumInit)
   ===================================================================================== */

function deriveCascades(peakWavelength, cascadeReach) {
  const bands = new Array(4);
  let top = Math.max(peakWavelength, 1e-3) * OCEAN.TOP_BAND_PEAK_MULTIPLE;
  for (let i = 3; i >= 0; i--) { bands[i] = top; top *= OCEAN.CASCADE_BAND_RATIO; }
  const bandMax = bands;
  const bandMin = [0, bandMax[0], bandMax[1], bandMax[2]];
  const domainSizes = bandMax.map((b) => b * OCEAN.CASCADE_TILE_OVERSAMPLE);
  const visibleAreas = bandMax.map((b) => b * OCEAN.VISIBLE_AREA_BAND_MULTIPLE * Math.max(cascadeReach, 0));
  return { bandMax, bandMin, domainSizes, visibleAreas };
}

function jonswapOmni(kMag, omegaP, gamma, seaDepth) {
  if (kMag < 1e-4) return 0;
  const omega = Math.sqrt(OCEAN.GRAVITY * kMag);
  const peakRatio2 = (omegaP / omega) * (omegaP / omega);
  let shape = Math.exp(-OCEAN.JONSWAP_PEAK_DECAY * peakRatio2 * peakRatio2) / Math.pow(omega, 5);
  const sigma = omega <= omegaP ? OCEAN.JONSWAP_SIGMA_LOW : OCEAN.JONSWAP_SIGMA_HIGH;
  const peakOffset = (omega - omegaP) / Math.max(sigma * omegaP, 1e-6);
  shape *= Math.pow(Math.max(gamma, 1), Math.exp(-0.5 * peakOffset * peakOffset));
  if (seaDepth > 0)
    shape *= 0.5 + 0.5 * Math.tanh(OCEAN.TMA_SLOPE * (omega * Math.sqrt(seaDepth / OCEAN.GRAVITY) - OCEAN.TMA_OFFSET));
  const dOmegaDk = 0.5 * Math.sqrt(OCEAN.GRAVITY / kMag);
  return shape * dOmegaDk / kMag;
}

function swellShape(kx, ky, kMag, sea, windDir) {
  if (sea.swellHeight <= 0 || kMag < 1e-4) return 0;
  const kSwell = OCEAN.TWO_PI / Math.max(sea.swellWavelength, 1e-3);
  const width = kSwell * OCEAN.SWELL_WIDTH;
  const radial = Math.exp(-((kMag - kSwell) * (kMag - kSwell)) / (2 * width * width));
  const directional = Math.pow(clamp01((kx * windDir[0] + ky * windDir[1]) / kMag), OCEAN.SWELL_DIR_POWER);
  return radial * directional;
}

function computeGains(sea, cascades) {
  const N = OCEAN.FFT_SIZE;
  const omegaP = Math.sqrt(OCEAN.GRAVITY * OCEAN.TWO_PI / Math.max(sea.peakWavelength * Math.max(0.001, sea.waveScale), 1e-3));
  let windSeaVariance = 0, swellVariance = 0;
  for (let c = 0; c < 4; c++) {
    const domain = Math.max(cascades.domainSizes[c], 1e-3);
    const dk = OCEAN.TWO_PI / domain;
    const measure = dk * dk;
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
      const kx = (x - N / 2) * dk, ky = (y - N / 2) * dk;
      const kMag = Math.sqrt(kx * kx + ky * ky);
      if (kMag < 1e-6) continue;
      const wavelength = OCEAN.TWO_PI / kMag;
      if (wavelength <= cascades.bandMin[c] || wavelength > cascades.bandMax[c]) continue;
      windSeaVariance += measure * jonswapOmni(kMag, omegaP, sea.peakSharpness, sea.seaDepth);
      swellVariance += measure * (swellShape(kx, ky, kMag, sea, [1, 0]) + swellShape(-kx, -ky, kMag, sea, [1, 0]));
    }
  }
  const gainFor = (hs, unitVariance) => {
    if (hs <= 0 || unitVariance < 1e-12) return 0;
    const targetRms = hs / OCEAN.SIG_HEIGHT_TO_RMS;
    return targetRms * targetRms / (unitVariance * OCEAN.REAL_PART_VARIANCE_SHARE);
  };
  return {
    spectrumGain: gainFor(sea.significantWaveHeight, windSeaVariance),
    swellGain: gainFor(sea.swellHeight, swellVariance),
  };
}

/* Wang hash + Box-Muller — bit pattern mirrored (uint32 wraparound via Math.imul) */
function wangHash(s) {
  s = (s ^ 61) ^ (s >>> 16);
  s = (s + (s << 3)) >>> 0;                 // s *= 9
  s = s ^ (s >>> 4);
  s = Math.imul(s, 0x27d4eb2d) >>> 0;
  s = s ^ (s >>> 15);
  return s >>> 0;
}
function makeRandomStream(state) {
  return () => {
    state = wangHash(state);
    return state * (1 / 4294967296);
  };
}

function buildH0(sea, cascades) {
  const N = OCEAN.FFT_SIZE;
  const heading = sea.windFromDegrees * Math.PI / 180;
  const windDir = [Math.cos(heading), Math.sin(heading)];
  const gains = computeGains(sea, cascades);
  const spread = (cosTheta, turbulence) => {
    const pedestal = OCEAN.SPREAD_PEDESTAL * turbulence;
    return cosTheta > 0 ? cosTheta * cosTheta * (1 - turbulence) + pedestal : pedestal;
  };
  const spreadNorm = (turbulence) =>
    2 / Math.max((1 - turbulence) + 4 * OCEAN.SPREAD_PEDESTAL * turbulence, 1e-4);
  const out = [];
  for (let c = 0; c < 4; c++) {
    const data = new Float32Array(N * N * 4);
    const domain = Math.max(cascades.domainSizes[c], 1e-3);
    const dk = OCEAN.TWO_PI / domain;
    const measure = dk * dk;
    let turbulence = clamp01(sea.windTurbulence);
    if (c === 0) turbulence = Math.max(turbulence, OCEAN.TURBULENCE_FLOOR_FINEST);
    else if (c === 1) turbulence = Math.max(turbulence, OCEAN.TURBULENCE_FLOOR_FINE);
    for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
      const kx = (x - N / 2) * dk, ky = (y - N / 2) * dk;
      const kMag = Math.sqrt(kx * kx + ky * ky);
      const windSea = jonswapOmni(kMag, Math.sqrt(OCEAN.GRAVITY * OCEAN.TWO_PI / Math.max(sea.peakWavelength * Math.max(0.001, sea.waveScale), 1e-3)), sea.peakSharpness, sea.seaDepth)
                    * spreadNorm(turbulence) * gains.spectrumGain;
      const cosTheta = kMag > 1e-4 ? (kx * windDir[0] + ky * windDir[1]) / kMag : 0;
      let ampK = Math.sqrt((windSea * spread(cosTheta, turbulence) + swellShape(kx, ky, kMag, sea, windDir) * gains.swellGain) * measure * 0.5);
      let ampMK = Math.sqrt((windSea * spread(-cosTheta, turbulence) + swellShape(-kx, -ky, kMag, sea, windDir) * gains.swellGain) * measure * 0.5);
      const wavelength = kMag > 1e-4 ? OCEAN.TWO_PI / kMag : 1e9;
      if (wavelength <= cascades.bandMin[c] || wavelength > cascades.bandMax[c]) { ampK = 0; ampMK = 0; }
      /* Wang-hash stream, per-texel seed (verbatim) */
      let state = ((Math.imul(x, 1973) + Math.imul(y, 9277) + Math.imul(c, 26699) + OCEAN.SPECTRUM_SEED) | 1) >>> 0;
      const rand = makeRandomStream(state);
      const gaussianPair = () => {
        const u1 = Math.max(1e-6, rand());
        const u2 = rand();
        const r = Math.sqrt(-2 * Math.log(u1));
        const theta = OCEAN.TWO_PI * u2;
        return [r * Math.cos(theta), r * Math.sin(theta)];
      };
      const [g1x, g1y] = gaussianPair();
      const [g2x, g2y] = gaussianPair();
      const i4 = (y * N + x) * 4;
      data[i4 + 0] = g1x * ampK;
      data[i4 + 1] = g1y * ampK;
      data[i4 + 2] = g2x * ampMK;                 // conj(a) = (a.x, -a.y)
      data[i4 + 3] = -g2y * ampMK;
    }
    out.push(data);
  }
  return out;
}

/* Butterfly precompute (DIT, positive-sin inverse twiddles, stage-0 bit reversal) */
function buildButterfly() {
  const size = OCEAN.FFT_SIZE, stages = OCEAN.FFT_STAGES;
  const bitReverse = (x, bits) => {
    let r = 0;
    for (let i = 0; i < bits; i++) { r = (r << 1) | (x & 1); x >>= 1; }
    return r;
  };
  const data = new Float32Array(stages * size * 4);
  for (let stage = 0; stage < stages; stage++) {
    const span = 1 << stage;
    const blockSize = 1 << (stage + 1);
    for (let y = 0; y < size; y++) {
      const k = (y * (size >> (stage + 1))) % size;
      const ang = OCEAN.TWO_PI * k / size;
      const top = (y % blockSize) < span;
      let a, b;
      if (stage === 0) {
        a = top ? bitReverse(y, 7) : bitReverse(y - span, 7);
        b = top ? bitReverse(y + span, 7) : bitReverse(y, 7);
      } else {
        a = top ? y : y - span;
        b = top ? y + span : y;
      }
      const i4 = (y * stages + stage) * 4;        // texel (x=stage, y=element)
      data[i4 + 0] = Math.cos(ang);
      data[i4 + 1] = Math.sin(ang);
      data[i4 + 2] = a;
      data[i4 + 3] = b;
    }
  }
  const tex = new THREE.DataTexture(data, stages, size, THREE.RGBAFormat, THREE.FloatType);
  tex.minFilter = tex.magFilter = THREE.NearestFilter;
  tex.needsUpdate = true;
  return tex;
}

/* =====================================================================================
   SECTION 6 — FFT ocean, GPU side (OceanFft.compute kernels as TSL compute passes)
   ===================================================================================== */

const N_FFT = OCEAN.FFT_SIZE;

function makeStorageTex(n, type, { wrap = false, filter = true, mips = false } = {}) {
  const t = new THREE.StorageTexture(n, n);
  t.type = type;
  t.format = THREE.RGBAFormat;
  t.colorSpace = THREE.NoColorSpace;
  t.wrapS = t.wrapT = wrap ? THREE.RepeatWrapping : THREE.ClampToEdgeWrapping;
  t.magFilter = filter ? THREE.LinearFilter : THREE.NearestFilter;
  t.minFilter = filter ? (mips ? THREE.LinearMipmapLinearFilter : THREE.LinearFilter) : THREE.NearestFilter;
  t.generateMipmaps = mips;
  t.mipmapsAutoUpdate = mips;
  return t;
}

/* complex multiply on vec2 nodes */
const cmul = (a, b) => vec2(
  a.x.mul(b.x).sub(a.y.mul(b.y)),
  a.x.mul(b.y).add(a.y.mul(b.x))
);

function createOceanFFT() {
  const cascades = deriveCascades(seaState.peakWavelength * Math.max(0.001, seaState.waveScale), seaState.cascadeReach);

  /* uniforms shared with materials */
  const U = {
    domain: [0, 1, 2, 3].map((c) => uniform(cascades.domainSizes[c])),
    visible: [0, 1, 2, 3].map((c) => uniform(cascades.visibleAreas[c])),
    waveTime: uniform(0),
    choppiness: uniform(seaState.choppiness),
    windDir: uniform(new THREE.Vector2(1, 0)),
    windSpeed: uniform(seaState.windSpeed),
    foamMinWind: uniform(seaState.foamWindThreshold),
    foamCoverage: uniform(seaState.foamCoverage),
    foamStrength: uniform(seaState.foamStrength),
    foamFadeRate: uniform(seaState.foamFadeRate),
    foamSlowFadeFraction: uniform(1 - seaState.foamDeposit),
    foamDriftFraction: uniform(seaState.foamDrift),
    foamMax: uniform(seaState.foamMax),
    foamAnisotropy: uniform(seaState.foamAnisotropy),
    foamCrestGate: uniform(seaState.foamCrestGate),
    foamFaceBias: uniform(seaState.foamFaceBias),
    foamCascadeMix: uniform(seaState.foamCascadeMix),
    foamDt: uniform(0),
    foamHistoryValid: uniform(0),
    amplitude: uniform(seaState.amplitude),
  };

  const butterflyTex = buildButterfly();

  const h0Tex = [], specPingA = [], specPingB = [], specPongA = [], specPongB = [];
  const dispTex = [], normTex = [], foamTexA = [], foamTexB = [];
  for (let c = 0; c < 4; c++) {
    const h0 = new THREE.DataTexture(new Float32Array(N_FFT * N_FFT * 4), N_FFT, N_FFT, THREE.RGBAFormat, THREE.FloatType);
    h0.minFilter = h0.magFilter = THREE.NearestFilter;
    h0Tex.push(h0);
    specPingA.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
    specPingB.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
    specPongA.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
    specPongB.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
    dispTex.push(makeStorageTex(N_FFT, THREE.HalfFloatType, { wrap: true, filter: true }));
    normTex.push(makeStorageTex(N_FFT, THREE.HalfFloatType, { wrap: true, filter: true, mips: true }));
    foamTexA.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
    foamTexB.push(makeStorageTex(N_FFT, THREE.FloatType, { filter: false }));
  }

  const texelXY = () => {
    const x = int(instanceIndex.mod(uint(N_FFT))).toVar();
    const y = int(instanceIndex.div(uint(N_FFT))).toVar();
    return [x, y];
  };

  /* --- SpectrumUpdate: H0 -> time-evolved packed spectra (X in A.xy, Y in A.zw, Z in B.xy) */
  const spectrumKernels = [];
  for (let c = 0; c < 4; c++) {
    spectrumKernels.push(Fn(() => {
      const [x, y] = texelXY();
      const h0 = textureLoad(h0Tex[c], ivec2(x, y)).toVar();
      const centred = vec2(float(x).sub(N_FFT * 0.5), float(y).sub(N_FFT * 0.5));
      const k = centred.mul(OCEAN.TWO_PI).div(max(U.domain[c], 1e-3)).toVar();
      const kMag = length(k).toVar();
      const omega = sqrt(kMag.mul(OCEAN.GRAVITY)).toVar();
      const s = sin(omega.mul(U.waveTime)).toVar();
      const co = cos(omega.mul(U.waveTime)).toVar();
      const h = cmul(h0.xy, vec2(co, s)).add(cmul(h0.zw, vec2(co, s.negate()))).toVar();
      const nk = select(kMag.greaterThan(1e-4), k.div(kMag), vec2(0, 0)).toVar();
      const iH = vec2(h.y.negate(), h.x).mul(U.choppiness).toVar();
      textureStore(specPingA[c], ivec2(x, y), vec4(iH.mul(nk.x), h));
      textureStore(specPingB[c], ivec2(x, y), vec4(iH.mul(nk.y), 0, 0));
    })().compute(N_FFT * N_FFT, [64]));
  }

  /* --- FFT butterfly stages (out = in[a] + w * in[b]; per spec, positive-sin twiddles) */
  const fftKernels = [];       // ordered dispatch list per cascade
  for (let c = 0; c < 4; c++) {
    const list = [];
    /* horizontal: element = x; 7 stages ping->pong->ping...  */
    for (let s = 0; s < OCEAN.FFT_STAGES; s++) {
      const srcA = (s % 2 === 0) ? specPingA[c] : specPongA[c];
      const srcB = (s % 2 === 0) ? specPingB[c] : specPongB[c];
      const dstA = (s % 2 === 0) ? specPongA[c] : specPingA[c];
      const dstB = (s % 2 === 0) ? specPongB[c] : specPingB[c];
      list.push(Fn(() => {
        const [x, y] = texelXY();
        const bf = textureLoad(butterflyTex, ivec2(int(s), x)).toVar();
        const a = int(bf.z.add(0.5)).toVar();
        const b = int(bf.w.add(0.5)).toVar();
        const w = bf.xy.toVar();
        const inAa = textureLoad(srcA, ivec2(a, y)).toVar();
        const inAb = textureLoad(srcA, ivec2(b, y)).toVar();
        const inBa = textureLoad(srcB, ivec2(a, y)).toVar();
        const inBb = textureLoad(srcB, ivec2(b, y)).toVar();
        textureStore(dstA, ivec2(x, y), vec4(inAa.xy.add(cmul(w, inAb.xy)), inAa.zw.add(cmul(w, inAb.zw))));
        textureStore(dstB, ivec2(x, y), vec4(inBa.xy.add(cmul(w, inBb.xy)), 0, 0));
      })().compute(N_FFT * N_FFT, [64]));
    }
    /* vertical: element = y; starts from PONG (7 horizontal stages ended there) */
    for (let s = 0; s < OCEAN.FFT_STAGES; s++) {
      const srcA = (s % 2 === 0) ? specPongA[c] : specPingA[c];
      const srcB = (s % 2 === 0) ? specPongB[c] : specPingB[c];
      const dstA = (s % 2 === 0) ? specPingA[c] : specPongA[c];
      const dstB = (s % 2 === 0) ? specPingB[c] : specPongB[c];
      list.push(Fn(() => {
        const [x, y] = texelXY();
        const bf = textureLoad(butterflyTex, ivec2(int(s), y)).toVar();
        const a = int(bf.z.add(0.5)).toVar();
        const b = int(bf.w.add(0.5)).toVar();
        const w = bf.xy.toVar();
        const inAa = textureLoad(srcA, ivec2(x, a)).toVar();
        const inAb = textureLoad(srcA, ivec2(x, b)).toVar();
        const inBa = textureLoad(srcB, ivec2(x, a)).toVar();
        const inBb = textureLoad(srcB, ivec2(x, b)).toVar();
        textureStore(dstA, ivec2(x, y), vec4(inAa.xy.add(cmul(w, inAb.xy)), inAa.zw.add(cmul(w, inAb.zw))));
        textureStore(dstB, ivec2(x, y), vec4(inBa.xy.add(cmul(w, inBb.xy)), 0, 0));
      })().compute(N_FFT * N_FFT, [64]));
    }
    fftKernels.push(list);
  }

  /* --- Finalize: (-1)^(x+y) shift, keep REAL parts (no 1/N^2 — CPU gain carries scale) */
  const finalizeKernels = [];
  for (let c = 0; c < 4; c++) {
    finalizeKernels.push(Fn(() => {
      const [x, y] = texelXY();
      const A = textureLoad(specPingA[c], ivec2(x, y)).toVar();
      const B = textureLoad(specPingB[c], ivec2(x, y)).toVar();
      const sgn = select(int(x.add(y)).bitAnd(int(1)).equal(int(1)), float(-1), float(1));
      textureStore(dispTex[c], ivec2(x, y), vec4(A.x.mul(sgn), A.z.mul(sgn), B.x.mul(sgn), 0));
    })().compute(N_FFT * N_FFT, [64]));
  }

  /* --- ComputeNormal: normal + Jacobian fold + temporal whitecap foam (spec section 5) */
  const normalKernels = [[], []];        // [frameParity][cascade]
  for (let parity = 0; parity < 2; parity++) {
    for (let c = 0; c < 4; c++) {
      const foamPrev = parity === 0 ? foamTexA[c] : foamTexB[c];
      const foamNext = parity === 0 ? foamTexB[c] : foamTexA[c];
      normalKernels[parity].push(Fn(() => {
        const [x, y] = texelXY();
        const stepM = max(U.domain[c], 1e-3).div(N_FFT).toVar();       // metres per texel
        const wrapC = (v) => v.bitAnd(int(N_FFT - 1));
        const dr = textureLoad(dispTex[c], ivec2(wrapC(x.add(1)), y)).xyz.toVar();
        const dl = textureLoad(dispTex[c], ivec2(wrapC(x.add(N_FFT - 1)), y)).xyz.toVar();
        const du = textureLoad(dispTex[c], ivec2(x, wrapC(y.add(1)))).xyz.toVar();
        const dd = textureLoad(dispTex[c], ivec2(x, wrapC(y.add(N_FFT - 1)))).xyz.toVar();
        const centreHeight = textureLoad(dispTex[c], ivec2(x, y)).y.toVar();

        const tangentX = vec3(dr.x.sub(dl.x).add(stepM.mul(2)), dr.y.sub(dl.y), dr.z.sub(dl.z));
        const tangentZ = vec3(du.x.sub(dd.x), du.y.sub(dd.y), du.z.sub(dd.z).add(stepM.mul(2)));
        const nrm = normalize(cross(tangentZ, tangentX)).toVar();

        const invTwoStep = float(1).div(stepM.mul(2)).toVar();
        const jxx = dr.x.sub(dl.x).mul(invTwoStep).add(1).toVar();
        const jzz = du.z.sub(dd.z).mul(invTwoStep).add(1).toVar();
        const jxz = dr.z.sub(dl.z).mul(invTwoStep).toVar();
        const jzx = du.x.sub(dd.x).mul(invTwoStep).toVar();
        const jacobian = jxx.mul(jzz).sub(jxz.mul(jzx)).toVar();
        const foldTrace = jxx.add(jzz).toVar();
        const foldDisc = max(foldTrace.mul(foldTrace).sub(jacobian.mul(4)), 0).toVar();
        const foldMeasure = mix(jacobian, foldTrace.sub(sqrt(foldDisc)).mul(0.5), saturate(U.foamAnisotropy)).toVar();
        const fresh = saturate(U.foamCoverage.sub(foldMeasure)).toVar();

        /* per-cascade fold weight (named backwards in source, kept bit-identical) */
        const dampConst = (c === 0) ? 0.0 : (c === 1) ? OCEAN.FOAM_CASCADE1_DAMP : (c === 3 ? 0.5 : 1.0);
        fresh.mulAssign(mix(float(dampConst), float(1), saturate(U.foamCascadeMix)));
        fresh.mulAssign(select(U.windSpeed.greaterThan(U.foamMinWind), float(1), float(0)));

        const neighbourMin = min(min(dr.y, dl.y), min(du.y, dd.y));
        const neighbourMax = max(max(dr.y, dl.y), max(du.y, dd.y));
        const crestRise = saturate(centreHeight.sub(neighbourMin).div(max(neighbourMax.sub(neighbourMin), 1e-5))).toVar();
        fresh.mulAssign(mix(float(1), smoothstep(float(OCEAN.FOAM_CREST_GATE_LOW), float(1), crestRise), saturate(U.foamCrestGate)));

        const tiltLength = length(nrm.xz).toVar();
        const faceAlign = select(tiltLength.greaterThan(1e-5), dot(nrm.xz.div(tiltLength), U.windDir), float(0));
        fresh.mulAssign(mix(float(1), saturate(faceAlign.mul(0.5).add(0.5)), saturate(U.foamFaceBias)));

        /* downwind advection with manual bilinear (wrapped) */
        const driftSpeed = U.windSpeed.mul(U.foamDriftFraction);
        const driftTexels = U.windDir.mul(driftSpeed.mul(U.foamDt).div(stepM)).toVar();
        const pos = vec2(float(x), float(y)).sub(driftTexels).toVar();
        const p0 = floor(pos).toVar();
        const f = pos.sub(p0).toVar();
        const i0x = wrapC(int(p0.x).add(N_FFT * 8)).toVar();      // +N*8 keeps it positive pre-mask
        const i0y = wrapC(int(p0.y).add(N_FFT * 8)).toVar();
        const i1x = wrapC(i0x.add(1)).toVar();
        const i1y = wrapC(i0y.add(1)).toVar();
        const f00 = textureLoad(foamPrev, ivec2(i0x, i0y)).x;
        const f10 = textureLoad(foamPrev, ivec2(i1x, i0y)).x;
        const f01 = textureLoad(foamPrev, ivec2(i0x, i1y)).x;
        const f11 = textureLoad(foamPrev, ivec2(i1x, i1y)).x;
        const advected = mix(mix(f00, f10, f.x), mix(f01, f11, f.x), f.y).mul(U.foamHistoryValid).toVar();

        /* split decay + prewarm (FoamDecayBlend; dense foam decays slower) */
        const foamDtEff = select(U.foamHistoryValid.lessThan(0.5), float(1).div(max(U.foamFadeRate, 1e-3)), U.foamDt).toVar();
        const slowFade = U.foamFadeRate.mul(U.foamSlowFadeFraction);
        const fadeRate = mix(U.foamFadeRate, slowFade, saturate(advected.div(OCEAN.FOAM_DENSE_REFERENCE))).toVar();
        const foam = advected.mul(saturate(float(1).sub(fadeRate.mul(U.foamDt))))
          .add(fresh.mul(U.foamStrength).mul(foamDtEff).mul(OCEAN.FOAM_ACCUM_RATE)).toVar();
        const foamOut = min(foam, U.foamMax).toVar();

        textureStore(foamNext, ivec2(x, y), vec4(foamOut, 0, 0, 0));
        textureStore(normTex[c], ivec2(x, y), vec4(nrm.x, saturate(float(1).sub(foldMeasure)), nrm.z, foamOut));
      })().compute(N_FFT * N_FFT, [64]));
    }
  }

  let frameParity = 0;
  let lastDispatchTime = -1;
  let spectrumDirty = true;

  const api = {
    U, cascades, dispTex, normTex,
    markDirty() { spectrumDirty = true; },
    rebuild() {
      const derived = deriveCascades(seaState.peakWavelength * Math.max(0.001, seaState.waveScale), seaState.cascadeReach);
      api.cascades = derived;
      for (let c = 0; c < 4; c++) {
        U.domain[c].value = derived.domainSizes[c];
        U.visible[c].value = derived.visibleAreas[c];
      }
      const heading = seaState.windFromDegrees * Math.PI / 180;
      U.windDir.value.set(Math.cos(heading), Math.sin(heading));
      const h0 = buildH0(seaState, derived);
      for (let c = 0; c < 4; c++) {
        h0Tex[c].image.data.set(h0[c]);
        h0Tex[c].needsUpdate = true;
      }
      U.choppiness.value = seaState.choppiness;
      U.windSpeed.value = seaState.windSpeed;
      U.foamMinWind.value = seaState.foamWindThreshold;
      U.foamCoverage.value = seaState.foamCoverage;
      U.foamStrength.value = seaState.foamStrength;
      U.foamFadeRate.value = seaState.foamFadeRate;
      U.foamSlowFadeFraction.value = 1 - seaState.foamDeposit;
      U.foamDriftFraction.value = seaState.foamDrift;
      U.foamMax.value = seaState.foamMax;
      U.foamAnisotropy.value = seaState.foamAnisotropy;
      U.foamCrestGate.value = seaState.foamCrestGate;
      U.foamFaceBias.value = seaState.foamFaceBias;
      U.foamCascadeMix.value = seaState.foamCascadeMix;
      U.amplitude.value = seaState.amplitude;
      spectrumDirty = false;
    },
    dispatch(renderer, waveTime) {
      if (spectrumDirty) api.rebuild();
      U.waveTime.value = waveTime;
      U.foamDt.value = lastDispatchTime < 0 ? 0 : Math.min(Math.max(waveTime - lastDispatchTime, 0), OCEAN.MAX_FOAM_DT);
      lastDispatchTime = waveTime;
      for (let c = 0; c < 4; c++) {
        renderer.compute(spectrumKernels[c]);
        for (const k of fftKernels[c]) renderer.compute(k);
        renderer.compute(finalizeKernels[c]);
        renderer.compute(normalKernels[frameParity][c]);
      }
      U.foamHistoryValid.value = 1;
      frameParity = 1 - frameParity;
    },
  };
  return api;
}

/* =====================================================================================
   SECTION 7 — Live state (GUI-mutable), world constants
   ===================================================================================== */

const WORLD = {
  terrainWidth: ISLAND_PRESET.widthMeters,
  terrainHeight: ISLAND_PRESET.heightMeters,
  waterLevel: ISLAND_PRESET.seaLevel * ISLAND_PRESET.heightMeters,   // 13.8 m
  shoreFieldRes: 512,
  terrainMeshRes: 384,
  causticRes: 512,
  causticGridRes: 192,
  causticWindowHalf: 48.0,
};

const seaState = { ...SEA_DEFAULTS };
const surfState = { ...SURF_DEFAULTS };
const lookState = { ...LOOK };
const uwState = { ...UW, jerlovType: 'Open ocean I' };
const sunState = { elevationDeg: 8.0, azimuthDeg: 262.0, intensity: 2.6 };

const offshoreSignificantHeight = () =>
  Math.sqrt(seaState.significantWaveHeight ** 2 + seaState.swellHeight ** 2);

/* =====================================================================================
   SECTION 8 — Shared TSL library (one implementation, used by surface, post, caustics,
   terrain — mirrors the "ONE source of truth" contract in the Unity sources)
   ===================================================================================== */

function createShared(fft, shoreTextures) {
  const G = {};
  G.fft = fft;

  /* ---- global uniforms ---- */
  const jer = JERLOV[uwState.jerlovType];
  G.uSunDir = uniform(new THREE.Vector3(0, 1, 0));
  G.uSunColor = uniform(new THREE.Color(1, 1, 1));
  G.uAmbient = uniform(new THREE.Color(0.1, 0.15, 0.2));
  G.uCamPos = uniform(new THREE.Vector3());
  G.uWaterLevel = uniform(WORLD.waterLevel);
  G.uExtinction = uniform(new THREE.Vector3(...jer.ext));
  G.uScatterColor = uniform(new THREE.Color(...jer.body));
  G.uFogDensity = uniform(lookState.fogDensity);
  G.uOpacity = uniform(lookState.waterOpacity);
  G.uScatterIntensity = uniform(lookState.scatterIntensity);
  G.uScatterAmbientTerm = uniform(lookState.scatterAmbientTerm);
  G.uScatterSunTerm = uniform(lookState.scatterSunTerm);
  G.uScatterAnisotropy = uniform(lookState.scatterAnisotropy);
  G.uDepthDarkenStrength = uniform(uwState.depthDarkenStrength);
  G.uOffshoreHs = uniform(offshoreSignificantHeight());
  G.uSurfaceBand = uniform(8.0);

  /* shore field */
  G.shoreDepthTex = shoreTextures.depthTex;
  G.shoreSdfTex = shoreTextures.sdfTex;
  G.uShoreHalf = uniform(shoreTextures.half);
  G.uShoreValid = uniform(1);

  /* surf */
  G.uSurfActive = uniform(surfState.enabled ? 1 : 0);
  G.uSurfAmplitude = uniform(surfState.amplitude);
  G.uSurfWavelength = uniform(surfState.wavelength);
  G.uSurfPeriod = uniform(surfState.period);
  G.uSurfBandDepth = uniform(surfState.bandDepth);
  G.uSurfSetStrength = uniform(surfState.setStrength);
  G.uSurfLean = uniform(surfState.lean);
  G.uSurfCompression = uniform(surfState.compression);
  G.uSurfGreens = uniform(surfState.greens);
  G.uSurfAmbientFade = uniform(surfState.ambientFade);
  G.uSurfSwashAmplitude = uniform(surfState.swashAmplitude);
  G.uSurfSwashMaxSlopeTan = uniform(surfState.swashMaxSlopeTan);
  G.uSurfWaterlineFoam = uniform(surfState.waterlineFoam);
  G.uSurfSmallWaveFoam = uniform(surfState.smallWaveFoam);
  G.uSurfCrestLength = uniform(surfState.crestLength);
  G.uSurfCrestVariation = uniform(surfState.crestVariation);
  G.uSurfCrestPersistence = uniform(surfState.crestPersistence);
  G.uSurfDirectionality = uniform(surfState.directionality);
  G.uSurfBeatTime = uniform(0);
  G.uWetDryTime = uniform(surfState.wetDryTime);
  G.uShoalDepth = uniform(Math.max(surfState.shoalDepth, 2 * offshoreSignificantHeight()));
  G.uGreenBandDepth = uniform(surfState.shoalDepth);
  G.uSurfFoamStrength = uniform(surfState.foamStrength);
  G.uSurfFoamFeather = uniform(surfState.foamFeather);
  G.uSurfFoamTileSize = uniform(surfState.foamTileSize);
  G.uSurfFoamColor = uniform(new THREE.Color(...surfState.foamColor));
  G.uSurfFoamOpacity = uniform(surfState.foamOpacity);
  G.uSurfFoamBoreGain = uniform(surfState.foamBoreGain);
  G.uSurfFoamTrailGain = uniform(surfState.foamTrailGain);
  G.uSurfFoamTrailLength = uniform(surfState.foamTrailLength);
  G.uSurfFoamTrailDissolve = uniform(surfState.foamTrailDissolve);
  G.uSurfFoamCrestCap = uniform(surfState.foamCrestCap);
  G.uSurfSwashFoam = uniform(surfState.swashFoam);
  G.uSurfSwashFoamWidth = uniform(surfState.swashFoamWidth);
  G.uSurfSwashFoamDissolve = uniform(surfState.swashFoamDissolve);

  /* look */
  G.uReflectionStrength = uniform(lookState.reflectionStrength);
  G.uEnvReflectionIntensity = uniform(lookState.envReflectionIntensity);
  G.uFresnelFloor = uniform(lookState.fresnelFloor);
  G.uFresnelPower = uniform(lookState.fresnelPower);
  G.uSunRoughness = uniform(lookState.sunRoughness);
  G.uRoughnessFar = uniform(lookState.roughnessFar);
  G.uRoughnessFarDistance = uniform(lookState.roughnessFarDistance);
  G.uRoughnessFalloff = uniform(lookState.roughnessFalloff);
  G.uAnisoStretch = uniform(lookState.reflectionAnisoStretch);
  G.uWaveNormalStrength = uniform(lookState.waveNormalStrength);
  G.uHorizonHazeDensity = uniform(lookState.horizonHazeDensity);
  G.uHorizonHazeColor = uniform(new THREE.Color(...lookState.horizonHazeColor));
  G.uHorizonHazeAlpha = uniform(lookState.horizonHazeAlpha);
  G.uDetailStrength = uniform(lookState.detailNormalStrength);
  G.uDetailScale = uniform(lookState.detailNormalScale);
  G.uDetailFarScale = uniform(lookState.detailNormalFarScale);
  G.uDetailFarDistance = uniform(lookState.detailNormalFarDistance);
  G.uDetailSpeed = uniform(lookState.detailNormalSpeed);
  G.uDetailFarSpeed = uniform(lookState.detailNormalFarSpeed);
  G.uDetailCrestBoost = uniform(lookState.detailNormalCrestBoost);
  G.uDetailDistanceBoost = uniform(lookState.detailNormalDistanceBoost);
  G.uFoamColor = uniform(new THREE.Color(...lookState.oceanFoamColor));
  G.uFoamOpacity = uniform(lookState.oceanFoamOpacity);
  G.uFoamTileSize = uniform(lookState.oceanFoamTileSize);
  G.uFoamFeather = uniform(lookState.oceanFoamFeather);
  G.uFoamStreakStretch = uniform(lookState.oceanFoamStreakStretch);
  G.uFoamTexInfluence = uniform(lookState.oceanFoamTextureInfluence);
  G.uFoamDepthTint = uniform(lookState.oceanFoamDepthTint);
  G.uFoamNormalStrength = uniform(lookState.foamNormalStrength);
  G.uSssIntensity = uniform(lookState.sssIntensity);
  G.uSssSunFalloff = uniform(lookState.sssSunFalloff);
  G.uSssPinchMin = uniform(lookState.sssPinchMin);
  G.uSssPinchMax = uniform(lookState.sssPinchMax);
  G.uSssPinchFalloff = uniform(lookState.sssPinchFalloff);

  /* underwater / god rays / caustics */
  G.uGodRayColor = uniform(new THREE.Color(...uwState.godRayColor));
  G.uGodRayDensity = uniform(uwState.godRayDensity);
  G.uGodRayAnisotropy = uniform(uwState.godRayAnisotropy);
  G.uGodRayCausticStrength = uniform(uwState.godRayCausticStrength);
  G.uGodRayCausticDepthSoften = uniform(uwState.godRayCausticDepthSoften);
  G.uGodRayDepthFade = uniform(uwState.godRayDepthFade);
  G.uCausticDepthFade = uniform(uwState.causticDepthFade);
  G.uCausticStrength = uniform(uwState.causticStrength);
  G.uCausticTime = uniform(0);
  G.uCausticRippleScale = uniform(uwState.causticRippleScale);
  G.uCausticRippleStrength = uniform(uwState.causticRippleStrength);
  G.uCausticSmooth = uniform(uwState.godRayCausticSmooth);
  G.uSimCenter = uniform(new THREE.Vector3(0, WORLD.waterLevel, 0));
  G.uSimExtent = uniform(new THREE.Vector2(WORLD.causticWindowHalf, WORLD.causticWindowHalf));
  G.uWaterlineWidthPx = uniform(uwState.waterlineWidthPx);
  G.uWaterlineStrength = uniform(uwState.waterlineStrength);
  G.uWaterlineWarp = uniform(uwState.waterlineWarp);

  const fftU = fft.U;

  /* ---- shore sampling (WaterShore.hlsl / WaterShoreMath.hlsl) ---- */
  G.shoreSample = (worldXZ) => {
    const uvS = worldXZ.div(G.uShoreHalf.mul(2)).add(0.5).toVar();
    const edge = min(uvS, vec2(1, 1).sub(uvS)).toVar();
    const ramp = saturate(edge.div(SHORE.BORDER_FEATHER));
    const influence = ramp.x.mul(ramp.y).mul(G.uShoreValid).toVar();
    const uvC = clamp(uvS, vec2(0), vec2(1)).toVar();
    const depthTap = texture(G.shoreDepthTex, uvC).level(0).x;
    const depth = select(influence.greaterThan(0), depthTap, float(SHORE.DEEP_SENTINEL)).toVar();
    const sdf = texture(G.shoreSdfTex, uvC).level(0).toVar();
    const dir = sdf.xy.mul(2).sub(1).toVar();
    const dirLen = length(dir).toVar();
    const toShore = select(dirLen.greaterThan(1e-4), dir.div(dirLen), vec2(0, 0)).toVar();
    return { depth, sdfDist: sdf.z.toVar(), toShore, slopeTan: sdf.w.toVar(), influence };
  };

  G.shoalWeight = (depth, wavelength) => {
    const clamped = max(depth, 0);
    const raw = saturate(clamped.mul(SHORE.SHOAL_WAVELENGTH_FACTOR).div(max(wavelength, 1e-3)));
    const band = max(G.uShoalDepth, 1e-3);
    const deep = smoothstep(band.mul(SHORE.BAND_INNER_FRACTION), band, clamped);
    return mix(raw, float(1), deep);
  };

  /* ---- surf fronts (WaterSurfWaves.hlsl, exact) ---- */
  const surfHash = (n) => fract(sin(n.mul(SURF.HASH_SINE_FREQ)).mul(SURF.HASH_SINE_SCALE));
  const surfWrapIndex = (i) => i.sub(floor(i.div(SURF.BEAT_WRAP_FRONTS)).mul(SURF.BEAT_WRAP_FRONTS));
  const surfSetAmp = (frontIndex) => {
    const w = surfWrapIndex(frontIndex).toVar();
    const h = surfHash(w).toVar();
    const setWave = sin(w.div(SURF.SET_WAVES).mul(OCEAN.TWO_PI).add(h.mul(SURF.SETAMP_HASH_PHASE))).mul(0.5).add(0.5);
    return mix(float(1), mix(float(SURF.SETAMP_FLOOR), float(1), setWave), G.uSurfSetStrength)
      .mul(mix(float(SURF.SETAMP_JITTER_MIN), float(SURF.SETAMP_JITTER_MAX), h));
  };
  const surfWarpDistance = (s) => {
    const reach = max(G.uSurfWavelength, SURF.MIN_WAVELENGTH).mul(SURF.WARP_REACH_SPACINGS);
    return s.mul(G.uSurfCompression.mul(exp(max(s, 0).negate().div(reach))).add(1));
  };
  const surfCrestFactor = (worldXZ, frontIndex) => {
    const invLen = float(1).div(max(G.uSurfCrestLength, SURF.CREST_MIN_LENGTH));
    const w = surfWrapIndex(frontIndex).toVar();
    const p = saturate(G.uSurfCrestPersistence);
    const seedFresh = surfHash(w).mul(SURF.CREST_SEED_FRESH_SCALE).toVar();
    const seedA = mix(seedFresh, w.mul(SURF.CREST_SEED_DRIFT_A), p);
    const seedB = mix(seedFresh.mul(SURF.CREST_FRESH_OCTAVE_RATIO), w.mul(SURF.CREST_SEED_DRIFT_B), p);
    const n = sin(dot(worldXZ, vec2(1, SURF.CREST_DIR_A_Z)).mul(OCEAN.TWO_PI).mul(invLen).add(seedA))
      .add(sin(dot(worldXZ, vec2(SURF.CREST_DIR_B_X, 1)).mul(OCEAN.TWO_PI).mul(invLen).mul(SURF.CREST_FREQ_RATIO).add(seedB)).mul(SURF.CREST_OCTAVE_B_WEIGHT));
    const n01 = saturate(n.div(SURF.CREST_NOISE_NORM).mul(0.5).add(0.5));
    return float(1).sub(G.uSurfCrestVariation.mul(float(1).sub(n01)));
  };
  const surfWindDir = () => fftU.windDir;      /* _SurfWindDirXZ = (cos, sin) of wind heading */
  const surfExposure = (toShore) => mix(float(1),
    smoothstep(float(SURF.EXPOSURE_FACING_LO), float(SURF.EXPOSURE_FACING_HI), dot(surfWindDir(), toShore)),
    saturate(G.uSurfDirectionality));
  const surfIribarren = (tanBeta, deepHeight) => {
    const T = max(G.uSurfPeriod, SURF.MIN_PERIOD);
    const L0 = T.mul(T).mul(SURF.DEEPWATER_LENGTH_COEF);
    return tanBeta.div(sqrt(max(deepHeight, 1e-3).div(max(L0, 1e-3))));
  };
  const surfBreakerWeights = (xi) => {
    const pastSpill = smoothstep(float(SURF.XI_SPILL_END_LO), float(SURF.XI_SPILL_END_HI), xi).toVar();
    const surge = smoothstep(float(SURF.XI_SURGE_START_LO), float(SURF.XI_SURGE_START_HI), xi).toVar();
    return { spill: float(1).sub(pastSpill), plunge: pastSpill.mul(float(1).sub(surge)), surge };
  };
  const surfGamma = (tanBeta) => clamp(tanBeta.mul(SURF.GAMMA_SLOPE_GAIN).add(SURF.GAMMA_BASE), float(SURF.GAMMA_BASE), float(SURF.GAMMA_MAX));

  const surfFrontTerms = (worldXZ, sWarp, depth, tanBeta, timeN) => {
    const L = max(G.uSurfWavelength, SURF.MIN_WAVELENGTH).toVar();
    const T = max(G.uSurfPeriod, SURF.MIN_PERIOD).toVar();
    const phase = sWarp.div(L).add(timeN.div(T)).toVar();
    const frontIndex = floor(phase).toVar();
    const f = phase.sub(frontIndex).toVar();

    const ampThis = surfSetAmp(frontIndex).mul(surfCrestFactor(worldXZ, frontIndex));
    const halfCell = f.sub(0.5).toVar();
    const neighborIdx = frontIndex.add(select(halfCell.greaterThan(0), float(1), float(-1))).toVar();
    const ampNeighbor = surfSetAmp(neighborIdx).mul(surfCrestFactor(worldXZ, neighborIdx));
    const edgeBlend = smoothstep(float(SURF.EDGE_BLEND_START), float(0.5), abs(halfCell)).mul(0.5);
    const setAmp = mix(ampThis, ampNeighbor, edgeBlend).toVar();

    const d = max(depth, SURF.MIN_DEPTH).toVar();
    const deepHeight = G.uSurfAmplitude.mul(setAmp).toVar();
    const bw = surfBreakerWeights(surfIribarren(tanBeta, deepHeight));

    const green = min(pow(max(G.uSurfBandDepth, d).div(d), SURF.GREEN_EXPONENT), max(G.uSurfGreens, SURF.MIN_GREENS));
    const H = G.uSurfAmplitude.mul(setAmp).mul(green).toVar();
    const capH = surfGamma(tanBeta).mul(d).toVar();
    const overCap = H.div(max(capH, 1e-3)).toVar();
    const cresting = smoothstep(float(SURF.CRESTING_START), float(SURF.CRESTING_END), overCap).toVar();
    const broken = smoothstep(float(SURF.BROKEN_START), float(SURF.BROKEN_END), overCap).mul(float(1).sub(bw.surge)).toVar();
    const height = min(H, capH).toVar();

    const dAcross0 = f.sub(0.5).mul(L).toVar();
    const lean = G.uSurfLean.mul(height).mul(cresting);
    const dAcross = dAcross0.add(lean.mul(exp(abs(dAcross0).negate().div(L.mul(SURF.LEAN_REACH_FRACTION))))).toVar();
    const faceLen = L.mul(SURF.FACE_FRACTION).toVar();
    const backLen = L.mul(SURF.BACK_FRACTION).toVar();
    const faceSharpen = mix(float(1), float(SURF.PLUNGE_FACE_SHARPEN), bw.plunge.mul(cresting));
    const profLen = select(dAcross.lessThan(0), faceLen.mul(faceSharpen), backLen).toVar();
    const sech = float(1).div(cosh(min(abs(dAcross).div(profLen), SURF.SECH_ARG_MAX))).toVar();
    const profile = sech.mul(sech).toVar();

    const boreSech = float(1).div(cosh(min(abs(dAcross).div(backLen.mul(SURF.BORE_WIDTH_FACTOR)), SURF.SECH_ARG_MAX))).toVar();
    const boreAmp = mix(height, d.mul(SURF.BORE_STABLE_GAMMA), broken).toVar();

    return { L, T, f, frontIndex, setAmp, bw, height, capH, overCap, cresting, broken,
             dAcross, faceLen, backLen, profile, boreSech, boreAmp };
  };

  const surfFrontHeightFromTerms = (t) => mix(t.height.mul(t.profile), t.boreAmp.mul(t.boreSech), t.broken);

  const surfFrontFoamFromTerms = (t) => {
    const boreWeight = G.uSurfFoamBoreGain;
    const trailWeight = G.uSurfFoamTrailGain;
    const trailLenMul = max(G.uSurfFoamTrailLength, 0.05);
    const trailLen = t.backLen.mul(mix(float(1), float(SURF.PLUNGE_TRAIL_NARROW), t.bw.plunge)).mul(trailLenMul).toVar();
    const trail = select(t.dAcross.greaterThan(0), exp(t.dAcross.negate().div(trailLen)), float(0)).toVar();
    const whitewash0 = t.broken.mul(max(t.boreSech.mul(boreWeight), trail.mul(SURF.TRAIL_BASE_WEIGHT).mul(trailWeight)))
      .mul(saturate(t.setAmp)).mul(t.bw.plunge.mul(SURF.PLUNGE_WHITEWASH_GAIN).add(1)).toVar();
    const landing = t.bw.plunge.mul(t.cresting).mul(float(1).sub(t.broken)).mul(saturate(t.setAmp))
      .mul(exp(abs(t.dAcross.add(t.faceLen.mul(SURF.PLUNGE_LANDING_AHEAD))).negate().div(max(t.faceLen.mul(SURF.PLUNGE_LANDING_WIDTH), 1e-3))));
    const whitewash1 = saturate(whitewash0.add(landing.mul(SURF.PLUNGE_LANDING_FOAM))).toVar();
    const smallCrest = t.profile;
    const smallTail = select(t.dAcross.greaterThan(0), exp(t.dAcross.negate().div(max(t.backLen, 1e-3))), float(0));
    const whitewash = saturate(whitewash1.add(saturate(t.setAmp).mul(max(smallCrest, smallTail)).mul(float(1).sub(t.broken)).mul(G.uSurfSmallWaveFoam))).toVar();
    const lipProfile = pow(t.profile, mix(float(1), float(SURF.PLUNGE_FACE_SHARPEN), t.bw.plunge));
    const lipShape = lipProfile.mul(t.bw.plunge.mul(SURF.PLUNGE_BREAKER_WIDEN).add(1)).mul(float(1).sub(t.bw.surge)).toVar();
    const breaker = t.cresting.mul(float(1).sub(t.broken)).mul(lipShape).toVar();
    const trailAge = t.broken.mul(max(t.dAcross, 0)).mul(t.T.div(t.L)).toVar();
    return { whitewash, breaker, lipShape, trailAge };
  };

  const surfOwnershipMask = (depth, toShore, influence) => {
    const band = max(G.uSurfBandDepth, SURF.MIN_BAND_DEPTH);
    const develop = float(1).sub(smoothstep(band.mul(0.55), band, max(depth, 0)));
    return develop.mul(influence).mul(surfExposure(toShore)).mul(G.uSurfActive);
  };
  const surfFieldMask = (depth, toShore, influence) => {
    const band = max(G.uSurfBandDepth, SURF.MIN_BAND_DEPTH);
    const develop = float(1).sub(smoothstep(band.mul(0.55), band, max(depth, 0)));
    return develop.mul(smoothstep(float(SURF.WET_FADE_LO), float(SURF.WET_FADE_HI), depth))
      .mul(influence).mul(surfExposure(toShore)).mul(G.uSurfActive);
  };

  /* EvaluateSurfWaves — returns the full sample (all gated by uSurfActive) */
  G.surfEvaluate = (worldXZ, shore, timeN) => {
    const mask = surfFieldMask(shore.depth, shore.toShore, shore.influence).toVar();
    const exposure = surfExposure(shore.toShore).toVar();
    /* standing waterline lace */
    const lacePhase = timeN.div(max(G.uSurfPeriod, SURF.MIN_PERIOD)).sub(0.5).toVar();
    const laceIndex = floor(lacePhase).toVar();
    const laceSeg = mix(surfCrestFactor(worldXZ, laceIndex), surfCrestFactor(worldXZ, laceIndex.add(1)),
      smoothstep(float(0), float(1), lacePhase.sub(laceIndex)));
    const lace = float(1).sub(smoothstep(float(0.2), float(1.8), max(shore.depth, 0)))
      .mul(smoothstep(float(-0.35), float(-0.05), shore.depth))
      .mul(G.uSurfWaterlineFoam).mul(shore.influence).mul(exposure).mul(laceSeg).mul(G.uSurfActive).toVar();

    const s = max(shore.sdfDist, 0).toVar();
    const t = surfFrontTerms(worldXZ, surfWarpDistance(s), shore.depth, shore.slopeTan, timeN);
    const foam = surfFrontFoamFromTerms(t);
    const h0 = surfFrontHeightFromTerms(t).toVar();
    /* FD slope along the shore-distance axis */
    const t1 = surfFrontTerms(worldXZ, surfWarpDistance(s.add(SURF.SLOPE_EPSILON)), shore.depth, shore.slopeTan, timeN);
    const h1 = surfFrontHeightFromTerms(t1);
    const dhds = h1.sub(h0).div(SURF.SLOPE_EPSILON);

    return {
      height: h0.mul(mask).toVar(),
      slopeXZ: shore.toShore.negate().mul(dhds.mul(mask)).toVar(),
      whitewash: saturate(foam.whitewash.mul(mask).add(lace)).toVar(),
      breaker: saturate(foam.breaker.mul(mask)).toVar(),
      mask,
      overCap: t.overCap,
      lipShape: foam.lipShape.mul(mask).toVar(),
      trailAge: foam.trailAge,
    };
  };
  G.surfInert = () => ({
    height: float(0), slopeXZ: vec2(0, 0), whitewash: float(0), breaker: float(0),
    mask: float(0), overCap: float(0), lipShape: float(0), trailAge: float(0),
  });
  G.surfAmbientWeight = (mask) => float(1).sub(mask.mul(saturate(G.uSurfAmbientFade)));
  G.surfOwnershipMask = surfOwnershipMask;

  /* EvaluateSurfSwash — (swashLevel, wetLevel) */
  G.surfSwash = (worldXZ, toShore, tanBeta, influence0, timeN) => {
    const slopeFalloff = max(G.uSurfSwashMaxSlopeTan.mul(1 - SURF.SWASH_SLOPE_FEATHER), 1e-4);
    const slopeT = saturate(G.uSurfSwashMaxSlopeTan.sub(tanBeta).div(slopeFalloff));
    const influence = influence0.mul(smoothstep(float(0), float(1), slopeT)).mul(G.uSurfActive).toVar();
    const T = max(G.uSurfPeriod, SURF.MIN_PERIOD).toVar();
    const phase = timeN.div(T).toVar();
    const arrivalIndex = floor(phase.sub(0.5)).toVar();
    const f = fract(phase.sub(0.5)).toVar();
    const exposure = surfExposure(toShore).toVar();
    const runFor = (idx) => {
      const deepHeight = G.uSurfAmplitude.mul(surfSetAmp(idx)).mul(surfCrestFactor(worldXZ, idx)).toVar();
      const xi = surfIribarren(tanBeta, deepHeight).toVar();
      const surge = surfBreakerWeights(xi).surge;
      return min(xi, SURF.RUNUP_XI_CAP).mul(deepHeight).mul(G.uSurfSwashAmplitude)
        .mul(mix(float(1), float(SURF.SURGE_RUNUP_BOOST), surge)).mul(influence).mul(exposure).toVar();
    };
    const run = runFor(arrivalIndex);
    const runPrev = runFor(arrivalIndex.sub(1));
    const upDown = select(f.lessThan(SURF.SWASH_UPRUSH),
      smoothstep(float(0), float(SURF.SWASH_UPRUSH), f),
      float(1).sub(smoothstep(float(SURF.SWASH_UPRUSH), float(1), f)));
    const swashLevel = run.mul(upDown).toVar();
    const dryTime = select(G.uWetDryTime.greaterThan(0), G.uWetDryTime, float(SURF.DEFAULT_DRY_SECONDS));
    const tau = dryTime.div(SURF.DRY_TIME_CONSTANTS).toVar();
    const thisCycleWet = select(f.lessThan(SURF.SWASH_UPRUSH), swashLevel,
      run.mul(exp(f.sub(SURF.SWASH_UPRUSH).mul(T).negate().div(tau))));
    const prevCycleWet = runPrev.mul(exp(f.add(1 - SURF.SWASH_UPRUSH).mul(T).negate().div(tau)));
    return { swashLevel, wetLevel: max(thisCycleWet, prevCycleWet).toVar() };
  };

  /* ---- FFT cascade sampling (WaterLargeWaves.hlsl) ---- */
  G.cascadeFade = (camDist, c) => {
    const f = saturate(camDist.div(max(fftU.visible[c], 1e-3)));
    return float(1).sub(f.mul(f).mul(f));
  };
  G.cascadeShoalWeight = (c, shore) => {
    const wavelength = max(fftU.domain[c], 1e-3).mul(OCEAN.CASCADE_WAVELENGTH_FRACTION);
    return mix(float(1), G.shoalWeight(shore.depth, wavelength), saturate(shore.influence));
  };

  G.fftDisplacement = (worldXZ, shore, camDist) => {
    const sum = vec3(0).toVar();
    for (let c = 0; c < 4; c++) {
      const uvC = worldXZ.div(max(fftU.domain[c], 1e-3));
      const fade = G.cascadeFade(camDist, c);
      const shoal = G.cascadeShoalWeight(c, shore);
      sum.addAssign(texture(fft.dispTex[c], uvC).level(0).xyz.mul(fade.mul(shoal)));
    }
    return sum;
  };

  G.fftNormalSums = (worldXZ, shore, camDist) => {
    const tilt = vec2(0).toVar();
    const pinch = float(0).toVar();
    const foamSum = float(0).toVar();
    for (let c = 0; c < 4; c++) {
      const domain = max(fftU.domain[c], 1e-3);
      const uvC = worldXZ.div(domain);
      const fade = G.cascadeFade(camDist, c).toVar();
      const lod = log2(camDist.div(domain).add(1));
      const shoal = G.cascadeShoalWeight(c, shore).toVar();
      const tap = texture(fft.normTex[c], uvC).level(lod).toVar();
      tilt.addAssign(tap.xz.mul(shoal.mul(max(fade, OCEAN.FAR_SLOPE_FLOOR[c]))));
      pinch.addAssign(tap.y.mul(shoal.mul(fade)));
      foamSum.addAssign(tap.w.mul(shoal.mul(fade)));
    }
    return { tilt, pinch: saturate(pinch), foam: saturate(foamSum) };
  };

  /* vertex composition (FFT branch of LargeBodyWaveHeightDispShore) */
  G.largeBodyHeightDisp = (worldXZ, shore, surf, camDist) => {
    const fftD = G.fftDisplacement(worldXZ, shore, camDist).toVar();
    const ambient = G.surfAmbientWeight(surf.mask).toVar();
    const height = fftD.y.mul(fftU.amplitude).mul(ambient).add(surf.height).toVar();
    const disp = fftD.xz.mul(fftU.amplitude.mul(ambient)).toVar();
    return { height, disp };
  };

  /* SurfaceHeightAtXZ — THE one source of truth for the waterline (WaterWaterline.hlsl) */
  G.surfaceHeightAtXZ = (worldXZ) => {
    const shore = G.shoreSample(worldXZ);
    const surf = G.surfEvaluate(worldXZ, shore, G.uSurfBeatTime);
    const camDist = length(worldXZ.sub(G.uCamPos.xz));
    const hd = G.largeBodyHeightDisp(worldXZ, shore, surf, camDist);
    return G.uWaterLevel.add(hd.height);
  };
  G.surfaceSignedGap = (worldPos) => worldPos.y.sub(G.surfaceHeightAtXZ(worldPos.xz));

  /* SurfaceHeightBand — CPU-mirrored envelope */
  G.surfaceHeightBandCPU = () => {
    const surfReach = surfState.enabled
      ? surfState.amplitude * SURF.SETAMP_JITTER_MAX * Math.max(surfState.greens, SURF.MIN_GREENS) : 0;
    const analyticReach = Math.abs(seaState.amplitude) * UW.SURFACE_BAND_AMPLITUDES;
    const seaReach = offshoreSignificantHeight() * Math.abs(seaState.amplitude) * UW.SURFACE_BAND_CREST_REACH;
    return Math.max(Math.max(analyticReach, seaReach), surfReach) + UW.SURFACE_BAND_PAD;
  };

  /* ---- fog / in-scatter (WaterFog.hlsl) ---- */
  G.schlickPhase = (g, cosTheta) => {
    const k = g.mul(1.5).sub(g.mul(g).mul(g).mul(0.5));
    const denom = k.mul(cosTheta).add(1);
    return float(1).sub(k.mul(k)).div(max(denom.mul(denom), 1e-4));
  };
  G.inscatterColor = (viewDirWS, sunBoost) => {
    const phase = G.schlickPhase(G.uScatterAnisotropy, dot(G.uSunDir, viewDirWS));
    const lighting = G.uAmbient.mul(G.uScatterAmbientTerm)
      .add(G.uSunColor.mul(G.uScatterSunTerm.mul(phase).mul(sunBoost.add(1))));
    return vec3(G.uScatterColor).mul(G.uScatterIntensity).mul(lighting);
  };
  G.applyWaterVolumeClarity = (color, dist, inscatter, clarity) => {
    const density = G.uFogDensity.mul(mix(float(4), float(1), saturate(clarity)));
    const absorb = exp(vec3(G.uExtinction).negate().mul(density.mul(max(dist, 0))));
    return mix(inscatter, color, absorb);
  };
  G.applyOpacityTint = (color, inscatter, clarity) => {
    const opacity = saturate(G.uOpacity.add(float(1).sub(saturate(clarity)).mul(float(1).sub(G.uOpacity))));
    return mix(color, inscatter, opacity);
  };
  G.downwellingAttenuation = (pointY, level) => {
    const depthM = max(level.sub(pointY), 0);
    return exp(vec3(G.uExtinction).negate().mul(G.uDepthDarkenStrength.mul(depthM)));
  };
  G.depthFadeScalar = (pointY, level, coeff) => exp(coeff.negate().mul(max(level.sub(pointY), 0)));

  return G;
}

/* =====================================================================================
   SECTION 9 — Sky (analytic sunset), specular, detail normals, whitecap foam, caustic
   field — shared TSL, continued
   ===================================================================================== */

function extendSharedVisual(G, textures) {
  const { whitecapTex, detailNormalTex } = textures;

  /* sky palette uniforms — driven from sun elevation on the CPU (updateSun) */
  G.uZenithColor = uniform(new THREE.Color(0.06, 0.16, 0.36));
  G.uHorizonColor = uniform(new THREE.Color(1.0, 0.45, 0.18));
  G.uGlowColor = uniform(new THREE.Color(1.1, 0.5, 0.2));
  G.uSunTint = uniform(new THREE.Color(1, 1, 1));
  G.uNightFactor = uniform(0);

  const hgPhase = (cosG, g) => {
    const g2 = g * g;
    return float((1 - g2) / (4 * Math.PI)).div(pow(float(1 + g2).sub(cosG.mul(2 * g)), 1.5));
  };

  /* analytic sunset sky — base radiance without the sun disc */
  G.skyBase = (dir) => {
    const y = dir.y.toVar();
    const cosG = dot(dir, G.uSunDir).toVar();
    const grad = mix(vec3(G.uHorizonColor), vec3(G.uZenithColor), pow(saturate(y.add(0.02)), 0.38)).toVar();
    /* warm forward glow that hugs the horizon on the sun side */
    const glowAmt = pow(saturate(cosG.mul(0.5).add(0.5)), 6).mul(pow(float(1).sub(saturate(y)), 2.5));
    grad.addAssign(vec3(G.uGlowColor).mul(glowAmt));
    /* mie halo around the sun */
    grad.addAssign(vec3(G.uSunTint).mul(hgPhase(cosG, 0.78)).mul(0.35));
    /* below the horizon: hold the horizon colour (REFLECTION_MIN_UP_Y handles rays anyway) */
    const horizonHold = mix(vec3(G.uHorizonColor).mul(0.55), grad, smoothstep(float(-0.12), float(0.0), y));
    return select(y.lessThan(0), horizonHold, grad);
  };
  G.skyWithSun = (dir, roughness) => {
    const cosG = dot(dir, G.uSunDir).toVar();
    const discCos = float(0.99996).sub(roughness.mul(0.02));       // widen with roughness
    const disc = smoothstep(discCos.sub(0.00012), discCos, cosG)
      .mul(float(60).div(roughness.mul(400).add(1)));
    return G.skyBase(dir).add(vec3(G.uSunTint).mul(disc));
  };

  /* SampleSkyEnvironmentAniso — 5-tap vertical smear, y-clamped 0.02 (spec 3.2) */
  G.sampleSkyAniso = (worldRay, roughness) => {
    const spread = G.uAnisoStretch.mul(roughness).toVar();
    const offsets = [-1.0, -0.5, 0.0, 0.5, 1.0];
    const weights = [0.1, 0.2, 0.4, 0.2, 0.1];
    const col = vec3(0).toVar();
    for (let t = 0; t < 5; t++) {
      const tapVec = worldRay.add(vec3(0, spread.mul(offsets[t]), 0)).toVar();
      const safe = select(lengthSq(tapVec).lessThan(1e-8), worldRay, normalize(tapVec)).toVar();
      const tapRay = vec3(safe.x, max(safe.y, 0.02), safe.z);
      col.addAssign(G.skyWithSun(normalize(tapRay), roughness).mul(weights[t]));
    }
    return col.mul(G.uEnvReflectionIntensity);
  };

  /* WaterSurfaceSpecular.hlsl — dual-lobe GGX (verbatim) */
  G.effectiveRoughness = (viewDist) => {
    const ramp = pow(saturate(viewDist.div(max(G.uRoughnessFarDistance, 1))), G.uRoughnessFalloff);
    return mix(G.uSunRoughness, G.uRoughnessFar, ramp);
  };
  const ggxLobeDistribution = (noh, nol, nov, r) => {
    const a = r.mul(r), a2 = a.mul(a);
    const d = noh.mul(noh).mul(a2.sub(1)).add(1);
    const ndf = a2.div(max(d.mul(d).mul(Math.PI), 1e-9));
    const vis = float(0.5).div(max(mix(nol.mul(nov).mul(2), nol.add(nov), a), 1e-5));
    return ndf.mul(vis).mul(nol);
  };
  G.sunSpecular = (normalW, viewDir, roughness) => {
    const halfVec = viewDir.add(G.uSunDir).toVar();
    const hLen2 = lengthSq(halfVec).toVar();
    const h = normalize(halfVec).toVar();
    const noh = saturate(dot(normalW, h)).toVar();
    const nov = saturate(dot(normalW, viewDir)).toVar();
    const loh = saturate(dot(G.uSunDir, h)).toVar();
    const nol = saturate(dot(normalW, G.uSunDir)).toVar();      // grazeBoost 0 (preset)
    const F = float(LOOK.fresnelF0).add(pow(float(1).sub(loh), 5.0).mul(1 - LOOK.fresnelF0));
    const lobe = ggxLobeDistribution(noh, nol, nov, roughness).mul(F).toVar();
    const spec = min(lobe, 50.0).mul(vec3(G.uSunColor));
    return select(hLen2.lessThan(1e-8), vec3(0), spec);
  };

  /* ---- detail normal ladder with hex stochastic tiling (WaterSurfaceDetailNormal) ---- */
  const detailHexHash = (cell) => {
    const q = uvec2(ivec2(cell.add(1024)));
    const h0 = q.x.mul(uint(1597334677)).bitXor(q.y.mul(uint(3812015801))).toVar();
    h0.assign(h0.bitXor(h0.shiftRight(uint(15))).mul(uint(2246822519)));
    h0.assign(h0.bitXor(h0.shiftRight(uint(13))));
    return vec2(float(h0.bitAnd(uint(0xffff))), float(h0.shiftRight(uint(16)).bitAnd(uint(0xffff)))).div(65536);
  };
  const detailSampleTilt = (uvIn, ddxIn, ddyIn) => {
    /* hex stochastic tiling (Heitz), weights^3, variance-restored */
    const st = uvIn.mul(DETC.HEX_LATTICE_SCALE).toVar();
    const skewed = vec2(st.x.sub(st.y.mul(0.57735027)), st.y.mul(1.15470054)).toVar();
    const baseId = floor(skewed).toVar();
    const fpart = fract(skewed).toVar();
    const tz = float(1).sub(fpart.x).sub(fpart.y).toVar();
    const flip = tz.lessThan(0).toVar();
    const w1 = select(flip, tz.negate(), tz).toVar();
    const w2 = select(flip, float(1).sub(fpart.y), fpart.y).toVar();
    const w3 = select(flip, float(1).sub(fpart.x), fpart.x).toVar();
    const v1 = select(flip, baseId.add(vec2(1, 1)), baseId).toVar();
    const v2 = select(flip, baseId.add(vec2(1, 0)), baseId.add(vec2(0, 1))).toVar();
    const v3 = select(flip, baseId.add(vec2(0, 1)), baseId.add(vec2(1, 0))).toVar();
    const p1 = pow(w1, DETC.HEX_WEIGHT_EXPONENT).toVar();
    const p2 = pow(w2, DETC.HEX_WEIGHT_EXPONENT).toVar();
    const p3 = pow(w3, DETC.HEX_WEIGHT_EXPONENT).toVar();
    const norm = p1.add(p2).add(p3).max(1e-6);
    const s1 = texture(detailNormalTex, uvIn.add(detailHexHash(v1))).grad(ddxIn, ddyIn).xy.mul(2).sub(1);
    const s2 = texture(detailNormalTex, uvIn.add(detailHexHash(v2))).grad(ddxIn, ddyIn).xy.mul(2).sub(1);
    const s3 = texture(detailNormalTex, uvIn.add(detailHexHash(v3))).grad(ddxIn, ddyIn).xy.mul(2).sub(1);
    const blended = s1.mul(p1).add(s2.mul(p2)).add(s3.mul(p3)).div(norm);
    const wlen = sqrt(max(p1.mul(p1).add(p2.mul(p2)).add(p3.mul(p3)).div(norm.mul(norm)), 1e-6));
    return blended.div(wlen);
  };
  const detailOctave = (worldXZ, scroll0, scroll1, speed, tile, wDdx, wDdy) => {
    const invTile0 = float(DETC.CROSS_TILE_SPLIT).div(max(tile, 1e-3)).toVar();
    const invTile1 = float(1).div(max(tile, 1e-3).mul(DETC.CROSS_TILE_SPLIT)).toVar();
    const speed0 = speed.div(DETC.CROSS_SPEED_SPLIT), speed1 = speed.mul(DETC.CROSS_SPEED_SPLIT);
    const uv0 = worldXZ.add(scroll0.mul(speed0)).mul(invTile0);
    const uv1 = worldXZ.add(scroll1.mul(speed1)).mul(invTile1);
    return detailSampleTilt(uv0, wDdx.mul(invTile0), wDdy.mul(invTile0))
      .add(detailSampleTilt(uv1, wDdx.mul(invTile1), wDdy.mul(invTile1)));
  };
  G.detailNormalTilt = (worldXZ, viewDist) => {
    const wind = fft.U.windDir;
    const cm = (a, b) => vec2(a.x.mul(b.x).sub(a.y.mul(b.y)), a.x.mul(b.y).add(a.y.mul(b.x)));
    const dir0 = cm(vec2(...DETC.DIR0), wind).toVar();
    const dir1 = cm(vec2(...DETC.DIR1), wind).toVar();
    const scroll0 = dir0.mul(fft.U.waveTime).toVar();
    const scroll1 = dir1.mul(fft.U.waveTime).toVar();
    const tileRatio = max(G.uDetailFarScale, 1e-3).div(max(G.uDetailScale, 1e-3)).toVar();
    const lnPhi2 = Math.log2(DETC.FAR_TILE_MULT);
    const maxOctave = max(log2(max(tileRatio, 1e-3)).div(lnPhi2), 0).toVar();
    const octaveReference = max(G.uDetailFarDistance.sub(DETC.FAR_BLEND_START), 1).div(max(tileRatio.sub(1), 1e-3));
    const ladderOctave = log2(max(viewDist.sub(DETC.FAR_BLEND_START), 0).div(octaveReference).add(1)).div(lnPhi2).toVar();
    const octave = min(ladderOctave, maxOctave).toVar();
    const i = floor(octave).toVar();
    const f = octave.sub(i).toVar();
    const tileNear = G.uDetailScale.mul(pow(float(DETC.FAR_TILE_MULT), i)).toVar();
    const speedRatio = max(G.uDetailFarSpeed, 1e-4).div(max(G.uDetailSpeed, 1e-4));
    const speedPerOctave = select(maxOctave.greaterThan(1e-3), pow(speedRatio, float(1).div(maxOctave)), float(1)).toVar();
    const speedNear = G.uDetailSpeed.mul(pow(speedPerOctave, i)).toVar();
    const wDdx = dFdx(worldXZ).toVar();
    const wDdy = dFdy(worldXZ).toVar();
    const tiltNear = detailOctave(worldXZ, scroll0, scroll1, speedNear, tileNear, wDdx, wDdy).toVar();
    const tiltFar = detailOctave(worldXZ, scroll0, scroll1, speedNear.mul(speedPerOctave), tileNear.mul(DETC.FAR_TILE_MULT), wDdx, wDdy).toVar();
    const blendRms = sqrt(max(f.oneMinus().mul(f.oneMinus()).add(f.mul(f)), 1e-4));
    const tilt = mix(tiltNear, tiltFar, f).div(blendRms).toVar();
    const capped = max(ladderOctave.sub(maxOctave), 0);
    tilt.mulAssign(G.uDetailDistanceBoost.mul(capped).add(1));
    const fadeD = float(1).sub(saturate(viewDist.sub(DETC.FADE_START).div(DETC.FADE_RANGE)));
    return tilt.mul(fadeD);
  };

  /* ---- whitecap pattern + dissolve (WaterSurfaceFoamSampling.hlsl) ---- */
  const rot30 = (v) => vec2(
    v.x.mul(FOAMC.OCTAVE2_COS).sub(v.y.mul(FOAMC.OCTAVE2_SIN)),
    v.x.mul(FOAMC.OCTAVE2_SIN).add(v.y.mul(FOAMC.OCTAVE2_COS)));
  const whitecapOctaveBlend = (a, b) =>
    saturate(a.add(b).sub(2 * FOAMC.PATTERN_MEAN).mul(FOAMC.OCTAVE_BLEND_NORM).add(FOAMC.PATTERN_MEAN));
  G.samplePatternTiled = (worldXZ, camDist, tileSize, ddxIn, ddyIn) => {
    const uv0 = worldXZ.div(tileSize);
    const octave0 = texture(whitecapTex, uv0).grad(ddxIn.div(tileSize), ddyIn.div(tileSize)).x.toVar();
    const rotXZ = rot30(worldXZ);
    const tile1 = tileSize.mul(FOAMC.OCTAVE2_SCALE);
    const octave1 = texture(whitecapTex, rotXZ.div(tile1)).grad(rot30(ddxIn).div(tile1), rot30(ddyIn).div(tile1)).x;
    const blend = saturate(camDist.div(FOAMC.OCTAVE_BLEND_DIST));
    return mix(octave0, whitecapOctaveBlend(octave0, octave1), blend);
  };
  G.streakFrame = (v) => {
    const wind = fft.U.windDir;
    return vec2(dot(v, wind).div(max(G.uFoamStreakStretch, 1e-3)), dot(v, vec2(wind.y.negate(), wind.x)));
  };
  const foamDissolveTerms = (coverage, extra) => {
    const c = saturate(coverage);
    return {
      contrast: mix(float(FOAMC.CONTRAST), float(FOAMC.CONTRAST_DENSE), c),
      threshold: float(1).sub(sqrt(c)).add(extra),
    };
  };
  G.foamDissolve = (patternValue, coverage, feather, extra) => {
    const t = foamDissolveTerms(coverage, extra);
    const sharpened = pow(saturate(patternValue), t.contrast);
    return smoothstep(t.threshold, t.threshold.add(max(feather, 1e-3)), sharpened);
  };
  G.foamDissolveExpected = (coverage, feather, extra) => {
    const t = foamDissolveTerms(coverage, extra);
    const midBand = pow(saturate(t.threshold.add(max(feather, 1e-3).mul(0.5))), float(1).div(max(t.contrast, 1e-3)));
    const z = midBand.sub(FOAMC.PATTERN_MEAN).div(FOAMC.PATTERN_STDDEV);
    return smoothstep(float(-FOAMC.PATTERN_CDF_SPAN), float(FOAMC.PATTERN_CDF_SPAN), z.negate());
  };
  G.foamWrappedDiffuse = (n) => saturate(dot(n, G.uSunDir).mul(1 - FOAMC.LIGHT_WRAP).add(FOAMC.LIGHT_WRAP));
  G.foamLitColor = (albedo, wrapped) => albedo.mul(vec3(G.uSunColor).mul(wrapped).add(FOAMC.AMBIENT));
  G.applyFoamTiltToNormal = (n, tilt) => {
    const tangent = normalize(cross(n, vec3(0, 0, 1))).toVar();
    const bitangent = cross(n, tangent);
    return normalize(n.add(tangent.mul(tilt.x)).add(bitangent.mul(tilt.y)));
  };
  G.sampleWhitecapTilt = (worldXZ, tileSize, ddxIn, ddyIn) => {
    const dd = tileSize.mul(FOAMC.NORMAL_DELTA).toVar();
    const base = G.samplePatternTiled(worldXZ, float(0), tileSize, ddxIn, ddyIn);
    const px = G.samplePatternTiled(worldXZ.add(vec2(dd, 0)), float(0), tileSize, ddxIn, ddyIn);
    const pz = G.samplePatternTiled(worldXZ.add(vec2(0, dd)), float(0), tileSize, ddxIn, ddyIn);
    return vec2(px.sub(base), pz.sub(base)).mul(-FOAMC.NORMAL_GAIN);
  };

  /* ---- dedicated caustic ripple field (LargeBodyCaustics.shader, verbatim) ---- */
  G.causticField = (p) => {
    const slopeAcc = vec2(0).toVar();
    const heightAcc = float(0).toVar();
    for (let i = 0; i < 9; i++) {
      const ang = 2.399963 * i + 0.7;
      const dirC = [Math.cos(ang), Math.sin(ang)];
      const jitter = (Math.sin(ang * 12.9898) * 43758.5453) % 1;
      const jit = jitter < 0 ? jitter + 1 : jitter;
      const octave = i < 6 ? 1 : (i === 6 ? 6 : (i === 7 ? 10 : 17));
      const steep = i < 6 ? 0.02 : 0.012;
      const lambda = G.uCausticRippleScale.mul(octave * (0.75 + 0.6 * jit)).toVar();
      const k = float(OCEAN.TWO_PI).div(max(lambda, 0.05)).toVar();
      const omega = sqrt(k.mul(OCEAN.GRAVITY)).toVar();
      const phase = dot(vec2(...dirC), p).mul(k).sub(omega.mul(G.uCausticTime)).add(i * 1.7).toVar();
      const amp = lambda.mul(steep).toVar();
      slopeAcc.addAssign(vec2(...dirC).mul(amp.mul(k).mul(cos(phase))));
      heightAcc.addAssign(amp.mul(sin(phase)));
    }
    return { slope: slopeAcc, height: heightAcc };
  };

  return G;
}

/* =====================================================================================
   SECTION 10 — Terrain (island mesh + IslandTerrain.shader-style substrate shading,
   wet band, caustics) — the lit-terrain function is shared with the water's refraction
   ===================================================================================== */

function createTerrain(G, island, textures) {
  const { terrainTex, controlTex, whitecapTex } = textures;
  const width = WORLD.terrainWidth, hMeters = WORLD.terrainHeight;

  /* world xz -> field uv */
  const fieldUV = (worldXZ) => clamp(worldXZ.div(width).add(0.5), vec2(0), vec2(1));

  G.terrainHeightAt = (worldXZ) => texture(terrainTex, fieldUV(worldXZ)).level(0).z;

  /* lit terrain colour at a world xz (normal + height from the baked field texture).
     viewDirToCam: unit vector surface->camera, used for specular. */
  G.litTerrain = (worldXZ, worldY, viewDirToCam, opts = {}) => {
    const uvT = fieldUV(worldXZ).toVar();
    const t = texture(terrainTex, uvT).level(0).toVar();
    const nrm = vec3(t.x, sqrt(saturate(float(1).sub(t.x.mul(t.x)).sub(t.y.mul(t.y)))), t.y).toVar();
    const ctrl = texture(controlTex, uvT).toVar();
    const total = max(ctrl.x.add(ctrl.y).add(ctrl.z).add(ctrl.w), 1e-4);
    const w = ctrl.div(total).toVar();          // (seabed, beach, rock, grass)

    /* flat tints + a soft value-noise breakup borrowed from the whitecap tile */
    const breakup = texture(whitecapTex, worldXZ.mul(0.11)).level(0).x.mul(0.36).add(0.82).toVar();
    const albedo = vec3(...SUBSTRATE.seabed.tint).mul(w.x)
      .add(vec3(...SUBSTRATE.beach.tint).mul(w.y))
      .add(vec3(...SUBSTRATE.rock.tint).mul(w.z))
      .add(vec3(...SUBSTRATE.grass.tint).mul(w.w))
      .mul(breakup).toVar();
    const smoothness = float(SUBSTRATE.seabed.smoothness).mul(w.x)
      .add(float(SUBSTRATE.beach.smoothness).mul(w.y))
      .add(float(SUBSTRATE.rock.smoothness).mul(w.z))
      .add(float(SUBSTRATE.grass.smoothness).mul(w.w)).toVar();
    const wetDarken = float(SUBSTRATE.seabed.wetDarken).mul(w.x)
      .add(float(SUBSTRATE.beach.wetDarken).mul(w.y))
      .add(float(SUBSTRATE.rock.wetDarken).mul(w.z))
      .add(float(SUBSTRATE.grass.wetDarken).mul(w.w)).toVar();
    const wetSmooth = float(SUBSTRATE.seabed.wetSmoothness).mul(w.x)
      .add(float(SUBSTRATE.beach.wetSmoothness).mul(w.y))
      .add(float(SUBSTRATE.rock.wetSmoothness).mul(w.z))
      .add(float(SUBSTRATE.grass.wetSmoothness).mul(w.w)).toVar();

    /* wetness: swash-driven wet line + band above the still waterline */
    const shore = G.shoreSample(worldXZ);
    const swash = G.surfSwash(worldXZ, shore.toShore, shore.slopeTan, shore.influence, G.uSurfBeatTime);
    const beachRise = worldY.sub(G.uWaterLevel).toVar();
    const bandWet = float(1).sub(saturate(beachRise.sub(swash.wetLevel).div(SUBSTRATE.wetBandHeight)));
    const wet = saturate(bandWet).mul(SUBSTRATE.wetStrength).toVar();
    albedo.assign(mix(albedo, albedo.mul(float(1).sub(wetDarken)), wet));
    smoothness.assign(mix(smoothness, wetSmooth, wet));
    const shadedNormal = normalize(mix(nrm, vec3(0, 1, 0), wet.mul(SUBSTRATE.wetNormalFlatten))).toVar();

    /* lambert + blinn-phong */
    const ndl = saturate(dot(shadedNormal, G.uSunDir)).toVar();
    const col = albedo.mul(vec3(G.uAmbient).add(vec3(G.uSunColor).mul(ndl))).toVar();
    const h = normalize(viewDirToCam.add(G.uSunDir));
    const specExp = mix(float(8), float(256), smoothness);
    const spec = pow(saturate(dot(shadedNormal, h)), specExp).mul(ndl);
    col.addAssign(vec3(G.uSunColor).mul(vec3(...SUBSTRATE.specColor)).mul(spec).mul(smoothness.mul(1.5).add(0.25)));

    /* underwater: caustics (window frame) + downwelling + tint */
    const underwater = worldY.lessThan(G.uWaterLevel).toVar();
    const refracted = refract(G.uSunDir.negate(), vec3(0, 1, 0), UW.IOR_RATIO).toVar();
    const safeY = sign(refracted.y).mul(max(abs(refracted.y), UW.MIN_REFRACTED_LIGHT_Y));
    const refPlaneY = G.uSimCenter.y.sub(UW.CAUSTIC_REFERENCE_DEPTH);
    const projXZ = worldXZ.add(refracted.xz.mul(refPlaneY.sub(worldY).div(safeY))).toVar();
    const windowNorm = projXZ.sub(G.uSimCenter.xz).div(max(G.uSimExtent, 1e-3)).toVar();
    const edge = vec2(1, 1).sub(abs(windowNorm)).toVar();
    const footprint = saturate(min(edge.x, edge.y).div(UW.CAUSTIC_WINDOW_FADE))
      .mul(step(0.0, edge.x)).mul(step(0.0, edge.y)).toVar();
    const causticSample = texture(G.causticTex, windowNorm.mul(0.5).add(0.5)).toVar();
    const causticFadeD = G.depthFadeScalar(worldY, G.uWaterLevel, G.uCausticDepthFade);
    const caustic = causticSample.x.mul(G.uCausticStrength).mul(causticFadeD).mul(footprint).mul(ndl);
    const colUnder = col.add(albedo.mul(caustic))
      .mul(vec3(...SUBSTRATE.underwaterTint))
      .mul(G.downwellingAttenuation(worldY, G.uWaterLevel)).toVar();
    return select(underwater, colUnder, col);
  };

  /* mesh — CPU-displaced grid over the island heightmap */
  const meshRes = WORLD.terrainMeshRes;
  const geo = new THREE.PlaneGeometry(width, width, meshRes, meshRes);
  geo.rotateX(-Math.PI / 2);                                   // XZ plane, +Y up
  const pos = geo.attributes.position;
  const res = island.res;
  const hAt = (fx, fz) => {                                    // bilinear in heightmap space
    const x0 = Math.min(Math.max(Math.floor(fx), 0), res - 2);
    const z0 = Math.min(Math.max(Math.floor(fz), 0), res - 2);
    const tx = Math.min(Math.max(fx - x0, 0), 1), tz = Math.min(Math.max(fz - z0, 0), 1);
    const i = z0 * res + x0;
    return (island.map[i] * (1 - tx) * (1 - tz) + island.map[i + 1] * tx * (1 - tz)
      + island.map[i + res] * (1 - tx) * tz + island.map[i + res + 1] * tx * tz) * hMeters;
  };
  for (let i = 0; i < pos.count; i++) {
    const wx = pos.getX(i), wz = pos.getZ(i);
    const fx = (wx / width + 0.5) * (res - 1);
    const fz = (wz / width + 0.5) * (res - 1);
    pos.setY(i, hAt(fx, fz));
  }
  pos.needsUpdate = true;
  geo.computeVertexNormals();

  const mat = new THREE.MeshBasicNodeMaterial();
  const wPos = positionLocal;                                  // terrain mesh sits at origin
  mat.colorNode = Fn(() => {
    const worldPos = positionLocal.toVar();
    const viewDir = normalize(vec3(G.uCamPos).sub(worldPos)).toVar();
    return vec4(G.litTerrain(worldPos.xz, worldPos.y, viewDir), 1);
  })();
  const mesh = new THREE.Mesh(geo, mat);
  mesh.frustumCulled = false;
  return mesh;
}

/* =====================================================================================
   SECTION 11 — Water surface material (WaterSurfaceVertStage + FragStages, ocean path)
   ===================================================================================== */

function buildOceanGeometry() {
  /* camera-centred polar sheet: dense near rings, exponential falloff to the horizon */
  const RADIAL = 200, ANGULAR = 312;
  const rMax = 9200, rNear = 0.8;
  const radii = [0];
  for (let j = 1; j <= RADIAL; j++)
    radii.push(rNear * Math.exp((j - 1) / (RADIAL - 1) * Math.log(rMax / rNear)));
  const positions = new Float32Array((RADIAL + 1) * ANGULAR * 3);
  let p = 0;
  for (let j = 0; j <= RADIAL; j++) {
    for (let a = 0; a < ANGULAR; a++) {
      const th = a / ANGULAR * Math.PI * 2;
      positions[p++] = Math.cos(th) * radii[j];
      positions[p++] = 0;
      positions[p++] = Math.sin(th) * radii[j];
    }
  }
  const indices = [];
  for (let j = 0; j < RADIAL; j++) {
    for (let a = 0; a < ANGULAR; a++) {
      const a1 = (a + 1) % ANGULAR;
      const i00 = j * ANGULAR + a, i01 = j * ANGULAR + a1;
      const i10 = (j + 1) * ANGULAR + a, i11 = (j + 1) * ANGULAR + a1;
      indices.push(i00, i10, i11, i00, i11, i01);              // +Y winding
    }
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geo.setIndex(indices);
  geo.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 1e6);
  return geo;
}

const smoothMinN = (a, b, k) => {
  const h = saturate(b.sub(a).div(k).mul(0.5).add(0.5));
  return mix(b, a, h).sub(h.mul(float(1).sub(h)).mul(k));
};
const smoothMaxN = (a, b, k) => {
  const h = saturate(a.sub(b).div(k).mul(0.5).add(0.5));
  return mix(b, a, h).add(h.mul(float(1).sub(h)).mul(k));
};

function createWaterSurface(G) {
  const fft = G.fft;
  const geo = buildOceanGeometry();
  const mat = new THREE.MeshBasicNodeMaterial({ side: THREE.DoubleSide });
  G.uMeshOffset = uniform(new THREE.Vector2(0, 0));

  /* ---------- vertex ---------- */
  const vertexData = Fn(() => {
    const srcXZ = positionLocal.xz.add(G.uMeshOffset).toVar();  // undisplaced world xz
    const shore = G.shoreSample(srcXZ);
    const surf = G.surfEvaluate(srcXZ, shore, G.uSurfBeatTime);
    const camDist = length(srcXZ.sub(G.uCamPos.xz)).toVar();
    const hd = G.largeBodyHeightDisp(srcXZ, shore, surf, camDist);
    const worldY = G.uWaterLevel.add(hd.height).toVar();

    /* shore film lift (vertex stage, spec 1.4/6.1) */
    const beachRise = shore.depth.negate().toVar();
    const swash = G.surfSwash(srcXZ, shore.toShore, shore.slopeTan, shore.influence, G.uSurfBeatTime);
    const geomReach = swash.wetLevel;
    const filmTop = G.uWaterLevel
      .add(smoothMinN(beachRise, geomReach, float(SURF.FILM_BLEND)))
      .add(SURF.FILM_THICKNESS);
    const lifted = smoothMaxN(worldY, filmTop, float(SURF.FILM_BLEND));
    const doLift = shore.influence.greaterThan(0).and(beachRise.greaterThan(0)).and(geomReach.greaterThan(1e-3)).and(G.uSurfActive.greaterThan(0.5));
    worldY.assign(select(doLift, lifted, worldY));

    return vec3(positionLocal.x.add(hd.disp.x), worldY, positionLocal.z.add(hd.disp.y));
  });
  mat.positionNode = vertexData();
  const vSourceXZ = vertexStage(positionLocal.xz.add(G.uMeshOffset));

  /* ---------- fragment (frag() pipeline order, spec section 8) ---------- */
  mat.colorNode = Fn(() => {
    const worldPos = positionWorld.toVar();
    const srcXZ = vSourceXZ.toVar();
    const camDist = length(worldPos.sub(G.uCamPos)).toVar();
    const incomingRay = normalize(worldPos.sub(G.uCamPos)).toVar();
    const viewDir = incomingRay.negate().toVar();
    const camDistXZ = length(srcXZ.sub(G.uCamPos.xz)).toVar();

    const shore = G.shoreSample(srcXZ);
    const surf = G.surfEvaluate(srcXZ, shore, G.uSurfBeatTime);
    const swash = G.surfSwash(srcXZ, shore.toShore, shore.slopeTan, shore.influence, G.uSurfBeatTime);

    /* --- shoreline clip: dry beach above the film/swash reach is discarded --- */
    const shoreKeep = max(swash.swashLevel, swash.wetLevel).toVar();
    Discard(shore.influence.greaterThan(0).and(shore.depth.add(0.02).add(shoreKeep).lessThan(0)));

    /* --- normal assembly (spec section 2) --- */
    const sums = G.fftNormalSums(srcXZ, shore, camDistXZ);
    const ambientW = G.surfAmbientWeight(surf.mask).toVar();
    const fftTilt = sums.tilt.mul(ambientW).sub(surf.slopeXZ).toVar();
    const normalW = normalize(vec3(
      fftTilt.x.mul(G.uWaveNormalStrength), 1, fftTilt.y.mul(G.uWaveNormalStrength))).toVar();
    const slopeSine = length(normalW.xz);
    const steepness = saturate(slopeSine.div(DETC.CREST_REFERENCE_SLOPE));
    const dStrength = G.uDetailStrength.mul(G.uDetailCrestBoost.mul(steepness).add(1));
    const dTilt = G.detailNormalTilt(srcXZ, camDist);
    normalW.assign(normalize(normalW.add(vec3(dTilt.x, 0, dTilt.y).mul(dStrength))));

    /* geometry foam on surf bodies (LBW constants) */
    const pinchG = sums.pinch.mul(ambientW.mul(1.5));
    const steepG = smoothstep(float(0.28), float(0.65), length(fftTilt));
    const surfGeomFoam = saturate(max(pinchG, steepG)).mul(surf.mask).toVar();

    const roughness = G.effectiveRoughness(camDist).toVar();
    const inscatter = G.inscatterColor(viewDir, float(0)).toVar();

    const outCol = property('vec3', 'outCol');

    If(frontFacing, () => {
      /* ---------------- from-above ocean pixel ---------------- */
      /* refraction: bed-terrain sample (the spec's porting substitution for the
         opaque-scene grab), fogged over the real refracted span */
      const rr = normalize(mix(incomingRay, refract(incomingRay, normalW, UW.IOR_RATIO), 1.0)).toVar();
      const colDepth = clamp(shore.depth, 0.0, 1e5).toVar();
      const span = min(colDepth.div(max(abs(rr.y), 0.05)), 500).toVar();
      const hitXZ = srcXZ.add(rr.xz.mul(span)).toVar();
      const bedCol = G.litTerrain(hitXZ, G.terrainHeightAt(hitXZ), viewDir);
      const deepWater = shore.depth.greaterThan(400);
      const refracted0 = select(deepWater, inscatter,
        G.applyWaterVolumeClarity(bedCol, span, inscatter, float(1)));
      const refracted = G.applyOpacityTint(refracted0, inscatter, float(1)).toVar();

      /* fresnel + aniso sky reflection */
      const grazing = pow(saturate(float(1).sub(dot(normalW, viewDir))), G.uFresnelPower);
      const fresnel = max(grazing.mul(1 - LOOK.fresnelF0).add(LOOK.fresnelF0), G.uFresnelFloor).toVar();
      const reflRay = reflect(incomingRay, normalW).toVar();
      const skyRay = normalize(vec3(reflRay.x, max(reflRay.y, 0.02), reflRay.z)).toVar();
      const reflected = G.sampleSkyAniso(skyRay, roughness).toVar();

      /* crest SSS (true Jacobian fold) */
      const ramp = saturate(sums.pinch.sub(G.uSssPinchMin).div(max(G.uSssPinchMax.sub(G.uSssPinchMin), 1e-3)));
      const sunFacing = pow(saturate(dot(viewDir, G.uSunDir)), G.uSssSunFalloff).toVar();
      const sssBoost = pow(ramp, G.uSssPinchFalloff).mul(sunFacing).mul(G.uSssIntensity)
        .add(surf.breaker.mul(sunFacing).mul(G.uSssIntensity)).toVar();

      /* --- whitecaps (spec 6.1-6.4) --- */
      const coverage = sums.foam.mul(float(1).sub(G.surfOwnershipMask(shore.depth, shore.toShore, shore.influence))).toVar();
      const sampleXZ = srcXZ.add(viewDir.xz.mul(float(FOAMC.PARALLAX_HEIGHT).div(max(viewDir.y, FOAMC.PARALLAX_MIN_VIEW_Y)))).toVar();
      const wDdx = dFdx(srcXZ).toVar();
      const wDdy = dFdy(srcXZ).toVar();
      const pattern = G.samplePatternTiled(G.streakFrame(sampleXZ), camDistXZ, G.uFoamTileSize,
        G.streakFrame(wDdx), G.streakFrame(wDdy)).toVar();
      const texWeight = G.uFoamTexInfluence.mul(float(1).sub(saturate(camDistXZ.sub(FOAMC.TEXTURE_FADE_START).div(FOAMC.TEXTURE_FADE_RANGE))));
      const dissolved = G.foamDissolve(pattern, coverage, G.uFoamFeather, float(0));
      const oceanFoam = mix(G.foamDissolveExpected(coverage, G.uFoamFeather, float(0)), dissolved, texWeight).toVar();
      const wcTilt = G.sampleWhitecapTilt(G.streakFrame(sampleXZ), G.uFoamTileSize, G.streakFrame(wDdx), G.streakFrame(wDdy))
        .mul(G.uFoamNormalStrength.mul(oceanFoam));
      const foamNormal = G.applyFoamTiltToNormal(normalW, wcTilt);
      const foamTint = vec3(G.uFoamColor).mul(mix(vec3(pattern), vec3(1), oceanFoam))
        .mul(mix(vec3(1), exp(vec3(G.uExtinction).negate().mul(float(1).sub(saturate(oceanFoam)))), G.uFoamDepthTint));
      const oceanLook = G.foamLitColor(foamTint, G.foamWrappedDiffuse(foamNormal)).toVar();
      const oceanA = oceanFoam.mul(G.uFoamOpacity).toVar();

      /* --- surf whitewash (spec 6.6) --- */
      const surfCrestCap = surf.lipShape.mul(smoothstep(float(SURF.CRESTING_START), float(SURF.CRESTING_END), surf.overCap)).mul(G.uSurfFoamCrestCap);
      const surfCoverage = saturate(surf.whitewash.add(surfCrestCap).add(surfGeomFoam).mul(G.uSurfFoamStrength)).toVar();
      const surfPattern = G.samplePatternTiled(sampleXZ, camDistXZ, G.uSurfFoamTileSize, wDdx, wDdy).toVar();
      const erode = select(G.uSurfFoamTrailDissolve.greaterThan(0),
        saturate(surf.trailAge.div(G.uSurfFoamTrailDissolve)).mul(FOAMC.TRAIL_ERODE_MAX), float(0));
      const surfFoam = G.foamDissolve(surfPattern, surfCoverage, G.uSurfFoamFeather, erode).toVar();
      const surfTint = vec3(G.uSurfFoamColor).mul(mix(vec3(surfPattern), vec3(1), surfFoam));
      const surfLook = G.foamLitColor(surfTint, G.foamWrappedDiffuse(normalW)).toVar();
      const surfA = surfFoam.mul(G.uSurfFoamOpacity).toVar();

      /* --- swash foam line (spec 6.7) --- */
      const beachRise = shore.depth.negate().toVar();
      const swashBand = max(G.uSurfSwashFoamWidth, 0.01).toVar();
      const swashPhase = fract(G.uSurfBeatTime.div(max(G.uSurfPeriod, SURF.MIN_PERIOD)).sub(0.5)).toVar();
      const refluxAge = smoothstep(float(SURF.SWASH_UPRUSH), float(1), swashPhase);
      const edgeFoamW = saturate(float(1).sub(abs(beachRise.sub(swash.swashLevel)).div(swashBand)));
      const depositEnv = smoothstep(float(SURF.SWASH_UPRUSH), float(FOAMC.SWASH_DEPOSIT_PEAK), swashPhase)
        .mul(float(1).sub(smoothstep(float(FOAMC.SWASH_DEPOSIT_PEAK), float(1), swashPhase)));
      const depositW = saturate(float(1).sub(abs(beachRise.sub(swash.wetLevel)).div(swashBand))).mul(depositEnv).toVar();
      const rawCoverage = max(edgeFoamW, depositW).toVar();
      const inZone = step(swashBand.negate(), beachRise).mul(G.uSurfActive);
      const swashCoverage = saturate(rawCoverage.mul(G.uSurfSwashFoam)).mul(inZone).toVar();
      const depositShare = depositW.div(max(rawCoverage, FOAMC.MASK_EPSILON));
      const swashErode = refluxAge.mul(depositShare).mul(G.uSurfSwashFoamDissolve).mul(FOAMC.SWASH_ERODE_MAX);
      const swashPattern = G.samplePatternTiled(srcXZ, camDistXZ, G.uSurfFoamTileSize, wDdx, wDdy);
      const swashFoam = G.foamDissolve(swashPattern, swashCoverage, G.uSurfFoamFeather, swashErode).toVar();
      const swashLook = G.foamLitColor(vec3(G.uSurfFoamColor).mul(mix(vec3(swashPattern), vec3(1), swashFoam)),
        G.foamWrappedDiffuse(normalW)).toVar();
      const swashA = swashFoam.mul(G.uSurfFoamOpacity).mul(inZone).toVar();

      const foamMatte = max(oceanFoam, surfFoam.mul(step(0.001, surfCoverage))).toVar();

      /* --- composite (spec section 8 order) --- */
      const col = mix(refracted, reflected, fresnel.mul(G.uReflectionStrength).mul(float(1).sub(foamMatte))).toVar();
      col.addAssign(G.sunSpecular(normalW, viewDir, roughness).mul(G.uReflectionStrength.mul(float(1).sub(foamMatte))));
      col.addAssign(vec3(G.uScatterColor).mul(vec3(G.uSunColor)).mul(sssBoost.mul(float(1).sub(foamMatte))));

      /* shallow clarity (surf run-out) */
      const shallow = float(1).sub(saturate(shore.depth.div(0.6))).mul(0.5).mul(shore.influence)
        .mul(step(0.0, shore.depth)).mul(G.uSurfActive);
      col.assign(mix(col, refracted, shallow));

      /* thin-film transparency + drying glaze on the beach */
      const filmT = saturate(beachRise.div(max(swash.wetLevel, 1e-3)));
      const filmFade = smoothstep(float(-0.15), float(0.15), beachRise);
      const filmGate = step(0.0001, swash.wetLevel);
      col.assign(mix(col, refracted, filmT.mul(0.3).add(0.6).mul(filmFade).mul(filmGate)));
      const aboveFilm = saturate(beachRise.sub(swash.swashLevel).div(max(swash.wetLevel.sub(swash.swashLevel), 1e-3)));
      const glaze = aboveFilm.mul(smoothstep(float(0), float(0.25), swash.wetLevel.sub(beachRise).div(max(swash.wetLevel, 1e-3))))
        .mul(step(0.0, beachRise)).mul(filmGate);
      const wetLook = refracted.mul(0.7).add(reflected.mul(0.12));
      col.assign(mix(col, wetLook, glaze.mul(0.85)));

      /* exclusive foam composite */
      const A = max(max(oceanA, surfA), swashA).toVar();
      const lookSum = oceanLook.mul(oceanA).add(surfLook.mul(surfA)).add(swashLook.mul(swashA));
      const wSum = max(oceanA.add(surfA).add(swashA), 1e-5);
      col.assign(mix(col, lookSum.div(wSum), A));

      /* horizon haze toward the sky at this bearing */
      const haze = float(1).sub(exp(G.uHorizonHazeDensity.mul(0.001).mul(camDist).negate()));
      const hLen = max(length(incomingRay.xz), 1e-4);
      const horizonDir = vec3(incomingRay.x.div(hLen), 0, incomingRay.z.div(hLen));
      const hazeTarget = mix(G.skyBase(horizonDir), vec3(G.uHorizonHazeColor), G.uHorizonHazeAlpha);
      col.assign(mix(col, hazeTarget, haze));

      outCol.assign(col);
    }).Else(() => {
      /* ---------------- underside sheet (camera below the surface) ---------------- */
      const nDown = normalW.negate().toVar();
      const refrUp = refract(incomingRay, nDown, 1.333).toVar();
      const tir = lengthSq(refrUp).lessThan(1e-6).toVar();
      const cosI = saturate(dot(viewDir, nDown)).toVar();
      const sinT2 = float(1.333 * 1.333).mul(float(1).sub(cosI.mul(cosI))).toVar();
      const tirBlend = smoothstep(float(1 - 0.08), float(1), sinT2).toVar();
      const viewTint = exp(vec3(G.uExtinction).negate().mul(G.uFogDensity)).toVar();
      const skyThrough = G.skyWithSun(normalize(select(tir, vec3(0, 1, 0), refrUp)), roughness)
        .mul(viewTint).toVar();
      const mirror = inscatter.toVar();
      const grazing = pow(saturate(float(1).sub(cosI)), G.uFresnelPower);
      const fresnelB = max(grazing.mul(1 - LOOK.fresnelF0).add(LOOK.fresnelF0), G.uFresnelFloor);
      const mixAmt = max(tirBlend.mul(0.8), fresnelB.mul(0.8)).toVar();      // mirrorWaterBlend 0.8
      const col = mix(skyThrough, mirror, mixAmt).toVar();
      /* whitecap patches read as dark silhouettes from below */
      const coverage = sums.foam.toVar();
      col.assign(mix(col, inscatter.mul(0.6), saturate(coverage.mul(0.7))));
      outCol.assign(col);
    });

    return vec4(outCol, 1);
  })();

  const mesh = new THREE.Mesh(geo, mat);
  mesh.frustumCulled = false;
  return { mesh, mat, vSourceXZ };
}

/* =====================================================================================
   SECTION 12 — Caustic map generation (LargeBodyCaustics.shader: refracted grid splat,
   per-vertex focusing Jacobian)
   ===================================================================================== */

function createCausticPass(G, renderer) {
  const rt = new THREE.RenderTarget(WORLD.causticRes, WORLD.causticRes, {
    type: THREE.HalfFloatType,
    generateMipmaps: true,
    minFilter: THREE.LinearMipmapLinearFilter,
    magFilter: THREE.LinearFilter,
  });
  G.causticTex = rt.texture;

  const gridRes = WORLD.causticGridRes;
  const geo = new THREE.PlaneGeometry(2, 2, gridRes, gridRes);   // xy in [-1,1]
  const mat = new THREE.MeshBasicNodeMaterial();
  mat.depthTest = false; mat.depthWrite = false;

  const gridStepNorm = 2 / gridRes;

  /* height of the wave field for caustic projection (FFT + shore shoal, no surf fronts) */
  const waveHeightAt = (worldXZ) => {
    const shore = G.shoreSample(worldXZ);
    const camDistL = length(worldXZ.sub(G.uCamPos.xz));
    return G.fftDisplacement(worldXZ, shore, camDistL).y.mul(G.fft.U.amplitude);
  };
  const safeRefrY = (y) => sign(y).mul(max(abs(y), UW.MIN_REFRACTED_LIGHT_Y));

  const projectedPos = (windowNorm) => {
    const worldXZ = G.uSimCenter.xz.add(windowNorm.mul(G.uSimExtent)).toVar();
    const nrm = vec3(0, 1, 0).toVar();
    const r = max(G.uCausticSmooth, 0.25).toVar();
    const hXP = waveHeightAt(worldXZ.add(vec2(r, 0))).toVar();
    const hXN = waveHeightAt(worldXZ.sub(vec2(r, 0))).toVar();
    const hZP = waveHeightAt(worldXZ.add(vec2(0, r))).toVar();
    const hZN = waveHeightAt(worldXZ.sub(vec2(0, r))).toVar();
    const grad = vec2(hXP.sub(hXN), hZP.sub(hZN)).div(r.mul(2)).toVar();
    nrm.assign(vec3(nrm.x.sub(grad.x.mul(G.uWaveNormalStrength)), nrm.y, nrm.z.sub(grad.y.mul(G.uWaveNormalStrength))));
    const swellHeight = hXP.add(hXN).add(hZP).add(hZN).mul(0.25).toVar();
    const field = G.causticField(worldXZ);
    nrm.assign(vec3(
      nrm.x.sub(field.slope.x.mul(G.uWaveNormalStrength.mul(G.uCausticRippleStrength))),
      nrm.y,
      nrm.z.sub(field.slope.y.mul(G.uWaveNormalStrength.mul(G.uCausticRippleStrength)))));
    const nn = normalize(nrm).toVar();
    const ray = refract(G.uSunDir.negate(), nn, UW.IOR_RATIO).toVar();
    const waveH = swellHeight.add(field.height).toVar();
    const originY = G.uSimCenter.y.add(waveH).toVar();
    const refPlaneY = G.uSimCenter.y.sub(UW.CAUSTIC_REFERENCE_DEPTH);
    const t = refPlaneY.sub(originY).div(safeRefrY(ray.y));
    return vec3(worldXZ.x.add(ray.x.mul(t)), refPlaneY, worldXZ.y.add(ray.z.mul(t)));
  };

  const vFocus = varyingProperty('float', 'vFocus');
  mat.vertexNode = Fn(() => {
    const windowNorm = positionLocal.xy.toVar();               // plane xy = window [-1,1]
    const e = Math.max(gridStepNorm * 0.5, 1e-5);
    const newPos = projectedPos(windowNorm).toVar();
    const nX0 = projectedPos(windowNorm.sub(vec2(e, 0))).toVar();
    const nX1 = projectedPos(windowNorm.add(vec2(e, 0))).toVar();
    const nZ0 = projectedPos(windowNorm.sub(vec2(0, e))).toVar();
    const nZ1 = projectedPos(windowNorm.add(vec2(0, e))).toVar();
    const oldArea = float(2 * e).mul(abs(G.uSimExtent.x)).mul(float(2 * e).mul(abs(G.uSimExtent.y))).toVar();
    const newArea = length(nX1.xz.sub(nX0.xz)).mul(length(nZ1.xz.sub(nZ0.xz))).toVar();
    vFocus.assign(oldArea.div(max(newArea, 1e-6)).mul(UW.CAUSTIC_FOCUS_SCALE));
    const causticNorm = newPos.xz.sub(G.uSimCenter.xz).div(max(G.uSimExtent, 1e-3));
    return vec4(causticNorm.x, causticNorm.y, 0.5, 1);
  })();
  mat.colorNode = vec4(vFocus, 1, 0, 1);

  const scene = new THREE.Scene();
  const mesh = new THREE.Mesh(geo, mat);
  mesh.frustumCulled = false;
  scene.add(mesh);
  const cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);

  return {
    rt,
    render() {
      const prev = renderer.getRenderTarget();
      renderer.setRenderTarget(rt);
      renderer.render(scene, cam);
      renderer.setRenderTarget(prev);
    },
  };
}

/* LargeBodyCausticAt — shared window-frame caustic sample (god rays + seabed) */
function causticAtFn(G) {
  return (p, refractedSun, refPlaneY, lod) => {
    const safeY = sign(refractedSun.y).mul(max(abs(refractedSun.y), UW.MIN_REFRACTED_LIGHT_Y));
    const projXZ = p.xz.add(refractedSun.xz.mul(refPlaneY.sub(p.y).div(safeY))).toVar();
    const windowNorm = projXZ.sub(G.uSimCenter.xz).div(max(G.uSimExtent, 1e-3)).toVar();
    const edge = vec2(1, 1).sub(abs(windowNorm)).toVar();
    const inside = step(0.0, edge.x).mul(step(0.0, edge.y));
    const fade = saturate(min(edge.x, edge.y).div(UW.CAUSTIC_WINDOW_FADE)).mul(inside);
    return texture(G.causticTex, windowNorm.mul(0.5).add(0.5)).level(lod).x.mul(fade);
  };
}

/* =====================================================================================
   SECTION 13 — Underwater post pass: fog absorb+inscatter, per-pixel waterline mask,
   marched surface crossing, meniscus, god rays (WaterUnderwaterFog + LargeBodyGodRays)
   ===================================================================================== */

function createPost(G, renderer, scene, camera) {
  const postProcessing = new THREE.PostProcessing(renderer);
  const scenePass = pass(scene, camera);
  const sceneColorNode = scenePass.getTextureNode('output');
  const sceneDepthNode = scenePass.getTextureNode('depth');

  G.uProjInv = uniform(new THREE.Matrix4());
  G.uCamWorld = uniform(new THREE.Matrix4());
  G.uCamFwd = uniform(new THREE.Vector3(0, 0, -1));
  G.uCamNear = uniform(0.1);
  G.uGodRayFrame = uniform(0);
  const causticAt = causticAtFn(G);

  const waterlineCoverage = (gap, gapPerPixel) => {
    const perPixel = clamp(gapPerPixel, 1e-5, 0.5 / UW.WATERLINE_FEATHER_PIXELS);
    const gapPixels = gap.div(perPixel);
    return saturate(float(0.5).sub(gapPixels.div(UW.WATERLINE_FEATHER_PIXELS)));
  };

  postProcessing.outputNode = Fn(() => {
    const uvS = screenUV.toVar();
    const sceneColor = sceneColorNode.sample(uvS).rgb.toVar();
    const depth = sceneDepthNode.sample(uvS).x.toVar();
    const viewPos = getViewPosition(uvS, depth, G.uProjInv).toVar();
    const worldPos4 = G.uCamWorld.mul(vec4(viewPos, 1)).toVar();
    const sceneWorld = worldPos4.xyz.toVar();
    const cam = vec3(G.uCamPos).toVar();
    const rayVec = sceneWorld.sub(cam).toVar();
    const sceneDist = min(length(rayVec), 20000).toVar();
    const rayDir = rayVec.div(max(length(rayVec), 1e-4)).toVar();

    /* per-pixel waterline mask from the near-plane point (spec 3.5) */
    const nearWorld = cam.add(rayDir.mul(G.uCamNear.div(max(dot(rayDir, G.uCamFwd), 1e-4)))).toVar();
    const gap = G.surfaceSignedGap(nearWorld).toVar();
    const armWeight = waterlineCoverage(gap, fwidth(gap)).toVar();
    const rayStartsWet = armWeight.greaterThanEqual(0.5).toVar();

    const outColor = property('vec3', 'outColorPost');
    outColor.assign(sceneColor);

    const camSurfY = G.surfaceHeightAtXZ(cam.xz).toVar();

    /* ---- in-water span (OceanWavyPath, spec 3.6b) ---- */
    const sceneSurf = G.surfaceHeightAtXZ(sceneWorld.xz).toVar();
    const sceneUnder = sceneWorld.y.lessThanEqual(sceneSurf).toVar();
    const pathLen = property('float', 'pathLen');
    const deepestY = property('float', 'deepestY');
    const wetStartT = property('float', 'wetStartT');       // distance from cam to wet start
    pathLen.assign(0); deepestY.assign(G.uWaterLevel); wetStartT.assign(0);

    If(rayStartsWet.and(sceneUnder), () => {
      pathLen.assign(sceneDist);
      deepestY.assign(min(cam.y, sceneWorld.y));
    }).ElseIf(rayStartsWet.not().and(sceneUnder.not()), () => {
      pathLen.assign(0);
    }).Else(() => {
      /* mixed ray: fixed 1.5 m march across the surface band, bisection refine */
      const dySafe = rayDir.y.add(select(rayDir.y.greaterThanEqual(0), float(1e-4), float(-1e-4))).toVar();
      const band = G.uSurfaceBand.toVar();                   // surface height band (CPU-synced)
      const tFlat = G.uWaterLevel.sub(cam.y).div(dySafe).toVar();
      const tBand = band.div(max(abs(rayDir.y), 1e-4)).toVar();
      const startDist = saturate(tFlat.sub(tBand).div(max(sceneDist, 1e-4))).mul(sceneDist).toVar();
      const hitFlat = cam.add(rayDir.mul(clamp(tFlat, 0.0, sceneDist))).toVar();
      const hit = property('vec3', 'hitP');
      hit.assign(hitFlat);
      const prevP = property('vec3', 'prevP');
      prevP.assign(cam.add(rayDir.mul(startDist)));
      const gapPrev = property('float', 'gapPrev');
      gapPrev.assign(G.surfaceSignedGap(prevP));
      const marchReach = startDist.add(UW.CROSS_MAX_STEPS * UW.CROSS_STEP).toVar();
      const found = property('float', 'crossFound');
      found.assign(0);
      Loop({ start: 1, end: UW.CROSS_MAX_STEPS + 1, type: 'int', condition: '<' }, ({ i }) => {
        const d = startDist.add(float(i).mul(UW.CROSS_STEP)).toVar();
        If(d.greaterThanEqual(sceneDist).or(found.greaterThan(0.5)), () => { Break(); });
        const p = cam.add(rayDir.mul(d)).toVar();
        const g = G.surfaceSignedGap(p).toVar();
        If(gapPrev.mul(g).lessThanEqual(0), () => {
          /* bisection refine on [prevP, p] */
          const a = property('vec3', 'bisA'); a.assign(prevP);
          const b = property('vec3', 'bisB'); b.assign(p);
          const ga = property('float', 'bisGa'); ga.assign(gapPrev);
          Loop({ start: 0, end: UW.CROSS_REFINE_ITERS, type: 'int', condition: '<' }, () => {
            const m = a.add(b).mul(0.5).toVar();
            const gm = G.surfaceSignedGap(m).toVar();
            If(ga.mul(gm).lessThanEqual(0), () => { b.assign(m); }).Else(() => { a.assign(m); ga.assign(gm); });
          });
          const seam = smoothstep(marchReach.mul(UW.SEAM_BLEND_START), marchReach, d);
          hit.assign(mix(a.add(b).mul(0.5), hitFlat, seam));
          found.assign(1);
        });
        prevP.assign(p);
        gapPrev.assign(g);
      });
      const underEnd = select(sceneUnder, sceneWorld, cam).toVar();
      pathLen.assign(length(underEnd.sub(hit)));
      deepestY.assign(min(hit.y, underEnd.y));
      wetStartT.assign(select(rayStartsWet, float(0), length(hit.sub(cam))));
    });

    /* ---- fog integral (spec 3.7) ---- */
    const sigma = vec3(G.uExtinction).x.add(vec3(G.uExtinction).y).add(vec3(G.uExtinction).z)
      .div(3).mul(G.uFogDensity).toVar();
    const sL = sigma.mul(pathLen).toVar();
    const eL = exp(sL.negate()).toVar();
    const tMean = select(sL.greaterThan(1e-3),
      float(1).div(max(sigma, 1e-6)).sub(pathLen.mul(eL).div(max(float(1).sub(eL), 1e-6))),
      pathLen.mul(0.5)).toVar();
    const wetStart = cam.add(rayDir.mul(wetStartT)).toVar();
    const downwellY = max(wetStart.y.add(rayDir.y.mul(tMean)), deepestY).toVar();
    const surfaceRefY = select(sceneUnder, sceneSurf, camSurfY).toVar();
    const depthAtten = G.downwellingAttenuation(downwellY, surfaceRefY).toVar();
    const transmittance = exp(vec3(G.uExtinction).negate().mul(G.uFogDensity.mul(pathLen))).toVar();
    const viewDirWS = rayDir.negate();
    const fogColor = G.inscatterColor(viewDirWS, float(0)).toVar();

    const pix = screenCoordinate.xy.toVar();
    const ditherN = fract(fract(dot(pix, vec2(0.06711056, 0.00583715))).mul(52.9829189));
    const dither = ditherN.sub(0.5).div(255).toVar();

    outColor.assign(sceneColor.mul(mix(vec3(1), transmittance.mul(depthAtten), armWeight))
      .add(fogColor.mul(vec3(1).sub(transmittance)).mul(armWeight).mul(depthAtten))
      .add(vec3(dither)));

    /* ---- god rays (LargeBodyGodRays raymarch, inline; shadow term = 1 over ocean) ---- */
    const submergeFade = saturate(camSurfY.sub(cam.y).div(UW.GODRAY_SUBMERGE_FADE_METERS)).toVar();
    If(submergeFade.greaterThan(0).and(G.uGodRayDensity.greaterThan(0)), () => {
      const tExit = property('float', 'tExit');
      tExit.assign(min(sceneDist, UW.SHAFT_MAX_DISTANCE));
      If(rayDir.y.greaterThan(1e-4), () => {
        /* up-ray exit: 5-iteration bisection on the signed gap */
        If(G.surfaceSignedGap(cam.add(rayDir.mul(tExit))).greaterThan(0), () => {
          const tLo = property('float', 'tLo'); tLo.assign(0);
          const tHi = property('float', 'tHi'); tHi.assign(tExit);
          Loop({ start: 0, end: 5, type: 'int', condition: '<' }, () => {
            const tMid = tLo.add(tHi).mul(0.5).toVar();
            If(G.surfaceSignedGap(cam.add(rayDir.mul(tMid))).lessThanEqual(0),
              () => { tLo.assign(tMid); }).Else(() => { tHi.assign(tMid); });
          });
          tExit.assign(tLo.add(tHi).mul(0.5));
        });
      });
      const steps = UW.godRaySteps;
      const dt = tExit.div(steps).toVar();
      const jitter = fract(fract(dot(pix.add(G.uGodRayFrame.mul(5.588238)), vec2(0.06711056, 0.00583715))).mul(52.9829189)).toVar();
      const g = G.uGodRayAnisotropy;
      const cosSun = dot(rayDir, G.uSunDir);
      const phase = float(1).sub(g.mul(g))
        .div(pow(max(g.mul(g).add(1).sub(g.mul(cosSun).mul(2)), 1e-4), 1.5).mul(4 * Math.PI)).toVar();
      const viewFogStep = exp(vec3(G.uExtinction).negate().mul(G.uFogDensity.mul(dt))).toVar();
      const refractedSun = refract(G.uSunDir.negate(), vec3(0, 1, 0), UW.IOR_RATIO).toVar();
      const refPlaneY = camSurfY.sub(UW.CAUSTIC_REFERENCE_DEPTH).toVar();
      const accum = property('vec3', 'grAccum'); accum.assign(vec3(0));
      const viewFog = property('vec3', 'grViewFog'); viewFog.assign(vec3(1));
      const wSum = property('float', 'grWSum'); wSum.assign(0);
      Loop({ start: 0, end: steps, type: 'int', condition: '<' }, ({ i }) => {
        const t = float(i).add(jitter).mul(dt).toVar();
        const p = cam.add(rayDir.mul(t)).toVar();
        const depthFade = G.depthFadeScalar(p.y, camSurfY, G.uGodRayDepthFade).toVar();
        const depthBelow = max(camSurfY.sub(p.y), 0).toVar();
        const baseCalm = float(1).sub(saturate(depthBelow.div(UW.GODRAY_BASE_CALM_DEPTH))).toVar();
        const lod = baseCalm.mul(UW.GODRAY_BASE_CALM_LOD).add(depthBelow.mul(G.uGodRayCausticDepthSoften)).toVar();
        const caustic = causticAt(p, refractedSun, refPlaneY, lod)
          .mul(baseCalm.mul(UW.GODRAY_BASE_CALM_GAIN).add(1))
          .mul(float(1).sub(saturate(t.mul(UW.GODRAY_CAUSTIC_DISTANCE_FADE)))).toVar();
        accum.addAssign(viewFog.mul(depthFade).mul(caustic.mul(G.uGodRayCausticStrength).add(1)));
        const w = viewFog.x.add(viewFog.y).add(viewFog.z).div(3);
        wSum.addAssign(w);
        viewFog.mulAssign(viewFogStep);
      });
      accum.divAssign(max(wSum, 1e-4));
      const shafts = vec3(G.uGodRayColor).mul(vec3(G.uSunColor))
        .mul(accum).mul(G.uGodRayDensity.mul(phase)).mul(submergeFade).toVar();
      outColor.addAssign(shafts.mul(armWeight));
    });

    /* ---- waterline meniscus (spec 4.3) ---- */
    const metersPerPixel = max(fwidth(gap), 1e-5);
    const pixelsFromLine = abs(gap).div(metersPerPixel).toVar();
    const band = float(1).sub(smoothstep(float(0), max(G.uWaterlineWidthPx, 1), pixelsFromLine)).toVar();
    const lineAlpha = band.mul(G.uWaterlineStrength).toVar();
    const warpBandPx = max(G.uWaterlineWidthPx, 1).mul(6);
    const m = float(1).sub(saturate(pixelsFromLine.div(warpBandPx))).toVar();
    const offset = G.uWaterlineWarp.mul(0.06).mul(4).mul(m).mul(float(1).sub(m)).toVar();
    const upSign = select(dFdy(gap).greaterThanEqual(0), float(1), float(-1));
    const warped = sceneColorNode.sample(clamp(uvS.add(vec2(0, upSign.mul(offset))), vec2(0), vec2(1))).rgb
      .mul(mix(vec3(1), transmittance.mul(depthAtten), armWeight))
      .add(fogColor.mul(vec3(1).sub(transmittance)).mul(armWeight).mul(depthAtten)).toVar();
    const warpCoverage = smoothstep(float(0), float(0.15), m).mul(step(0.001, G.uWaterlineWarp)).toVar();
    outColor.assign(mix(outColor, warped, warpCoverage.mul(0.65)));
    outColor.assign(mix(outColor, vec3(0), lineAlpha));

    return vec4(outColor, 1);
  })();

  return { postProcessing, scenePass };
}

/* =====================================================================================
   SECTION 14 — Sky dome + sun driving (analytic sunset; sun colour/ambient CPU-mirrored)
   ===================================================================================== */

function createSkyDome(G) {
  const geo = new THREE.SphereGeometry(14000, 48, 24);
  const mat = new THREE.MeshBasicNodeMaterial({ side: THREE.BackSide });
  mat.colorNode = Fn(() => {
    const dir = normalize(positionLocal).toVar();
    return vec4(G.skyWithSun(dir, float(0.015)), 1);
  })();
  const mesh = new THREE.Mesh(geo, mat);
  mesh.frustumCulled = false;
  return mesh;
}

function updateSun(G) {
  const elev = sunState.elevationDeg * Math.PI / 180;
  const azim = sunState.azimuthDeg * Math.PI / 180;
  const sunY = Math.sin(elev);
  const cosE = Math.cos(elev);
  G.uSunDir.value.set(cosE * Math.cos(azim), sunY, cosE * Math.sin(azim));

  /* sunlight transmittance through the atmosphere (CPU mirror of the sky palette) */
  const betaSun = [0.12, 0.26, 0.55];
  const amS = 1 / Math.max(sunY + 0.028, 0.028);
  const tint = betaSun.map((b) => Math.exp(-b * (amS - 1)));
  const disk = clamp01((sunY + 0.09) * 9);                     // dims once fully set
  G.uSunColor.value.setRGB(
    tint[0] * sunState.intensity * disk,
    tint[1] * sunState.intensity * disk,
    tint[2] * sunState.intensity * disk);
  G.uSunTint.value.setRGB(tint[0] * disk, tint[1] * disk, tint[2] * disk);

  const day = clamp01((sunY + 0.06) * 4);
  const zen = [0.055, 0.14, 0.32].map((v) => v * (0.12 + 0.88 * day));
  const warmth = clamp01(1 - sunY * 2.6);
  const dayHorizon = [0.5, 0.65, 0.85], duskHorizon = [1.05, 0.42, 0.15];
  const hor = dayHorizon.map((v, i) => uLerp(v, duskHorizon[i], warmth) * (0.08 + 0.92 * day));
  G.uZenithColor.value.setRGB(...zen);
  G.uHorizonColor.value.setRGB(...hor);
  G.uGlowColor.value.setRGB(tint[0] * 1.15 * day, tint[1] * 0.7 * day, tint[2] * 0.45 * day);
  G.uAmbient.value.setRGB(
    zen[0] * 0.85 + hor[0] * 0.25 + 0.004,
    zen[1] * 0.85 + hor[1] * 0.25 + 0.005,
    zen[2] * 0.85 + hor[2] * 0.25 + 0.008);
}

/* =====================================================================================
   SECTION 15 — Fly camera (FlyCamera.cs: 6 m/s, Shift x3, 0.1 deg/px right-drag,
   pitch clamp +-89.99, world-up Q/E, no smoothing, unscaled dt)
   ===================================================================================== */

class FlyCamera {
  constructor(camera, domElement) {
    this.camera = camera;
    this.dom = domElement;
    this.moveSpeed = 6;
    this.boostMultiplier = 3;
    this.lookSensitivity = 0.1;              // degrees per pixel
    this.keys = new Set();
    this.looking = false;
    const e = camera.rotation.clone().reorder('YXZ');
    this.yaw = -THREE.MathUtils.radToDeg(e.y);
    this.pitch = THREE.MathUtils.radToDeg(e.x);

    domElement.addEventListener('contextmenu', (ev) => ev.preventDefault());
    domElement.addEventListener('pointerdown', (ev) => {
      if (ev.button === 2) { this.looking = true; domElement.setPointerCapture(ev.pointerId); }
    });
    domElement.addEventListener('pointerup', (ev) => {
      if (ev.button === 2) this.looking = false;
    });
    domElement.addEventListener('pointermove', (ev) => {
      if (!this.looking) return;
      this.yaw += ev.movementX * this.lookSensitivity;
      this.pitch = Math.min(Math.max(this.pitch - ev.movementY * this.lookSensitivity, -89.99), 89.99);
    });
    window.addEventListener('keydown', (ev) => { this.keys.add(ev.code); });
    window.addEventListener('keyup', (ev) => { this.keys.delete(ev.code); });
    window.addEventListener('blur', () => this.keys.clear());
  }
  update(dt) {
    const cam = this.camera;
    cam.rotation.set(THREE.MathUtils.degToRad(this.pitch), THREE.MathUtils.degToRad(-this.yaw), 0, 'YXZ');
    const k = this.keys;
    const mx = (k.has('KeyD') ? 1 : 0) + (k.has('KeyA') ? -1 : 0);
    const my = (k.has('KeyE') ? 1 : 0) + (k.has('KeyQ') ? -1 : 0);
    const mz = (k.has('KeyW') ? 1 : 0) + (k.has('KeyS') ? -1 : 0);
    const fwd = new THREE.Vector3();
    cam.getWorldDirection(fwd);
    const right = new THREE.Vector3().crossVectors(fwd, cam.up).normalize();
    const move = new THREE.Vector3()
      .addScaledVector(right, mx)
      .addScaledVector(new THREE.Vector3(0, 1, 0), my)
      .addScaledVector(fwd, mz);
    if (move.lengthSq() > 1) move.normalize();               // clampMagnitude(1)
    const speed = this.moveSpeed * (k.has('ShiftLeft') || k.has('ShiftRight') ? this.boostMultiplier : 1);
    cam.position.addScaledVector(move, speed * dt);
  }
}

/* =====================================================================================
   SECTION 16 — Bootstrap: renderer, island bake, textures, scene, GUI, main loop
   ===================================================================================== */

function loadTextureAsync(url, { srgb = false, repeat = true } = {}) {
  return new Promise((resolve, reject) => {
    new THREE.TextureLoader().load(url, (t) => {
      t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
      if (repeat) t.wrapS = t.wrapT = THREE.RepeatWrapping;
      t.minFilter = THREE.LinearMipmapLinearFilter;
      t.magFilter = THREE.LinearFilter;
      t.anisotropy = 4;
      resolve(t);
    }, undefined, reject);
  });
}

function buildFieldTextures(island, shore) {
  const fr = shore.fieldRes, n = fr * fr;
  const depthTex = makeHalfTexture(fr, (d) => {
    for (let i = 0; i < n; i++) d[i * 4] = toHalf(shore.depth[i]);
  });
  const sdfTex = makeHalfTexture(fr, (d) => {
    for (let i = 0; i < n; i++) {
      d[i * 4 + 0] = toHalf(shore.dirX[i] * 0.5 + 0.5);
      d[i * 4 + 1] = toHalf(shore.dirZ[i] * 0.5 + 0.5);
      d[i * 4 + 2] = toHalf(shore.dist[i]);
      d[i * 4 + 3] = toHalf(shore.slopeTan[i]);
    }
  });
  /* terrain field: (nx, nz, height m, beach zone) at shore-field resolution */
  const res = island.res, width = WORLD.terrainWidth, hM = WORLD.terrainHeight;
  const cell = width / fr;
  const hAtF = (x, z) => {
    const cx = Math.min(Math.max(x, 0), fr - 1), cz = Math.min(Math.max(z, 0), fr - 1);
    return WORLD.waterLevel - shore.depth[cz * fr + cx];       // terrain height (m)
  };
  const islandSample = (fx, fz, arr) => {
    const x = Math.min(Math.max(Math.round(fx * (res - 1) / (fr - 1)), 0), res - 1);
    const z = Math.min(Math.max(Math.round(fz * (res - 1) / (fr - 1)), 0), res - 1);
    return arr[z * res + x];
  };
  const terrainTex = makeHalfTexture(fr, (d) => {
    for (let z = 0; z < fr; z++) for (let x = 0; x < fr; x++) {
      const i = z * fr + x;
      const dhx = (hAtF(x + 1, z) - hAtF(x - 1, z)) / (2 * cell);
      const dhz = (hAtF(x, z + 1) - hAtF(x, z - 1)) / (2 * cell);
      const inv = 1 / Math.sqrt(dhx * dhx + dhz * dhz + 1);
      d[i * 4 + 0] = toHalf(-dhx * inv);
      d[i * 4 + 1] = toHalf(-dhz * inv);
      d[i * 4 + 2] = toHalf(hAtF(x, z));
      d[i * 4 + 3] = toHalf(islandSample(x, z, island.beachZone));
    }
  });
  const controlData = new Uint8Array(n * 4);
  for (let z = 0; z < fr; z++) for (let x = 0; x < fr; x++) {
    const i = z * fr + x;
    controlData[i * 4 + 0] = Math.trunc(clamp01(islandSample(x, z, island.seabed)) * 255);
    controlData[i * 4 + 1] = Math.trunc(clamp01(islandSample(x, z, island.beachCoverage)) * 255);
    controlData[i * 4 + 2] = Math.trunc(clamp01(islandSample(x, z, island.rock)) * 255);
    controlData[i * 4 + 3] = Math.trunc(clamp01(islandSample(x, z, island.grass)) * 255);
  }
  const controlTex = new THREE.DataTexture(controlData, fr, fr, THREE.RGBAFormat, THREE.UnsignedByteType);
  controlTex.minFilter = controlTex.magFilter = THREE.LinearFilter;
  controlTex.colorSpace = THREE.NoColorSpace;
  controlTex.needsUpdate = true;
  return { depthTex, sdfTex, terrainTex, controlTex, half: shore.half };
}

function pickSpawn(island, shore) {
  /* the sandiest windward cell with 2-6 m of water just offshore */
  const fr = shore.fieldRes, res = island.res;
  let best = null, bestScore = -1;
  for (let z = 8; z < fr - 8; z += 2) for (let x = 8; x < fr - 8; x += 2) {
    const i = z * fr + x;
    if (shore.dist[i] > -8 || shore.dist[i] < -60) continue;    // 8..60 m offshore
    if (shore.depth[i] < 1.5 || shore.depth[i] > 7) continue;
    const ix = Math.round(x * (res - 1) / (fr - 1)), iz = Math.round(z * (res - 1) / (fr - 1));
    const sand = island.beachCoverage[iz * res + ix];
    if (sand > bestScore) {
      bestScore = sand;
      best = { x: (x + 0.5) / fr * WORLD.terrainWidth - WORLD.terrainWidth / 2,
               z: (z + 0.5) / fr * WORLD.terrainWidth - WORLD.terrainWidth / 2,
               toShore: [shore.dirX[i], shore.dirZ[i]] };
    }
  }
  if (!best) best = { x: 0, z: -WORLD.terrainWidth * 0.4, toShore: [0, 1] };
  return best;
}

async function main() {
  if (!navigator.gpu) fatal('WebGPU is not available in this browser.\nUse Chrome/Edge 113+, Safari 26+, or recent Firefox — and make sure hardware acceleration is enabled.');
  setStage('creating WebGPU renderer', 0.02);

  const renderer = new THREE.WebGPURenderer({ antialias: true });
  await renderer.init();
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.setSize(window.innerWidth, window.innerHeight);
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.0;
  document.getElementById('app').appendChild(renderer.domElement);

  setStage('loading textures', 0.05);
  const [whitecapTex, detailNormalTex] = await Promise.all([
    loadTextureAsync(ASSET_WHITECAP),
    loadTextureAsync(ASSET_DETAIL_NORMAL),
  ]);

  /* island — seed/resolution overridable via #seed=...&res=... */
  const hash = new URLSearchParams(location.hash.slice(1));
  const preset = JSON.parse(JSON.stringify(ISLAND_PRESET));
  if (hash.get('seed')) {
    const s = parseInt(hash.get('seed'), 10) | 0;
    preset.seeds = { terrain: s, mountain: s + 11, voronoi: s + 22, warp: s + 33,
                     continental: s + 44, erosion: s + 55, beach: s + 66, detail: s + 77 };
  }
  if (hash.get('res')) preset.resolution = parseInt(hash.get('res'), 10) === 1025 ? 1025 : 513;

  const island = await generateIsland(preset, (label, f) => setStage(label, 0.06 + f * 0.55));
  setStage('shore fields', 0.66); await nextFrame();
  const shore = buildShoreFields(island, WORLD.terrainWidth, WORLD.terrainHeight, WORLD.waterLevel, WORLD.shoreFieldRes);
  const fieldTex = buildFieldTextures(island, shore);

  /* aim the sea at the chosen beach */
  const spawn = pickSpawn(island, shore);
  seaState.windFromDegrees = Math.atan2(spawn.toShore[1], spawn.toShore[0]) * 180 / Math.PI;

  setStage('building spectrum + kernels', 0.72); await nextFrame();
  const fft = createOceanFFT();
  const G = createShared(fft, fieldTex);
  extendSharedVisual(G, { whitecapTex, detailNormalTex });

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 30000);
  camera.position.set(spawn.x - spawn.toShore[0] * 90, WORLD.waterLevel + 11, spawn.z - spawn.toShore[1] * 90);
  camera.lookAt(spawn.x + spawn.toShore[0] * 60, WORLD.waterLevel + 4, spawn.z + spawn.toShore[1] * 60);

  const caustics = createCausticPass(G, renderer);
  const terrainMesh = createTerrain(G, island, { ...fieldTex, whitecapTex });
  scene.add(terrainMesh);
  const water = createWaterSurface(G);
  scene.add(water.mesh);
  scene.add(createSkyDome(G));
  updateSun(G);

  setStage('compiling shaders', 0.85); await nextFrame();
  const post = createPost(G, renderer, scene, camera);
  const flycam = new FlyCamera(camera, renderer.domElement);

  window.addEventListener('resize', () => {
    camera.aspect = window.innerWidth / window.innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(window.innerWidth, window.innerHeight);
  });

  buildGUI(G, fft);

  /* ---- main loop ---- */
  let waveTime = 60;                                          // pre-aged sea
  let frame = 0;
  const clock = new THREE.Clock();
  const snap = (v, s) => Math.round(v / s) * s;

  renderer.setAnimationLoop(() => {
    const dt = Math.min(clock.getDelta(), 0.1);
    waveTime += dt;
    flycam.update(dt);
    camera.updateMatrixWorld();

    G.uCamPos.value.copy(camera.position);
    G.uProjInv.value.copy(camera.projectionMatrixInverse);
    G.uCamWorld.value.copy(camera.matrixWorld);
    camera.getWorldDirection(G.uCamFwd.value);
    G.uCamNear.value = camera.near;
    G.uSurfaceBand.value = G.surfaceHeightBandCPU();
    G.uOffshoreHs.value = offshoreSignificantHeight();
    G.uShoalDepth.value = Math.max(surfState.shoalDepth, 2 * offshoreSignificantHeight());
    G.uGreenBandDepth.value = surfState.shoalDepth;

    const period = Math.max(surfState.period, SURF.MIN_PERIOD);
    G.uSurfBeatTime.value = waveTime % (period * SURF.BEAT_WRAP_FRONTS);
    G.uCausticTime.value = waveTime * uwState.causticTimeScale;
    G.uGodRayFrame.value = frame & 1023;

    /* water sheet + caustic window follow the camera on snapped lattices */
    const ox = snap(camera.position.x, 2), oz = snap(camera.position.z, 2);
    G.uMeshOffset.value.set(ox, oz);
    water.mesh.position.set(ox, 0, oz);
    G.uSimCenter.value.set(snap(camera.position.x, 4), WORLD.waterLevel, snap(camera.position.z, 4));

    fft.dispatch(renderer, waveTime);
    caustics.render();
    post.postProcessing.render();
    frame++;
  });

  overlay.classList.add('hidden');
  setStage('done', 1);
}

/* =====================================================================================
   SECTION 17 — GUI
   ===================================================================================== */

function buildGUI(G, fft) {
  const gui = new GUI({ title: 'WebGPU Ocean' });
  const dirty = () => fft.markDirty();

  const sea = gui.addFolder('Sea state');
  sea.add(seaState, 'significantWaveHeight', 0.1, 8, 0.1).name('Hs (m)').onChange(dirty);
  sea.add(seaState, 'peakWavelength', 10, 250, 1).name('peak wavelength').onChange(dirty);
  sea.add(seaState, 'peakSharpness', 1, 7, 0.1).name('gamma (JONSWAP)').onChange(dirty);
  sea.add(seaState, 'windTurbulence', 0, 1, 0.01).onChange(dirty);
  sea.add(seaState, 'choppiness', 0, 2, 0.05).onChange(dirty);
  sea.add(seaState, 'swellHeight', 0, 3, 0.05).onChange(dirty);
  sea.add(seaState, 'swellWavelength', 20, 400, 1).onChange(dirty);
  sea.add(seaState, 'windSpeed', 0, 20, 0.1).onChange(dirty);
  sea.add(seaState, 'windFromDegrees', -180, 180, 1).name('wind heading').onChange(dirty);
  sea.add(seaState, 'amplitude', 0, 2, 0.05).name('master amplitude').onChange(dirty);

  const foam = gui.addFolder('Whitecaps');
  foam.add(seaState, 'foamCoverage', 0, 2, 0.01).onChange(dirty);
  foam.add(seaState, 'foamStrength', 0, 4, 0.05).onChange(dirty);
  foam.add(seaState, 'foamFadeRate', 0.05, 4, 0.05).onChange(dirty);
  foam.add(seaState, 'foamDrift', 0, 0.3, 0.01).onChange(dirty);
  foam.add(seaState, 'foamFaceBias', 0, 1, 0.01).onChange(dirty);
  foam.add(lookState, 'oceanFoamFeather', 0.05, 1, 0.01).name('feather')
    .onChange((v) => { G.uFoamFeather.value = v; });
  foam.add(lookState, 'oceanFoamTextureInfluence', 0, 1, 0.01).name('texture influence')
    .onChange((v) => { G.uFoamTexInfluence.value = v; });
  foam.close();

  const surf = gui.addFolder('Shore surf');
  surf.add(surfState, 'enabled').onChange((v) => { G.uSurfActive.value = v ? 1 : 0; });
  surf.add(surfState, 'amplitude', 0, 3, 0.05).onChange((v) => { G.uSurfAmplitude.value = v; });
  surf.add(surfState, 'wavelength', 10, 120, 1).onChange((v) => { G.uSurfWavelength.value = v; });
  surf.add(surfState, 'period', 4, 16, 0.5).onChange((v) => { G.uSurfPeriod.value = v; });
  surf.add(surfState, 'bandDepth', 2, 25, 0.5).onChange((v) => { G.uSurfBandDepth.value = v; });
  surf.add(surfState, 'setStrength', 0, 1, 0.01).onChange((v) => { G.uSurfSetStrength.value = v; });
  surf.add(surfState, 'lean', 0, 1, 0.01).onChange((v) => { G.uSurfLean.value = v; });
  surf.add(surfState, 'greens', 1, 3, 0.05).onChange((v) => { G.uSurfGreens.value = v; });
  surf.add(surfState, 'swashAmplitude', 0, 2, 0.05).onChange((v) => { G.uSurfSwashAmplitude.value = v; });
  surf.add(surfState, 'foamStrength', 0, 3, 0.05).name('whitewash strength').onChange((v) => { G.uSurfFoamStrength.value = v; });
  surf.add(surfState, 'swashFoam', 0, 2, 0.05).onChange((v) => { G.uSurfSwashFoam.value = v; });
  surf.add(surfState, 'waterlineFoam', 0, 2, 0.05).onChange((v) => { G.uSurfWaterlineFoam.value = v; });
  surf.close();

  const waterF = gui.addFolder('Water & fog');
  waterF.add(uwState, 'jerlovType', Object.keys(JERLOV)).name('Jerlov type').onChange((v) => {
    const j = JERLOV[v];
    G.uExtinction.value.set(...j.ext);
    G.uScatterColor.value.setRGB(...j.body);
  });
  waterF.add(lookState, 'fogDensity', 0.02, 2, 0.01).onChange((v) => { G.uFogDensity.value = v; });
  waterF.add(lookState, 'waterOpacity', 0, 1, 0.01).onChange((v) => { G.uOpacity.value = v; });
  waterF.add(lookState, 'scatterIntensity', 0, 0.5, 0.005).onChange((v) => { G.uScatterIntensity.value = v; });
  waterF.add(lookState, 'reflectionStrength', 0, 1, 0.01).onChange((v) => { G.uReflectionStrength.value = v; });
  waterF.add(lookState, 'horizonHazeDensity', 0, 2, 0.01).onChange((v) => { G.uHorizonHazeDensity.value = v; });
  waterF.close();

  const sun = gui.addFolder('Sun');
  sun.add(sunState, 'elevationDeg', -3, 60, 0.5).name('elevation').onChange(() => updateSun(G));
  sun.add(sunState, 'azimuthDeg', 0, 360, 1).name('azimuth').onChange(() => updateSun(G));
  sun.add(sunState, 'intensity', 0.2, 6, 0.1).onChange(() => updateSun(G));

  const uw = gui.addFolder('Underwater');
  uw.add(uwState, 'godRayDensity', 0, 1, 0.01).name('god rays').onChange((v) => { G.uGodRayDensity.value = v; });
  uw.add(uwState, 'godRayCausticStrength', 0, 8, 0.1).onChange((v) => { G.uGodRayCausticStrength.value = v; });
  uw.add(uwState, 'causticStrength', 0, 8, 0.1).name('seabed caustics').onChange((v) => { G.uCausticStrength.value = v; });
  uw.add(uwState, 'causticRippleStrength', 0, 2, 0.05).onChange((v) => { G.uCausticRippleStrength.value = v; });
  uw.add(uwState, 'waterlineStrength', 0, 1, 0.02).name('meniscus').onChange((v) => { G.uWaterlineStrength.value = v; });
  uw.close();

  const island = gui.addFolder('Island');
  const regen = {
    seed: 8675309,
    fullRes: false,
    regenerate() {
      location.hash = `seed=${regen.seed}&res=${regen.fullRes ? 1025 : 513}`;
      location.reload();
    },
  };
  island.add(regen, 'seed', 1, 99999999, 1);
  island.add(regen, 'fullRes').name('full 1025 res (slow)');
  island.add(regen, 'regenerate').name('regenerate island');
  island.close();
}

if (!globalThis.__OCEAN_NO_AUTORUN__) {
  main().catch((e) => {
    console.error(e);
    fatal(String(e && e.stack ? e.stack : e));
  });
}

/* exports for headless tests of the CPU-side math (harmless in the browser) */
export { generateIsland, buildShoreFields, deriveCascades, computeGains, buildH0,
         buildButterfly, ISLAND_PRESET, WORLD, seaState, wangHash, DotNetRandom, PerlinNoise };
