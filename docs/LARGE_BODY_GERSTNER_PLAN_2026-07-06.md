# Large-Body Water — Step 1: Gerstner Choppiness (PLAN, awaiting OK)

Branch: `feature/large-body-water` · Date: 2026-07-06 · Status: **PROPOSED — no code yet**

## Goal

Give the open-water swell sharp, pinched crests (choppiness) instead of the current
rounded sine sum, **without** touching the pool / near-field look and while keeping the
CPU buoyancy mirror matched to the rendered surface.

## Reference basis (checked, not guessed)

- **Crest** (`com.waveharmonic.crest`)
  - `Runtime/Shaders/Waves/Gerstner/Gerstner.compute` — horizontal displacement is
    `disp = chopAmplitude * sin(phase); result.xz = disp * dir; result.y = amplitude * cos(phase)`.
  - `Runtime/Scripts/Waves/WaveSpectrum.cs` — `_Chop` default **1.6**, range **0–2**.
  - `Runtime/Shaders/Library/Cascade.hlsl` — normals come from the **Jacobian** of the
    displaced position (cross-product of displacement derivatives), so chop's effect on
    slope is captured. Also `SampleInvertedDisplacement()` — a **4-iteration** fixed-point
    loop that inverts horizontal displacement to answer a height query at a world XZ.

We reuse these *techniques*, written fresh in `AbstractOcclusion.WebGpuWater`.

## Key design property — continuous at choppiness = 0

Everything below is driven by a new per-body `choppiness` knob (`Q`). At **Q = 0** the math
collapses **exactly** to today's behaviour:

- horizontal displacement terms are `Q·(…)` → zero, so vertices don't move in XZ;
- the Jacobian normal reduces algebraically to `normal.xz = -slope` — identical to the
  current `ApplyLargeBodyWaveNormal` additive tilt;
- the buoyancy inversion converges in one step (error = 0) → same sampled XZ as today.

So we can **default `choppiness = 0` and ship the code as a byte-for-byte no-op**, then
raise it live with you in the editor. Pools stay untouched regardless (still gated on
`_LargeBody` / `openWater`, exactly as now).

## The math we'll use (source point `s = worldXZ`, per component)

```
phase   = k·dot(dir, s) − ω·t + φ
H       = Σ A·sin(phase)                       // height (unchanged from today)
Dx      = Σ Q·A·dir.x·cos(phase)               // NEW horizontal displacement, x
Dz      = Σ Q·A·dir.z·cos(phase)               // NEW horizontal displacement, z
```

Displaced surface point `P = (s.x + Dx, H, s.z + Dz)`.

