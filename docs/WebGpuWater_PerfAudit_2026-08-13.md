# WebGpuWater Performance Audit — 2026-08-13

Analysis only — **zero code changed**. Four parallel deep audits (leak-on-tweak lifecycle, per-frame CPU,
GPU passes/shaders, editor layer) over the full package source (170 C# files, 66 shader files), followed by
line-level verification of every headline claim. Verdicts: **CONFIRMED** = I read the code and the mechanism
is real; **SUSPECT** = mechanism real, trigger condition unproven without a profile capture.

Symptom under investigation: *150 fps sometimes, 40 fps sometimes, correlates with tweaking water values /
scenes, full restart fixes it.*

---

## PART A — The "restart fixes it" degradation

The good news first: the classic bug class for this symptom — resources re-allocated on parameter change
without releasing the old ones — is **already fixed in your current source**. Every renderer feature
`Create()` releases before re-creating (URP calls `Create()` on *every inspector tweak*), all RT/buffer/material
lifecycles are paired, all event subscriptions balanced, statics have `ResetStaticState`. The lifecycle agent
found **zero unpaired allocation** in current code. What remains are four verified mechanisms that accumulate
until restart, plus two big per-drag CPU cliffs that feel like "tweaking broke it".

### A1. CONFIRMED — Inspector constant-repaint latch, persisted in SessionState
`Editor/WaterVolumeEditor.Inspector.cs:192-193`
```csharp
public override bool RequiresConstantRepaint() =>
    _tab == InspectorTab.Body && _showWiring && HasSun;
```
`_tab` and every foldout are persisted through **SessionState** (`Inspector.cs:103-109, 183-187`), which
survives selection changes and domain reloads and clears **only on editor restart**. Open Body ▸ Wiring once
on a sun-wired body and from then on, *every* time a WaterVolume is selected, the inspector repaints
continuously — `serializedObject.Update()` on a very large object plus dozens of string `FindProperty`
lookups per repaint, forever. This is the single best match for "I tweak values → it gets slow → restart
fixes it": tweaking is exactly when the volume is selected, and restart is exactly what clears SessionState.
Interim workaround (no code): collapse the Wiring foldout / switch tab.

### A2. CONFIRMED-FRAGILE — Planar mirror retire slot can silently leak a screen-sized RT + hidden camera
`Runtime/WaterVolume.Underwater.cs:201-218` — `RetirePlanarMirror()` blindly overwrites the single
`_planarMirrorRetiring` slot; safety rests on "Update always drains between two retires". That invariant
breaks whenever the camera renders more than once without a player-loop tick (editor repaints, explicit
render requests) while `EffectiveUsePlanar` flaps (planar budget of 3 with more planar bodies, or
`IsVisibleToCamera` flapping — both of which happen while tweaking reflection settings). Each miss leaks one
`HideAndDontSave` mirror rig: an RGBAHalf mipped half-res screen RT (~16-30 MB at 1440p) + depth + a hidden
camera object. Nothing collects them until restart/`UnloadUnusedAssets`. A handful of these = real VRAM
pressure = exactly the kind of sustained 150→40 drop that survives scene reload but not restart. This is the
only lifecycle seam in the package whose correctness rests on a scheduling assumption instead of code.

### A3. SUSPECT — `Bodies` registry leak if init throws mid-way, keeps the 60 Hz editor pump alive forever
`Runtime/WaterVolume.cs:234` adds the body to the static `Bodies` list *before* `_initialized = true`
(line 254). `OnDisable` (line 262) does `if (!_initialized) return;` **before** `Bodies.Remove(this)`
(line 274). Any exception between the two (bed bake, caustic render, window patch — WebGPU device loss is an
acknowledged reality elsewhere in the code) leaves a permanent ghost entry: `ActiveBodyCount > 0` forever, so
the editor preview driver (A5) keeps pumping the full player loop at 60 Hz **even in an empty scene**, and
`TryInitialize` may re-throw every Update tick (exception + console error per frame). Cleared only by domain
reload/restart. If you ever see repeated init errors in the console before a slowdown, this is it.

### A4. Bounded stragglers (real but capped)
`Rendering/LargeBodyAtmospherePass.cs:212-257` — temporal-history RTs of destroyed cameras are only swept
when the map reaches 4 entries: up to 3 dead cameras' half-res RGBAHalf histories persist (camera
recreation while scene-tweaking). `WaterExclusionVolume.cs:303` — static wall-material cache re-leaks one
native material shell per Fast-Enter-Play cycle (kilobytes). Neither explains 40 fps alone; both are
restart-cleared.

