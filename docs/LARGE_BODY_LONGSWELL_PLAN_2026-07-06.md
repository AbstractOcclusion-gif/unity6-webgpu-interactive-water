# Large-Body Water — Long-Period Swell + Horizon Fade (PLAN, awaiting OK)

Branch: `feature/large-body-water` · Date: 2026-07-06 · Status: **PROPOSED — no code yet**

## Goal

Make the distant sea keep **rolling** to the horizon (long-period swell) instead of going flat,
and soften the hard horizon line — without disturbing the tuned near-field chop, and keeping pools /
bounded bodies byte-for-byte unchanged.

## Why the far sea is flat today

The current fix (`oceanWaveReach`) fades **all** wave amplitude to zero with distance, because the
coarse far clipmap triangles can't resolve our short (≤9 m) waves and they alias/crawl. So the only
way to stop the crawl was to flatten everything — hence you needed reach ≈ 900 m and still got a calm
horizon. The real answer (below) is to keep the LONG waves (which the coarse mesh *can* resolve) and
drop only the short ones with distance.

## Reference basis (checked, not guessed)

- **Crest** `WaveSpectrum.cs`: Pierson-Moskowitz spectrum over **14 octaves, ~0.06 m to ~512 m**;
  amplitude `A ∝ √power` with power **peaking toward the longer/mid wavelengths** (longer waves are
  taller). KWS uses the same PM spectrum (`A ∝ 1/ω⁵` windowed).
- **Crest** `FFTSpectrum.compute` band-limits per cascade: a LOD only keeps wavelengths in
  `[4·texel, 8·texel]` (`WAVE_SAMPLE_FACTOR = 8`), zeroing the rest — i.e. **minimum resolvable
  wavelength ∝ texel size**, which for a clipmap grows with distance.
- **Horizon**: neither fades the sea to sky; Crest uses a horizon **mask plane + atmospheric fog**,
  KWS uses far-plane clipping. So proper horizon softening = **fog (our Phase 4)**; we add only a
  light stopgap now.

## Design

### 1. Extend the spectrum with a LONG-SWELL band (lockstep: BOTH files)
Keep the existing chop band exactly as tuned (short crests you liked), and **add** a second set of
`LBW_SWELL_COUNT` long components at wavelengths ~60–220 m with PM-style bigger amplitude for longer
waves, wind-driven direction like the current swell. This adds rolling swell without touching the
near chop. New named consts in `WaterLargeWaves.hlsl` **and mirrored byte-for-byte in
`LargeWaveField.cs`** (buoyancy must ride the same swell — this is the lockstep-sensitive change).
Amplitude of the long band scales with the existing wind/`largeWaveAmplitude` so one knob still drives
size; add a `swellHeight` multiplier for the long band specifically.

### 2. Distance band-limit (replaces the blunt amplitude fade; render-only)
Inside the wave evaluation, weight **each component** by whether the local mesh can resolve it:
`minWavelength = distanceFromCamera · detailSlope`; a component with wavelength `λ` gets
`weight = smoothstep(minWavelength·LOW, minWavelength·HIGH, λ)`. Far out `minWavelength` is large →
short chop drops (no crawl); long swell (`λ` big) survives → rolling horizon. Near the camera
`minWavelength ≈ 0` → full spectrum. Published `_LargeWaveDetailSlope` is **0 for non-ocean bodies**,
so the weight is 1 and their look is unchanged. `oceanWaveReach` (blunt amplitude fade) is retired in
favour of this.
- **Lockstep note**: the band-limit lives in the shader only. `LargeWaveField.cs` stays the FULL
  spectrum (buoyancy samples only near the camera, where the shader is also full), so the shader's
  `detailSlope = 0` path remains byte-identical to the CPU mirror. No CPU change for the band-limit.

### 3. Horizon fade (light stopgap; real fix = Phase 4 fog)
Blend the far surface toward the reflected-sky / fog colour over the last stretch so there's no hard
line. Tunable `horizonFadeDistance` (0 = off). This is explicitly a placeholder until the Phase 4
render-feature fog lands, which will supersede it. Fragment-only, ocean-gated.

## Buoyancy

Unchanged in principle: `LargeWaveField` stays full-spectrum (now including the long band) and is
sampled near the camera, so floaters ride both the chop and the new rolling swell. Watch: a bigger
long swell means bigger vertical motion — verify the cube still rides cleanly (the 4-iteration chop
inversion already handles horizontal displacement).

## Knobs (no magic numbers)

New serialized on `WaterVolume` (ocean/large-body): `swellHeight` (long-band multiplier),
`swellWavelength` (long-band base, metres), `oceanDetailFalloff` (band-limit slope), and
`horizonFadeDistance`. New shader consts: `LBW_SWELL_COUNT`, swell wavelength/amplitude falloffs,
band-limit `LOW/HIGH`. Publish `_LargeWaveDetailSlope` (0 for non-ocean) + `_HorizonFadeDistance`.

## Gating guarantees

- Pools / bounded bodies: `_LargeBody = 0` or `_LargeWaveDetailSlope = 0` → no band-limit, no swell
  band change to their path, no horizon fade → unchanged.
- Only ocean bodies get the band-limit + horizon fade; the extra swell band rides the existing
  `_LargeBody` swell path (also present on bounded open-water bodies, which is fine — they're small,
  near camera, so they just get richer swell; verify the bounded open-water look still reads right).

## Risks / watch-list

- **Lockstep**: the new long-swell spectrum constants MUST match in `WaterLargeWaves.hlsl` and
  `LargeWaveField.cs` (same failure mode as before — floaters drift off the visual crest).
- **Band-limit continuity**: `detailSlope = 0` path must stay byte-identical to today near camera.
- **Long swell + buoyancy**: taller vertical motion; confirm the cube rides, no jitter.
- **Horizon fade vs fog**: keep it light; Phase 4 fog will replace it (avoid double-darkening later).
- **Perf**: more components in the fragment (chop + swell bands); keep counts modest, check WebGPU.

## Proposed increments (small, with you at the editor)

1. **Long-swell band** — extend spectrum in both files (lockstep), wire `swellHeight`/`swellWavelength`.
   Confirm: rolling long swell appears near field, cube rides it, bounded bodies unchanged.
2. **Band-limit** — per-component distance weight, retire `oceanWaveReach`. Confirm: horizon keeps the
   long swell, short chop drops with distance (no crawl), near field unchanged.
3. **Horizon fade** — light surface→sky blend, `horizonFadeDistance`. Confirm no hard line; tune.
4. Commit per increment (you run git; commands one per line).
