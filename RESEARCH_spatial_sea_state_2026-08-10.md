# Spatial sea-state realism — research synthesis (2026-08-10)

Goal: (1) replace the too-periodic small-wind-wave group method, (2) make large bodies
spatially NON-uniform like real water — flat glassy zones, roughness patches, different
wave sizes across the surface, wind/current-driven distribution. Research only, no code.

Ground truth read from the repo today (not from memory):

- Small-wave path (`WaterWaves.hlsl`): chop shaped by `WaveGroupEnvelope` = mean + **two
  crossed sines** travelling at the carrier's group velocity (`_WaveGroupA/B`).
- Open water (`WaterLargeWaves.hlsl` + `OceanFft.compute` + `WaterOceanFft.cs`): 4 FFT
  cascades, disjoint wavelength bands, `CascadeTileOversample = 4` as the anti-tiling,
  per-cascade distance fade, analytic swell band, edge feather.

---

## 0. The one law that explains what Bert sees at distance

At distance, what the eye reads is NOT displacement — it's the **mean-square slope (mss)
of sub-metre waves**, i.e. glitter/roughness contrast. Cox & Munk (1954, sun-glitter
photography): `mss ≈ 0.003 + 5.12e-3 · U10` (linear in wind, r=0.986). Every real-world
non-uniformity is a spatial field modulating either this mss (slicks down, gusts up) or
the long-wave geometry (fetch, groups, currents, swell).

Optical sign rule: a SMOOTH patch is **brighter inside the sun-glitter core, darker
off-axis**; a ROUGH patch the opposite; the contrast sign flips ~12° off the specular
direction. Under overcast, smooth = lighter glassy sheet, rough = dark matte. Getting this
sign flip right is what makes flat zones read as *water* and not as a decal.

Consequence for us: the far field does not need displacement changes at all — modulating
**normal/roughness/foam (mss) per-pixel** is enough past the cascade fade distances, and
that is nearly free.

---

## 1. Fix the group method (small wind waves)

**Why the current one reads fake:** a 2-sine envelope is perfectly periodic and
deterministic — sets arrive like clockwork, same size forever. Real seas (Longuet-Higgins
1984): the envelope of a narrow-banded Gaussian sea is a **stochastic Rayleigh-distributed
field**; only its *characteristic* scale is set by spectral bandwidth. Field data:

- Mean "run" of high waves: **1.5–3.5 waves** (JONSWAP growing sea ≈ 2.7).
- Groupiness factor (SIWEH, Funke & Mansard): 0.38–0.93, mean ≈ 0.67.
- Envelope correlation length ≈ **3–6 peak wavelengths along-wind**, shorter crosswise.
- Narrow spectrum (swell) → long clear groups; fresh wind sea → ragged weak groups.
- Group velocity = c/2 (deep water) — the current code already gets this right; crests
  born at the back of a set, dying at the front, is also right and worth keeping.

**Replacement candidate (same interface, same cost class):** keep
`WaveGroupEnvelope(m) → amplitude, gradient`, but source it from a **scrolling low-pass
complex Gaussian field**: |tiny precomputed 2-tap noise texture| (or 3–4 sines with
incommensurate frequencies and hashed spatial phases as the zero-texture fallback),
advected at c_g along wind, anisotropic correlation (3–6 λp × ~1.5 λp), amplitude shaped
to Rayleigh-ish so lulls and big sets both happen. Grouping-strength knob maps to
bandwidth (narrow = swelly sets, wide = choppy buzz). The gradient comes analytically from
the same taps, so the slope path survives.

Bonus insight: an FFT cascade whose band is deliberately NARROW produces physically
correct groups **for free** (groups ARE narrow-band interference). The swell band is the
natural place: narrow k-band + narrow direction fan → visible aperiodic sets at the
horizon with zero new machinery.

---

## 2. Sea-state field for big bodies (the flat zones / patchiness ask)

