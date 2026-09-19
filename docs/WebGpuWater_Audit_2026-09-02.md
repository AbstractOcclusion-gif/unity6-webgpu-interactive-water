# WebGpuWater package audit — 2026-09-02 (ANALYSIS ONLY, no code touched)

Scope: `Packages/com.abstractocclusion.webgpuwater` — 240 C# files (~56k lines), 70 shader files (~24k lines). Five parallel reviewers (fog/render perf, shader+compute perf, runtime C#, river inventory, connections+wizard), then the top claims re-verified by hand against the tree. Detail reports (with every file:line) are in `docs/audit_2026-09-02/01..05_*.md`.

Legend: **CONFIRMED** = every consumer/call site read in the tree. **PLAUSIBLE** = structure confirmed, GPU/CPU magnitude needs a measurement. Every item has a fix DIRECTION only — nothing is written until GO.

---

## 0. Headline

1. The fog stack's biggest remaining costs are **not inside the solve** any more (C1 + half-res did their job). They are **around** it: a full-res prepass that oceans never read (B1), the god-ray composite redoing the waterline classification 4× per full-res pixel (B3), the god-ray march shipping unoptimised on WebGPU (B4), full-res classify regardless of solve scale (B5), and the arming band being ~5 m+ wide so "above water looking at water" pays the whole 13-pass chain (B6/arming).
2. The **surface shader** evaluates the wind-wave field 4× per fragment and 4× per vertex on every pool/ocean because the river/mouth twins sit before the `_IsRiver` uniform branch (S1) — the same "cheap mode is a uniform" family as the 07-29 cliff, just smaller per instance.
3. Runtime C# is unusually clean (0 dead blocks, 0 LINQ, 0 TODO, no per-frame allocs). The real CPU items are three hot paths that grew since the 08-29 domains round: per-droplet `WaterDomainResolver.Resolve` (C1), per-call `TRS().inverse` in exclusion containment (C2), brute-force ribbon containment (C3).
4. For the next three steps (river editor, connections, system window) the analysis is done and the recommendations are: **Option C** river editor (nested SerializedObjects in one facade editor + one wiring clean-up), **D1** connections (auto-generate/auto-sync/warn, no data-model change), and a **Wizard section** for the system window that replays the existing `CreateConnectedWatersRig` recipe as data.

---

## 1. Perf — fog / render stack (Full tier)

Frame map (submerged Full-tier ocean, no river): **11 raster passes + 2 copy passes** owned by this stack — VisibleWaterSurfaceDepth, ownership prepass (½×½), HeightRT (256², 74k-vert grid), LensHeightRT, Classify (full-res RG32F), Solve MRT (× fogSolveScale), Blend, WaterlineCopy, Waterline, GodRay march (½), history copy, GodRay composite (full), particles/transparents. Full table with record lines in `01_fog_render_perf.md §1`.

