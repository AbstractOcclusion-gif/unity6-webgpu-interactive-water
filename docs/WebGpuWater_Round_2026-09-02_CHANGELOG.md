
## TRACK B — surface/compute (audit 02, findings 1-4, 6-8, 12-13; S1-S5, S8-S10, S12)

Files: Runtime/Shaders/WaterSurfaceFragStages.hlsl, Runtime/Shaders/WaterSurfaceVertStage.hlsl,
Runtime/Shaders/WaterLargeWaves.hlsl, Runtime/Shaders/WaterSurface.shader, Runtime/WaterOceanFft.cs,
Editor/WaterWaveConstantsValidator.cs. Nothing else touched (no new files, no .meta changes).

### B1 = S1 + S2: wind-wave field 4x -> 1x per fragment/vertex; detail-normal outflow twin gated
- FragStages `EvaluateSurfaceGeometry` (~:132-176) and VertStage `DisplaceSurfaceVertex` (~:367-412):
  the grid `WaveSlope`/`WaveHeight` is evaluated first and unconditionally; the mouth-outflow drift
  twin is now inside `[branch] if (_MouthOutflowCount > 0.5)` (per-body uniform, published by
  WaterUniformPublisher/WaterRiverSurface); the two river phase evaluations A/B are inside
  `[branch] if (_IsRiver > 0.5)`. Endpoint costs: pool/lake/ocean with no outflow = 1 bank
  evaluation (was 4); ocean/lake receiving a river mouth = 2 (was 4); river = 3 (grid + A/B; was 4)
  or 4 with its own mouth published (a river publishes its own mouth in slot 0, so the count is
  >= 1 there; the plume test inside is per-pixel and stays inside the branch).
- Detail normals (~:290-320): both the river-path `bodyOutflowTilt` and the pool-path
  `detailOutflowTilt` twins are inside `[branch] if (_MouthOutflowCount > 0.5)`. 4 fewer
  `tex2Dgrad` per pixel (12 with hex tiling) on every body without a connected mouth.
- `RiverCurrentWaveSampleXZ` is pure ALU (WaterWaves.hlsl:74-90, no texture reads) - safe to call
  only on rivers. `SampleRiverFluidVelocity` (one uniform-gated `tex2Dlod`) is left where it was:
  its result is also consumed by the river detail-normal path.
- Bit-identity, case by case (old expression -> new expression):
    // A. non-river, count 0:
    //   old: gridWindSlope = lerp(WaveSlope(g), WaveSlope(o), MouthOutflowCurrentInfluence(xz))
    //        with count 0 => EvaluateMouthOutflow loop runs 0 iterations => influence = 0,
    //        velocity = 0 => o = RiverEndWindWaveSampleXZ(WorldToPool(xz), xz, sel) and
    //        lerp(a, b, 0) = a + 0 * (b - a) = a          (b finite)
    //        windSlope = gridWindSlope * w
    //   new: gridWindSlope = WaveSlope(g); branch skipped; windSlope = gridWindSlope * w
    //   => identical (the only conceivable difference is the sign of an exact zero, which no
    //      downstream consumer can observe).
    // B. non-river, count > 0:
    //   old: lerp(WaveSlope(g), WaveSlope(o), infl) * w
    //   new: gridWindSlope = WaveSlope(g); gridWindSlope = lerp(gridWindSlope, WaveSlope(o), infl);
    //        * w                                          => same expression, same order.
    // C. river (any count):
    //   old: windSlope = lerp(gridWindSlope, lerp(WaveSlope(A), WaveSlope(B), blend), riverWeight)
    //   new: same, the inner lerp merely moved inside the _IsRiver branch.
    // Vertex: identical structure with WaveHeight and `position.y += ...`; the river branch keeps
    // `lerp(gridWaveHeight * w, riverWaveHeight, riverWeight)`, the else keeps `gridWaveHeight * w`.
    // Detail: old lerp(base, outflowTilt, influence) with influence 0 and outflowXZ == xz -> base.
- Compile risk: `[branch]` on a uniform ahead of `ddx/ddy` code (`DetailNormalTiltScrolled`) - the
  same shape the file already uses for `if (_IsRiver > 0.5)` at the same spot. Two sibling
  `[branch]` blocks in the vertex program: pure ALU.
- Test: pool + lake + FFT ocean without a river: frame-diff against the baseline (expect identical);
  ocean/lake with a connected river mouth: wave carry + micro-detail drift past the mouth unchanged;
  river ribbon: terminal band still sews to the receiving body, no washboard.

### B2 = S3: ONE FFT cascade sum per pixel
- WaterLargeWaves.hlsl: `OceanFftCascadeSumInert()`, `OceanFftFoamFromSum(sum, xz)`,
  `OceanFftJacobianFromSum(sum)`; `ApplyLargeBodyWaveNormalFoamShore` split into
  `ApplyOceanFftNormalFoamSum(..., OceanFftCascadeSum fft)` + `ApplyLargeBodyWaveNormalFoamAnalytic`
  with the old name kept as the uniform path pick (FoamParticles.shader's `ApplyLargeBodyWaveNormalShore`
  caller is untouched and byte-equivalent). `OceanFftFoam` / `OceanFftJacobianShore` remain as the
  standalone (self-summing) twins and now call the FromSum helpers - one copy of the math.
- FragStages: `WaterGeomStage.fft` (new field) holds `OceanFftNormalSumShore(largeWaveSourceXZ, shoreFrag)`,
  taken once under `if (_OceanFftActive > 0.5)` BEFORE the `_LargeBody` block (so the crest glow,
  which gates on `_OceanFftActive` alone, sees exactly the sum it used to compute itself). Consumers:
  (a) normal tilt and (b) geometry-foam pinch via `ApplyOceanFftNormalFoamSum`, (d) crest glow via
  `OceanFftJacobianFromSum(g.fft)`. All three used `(i.largeWaveSourceXZ, shoreFrag)` with the same
  LOD (`log2(1 + camDist/domain)`) - merged.
- (c) whitecap coverage (`OceanFftFoam`) is NOT identical in general: it samples the shore whenever a
  field is BAKED, while `shoreFrag` is inert unless `_SurfActive` is also on. Merged only under the
  uniform condition where the two provably coincide - `_SurfActive > 0.5` (both = `ShoreSample(xz)`)
  or `_ShoreDepthValid < 0.5` (both inert); the remaining case (baked shore, surf off) keeps calling
  `OceanFftFoam` (its own shore tap + sum) exactly as before.
- Saves 8-12 array taps per FFT-ocean pixel (with aperiodic tiling: 24-36 taps + 96-144 Loads), plus
  the SeaStateFetch/MssScale work inside each redundant sum.
- Bit-identity: the sum is the same function with the same arguments; consumers read the struct
  fields the wrappers used to return. The `foamGate > 0.0` per-pixel branch that used to wrap the
  pinch sum now wraps only ALU (the sum is explicit-LOD, so hoisting it out is derivative-neutral).
- Compile risk: `WaterGeomStage` gained a struct field of a type declared in WaterLargeWaves.hlsl -
  FragStages is only ever included after it (WaterSurface.shader passes 0 and 2, both include
  WaterLargeWaves first). Two new uniform `if`s on `_OceanFftActive`.
- Test: FFT ocean frame-diff (surf on and off; SSS on/off; foam strength 0 and > 0) against baseline.

### B3 = S4 + S12(dead forks): aperiodic ternary hoisted; WATER_DISABLE_OCEAN_APERIODIC removed
- `OceanFftDisplacementShore` and `OceanFftNormalSumShore` now read
  `[branch] if (_OceanAperiodicParams.x > 0.5) { loop A } else { loop B }` with the per-cascade
  addressing/weights in ONE helper (`OceanFftCascadeTerms` / `OceanFftCascadeTermsAt`) and the
  accumulation in one helper per sum (`OceanFftDisplacementCascadeWeight`, `OceanFftAccumulateNormalTap`),
  so the two arms cannot drift. The weight FACTORS are kept separate and composed in each sum's
  historical product order (`active * fade * shoal * fetch` for displacement,
  `(active * shoal * fetch)` then `* max(fade, floor)` / `* fade` for the normal sum) - no float
  reassociation, byte-identical per arm.
- Grep of the whole tree (incl. .shader/.compute/.cs): nothing defines `WATER_DISABLE_OCEAN_APERIODIC`;
  the 5 `#if` forks in WaterLargeWaves.hlsl (include, uniform block, direction-map block, two loop
  taps) compiled only their `#else`. Deleted. (WaterUnderwaterFog.shader:37 still MENTIONS the
  symbol in a historical comment - not my file; noted in CROSS_REQUESTS.md.)
- Compile risk: uniform `[branch]` around loops of explicit-LOD fetches (vertex + fragment + the
  waterline/height-RT includes). Two `for (int c ...)` loops in sibling scopes.
- Test: `oceanAperiodicEnabled` off = baseline frame; on = baseline frame (each arm is the old
  ternary lane verbatim). If fps now moves between the two, the ternary WAS evaluating both lanes.

### B4 = S8: FFT height-field bake demand-gated
- WaterOceanFft.cs: the bake block moved into `BakeHeightField(...)` (stamps `_bakedCenter/_bakedSize/
  _bakedTime` inside it, so a skipped bake never re-stamps a stale field), called from `Dispatch` only
  when `ReadbackDemandActive()` (the SAME `Time.frameCount - _lastReadbackDemandFrame <=
  ReadbackDemandWindowFrames` window `RequestHeightReadback` uses). The aperiodic uniforms move with
  it - grep of OceanFft.compute shows they are read by the bake path only (`OceanBakeDisplacement`).
  `_normal.GenerateMips` is left unconditional: the surface's `SampleLevel` distance LOD consumes it.
- `_bakedAtLastDispatch` (new flag, false at construction, cleared at every Dispatch, set by the bake)
  gates `RequestHeightReadback`, so a readback can never land (1) a never-baked target, nor (2) a bake
  left over from a lapsed demand period at demand onset: the first request after onset waits for the
  next Dispatch's bake (<= one FFT interval, <= WaterQuality.MaxOceanFftInterval = 4 frames). A flag
  rather than a frame stamp so a PAUSED body (no Dispatch) keeps re-landing its last bake exactly as
  before. `NeverStampedFrame` names the pre-existing `-1000` sentinel.
- Behaviour: identical whenever a consumer is querying (the fog gate stamps demand every frame on an
  ocean, so in practice the bake runs as before there). Decorative oceans skip a 128^2 dispatch per
  FFT dispatch. Only visible difference: the very first landing after >12 idle frames arrives up to
  one FFT interval later (buoyancy falls through to the previous landed field / analytic mirror
  meanwhile, as it already did between landings).
- Test: FFT ocean + a floating rigidbody (buoyancy) + the camera dipping under the surface (fog gate):
  both must behave as before; profile a scene with an FFT ocean and NO buoyancy/fog consumers: the
  `BakeHeightField` dispatch must be absent.

### B5 = S9: foam particle alive-count draws - SKIPPED
- The tree has no `DrawProceduralIndirect`, `CopyCounterValue`, `IndirectArguments` or
  `AppendStructuredBuffer` precedent (grep Runtime + Editor), and the compute keeps no alive count -
  `COUNTER_RING_CURSOR` is an ever-increasing spawn cursor over a ring whose live and dead slots are
  interleaved, so no scalar can size a contiguous draw; `min(capacity, cursor)` would only help before
  the ring first wraps and, being an async readback, would pop freshly spawned particles in late.
  What it needs: an Update-kernel compaction into an alive-index buffer (append or InterlockedAdd
  counter) + a 4-uint indirect-args buffer written by a tiny kernel (`vertexCount = alive * 6`,
  `instanceCount = 1`) + `cmd.DrawProceduralIndirect` per pass; all forbidden without precedent.

### B6 = S12: validator pairs
- Added to Editor/WaterWaveConstantsValidator.cs in the existing `(Hlsl, CSharp)[]` table style:
  `OCEAN_FFT_TG` <-> `WaterOceanFft.ThreadGroupSize`; `WATER_MAX_MOUTH_OUTFLOWS` <->
  `WaterVolume.Currents.MaximumShaderOutflows` (dotted filename -> read outside the main gate, like
  WaterVolume.Underwater); `RIVER_DISTURBANCE_MAX_SOURCES` <-> `WaterRiverDisturbance.MaximumSourceCount`;
  `RIVER_CASCADE_TRANSPORT_SAMPLE_COUNT` <-> `WaterRiverFoam.CascadeTransportResolution`;
  `PEAKED_REFINE_MAX_STEPS` <-> `WaterQuality.MaxRefineSteps`. Three new HLSL sources are read
  (WaterSurfaceVertStage/FragStages/FoamSampling) and three C# (WaterRiverDisturbance, WaterRiverFoam,
  WaterQuality).
- `OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION` has no C# literal twin - it is DERIVED (0.25 /
  `CascadeTileOversample`). Added `CollectCascadeWavelengthFractionProblems`: parses both sides and
  checks `fraction * oversample == CascadeBandTopFraction (0.25)`, the one fixed side of the relation
  (named in the validator with its WHY; the CPU never evaluates that wavelength).
- `KIND_SPRAY/KIND_BUBBLE/KIND_RIPPLE_CREST` have NO C# twin: `DrawKindSpray = 2` / `DrawKindBubble = 3`
  are the `_DrawKind` PASS selector (1 foam, 2 spray, 3 bubble, 4 crest, 5 roller), a different
  enumeration from the particle kind (1 spray, 2 bubble, 3 ripple crest). Not a mirrored pair -
  skipped. (The compute<->shader KIND_ triple is an intra-HLSL pair the validator does not model.)
- All new pairs were dry-run with the validator's exact regexes on the current tree: every side parses
  and matches (8/8, 4/4, 8/8, 256/256, 8/8; 0.0625 * 4 = 0.25).
- Test: open the dev project - no `[WaterWaveConstants]` warning; change one side of any new pair and
  confirm the warning names it.

