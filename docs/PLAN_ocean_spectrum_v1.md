# 🌊 PLAN — Ocean spectrum, scale, and sea variety (v1)

**Date** 2026-08-02 · **Status** RESEARCH + DIAGNOSIS COMPLETE, **NO CODE WRITTEN**, decisions gated on Bert.

You asked for five things:

1. Too much **uniformity at distance**.
2. Can't author **small but agitated** seas.
3. No control of **global scale / wave numbers** for the big waves.
4. Water Volume editor: **decouple wind from wave scale**, clean the small-wind-wave section.
5. (added at lunch) **Gulliver-small or giant** oceans, and **variation across one sea** — real water changes with current and wind rotation.

All five turn out to be **the same root cause wearing five hats**: the FFT ocean has no spectrum
parameterisation. It has a raw Phillips density whose *only* input is wind speed, and a fixed set
of hardcoded cascade constants. Everything below follows from that.

---

## 1. Root causes — proven, with line references

### 1.1 ☠️ THE BIG ONE: at any authorable wind, the far-field cascade is EMPTY

`OceanFft.compute:157-166` — the spectrum is omnidirectional Phillips:

```
S(k) = A · exp(−1/(k²L²)) / k⁴ · exp(−k²·small²)      A = 4e−3, L = U²/g, small = L·1e−3
```

The `exp(−1/(k²L²))` factor is the long-wave cutoff. It kills everything with **λ ≳ 2πL = 2π·U²/g**:

| Wind speed (slider range is 0–15, `WaterVolume.Settings.WindWaves.cs:34`) | L = U²/g | Longest wave with any energy |
|---|---|---|
| 3 m/s (the **default**) | 0.92 m | **≈ 5.8 m** |
| 8 m/s | 6.5 m | ≈ 41 m |
| 15 m/s (**slider max**) | 22.9 m | ≈ 144 m |

Now cross that against the cascade bands (`WaterOceanFft.cs:51`, `DefaultCascadeBands = {5, 20, 100, 600}`)
and the per-cascade view distances (`WaterOceanFft.cs:71`, `DefaultVisibleAreas = {40, 160, 800, 4800}`,
faded cubically by `OceanCascadeDistanceFade`, `WaterShared.hlsl:74-78`):

- Past **40 m** cascade 0 (waves ≤ 5 m) is gone.
- Past **160 m** cascade 1 (5–20 m) is gone.
- Past **800 m** cascade 2 (20–100 m) is gone.
- **Past 800 m the ONLY cascade still drawn is cascade 3, band 100–600 m** — which Phillips has
  suppressed to *numerically zero* at every wind speed the slider allows.

So beyond 800 m the sea carries **no wave energy at all**. What you're seeing out there is not a
uniform sea; it is a **flat plane**, shaded only by `OceanFftFarSlopeFloor`
(`WaterLargeWaves.hlsl:292`, `float4(0.15, 0.20, 0.25, 0.25)`).

**And that floor doesn't work either.** It's applied as `max(fade, floor[c]) * tap.xz`
(`WaterLargeWaves.hlsl:327`) where `tap` was sampled at `lod = log2(1 + camDist/domain)`
(`WaterLargeWaves.hlsl:324`). At 2000 m, cascade 0 (tile 20 m) selects mip **6.7** of a 128² texture
— 1–2 texels, i.e. the *average tilt of the whole tile*, which for a zero-mean Gaussian field is
**≈ 0**. The floor therefore restores 15 % of nothing. This is the classic normal-mip variance
collapse; the fix is LEAN/Toksvig-style **moment** filtering, not a scalar floor (see §2.2).

### 1.2 The only thing that CAN fill the far field is a single monochromatic bump

`OceanFft.compute:212-220` — `OceanSwell` is a narrow Gaussian in |k| (`OCEAN_SWELL_WIDTH = 0.12`)
at `swellWavelength` (default **140 m**, lands in cascade 3), directionally sharpened by
`pow(cos θ, 6)` (`OCEAN_SWELL_DIR_POWER`).

