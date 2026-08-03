# 🌫️ PLAN — Fog surface band (correctness + cost) and the frame spikes (v1)

**Date** 2026-08-03 · **Status** SCOPED, **NO CODE WRITTEN**, handoff for a fresh session.
**Prereq context:** `docs/PLAN_buoyancy_swell_v1.md` (the orphan-knob story) — this is its deferred item.

Two SEPARATE problems. Evidence says they are not the same thing.

## Measurements (Editor, Play mode, GPU profiler on)

```
CPU 22.20 ms   GPU 20.09 ms   336 draws
PlayerLoop                          336 draws  20.017 ms
├─ RenderPlayModeViewCamera         314        14.676
│  └─ ExecuteRenderGraph            210        14.337
│     ├─ WaterUnderwaterFog           2         6.546   ← 45% of camera render
│     ├─ DrawTransparentObjects      21         4.505
│     ├─ DrawOpaqueObjects           83         0.753
│     ├─ LargeBodyGodRays.Composite   1         0.663
│     ├─ WaterUnderwaterFog.SurfaceDepth 22     0.623
│     └─ (rest)                                ~1.25
└─ UpdateScene                       22         5.341   ← compute: FFT chain, caustics, foam
```

Draw-call count is NOT the problem: 336 draws at 20 ms is ~60 µs/draw. This is fill rate.

**Key experiment already run:** with `WATER_FOG_SIMPLE` on (wavy-crossing march compiled out) the
spikes REMAIN, still showing as `Semaphore.WaitForSignal`. ⇒ the fog march is **baseline cost, not
the spike source**. The two problems are independent.

---

# PROBLEM 1 — `SurfaceHeightBand()` is wrong for FFT oceans

`Runtime/Shaders/WaterWaterline.hlsl:84`, CPU mirror `WaterVolume.Underwater.cs:399`:

```hlsl
#define SURFACE_BAND_AMPLITUDES 3.0
#define SURFACE_BAND_PAD_METERS 2.0
float SurfaceHeightBand()
{
    float surfReach = (_SurfActive > 0.5) ? _SurfAmplitude * SURF_SETAMP_JITTER_MAX
                                          * max(_SurfGreens, SURF_MIN_GREENS) : 0.0;
    return max(abs(_LargeWaveAmplitude) * SURFACE_BAND_AMPLITUDES, surfReach)
         + SURFACE_BAND_PAD_METERS;
}
```

Its own doc comment states the contract:

> *"Conservative half-band (metres) around the rest plane that brackets EVERY height the displaced
> surface can reach this frame... Widening it costs march steps / early-outs, never correctness;
> **narrowing it below a real crest clips a crossing**."*

**It is now narrower than a real crest.** `_LargeWaveAmplitude` is 1 after the v12 migration, so the
band is `1*3 + 2 = 5 m`. The sea is `significantWaveHeight 15`. Crest reach for a Gaussian sea is
roughly `0.93 * Hs` (from `H_max ≈ 1.86 Hs`, crest ≈ half of that), i.e. **~14 m**, plus chop.

The formula only ever worked by accident: it assumes `_LargeWaveAmplitude` IS the wave scale, which
was true for the ANALYTIC generator (chop band sums to ~0.58 m per unit amplitude, so `3 x amplitude`
was a ~5x conservative bound). On the FFT path the height lives in `significantWaveHeight` /
`swellHeight` and the multiplier means nothing. `LargeWaveHeightMeters`
(`WaterVolume.Settings.Ocean.cs:403-408`) already computes the right quantity: `sqrt(Hs² + swell²)`.

## ⚠️ Correction to something said in-session

I told Bert the widened band "roughly doubled the pixels entering the 40-step march". **That was
wrong — I had not read the march when I said it.** Re-reading `WaterUnderwaterFog.shader:205-238`:

```hlsl
float tBand      = band / max(abs(ray.y), 1e-4);
float startDist  = saturate(tFlat - tBand) * rayLen;      // band only sets WHERE the march STARTS
float marchReach = startDist + UNDERWATER_CROSS_MAX_STEPS * UNDERWATER_CROSS_STEP_METRES;
```

Step count is fixed at `UNDERWATER_CROSS_MAX_STEPS = 40`, step size fixed at
`UNDERWATER_CROSS_STEP_METRES = 1.5` (60 m of reach). The band shifts the march's START along the
ray; it does **not** add steps. So widening the band does not multiply per-pixel fog cost.

What the band DOES gate is **whether the passes run at all**:

| Consumer | Site | Effect of a wider band |
|---|---|---|
| Fog crossing-march start | `WaterUnderwaterFog.shader:210` | march starts earlier along the ray |
| Fog pool box ceiling | `WaterUnderwaterFog.shader:602` | taller box |
| God-ray dry-camera early-out | `LargeBodyGodRays.shader:367` | god rays early-out LESS often |
| **CPU fog arm ceiling** | `WaterVolume.Underwater.cs:335` | **fog pass arms over a taller camera range** |
| CPU atmosphere dry reject | `Rendering/LargeBodyAtmosphereFeature.cs:64` | rejects less often |

