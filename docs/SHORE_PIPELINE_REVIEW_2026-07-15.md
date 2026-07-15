# Shore Wave Pipeline Review — sync beat, issues, param simplification, roller particles

Date: 2026-07-15. **Review only — no code was touched.** Everything below was verified against the
live tree (staged tonight) unless marked *(plausible — needs an in-editor eyeball)*. References:
KWS1 shoreline source (`ShorelineWavesPass.cs`, `KWS_ShorelineWaves.shader`,
`KWS_ShorelineFoam_Common.cginc`, `KWS_ShorelineFoamParticlesCompute.compute`), KWS2/Crest notes
from the master plan, and the full staged surf/curl/foam stack.

---

## 1. The sync story — why it reads out of sync, and what a "master beat" actually needs

First the good news, because it changes the shape of the fix: **you already have a master beat.**
Every height-moving consumer evaluates the SAME closed-form field on the SAME clock:

| Consumer | Time source | Verified |
|---|---|---|
| Surface vertex fronts + swash film | `_WaveTime` global | ✓ |
| Surface fragment whitewash/breaker/glaze | `_WaveTime` | ✓ |
| Curl lip sheet | `_WaveTime` (`WaterSurfCurl.hlsl:149`) | ✓ |
| Sim foam injection | `_ShoreFoamTime` = the same `_waveTime` (`WaterVolume.cs:2954`) | ✓ |
| Particle spray gates + density glue | `_ShoreFoamTime` = same | ✓ |
| CPU buoyancy mirror | `_waveTime` | ✓ |

The beat exists: `phase = sWarp/L + t/T`, crest arrival at the waterline at `frac(t/T) = 0.5`,
and the swash convention (`arrivalIndex = floor(t/T − 0.5)`) is **exactly consistent** with it —
re-derived and confirmed. The desync you SEE comes from six specific places that fall off that
beat. Fix these, and no new "beat system" is needed; publish the beat *explicitly* (see 1.7) and
every future system (particles, audio, VFX) locks on for free.

### 1.1 Foam particles are not on the beat at all — the biggest offender
The front field only *classifies* particles; it never spawns or moves them:
- Spawn = stochastic roll on the ripple-sim foam texture (`WaterFoamParticles.compute:428-450`) —
  so particles appear a random sim-advection-latency AFTER the front passes.
- Motion = flow drift + wind + noise curl, relaxed by drag (`:617-636`) — nothing references the
  front phase; the front outruns its own foam ("wash").
- Ballistic integration uses raw `Time.deltaTime` (`WaterFoamParticles.cs:332,356`) — **not**
  scaled by the body `timeScale`, and noise drift uses `Time.time` (`:357`). Slow-motion or a
  paused body desyncs spray from the fronts that threw it.
- Lifetime is random 1.5–4 s, unrelated to the front's collapse phase (`:529` + envelope).

### 1.2 The curl visibly retracts at hand-over — CONFIRMED in code
`WaterSurfCurl.hlsl:202` sets `weight = rollGates · cresting · (1 − broken)`, and `:217` scales
the geometry by `smoothstep(MIN_WEIGHT, FOOT_BLEND_END, weight)`. The foot blend was meant to be
*spatial* (ease the sheet edge onto the water), but `weight` also carries the *temporal* lifecycle
fade — so as `broken` takes the wave, the whole rolled lip geometrically pulls back onto the base
surface before the discard line hits. That is the same "un-roll" family of bug CURL-2.1 fixed for
the roll angle (`SURF_CURL_ROLL_START` comment says exactly this), reintroduced through the foot
blend. **Fix:** ease the delta by a *spatial* foot term (e.g. `smoothstep` on `crestness`/lever
distance) and let the temporal fade act on fragment alpha/discard only.

### 1.3 Ambient swell and surf fronts bunch on different curves
One knob (`shoreCompression`) feeds two DIFFERENT warps: ambient swell uses `ShoreWarpExtra`
(reach `max(4·shoalDepth, 8)` — `WaterShore.hlsl:158-164`) while fronts use `SurfWarpDistance`
(reach `2·L` — `WaterSurfWaves.hlsl:141-145`). Crest spacing of the swell and the fronts compress
differently as they approach the beach, so the two wave families visibly slide against each other
in the hand-over band. A master beat should use ONE warp function.