`swellHeight` defaults to **0** (`WaterVolume.Settings.Ocean.cs:43`). So out of the box the far
field is flat; turn the swell up and it becomes **one wavelength, one direction, cos⁶-narrow** —
corduroy. Both states read as "too uniform". That's your symptom exactly, and it is not a tuning
problem.

### 1.3 The four cascade tiles are exact multiples of each other → hard 2400 m repeat

`WaterOceanFft.cs:212`: `_domainSizes = _bandMax * CascadeTileOversample` with oversample 4
(`:66`) ⇒ tiles = **{20, 80, 400, 2400} m**. 80 = 4×20, 400 = 5×80, 2400 = 6×400.

`lcm(20, 80, 400, 2400) = 2400`. **The entire ocean repeats exactly every 2400 m.** With
`clipmapOuterRadius` at 10 000 m (`Settings.Ocean.cs:397`) that's ~8 identical copies across the
visible sea. WSCG 2025's stated rule is the opposite: *"choose the LOD scales such that these are
not simple fractions of each other, so their combined period is as large as possible."*

### 1.4 No fetch ⇒ "small but agitated" is unreachable

Steepness is what "agitated" means. From the verified fetch-limited growth laws (Hasselmann/JONSWAP,
NOAA review), with dimensionless fetch `F̂ = gF/U²`:

```
ω_p = 22 (g²/(U F))^(1/3)        λ_p = 2πg/ω_p²  = 0.01299 (U²/g) F̂^0.66
H_s = 0.0016 F̂^0.5 · U²/g        H_s/λ_p ≈ 0.123 · F̂^(−0.16)
```

**Wind speed cancels out of steepness.** U sets the *scale* (λ_p ∝ U², H_s ∝ U²); **fetch sets the
character**. Short fetch = steep and small; long fetch = swell-flat and big.

Our FFT has no fetch term, so the only "make it smaller" lever is lowering U — which shrinks λ_p and
H_s *together* and leaves steepness exactly where it was. On top of that,
`LargeWaveAmplitudeEffective = largeWaveAmplitude · (windSpeed / 3)`
(`Settings.Ocean.cs:344`) multiplies the height by U/3 as well. **Lower wind ⇒ smaller AND flatter,
always.** Small-but-agitated is structurally impossible to author today.

> 🙃 The irony: the **analytic small-wave bank already does this properly** —
> `WaterWaveBank.cs:26-33` has JONSWAP peak factor 22, γ = 3.3, σ = 0.07/0.09, and the fetch-limited
> variance law `m₀ = 1.6e−7 · F̂ · U⁴/g²`, with fetch = `2 · waveScaleMeters` (`WaterVolume.Waves.cs:114`).
> The good spectrum is on the *little* waves; the *ocean* got raw Phillips.

### 1.5 No scale knob at all — so no Gulliver, no giants

Everything that sets the size of the sea is a `static readonly` in `WaterOceanFft.cs`:
bands `{5,20,100,600}` (`:51`), height scales `{0.5,0.5,0.6,0.9}` (`:68`), visible areas
`{40,160,800,4800}` (`:71`), tile oversample `4` (`:66`), FFT size **128, fixed** (`:46,74`).
None is authorable. `largeWaveAmplitude` scales height only; `swellWavelength` moves one narrow bump.
There is no wavenumber, no peak wavelength, no global scale.

### 1.6 One wind vector for the whole planet — no variation, no current

`WaterVolume.Update.cs:97` passes a single `windSpeed` + `LargeWaveHeadingRad` into `Dispatch`, and
H0 is rebuilt only when they change (`WaterOceanFft.cs:443-455`). The swell's direction is *hard-tied
to the wind* (`OceanFft.compute:218` dots against `OceanWindDir`) — you cannot have swell from the
south-west under a north wind, which is the single most recognisable "real sea" cue. There is **no
current concept anywhere** in the runtime.

### 1.7 The editor coupling you flagged

