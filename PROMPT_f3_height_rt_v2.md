# PROMPT — F3 v2: top-down displaced-height RT, rebuilt on what round one proved

You are a Unity senior dev on the WebGpuWater package
(`Packages/com.abstractocclusion.webgpuwater`). Follow the project coding standards
(no magic numbers, comments explain WHY, reuse-never-rewrite, ask before code, fail
fast, md5-gate every patch with FRESH measurements). Read
`docs/PLAN_F3_height_rt_2026-08-10.md` for the original design, then this prompt —
where they disagree, THIS PROMPT WINS: it encodes what the first build proved.

## WHAT ROUND ONE PROVED (2026-08-10/11) — do not re-derive any of this
1. **The mechanism WORKS.** The height pass rendered a correct 256² R16F wavy
   heightfield on the first try (frame-debugger verified). Every RenderGraph API the
   C# needs exists (`RasterCommandBuffer.SetViewProjectionMatrices`,
   `TextureDesc(int,int)` + `filterMode`/`wrapMode`,
   `UniversalCameraData.worldSpaceCameraPos`/`GetViewMatrix`/`GetGPUProjectionMatrix`).
2. **The RT was innocent of the dark line.** That artifact was the ownership
   coin-toss dipping the coverage mask (fixed: F7 corroboration, BOTH copies of
   `OceanRenderedCoverage` — fog AND meniscus) — see memory `f7-mask-corroboration`.
