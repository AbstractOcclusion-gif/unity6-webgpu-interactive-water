# WebGpuWater — Ultra-tier performance audit — 2026-07-31

Read-only pass. Scope: everything shipped since the 2026-07-29 audit + re-verification of that
audit's leftovers + the boat path + a KWS2/Crest technique comparison. Every finding below was
re-verified against the working tree (line numbers are current). **No code was changed.**

**Context that frames everything:** the Mid tier already runs Full fog (`mediumUnderwaterFog: 2`),
rich reflections, real refraction, and renderScale 1. The Mid→High delta is almost entirely
**resolutions and loop counts**: simRes 256→**512**, caustics 512→**1024**, godRaySteps 16→24
(ocean, `min(24, 32)`), waves 12→16, refine 3→5. So "mid is fine, ultra is not" points squarely at
the **simRes-quadratic** costs — and two meshes silently ride simRes.

---

## ✅ Good news first — the boat fix is intact

The 07-30 queued-injection fix survived the later sessions' edits, verified line by line:
queue + overflow-flush in `WaterSimulation.cs:513/536`, one dispatch per kind with a count-uniform
loop (`WaterSim.compute:360/391`), Sanitize inside the loop (`:371/:421`), flush before `Step`
(`WaterVolume.Update.cs:67`). Zero immediate-dispatch callers remain. Only stale comments left
(`WaterInteractable.cs:76`, `WaterSphereInteractor.cs:20`). **Still untested in play — the Frame
Debugger check stands: driving the boat should show ONE `Drop` dispatch per frame.**

---

## TIER A — ms-class at High, the "ultra feel" (ranked)

### A1. The caustic generator's lattice rides simRes: ~263k verts × ~140 fetches, every frame
`causticDetail` defaults to `MatchSim` → `causticGridResolution` resolves 0 → the caustic pass
draws **the sim-window patch mesh = `BuildGrid(_simRes)`** (`WaterCausticsPass.cs:130` comment
:183, `WaterVolume.SimWindowPatch.cs:76`). At High that is a **513² grid (~263k verts)**, and
`LargeBodyCaustics.shader:226-235` evaluates `CausticProjectedPos` **5× per vertex** (centre + 4
central differences), each ≈ 1 sim tap + 4 × `LargeBodyWaveHeight` (≈2 shore + 4 FFT fetches)
≈ ~28 fetches → **~37M texture fetches/frame** into a 1024² RT + explicit mips, at
`highCausticInterval: 1`. Mid pays exactly ¼ of the vertices and ¼ of the RT. The tooltip itself
prices it ("Costs 4x the caustic pass's vertex work, which is already 5 projections per vertex").
- **Fix shape (small):** cap the caustic lattice independently of simRes (the
  `causticGridResolution` plumbing already exists — a ≤256 cap at High), and/or
  `highCausticInterval: 2`. The RT can stay 1024; the lattice is the cost.
- Confidence: high. This is the single most ultra-specific GPU item found.

### A2. The sim-window patch mesh is 513² × 2 twins ≈ 526k verts/frame at High
Same root: `_patchGrid = BuildGrid(_simRes)` (`WaterVolume.SimWindowPatch.cs:76`). Each patch
vertex runs the full vert stage ≈ 16 bicubic sim fetches + 2 shore + 4 FFT (~22 fetches)
(`WaterSurfaceVertStage.hlsl:70-87`). ~12M vertex fetches vs ~3M at Mid — and on fog-armed frames
the same mesh is **re-drawn by the eye-depth prepass and the foam overlay** (up to ×3). The
clipmap itself is innocent (~76k verts total).
- **Fix shape (small):** decouple the patch grid from simRes (cap ~256). The sim texture stays
  512 — a bicubic-filtered read does not need one vertex per texel.
- Confidence: high.

### A3. Doubled fullscreen fog march — the 07-29 leftover, confirmed untouched
`FragAbsorb` (:919) and `FragInscatter` (:975) each run the full `UnderwaterFog()`:
40-step march + 12-iter bisection + exclusion loops, twice per pixel per submerged frame.
Known; the fix is the AddCopyPass + in-shader composite (blend-fold is impossible, established).
- Confidence: high. Biggest single underwater item at High.