| # | Finding | Status | Where | Fix direction | A/B today |
|---|---|---|---|---|---|
| B1 | `_VisibleWaterSurfaceDepth` recorded every armed frame at FULL res (R32F + Depth32, `AllowPassCulling(false)`) by re-drawing every above-surface sheet of every body (~11 displaced draws) — its only readers are the river branch (`_RiverFogDepthValid`) and the bounded-body branch of `ArmWeight` after the `_UnderwaterUnbounded` early return. **Oceans never sample it.** Same pattern the file itself diagnosed for the Simple tier at :279-288. | CONFIRMED | `WaterUnderwaterFogPass.cs:264-266, 395-433`; readers `WaterUnderwaterFog.shader:247, 1097` | Gate on `riverFogRecorded \|\| (fogActive && !fogSource.IsOceanClipmap)`; when needed, allocate at `PrepassResolutionScale`. | none — needs a toggle |
| B3 | God-ray COMPOSITE (full res) computes `SurfaceSignedGapChopInverted` + `SurfaceSignedGap` at the near plane = 4 analytic field evals/pixel (16–96 texture reads) while `_WaterFogClassifyRT` already holds that exact pair this frame. No `WaterlineFarFromSurface` skip either. Its local `OceanRenderedCoverage` copy has drifted from the fog's. | CONFIRMED (dup), PLAUSIBLE (cost) | `LargeBodyGodRays.shader:1165-1181, 1098-1107` | Add the `WATER_FOG_CLASSIFY_RT` variant to the composite and LOAD the RT; analytic fallback only when the fog chain didn't record. | probe **G** (whole pass) |
| B4 | `#pragma skip_optimizations webgpu` applies to all 24 raymarch variants (up to 64 steps × shadow tap × exclusion loop × caustic tap × 8-lamp `atan` loop). Justified by a translator bug in Simple-tier variants only. | CONFIRMED (presence), PLAUSIBLE (magnitude) | `LargeBodyGodRays.shader:89-97` | Split Full/Simple raymarch into two passes; pragma on Simple only. Needs a rebuild to measure. | probe **G** |
| B5 | Classify RT is full-res RG32F regardless of `FogSolveScale`; at 0.5 it computes 4× the pixels the solve reads. Within 4 m of the surface it's ≥1 analytic eval per full-res pixel incl. sky; **any exclusion volume disables the far-skip forever** (`WaterWaterline.hlsl:254-267`). | CONFIRMED | `WaterUnderwaterFogPass.cs:462-468`; `WaterUnderwaterFog.shader:963-993` | Classify at solve scale; meniscus keeps its own analytic path (already exists, `WaterUnderwaterWaterline.shader:98-121`). Consider R16G16F. | none (R scales solve only) |
| B6 | Arming band = `rest ± (envelope + 0.5 m)`, envelope ≥ 5 m on an FFT ocean → fog chain + meniscus + full-res colour COPY (`MeniscusWarp` default 0.35) + god rays all run whenever the camera is within ~5 m above the surface. Pass header "only the few straddle frames" (:852) is false for oceans. | CONFIRMED | `WaterVolume.Underwater.cs:496-551, 584-598`; `WaterUnderwaterFogPass.cs:862-897` | Record the copy conditionally on CPU `abs(cam.y − surfaceY) < margin`; early-out the waterline pass via `WaterlineFarFromSurface` before the copy. | inspector `Meniscus Warp = 0` |
| B2 | Per armed frame ~24 displaced-mesh draws in 4 prepass passes (11 ownership ½-res + 11 visible-depth + 74,529-vert height-RT grid + lens grid) on top of ~22 queue-time draws. Height grid is 1 vertex per texel of a bilinear R16F target. | CONFIRMED (counts) | `WaterUnderwaterFogPass.cs:77-81, 628-633, 911-981`; `WaterVolume.OceanClipmap.cs:38-45` | B1 first; then height grid at ½ texel density (4× fewer verts) or bake height from cascades in a fullscreen pass; cull far clipmap levels from the prepass when submerged. | probe **F** (whole fan) |
| B7 | Scaled solve: `SolveUpsampleTaps` (4 loads + depth) computed twice (absorb :1451, inscatter :1489); solve samples scene depth 3× (:1145, :1598, :1637). | CONFIRMED, small | `WaterUnderwaterFog.shader` | out-param the depth; accept or share the weights. | probe **R** |
| B8 | 13–14 attachment load/store cycles per fogged frame — the package's own comments say the RT switch is the dominant WebGPU cost. | PLAUSIBLE | frame map | Merge height+lens into one pass (two viewports); fold visible-depth into the ownership MRT or delete per B1. | — |
| B9 | Debug views are a UNIFORM (`_WaterDebugMode`) in all 12 release solve variants; branch ids kept in `static` globals across the solve. ALU only, not a register cliff. | CONFIRMED | `WaterFogDebug.hlsl:198-247`; solve :1362 | Fence behind a `WATER_FOG_DEBUG` keyword compiled only in editor/dev. | — |
| — | 07-29 cliff: FIXED in the fog shader (`_UnderwaterFogSimple` read only by the debug view). Stale comment at `Pass.cs:279-281`. **Still a uniform branch in `WaterExclusionWall.shader:137/158`.** No "double fog" found (underside tint is a fixed 1 m term; PREPASS_AIR zeroes from-air pixels). | CONFIRMED | | fix the comment; review the wall shader. | |

