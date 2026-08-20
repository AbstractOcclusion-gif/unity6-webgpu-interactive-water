# Wave-Crest & Shore-Breaking "Rolling" Foam Particles — Deep Research + Design

**ThreeJSWaterPort · `com.abstractocclusion.webgpuwater` · 2026-08-20**
**Scope (per Bert):** shore breaking-wave *rolling* foam particles + wave-crest foam particles. Ocean whitecaps and shore breakers are the priority; treat them as *separable* systems that share one pool + one renderer. Ignore persistent/deposited foam for now. Splash bursts are **out of scope and untouched**. A full refactor of the auto-generated foam/crest particle path is on the table.
**Status:** research + design only. No code changed. Nothing here ships without your explicit go-ahead (project rule: *ask before touching code*).

---

## 0. TL;DR — the one thing that has been wrong

You have been trying to grow crest-rolling foam out of `KIND_RIPPLE_CREST`, which reads your **interactive ripple sim**. But your **shore breaking waves do not live in that sim** — they are *analytic geometry* (`WaterSurfWaves.hlsl`: a closed-form height field + a foam texture). And on ocean/shore bodies the particle `Spawn` kernel **early-returns and emits nothing** (`#ifdef OCEAN_FFT_GLUE return;`, line 1044). So there is literally no particle riding a shore wave face today — which is exactly why every crest-rolling test "goes wrong": you are tuning a mechanism that is never fed the wave it is supposed to roll off of.

KWS gets rolling crest foam almost for free, and the reason is a single architectural fact: **KWS's shore waves and its foam particles share one field.** Its shore breakers *are* a shallow-water velocity+height field (injected from a baked shoreline SDF), so a foam particle that sits on the wave face literally feels the face collapse beneath it and gets thrown. The whole "rolling" look is one line in KWS's update kernel:

```
particle.isFreeMoving = previousDeltaHeight > 2.0;   // the surface dropped fast under me → I detach and fly
```

Everything else (gravity, air drag, tumble, re-land, drift) hangs off that trigger.

**The good news:** your analytic surf model already exposes a *richer* crest signal than KWS's crude `divergence > 0.1` — it hands you the lip line, the throw direction, the breaker type (spill/plunge/surge), and a lifecycle clock. And your ballistic-spray + deposit + density-render machinery is already the exact skeleton a crest-rolling particle needs. You are missing *one bridge*, not a new engine. This doc lays out that bridge two ways (a light architecture-honoring path, and the full KWS-style unification), ranks them, and gives you a small-increment plan so the next test does **not** go wrong.

---

## 1. The three-surfaces problem (root-cause architecture)

Your water renders three independent surface-shape systems. Only one of them contains a velocity field, and it is not the one your shore waves live in:

```
                         ┌──────────────────────────────────────────────────────────┐
                         │                YOUR CURRENT ARCHITECTURE                   │
                         └──────────────────────────────────────────────────────────┘

  (1) FFT OCEAN            (2) ANALYTIC SURF / SHORE          (3) INTERACTIVE RIPPLE SIM
      cascades                  WaterSurfWaves.hlsl                WaterSim.compute
  ─────────────           ───────────────────────────        ────────────────────────
  height (swell)          height (breaking front geom)        height + velocity(!) 
  Jacobian whitecaps      breaker / whitewash / overCap       SimHorizontalFlow(!)
  NO velocity field       lipShape / toShore / trailAge       FoamTex (mask)
  NO particles            breakType (spill/plunge/surge)       ← boat wakes, mouse
                          NO velocity field                    ← THE ONLY REAL FLOW
                          NO particles (shades a texture)
        │                          │                                    │
        │                          │                                    │
        └──────────┐               │            ┌───────────────────────┘
                   ▼               ▼            ▼
             ┌───────────────────────────────────────────┐
             │        WaterFoamParticles.compute          │
             │  KIND_SURFACE / SPRAY / BUBBLE / RIPPLE_CREST │
             │                                             │
             │  reads:  Sim, SimHorizontalFlow, FoamTex    │  ← only sees system (3)
             │  Spawn(): #ifdef OCEAN_FFT_GLUE return;     │  ← systems (1)(2) emit NOTHING
             │  SurfSampleAt(): used ONLY for height glue  │  ← breaker signal wasted
             └───────────────────────────────────────────┘
```

