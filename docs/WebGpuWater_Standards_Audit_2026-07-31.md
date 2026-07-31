# WebGpuWater — Coding-standards drift audit — 2026-07-31

Flag-only pass against the project rules (no magic numbers, no hardcoded strings, descriptive
names, single-responsibility functions, early returns, comments explain WHY, no dead code,
minimal public API, fail fast, validate at boundaries). Four reviewers over Runtime C# (~104
files), Shaders (56), Editor (~39); every HIGH finding re-verified by hand against the tree.
**Nothing was changed.** Known-accepted items (the 07-28 dedupe list, the `> 0.5` bool idiom,
the 21-partial WaterVolume split, validator literal tables, neutral-value tuning knobs) were
screened out before ranking.

**Overall verdict first:** the codebase is in better shape than the rule set anticipates —
zero commented-out code blocks, zero unused private methods in Runtime, early-return
discipline is conspicuously good, inheritance is clean, and shader-property hygiene is
near-perfect (447 cached PropertyToID uses, zero string-keyed Set*). The drift concentrates on
four axes: **unvalidated C#↔HLSL mirrored literals, function length in the dispatch/publish hot
paths, comment claims outrun by recent fixes, and the Editor boat/wizard constants.**

---

## TIER 1 — the findings that can actually bite (verified)

### S1. Hash constants mirrored C#↔HLSL as raw literals — invisible to the validator ⚠️
The exact drift class the validator exists to prevent, in the exact file that was burned before:
- `LargeWaveField.cs:467` `Hash(fn + phaseSeed + 16f)` ↔ `WaterLargeWaves.hlsl:132`
  `LbwHash(fn + phaseSeed + 16.0)` — the phase-hash stream offset, raw on BOTH sides. The
  file's own comment recalls an unnamed twin that once drifted to 2.0f.
- `LargeWaveField.cs:172` sine-hash pair `12.9898 / 43758.5453` — 4 unvalidated copies
  (`WaterSurfWaves.hlsl:237`, `WaterLargeWaves.hlsl:95`, `LargeBodyCaustics.shader:89`).
  One digit of drift = CPU buoyancy silently disagrees with the rendered surface.
- `WATER_SCENE_LIGHT_MAX` (WaterFog.hlsl:218) ↔ `MaxSceneLights` (WaterUniformPublisher.cs:44)
  — the NEW scene-lights pair relies on a KEEP-IN-SYNC comment instead of the validator.
**Fix shape:** name the constants and register all three families in
`WaterWaveConstantsValidator` — pure hygiene, zero behavior change.

### S2. NaN passes the injection boundary and can kill the whole surface
`WaterVolume.Facade.cs:23` — `if (sim.x < -1f || sim.x > 1f ...) return;` is **false for NaN**,
so a NaN world position on a windowed/ocean body is stamped into the GPU heightfield and
propagates through the ping-pong sim: the surface dies with no message. No NaN/negative guard
on any of the four public injection entry points (`AddRipple`, `AddSphereInteraction`,
`SpawnRipple` + static variants), while the same file throws `ArgumentNullException` elsewhere —
the fail-fast rule applied inconsistently at the most external boundary the package has.
**Fix shape:** `float.IsFinite` + `> 0` guards at the four entry tops.

### S3. Caustic window projection: duplicated AND the comment's "same reference plane" is now false — QUESTION
`WaterCausticMap.hlsl:62` projects with `_SimCenter.y − LARGE_CAUSTIC_REFERENCE_DEPTH` (and
its header claims it reproduces LargeBodyGodRays "exactly — same reference plane"), but
`LargeBodyGodRays.shader:651` now uses `camSurfY − LARGE_CAUSTIC_REFERENCE_DEPTH` — the live
camera-xz surface introduced by the confirmed R1 waterline fix. The generator writes against
the rest plane. So one RT is sampled against two different planes by its two consumer families,
and the formula is hand-copied in both files — the exact seam mechanism WaterCausticMap.hlsl
was created to end. **Question before fixing:** was moving the god-ray caustic plane to
camSurfY deliberate in R1 (shafts riding the swell) or a side effect? Either answer → extract
one shared projection helper parameterised on refPlaneY and correct the stale comment.