Variants: fog shader 18 fragment programs (2 dead `SIMPLE+POINT_LIGHTS` combos); god rays 30 (2 never-dispatched blur passes). Not an explosion. No per-frame allocations, no GetTemporaryRT, no per-frame materials in the passes.

**Suggested measurement order (no rebuild):** probe **G** off → if the delta is large, B3+B4 are the target. Probe **R** at 0.5 → if the solve barely moves, B5/B1/B2 are the target. `Meniscus Warp = 0` → prices the copy.

---

## 2. Perf — surface shader + compute

| # | Finding | Status | Where | Fix direction | A/B today |
|---|---|---|---|---|---|
| S1 | **4× `WaveSlope()` per fragment and 4× `WaveHeight()` per vertex, unconditional** — grid, mouth-outflow twin, river A, river B — evaluated BEFORE the `_IsRiver` uniform branch; three of the four are multiplied by 0 on every pool/ocean. Each is a 16-sinusoid `[loop]` + envelope. (Hand-verified.) | CONFIRMED (structure), PLAUSIBLE (compiler may not sink) | `WaterSurfaceFragStages.hlsl:141-155`; `WaterSurfaceVertStage.hlsl:372-388` | Move river A/B and outflow samples inside `_IsRiver` / `_MouthOutflowCount > 0` branches; pools single-evaluate, byte-identical. | `MaxWaveCount` tier knob prices one eval |
| S2 | Detail normals sampled twice (8 `tex2Dgrad`, 24 with hex) — the outflow-drift twin runs on bodies with no river mouth. | CONFIRMED | `WaterSurfaceFragStages.hlsl:290-295` | Same gate as S1. | Detail Normal Strength |
| S3 | FFT cascade normal sum evaluated up to 4× per pixel at the same XZ (tilt, geometry-foam Jacobian, `OceanFftFoam` which re-runs `ShoreSample`, crest-glow Jacobian) — 16 array `SampleLevel` where 4 suffice. | CONFIRMED (call sites) | `WaterLargeWaves.hlsl:567-653`; `FragStages:218, 363, 1062` | Evaluate once into a struct passed down. | — |
| S4 | Aperiodic-tiling ternary INSIDE the cascade loop (`_OceanAperiodicParams.x > 0.5 ? OceanAperiodicNormal : SampleLevel`) — if lowered to a select, non-aperiodic oceans pay 3 taps + 12 Loads + `atan2` per cascade per sum. `WATER_DISABLE_OCEAN_APERIODIC` is never defined (5 dead forks). | PLAUSIBLE | `WaterLargeWaves.hlsl:530-532, 590-592` | Hoist to a uniform branch outside the loop, or a keyword. | `oceanAperiodicEnabled` toggle |
| S5 | Horizon haze = 21 fetches/pixel (2× `SampleHorizonSky`: 5 opaque + 5 depth each + cube) for an azimuth-only colour. | CONFIRMED | `FragStages:1856-1878, 2016, 2023` | Per-frame 1D azimuth LUT or vertex-stage evaluation. | Horizon Haze density |
| S6 | Full-quality ocean pixel ≈ **138 taps** (52 ripple incl. 5-step refine, 8 detail, 16 cascade, 34 foam layers, 21 haze) + ~200 transcendentals; +24-step SSR → ~167. Pass 0 = **48 fragment variants** (fog 4 × shadows 3 × underside-foam 2 × point-lights 2). | CONFIRMED (tally) | `02_shader_compute_perf.md §1` | Budget view; S1–S5 are the cuts. | reflection mode, `highRefineSteps` |
| S7 | **16 samplers exactly** in Pass 0 and **23 sampled textures** in one stage — above WebGPU's base `maxSampledTexturesPerShaderStage` (16); runs today only because the adapter limit is higher. Portability cliff (mobile/iGPU). | PLAUSIBLE | `02 §4` (enumeration) | Atlas foam layers / share samplers. | — |
| S8 | FFT `BakeHeightField` + array `GenerateMips` every dispatch regardless of readback demand (demand gate exists at :706-714 but only gates the request). | CONFIRMED | `WaterOceanFft.cs:664-682` | Gate the bake dispatch on the same demand window. | — |
| S9 | Foam particles draw **full capacity ×3–4 passes, no indirect args**; dead slots exit in VS. 65,536 cap → ~1.5 M vertex invocations/body/frame. | CONFIRMED | `WaterFoamParticles.cs:1137-1265`; `FoamParticles.shader:433-456` | `DrawProceduralIndirect` with an alive-count args buffer. | `highMaxFoamParticles` |
| S10 | Pond foam coverage evaluated twice per overlay fragment (5 taps); patch-rim vertex morph runs the base-sheet vertex 3×; chunk wall marches 8 + 6·crossings full surface evals per fragment. | CONFIRMED | `WaterSurface.shader:540` + `FragStages:1416`; `VertStage:509-566`; `WaterChunkWall.shader:281-333` | share; cap the wall march. | — |
| S11 | Redundancy: sim-window edge fade ×5 (compute copy drifted); caustic PCF gather ×5; Receiver vs Terrain submerged-lighting blocks drifted (diffed); IGN ×4; rotated-octave transform ×2. | CONFIRMED | `02 §3` (both copies cited each) | one include per block; the 200k-sample numeric-proof method before dedupe (07-28 rule). | — |
| S12 | Seven mirrored constants missing from the validator: `WATER_MAX_MOUTH_OUTFLOWS`, `RIVER_DISTURBANCE_MAX_SOURCES`, `RIVER_CASCADE_TRANSPORT_SAMPLE_COUNT`, `PEAKED_REFINE_MAX_STEPS`, `OCEAN_FFT_TG`, `OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION`, `KIND_*`. (Hand-verified: 0 hits in `WaterWaveConstantsValidator.cs`.) | CONFIRMED | `Editor/WaterWaveConstantsValidator.cs` | add the pairs. | — |