The shipped-and-proven pattern (Unity HDRP "simulation mask", War Thunder "downwind
texture", Crest "Scale By Factor"): a **low-res world-space field, one channel per
cascade group, multiplying per-cascade amplitude + driving shading mss**. All the
phenomenology becomes layers COMPOSITED INTO THIS ONE FIELD:

### 2a. Fetch / wind-exposure (lakes especially)
- Physics (JONSWAP growth laws): `Hs ∝ F^0.5`, `λp ∝ F^0.66` with fetch F = distance from
  the upwind shore. U10=5 m/s: F=100 m → Hs≈2–3 cm; F=10 km → Hs≈25 cm, λp≈6 m.
  Windward shore = glassy/capillary only; waves grow downwind. This is THE lake signature.
- Shipped proof: **War Thunder (Tcheblokov, CGDC 2015)** bakes a directional
  terrain-occlusion "downwind" texture; wind-sheltered water (lee of islands, rivers,
  bays) goes calm automatically. Whole ocean 0.5 ms on a GTX 770.
- For us: bake per-direction shore occlusion into a small texture (we already have the
  shore SDF machinery — Layer A depth field — as a starting substrate); long-wavelength
  cascades attenuate hardest (long waves need fetch, ripples survive partial shelter),
  and λp shift can be approximated by re-weighting cascade amplitudes, not new FFTs.

### 2b. Gusts / cat's paws (roughness UP patches)
- Physics: gust footprints raise local mss within seconds; `mss` linear in local wind;
  below ~2–3 m/s ripples can't be sustained (capillary minimum c=0.23 m/s @ λ=1.7 cm) →
  light-wind water is bimodal: glassy background + discrete ruffled patches. Scales:
  metres–hundreds of metres (paws), 1–10 km streaks/cells aligned with wind, advecting
  downwind at ~mean wind speed (Dorman & Mollo-Christensen 1973).
- For us: 2 octaves of stretched (along-wind) noise scrolling at wind speed, writing
  `U_local` → boosts ONLY the small cascades' amplitude + fragment mss + whitecap
  threshold. Below a `U_local` floor, hard-flatten the ripple band → glassy holes.

### 2c. Slicks / windrows (roughness DOWN streaks — the "flat zones")
- Physics: surfactant films (Marangoni damping) kill ONLY λ ≈ 1–30 cm (resonance
  ~5–15 cm), leaving longer waves rolling through untouched — a slick is flat *texture*,
  not flat *water*. mss ÷2–3 inside (Cox & Munk measured slick line: 0.008+1.56e-3·U).
  Geometry: **curvilinear streaks aligned with wind** (Langmuir windrows, spacing 5–25 m
  on lakes up to ~300 m at sea, length:spacing >100:1), plus current-front bands. They
  disperse above ~10–12 m/s wind; most prominent under ~6–7 m/s.
- For us: streak mask (anisotropic noise, extreme elongation) that ONLY suppresses the
  smallest cascade + mss. The λ-selectivity is the realism: long swell must visibly
  continue through the glassy band. Fade the whole layer out with wind.

### 2d. Composition + caveat
- The field is tiny (128–256², world-projected, a few compute taps/frame) and the sample
  cost is one texture tap in displacement + one in fragment. HDRP ships exactly this at
  scale; per-pixel amplitude scaling of an FFT field is "wrong" (horizontal displacement
  scales too) but universally accepted — every shipped version feathers mask edges.
- CPU buoyancy mirror must sample the same field (our height-is-pure-function-of-XZ
  contract survives if the field is, too).

---

## 3. Swell vs wind sea decoupling (cheap, large realism)

>75% of the open ocean is swell-dominated; ~70% of coasts see swell + wind sea
simultaneously, usually at an oblique angle (swell direction = remote storm, not local
wind). Crossing systems give the moving diamond/checkerboard interference and smooth
glassy swell shoulders under short chop — a look our single wind-heading fan can't make.
For us: give the swell band its own heading uniform (decoupled from `_LargeWaveWindHeading`),
narrow spread, and strong slow grouping (§1). Nearly free — the bands already exist.

## 4. Direction variance / next-gen anti-tiling (bigger, optional)

**Lutz, Schoentgen, Gilet — HPG 2024 (Ubisoft La Forge), "Fast Orientable Aperiodic Ocean
Synthesis Using Tiling and Blending"**: hexagonal histogram-preserving tiling-and-blending
(Deliot-Heitz) applied to the FFT *displacement output*, with per-tile random offset +
rotation driven by a runtime-editable **direction map**. Gives true aperiodicity from ONE
FFT tile AND spatially varying wave direction (gust steering, curved wind fields) —
production-proven, cheaper than multi-cascade oversampling. Caveats: slight crest
softening (variance renormalization), Jacobian continuity across tile borders needs care
for our foam. This could eventually REPLACE the 4× oversample, but it's a surgery on the
cascade sampling path — phase 2, after §2 proves out.

## 5. Currents (bigger still, phase 3)

Physics: wave action conservation — opposing current compresses λ (k up to 4× at blocking,
U = −c₀/4) and steepens/breaks; following current flattens. Observed: ±30% Hs over ~10 km
with 1 m/s currents; whitecap coverage varying >10× across a front; sharp rough/calm bands
at tidal races and river mouths (often paired with slick lines — compound signature).
Graphics state of the art is Jeschke & Wojtan SIGGRAPH 2023 (dispersive waves on shallow-
water bulk flow); the game-budget version is a painted/baked current map driving phase
advection (Doppler warp) + per-cascade amplitude via the action rule + breaking mask where
steepness > ~0.3. Open-source ray tracer (Halsne et al., GMD 2023) can bake ground-truth
k(x)/A(x) maps offline for a static current field.

## 6. Also found, filed for later

- **Water Surface Wavelets** (Jeschke et al., SIGGRAPH 2018): amplitude field A(x,θ)
  advected at group speed on a coarse grid — the principled superset of §2 (gusts, fetch,
  sets all emerge from one transport PDE). If §2's static/scrolling layers ever feel too
  canned, this is the upgrade path; interactive on GPU in the paper.
- **Water Wave Packets** (SIGGRAPH 2017): sparse Lagrangian wave groups; code released.
- **Donatini et al. 2024** (Applied Ocean Research): blend N local spectra spatially,
  sub-ms per 512² band — the published blueprint for "different seas in different places".
- **Multi-Dimensional Procedural Wave Noise** (Guehl et al., SIGGRAPH 2025): FFT-free
  pointwise wave synthesis with per-point spectrum control — candidate future replacement
  for the analytic small-wave layer.
- **Horvath 2015 / EncinoWaves**: the reference for parameterizing our spectra by wind/
  fetch/depth instead of raw amplitude knobs — pairs naturally with §2a.
- Anti-citation: MDPI JMSE 2022 "Amplitude and Phase Computable Ocean Wave..." was
  RETRACTED (plagiarism) — do not use.

---

## Proposed order (realism per cost, my ranking — nothing touched yet)

| # | What | Fixes | Cost class |
|---|------|-------|-----------|
| P1 | Stochastic group envelope (§1), same interface | "group method not realistic" | Small — one function body + a tiny noise source |
| P2 | Sea-state field: fetch bake + gust layer + slick layer → per-cascade amp + far-field mss (§2) | flat zones, patchiness, wave-size variance, lake windward calm | Medium — one small compute + 2 taps; buoyancy mirror |
| P3 | Swell heading decoupled + narrow-band swell groups (§3) | horizon-scale realism | Small |
| P4 | Tiling-and-blending with direction map (§4) | direction variance, replaces oversample | Large — cascade sampling surgery |
| P5 | Current map: Doppler warp + action-rule amplitude (§5) | rivers/tide races/fronts | Large |

P1+P2+P3 together cover everything in the original ask except currents, at roughly the
cost of one extra small texture pipeline.

## Key sources

- Cox & Munk 1954 (glitter/mss): https://userpages.umbc.edu/~martins/phys650/Cox%20and%20Munk%20Glint%20paper.pdf
- Longuet-Higgins 1984 (group statistics): https://royalsocietypublishing.org/doi/10.1098/rsta.1984.0061
- Alpers & Hühnerfuss 1989 (slick damping): https://agupubs.onlinelibrary.wiley.com/doi/10.1029/JC094iC05p06251
- Dorman & Mollo-Christensen 1973 (cat's paws): https://journals.ametsoc.org/view/journals/phoc/3/1/1520-0485_1973_003_0120_ootsom_2_0_co_2.xml
- JONSWAP fetch laws (NOAA tabulation): https://repository.library.noaa.gov/view/noaa/62217/noaa_62217_DS1.pdf
- War Thunder ocean (CGDC 2015 slides): https://developer.download.nvidia.com/assets/gameworks/downloads/regular/events/cgdc15/CGDC2015_ocean_simulation_en.pdf
- Unity HDRP simulation mask: https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.2/manual/add-swell-agitation-or-ripples.html
- Lutz et al. HPG 2024 + La Forge write-up: https://dl.acm.org/doi/10.1145/3675388 , https://www.ubisoft.com/en-us/studio/laforge/news/5WHMK3tLGMGsqhxmWls1Jw/making-waves-in-ocean-surface-rendering-using-tiling-and-blending
- Water Surface Wavelets (SIGGRAPH 2018): https://visualcomputing.ist.ac.at/publications/2018/WSW/
- Water Wave Packets (SIGGRAPH 2017): https://visualcomputing.ist.ac.at/publications/2017/WWP/
- Jeschke & Wojtan 2023 (waves+currents): https://visualcomputing.ist.ac.at/publications/2023/GSWSDSW/
- Donatini et al. 2024: https://www.vliz.be/imisdocs/publications/80/394980.pdf
- Horvath 2015 / EncinoWaves: https://dl.acm.org/doi/10.1145/2791261.2791267 , https://github.com/blackencino/EncinoWaves
- Semedo et al. 2011 (swell dominance): https://journals.ametsoc.org/view/journals/clim/24/5/2010jcli3718.1.xml
- Crest local wave inputs: https://docs.crest.waveharmonic.com/Manual/Simulation/Waves.html
- Guehl et al. SIGGRAPH 2025 wave noise: https://pascalguehl.github.io/siggraph2025-wave-noise/
