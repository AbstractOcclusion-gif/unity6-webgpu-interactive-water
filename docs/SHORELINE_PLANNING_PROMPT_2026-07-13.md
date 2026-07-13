# Shoreline Design — Analysis & Planning Prompt

Date: 2026-07-13. Status: brief for a planning session. No code until Bert signs off on the resulting plan.

---

## 0. How to use this document

You are a senior Unity graphics engineer. Your job is **not to write code yet** — it is to analyze our water package against three reference systems and produce a **phased, layered implementation plan** for a marvelous *automatic* shoreline. Follow the project rules: reuse working code as thin adapters (never rewrite), verify every claim against the actual files (never guess), keep every change a small reviewable chunk, and get Bert's explicit go before touching code. This brief hands you the grounded starting facts so you don't re-derive them; confirm anything load-bearing before building on it.

The mission: on any water body, where water meets land, waves should **shoal** (slow, steepen, bunch), **break/crash** convincingly, **run up and drain** the beach, and grow **foam** at the crest and the waterline — all **driven automatically from terrain depth**, with an optional hand-placed hero-crash layer for set-piece moments.

---

## 1. Grounded current state of our package (verified 2026-07-13)

Package: `Packages/com.abstractocclusion.webgpuwater/`. The branch `cleanup/remove-swe-shoal-foam` **stripped the previous shoreline stack**, so we start from a nearly clean slate. Confirmed against the live tree (the older `docs/swe_audit_current.txt` and `docs/foam_audit_current.txt` are PRE-cleanup and now INACCURATE — do not trust them).

What still exists:
- **Contact foam** — `Runtime/Shaders/WaterSurface.shader:1017-1031`. Screen-depth test faded over `_FoamContactDepth`. Gated `_FoamEnabled>0.5 && _SimWindowed<0.5` — **bounded bodies only**, skipped on ocean/windowed. Self-described at line 1019 as "unreliable (it fought the shore/SWE work)."
- **"Shoreline foam" is really a pool-rectangle border band** — `WaterSurface.shader:1013-1015`, `_FoamBorderWidth`, keyed to axis-aligned pool edges in pool space. **Not** shore- or depth-aware; useless on irregular terrain waterlines.
- **Bed/depth field** — `Runtime/WaterBedBaker.cs` bakes `Terrain.SampleHeight` into a pool-space R-float `_BedTex` (`Rebake()` 39-67). **Off by default, bounded-only, static, single-terrain, pool-frame.** Published `WaterVolume.cs:2724-2725` (disabled when `_windowed`); sampled in `WaterSim.compute:201-203` and `WaterSurface.shader:1081-1092` (deep-water tint + dry-beach `clip()`).
- **Manual hero wave** — `Runtime/WaterHeroWave.cs` + `.hlsl`. Opt-in, transform-placed, depth-agnostic overturning-lip strip. Our only "breaking wave."
- **Foam systems**: interactive sim foam (`WaterSim.compute` Foam kernel → `WaterSurface.shader:999-1058`, texture `Samples~/Demos/Common/FoamFlipbook_4x4.png`); GPU particle foam (`WaterFoamParticles.cs`/`.compute` + `FoamParticles.shader` + `FoamDensityComposite.shader`, atlas `FoamParticleAtlas_2x2.png`); ocean whitecap (`OceanFftFoam`, `OceanWhitecap.png`).

What was REMOVED (must be rebuilt, not repaired):
- **Shoaling** — `SwellShoalFactor` gone from every shader and CPU. Waves no longer respond to depth at all.
- **SWE / Saint-Venant** — no shallow-water momentum solver. The sim (`WaterSim.compute` Update, 335-378) is a **linear wave-equation** pool solver; bed coupling is only "hold flat on dry land" + an 8% outer drain band. It explicitly prevents run-up (ripples reflect off the waterline).
- **Depth-based shoreline/breaking foam** — `shorelineFoamDepth/Strength`, `breakingFoamStrength` all gone.