No dead uniforms (287 checked). WaterCostProbe has no surface-side keys; every S-item is priced by a live tier/MPB knob (table in `02 §5`).

---

## 3. Runtime C# — practices, CPU cost, redundancy

Baseline: 0 `#if false`, 0 TODO/HACK, 0 commented-out code, 0 LINQ, no per-frame allocations, no `Camera.main`/`Find` in Update, nesting ≤ 5, all static state reset centrally. (Scripts in `03 §0`.)

| # | Finding | Sev | Where | Fix direction |
|---|---|---|---|---|
| C1 | Every Shuriken droplet per frame runs a full `WaterDomainResolver.Resolve` (provider scan + surface sample + exclusion + seam blend) then `TryGetSurface` re-samples the same point — 2 samples + 1 resolve per droplet, no hysteresis. | HIGH | `WaterSplashEmitter.cs:319-375` | Resolve once per emitter per frame (droplets share a body), sample per droplet. |
| C2 | `WaterExclusionVolume.ContainsPoint` builds `TRS(...).inverse` per volume per call; since 08-29 it's on every buoyancy/membership/probe/droplet resolve — the "nothing next to the raycast" comment (:398-402) is stale. | HIGH | `WaterExclusionVolume.cs:394-407` | Cache the world→shape matrix on transform change. |
| C3 | Ribbon containment/sample = brute-force `(knots−1)×17` spline evaluations per distinct point; memo keyed on exact point+margin so the resolver pays it twice per miss; × `WaterRiverDisturbance.RebuildSources` per interactor. | HIGH | `WaterRiverSplineEvaluator.cs:73-147` | Coarse AABB/segment reject first; memo by frame. |
| C4 | Publisher-tracked ids written out-of-band on cached blocks (`_MouthOutflow*`, `_PatchCoverActive`, `_UseBedDepth`, `_ClipOceanToTerrain`, `_PatchPool*`) — violates the 08-13 CachedUniformSink rule (hotfix-d lesson). | MED | `WaterRiverSurface.cs:676-697`; `SimWindowPatch.cs:136-137` | route through `WriteBodyUniforms`. |
| C5 | All cached sinks self-heal on the same 128-frame boundary → periodic ~2k native-set spike. | MED | `WaterUniformPublisher.cs:1027` | stagger by sink hash. A/B: probe worst-ms. |
| C6 | Surface-height pipeline implemented 4× (`Facade.cs:167-231` + `Query.cs:69-110`); `TryGetWaterHeight` / `TrySampleSubmersion` have 0 external callers. | MED | | collapse onto the provider path. |
| C7 | Resolver samples the winner twice on non-containing intents; `TryBuildMouthOutflow` runs 3× per river per frame (no frame cache in edit mode). | MED | `Query/WaterDomainResolver.cs`; `WaterRiverSurface.cs` | frame-stamp cache. |
| C8 | `WaterVolume` = 33 partials / 8,065 lines / ~1,080 members / 159 SerializeFields / 514 internals; `Settings.Ocean` has 58 pass-through forwarders. The split moved fields, not behaviour; `IWaterModule` is the ready seam. | MED | | move behaviour into modules, one partial at a time. |
| C9 | `WaterFoamParticles` (sim+draw+veil in one), `WaterSplashEmitter` (175 lines of Shuriken authoring in runtime); full-RGBAFloat sim readback every frame memcpy'd to `Color[]` (plus FFT). | MED / PLAUSIBLE | | split; readback R16F/half region. |
| C10 | 13 direct `Shader.SetGlobal*` outside the publisher (Planar 3 legacy, DebugView 3, OceanFft 5 documented, ParticlePool 1). | LOW | `03 §1` | keep documented ones; retire Planar's. |
| C11 | Public API: **102 public members with zero refs outside their file**; top-20 in `03 §3` (`SampleHeightAcrossBodies`, `SimWindowHalfExtent`, `TrySampleSubmersion`, `CameraPathRunning`, ContextMenu methods, 7 public mutable fields on `WaterExclusionVolume`). | LOW | | internal / delete. |
| C12 | Strings: 479 `PropertyToID` literals vs 142 via `WaterShaderProps`; 14 names declared in two files. Magic numbers: `WaterSplashRange` live logic (7 sites), `WaterSimScheduler` `-1` beside an unused `InvalidFrame` const. | LOW | `03 §5` | consolidate into `WaterShaderProps`. |
| C13 | Query/: logic is deterministic and correct; smells = double sample, `MaxVerticalDistance==0` sentinel, `_nextConnectionId` reset while body-id counter survives (dup ids under Fast Enter Play Mode), first-seam-wins on overlapping connections, `BodyHint` typed `WaterVolume` (can't target a ribbon), `WaterSurfaceProviders` internal while `IWaterSurfaceProvider` is public. | MED | `03 §6` | matters for the system window's data model (§5). |

---

## 4. River scripts → one dedicated editor (analysis for step 2)

**Today:** river = 3 required components (`WaterRiverSpline`, `WaterRiverCurrentField`, `WaterRiverSurface`; enforced by `WaterRiver`'s RequireComponent, `WaterRiver.cs:64-65`) + 3 optional (`WaterRiverFluid`, `WaterRiverFoam`, `WaterRiverDisturbance`) + `WaterRiverInteractor` on foreign objects + a `WaterRiverFluidBakeData` asset. ~50 serialized tuning fields live on sub-components (Fluid 16, Foam 11, Disturbance 8, Surface 5, Spline 1); the facade owns only `parentVolume`, two end connections and 5 mouth-outflow knobs. Five custom editors (River, Spline, CurrentField, Fluid, Foam) in three different section languages (raw bold labels / `WaterEditorUI.SubHeading` / `Section+TabBar`); Surface/Disturbance/Interactor use default inspectors; the Fluid editor never draws `bakeData`. Only Surface/Fluid/Foam/Disturbance are `[ExecuteAlways]`. Component + editor maps with fields in `04 §1-2`.

**Drift risks (CONFIRMED):** parent volume stored twice (facade + surface, one-way sync); spline reference in three places with fill-null-only wiring; `currentField.fluid` duplicated; inert mouth-foam fallback constants differing from Foam's defaults. `WaterEditorPreviewDriver` is gated on `WaterVolume.ActiveBodyCount` → standalone rivers get no live preview. `WaterRiverAuthoringChangeRouter` scans every moved Transform but never re-aims generated ports.

**Keep:** `WaterRiverEditor.GenerateEnd/RemoveEnd` Undo handling is exemplary and reused by both builders (`WaterBuildKit.River.cs:57`, `ConnectedWatersDemo.cs:212/291/297`).

**Model to copy from WaterVolumeEditor:** orchestration partial + one partial per tab, `WaterEditorUI` header/tabs/sections, single `SyncFoldouts` list, `DrawFields/DrawFieldsIf` with no range literals, button-driven defaults, read-only readouts, component-row discovery. Not worth copying: look presets, Jerlov, body-type selector, 60-bool foldout sprawl, null-guarded Chunk paths.

**Options:**
- **A** — one `WaterRiverEditor` drawing nested `SerializedObject`s of the siblings. Zero serialization change; pattern already in tree (`WaterFoamProfileEditor.cs:361-368`).
- **B** — move ~43 fields onto `WaterRiver`, sub-components become workers. Rewrites ~50 test lines in 9 files + 2 builders, needs scene migration for the demo scenes, contradicts the facade's own charter (`WaterRiver.cs:5-6`), and cannot remove `WaterRiverSurface` (`WaterConnectionPort.river`).
- **C (recommended)** — A + one runtime clean-up: `ApplyWiring` assigns the spline always (not fill-null-only) and the editor draws wiring refs read-only in a "Wiring (facade-managed)" fold; keep the spline editor for scene handles; stub the other sub-editors. Tabs suggested: Path / Flow / Surface & Seams / Fluid & Bake / Foam / Disturbance / Connections / Wiring.

**Hidden states needing explicit UI:** DontSave ribbon/fog meshes + rebuild error, underside twin child, `bakeData` asset + staleness, generated port/connection children, parent `currentFields[]` membership (hand-added rivers not listed at edit time), overlay/provider registration.

**River code-quality top items:** dead `WaterRiverFoam.Configure/BakedTexture/RiverLength`; unused 4-arg `WaterRiverSurface.Configure`; six `HalfWidth` copies; four spline arc-length walks; duplicated `ProjectAabbExtent`/feature-flag consts; magic numbers in `WaterRiverFluid`; stale `ResolveCurrentField` comment. File:line in `04 §6`.

---

## 5. Body connections + water-system window (analysis for steps 3–4)

**Connection anatomy today** (full step list with consumer lines in `05 §1`): a connection = **3 generated scene children per river end** (`Port - River X`, `Port - <Body>`, `Connection - River to <Body>`) owned by `WaterRiver` through serialized refs (`WaterRiver.cs:50-53`, created `:518-545`), plus `WaterTopology`'s static registry doing a 50/50 slab blend at query time (`Query/WaterTopology.cs:94-165`). The user must: create the river → set `parentVolume` → set `sourceEnd.body` / `mouthEnd.body` → press **Generate Connection** per end (`WaterRiverEditor.cs:128-134`, the only non-kit path) → turn `waterFog` on per body → keep terminal knots on the footprint border → press **Update Connection** after any move.

**Silent failures (CONFIRMED):** body assigned but never generated (no warning, `WaterRiver.cs:724-740`); ports go stale on knot/body moves (facade never subscribes to `WaterRiverSpline.Changed`); `waterFog` off on end bodies unvalidated; `WaterRuntimeValidation.ValidateConnection` has **0 callers** (hand-verified) — the network window re-implements it.

**Redundant data:** parent on both `WaterRiver` and `WaterRiverSurface` (`:212-216`); transition radius + flow rate copied to ports AND connection (`:523,530,539,540`); flow rate has zero in-package readers. Everything on a port is derivable (terminal spline frame `:395-416`, nearest border `:427-447`) except the GUID. Apron (UV1.w) and wave anchors read `end.body`, not ports; fog medium comes from parent + end bodies via `SelectRiverFogMediumBody` (`Settings.Underwater.cs:175-203`), never from ports. → **Ports are pure runtime identity; nothing authored needs them visible.**

**Directions (ranked):**
- **D1** — auto-generate on body assignment, auto-sync on spline/body change (subscribe to `Changed`), warn on "assigned but not generated", call `ValidateConnection` from the inspector. No data-model change, all existing scenes/tests intact.
- **D2** — hide/derive the port children (HideFlags or generate at enable from the GUID kept on the facade). Backward-compat: existing scenes keep their children; migration = read GUID from child once.
- **D3** — a lightweight `WaterSystem` root for quality / fog / membership defaults (NOT topology — `WaterTopology` already is the registry). Overlap-derived connections and ScriptableObject connections: not recommended (reasons in `05 §3`).

**What the kit already covers:** the whole "water system" recipe exists once, hard-coded, in `CreateConnectedWatersRig` (`WaterBuildKit.ConnectedWatersDemo.cs:138-277`); `CreateConnectedRiver`, `EnableUnderwaterFog`, `ConfigureUnboundedOcean` are private to that partial. `GameObject > River` re-implements the mouth-connect inline and creates a fresh `Waters/Water N` folder + materials per river instead of reusing the parent's (`River.cs:43,56-60`). The Wizard creates exactly one primary body per root — no river/connection/exclusion/quality; its "secondary body" is a renderer clone (`WaterSceneBuilder.cs:153-195`), a **third** body-building recipe beside `CreateWaterBody` and the prefab builder. `WaterNetworkManagerWindow` is a read-only runtime table (union-find over `WaterTopology`) — reuse its component computation as the future window's status pane; its menu path is off the shared `MenuRoot` (3 menu roots exist; `[WebGpuWater]` literal inlined ~25× in editor).

**System window sketch:** data model = system → bodies → rivers → connections → exclusions → quality asset; bodies/rivers/connections/exclusions already exist as runtime types, quality as `WaterQuality`. Recommended shape: a **new Water Wizard section** that builds a plan (list of bodies/rivers/connections/exclusions + one quality asset), executed in one undo group from the existing kit recipes (made internal, not private), with the network window's table as the status pane. Risks: renderer-feature install, per-river asset folders, prefab refs, GUID regeneration on rebuild, `_nextConnectionId` reset (C13).

---

## 6. Suggested order (for GO, one increment at a time)

1. **Measure first, no rebuild:** probe G / R / `Meniscus Warp 0` as in §1 → tells us whether B3+B4 or B1/B2/B5 is the fog target.
2. **B1** (gate the visible-depth prepass) — one gate condition, oceans unaffected by construction, biggest free win on the list.
3. **S1+S2** (river twins under the `_IsRiver` gate) — pools byte-identical, river pixels unchanged.
4. **B3** (composite reads the classify RT) and **B5** (classify at solve scale).
5. **C1+C2+C3** CPU hot paths (each is a local change).
6. River editor **Option C** → connections **D1** → system window as a Wizard section.

Everything else in the tables is filed for when the surrounding file is opened anyway.
