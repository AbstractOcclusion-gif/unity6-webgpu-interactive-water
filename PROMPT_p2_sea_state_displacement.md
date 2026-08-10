# PROMPT — P2: sea-state DISPLACEMENT (fetch / wind-exposure field on the wave heights)

Unity senior dev, WebGpuWater (`Packages/com.abstractocclusion.webgpuwater`). Rules:
ask before code (show a file/line plan first), no magic numbers, comments explain WHY,
reuse-never-rewrite, md5-gate patches with FRESH measurements (files drifted across
several sessions on 2026-08-10 — re-measure EVERYTHING, pin nothing from this prompt),
line endings PER FILE, NEVER run git through the bridge, quote expected duration,
check in every ~10 min, keep replies short.

## CONTEXT — what already shipped (2026-08-10, all confirmed or in Bert's tree)
- P1: stochastic wave-group envelope (Rayleigh |4-phasor| in WaterWaves.hlsl +
  WaterWaveBank.cs mirror). P3: swell heading decoupled (swellHeadingOffsetDegrees,
  analytic + FFT + CPU mirror). P2-SHADING: SeaStateMssScale in WaterLargeWaves.hlsl —
  gust/slick noise scaling FFT tilt/pinch/foam + detail normals, knobs
  ocean.seaStateGusts/.seaStateSlicks, SHADING ONLY, no displacement.
- Dark-line saga CLOSED via F7 (air-claim corroboration in BOTH OceanRenderedCoverage
  copies, fog + meniscus) — the fog carries F7/F8 hunks; do not disturb them.
- ⚠️ ORDERING: the F3 height RT was built, verified correct, then REVERTED during the
  hunt; Bert ordered its REBUILD as the must-do BEFORE P2 (recipe + rebase notes in
  the transition memory + PROMPT_f3_height_rt.md). P2 assumes the RT exists when it
  runs — it renders through the SAME vertex path, so P2's displacement flows into it
  automatically and it doubles as P2's ground-truth verification view. If the RT is
  not in the tree when you start, STOP and confirm ordering with Bert.
- The fog compiles per-variant huge; WATER_STRIP_SHORE fork fixed iteration time.
  Touching WaterLargeWaves/WaterWaterline recompiles the fog — BATCH shader edits.

## GOAL
Real seas are not statistically uniform: wave HEIGHT varies with wind exposure. On a
lake, water near the upwind shore is glassy and waves GROW downwind (fetch-limited
growth: Hs ~ F^0.5, peak wavelength ~ F^0.66 — JONSWAP; at 5 m/s: F=100 m -> Hs~2-3 cm,
F=10 km -> Hs~25 cm). The shipped reference for games: War Thunder's baked "downwind"
wind-exposure texture attenuating wave energy in wind shadows (lee of islands, bays,
rivers) — whole ocean 0.5 ms on a GTX 770 (Tcheblokov, CGDC 2015).
P2 = a baked WIND-EXPOSURE (fetch) field that ATTENUATES wave amplitude spatially,
per wavelength band, on both the FFT and analytic paths, with exact CPU/GPU agreement.

## HARD CONSTRAINTS (each one is a shipped contract — violating any is a regression)
1. **Buoyancy mirror.** Height stays a pure function of world XZ, and the CPU samples
   the SAME field: the fetch field is a CPU-SIDE BAKE (float[] + uploaded Texture2D).
   The CPU mirror samples the array (bilinear, same indexing math, validator-paired
   constants); the GPU samples the texture. NO GPU readback, NO procedural runtime
   field on the displacement path.
2. **Attenuation ONLY (factor in [0,1]).** SurfaceHeightBand / SurfaceHeightEnvelope
   are conservative ceilings consumed by the fog arm gate, the crossing march and the
   god rays. A field <= 1 keeps every ceiling valid with ZERO re-audit. Amplification
   (gust boosts) stays in the SHADING layer that already ships. This is the single
   most important scope cut — today's waterline saga is what a violated ceiling
   costs.
