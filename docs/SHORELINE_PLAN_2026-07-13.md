# Shoreline Implementation Plan — Auto SWE Base + Hero Crashers

Date: 2026-07-13. Companion to `docs/SHORELINE_PLANNING_PROMPT_2026-07-13.md` (grounded recon + reference file paths). Status: **plan for approval — no code until Bert signs off, and each code-touching layer gets its own go.**

## Decisions locked (Bert, 2026-07-13)
- **Two distinct layers, not equated.** (1) An **automatic base shoreline** that runs everywhere from terrain depth. (2) A **separate, optional hand-placed hero-crasher layer** (KWS1-style) that sits on top for set pieces. The heroes are additive polish; the base stands on its own.
- **Base breaking is emergent from a shallow-water (SWE) zone** — not baked flipbooks. Flipbooks are only the hero layer.

## Still-open (confirm before the dependent layer; defaults chosen so we don't stall)
- **Buoyancy reach** — default: **shoreline is render-only first** (sidesteps the WebGPU readback wall). Physical run-up/breaker push on floaters is a later, opt-in extension. Confirm before Layer C locks.
- **KWS1 hero assets** — the hero layer needs KWS1's baked flipbook + foam-buffer assets, or a reproduced bake. Default: **defer the hero layer to last (Layer E)** so this question only bites when we get there.

## Non-negotiable constraints (carried into every layer)
- **WebGPU: no GPU→CPU readback.** The whole base backbone (ortho depth, jump-flood SDF, shoaling, SWE compute, foam sim) is GPU compute + textures — readback-free, so portable. Readback only matters if a feature must reach CPU buoyancy; each layer states whether it does.
- **WebGPU won't filter float32** — depth/SDF/foam RTs use half or manual-bilinear (`_WaterTexel` pattern). WebGPU build uses the Mobile URP asset.
- **Reuse, never rewrite** — adapt KWS2/Crest/KWS1 techniques as thin adapters into our RenderGraph/URP17 path.
- **Fresh names** — `_Shoreline*` and `FormerlySerializedAs("shoreline*")` already mean *bed tint*. New shore uniforms use a new prefix (proposed `_Shore*`/`_SwZone*`; finalize in Layer A).
- **Reviewable chunks** — every layer compiles, is independently revertible, and has a verification gate.

---

## Architecture at a glance

```
        Terrain / seabed
              │
   ┌──────────▼───────────┐
   │ LAYER A  Depth + SDF │  world-frame ortho depth capture → jump-flood SDF
   │ (shared substrate)   │  → distance-to-shore, direction-to-shore, depth
   └──────────┬───────────┘
              │ (one field, consumed by everything below)
   ┌──────────▼─────┐ ┌───────▼────────┐ ┌────────▼────────┐
   │ LAYER B Shoal  │ │ LAYER C SWE    │ │ LAYER D Foam    │
   │ attenuate swell│ │ zone: breaking │ │ whitecap +      │
   │ by depth/λ     │ │ + run-up (emerg)│ │ shoreline foam  │
   └────────────────┘ └────────────────┘ └─────────────────┘
                          AUTOMATIC BASE
   ───────────────────────────────────────────────────────
   ┌─────────────────────────────────────────────────────┐
   │ LAYER E (separate)  Hand-placed hero crashers +      │
   │ shoreline color / wet sand / spray polish            │
   └─────────────────────────────────────────────────────┘
```

---

## Layer A — World-frame depth + jump-flood SDF substrate  *(build first)*