Consequences, precisely:

- **Shore breakers spawn zero particles.** The `Spawn` kernel bails on any `OCEAN_FFT_GLUE` body before the ambient/surf path. The surf-lip spray the comments describe (lines 368, 1067) is *vestigial* — the wiring exists, the emission is short-circuited.
- **`KIND_RIPPLE_CREST` is doing crest-detection the hard way** because it has no real crest signal to read. It reconstructs "is this a breaking front?" from the ripple sim's height curvature via a *peakness stencil*, a *front-coherence* gate (13-tap signed/abs velocity sum), and a *foam-mask feedback* path, then routes flecks through 3 LOD density tiers. That is a very clever pile of compensation for a missing input. It is fragile because curvature-on-a-ripple-field is an intrinsically noisy crest proxy — you have been fighting the grid chop, not authoring foam.
- **Your best crest signal is thrown away.** `SurfSampleAt()` already computes `breaker`, `whitewash`, `overCap`, `lipShape`, `toShore` inside the compute — and uses none of it to spawn. This is the single highest-leverage fact in this document.

---

## 2. How KWS actually does it (decoded from KWS2 source)

KWS's rolling crest foam rests on **three pillars**. Pillars 1–2 are the mechanism you want; pillar 3 you have already ported.

### Pillar 1 — one unified shallow-water field that *includes* the shore

KWS runs a single SWE-ish dynamic-waves sim (`KWS_DynamicWaves.shader`) on a grid: `RGBA16 = (velX, velZ, heightOffset, terrainHeight)`. The **shore is inside this sim**: a baked top-down depth camera → jump-flood **shoreline SDF** (`KWS_JumpFloodSDF.shader`) gives a signed distance + inward `shoreDir`, and incoming ocean/FFT waves are injected at that shoreline band. So a breaking shore wave is a real moving mound of water with a real velocity vector — the same field the foam particle reads. **This is the thing your architecture doesn't have**: your shore wave is analytic geometry, not sim state.

Alongside it KWS maintains a second RT, `DynamicWavesAdditionalData = (wetMap, shorelineMask, foamMask, wetDepth)`:

- **`shorelineMask`** — a static distance-to-shore ramp (`smoothstep(1,25, sdfDist)`), from the baked SDF. Marks "this is the surf band," extends particle life, biases direction.
- **`foamMask`** — the *feedback* channel, and a big part of why KWS foam trails and rolls. Each frame it is **advected backward along the flow and blended 95%**, decayed slowly, and re-fed by physically-motivated sources:

```
foamAdvectionVel   = vC*1.5 + turbulenceNoise*1.5
oldFoam            = lerp(additional.z, sample(uv - foamAdvectionVel*dt).z, 0.95)   // memory that drifts
newFoam            = crestFoam + turbulenceFoam + shorelineFoam + compressionFoam
foamMask           = saturate(oldFoam - slowDecay + newFoam)

crestFoam    = max( smoothstep(..,hC-avgH)*smoothstep(..,curvature),      // wave peak
                    smoothstep(..,slope)*smoothstep(0.04,0.35,-div)*0.5 )  // breaking front = CONVERGENCE (-div)
               * smoothstep(0.15,0.65, froude)                            // only fast (supercritical) water
               * pow2(saturate(dot(flowDir, shoreDir)))                   // only water heading AT the shore
```

Note what the crest source really is: **negative divergence (convergence/compression) of a fast, shoreward-moving surface** — a pile-up front. That is the physical definition of a breaking wave face, and it is cheap.

### Pillar 2 — the particle that rides the field and detaches when the face collapses