Traps to avoid (naming collisions from the cleanup):
- `_ShorelineDepthScale` / `_ShorelineStrength` uniforms and `ID_ShorelineScale/Strength` (`WaterUniformPublisher.cs:48-49,319-320`) now drive the **deep-water bed tint**, not shoreline anything. `WaterVolume.cs:1046,1048` carry `[FormerlySerializedAs("shoreline*")]`. New shore work must pick fresh names or it will clash.

---

## 2. The core design flaw to fix first (Layer A)

Every recurring failure in the memory log (SWE saga, coastline plan, distance-empty water) traces to one root: **we have no world-frame depth + shoreline field.** Our bed is pool-frame, single-volume, off by default, and absent on the ocean/windowed path — so nothing downstream (shoaling, foam, breaking, run-up, color) has a reliable "how deep / how far to shore / which way is shore" signal on open water.

**Both KWS2 and Crest solve this the same way, and it is the thing to build first:** a top-down **orthographic depth capture** of the seabed/terrain (camera-following, quantized) plus a **jump-flood SDF** giving, per texel, signed distance-to-shore and direction-to-shore. This one substrate feeds *everything* else. It is GPU-only (compute + textures), so it is **WebGPU-safe** (see §5). Get this world-frame depth+SDF layer right and the rest becomes tractable; skip it and we relive the coordinate-frame saga.

---

## 3. Reference playbook (analyze these — exact paths)

### KWS1 (`KWS1` project) — the crashing-wave *look*, baked
Root: `Assets/KriptoFX/WaterSystem/WaterResources/`.
- Crashing waves = **pre-baked flipbook (VAT-style)**: offline-simulated breaker baked to displacement/normal/alpha flipbooks (`Resources/Textures/ShorelinePos.kwsTexture`, `ShorelineNorm.png`, `ShorelineAlpha.png`), rendered as **GPU-instanced quads** into a 2048² camera-area displacement/normal RT that the surface then adds (`Scripts/Core/CommandPass/ShorelineWavesPass.cs`; `Shaders/.../KWS_ShorelineWaves.shader` — 14×15 @18fps flipbook; consumed in `KWS_WaterPassHelpers.cginc:964-1014`).
- Foam = **pre-baked GPU particle splat** compute buffers, not a texture (`ShorelineFoamPass.cs`; `KWS_ShorelineFoamParticlesCompute.compute`; buffers `Resources/ComputeBuffers/ComputeFoamData0.kwsComputeBuffer`).
- Shore detection = **manual placement** (`KWS_EditorShoreline.cs`) + runtime ortho terrain depth (`OrthoDepthPass.cs`).
- **Caveat for us**: gorgeous, cheap, but (a) depends on baked assets we don't have (need their assets or reproduce the offline bake) and (b) placement is manual — it is a *hero-crash add-on*, not an auto system.

### KWS2 (`KWSWater` project) — automatic SWE shoreline
Root: `Assets/KriptoFX/WaterSystem2/WaterResources/`.
- **Shore substrate**: ortho seabed depth (`CommandPass/KWS_DrawToDepth.shader`) → jump-flood SDF (`CommandPass/KWS_JumpFloodSDF.shader`) → `shorelineMask`/`shoreDir` in `KWS_DynamicWaves.shader:359-385`.
- **Breaking + run-up**: local **Saint-Venant sim zone** (`KWS_DynamicWaves.shader` velocity/height integration + `Advection`), fed by ocean FFT pushed shoreward along `shoreDir` (`KWS_DynamicWavesHelpers.cginc::AddFFTWaves`, run-up `lerp(2,0.5,windIncoming)`); breaker front `= smoothstep(0.015,0.08,slope)*compression` (`GetFoamMask`).
- **Foam**: crest/breaking/shore/turbulence sources, **advected** (`uv - foamAdvectionOffset`) and **decayed** (`DecayFoamFastThenSlow`); ocean whitecap via negative Jacobian in `KWS_WavesFFT.compute`. Foam texture = channel-packed atlas `Resources/Textures/FluidsFoamTex.png` (stochastic tiled, flow-jump sampled), decals `FoamDecals*.png`.
- Orchestration `Scripts/Core/CommandPass/DynamicWavesPass.cs`, zone `Zones/KWS_DynamicWavesSimulationZone.cs`.

