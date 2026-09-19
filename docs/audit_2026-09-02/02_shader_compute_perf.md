# 02 - Shader + compute perf / code-quality audit (surface stack, waves, sims, particles)

Scope: `Runtime/Shaders/*` EXCEPT the fog stack (WaterUnderwaterFog.shader, WaterFog*.hlsl,
LargeBodyGodRays.shader, WaterParticleFog.hlsl, WebGpuWaterFogAPI.hlsl). Read-only. Every count
below was made by reading the call chain; "CONFIRMED" = every consumer read + grep-verified,
"PLAUSIBLE" = structure confirmed but the runtime cost depends on compiler behaviour (CSE /
ternary lowering) or a runtime measurement. Fix text is DIRECTION only.

Tree: /tmp/ww (2026-09-02). Line numbers are from the files as they are today.

---------------------------------------------------------------------------------------------------
## 0. Ranked findings (most valuable first)

| # | Finding | Status | Where | A/B |
|---|---------|--------|-------|-----|
| 1 | **4x wind-wave field per fragment AND per vertex where 1x is needed.** `EvaluateSurfaceGeometry` calls `WaveSlope()` four times unconditionally (grid, outflow, riverA, riverB); the vertex calls `WaveHeight()` four times. Each is a `[loop]` over `_WaveCount` (up to 16) sinusoids + a 4-`sincos` group envelope. On every pool/lake/ocean pixel three of the four are multiplied by 0 (`outflowInfluence`, `riverWeight`) or discarded by the `_IsRiver` branch. | CONFIRMED (structure) / PLAUSIBLE (compiler cannot fold: the loop trip count and the mouth-outflow drift are uniforms, so no CSE) | WaterSurfaceFragStages.hlsl:132-158 (`WaveSlope(gridWaveSample), WaveSlope(outflowWaveSample)` line 141; `WaveSlope(riverWaveSampleA), WaveSlope(riverWaveSampleB)` line 150); WaterSurfaceVertStage.hlsl:375-385 (`WaveHeight` x4). WaterWaves.hlsl:486-520 loop bodies. | Set Wind Waves count 16 -> 1 on the body (WaterQuality `MaxWaveCount` tier knob, or WaterVolume wind-wave count) and watch fps; the delta is 4x what one bank costs. Direction: gate river A/B on `_IsRiver` and the outflow sample on `_MouthOutflowCount > 0.5` (both uniform, pure ALU, derivative-safe) - the code already has the `if (_IsRiver > 0.5)` select at line 155, just move the evaluations inside it. |
| 2 | **Detail-normal ladder sampled twice per pixel (8 tex2Dgrad instead of 4; 24 vs 12 with hex tiling)** - the "mouth outflow drift" twin is evaluated unconditionally and lerped by `MouthOutflowCurrentInfluence`, which is 0 on every body with no connected river mouth. Rivers pay 16 (2 river phases + 2 body). | CONFIRMED | WaterSurfaceFragStages.hlsl:290-296 (pool path: `DetailNormalTilt` x2), 276-282 (river: `RiverDetailNormalTilt` + 2x `DetailNormalTilt`); WaterSurfaceDetailNormal.hlsl:205-220 (2 taps/octave x 2 octaves = 4 per call; 155-175 hex x3). | Toggle Detail Normal Strength 0 vs 0.6 on the volume (uniform gate at FragStages:249 skips the whole block) to price the block, then halve. Direction: `if (_MouthOutflowCount > 0.5)` (uniform) around the outflow twin; both taps use explicit gradients (`tex2Dgrad`) so the uniform branch is WGSL-legal. |
| 3 | **FFT cascade normal sum evaluated up to 4x per pixel (16 `Texture2DArray.SampleLevel` where 4 suffice; with aperiodic tiling 48 + 192 `Load`s).** `OceanFftNormalSumShore` is called by (a) `OceanFftNormalTiltShore` for the normal, (b) `OceanFftJacobianShore` for geometry foam, (c) `OceanFftFoam` for whitecap coverage (which also re-runs `ShoreSample`), (d) `OceanFftJacobianShore` again for crest glow - all at the SAME `largeWaveSourceXZ` with the same shore sample. The struct already returns tilt+pinch+foam together; the callers throw two of three away each time. | CONFIRMED (4 call sites) / PLAUSIBLE that the compiler does not CSE across them (each call is a 4-iteration loop with a per-iteration uniform ternary - see #4). | WaterLargeWaves.hlsl:567-606 (sum), 614-617, 631-640 (`OceanFftFoam`: `ShoreSample` AGAIN at 635), 646-653; call sites WaterSurfaceFragStages.hlsl:218->WaterLargeWaves.hlsl:795 + 802, FragStages:363 (`OceanFftFoam`), FragStages:1062 (`OceanFftJacobianShore`), FragStages:939 (underside, keyword). | Compare an FFT ocean frame with `_SssEnabled` 0 (removes call d) and Ocean Foam strength 0 (call c still runs - coverage is computed before the `coverage > eps` test). Direction: sample `OceanFftCascadeSum` ONCE in `EvaluateSurfaceGeometry`, store tilt/pinch/foam in `WaterGeomStage`, and have whitecap coverage + crest glow read the struct. |
| 4 | **Aperiodic-tiling ternary may execute BOTH paths per cascade (the 07-29 "uniform in front of a big path" shape).** `_OceanAperiodicParams.x > 0.5 ? OceanAperiodicNormal(...) : SampleLevel(...)` inside the 4-cascade loop. The aperiodic side is 3 array taps + 3x direction-map bilinear (12 `Load`s) + 3 `atan2` + 3 `sincos`. This codebase's own comment (FragStages:1119) states "an HLSL ternary evaluates both lanes". If that holds on the WebGPU toolchain, every non-aperiodic ocean pixel pays ~15 fetches per cascade per sum (x4 sums, #3) for a value it discards. | PLAUSIBLE (toolchain-dependent; `?:` on a uniform is not guaranteed to lower to a branch) | WaterLargeWaves.hlsl:530-532 (displacement, vertex) and 590-592 (normal, fragment); the aperiodic bodies 455-493; direction map 417-436. Same shape in OceanFft.compute bake / foam compute (grep `_OceanAperiodicParams.x > 0.5`). | Enable/disable `oceanAperiodicEnabled` on the ocean: if fps does not move when it is OFF vs the taps being present, the ternary is a branch; if OFF costs the same as ON, both lanes run. Direction: rewrite as `[branch] if (_OceanAperiodicParams.x > 0.5) {...} else {...}` (uniform, explicit-LOD -> WGSL-safe). |
| 5 | **Horizon haze costs 21 texture fetches per ocean pixel (10 opaque + 10 depth + 1 cube) for a colour that is a function of azimuth only.** `SampleHorizonSky` is a 5-tap blur where every tap also reads depth (`HorizonSkyWeight`), and it is called twice (per-azimuth + centre column). Runs on every pixel of every body with `_HorizonHazeDensity > 0` AND `_RealRefraction`. | CONFIRMED | WaterSurfaceFragStages.hlsl:1820-1825 (`HorizonSkyWeight` = depth tap), 1856-1878 (5 opaque + 5 depth), 2016 + 2023 (two calls), gate 1918/1950. | Set Horizon Haze density 0 on the ocean and read the fps delta. Direction: bake the horizon sky band once per frame into a tiny 1D LUT (e.g. 256x1, one draw) indexed by azimuth, or evaluate the two `SampleHorizonSky` calls in the VERTEX stage of the clipmap and interpolate (the value is low-frequency by construction). |
| 6 | **Pond foam coverage evaluated twice per overlay fragment** (`PondFoamCoverage` = contact depth tap + 4 foam-mask taps + mouth loop): once for the early clip, again inside `PondFoamLayer`. | CONFIRMED (two calls) / PLAUSIBLE savings (tex2Dlod + identical args; a compiler MAY CSE) | WaterSurface.shader:540 and WaterSurfaceFragStages.hlsl:1416 (via 543). | Direction: pass the clipped coverage into `PondFoamLayer` as a parameter (Pass 0 computes it there anyway). |
| 7 | **Ocean-FFT height-field bake + `GenerateMips` on a Texture2DArray run every dispatch, whether or not anyone will read them.** `BakeHeightField` (128^2 dispatch into a 256 KB RGBAFloat RT) is unconditional at every `Dispatch`; only the readback is demand-gated (`ReadbackDemandWindowFrames`). The normal-array `GenerateMips` runs every frame; the fragment uses `SampleLevel` with a distance LOD, so it is a real consumer, but the whole mip chain is regenerated even on interval frames. | CONFIRMED (bake unconditional) | WaterOceanFft.cs:664-682 (bake), 661 (mips), 706-714 (readback demand gate). Cadence: WaterVolume.Update.cs:93 (`Time.frameCount % _oceanFftInterval`). | Direction: gate the bake on the same demand window the readback uses (`_lastReadbackDemandFrame`). |
| 8 | **Foam particles: full-capacity draws with no indirect args, 3-4 passes per body per frame.** Every pass draws `_capacityPow2 * 6` vertices; the vertex shader loads the particle, checks life + kind + `OceanTerrainSwashVisible` (2 shore taps when clip-to-terrain is on) and returns a degenerate quad. Default capacity 4096 -> 24.6k vertices x 4 passes; a 65536 cap -> 1.57M vertex invocations per body per frame of which most are dead. Update + RasterizeDensity also dispatch over full capacity. | CONFIRMED | WaterFoamParticles.cs:1137, 1166, ~1200, 1223 (RenderPrimitives), 1248-1265 (after-fog re-submit), 929 (Update), 1007 (RasterizeDensity); FoamParticles.shader:431-456 (dead exits); WaterQuality.cs tooltip line 213 "all capacity is drawn every frame". | Set `highMaxFoamParticles` 65536 -> 4096 on the tier; if the delta is measurable with few particles alive, the draws are the cost. Direction: the ring cursor / counters already exist (compute:578); a compact-alive-list + `DrawProceduralIndirect` per kind, or at minimum size the draws by `min(capacity, cursor)`. |
| 9 | **Patch-rim vertex morph evaluates the full base-sheet vertex THREE times** (`InterpolateBaseSheetTriangle` -> 3x `SampleBaseSheetVertex` -> 3x bicubic ripple (16 taps each) + 3x `DisplaceSurfaceVertex` (4x `WaveHeight`, `ShoreSample`, `EvaluateSurfWaves`, cascade fetch)). Per-vertex non-uniform `if (baseMorph > 0.0)` so only rim vertices run it, but the vertex program is sized for it in all 4 surface passes. | CONFIRMED (structure) | WaterSurfaceVertStage.hlsl:509-566, call 671-679. | Direction: fetch the coarse-grid corner heights from the sim/height RT (the base sheet is itself a rasterised surface; `_WaterHeightRT` exists) instead of re-running the displacement three times. |
| 10 | **Sim-window edge fade copied 5 times** (SampleRipple, FoamWindowFade, FoamParticles.shader, WaterChunkWall.shader, WaterFoamParticles.compute) with one drifted form (compute divides by `_Size`, the others by `_WaterTexel.x`). | CONFIRMED drift | WaterSurfaceVertStage.hlsl:100-102; WaterFoamMask.hlsl:70-75; FoamParticles.shader:203; WaterChunkWall.shader:137-139; WaterFoamParticles.compute:758. | Direction: one `SimWindowFade(uv)` in WaterVolume.hlsl (it already owns `_SimEdgeFadeTexels`). |
| 11 | **Caustic occluder PCF gather + submerged-ground lighting block hand-copied in 5 shaders**, two drifted (Receiver vs Terrain differ in gate variable and specular-gain structure). | CONFIRMED (diffed) | WaterCommon.hlsl:161-167, AnalyticPool.shader:155-161, WaterCausticProjection.shader:175-181, WaterReceiver.shader:296-302 (+lighting 255-330), WaterTerrain.shader:352-358 (+lighting 310-385). | Direction: a `CausticOccluderLitPCF(tex, sampler, cuv, poolY, centreGreen)` in WaterShared/WaterCausticMap taking the sampler pair (both CG `tex2Dlod` and URP `SAMPLE_TEXTURE2D_LOD` can wrap it). |
| 12 | **Dead `#if` path: `WATER_DISABLE_OCEAN_APERIODIC` is never defined anywhere** (grep: only the `#if defined` tests exist). Five preprocessor forks in WaterLargeWaves.hlsl compile only their `#else`. | CONFIRMED | WaterLargeWaves.hlsl:18, 369, 386, 526, 587. | Delete the forks or wire the define into a keyword (it would also be the natural fix for #4). |
| 13 | **Seven C#<->HLSL mirrored constants are NOT in WaterWaveConstantsValidator**: `WATER_MAX_MOUTH_OUTFLOWS`(4) <-> `WaterVolume.MaximumShaderOutflows`; `RIVER_DISTURBANCE_MAX_SOURCES`(8) <-> `WaterRiverDisturbance.MaximumSourceCount`; `RIVER_CASCADE_TRANSPORT_SAMPLE_COUNT`(256) <-> `WaterRiverFoam.CascadeTransportResolution`; `PEAKED_REFINE_MAX_STEPS`(8) <-> `WaterQuality.MaxRefineSteps`; `OCEAN_FFT_TG`(8) <-> `WaterOceanFft.ThreadGroupSize`; `OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION`(0.0625 = 0.25/`CascadeTileOversample`); `KIND_SPRAY/KIND_BUBBLE/KIND_RIPPLE_CREST` (compute + FoamParticles.shader, "MUST match" comments) <-> `DrawKindSpray/Bubble`. | CONFIRMED (grep of Editor/WaterWaveConstantsValidator.cs for each name = 0 hits) | WaterWaves.hlsl:96; WaterSurfaceVertStage.hlsl:173; WaterSurfaceFoamSampling.hlsl:171; WaterSurfaceFragStages.hlsl:42; OceanFft.compute:114; WaterShared.hlsl:96; WaterFoamParticles.compute:56-58, FoamParticles.shader:79-81. C# pairs: WaterVolume.Currents.cs:52, WaterRiverDisturbance.cs:14, WaterRiverFoam.cs:31, WaterQuality.cs:28, WaterOceanFft.cs:163-165, WaterFoamParticles.cs:260-261. | Add pairs. A too-small HLSL array is a silent over-run on `SetVectorArray`. |
| 14 | **Sampler + sampled-texture register pressure in WaterSurface Pass 0.** Fragment samplers referenced = 16 exactly (13 CG samplers + `sampler_CameraOpaqueTexture` + `sampler_PointClamp` + `sampler_OceanFftNormal`) - matches the file's own "at the cap" comments. Sampled TEXTURES referenced in the same stage = 23 (+1 in shadow variants): the 16 above plus `_LargeGodRayLastFrame`, `_SeaStateFetchTex`, `_OceanDirectionMap`, `_ExclusionMeshFront/BackDepth`, `_ChunkFogFront/BackDepth`, `_MainLightShadowmapTexture`. WebGPU's BASE limit `maxSampledTexturesPerShaderStage` is 16; the pass runs today, so Unity is requesting the adapter's higher limit - a portability cliff on adapters that only offer the base (compat mode / some mobile). | CONFIRMED count / PLAUSIBLE risk | WaterSurfaceScreen.hlsl:19-26, WaterSurfaceSpecular.hlsl:122, WaterSeaStateFetch.hlsl:4, WaterLargeWaves.hlsl:360-370, WaterExclusionMesh.hlsl:27-28, WaterSurface.shader:191-192, WaterSurfaceShadow.hlsl:21. | Direction: the chunk-only and exclusion-mesh-only depth pairs are the obvious keyword candidates (they are per-body/uniform gated today). |
| 15 | **Debug taps compiled into the production pass.** `WaterDebugColor` is uniform-gated (`_WaterDebugMode < 0.5` early return) but its 8 explicit-LOD taps (5x `_WaterTex`, 2x `_PlanarReflectionTex`, ...) size the register allocation of every variant - the exact concern the file's own WATER_UNDERSIDE_FOAM comment (WaterSurface.shader:123-129) documents. | CONFIRMED (8 taps) / PLAUSIBLE cost | WaterSurfaceDebug.hlsl:57-68, 151, 227, 235; call WaterSurface.shader:327. | Direction: `#pragma multi_compile_fragment _ WATER_SURFACE_DEBUG` (or shader_feature) so shipped variants drop it. |
| 16 | **Full 1 MiB height readback per body per frame** (`WaterSurfaceSampler` reads the whole 256^2 RGBAFloat sim texture at `readbackInterval` 1, demand-gated by `TrySamplePoolSurface` stamps). On a 4-body scene with buoyancy that is 4 MiB/frame of GPU->CPU traffic + a managed `CopyTo` of 65k `Color`s. | CONFIRMED (structure) / PLAUSIBLE cost (needs a browser trace) | WaterSurfaceSampler.cs:50-69, WaterVolume.Update.cs:136-139; tier knob `ReadbackInterval` (WaterQuality.cs:80). | A/B: `highReadbackInterval` 1 -> 4. Direction: read back a camera-centred sub-rect or a half-res copy; the FFT path already does this with its 128^2 bake. |
| 17 | **Chunk wall waterline solve: 8 march steps + 6 bisections per crossing, each step a full surface-height evaluation** (`WaveHeight` 16 sines + `LargeBodyWaveHeight` (shore 2 taps + surf + 4 cascade taps or 16 Gerstner) + bicubic ripple 16 taps). Worst case ~50 evaluations x ~22 taps per chunk-wall fragment, plus a 12-step god-ray march. Niche (chunk bodies) but a large per-pixel cliff on the screen area of the shell's back faces (drawn ZTest Always). | CONFIRMED (structure) | WaterChunkWall.shader:95, 99, 110 (`CHUNK_WATERLINE_BISECT_STEPS 6`, `MARCH_STEPS 8`, `GODRAY_STEPS 12`), 148-162, 281-333, 424-437. | Direction: march the cheap vertical read (`_WaterHeightRT` / wind-only) like the fog's "the marches keep the cheap vertical read" rule (WaterWaterline.hlsl:175), refine only the final crossing with the full field. |

---------------------------------------------------------------------------------------------------
## 1. Per-pixel cost picture: WaterSurface.shader Pass 0 at FULL quality

Scenario for the counts: High tier (`RefineSteps` 5, `MaxWaveCount` 16, RealRefraction on, rich
reflections on), unbounded FFT ocean with shore+surf field baked, foam on, detail normals on (no
hex tiling), horizon haze on, above-water twin (`_Underwater` = 0), no aperiodic tiling, no
exclusion volumes, no river/mouth. "tap" = one texture instruction. All counts are from reading the
code; nothing here is a measurement.

### 1.1 Vertex (per vertex; runs identically in Pass 0/1/2/3 - the shared VertStage)
| Block | Taps | Lines |
|---|---|---|
| `SampleRipple` bicubic (4 bilinear x 4) | 16 `_WaterTex` | VertStage:80-109 -> WaterCommon.hlsl:62-90 |
| `SampleRiverFluidVelocity` (gated `_RiverFluidActive`) | 0-1 | VertStage:323-330 |
| `DisplaceSurfaceVertex`: 4x `WaveHeight` (see finding #1) | 0 taps, ~4x(16 sin + 4 sincos) | VertStage:375-385 |
| `ShoreSample` + `EvaluateSurfWaves` (gated `_LargeBody`) | 2 (`_ShoreDepthTex`, `_ShoreSDFTex`) + surf ALU (2x front terms with cosh chain) | VertStage:417-420 |
| `OceanFftDisplacementShore`: 4 cascades (x `SeaStateFetchWeight` 4 Loads each when fetch field valid) | 4 (+16 Loads) | WaterLargeWaves.hlsl:511-537 |
| Swash deposit hold (gated) | 4 `_FoamMask` | VertStage:475-479 |
| Patch rim morph (rim vertices only, finding #9) | 3x (16 + 2 + 4) | VertStage:522-566 |
| `MouthOutflow*` loops x3 (count 0 -> loop skipped) | 0 | WaterWaves.hlsl:112-262 |
**Typical ocean vertex: ~22 taps + ~100 transcendentals.** Pool vertex: 16 taps + 4x wind bank.

### 1.2 Fragment stage-by-stage
| Stage | Taps (typical / worst) | Notes / lines |
|---|---|---|
| Pre-shading discards | 0 (+7 with `_ClipOceanToTerrain`: 1 depth + 2 shore + 4 foam in `OceanTerrainSurfaceVisible`) | WaterSurface.shader:201-280; FoamSampling:51-70. Exclusion loops are `[loop]` over `_ExclusionCount` (0 = skipped). Mesh-exclusion / chunk paths: 2 `Load`s each, uniform-gated. |
| `EvaluateSurfaceGeometry` - ripple | 16 + 5x4 + 16 = **52** `_WaterTex` | FragStages:89-120 (the overlay-pass comment at WaterSurface.shader:534 says "~52", matches) |
| - wind slope | 0 taps, **4x** `WaveSlope` (finding #1) | FragStages:132-158 |
| - shore + surf (hoisted once) | 2 | FragStages:196-203 |
| - `ApplyLargeBodyWaveNormalFoamShore` (FFT) | 4 tilt + 4 jacobian (when `foamGate > 0`) (+ `SeaStateFetch` 4 Loads/cascade, `SeaStateMssScale` 8-12 hash sins) | WaterLargeWaves.hlsl:793-808 |
| - detail normals | **8** (finding #2); 24 with hex | FragStages:249-299 |
| `EvaluateWaterClarity` | 1 `_BedTex` | FragStages:323-330 |
| `ReflectionStage` | planar: 5 `tex2Dlod` OR sky: 1-5 `texCUBElod`; SSR: up to `_SSRMaxSteps` (default 24, range 8-64) depth `SampleLevel` + 5 opaque on hit | Specular.hlsl:269-286, 365-389, 177-206 |
| `RefractionStage` | `GetSurfaceRayColor`: large body 0 / pool 1 `_Tiles` + 4 `_WaterTex` + 1 caustic + 4 PCF (+1 shadow); real refraction: 1-2 depth + 1 opaque; `WATER_FOG_POINT_LIGHTS`: 8-light ALU loop | FragStages:1139-1272; PoolTrace.hlsl; WaterCommon.hlsl:139-177 |
| `EvaluateCrestGlow` | 4 cascade (finding #3) | FragStages:1057-1068 |
| `OceanWhitecapLayer` | `OceanFftFoam`: 2 shore + 4 cascade; pattern 2 (4 flipbook); tilt 3 (6) = **11** | FragStages:340-422, 1278-1330; FoamSampling:429-461, 482-505 |
| `PondFoamLayer` | contact depth 1 + 4 `_FoamMask` + `EvaluateFoam` 5 pattern samples (x2 flipbook) (+ a second `EvaluateFoam` when outflow influence > 0) = **10-20** | FragStages:1339-1457; FoamSampling:293-369 |
| `SurfWhitewashLayer` | LUT 1 + pattern 2 + tilt 3 = 6 | FragStages:427-500, 1462-1490 |
| `ShorelineStage` | 1 `_BedTex` + 4 foam (deposit) + 2 swash pattern = 7 | FragStages:1585-1800 |
| `FinalCompositeStage` haze | **21** (finding #5) | FragStages:1918-2053 |
| Debug | 0 (uniform early-out; 8 taps compiled) | WaterSurfaceDebug.hlsl |
| Scene fog | 0 | |

**Above-water FFT-ocean pixel, all features on, SSR miss: ~ 52+2+8+8+1+5+3+4+11+10+6+7+21 = ~138
taps + ~200 transcendentals; with a 24-step SSR march ~167; with hex detail +16; with aperiodic
tiling x4 cascade sums (#3 x #4) up to +36 array taps +144 Loads.** Bounded pool pixel (no FFT,
no shore, no haze, pool refraction): ~52+8+1+5+11+10 = ~87 taps.

Of these, the CONFIRMED "paid for nothing in the common case" share is: 3x `WaveSlope` (ALU),
4 detail taps, 8-12 cascade taps (3 redundant sums), 5 foam-coverage taps in the overlay pass, and
the 21-tap haze which is heavier than needed rather than redundant.

### 1.3 Underwater twin (`_Underwater` = 1, separate draw, same program)
`UnderwaterStage` (FragStages:796-983): 1 cube (`SampleEnvironment`) + 1 `_LargeGodRayLastFrame`
+ `GetSurfaceRayColor` (pool: 1+4+1+4) + 1 opaque (real refraction) + 4 foam mask + 5-10
`EvaluateFoam` + [WATER_UNDERSIDE_FOAM: 2 shore + 4 cascade + 2 + 2 pattern]. Plus the ENTIRE
`EvaluateSurfaceGeometry` above (52 + 8 + 8...), i.e. the underside pays the full ripple refine and
FFT normal even though it draws a darkened ceiling. `ReflectionStage`/`RefractionStage`/foam/haze
are skipped by the `_Underwater` uniform branch at WaterSurface.shader:287 - compiled in, not run.

### 1.4 Branch structure
- Keyword fences (compile-time): `_MAIN_LIGHT_SHADOWS(_CASCADE)`, `WATER_UNDERSIDE_FOAM`,
  `WATER_FOG_POINT_LIGHTS`, `WATER_FOAM_OVERLAY_PASS` (define, Pass 2), `multi_compile_fog`.
- Uniform branches (compiled in, skipped at runtime): `_Underwater`, `_IsRiver`, `_LargeBody`,
  `_OceanFftActive`, `_SurfActive`, `_UseBedDepth`, `_RealRefraction`, `_UsePlanar`, `_UseSSR`,
  `_FoamEnabled`, `_HorizonHazeDensity`, `_SssEnabled`, `_DetailNormalStrength`, `_ChunkUseMesh`,
  `_ChunkSphereClip`, `_ExclusionMeshCount`, `_WaterDebugMode`, `_DetailNormalHexTiling`,
  `_OceanAperiodicParams.x` (as a TERNARY - finding #4), `_ShoreSwashDepositGain`.
- Per-fragment (dynamic): coverage > `FOAM_MASK_EPSILON` tests around each foam engine, `swash`
  bands, SSR early exits, `baseMorph > 0` (vertex).
- Loops and trip counts: ripple refine `clamp((int)_PeakedRefineSteps, 0, 8)` (FragStages:103);
  `WaveHeight`/`WaveSlope` `(int)_WaveCount` (<= 16, C#-clamped, not shader-clamped);
  `EvaluateMouthOutflow` `clamp(count,0,4)` x ~9 call sites per fragment (0 iterations when no
  mouth); exclusion `[loop]` over `_ExclusionCount` (<= 4) x 1-2 per pass; cascade loops fixed 4;
  aniso smear `[unroll] 5`; SSR `(int)_SSRMaxSteps` (C# Range 8-64); river disturbance
  `[unroll] 8` sources x 5 evaluations (gated `_IsRiver && _RiverDisturbanceActive`); scene lights
  `[loop]` <= 8 (keyword).
- `discard`: Pass 0 has 6 `discard`/`clip` sites before shading + 1 in `ShorelineStage`
  (FragStages:1633). All are early (cheap per fragment). Consequence to note: any discard in the
  program disables early depth WRITE, so the coincident twins (above/under, base/patch) cannot
  use each other's depth until the previous draw completes - both sheets pay their vertex + early
  discard at full rate. `ZWrite On` + `Blend Off` in the Transparent queue is deliberate and
  documented (WaterSurface.shader:97-100), not an oddity.

### 1.5 Variant counts (`#pragma` lines per shader)
| Shader | Pragmas | Fragment programs | Vertex programs |
|---|---|---|---|
| WaterSurface Pass 0 | `multi_compile_fog` (4) x `_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE` (3) x `_fragment _ WATER_UNDERSIDE_FOAM` (2) x `_fragment _ WATER_FOG_POINT_LIGHTS` (2) | 48 | 4 |
| WaterSurface Pass 1 (`OceanSurfaceEyeDepth`), Pass 3 (`WaterFogOccluderDepth`) | none | 1 each | 1 each (full VertStage) |
| WaterSurface Pass 2 (`PondFoamOverlay`) | `multi_compile_fog` | 4 | 4 |
| WaterReceiver / WaterTerrain / WaterTransparent ForwardLit | shadows (4) x soft (5) [+ `_AUTOTILEOBJSIZE` x2 on Receiver] | 20 / 20 / 20 (Receiver 40) | 1 |
| AnalyticPool ForwardLit | shadows (4) x soft (5) x `_USECUSTOMTILES` (2) x `_AUTOTILE` (2) | 80 | 1 |
| GodRays | shadows (3) | 3 | 1 |
| FoamParticles / SplashParticles / FoamDensityComposite | `multi_compile_fog` | 4 | 4 |
| WaterUnderwaterWaterline | `_ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT` | 3 | 1 |
| Everything else (Caustics, LargeBodyCaustics, WaterCausticProjection x2, WaterChunkWall, WaterExclusionWall, WaterHeightRT, depth passes) | none | 1 | 1 |
URP strips unused `_SHADOWS_SOFT_*` combos at build time, so the 20/80 numbers are the upper bound.
Note WaterSurface's `_MAIN_LIGHT_SHADOWS_SCREEN` omission is deliberate and documented (line 116).

---------------------------------------------------------------------------------------------------
## 2. Compute side

### 2.1 WaterSim.compute (interactive ripple)
- 14 kernels, `THREAD_GROUP_SIZE 8` (8x8) except the two reductions (`REDUCE_GROUP_THREADS 64`).
  Grid `_groups = Resolution/8` (256 -> 32x32 = 1024 groups). WaterSim.compute:31-34.
- Per frame per simulated body (WaterVolume.Solver.cs:48-191): `Scroll`+`ScrollFoam` (windowed,
  only when the window moves; WaterSimWindow.cs:114), `Drop` (only with queued drops), `SphereInteract`
  (only with interactors; ping-pongs the foam buffer too), `ObstacleSmooth`+`Obstacle`
  (FootprintDelta mode only), `Update` x `steps` (time-debt: `stepsPerFrame` 2 at 60 fps -> 2, at
  200 fps ~0.6/frame, cap `MaxSolverStepsPerFrame` 3), `ReduceMean`+`ReduceMeanFinal`+`Conserve`
  (when `conserveVolume && !bedActive`: 3 dispatches), `Normal` (1), `Foam` (1, once per frame), and
  every 30 frames `ReduceActivity`+`ReduceActivityFinal` + a 4-byte async readback
  (WaterSimulation.cs:909-954). Budget: `MaxSimulatedBodies` 4 -> ~7-9 dispatches/frame/body.
- Kernels are cheap (5-9 Loads per texel; Foam kernel 9 Loads + shore taps when the surf injection
  is active). Nothing per-frame is unbounded. Minor: `Normal` is a separate full-grid pass that could
  be folded into `Update`'s write (it only needs the +x/+y neighbours already read) - PLAUSIBLE
  saving of one 256^2 pass per frame.
- No `GetData` sync readbacks; the one sync point is `AsyncGPUReadback.WaitAllRequests()` at
  WaterVolume.cs:372 (teardown only).
- Readback: `WaterSurfaceSampler` full-texture RGBAFloat every `readbackInterval` frames when a
  buoyancy consumer stamped demand (finding #16).

### 2.2 OceanFft.compute
- Kernels: `SpectrumInit` (only on spectrum change), `SpectrumUpdate` (8x8 groups x cascades),
  `FftHorizontal{64,128,256}` (`numthreads(SIZE,1,1)`, 1 x res x cascades groups), `FftVertical*`
  (`numthreads(1,SIZE,1)`), `ComputeNormal` (8x8 x cascades), `BakeHeightField` (128^2, 16x16
  groups), `VisualizePreview` (dev builds only, `#if UNITY_EDITOR || DEVELOPMENT_BUILD`).
  OceanFft.compute:24-34, 114, 379-441; WaterOceanFft.cs:604-692.
- Cadence: every `_oceanFftInterval` frames (tier; 1 on High) when `IsOceanClipmap && !_paused`
  (WaterVolume.Update.cs:93). Per dispatch: 4 kernels + `GenerateMips` on the normal array + bake
  (+ preview in dev). Foam history ping-pongs (no copy). H0 rebuild throttled to 15 frames
  (WaterOceanFft.cs:281).
- Redundant work: finding #7 (bake unconditional). Readback of the 128^2 RGBAFloat bake = 256 KB,
  demand-gated to 12 frames (WaterOceanFft.cs:311, 709).
- Clear/copy churn: none found (no Blit/CopyTexture in the FFT path; the old Blit+GenerateMips mean
  was already replaced by the reduction - WaterSimulation.cs:883-903 comment).

### 2.3 WaterFoamParticles.compute + FoamParticles.shader
- Kernels: `BeginFrame` (tile counters, `TileCount/64` groups), `Spawn` (`SPAWN_THREAD_GROUP_SIZE`
  8x8 over the SIM grid: 256^2 = 1024 groups, early-outs on foam excess / LOD ticket / probability),
  `SpawnBurst` (`MAX_BURST_DROPLETS` threads x burst count), `Update` (`UPDATE_THREAD_GROUP_SIZE`
  64, `_capacityPow2/64` groups - full capacity), `ClearDensity` (screen/2 texels), `RasterizeDensity`
  (full capacity; up to 33x33 `InterlockedAdd` splat per near particle, `SURFACE_ATLAS_RADIUS_MAX`
  16). WaterFoamParticles.compute:22-27, 53-54, 300-311, 1177, 1540, 1742, 1764, 1836-1869;
  WaterFoamParticles.cs:845-1008.
- Draws: finding #8. Each surviving particle's 6 vertices each evaluate `EvaluateWaterSurface`
  (shore 2 + surf + cascade 4 + wind bank 16 sines + ripple bilinear 4) - 6x per particle because
  it is `SV_VertexID`-expanded, not instanced; bubbles evaluate it twice (refine) and corner
  vertices add `OpenWaterShortWaveHeight` (FoamParticles.shader:463-466, 497-500, 645).
- No `GetData`/readbacks. `_burstRequests.SetData` per frame only when bursts are pending.

### 2.4 Caustics (WaterCausticsPass.cs)
- One 1024^2 ARGB32 RT per body per `causticInterval` frame (High: every frame), cleared + drawn
  with a `causticGridResolution^2` grid mesh (WaterCausticsPass.cs:82, 104, 138-140); ocean path
  draws with `LargeBodyCaustics.shader` (9-wave analytic field `[loop]` in the vertex, 5 projections
  per vertex via `SampleLargeCausticOcean` = 5x(4+4) array taps). Up to `MaxCausticProjectionBodies`
  (4) fullscreen projection draws per camera + optional refracted-shadow draw each.

---------------------------------------------------------------------------------------------------
## 3. Redundancy (duplicated math blocks)

| Block | Locations | Identical? |
|---|---|---|
| Sim-window edge fade (`band = _SimEdgeFadeTexels * texel; fade = saturate(min(d)/band)`) | WaterSurfaceVertStage.hlsl:100-102; WaterFoamMask.hlsl:70-75; FoamParticles.shader:203-206; WaterChunkWall.shader:137-140; WaterFoamParticles.compute:758-761 | 4 byte-equivalent, compute DRIFTED (`/ max(_Size,1)` vs `* _WaterTexel.x`; same value only while `_WaterTexel.x == 1/_Size`) |
| Windowed ripple sample (`SampleRipple` vs `ChunkRippleHeight`) | WaterSurfaceVertStage.hlsl:80-109; WaterChunkWall.shader:130-141 | Drifted (chunk returns `.r` only, no `.ba` fade) |
| Caustic occluder 4-tap PCF gather | WaterCommon.hlsl:161-167; AnalyticPool.shader:155-161; WaterCausticProjection.shader:175-181; WaterReceiver.shader:296-302; WaterTerrain.shader:352-358 | Byte-identical modulo sampler macro (CG `tex2Dlod` vs URP `SAMPLE_TEXTURE2D_LOD`) |
| Submerged-ground lighting (wet look -> caustic sample -> shadow -> spec -> downwelling -> caustic add) | WaterReceiver.shader:255-330; WaterTerrain.shader:310-385 | DRIFTED (diffed): gate var `waterMask` vs `insideBody`; spec gain uses `_Smoothness` vs blended `dryExponent`; comment blocks differ |
| Interleaved gradient noise | GodRays.shader:77-80; LargeBodyGodRays.shader:242-245; WaterChunkWall.shader:121-124; WaterUnderwaterFog.shader:1374 | Byte-identical constants, 3 function copies + 1 inline |
| `frac(sin(n*12.9898)*43758.5453)` hash | WaterLargeWaves.hlsl:134-137 (`LbwHash`), WaterSurfWaves.hlsl:250-253 (`SurfHash`), LargeBodyCaustics.shader:92 (inline), WaterLargeWaves.hlsl:175-178 (2D variant), WaterOceanAperiodic.hlsl (2D, different matrix) | Two validator-guarded copies (LBW_/SURF_) are intentionally separate; the LargeBodyCaustics inline literal is unguarded |
| Rotated 2nd-octave anti-tiling transform (cos30/sin30 2x2 applied to uv, ddx, ddy) | WaterSurfaceFoamSampling.hlsl:315-327 (`EvaluateFoam`) and 447-456 (`SampleOceanWhitecapPatternTiled`) | Byte-equivalent math, hand-inlined twice (no `RotateOctave(float2)` helper) |
| Two-phase bounded transport (`phaseA = frac(t*rate); phaseB = frac(phaseA+0.5); blend = abs(phaseA*2-1)`) | WaterWaves.hlsl:82-84; WaterSurfaceDetailNormal.hlsl:348-350; WaterSurfaceFoamSampling.hlsl:301-303 | Identical shape; two copies of the same `RATE 2.0 / WINDOW 0.5` constants (`RIVER_CURRENT_PHASE_*` WaterWaves.hlsl:71-72 vs `RIVER_DETAIL_PHASE_*` DetailNormal.hlsl:326-327, comment "same contract") - an intra-HLSL mirrored pair with no guard |
| Far-slope floor vector | WaterLargeWaves.hlsl:549 `OceanFftFarSlopeFloor`; WaterLargeCausticWaves.hlsl:41 `LargeCausticFftFarSlopeFloor` | Byte-identical values, two definitions (the caustic header is a deliberate compile-bounded copy of the cascade sum, WaterLargeCausticWaves.hlsl:1-8) |
| `OceanCurrentDrift` + `_OceanCurrentOffset` | WaterWaves.hlsl:49-57; WaterLargeWaves.hlsl:75-83; WaterLargeCausticWaves.hlsl:29-37 | Identical, include-guarded (deliberate) |
| Manual bilinear over a float RT | WaterCommon.hlsl:33-44 (`SampleWaterBilinear`); WaterFoamMask.hlsl:44-56; WaterSeaStateFetch.hlsl:16-29; WaterSim.compute:719-723; WaterLargeWaves.hlsl:426-434 | Same recipe, 5 copies, two API dialects (`tex2Dlod` vs `Load`) |
| `ShoreSample` + `EvaluateSurfSwash` pre-shading re-run | WaterSurfaceFoamSampling.hlsl:56-58 (`OceanTerrainSurfaceVisible`, top of every surface pass) vs FragStages:198-203 (geometry) and 1601-1604 (`ShorelineStage`) | Same args (`largeWaveSourceXZ`), evaluated up to 3x per fragment on clip-to-terrain oceans |
| `PondFoamCoverage` | WaterSurface.shader:540 and FragStages:1416 (Pass 2) | Same function, same args, twice (finding #6) |

---------------------------------------------------------------------------------------------------
## 4. Bad practices / hygiene

- Magic numbers not mirrored via the validator: finding #13 (seven pairs). Also unguarded
  intra-HLSL twins: `RIVER_CURRENT_PHASE_*` vs `RIVER_DETAIL_PHASE_*` (above),
  `OceanFftFarSlopeFloor` vs `LargeCausticFftFarSlopeFloor`.
- Dead `#if` paths: `WATER_DISABLE_OCEAN_APERIODIC` (finding #12). `WATER_APERIODIC_MAP_SAMPLER`
  is defined only by the fog shaders (WaterUnderwaterFog.shader:46, WaterUnderwaterWaterline.shader:26)
  - live. `WATER_STRIP_SHORE` lives only in fog/god-ray multi_compiles - live.
- Uniforms declared but never read: NONE in the surface include set (287 declarations checked
  by grep across the 27 files of the Pass 0 include chain; every one has >= 1 read, the five
  single-hit names `_CausticDepthFade`, `_CausticFrameMode`, `_ExclusionPrepassValid`,
  `_GodRayDepthFade`, `_WetMarkActive` are read by other shaders that include the same header).
  Unity strips unreferenced uniforms from the CB anyway, so this is a code-hygiene negative, not a
  perf one.
- Sampler registers: finding #14.
- Precision: the whole surface stack is `float`; `fixed4`/`half4` appear only on return types
  (WaterSurface.shader:197, WaterChunkWall). On WebGPU `half` is f32 unless `shader-f16` is
  negotiated, so there is nothing to win on the web target today; on Metal/Vulkan mobile the
  foam/detail tilt math and the aniso-smear loops are `half`-safe candidates. Not a WebGPU item.
- Unbounded loops: none. Every `[loop]` trip count is a uniform clamped either in-shader
  (`PEAKED_REFINE_MAX_STEPS`, `WATER_MAX_MOUTH_OUTFLOWS`, cascades) or in C# (`_WaveCount` <= 16 via
  `WaterWaveBank.MaxWaves`, `_SSRMaxSteps` Range 8-64, `_ExclusionCount` <= 4).
- `discard` in the surface shader: 6 early sites + `ShorelineStage` clip (section 1.4). Acceptable;
  the only structural cost is the loss of early-Z write across the coincident sheets.
- Alpha / ZWrite / queue: WaterSurface = Transparent queue + `ZWrite On` + no blend (documented,
  needed for the opaque copy). `PondFoamOverlay` = `ZWrite Off`, SrcAlpha blend, `ZTest LEqual`
  against Pass 0's depth (documented). WaterChunkWall = `Cull Front` + `ZTest Always` (documented).
  FoamParticles = Transparent+10, `ZWrite Off`. Nothing found that contradicts its own comment.
- `OceanFftNormalSumShore` per-cascade `SeaStateFetchWeight` (4 `Load`s) and `SeaStateMssScale`
  (8-12 hash `sin`s) run inside every one of the 4 redundant sums (finding #3 multiplier).
- `EvaluateMouthOutflow` is a 100-line function instantiated by 5 wrappers (WaterWaves.hlsl:222-275)
  and called ~9 times per fragment (FragStages:126, 134, 139, 279, 282, 292, 295, 375, 668, 891/1376)
  each returning ONE of its seven outputs. Zero-cost at count 0 but a compile-size/inlining-pressure
  item (the same pressure the file's own comment at FragStages:189-193 cites for hoisting the shore
  sample). Direction: evaluate once into a struct in `EvaluateSurfaceGeometry`.
- Register-sizing by uniform-gated big paths (the 07-29 shape) still present in Pass 0: river
  disturbance (8 sources x 5 evaluations, `[unroll]`, VertStage:288-321 / FragStages:178-179),
  river cascade transport 64-vector array (FoamSampling:175), chunk mesh clip + exclusion mesh
  (4 depth textures), debug taps (finding #15). None is a keyword.

---------------------------------------------------------------------------------------------------
## 5. How to A/B without a rebuild (WaterCostProbe keys + live knobs)

WaterCostProbe (Runtime/WaterCostProbe.cs) only exposes F (fog mode), G (god rays), R (fog solve
scale), H (hide) - no surface-side key. Everything in this report is a per-body UNIFORM published
every frame through the MaterialPropertyBlock (WaterUniformPublisher.WriteBodyUniforms), so the
A/B lever is the WaterVolume inspector / a 5-line debug script flipping the property in play mode:
- #1 wind bank: `MaxWaveCount` tier knob (WaterQuality.cs:78/191) or Wind Waves off.
- #2 detail normals: Detail Normal Strength 0 vs 0.6 (`_DetailNormalStrength`), Hex Tiling on/off.
- #3/#4 cascades: `_SssEnabled` off, Ocean Foam strength 0, `oceanAperiodicEnabled` on/off.
- #5 haze: Horizon Haze density 0.
- #7 FFT bake: `highReadbackInterval` 1 -> 8 (reduces the readback, NOT the bake - which is the
  point: if fps does not move, the bake itself is not the cost; the bake is ~1/16 of a cascade).
- #8 particles: `highMaxFoamParticles` 65536 -> 4096 (WaterQuality.cs:213).
- Ripple refine: `highRefineSteps` 5 -> 0 (prices 36 of the 52 `_WaterTex` taps).
- SSR: Reflection mode SkyOnly / Planar / SSR; `ssrMaxSteps` 24 -> 8.
- Real refraction: `highRealRefraction` off (also drops the opaque copy; the haze then takes the
  cube fallback, so measure haze separately first).
Suggested probe additions (one key each, all runtime-only like F/G/R): detail normals, horizon
haze, refine steps, reflection mode - they would answer #1/#2/#5 in one build.
