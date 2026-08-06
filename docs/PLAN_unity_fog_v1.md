# 🌫️ PLAN — Unity / URP scene fog support across the water stack (v1)

**Date** 2026-08-04 · **Status** SCOPED, **NO CODE WRITTEN**, handoff for an approved session.
**Sibling doc:** `docs/PLAN_fog_band_and_spikes_v1.md` — that one is about the *water's own volumetric*
underwater fog (cost + band). This one is about **Unity's scene fog**, a completely different system.
They share a word and nothing else. Do not merge them.

---

## 0. What this is for, and what it is NOT

**The problem.** Unity/URP scene fog (`Window → Rendering → Lighting → Environment → Fog`) fogs the
terrain, the props, the skybox-adjacent geometry — and **not the water**. Every water surface in the
package renders crisp at any distance while the land beside it dissolves. At a shoreline that is a
hard seam; on a pond ringed by fogged trees it reads as a hole in the world.

**Verified 2026-08-04 against the working tree** (not trusted from the earlier audit — re-grepped):

```
multi_compile_fog   0 hits
UNITY_FOG_COORDS    0 hits
UNITY_TRANSFER_FOG  0 hits
UNITY_APPLY_FOG     0 hits
MixFog              0 hits
ComputeFogFactor    0 hits
unity_FogColor      0 hits
unity_FogParams     0 hits
```

across every `.shader` and `.hlsl` in `Runtime/Shaders/`. **Nothing in the water stack has ever
supported scene fog.**

**This is NOT the fix for the horizon-haze pop.** That is a separate, diagnosed defect in the haze's
sky-*sampling* path (see §1). Shipping fog does not repair it, and repairing it does not remove the
need for fog. Two problems, two fixes.

---

## 1. POLICY — haze and fog both stay. They are not alternatives.

The earlier note said *"use one or the other — haze for oceans, Unity fog for ponds."* **That framing
is wrong and this plan supersedes it.** They solve different problems on different axes:

| | Horizon haze | Unity scene fog |
|---|---|---|
| Target colour | the **rendered sky**, per azimuth, sampled live | a **fixed** `unity_FogColor` |
| Boundary it heals | water ↔ **sky** (the far mesh edge) | water ↔ **land / props / particles** |
| Scale | kilometres (ocean clipmap) | scene-authored (linear start/end, or exp) |
| Tracks a sunset / animated sky | **yes** — that is its entire reason to exist | no |
| Pose-dependent | yes (that is the defect in §1.1) | no, by construction |
| Applies to the rest of the stack | no — surface pass only | yes — that is the point |

**The rule to encode:** the haze owns the water↔sky boundary at ocean scale. Unity fog owns the
water↔everything-else match at scene scale. A scene may run both; they must not both be driven to
saturation over the same distance band (see the double-fog trap, §6).

**Consequence for the haze escape hatch.** `horizonHazeColor.a = 1` collapses the haze to a fixed
colour — which is, precisely, "fog to a flat colour" with extra sampling cost. It is a valid
*workaround* for the pop and a valid *artistic* choice, but it is not a fix: at alpha 1 the entire
auto-sky-match path is dead weight. **Either the sampling path gets repaired, or it should be deleted
and the fixed-colour case handed to Unity fog.** Keeping a broken sampler behind a knob nobody dares
raise is the worst of the three.

### 1.1 The haze defect, for the record (diagnosed 2026-08-04, confirmed by Bert)

`WaterSurfaceFragStages.hlsl:1546` / `:1570`:

```hlsl
float2 huv      = saturate(horizonUV);                    // the 5 blur taps
float2 centreUV = float2(0.5, saturate(horizonUV.y));     // the centre band
```

The horizon direction sits at eye level for any camera above an infinite ocean, so at FOV 45 it
leaves the top of the frame at pitch ≈ **−22.5°**. Past that, `horizonUV.y > 1` and both `saturate()`
calls pin **every** sample to the top screen row. Water is `Queue = Transparent`
(`WaterSurface.shader:101`) so it is absent from `_CameraOpaqueTexture`; the top row over open ocean
is therefore **skybox below the horizon** — flat `_GroundColor` on the built-in Procedural sky, one
near-discontinuity away from the bright horizon band. The 2026-07-30 fix smoothed the blend *weight*;
the two colours it blends between are the step. Confirmed by Bert: alpha 1 (no sampling) → no pop.

Two more findings in the same error class, same block:
- `:1594` the last-resort fallback is `SampleEnvironment(incomingRay)`, but `incomingRay` is
  camera→surface (`:61`, `:166`) — steeply **downward** from a high camera, so it samples the cube
  *below* the horizon. `horizonDir` is already in scope and is the geometrically correct vector.
- `:1571` in the off-screen regime `skyConfidence` collapses to `centreSky`, a **single point tap**.
  One mast or coastline crossing that one pixel swings the whole far ocean. Latent in `boatest`
  (haze 0.631, nothing on the horizon row); live in any scene with distant terrain, since the depth
  test rejects everything nearer than `0.90 × far` = 9000 m as non-sky.

---

## 2. Two shader families, two recipes — this is the whole shape of the work

