# PROMPT — Underwater Fog (Full tier) perf, round 3

Project: `ThreeJSWaterPort` — package `Packages/com.abstractocclusion.webgpuwater`.
Scene: `boatest.unity` (ocean clipmap primary, 1 boat active, hull DryInterior exclusion volume,
3 underwater point lights, God Rays on High/64 steps, caustic 1024 @ interval 1, readback interval 1).
Editor profiling under DX11, target WebGPU.

## Problem history (rounds 1–2, DONE — do not redo)

Symptom: quality-tier `UnderwaterMode.Full` fog caused frame-time spikes at roughly the swell
period; `Simple` ran ~200 fps. Diagnosis: spikes surface as `Semaphore.WaitForSignal`
(median ~3 ms, max 43 ms = render-thread/GPU bound; GPU module was off in the first captures).

Root causes found in `WaterUnderwaterFog.shader` / `WaterUnderwaterFogPass.cs`:

1. The SurfaceDepth prepass drew ~20 displaced ocean sheets into a **camera-sized** R32F +
   Depth32 every armed frame (+ mid-frame RT switch, extra costly on WebGPU).
2. The 40-step × ~6-fetch crossing march is compiled into BOTH fullscreen passes (absorb +
   inscatter) → register pressure sizes every Full-tier pixel; it actually RUNS on the
   "mixed ray with no prepass sample" set (near-clip strip on partial-submersion frames, carve
   holes, silhouettes) — a set that grows with wave phase → the periodic spike.
3. Both fullscreen passes evaluated `SurfaceHeightAtXZ(cam.xz)` (~6 fetches) per pixel for a
   per-frame constant, and evaluated the scene-side height before the prepass branch even when
   the prepass owned the pixel.

Changes shipped (all in the two files above, comments dated 2026-08-03):

- Prepass renders at **0.5× resolution** (`PrepassResolutionScale`, published as
  `_OceanSurfacePrepassScale`; all pixel LOADs scale + clamp through it). Revert knob: set the
  const to `1f`.
- `UNDERWATER_CROSS_MAX_STEPS` 40 → 24 (same 1.5 m step, reach 60→36 m past band entry).
  Revert if the wavy→flat handover line shows on grazing up-looks near shore surf.
- `camSurf` in `OceanWavyPath` / `OceanPrepassPath` = published `_UnderwaterSurfaceY`
  (it only feeds surfaceRefY/early-out refs, never the crossing).
- `sceneSurf`/`sceneUnder` in `OceanPrepassPath` deferred to the analytic-authority section
  (its only consumer).

Result: clear improvement; residual spikes ≤ ~16 ms and believed editor-related.
NOTE: after ANY fog-shader edit, first play looks WORSE (variant compilation) — restart the
editor before judging.

## Next move A (the big one): fog span-RT refactor

Goal: compute the expensive part of `UnderwaterFog()` ONCE per frame instead of twice, and get
the march (and point-light loop) out of the fullscreen composites' register allocation.

Sketch:
- New raster pass "WaterFogSpan" before the absorb/inscatter pair (same gates), fullscreen or
  half-res, writing packed MRT(s), e.g.:
  - RT0 (RGBA16F): per-channel path transmittance .rgb + armWeight .a
  - RT1 (RGBA16F): depthAttenuation .rgb + sunVisibility .a
  - RT2 (RG16F): tStart (= distance(cam, wetStart)) + wetSpanLen
- Absorb pass becomes: read RT0 → output for `Blend Zero SrcColor`.
- Inscatter becomes: read RTs → colour math + `WaterSceneLightsInscatter` (needs only
  tStart/wetSpanLen/dir) → `Blend One One`.
- Keep the two-pass hardware-blend architecture (RecordFogPass's header explains why the
  attachment can't be self-read; the span RT sidesteps it).
- The march + `SurfaceHeightAtXZ` machinery then live ONLY in the span pass → composites get
  tiny register footprints. Consider `WATER_FOG_SIMPLE` keeping its current direct path.
- Mind the debug views (`WaterFogDebug.hlsl` branch stamps) — either evaluate views in the span
  pass and pass the colour through, or gate the refactor off while `_WaterDebugMode` is active.
- If half-res span: bilinear upsample is wrong across the waterline edge — use armWeight/sign
  aware upsample or keep full-res first, measure, then try half.

Verify after: partial-submersion crossing (no unfogged band pop), inside the boat's dry
interior (carve still carved, fog through windows correct), shore surf grazing up-looks,
meniscus pass, fog debug views 10/12/13, Simple tier unchanged, pond/bounded body regression.

## Next move B: residual periodic spikes (fog-independent)

Seen even on Simple above water. With GPU Usage module open, click ONE spike frame → note which
category (Opaque/Shadows/PostProcess/Other) owns it, then Hierarchy on that frame.
Cheap knob tests (quality asset, no code): `Caustic Interval` 1→2, `Readback Interval` 1→2,
`Ocean Fft Interval` 1→2. Also test a standalone build — `EditorLoop` was 36% of some frames,
and the 400 ms outliers were editor-only.

## Explicitly rejected (don't redo)

- Culling far clipmap levels from the prepass renderer list: template is only 64×64 (~4k verts,
  ~26 draws total — vertex cost is small) and culled horizon pixels would fall onto the
  EXPENSIVE march path. Net loss.
- Lowering `UNDERWATER_CROSS_REFINE_ITERS` below 12: documented visible seam vs the exclusion
  wall's exact waterline (round-1 post-mortem rule in the shader).
- Dual-source blending to merge absorb+inscatter: optional feature on WebGPU — not safe for an
  asset-store package.