### S4. Dead shader function with a false WHY: `ExclusionInteriorDepth`
`WaterExclusion.hlsl:102` — zero call sites across all 56 shader files (verified). Its comment
says "the foam particles use it" — they use `ExclusionParticleInteriorDepth`, a near-duplicate
of the same loop. Dead code + lying comment + duplicated body, all three rules at once.
**Fix shape:** delete it (or make the particle variant share it) and fix the comment.

### S5. Editor boat constants — the known cluster, still the worst magic numbers in the package
`WaterBuildKit.Boat.cs:22-24`: `BoatMass = 200f`, `BoatBuoyancy = 2.6f`,
`BoatSamplesPerAxis = 3` — fixed for any hull, so a galleon gets 200 kg and 27 probes.
`objectWidth` was correctly reworked to derive from the fitted footprint; these three were not.
Already on the roadmap (boat-creator rework: presets + mass-from-volume + probe layout) — this
audit just confirms it's the top Editor item.

### S6. Crown-sheet path constants — KNOWN ship-readiness item, unchanged
`WaterBuildKit.cs:104-108` still point at `Samples~/...` which doesn't exist in the dev state
(the folder is `Samples/`, the `~` goes on at publish — deliberate). In dev, provisioning warns
and the splash crown builds untextured. Per the established rule for deliberate dev states: the
right artifact is a **machine check that passes in both states** (or provisioning from
`Runtime/Textures/`, where the same three sheets already ship) — not a "fix" to the temp state.

---

## TIER 2 — real drift, lower stakes

**Comment drift (comments must not lie):**
- `WaterSurfaceFoamSampling.hlsl:366-369` — whitecap header still describes the retired `min()`
  octave combine that the variance-preserving blend replaced *because* it was broken; actively
  points the next editor at re-introducing a proven bug.
- `WaterBuildKit.cs:18-23` — `LogPrefix` comment claims the four-spellings drift was ended, but
  only 2 call sites use the const vs **31** inline `"[WebGpuWater] "` and 6 `"WebGpuWater: "`
  literals bypassing it. Runtime has the same disease in three formats across 12 files
  (`"[WaterVolume]"` / `"WaterVolume:"` / `"WaterVolume '{name}':"`).
- `WaterVolumePropertyPaths.cs:41` + `WaterBuildKit.Props.cs:81` — both cite "the showcase
  builder" as their consumer; it was removed. ~14 of 38 registry entries are now single-use,
  violating the file's own scope rule.
- Already logged, still present: `WaterInteractable.cs:76` / `WaterSphereInteractor.cs:20`
  claim immediate dispatch (queued since 07-30).

**New duplicated math since the 07-28 dedupe pass (the drift-into-seams rule):**
- Caustic grad-sample + 4-tap occluder PCF block hand-copied ×3 (`WaterReceiver.shader:275`,
  `WaterTerrain.shader:336`, `WaterCausticProjection.shader:123`) — the resolver was
  centralised on 07-30, the sampling that consumes it was not.
- Lamp closest-approach + atan span kernel duplicated between `WaterFog.hlsl:282-294` and
  `LargeBodyGodRays.shader:625-634` — the 0.000%-error match depends on two hand-copies staying
  identical.
- Rotated-octave 2D transform written out ×6 in `WaterSurfaceFoamSampling.hlsl` (269-281 vs
  397-406); one transposed sign = pond foam visibly mismatching ocean whitecaps.
- Twin lamp-glow call blocks in `WaterSurfaceFragStages.hlsl:720-728` vs `749-757`.