`WaterVolumeEditor.Motion.cs:44-62` — the **Wind Waves (spectral)** section draws
`windSpeed`, `windFromDegrees`, `waveScaleMeters`, `waveAmplitudeScale`. But:

- `waveScaleMeters` ("*sets wind-wave scale; fetch is twice this*") feeds **only** the analytic
  small-wave bank (`WaterVolume.Waves.cs:114`). Correct, but it's mislabelled — it is a *fetch*, not
  a *scale*, and its tooltip says "physical size the body half-extent represents", which is a third
  meaning again.
- `windSpeed` in that same section silently drives **three** unrelated things: the analytic bank, the
  FFT ocean spectrum, **and** the swell amplitude (`Settings.Ocean.cs:344`).

So the ocean's big-wave scale is being steered from the small-wind-wave section. That's the exact
mix-up you described.

---

## 2. What the current literature says we should do

Full briefing with equations is in the appendix file; the load-bearing conclusions:

### 2.1 Spectrum: JONSWAP → TMA, parameterised on (H_s, λ_p, γ, spread), not on (U, F)

Horvath's DigiPro 2015 reference implementation (EncinoWaves) is the graphics-standard formulation:

```
α    = 0.076 · F̂^(−0.22)                      F̂ = gF/U²
ω_p  = 2π · 3.5 · (g/U) · F̂^(−0.33)           ( ≡ 22 (g²/(UF))^(1/3) )
S_J(ω) = γ^r · α g² ω⁻⁵ exp(−1.25 (ω_p/ω)⁴)
r    = exp(−½ ((ω−ω_p)/(σ ω_p))²)             σ = 0.07 (ω≤ω_p), 0.09 (ω>ω_p)
TMA:  × φ(ω) = 0.5 + 0.5·tanh(1.8(ω√(h/g) − 1.125))     ← smooth, shader-friendly
```

γ is the "how organised is this sea" dial: γ=1 collapses to Pierson-Moskowitz (broad, confused,
choppy); γ=3.3 is the classic storm sea; γ=5–7 is stylised long-crested corduroy. **γ also moves H_s,
so it must be renormalised** or it doubles as a height slider.