This is the crest-rolling mechanism. KWS's foam particle (`KWS_DynamicWavesFoamParticlesCompute.compute`):

**Spawn** (per sim texel, 8×8): gate `height>0.05 && waveSpeed>0.05 && divergence>0.1`, then accept foam if `(foamMask > thr) OR (divergence > thr)` and a random roll passes. Emit a **cluster** offset along `-flowDir` and `±perpDir` (a little fan behind the crest). Life = `FoamParticleLifetime ± 40%`. Carry `shorelineMask` per particle (shore foam lives longer). If `waveSpeed > 7`, spawn already `isFreeMoving`.

**Update** — the whole "rolling" behavior is a two-state machine:

```
deltaHeight       = (surfaceHeight - particle.y) / dt
previousDeltaHeight = particle.prevY - particle.y
isFreeMoving      = previousDeltaHeight > 2.0        // the surface fell >2 m/s beneath me

IF isFreeMoving (THROWN / TUMBLING):
    velocity += (0,-9.8,0)*dt_sliced + airDrag(-velocity*dt*(1+rand))
    position += velocity*dt
    y = lerp(max(surfaceHeight, y), y, rand)          // some ride the surface up, some fly free
ELSE (RIDING / ADVECTING on the surface):
    flow  = NormalizeDynamicWavesVelocity(waves.xy) * groundDrag
    curl  = SampleCurlNoiseArray(pos)                  // divergence-free swirl
    velocity = lerp(velocity, lerp(flow, curl, 0.1), forceMul)
    position.xz += velocity * dt_sliced
    position.xz += clumpAttraction(perlin) * FoamClumping
                   * sin(life·π)·slowness              // gather AFTER the crest passes
    y = surfaceHeight                                   // glued to the surface
```

Read the trigger physically: while the foam sits on the *back* of an advancing wave the surface under it is rising, `previousDeltaHeight < 0`, it stays glued and advects. The instant the crest passes and the **front face drops out from under it** (`previousDeltaHeight > 2`), it detaches, gravity + air drag take over, it arcs forward and **tumbles down the collapsing face**, then re-lands (`max(surfaceHeight, y)`) and resumes advecting. That is rolling crest foam. No breaker classification, no lip geometry — it *emerges* from a particle on a real collapsing surface.

### Pillar 3 — screen-space density render (you already have this)

Not lit billboards. Each live particle `InterlockedAdd`s `1` into one of **3 LOD count buffers** (bigger on-screen → coarser buffer), then a shading pass turns counts into foam: `foamLow = density·0.2` (linear, thin spray) `+ foamHigh = density²·0.5` (quadratic, bright cores), tinted by surface volumetric light `×(0.75,0.85,1)`, dilated 1px, composited **additively**. Your `RasterizeDensity` + `FoamDensityComposite` + tier buffers are a faithful port of this already. **The one KWS render trick you're missing:** the particle billboard is *stretched up to 7× along its screen-space motion vector* (`stretch = lerp(2,7, speed)`), which is what makes fast crest foam read as forward-smeared streaks instead of round dots.

---

## 3. What the literature says (the mechanism map)

The academic + production landscape converges on a small set of reusable ideas. Full source list in §10.

**Ihmsen et al. 2012, "Unified Spray, Foam and Bubbles"** — the canonical foam-spawn criterion everything (Houdini Whitewater, Bifrost, most engines) descends from. Foam is born from three scalar potentials: **wave-crest** = surface curvature summed over *convex* neighbours, gated by an outward-velocity flag `v̂·n̂ ≥ 0.6` (an *advancing* crest, not a trough); **trapped-air** = relative-velocity convergence (your `-div`); **kinetic energy** as a multiplier (no energy → no foam). Motion is classified by neighbour count → *spray (ballistic) / foam (advected, velocity discarded) / bubble (buoyant)*, and — crucially — **a foam particle thrown above the surface loses neighbours and reclassifies to ballistic spray automatically.** That reclassification *is* the advected↔ballistic switch; KWS's `deltaHeight` trigger is the height-field-cheap version of it.

