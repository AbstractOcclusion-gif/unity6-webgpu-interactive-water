# Foam Systems Audit & Enhancement Plan — 2026-07-11

Scope requested by Bert, in order: (1) ocean wave foam, (2) ripple foam + splashes + flipbook/textures,
(3) foam particles. Goal: de-overlap the mess and push realism. References mined:
KWS Water System 2 (top ref for particle render), WaveHarmonic Crest 5.9.3, Stylized Water 3,
RAM 3, NWH DWP2 — all inside `KWSWater`.

STATUS: analysis + plan only. **No code changes made — awaiting authorization per item.**

---

## PART A — What we have today (verified in code)

Three independent foam-GENERATION pipelines, four foam SHADING paths, two SPLASH systems.

### A1. Ocean whitecaps
- Gen: `OceanFft.compute` → `ComputeNormal`. Jacobian fold → fresh foam; in-cascade
  wind-drift advection on r32f history (`OceanFoamPrev/Next`); split decay
  (`OCEAN_FOAM_DENSE_REFERENCE 0.6`); result stored in `OceanNormal.w`, crest pinch in `.y`.
- Shade: `WaterSurface.shader` lines ~819–896. Dedicated `_OceanWhitecapTex(+Normal)`,
  2-octave rotated anti-tiling (`OCEAN_WHITECAP_OCTAVE2_SCALE 2.37`, blend 60 m),
  contrast `pow 1.6`, dissolve smoothstep, matte reflection/SSS knockdown, wrapped diffuse.
- Knobs: coverage 1.0, strength 1.0, fadeRate 0.5, deposit 0.85, drift 0.08, max 1.0,
  tileSize 8, feather 0.25, windThreshold 4.

### A2. Ripple/sim foam (pond, shoreline, wakes)
- Gen: `WaterSim.compute` → `Foam` kernel. Semi-Lagrangian advection (cap 4 texels),
  diffusion, gen from `speed*_FoamFromSpeed + curvature*_FoamFromCurv`, Froude breaking foam,
  shore band, split decay via per-step survival (`FOAM_FRESH_THRESHOLD 0.5`).
- Shade: `WaterSurface.shader` ~898–944 (above) + ~701–727 (underwater silhouette).
  `_FoamTex` 4×4 flipbook dragged along flow (2 cross-faded phases), core/lace split,
  `_FoamCoreCut`, `_FoamFeather`, relief from `_FoamNormalTex`.
- Knobs: genRate 0.6, decay 0.96 / residual 0.993 / decayRate 1.0, spread 0.2, advect 3,
  fromSpeed 6, fromCurvature 30, strength 1, feather 0.15, coreCut 0.5, border 0.08, contact 0.06.

### A3. Foam particles (GPU, KWS Phase 2)
- `WaterFoamParticles.compute` (BeginFrame/Spawn/Update, 48-byte struct, ring pool,
  KIND_SURFACE + KIND_SPRAY) + `FoamParticles.shader` (procedural quads, `_ParticleTex` 2×2
  atlas, optional flipbook) + `WaterFoamParticles.cs` (capacity 4096, spawnThreshold 0.25,
  sprayChance 0.15).
- Spawn source = `max(sim foam, ocean crest .w)` under `OCEAN_CREST_FOAM`.

### A4. Splashes
- `WaterSplash.cs` (rigidbody punch-through) → `WaterSplashEmitter.cs` (Shuriken drift
  droplets + `SplashFlipbook_8x8` crown) → `SplashParticles.shader`.
- `WaterSphereInteractor` + `SphereInteract` kernel: velocity dipole → wake → feeds A2 foam.

### A5. Textures (all from `Samples\Demos\Generated\Sources~\gen_*.py`)
`FoamFlipbook_4x4` + normal, `OceanWhitecap` tile + normal, `FoamParticleAtlas_2x2`,
`SplashFlipbook_8x8`.

### A6. The mess (frank list)
1. **3 foam solvers re-implement advection + split decay** with incompatible parameterizations
   (rate vs survival factor; dense ref 0.6 vs fresh threshold 0.5). No shared HLSL helper.
2. **Ocean + pond foam shading blocks can BOTH run and stack** on an ocean body with sim foam
   enabled → double foam. Underwater block is a third copy of the look.