### A5. Context: the editor baseline is heavy by design (and hides the real tier)
Two confirmed facts multiply everything below while you work in the editor:
`Editor/WaterEditorPreviewDriver.cs:30-66` pumps `QueuePlayerLoopUpdate()` + `SceneView.RepaintAll()` at
60 Hz whenever any WaterVolume exists (kill switch: *Window ▸ AbstractOcclusion ▸ WebGpuWater ▸ Live Water
Preview*), and `Runtime/WaterSimScheduler.cs:32-40` disables **all** culling, activation distance, and the
4-body sim budget in edit mode — every body simulates at Full tier every tick (`WaterVolume.Quality.cs:26,90`
gates the Low/Mid relief to play mode). This matches the earlier finding on record that "ultra heavy" was
always the editor at Full. Your 150 vs 40 measurements are taken on top of this floor, so any of A1-A3
stacking onto it visibly cliffs.

### A6. Per-drag CPU cliffs (recover on release — they *feel* like breakage while dragging)
- **CONFIRMED** `Runtime/WaterOceanFft.cs:555-558, 121-125` — `ShapeEquals` uses exact float equality on 7
  sea-shape params; dragging SignificantHeight / PeakWavelength / SwellHeight etc. re-runs the
  "resolution² × cascades CPU integral … 65k transcendental evaluations" **every frame of the drag**, live in
  edit mode.
- **CONFIRMED** `Runtime/WaterSeaStateFetchField.cs:19, 38-110` — wind-direction epsilon is 0.001°, so
  dragging `windFromDegrees` (or extent/position) rebakes a 256² CPU shore raymarch per frame **plus**
  `new float[65536]` + `new Color[65536]` per rebake → GC churn (opt-in feature; only when fetch enabled).

---

## PART B — Steady-state FPS eaters, ranked

### B1. Uniform publishing: ~1,700-2,400 native shader-property writes per frame per ocean body
`Runtime/WaterUniformPublisher.cs:640-926` — `WriteBodyUniforms` performs **170 `Set*` calls** (verified
count), ~15 texture binds, 2×16 Vector4 arrays, all values re-derived and re-pushed unconditionally. It runs
~10-14× per frame per body: body MPB, patch above+under, clipmap, `PublishBodyGlobals` in Update
(`WaterVolume.Update.cs:124`) **plus a second full `PublishBodyGlobals` every frame** in
`UpdateUnderwaterState` (`WaterVolume.Underwater.cs:251` — needed for secondary-pool fog, but unconditional
even when FogSource is the primary and nothing changed), 4× in foam particle draws
(`WaterFoamParticles.cs:1017-1091`), membership block, and per-pass blocks. Only `_WaveTime` and a few
camera-dependent values actually change per frame. The file's own comment (line 371) already flags the shape.
Biggest single CPU lever in the package: per-section dirty flags, and drop the duplicate underwater publish.

### B2. Planar reflection: a full second scene render per planar body per frame
`Runtime/PlanarMirror.cs:25,62-169` — `MinimumUpdateIntervalFrames = 1` and default
`planarUpdateInterval = 1` (`Settings.Reflections.cs:192`); every frame it submits a complete URP camera
render (CPU culling + submission + GPU) at 0.5× res into a mipped RT with `autoGenerateMips = true` → full
mip-chain regen after every render. Runs live in edit mode via the 60 Hz pump. Cheapest lever you own today:
raise `planarUpdateInterval` to 2-3 on tweak-heavy scenes.

### B3. Underwater fog stack (see Part C for the refactor verdict)
Confirmed waste in the current design, per submerged/armed frame: up to 7 recorded passes + 2 blits — 0.5×
eye-depth prepass (~10 displaced-mesh draws), 256² `_WaterHeightRT` (74k-vert grid), 256²
`_WaterLensHeightRT` (**289² ≈ 83k verts, of which ~98% is apron** — the shared `HeightRtChopApron = 16 m`
constant at `WaterUnderwaterFogPass.cs:62` is 128 cells per side at the lens grid's 0.125 m cells, vs 32
window cells), full-res RG32F classify, absorb fullscreen, inscatter fullscreen, waterline + copy. And the
headline: **absorb and inscatter each re-run the entire `UnderwaterFog()` per-pixel solve**
(`WaterUnderwaterFog.shader:1278` and `:1343` both call `:1018`) — segment solve / 16-step march + 8
bisections, exclusion loops, downwelling — ≈2× the ALU of the most expensive fullscreen shader in the
package, a deliberate trade to avoid a scene copy.
Two arming holes: **ponds/bounded bodies arm fullscreen fog from ANY camera position**
(`WaterVolume.Underwater.cs:297-298` — the non-ocean arm is literally `: true)`, so a 20 px pond behind the
camera pays classify+absorb+inscatter every frame), and oceans over-arm whenever any near-plane corner dips
under rest + envelope + pad — constant on a boat deck in heavy seas.