### A4. One boat `withSplash` holds a 4 MiB/frame GPU→CPU readback open, forever
`WaterSplash.FixedUpdate` (`WaterSplash.cs:50`) calls `TryGetWaterHeight` every physics tick —
a *rippled* query, so it stamps `_lastDemandFrame` and re-opens the D-1 demand gate permanently.
The readback is the **whole sim RT as RGBAFloat** (`WaterSurfaceSampler.cs:58`): at High simRes
512 that is **512×512×16 B = 4 MiB per frame** (~240 MB/s at 60 fps) plus a ~4 MB managed
`Color[]` copy per landing — in a browser. All to answer "is my collider bottom under the
surface", which the analytic fallback on the very next line already answers. (Buoyancy is clean:
the boat kit sets `ignoreInteractiveRipples = true`, so probes don't stamp demand.)
- **Fix shape (tiny):** WaterSplash queries the analytic waterline only (or an excludeRipples
  query). Likely THE boat-feel item in the browser, alongside the untested injection fix.
- Confidence: high.

### A5. The sim chain has no idle gate, and it now runs at 512²
7–8 full-grid dispatches per frame per body (2× Update, 3× Conserve reduce/final/conserve,
Normals, Foam) + the caustic render — **every frame, even mirror-flat with zero interactors**
(`WaterVolume.Solver.cs:36-131`, no quiescence check exists). At High these run at 512²: 4× the
Mid texel count.
- **Fix shape (medium):** frames-since-last-injection counter + max-energy tap piggybacked on the
  existing ReduceMean pass; skip Update/Conserve/Normals below epsilon. See also KWS-1 below
  (fixed-Hz buckets) — strictly cheaper on 120–144 Hz browser displays.
- Confidence: high on the facts, medium on the ms win (scene-dependent).

---

## TIER B — sub-ms each, they stack

- **B1. God rays pay a fullscreen tax every above-water frame.** The feature enqueues on
  "ocean + density > 0" with no camera-height term (`LargeBodyAtmosphereFeature.cs:43`). Above
  water with `FromAir = 0` the half-res march early-outs cheaply — but the frame still pays the
  half-res target + history copy + a **full-res composite whose mask computes
  `SurfaceSignedGap(nearWorld)` (~6 fetches + 16-sine wave loop) per pixel**
  (`LargeBodyGodRays.shader:999-1013`). That's most of gameplay time. Fix: mirror the shader's
  own reject on the CPU — skip enqueue when `camY > restY + band && FromAir <= 0` (history
  invalidation already exists via `entry.Valid`).
- **B2. Planar mirror renders the second scene at full quality.** No shadow kill, no LOD
  bias/max-LOD clamp, no update interval, RT = 0.5× *screen* with auto-mips regenerated every
  render, up to 3 bodies (`PlanarMirror.cs:110-159`). KWS Ultra: fixed 2048×1024 max, shadows
  off, pixelLightCount 0, lodBias ×0.5. Crest: 256² default + every-N-frames refresh. Mid pays
  this identically — not the Mid→High delta, but the biggest "rich reflections" lever we own.
- **B3. Sky aniso blur computed then thrown away under planar.** `ReflectionStage` always runs
  `SampleSkyEnvironmentAniso` (5 cube taps), then `if (_UsePlanar > 0.5)` replaces it with 5
  planar taps (`WaterSurfaceFragStages.hlsl:585-593`). Every above-water pixel, both tiers. Fix:
  move the sky sample into the planar-off branch (all explicit-LOD, WGSL-safe).
- **B4. Whitecap coverage re-fetches what the geometry stage already has.**
  `OceanFftFoam(i.largeWaveSourceXZ)` re-runs `ShoreSample` (+2) though `g.shore` was hoisted, and
  re-runs the 4-tap cascade sum the normal/jacobian stages already ran — 6–10 avoidable
  fetches per above-water ocean pixel (`WaterSurfaceFragStages.hlsl:229`,
  `WaterLargeWaves.hlsl:355`). CSE across [loop] boundaries is exactly what Tint won't do.
