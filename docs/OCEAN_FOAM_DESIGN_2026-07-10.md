# Ocean Wave Foam — Analysis & Design

**Date:** 2026-07-10
**Status:** SUPERSEDED (2026-07-11). The shipped implementation diverged from this plan: the
foam advection was built IN-CASCADE inside `OceanFft.compute → ComputeNormal` on r32f
ping-pong history (`OceanFoamPrev/Next`) — there is NO separate `OceanFoamField.compute`
and NO RG16 target as proposed in Increment 3. Increments 1 (anti-tiling/whitecap texture)
and 2 (split decay / crest handling) are done. Current audit + forward plan:
`FOAM_SYSTEMS_AUDIT_2026-07-11.md`. Kept for the analysis sections only.
**Scope:** Ocean FFT *surface whitecap* foam only. Not interactive/pond foam (`_FoamMask` path), not the GPU foam particles (`WaterFoamParticles`), not shoreline contact foam.

---

## 1. Goal (Bert's brief)

Ocean wave foam "results are not bad but not stunning." Three concrete complaints:

1. **Too patchy and round** — foam reads as soft round caps, not crisp whitecaps.
2. **Generated at the full top, should be at the crest** — foam should form on the breaking/forward face and spill, not sit symmetrically on the peak.
3. **Foam should roll and deposit** — it should be carried along the surface and left behind as trails/windrows, not blink on and off in place.
4. Plus: **patch tiling is visible at distance.**

---

## 2. Current implementation (audited)

### 2.1 Generation — `Runtime/Shaders/OceanFft.compute → ComputeNormal`
Per-cascade texel, from central differences of the horizontal displacement:

```hlsl
float jacobian = jxx * jzz - jxz * jzx;              // fold determinant
float fresh    = saturate(OceanFoamCoverage - jacobian);
fresh *= (slice == 0u) ? 0.0 : (slice == 1u ? 0.25 : 1.0);   // cascade weighting
fresh *= (OceanWindSpeed > OceanFoamMinWind) ? 1.0 : 0.0;     // wind gate
// temporal accumulation (ping-pong), framerate independent:
float foam = prevFoam * saturate(1.0 - OceanFoamFadeRate * dt)
           + OCEAN_FOAM_ACCUM_RATE * OceanFoamStrength * dt * fresh;
foam = min(foam, OceanFoamMax);
OceanNormal[id] = float4(n, foam);                   // stored in .w, per cascade
```

### 2.2 Sampling — `Runtime/Shaders/WaterLargeWaves.hlsl → OceanFftFoam`
Sums `.w` across active cascades with a cubic distance fade + `log2` mip LOD (same fade as the normal tilt). Coverage is a saturated 0..1 mask.

### 2.3 Shading — `Runtime/Shaders/WaterSurface.shader` (~717–783)
```hlsl
float coverage = OceanFftFoam(sourceXZ) * SwellShoalFactor(sourceXZ);
oceanFoamPattern = SampleFoamPattern(sourceXZ / _OceanFoamTileSize);   // reuses the POND flipbook
float threshold  = 1.0 - coverage;
oceanFoam = smoothstep(threshold, threshold + _OceanFoamFeather, oceanFoamPattern.r);
// lit: wrapped diffuse + ambient, foam-normal relief, matte (knocks down reflection)
```

**Assessment:** the generation core is sound and already Crest-calibrated (Jacobian fold + temporal accumulate). The weakness is entirely in (a) the texture/sampling and (b) the absence of spatial motion.

---

## 3. Reference findings (KWS + Crest, read from `UnityProjects/KWSWater`)

Both engines build foam from the **same Jacobian fold** we already use — we are not behind on generation. What they add:

| Aspect | KWS (Kripto289) | Crest (Wave Harmonic) |
|---|---|---|
| Foam source | `saturate(-J)`, `J=(1+Dx)(1+Dy)-DxDy` | `saturate(coverage - det(jacobian))`, coverage 0.55 ×5 |
| Cascade weighting | LOD0 ×0, LOD1 ×0.25, LOD2/3 ×1 | `max(MinimumWavesSlice=2, slice)` when scale ≤ 8 |
| Wind gate | `WindSpeed > 4` | none |
| Temporal | `prev*(0.9..0.999) - 0.001 + deposit` | `foam *= 1 - fadeRate*dt`, clamp to max (10) |
| **Spatial advection** | **Fake** — 2-phase advected-UV cross-fade at *shade* time | **Real** — semi-Lagrangian back-trace on a persistent field using a Flow LOD velocity |
| Shoreline/depth foam | dynamic-waves path | depth-threshold + negative-depth priming |
| Anti-tiling | Heitz–Deliot triangle-grid `Tex2DStochastic` | Same Heitz sampler + multi-scale octaves + scroll |
| Texture as dissolve | `pow(tex,contrast) - (1 - sqrt(mask))` | `smoothstep(1-foam, +feather, tex)` ← **we already do this** |
| Foam normal | no | yes (finite-diff of whitecap texture) |