### B7 = S10: pond foam coverage once per overlay fragment
- FragStages: the pond-foam look body is now `PondFoamLayerFromCoverage(i, g, coverage)`;
  `PondFoamLayer(i, g)` keeps its uniform gate and calls it with `PondFoamCoverage(i)` (Pass 0 pays the
  coverage lazily, as before). WaterSurface.shader Pass 2 evaluates `PondFoamCoverage(i)` once, clips on
  it, then hands the value to `PondFoamLayerFromCoverage`. Bit-identical: the overlay pass has already
  discarded on `_FoamEnabled < 0.5`, so the `_FoamEnabled || _MouthOutflowCount` gate it skipped was
  always true there; same function, same arguments otherwise. Saves the contact-depth tap + 4 foam-mask
  taps + the mouth loop per overlay fragment.
- Test: fog-armed scene with a pond (overlay pass live): foam looks identical above water.

### B8 = S5: horizon haze centre-column sample gated
- FragStages ~:2080-2100: `useCentre = max(toCentre, 1 - perAzimuthSky)` is computed first; the
  centre-column `SampleHorizonSky` (5 opaque + 5 depth taps) runs under `[branch] if (useCentre > 0.0)`,
  with `centreBand = perAzimuth` / `centreSky = perAzimuthSky` as the substitutes. On an open horizon
  both smoothstep products saturate to exactly 1 and the blur weights sum to exactly 1.0f
  (0.34+0.24+0.24+0.09+0.09 verified in fp32), so `useCentre` is exactly 0 and both lerps were already
  the identity there: `lerp(a, b, 0) = a + 0*(b-a) = a`. Any fp deviation just takes the branch.
  Per-pixel branch, but every fetch in `SampleHorizonSky` is explicit LOD (`SampleLevel` /
  `UNITY_SAMPLE_TEX2D_LOD`) - WGSL-legal. 21 -> 11 fetches on typical ocean pixels. No LUT.
- Test: ocean with horizon haze density > 0 + real refraction; pitch the camera so the horizon leaves
  the frame and back; sail a mast across the horizon row: no pops, frame-diff identical.

### Self-check
- `git diff --stat` over my files: 6 files. Brace/paren/bracket balance on comment- and string-
  stripped code verified for every touched file. Every introduced identifier declared once; the
  removed `WATER_DISABLE_OCEAN_APERIODIC` has zero code references left (one historical comment in
  WaterUnderwaterFog.shader, cross-requested).

## TRACK A — fog/render (2026-09-02)

Evidence: reports/01_fog_render_perf.md. Files touched: Runtime/Rendering/WaterUnderwaterFogPass.cs, Runtime/Rendering/LargeBodyAtmospherePass.cs, Runtime/Rendering/LargeBodyAtmosphereFeature.cs (comment), Runtime/WaterVolume.Underwater.cs, Runtime/Shaders/WaterUnderwaterFog.shader, Runtime/Shaders/WaterUnderwaterWaterline.shader (comment), Runtime/Shaders/LargeBodyGodRays.shader, Runtime/Shaders/LargeBodyGodRaysRaymarch.hlsl (+ .meta, NEW), Runtime/Shaders/WaterExclusionWall.shader. Not touched: WaterQuality.cs, WaterCostProbe.cs, WaterFogDebug.hlsl, WaterDebugMode.hlsl, WaterOceanRenderedCoverage.hlsl (no tier knob was needed; A6 skipped).

### A1 (B1) — `_VisibleWaterSurfaceDepth` recorded only for readers, at prepass scale
- Gate is now `riverFogRecorded || (UnderwaterFogActive && fogSource != null && !fogSource.IsOceanClipmap)` (WaterUnderwaterFogPass.RecordRenderGraph). Re-verified the readers: exactly two, `RiverFogSegment` (behind `_RiverFogDepthValid`) and `ArmWeight`'s bounded branch (after the `_UnderwaterUnbounded > 0.5` return). No third reader exists in the tree (grep `_VisibleWaterSurfaceDepth|VisibleWaterSurfaceEyeDepth`).
- When recorded, colour + depth go through the existing `ApplyPrepassScale` (0.5) and the applied scale is published as `_VisibleWaterSurfaceDepthScale`; `VisibleWaterSurfaceEyeDepth` in the shader derives its LOAD pixel exactly like `OceanSurfacePrepassPixel` (`_ScaledScreenParams.xy * scale`). Validity stays 0 when skipped.
- Stale comments fixed: Pass.cs "UnderwaterSegment tests _UnderwaterFogSimple BEFORE …" (now describes the keyword fence); fog shader header "U3 … a uniform"; the "Full-resolution R32 depth keeps the comparison exact at seams" note.
- Default: an unbounded ocean without external river fog is bit-identical (the texture was never read). River / bounded-lake frames CHANGE: the ownership comparison now runs on a half-res texel, so a one-texel silhouette row at a sheet edge (river exit vs. connected sheet, lake top vs. river sheet) can land on the other owner (the +-2 screen px trade PrepassResolutionScale already documents for the ownership RT). If that shows at a river mouth, revert the two `ApplyPrepassScale` calls in `RecordVisibleWaterSurfaceDepth` (the published scale then reads 1 and the shader is unchanged).
- Compile risk: none new (same APIs as the ownership prepass).
- Test: ocean, Full tier, camera in the arming band, Frame Debugger → `WaterUnderwaterFog.VisibleWaterSurfaceDepth` must be ABSENT; add the Connected Waters demo river → present at half res, river/lake fog handoff unchanged at the mouth.

