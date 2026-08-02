# 🏖️ PROMPT — Shore: fix what's broken, then make it wow

**For a dedicated session.** Written 2026-08-02, right after the ocean spectrum rewrite. Everything
below marked ✅ was verified in the source this session; everything marked ❓ was NOT read and must be
before it is trusted. **Nothing here is authorised to be coded — research and design first, then ask.**

---

## 0. Read these first

- `docs/PLAN_ocean_spectrum_v1.md` — the ocean rewrite this sits on top of.
- Project memory: `shore-stack-layering-2026-08-02`, `ocean-spectrum-diagnosis-2026-08-02`.
- The ocean now authors **Significant Height (m)**, **Peak Wavelength (m)**, **Peak Sharpness γ**. The
  shore does not know about any of them. That is the theme of this whole document.

---

## 1. The stack as it actually is ✅

Deep water → sand, in order:

1. **`OceanCascadeShoalWeight`** (`WaterLargeWaves.hlsl:245`) attenuates each FFT cascade:
   `lerp(1, ShoalWeight(depth, λ_c), shore.influence)`.
   `ShoalWeight = lerp(saturate(2·depth/λ), 1, smoothstep(0.35·band, band, depth))` — so **nothing is
   attenuated deeper than `band`**, and it only fully bites below `0.35·band`.
2. **`SurfAmbientWeight = 1 − surfMask · _SurfAmbientFade`** (`WaterSurfWaves.hlsl:650`, fade default
   0.8) — the hand-over: where fronts own the surface the ambient sea is faded out.
3. **Surf fronts** — `EvaluateSurfWaves`, amplitude `SurfAmplitudeEffective = max(surfAmplitude, SwellHeight)`.
4. **`ShoreGreenGain`** (`WaterShoreMath.hlsl:141`) — Green's law `(band/depth)^0.25`, capped by
   `_ShoreGreens`, faded out over the last `0.35·band`.

Authored ranges that matter:

| field | range | default |
|---|---|---|
| `surfAmplitude` | 0–3 m | 0.8 |
| `surfPeriod` | 2–30 s | 9 |
| `surfBandDepth` | 0.5–20 m | 6 |
| `shoreShoalDepth` | 0–30 m | 4 |
| `surfAmbientFade` | 0–1 | 0.8 |

`SurfWavelengthEffective` auto = `clamp(0.2 · 1.56 · T², 4, 120)` — front spacing from the period.

---

## 2. Confirmed defects, with evidence ✅

### 2.1 Surf fronts have NO distance band-limit — this is the big one

`WaterSurfWaves.hlsl` contains **no** camera-distance term and **no** minimum-wavelength cut (grepped:
zero hits for `camDist` / `MinWavelength` / `distance(`). And `surf.height` is added **raw**, *after*
the FFT term and *outside* the cascade distance fade that protects everything else:

```
WaterLargeWaves.hlsl:405   height = OceanFftDisplacementShore(...).y * _LargeWaveAmplitude
                                    * SurfAmbientWeight(surf.mask) + surf.height;
WaterLargeWaves.hlsl:437   height = (fft.y * _LargeWaveAmplitude * ambient + surf.height) * edge;
```

On the coarse far clipmap that is sub-Nyquist geometry — the fronts visibly deform at range. Observed
this session the moment their amplitude went up. **Fix this before touching front size**, or any
increase re-creates it. The FFT path already has the pattern to copy (`OceanCascadeDistanceFade` +
per-cascade visible areas, `WaterShared.hlsl:74`).

### 2.2 The FFT branch never shoals UP

`ShoreGreenGain` has exactly **one** call site: `WaterLargeWaves.hlsl:206`, inside
`EvaluateLargeBodyWaveShore` — the **analytic** branch. The FFT branch gets `OceanCascadeShoalWeight`
attenuation and nothing else. So an ocean's waves only ever *shrink* toward the beach; real ones rear
up first, then break. Bert has declined this twice — do not add it without asking again, but know that
with the shoal band now opening much further out on a big sea, the fade happens over a longer stretch
and may read as "the ocean quietly gives up".

### 2.3 The coast doesn't know what sea is arriving

`surfAmplitude`, `surfPeriod` and `surfBandDepth` are authored independently of the ocean. So a 6-second
sea can break at a 9-second rhythm, and an 8 m sea can produce the same breakers as a millpond.

⚠️ **A hard floor is NOT the fix — that was tried and reverted this session.** Flooring
`SurfAmplitudeEffective` on the offshore height drove fronts to 8 m against a 0–3 m slider, ~3× the
largest value the renderer was built for, and overrode a hand-tuned coast. See §4.

---

## 3. Research to do before designing

Ground the numbers; do not invent them.

- **Breaking criterion.** `H_b = γ·h_b`, γ ≈ 0.78 nominal (McCowan, solitary wave) but genuinely
  varies ~0.6–1.2 with beach slope and deep-water steepness. Find the modern parameterisation
  (Battjes–Stive and successors) — `slopeTan` is already carried in `ShoreData`, so slope dependence
  is available rather than assumed.
- **Breaker type.** Iribarren number `ξ = tanβ / √(H₀/L₀)` separates spilling / plunging / surging.
  That single number should drive whether a front spills gently or throws a barrel — and we already
  have both the slope and (now) the deep-water steepness from `Hs/λp`.