- **B5. Horizon haze: ~12 fetches/pixel with no early-out** — including near-field pixels where
  haze ≈ 0 (`WaterSurfaceFragStages.hlsl:1292-1432`). A per-pixel `if (haze > eps)` is legal
  (all taps explicit-LOD).
- **B6. Straddle-frame waste, two items:** the ocean eye-depth prepass records on
  waterline-only frames where the fog pass (its only consumer) doesn't run
  (`WaterUnderwaterFogPass.cs:93` vs `:110`); the waterline pass does an unconditional
  camera-sized `AddCopyPass` read only when `_WaterlineWarp > 0` (`:129-133` vs shader `:1098`).
  Both: cheap C# gates.
- **B7. Exclusion-scene selects (fog + wall):** `tDeep` ternary evaluates BOTH volume loops
  (`WaterUnderwaterFog.shader:794`, `WaterExclusionWall.shader:180`); absorb computes
  `ExclusionSpanSunVisibility` then discards it (`:914`); the wall shader still has NO
  `WATER_FOG_SIMPLE` keyword so both lanes compile everywhere (`WaterExclusionWall.shader:133,
  227`); god-ray caustic tap is a per-step select paid at strength 0
  (`LargeBodyGodRays.shader:689`). `ShoreShoalDepth` still evaluated as an argument before the
  `_DepthClarityStrength` gate (`:859`; wall `:193`) — the surface shader (`FragStages:1118`)
  already shows the correct gated shape.

## TIER C — CPU / memory / housekeeping

- **C1. `PublishSceneLights` allocates a `Light[]` via `FindObjectsByType` every armed frame**
  (`WaterUniformPublisher.cs:436`). Correctly gated when knobs are 0. Fix: OnEnable/OnDisable
  registration list (the `WaterFogTransparent.Live` pattern, already in the codebase).
- **C2. D-6 unchanged:** ~20 un-SRP-batched draws/camera, MPB per renderer; the write side was
  collapsed (b3) but the draw side stands. KWS renders its whole ocean as **one
  `Graphics.RenderMeshIndirect`**. Real architectural change; the annulus template mesh already
  exists (`LargeWaterClipmap.cs:44`), so an instanced path is geometrically possible.
- **C3. Ungated uniform traffic:** `WaterShoreDepthField.Publish` = 51 `SetGlobal*`/frame (37 in
  the surf section alone, publishable-skippable per the C-2 recipe); `WriteBodyUniforms` grew to
  **144** `Set*`. No dirty flags anywhere (still no `OnValidate` in Runtime).
- **C4. D-3 unchanged:** ~8 MB/body of RTs for features that are off (obstacle chain ~3.5 MB under
  `MouseLikeDrops`, caustic RT 4 MB even at `CausticFrame.None` via `Enabled => true`
  (`WaterCollaboratorModules.cs:62`), foam ping-pong 1 MB with foam off).
- **C5. Boat-path residue:** ocean FFT height readback has NO demand gate (64 KiB/frame always,
  `WaterOceanFft.cs:535`); sphere-flush ping-pongs the foam pair even at `wakeFoam 0` (pure copy);
  overflow can dispatch on a scheduler-paused body; buoyancy runs 3 separate 16-wave loops per
  probe (~1.5k transcendentals/boat/tick — fusable); `_heightReady` staleness confirmed
  (correctness, not perf).

---

## ⚠️ Two config questions (deliberate-or-drift — your call, not filed as bugs)

1. **`HighWaterQuality.asset` has `selection: 0` (Auto)** — and `WaterQuality.Probe()` returns
   **Low on any WebGL/WebGPU player**. Auto was the deliberate b2/A-1 choice for desktop VRAM
   fallback — but in a browser build this asset can never deliver High on its own. **How are you
   forcing ultra in your browser tests?** If the demo's quality selector sets `selection` at
   runtime, fine; if not, the "ultra" browser numbers may not be measuring the High tier at all.