**Thürey & Müller-Fischer 2007, "Real-time Breaking Waves for Shallow Water"** — the paper most directly on your target, and it models the overturning lip explicitly in real time. Break where `|∇H| > p_H·g·Δt/Δx AND ∇H·u < 0` (a steep front *opposing* the flow). Then emit an overturning sheet whose **crest moves faster than its base**: `u_s = (1 + p_v·g·(H − H_base))·u`. That single term is what curls the lip forward — steal it verbatim as the initial throw velocity for a thrown crest particle. On surface re-impact it spawns secondary spray. The one GPU-hostile part is its serial flood-fill to build the wave *line*; you don't need it (emit per steep texel / per lip sample and let the density buffer merge coverage).

**FFT Jacobian fold (Tessendorf lineage)** — the deep-water whitecap trigger you already use in spirit on the surface shader: `J = det(I + ∂(horizontal displacement))`; `J < bias` ⇒ the surface is folding/pinching ⇒ whitecap. This is your **ocean-crest** emitter's spawn field (offshore, where there is no shore breaker signal).

**Bridson curl-noise** — a divergence-free swirl to perturb *settled* foam advection without a flow sim. You already do this (`FoamNoiseGrad` rotated 90°); KWS does the same (`SampleCurlNoiseArray`). Keep it.

**Crest (Unity asset) / "mass-preserving" foam** — for the *settled* phase, foam is a scalar deposited into a texture that multiplicatively decays and advects with the flow. This is KWS's `foamMask` feedback in texture form. You have `FoamTex`; whether to advect it is a §7 decision.

**Ranked ideas worth stealing** (synthesised): (1) Thürey `crest-faster-than-base` throw + `∇H·u<0` front test; (2) Ihmsen/KWS **advected↔ballistic state switch** as the core update; (3) Jacobian fold as the offshore ocean-crest gate; (4) advected+decaying foam feedback for the settled trail; (5) density-accumulation render (done) + motion-stretch (to add). WebGPU caveats: no geometry shaders (emit instanced/indirect, not extruded sheets), only 32-bit **integer** atomics (your fixed-point `InterlockedAdd` density path is already the correct workaround), no SPH neighbour search (use height-field curvature `∇²η` and divergence `∇·u` instead — cheaper *and* atomics-free).

---

## 4. The crest-rolling mechanism (the heart) — one state machine for all three sources

Whatever architecture you pick (§6), the particle behaviour is the same four-state life. This is the design you should hold in your head; the three emitters differ only in *how state SPAWN is triggered and the initial throw*.

```
                    ┌──────────────────────────────────────────────────────────────┐
                    │            CREST-ROLLING PARTICLE — STATE MACHINE             │
                    └──────────────────────────────────────────────────────────────┘

  ┌──────────┐  spawn on crest signal            ┌──────────────┐
  │  SPAWN   │  (breaker / Jacobian / -div)       │   (dead)     │
  │  on lip  │  initial v = throw (see below)     └──────────────┘
  └────┬─────┘                                            ▲ age ≥ life
       │ born already thrown for a PLUNGE lip             │ or off-frame
       ▼                                                  │
  ┌──────────────────┐   surface fell away?    ┌──────────────────────────┐
  │  THROWN / TUMBLE │◄────── yes ─────────────│  RIDING / ADVECT (foam)  │
  │  (ballistic)     │   deltaHeight > Vdrop   │  glued to surface        │
  │  v += g·dt       │                         │  v ← flow (+curl +clump) │
  │  v += airDrag    │────── landed? ─────────►│  y  = surfaceHeight      │
  │  x += v·dt       │   y ≤ surfaceHeight &    │  drift shoreward         │
  │  tumbles forward │   v.y < 0  → convert     │  (this is the "roll")    │
  └──────────────────┘                         └───────────┬──────────────┘
       ▲ plunge lip re-throw                               │ fade
       └───────────────────────────────────────────────────┘
                                                            ▼
                                                    density splat → render
```

