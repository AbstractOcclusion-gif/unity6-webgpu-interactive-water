# 🚤 PLAN — Swell > 1 m breaks boat buoyancy on the FFT ocean (v1)

**Date** 2026-08-03 · **Status** DIAGNOSIS COMPLETE, **NO CODE WRITTEN**, decisions gated on Bert.

**Symptom** — raising `swellHeight` past ~1 on the FFT ocean breaks the boat's buoyancy.
**Verdict** — **not a tuning limit. One orphaned knob + two code flaws + one geometry limit.**
Capping the swell hides all of it. Flaw A is the one that unblocks you; the orphan knob is why
nothing you type in the sea-state panel behaves the way the label promises.

## Reference config

Live inspector values (Bert's screenshot, 2026-08-03) vs. what is on disk in `Assets/boatest.unity`:

| Ocean field | Inspector | Scene YAML | |
|---|---|---|---|
| `significantWaveHeight` | **150** | *absent* → default 1.5 | scene predates the field |
| `peakWavelength` | **300** | *absent* → default 60 | " |
| `peakSharpness` | 3.3 | *absent* → default 3.3 | " |
| `largeWaveChoppiness` | 0.7 | 0.7 | |
| `waveScale` | 1 | *absent* → default 1 | |
| `swellHeight` | **1.5** | 0.1 | scene is stale |
| `swellWavelength` | 80 | 80 | |
| `seaDepth` | 0 | *absent* → 0 | |
| `cascadeReach` | 8 | *absent* → default 3 | |
| `unboundedOcean` | ✔ | 1 | |
| `edgeFeatherMeters` | 10 | 10 | **inert** — `LargeWaveEdgeFeatherEffective` returns 0 when `unboundedOcean` (`WaterVolume.Settings.Ocean.cs:412`), so `edge == 1`. Not a factor. |
| `largeWaveAmplitude` | **NOT SHOWN — see §1.1** | **0.1** | ☠️ |
| `windSpeed` | — | 5 | |

Boat (`Assets/boatest.unity`, `WaterBuoyancy` at `&148968734`):

| `WaterBuoyancy` | | `BoxCollider` / `Rigidbody` | |
|---|---|---|---|
| `buoyancy` | 2.6 | `m_Size` | (0.834, **0.534**, 2.500) |
| `waterLinearDamping` | 2 | `m_LocalScale` | (1, 1, 1) |
| `waterAngularDamping` | 1 | `m_Mass` | 200 |
| `samplesPerAxis` | 3 | `m_CenterOfMass.y` | −0.134 |
| `floatRadiusScale` | 1.5 | | |
| `waveDriftStrength` | 1 | | |
| `verticalSettleDamping` | 1 | | |
| `objectWidth` | 2.5 | | |
| `maxBuoyancyForce` | **0** (uncapped) | | |
| `surfaceRelativeDrag` | **1** | | |

> ⚠️ The scene on disk is stale against your live inspector — re-save `boatest.unity` before any
> before/after measurement, or the numbers won't be reproducible.

---

## 1. Root causes — proven, with line references

### 1.1 ☠️ THE ORPHAN: `largeWaveAmplitude` is live in the runtime and **absent from the inspector**

**Answer to Bert's question: yes — `largeWaveAmplitude` is a code-only name. There is no editor
field for it, under any label.**

- Declared as a drawable path: `Editor/WaterVolumePropertyPaths.cs:44`
  → `internal const string LargeWaveAmplitude = "ocean.largeWaveAmplitude";`
- **Referenced by nothing.** A grep of the whole `Editor/` tree returns that declaration and no use.
- The sea-state block that draws your panel is `Editor/WaterVolumeEditor.Motion.cs:105-109`:
  `SignificantWaveHeight`, `PeakWavelength`, `PeakSharpness`, `LargeWaveChoppiness`, `WaveScale`.
  Then `DrawSteepnessReadout` (L112), then the Swell foldout (L116). `LargeWaveAmplitude` is skipped.

Meanwhile it is **fully live at runtime**, on five paths:

| Consumer | Site |
|---|---|
| shader `_LargeWaveAmplitude` | `WaterUniformPublisher.cs:577` |
| FFT height-field bake (`OceanFieldAmplitude`) | `WaterVolume.Update.cs:102` → `OceanFft.compute:616` |
| analytic CPU chop band | `WaterVolume.Waves.cs:84, 89` |
| foam particles | `WaterFoamParticles.cs:718, 771` |
| underwater surface band | `WaterVolume.Underwater.cs:407` |

`boatest.unity` has it serialized at **0.1**. So:

> **Every height you author in that panel is being silently divided by ten on the render — and you
> cannot see or change the divider from the inspector.**

That is why you had to type **150 m** significant height to get a sea that looks right: you were
compensating for an invisible ÷10 (150 × 0.1 = **15 m** rendered). Same for the swell: `1.5` renders
as **0.15 m**.

Two knock-on effects worth naming:

- **The steepness readout lies.** `DrawSteepnessReadout` (L132-141) computes
  `SignificantWaveHeight / PeakWavelengthEffective` = 150/300 = 1/2 and prints
  *"breaking — expect heavy whitecaps"*. The rendered field is 15 m at 300 m = **1/20**,
  *"steep, agitated"*. The readout is blind to the same orphan.
- The tooltip on the field (`WaterVolume.Settings.Ocean.cs:31-33`) says *"Leave at 1 to keep the
  authored heights honest in metres"* — which reads as an instruction to a user who can't follow it.

**Most likely history:** the knob was deliberately retired from the UI when the sea state moved to
honest metres (`significantWaveHeight` / `swellHeight` both feed `WaterOceanSpectrum.GainForHeight`,
L199), but it was left live in the runtime and stale at 0.1 in this scene. That is the bug to close.

### 1.2 ☠️ FLAW A: the FFT branch returns FFT **height** and analytic **velocity**

`WaterVolume.Waves.cs:79-87`:

```csharp
if (OceanFftActive && _oceanFft.TrySampleField(worldX, worldZ, out Vector3 fft))
{
    // "The readback carries no time derivative, so the rate stays on the analytic mirror"
    verticalRate = LargeWaveField.VerticalVelocityAtQuery(worldX, worldZ, _waveTime,
        LargeWaveAmplitudeEffective, LargeWaveHeadingRad, SwellWavelength, SwellHeight,
        LargeWaveChoppiness, ctx) * edge;                      // ← ANALYTIC Gerstner mirror
    return LargeWaveField.ApplyShoreToFftSample(fft, worldX, worldZ, _waveTime,
        SwellWavelength, ctx) * edge;                          // ← FFT readback
}
```

The in-code comment is honest about *why*. What it misses is that `WaterLargeWaves.hlsl:403-409`
makes the two branches **mutually exclusive**:

```hlsl
if (_OceanFftActive > 0.5)
    height = OceanFftDisplacementShore(worldXZ, shore).y * _LargeWaveAmplitude ...
else
    height = EvaluateLargeBodyWaveShore(...).height;
```

When the FFT is live the analytic Gerstner field **is never rendered**. So `sample.Velocity.y` is the
time-derivative of a surface that does not exist, in a phase unrelated to `sample.Height`.

The boat has `surfaceRelativeDrag: 1`, so that phantom `.y` reaches **both** consumers —
`WaterBuoyancy.cs:259` (`relative = pointVelocity - sample.Velocity`) and `WaterBuoyancy.cs:246`
(`drift = surfaceRelativeDrag ? sample.Velocity : HorizontalOnly(sample.Velocity)` — the
`HorizontalOnly` guard that would have stripped it is bypassed).

**Magnitude.** Swell band, `swellWavelength 80`, `SwellCount 4`, falloffs 0.68 / 0.85
(`LargeWaveField.cs:104-108`):

| n | λ (m) | k = 2π/λ | ω = √(gk) | A (per m of `swellHeight`) | A·ω |
|---|---|---|---|---|---|
| 0 | 80.0 | 0.0785 | 0.878 | 1.000 | 0.878 |
| 1 | 54.4 | 0.1155 | 1.064 | 0.850 | 0.904 |
| 2 | 37.0 | 0.1698 | 1.291 | 0.723 | 0.933 |
| 3 | 25.2 | 0.2497 | 1.565 | 0.614 | 0.961 |
| | | | | **Σ (peak)** | **3.68 m/s** |

⇒ **3.68 m/s of phantom vertical water velocity per metre of `swellHeight`.**
At your live `swellHeight = 1.5`: **5.52 m/s peak** (≈1.96 m/s RMS).

Note the asymmetry with §1.1: the swell band's amplitude is `swellHeight` **raw**
(`LargeWaveField.cs:536`), so the orphan's ÷10 does *not* apply to the phantom — only to the render.

### 1.3 FLAW B: the swell bypasses `largeWaveAmplitude` on the analytic path only

| Band | CPU (`LargeWaveField.cs`) | HLSL (`WaterLargeWaves.hlsl`) |
|---|---|---|
| chop | `amplitudeScale * bandScale` — L533 | `_LargeWaveAmplitude * bandScale` — L213 |
| swell | `swellHeight * bandScale` — **L536** | `_LargeSwellHeight * bandScale` — **L216** |

CPU and HLSL agree with *each other*, so this never surfaced as a mirror desync — it is a contract
violation against the FFT path, invisible until `largeWaveAmplitude ≠ 1`.

**Consequence: at `swellHeight = 1.5`, one knob produces three different numbers.**

| Consumer | Path | Result |
|---|---|---|
| What you SEE | FFT × 0.1 | **0.15 m** of swell |
| What buoyancy's DRAG chases | analytic, unscaled (§1.2) | **5.52 m/s** phantom |
| What buoyancy's HEIGHT reads outside the 256 m readback box | analytic, unscaled | **1.5 m** of swell |

That third row is a separate failure: `TrySampleField` (`WaterOceanFft.cs:614`) returns false outside
the camera-centred 256 m square (`HeightFieldRes 128`, `HeightFieldSize 256`) and before the first
landing, and `WaterVolume.Waves.cs:89` then serves the full analytic mirror — a surface the renderer
isn't drawing, 10× too tall. The boat steps vertically as it crosses that boundary.

### 1.4 LIMIT C: the probe sphere radius is 13 cm — and it's derived, not authored

`WaterBuoyancy.cs:144`:

```csharp
sphereRadius = Mathf.Max(MinSphereRadius, 0.5f * spacingY * floatRadiusScale);
//            = 0.5 * (0.534 / 3) * 1.5 = 0.1336 m
```

`SphereSubmergedFraction` is the **only** proportional term in the whole force model, so buoyancy is
linear over ±13.4 cm per probe and flat outside it. Past saturation it is bang-bang:
`(2.6 − 1) · 9.81 = +15.7 m/s²` submerged, `−9.81` clear of the water, with `maxBuoyancyForce: 0`
(uncapped — despite the field's own tooltip existing "so a deeply plunged float doesn't erupt").

---

## 2. Why it breaks at ~1 — the arithmetic that sets the fix order

**Heave stiffness.** `f'(0) = 3/(4r) = 3/(4·0.1336) = 5.61 m⁻¹`, so
`k = g · buoyancy · f'(0) = 9.81 · 2.6 · 5.61 = 143 s⁻²`. Only **one of the three probe layers** is
ever inside its transition zone (the other two sit saturated at 0 or 1), so:

```
k_eff ≈ 143 / 3 = 47.7 s⁻²   →   ω_n = 6.9 rad/s   (T_n ≈ 0.91 s)
ζ = (waterLinearDamping + verticalSettleDamping) / (2 ω_n) = 3 / 13.8 = 0.22
```

**Your swell is 0.88 rad/s — 7.8× slower than ω_n.** The boat is deep in the quasi-static regime; it
*should* ride an 80 m swell of any sane height with essentially zero lag and never leave the linear
band. Your wind sea is slower still (`peakWavelength 300` → ω = 0.45 rad/s). **Limit C is not what's
breaking you, and neither is the 15 m sea.**

The phantom velocity enters through two paths whose gains add:

```
drag  (WaterBuoyancy.cs:259)  → waterLinearDamping = 2
drift (WaterBuoyancy.cs:246)  → waveDriftStrength  = 1
                                                  ─────
                                                     3

parasitic heave ≈ 3 · v_phantom / k_eff = 3 · 3.68 / 47.7 = 0.23 m per metre of swellHeight
linear band                                                = 0.134 m
```

| `swellHeight` | parasitic heave | vs. 0.134 m band | behaviour |
|---|---|---|---|
| 0.1 | 0.023 m | 0.17× | fine — 6× of margin |
| 0.5 | 0.116 m | 0.86× | on the edge |
| **0.58** | **0.134 m** | **1.0×** | **saturation onset** |
| 1.0 | 0.231 m | 1.7× | bang-bang: ~5.2 m/s ejection, ~1.4 m hop, re-entry, repeat |
| **1.5 (yours)** | **0.347 m** | **2.6×** | **fully ballistic** |
| 3.0 | 0.694 m | 5.2× | " |

Onset at **0.58** — independent of every other knob, which is exactly the "it breaks past about 1"
threshold you hit.

With a **correct** FFT-derived rate the real surface velocity is `0.15 m × 0.878 = 0.13 m/s`, giving
`3 · 0.13 / 47.7 = 0.008 m` — **42× smaller, and it scales with what you actually see.**

> Unequal per-probe `weight` means the drift force is applied via `AddForceAtPosition` with an
> off-centre resultant → the phantom also generates roll/pitch torque, not just heave.

### 2.1 Why a PHANTOM velocity is far worse than a real one of the same size

The `waterLinearDamping` gain of 2 above is the *worst case*, and it is only reachable with a phantom.

With a **correct** surface velocity the drag term is **self-cancelling**: the boat accelerates toward
`sample.Velocity`, and as it arrives `relative = pointVelocity − sample.Velocity → 0`, so the drag
force relaxes to zero. Only the unconditional drift term (`waveDriftStrength = 1`) leaves a standing
offset. A genuine 3 m swell at λ = 80 heaves the surface at 2.6 m/s and settles at
`1 · 2.6 / 47.7 = 0.055 m` — well inside the band, with the boat simply *riding* ±3 m.

With a **phantom** velocity the drag can never relax, because the height that would let the boat
"arrive" is somewhere else entirely — the two signals are uncorrelated by construction. Chasing the
phantom carries the boat off the surface; buoyancy hauls it back; `relative` stays large forever. The
full gain of 3 applies permanently.

**That is the bug in one sentence: a real surface velocity is a target the boat converges to; a
phantom one is a target it can never reach, so the damping term becomes a permanent energy source.**

**⇒ Flaw A alone accounts for the failure. §1.1 is why the whole panel feels wrong. B is a
correctness fix. C is a cheap safety net.**

---

## 3. Fix A — FFT-derived vertical rate (two-readback finite difference)

No new GPU work; one extra CPU buffer and one extra bilinear tap.

**`WaterOceanFft.cs`**

1. Mirror the existing centre/size triple (`_bakedCenter` L549 → `_pendingCenter` L585 →
   `_sampledCenter` L597) with a **time** triple: stamp `waveTime` in `Dispatch` beside
   `_bakedCenter = cameraXZ`, carry it through `RequestHeightReadback`, land it in `OnHeightReadback`.
2. Add `_heightCpuPrev` + `_sampledCenterPrev / _sampledSizePrev / _sampledTimePrev` +
   `_hasPrevField`. On landing, **swap** the two `float[]` before the copy (ping-pong, zero
   allocation — the same pattern `_foamHistA/_foamHistB` already uses at L520).
3. New overload:
   ```csharp
   internal bool TrySampleField(float worldX, float worldZ,
                                out Vector3 heightSlope, out float verticalRate)
   ```
   `verticalRate = (h_now − h_prev) / (t_now − t_prev)`, **each field sampled against its own
   centre/size** (the camera moved between bakes — `TryFieldUV` already takes them as parameters).
   Guard → `verticalRate = 0` when: no prev field, point outside the prev region, or `dt <= 0`.
   Keep the 3-arg overload delegating to it so `WaterVolume.Underwater.cs:438` and the fog gate are
   untouched.
4. `Release()` must null the prev buffer too (L744 neighbourhood).

**`WaterVolume.Waves.cs:79-87`** — take the rate from the new out-param; delete the
`LargeWaveField.VerticalVelocityAtQuery` call on this branch. The analytic branch (L89) is unchanged.

**`LargeWaveField.ApplyShoreToFftSample` (L430)** — it scales the FFT height by `weight` and adds
`surfHeight`; the rate must get the **same** `weight` so the shore fade stays lockstep. Cleanest is a
`ref float verticalRate` parameter that applies `weight` and adds the surf front's own finite
difference (the analytic path already computes that in `EvaluateBands`, L~560).

**Sampling adequacy (checked, not assumed).** `_readbackInterval` maxes at
`WaterQuality.MaxUpdateInterval = 8` frames → dt ≤ 133 ms → Nyquist 23.6 rad/s. The height field is
`256 m / 128 = 2 m` per texel, so the fastest wave it can carry is λ ≈ 4 m → ω = √(9.81·1.57) = 3.9
rad/s. **6× of margin. No aliasing.** Camera teleport → the two regions don't overlap → rate 0, which
is the correct degradation.

**Files:** `WaterOceanFft.cs`, `WaterVolume.Waves.cs`, `LargeWaveField.cs`. No shader change.

---

## 4. 🚦 DECISION GATE 1 — what to do with the orphan (§1.1)

### O1 — retire it *(recommended)*

Migrate `ocean.largeWaveAmplitude` to **1** (bump `_settingsVersion` to 10 and do it in
`WaterVolume.Settings.Migration.cs`, folding the old value into `significantWaveHeight` and
`swellHeight` so existing scenes look identical), then delete the multiplier from the five runtime
sites in §1.1's table and drop the dead path constant.

- ✅ The sea state is authored in honest metres, everywhere, one number per concept.
- ✅ Kills Flaw B outright — with no multiplier there is nothing to disagree about.
- ✅ Steepness readout becomes truthful.
- ⚠️ `LargeWaveAmplitudeEffective` (`WaterVolume.Settings.Ocean.cs:385`) also carries the
  **wind coupling** for *bounded* open water (`largeWaveAmplitude * windSpeed / LargeWaveReferenceWind`).
  Removing it wholesale changes bounded lakes. That branch has to be preserved or migrated
  separately — **this is the one thing that makes O1 more than a delete.**
- **For you, right now:** your scene becomes `significantWaveHeight 15`, `swellHeight 0.15`,
  auto-migrated, looking exactly as it does today — and from then on you type real metres.

### O2 — expose it again

Add `WaterVolumePropertyPaths.LargeWaveAmplitude` to the draw list at
`WaterVolumeEditor.Motion.cs:105`, and feed it into `DrawSteepnessReadout`.

- ✅ One line + the readout fix. Nothing else moves.
- ❌ Keeps a redundant stylisation knob on top of a metres-honest spectrum — the thing that made this
  confusing in the first place.

### O3 — leave the runtime alone, fix your scene only

Set `largeWaveAmplitude` to 1 via the component's **Debug inspector** (right-click the `WaterVolume`
header → *Debug*) or by hand in the YAML, then re-author `significantWaveHeight 150 → 15` and
`swellHeight` to the metres you actually want.

- ✅ Zero code. You can do it in the next two minutes.
- ❌ The next scene anyone makes hits the same trap.

## 4b. 🚦 DECISION GATE 2 — Fix B, only if you pick O2 or O3

If the orphan is retired (O1), Flaw B disappears with it and this gate is moot. Otherwise:

**B1 (recommended if B is still live)** — multiply the analytic swell band's `amplitudeScale` by the
same factor the chop band uses, on both sides: `LargeWaveField.cs:536` and
`WaterLargeWaves.hlsl:216`. Two lines; no change to your render; kills the §1.3 boundary step. Check
whether `WaterWaveConstantsValidator` guards this CPU/HLSL pair — if not, it should.

**B2** — drop `_LargeWaveAmplitude` from the FFT path instead (`OceanFft.compute:616`,
`WaterLargeWaves.hlsl:404/437/444`, folding it into `OceanSpectrumGain`). *This is the option that
would make your waves 10× **bigger**, not smaller — it is the one you asked about.* Effectively O1
by another route, but without the migration, so every tuned scene breaks. **Not recommended as a
standalone.**

---

## 5. Fix C — safety net (scene-only, no code)

1. `maxBuoyancyForce` on the boat → **~30**. The third floater in this very scene (`&…6314`,
   `buoyancy 3.2`) already uses 30, so the value is house-precedent. Caps the bang-bang eruption
   without touching the linear regime.
2. *Optional, 1 line:* widen `[Range(0.5f, 3f)] floatRadiusScale` (`WaterBuoyancy.cs:42`) — at 3 the
   radius is 0.267 m, doubling the linear band. Only worth it if you later want short, steep seas
   where ω approaches ω_n = 6.9 rad/s (i.e. wavelengths under ~2 m — so: probably never for a boat).

**Not proposed:** coupling the probe radius to the sea state. It would make `BuildProbeLayout` depend
on `_body`, which is resolved per-`FixedUpdate` (`WaterBuoyancy.cs:184`) while the layout is built
once in `Start` — a rebuild trigger for no benefit once A is fixed.

---

## 6. Verification checklist

Re-save `boatest.unity` first (§ reference config) so the baseline is reproducible.

- [ ] **Baseline capture** — `WaterProbe` on the hull, log `sample.Height` and `sample.Velocity.y`
      for 10 s at `swellHeight 1.5`. Expect `Velocity.y` peaks ≈ 5.5 m/s against a height envelope of
      ≈ ±0.15 m. *That ratio IS the bug*, and it's the single clearest confirmation.
- [ ] **After A** — same capture. `Velocity.y` peaks should fall to ≈ 0.13 m/s and be **in phase**
      with `d(Height)/dt`. Cross-correlate; ≈ +1 at lag 0.
- [ ] Boat heave stays inside ±0.134 m of the surface; no probe reads `fraction == 1` on all 27.
- [ ] **After O1/O3** — steepness readout matches the rendered sea (15 m / 300 m → 1/20,
      "steep, agitated"), and the ocean looks unchanged from today.
- [ ] **After B1 (or O1)** — drive the boat past 128 m from the camera and back. No vertical step at
      the readback-box boundary.
- [ ] Sweep `swellHeight` 0 → 3. Behaviour degrades smoothly, no threshold.
- [ ] Non-FFT bodies (`pool.unity`, `islandtest.unity`) byte-identical — A touches only the
      `OceanFftActive` branch. **O1 must be re-checked on any BOUNDED open-water scene** (the wind
      coupling in `LargeWaveAmplitudeEffective`, §4/O1).
- [ ] `WaterWaveConstantsValidator` still passes.
- [ ] Profiler: `WaterVolume.SampleHeights` marker — A should make it *cheaper* on the FFT branch
      (one `VerticalVelocityAtQuery` = 5 `EvaluateBands` passes per probe, deleted).

---

## 7. Open questions for Bert

1. **Gate 1 (§4): O1 retire, O2 expose, or O3 scene-only?** O1 recommended; the only real work in it
   is preserving the bounded-body wind coupling.
2. **Gate 2 (§4b)** — only if you pick O2 or O3.
3. Scope for the implementation pass: **A only**, **A + the chosen O**, or **A + O + C**?
4. ~~Was 150 a real sea or compensation?~~ **ANSWERED 2026-08-03: compensation.** Bert: *"yes it was
   a compensation to have my raging sea but i felt it was weird."* → the O1 migration target is
   confirmed: `significantWaveHeight 150 → 15`, `swellHeight 1.5 → 0.15`, `peakWavelength 300`
   unchanged. Render identical to today.

---

## 8. What the sea actually is, once the ÷10 is gone

Worth recording, because it makes the migration target look right rather than arbitrary.

`Hs = 15 m` at `peakWavelength = 300 m` → deep-water peak period `T = √(300 / 1.56) = 13.9 s`, and
steepness `1/20` (the editor's "steep, agitated"). Real North Atlantic severe storms run
**Hs 14-16 m at Tp 14-16 s**. So the sea Bert built by compensation is a **physically plausible
severe storm sea** — the raging sea he wanted. Nothing about it needs re-art-directing; it only needs
to be *spelled* in metres instead of in metres-times-a-hidden-tenth.

**The swell is the part that was never working.** At `swellHeight 1.5 × 0.1` the rendered roller was
**0.15 m** on top of a 15 m sea — invisible. That is almost certainly why it kept getting pushed
higher, straight into the §2 saturation threshold at 0.58. After A + O1, a **3-4 m** swell is both
visible and safe:

| `swellHeight` (post-O1, honest metres) | rendered roller | standing heave offset (§2.1) | vs 0.134 m band |
|---|---|---|---|
| 0.15 (migration target) | 0.15 m | 0.003 m | 0.02× |
| 2.0 | 2.0 m | 0.037 m | 0.27× |
| 3.0 | 3.0 m | 0.055 m | 0.41× |
| 4.0 | 4.0 m | 0.074 m | 0.55× |

(The boat still *rides* the full ±3-4 m — that is the point. Only the standing offset has to stay
inside the linear band, and it does, with room to spare.)