Key transferable ideas: **(1)** Crest's semi-Lagrangian advection on a persistent texture is the only thing that delivers genuine "roll and deposit"; **(2)** the Heitz triangle-grid stochastic sampler is the shared, definitive anti-tiling primitive (identical skew matrix in both engines); **(3)** multi-scale octave + cascade-scaled foam UV to prevent a single visible tile at distance; **(4)** a *dedicated* sharp whitecap texture, not a soft cloud flipbook.

Academic anchor: INRIA *"Real-time Animation and Rendering of Ocean Whitecaps"* — Jacobian-PDF coverage + an advected foam texture; the same recipe.

---

## 4. Diagnosis mapped to the complaints

- **Round/patchy** ← we reuse the *pond* `_FoamTex` flipbook (soft, cloudy) as the whitecap texture, and the coverage is blurred by temporal accumulation + mip. Fix = dedicated sharp whitecap texture + Heitz stochastic + sharper dissolve contrast.
- **"At crest not full top"** ← mostly NOT a generation bug (the Jacobian already fires on the pinched forward face). What sells "breaking and spilling" is **advection**: foam born at the fold must be carried down the face. Optional generation gate by crest-height + forward-slope keeps it off the trough side.
- **"Roll and deposit"** ← we have temporal *linger* but zero *spatial motion*; foam is locked to the world-XZ texel where it was born. Needs advection.
- **Tiling at distance** ← `SampleFoamPattern(worldXZ / tileSize)` is a single `frac()` tile with no stochastic breakup and no multi-scale.

---

## 5. Proposed increments (cheapest-first)

### Increment 1 — Anti-tiling + real whitecap texture *(pure shader, low risk)*
Touches: `WaterSurface.shader` (ocean foam block), new whitecap texture asset, `WaterLargeWaves.hlsl` uniforms.

- Add a Heitz–Deliot triangle-grid **stochastic sampler** for the ocean whitecap texture (SampleGrad with ddx/ddy).
- Add a **2-octave multi-scale blend**: tap at scale `s` and `2s`, cross-fade by camera distance; add a slow scroll offset.
- Author a **dedicated whitecap texture** (sharp filament/bubble structure) instead of reusing `_FoamTex`; keep the existing `smoothstep(1-coverage, +feather, tex)` dissolve but expose a contrast knob.

Sketch:
```hlsl
// triangle-grid stochastic tap (Heitz & Deliot 2018) — kills visible repetition
float3 Tex2DStochastic(sampler2D t, float2 uv, float2 dx, float2 dy) {
    float2 skewUV = mul(float2x2(1.0, 0.0, -0.57735027, 1.15470054), uv * 3.464);
    int2 base = int2(floor(skewUV)); float3 bary = float3(frac(skewUV), 0);
    bary.z = 1.0 - bary.x - bary.y; /* pick 3 hashed offsets, weight by |bary| */ ...
}
```
Expected: removes most of the "round/patchy at distance" look on its own. **Highest look-per-effort.**

### Increment 2 — Crest-face biased generation + split decay *(small compute change)*
Touches: `OceanFft.compute → ComputeNormal`.

- Weight `fresh` toward the crest and the forward face:
  ```hlsl
  float crestMask   = saturate((height - crestStart) / crestBand);   // only near peaks
  float forwardFace = saturate(-dot(slopeXZ, windDirXZ));             // leeward/forward face
  fresh *= crestMask * lerp(1.0, forwardFace, faceBias);
  ```
- **Split decay**: thin foam fades fast, dense deposited foam lingers → windrows persist in troughs.
  ```hlsl
  float fade = lerp(fastFade, slowFade, saturate(prevFoam / denseFoamRef));
  foam = prevFoam * saturate(1.0 - fade * dt) + accum * fresh;
  ```
Keeps foam off the "full top" and gives it a residual-deposit character even before true advection.

