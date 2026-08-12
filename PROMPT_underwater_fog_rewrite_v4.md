# PROMPT — Underwater Fog rewrite (round 4)

Project: `ThreeJSWaterPort` — package `Packages/com.abstractocclusion.webgpuwater`.
Scene of record: `Samples/Demos/Scenes/12. Ocean Demo.unity` + `Samples/Demos/Common/HighWaterQuality.asset`
(ForceHigh: `highUnderwaterFog: 2` = Full, godRaySteps 64, caustic 1024 @ interval 1, readback 1,
fftInterval 1, foamCap 65536). Ocean: `significantWaveHeight: 15`, `largeWaveAmplitude: 1`,
`cascadeReach: 4`, **`oceanAperiodicEnabled: 1`**, `useScreenSpaceReflection: 0`,
`usePlanarReflection: 1` (interval 5), `largeGodRayDensity: 0`, `useBedDepth: 0` (→ `WATER_STRIP_SHORE` on),
`seaStateFetchEnabled: 0`, `meniscus: 1`, `meniscusWarp: 0`. Editor DX11, target WebGPU.

**Supersedes `PROMPT_fog_perf_round3.md`.** Round 3's "Next move A" (the span-RT refactor) is still
the correct move and is still not done. Everything in round 3's *Explicitly rejected* section still
holds — read it before proposing anything.

---

## 1. The problem, in one paragraph

`UnderwaterFog()` is evaluated **three times per pixel per frame** at the waterline: once by the
absorb draw, once by the inscatter draw (`WaterUnderwaterFogPass.cs:453-457` — two `DrawFullScreen`
calls of the same fragment with identical inputs), and a third equivalent solve by the meniscus pass
(`WaterUnderwaterWaterline.shader`, same near-plane point, same classification). Inside each, the
waterline classification runs the analytic ocean field **three times** (the chop-inversion fixed
point, `WaterWaterline.hlsl` `SurfaceHeightAtXZChopInvertedVertical`, `[loop] i < 3`). On an
aperiodic ocean one field evaluation is 4 cascades × 15 source reads. Nothing about that is a bug —
each consumer is individually reasonable. The cost is structural: **one quantity, computed nine
times.** No amount of local optimisation fixes it; the shared value has to be computed once and
consumed.

---

## 2. Measured facts (do not re-derive)

- User GPU capture, `docs/PLAN_fog_band_and_spikes_v1.md:8`:
  `WaterUnderwaterFog` **6.546 ms for 2 draws = 45% of camera render**; `.SurfaceDepth` prepass
  **0.623 ms**. A 10.5× ratio — fullscreen fragment bound, prepass is not the problem.
  ⚠️ That capture is dated **2026-08-03, before F3 (2026-08-10)**. It measured a march that still
  called the analytic field per step, and a frame with **no height-RT pass at all**. Re-capture
  before sizing anything, and record `WaterUnderwaterFog.HeightRT` this time.
- Cost per field evaluation (`OceanFftDisplacementShore`, `WaterLargeWaves.hlsl`):
  **periodic 4 source reads; aperiodic 60** (3 taps + 3×4 direction-map reads, per cascade, ×4).
- Classification = 3 field evaluations. Fog fragment ≈ 3 evals + segment + downwell.
  Screen total at the waterline ≈ 3 fullscreen passes × 3 evals.
- The **march is not the bottleneck**: `UNDERWATER_CROSS_MAX_STEPS 16` + `REFINE_ITERS 8`, and since
  F3 every sample is one `tex2Dlod` of the 256² `_WaterHeightRT` (`SurfaceSignedGapRT`). 26 cheap
  taps, on the minority of pixels with no prepass sample. Comments claiming "~290 fetches, the single
  largest cost" were corrected on 2026-08-11 — if you find another, it is stale.

---

## 3. Already shipped — DO NOT REDO (2026-08-11/12, all compiling)