### 1.4 No dispersion link between the front speed and anything else
Front speed is `L/T` from two free knobs; the swell moves at `ω = √(gk)`; the physical shallow
speed is `√(gd)`. Fronts and the swell they allegedly grow out of travel at unrelated speeds.
This is the deepest "feels off-beat" cause — even with perfect clocks, the *spatial* rhythm
disagrees. Recommendation in §4: derive `L` from `T` (deep-water `L0 = 1.56·T²` scaled by a fixed
fraction), keep an Advanced override.

### 1.5 The unbounded front index slowly corrupts the beat (and CPU/GPU parity)
`frontIndex = floor(sWarp/L + t/T)` grows forever; it feeds `frac(sin(n·12.9898)·43758.5453)`
(`WaterSurfWaves.hlsl:125-128`). GPU float32 `sin` vs CPU double `Mathf.Sin` diverge as the
argument grows — within a long session the render's per-front set amplitudes no longer match the
buoyancy mirror ("byte-for-byte" breaks), and `t/T` itself loses fractional precision. **Fix:**
wrap the beat — publish `_waveTime mod (T·N)` (e.g. N = 4096 fronts) so phase and index stay
small forever. One change, fixes drift + parity + precision.

### 1.6 Small clock strays (list for completeness)
- Pond foam pattern flow + flipbook use `_Time.y` (`WaterSurface.shader:338,395`) — drifts on a
  paused body.
- Multi-body: each body republishes its own `_waveTime` to the ONE `_WaveTime` global
  (`WaterUniformPublisher.cs:139`) and `WaterShoreDepthField.Publish` runs unconditionally per
  body per frame (`WaterVolume.cs:2136`) — a second body (even a pond with bed depth off)
  publishes `_ShoreDepthValid = 0` and can kill or flicker the whole coastline depending on
  update order. Single-body scenes are safe; worth a guard before any demo scene grows a pond.
- Sim's waterline lace uses its own depth window and no segmentation/rhythm
  (`WaterSim.compute:578-581` — commented as deliberate) while the rendered lace is segmented and
  arrival-seeded; the two laces disagree in position and pattern.

### 1.7 The concrete "master beat" proposal (small, no rewrite)
Publish the beat once, consume it everywhere:
1. `WaterVolume` computes per frame: `surfBeatTime = _waveTime mod (surfPeriod · 4096)` and
   publishes `_SurfBeatTime` (replaces raw `_waveTime` in every surf consumer) — fixes 1.5.
2. Unify the compression warp: fronts and ambient both call `SurfWarpDistance` — fixes 1.3.
3. Derive front spacing from the period by default (dispersion link) — fixes 1.4.
4. Curl foot blend → spatial term — fixes 1.2.
5. Particles: scale `_DeltaTime` by the body `timeScale`, swap `Time.time` → `_waveTime` for the
   noise drift (two lines) — and the real fix, the dedicated roller system in §5.
Optional but cheap: expose `float WaterVolume.SurfBeat01` (frac(t/T)) + `SurfFrontIndex` on the
C# side so gameplay/audio/VFX can subscribe to the same beat.

---

## 2. Bugs and wrong ways found (beyond sync)

Ranked. LK = look-killer, B = bug, P = parity, S = smell.

1. **[LK] Barren foam ring / lee-side foam hole.** `LbwGeometryFoamGate` suppresses the FFT/ocean
   whitecaps inside `0.7·band..1.5·band` with **no exposure and no wet term**
   (`WaterLargeWaves.hlsl:394-399`), but the replacing surf whitewash is masked by
   `SurfFieldMask` (`0.55·band..band` × wet × exposure). On the lee side of an island, and in the
   `band..1.5·band` ring, existing whitecaps are killed and nothing replaces them. Align the two
   windows and give the gate the same exposure term.
2. **[B] Front-cell C0 seam.** At the cell boundary (`f = 0↔1`, half a spacing offshore of each
   crest) `setAmp`/`SurfCrestFactor` jump per front while the bore shape is still ≈0.43 there
   (`WaterSurfWaves.hlsl:241,285-286`) — with sets/segmentation up, a shore-parallel height+foam
   step marches mid-way between bores, and the ε=0.5 m FD slope spikes on it (normal seam — this
   is likely your reviewer-flagged "sparkle line", and it is NOT fixed by lowering Set Strength
   alone). Fix: cross-fade the two neighbouring fronts' amplitudes near the cell edge (or blend
   `setAmp` over `f` with its neighbour), and hold `frontIndex` fixed across the FD step (the
   comment at `:395` claims this but the code re-derives it).