### Increment 3 — TRUE advection: persistent camera-centered foam field *(architectural — recommended)*
Touches: new `OceanFoamField.compute` (or extend `OceanFft.compute`), `WaterOceanFft.cs` (allocate + dispatch + publish), `WaterLargeWaves.hlsl` (sample), `WaterSurface.shader` (consume).

**Why a separate field:** foam currently lives *inside* the tiling FFT cascades (`OceanNormal.w`), which wrap at different scales — you cannot cleanly advect there. Crest's answer is a dedicated camera-centered persistent foam texture, decoupled from the cascades.

**Reuse:** build it on the **scrolling camera-following ripple-sim window pattern already in the codebase** (see the large-water sim window). Same toroidal-wrap / snap-to-texel recentering — this de-risks the only hard part.

Design:
- One camera-centered `RG16`/`R16` persistent texture, ping-ponged (RG16 filterable on WebGPU — not the float32 filter problem).
- **Each frame:** (1) recenter/scroll to follow the camera (snap to texel, wrap edges); (2) **advect** — semi-Lagrangian back-trace `pos -= velocity * dt`; (3) **decay** (split fast/slow from Inc 2); (4) **inject** the Jacobian fold as the *source* (the current `fresh` term becomes injection into this field, not the final foam).
- **Velocity = wind drift + a fraction of the FFT orbital velocity** (`kappa * dDisplacement/dt`). The orbital component points down the forward face of the crest → foam is carried off the top and deposited on the front/trough. **This single mechanism answers both "at crest not full top" and "roll and deposit."**
- Surface shader samples this world-space field instead of (or blended over) `OceanFftFoam`.

Sketch:
```hlsl
// OceanFoamField.compute (per frame)
float2 vel  = OceanWindDrift + kappa * OceanOrbitalVelocity(worldXZ);   // roll driver
float2 back = worldXZ - vel * dt;
float  prev = SampleFoamField(back);                                    // semi-Lagrangian
float  fade = lerp(fastFade, slowFade, saturate(prev / denseRef));
float  foam = prev * saturate(1.0 - fade * dt)
            + injectGain * saturate(OceanFoamCoverage - jacobian);      // Jacobian = source
FoamFieldNext[id] = min(foam, OceanFoamMax);
```

---

## 6. Recommendation on advection (the requested "what do YOU lean for")

**Lean: true advection (Increment 3), built on the existing scrolling-window pattern.**

- The fake (KWS shade-time flow-offset) only drifts the *texture pattern*; the coverage mask still blinks on/off in place. It **cannot** deposit trails or pool foam in troughs, so it does not meet the brief.
- True advection costs one small filterable texture + one cheap compute dispatch/frame. The genuinely fiddly part (camera recentering/wrap) is **already solved** in the large-body ripple-sim window → reuse, don't rewrite.
- Bonus: driving advection by wind + orbital velocity resolves the "at crest not full top" complaint for free.
- Honest tradeoff: it's a persistent sim to babysit (can accrue artifacts on teleport/large camera jumps → needs a prewarm/clear path like Crest's) and more plumbing than Inc 1–2.

Suggested order: **Inc 1 → Inc 2 → Inc 3.** Inc 1+2 remove most of the round/patchy/tiling look cheaply; Inc 3 is the one that makes it *stunning*.

---

## 7. Open decisions before coding

1. Foam field **resolution & domain** (e.g. 512² over what world extent) and update rate (per-frame vs fixed 30 Hz like Crest).
2. **Orbital velocity source** — finite-diff between frames vs analytic from the spectrum. Finite-diff is simpler and reuses existing displacement targets.
3. Whether to **keep** the cascade `.w` foam as a near-field detail layer blended over the advected field, or fully replace it.
4. WebGPU: confirm `RG16_SFloat` UAV load/store + linear filter on the target devices (expected fine; not the float32 case).
5. Whitecap texture: author new, or adapt an existing KWS/Crest-style whitecap atlas (respecting licensing — author our own in our namespace per project rule).

---

## 8. File touch-list (when approved)

- Inc 1: `WaterSurface.shader`, `WaterLargeWaves.hlsl`, new `Whitecap.png` + material knobs, `WaterVolumeEditor.Appearance/Ocean.cs`.
- Inc 2: `OceanFft.compute` (ComputeNormal), foam param uniforms in `WaterOceanFft.cs`.
- Inc 3: new `OceanFoamField.compute`, `WaterOceanFft.cs` (alloc/dispatch/publish), `WaterLargeWaves.hlsl` (sampler), `WaterSurface.shader` (consume), editor foam settings.