| change | where |
|---|---|
| `SurfaceHeightAtXZChopInvertedVertical` + `SurfaceSignedGapChopInvertedPair` — the inversion's first iteration IS the vertical read, so both gaps come from ONE solve (4 field evals → 3). Old names kept as wrappers. | `WaterWaterline.hlsl` |
| `WaterlineFarFromSurface` — one height-RT tap replaces the whole classification when the **camera** is >4 m from its local surface (`WATERLINE_RT_SKIP_MARGIN_METERS`). Test reads the CAMERA, not the pixel, so it is uniform and the `ddx/ddy` below stays defined. **Measured gain above the surface; nothing at/below it, by design.** | `WaterWaterline.hlsl`, both fog shaders |
| `armWeight <= 0` early-out after the classification (keeps the long path when a fog debug view is selected) | `WaterUnderwaterFog.shader` |
| Downwelling `lerp(analytic, rt, w)` → 3-branch; the `w >= 1` lane skips the analytic arm entirely (`lerp` evaluates both arms) | `WaterUnderwaterFog.shader` |
| `ShoreShoalDepth` gains the missing `WATER_STRIP_SHORE` guard | `WaterShore.hlsl` |
| `OceanAperiodicDirectionMapBilinear`: hardware bilinear behind `WATER_APERIODIC_MAP_SAMPLER`, Load fallback default | `WaterLargeWaves.hlsl` |
| `MaxSolverStepsPerFrame` 8 → 3 (a slow frame owed more debt → 4× sim compute → kept frames slow) | `WaterVolume.State.cs` |
| `WATER_UNDERSIDE_FOAM` gated on `cameraUnderwater \|\| cameraDryVolume` instead of `fogArmed` | `WaterUniformPublisher.cs` |

---

## 4. DEAD ENDS — tried, failed, do not retry

1. **Dual-source blending to merge absorb+inscatter.** `Blend One Src1Color` →
   `Parse error: syntax error, unexpected TVAL_ID, expecting TVAL_VARREF or TVAL_BMODE`.
   ShaderLab on this Unity does not accept `Src1Color` at all. Round 3 had *already* rejected it on
   the separate grounds that it is optional on WebGPU. **Two independent reasons. Closed.**
2. **`#define WATER_DISABLE_OCEAN_APERIODIC` in the fog/meniscus** (periodic-only classification,
   as `LargeBodyCaustics.shader:38` does). Compiles, big win, **desyncs the fog transition from the
   visible under/above-water boundary**. The reasoning that `OceanRenderedCoverage` multiplies the
   analytic term by `(1 - ownership.g)` and therefore owns every rasterised pixel is *incomplete*:
   the analytic surface also feeds the wet/dry ray decision and the segment solve, and the CPU's own
   crossing gates come from the aperiodic field. Only retry as part of moving **every** consumer onto
   one field.
3. **A new `SamplerState` in any header `WaterSurface.shader` includes.**
   `maximum ps_4_0 sampler register index (16) exceeded`. That pass documents itself as exactly at
   the cap. This is why `OceanAperiodicDirectionMapBilinear` hand-rolls its filter from `Load`s —
   `Load` needs no sampler. Opt in per-consumer (`WATER_APERIODIC_MAP_SAMPLER`) or not at all.
4. **Borrowing another texture's sampler** (`sampler_OceanFftDisplacement` for the direction map).
   `Unrecognized sampler ... does not exist` in `fragFoamOverlay` — `sampler_<Name>` resolves against
   `_<Name>`, and a pass that never references that texture has it stripped.
5. **Hardcoded pass indices.** `const int CombinedShaderPass = 4` →
   `invalid pass index 4 in DrawProcedural`. Use `Material.FindPass(name)`, cache the answer
   including the negative, and fall back automatically. A const the user must find and flip is not a
   fallback.

---

## 5. Invariants the rewrite MUST preserve

These are load-bearing. Each was paid for with a shipped bug; the file comments carry the full story.

1. **The arming band must remain a strict superset of what the per-pixel mask can admit.**
   `WaterVolume.Underwater.cs:386-398`. Over-arming is free (an armed pass whose mask admits nothing
   changes no pixel); under-arming pops. The band comes from `SurfaceHeightEnvelope()` — no readback,
   so it cannot flap on staleness.
2. **The lens mask stays analytic. The height RT is a 2 m lattice** and cannot represent a
   centimetre-range crossing (`WaterWaterline.hlsl`, the `HeightRTSurfaceY` block). RT is right for
   the march and for *arming*-scale questions; wrong for the meniscus itself.
3. **`ddx/ddy` of the gap must be taken in uniform control flow.** Any branch that selects between
   two ways of computing `gapSmooth` must be decided on a UNIFORM value (that is why
   `WaterlineFarFromSurface` reads the camera, not the pixel).
4. **Position from the inverted read, feather width from the smooth vertical read.** The inverted gap
   can converge to different wave sources on adjacent pixels near pinched crests; its screen
   derivative fizzes. `WaterUnderwaterFog.shader` ArmWeight documents this.
5. **`rayStartsWet` must come from the coverage WEIGHT, not the raw gap.** They part by
   `WATERLINE_CARVE_OVER_COVER_PIXELS` inside a dry carve — that was the thin red line in fog debug
   view 12.
6. **Ownership claims need both-flank corroboration, in both directions.**
   `WaterOceanRenderedCoverage.hlsl` — F7 (isolated air texel printed a dark line) and F9 (isolated
   wet texel armed the mask over open sky).