**Structure (the one axis Runtime drifts on — function length in hot paths):**
Top god functions, ranked: `WaterFoamParticles.DispatchSimulation` (172 lines, ~9 jobs),
`WaterUniformPublisher.WriteBodyUniforms` (235 lines / ~10 uniform families — split would
mirror the Settings partials), `WaterVolume.Solver.Step` (134 lines, ~8 jobs — the 17-argument
`StepFoam` call inside is its own smell), `WaterShoreDepthField.BuildSdf` (115, 5 phases),
`WaterVolume.TryInitialize` (102), `WaterVolume.Update` (104), `WaterOceanFft.Dispatch` (96),
`WaterUnderwaterFogFeature.RecordRenderGraph` (78, two labelled halves begging to be two
methods). All flat, well-commented sequences — readable, just long. Deep nesting: only 2 sites
package-wide (both algorithmic grid kernels in WaterShoreDepthField).

**Public API surface:**
- `WaterSimulation` is public with ~15 public methods; zero out-of-assembly consumers — the
  facade already covers every need. → internal.
- Demo/instrument components public in Runtime (`FlyCamera`, `OrbitCamera`, `BoatController`,
  `WaterBuoyancyStressSpawner` (zero references anywhere), `WaterCostProbe`,
  `WaterMetricsOverlay`) while the Showcase family is correctly `internal sealed`.
- `WaterVolumeEditor` is the only public type in the Editor assembly; `[CustomEditor]` works
  internal.

**Magic-number stragglers worth naming (the package names most of its constants — these are
the residue):** Laplacian `0.25f` ×3 silently coupled across `WaterVolume.Frames.cs:40/47` ↔
`WaterSimulation.cs:103`; pass-ordering `InjectionPoint + 2` (`WaterUnderwaterFogFeature.cs:133`)
coupled to LargeBodyAtmosphere's `+1` with nothing enforcing it; JONSWAP `-1.25f`
(`WaterWaveBank.cs:266`) amid fully-named siblings; ~15 unnamed epsilons across 5 magnitudes in
C# + ~17 in shaders (files that name their other epsilons); foam default values duplicated
across `WaterFoamProfile` ↔ `WaterFoamParticles` ↔ `WaterSplashEmitter` (the "null profile =
identical" contract holds only while unshared copies agree); wizard/kit constants hand-mirroring
Runtime defaults (`EdgeFoamBorderWidth 0.08`, wave-scale 1-500 range, sun `(2,2,-1)`);
`ln(2)` declared twice in one file (`WaterVolumeEditor.Volume.cs:47/116`); god-ray IGN frame
multiplier `5.588238` and Gaussian blur weights ×2 unnamed in `LargeBodyGodRays.shader`.

**Dead-ish, low stakes:** `CHUNK_SHAPE_*` aliases with zero readers
(`WaterChunkPrimitive.hlsl:15`); `PropBaseColor` const dead in the kit while the converter
re-declares its own; `WEBGPUWATER_DEV`-fenced ObstacleDebug partial (deliberate per its header —
confirm you still want author tooling shipped); `WaterSceneBuilder.DemoMaterialsRoot` can only
ever hit its fallback (samples import to `Assets/Samples/...`, never that path).

---

## What was checked and found CLEAN (don't re-audit)
No commented-out code anywhere. No unused private methods in Runtime (all verified across
partials; the two zero-caller hits are attribute entry points). No written-never-read fields.
No silent catch blocks (the single Runtime catch warns loudly and disables). No
guard-clause-fixable nesting. No inheritance abuse. Shader uniform set fully consumed. Menu
paths all route through MenuRoot except one documented deliberate bypass. SWE/shoal/curl/
roller/hero-wave: fully purged (only the two comment relics above). Asset-path constants
otherwise all verified against the real tree.

## Suggested batching IF you want fixes (your call — nothing touched)
- **SD1 (cheap, highest value):** S1 validator registration + S4 dead function + the two
  actively-misleading comments (whitecap min(), LogPrefix claim) + stale interactor comments.
- **SD2 (behavior-relevant):** S2 NaN guards (4 entry points, ~12 lines).
- **SD3 (design-gated):** S3 caustic-plane answer → shared projection helper; the ×3 caustic
  sampling block dedupe rides along.
- **SD4 (when boat creator happens):** S5 is already its spec.
- The god-function splits and API-surface tightening are worth doing only as touched-anyway
  refactors — flagged for awareness, not urgency.
