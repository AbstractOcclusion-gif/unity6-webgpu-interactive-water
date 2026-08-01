# PLAN — Bubbles v1 (`KIND_BUBBLE` + surface bubbles + fresh/salt regime)

Date: 2026-08-01. Grounding: `docs/RESEARCH_bubbles_spray_particles_2026-08-01.md` (physics numbers,
KWS on-disk verification). Design-gated — **no code until the decisions in §6 are locked.**

## 1. What this adds and why

The GPU pool implements two of the three canonical whitewater classes (spray → foam via landing
conversion). Nothing in the package goes *below* the surface. Physically, every impact that throws
droplets up also drives a bubble plume down to roughly the throw height — this is nearly the whole
visual of a fresh-water (pool) splash, and the white core of an ocean breaker.

Target behavior (from measured physics, not invented):

| Property | Value | Source |
|---|---|---|
| Plume depth | ≈ impact strength, ~0.5 m for a pool splash, ~1× wave height for a breaker | Cifuentes-Lorenzen 2023 |
| Rise speed | ramp to terminal ~0.20–0.30 m/s (size-scaled) | Baz-Rodríguez; KWS ships gravity −0.012 ≈ same band |
| Wobble | only above ~2 mm visual size; amplitude ∝ size | PNAS path instability |
| Life | 2–4 s (surface kill, not timer death, when reachable) | Deane & Stokes plume ~1 s + rise time |
| Surface arrival | clamp to `surface − size`, fade alpha near surface | KWS `KWS_UnderwaterBubbles.shader` guards |
| On surfacing | salt: convert to foam fleck; fresh: brief surface bubble, then gone | coalescence physics |

## 2. Architecture — one pool, one new kind

Stay inside `WaterFoamParticles` (pool, budgets, exclusion, fog reroute already exist):

- **New `KIND_BUBBLE`** beside `KIND_SURFACE` / spray in the compute. Spawned with **downward**
  velocity from the same events that make droplets.
- **Motion (Update kernel):** buoyant acceleration toward a size-scaled terminal rise speed +
  drag toward local flow (reuse the sim flow read) + wobble = existing `FoamNoiseGrad` field
  applied in XZ with amplitude ∝ size (no new noise system — reuse-never-rewrite).
- **Death:** `worldPos.y >= SurfaceWorldY(...) - size` → surface conversion (regime-dependent),
  plus the normal exclusion-volume dissolve. `SurfaceWorldY` already handles pool sim height AND
  ocean FFT glue (today's `OCEAN_FFT_GLUE` rename) — bubbles ride the same placement math.
- **Spawn sources:**
  1. `QueueSplashBurst` grows a bubble share — same request struct, N bubbles injected downward
     in a cone under the impact point, depth/count ∝ strength.
  2. (with the plunging plan) surf-lip landings and cascade landings inject plumes.
  3. NOT ambient — no turbulence-driven bubbles in v1 (ambient underwater fizz à la KWS
     "InfiniteUnderwaterBubbles" is a separate later increment, camera-following, cheap).

## 3. Rendering — `_DrawKind = 3`, static sprite, shader wobble (no flipbook)

KWS-proven recipe, verified in their shader on disk:

- One 256² generated bubble sprite (rim-bright circle; color/shine/alpha channel-packed) +
  optional 128² normal. **Authored by a bake script, not hand-painted** — reproducible.
- Per-pixel life = scrolling noise distorting sprite UVs (strength ∝ size) — NOT texture animation.
- Draw in the same procedural-quad path as foam (`SV_VertexID`), new draw kind, billboards.
- **Fog/lighting: bubbles are underwater geometry** — they must take underwater fog. Route through
  the public transparent API (`WebGpuWaterFogAPI.hlsl` / the `WaterFogTransparent` reroute shipped
  2026-07-31) rather than inventing a third fog path. Same armed-frame reroute discipline as the
  existing foam draws (`cs:766` withhold).
- Above-water camera: bubbles are visible through the surface as distorted brightness — v1 draws
  them only for underwater pixels (KWS kills splash quads underwater with a NaN trick; we do the
  inverse: kill bubble pixels that are NOT under the water surface, cheap test against the
  fog/waterline data already bound).

## 4. Surface bubbles (the "few drifting bubbles" from the pool session)

- A deposit **variant** of `KIND_SURFACE`: when a bubble surfaces in FRESH water, it converts to a
  surface-bubble particle — same floating advection as foam, different sprite (2–4 dome-cluster
  variants in the existing foam flipbook atlas slots), short life (< 1 s), slight scale-up then gone.
- Pop micro-flipbook (4–8 frames, dome → ring): OPTIONAL later garnish, baked from the FLIP baker.
  Not in v1 — at foam-fleck screen size a fade reads the same.

## 5. Fresh/salt regime knob

One enum + two floats, on the **Foam Profile** (so one asset styles a body):

- `WaterFoamRegime { Salt, Fresh }` (default Salt = current behavior).
- Fresh: bubble share of bursts ↑ (e.g. 0.7), deposited-foam life ×0.65 and deposit size/rate ↓
  hard, surfaced bubbles → surface-bubble variant.
- Salt: bubble share moderate, surfaced bubbles → ordinary foam flecks (current deposit path).

## 6. Decisions to lock before any code

1. **Bubble budget:** bubbles share the one pool. A plunge can flood it. Per-kind soft caps
   (spray tile budget precedent) or accept sharing? *(My default: share, cap bubbles per burst.)*
2. **Knob placement:** regime on the Foam Profile (proposed) vs WaterVolume? Profile "Drive"
   toggles pattern must be respected.
3. **Sprite authoring:** accept a Python/editor bake script generating the bubble sprite + normal
   into `Runtime/Textures` (wizard-wired like other defaults)?
4. **Above-water visibility rule:** hard-kill bubble pixels above the waterline (proposed) vs
   refraction-aware fade?
5. **v1 scope:** burst-driven plumes only (proposed), ambient fizz + lip-landing plumes later?
6. **Struct room:** `FoamParticle` is 12 floats — kind/generation/wobble-phase packing must fit
   without growing the stride if possible. If a 13th float is unavoidable, stride grows for ALL
   particles — needs a yes.

## 7. Traps (known before starting)

- **Sorting:** bubbles draw at the foam pass's queue point; underwater they must composite with
  volumetric fog correctly on BOTH camera sides of the waterline — test the crossing strip
  (the 07-31 waterline traps memory applies).
- **Density mode:** the screen-space density veil is floating-foam-only; bubbles must be excluded
  from `RasterizeDensity` (kind filter) or they'd fog the veil from below.
- **WebGPU binds:** new textures on the foam material — check reachable texture count (16-sampler
  d3d11 rule lives in the shading memory).
- **Editor preview:** pool is play-mode only; bubbles inherit that (fine — same as foam).

## 8. Test checklist (when built)

Pool demo: mouse splash → visible plume, rises in ~1–2 s, pops at surface with no foam residue
(Fresh). Ocean shore: breaker face shows white sub-surface core moving with the bore (Salt →
flecks). Exclusion demo: no bubbles inside the carve. Waterline crossing: no popping at the
half-submerged frame. Budget: burst spam does not evict floating foam (slot-claim drop rule).