3. **Foam lighting constants copy-pasted in 3 shaders** (`FOAM_LIGHT_WRAP 0.4`,
   `FOAM_AMBIENT 0.35`, `EROSION_SOFTNESS 0.35`) — WaterSurface, FoamParticles, SplashParticles.
4. **Two overlapping spray systems**: GPU KIND_SPRAY droplets vs Shuriken drift droplets + crown.
   Same visual role, different tech, different textures.
5. **Double-rendered crests**: particle Spawn reads the same ocean `.w` the surface shading
   already drew — coverage foam + particle quads render the same crest with no handoff.
6. **6 foam texture slots** across 4 sampling styles; `_OceanFoamTileSize` vs `_FoamTex_ST` naming clash.
7. **Redundant decay knob surface**: pond has 3 decay controls; ocean has 4 overlapping ones.
   User-facing intent ("how fast does foam die / linger") expressed differently per system.
8. Stale `OCEAN_FOAM_DESIGN_2026-07-10.md` (describes unbuilt separate OceanFoamField.compute).
9. Debug leftovers: `VisualizePreview` kernel, `OceanFftDebug.compute`, retired `_HorizonFadeDistance`.
10. `OceanNormal.y/.w` channel repurposing documented only in a comment — fragile.

---

## PART B — What the references do better