7. **The exclusion/carve path.** Fog must still arm unconditionally inside a dry carve, and the
   exclusion wall self-completes only while `_UnderwaterFogArmed` is 0.
8. **Simple tier must stay compiled-out, not branched-out.** Register allocation is sized to the
   worst path; `WATER_FOG_SIMPLE` is a preprocessor fence for that reason.

---

## 6. Target architecture

Round 3's span-RT sketch, updated for what F3 and the 2026-08-11 audit changed.

**One new raster pass, "WaterFogClassify", recorded immediately before the fog composites, under
exactly the same gates.** It runs the expensive shared solve once and writes it; the two composites
and the meniscus become cheap readers.

What to write (start full-res, one RT, expand only if measurement demands):

- `RT0` (RGBA16F or RG32F): `classifyGap`, `gapSmooth` — the two numbers `ArmWeight` and the meniscus
  both need. **These two alone remove 6 of the 9 field evaluations.**
- Optional second target, only if it measures: `pathTransmittance.rgb + armWeight.a` and
  `depthAttenuation.rgb + sunVisibility.a`, i.e. round 3's full span RT. That removes the segment
  solve duplication as well, but it is strictly more risk — do it as a second step, not the first.

Consumers:
- `ArmWeight` reads `RT0` instead of calling `SurfaceSignedGapChopInvertedPair`. Everything downstream
  (coverage, `OceanRenderedCoverage`, `rayStartsWet`) is unchanged.
- The meniscus pass reads the same `RT0`. Its `ddx/ddy` then operate on a sampled value — **which is
  more stable than today's per-pixel fixed point**, whose fizz the shader already complains about.
- `WATER_FOG_SIMPLE` keeps its current direct path; do not route it through the RT.

Gotchas that will bite:
- **Debug views.** `WaterFogDebug.hlsl` stamps branch state per pixel. Either evaluate the views in
  the classify pass and pass the colour through, or gate the whole refactor off while
  `_WaterDebugMode >= WATER_DEBUG_FOG_FIRST`.
- **Half-res is not free.** Bilinear upsample across the waterline is wrong. Full-res first, measure,
  then try half with a sign-aware upsample.
- **RT format.** Gaps are metres over a ±20 m band and the meniscus needs centimetres. `RG32F` is
  safe; `RG16F` gives ~1.6 cm at 20 m — marginal. Start at 32F, drop only with a visual A/B.
- **One more mid-frame render-target switch**, which `WaterUnderwaterFogPass.cs:58-65` notes costs
  far more on WebGPU than native. This is the main way the refactor could fail to pay off — it must
  be measured on the WebGPU backend, not only in the DX11 editor.

---

## 7. Decisions to take before writing code

1. Full-res or half-res classify RT? (Recommend: full first.)
2. `RT0` only, or the full span RT of round 3? (Recommend: `RT0` only, ship, measure, then decide.)
3. Debug views: pass-through or gate-off? (Recommend: gate-off first — smaller diff.)
4. Does the meniscus keep its own pass, or fold into the composites now that all three share inputs?
5. Is the ~20 m arming band (from `Hs = 15`) acceptable, or should arming move to a **local**
   predicted surface height (height RT at the camera xz + pad)? Precision is fine there — this is an
   arming-scale question, invariant 2 does not forbid it. This is the only change that can take the
   cost to **zero** on frames where the camera is genuinely clear of the water, rather than reducing
   it. Consider doing it FIRST: it is smaller than the RT refactor and may be worth more.

---

## 8. Verification protocol

After each step, not at the end:

1. Compile. After ANY fog-shader edit the first play looks worse (variant compilation) — **restart
   the editor before judging fps**.
2. Camera above the surface, at the surface straddling, fully submerged, and inside the boat's dry
   interior (carve still carved, fog through windows correct).
3. Partial-submersion crossing in a heavy sea — no unfogged band pop, no transition desync. This is
   the test that caught dead end #2.
4. Shore surf grazing up-looks; pond/bounded body regression; Simple tier unchanged.
5. Fog debug views 10 / 12 / 13.
6. GPU capture at the same camera spot each time, recording `WaterUnderwaterFog`,
   `.SurfaceDepth`, `.HeightRT`, `.Waterline`.

---

## 9. Working method

- Compile after every step. Do not batch shader edits — three separate build breaks in this session
  came from batching changes that could not be compiled where they were written.
- Every untested change needs an **automatic** fallback, not a constant someone has to find.
- Read `PROMPT_fog_perf_round3.md`'s rejected list and §4 above before proposing anything. Both
  dual-source blending and the prepass-culling idea were already closed once.