So the migration's 2.3 m → 5 m raised how often the fog pass and god rays run, not the per-pixel
cost. A *correct* ~20 m band would mean they effectively always run whenever the camera is within
20 m of the rest plane — which for a 15 m sea is honest, because the camera genuinely can be
submerged at any moment.

## The real design question

A conservative rest-plane band is the wrong gate for a big sea. **The precise answer already
exists**: `WaterVolume.Underwater.cs:438` gates on `_oceanFft.TrySampleHeightLatest(x, z, ...)` —
the ACTUAL FFT height at the camera xz from the readback. The band should be the FALLBACK for when
that sample is unavailable (no readback yet, outside the 256 m region, non-FFT body), not the
primary gate.

### Proposed direction

1. **Make the band correct.** `SurfaceHeightBand()` / `SurfaceHeightEnvelope()` derive from the sea
   height on FFT bodies:
   `max(LargeWaveHeightMeters * CREST_REACH_FACTOR, analytic reach, surfReach) + pad`, with
   `CREST_REACH_FACTOR ≈ 1.2` (0.93 for the Rayleigh crest + chop headroom — pick and NAME it, with
   the derivation in the comment). Needs `LargeWaveHeightMeters` published as a global for the HLSL
   side; the CPU side already has the property.
2. **Keep the precise gate primary.** Where a `TrySampleHeightLatest` sample is available, gate on
   local surface height + a small band. Fall back to the conservative envelope only when it is not.
   This is what stops a correct 20 m band from arming everything permanently.
3. **Only then** consider march cost. 40 x 1.5 m = 60 m of reach is independent of the band, so it
   is a separate tuning axis; do not touch it in the same change.

### Verify

- [ ] Camera above a 15 m sea, wave crest passes through the near plane → fog waterline follows the
      wave instead of snapping to the flat plane (this is the correctness bug the 5 m band causes).
- [ ] `SURFACE_BAND_*` CPU/HLSL pair still agrees — check whether `WaterWaveConstantsValidator`
      guards it (a grep for `SURFACE_BAND` in the validator found NOTHING, so it may need adding).
- [ ] God-ray dry early-out still fires when the camera is genuinely high and dry.
- [ ] Pools / bounded bodies byte-identical (they keep the analytic branch).

---

# PROBLEM 2 — the frame spikes

`Semaphore.WaitForSignal` on the main thread = it is IDLING while the render thread / GPU finishes.
The cost is elsewhere; the Hierarchy view cannot show it. **Confirmed not the fog march** (Simple
fog still spikes).

## Prime suspect: the tier-amortised passes colliding

Three things run on frame counters, all in `WaterVolume.Update.cs`:

```csharp
if (IsOceanClipmap && !_paused && Time.frameCount % _oceanFftInterval == 0)  // FFT chain
if (_simulate && Time.frameCount % _causticInterval == 0)                    // caustic render
if (_simulate && Time.frameCount % _readbackInterval == 0)                   // both readbacks
```

When their periods coincide, ONE frame carries the whole FFT chain (spectrum update, horizontal +
vertical FFT per cascade, normal+foam, `GenerateMips`, height bake) AND the caustic render AND two
GPU→CPU readbacks. `UpdateScene` already shows **5.341 ms of GPU on 22 draws** — i.e. it is nearly
all compute — and that is the AVERAGE, so the dispatch frames are worse.

Plus, editor-only: `WaterOceanFft.Dispatch` runs a preview kernel over every cascade under
`#if UNITY_EDITOR || DEVELOPMENT_BUILD`. A build never pays it.

## The experiment that settles it (do this FIRST, before any code)

1. **Count frames between spikes.** Constant period ⇒ amortised collision, confirmed.
2. Force the intervals apart (or all to 1) via the quality tier and re-measure. If the spike cadence
   moves with the interval, done.
3. Profiler in **Timeline** view, spike frame, read the **Render Thread** row — that names the
   dispatch.
4. Compare against a **build**, not the Editor, before tuning anything.

If confirmed, the fix is scheduling (phase-offset the three intervals so they never coincide), not
optimisation. Cheap and safe.

## Secondary suspects, in order

- `DrawTransparentObjects` at 4.505 ms / 21 draws — water surface + spray overdraw. Whitecap
  coverage is Jacobian-driven (slope-based), so removing the 0.1 multiplier raised spawn rates hard.
  `oceanFoamCoverage` is 1.042; the spray tile budget is the other knob. **Tuning, not code.**
- GC: check the GarbageCollector track. Prior finding on record: boat GC spikes were EDITOR-ONLY
  and closed — do not re-litigate without build numbers.

---

# What this session already shipped (context for the next one)

Fix A (FFT-derived vertical rate), the v12 orphan migration, the sea-size readout, and the wake
coherence fix. See the `buoyancy-swell-fft-desync` memory for md5s and the full reasoning.

**Note for the spike hunt:** Fix A *removed* CPU work — roughly 2,000 Gerstner component evaluations
per FixedUpdate (27 probes x 5 `EvaluateBands` x 16 components). Byte-exact pre-change copies of all
8 touched files were staged from disk before editing, if a true A/B is ever wanted.