### Crest (`KWSWater` project) — physical shoaling + foam sim
Root: `Packages/com.waveharmonic.crest` (+ `.shallow-water` add-on).
- **Shoaling** (physical, canonical): depth-based spectrum attenuation `weight = saturate(2*depth/averageWavelength)` in `Runtime/Shaders/Data/Input/Hidden/AnimatedWavesGenerate.compute:188-206` (deep if depth > half wavelength). Seabed `Scripts/Data/DepthLod.cs` (+SDF), `JumpFloodSDF.compute`. Shoreline color `Data/ShorelineColor.compute`.
- **Foam sim**: `Runtime/Shaders/Data/UpdateFoam.compute` — flow-advect + fade-decay + Jacobian whitecap + shallow-depth shoreline foam (+ `_Crest_FoamNegativeDepthPriming` for SWS leading edge). Shading `Runtime/Shaders/Surface/Foam.hlsl` (multi-scale tiled texture, black-point feather, derivative foam normal, flow dual-sample). Texture `Runtime/Textures/Foam.png` / `Foam (Packed).png`.
- **True breaking/run-up**: `com.waveharmonic.crest.shallow-water/.../ShallowWaterSimulation.compute` — full SWE (advect, height, velocity, wet/dry **leading-edge** mask), driver `ShallowWaterSimulation.cs` injects back into Crest LODs.

**Synthesis for the plan.** Both KWS2 and Crest = depth capture → jump-flood SDF → (shoaling attenuation) + (SWE for breaking/run-up) + (Jacobian whitecap & shallow-depth foam, advected+decayed). Foam generation is nearly identical across both. KWS1 is a different philosophy: a *baked look* layered on top. The auto backbone should follow KWS2/Crest; KWS1-style crashers are an optional hero layer.

---

## 4. Evaluate Bert's hypothesis explicitly

Bert's instinct: "a shoal effect + a KWS1-style shore-wave add-on would do the trick." Assess it honestly in the plan:
- **Shoaling is correct and foundational** — it is Layer B and every reference has it. Adopt Crest's depth/wavelength attenuation as the model (WebGPU-friendly, cheap, physical).
- **KWS1 crashers are a look, not an auto system** — they need baked assets we don't have and manual placement. As an *auto* shoreline they're a poor fit; as a *hero add-on* (auto-spawned along the SDF shoreline, or artist-placed) they're excellent. The plan should either (a) reproduce/port KWS1's instanced-quad-into-area-RT mechanism but drive placement from the SDF, or (b) generate breaking emergently from an SWE zone (KWS2/Crest-SWS) and reserve KWS1 flipbooks for set pieces.
- Recommend the plan compare "SWE-driven emergent breaking (KWS2/Crest-SWS)" vs "baked-flipbook crashers (KWS1)" as an explicit fork, with WebGPU cost and the auto-vs-manual tradeoff called out.

---

## 5. Hard constraints (bake these into the plan)