### B1. KWS (top ref — the marvelous particle render)
Files under `Assets\KriptoFX\WaterSystem2\WaterResources\`.
1. **Screen-space density-accumulation foam.** Foam particles are NOT drawn as quads:
   `RenderParticlesToScreenSpaceBuffer` `InterlockedAdd`s each particle into a density buffer
   at 3 LOD resolutions (2048/1024/512 wide), then a fullscreen pass maps
   density → foam (`foamLow = sat(d)*0.2; foamHigh = sat(d²*0.01)*0.5`), a **max-filter
   dilation pass** closes pinholes, and it composites additively — lit by volumetric surface
   light with a cool `(0.75,0.85,1)` tint. Thousands of particles, near-zero overdraw,
   reads as *connected* sea foam.
2. **Channel-packed splash sheet.** ONE 4-frame `WaterSplash.png`:
   R=mass, G=shine, B=dissolve-noise, A=depth/thickness. Lifetime erosion
   `noise = sat(noise − life*2 + 1)` (organic disintegration, not alpha fade);
   `shine³` for tight sparkle; velocity stretch `pow5(gravityFactor)*speed`-driven;
   rotation around base pivot; soft-particle vs min(waterDepth, sceneDepth) plus the A channel;
   blue-noise dithered shadow casting.
3. **One sim-owned foam field, three consumers.** `GetFoamMask` sums crest (Froude-gated),
   turbulence (|curl|+shear), shoreline (SDF band), compression (flow vs obstacle) → advected
   + fast-then-slow decay. Particles, splash AND surface shading all read the same field.
   Wakes are emergent — no special wake emitter.
4. **Spawn quality levers**: divergence-gated spawn; per-64px-screen-tile splash budget
   (`InterlockedAdd` capped 15–50/tile) → even coverage, no clumps; stochastic distance LOD
   (`lod > random²`) → dithered density falloff, no pop; 4-frame time-slicing with
   prev→cur interpolation (structs carry prevPosition/prevLifetime).
5. **Foam motion richness**: animated curl-noise array (32-slice) + Perlin **clumping**
   (attraction pulls foam into streaks); grounded foam has Manning-style depth drag;
   fast particles (`speed>7`) flip to ballistic free-moving and land back.
6. **Underwater bubbles are a separate, simple system**: classic particle mesh, R=shine/G=color/A=alpha,
   `color*0.1 + shine*20`, clamped below the surface, lit with volumetric light + absorption-by-depth.
7. Surface foam: ONE tiling `KW_FluidsFoamTex` (4 variation channels selected by mask),
   stochastic/hex sampling for ocean, advected two-phase UVs for flow, and
   `foam = sat(pow(tex, contrast) − (1 − sqrt(mask)))` — contrast *relaxes* where mask is
   dense so heavy foam goes solid. Foam suppresses sun specular (matte cue).

### B2. Crest 5.9.3 (`Packages\com.waveharmonic.crest`)
1. `UpdateFoam.compute`: R16 persistent cascade buffer @ fixed 30 Hz;
   `foam += 5*dt*strength*sat(coverage − jacobianDet)`; decay `*= 1 − fadeRate*dt`;
   shoreline foam sampled at the **displaced** position; clamp.
   ≈ what we already built — confirms our architecture.
2. **`_FilterWaves`**: exclude the finest cascades from foam gen near camera — kills
   "too much foam at your feet" without touching the waves.
3. **Prewarm**: on load/teleport substitute `dt = 1/fadeRate` (steady state) → settled foam frame 1.
4. **Sliding black-point feather** `smoothstep(1−foam, 1−foam+feather, tex)` (feather 0.75)
   — foam *grows out of* the noise texture. We already do a variant; theirs is cleaner.
5. **Procedural foam normals from albedo finite differences** — no normal map needed,
   self-flattens where foam is absent. (We ship 2 generated normal maps we could drop.)
6. Boat-wake trails = just presets: fadeRate 0.1, strength 3.27, FilterWaves 0. No wake code.
7. Foam 2nd channel reused for **bioluminescent sparkle** (emissive glow from the same mask).
8. Crest has NO spray particles at all — everything stays on the surface.

### B3. Others
- **SW3**: foam **parallax** `uv −= k * viewDir.xz * sat(dot(N,V))` — fake foam height, one line.
  Dual-scale distance foam. Dissolve = same sliding-threshold idea.
- **RAM 3**: channel-packed foam textures (R foam / G cascade / B height / A side-foam) per water type.
- **DWP2** (`WaterParticleSystem.cs`): sprays from **waterline triangles** of a submerged mesh,
  emission + alpha proportional to rigidbody velocity (alpha cap 0.15) — the model for bow spray.

---

## PART C — Enhancement plan (Bert's order)

Each item independent; authorize à la carte. Effort: S < half-day, M ~ a day, L multi-day.

### C0. De-overlap first (prerequisite hygiene)
- **C0.1 (S) Shared `WaterFoamCommon.hlsl`**: one copy of the lighting constants
  (LIGHT_WRAP/AMBIENT/EROSION), one advect+split-decay helper parameterized the same way for
  ocean + sim foam, one dissolve/feather function. Retune once, applies everywhere.
- **C0.2 (S) Exclusive foam compositing** in `WaterSurface.shader`: ocean block and pond block
  combine via `max()` into ONE mask shaded by ONE foam-lighting path (also reused underwater)
  — kills double-foam stacking and the triplicated look.
- **C0.3 (M) Unify spray**: retire either Shuriken drift droplets or GPU KIND_SPRAY as the
  droplet carrier (recommend: keep GPU KIND_SPRAY, keep Shuriken ONLY for the crown flipbook,
  which is a one-shot CPU event anyway). One droplet texture, one lighting path.
- **C0.4 (S) Docs/debug cleanup**: mark OCEAN_FOAM_DESIGN doc superseded; delete
  `VisualizePreview`/`OceanFftDebug` or gate behind a DEBUG define; comment block documenting
  the OceanNormal .y/.w channel contract at the declaration site.
- **C0.5 (S) Knob rationalization**: expose decay everywhere as (fadeRate, deposit) pair —
  ocean already does; convert pond's 3 controls to the same pair internally
  (survival = exp(−rate·dt) mapping), keep serialized fields for compat.

### C1. Ocean wave foam
- **C1.1 (S) FilterWaves** (Crest): skip cascade contributions to foam gen near camera
  (already ×0 on slice 0 — extend to a distance-aware slice filter). Fixes near-camera foam soup.
- **C1.2 (S) Prewarm**: first ocean-foam dispatch uses dt = 1/fadeRate → settled whitecaps on scene load.
- **C1.3 (S) Foam suppresses specular fully + roughens**: we matte the reflection; also lerp
  smoothness down (Crest 0.7) so foam breaks the specular sheet — biggest cheap realism cue.
- **C1.4 (M) KWS contrast law**: replace fixed `pow(pattern, 1.6)` with
  `sat(pow(tex, contrast) − (1 − sqrt(coverage)))` where contrast relaxes as coverage rises —
  dense whitecaps go solid instead of staying lacy.
- **C1.5 (M) Procedural foam normals** (Crest finite-difference from albedo) → drop
  `_OceanWhitecapNormalTex` (and optionally `_FoamNormalTex`), one less texture + self-flattening relief.
- **C1.6 (S) Foam parallax** (SW3 one-liner) on whitecaps: fake lift above the surface.

### C2. Ripple foam + splashes + flipbook/textures
- **C2.1 (M) One shared foam field contract (KWS model)**: keep the two solvers (ocean cascades
  vs sim texels are legitimately different domains) but define one *semantic*: a single
  `FoamAt(worldXZ)` HLSL entry returning max(ocean, sim) used by surface shading, particles,
  and splashes. Removes double-count at every consumer.
- **C2.2 (M) Turbulence term** in `WaterSim.compute` foam gen: add |curl|+shear of velocity
  (KWS) alongside speed+curvature — wakes and interactor turbulence foam where flow shears,
  not just where it's fast.
- **C2.3 (M) Splash sheet upgrade — channel-packed RGBA (KWS)**: regenerate
  `gen_splash_flipbook.py` to output R=mass, G=shine, B=dissolve-noise, A=thickness;
  shader: lifetime erosion via B (`sat(B − life*2 + 1)`), `shine³` sparkle, thickness-aware
  soft fade. This is the single biggest splash-realism win.
- **C2.4 (S) Velocity stretch + base-pivot rotation** on splash billboards (KWS formulas in B1.2).
- **C2.5 (S) DWP2-style velocity-proportional splash**: scale `WaterSplash` emission count,
  droplet speed AND alpha by impact speed (alpha capped low ~0.15) — fast objects throw
  sheets, slow ones dribble.
- **C2.6 (S) Texture regen pass**: whitecap tile + flipbooks re-authored at higher frequency
  detail via the existing gen_*.py pipeline (and/or LiquiGen per foam-texture-authoring-tools
  memory) once C2.3 fixes the format.

### C3. Foam particles (KWS render as target)
- **C3.1 (L) Screen-space density foam — the flagship.** Replace per-quad rendering of
  KIND_SURFACE with KWS's model: particles `InterlockedAdd` into 2–3 LOD density buffers,
  fullscreen density→foam curve + max-dilation + additive composite, lit with our existing
  scatter light + cool tint. Keeps textured quads ONLY for KIND_SPRAY. WebGPU-friendly
  (atomics on uint buffers, no readback). Solves overlap #5 too: surface-coverage foam and
  particle foam merge in one composite.
- **C3.2 (M) Spawn quality trio (KWS)**: stochastic distance LOD (`lodDistance > rand²`),
  per-screen-tile spray budget, divergence gate from sim velocity — even coverage, no clumps,
  no pop.
- **C3.3 (M) Curl noise + clumping**: small curl-noise array (can generate at runtime) +
  Perlin attraction so surface foam forms streaks/patches instead of uniform sprinkle.
- **C3.4 (M) Time-slicing**: 2–4 buckets with prevPos/prevLife interpolation (struct already
  has room) → 4× capacity at same sim cost.
- **C3.5 (M) Underwater bubbles**: separate tiny system (KWS model): sprite with
  R=shine/G=color/A=alpha, clamped below surface, lit by our absorption/volume-scatter path.
  We currently have nothing underwater except the foam silhouette.

### Suggested batches
1. **Batch 1 — cleanup**: C0.1, C0.2, C0.4, C0.5 (mostly S; makes everything after safer).
2. **Batch 2 — cheap realism**: C1.1–C1.3, C1.6, C2.4, C2.5.
3. **Batch 3 — splash + texture format**: C2.3 + C2.6, C1.4, C1.5, C2.2.
4. **Batch 4 — flagship particle render**: C3.1 + C3.2, then C3.3/C3.4/C3.5, and C0.3 folds in.

Open questions for Bert:
- C0.3: OK to make GPU KIND_SPRAY the only droplet system (Shuriken keeps just the crown)?
- C3.1 is the big rewrite of FoamParticles rendering — green-light as its own session?
- C1.5 removes the two generated foam normal maps — OK to drop those assets?
