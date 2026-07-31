# PROMPT — "Poor man's Houdini": the offline FLIP splash baker (Bert's dream project)

You are working on WebGpuWater at
`C:\Users\bebx\Documents\UnityProjects\ThreeJSWaterPort\Packages\com.abstractocclusion.webgpuwater`.
Unity 6, URP RenderGraph, shipping target WebGPU/WGSL, editor D3D11/12. Work on cloud
copies, deliver via SendUserFile + `device_commit_files`, never run git through the bridge.

**Read FIRST (project memory):** `MEMORY.md`, `wave-bake-tool-idea-2026-07-17.md` (the
locked design sketch — cassette model, 2D slice, wavemaker, variant library),
`splash-crown-sixway-2026-07-25.md` (flipbook/6-way state + the ALPHA-BUDGET PATTERN +
"Blender FLIP hero bake = baker-tool v0" deferral), `never-write-via-shell-mount.md`
(editing method + line endings), `feedback_workflow_bert.md` (his working rules),
`underwater-lights-transparents-prompt-2026-07-31.md` (particle fog/lamp-glow state),
`webgpu-float32-not-filterable.md`. Do not start designing before these are read.

## The goal, in Bert's words
Mimic — or nail — KWS's particles ("double wow") with a dedicated offline bake tool:
sim once in the editor, play back forever at runtime as a recording. V1 of the water
package is DONE and pushed; this is the next flagship feature.

## What KWS actually does (verified in the reference project — concepts only, re-derive everything)
Reference: `C:\Users\bebx\Documents\UnityProjects\KWSWater`. Their splash/foam particles
are **NOT baked fluid sims**. Verified by reading
`KWS_DynamicWavesFoamParticlesCompute.compute` + `KWS_DynamicWavesSplashParticles.shader`
+ `KWS_DynamicWavesSimulationZone.cs`:
- GPU particles SPAWNED from the dynamic-waves sim where turbulence is high
  (InterlockedAdd into a counter buffer), advected by the SIM'S OWN FLOW FIELD
  (`dynamicWaves.xy`) blended with a **curl-noise array** + perlin jitter, gravity + air
  drag, ground-drag from the sim.
- Rendering: velocity-STRETCHED billboards (`pow5(gravityFactor)·speed` vertical stretch),
  per-particle random rotation about the sprite base, sin-lifetime alpha envelope,
  soft-particles vs depth AND water depth, full lighting incl. their own foam shadowmap,
  underwater-mask interplay, bayer/IGN dithered cutout.
- Their "BakedSplash" is NOT a particle recording: it bakes a SIM ZONE's flow/terrain
  data, then a Unity ParticleSystem prefab emits over it at runtime.
**Conclusion the design must own: KWS's wow = MOTION (sim-driven advection + curl) +
rendering polish. A baked FLIP cassette can match the motion polish AND add what they
cannot: real fluid SHAPES — connected sheets, Worthington crowns, plunging-breaker
tubes, filaments — with true parallax. That's the pitch. We already ship their rendering
tricks (6-way lightmaps, transmission, soft particles, exclusion collision); the gap is
the shape source.**

## What already exists in the package (reuse, never rewrite)
- `Runtime/WaterFoamParticles.cs` + `WaterParticlePool.cs` + `WaterSplashEmitter.cs` —
  GPU particle pool, flipbook draw paths, after-fog reroute, exclusion-aware
  slide/bounce collision.
- `Runtime/Shaders/SplashParticles.shader` — 6-way lightmaps + transmission (default-off),
  crown flipbook; `FoamParticles.shader`; `WaterParticleFog.hlsl` (self-fog);
  `WebGpuWaterFogAPI.hlsl` (lamp glow for transparents — playback quads should price the
  same medium).
- Wave data: `SurfFrontHeightFromTerms` / the surf front engine (the WAVEMAKER — drive
  the sim boundary with OUR wave so the bake matches the runtime wave by construction),
  crest segmentation + overCap break detection (the runtime TRIGGER), Iribarren-style
  spilling/plunging classification (variant selection).