- **Run-up.** Hunt's formula and Stockdon et al. for swash excursion — the current
  `surfSwashAmplitude` is authored blind.
- **Wave setup.** Breaking raises the mean water level shoreward of the break line by ~15–20% of the
  breaker height. Nobody models this and it is cheap — it makes the whole surf zone sit *higher*, which
  is a strong "this is real" cue.
- **Rendering references.** Look for recent (2024–2026) work on real-time surf zones specifically —
  the ocean literature is deep but the surf zone is under-covered. Check whether the hybrid
  wave-particle/FFT paper (arXiv 2511.02852, already read for the ocean work) has anything on
  shoaling injection.

---

## 4. The proper link — design sketch, NOT authorised

The failure of the hard floor was diagnostic: **it coupled the magnitude without coupling the
geometry**. Taller fronts still broke in the same shallow water at the same rhythm, which is
geometrically impossible and looked it. Couple all three or none.

Use the auto-with-override pattern already in the codebase (`surfWavelengthAuto` + a derived readout):

1. **Period ← the sea.** `Tp = √(2πλp/g)` — a 60 m sea is a 6.2 s sea. Cheapest change, biggest
   coherence win, and it makes the sets arrive at the ocean's own rhythm.
2. **Breaking depth ← the breaker.** `surfBandDepth = H_b / γ(slope)`. A bigger sea then starts
   breaking further out on its own — which is *also* what keeps the fronts on mesh dense enough to
   resolve them, so it partly fixes §2.1 from the other end.
3. **Amplitude last, as a TRANSFER not a height.** Replace `surfAmplitude` (metres) with **Surf Energy
   Transfer** 0–1: how much of the offshore sea actually reaches this shoreline — a sheltered bay, a
   reef, a headland. Height becomes `Hs₀ × shoaling gain × transfer`, clamped to what the renderer
   handles. The artist keeps the *look*; the *scale* follows the sea for free.

Migration: `surfAmplitude` → transfer needs a unit change, so convert using the scene's current sea
state so existing coasts land where they are.

---

## 5. The wow list — pick, don't do all

Ordered by (visible impact ÷ risk). None of these are worth starting before §2.1 is fixed.

- **Plunging vs spilling from the Iribarren number.** One derived number switching the front's whole
  character — barrel-throwing on a steep beach, gentle spill on a flat one. The biggest visual return
  in this list because it is *variety*, and the current surf reads samey.
- **Wave setup.** Mean water level rises shoreward of the break. Cheap, and the whole surf zone
  suddenly sits right.
- **Spray off the crest.** There is already `PLAN_plunging_particles_v1.md` and the bubbles/spray
  research in `docs/` — the crest of a plunging front is where that work pays off.
- **Backwash interference.** The returning swash meeting the next front is what makes a real beach
  busy. Analytic and cheap: the swash phase is already closed-form.
- **Longshore drift.** Foam and spray tracking *along* the beach rather than straight in, driven by
  the angle between front and shoreline — both already available.
- **Wet-sand response tied to run-up maximum** — partly there (`wetLevel`), worth auditing.
- **Rip currents.** Highest effort, highest payoff for a "real coast" read. Needs a shoreline-parallel
  flow field; probably its own session.

---

## 6. Traps — earn these for free

- ❓ **`EvaluateSurfWaves` (51 KB, `WaterSurfWaves.hlsl`) has NOT been read.** Its inputs and outputs
  are understood; how amplitude actually becomes front height inside it is not. **Read it before
  wiring anything to amplitude** — there may be internal scaling that changes all the arithmetic.
- **Two bands, not one.** `_ShoreShoalDepth` (attenuation, floored at 2×Hs) and `_ShoreGreenBandDepth`
  (authored, Green's law) were split this session precisely because one number was doing two opposite
  jobs. Do not re-merge them.
- **`linear` is a reserved word in D3D HLSL** (with `centroid` / `nointerpolation` / `noperspective` /
  `sample`). `float linear = 0;` gives a bare "unexpected token" far from the cause. Cost one build.
- **Signed-int `%` is slow and warns.** The FFT compute now masks instead (`OCEAN_TILE_WRAP`).
- **Every HLSL change has a CPU mirror.** `LargeWaveField.cs` mirrors `WaterLargeWaves.hlsl`,
  `WaterOceanSpectrum.cs` mirrors the spectrum, `WaterWaveBank.cs` mirrors `WaterWaves.hlsl`. Buoyancy
  diverging from the render is the classic bug here. `WaterWaveConstantsValidator` guards the constant
  pairs — add to it when adding constants.
- **Grep the DEVICE, not a staged copy.** A stale grep missed `WaterWizardWindow.cs` this session and
  cost a compile error.
- **Profile a Development Build, not the editor.** Editor shader-variant compilation shows up as
  render-thread spikes with an idle GPU and cost an hour this session. **The water's real GPU cost is
  ~5 ms** — diff against that.

---

## 7. Decisions to put to Bert before any code

1. Fix §2.1 (front distance band-limit) first — confirm, since it is invisible until fronts get big.
2. Green's law on the FFT branch (§2.2) — declined twice; ask once more now that the shoal band is
   wider, or drop it permanently.
3. How far to take §4 — period only, period + breaking depth, or the full transfer rig with a unit
   change and migration.
4. Which wow items from §5, and whether the plunging/spray work merges with the existing
   `PLAN_plunging_particles_v1.md`.
