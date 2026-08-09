# WebGPU Ocean — standalone Three.js demo

A single-file, no-Unity port of the math inside `AbstractOcclusion.WebGpuWater` +
`island generator`, running on Three.js `WebGPURenderer` + TSL (r185).

## Run

Open `index.html` in a WebGPU browser (Chrome/Edge 113+, Safari 26+). It is fully
self-contained (textures inlined); only Three.js itself comes from the jsdelivr CDN.
`main.js` + the dev `index.html` are the readable, non-inlined source of the same build.

Controls: **WASD** move, **Q/E** down/up, hold **right mouse** to look, **Shift** boost
(FlyCamera.cs feel: 6 m/s, ×3 boost, 0.1°/px, no smoothing). Dive under the surface.

## What is ported 1:1 (constants + equations mirrored from the Unity sources)

- **Spectral FFT ocean** — `OceanFft.compute` / `WaterOceanSpectrum.cs`: 4 cascades at 128²,
  1/φ³ band ratio, JONSWAP×TMA spectrum with one-sided cos² spreading + pedestal 0.5287611,
  swell ring, Wang-hash/Box-Muller seeding (seed 1337), CPU Hs-normalised gains
  (RealPartVarianceShare 0.5), DIT butterfly FFT (positive-sin twiddles, (−1)^(x+y) shift),
  eigenvalue-anisotropy Jacobian fold, temporal whitecap accumulator with downwind advection,
  crest/face gates and split decay.
- **Surface shading** — `WaterSurfaceFragStages/Specular/DetailNormal/FoamSampling`:
  cascade fades 1−f³ with per-cascade far-slope floors, distance-mip LOD, φ²-ladder detail
  normals with hex stochastic tiling, Schlick fresnel (preset 3.3/floor 0.25), 5-tap aniso sky
  smear, dual-lobe GGX sun, Jerlov Ocean I in-scatter/extinction, crest-fold SSS, whitecap
  dissolve law (threshold 1−√c, contrast 1.6→1.0, pattern mean 0.501/σ0.189), horizon haze.
- **Shore & surf** — `WaterShoreMath/WaterSurfWaves/WaterShoreDepthField`: still-water depth
  field + jump-flood shoreline SDF + smoothed tanβ, ShoalWeight/Green's-law/warp compression,
  5-front sech² wave train with set-wave modulation and crest segmentation, Iribarren
  spill/plunge/surge weights, Weggel breaker cap, DDD bore, Hunt swash run-up with two-front
  wet line, whitewash/bore/trail/swash foam envelopes, vertex swash film (SmoothMin/Max).
- **Underwater** — `WaterFog/WaterUnderwaterFog/WaterWaterline/LargeBodyGodRays/LargeBodyCaustics`:
  Jerlov tables verbatim, absorb+inscatter fog with transmittance-weighted mean-depth
  downwelling, per-pixel waterline coverage mask (6 px feather) from the near-plane gap,
  1.5 m × 24-step marched surface crossing with 12-iter bisection, meniscus band with
  lens-tension warp, 24-step jittered god-ray march with HG phase and the analytic caustic
  window, refracted-grid caustic map with per-vertex focusing Jacobian (9-wave golden-angle
  ripple field).
- **Island** — `island generator` (ShelteredSandyArchipelago preset): .NET-random-faithful
  Perlin/FBM/ridged/warp base shape, falloff, hydraulic + thermal erosion, Felzenszwalb EDT
  shoreline distance, coastal morphology suitability, Dean-profile beach shaper with offshore
  bar, priority-flood depression fill, substrate coverage masks; IslandTerrain substrate
  tints/wet-band constants.

## Deliberate substitutions (no Unity scene infra here)

planar/SSR reflection → analytic sunset sky (same aniso/mip law); screen-space refraction →
bed-terrain sample fogged over the true refracted span (the porting note in the source);
god-ray shadowmap term → 1 (open ocean; caustics carry the beams); no temporal reprojection;
clipmap → camera-following exponential polar sheet (the wave field is still a pure function
of world XZ, so geometry never swims the waves).

Default island bakes at 513² for startup speed (~2 s); the erosion auto-rescales exactly as
`ResolutionScaling.cs` does. Use the GUI → Island → "full 1025 res" for the preset-exact bake.