- **WebGPU has no async GPU→CPU readback.** This is the recurring wall. Good news: the KWS2/Crest shore backbone (ortho depth, jump-flood SDF, shoaling attenuation, SWE compute, foam sim, instanced-quad displacement RT) is **all GPU compute + textures with no readback** — so it is WebGPU-portable. The only place readback bites is **CPU buoyancy**, which is a *separate* system; any shore height a floating object must feel has to be reachable analytically or accepted as render-only. State clearly, per feature, whether it needs to reach buoyancy.
- **Also mind**: WebGPU won't filter float32 (`webgpu-float32-not-filterable`) — pick half/manual-bilinear formats for the depth/SDF/foam RTs; WebGPU build uses the Mobile URP asset; watch float readback nuances.
- **Reuse, never rewrite** — wrap KWS2/Crest/KWS1 techniques as thin adapters into our RenderGraph/URP17 path; don't hand-roll what a reference already solves.
- **Reviewable chunks + ask before code** — layered phases, each compiling and independently revertible; Bert approves the plan and each code-touching phase.
- **Fresh names** — avoid `_Shoreline*` (now bed tint) and any `FormerlySerializedAs("shoreline*")` collisions.

---

## 6. Recent papers / prior art to consult

- **Thürey, Müller-Fischer, Schirm, Gross — "Real-time Breaking Waves for Shallow Water Simulations"** (matthias-research.github.io/pages/publications/breakingWaves.pdf). The seminal height-field-breaker technique KWS2/Crest-SWS descend from: detect steep front → spawn connected particle sheets absorbed into the surface → drops + foam. Directly relevant to auto breaking.
- **"Real-Time Interactive Hybrid Ocean: Spectrum-Consistent Wave Particle-FFT Coupling"** (arXiv 2511.02852, 2025) — recent wave-particle ↔ FFT coupling; relevant if we want shore wave-particles consistent with the FFT ocean.
- **Boussinesq-type shallow-water coastal models** — shoaling/refraction/diffraction/breaking in height fields (marine-simulator literature); background for physically-motivated shoaling and refraction along `shoreDir`.
- **Jeschke & Wojtan — Water Wave Animation via Wavefront Parameter Interpolation** (ACM TOG) — wavefront/refraction handling near shore.

---

## 7. What the planning session must produce

A layered plan, each layer a small reviewable phase, with a Phase 0 read-only grounding pass that re-verifies §1 against the live tree first. Suggested layering (the session should challenge it):
- **Layer A — world-frame depth + jump-flood SDF substrate** (the §2 fix). Camera-following ortho depth capture + SDF (distance + direction to shore), WebGPU-safe formats, works on bounded *and* ocean bodies. Everything else consumes this.
- **Layer B — shoaling** off the depth field (Crest-style spectrum attenuation for swell/wind; ties into our `WaterLargeWaves`).
- **Layer C — shore waves / breaking / run-up**: decide the fork from §4 (SWE zone vs baked-flipbook crashers vs both), with WebGPU + auto-vs-manual tradeoffs.
- **Layer D — shore foam**: Jacobian whitecap + shallow-depth shoreline foam, advected + decayed, reusing our existing foam sim/particle systems where possible; a real waterline foam to replace the pool-rectangle border.
- **Layer E — polish**: shoreline color gradient, wet-sand darkening, spray/mist, hero-crash add-on.
For each layer: the reference to adapt (with file paths), the WebGPU note, whether it must reach CPU buoyancy, the new uniforms/names, the test/verification, and the risks. Surface every real decision to Bert rather than guessing.

---

## 8. Open questions to put to Bert before finalizing the plan

1. **Auto vs hero**: is the goal a fully automatic shoreline everywhere, or an automatic base plus hand-placed hero crashers (KWS1-style) for set pieces?
2. **Breaking mechanism**: emergent from an SWE zone (KWS2/Crest-SWS, physical, heavier) vs baked-flipbook crashers (KWS1, cheaper look, needs assets)? Or SWE base + optional flipbook heroes?
3. **Buoyancy**: must floating objects physically feel shore run-up / breakers, or is shore render-only (given the WebGPU readback wall)?
4. **Scope of first slice**: ship Layer A+B (depth field + shoaling) as the first visible win, then iterate — agreed?
5. **KWS1 assets**: do we have rights/means to reuse KWS1's baked flipbook + foam-buffer assets, or must we reproduce the bake?