Best practice from every shipping engine: **don't expose fetch.** Expose `(H_s, λ_p, γ, spread,
swell, chop)`, derive `ω_p = √(2πg/λ_p)`, and rescale the whole spectrum by `(H_s_target/(4√m₀))²`.
That makes *how big*, *how long* and *how steep* three independent axes — which is precisely what you
asked for.

### 2.2 Distance: LEAN moments, not a slope floor

Ubisoft LaForge's HPG 2024 ocean method (Lutz/Schoentgen/Gilet) explicitly pairs its aperiodic
synthesis with **LEAN mapping** (Olano & Baker 2010) so the field mips correctly — "turning the
normal map into a BRDF as the resolution decreases". WSCG 2025 openly lists *"view-distance
aliasing, because of the high-frequency simulated waves"* as unsolved in their system. LEAN stores
two extra channels:

```
B = (bx, by)                    M = (bx² + 1/s, by² + 1/s, bx·by)
Σ = [[M.x − B.x², M.z − B.xB.y], [M.z − B.xB.y, M.y − B.y²]]
```

**B and M mip and blend linearly and correctly** — unlike normals. And cascades can be summed in
LEAN space, which is the physically right way to fold sub-pixel wave slope into roughness. This
directly replaces the broken `OceanFftFarSlopeFloor` hack with something that actually preserves the
far-field slope distribution.

### 2.3 De-tiling: non-commensurate tiles now, hex tiling-and-blending later

Cheap and universal: pick tile lengths pairwise non-commensurate (WSCG 2025 ships 12/91/383 m) and
fade cascades with distance (War Thunder). Proper: LaForge's hexagonal tiling + **variance-preserving
blend** — 3 taps/map, one IFFT, genuinely aperiodic. Our field is already zero-mean Gaussian by
construction, so the Heitz-Neyret Gaussianization LUT is unnecessary; the operator collapses to

```
D(x) = Σᵢ wᵢ D(xᵢ + oᵢ) / √(Σᵢ wᵢ²)
```

### 2.4 How the engines decouple wind from scale

| Engine | Approach |
|---|---|
| **Unity HDRP** | `Repetition Size` (metres) is an *independent* knob from `Distant Wind Speed`; two separate wind speeds (distant swell vs local ripples); per-band `Amplitude Dimmer`; `Chaos` = spread |
| **Unreal Water** | **No wind speed at all.** Min/Max Wavelength, Min/Max Amplitude, and *separate* Small/Large Wave Steepness — three independent ranges. Wind survives only as a direction + angular spread |
| **Crest** | Power-per-octave sliders **labelled in metres of wavelength**; Pierson-Moskowitz is a *preset that writes the sliders*, not the runtime parameterisation |

The consensus is unanimous and it is the opposite of what we do: **physics is a preset generator,
not the parameterisation.**

---

## 3. Proposed work — 6 increments, each independently shippable

Ordered by (visible wow ÷ risk). Nothing here is written yet.

### S1 — Real spectrum: JONSWAP/TMA + Hs/λ_p parameterisation ⭐ biggest win
**Touches** `OceanFft.compute` (`OceanPhillipsOmni` → `OceanJonswapOmni`), `WaterOceanFft.cs`
(new uniforms), `Settings.Ocean.cs` (new fields).

- Replace Phillips with JONSWAP × TMA depth factor, using the constants **already proven in
  `WaterWaveBank.cs`** (22, 3.3, 0.07/0.09, 1.6e−7) so the two wave layers finally agree.
- New authored knobs: **Peak Wavelength λ_p (m)**, **Significant Height H_s (m)**, **Peak Sharpness γ
  (1–7)**, keep wind for *direction + spreading only*.
- Normalise m₀ to H_s on the CPU so γ and spread never move the wave height.
- Kills 1.1 and 1.4 outright: the far cascade gets energy at *any* λ_p you ask for, and
  small-with-steep becomes a two-slider operation.

### S2 — Derived cascades: global scale, wavenumbers, Gulliver ⭐ the "scale" ask
**Touches** `WaterOceanFft.cs:51,66,68,71` (constants → derived), `Settings.Ocean.cs`.

- Derive cascade bands from λ_p instead of hardcoding: **L₀ ≈ 6λ_p**, then step down by
  **non-commensurate ratios** (golden-ratio spaced, e.g. ×0.618) so the tiles stop being exact
  multiples. Kills 1.3.
- Derive `VisibleAreas` from the band tops too (they're currently a fixed 40/160/800/4800 that
  assumes a 5–600 m sea; on a Gulliver sea they'd fade the whole ocean out at 40 m).
- One **Wave Scale** multiplier on top for "toy pond ↔ giant sea" — plus a **dispersion time scale**,
  because real physics makes small waves *fast* and that's what makes miniature water read as toy.
  (`timeScale` already exists at `Motion` tab top; this would be the ocean-local one.)
- Expose **Chop** properly: today horizontal displacement is unconditionally chop = 1.0
  (`OceanFft.compute:265-268`) — `largeWaveChoppiness` never reaches the FFT.

### S3 — Far field that isn't flat: LEAN moments + variance-preserving mip
**Touches** `OceanFft.compute` (ComputeNormal writes B/M), `WaterLargeWaves.hlsl` (sum in LEAN
space), delete `OceanFftFarSlopeFloor`.

- Replaces the 15 %-of-zero floor with a real slope-variance carry to the horizon.
- Cheapest correct fix for horizon aliasing/mirroring. Costs one extra half4 channel set.

### S4 — Sea variety: independent swell trains ⭐ the "double wow"
**Touches** `OceanFft.compute` (`OceanSwell` gets its own direction + spread), `Settings.Ocean.cs`.

- **Swell direction independent of wind** (currently hard-dotted against `OceanWindDir`,
  `OceanFft.compute:218`). Cross-swell under a side wind is the single strongest "this is a real
  ocean" cue and it is ~15 lines.
- Optional **second swell train** (different λ, direction, sharpness). Energy just adds — phases are
  independent — so `H_s = 4√(m₀ˢʷᵉˡˡ¹ + m₀ˢʷᵉˡˡ² + m₀ʷⁱⁿᵈ)`.
- Swap the cos⁶ hack for Horvath's proper swell narrowing:
  `shape = 16.1·tanh(ω_p/ω)·swell²` — narrows the long waves while leaving ripples omnidirectional,
  which is both physically right and what actually looks like swell.

### S5 — Spatial variation: current + wind field ⭐ the other half of "double wow"
**Touches** new; `OceanFft.compute` sampling side, `WaterLargeWaves.hlsl`.

Two options, very different cost — **needs your decision**:

- **(a) Cheap, ~90 % of the look**: a *flow/current map* (or a couple of analytic gyres) that (i)
  advects the cascade UVs and (ii) rotates the sampled displacement/normal per hexagon, LaForge-style.
  One IFFT, no extra sim. Gives you wind-rotation and current-shear variation across the sea.
- **(b) Physical**: current-modified dispersion `ω = √(gk) + k·U_current` — real wave refraction and
  steepening against an opposing current (the thing that makes a tide race look like a tide race).
  Needs the current at spectrum-build time, so it's per-region, not per-texel.

### S6 — Editor re-layout: wind ⊥ scale
**Touches** `WaterVolumeEditor.Motion.cs`, `WaterVolumePropertyPaths.cs`, `Settings.Migration.cs`.

Proposed section split, top-down by scale:

```
▸ Ocean Sea State  (open water)          ← NEW, owns the FFT spectrum
    Significant Height  H_s   (m)
    Peak Wavelength     λ_p   (m)
    Peak Sharpness      γ     (1 = confused … 7 = corduroy)
    Chop
    Wave Scale          (Gulliver ↔ giant, multiplies λ_p and the cascade set)
    ▸ Swell
        Swell Height / Wavelength / Direction (independent of wind) / Narrowness
    ▸ Advanced
        Depth (TMA), cascade count, tile oversample, visible-area multiplier