**The two transitions that make or break the look:**

1. **RIDING → THROWN.** The KWS trigger `previousDeltaHeight > V_drop` (≈2 m/s). This needs a *surface height the particle can compare against frame to frame*. You already compute exactly that: `SurfaceWorldY(worldPos, simNorm)` sums FFT swell + surf fronts + ripple. So even for analytic shore waves you can detect "the face collapsed under me" by carrying `prevSurfaceY` on the particle and testing `(surfaceY - prevSurfaceY)/dt < -V_drop`. **This is the key realization: you don't need a numeric velocity field to detect collapse — you need the surface height's time derivative, which you can already sample.**

2. **THROWN → RIDING (land).** Your `KIND_SPRAY` already does this: ballistic integrate, and when `worldPos.y ≤ landingEpsilon && v.y < 0` → `DepositFrom()` → `KIND_SURFACE`. The only change for crest foam is landing against `SurfaceWorldY` instead of `y=0`, and converting to an advecting foam that drifts *shoreward* (`toShore`) rather than a static deposit.

**The initial throw (Thürey `crest-faster-than-base`), classified by breaker type:**

```
throwSpeed  = (1 + k_v · g · (H − H_base)) · |u_shore|        // crest overruns its base
throwDir    = normalize(toShore + upBias·(0,1,0))
             ── PLUNGE (breakType.y): high upBias, born THROWN, lands ~faceLen ahead (violent barrel)
             ── SPILL  (breakType.x): low upBias, stays RIDING longer, gentle forward roll/tumble on the face
             ── SURGE  (breakType.z): suppressed — no airborne foam (throw ≈ 0)
```

Your surf model already computes the plunge **landing lobe** (`~faceLen` shoreward of the crest) and the `breakType` weights — so the thrown particle even knows *where it should land*.

---

## 5. Inventory — what you already have (reuse, don't rebuild)

Before proposing anything new, here is what is already in the codebase and directly reusable. This matters because your pain is "each test goes wrong" — the way to stop that is to reuse proven machinery and change *one* thing per test.

**Reusable as-is (the skeleton is already there):**

- **Ballistic → land → deposit path.** `KIND_SPRAY` integrate + `DepositFrom` is the THROWN→RIDING half of the state machine. Crest foam is a spray that (a) is born on the lip and (b) lands as *advecting* foam.
- **Density render.** `RasterizeDensity` + tier buffers + `FoamDensityComposite` = KWS pillar 3, done. Crest foam should splat through the exact same path (it already accepts `KIND_RIPPLE_CREST`).
- **WebGPU-safe pool.** `ClaimPoolSlot` (dead-slot probe, never stomps live foam), world-anchored spawn keys, stochastic distance LOD, frame budgets. Keep all of it.
- **The shore field is already bound into the compute.** `ShoreFoamState.BindTo` is called for `_kSpawn`, `_kUpdate`, `_kRasterizeDensity`. `SurfSampleAt(worldXZ, out depth, out influence, out toShore)` works *inside the compute today*. The breaker signal is one function call away in the exact kernel that needs it.
- **`SurfaceWorldY`** — the analytic surface height for collapse-detection and landing.

**The goldmine — signals your analytic surf model already exposes** (`EvaluateSurfWaves` → `SurfWaveSample`):

| field | meaning | use in the particle system |
|---|---|---|
| `breaker` | 0..1 cresting-lip line (plunge-amplified, surge-killed) | **spawn gate** for shore-crest particles |
| `overCap` | `H/(γ·d)` lifecycle clock (<1 unbroken, ~1 breaking, >1 broken) | when to throw / fade timing |
| `lipShape` | timing-free lip footprint | spawn density along the lip |
| `toShore` | unit vector toward the waterline | **ballistic throw direction** |
| `breakType` | (spill, plunge, surge) partition | throw strength + up-bias classification |
| `whitewash` | broken-bore + trail coverage | settled foam carpet (already shaded) |
| `trailAge` | seconds since crest passed | fade the rolled foam behind the crest |