**Goal.** One camera-following field, valid on bounded *and* ocean bodies, giving per-texel: seabed depth below sea level, signed distance-to-shore, and direction-to-shore. This is the fix for the root flaw (today's bed is pool-frame, bounded-only, off).

**Reference to adapt.** KWS2 `CommandPass/KWS_DrawToDepth.shader` (ortho seabed capture) + `CommandPass/KWS_JumpFloodSDF.shader`; Crest `Data/Input/Hidden/DepthTexture.compute` + `JumpFloodSDF.compute` + `Scripts/Data/DepthLod.cs` (SDF toggle, R16G16 depth+distance formats). Ours to extend: `Runtime/WaterBedBaker.cs` (today's pool-frame bake) — generalize to a camera-following world-frame capture pass, or add a new pass alongside it.

**Approach.** A top-down orthographic pass renders terrain/seabed height into a world-aligned, camera-following, quantized-step depth RT (half-float). A jump-flood compute derives the SDF (distance + direction) from the waterline (depth ≈ 0 crossing). Publish as globals via our uniform publisher; mind the RenderGraph `SetGlobalTextureAfterPass` gotcha (see memory `unifiedwater-surface-render`).

**New names.** `_ShoreDepthTex` (half R or RG), `_ShoreSdfTex` (RG: signed distance + packed direction), `_ShoreFieldCenter/Size/Res` (camera-follow frame). Do **not** reuse `_Shoreline*`.

**WebGPU.** Pure compute + textures, readback-free → portable. Half-float, manual bilinear where filtered.

**Buoyancy reach.** None yet (field is for rendering + downstream sims).

**Verification.** Debug view that visualizes depth and SDF distance/direction; confirm the waterline tracks irregular terrain and the camera-follow has no swim/seams. Confirm it lights up on an ocean/windowed body (where the old bed was absent).

**Risk.** Camera-follow quantization seams; frame alignment vs our clipmap. Phase 0 must locate the exact RenderGraph hook points before coding.

---

## Layer B — Shoaling (waves respond to depth)

**Goal.** Swell/wind waves slow, steepen, and shorten as depth drops — the single most-missed cue since the cleanup.

**Reference to adapt.** Crest `Data/Input/Hidden/AnimatedWavesGenerate.compute:188-206` — `weight = saturate(2*depth/averageWavelength)` (deep when depth > ½λ, else ramp down), with a max-attenuation-depth floor. Apply to our swell/wind path in `Runtime/Shaders/WaterLargeWaves.hlsl` (and its guarded C# mirror `LargeWaveField.cs` **only if** shoaled height must reach buoyancy — default no).

**Approach.** Sample `_ShoreDepthTex` in the large-wave evaluation; attenuate per-component amplitude by depth/wavelength. Keep it a render-only shader term first (no C# mirror) to avoid re-opening the byte-for-byte tax; the Phase-2 constants guard already protects the swell constants.

**New names.** `_ShoreAttenuationDepth`, `_ShoreAttenuationEnabled`.

**WebGPU.** Shader-only texture read → fine.

**Buoyancy reach.** Default none (render-only). If floaters must feel flattened shallows later, add a mirror term then.

**Verification.** Swell visibly bunches/steepens toward shore on a sloped beach; deep water unchanged; no regression on pools with no bed.

**Risk.** Interaction with the existing distance band-limit; tune so shoaling and LOD band-limit don't fight.

---

## Layer C — SWE shore zone: emergent breaking + run-up

**Goal.** A local shallow-water simulation near the waterline that produces emergent breaking fronts and wave run-up/drain — the automatic crashing base.

**Reference to adapt.** KWS2 `KWS_DynamicWaves.shader` (Saint-Venant height/velocity integration + `Advection`) + `KWS_DynamicWavesHelpers.cginc` (`AddFFTWaves` run-up along `shoreDir`, breaker front `smoothstep(0.015,0.08,slope)*compression`, `OceanRelax`), driver `DynamicWavesPass.cs`, zone `KWS_DynamicWavesSimulationZone.cs`. Cross-check Crest `com.waveharmonic.crest.shallow-water/.../ShallowWaterSimulation.compute` for the wet/dry **leading-edge** mask (`CrestMaskEdge`/`CrestExpandEdge`) and stability (overshoot reduction, velocity clamp). Prior art: Thürey et al. breaking-waves paper.

**Approach.** A camera-following SWE grid gated to the near-shore band (from Layer A's SDF). Feed our swell shoreward along `shoreDir` scaled by `shorelineMask`; integrate height/velocity; detect breaking from slope×compression; output a height/velocity field the surface adds near shore and a foam source for Layer D. Blend back to open-water height away from shore (relax).

**New names.** `_SwZoneTex` (height+vel), `_SwZoneCenter/Size/Res`, `_SwZoneEnabled`, run-up/friction/drain tunables.

**WebGPU.** SWE is GPU compute + ping-pong textures, readback-free → portable. Watch float32 filtering.

**Buoyancy reach.** **This is the decision point.** Default render-only (floaters don't feel run-up). Physical coupling would need the height reachable without readback — deferred extension.

**Verification.** Waves visibly steepen → break → run up the beach → drain, tracking an irregular waterline; stable (no blow-up) over long runs; near-far blend seamless.

**Risk.** Highest-risk layer: SWE stability, grid resolution vs cost, frame-coherent camera-follow, and the near/far blend seam. Sequence it *after* A+B are solid and consider shipping A+B first as a visible win.

---

## Layer D — Shore foam (whitecap + waterline)

**Goal.** Real foam at breaking crests and along the waterline, replacing today's pool-rectangle border band; advected and decayed, not static.

**Reference to adapt.** Crest `Data/UpdateFoam.compute` (Jacobian whitecap `saturate(coverage - det)` + shallow-depth shoreline foam `saturate(1 - depth/maxDepth)`, flow-advect, fade-decay) + shading `Surface/Foam.hlsl` (multi-scale tiled texture, black-point feather, derivative foam normal). KWS2 `GetFoamMask` (crest/breaking/shore/turbulence sources, advected + `DecayFoamFastThenSlow`). Reuse **our** existing foam systems (`WaterSim.compute` foam kernel, `WaterFoamParticles.*`, textures `FoamFlipbook_4x4.png` / `FoamParticleAtlas_2x2.png`, `OceanWhitecap.png`) as the substrate — don't add a parallel foam stack.

**Approach.** Add two foam sources fed by Layers A/C: negative-Jacobian whitecap at breaking fronts, and shallow-depth foam along the SDF waterline; advect + decay in the existing foam buffer; feed the existing particle-density composite for spray.

**New names.** `_ShoreFoamStrength`, `_ShoreFoamMaxDepth`, `_BreakFoamStrength`.

**WebGPU.** Compute + textures → fine.

**Buoyancy reach.** None (foam is visual).

**Verification.** Foam appears on breaking crests and hugs the irregular waterline, rolls shoreward, and fades; no pool-rectangle artifact; ocean whitecaps unaffected.

**Risk.** Tuning coverage vs over-foaming; reconciling shore foam with existing ocean whitecap so they don't double up.

---

## Layer E — Hero crashers (separate layer) + polish

**Goal.** Optional hand-placed KWS1-style breaking waves for set pieces, plus shoreline color / wet-sand darkening / spray.

**Reference to adapt.** KWS1 `ShorelineWavesPass.cs` + `KWS_ShorelineWaves.shader` (instanced flipbook quads → area displacement/normal RT read by the surface) and `ShorelineFoamPass.cs` + `KWS_ShorelineFoamParticlesCompute.compute` (baked foam-particle splat). Shoreline color: Crest `Data/ShorelineColor.compute`.

**Approach.** A distinct, opt-in system: instanced quads driven by baked flipbooks into an area RT our surface adds — placement by hand (or auto-seeded along the SDF later). **Blocked on the KWS1 asset question** (reuse baked assets vs reproduce the bake).

**WebGPU.** Runtime mechanism is instanced draw + RT read → portable. The *bake* is offline.

**Buoyancy reach.** None (render-only heroes).

**Verification.** A placed hero crasher renders and animates on top of the auto base without fighting it.

**Risk.** Asset availability (default: defer). Keep strictly separate from the auto base so it can't destabilize it.

---

## Sequencing & first slice
1. **Layer A** (substrate) — foundational, unblock everything, and a debug-viz win.
2. **Layer B** (shoaling) — first *visible* shoreline improvement; ship A+B together as the first milestone.
3. **Layer C** (SWE) — the big, risky one; only after A+B are stable, buoyancy-reach confirmed.
4. **Layer D** (foam) — rides on A/C.
5. **Layer E** (heroes + polish) — separate, last, gated on the KWS1 asset question.

Each layer starts with a Phase-0 read-only grounding pass (re-verify the recon against the live tree, locate exact RenderGraph hook points) and ends with its verification gate. Bert approves the plan, then each layer's go before code.

## Two confirmations to move
- **Layer C buoyancy**: shoreline render-only first (recommended), or must floaters physically feel run-up/breakers?
- **Layer E assets**: do we have KWS1's baked flipbook + foam-buffer assets to reuse, or reproduce the bake (or drop the hero layer)?
