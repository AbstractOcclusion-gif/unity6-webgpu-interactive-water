# A2 design note — scene lights inside the volumetric god-ray march

Status: **DESIGN ONLY — waiting for your go.** No code has been touched.

Goal: the 8 published scene lights (`_WaterSceneLight*`, A1 architecture) contribute
per-sample in-scatter inside `LargeBodyGodRays.shader` Pass 0, so a sunk lamp grows a
volumetric halo in the march — and, because the march RT feeds `_LargeGodRayLastFrame`,
appears in the underside TIR mirror shafts for free.

KWS reference (concepts only, verified in `KWS_VolumetricLighting_URP.cginc`): they run a
*separate* additional-lights loop over the same jittered steps, per step per light
`shadow × distanceAtten`, an optional caustic term when the light sits above the surface, a
Mie phase *boost* (`atten += atten * Mie(cos) * 5`) sharing the sun's anisotropy, slice-
integrated against their own transmittance, all fenced by URP's `_ADDITIONAL_LIGHTS`
keywords and the per-object light API — the exact API our A1 architecture rejected for four
verified reasons, so everything below re-derives on our published list instead.

---

## 1. Sample-level helper, not the integral — RECOMMENDED SHAPE

`WaterSceneLightsInscatter` already folds view-leg extinction into its result; the march
carries its own running per-channel transmittance (`viewFog`). Calling the integral per
segment would double-apply view extinction. So:

**Refactor** in `WaterFog.hlsl`: extract the point-local factors of the existing integral
into one helper both consumers call —

```
// All position-local factors of one published light at sample point p:
//   range window² × cone² × 1/d² × depthMood, colour folded in.
// lightDist is returned RAW (not exponentiated) so each caller applies its own
// extinction policy — the closed form keeps its single exp(view+light), the march
// pairs the light leg with its running view transmittance.
float3 WaterSceneLightPointRadiance(float4 posRange, float4 colorCone, float4 spotDir,
                                    float3 p, float surfaceY, out float lightDist)
```

* Closed form (`WaterSceneLightsInscatter`) calls it at the nearest in-span point
  (`cam + dir*tAtten`) and multiplies by its analytic kernel `h·deltaAtan` and its single
  `exp(−ext·density·(viewLeg + lightDist))` — the same arithmetic it performs today,
  regrouped. **Byte-identical claim, stated honestly:** identical by construction at the
  operation level (same ops, same clamps, same `MIN_DIST_SQ` floors); the only exposure is
  float re-association across the function boundary, which the compiler already performs on
  the current code. Verification in Phase 2: preprocessor simulation of both call sites +
  knob-at-0 guarantee, and visual A/B of the fog glow before/after the refactor alone
  (refactor ships as its own reviewable step inside the change).
* March (per sample `p`): `lampAccum += radiance * exp(−ext·density·lightDist) * viewFog`,
  then `× dt × _WaterFogDensity × WATER_SCENE_LIGHT_GAIN` at the end — a true Riemann sum on
  the same scale as the closed form, so the two glows share magnitude and tuning.
  Deliberately **not** run through the sun term's self-normalising average (that
  normalisation exists to keep sun shafts O(1) against fog density; the lamp term's
  magnitude must instead match the analytic integral it coexists with).
* The lamp sum gets its **own accumulator**: the sun accumulator is later multiplied by
  `_SunColor × HG phase × _LargeGodRayColor × density` — none of which a lamp owes.
  The lamp term joins `col` *before* the temporal blend, so history — and therefore the
  mirror shafts — carry it automatically. It is scaled by the same `regime` factor
  (waterline/submersion/pane), and the composite's coverage mask applies unchanged, so
  no new seam paths exist.
* `depthMood` and the colour are per-light constants along the ray → folded once in the
  pre-march setup (see §2), not per step.
* Samples inside exclusion volumes already skip scatter (`InsideExclusion`) — the lamp term
  sits in the same branch. Lamps get no `ExclusionSunVisibility` (sun-specific) and no
  shadow-map term — matching the analytic fog, which also doesn't shadow the lamp glow.

## 2. Cost shape — fences + pre-march rejection

* Fence: add `#pragma multi_compile_fragment _ WATER_FOG_POINT_LIGHTS` to **Pass 0**
  (checked: it is NOT there today — Pass 0 has only the shadow pair and
  `WATER_FOG_SIMPLE`). Variants go 6 → 12. The lamp code sits behind
  `#if defined(WATER_FOG_POINT_LIGHTS) && !defined(WATER_FOG_SIMPLE)` so the never-armed
  SIMPLE×LIGHTS combo compiles to nothing and Simple-tier occupancy is untouched —
  keyword + `#ifdef`, no uniform branch around the heavy path (fps-cliff rule).
* **Pre-march per-light ray–segment rejection**, before the loop: closest approach of the
  light to the span `[tEnter, tExit]`; reject if the closest in-span distance² exceeds
  range². Survivors go into a compact local list (index + prefolded colour·depthMood).
  An out-of-reach light costs ~1 dot + compare, not steps× evaluations.