This is strictly *more* information than KWS's spawn path has. KWS reverse-engineers "is this a breaking front?" from `-divergence`; you get it handed to you, pre-classified by breaker type, with a throw direction attached.

**What to cut (the compensation machinery):** once a real crest signal feeds spawning, the `KIND_RIPPLE_CREST` front-coherence gate (13-tap signed/abs sum), the peakness stencil, and the foam-mask re-emission path are all solving a problem you no longer have. They exist to extract a crest from a noisy ripple field; both paths in §6 give you a clean crest instead. Retire them — that alone removes most of the fragility.

---

## 6. Two architectures (ranked)

### ▶ Option A — Analytic-driven crest particles (RECOMMENDED first)

Keep your three surface systems. **Bridge the analytic surf model into the particle spawner** and let the state machine of §4 run against `SurfaceWorldY`. No new sim.

```
   WaterSurfWaves (analytic)                 WaterFoamParticles.compute
   ─────────────────────────                 ──────────────────────────
   SurfSampleAt(xz) ──breaker──►  SPAWN shore-crest particle on the lip
                    ──toShore──►  throw shoreward, up-biased by breakType
                    ──overCap──►  timing / fade
        FFT Jacobian ──J<bias──►  SPAWN ocean-crest particle offshore
                                     │
                                     ▼
                        §4 state machine vs SurfaceWorldY()
                        (RIDE ⇄ THROW via d/dt SurfaceWorldY)
                                     │
                                     ▼
                        RasterizeDensity → composite  (unchanged)
```

- **Shore-crest emitter:** in `Spawn`, *before* the `OCEAN_FFT_GLUE return`, add a surf branch: sample `SurfSampleAt`; if `breaker > thr`, roll a cluster on the lip, born THROWN for plunge (up-biased ballistic) or RIDING for spill, throw dir = `toShore`.
- **Ocean-crest emitter:** offshore (low `shoreInfluence`), gate on the FFT Jacobian fold `J < bias` (needs the cascade displacement, which `OCEAN_FFT_GLUE` already binds). Born RIDING; the `d/dt SurfaceWorldY` trigger throws them when a steep swell crest passes.
- **Collapse trigger without a sim:** carry `prevSurfaceY`; `throw when (SurfaceWorldY - prevSurfaceY)/dt < -V_drop`. Reuses your existing spray integrate for the airborne arc.
- **Render:** unchanged. Add motion-vector stretch to the crest splat for the streak look.

**Pros:** reuses everything proven; smallest diff; testable in tiny increments; the surf model's signals are *better* than KWS's; naturally separable (three emitters, one pool, one render — exactly your "separate things"); no second sim, no WebGPU SWE cost. **Cons:** the throw is *scripted* off analytic signals rather than *emergent* from a real fluid, so violent barrel interiors won't self-organize the way a true overturning sim would; ocean-crest foam relies on `d/dt` of a summed analytic height (fine, but it's a derivative of an approximation).

### ▶ Option B — KWS-style unified SWE shore field (the full refactor)

Put shore breakers into an actual shallow-water velocity field (extend your ripple sim to inject shore waves from the SDF, KWS-style, or add a dedicated coastal SWE band), then port KWS's spawn/advect/`isFreeMoving` verbatim. One particle path for ocean + shore + ripple crest.

**Pros:** the rolling motion becomes *emergent* and physically coherent; unifies all crest sources; `foamMask` advected-feedback gives free trailing foam; it is the closest to KWS's actual result. **Cons:** a second sim (or a major sim extension) on a WebGPU budget; you must reconcile the *numeric* sim height with your *analytic* surf render (today the surface draws analytic surf fronts — the sim would have to agree or you'd get double waves); this is the biggest, highest-risk change, and your history says big changes are where tests go wrong.