3. **THE ONE REAL F3 REGRESSION: the fog waterline DESYNCED AT CLOSE RANGE**
   ("fog pops at wrong time"). Root understanding: v1 migrated the per-pixel MASK
   (`ArmWeight`'s near-plane classification) to the RT gap. The RT has 2 m texels;
   the near-plane crossing needs sub-texel precision at CENTIMETRE range. A bilinear
   2 m lattice cannot carry the lens-level waterline, so the mask's line detached
   from the drawn surface exactly where the eye crosses it. The analytic field is
   effectively infinite-resolution there and its cost at the near plane is one
   cache-hot evaluation — it was never the expensive part.

## THE v2 DOCTRINE (locked): TWO AUTHORITIES BY RANGE, ONE EACH
- **NEAR FIELD = ANALYTIC.** The mask / near-plane classification
  (`SurfaceSignedGapChopInverted` in `ArmWeight`, and its `gapSmooth` twin) is NOT
  migrated. Ever, in this increment. Close-range waterline stays byte-identical.
- **FAR FIELD + MARCHES = RT.** The RT replaces the analytic field where the field is
  the WRONG tool: per-step march evaluations (`OceanWavyPath` march + refine), the
  far endpoint classifications (`sceneSurf` reads at 230/615), the carve-exit
  wetness test (497), the downwelling reference (1122, RT inside window, field
  fallback). These are the compile-size and runtime wins, and 2 m texels are MORE
  than enough at wave scale.
- Consequence: the ChopInverted [loop] stays compiled in the fog (mask still uses
  it) — the compile win is smaller than v1's, and that is the correct trade. Do not
  "optimize" the mask back onto the RT.

## WHAT TO BUILD (V1 scope)
1. **Height pass** — rebuild exactly as proven; the recipe survives in TWO places:
   the full files from round one (Bert's chat download history: VertStage 43206563,
   Waterline 91026eba, FogPass a6a1603e, Feature 40986ef8, Validator c346017d, and
   `_to_delete/WaterHeightRT.shader` 2c4f8bd2 + its .meta) and the written recipe in
   memory `transition-exclusion-analysis`. Key points: `DisplaceSurfaceVertex`
   extraction from `vert()` (VERBATIM move — verify with a normalized diff);
   WaterObstacle ortho recipe + `GL.GetGPUProjectionMatrix(proj, true)`;
   texel-snapped 512 m / 256² window; grid with 16 m chop apron, UInt32 indices; own
   Depth32 so folded chop resolves to the highest sheet; property block from
   `s_SurfaceRenderers[0]`; RESTORE the camera view/projection after the draw;
   center+extent+validity in ONE `float4` global zeroed every non-recording frame;
   R16F + bilinear (WebGPU cannot filter float32); NO `WATER_STRIP_SHORE` in the
   height pass; NO exclusion discards in the RT; ripple passed as 0.
2. **Sampling API in `WaterWaterline.hlsl`** — as round one
   (`HeightRTFeatherWeight` / `SampleHeightRTWorldY` / `HeightRTSurfaceY` /
   `SurfaceSignedGapRT`), plus the v2 lesson: the far junction (~250 m) sat inside
   v1's 32 m feather band, weakening the authority exactly where it matters. Either
   FEATHER 16 m, or window 1 km / 4 m texels (plan option B) — propose one to Bert
   with the tradeoff, before code.
3. **Fog migration (far-field sites ONLY** — the mask block at ~907/924 is
   untouchable): march + `RefineSurfaceCrossingRT`, both `sceneSurf` endpoint reads,
   carve-exit test, downwelling reference. All fallbacks to the flat `camSurf`
   asymptote as in v1. NO F6 span repair — F7 fixed what F6 chased.
4. **Dedupe `OceanRenderedCoverage`** (fog + meniscus copies, currently kept in
   lockstep by comment) into a shared header AS PART of this work — you are already
   recompiling both shaders.
5. **Validator pair** for window/resolution, as round one.

## CRITICAL REBASE WARNING
The fog shader on disk is NOT v1's base. It carries **F7 (ownership corroboration)
and F8 (finite tattlers)** — expect ~950be49e-lineage, and the meniscus carries its
F7 mirror (~c915d437-lineage), `WaterFogDebug.hlsl` carries the NaN witness
(~d4d76862). RE-MEASURE EVERY md5 FRESH (Bert hand-edits; line endings PER FILE —
`WaterUnderwaterFogPass.cs` is MIXED CRLF/LF, byte-level patches only). Blind-restoring
any round-one file ERASES confirmed fixes. Apply v1's hunks onto the CURRENT files.

## TEST PROTOCOL (Bert runs; quote duration before starting)
1. **THE POP FIRST**: slow submerge/emerge crossing, raging sea — the close-range
   waterline must be byte-identical in feel (the mask never changed; if it pops,
   STOP and find what else moved).
2. Far junction, above AND below, raging sea: no regressions vs the F7-fixed state.
3. Exclusion/carve stitch unchanged.
4. Compile size (`Logs/shadercompiler-*.log`, `outsize=`) + fps: expect a drop from
   the march field-math leaving; smaller than v1 claimed (mask keeps ChopInverted).
5. F8 tattlers stay silent (no magenta); FogUnpainted stays clean.

## TRAPS (each cost a round; the full list lives in memory — highlights)
- Fog debug views SUPPRESS the meniscus pass — test the meniscus only by its knob in beauty.
- WaterCostProbe F-toggle is SESSION-STICKY — confirm Full tier via FOG_GATES first.
- Score every A/B as a PERCENTAGE, never a boolean — stacked painters produced two
  false verdicts in this hunt.
- Player builds strip `Shader.Find` — the feature's serialized heightRtShader slot
  must be assigned.
- Bert runs git HIMSELF. Never through the bridge, not even status.

## ALSO IN SCOPE: THE ABOVE-WATER SILHOUETTE SPECKS (the semi-win remainder)
Confirmed still present from ABOVE water after F7 (fog density 0 → surface-side).
Mechanism: the above/under coincident twins are the SAME mesh drawn twice with
opposite culling, so wherever both rasterize a pixel the depths are EXACTLY equal —
a pure z-fight — and where the UNDER twin wins a tie from above, the pixel shades
"underwater seen from air": the dark speck at crest silhouettes.
FIX (C#-only, no shader change, no recompile): the twin matching the CAMERA'S
MEDIUM wins all ties via the existing per-renderer `_PatchDepthBias` plumbing
(proven by the patch). Camera above water → above twins get an extra toward-camera
bias; camera submerged → under twins get it. GUARD: apply NO extra while
`WaterVolume.WaterlineActive` (the straddle) — a deterministic winner is wrong for
half a straddling screen (the `_Underwater`-flip mistake, round one; a coin-toss is
the lesser evil for those few frames). The extra must EXCEED the whole level-bias
spread so a matched coarse level beats a mismatched fine one where LODs overlap:
~0.04 m view-space (patch bias is 0.02; level steps are 0.002). Touch points:
`PlaceClipmapRenderer` (stamp per-twin, both calls) + the SimWindowPatch block for
the patch twins. Note the eye-depth prepass stores PHYSICAL depth, so the bias
cannot corrupt the fog's data — it only orders the raster, its designed job.
Test: above-water raging sea, crest silhouettes — specks gone; then the straddle
crossing unchanged.

## PROCESS
Quote expected duration; check in every ~10 min; show a file/line plan before
generating code and WAIT for GO; md5-gate with fresh measurements; keep replies
short. Bert does a human pass on everything.
