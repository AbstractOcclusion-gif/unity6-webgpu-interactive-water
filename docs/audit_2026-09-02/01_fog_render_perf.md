# 01 - Underwater / through-surface fog stack: render-side perf audit (Full quality)

Read-only audit, 2026-09-02. Tree: /tmp/ww. Every claim cites file:line; CONFIRMED = every consumer read in the tree, PLAUSIBLE = code structure is confirmed but the GPU magnitude needs a measurement. No code was changed; only fix DIRECTIONS are given.

Scope files read in full or by section: Runtime/Rendering/WaterUnderwaterFogPass.cs, WaterUnderwaterFogFeature.cs, LargeBodyAtmospherePass/Feature/Gate.cs, WaterSkyFogPass/Feature.cs, WaterCausticProjectionPass/Feature.cs, WaterChunkDepthPass.cs, WaterExclusionDepthPass.cs, WaterDepthTarget.cs, WaterPassCameraGate.cs; Runtime/Shaders/WaterUnderwaterFog.shader, WaterUnderwaterWaterline.shader, WaterFog.hlsl, WaterFogDebug.hlsl, WaterDebugMode.hlsl, WaterParticleFog.hlsl, WebGpuWaterFogAPI.hlsl, WaterWaterline.hlsl, WaterOceanRenderedCoverage.hlsl, LargeBodyGodRays.shader (raymarch + composite), WaterSurface.shader (passes 1 and 3), WaterHeightRT.shader, WaterExclusion.hlsl (loop shape), WaterExclusionMeshSpan.hlsl; Runtime/WaterVolume.Underwater.cs, WaterVolume.Settings.Underwater.cs, WaterVolume.OceanClipmap.cs, WaterVolume.Settings.Ocean.cs, WaterQuality.cs, WaterCostProbe.cs, WaterUniformPublisher.cs (PublishUnderwater).

---

## 0. Ground truth that shapes everything below

**The arming band is wide, not "near the surface".** `UnderwaterFogActive` for an ocean is `waterFog && tierAllowsFog && (_fogNearSurface || eyeInDryVolume)` (WaterVolume.Underwater.cs:370-371). `_fogNearSurface` is "any near-plane corner below `rest + SurfaceHeightEnvelope() + 0.5`" (:496, :525, :546). `SurfaceHeightEnvelope()` (:584-598) is `max(3*|amp|, Hs*|amp|*1.2, surfReach) + 2 m`, so it is never below 5 m on an FFT ocean with amplitude 1, and larger in a heavy sea. **The entire fog chain therefore runs whenever the camera is within ~5+ m ABOVE the rest plane, looking at anything** - that is scenario (a). CONFIRMED.

**The meniscus band is the same width, on both sides.** `nearPlaneStraddles = inFootprint && straddleUnder > 0 && straddleAbove > 0` where `straddleUnder = corner.y < rest + envelope + 0.5` and `straddleAbove = corner.y > rest - envelope - 0.5` (:511-526, :551). If all four corners sit inside the band both counters are 4, so `WaterlineActive` is true for the whole +-(envelope+0.5 m) band around rest - i.e. the whole time you are within ~5 m of the surface, above or below. The pass header's "only during the few straddle frames the waterline is armed" (WaterUnderwaterFogPass.cs:852) is false for an FFT ocean. CONFIRMED.

**Keyword state (07-29 cliff) is fixed in the fog shader.** `_UnderwaterFogSimple` in WaterUnderwaterFog.shader is declared at :58 and read ONLY by the debug view (WaterFogDebug.hlsl:234); the Simple fork is `#ifdef WATER_FOG_SIMPLE` (:301, :711, :786, :808, :844, :1015, :1300). The pass comment at WaterUnderwaterFogPass.cs:279-281 ("UnderwaterSegment tests _UnderwaterFogSimple BEFORE _OceanSurfaceDepthValid") is stale documentation. CONFIRMED. (WaterExclusionWall.shader:137/:158 still branches on the uniform - out of scope, but note it.)

---

## 1. Pass-by-pass frame map (Full tier, ocean clipmap fog source, as CURRENTLY coded)

Injection: fog chain at `BeforeRenderingPostProcessing` (WaterUnderwaterFogPass.cs:27), god rays at +1 (LargeBodyAtmospherePass.cs:39), particles/transparents at +2 (WaterUnderwaterFogFeature.cs:160).

Enqueue gate: WaterUnderwaterFogFeature.cs:101-115 (fog OR waterline OR external river fog, then per-camera pond frustum cull; oceans always pass).