### Recommendation

**Do Option A now, keep Option B as the north star.** Option A delivers rolling crest foam by *connecting things you already built*, in increments small enough that a failed test tells you exactly which knob was wrong. It also de-risks Option B: the state machine, the throw model, the render, and the emitter separation you build in A are exactly what B reuses — if you later decide the analytic throw isn't emergent enough, you swap the *spawn/collapse source* from analytic signals to a real SWE field and keep the whole particle life intact.

---

## 7. Separation of the three emitters (your "separate things")

One pool, one renderer, one state machine — three spawn sources with different signals and throws:

```
 EMITTER          SPAWN SIGNAL                     BORN           THROW                 LIVES ON
 ───────────      ─────────────────────────        ──────         ──────────────        ────────────
 SHORE-CREST      surf.breaker > thr               THROWN(plunge) toShore, up=breakType  surf band
  (priority)      (lip line, pre-classified)       RIDING (spill) crest-faster-than-base near waterline
 OCEAN-CREST      FFT Jacobian J < bias            RIDING         thrown by d/dt surfaceY open sea
  (priority)      (offshore whitecaps)             → THROWN when crest passes             swell crests
 RIPPLE-CREST     sim -divergence + foamMask       RIDING         mild (wake compression) interactive
  (keep, SIMPLIFY)(replace coherence machinery)    → THROWN rare                          boat/mouse
```

They differ *only* in the spawn block. Give each a profile section (you already audit UX as an Asset Store product — this maps cleanly to per-emitter foldouts) and let all three share the pool budget, the density tiers, and the fade envelope. This is also the honest answer to "all of them but separate": **one system, three switchable sources.**