3. **[B] WGSL derivative hazard on every foam edge.** `tex2D`/`tex2Dgrad(ddx…)` inside
   fragment-varying branches (`WaterSurface.shader:1189/1200` surf whitewash, `:1061/1093`
   whitecaps, `:369-376` pond foam) — undefined derivatives in non-uniform control flow on
   WebGPU; expect mip sparkle at coverage boundaries (or Tint/naga validation errors). Sample
   unconditionally / `tex2Dlod`, gate the use.
4. **[B] Vertex vs fragment swash disagree.** Vertex film lift samples `ShoreSample(worldPos.xz)`
   AFTER chop/hero horizontal displacement and gates on `_ShoreDepthValid`
   (`WaterSurface.shader:625-637`); the fragment clip/glaze evaluates at `largeWaveSourceXZ`
   inside the `_BedValid` block (`:1251-1279`). Two consequences: slight phase mismatch between
   lifted film and glaze under chop, and — if the pool-frame bed bake fails while the world bake
   succeeds — film geometry with no clip = floating sheet on dry sand. Evaluate both at the same
   source xz, gate both on the same validity.
5. **[P] CPU mirror misses the band-limit weight.** Shader drops sub-`minWavelength` components
   by camera distance (`WaterLargeWaves.hlsl:138-139`); `LargeWaveField.AccumulateBand` has no
   such term — far-from-camera floaters bob on waves the render zeroed. Also
   `ApplyShoreToFftSample` collapses per-cascade shoal weights to one swell-wavelength weight
   (documented, but it IS height error in the shoal band on FFT oceans).
6. **[S] Extend the lockstep guard.** `Editor/WaterWaveConstantsValidator.cs` exists and guards
   the swell `LBW_*` set — verify it also covers the 20 `SURF_*` constants and the surf-front
   mirror functions after the CURL-3 additions (the bore-width comment in `LargeWaveField.cs`
   proves drift happened at least once before the guard). *(Guard coverage not re-read this
   session — check when next in the editor.)*
7. **[S] Duplicated "keep in sync by comment" constants.** `SHORE_BORDER_FEATHER` ×4 files,
   `ShoalWeight` ×3 implementations, five different depth-window formulas for "where the surf
   zone is" (mask, lace, sim lace, foam gate, shallow clarity). Any retune touches 4–5 files —
   this is the same mirror-tax pattern the SwellShoalFactor saga taught. Fold what can be folded
   into `WaterShore.hlsl` + the sim-side replica pair.
8. **[B/minor] Half SDF distance quantizes offshore fronts.** RGBAHalf B channel: 0.25–0.5 m
   steps beyond ~512 m from shore — front lines step slightly on very large fields and drift
   from the full-float CPU arrays.
9. **[Perf, big] The open-water vertex pays the field ~2.5×.** `LargeBodyWaveHeight` +
   `LargeBodyWaveDisplacement` each independently do `ShoreSample` + `EvaluateSurfWaves` (each =
   2 `SurfFrontHeight` FD evals) + the 16-component swell loop, then the swash adds a THIRD
   `ShoreSample` (`WaterSurface.shader:588-589, 627-633`). The combined `*Shore` variants exist
   in `WaterLargeWaves.hlsl` precisely to prevent this and the vertex never calls them. Cheapest
   meaningful perf win in the file.
10. **[Perf] Curl sheet cost.** 2 strips × ~74k verts, each running the full water vertex path
    PLUS 4× `SurfCurlEvaluate` (offset + 3-tap FD normal), each with `ShoreSample` +
    `SurfComputeFrontTerms` → order 600k front evaluations/frame even when centimetres of lip are
    visible. Tier it: skip the under strip when the lip is inactive, drop FD normal to 2 taps,
    and gate density by camera distance.
11. **[Perf/minor] Sim foam kernel + `SurfLipAt` compute the FD slope they never read** (the
    `EvaluateSurfWaves` slope path) — a cheap overload without the slope halves front math there.

Verified clean (worth knowing): swash/lace phase conventions, wet-line two-front continuity at
rollover, cosh clamps, WGSL loop bounds, the sim/render lace double-count guard, CPU bilinear +
feather replication, and the whole SURF-PHYS constant set parity (all `SURF_*`/`LBW_*` constants,
floors, windows, order of ops — checked term by term).