2. **`highGodRaySteps` drift:** 32 in HighWaterQuality.asset vs 64 in the other two assets' high
   blocks. Which is intended?

---

## Stealable techniques (KWS2 / Crest, verified in their code)

- **KWS-1. Fixed-Hz sim buckets** (30/45/60 Hz with ≤2-frame catch-up, `KWS_UpdateManager.cs`),
  not every-Nth-frame: FFT/caustics capped at 60 Hz, foam at 30. On a 144 Hz browser our
  interval-1 dispatches run 2.4× more often than KWS Ultra. Free win, pure C#.
- **KWS-2. Volumetrics at ≤50% screen height, 8 steps, IGN jitter + prev-frame reprojection +
  pyramid blur** (`VolumetricLightingPass.cs`, `KWS_VolumetricLighting.shader:216-237`). Their
  max-quality volumetric budget ≈ 1–1.5 effective steps/pixel vs our 40-step full-res march ×2.
  Nobody pays full-res dependent-fetch marches: Crest's underwater is 100% closed-form, KWS
  leans on a 0.35× water prepass. Route for A3 beyond the composite dedupe: half-res march +
  jitter + the temporal pattern we already own from god rays.
- **KWS-3. Mirror gutting** (see B2) + **Crest's refresh interval / fixed small RT / far-clip
  clamp / occlusion-cull off** for planar (`WaterReflections.cs:66-147`).
- **KWS-4. Point-query GPU buoyancy**: positions buffer → compute eval → tiny readback; one
  request in flight (`BuoyancyPass.cs:83-147`). Crest batches all queries into one dispatch and
  finite-differences velocity on the CPU. Kills our field-readback bandwidth class entirely
  (A4 becomes moot for ALL consumers, not just splash).
- **KWS-5. SSR option**: 5-step secant screen-space reflection at ≤75% res as the default "rich"
  path, planar reserved for mirror-critical scenes (`KWS_SSR.shader`, quality enum). We have no
  SSR at all — it's the cheaper 90% of planar's look for sailing-height cameras.
- **Crest-6. Generated per-platform settings header** (`Settings.Crest.Web.hlsl`): compile-time
  kill list for the web build (their Web profile disables shadow sim, planar, stochastic foam…).
  Our multi_compile tiers ship every variant; a generated `WaterSettings.Web.hlsl` would fence
  heavy paths at zero runtime cost.
- **Crest-7. WebGPU landmines they documented** (worth a defensive pass): random-write RGFloat
  lies about support; storage textures trusted only as R32F/RGBA32F; FFT crashes above res 128
  on WebGPU in their stack (we run 128 — at the edge); float2 RTs padded to float4.

## Proposed batches (awaiting your go — nothing touched)

- **Batch U1 — the quadratic pair (A1+A2):** cap caustic lattice + patch grid at 256,
  independent of simRes. 2 files, small, directly targets the Mid→High delta.
- **Batch U2 — boat feel (A4 + C5 gate):** WaterSplash → analytic query; demand-gate the FFT
  height readback. 2 files, tiny.
- **Batch U3 — every-frame gates (B1+B6+B3):** god-ray enqueue height gate, prepass/copy gates,
  sky-vs-planar branch. C# + 1 shader edit, all cheap.
- **Batch U4 — fog composite dedupe (A3):** the AddCopyPass + in-shader composite. 1 shader +
  1 pass file, the known recipe. Bigger; measure with WaterCostProbe F-toggle after.
- **Batch U5+ (design-gated):** idle gate / fixed-Hz buckets (A5+KWS-1), mirror gutting (B2),
  point-query buoyancy (KWS-4), SSR, instanced clipmap (C2). Each deserves its own yes/no.

*Housekeeping: 4 `perfpass_*.tar.gz` staging archives were left in `Temp/` (I can't delete files
on your disk) — safe to delete anytime.*