### B4. Ripple sim: no idle gate for bounded bodies; ocean sleep defeated by two settings
Verified in `WaterVolume.Update.cs:139-153`: `ShouldRunRippleSolver()` returns **`true` unconditionally for
every non-ocean body** — 2×Update + Normal + Foam + Conserve at simRes² every frame on perfectly still water,
forever. Oceans have a proper GPU-activity sleep latch (`WaterSimulation.cs:878-924`), but
`HasContinuousRippleSource()` bypasses it entirely when `objectInteraction == FootprintDelta` **or** any surf
foam gain > 0 (`surfFoamGain + surfWaterlineFoam + surfSwashDepositGain > 0`) — i.e. every beach-demo ocean
never sleeps and never even runs the sleep check. An idle gate for bounded bodies (same activity reduction
the ocean already has) is the obvious sim-side win.

### B5. Foam particles: full-capacity work regardless of live count
`Runtime/WaterFoamParticles.cs:1003-1083, 826` — 3-4 `RenderPrimitives` submissions of
`capacityPow2 × 6` vertices every frame (capacity 4096 default, 65k max → up to ~1.5M mostly-degenerate
verts whose VS still reads particle buffer + wave field), Update kernel dispatched over full capacity, ~90-100
`cs.Set*` + 4 full 170-write `WriteBodyProps` per frame while active. Idle gating exists and is good; the
active-state cost is flat.

### B6. CPU wave sampling: the analytic fallback is ~80 Gerstner evals per query point
`Runtime/LargeWaveField.cs:171,631` — chop inversion = 4+1 full band passes × 16 Gerstner components + shore
+ surf-front profiles per point. Cheap when the FFT readback region covers the query (~7 bilinear taps,
`WaterOceanFft.cs:730`); brutal outside it / before first landing. Multipliers: buoyancy 8-27 probes ×
floaters per FixedUpdate (`WaterBuoyancy.cs:192-234` — 27-probe floater ≈ 2,000+ trig evals/tick), spray
pump batches, legacy Shuriken splash droplets (`WaterSplashEmitter.cs:314-317` — full wave query +
`BodyContaining` **per droplet per frame**; 200 droplets ≈ 16k Gerstner evals/frame — only when the legacy
path is live), and 1-query-per-frame consumers (splash, gauge, probe, interactable). Widening/caching the
readback region is the listed-DEFERRED fix in the file itself.

### B7. CONFIRMED — `WaterSkyFogFeature` is the one feature with no camera gate
`Runtime/Rendering/WaterSkyFogFeature.cs:32-41` gates on `RenderSettings.fog` + opacity only — no
`cameraType` check, unlike every other feature (they use `WaterPassCameraGate`). It records a fullscreen
pass for preview thumbnails, reflection cameras (including the package's own planar mirror — the exact bug
class `WaterPassCameraGate.cs` documents), and every scene view, whenever scene fog is on.

### B8. Smaller confirmed items
- God rays enqueue above water whenever `LargeGodRayFromAir > 0` (disables the provably-dry CPU reject,
  `LargeBodyAtmosphereFeature.cs:59-65`): half-res 24-step march + history copy + composite for a veil term.
- Ocean caustic RT (66k-vert lattice × 5 field projections → 1024² + mips, every `_causticInterval` frames)
  renders even when nothing consumes it (camera dry, god rays skipped, screen caustics off) —
  `WaterVolume.Update.cs:118-119`.
- Chunk/exclusion depth prepasses use `SkipCamera`, not `SkipCameraFullscreen`, so a planar-mirror frame
  re-pays 2-4 camera-sized Depth32 passes (`WaterChunkDepthFeature.cs:41`, `WaterExclusionDepthFeature.cs:46`).
- Restore-depth fullscreen + transparent reroute run whenever any `WaterFogTransparent` exists and a body is
  active — armed or not (`WaterUnderwaterFogFeature.cs:79-87`).
- Scene-light gather allocates a `Light[]` every 0.5 s and re-sorts + re-uploads 3 vector arrays every armed
  frame (`WaterUniformPublisher.cs:557-611`).
- 5 global keyword flips per frame by **string** (`WaterUniformPublisher.cs:497-537`) — `GlobalKeyword`
  structs would remove the hash lookups; first-crossing flips can trigger PSO compiles (self-documented).