---

## 3. Param simplification — 3 hero knobs + Advanced

Current state: ~28 fields flat in one inspector section (3 subheadings, no foldouts), plus 18 on
the curl component. Full inventory is in the appendix table of this review (agent-verified with
file:line). The good news: the three hero knobs you want already exist and everything else is
already consumed through exactly three publish paths (`Publish`, `FillShoreFoamState`,
`ShoreWaveCtx`), so folding is cheap.

### Hero knobs (always visible under "Shore waves")
- **Strength** → `surfAmplitude`. ⚠ It is silently floored at the ocean's `swellHeight`
  (`SurfAmplitudeEffective`, `WaterVolume.cs:1138`) — below the swell the slider does nothing.
  Either remove the floor or show "effective: X m" in the inspector; a dead hero knob is worse
  than no knob.
- **Period** → `surfPeriod`. Already the physics driver (Iribarren `L0 = 1.56·T²`, swash rhythm,
  lace seed) — the true beat knob.
- **Length** → `surfWavelength`, but make it **Auto by default**: derive spacing from the period
  (`L = k·1.56·T²`, k ≈ 0.20 lands the default pair 9 s → 25 m almost exactly on today's 26 m)
  with an Advanced override slider. This also closes the dispersion gap (§1.4).

### Advanced (one `WaterEditorUI.SubSection` — the crest-glow subsection is the existing idiom;
no "Advanced" fold exists yet in the editor, this introduces it)
- *Shoal transform:* `shoreShoalDepth`, `shoreRefraction`, `shoreCompression`, `shoreGreens`.
- *Front shaping:* `surfBandDepth` (candidate to derive from amplitude/γ later), `surfSetStrength`,
  `surfLean`, `surfAmbientFade`, `surfDirectionality`.
- *Crest segmentation:* `surfCrestLength`, `surfCrestVariation`, `surfCrestPersistence`.
- *Swash:* `surfSwashAmplitude` (default 1 = physics — belongs in Advanced by its own tooltip).
- *Foam:* `surfFoamGain`, `surfWaterlineFoam`, `surfFoamStrength`, `surfFoamFeather`,
  `surfFoamTileSize`, `surfFoamColor` (note: `surfFoamColor.a` duplicates `surfFoamStrength` as a
  second master intensity — consider dropping the alpha semantics).