* Worst case (8 in-range lights × 64 steps, half res): ~500 pure-ALU evaluations/pixel
  (~30 ALU + one exp3 each). Realistic night scene (2 lamps, default 24 steps): ~50.
  No new textures, no samplers, no derivatives — WGSL-safe, and `WaterFog.hlsl` stays
  SRP-macro-free pure ALU (16-sampler cap unaffected).
* Publisher change (`WaterUniformPublisher.cs`): arming becomes
  `(lightScatter > 0 || godRayLightScatter > 0) && !fogSimple` so god-ray lamps work even
  with the fog knob at 0. List, keyword and knobs still move together or not at all;
  disarmed frames still publish count 0.

## 3. Phase function — RECOMMEND ISOTROPIC (first increment)

The analytic fog integral has **no phase term** — a lamp's fog glow is isotropic today.
Giving the march an HG lobe would make the two glows *disagree in shape* per view angle,
exactly the drift the shared-helper design exists to prevent. Isotropic is also the cheap
option and reads correctly as a halo. KWS's Mie boost on `dot(viewDir, dirToLight)`
(shared anisotropy with the sun) is the stretch — if we ever want it, it should be added to
**both** consumers through the shared helper in the same change, so they can't split.

## 4. Double counting — RECOMMEND (a): `godRayLightScatter` knob, default 0

New knob in the ocean god-ray block (`WaterVolume.Settings.Ocean.cs`, `[Range(0,1)]`,
default 0) scaling only the march contribution; the analytic fog term stays untouched
(it is the stable, masked, always-on layer — suppressing it when the march arms, option
(b), would tie the steady glow to a masked/reprojected/history-lagged signal and pop at
every gate edge). This mirrors exactly how sun in-scatter and sun god rays already coexist
as two author-balanced layers. Default 0 ⇒ knob-at-0 is byte-identical to today. Your call.

## 5. Temporal history — the honest risk

`TemporalHistoryWeight = 0.88` ⇒ ~8-frame integration. A **moving lamp's halo will ghost**
(a trail dissolving over ~8 frames), because reprojection is by scene position and knows
nothing about light motion — same physics as KWS (0.35, ~2 frames, they smear less but
flicker more). Static and slowly-flickering lamps are fine; the halo even inherits the calm.
Not mitigated in this increment (neighbourhood clamp is the known future fix, out of scope).
Test plan includes **"moving lamp at night, god rays on"** to characterise it, not deny it.
The mirror-shaft feedback adds one more frame of lag on top — invisible under the same
blend, per the R4 record.

## 6. Stale-scalar doctrine check

* Light positions/ranges/colours: CPU-published fresh each armed frame — not
  surface-derived, doctrine-clean.
* `depthMood`'s surface reference: **`_VolumeCenter.y` (rest plane), exactly what both
  existing consumers pass** (verified at WaterUnderwaterFog.shader:1011 and
  WaterSurfaceFragStages.hlsl:721/750). Using the march's `camSurfY` here would make the
  march's lamp glow drift from the fog's for the same lamp; a lamp's depth against the
  MEAN level is also the stable choice (a wave passing over a lamp should not pump its
  mood). Everything already keyed on `camSurfY` (sun downwelling, caustic plane, exits)
  stays on the live field; `_UnderwaterSurfaceY` remains gate-only. No new readback
  dependencies.

---

## Files Phase 2 would touch

| File | Ending (verify before edit) | Change |
|---|---|---|
| `Runtime/Shaders/WaterFog.hlsl` | CRLF | extract `WaterSceneLightPointRadiance`, integral re-composed on it |
| `Runtime/Shaders/LargeBodyGodRays.shader` | LF | keyword pragma + rejection prepass + lamp accumulator in Pass 0 |
| `Runtime/WaterUniformPublisher.cs` | CRLF | arming ORs the new knob; publish `_GodRayLightScatter` |
| `Runtime/WaterVolume.Settings.Ocean.cs` | (verify) | `godRayLightScatter` knob (if §4(a) approved) |
| `Editor/WaterVolumeEditor.*` (ocean god-ray rows) | CRLF | knob row |

Verification before shipping: per-file line-ending detect/restore; preprocessor-depth-0 +
brace-balance script on every edited file; 4-variant keyword simulation
(`WATER_FOG_POINT_LIGHTS` × `WATER_FOG_SIMPLE`) proving the fences; knob-0/keyword-off
byte-identical; denominators clamped before any select (both-lanes rule — and if the
console ever surfaces the file:line of the standing `WaterUnderwaterFogInscatter
<no keywords>` div-by-zero warning, it gets captured and fixed in the same cycle).

Acceptance (from the prompt): sunk lamp halo in the march, waterline-masked with no seam
regressions (FOG_GATES / branch / mask-vs-span debug views if in doubt), lamp in the
underside mirror shafts, knob 0 identical to today, Simple tier untouched, honest
WaterCostProbe fps A/B (F-toggle is session-sticky — confirm the tier before measuring).