Normal from the analytic Jacobian (matches Crest's intent, no texture):

```
gx = dH/dx  = Σ A·k·dir.x·cos(phase)           // == today's slope.x
gz = dH/dz  = Σ A·k·dir.z·cos(phase)           // == today's slope.z
Dx_x = Σ −Q·A·k·dir.x·dir.x·sin(phase)   Dx_z = Σ −Q·A·k·dir.x·dir.z·sin(phase)
Dz_x = Σ −Q·A·k·dir.z·dir.x·sin(phase)   Dz_z = Σ −Q·A·k·dir.z·dir.z·sin(phase)

Tx = (1 + Dx_x, gx, Dz_x)      // ∂P/∂x
Tz = (Dx_z,     gz, 1 + Dz_z)  // ∂P/∂z
n  = normalize(cross(Tz, Tx))  // +Y up
```

We feed the normal to the fragment as an **additive tilt** on top of the existing
composed normal (so near-field ripple normals are preserved):
`tiltXZ = n.xz / max(n.y, ε)`; at Q = 0 this equals `-slope`, i.e. today.

## Render side — `WaterSurface.shader` + `WaterLargeWaves.hlsl`

1. **`WaterLargeWaves.hlsl`**
   - Add `float _LargeWaveChoppiness;` (falls back to 0 when unpublished → no-op).
   - Add `LBW_INVERSION_ITERATIONS 4` (named, from Crest) — used by the CPU side; kept in
     the header comment as the shared constant.
   - Extend the evaluator to also return `Dx, Dz` and the four displacement derivatives.
     Keep `LargeBodyWaveHeight()` returning `H` (still the vertex height).
   - New `float2 LargeBodyWaveDisplacement(float2 worldXZ)` → `(Dx, Dz)`.
   - Rewrite the body of `ApplyLargeBodyWaveNormal(worldNormal, sourceXZ, strength)` to use
     the Jacobian tilt above (still additive, still `strength`-scaled). Signature unchanged.

2. **`WaterSurface.shader`**
   - Vertex (currently line ~353): after `worldPos.y += LargeBodyWaveHeight(worldPos.xz)`,
     also `worldPos.xz += _LargeWaveChoppiness · LargeBodyWaveDisplacement(worldPos.xz)`.
     Capture the **pre-displacement** `worldPos.xz` into a new varying
     `float2 largeWaveSourceXZ` (at Q = 0 it equals the displaced XZ).
   - Fragment (currently line ~450): evaluate `ApplyLargeBodyWaveNormal` at
     `i.largeWaveSourceXZ` instead of `i.worldPos.xz`, so the normal is read at the wave's
     source point, not the displaced fragment position.
   - All new work stays inside the existing `if (_LargeBody > 0.5)` guards.

## CPU buoyancy — `LargeWaveField.cs` (keep byte-for-byte lockstep)

- Add `Choppiness` const-mirror + the same displacement/derivative terms.
- Add `Vector2 Displacement(worldX, worldZ, …)` mirroring `LargeBodyWaveDisplacement`.
- Add `float HeightAtQuery(worldX, worldZ, …, choppiness)` that runs the **4-iteration**
  inversion (Crest `SampleInvertedDisplacement`): start `src = query`; 4×
  `src -= (src + Q·Displacement(src) − query)`; return `Height(src)`.
- Header comment updated to note the shared `LBW_INVERSION_ITERATIONS = 4`.

## Wiring — `WaterVolume.cs` + `WaterUniformPublisher.cs`

- `WaterVolume`: add `[Range(0f, 2f)] [SerializeField] internal float largeWaveChoppiness = 0f;`
  and an `internal float LargeWaveChoppiness => largeWaveChoppiness;` accessor (next to the
  existing `largeWaveAmplitude` / `LargeWaveHeadingRad` block, ~line 89–96).
- The three buoyancy call sites (lines ~1218, ~1249, ~1321) switch from
  `LargeWaveField.Height/Evaluate(worldX, worldZ, …)` to the inversion-aware
  `HeightAtQuery(...)`, passing `LargeWaveChoppiness`. Flow keeps using `slope` as today.
- `WaterUniformPublisher`: add `ID_LargeWaveChoppiness = "_LargeWaveChoppiness"` and publish
  `sink.SetFloat(ID_LargeWaveChoppiness, _body.LargeWaveChoppiness)` next to the existing
  `_LargeWaveAmplitude` / `_LargeWaveWindHeading` (line ~131).

## Constants (no magic numbers)

`LBW_INVERSION_ITERATIONS = 4` (Crest); choppiness default `0` (range 0–2, Crest uses 1.6);
all existing `LBW_*` spectrum constants unchanged. New uniform string `"_LargeWaveChoppiness"`
declared once in the publisher.

## What stays untouched (guarantees)

- Pools / small bodies: `_LargeBody = 0` and `openWater = false` short-circuit every branch.
- Open-water bodies at `choppiness = 0`: identical render + buoyancy to today (shown above).
- No new render passes, no readbacks, no compute — pure analytic, same CPU/GPU-mirror pattern
  already used for `WaterWaveBank ↔ WaterWaves.hlsl`.

## Risk / watch-list

- **Lockstep**: `LargeWaveField.cs` must mirror the new HLSL terms exactly (same failure mode
  as before — a divergence shows as floaters sitting off the visual crest).
- **Over-chop inversion**: if `Q·A·k` exceeds ~1 the surface self-intersects (Gerstner
  "pinch-through") and the 4-iteration inversion won't converge. The 0–2 clamp plus our small
  amplitudes keep us well under that; we validate by eye as we raise `Q`.
- **Normal cost**: the Jacobian adds a `sin`+a few mults per component in the fragment; trivial
  at 12 components, but noted for the WebGPU perf budget.

## Proposed build-and-look sequence (small steps, with you at the editor)

1. HLSL + shader vertex/fragment + new varying; `choppiness` published but default 0 → confirm
   **no visual change** (proves the no-op path).
2. Raise `largeWaveChoppiness` on the open-water demo body → confirm crests sharpen, normals/
   refraction still correct, pools unaffected.
3. CPU inversion in `LargeWaveField` + the three call sites → drop the cube in and confirm it
   still rides the (now chopped) crest without drift.
4. Commit (you run git; commands presented one per line).