A note on the settled trail: KWS's advected-decaying `foamMask` is what makes rolled foam *stay and drift* after the crest. For **shore**, your analytic `whitewash` + `trailAge` already give that carpet — you likely don't need a new feedback field there. For **ripple/ocean**, if the rolled foam looks too transient, the cheapest fix is advecting `FoamTex` backward along the flow (KWS's 95% lerp), not more per-particle logic.

---

## 8. Proposed increments (nothing ships without your OK)

Small, independently testable, each with a clear pass/fail. This directly targets "each test gone wrong": one variable per step.

1. **Wire the signal (no behaviour change yet).** In `Spawn`, compute `SurfSampleAt` on shore bodies and *visualize* `breaker`/`toShore` (debug splat where `breaker>thr`). **Test:** the debug dots trace the moving lip line. If they don't, the problem is signal/plumbing, isolated before any particle physics.
2. **Spawn RIDING shore-crest foam on the lip** (no throw yet), advecting shoreward along `toShore`, density-rendered. **Test:** a band of foam sits on the breaking line and drifts to the beach. This alone will already look like KWS's spilling foam.
3. **Add the collapse trigger + ballistic throw** (`d/dt SurfaceWorldY`, reuse spray integrate; land → advect). **Test:** foam detaches at the crest and tumbles forward — the rolling look. Tune `V_drop` and the throw `k_v` here, in isolation.
4. **Breaker-type classification** (plunge = born thrown/high up-bias; surge = suppressed). **Test:** steep beach barrels throw hard, flat shelf spills gently, cliffs throw nothing.
5. **Ocean-crest emitter** (Jacobian offshore). **Test:** whitecaps roll off open-sea swell crests.
6. **Simplify `KIND_RIPPLE_CREST`** — retire the coherence/peakness/foam-mask machinery, respawn it from `-divergence` like KWS. **Test:** wakes still fleck, with far less code.
7. **Render polish** — motion-vector stretch on the crest splat. **Test:** fast crest foam reads as forward streaks.

Steps 1–3 are the whole "rolling crest" win; 4–7 are refinement. Each is a byte-scoped diff you can revert cleanly.

---

## 9. Failure-mode checklist (why past tests "went wrong", pre-empted)

- **Nothing spawns on shore.** The `OCEAN_FFT_GLUE return` gate — the surf branch must sit *before* it, or be explicitly exempted.
- **Foam glued to the camera.** World-anchored spawn keys + the window-edge age band already fix this for the ambient path; the crest emitters must use the *same* world-lattice keys, not window-texel ids.
- **Crest foam floats below the rendered wave / gets depth-culled.** Land and ride against `SurfaceWorldY` (which includes the surf fronts), never `y=0` — your bug-comment at line ~655 is exactly this class.
- **Throw looks like a fountain, not a roll.** Up-bias too high for spilling; `V_drop` too low (everything detaches). Plunge should throw up; spill should mostly tumble forward on the face.
- **Strobing / popping under load.** Keep `ClaimPoolSlot` (dead-slot probe) and the per-frame budgets; never a raw ring stomp.
- **Grid chop read as crests.** Only a risk if you keep deriving crests from ripple curvature. The analytic `breaker` and the FFT Jacobian don't have this failure — which is the point of §5's "cut the machinery."
- **CPU/GPU surf mismatch.** Always evaluate the surf model on `_ShoreFoamTime` (the wrapped surf beat), never raw `_WaveTime` — the CPU mirror (`LargeWaveField.SurfFrontHeight`) agrees only on that clock.

---

## 10. Sources

Academic / technique:
- Ihmsen et al. 2012, *Unified Spray, Foam and Bubbles for Particle-Based Fluids* — https://cg.informatik.uni-freiburg.de/publications/2012_CGI_sprayFoamBubbles.pdf
- Thürey & Müller-Fischer 2007, *Real-time Breaking Waves for Shallow Water Simulations* — https://matthias-research.github.io/pages/publications/breakingWaves.pdf
- Jeschke & Wojtan, *Water Wave Packets* (2017) / *Water Surface Wavelets* (2018) — https://research-explorer.ista.ac.at/download/470/7359/wavepackets_final.pdf · https://dl.acm.org/doi/10.1145/3197517.3201336
- Yuksel, *Wave Particles* (2007) — https://www.cemyuksel.com/research/waveparticles/
- Wretborn, Flynn & Stomakhin 2022 (Wētā), *Guided Bubbles and Wet Foam* — https://www.physicsbasedanimation.com/2022/08/12/guided-bubbles-and-wet-foam-for-realistic-whitewater-simulation/ · https://dl.acm.org/doi/10.1145/3528223.3530059
- Bridson, Hourihan & Nordenstam, *Curl-Noise for Procedural Fluid Flow* — https://history.siggraph.org/learning/curl-noise-for-procedural-fluid-flow-by-bridson-houriham-and-nordenstam/
- SideFX Houdini Whitewater (production Ihmsen) — https://www.sidefx.com/docs/houdini/fluid/whitewater.html

Production / real-time:
- FFT Jacobian foam derivation — https://rtryan98.github.io/2025/10/04/ocean-rendering-part-1.html
- Crest ocean foam + shoreline sim — https://crest.readthedocs.io/en/4.12/user/ocean-simulation.html
- Assassin's Creed IV: Black Flag ocean breakdown — https://simonschreibt.de/gat/black-flag-waterplane/
- Halftone / density-accumulation whitewater render — https://ianparberry.com/pubs/GAMEON-NA_GRAPH_04.pdf
- KWS2 Dynamic Water System — https://assetstore.unity.com/packages/tools/particles-effects/kws2-dynamic-water-system-323662

Primary source read directly this session: your `WaterFoamParticles.compute` / `WaterSim.compute` / `WaterSurfWaves.hlsl`, and KWS2 `KWS_DynamicWavesFoamParticlesCompute.compute` / `KWS_DynamicWaves.shader` / `KWS_DynamicWavesFoamParticlesShading.shader` / `KWS_DynamicWavesHelpers.cginc` / `KWS_DynamicWavesSimulationZone.cs`.