- Flipbook generation scripts (`gen_*.py`, Samples) — the offline-content pipeline
  precedent, including the LOCKED PATTERN: **simulate the shader's alpha budget
  (mass·erosion·envelope·opacity per frame) BEFORE shipping any baked content** — the
  crown saga cost 3 rounds to learn that.

## Technique references (dig further as needed; cite what you use; NEVER copy code)
- **Solver:** FLIP (Zhu & Bridson 2005, "Animating Sand as a Fluid"); **APIC** (Jiang et
  al. 2015) — replaces FLIP's noisy velocity transfer, industry default now; Bridson's
  *Fluid Simulation for Computer Graphics* (2nd ed.) is the implementation bible;
  narrow-band FLIP (Ferstl et al. 2016) if bake times ever hurt. Alternatives to weigh:
  **PBF** (Macklin & Müller 2013) — simplest robust GPU implementation; **DFSPH**
  (Bender & Koschier) — best SPH incompressibility; SPlisHSPlasH (open source) is the
  reference implementation family. For a BAKED asset, brute-force small timesteps are
  affordable — favour simplicity + splash quality over speed.
- **Whitewater:** Ihmsen et al. 2012 "Unified Spray, Foam and Bubbles for Particle-Based
  Fluids" (https://cg.informatik.uni-freiburg.de/publications/2012_CGI_sprayFoamBubbles.pdf)
  — the trapped-air / wave-crest / kinetic-energy potentials that classify secondary
  particles into SPRAY / FOAM / BUBBLES. This is exactly the Houdini Whitewater lineage
  and the classification the cassette should store per particle. Newer: "Guided bubbles
  and wet foam" (SIGGRAPH 2022) if bubbles ever matter.