The stack is **not** uniform. Grepped per file:

| Shader | Program | Queue | Blend | v2f TEXCOORDs used | Fog recipe |
|---|---|---|---|---|---|
| `WaterSurface.shader` (3 passes) | **CGPROGRAM** | Transparent | Off (ZWrite On) | 0–3 | legacy macros |
| `WaterChunkWall.shader` | **HLSLPROGRAM** | Transparent | `One OneMinusSrcAlpha` | 0 | `MixFog`, premultiplied |
| `WaterExclusionWall.shader` | **HLSLPROGRAM** | Transparent | `One OneMinusSrcAlpha` | 0 | `MixFog`, premultiplied |
| `WaterTransparent.shader` | **HLSLPROGRAM** | Transparent | `SrcAlpha OneMinusSrcAlpha` | 0–3 | `MixFog`, straight |
| `FoamParticles.shader` | **CGPROGRAM** | Transparent+10 | `SrcAlpha OneMinusSrcAlpha` | **0–6** | legacy, compose |
| `SplashParticles.shader` | **CGPROGRAM** | Transparent+10 | `SrcAlpha OneMinusSrcAlpha` | **0–6** | legacy, compose |
| `FoamDensityComposite.shader` | CGPROGRAM | Transparent+5 | `One OneMinusSrcAlpha` | 0–1 | **fullscreen — per-pixel from depth** |
| `GodRays.shader` | HLSLPROGRAM | Transparent+100 | `One One` (additive) | — | attenuate only, **never tint** |

The earlier note's *"legacy macro trio, not MixFog"* is correct **for `WaterSurface.shader` only** —
it is `CGPROGRAM` (UnityCG.cginc) and `MixFog` would want an `HLSLPROGRAM` pass. The three wall /
transparent shaders are already `HLSLPROGRAM` and must use URP's `MixFog`, not the legacy macros.
Mixing the two recipes per file is correct; forcing one recipe on everything is not.

---

## 3. Increment F1 — `WaterSurface.shader` Pass 0 (≈80% of the visible win)

The only pass that matters for the shoreline seam. Anchors verified 2026-08-04:

| Site | Line | Change |
|---|---|---|
| Pass 0 pragma block | `WaterSurface.shader:108–136` | add `#pragma multi_compile_fog` |
| `struct v2f` | `WaterSurfaceVertStage.hlsl:120–129` | add `UNITY_FOG_COORDS(4)` — **0–3 are taken** |
| `v2f vert(...)` return | `WaterSurfaceVertStage.hlsl` (end of `vert`) | `UNITY_TRANSFER_FOG(o, o.pos)` |
| frag, fog application | `WaterSurface.shader` **between :294 and :303** | `UNITY_APPLY_FOG(i.fogCoord, outColor)` |

**The application site is exact and load-bearing:** after `FinalCompositeStage` (`:294`), **before**
the debug branch (`:303–304`). Debug views must return unfogged — they are data, not a look.

**The submerged case needs no gate.** `WaterSurface.shader:267` early-returns
`UnderwaterStage(i, geom, waterClarity)` under `_Underwater > 0.5`, upstream of the fog site, so the
water's own volumetric fog keeps sole ownership of that regime for free. (The earlier note's
"skip when `_CameraUnderwater > 0.5`" was pre-emptive; the uniform it names is a *different* one —
`_CameraUnderwater` lives in `WaterSurfaceSpecular.hlsl:135`, `WaterFogDebug.hlsl:41` and
`WaterExclusionWall.shader:63`. Pass 0's gate is `_Underwater`, `WaterSurfaceVertStage.hlsl:11`.)

**Never fog Pass 1** `OceanSurfaceEyeDepth` (`:327`) — it writes depth *data* to an RT, not colour.
Fogging it corrupts every consumer downstream.

**Pass 2** `PondFoamOverlay` (`:412`, `Blend SrcAlpha OneMinusSrcAlpha`, ZWrite Off) draws **after**
the underwater fog pass and needs the same treatment, or the foam sits crisp on fogged water. Same
macro trio; the v2f is shared, so the `UNITY_FOG_COORDS(4)` slot is already paid for.

### Verification gate before F1 counts as done
URP must actually be populating the `FOG_LINEAR` / `FOG_EXP` / `FOG_EXP2` keywords and
`unity_FogParams` for a `CGPROGRAM` pass tagged `UniversalPipeline`. **Assume nothing** — enable fog
in one scene, set an aggressive linear range, and confirm the water moves. If the keywords are not
set, F1 becomes "compute the factor by hand from `unity_FogParams`", which is a different (still
small) patch. `boatest.unity` currently has `m_Fog: 0`, `m_FogMode: 3` (Exp2), `m_FogDensity: 0.01` —
a fine test bed once toggled on.

---

## 4. Increment F2 — the blended siblings (`ChunkWall`, `ExclusionWall`, `Transparent`)

All three are `HLSLPROGRAM`: `half3 MixFog(half3 color, half fogFactor)` with
`half fogFactor = ComputeFogFactor(positionCS.z)` transferred per-vertex, plus
`#pragma multi_compile_fog`.