- Editor: `WaterSprayPumpEditor.cs:462` runs `FindFirstObjectByType` per repaint while selected; wizard
  draft solve re-runs per GUI event while its section is open (`WaterWizardWindow.HullSpray.cs:112-144`).

### Checked and clean (so you don't re-audit)
No LINQ/closures/string-props/`GetComponent`/`Camera.main` in hot paths; all `PropertyToID` cached; readback
delegates cached, single-in-flight, demand-gated; no `Physics.*` queries anywhere; wave-bank/LUT/bed/shore
bakes all dirty-keyed; `WaterVolume` has **no OnValidate** — slider drags do NOT rebuild GPU resources
(resolutions fixed per-enable); renderer-feature `Create()` leak already fixed with release-before-create in
all 6 features; membership block shared (the 6,900-writes-per-frame bug already killed); `RequiresConstantRepaint`
correctly narrow — except for the SessionState latch in A1.

---

## PART C — Should underwater be refactored? Verdict: surgical refactor YES, rewrite NO.

The 96 KB shader is not bloat in the pejorative sense — the bulk is load-bearing documentation of reverted
experiments, and waterline/restore-depth are already separate shaders; the classification/exclusion math is
shared with god rays and the surface via includes (a feature, not duplication). A ground-up rewrite would
re-litigate a dense web of validated invariants (prepass authority, carve rules, the fog/god-ray blend-order
coupling at `LargeBodyAtmospherePass.cs:33-39`, several "DO NOT RE-TRY" fences) for months of regression risk.

What a **surgical** refactor buys, keeping the classification logic untouched:

1. **Fold the double solve.** Compute transmittance + inscatter ONCE into an intermediate (optionally
   half-res) target, then one composite pass reading both. The per-channel dst-multiply that forced the
   two-blend design disappears once you own a composite. ≈ halves fog per-pixel cost and opens the standard
   KWS/Crest half-res-with-bilateral-upsample path. The classify RT is precedent — it is exactly this
   pattern, already shipped and working.
2. **Pond arming gate.** Give bounded bodies the CPU footprint-vs-frustum/coverage test oceans effectively
   have — near-100% saving in pond scenes with the camera away/above.
3. **Per-grid apron constant.** The lens grid's 16 m apron at 0.125 m cells is 98% waste; a per-grid apron
   is a one-constant fix, ~83k verts → ~5k per submerged frame.
4. **Prune dead weight.** Unreachable `WATER_FOG_BRANCH_FLAT_FALLBACK` (shader:593), never-read
   `classifyGap` out-param (parked awaiting a play-test), god-ray blur passes "COMPILED BUT NEVER
   DISPATCHED", and the 6+12 multi_compile variant fan-out (SIMPLE and CLASSIFY_RT are exclusive states of
   one concept — restructure cuts the "~600 KB bytecode, minutes-long d3d11 optimize" compile cost).

Items 1-4 combined capture an estimated 40-50% of the fog stack's submerged-frame cost and ~100% of the
off-screen-pond cost — most of what a rewrite could ever deliver, at a fraction of the risk.

---

## Suggested order of attack (when you authorize changes)

1. **A1** one-line repaint-latch fix (biggest suspect for your exact symptom, zero risk).
2. **A2** make planar retire slot drain-before-overwrite (removes the only scheduling-dependent leak; kills
   the VRAM-creep suspect).
3. **A3** move `Bodies.Remove` above the `_initialized` guard in OnDisable (one-line, kills the ghost-body pump).
4. **B1** dirty-flag `WriteBodyUniforms` sections + drop the duplicate `PublishBodyGlobals` — biggest
   steady-state CPU win.
5. **C2 + C3** pond fog gate + lens apron (small, safe, big in pond/submerged scenes).
6. **B4** idle gate for bounded-body ripple sim (reuse the ocean activity reduction).
7. **B7** camera-gate `WaterSkyFogFeature`; **A6** epsilon-based `ShapeEquals` + fetch-bake drag throttle.
8. **C1** fog single-solve restructure — the big one, own round with play-testing.
9. **B2/B5/B6** planar interval default, foam live-count draws, FFT readback region — design-gated.

No-code workarounds available right now: collapse the Body ▸ Wiring foldout; set `planarUpdateInterval ≥ 2`;
toggle *Live Water Preview* off when idling; keep surf foam gains at 0 on oceans you're not shore-tuning
(re-enables the ripple sleep latch — note `FootprintDelta` interaction also blocks sleep).