| # | Pass (record site) | RT(s) allocated per camera per frame | Res / format | Draws | Runs in (a) above-in-band | Runs in (b) submerged |
|---|---|---|---|---|---|---|
| 1 | RiverFogFrontDepth / RiverFogBackDepth (Pass.cs:370-393, :435-458) | `_RiverFogFrontDepth`, `_RiverFogBackDepth` | full-res, Depth32 x2 | 1 mesh draw per river surface x2 | only with an external river fog source | same |
| 2 | **VisibleWaterSurfaceDepth** (Pass.cs:264-266, :395-433) | `_VisibleWaterSurfaceDepth` R32F + `VisibleWaterSurfaceDepthBuffer` Depth32 | **FULL res**, no scale | **every live above-surface renderer of EVERY body + rivers**, WaterSurface pass 3 "WaterFogOccluderDepth" (full displacement vertex stage, WaterSurface.shader:558-620) | YES (gate is `riverFogRecorded \|\| UnderwaterFogActive`) | YES |
| 3 | SurfaceDepth ownership prepass (Pass.cs:292-307, :911-964) | `_OceanSurfaceEyeDepth` R32F, `_OceanSurfaceOwnership` R8G8, `OceanSurfaceDepthBuffer` Depth32 (MRT) | **0.5 x 0.5** (`PrepassResolutionScale` :119) | fog source's above renderers only: base + patch + N clipmap "above" levels, WaterSurface pass 1 "OceanSurfaceEyeDepth" (WaterSurface.shader:348) | YES (gate: fog OR waterline, ocean, !Simple) | YES |
| 4 | HeightRT (Pass.cs:311-320, :485-536) | `_WaterHeightRT` R16F + `WaterHeightRT.Depth` Depth32 | 256x256, 512 m window | 1 draw of a **272x272-cell grid = 74,529 verts / 147,968 tris** (constants :77-81, grid :628-633) through WaterHeightRT.shader (full WaterSurfaceVertStage displacement, no ripple) | YES (UnderwaterFogActive) | YES |
| 5 | LensHeightRT (Pass.cs:333-341, :538-593) | `_WaterLensHeightRT` RG16F + depth | 256x256, 4 m window | 1 draw, ~2.4k-vert two-density grid, ripple ON | YES (classifyRtRecorded && ocean) | YES |
| 6 | **WaterFogClassify** (Pass.cs:342-344, :460-483) | `_WaterFogClassifyRT` | **FULL res, R32G32_SFloat (8 B/px)** (:36, :462-468, no ApplyScale) | 1 fullscreen | YES (fog OR waterline, !Simple, !debug) | YES |
| 7 | **WaterFogSolve** MRT (Pass.cs:1038, :1084-1112) | `_WaterFogSolveAbsorb`, `_WaterFogSolveInscatter` | camera res x `FogSolveScale` (tier / probe R), R16G16B16A16_SFloat x2 | 1 fullscreen, keyword `WATER_FOG_CLASSIFY_RT` toggled on the cmd (:1107-1110) | YES | YES |
| 8 | WaterUnderwaterFog blend (Pass.cs:1040-1065) | camera colour ReadWrite | full res | 2 fullscreen draws in ONE raster pass (absorb Zero/SrcColor, inscatter One/One) | YES | YES |
| 9 | WaterlineCopy (Pass.cs:864-872) | `_WaterlineSceneTex` = copy of camera colour | full res, camera colour format | AddCopyPass | YES whenever `MeniscusWarp > 0` (default 0.35, Settings.Underwater.cs:65) and WaterlineActive | YES (same band) |
| 10 | WaterUnderwaterFog.Waterline (Pass.cs:874-897) | camera colour ReadWrite | full res | 1 fullscreen (UsePass -> WaterUnderwaterWaterline.shader) | YES | YES |
| 11 | LargeBodyGodRays.Raymarch (Atmosphere.cs:169, :271-308) | `LargeBodyGodRaysHalfRes` (camera colour format, half res :199-210) | half res | 1 fullscreen, `_LargeGodRaySteps` (default 24, min(tier), clamp 64) steps | YES: feature only rejects when `cam.y > rest + envelope + 0.5` (Feature.cs:388-392); inside the band every march pixel early-outs at shader :442 after paying `GodRaySurfaceY(cam)` | YES |
| 12 | LargeBodyGodRays.HistoryCopy (Atmosphere.cs:177-179) | `_LargeGodRayHistory` persistent RTHandle per game camera (:231) | half res | AddCopyPass every game-camera frame | YES | YES |
| 13 | **LargeBodyGodRays.Composite** (Atmosphere.cs:185, :310-324) | camera colour ReadWrite | **full res** | 1 fullscreen; per pixel: 4 analytic surface-field evaluations + 3 ownership samples (shader :1165-1190) | YES - the composite is NOT early-outed in the band, only the march is | YES |
| 14 | WaterParticlesAfterFog / WaterTransparentsAfterFog (Feature.cs:170-239) | camera colour RW (+ depth RW for user half) | full res | foam overlays (WaterSurface pass 2), sprites, RestoreOpaqueDepth fullscreen + user renderers | sprites: fog armed && live emitters; user transparents: ANY water frame | same |

Also in the frame but not part of the fog chain: WaterExclusionDepth front/back (2 depth RTs, all exclusion shapes, AfterRenderingOpaques), WaterChunkDepth front/back (mesh chunks only), WaterCausticProjection (per-body fullscreen x2 at AfterRenderingSkybox), WaterSkyFog (AfterRenderingSkybox, only with RenderSettings.fog).