**⚠️ Premultiplied-alpha trap.** `WaterChunkWall` and `WaterExclusionWall` both use
`Blend One OneMinusSrcAlpha`, i.e. `rgb` is *already multiplied by coverage*. `MixFog` lerps toward
`unity_FogColor` as if `rgb` were straight colour, so applied naively it injects fog light into
pixels the surface does not cover — a glow along every chunk edge. On a premultiplied target the fog
colour must be scaled by the pixel's own alpha before the lerp. `WaterTransparent` is
`SrcAlpha OneMinusSrcAlpha` (straight) and takes `MixFog` as-is.

`WaterExclusionWall` already declares `_CameraUnderwater` (`:63`) — reuse it as the submerged gate
here, since these shaders have no early return equivalent to Pass 0's.

---

## 5. Increment F3 / F4 — particles and the fullscreen veil

**F3 — `FoamParticles` / `SplashParticles`.** Both already carry `fogMul : TEXCOORD5` and
`fogAdd : TEXCOORD6` — **the water's own camera→sprite volumetric transmittance and in-scatter**
(`FoamParticles.shader:247–248`, `SplashParticles.shader:116–117`). Unity fog must **compose with**
these, not replace them: apply the volumetric terms first (they are the underwater regime), then
scene fog on the result. Next free interpolator is **TEXCOORD7** in both. Both are `CGPROGRAM` →
legacy macros.

**F4 — `FoamDensityComposite`.** A **fullscreen triangle** from `SV_VertexID`
(`FoamDensityComposite.shader:104–121`) — there is no geometry to transfer a per-vertex fog coord
from, and `o.pos` is raw NDC. Fog here must be computed **per pixel** from the depth it already taps
(`_CameraDepthTexture` is declared at `:93`), reconstructing eye depth for the surface it is veiling.
Lowest priority: it is a foam density veil, and it will look acceptable unfogged for longer than the
walls will.

**Never fog, ever:** `OceanSurfaceEyeDepth`, `WaterChunkDepth`, `WaterExclusionDepth`,
`ObstacleDepth`, `CausticOccluder`, `WaterCausticProjection`, every `_WaterDebugMode` view, and every
compute pass. All of them carry data, not colour.

**`GodRays.shader` is `Blend One One` (additive).** Scene fog on an additive pass must **attenuate
only** — multiply by transmittance, never lerp toward `unity_FogColor`, or the god rays *add* fog
light and the shafts brighten with distance instead of dissolving. Same for the `LargeBodyGodRays`
composite.

---

## 6. Trap list (all specific to this codebase)

1. **Double-fog with the horizon haze.** `_HorizonHazeDensity` already dissolves the far ocean into
   the sky. Both at saturation over the same band = a muddy grey wall. Per §1, run haze for the
   sky boundary and fog for the land match; do not drive both to 1.
2. **Refraction is already fogged.** `RefractionStage` reads `_CameraOpaqueTexture`, which URP has
   *already* fogged. Fogging the water on top fogs that content twice. Minor in magnitude, but it is
   the thing that will look subtly wrong first — most visible in shallow clear water over fogged
   terrain.
3. **The debug branch must stay unfogged** (`WaterSurface.shader:303–304`). It is a return path, so
   getting the insertion order wrong is silent.
4. **Premultiplied blend + `MixFog`** — see §4.
5. **Additive passes tint instead of dissolve** — see §5.
6. **The particles' existing `fogMul`/`fogAdd` are NOT scene fog** — see §5. Do not "consolidate"
   them; they are the volumetric underwater path.
7. **`OceanSurfaceEyeDepth` is not a colour pass.** It is the single most damaging place to paste
   the macro by pattern-matching.
8. **Interpolator budget.** Pass 0 has 0–3 used → 4 free. Particles have 0–6 used → 7 free. Check
   before adding; a silent overflow on a WebGPU target is a compile failure at the worst moment.

---

## 7. Suggested order and cost

| Increment | Scope | Value |
|---|---|---|
| **F1** | `WaterSurface.shader` Pass 0 + Pass 2, `WaterSurfaceVertStage.hlsl` | the shoreline seam — most of the visible win |
| **F2** | `WaterChunkWall`, `WaterExclusionWall`, `WaterTransparent` | chunk/pond edges stop floating |
| **F3** | `FoamParticles`, `SplashParticles` | spray stops popping out of the fog |
| **F4** | `FoamDensityComposite` | the density veil; lowest priority |

F1 first, alone, and **look at it** before F2 — if URP is not feeding the keywords to a `CGPROGRAM`
pass, that discovery changes the recipe for every increment below it and is worth finding out for the
price of one file.

## 8. Open decisions for Bert

1. **Haze sampling: repair or delete?** (§1) Repair keeps the auto-sky-match — the only thing haze
   does that fog cannot. Delete is honest if fixed-colour is the look you actually ship.
2. **Should scene fog be exposed as a per-body toggle**, or always-on when Unity fog is enabled?
   A toggle costs an inspector field and a keyword; always-on is one less knob to get wrong.
3. **How far down the increment list to go before shipping.** F1+F2 is a coherent release. F3/F4 can
   follow.