3. **Shared-weight rule.** The attenuation must reach EVERY consumer of wave energy
   identically or foam/glow sits on flattened water ("patches corresponding to
   nothing"). Enumerate and hit ALL of them in one review table before coding:
   FFT: OceanFftDisplacementShore, OceanFftNormalSumShore (tilt+pinch+foam),
   OceanFft.compute BakeHeightField (buoyancy bake!), the foam-particle compute's
   OCEAN_FFT_GLUE spatial sum, foam-particle render glue (FFT branch).
   ANALYTIC: LbwAccumulateBand amplitudeScale (both bands) + LargeWaveField.cs
   AccumulateBand mirror. The cleanest cut: ONE function `SeaStateFetchWeight(worldXZ,
   wavelength)` in WaterLargeWaves.hlsl + C# twin, multiplied at the band/cascade
   level (per-cascade representative wavelength already exists:
   OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION).
4. **Per-wavelength response.** Long waves need fetch; ripples survive partial
   shelter. Attenuate long-wavelength cascades/bands HARDER near the windward shore:
   weight = saturate(F / FetchRequiredFor(wavelength)) shaped by the JONSWAP growth
   exponent — named constants, physical comment, no hand magic.
5. **Sampler budget.** The field is sampled inside WaterSurface's include chain and
   WaterSurface Pass 0 sits at EXACTLY 16 d3d11 samplers. Declare the fetch texture
   UNITY_DECLARE_TEX2D_NOSAMPLER and borrow sampler_CameraOpaqueTexture (the
   documented pattern), or sample with an existing shore sampler. Verify the count.
6. **Default = bit-identical.** Toggle off (default) publishes weight 1 / a white
   texture: every existing scene renders byte-identically. The wizard authors nothing.

## THE BAKE (V1)
- Substrate: the existing shore machinery — WaterShoreDepthField (seabed depth bake)
  + shoreline SDF give land/water; a point is SHELTERED where marching UPWIND from it
  hits land. Bake fetch = upwind distance to land (clamped to FetchFullyDeveloped),
  256x256 over the body footprint (bounded bodies) — CPU bake at startup/on-change,
  same trigger family as the shore bake. UNBOUNDED oceans with no shore field: field
  inert (weight 1) — oceans get their variation from the shading layer + later phases.
- Re-bake when windFromDegrees changes (the bake is direction-specific — the War
  Thunder multi-direction atlas is V2 if live wind rotation ever matters; Bert's wind
  is authored per scene).
- Inspector: Ocean Sea State -> "Wind Fetch" subsection: enable toggle (default off),
  [0,1] strength (lerp toward the physical weight), readout of bake state. Property
  paths + foldout Sync + editor pattern: mirror the Sea State Variation subsection.
- Field frame published like the shore field's (center/size uniforms, clamp sampling,
  out-of-field = weight 1). Validator pairs for resolution/constants shared CPU/GPU.

## V1 SCOPE — OUT (do not build, note only)
Dynamic gust/lull displacement (stays shading-only); currents (P5); Lutz
tiling-and-blending (P4); multi-direction fetch atlas; per-cascade lambda_p SHIFT
(approximated by the per-wavelength attenuation curve); HDRP-style painted masks
(the bake IS the mask source in V1).

## TEST PROTOCOL (quote to Bert before starting)
1. Bounded lake with terrain + bed depth, wind across it: glassy band at the upwind
   shore, waves growing downwind, ripples surviving where long waves die. Rotate wind
   180° -> the calm band swaps shores after re-bake.
2. Buoyancy parity: floater in the attenuated zone sits ON the flattened surface
   (no bobbing on invisible waves) — the strongest mirror test.
3. Foam coherence: no whitecaps/crest glow on flattened water near the calm shore.
4. Waterline/fog regression sweep at the calm-rough boundary (the ceiling contract):
   FogUnpainted shows no new red; the F3 height RT shows the attenuation.
5. Toggle off -> byte-identical (screenshot diff a frozen frame).
6. Pool + unbounded ocean: untouched by construction (assert inert path).

## RESEARCH ANCHORS (embedded — the research doc lives in chat, not the repo)
JONSWAP fetch growth: E ~ F^1.0 -> Hs ~ F^0.5; f_p ~ F^-0.33 -> lambda_p ~ F^0.66.
Fully developed limit (Pierson-Moskowitz) caps growth. War Thunder downwind texture:
CGDC 2015 slides (Tcheblokov) — per-pixel exposure multiplying wave energy.
Cox & Munk: what reads at distance is sub-metre mss — why the shading layer and this
displacement field must stay COHERENT (same wind story) even though they are separate
mechanisms.

## PROCESS
Read the Ocean Sea State settings/editor/publisher/validator patterns before writing;
show the full consumer table (constraint 3) and the file/line plan; get Bert's GO;
implement in md5-gated batches (shader edits batched to minimize fog recompiles);
deliver a test checklist. Bert runs git himself and does a human pass on everything.