- *Bake/quality:* `bedResolution` (+ the bed tint trio stays in the Bed Depth base section — it
  is unrelated to surf and shouldn't be counted against the surf UI).

### Curl component
Hero: `masterGain`, `rollSpeed` (already timing-pure). Advanced: pivot/lip/shoulder five.
Debug-only (hide behind a Debug foldout or `HideInInspector` in live mode): `mode`, `centerDepth`,
`beachSlope`, `plungeOverride`, `renderFullFront` — all inert in FollowBreakLine mode anyway.
Mesh segment counts → quality tier, not a look knob.

Rule for everything folded: the C# mirror (`ShoreWaveCtx`) and `FillShoreFoamState` re-list every
field — any fold/derive must land in those two sites + `Publish` together, or parity breaks.

---

## 4. Dedicated rolling-wave particles — why, and the design

### Why the current system can't do it (verified, the short version)
`WaterFoamParticles` is a *wash* system by construction: spawn = random roll on the sim foam
texture (lags the front), motion = flow/wind/noise with drag (front outruns it), lifetime =
random 1.5–4 s, spray always lands and converts to washing floaters at 40% speed, and — the one
you named — **stretching is unconditional**: the quad elongates `1 + min(4, speed·3)` along
velocity (`FoamParticles.shader:174-196`), so a 6 m/s lip droplet flies at the 5× clamp its whole
arc. Budgets make it worse exactly at a breaker (256 spawns/frame, 6 spray/screen-tile with
demotion to floaters, r² distance cull at 80 m). None of this is fixable by tuning; they are the
system's design goals (ambient foam), not defects.

### What KWS1 actually does (read from source tonight — the lesson is sharper than the memory)
- The shoreline wave and its foam particles share ONE clock: `KW_Time · KW_GlobalTimeScale +
  wave.timeOffset` drives BOTH the displacement flipbook frame AND the particle animation frame
  (`KWS_ShorelineWaves.shader` + `KWS_ShorelineFoam_Common.cginc:RenderFoam`). Sync is not
  maintained — it is *structural*: one clock, one frame index.
- The particles are **baked per flipbook frame** (positions + alpha + AO packed in a
  StructuredBuffer, each particle linked to its next-frame index and lerped between frames) —
  particle motion IS the wave animation, never simulated. That is why their foam never washes
  off the wave.
- Rendering is **fixed-footprint screen splats** (`InterlockedAdd` into a buffer, 1–4 px radius
  by distance, then one composite pass) — a point has no axis, so nothing can stretch, and
  thousands of particles cost one composite.

### The design (procedural KWS1: "baked-by-math" instead of baked-by-asset)
New component + kernel pair, `WaterSurfRollerParticles` (sibling of, not a mode of, the existing
foam particles):
1. **Emitter — phase-locked, not stochastic.** Per frame, walk the break line the same way
   `WaterSurfCurl.TrySolveBreakLine` already does (CPU, closed-form, no readback), and emit along
   the crest segments where `breaker × plunge` (from `SurfComputeFrontTerms` — the binder
   `ShoreFoamState.BindTo` already delivers everything a kernel needs) crosses a threshold.
   Emission budget per front, not per frame — a front owns its particles.
2. **Motion — kinematic in the front's frame.** A particle stores its front index + spawn phase +
   a local offset; each frame its position is a *function* of the front's current terms: carried
   at the front's phase speed, orbited around the same attractor pivot the curl sheet uses
   (`_SurfCurlShape` math, already in `WaterSurfCurl.hlsl`), thrown ballistic only for the final
   spray moment, then killed (or handed to the EXISTING wash system as ordinary foam — wash is
   the right behaviour AFTER the roller is done). No drag, no flow drift, no noise motion —
   at most noise on the offset. Result: the particle cloud IS the wave, like KWS1's VAT, but
   procedural — no baked assets, any coastline, and automatically on the master beat.
3. **Lifetime = phase window,** not seconds: born at `overCap ≈ rollStart`, dead shortly after
   `broken` completes (+ small hash jitter). Set lulls emit nothing for free.
4. **Rendering — no stretch, by construction.** Fixed-size camera-facing billboards from the
   existing atlas (`FoamParticleAtlas`/flipbook machinery reuses as-is) with the shared
   `WaterFoamCommon.hlsl` lighting — and NO velocity-stretch block; near/far tiering can splat
   the far field into the existing density buffer (`RasterizeDensity`/`FoamDensityComposite`
   reuse verbatim — that's the KWS1 splat idea already in the codebase).
5. **Reuse list (all verified reusable as-is):** `ShoreFoamState` binder + `SurfSampleAt`,
   pow2 ring-buffer pool idiom, `Graphics.RenderPrimitives` + SV_VertexID quad path, capability
   gates + fallbacks, atlas + flipbook, `FoamParticleEnvelope`/lighting/erosion helpers, density
   splat + composite pair. **New:** the emitter kernel, the kinematic update, a stretch-free
   billboard vertex path, phase-window lifetime. The two-kind enum, landing conversion and
   sim-window kill box stay untouched in the old system.
6. **Budget:** its own small pool (2–4k), its own draw — so a breaking set can never evict or be
   evicted by ambient foam.

This slots into the CURL-3 spray story: the lip spray you shipped keeps its role as incidental
droplets; the roller system is the *structured* cloud that makes the barrel read at KWS1 level.

---

## 5. Suggested order (each a small reviewable chunk, per your rules)

1. **Beat repairs** (§1.7 items 1–4 + §2.1–2.2): wrapped beat time, one warp, derived length,
   curl foot-blend fix, foam-gate window alignment. Small diffs, all in the files that already
   exist; biggest visible payoff per line.
2. **WGSL foam-edge sampling fix** (§2.3) — WebGPU correctness.
3. **Inspector fold** (§3) — pure editor work, no runtime risk; introduces the Advanced idiom.
4. **Roller particles v1** (§4): emitter + kinematic carry + no-stretch billboards, curl orbit in
   v2. Judge on the terrain-lake scene against KWS1 footage.
5. Parity cleanups (§2.5–2.8) + perf passes (§2.9–2.11) as background chores.

Nothing here proposes touching the SWE decision, Layer A's bake mechanism, or the analytic
backbone — the architecture held up well under review; the findings are joints, not bones.