▸ Wind                                   ← NEW, direction-and-spreading only
    Wind Speed        (drives whitecap threshold + spreading + drift; NOT scale)
    Wind Direction
    Directional Spread / Turbulence

▸ Small Wind Waves (ripple layer)        ← RENAMED + cleaned
    Amplitude Scale
    Fetch (m)          ← renamed from "waveScaleMeters", which is a fetch
    ▸ Advanced: Count, Direction Spread, Normal Strength
```

Migration: `waveScaleMeters` → `windWaveFetchMeters` via `FormerlySerializedAs` (the file already
has the pattern, `Settings.WindWaves.cs:39`); `largeWaveAmplitude`'s `× windSpeed/3` coupling
(`Settings.Ocean.cs:344`) gets baked into the migrated H_s once so existing scenes keep their look.

---

## 4. Risks / things I'd want to measure before committing

- **Half-float spectra.** `OceanSpecX/Y/Z` are packed as two halves per texel
  (`WaterOceanFft.cs:270-272`). A JONSWAP peak is much taller than a Phillips one at the same H_s;
  worth checking headroom before assuming the packing survives γ = 7.
- **FFT_SIZE is hard-locked at 128** (`OceanFft.compute:90`, `WaterOceanFft.cs:46,74`, validator-
  guarded). Derived cascade bands change the k-lattice, not the resolution, so this is fine — but a
  Gulliver sea wanting sub-centimetre capillary detail would need 256, which is a separate WebGPU
  threadgroup question.
- **H0 rebuild cost.** Every spectrum knob change re-dispatches `SpectrumInit`
  (`WaterOceanFft.cs:443`). Fine for authoring, but a *spatially varying* wind (S5b) would rebuild
  per region — that's the reason S5a is the recommended first cut.
- **Buoyancy must follow.** `BakeHeightField` applies the identical cascade fade
  (`OceanFft.compute:542`); any change to the fade or the band set has to move on both sides or
  floaters ride a surface that isn't drawn. Same for `WaterWaveConstantsValidator`, which guards the
  HLSL/C# constant pairs.

---

## 5. Decisions I need from you 🔒

1. **Scope for round 1** — my recommendation is **S1 + S2 together** (they're one change really:
   spectrum + the cascades derived from it), because that alone fixes uniformity-at-distance,
   small-but-agitated, global scale and Gulliver. S3/S4 next, S5 after.
2. **Parameterisation**: `(H_s, λ_p, γ)` as proposed — or do you want the *Crest* style instead
   (power-per-octave sliders labelled in metres, with JONSWAP as a preset button)? The Crest rig is
   more art-directable and less physical; the H_s/λ_p rig is fewer knobs and always plausible.
3. **S5 (a) or (b)** — flow-map advection, or current-modified dispersion?
4. **Backwards compat**: are the three ocean demo scenes allowed to change look (migrated to the
   nearest H_s/λ_p), or must they be byte-identical?

Nothing gets touched until you say. 🫡

---

### Appendix — sources

- [Ubisoft La Forge — Making Waves in Ocean Surface Rendering using Tiling and Blending](https://www.ubisoft.com/en-us/studio/laforge/news/5WHMK3tLGMGsqhxmWls1Jw/making-waves-in-ocean-surface-rendering-using-tiling-and-blending) · [Lutz, Schoentgen, Gilet, HPG 2024 (paywalled, not read)](https://dl.acm.org/doi/10.1145/3675388)
- [Burley — Histogram-preserving Blending for Randomized Texture Tiling, JCGT 2019](https://www.jcgt.org/published/0008/04/02/paper-lowres.pdf) · [Heitz & Neyret, HPG 2018](https://eheitzresearch.wordpress.com/722-2/)
- [Gugi & Csaba — Ocean Rendering with FFT for Real-Time Applications, WSCG 2025 (PDF)](https://dspace.zcu.cz/bitstreams/04db0372-39e5-45ec-894d-e6664df59d04/download)
- [Xue et al. — Real-Time Interactive Hybrid Ocean: Spectrum-Consistent Wave Particle-FFT Coupling, arXiv 2511.02852](https://arxiv.org/abs/2511.02852)
- [Horvath — Empirical Directional Wave Spectra for CG, DigiPro 2015 (paywalled)](https://dl.acm.org/doi/10.1145/2791261.2791267) · reference implementation: [EncinoWaves Spectra.h](https://raw.githubusercontent.com/blackencino/EncinoWaves/master/src/EncinoWaves/Spectra.h) · [DirectionalSpreading.h](https://raw.githubusercontent.com/blackencino/EncinoWaves/master/src/EncinoWaves/DirectionalSpreading.h)
- [Olano & Baker — LEAN Mapping](https://userpages.cs.umbc.edu/olano/papers/lean/lean.pdf)
- [NOAA — fetch-limited growth laws (JONSWAP / Kahma-Calkoen / Hwang-Wang)](https://repository.library.noaa.gov/view/noaa/62217/noaa_62217_DS1.pdf) · [WikiWaves — Ocean-Wave Spectra](https://www.wikiwaves.org/index.php/Ocean-Wave_Spectra)
- [NVIDIA CGDC 2015 — War Thunder ocean (4 cascades, 5 m → 1 km)](https://developer.download.nvidia.com/assets/gameworks/downloads/regular/events/cgdc15/CGDC2015_ocean_simulation_en.pdf)
- [Unity HDRP water simulation](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.5/manual/water-water-system-simulation.html) · [Unreal Water Waves asset](https://dev.epicgames.com/documentation/en-us/unreal-engine/simulating-waves-using-the-water-waves-asset-in-unreal-engine) · [Crest waves/spectrum](https://docs.crest.waveharmonic.com/Manual/Simulation/Waves.html)