- **Cassette format precedent:** SideFX **Labs VAT 3.0, Particle Sprites mode**
  (https://www.sidefx.com/docs/houdini/nodes/out/labs--vertex_animation_textures-3.0.html):
  position texture (+ size/compressed data in alpha), rotation/attribute textures, rows
  keyed by particle, id-keyed inter-frame interpolation, HDR 16/32F or split-precision
  LDR. Our cassette = the same idea, package-native.
- **Product benchmark:** JangaFX **LiquiGen 1.0** (2025, https://jangafx.com/software/liquigen)
  — GPU real-time-ish liquid sim tool for game VFX. It is literally the "poor man's
  Houdini" market proof; study its export/workflow story from public material for UX
  ideas (do NOT depend on it — our value is one-click inside Unity, driven by OUR waves).
- **Flipbook motion-vector blending** (Klemen Lozar's classic write-up / RealtimeVFX) —
  the deferred "MV frame blending (4×4@256 trade)" from the crown memory; the cassette
  makes it partly moot (real motion), but sprite-level MV blending is still the polish
  for the per-particle flipbook textures.

## Phase 0 — LOCKED DESIGN DOC before any code (Bert's rule; STOP for his go)
Produce a design doc answering each, with a recommendation, quote the expected duration
of every later phase, and wait for approval:
1. **Solver.** Recommended: 2D APIC/FLIP on a MAC grid, cross-section slice (across-shore
   × up) — thin sheets and crowns need grid pressure (pure SPH blobs); 2D at fine
   resolution (~256×512 grid, 100-300k particles) bakes in minutes on the editor GPU or
   even plain C# Burst. State the PBF fallback and why it lost (or won). Surface tension
   / droplet break-up model for crown fingers (simple curvature force or particle-age
   pinch-off heuristic — decide).
2. **Compute where.** Editor-only bake: compute shaders on D3D11/12 with sync readbacks
   ALLOWED (never in a build — the WebGPU rules don't bind the bake tool). C# Burst
   fallback if editor-GPU portability bites. Decide.
3. **Cassette format.** Particle-row × frame-column textures, RGBA16Half (WebGPU
   FILTERABLE — float32 is not, see memory): position.xyz + size in A; attributes
   texture: age, rotation, type (spray/foam/sheet as ±/enum), random seed. Positions
   quantized to the event's local box. One-shot birth→death, no loop. Id-stable rows for
   free inter-frame lerp. ScriptableObject wrapper asset (cassette + metadata: H, period,
   Iribarren class, duration, particle count, alpha-budget report).
4. **Authoring path.** In-Unity solver window ("Water Splash Studio": pick wave params →
   Bake → preview scrub → save cassette) vs Blender/Mantaflow import as v0 shortcut
   (memory records "Blender FLIP hero bake = baker-tool v0"). Recommend in-Unity (it's
   the product; users bake their own), keep the importer as a stretch.
5. **Wavemaker.** Drive the inlet/boundary from `SurfFrontHeightFromTerms` with
   normalized H and period so one bake rescales to any beach; 2-3 amplitudes × spilling
   vs plunging = the variant library (Iribarren weights pick at runtime — already
   sketched in memory).
6. **Runtime playback.** Cassette instances pinned to crest segments when overCap crosses
   break (existing trigger); alongshore = instance the 2D slice along the crest with
   per-instance jitter/phase/mirror; render through the EXISTING SplashParticles path
   (6-way, transmission, soft particles, self-fog via WaterParticleFog.hlsl, exclusion
   filters). On top of the baked motion, keep KWS's life: small curl-noise jitter and
   velocity-stretch at the sprite level — recording + noise, not noise alone.
   Budget: reuse WaterParticlePool slots or a dedicated instanced draw — decide, with
   the fps-cliff rule in mind (keywords, no fat uniform branches).
7. **Scope fence.** V1 = ONE hero event class (breaking shore wave). Boat splash / rock
   splash / waterfall cassettes are follow-ups; say so explicitly so the design doesn't
   balloon.

## Later phases (each design-gated increment ships small; quote durations up front)
- **P1** — 2D slice solver + in-window preview (scrub, particle count, timing). Accept:
  a plunging breaker that visibly curls and throws a jet in the preview.
- **P2** — whitewater classification (Ihmsen potentials) + cassette encode + the
  alpha-budget simulation report. Accept: cassette asset on disk, re-playable in preview
  from the TEXTURES (not the live sim).
- **P3** — runtime playback path (one hand-placed cassette instance in a demo scene,
  full rendering stack). Accept: side-by-side vs the current stochastic crest particles
  — the recording must read obviously better; fps A/B via WaterCostProbe (F-toggle is
  session-sticky — confirm tier first).
- **P4** — wave-break integration (crest-segment trigger, alongshore instancing,
  Iribarren variant pick). Accept: waves break with cassette splashes on the shore demo,
  no visible repetition at 3+ simultaneous breaks.
- **P5** — variant library bakes + polish (MV blending if needed, LOD/distance cull,
  memory budget pass).

## Standing traps (all have bitten before)
- Line endings are MIXED PER FILE (even within one folder/prefix — Feature LF vs Pass
  CRLF). Detect per file, restore on write. New shader files LF, new Runtime .cs CRLF.
- WebGPU: float32 textures NOT filterable → cassettes are 16F (or point-sample);
  AsyncGPUReadback only (sync = editor bake tool only); no unbounded loops in shaders.
- The 16-sampler ps_4_0 cap on WaterSurface Pass 0 — playback shaders are separate TUs,
  but any NEW texture touching the surface pass needs the NOSAMPLER borrow pattern.
- A WebGPU build is 18+ minutes — prefer editor/runtime toggles over build tests; never
  spend a build on something Bert already tested.
- Never run git through the bridge; show a file/line table before generating code; ASK
  BEFORE TOUCHING CODE — this whole project is design-gated at every phase.
- Legality: techniques are free (FLIP/APIC/whitewater are published research; VAT is a
  public format), code and assets are not — nothing from KWS/Houdini/LiquiGen gets
  copied, and their names stay out of store marketing.

## Acceptance for the project as a whole
A shore wave breaks and throws a real curling sheet with crown fingers and spray that
reads like a recorded fluid sim — with parallax, lit by the 6-way sheets, fogged and
lamp-lit like everything else in the water, colliding with exclusion volumes — at a
runtime cost of texture fetches + instanced quads, zero simulation. KWS-level motion,
better-than-KWS shape. That's the double wow.