**Render-pass count for one submerged Full-tier ocean frame (no river, no exclusions): 11 render passes + 2 copy passes owned by this stack** (#2,#3,#4,#5,#6,#7,#8,#10,#11,#13,#14 + copies #9,#12), i.e. 11+ attachment load/store cycles and RT switches - the thing the file's own comments say costs "far more on the WebGPU backend than native" (Pass.cs:115-116, :285-287). CONFIRMED by counting the `AddRasterRenderPass`/`AddCopyPass` sites above.

**Keyword variants compiled (WaterUnderwaterFog.shader):**
- Pass 0 Absorb, Pass 1 Inscatter: no `multi_compile` -> 1 fragment each (:1431-1437, :1476-1481).
- UsePass Waterline: `_ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT` -> 3 (WaterUnderwaterWaterline.shader:20).
- UsePass RestoreOpaqueDepth: 1.
- WaterFogClassify: `_ WATER_STRIP_SHORE` -> 2 (:1519).
- WaterFogSolve: `_ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT` x `_ WATER_STRIP_SHORE` x `_ WATER_FOG_POINT_LIGHTS` = 3 x 2 x 2 = **12** (:1542-1553). Of these, the 2 `WATER_FOG_SIMPLE + WATER_FOG_POINT_LIGHTS` combos are documented as never armed together (:1552, WaterUniformPublisher.cs:561-571) and are dead compiles.
- RiverFogFront/BackDepth: 1 each.
- **Total: 18 fragment programs in the fog shader (+4 via UsePass).** Not an explosion; but 12 of them are the ~600 KB heavy module (own comment :1546). The `multi_compile_fragment` keeps vertex programs at 1 per pass. CONFIRMED.

**LargeBodyGodRays.shader:** Raymarch `3 (shadows) x 2 (SIMPLE) x 2 (STRIP_SHORE) x 2 (GODRAY_POINT_LIGHTS) = 24`; BlurH/BlurV 1 each (never dispatched, own comment :18-19); Composite `2 x 2 = 4`. **30 fragment programs**, 24 of them the 64-step march.

Which keywords are on at Full tier, ocean, no lamps, no bed depth: solve = `WATER_FOG_CLASSIFY_RT + WATER_STRIP_SHORE`; classify = `WATER_STRIP_SHORE`; waterline = `WATER_FOG_CLASSIFY_RT`; god rays = `_MAIN_LIGHT_SHADOWS_CASCADE + WATER_STRIP_SHORE`.

---

## 2. Ranked bottleneck list

### B1. `_VisibleWaterSurfaceDepth` prepass is recorded on EVERY armed frame and, for an ocean fog source, is NEVER READ - a full-res re-draw of every surface sheet in the scene thrown away. **CONFIRMED.**
- Record gate: WaterUnderwaterFogPass.cs:264-266 `(riverFogRecorded || WaterVolume.UnderwaterFogActive) && RecordVisibleWaterSurfaceDepth(...)`.
- Cost: `CollectAllAboveSurfaceRenderers` (ALL bodies) + river renderers (:397-398) drawn through WaterSurface.shader pass 3 "WaterFogOccluderDepth" (:558-620, full `WaterSurfaceVertStage` vert = FFT/shore/surf displacement per vertex) into a FULL-res R32F colour + FULL-res Depth32 (:401-416, no `ApplyPrepassScale`), `AllowPassCulling(false)` (:425) so RenderGraph cannot drop it. With the default clipmap (grid 64, sim window 32 m, outer radius 10 km -> `1 + ceil(log2(10000/65.8)) = 9` levels, Settings.Ocean.cs:583-626) that is base + patch + 9 levels = **11 displaced-mesh draws per frame**, on top of the identical 11 draws the ownership prepass (#3) already makes at half res.
- Consumers of the texture, exhaustively (grep `_VisibleWaterSurfaceDepth|VisibleWaterSurfaceEyeDepth`): WaterUnderwaterFog.shader:216-221 (helper), :247-255 inside `RiverFogSegment` (gated `_RiverFogDepthValid`, i.e. river only), and :1097-1113 inside `ArmWeight` AFTER `if (_UnderwaterUnbounded > 0.5) return ...` at :1054-1062, i.e. **bounded bodies only**. For an unbounded ocean with no external river fog there is no reader.
- This is exactly the pattern the same file diagnosed for the ocean prepass on the Simple tier (:279-288: "Recording it anyway re-drew every ocean surface renderer a second time ... and threw the result away. It also forced a mid-frame render-target switch").
- A/B: no probe key. Disable by clearing the gate condition; nothing else changes for oceans.
- Fix direction: record only when `riverFogRecorded || (UnderwaterFogActive && fogSource != null && !fogSource.IsOceanClipmap)`; and when it IS needed, allocate it at `PrepassResolutionScale` like the ownership RT (its only comparison is against a 0.01 m epsilon in eye depth, which survives half res).

### B2. Ocean-surface prepass re-draw set: ~11 displaced-mesh draws per prepass, 2 prepasses, plus a 74k-vertex height-RT grid, plus the lens grid - every armed frame. **CONFIRMED (draw counts), PLAUSIBLE (share of GPU time).**
- Ownership prepass (#3): `CollectAboveSurfaceRenderers` = `surfaceAbove + _patchRenderer + every _clipmapLevels[i].above` (WaterVolume.OceanClipmap.cs:38-45), drawn per renderer with `GetPropertyBlock` + `DrawMesh` (Pass.cs:966-981). Each draw runs the full surface vertex stage (pass 1 includes WaterSurfaceVertStage.hlsl, WaterSurface.shader:374). The ownership RT is half res (good), but vertex cost does not scale with resolution.
- Height RT (#4): grid is `(512/2 + 2*8)^2 = 272^2` cells -> 74,529 vertices, 147,968 triangles (Pass.cs:77-81, :628-633), each vertex running WaterHeightRT.shader's `vertHeight` = the full surface displacement chain (WaterHeightRT.shader:43-58 includes WaterSurfaceVertStage.hlsl). Redrawn every armed frame even when the camera has not moved a texel (the centre is snapped to the 2 m texel, :488-489, but there is no "unchanged this frame" skip - the FFT animates so it is a legitimate per-frame update, but the apron+window could be far coarser: the RT is 256^2 and the grid is 272^2, so the raster is essentially 1 vertex per texel).
- Lens RT (#5): ~2.4k verts (own comment :102) - cheap, but it is a 5th render pass with its own RT switch.
- Total per armed frame: 11 (ownership) + 11 (visible depth, B1) + 1 (height RT, 74k verts) + 1 (lens) = **24 displaced-mesh draws in 4 render passes**, plus the ~22 queue-time draws of the same sheets. 
- A/B: none for the prepass. Cost-probe F (Simple) removes ALL of #2-#6 at once (gates :293, :314, :329), so "F: Full -> Simple" isolates "the whole prepass+classify fan" but cannot separate it from the analytic-vs-flat solve.
- Fix direction: (i) B1 first; (ii) reduce the height-RT grid to a fraction of texel density (the target is bilinear-filtered R16F; a 1 vertex per 2 texels grid = 4x fewer verts) or bake the FFT height straight from the cascade textures in a fullscreen pass over the 256^2 target instead of rasterising a displaced mesh; (iii) cull the far clipmap levels from the prepass when the camera is submerged (the underside sheet beyond a few hundred metres cannot be visible through fog at Full-tier densities - PLAUSIBLE, needs the density numbers).

### B3. The god-ray COMPOSITE pass recomputes the waterline classification analytically at FULL res - 4 surface-field evaluations per pixel - while the frame already holds that exact pair in `_WaterFogClassifyRT`. **CONFIRMED (duplication), PLAUSIBLE (top-3 GPU cost).**
- LargeBodyGodRays.shader:1165-1181: `nearWorld` (near-plane point, identical to `WaterlineClassifyPoint` when not in a dry carve, WaterUnderwaterFog.shader:927-937), then `gap = SurfaceSignedGapChopInverted(nearWorld)` (= 3 field evaluations, WaterWaterline.hlsl:122-164) AND `gapSmooth = SurfaceSignedGap(nearWorld)` (= a 4th full evaluation, :96-99). WaterWaterline.hlsl:16-17 prices one evaluation at 4 source reads (periodic FFT) or 24 (aperiodic). So **16-96 texture reads per full-res pixel**, on every frame the god-ray pass is enqueued, including scenario (a) where the march early-outs and the composite multiplies by ~0.
- The fog side already does this once per frame into `_WaterFogClassifyRT` (RG32F: x = chop-inverted gap, y = smooth gap, WaterUnderwaterFog.shader:1118-1126) and both fog consumers load it (:997-1002, WaterUnderwaterWaterline.shader:72-97). The composite pass does not declare or read it.
- The composite also has NO `WaterlineFarFromSurface` skip (the fog and meniscus have it: WaterUnderwaterFog.shader:970, WaterUnderwaterWaterline.shader:112) and NO lens-RT path, so it is the most expensive of the three classifiers.
- Plus `PaneAwareCompositeMask` (:1109-1142) compiles a 5th evaluation (`SurfaceSignedGapChopInverted(carveExitWorld)`, :1139) behind uniform `_LargeGodRayFromAir > 0` - register-sized in, rarely run.
- A/B: probe **G** toggles the whole god-ray pass (march + copy + composite). No key isolates the composite.
- Fix direction: give the composite the `WATER_FOG_CLASSIFY_RT` variant and load the RT (it is produced earlier in the same frame and bound as a global by `SetGlobalTextureAfterPass`, Pass.cs:477); fall back to the analytic pair only when the fog chain did not record it.

### B4. The god-ray raymarch ships UNOPTIMISED on the one platform that matters: `#pragma skip_optimizations webgpu`. **CONFIRMED (presence), PLAUSIBLE (magnitude).**
- LargeBodyGodRays.shader:97, justified at :89-96 (translator bug in Simple-tier variants). The pragma applies to ALL 24 raymarch variants, including every Full-tier one that "compiled clean".
- What is unoptimised: a `[loop]` of up to 64 steps (:161, :671, :760-847), each step a cascade shadow-map fetch (:765), an `ExclusionSunVisibility` loop (:769), `DepthFadeScalar` (exp), a caustic SampleLevel behind uniform `wantCaustic` (:784), `InsideExclusion` (:797), and in the lamp variant an inner `[loop]` over up to 8 lamps with `atan` (:822-841). With optimisation off none of the loop-invariant hoisting the source relies on (`viewFogStep`, `refractedSun`, `causticRefPlaneY` :690-746) is guaranteed to survive the GLSL translation.
- Trip count: `steps = clamp((int)_LargeGodRaySteps, 1, 64)`; `_LargeGodRaySteps = min(largeGodRaySteps, tier.GodRaySteps)` (WaterVolume.Settings.Ocean.cs:658), defaults 24 (:576) and tier High 24 (WaterQuality.cs:34). Half-res target. Gated by tier `GodRays` (WaterQuality.cs:79) and by the dry-camera reject ONLY above `rest + envelope + 0.5` (Feature.cs:388-392) - i.e. NOT gated inside the wide arming band, where :442 early-outs per pixel after paying the camera-height fetch.
- A/B: probe **G**. There is no runtime switch for the pragma; the browser build must be rebuilt with the pragma scoped to `WATER_FOG_SIMPLE` variants only (or with the Simple raymarch variant stripped, since the Simple fog tier is exactly the tier that turns god rays down).
- Fix direction: move the pragma into a `#if defined(WATER_FOG_SIMPLE)`-only sub-shader/`#pragma` guard is not possible per se; instead split the raymarch into two passes (Full / Simple) and only the Simple one carries `skip_optimizations`, or find the offending construct with the Simple variant isolated. Measure first with G.

### B5. `WaterFogClassify` runs at FULL resolution with an 8-byte pixel, regardless of `FogSolveScale`. **CONFIRMED.**
- Pass.cs:462-468: `GetTextureDesc(cameraColor)`, format `R32G32_SFloat` (:36), no `ApplyScale`. The solve (its main consumer) runs at `FogSolveScale` (0.5 or 0.25 under probe R) and LOADs the classify RT at the full-res pixel of its uv (WaterUnderwaterFog.shader:997-1002) - so at scale 0.5 the classify computes 4x the pixels the solve reads; at 0.25, 16x.
- Per-pixel classify cost (WaterUnderwaterFog.shader:963-993): uniform `WaterlineFarFromSurface` skip (1 tap) when the camera is > 4 m from its local surface AND no exclusion volume exists (WaterWaterline.hlsl:254-267: `if (exclusionsActive) return false;`). Otherwise: 1 full analytic evaluation for `gapSmooth` + 1 lens-RT tap (when the lens RT covers the point) or 3 more evaluations (`SurfaceSignedGapChopInverted`) where it does not. Off the lens window (any pixel whose near-plane point is > 2 m from the camera xz - impossible for a normal near plane, so in practice the lens covers) it is 1 evaluation + 1 tap.
- So within 4 m of the surface (the whole waterline band) the classify is >= 1 analytic field evaluation per FULL-res pixel including all sky pixels, and any exclusion volume in the scene disables the far-skip forever.
- The other consumer, the meniscus (#10), is full res and genuinely needs full-res gaps only in the ~6 px band.
- A/B: none (R scales the solve only). F to Simple removes it.
- Fix direction: allocate the classify RT at `max(FogSolveScale, waterline ? 1 : scale)`, or have the meniscus fall back to its own analytic solve (it already has that path, WaterUnderwaterWaterline.shader:98-121) so the RT can follow the solve scale; consider R16G16F if the 6-px feather tolerates ~1 cm error at 10 m gaps (the RG32F choice is documented at :78-80 as precaution, not measurement).

### B6. The waterline (meniscus) pass + a full-res camera-colour COPY run on every frame within +-(envelope+0.5 m) of the rest plane, not "the few straddle frames". **CONFIRMED.**
- Gate derivation in section 0 (WaterVolume.Underwater.cs:511-526, :551, :376). Envelope >= 5 m for an FFT ocean at amplitude 1.
- Copy: Pass.cs:863-871 whenever `MeniscusWarp > 0` (default 0.35, Settings.Underwater.cs:65). Then a full-res raster pass with attachment ReadWrite (:874-897).
- Per pixel the meniscus itself is cheap on the classify-RT variant (1 load + 3 ownership samples + ddx/ddy) and `clip()`s outside the band, but the pass and the copy are full-res load/store cycles on WebGPU.
- A/B: inspector `Meniscus Warp = 0` removes the copy at runtime (gate reads the live knob every record, :862-863); inspector `Meniscus` off removes the pass. No probe key.
- Fix direction: arm `WaterlineActive` on the near-plane corners against the height RT / lens RT (current-frame rasterised heights, +-0.5 m pad) instead of the analytic envelope; the "no readback" doctrine at :482-493 was about the stale FFT readback, but the height RT is a same-frame GPU authority - a small CPU readback of it is not available, so the cheaper alternative is to keep the CPU band but have the pass early-out when `WaterlineFarFromSurface` is true (uniform, 1 tap) BEFORE the copy: record the copy conditionally on `abs(cam.y - surfaceY) < margin` using the CPU `surfaceY` from `ComputeCameraSubmerged` (:466), which is the stale-but-close readback - only the copy would flap, never the pass.

### B7. Scaled-solve upsample weights are computed twice per pixel (absorb AND inscatter), and the solve samples scene depth 3 times. **CONFIRMED, small.**
- `SolveUpsampleTaps` (WaterUnderwaterFog.shader:1390-1422): 4 alpha LOADs + 1 depth sample + weights; called at :1451 (absorb) and :1489 (inscatter) with identical inputs - 10 fetches per full-res pixel duplicated, 20 total on scaled frames, versus 2 on unscaled.
- In the solve: `SceneWorldPos(uv)` at :1145 (inside `UnderwaterFog`) and again at :1598, plus `SampleSceneDepth` a third time at :1637 for `upsampleDepth`. Three depth fetches + two `ComputeWorldSpacePosition` matrix multiplies where one would do.
- A/B: probe **R** (full/half/quarter) shows the net effect of the scaled path; it cannot isolate the duplicate.
- Fix direction: return `sceneWorld`/`sceneEye` from `UnderwaterFog` by out-param; for the blend passes either write the 4 weights into a tiny R8G8B8A8 target in the solve or accept the duplication (it is ~10 loads).

### B8. Twelve render passes + two copies for one fogged frame; every RT is a separate transient. **CONFIRMED (count), PLAUSIBLE (that pass count, not shading, dominates at 200 fps).**
- See the frame map. At 5 ms/frame the fixed per-pass overhead (encoder begin/end, attachment load/store, WebGPU validation) of 13-14 passes is not free; the package's own comments call the RT switch the dominant WebGPU cost (Pass.cs:115-116, :285-287).
- Cheap merges available without shading changes: (i) #4 and #5 (height + lens RT) are two 256^2 targets rendered back to back from the same material - one 512x256 atlas (or MRT is not possible since the grids differ, but a single pass with two viewports is) saves a pass; (ii) #2 into #3 as a third MRT attachment at prepass scale (it is the same renderer set drawn through a near-identical fragment) - or delete #2 per B1; (iii) #9 copy can be replaced by reading `_CameraOpaqueTexture`... no - the meniscus warps the FOGGED scene, so the copy is needed only while warp > 0 (B6).

### B9. Debug false-colour views are compiled into every release solve variant behind a UNIFORM. **CONFIRMED (structure); cost small, not the 07-29 cliff pattern.**
- `_WaterDebugMode` is a float uniform (WaterDebugMode.hlsl:14-16). `WaterFogDebugColor` (WaterFogDebug.hlsl:198-247) is called unconditionally at the end of `UnderwaterFog` (WaterUnderwaterFog.shader:1362-1365); the branch ids are written into `static` globals at every span return (:341, :479, :584, :618, :632, :645, :659, :708, :723, :801) so `g_WaterFogDebugBranch`/`g_WaterFogDebugSheetSigned` stay live across the whole solve. Also the `armWeight <= 0` early-out is conditioned on `_WaterDebugMode < WATER_DEBUG_FOG_FIRST` (:1179).
- It is pure ALU (no textures, no loops) - ~30 selects and 2 live registers - so it is not a register cliff, but it is dead code in 12 shipped variants and it blocks the compiler from dead-stripping `wetSpanLen`/`classifyPushDist` bookkeeping.
- Fix direction: fence `WaterFogDebug.hlsl` and the `WaterFogDebugBranch()` stamps behind a `WATER_FOG_DEBUG` keyword compiled only in the editor (`#pragma multi_compile_fragment _ WATER_FOG_DEBUG` + `#pragma editor_sync_compilation`, or a `shader_feature` set on the engine material from `WaterDebugView` in the editor only); release builds then carry the `_ ` variant only.

### B10. Shore/clarity fetches paid even when the clarity feature is off (non-STRIP bodies). **CONFIRMED, small.**
- `WaterDepthClarity(ShoreShoalDepth(sceneWorld.xz))` (WaterUnderwaterFog.shader:1354): the argument (a shore tex2Dlod, WaterShore.hlsl:114-126) is evaluated before `WaterDepthClarity`'s `_DepthClarityStrength <= 0` early-out (WaterFog.hlsl:190-194). Same for `OceanTerrainFootprintWet` at :1153 (WaterShore.hlsl:73-78). Under `WATER_STRIP_SHORE` both collapse; on a bed-depth body they are 2 fetches/pixel unconditionally.

### B11. `surfaceRefY` is computed by every span path but consumed only when the carve empties the span. **CONFIRMED, small on oceans / 1 analytic evaluation per pixel on ponds.**
- Producers: :350/:359/:366, :429, :660/:666/:673, :729, :849, :856 (pond: `SurfaceHeightAtXZ(enterWorld.xz)` = a full analytic field evaluation). Consumer in the Full variant: only `deepestY = surfaceRefY` at :1239 (when `pathLen <= 0`); the downwelling reference is re-derived at the mean point (:1313-1335). Ocean paths pay 1 height-RT tap for it (`HeightRTSurfaceY` :350, :660); the pond path pays the analytic wind-wave + swell evaluation.

### B12. CPU: `WriteBodyProps` (~170 property writes) per camera per frame in the god-ray pass and per body per draw in caustic projection. **CONFIRMED, CPU-side only.**
- LargeBodyAtmospherePass.cs:139 `sourceOcean.WriteBodyProps(sourceBlock)` every record; WaterCausticProjectionPass.cs:241-242 inside the render func per body per pass (x2 passes). The fog pass avoids this via `renderer.GetPropertyBlock` copies (Pass.cs:977, :531, :588), which are native block copies - cheaper but still per draw. Not a GPU item; listed because 200 fps = 5 ms total.

---

## 3. Specific questions

**Classify pass resolution:** full res, RG32F, never scaled - B5. The solve consumes it at `FogSolveScale`; the meniscus at full res.

**Ocean-surface depth prepass re-draw set:** two sets per armed frame. (1) Ownership prepass: fog source's `surfaceAbove + patch + N clipmap-above levels` = 11 draws at default settings, half res, MRT R32F+RG8+D32, WaterSurface pass 1. (2) VisibleWaterSurfaceDepth: ALL bodies' above sheets + river surfaces = 11+ draws, FULL res, R32F+D32, WaterSurface pass 3, unread on oceans (B1). Under twins are not drawn (correct, :296-300). Mesh source: `MeshFilter.sharedMesh` per renderer (:975-978), fetched via `GetComponent<MeshFilter>()` per renderer per draw per frame (a native lookup, ~22 per frame - minor; cache the filter beside the renderer).

**Lens / height RT fan:** 2 RTs + 2 depth buffers, both 256x256 (`HeightRtResolution` :77, `LensHeightRtResolution` :91), R16F and RG16F + Depth32 each. Height grid 74.5k verts / 148k tris; lens grid ~2.4k verts. Two render passes. Both recorded from the same `_heightRtMaterial` with `s_SurfaceRenderers[0]`'s property block (:530-534, :587-591). Merge candidate (B8).

**God-ray march:** `steps = clamp(_LargeGodRaySteps, 1, 64)`, default 24, half res, jittered, temporal 0.88 (LargeBodyAtmospherePass.cs:49). Per step: 1 cascade shadow fetch + 1 optional caustic sample + 2 exclusion loops (zero-trip with no volumes) + exp + ALU; lamp variant adds an 8-lamp inner loop with `atan`. Gated: tier `GodRays`/`GodRaySteps` (WaterQuality.cs:78-79, :106), probe G, `LargeGodRayDensity > 0`, dry-camera ONLY above the envelope (Feature.cs:388-392), per-pixel early-outs at :404-406 (Full: above band), :442 (dry lens, from-air off), :519 (in air, no exclusions), :548. Inside the arming band the march is a per-pixel early-out (cheap) but the full-res composite is not (B3). And the whole pass is unoptimised on WebGPU (B4). The history copy (#12) runs every game-camera frame regardless.

**"Double fog":** I found no pixel that receives the fullscreen path fog twice at Full tier:
- Underside sheet: applies `UnderwaterViewTint()` (fixed 1 m boundary tint, WaterFog.hlsl:42-46, explicitly "must NOT re-integrate depth") and skips its camera-depth downwelling while `_UnderwaterFogArmed` (WaterSurfaceFragStages.hlsl:975-981); the fullscreen PREPASS_WET branch fogs [eye -> sheet] (:639-652). Complementary, not double.
- From-air sheet pixels: fullscreen span forced to 0 (PREPASS_AIR :630-637) so the sheet's own from-above absorption owns them - except the uncorroborated 1-px silhouette rows, which deliberately fall through to PREPASS_WET (documented :622-629): those rows get sheet absorption + fullscreen [eye -> sheet] fog. Intentional over-cover, 1-2 px.
- God rays: apply their own per-step Beer-Lambert (:690-691, :846) and inject at +1 so the absorb pass cannot multiply them (Atmosphere.cs:33-39). Correct.
- Sprites / foam overlay / user transparents: drawn at +2 after the fog with their own flat-waterline fog (WaterParticleFog.hlsl:47-61, WebGpuWaterFogAPI.hlsl:118-150). The fullscreen fog integrates to OPAQUE depth behind them; the sprite overwrites with its own [eye -> sprite] fog. Correct by construction, but note the approximation gap: the API's downwelling uses the fragment depth (WebGpuWaterFogAPI.hlsl:47-52) while the fullscreen uses the transmittance-weighted mean depth (:1288-1299), so a prop and the fog behind it can differ in darkness - a look item, not double fog.
- Bounded body seen through a river/lake sheet: ownership decided by `_VisibleWaterSurfaceDepth` (:1097-1113). This is the correctness item the owner pre-scoped; from the code it reads correct (returns 0 when the sheet is nearer than the box entry), and it is the only legitimate consumer of the B1 texture.
- One genuine double-application candidate is the **exclusion wall** (WaterExclusionWall.shader:119, :397-402): it reconstructs fog itself while `_UnderwaterFogArmed < 0.5`, and `fogArmed` is published from the WIDE band, so inside the band but with the eye in air the wall stops self-completing and relies on the fullscreen pass - whose mask admits nothing above the line. Out of this audit's scope; flag for the exclusion reviewer.

**Simple/cheap mode as a UNIFORM in front of a big fenced path (the 07-29 cliff):** not present in WaterUnderwaterFog.shader (section 0). Remaining uniform-fronted paths and their size:
- `_WaterFogSolveScale < 0.999` (:1448, :1487) in front of the 4-tap upsample - small, acknowledged (:1443-1447). OK.
- `_OceanSurfaceDepthValid > 0.5` (:789) selecting `OceanPrepassPath` vs `OceanWavyPath` - both are needed in the Full variant anyway (the prepass path calls the wavy march for carves and no-sheet pixels), so no extra module. OK.
- `_WaterLensHeightRTFrame.w > 0.5` (:984) - only in the non-classify variant. OK.
- `WaterlineFarFromSurface` (:970, WaterUnderwaterWaterline.shader:112) - uniform in front of the 3-evaluation inversion; the module is compiled in regardless (it must be), the skip is a runtime win only. OK.
- `_WaterDebugMode` - B9 (small).
- **LargeBodyGodRays composite `_LargeGodRayFromAir > 0.0` (:1115)** fronts a 3-evaluation `SurfaceSignedGapChopInverted` (:1139) - B3.
- **LargeBodyGodRays raymarch `wantCaustic`** (:742, :784) - uniform in front of a per-step texture sample; fine, but `_LargeGodRayCausticStrength` is per body, so it could be a keyword. Minor.
- Point lights: keyword (`WATER_FOG_POINT_LIGHTS`, `WATER_GODRAY_POINT_LIGHTS`) - correct.
- WaterExclusionWall.shader:137/:158 still branch on the `_UnderwaterFogSimple` uniform (out of scope, flag).

---

## 4. Bad practices / redundancy

**Per-frame allocations in the passes:** none found. Lambdas capture only statics/consts (Pass.cs:427, :447, :478, :528, :585, :889, :959, :1061, :1105); lists and property blocks are static/readonly scratch (:161-166); grids are lazily created once (:607-621). `CalculateFrustumPlanes` uses the non-allocating overload (WaterVolume.Underwater.cs:446, :456). Good. Per-frame native calls that are avoidable: `renderer.GetComponent<MeshFilter>()` per surface renderer per prepass draw (Pass.cs:975; ~22 per frame); `renderer.GetPropertyBlock(block)` per draw (:977; native copy of ~170 properties, ~24 per frame).

**GetTemporaryRT vs RTHandle:** no `GetTemporaryRT` anywhere in Runtime/Rendering (grep). All fog RTs are RenderGraph transients (pooled by RG). The only persistent allocation is the god-ray history `RTHandles.Alloc` per game camera with resize/release handling (LargeBodyAtmospherePass.cs:212-239, :96-105). `ImportTexture(entry.Rt)` twice per frame for the same handle (:166, :177) is legal but creates two aliased imports per frame - harmless.

**Materials created per frame:** none. `CoreUtils.CreateEngineMaterial` only in `Create()` with release-before-create (WaterUnderwaterFogFeature.cs:37-42, Atmosphere/Caustic/Chunk/Exclusion features likewise). Material property mutation at execute time (`d.material.SetTexture` Pass.cs:893-894; Atmosphere.cs:300-305) is per frame but not an allocation.

**Shader.SetGlobal churn:** the fog pass writes 6-8 globals per camera per record (Pass.cs:259, :267, :276, :308, :320/:526, :341/:583, :923, :1037); the god-ray pass 1 texture global (Atmosphere.cs:157). Record-time globals with multiple cameras are only safe because URP records+executes each camera's graph in sequence (acknowledged at :1036). Fine at this count.

**Dead code compiled into release:** debug views (B9); LargeBodyGodRays blur passes 1+2 (never dispatched, :18-19, :956-1039) - 2 fragment programs; the `WATER_FOG_SIMPLE + WATER_FOG_POINT_LIGHTS` solve combos (2 of 12, never armed together per WaterUniformPublisher.cs:561-571) - could be excluded with `#pragma multi_compile ... ` restructuring or a `#if defined(WATER_FOG_SIMPLE) && defined(WATER_FOG_POINT_LIGHTS) #error`-style guard (actually the `#if defined(WATER_FOG_POINT_LIGHTS) && !defined(WATER_FOG_SIMPLE)` fence at :1611 already makes them byte-identical to the non-lamp Simple variant; Unity will still compile and ship both).

**Duplicated math (both copies cited):**
1. `OceanRenderedCoverage`: WaterOceanRenderedCoverage.hlsl:22-48 (with the F7/F9 flank-corroboration clamps) vs LargeBodyGodRays.shader:1098-1107 (the pre-F7 form, no corroboration). **The two have already drifted**: the god-ray mask can be pulled below/above analytic by an isolated ownership texel exactly the way F7/F9 fixed for the fog. CONFIRMED.
2. `OceanOwnershipSample`: WaterUnderwaterFog.shader:178-182, WaterUnderwaterWaterline.shader:63-67, LargeBodyGodRays.shader:1092-1096 (three identical copies; the include contract at WaterOceanRenderedCoverage.hlsl:14-15 asks each consumer to supply it).
3. The full waterline-classification block (classify point -> far-skip -> chop-inverted pair -> ddx/ddy -> `WaterlineCoverage` -> rendered coverage with screen direction): WaterUnderwaterFog.shader:963-993 + :1043-1061; WaterUnderwaterWaterline.shader:110-142; LargeBodyGodRays.shader:1165-1190 (no far-skip, no lens RT, no classify RT). Three copies; B3/B5.
4. Interleaved-gradient noise: WaterUnderwaterFog.shader:1372-1376 (`FogDither`) and LargeBodyGodRays.shader:242-245 (`InterleavedGradientNoise`) - identical constants.
5. Beer-Lambert transmittance / in-scatter: WaterFog.hlsl:27-32 (`ApplyWaterFog`), :113-119 (`ApplyWaterVolumeClarity`, which owns the `density * lerp(CLARITY_FOG_DENSITY_MAX, 1, clarity)` rule at :116); WaterUnderwaterFog.shader:1355-1356 re-derives that same density rule inline and :1587/:1607 the lerp; WaterParticleFog.hlsl:56-60; WaterFog.hlsl:309-311 (lamp legs); LargeBodyGodRays.shader:690-691 and :839-840. Six sites for one formula; the fullscreen fog is the one that does not call the shared helper.
6. Flat waterline path: `WaterPathLength` (WaterFog.hlsl:205-215) vs the god-ray Simple exit `camGap / rayDir.y` (LargeBodyGodRays.shader:508) vs `OceanFlatPath`'s inline crossing (:733-738).
7. Scene position from depth: `SceneWorldPos` computed twice per solve pixel (:1145, :1598) + a third depth sample (:1637); god-ray composite reconstructs `nearWorld` (:1165) exactly as `WaterlineClassifyPoint` (:930).
8. Per-frame constants evaluated per pixel: god-ray raymarch `GodRaySurfaceY(_WorldSpaceCameraPos)` (:407) - the fog moved the identical value to the CPU-published `_UnderwaterSurfaceY` for exactly this reason (:342-349) but the god-ray file rejects that scalar as stale (:144-150); `WaterlineFarFromSurface` samples the height RT at the camera xz per pixel (WaterWaterline.hlsl:262-263) in classify, solve and meniscus. One tap each, but three passes.

**Stale comments that will mislead the next reader:** WaterUnderwaterFogPass.cs:279-281 (`_UnderwaterFogSimple` uniform test - now a keyword); :852 ("few straddle frames"); WaterUnderwaterFog.shader:16-18 header ("U3: _UnderwaterFogSimple, a uniform so every pixel takes the same branch"); LargeBodyAtmosphereFeature.cs:379-380 ("coverage mask costs ~6 surface fetches per pixel" - it is 4 field evaluations = 16-96 reads, B3).

---

## 5. What to A/B first (existing runtime toggles only)

| Key / knob | Isolates | Notes |
|---|---|---|
| WaterCostProbe **G** | whole god-ray pass (#11-#13) | if G alone recovers most of the Full-tier gap, B3 + B4 are the story |
| WaterCostProbe **F** Full -> Simple | #2-#7 fan + analytic solve | cannot separate prepass from solve; combine with R |
| WaterCostProbe **R** 1 -> 0.5 -> 0.25 | solve pixel count only (#7) | classify (#6) and blend (#8) stay full res, so a small delta here means the solve is NOT the bottleneck any more and the fan / pass count is |
| Inspector `Meniscus Warp` = 0 | the #9 copy | live, no rebuild (Pass.cs:862-863) |
| Inspector `Meniscus` off | #10 (+#9) | live |
| Inspector `Large God Ray Steps` 24 -> 8 | march trip count | live in editor; in a build only via tier |
| Delete/disable exclusion volumes | all `_ExclusionCount` loops + re-enables `WaterlineFarFromSurface` | any volume in the scene forces full classify (WaterWaterline.hlsl:261) |
| No existing toggle | B1 (visible-depth prepass), B5 (classify res), height/lens RT, prepass scale | these need a probe key or a one-line gate edit + rebuild |

Recommended order by expected payoff / risk: B1 (delete a full-res 11-draw pass with no reader - zero visual risk), B3 (composite reads the classify RT - zero visual risk when the RT is valid), B4 (rebuild with the pragma scoped - measure with G first), B5/B6 (resolution and arming-band changes - visual verification needed), then B2 (grid density / level culling - visual verification needed).