### A2 (B3) — god-ray composite loads `_WaterFogClassifyRT`
- LargeBodyGodRays.shader composite: `#pragma multi_compile_fragment _ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT` (the fog solve's exact set), declares `_WaterFogClassifyRT` + `_WaterFogClassifyScale` and a `LoadWaterFogClassification` identical to the fog's. Under the keyword the mask pair is one LOAD instead of `SurfaceSignedGapChopInverted` + `SurfaceSignedGap` (4 field evaluations). With the eye in a dry carve the RT holds the fog's PUSHED (carve-exit) classification, not the lens's, so that case keeps the analytic pair under a uniform `_CameraDryVolume` branch — `PaneAwareCompositeMask` sees the coverage it always did.
- Analytic fallback (keyword off) now uses `SurfaceSignedGapChopInvertedPair` (3 evaluations, byte-identical pair per WaterWaterline.hlsl's own contract) and the fog/meniscus `WaterlineFarFromSurface` early-skip (uniform: reads the height RT at the camera xz; returns false with exclusions or no RT, so it is never a wrong answer).
- The drifted local `OceanRenderedCoverage` copy was deleted; the composite now includes the shared WaterOceanRenderedCoverage.hlsl (same signature; the shared one carries the F7/F9 flank-corroboration clamps). BEHAVIOUR FIX: an isolated half-res ownership texel can no longer pull the shaft mask off the analytic coverage.
- C#: WaterUnderwaterFogPass publishes a per-camera, per-frame handoff (`TryGetClassifyRt(Camera, out TextureHandle)`, consumed once) of the full-res classify RT; LargeBodyAtmospherePass declares `UseTexture` on it, `AllowGlobalStateModification(true)`, and enables/disables the keyword on the cmd around the draw — the fog solve's mechanism verbatim. Falls back to analytic on any frame the fog chain did not record it at full res.
- Default: on Full-tier ocean frames the composite mask now comes from the fog's classification (lens RT + far-skip included), i.e. the fog's own waterline; pixel differences vs. the old independent analytic solve are within the lens-RT/analytic agreement the fog already ships. Variants: composite 3 x 2 = 6 (was 4).
- Compile risk: the composite now includes WaterOceanRenderedCoverage.hlsl after its local `OceanOwnershipSample` and `_OceanSurfacePrepassScale` declarations (the include's stated contract).
- Test: submerged Full tier with god rays ON — probe key G on/off, shafts identical to before along the waterline; Frame Debugger composite draw shows `WATER_FOG_CLASSIFY_RT`; inside an exclusion room the pane view is unchanged.

### A3 (B4) — `skip_optimizations webgpu` scoped to the Simple raymarch
- The raymarch body (854 lines, from the first `#include` to the end of `FragRaymarch`) moved VERBATIM (verified line-for-line, 12-space dedent only) into NEW `Runtime/Shaders/LargeBodyGodRaysRaymarch.hlsl` (+ two-line .meta, fresh uuid4 guid — no sibling .meta exists in this copy to clone, so it follows the brief's two-line shape).
- Pass 0 "LargeBodyGodRaysRaymarch": no `WATER_FOG_SIMPLE` multi_compile, no `skip_optimizations` → 3 x 2 x 2 = 12 Full variants, optimised on web. NEW pass 4 "LargeBodyGodRaysRaymarchSimple": `#define WATER_FOG_SIMPLE 1` before the include (the same macro the keyword defined), `_MAIN_LIGHT_SHADOWS*` x `WATER_STRIP_SHORE` = 6 variants, carries the guard pragma and its full justification comment. No `WATER_GODRAY_POINT_LIGHTS` on it (the lamp fork is `&& !defined(WATER_FOG_SIMPLE)`). Raymarch programs: 18 (was 24).
- Appended AFTER the composite so indices 0-3 stay load-bearing (grepped every `RaymarchShaderPass`/`CompositeShaderPass`/pass-name use: only LargeBodyAtmospherePass.cs). The pass picks `RaymarchSimpleShaderPass` by `WaterVolume.UnderwaterFogSimplePublished`, a new CPU mirror of the keyword written at the three `PublishUnderwater` sites in WaterVolume.Underwater.cs from the SAME `fogSimple` value (includes the river-override case, which the keyword also covers) and cleared in `ClearUnderwaterCameraState` before its early return.
- Default: bit-identical per fork (same macro, same code, same keywords); the only change is that Full web builds get optimised codegen for the march.
- Compile risk: (1) the Simple pass is the one that hit the translator bug — it still carries the guard; (2) if the Full march ever trips the same translator bug once optimised, the fix is to add the pragma back to pass 0 only. (3) Unity's `#define` of a keyword-named macro in pass 4 is the ordinary way to pin a fork; nothing in the tree tests `#if WATER_FOG_SIMPLE` numerically (grepped).
- Test: WebGPU build must compile; in play, probe key F cycles Full/Simple/Off — Simple shafts must look as before (flat waterline), Full as before; Frame Debugger raymarch draw shows pass "LargeBodyGodRaysRaymarchSimple" on the Simple tier.

### A4 (B5) — classify RT at the solve scale (while the solve is its only reader)
- `RecordClassifyPass` takes a scale, applies it with the existing `ApplyScale`, and publishes the APPLIED fraction as `_WaterFogClassifyScale` (the `_OceanSurfacePrepassScale` doctrine). The scale is `WaterlineActive ? 1 : FogSolveScale`: the meniscus is full res and needs exact gaps in its band, and a scaled RT would have sent it back to a 3-evaluation analytic solve at FULL res — more than the scaled classify saves in the straddle band (the audit's `max(FogSolveScale, waterline ? 1 : scale)` direction). Deep frames with a scaled solve are where the full-res classify was pure waste.
- Readers: fog solve `LoadWaterFogClassification` derives its pixel from `_ScaledScreenParams.xy * _WaterFogClassifyScale` (one texel per solve pixel on scaled frames, so the gapSmooth derivative stays a real slope); waterline pass is handed the RT only when the applied scale is 1 (`classifyRtFullRes`, gate on the value actually applied) — its loader is unchanged, comment added; god-ray composite receives the handoff only when full res (same gate).
- Default (FogSolveScale 1): bit-identical — scale 1 leaves the desc untouched and the multiplier is 1.
- Compile risk: none (uniform float + arithmetic).
- Test: probe key R (1 → 0.5 → 0.25) deep underwater: Frame Debugger `WaterUnderwaterFog.Classify` target shrinks with the solve target; near the surface (meniscus armed) it stays full res; no change at R = 1.
- `WaterlineFarFromSurface` returning false whenever exclusions exist is left as is (not my file); no concrete safe proposal — see CROSS_REQUESTS.md.

### A5 (B6/B7) — warp copy gate; solve depth hoist
- (i) WaterVolume.Underwater.cs: `WaterlineWarpCopyWanted = WaterlineActive && (!surfaceYMeasured || |eye.y − surfaceY| < nearPlaneVerticalReach + WaterlineWarpCopyPadMeters)`. `nearPlaneVerticalReach` is the largest |corner.y − eye.y| of the four near-plane corners already computed in `ComputeCameraSubmerged` (FOV/aspect/roll/near included), the pad is 1.5 m (readback lag + chop-vs-vertical classification error, WHY-commented). `SurfaceHeightAtWorldXZ` now reports whether its height was a measurement (`measured = !OceanFftActive` on the analytic fallback), so a missing FFT readback keeps recording. `PublishWaterline` publishes warp 0 on the same frames, so the shader takes its `_WaterlineWarp > 0` = false branch (black line only, `_WaterlineSceneTex` bound black by the pass as before). `RecordWaterlinePass` gates the copy on `MeniscusWarp > 0 && WaterlineWarpCopyWanted`. The meniscus pass itself still runs (masked). Stale "few straddle frames" comment replaced.
- Default: on frames the crossing can be on screen — bit-identical (same warp, same copy). On frames the eye is more than (reach + 1.5 m) from its surface reading the copy is skipped and the warp is 0; the line is then off screen by construction, so nothing visible changes. Failure mode if the pad were ever exceeded: one frame without warp, never without the line.
- (ii) B7: `SceneWorldPos` returns the raw depth through an out-param; `UnderwaterFog` hands `sceneWorld` + raw depth out on every return path; `FragSolve` reuses them for the in-scatter view direction and the scaled-upsample key — one depth sample and one matrix multiply per solve pixel instead of three/two. Bit-identical (same inputs, same math). The `SolveUpsampleTaps` duplication across the absorb and inscatter DRAWS was left: they are separate fragment programs and the only way to share is a third target (the audit's own "accept ~10 loads" option).
- Compile risk: `UnderwaterFog` gained two out-params; it has one caller (grepped).
- Test: inspector `Meniscus Warp` 0.35, cross the surface slowly: warp visible as before; move 3 m below: Frame Debugger shows no `WaterUnderwaterFog.WaterlineCopy` while the meniscus pass still records; `Meniscus Warp 0` still removes the copy everywhere.

### A6 (B9) — SKIPPED
No shader-stripping mechanism exists in the tree (grep `IPreprocessShaders|OnProcessShader|ShaderCompilerData`: nothing; the only `shader_feature` uses are asset materials, and the fog material is an engine material whose own comment documents why it must be `multi_compile`). Fencing the debug path behind a keyword would therefore double the solve programs (12 → 24) in every player build with no way to drop the debug half — worse than the ~30 selects it removes. Left as is; the debug branch-id stamps and `_WaterDebugMode` early-out condition are unchanged.

### A7 — exclusion wall's `_UnderwaterFogSimple` uniform → `WATER_FOG_SIMPLE` keyword
- The two uniform branches (`FogCoverageAtPixel`, `WallSurfaceHeight`) fenced `SurfaceSignedGapChopInvertedPair` (3 field evaluations) and `SurfaceHeightAtXZ` (3 call sites) in a shader that had NO variants. Converted to `#pragma multi_compile_fragment _ WATER_FOG_SIMPLE` + `#ifdef`, the fog's mechanism; the keyword is GLOBAL (`Shader.EnableKeyword` in WaterUniformPublisher.PublishUnderwater beside the float), so every material of this shader receives it without per-material plumbing. The now-unused `float _UnderwaterFogSimple` declaration was deleted from the wall. Wall variants: 1 → 2; the Simple one no longer compiles the analytic field.
- Default: output-identical (same fact, same forks). Compile risk: the vertex stage does not read the field (checked), so `_fragment` scoping is correct.
- Test: Exclusion Demo, tier Simple vs Full: wall waterline unchanged in both.

### Frame map after Track A (Full tier, ocean, submerged, no river/exclusions)
#2 VisibleWaterSurfaceDepth: gone. #6 Classify: full res while the meniscus is armed, solve scale otherwise. #9 WaterlineCopy: only within (near-plane reach + 1.5 m) of the eye's surface reading. #11 Raymarch: pass 0 (Full, optimised) or pass 4 (Simple). #13 Composite: 1 LOAD instead of 4 field evaluations per pixel. Pass count on a submerged frame inside the arming band: 10 render passes (was 11: #2 gone) + 2 copies, dropping to 1 copy as soon as the eye is more than (near-plane reach + 1.5 m) from its surface reading while the band is still armed. The WaterUnderwaterFogPass.cs header now carries the chain.

### Test checklist (owner, Unity)
1. Console clean on domain reload (new statics, new include + .meta imported as a ShaderInclude).
2. WaterCostProbe **G** (god rays on/off), **R** (solve 1/0.5/0.25), **F** (Full/Simple/Off) in the ocean scene, submerged and in the band above: no visual change vs. baseline except the shaft mask now matching the fog waterline exactly.
3. Inspector `Meniscus Warp 0` and 0.35: copy pass present only near the crossing; warp look unchanged at the crossing.
4. Frame Debugger: no `VisibleWaterSurfaceDepth` pass on ocean frames; `Classify` scales with R when deep; composite lists `WATER_FOG_CLASSIFY_RT`.
5. WebGPU player build compiles (the Full raymarch is now optimised on web — the one real toolchain risk).

## TRACK E — water system wizard

Files (all Editor/): NEW `WaterBuildKit.System.cs` (+.meta), NEW `WaterWizardWindow.WaterSystem.cs` (+.meta); edited `WaterBuildKit.cs`, `WaterBuildKit.ConnectedWatersDemo.cs`, `WaterBuildKit.River.cs`, `WaterBuildKit.ExclusionVolume.cs`, `WaterBuildKit.MultiBodyStressTest.cs`, `WaterBuildKit.{Body,Boat,Props,SceneRig,Wiring}.cs` (log prefix only), `WaterWizardWindow.cs`, `WaterWizardWindow.OceanDefaults.cs`, `WaterWizardWindow.HullSpray.cs`, `WaterNetworkManagerWindow.cs`, `WaterSceneBuilder.cs`. No runtime file touched; only baseline facade API is called (`WaterRiver.sourceEnd/mouthEnd`, `WaterRiverEndConnection.body/upstreamRiver/transitionRadiusMeters`, `WaterRiverEditor.GenerateEnd`, `WaterRiverKnot(pos,tangent,width,speed)`, `WaterConnection.{Default,Min}TransitionRadiusMeters`, `WaterRiverSpline.{MinimumKnotCount,MinimumWidth,MinimumSpeed}`, `WaterConnectionPort.body`) — re-verified against the tree as track D left it.

### E1 — recipes as reusable steps
- `WaterBuildKit.System.cs`: `CreateBodyStep` (over `CreateWaterBody`; kind extras: analytic pool → `withPool`, SurfaceWithFog → `EnsureUnderwaterFog`, OpenWaterOcean → `ConfigureUnboundedOcean(body, bedTerrain)`, optional bed terrain / look preset), `CreateRiverStep` (over `CreateConnectedRiver`, materials taken from the parent body), `CreateExclusionStep` (over the menu creator's extracted `CreateExclusionVolume(parent,name,shape,center,size)`), `ApplyQualityStep` + `ResolveSystemQuality` / `LoadOrCopySystemQuality` (CopyAsset of the package default into the system folder — the `LoadOrCreateFoamProfile` idiom), `EnsureUnderwaterFog(body | bodies)`, `BuildWaterSystem(plan, ctx, root)` running them in the rig's order (bodies → fog → rivers → exclusions → quality).
- `ConnectedWatersDemo.cs`: `CreateConnectedWatersRig` is now root + `CreateContext` + coastal Terrain + `BuildWaterSystem(ConnectedWatersDemoPlan())` + floor + crates. `ConnectedWatersDemoPlan()` (internal) is the old literals as plan data: same five body names/centres/extents, same four rivers (knots, tangents, widths, speeds, 3 m seam radius, procedural foam), same parent/source/mouth targets, the upper→lower river stitch via `upstreamRiverIndex`, the carve under the root. Private `CreateConnectedRiver`, `EnableUnderwaterFog`, `ConfigureUnboundedOcean`, `RigFogDensity` and `AddProceduralRiverFoam` deleted from the demo (moved/generalised; zero remaining references).
- `River.cs`: `CreateConnectedRiver` promoted here as THE connected-river recipe (parent, name, knots, parent body, above/under materials, source body, mouth body, upstream river, seam radius, procedural-foam flag). The `GameObject > River` menu now calls it (Q2 duplication gone) and shares the PARENT body's own surface materials (`surfaceAbove/Under.sharedMaterial` — exactly what the demo rig has always handed its rivers); only a parentless river or a parent without materials falls back to `TryBuildSharedAssets` in a fresh `Waters/Water N` (F10 fixed). `MultiBodyStressTest.cs` call site updated to the new signature (same 3 m radius + foam as before).
- Bit-identity of the rig: every serialized value is the same (checked line by line against the old literals: `withPool:false, withGodRays:false, withFoamParticles:false, withSplash:true`; fog on all five at 0.2; ocean = Ocean/openWater/unbounded/largeBodyWindow/useBedDepth+terrain/surf off; quality = package default so `ApplyQualityStep` is a no-op; the pool material is still built because the rig still calls `CreateContext` with its default). Two ORDER differences only, no value differences: (a) the coastal Terrain is created before the bodies (it is the ocean plan's `bedTerrain`), so it precedes them as a sibling under the root; (b) the lower river's SOURCE (upstream stitch) is generated before its MOUTH (the recipe does source then mouth), so its generated seam children are ordered source-first. Neither changes any serialized field on any object.
- Menu river default: `mouthEnd.transitionRadiusMeters` is now written explicitly with `WaterConnection.DefaultTransitionRadiusMeters` (= the field's own default) — same value as before.

### E2 — plan data model
`WaterBuildKit.WaterSystemPlan { systemName, primaryBodyIndex, fogOnAllBodies, splash, qualitySource (PackageDefault|CopyIntoSystemFolder|Asset), quality, bodies[], rivers[], exclusions[] }`, `WaterSystemBodyPlan { name, kind (WaterKind), center, extent, lookPreset, bedTerrain }`, `WaterSystemRiverPlan { name, parentBodyIndex, sourceBodyIndex, upstreamRiverIndex, mouthBodyIndex, points[], tangent, widthMeters, speedMetersPerSecond, transitionRadiusMeters, proceduralFoam }`, `WaterSystemExclusionPlan { name, bodyIndex (-1 = system root), shape (WaterExclusionVolume.Shape), center, size }`. All `[Serializable]` with `[SerializeField] internal` fields and `[Tooltip]`s; the wizard holds it as a `[SerializeField]` field (the same EditorWindow serialization its other fields use). `WaterKind` moved from the wizard onto the kit (the wizard reaches it through its existing `using static`; OceanDefaults partial gets a `using WaterKind = …` alias) — serialized as int, existing wizard state unaffected. `ValidatePlan(plan, problems)` lists every rule (indices, parent-is-a-body, upstream earlier in list + shared parent + not with a source body, knot count, width/speed/radius minima, mesh exclusions refused, name valid as a folder).

### E3 — wizard section
`WaterWizardWindow.WaterSystem.cs`, registered in `OnGUI` after "Fit Spray To Object". Plan fields drawn through `new SerializedObject(this)` + `PropertyField(…, true)` — the river-spline inspector's list idiom, so bodies/rivers/exclusions are Unity's default reorderable lists and every edit is undoable. Validation readout = `ValidatePlan` + `WaterRendererFeatureCheck.Inspect().NeedsRepair` (build refused, user sent to Utilities > Renderer Setup, never auto-installed). "Load Connected Waters demo plan" = `ConnectedWatersDemoPlan()`. "Build Water System": root-exists dialog (Create Water precedent), `Undo.SetCurrentGroupName` / `GetCurrentGroup` / `CollapseUndoOperations` around root + `CreateContext(…, Assets/WebGpuWater/Waters/<system name>, buildPoolMaterial: any pool body)` + `BuildWaterSystem`; on failure `RevertAllDownToGroup` and the folder is deleted only if this build created it. After build: primary body selected, scene dirtied, assets saved, summary logged; when the plan is the demo (root name = "Connected Waters Test Rig") the rig's 12-point checklist is logged too (`ConnectedDemoBuiltMessage + ConnectedDemoChecklist`, one const shared with the menu rig).

### E4 — network manager reuse + menu root
- `WaterNetworkComponents` (internal static, same file): `BuildComponentRoots/Labels(bodies, connections)`, `CollectTopologyConnections`, `CollectSceneConnections` (edit-mode `FindObjectsByType<WaterConnection>`), `BodyOf(port)` (live provider's body, else the serialized `port.body`). The window uses it over its snapshot rows (labels aligned by index); the wizard's read-only "Status (scene)" foldout shows bodies / networks / connections (+unwired) and per body: network, assigned quality, fog, primary.
- Menu: `Window/Abstract Occlusion/Water Network Manager` → `MenuRoot + "Water Network Manager"`; `Tools/Abstract Occlusion/Water/Create Multi-Body Stress Test Scene` → `MenuRoot + "Create Multi-Body Stress Test Scene"`; new `GameObjectMenuRoot = "GameObject/AbstractOcclusion/"` used by the four GameObject creators (paths unchanged). Leaf names unchanged everywhere.
- `ValidateConnection`: the window's two inline `IsWired` re-implementations (`CountUnwiredConnections`, `ConnectionWarningsForBody`) now call `WaterRuntimeValidation.ValidateConnection` (confirmed identical intent: `IsWired ? None : UnwiredConnection`); the wizard status uses it too. Q1 dead helper is no longer dead.

### E5 — hygiene
- `[WebGpuWater] ` / `WebGpuWater: ` literals in my files → `WaterBuildKit.LogPrefix` (wizard 10 + HullSpray 2 + OceanDefaults 1, SceneBuilder 10, kit partials 8 incl. the three `WebGpuWater: ` spellings in Body/SceneRig/MultiBodyStressTest — those three messages now read `[WebGpuWater] …`). The honesty note in `WaterBuildKit.cs` updated to name the remaining legacy sites (inspectors/converters, not my files).
- `TableWidth` is now the sum of the 19 column widths (1982) instead of the hand-summed 2360.
- `DefaultFogDensity` (wizard) and `RigFogDensity` (demo) → one `WaterBuildKit.DefaultAuthoredFogDensity = 0.2f` (wizard const aliases it). `MinExtentComponent` aliases `WaterBuildKit.MinBodyExtentComponent`.

### Compile-risk notes
- `nameof(WaterSystemPlan.systemName)` on instance fields of a nested type reached via `using static` (C# 6+; fine on Unity 6).
- `new SerializedObject(this)` on the EditorWindow: EditorWindow is a ScriptableObject; `[SerializeField] internal` fields on `[Serializable]` nested classes serialize (precedent: runtime's `[SerializeField] internal` fields). If Unity refuses to draw the nested-static-class types, the fallback is moving the three plan classes to namespace scope (no other change).
- `qualitySource.enumValueIndex == (int)WaterQualitySource.Asset` relies on sequential enum ordinals (same idiom as the demo's `enumValueIndex = (int)WaterBodyType.Ocean`).
- `using WaterKind = AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit.WaterKind;` alias to a nested type in OceanDefaults.cs.
- No non-trailing named arguments (checked both `CreateConnectedRiver` call sites).

### Test checklist (Unity)
1. Wizard → Water System → "Load Connected Waters demo plan" → Build. Compare with GameObject > AbstractOcclusion > Connected Waters Test Rig: same 5 bodies (names/positions/extents, fog on, ocean unbounded), 4 rivers with 7 generated connections, carve; differences expected: no coastal Terrain/floor/crates, ocean `useBedDepth` off (assign a Terrain in the plan's ocean row to match), assets under `Assets/WebGpuWater/Waters/Connected Waters Test Rig/` instead of `Water N`. Console shows the same 12-point checklist.
2. Menu rig still builds; hierarchy identical except Terrain now first sibling; Play checks (1)–(12) unchanged.
3. From scratch: 2 bodies (index 0 primary, index 1 at x=+8) + 1 river (parent 0, source 1, mouth 0, 3 points) → Build enabled only once validation is clean; result under one root, materials + (if CopyIntoSystemFolder) `WaterQuality.asset` under one folder; both bodies report that quality in Status.
4. Ctrl+Z once after Build removes the whole system (root, bodies, rivers, ports, connections, carves).
5. Renderer features missing → Build disabled with the pointer to Utilities.
6. Menus: Window/AbstractOcclusion/WebGpuWater/{Water Wizard, Water Network Manager, Create Multi-Body Stress Test Scene}; GameObject/AbstractOcclusion/{Water Exclusion Volume, Connected Waters Test Rig, River, Multi-Body Water Stress Test Rig}.
7. GameObject > River with a body selected: no new `Waters/Water N` folder; the river uses the body's WaterAbove/WaterUnder materials. With nothing selected and no body in the scene: a fresh folder as before.
8. Network Manager in Play: same rows; "Unwired connections" and validation column as before; horizontal scroll width now ends at the last column.

## TRACK C — runtime CPU (audit 03 findings 1-5, 7, 13, 16-17, 20, 22; API trim §3)

Files: Runtime/WaterSplashEmitter.cs, Runtime/WaterExclusionVolume.cs, Runtime/WaterRiverSplineEvaluator.cs,
Runtime/WaterRiverSurfaceProvider.cs, Runtime/WaterRiverSurface.cs, Runtime/WaterVolume.SimWindowPatch.cs,
Runtime/WaterUniformPublisher.cs, Runtime/Query/WaterDomainResolver.cs, Runtime/WaterSimScheduler.cs,
Runtime/WaterSplashRange.cs, Runtime/WaterVolume.Facade.cs, Runtime/WaterVolume.Query.cs,
Runtime/WaterShaderProps.cs (additive block only). No new files, no .meta changes, no test edits needed
(no pinned signature changed; Tests/Runtime/* re-read: ExclusionFeatureTests, WaterRiverGameplayFeatureTests,
WaterRiverQueryFeatureTests, WaterDomainResolutionFeatureTests, TileUniformFeatureTests, ShoreUniformFeatureTests
all still compile against the new surface).

### C1 — splash droplets: one surface sample per droplet, no query while popping (WaterSplashEmitter.cs)
- `DriftOnSurface`: the pop-window test (age < popDuration) now runs BEFORE the domain query; a fresh burst is
  all popping droplets and each used to pay a full resolve for an answer the age test discarded. Same outcome
  for every droplet (the old code returned on `stillPopping` regardless of the surface).
- `TryResolveDriftSurface`: asks the resolver for the PROVIDER only (`WaterDomainResolver.TrySelectProvider`,
  new internal), then samples ONCE: volume body -> exclusion veto (`WaterExclusionVolume.ContainsPoint`, the
  same rule-5 test `TryFillSample` applies) + `TryGetSurface` (the legacy readback drift, unchanged); ribbon ->
  `WaterDomainResolver.TryFillSample` (identical to the old FillSample: sample + exclusion + seam blend).
  Before: full `Resolve` (which sampled the winner with Height|Velocity and threw it away on volumes) THEN
  `TryGetSurface`.
- Why NOT "resolve once per emitter per frame": `WaterVolume.ResolveSplashEmitter` (Wiring.cs:106) falls back to
  ANY emitter in the scene, so one Shuriken system can hold droplets from two bodies at once (lake + river in the
  connected-waters demo); a per-emitter body would carry the river's droplets on the lake plane. The domain
  decision is the cheap half (containment scan); the sample was the duplicated half and is now single.
- Bit-identical: surfaceY/drift come from the same calls with the same arguments; the "outside pool ->
  ballistic" rule is unchanged (TryGetSurface / TryFillSample fail under exactly the conditions the old
  Resolve+TryGetSurface pair failed; BuoyancySurface has no Primary fallback).
- Test: throw a crate (WaterSplashRange) on a pool WITHOUT WaterFoamParticles -> droplets pop, settle, bob and
  drift on ripples; in the connected-waters demo, drops on the river ribbon ride the spline height and are
  carried downstream; droplets landing inside an exclusion volume fall through (no drift).

### C2 — exclusion world->shape matrix cached (WaterExclusionVolume.cs)
- `WorldToShapeMatrix()` caches `ShapeToWorldMatrix().inverse` keyed on `transform.localToWorldMatrix` (one
  native read that changes with position/rotation/lossyScale/parent chain - every input ShapeToWorldMatrix reads)
  and `size`. `transform.hasChanged` is unused in the tree but is a flag any game script can reset, so the
  matrix compare was chosen. Compares are EXACT (`WaterUniformPublisher.ExactlyEqual`, hoisted from the
  cached-sink class to an internal static so both sites share it): the approximate Unity `==` would let a
  slowly drifting hull stay under the epsilon every frame and the cache would never follow it.
- Stale "nothing next to the raycast" comment replaced (the resolver calls it per gameplay sample).
- Bit-identical output (same TRS + inverse when the inputs change; the cached value when they do not).
- Test: play mode, move/rotate/rescale an exclusion volume (inspector or a moving hull) and click the water
  inside it -> no ripple/splash; click just outside -> ripples. Change Size at runtime -> the veto follows.

### C3 — river projection: conservative per-segment reject + margin-independent memo
- WaterRiverSplineEvaluator.TryProjectPoint: before the 17-sample scan of a segment, the squared distance from
  the point to the AABB of the segment's four Bezier control points (a lower bound, convex-hull property) is
  compared with the squared distance to the nearest KNOT (an upper bound: knots are scan samples at t=0/1).
  Segments whose lower bound exceeds the upper bound x (1 + ProjectionRejectSlack = 1e-3) are skipped. The scan
  keeps the first strictly-nearer sample, a rejected segment is strictly farther, so the winner, its t, the
  refinement and the result are identical; the slack is 4 orders above float32 rounding. Cost per rejected
  segment: 4 quaternion rotations + a clamp (was 17 x (4 rotations + 3 normalises + a cross)).
- NOT cached per spline as the task suggested: the evaluator is a pure static (owns neither the transform
  nor the spline's Changed event; WaterRiverSpline.cs is another track's), the bound is ~1/17 of one
  segment's scan, and a stale cache would silently move the river. Documented in the method comment.
- WaterRiverSurfaceProvider.TryProject: the memo now holds the PROJECTION (sample + lateral distance +
  projected flag), which does not depend on the caller's boundary margin; the lateral band test
  (`WithinLateralBand`) is applied on top per call. The resolver's ContainsPoint(0) -> ContainsPointWithin(0.5)
  -> TrySampleSurface(0) sequence on one point in one frame is now ONE projection (was up to three). Results
  identical (same projection, same band test).
- Test: crate on the ribbon floats at spline height; WaterRiverInteractor ripples follow a boat; buoyancy
  fall-through from high above the ribbon still resolves it (WaterRiverGameplayFeatureTests covers this).

### C4 — publisher-tracked ids written only inside the cached pass (hotfix-d rule)
- WaterUniformPublisher: new `IBodyUniformOverride` (TryOverrideFloat / TryOverrideVectorArray) and
  `WriteBodyProps(mpb, overrides)`; a reusable `OverridingUniformSink` wraps the block's CachedUniformSink for
  the pass and substitutes the override value at the moment WriteBodyUniforms (or the shore field it delegates
  to) writes that id. The shadow therefore holds exactly what the block holds. `WriteBodyProps(mpb)` forwards
  with null (public WaterVolume.WriteBodyProps unchanged). WaterVolume gets an internal 2-arg overload
  (WaterVolume.Facade.cs) because `Publisher` is private to the partial class.
- WaterRiverSurface: `_PatchCoverActive`, `_SurfActive` (also tracked - written by ShoreDepth.WriteUniforms;
  the audit missed it), `_UseBedDepth`, `_ClipOceanToTerrain` and the six `_MouthOutflow*` ids now travel
  through a `TrackedUniformOverrides` table into the parent's pass. Standalone ribbon (no parent): the same
  table is written straight into the cleared block (no publisher pass, nothing to shadow). Untracked ids
  (`_IsRiver`, `_RiverFoam*`, `_RiverCascadeTransportActive`, `_RiverFluidActive`, end anchors, the renderer
  property sources) stay direct writes - documented at `ApplyRiverShaderOverrides`.
- WaterVolume.SimWindowPatch: the duplicate `_PatchPoolCenter/_PatchPoolHalf` writes and their ids deleted
  (WriteBodyUniforms publishes the same getters to every renderer of the body); `_IsPatch`/`_PatchDepthBias`
  are the patch's own (never written by the pass) and stay, with a comment saying why.
- Block contents identical per frame; one fewer native set per overridden id per frame on rivers (the body's
  value no longer reaches the block before being overwritten).
- Compile-risk: `IUniformSink sink = overrides != null ? BindOverrides(...) : cache;` relies on the implicit
  CachedUniformSink -> IUniformSink conversion in the conditional (standard C#).
- Test (hotfix-d): pool + ocean with the near-field patch in play mode: no missing-uniform regression (patch
  draws, base sheet keeps its hole; fog through the surface on a second pool); river ribbon does NOT vanish
  when the parent's patch appears on entering play; mouth outflow plume/foam visible at the river mouth; a
  standalone ribbon (no parent volume) still renders with `_IsRiver` and its own mouth data.

### C5 — self-heal rewrites staggered per sink (WaterUniformPublisher.cs)
- Each CachedUniformSink takes a phase (0..127) from a static counter stepping by
  `FullRewritePhaseStrideFrames = 43` (odd -> coprime with the 128-frame interval, ~1/3 of it). After a reset
  (first pass / owner change) the next full rewrite is `interval - phase` frames away; every periodic rewrite
  after that keeps the full `FullRewriteIntervalFrames`. Same cadence per sink, bound still <= 128 frames,
  spikes spread over the interval. Counter reset in `ResetStaticState`.
- Test: WaterCostProbe worst-ms readout on the ocean demo no longer shows a ~0.6 s periodic spike.

### C6 — resolver: no double sample / double containment (Query/WaterDomainResolver.cs)
- Resolve = `TrySelectProvider` (rules 1-4, 7 -> `SelectedProvider {Provider, InsideDomain, HasHeightSample,
  HeightSample}`) + `TryFillSample` (rule 5, seam blend, fields). `SelectSurfaceInRange` returns the winner's
  Height sample; `TryFillSample` reuses it when `options.Fields == Height` (GameplayBodyAt, Membership,
  EmitSplash's RayInteraction resolve) - exactly the same call it would repeat - and re-samples otherwise.
  Buoyancy fall-through reuses the "nothing contains" result for InsideDomain. #22: the explicit-hint check is
  the single typed `options.BodyHint == null` (Unity fake-null aware).
- Behaviour identical: same intent dispatch, same fallback rules (MissingHint never falls back to Primary,
  exactly as before; the non-finite check stays ahead of selection).
- `TryBuildMouthOutflow` frame cache: the function lives in WaterRiver.cs (another track) -> written up as
  CROSS_REQUESTS "From TRACK C" item 1 with the exact code; not done here.

### C7 — standards
- WaterSplashRange: 7 magic numbers -> `MinWaveSizeFactor`, `AutoIntervalJitterMin/Max`,
  `EdgeBandMaxExtentFraction`, `EdgeContainMaxPenetration`, `MinFallHeightMeters`,
  `TargetSpreadNearFraction/FarFraction`, `LaunchSidewaysSpreadFraction` (values unchanged, WHY comments).
- WaterSimScheduler: `InvalidFrame` used at both sites.
- Shader names declared twice: the five `_MouthOutflow{Count,Origins,Directions,Parameters,FoamFrames}` now live
  once in WaterShaderProps (both declaring files were Track C's). The other nine duplicates involve
  WaterCausticsPass.cs / WaterFoamParticles.cs / WaterFoamProfile.cs (not Track C's) and are untouched;
  `_PatchPoolCenter/Half` are no longer duplicated because the patch's copies were deleted (C4).
- Stale comments fixed: publisher "~22x per frame" (#13) and the cached-sink "writes from OUTSIDE" audit note;
  Track A's request on the `_UnderwaterFogSimple` comment applied.

### C8 — API trim (own files only)
- `WaterExclusionVolume.Active` -> internal (publisher reads WriteVolumeUniforms; zero external refs).
- The seven public mutable look fields on WaterExclusionVolume (`wallScatterBoost`, `edgeColor`,
  `edgeIntensity`, `edgeSpread`, `affectParticles`, `particleFadeBand`, `particleDissolveSpeed`) ->
  `[SerializeField] internal` (same field names -> serialized scenes bit-identical; zero refs in Runtime/
  Editor/Tests; default inspector unchanged).
- `WaterVolume.SampleHeightAcrossBodies` deleted (zero refs anywhere, superseded by WaterDomainResolver, built
  on the LEGACY BodyContaining).
- Kept public on purpose: `TryGetWaterHeight` / `TrySampleSubmersion` (listed as the scripting API in
  CHANGELOG.md), `SimWindowHalfExtent` (documented GPU-consumer property), WaterTopology's
  ConnectionCount/GetConnection (Editor readers), WaterSplashRange's demo entry points.
- Compile-risk: none expected; a Samples~ script that read one of the seven exclusion fields would need
  `InternalsVisibleTo` or the field restored - none exist in the package tree.

### Self-check
- `git diff --stat` reviewed for the 13 files above; brace/paren/bracket balance on comment- and
  string-stripped code equals the baseline for every file; every introduced identifier declared once; removed
  identifiers (`FillSample`, `ResolveExplicit/Containing/Buoyancy/NearestSurface`, `_memoMargin`, `_memoHit`,
  `ID_PatchPoolCenter/Half`, `PublishMouthOutflowProperties`, `SampleHeightAcrossBodies`) have zero remaining
  references.

## TRACK D — river editor + connections (audit 04 §2-6, 05 §1-3; D1-D5)

Files touched: Runtime/WaterRiver.cs, Runtime/WaterRiverSpline.cs, Runtime/WaterRiverCurrentField.cs,
Runtime/WaterRiverDisturbance.cs, Runtime/WaterRiverFluid.cs, Runtime/WaterRiverFoam.cs,
Runtime/WaterConnection.cs, Runtime/WaterConnectionPort.cs, Editor/WaterRiverEditor.cs (rewritten as the
partial orchestrator), Editor/WaterRiverSplineEditor.cs, Editor/WaterRiverFluidEditor.cs,
Editor/WaterRiverFoamEditor.cs, Editor/WaterRiverCurrentFieldEditor.cs,
Editor/WaterRiverAuthoringChangeRouter.cs, Editor/WaterRiverFluidBaker.cs, Editor/WaterEditorUI.cs
(additive helpers only). Not touched: Runtime/WaterRiverSurface.cs, Runtime/WaterRiverSplineEvaluator.cs,
Editor/WaterBuildKit*.cs, Editor/WaterWizardWindow*.cs, Editor/WaterNetworkManagerWindow.cs, any test
file (all river tests compile unchanged - no assertion had to move). Editor/WaterEditorPreviewDriver.cs was
read and left as-is (nothing in D1-D5 needs it; the standalone-ribbon preview gate is now stated in the
Wiring tab's help text instead).

NEW FILES (each with a two-line .meta sibling, guid = uuid4):
- Editor/WaterRiverEditor.Path.cs (+ .meta)
- Editor/WaterRiverEditor.Flow.cs (+ .meta)
- Editor/WaterRiverEditor.Surface.cs (+ .meta)
- Editor/WaterRiverEditor.Fluid.cs (+ .meta)
- Editor/WaterRiverEditor.Foam.cs (+ .meta)
- Editor/WaterRiverEditor.Disturbance.cs (+ .meta)
- Editor/WaterRiverEditor.Connections.cs (+ .meta)
- Editor/WaterRiverEditor.Wiring.cs (+ .meta)
(The tree copy carries no .meta files at all, so the shape is the brief's two-liner.)

### D1 — consolidated River inspector (Option C)
- `WaterRiverEditor` is now `internal sealed partial`, one `[CustomEditor(typeof(WaterRiver))]`
  orchestrator (header via `WaterEditorUI.DrawHeader` with a parented/standalone/connected subtitle,
  `WaterEditorUI.TabBar`, one `switch` per tab, `DrawFooter`, foldouts persisted through ONE
  `SyncFoldouts` list + SessionState, tab persisted like the volume editor) + one partial per tab:
  Path (knot readouts, Add/Remove knot, knots list, "Regenerate Ribbon"), Flow (velocity source, per-end
  flow rate m³/s, parent currentFields membership), Surface (surface `samplesPerSegment` /
  `gameplayDepthMeters` / `underSurfaceMaterial`, above-material + mesh readouts, per-end apron readouts,
  the five mouth-outflow knobs greyed via `DrawFieldsIf` until the mouth is generated), Fluid (`bakeData`
  ObjectField + status + STALENESS readout, Bake Grid / Obstacle Rasterization / Fluid Solve / Generated
  Foam sections, Bake button - reuses `WaterRiverFluidBaker.Bake` with the same undo-group pattern the old
  editor had), Foam (Sources / Appearance, warnings), Disturbance ("Wakes" tab: 8 fields + "Interactors
  in range" readout), Connections (see D3/D4), Wiring (parent volume, facade-managed references drawn
  READ-ONLY through `DrawFieldsIf(false, ...)`, component rows with Select / Add).
- Sibling fields are drawn through nested `SerializedObject`s (`Nested<T>(ref cache)` in the orchestrator:
  one per sibling, re-created when the component appears/disappears/is replaced; every tab brackets its
  draw with `Update()` / `ApplyModifiedProperties()`), so Undo + dirty land on the owning component and NO
  field moved, NO scene migrates. Missing optional components (Fluid/Foam/Disturbance) show a help box +
  a `WaterEditorUI.ComponentRow` Add button (`Undo.AddComponent` then `river.ApplyWiring()` so the current
  field sees a freshly added fluid). Foam's Add is disabled until the fluid exists (its RequireComponent).
- A missing property path throws `InvalidOperationException` naming the type and path (the volume
  editor's "sweCompute" lesson) instead of a NullReference on unfold.
- Old sub-editors: `WaterRiverFluidEditor`, `WaterRiverFoamEditor`, `WaterRiverCurrentFieldEditor` are
  now one-line stubs (`WaterRiverEditor.DrawSubComponentStub`: "tuned on the Water River component (X
  tab)" + "Edit on Water River" button; a component whose object has no facade gets an "Add Water River"
  button instead). The stub button sets a static pending-focus pair that the facade editor adopts on its
  next repaint (no selection games, same GameObject). `WaterRiverSplineEditor` keeps OnSceneGUI handles +
  the `[DrawGizmo]` bank preview; its inspector body is the same stub; its Add/Remove-knot Undo code moved
  into `internal static AddKnotWithUndo/RemoveLastKnotWithUndo` (called by the Path tab). It is now
  `internal` (audit 04 §6.10) - grep: no external reference. `[CanEditMultipleObjects]` dropped from the
  stubs (multi-edit of the facade is not supported; nothing relied on it).
- `WaterEditorUI` additive helpers: `ComponentRow(label, component, onAdd = null, addEnabled = true)`,
  `Readout(label, value)`, `SelectAndPing(object)` + three `Style` literals. Nothing existing changed.
- `GenerateEnd` / `RemoveEnd` keep their `internal static` signatures (build-kit callers untouched);
  `RecordIfAlive` additionally records the child GameObject (hideFlags + name changes now undo too).
- Bit-identity: editor-only relocation; no default changed. The three deleted `DrawProperty` helpers and
  the retyped property-name consts are gone with the sub-editors (all paths now live once, in the
  orchestrator or the tab that is their single consumer).
- Compile-risk notes: `Nested<T>` compares `SerializedObject.targetObject != component` (UnityEngine.Object
  operator). `WaterEditorUI.ComponentRow` passes a generic method group (`AddOptionalComponent<T>`) as
  `Action` - standard C#. All partial declarations are `internal sealed partial class WaterRiverEditor`;
  only the orchestrator carries the attribute and base type.
- Test line: open a demo scene with a river (Connected Waters Test Rig), select a river -> ONE "WATER
  RIVER" inspector with 8 tabs; every sibling below it shows a one-line stub whose button switches the
  tab above. Change a foam slider in the Foam tab -> Undo reverts it and the WaterRiverFoam component is
  dirty. Fluid tab -> Bake Settled Fluid works as before; change Lateral Resolution afterwards -> "The
  bake is stale: the grid resolution changed" appears.

### D2 — facade owns the wiring (assign-always)
- `WaterRiver.ApplyWiring` now re-resolves siblings itself and assigns `currentField.spline`,
  `currentField.fluid` (= the sibling fluid or null) and `surface.spline` ALWAYS (was fill-null for the
  two splines, never for `fluid`); `surface.waterVolume = parentVolume` was already assign-always. Drift
  items from audit 04 §3.3 closed: spline x3 (one writer now), fluid x2 (one writer), parent x2 (unchanged
  one-way but now stated). Not closable without WaterRiverSurface.cs: the surface's stale
  `ResolveCurrentField` comment (CROSS_REQUESTS #2). The inert mouth-foam fallback constants
  (WaterRiver.cs:77-79) were left alone: replacing them with zeros would change the packed
  `foamAppearance` values in the no-foam case even though coverage is 0, so bit-identity could not be
  argued from this side.
- Bit-identity: every builder- and test-made river already has the sibling spline in both slots and the
  sibling fluid (or none) in `currentField.fluid` (CurrentField.OnEnable/Reset filled it the same way), so
  the assignment is a no-op on existing scenes. Only a surface/current field pointing at a FOREIGN spline
  under a facade changes - a configuration no builder produces and no test relies on (all facade tests
  use the sibling; the foreign-spline rigs in Ribbon/Query/Gameplay/Disturbance tests have no facade).
- Test line: select a demo river -> Wiring tab -> "Facade-managed References" shows the sibling spline
  / parent / fluid greyed; drag another spline onto River Surface's Spline field in Debug mode -> the
  next inspector nudge on Water River snaps it back to the sibling.

### D3 — Connections: assign-to-connect, auto-sync, validation
- (a) In the Connections tab, changing `body` (or `upstreamRiver` on the source) applies the property,
  then calls `GenerateEnd` when a single target is set, or `RemoveEnd` when the end went empty, all
  collapsed into the same Undo group as the field edit (`Undo.GetCurrentGroup` /
  `CollapseUndoOperations`, the build kit's precedent). The buttons remain as repair verbs
  ("Regenerate Connection" / "Remove Connection").
- (b) `WaterRiver` is now `[ExecuteAlways]` (it was the one river component that was not). It subscribes
  to `WaterRiverSpline.Changed` in OnEnable (rebinding on validate only while enabled) and unsubscribes in
  OnDisable, exactly as WaterRiverSurface does; the handler is the new `internal
  SyncGeneratedConnections()` = the former OnValidate tail (anchors + seams + mouth-outflow
  registration), so knot drags re-aim the generated ports in edit mode and play mode. Console warnings
  (`WarnOnInvalidSetup`) are gated to `Application.isPlaying` (the WaterVolume precedent for its
  missing-wiring failure): the inspector shows the same findings while editing, so a scene load no longer
  spams per river. `WaterRiverAuthoringChangeRouter` additionally collects moved `WaterVolume`s and, only
  when there is at least one, scans `FindObjectsByType<WaterRiver>` and calls `SyncGeneratedConnections`
  on every enabled river whose `HasEndBody(body)` is true - a moved river root already reaches the facade
  through the spline's Changed event. Edit-mode side effects of ExecuteAlways: OnEnable now performs the
  same in-memory parent `currentFields` append / mouth-outflow registration that OnValidate already did
  on every inspector nudge (idempotent, no Undo); OnDisable performs the matching detach. A hand-built
  river therefore joins its parent's list on load (audit 04 §5.2 wanted this); kit-built rivers already
  carry the serialized link so nothing changes for them. One ordering consequence for the build kit is
  filed as CROSS_REQUESTS #4 (append serialized BEFORE adding the facade so the body's Undo record is not
  skipped).
- (c) Connections-tab warnings per end: ambiguous target; partially generated; target assigned but not
  generated ("generation failed - see Console"); `WaterRuntimeValidation.ValidateConnection` (first
  caller - the window's copies are track E's, CROSS_REQUESTS #7) -> "lost a port reference"; end body
  `WaterFog` off; terminal knot inside the body's footprint (reuses the builder's border-contract check,
  refactored into `WaterRiver.IsAnchorDeepInsideFootprint(body, anchor, out inset)` +
  `IsEndKnotDeepInsideBody(end, out inset)`; the generation-time console warning calls the same helper);
  stitch parent mismatch. Play mode also gets a new `WarnOnUngeneratedEnd` console warning (F6).
- Bit-identity: no serialized default changed; `RegenerateConnection` places the same transforms. In play
  mode the facade's OnEnable ran before too; what is new there is one extra warning for a script-assigned,
  never-generated end. LateUpdate now also runs in edit mode (mouth-outflow refresh) - the surface already
  publishes per edit-mode tick, cost is the same 5 bilinear reads the surface's publish already paid.
- Compile-risk notes: `[ExecuteAlways]` + `Application.isPlaying` are used by the sibling components.
  `Object.FindObjectsByType<WaterRiver>(FindObjectsSortMode.None)` matches the tree's precedent form.
  The Tests asmdef has no platform filter (edit+play); nothing in the facade tests activates a rig, and
  the one foam test that adds a facade to an ACTIVE host now runs OnEnable in EditMode too - it wires the
  same sibling spline/fluid the test already set, and `parentVolume` is null there, so no attach happens.
- Test line: select a river, Connections tab, drag a lake onto Mouth End > Body -> the "Connected via
  '...' (port id ...)" readout appears immediately with no button press; Ctrl+Z once removes both the
  reference and the generated children. Drag a terminal knot in the Scene view -> the seam gizmo (select
  the connection through "Generated Objects > Select") and the apron follow; move the LAKE -> same.
  Enter play mode -> no new console warnings on the demo rivers.

### D4 — hidden generated children, single flow-rate copy
- Generated ports/connections get `HideFlags.HideInHierarchy` at generation and on every facade
  enable (`ApplyGeneratedObjectVisibility`, flag-preserving: only the HideInHierarchy bit is touched).
  New serialized `WaterRiver.showGeneratedObjects` (default false = hidden) drives it; the Connections
  tab's "Generated Objects" section has the toggle (records the facade + the six child GameObjects for
  Undo, calls `SetGeneratedObjectsVisible`, repaints the hierarchy) and Select buttons for each river
  port / target port / connection so they stay reachable. The facade keeps its serialized references,
  so existing scenes keep working; scenes authored before this change get their children hidden the next
  time the facade enables (scene load, domain reload) - the objects still save with the scene (no
  DontSave), only their hierarchy row disappears. Documented here as the migration.
- Flow rate: `WaterConnectionPort.authoredFlowRate` + `AuthoredFlowRate` DELETED (grep over
  Runtime/Editor/Tests/Shaders: zero readers; the facade wrote it twice per end). `WaterConnection.
  authoredFlowRate` is the ONE authored copy - `DownstreamPort` derives the flow sign from it and
  `WaterRiverFacadeFeatureTests` asserts it - and stays written by the facade (width x speed).
  Transition radius: `WaterTopology.ApplySeamBlend` reads `connection.TransitionRadiusMeters` at query
  time, so the copy stays; the facade end remains the writer (unchanged) and the connection's tooltip/
  comment now says so.
- Bit-identity: hideFlags are new state on generated objects (the item's purpose); `showGeneratedObjects`
  defaults to hidden. Removing a serialized float from ports drops a `authoredFlowRate: N` line from
  existing scene files on next save; no reader existed, so behaviour is identical. Connection radius/
  flow unchanged.
- Compile-risk notes (the two APIs used WITHOUT an in-tree precedent, both named by the brief/plain
  Unity API): `HideFlags.HideInHierarchy` (enum member; the tree uses `HideAndDontSave`/`DontSave` of the
  same enum) and `EditorApplication.RepaintHierarchyWindow()` (public since Unity 4; without it the
  hierarchy refreshes on its next hover/repaint - if the owner prefers to avoid it, delete that one line
  in WaterRiverEditor.Connections.cs `ApplyGeneratedObjectVisibility`).
- Test line: open the demo scene -> the "Port - ..." / "Connection - ..." children are gone from the
  hierarchy; Connections tab > Generated Objects > tick "Show generated objects" -> they appear; untick ->
  gone; Undo toggles it back. Select buttons open each object's inspector. Saved scene diff on a port:
  only `authoredFlowRate` disappears.

### D5 — river code quality (audit 04 §6, files in the D set)
- `WaterRiverFoam.Configure(float)`, `BakedTexture`, `RiverLength` and the `ActiveData` property that
  only they used: DELETED (zero call sites). `RequestRebuild` is now `internal` (only tests call it;
  InternalsVisibleTo covers them). `[Range(0f, DefaultStrength)]`-style ranges that used a DEFAULT as a
  LIMIT now use the new `MaximumUnitStrength = 1f` (same value -> same slider).
- HalfWidth: `WaterRiverSpline.HalfWidthFraction = 0.5f` (one WHY comment) + `WaterRiverKnot.HalfWidth`
  / `WaterRiverSplineSample.HalfWidth` internal properties; the copies in WaterRiverCurrentField,
  WaterRiverDisturbance, WaterRiverSplineEditor and the inline `terminal.Width * 0.5f` in WaterRiver are
  gone. WaterRiverFluidBaker's `HalfWidth` was three different meanings under one name: cell-centre
  offset (`CellCentreFraction`), signed->unit texel encode (`SignedToUnitScale`) and the real half-width
  (`WaterRiverSpline.HalfWidthFraction`); each is named for what it is, values unchanged. The two copies in
  files outside this track are CROSS_REQUESTS #3.
- WaterRiverFluid: `obstacleContactRadius = 0.1f` -> `DefaultObstacleContactRadiusMeters`; the twin
  `0.0001f` delta-time floors -> `MinimumDeltaTimeSeconds` (attribute + clamp).
- Stale comments fixed: WaterRiver header (fill-null -> owns wiring, ExecuteAlways rationale, hidden
  children), ApplyWiring comment, the OnValidate doctrine comment moved onto `SyncGeneratedConnections`,
  WaterRiverCurrentFieldEditor's "add this field to the Currents list by hand" (gone with the stub),
  WaterConnection field comments (who writes radius/flow).
- New helper `WaterRiverFluidBaker.TryMeasureRiverLength(spline, samplesPerSegment, out length)` wraps
  the existing `BuildArcSamples` walk (no new arc-length walk; audit 04 §6.5 stays open for track C) and
  feeds both the Path-tab length readout and the Fluid-tab staleness check (`BakeLengthStaleToleranceFraction
  = 0.001` - same sampling as the bake, so drift beyond float noise means the spline moved).
- Bit-identity: constants renamed/relocated with identical values; deleted members had no callers.
- Test line: run the WaterRiver* test suites (Tests/Runtime) - they compile unchanged and pass; the demo
  rivers look identical (bank gizmo, current domain, wake ownership all use the same 0.5 factor).

### Self-check
- `git diff --stat` reviewed; brace/paren/bracket balance on comment-and-string-stripped code verified for
  every touched C# file (script in scratchpad; all OK, new files balanced).
- Removed identifiers with zero remaining references: `WaterConnectionPort.authoredFlowRate/
  AuthoredFlowRate`, `WaterRiverFoam.Configure/BakedTexture/RiverLength/ActiveData`, local `HalfWidth`
  consts (CurrentField, Disturbance, SplineEditor, Baker), `WaterRiverEditor.DrawOptionalComponents/
  DrawMouthOutflow/DrawEnd(old)`, the sub-editors' `DrawProperty` helpers and per-editor property-name
  consts, `WaterRiverSplineEditor.InspectorHelp`.
- New identifiers declared once each (grep): `HalfWidthFraction`, `SyncGeneratedConnections`,
  `HasEndBody`, `SetGeneratedObjectsVisible`, `showGeneratedObjects`, `IsAnchorDeepInsideFootprint`,
  `IsEndKnotDeepInsideBody`, `WarnOnUngeneratedEnd`, `TryMeasureRiverLength`, `CellCentreFraction`,
  `SignedToUnitScale`, `MaximumUnitStrength`, `MinimumDeltaTimeSeconds`,
  `DefaultObstacleContactRadiusMeters`, `AddKnotWithUndo`, `RemoveLastKnotWithUndo`,
  `DrawSubComponentStub`, `WaterEditorUI.ComponentRow/Readout/SelectAndPing`.

## CROSS-REQUESTS APPLIED (2026-09-02, post-track merge; all tracks finished)

Each item from CROSS_REQUESTS.md, with what was done and where. All defaults bit-identical; none of these change shader output or serialized data.

- **B.1 (comment)** `Runtime/Shaders/WaterUnderwaterFog.shader:37-38` — dropped the `(WATER_DISABLE_OCEAN_APERIODIC, as LargeBodyCaustics.shader:38 does)` parenthetical (symbol no longer exists anywhere); reworded to "Compiling the aperiodic shape out of this pass was tried on 2026-08-12 and REVERTED".
- **B.2 (comment)** `Runtime/Shaders/OceanFft.compute:47-48` — `.y` lane contract now names `OceanFftJacobianFromSum` (fragment's hoisted cascade sum) with `OceanFftJacobianShore` as the standalone twin.
- **A.1** verified already done by track C — `Runtime/WaterUniformPublisher.cs:196` reads "(WaterFogDebug.hlsl, view 13) reads it". No change.
- **A.2** `Runtime/WaterVolume.cs:307-308` — last-body-out block now also resets `WaterlineWarpCopyWanted = false; UnderwaterFogSimplePublished = false;`. Both are `internal static ... { get; private set; }` on the same partial class (`WaterVolume.Underwater.cs:54,61`), so the assignment compiles. Symmetry only; no consumer can read them while `Bodies.Count == 0`.
- **A.3** noted, not a change request. Nothing done.
- **D.1** `Runtime/WaterRiverSurface.cs` — deleted the 4-arg `Configure(spline, body, material, segmentSamples)` overload. Grep of Runtime/Editor/Tests: the only `WaterRiverSurface.Configure` caller is `Editor/WaterBuildKit.River.cs:158` (5-arg form). Zero remaining references.
- **D.2 (comment)** `Runtime/WaterRiverSurface.cs:389-393` — `ResolveCurrentField` comment no longer claims an OnValidate re-run; it states the truth: resolved ONLY from OnEnable (`:156`), the facade's `ApplyWiring` rewrites `spline` before that, and a standalone surface whose spline is swapped while enabled keeps the old field until re-enable. Behaviour unchanged.
- **D.3** `Runtime/WaterRiverRibbonMeshGenerator.cs:23` local `const float HalfWidth = 0.5f` deleted; `:224` now passes `sample.HalfWidth` (`WaterRiverSplineSample.HalfWidth => Width * WaterRiverSpline.HalfWidthFraction`, same `0.5f` multiply — bit-identical). `Runtime/WaterRiverSurfaceProvider.cs:29` local const deleted; `WithinLateralBand` (`:205`) now uses `width * WaterRiverSpline.HalfWidthFraction` (it receives a float width, not a sample). Comment at `:27-28` names the shared const. No other references to either local const (grep Runtime/Editor/Tests).
- **D.4** `Editor/WaterBuildKit.River.cs:145-152` — `AppendCurrentFieldSerialized(parentVolume, currentField)` now runs right after `currentField.Configure(spline)`, BEFORE `AddComponent<WaterRiverSurface>()` / `AddComponent<WaterRiver>()`, so the Undo-recorded serialized link is written before the `[ExecuteAlways]` facade's OnEnable does its in-memory append (which then early-returns on `IndexOf >= 0`). The trailing call after `facade.ApplyWiring()` was removed (moved, not duplicated); `facade.ApplyWiring()` kept for inactive parents with a comment saying so.
- **D.5** informational; `GenerateEnd` calls kept as-is. Nothing done.
- **D.6** optional dedupe — skipped per instruction.
- **D.7** verified done by track E — `Editor/WaterNetworkManagerWindow.cs:315,333` call `WaterRuntimeValidation.ValidateConnection`. No change.
- **C.1** `Runtime/WaterRiver.cs` — `TryBuildMouthOutflow` frame-stamp/dirty cache:
  - `:93-94` `const int InvalidFrame = -1` (WHY comment: `Time.frameCount` is never negative). Same name/value as the existing `WaterSimScheduler.InvalidFrame` / `WaterRuntimeRelevance.InvalidFrame` (both private to their class, so a local const is the established shape).
  - `:147-157` fields `_mouthOutflowFrame`, `_mouthOutflowValid`, `_mouthOutflowCached`, `_mouthOutflowDirty = true`, plus `_subscribedFluid`.
  - `:406-424` `TryBuildMouthOutflow` is now the gate (`!_mouthOutflowDirty && Application.isPlaying && _mouthOutflowFrame == Time.frameCount` → cached), delegating to `BuildMouthOutflow` (the unchanged body, renamed) through `CacheMouthOutflow`, which stores result + stamp and clears the dirty flag on EVERY exit (all early `return false` paths and the final `return outflow.IsActive` flow through it). Edit mode never takes the cache (no advancing frame stamp), exactly as specified.
  - Dirty sites: `OnValidate` (`:217`), `SyncGeneratedConnections` = the spline `Changed` handler (`:237`), `SyncMouthOutflowRegistration` (`:382`), `SyncSeams` = the `ConfigureBodySeams` push (`:696`), `SyncEndAnchors` = port-anchor sync (`:772`), and the fluid bake assignment via a new `_fluid.ConfigurationChanged += MarkMouthOutflowDirty` subscription (`RebindFluidEvents`/`UnsubscribeFluidEvents` `:259-275`, bound from OnEnable/OnValidate and dropped in OnDisable — same shape as `RebindSplineEvents` and the same event `WaterRiverFoam.cs:99` already subscribes to; `WaterRiverFluid.AssignBakeData` raises it).
  - Bit-identical: every edit-mode call and the first play-mode call per frame run the original body; later same-frame calls return the identical struct the first one produced (inputs settle before LateUpdate). Compile risk: `in WaterRiverMouthOutflow` parameter on a `readonly struct` — precedent `BodySeamEnd.SameAs(in ...)` in the same file.
  - Test line: Play a connected river scene; toggle a mouth connection and edit `mouthOutflowLengthMeters` in play — the current field/foam apron must follow immediately (dirty path), and the Profiler should show one `BuildMouthOutflow` per river per frame.
- **C.2/C.3/C.4** informational. Nothing done.

Self-check: `git diff --stat` = 8 files; brace/paren/bracket balance on comment- and string-stripped code equals baseline for every touched file; every introduced identifier declared once, every removed one (`HalfWidth` local consts, 4-arg `Configure`) has zero remaining references.

## REVIEW — Shaders (compile-safety pass, 2026-09-02)

Scope: every staged file under Runtime/Shaders (11 files incl. the NEW LargeBodyGodRaysRaymarch.hlsl + .meta),
read as the DXC/Tint toolchain would, plus the C# keyword / pass-index consumers in Runtime/Rendering and
Runtime/*.cs. No compiler is available here; every check below is by reading.

### Fixes applied
- Runtime/Shaders/LargeBodyGodRays.shader:19-21 — stale header comment ("runs RaymarchShaderPass (0) then
  CompositeShaderPass (3); see its line 41") now names the 0-or-4 raymarch pick. Comment only; no code change.
- No definite compile error was found in any file, so no code edit was needed.

### What was verified (per file)
- LargeBodyGodRaysRaymarch.hlsl: the moved body is VERBATIM — `git show HEAD:…LargeBodyGodRays.shader` lines
  99-952 (12-space dedent) vs the include, whitespace-normalised diff = only the 15-line header + `#ifndef`
  guard + closing `#endif`. All 3 lamp forks are `defined(WATER_GODRAY_POINT_LIGHTS) && !defined(WATER_FOG_SIMPLE)`,
  so pass 4 (no lamp keyword, `#define WATER_FOG_SIMPLE 1`) compiles the exact former Simple variant and pass 0
  (no Simple keyword) the exact former Full variant. Pass 4 declares NO multi_compile containing WATER_FOG_SIMPLE,
  so the `#define` cannot collide with a keyword define. `.meta`: two lines, 32-hex guid, unique in the tree; no
  sibling .hlsl.meta exists in this copy (only the 11 new .cs.meta), so the brief's two-line shape is the precedent.
- LargeBodyGodRays.shader: pass indices 0/3 untouched, new pass appended as 4; the ONLY C# consumers are
  LargeBodyAtmospherePass.cs consts (RaymarchShaderPass=0, CompositeShaderPass=3, RaymarchSimpleShaderPass=4) —
  no FindPass/UsePass on this shader anywhere. Composite `multi_compile_fragment _ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT`
  matches WaterUniformPublisher `KW_UnderwaterFogSimple = "WATER_FOG_SIMPLE"` and both passes' `ClassifyRtKeyword =
  "WATER_FOG_CLASSIFY_RT"` exactly. Every identifier in the new composite arms is declared before use:
  `SurfaceSignedGapChopInvertedPair` / `WaterlineFarFromSurface` / `WATERLINE_GRADIENT_MIN` (WaterWaterline.hlsl,
  included above), `_ScaledScreenParams` (URP Core), `OceanOwnershipSample` + `_OceanSurfacePrepassScale` declared
  BEFORE the WaterOceanRenderedCoverage.hlsl include (its stated contract), the old local `OceanRenderedCoverage`
  fully deleted (single definition now). `farGap` is written on every path of WaterlineFarFromSurface. Derivatives:
  `ddx/ddy(gapSmooth)` sit AFTER the branches reconverge; the CLASSIFY_RT arm branches on `_CameraDryVolume`
  (uniform), the analytic arm on WaterlineFarFromSurface (camera-position + explicit-LOD height-RT read = uniform),
  and neither branch returns/discards — WGSL reconvergence rule satisfied, same shape the fog's ArmWeight already ships.
- WaterExclusionWall.shader: `_UnderwaterFogSimple` has zero remaining references in the wall (the float is still
  published + read by WaterUnderwaterFog/WaterFogDebug — unaffected). `#ifdef` arms both assign `gap`/`gapSmooth`;
  `fwidth(gapSmooth)` stays outside any per-pixel branch. Keyword is global (`Shader.EnableKeyword`), so no
  per-material plumbing is needed.
- WaterUnderwaterFog.shader: `UnderwaterFog` gained 2 out-params; single caller (FragSolve) updated; both outs
  are written on line 1169-1170 BEFORE the first return. `SceneWorldPos` has exactly one caller. New uniforms
  `_WaterFogClassifyScale` / `_VisibleWaterSurfaceDepthScale` are `Shader.SetGlobalFloat`-published by
  WaterUnderwaterFogPass.cs:503/570 in the same record that sets validity, so a stale scale can never be read
  alongside a valid flag. UsePass names unchanged (WaterUnderwaterWaterline pass untouched except a comment).
- WaterLargeWaves.hlsl: `OceanFftCascadeTerms` / `…TermsAt` / `…DisplacementCascadeWeight` / `…CascadeSumInert` /
  `…NormalCascadeLod` / `…AccumulateNormalTap` / `…FoamFromSum` / `…JacobianFromSum` / `ApplyOceanFftNormalFoamSum` /
  `ApplyLargeBodyWaveNormalFoamAnalytic` are each defined once and before every use (checked in-file order and the
  FragStages consumer, which is only ever included after WaterLargeWaves in WaterSurface.shader passes 0 and 2).
  `OceanFftFarSlopeFloor[c]` indexing by the loop int is the pre-existing pattern. FoamParticles.shader's
  `ApplyLargeBodyWaveNormalShore` caller: signature unchanged. `WATER_DISABLE_OCEAN_APERIODIC`: zero references
  left in any .hlsl/.shader/.compute/.cs (the fog-shader comment was rewritten too).
- WaterSurfaceFragStages.hlsl / WaterSurfaceVertStage.hlsl / WaterSurface.shader: `WaterGeomStage.fft` is set on the
  only construction site (`WaterGeomStage g;` at :345). `_MouthOutflowCount` (WaterWaves.hlsl:105), `_SurfActive`
  (WaterSurfWaves.hlsl:20), `_ShoreDepthValid` (WaterShore.hlsl:31), `_IsRiver` all reach both stages through the
  existing include chain. Every new `[branch]` is on a $Globals uniform except B8's `useCentre > 0.0`, whose body
  (`SampleHorizonSky` → `UNITY_SAMPLE_TEX2D_LOD` + `RawSceneDepth` = `SampleLevel`) has no implicit-LOD fetch or
  derivative — WGSL-legal. Hoisted locals (`riverCurrentData`, `riverWeight`, `oceanMouthWaveWeight`,
  `riverEndSelector`) are defined outside the new branches in both stages. `PondFoamLayerFromCoverage` is defined
  before `PondFoamLayer` and before WaterSurface.shader pass 2 uses it.
- Sampler/texture budget, WaterSurface.shader pass 0: the diffs of the four surface files add NO
  sampler/SAMPLER/Texture2D/TEXTURE2D/tex2D declaration (the `#if` removed around `_OceanDirectionMap` was never
  defined, so that declaration was already compiled in). Count unchanged at the pre-change 16.
- Balance: brace/paren/bracket and `#if…#endif` counts on comment- and string-stripped code balance in all 10
  code files (LargeBodyGodRays.shader 28/28 braces, Raymarch.hlsl 24/24, OceanFft.compute 32/32, Wall 18/18,
  LargeWaves 57/57, WaterSurface 23/23, FragStages 94/94, VertStage 38/38, Fog 90/90, Waterline 15/15).

### Bit-identity claims re-derived (Track B)
- B1, no-outflow pool (fragment):  OLD `gridWindSlope = lerp(WaveSlope(g), WaveSlope(o), infl)`, and with
  `_MouthOutflowCount = 0` → `MouthOutflowDriftedWorldXZ(xz) = xz` so `o == g`, `infl = 0` → `lerp(a, a, 0) = a`
  under either lowering (`a + 0*(a-a)` or `a*1 + a*0`).  NEW `gridWindSlope = WaveSlope(g)`; branch skipped.
  Then both `*= oceanMouthWaveWeight`, both `windSlope = gridWindSlope` (non-river), both `*= _WaveNormalStrength`.
  Vertex: OLD `gridWaveHeight = lerp(WaveHeight(g), WaveHeight(o), 0) = WaveHeight(g)`; NEW `WaveHeight(g)`;
  both `position.y += gridWaveHeight * oceanMouthWaveWeight`. With outflows (count > 0) the new code is the old
  expression with the first lerp operand held in the same variable — same operand order. Detail normals:
  `lerp(base, DetailNormalTilt(xz), 0) = base` — identical.
- B3 product order: displacement OLD `(active * fade * shoal * fetch) * tap` → NEW
  `OceanFftDisplacementCascadeWeight(t) * tap` = `(t.active * t.fade * t.shoalWeight * t.fetch) * tap` — same
  left-to-right chain, and `t.fetch` uses `max(domain,1e-3) * OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION` as before.
  Normal OLD `shoal = active * shoalW * fetch; tilt += (shoal * max(fade, floor[c])) * tap.xz; pinch += (shoal*fade)*tap.y;
  foam += (shoal*fade)*tap.w` → NEW `OceanFftAccumulateNormalTap` — token-for-token the same three lines. LOD
  `log2(1 + camDist/domain)` unchanged. The only structural change is ternary → `[branch]` + two loops.
- B2 single sum: the old three consumers each called `OceanFftNormalSumShore(i.largeWaveSourceXZ, shoreFrag)`
  (via `OceanFftNormalTiltShore`, `OceanFftJacobianShore`, both with `shore = shoreFrag`); the hoisted call has the
  same two arguments, LOD is internal to the function. Whitecap: `OceanFftFoam` samples `ShoreSample(xz)`, which
  returns inert when `_ShoreDepthValid < 0.5` and equals `shoreFrag` when `_SurfActive && _ShoreDepthValid` — so
  the `_SurfActive > 0.5 || _ShoreDepthValid < 0.5` pick is exactly the set where the two coincide. ✓
- B8: fp32 `(((0.34+0.24)+0.24)+0.09)+0.09 == 1.0` exactly (checked with numpy float32); `smoothstep` at t>=1 is
  exactly 1, so `toCentre = 1 - 1*1*1 = 0` and `useCentre = max(0, 1-1) = 0` on an open horizon → `lerp(a,b,0) = a`.
- A1/A4 pixel derivation: `int2(uv * (S * scale))` vs OceanSurfacePrepassPixel's `int2(uv * S * scale)` = `(uv*S)*scale`
  — different association, but identical at scale 1 (the shipped default) and for every fragment centre otherwise
  (the fractional part is 0.5 ± ulp, never at an integer boundary). Not a risk.

### Open risks, ranked (each with its one-line revert)
1. Full raymarch now OPTIMISED on WebGPU (pass 0 lost `skip_optimizations webgpu`). The 2026-08-15 note says every
   Full variant compiled clean, but that was one toolchain build ago and the bug "follows the optimizer". If the web
   build fails in pass 0: add `#pragma skip_optimizations webgpu` back under pass 0's `#pragma multi_compile_fragment _ WATER_GODRAY_POINT_LIGHTS`.
2. WaterUnderwaterFog.shader compile TIME: `OceanFftDisplacementShore`/`OceanFftNormalSumShore` now carry two loops
   under a `[branch]` instead of one loop with a ternary, inlined at every classification site of the (already ~500 s)
   fog compile. Instruction count should be ~equal (the ternary also emitted both lanes) but the optimizer sees two
   loops. If the fog compile balloons: revert WaterLargeWaves.hlsl's two `[branch] if (_OceanAperiodicParams.x > 0.5)`
   blocks to a single loop with `tap = _OceanAperiodicParams.x > 0.5 ? aperiodic : periodic` (keep the helpers).
3. God-ray composite analytic fallback is NOT bit-identical when the eye is > 4 m from its surface reading
   (`WaterlineFarFromSurface` substitutes a flat gap; documented as the fog's own skip). Revert: delete the
   `if (WaterlineFarFromSurface(...)) {...} else` wrapper in FragComposite's `#else` arm, keeping the Pair call.
4. God-ray composite under WATER_FOG_CLASSIFY_RT masks with the fog's classification, and the shared
   WaterOceanRenderedCoverage.hlsl adds the F7/F9 clamps — both are declared behaviour fixes. Revert (keyword):
   drop `WATER_FOG_CLASSIFY_RT` from the composite's multi_compile line (C# keyword enable becomes a no-op).
   Revert (clamps): `git show HEAD:Runtime/Shaders/LargeBodyGodRays.shader` lines 1097-1107 back in place of the include.
5. Composite keyword set `_ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT`: correctness relies on C# never enabling
   CLASSIFY_RT while the global SIMPLE keyword is on. Guaranteed today by `classifyRtRecorded` requiring
   `!fogSource.UnderwaterFogSimple` (WaterUnderwaterFogPass.cs:327) — the fog shader has shipped on the same
   invariant. No action; just do not loosen that gate.
6. `LargeBodyGodRaysRaymarch.hlsl.meta` is the brief's two-line form; Unity will add its `ShaderIncludeImporter`
   block on import (expect a modified .meta after the first domain reload — commit it).
7. B8 per-pixel `[branch]` over 10 explicit-LOD fetches: WGSL-legal, and the reconvergence after it is uniform (no
   early exit in the body). If a Tint uniformity error ever names FinalCompositeStage: remove the `[branch] if
   (useCentre > 0.0)` and restore the unconditional `SampleHorizonSky(float2(0.5, huv.y), centreSky)`.

## REVIEW — Runtime C# (compile-safety pass, 2026-09-02; no Unity available)

Scope: the 28 changed `Runtime/**/*.cs` files (`git diff --cached --name-only -- Runtime`), each diff read in
full and every hunk checked in context as compiler + reviewer; every changed signature grepped across
Runtime, Editor and Tests; `Runtime/AssemblyInfo.cs` InternalsVisibleTo covers both test asmdefs
(`AbstractOcclusion.WebGpuWater.Tests`, `...EditorTests`) and the Editor asmdef.

### Fixes made
- None. No definite compile error was found; no file was edited by this review.

### Verified (the things most likely to break, and why they do not)
- Cross-file signatures: `WaterUniformPublisher.WriteBodyProps(mpb)` still exists (forwards null) — the two
  test callers (TileUniformFeatureTests:22-23, ShoreUniformFeatureTests:106-110) compile; the 2-arg
  `WaterVolume.WriteBodyProps` lives in WaterVolume.Facade.cs, the 1-arg public one in WaterVolume.Update.cs
  (no duplicate across the 34 partials). `WaterDomainResolver.TrySelectProvider/TryFillSample` have one
  external caller (WaterSplashEmitter) with matching `in`/`out` shapes; the old `Resolve*`/`FillSample`
  names have zero remaining references. `WaterRiverSurface.Configure` 4-arg: only the 5-arg caller exists
  (Editor/WaterBuildKit.River.cs:157). `WaterConnectionPort.authoredFlowRate/AuthoredFlowRate`: zero refs;
  the test at WaterRiverFacadeFeatureTests:121 reads `WaterConnection.AuthoredFlowRate`, which still exists.
  `WaterRiverFoam.Configure/BakedTexture/RiverLength/ActiveData`: zero refs (the tests' `RiverLength` is a
  local const). `WaterVolume.SampleHeightAcrossBodies`: zero refs. `WaterExclusionVolume.Active` + the seven
  look fields: zero refs outside the file. `HalfWidth` local consts: zero refs; `WaterRiverKnot.HalfWidth`
  and `WaterRiverSplineSample.HalfWidth` are internal members on public structs (legal).
- `WaterRiver.CacheMouthOutflow(BuildMouthOutflow(out outflow), outflow)`: definite assignment holds
  (an `out` argument assigns before the next argument is evaluated); passing an `out` parameter to an `in`
  parameter is legal.
- `WaterDomainResolver.TryFillSample`: `surface` is definitely assigned after the `if / else if (!Try(out))`
  pair (the else-if's true branch returns). `out` variables declared in `Resolve`'s `if` condition
  (`selected`, `failure`) are in scope after the `if` (C# 7 enclosing-block scoping).
- `WaterExclusionVolume.WorldToShapeMatrix`: `ExactlyEqual(size, _worldToShapeSize)` resolves through
  Unity's implicit `Vector3 -> Vector4` conversion (the Matrix4x4 overload is not applicable); the hoisted
  `internal static ExactlyEqual` pair is declared once (nested-class copies deleted).
- `WaterUniformPublisher`: `IUniformSink sink = overrides != null ? BindOverrides(..) : cache;` — the
  conditional types as `IUniformSink` via the implicit class->interface conversion. `OverridingUniformSink`
  implements all six `IUniformSink` members. `s_nextFullRewritePhase` is a private static of the outer class
  read from the nested sink's ctor (legal) and reset in `ResetStaticState`.
- All six overridden mouth-outflow ids, `_PatchCoverActive`, `_UseBedDepth`, `_ClipOceanToTerrain` and
  `_SurfActive` (via `WaterShoreDepthField.WriteUniforms`, no early return) are written UNCONDITIONALLY by
  `WriteBodyUniforms`, so the ribbon's override table reaches the block on every pass — the C4 claim
  "block contents identical per frame" holds. `_PatchPoolCenter/_PatchPoolHalf` are published to the patch
  block by `WriteBodyProps(block)` inside `PositionPatch` (publisher :807-808), so the deleted duplicate
  writes were byte-identical.
- Unity/URP APIs without precedent: none introduced in Runtime. `AllowGlobalStateModification`,
  `RasterCommandBuffer.Enable/DisableShaderKeyword`, `builder.UseTexture`, `TextureHandle.IsValid()` all exist
  at baseline in WaterUnderwaterFogPass/LargeBodyAtmospherePass; `[ExecuteAlways]`, `HideFlags` bit ops,
  `Application.isPlaying`, `??=` and target-typed `new()` all have in-tree precedent. `LargeBodyGodRays.shader`
  does carry a 5th pass (`LargeBodyGodRaysRaymarchSimple`, index 4) for `RaymarchSimpleShaderPass`.
- Ordering for the classify-RT handoff: fog pass at `BeforeRenderingPostProcessing`, god-ray pass at `+1`,
  so the handle is produced earlier in the same camera graph; the frame+camera stamp and the consume-once
  clear prevent a stale handle crossing graphs.
- Events: `WaterRiver` subscribes `Changed`/`ConfigurationChanged` in OnEnable (and OnValidate only while
  enabled) and unsubscribes both in OnDisable; the resolver/exclusion caches hold no statics that survive
  `ResetStaticState` un-reset.
- Edit-mode (`[ExecuteAlways]` on WaterRiver): every OnEnable/LateUpdate step null-guards its sibling or
  host (`ApplyWiring` early-returns, `SyncSeams` checks `_surface`, `AttachCurrentField` checks
  `_currentField`, `LateUpdate` checks host + field, `_surface?.`/`_mouthOutflowHost?.`); the host list
  `_riverMouthOutflows` is a field initializer, so registration cannot NRE in edit mode.
- Brace/paren/bracket balance on comment- and string-stripped code equals the baseline for all 28 files.

### Open risks (ranked; none redesigned here)
1. **WaterOceanFft `_bakedAtLastDispatch` on a body that stops dispatching** (`_paused`, or
   `_oceanFftInterval` amortisation right at demand onset). If demand lapsed (flag false) before the body
   paused, a later consumer never lands a readback until a Dispatch runs; at baseline the stale field was
   still read back ("objects still float"). Cosmetic/gameplay latency only for a PAUSED FFT ocean; the
   comment in the file claims paused bodies "keep re-landing the last bake exactly as before", which is
   true only when the last dispatch baked. Owner test: pause an FFT ocean after floaters have settled, then
   drop a new floater outside the readback window.
2. **WaterRiver mouth-outflow frame cache vs. inputs that change later in the same frame.**
   `BuildMouthOutflow` reads `_foam.TransportedCascadeAtMouth`, `_surface.MouthRight/MouthLongitudinalMeters`
   and foam knobs that no dirty site covers; in play the first call of the frame (facade LateUpdate) is
   served to the host's render-time refresh, so those inputs now lag by at most one frame where they used
   to be re-read. Not visible at frame rate; listed because the changelog calls the result "bit-identical".
3. **WaterSplashEmitter volume path is slightly more permissive than before.** The old `Resolve` ran
   `WaterVolume.TrySampleSurface(Height|Velocity)` on the volume and returned false when that failed; the new
   path skips it and goes straight to `TryGetSurface`. Both fail on "no readback yet / outside footprint",
   so the difference is theoretical, but it is not literally the same predicate. The non-finite droplet
   position guard that `Resolve` applied is also no longer run (TrySelectProvider documents "must be
   finite"); Shuriken positions are finite in practice.
4. **`WaterRiver` ExecuteAlways in-memory side effects in edit mode** (already documented by track D):
   `parentVolume.currentFields` append/detach and `currentField.fluid`/`surface.spline` assignment without
   Undo/dirty; hideFlags applied on every enable. Correct as designed, but a scene saved after such an
   enable serialises the appended field. Cross-request D.4 covers the build-kit ordering.
5. **Self-heal phase stagger**: `CachedUniformSink` instances created before `ResetStaticState`
   (FEPM without domain reload) keep their old phase while the counter restarts at 0 — harmless (phases
   only spread spikes) but the "every phase visited before one repeats" comment then holds per run, not
   per process.
6. **`WaterExclusionVolume` matrix cache key** uses `transform.localToWorldMatrix` + `size`; a change to
   `MinEdgeLength` clamping inputs is covered (size/scale are inside the key). Nothing else feeds
   `ShapeToWorldMatrix`, so the cache is exact; flagged only so the key is extended if that method grows.

## REVIEW — Editor (compile-safety pass, 2026-09-02)

Scope: every changed/new file under Editor/ (34 .cs + 10 .meta) plus Tests/ against the CURRENT runtime.
Method: acted as compiler + tools reviewer per file - every `FindProperty`/`FindPropertyRelative` string checked
against the runtime's serialized field names (incl. `[SerializeField] private` on WaterRiverDisturbance and
multi-line-attribute fields on WaterRiverFluid), every cross-partial/cross-file call resolved to its current
signature, duplicate-member grep across the WaterRiverEditor.* and WaterBuildKit.* and WaterWizardWindow.*
partials, `[MenuItem]` path uniqueness after the MenuRoot/GameObjectMenuRoot unification, `[CustomEditor]`
uniqueness, brace/paren/bracket balance on comment- and string-stripped code (all 34 files balanced, equal to
baseline), and a removed-runtime-declaration sweep (Active, AuthoredFlowRate, BakedTexture, CachedUniformSink,
Configure(4-arg), RequestRebuild, RiverLength, SampleHeightAcrossBodies, WorldToShapeMatrix, WriteBodyProps,
the WaterExclusionVolume look fields, obstacleContactRadius/deltaTime) against Editor+Tests - every removed
name either still exists under the same name (moved/re-declared) or has zero remaining consumers.

### Fixes applied
None. No definite compile error was found in the Editor slice or in Tests.

### Verified specifically (so the owner need not re-check)
- WaterRiverEditor property paths -> runtime fields: facade (`parentVolume`, `sourceEnd`/`mouthEnd` +
  `body`/`upstreamRiver`/`transitionRadiusMeters`, `showGeneratedObjects`, the five `mouthOutflow*`), spline
  `knots`, surface `spline`/`waterVolume`/`samplesPerSegment`/`gameplayDepthMeters`/`underSurfaceMaterial`,
  current field `spline`/`fluid`, fluid (16 paths), foam (11 paths), disturbance (8 private `[SerializeField]`
  paths) - all present with those exact names.
- Nested SerializedObjects: cached per sibling (`Nested<T>`), `Update()` before and `ApplyModifiedProperties()`
  after each tab's draw; the Wiring tab's read-only nested objects call Update only (never Apply) - correct.
- Undo: `GenerateEnd`/`RemoveEnd` unchanged static signatures (kit callers: WaterBuildKit.River.cs:98/104);
  `SyncEndToTarget` (Connections.cs:47-58) captures the group BEFORE `ApplyModifiedProperties`, generates or
  removes, then collapses - one Ctrl+Z step. Loop check: Apply -> `WaterRiver.OnValidate` ->
  `SyncGeneratedConnections` is transform-only (never creates/destroys), and `RegenerateConnection` never
  re-enters OnValidate - no generate->validate->generate cycle.
- Router (WaterRiverAuthoringChangeRouter.cs:64-79): `HasEndBody` null-checks the body, rivers come from
  `FindObjectsByType` (never null), disabled facades skipped. `FindObjectsByType` has precedent (Boat.cs:378).
- Stubs (Spline/CurrentField/Fluid/Foam editors): `DrawSubComponentStub` returns after the HelpBox + Add button
  when no facade is present - never throws on a hand-built rig.
- Cross-file (track E): `CreateConnectedRiver(Transform, string, List<WaterRiverKnot>, WaterVolume, Material,
  Material, WaterVolume, WaterVolume, WaterRiver, float, bool)` - callers River.cs:47, System.cs:300,
  MultiBodyStressTest.cs:263 (positional-then-named, no duplicate) all match. `CreateExclusionVolume(Transform,
  string, Shape, Vector3, Vector3)` - ExclusionVolume.cs:24, System.cs:338 match. `WaterKind` now
  `WaterBuildKit.WaterKind`: WaterWizardWindow.cs reaches it via its existing `using static WaterBuildKit`
  (imports nested types), OceanDefaults.cs via a file-local alias - same type, no ambiguity, the serialized
  `_kind` int is preserved. `LoadOrCopySystemQuality`, `WaterNetworkComponents.*` (Collect*/BuildComponentLabels/
  BodyOf/UnassignedComponent), `WaterEditorUI.ComponentRow(string, Component, Action = null, bool = true)` /
  `Readout(string, string)` / `SelectAndPing(Object)` - every call site's arity/types match.
- `[MenuItem]` paths: 12 distinct leaf paths, no duplicates; `WaterBuildKit.MenuRoot + WindowTitle` and
  `MenuRoot + "Create Multi-Body Stress Test Scene"` are const-foldable string concatenations (attribute-legal).
- `[CustomEditor(typeof(WaterRiver))]` appears once (WaterRiverEditor.cs:30). Partial modifiers agree
  (`internal sealed partial`) across all nine WaterRiverEditor files.
- Wizard plan: `[SerializeField] WaterSystemPlan _systemPlan` on the EditorWindow (a ScriptableObject) drawn via
  `new SerializedObject(this)` + `FindProperty(nameof(_systemPlan))`; the plan classes are `[Serializable]`
  with `[SerializeField] internal` fields, lists of `[Serializable]` classes -> default drawers work.
- Unity APIs without tree precedent: `EditorApplication.RepaintHierarchyWindow()` (Connections.cs:117) and
  `HideFlags.HideInHierarchy` (runtime WaterRiver.cs:894-895, editor only records the GameObject for Undo) -
  both real, correctly used (bitwise on `gameObject.hideFlags`).
- WaterWaveConstantsValidator: the six new pairs resolve on both sides today (OCEAN_FFT_TG=8/ThreadGroupSize=8,
  WATER_MAX_MOUTH_OUTFLOWS=4/MaximumShaderOutflows=4, RIVER_DISTURBANCE_MAX_SOURCES=8/MaximumSourceCount=8,
  RIVER_CASCADE_TRANSPORT_SAMPLE_COUNT=256/CascadeTransportResolution=256, PEAKED_REFINE_MAX_STEPS=8/
  MaxRefineSteps=8, 0.0625 x CascadeTileOversample 4 = 0.25) - no false warning on first domain reload.
- Tests: both test asmdefs reference only the runtime assembly, so the editor rewrite cannot break them; every
  runtime member the river/exclusion/topology tests touch (`RegenerateConnection`, `TryBuildMouthOutflow`,
  `TryGetEndFrame`, `AttachCurrentFieldToParent`, `sourceEnd/mouthEnd`, `AuthoredFlowRate`, `WriteBodyProps`,
  the `ResetStaticState` family, `WaterRiverFoam.RequestRebuild` now internal via InternalsVisibleTo) still exists.

### Open risks, ranked
1. **WaterWizardWindow.WaterSystem.cs:84** - `new SerializedObject(this)` is created every repaint and never
   disposed (native handle leak until GC; no functional error). One-line fix if it shows in the profiler:
   `using (var serialized = new SerializedObject(this)) { ... }` around DrawWaterSystemPlanFields' body.
2. **WaterWizardWindow.WaterSystem.cs:106** - `WaterRendererFeatureCheck.Inspect()` runs on every repaint of the
   Water System section (it walks the URP renderer asset). Cheap on a small project; cache per
   `EditorApplication.projectChanged` if the wizard ever feels sluggish.
3. **WaterRiverEditor.Connections.cs:47-58** - `SyncEndToTarget` destroys/creates GameObjects inside the
   Section lambda of the same GUI pass. Standard inspector practice (buttons do the same), but if Unity logs a
   one-frame "GUI layout" mismatch after clearing a body, add `GUIUtility.ExitGUI()` after `serializedObject.Update()`.
4. **Runtime path reachable from the editor router** (WaterRiverAuthoringChangeRouter.cs:75 ->
   WaterRiver.SyncGeneratedConnections -> PlaceEndTransforms -> GetUpstreamRiverMouthFrame): a stitched source
   whose upstream river can no longer provide a mouth frame (spline deleted/<2 knots) throws
   InvalidOperationException from an Undo postprocess callback when the MOUTH body moves. Same exception already
   exists on the OnValidate path; low likelihood, loud failure, no data loss. Runtime-side guard if wanted:
   `if (!end.upstreamRiver.TryGetEndFrame(...)) return;` in SyncEndAnchors.
5. **No `[CustomEditor]` for WaterRiverDisturbance** - the component shows Unity's default inspector below the
   Water River one, so its eight knobs are editable in two places (both write the same serialized fields via
   SerializedObject; no conflict). Cosmetic; add a stub editor like the fluid/foam ones if wanted.
6. **Menu moves (user-facing, intentional)** - Water Network Manager moved from `Window/Abstract Occlusion/` to
   `Window/AbstractOcclusion/WebGpuWater/`; the stress-test SCENE builder moved from `Tools/Abstract Occlusion/
   Water/` to the same root. Anyone with muscle memory for the old paths will not find them.
7. **WaterRiverSplineEditor scene handles + the inspector's Path tab both write the spline** - the handles Apply
   via the spline editor's own SerializedObject, the tab via WaterRiverEditor's nested one; Unity re-syncs both
   from the object on Update, so no drift, but a knot drag while the Path tab is open triggers two OnValidates
   per frame (rebuild cost only).
8. **New `.meta` files** (10, two-line form) - the rest of this container copy carries no .meta at all, so their
   GUIDs cannot collide here; on import Unity keeps them as-is (MonoImporter adds its block on first save - commit it).
