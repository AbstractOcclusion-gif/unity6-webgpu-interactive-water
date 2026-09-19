# 03 — Runtime C# code-quality + CPU-perf audit (Runtime/*.cs, Runtime/Query/*.cs)

Scope: 145 files / ~36k lines (Runtime root + Query). Rendering/ excluded. WaterRiver* reviewed for code quality only.
Method: every claim below was grepped/read in the CURRENT tree (2026-09-02). **CONFIRMED** = every consumer read; **PLAUSIBLE** = needs a profiler/WaterCostProbe measurement. No files were modified.

Overall verdict: this is an unusually disciplined codebase — zero `#if false`, zero TODO/HACK/FIXME, zero commented-out code, zero LINQ, zero `Camera.main`-in-Update, no per-frame `new` in hot paths, nesting never exceeds 5 control levels, static state is centrally reset. The real findings are (a) CPU work the 08-29 domain resolver quietly multiplied per particle/probe, (b) publisher-cache bypasses, (c) a god class that was split into 33 files but not into objects, and (d) four copies of the surface-height pipeline.

---

## Ranked findings

| # | Sev | Status | File:line | Issue (one line) | Fix direction |
|---|-----|--------|-----------|------------------|---------------|
| 1 | HIGH | CONFIRMED (cost PLAUSIBLE) | `Runtime/WaterSplashEmitter.cs:319-322,355-375` | Every Shuriken droplet, every frame: full `WaterDomainResolver.Resolve` (provider scan → `TrySampleSurface` with Height|Velocity → `WaterExclusionVolume.ContainsPoint` → seam-blend loop) and THEN `domain.Body.TryGetSurface` re-samples the same point. 2 full surface samples + 1 resolver pass per droplet, `PreviousBodyId` never set (no hysteresis). | Resolve the body ONCE per emitter per frame (or per burst), then sample droplets against that provider only; pass the resolver sample through instead of re-sampling. |
| 2 | HIGH | CONFIRMED (cost PLAUSIBLE) | `Runtime/WaterExclusionVolume.cs:394,401-407` | `ContainsPoint` builds `Matrix4x4.TRS(transform.position, rotation, lossyScale).inverse` per volume per call. Since 08-29 it runs from `WaterDomainResolver.FillSample` (`Query/WaterDomainResolver.cs:295`) for every buoyancy body, spray-pump, membership object, interactor and droplet per frame — the doc comment ("nothing next to the raycast that precedes every call", :399-400) predates that and is now false. | Cache the world→shape inverse per volume with a frame stamp (or rebuild in its own `LateUpdate`); fix the comment. |
| 3 | HIGH | CONFIRMED (cost PLAUSIBLE) | `Runtime/WaterRiverSplineEvaluator.cs:73-131,139-147` + `Runtime/WaterRiverSurfaceProvider.cs:63-92,143-197` | Ribbon containment/sample = brute-force projection: `(knots-1)×17` calls to `TryEvaluateSegment`, each doing 4 quaternion rotations, 3 normalizes, a cross and a full `WaterRiverSplineSample` build — just to get a squared distance. The provider memo (:157-160) is keyed on the exact point+margin, so every distinct probe/droplet/interactor point pays it; resolver calls `ContainsPoint` (margin 0) then `ContainsPointWithin` (margin 0.5) → two scans per miss. | Position-only cubic for the coarse scan; per-segment AABB reject; cache knot world positions per frame; memo the projection independent of margin. |
| 4 | MED | CONFIRMED | `Runtime/WaterRiverSurface.cs:676-682,694-697` and `Runtime/WaterVolume.SimWindowPatch.cs:136-137` | Publisher-tracked ids written OUTSIDE `WriteBodyUniforms` onto publisher-cached blocks: river overrides `_MouthOutflow*`, `_PatchCoverActive`, `_UseBedDepth`, `_ClipOceanToTerrain` every frame; patch rewrites `_PatchPoolCenter/_PatchPoolHalf` that the sink already wrote (`WaterUniformPublisher.cs:757-758`). Owner's rule: "never write a publisher-tracked id outside WriteBodyUniforms". Works today only because the override is re-applied every frame; the shadow cache is lying. | Add an `IUniformOverride`/callback hook the publisher applies INSIDE the cached pass, or give rivers their own (non-body) block. Delete the redundant patch writes. |
| 5 | MED | CONFIRMED | `Runtime/WaterUniformPublisher.cs:1027,1057-1069` | `FullRewriteIntervalFrames = 128`: every cached sink (`_mpb`, 2 patch blocks, clipmap block, membership block, global sink, foam ×4, river, passes) was created in the same frame, so every 128 frames ALL of them `Clear()` + rewrite ~179 native properties on the same frame → periodic spike (~1.5-2k native sets in one frame at 200 fps = every 0.64 s). | Stagger `_nextFullRewriteFrame` per sink (hash of target) or drop the self-heal now that the missing-id rebuild exists. A/B: WaterCostProbe worst-ms readout. |
| 6 | MED | CONFIRMED | `Runtime/WaterVolume.Facade.cs:167-231` vs `Runtime/WaterVolume.Query.cs:69-110` | Surface-height pipeline implemented 4× (`TryGetWaterHeight`, `TryGetSurface`, `TrySampleSubmersion`, `TrySampleWorld`): each does QueryPoolXZ → TrySamplePoolSurface → PoolToWorld → `if (openWater) += SampleLargeWaveField`. Query.cs:70 even says "mirror TryGetSurface + TrySampleSubmersion exactly". `TrySampleSubmersion` also has its own inline footprint test (:225) instead of `QueryPoolXZ`. External refs: `TryGetWaterHeight` 0, `TrySampleSubmersion` 0, `TryGetSurface` 1 (SplashEmitter:369). | Make `TrySampleWorld` the single evaluator; express the three public wrappers as field-masked calls into it (or delete the two with zero callers). |
| 7 | MED | CONFIRMED | `Query/WaterDomainResolver.cs:140-152,154-161,280-289` | Non-containing intents sample the winner twice: `SelectSurfaceInRange` calls `TrySampleSurface` on every XZ-containing candidate, then `FillSample` samples the winner again. On a ribbon that is two brute-force projections (see #3). | Return the winning `WaterSample` from `SelectSurfaceInRange` and let `FillSample` accept a pre-sampled surface. |
| 8 | MED | CONFIRMED | `Runtime/WaterRiver.cs:161-167`, `Runtime/WaterRiverSurface.cs:668-671`, `Runtime/WaterVolume.Currents.cs:209-215,232` | `TryBuildMouthOutflow` (seam resolve + `MouthProfileSampleCount` bake samples) runs 3× per river per frame: river LateUpdate, surface LateUpdate, and the host body's refresh (which river LateUpdate invalidates every frame). In edit mode the host refresh has no frame cache (`:213` gated on `isPlaying`) so it re-runs on every `WriteBodyUniforms` pass. | Build once per frame in `WaterRiver.LateUpdate`, hand the struct to surface + host; frame-cache in edit mode too. |
| 9 | MED | CONFIRMED (structure) | `Runtime/WaterVolume*.cs` (33 partials, 8,065 lines) | God class: ~1,080 members, 159 `[SerializeField]`, 74 public + 514 internal members, 21 `static` mutable fields. Settings.Ocean alone = 753 lines / 212 members with 58 `=> ocean.` forwarders — the nested-Settings migration moved fields but not behaviour, so the surface did not shrink. | Continue the `IWaterModule` seam (`WaterCollaboratorModules.cs`): OceanClipmap, SimWindowPatch, Chunk, Underwater/fog gate, Currents and Quality each already own their state — promote them to modules holding their own MPBs, leaving WaterVolume as registry + orchestration. |
| 10 | MED | CONFIRMED (structure) | `Runtime/WaterFoamParticles.cs` (1,356 lines) | Three responsibilities: compute simulation (`DispatchSimulation` :719-948, `OnBeginCameraRendering` :950), draw/property-block authoring (`Draw` :1106-1237, `RenderAfterFog` :1239), density veil (`DrawDensityComposite`/`WriteDensityCompositeProps` :1286-1360) + burst queue diagnostics (:89-140). 96 `PropertyToID` literals. | Split at the block boundary: `FoamParticleSimulation` (buffers+kernels), `FoamParticleDrawer` (4 MPBs + RenderPrimitives + after-fog), `FoamDensityVeil`. |
| 11 | MED | CONFIRMED (structure) | `Runtime/WaterSplashEmitter.cs:669-843` | Runtime emitter carries ~175 lines of static Shuriken *authoring* (`ConfigureForDrift/Crown/Jets`, gradients, curves) used by the build kit — not a runtime concern. | Move the `Configure*` statics to an authoring helper next to WaterBuildKit. |
| 12 | MED | CONFIRMED | `Runtime/WaterSurfaceSampler.cs:58,63-69`; `Runtime/WaterQuality.cs:42` | Buoyancy readback = full RGBAFloat sim RT (256² default = 1 MB, 512² = 4 MB) requested EVERY frame (`DefaultReadbackInterval = 1`) and memcpy'd into a managed `Color[]`; CPU only reads `.r` (height) and a tilt. Plus a second full RGBAFloat FFT-height readback (`WaterOceanFft.cs:713-731`). On WebGPU that is the GPU→CPU bandwidth the tier system exists to save. | Read back an R16F/RG16F height(+tilt) copy at half res; consider one shared readback that packs sim + FFT height. A/B with the tier's `readbackInterval`. |
| 13 | LOW | CONFIRMED | `Runtime/WaterUniformPublisher.cs:392-394` | Stale WHY comment: "WriteBodyUniforms runs ~22x per frame on a default ocean (… every clipmap level x2)". Clipmap now shares ONE block (`WaterVolume.OceanClipmap.cs:105-115`); real count is ~8-12 (body, 2 patches, clipmap, global, membership, foam ≤4, river, atmosphere/caustic passes). | Update the comment (and re-measure the skybox-cache justification). |
| 14 | LOW | CONFIRMED | `Query/WaterTopology.cs:21,70,81` vs `Query/IWaterSurfaceProvider.cs:103-110` | Inconsistent no-domain-reload policy: body-id counter deliberately survives `ResetStaticState` (documented), but `_nextConnectionId` is zeroed while surviving `WaterConnection` components keep their old `ConnectionId` (runtime auto-property, `WaterConnection.cs:34`) → duplicate ids after Fast Enter Play Mode. | Mirror the provider policy: keep the counter, or clear `ConnectionId` on Unregister. |
| 15 | LOW | CONFIRMED | `Runtime/WaterWaveGauge.cs:155-165` | `ResolveVolume` claims "domain-resolved every frame" but caches the first hit into the serialized `volume` field, so the gauge never re-resolves (and stomps the inspector field at runtime). | Keep the resolved body in a private field; re-resolve on `_domainBodyId` change. |
| 16 | LOW | CONFIRMED | 14 shader property names declared in TWO files each (list in §5) e.g. `_OceanFftActive`: `WaterCausticsPass.cs:32` + `WaterUniformPublisher.cs:144`; `_MouthOutflowCount`: `WaterRiverSurface.cs:57` + `WaterUniformPublisher.cs:246` | Hardcoded-string rule: 479 `PropertyToID("…")` literals vs 142 via `WaterShaderProps`; duplicates are drift risks the validator cannot see. | Move every name used in ≥2 files into `WaterShaderProps` (Name const + id), as already done for `PatchCoverActive`. |
| 17 | LOW | CONFIRMED | `Runtime/WaterSimScheduler.cs:10-14` | `const int InvalidFrame = -1` declared, then `-1` literal used twice on the next lines. | Use the const. |
| 18 | LOW | CONFIRMED | `Runtime/WaterRiverDisturbance.cs:462-465` ≡ `Runtime/WaterRiverInteractor.cs:197-200` (`ProjectAabbExtent`); `WaterRiverDisturbance.cs:338-349` ≡ `WaterRiverSurfaceProvider.cs:184-197` (lateral-bound test) | Duplicated river helpers. | One `WaterRiverGeometry` static. |
| 19 | LOW | CONFIRMED | `Runtime/WaterRiverSurface.cs:216-221` | `catch (Exception)` logs `exception.Message` only — stack trace lost. | `Debug.LogException(exception, this)` after the cleanup. |
| 20 | LOW | CONFIRMED | `Runtime/WaterSplashRange.cs:225,329,334,392,441,484,499` | Magic numbers in the demo range's live logic (`Random.Range(0.7f,1.3f)`, `extent*0.5f`, `Min(penetration,1.5f)`, `Max(SizeFactor,0.05f)`, `0.01f`, `-0.6f..1f`, `±0.4f`). Prop-builder geometry (:670-736) is acceptable as data. | Name them (`AutoIntervalJitter`, `EdgeBandMaxFraction`, …). |
| 21 | LOW | CONFIRMED | `Runtime/WaterDebugView.cs:203,223` | `Shader.SetGlobalFloat` every frame by design (documented WHY) — 1 write, fine; listed for the "direct SetGlobal" inventory only. | none |
| 22 | INFO | CONFIRMED | `Query/WaterDomainResolver.cs:113-114` | `provider == null \|\| options.BodyHint == null` — the second half is the Unity fake-null test (the interface compare is not); reads as a redundant check. | Test `options.BodyHint == null` once, then cast. |

---

## 1. PER-FRAME CPU COST

### 1a. Who runs per frame (Runtime root + Query)

| Callback | Owner | Cardinality | What it does per tick | Notes |
|---|---|---|---|---|
| `Update` (ExecuteAlways, order −50) | `WaterVolume.Update.cs:18` | per body | scheduler, injection flush, solver step, wave bank, 3 `EnsureBaked`, FFT dispatch (ocean, every `_oceanFftInterval`), caustic render (every `_causticInterval`), `ApplyBodyBlock` (5 blocks), primary: `PublishSharedGlobals` + `PublishBodyGlobals`, readback request (every `_readbackInterval`) | edit-mode ticks via editor driver |
| `beginCameraRendering` | `WaterVolume.Underwater.cs:145` | per body × per camera (early-out unless `cam == targetCamera`) | planar mirror render (per planar body), primary: river-fog override → `BodyContainingForUnderwaterEffects` → `UpdateUnderwaterState` (≈`PublishBodyGlobals` if fog source ≠ primary, `ComputeCameraSubmerged` (surface height at 4 near-plane corners), `WaterExclusionVolume.ContainsPoint`, `PublishUnderwater` (5 globals + 4 keyword toggles + scene-light gather) | keyword toggles every frame are compare-and-set in Unity; fine |
| `beginCameraRendering` | `WaterFoamParticles.cs:950` | per foam system × per camera | deferred density splat (2 dispatches) | gated `_densityPending && cam == _densityCamera` |
| `beginCameraRendering` | `PlanarReflection.cs:46` (legacy global component) | per component × camera | mirror render + `Shader.SetGlobalTexture` | legacy path; per-body planar is the current design |
| `LateUpdate` | `WaterFoamParticles.cs:668` | per foam system | `BuildShoreFoamState` ×1 (+2 more in Dispatch/OnBegin), compute uniforms (~60 sets), 4 MPB `WriteBodyProps` + `RenderPrimitives` | fine |
| `LateUpdate` | `WaterSplashEmitter.cs:299` | per emitter | Shuriken `GetParticles`/`SetParticles` round-trip + **per droplet** resolver + 2 surface samples (#1) | HOT |
| `LateUpdate` | `WaterSprayPump.cs:334` | per pump | 1 resolver + ≤2 batched samples + per-probe step | fine |
| `LateUpdate` | `WaterMembership.cs:48` | per member | 1 resolver (`GameplayBodyAt`) + shared `MembershipBlock` | fine (block shared, built once/frame) |
| `LateUpdate` | `WaterInteractable.cs:107` | per interactable | `Renderer.bounds` + 1 resolver + analytic waterline | fine |
| `LateUpdate` | `WaterExclusionVolume.cs:316` | per volume | `Graphics.DrawMesh` with 4 MPB sets | fine |
| `LateUpdate` | `WaterRiver.cs:161`, `WaterRiverSurface.cs:186`, `WaterRiverDisturbance.cs:137` | per river ×3 | mouth-outflow build ×2 (#8); block publish (uncached: 5 `SetVectorArray` + 9 floats + 4 anchors) ; `RebuildSources` = 2-3 spline projections + 1 resolver **per interactor per river** (#3) | HOT with many interactors |
| `LateUpdate` | `WaterRiverInteractor.cs:102`, `WaterSurfaceDisturbance.cs:101`, `WaterSphereInteractor.cs:76`, `WaterBreachSplash.cs:104`, `WaterWaveGauge.cs:138`, `WaterFogTransparent.cs:81` | per component | 0-1 resolver/sample each | fine |
| `FixedUpdate` | `WaterBuoyancy.cs:194` | per floater | 1 resolver (+seam blend + exclusion) + batched `SampleHeights` (N probes × CPU analytic incl. chop inversion) + forces | fine; #2 and #7 apply |
| `FixedUpdate` | `WaterSplash.cs:41`, `WaterSplashRange.cs:294`, `BoatController.cs:114`, `WaterDemoSubmarineController.cs:25` | per component | demo/gameplay | fine |
| `Update` | `WaterDebugView.cs:203`, `WaterCostProbe.cs:57`, `WaterMetricsOverlay.cs:55`, `WaterDemoOverlay.cs:141`, `WaterProbe.cs:42`, `WaterRippleEmitter.cs:51`, `WaterChunkFillAnimator.cs:24`, cameras, demo movers | per component | overlays throttle string builds; 1 global float | fine |
| `Update` (internal, called from primary body) | `WaterInputRouter.cs:50` | once | keys + mouse; on click: raycast every body + exclusion test | fine |

Per-frame scheduling helpers: `WaterSimScheduler.EnsureSchedule` (`:18`) is frame-stamped; `WaterRuntimeRelevance.BuildSnapshot` (`:316-346`) runs once per (camera, frame): `CalculateFrustumPlanes`, per body `CullBounds` (8 `PoolToWorld`) + `TestPlanesAABB` + screen-coverage estimate, then `List<struct>.Sort(RelevanceComparer)` — allocation-free, N bodies tiny. CONFIRMED fine.

### 1b. Flags checked

- **Per-frame allocations**: none found in hot paths. `foreach` only over arrays/`HashSet<int>`/Input touches; no LINQ (`using System.Linq` = 0 files); string interpolation only in throttled overlays/warnings (`WaterCostProbe.cs:142-148` behind `ReadoutRefreshSeconds`; `WaterDemoOverlay` behind `FrameRateRefreshSeconds`). `WaterMetricsOverlay.OnGUI:66-68` allocates `_text.ToString()` + `new GUIContent` per OnGUI pass — dev-build only (`#if UNITY_EDITOR || DEVELOPMENT_BUILD`). CONFIRMED.
- **Direct `Shader.SetGlobal*` outside WaterUniformPublisher** (grep count): `PlanarReflection.cs` 3 (legacy), `WaterDebugView.cs` 3, `WaterOceanFft.cs:696-700` 5 (cascade textures/layout, documented as global by design, every FFT dispatch), `WaterParticlePool.cs:45` 1 (dead-buffer fallback, once). Inside the publisher itself 35 direct globals, all in the documented "camera/scene-global" paths (`PublishSharedGlobals`, `PublishExclusionVolumes`, `PublishUnderwater`, `PublishSceneLights`, `PublishWaterline`, `ClearBodyGlobals`) — none of them touch a sink-tracked id. **Material `.Set*` outside the sink**: `ApplyWaveUniforms(Material)` = 17 sets per caustic body per frame (`WaterUniformPublisher.cs:477-497`, called from `WaterCausticsPass.cs:127`), plus 7 large-body caustic material sets (`WaterCausticsPass.cs:225-237`) — uncached but small and documented. Publisher-tracked ids written out of band on CACHED blocks: #4.
- **Physics queries per frame**: only `WaterSplashRange.cs` (`Physics.IgnoreCollision` on activation, demo). Water surface picking is analytic (`TryRaycastSurface`). CONFIRMED none.
- **Unbounded per-frame loops over registries**: resolver scans `Bodies + _registered` per query (fine, N small) — the multiplier is queries-per-frame (#1, #3). `WaterRuntimeRelevance` per camera per frame: O(bodies log bodies).
- **`Camera.main`**: only in fallbacks (`WaterSimScheduler.cs:49`, `WaterVolume.Wiring.cs:83` play-mode once, `WaterFoamParticles.cs:709` only when body has no targetCamera, `WaterSplashRange.cs:281` cached). CONFIRMED fine.
- **`FindObjectsByType` per frame**: `WaterUniformPublisher.RefreshSceneLightCache:657-666` every `SceneLightCacheRefreshSeconds` and only when light-scatter > 0; `WaterVolume.Resolve():111-117` frame-cached fallback. CONFIRMED fine.
- **Sorting per frame**: `WaterRuntimeRelevance.cs:345` (struct list, tiny); `PublishSceneLights` insertion sort into fixed arrays. Fine.
- **Mesh rebuilds per frame**: none. River ribbon rebuilds are event-driven (`RequestRebuild` callers `WaterRiverSurface.cs:150-284`); clipmap/patch are transform placement only. CONFIRMED.
- **GPU readbacks + sync**: all async via `AsyncReadbackChannel` (one in flight per channel, cached delegate, stuck-latch, give-up latch). Per simulating body: sim height RT every `_readbackInterval` frames (default 1) and, on oceans, FFT height field; per sim: activity sleep-check float. Sync waits: `AsyncGPUReadback.WaitAllRequests()` on body disable only (`WaterVolume.cs:372`, documented); `WaitForCompletion` only in `#if UNITY_EDITOR && WEBGPUWATER_DEV` dump (`WaterVolume.ObstacleDebug.cs:147`). Cost item: #12.
- **Publisher cache cost (PLAUSIBLE)**: each `WriteBodyUniforms` pass = ~179 `Dictionary.TryGetValue` + `HashSet.Add` + getter derivations (`Matrix4x4.Rotate`, `VolumeExtentSafe` recomputed ~6×, `RiverMouthOutflowCount` refresh) × ~8-12 passes per ocean frame ≈ 2k dictionary ops/frame. Direction: derive once per frame into a flat indexed snapshot; per-target diff by index, not by id hash.

---

## 2. GOD CLASSES / STRUCTURE

| Class | Size | Two things it does | Natural seam |
|---|---|---|---|
| `WaterVolume` (33 partials, 8,065 lines, ~1,080 members, 159 SerializeFields, 74 public/514 internal) | #9 | Authoring data container AND runtime orchestrator AND static registry/global authority (`Primary`, `Bodies`, `FogSource`, `ResetStaticState`) | `IWaterModule` already exists (`IWaterModule.cs`, `WaterCollaboratorModules.cs`): OceanClipmap (278 l), SimWindowPatch (~200 l), Chunk (357 l), Underwater gate (678 l), Currents, Quality each own private state + their own MPBs → promote to modules. Static registry → `WaterBodyRegistry`. |
| `WaterUniformPublisher` (1,188 l) | 173 `PropertyToID` fields + one 340-line `WriteBodyUniforms` + scene-light gather + exclusion publish + sink/cache classes | Uniform derivation AND scene-light culling AND cache infrastructure | Split `CachedUniformSink`/`IUniformSink` into their own file; scene lights into `WaterSceneLightPublisher`; group `WriteBodyUniforms` into per-feature `Write*` methods (it is one function today). |
| `WaterFoamParticles` (1,356 l) | #10 | sim + draw + veil + diagnostics | see #10 |
| `WaterSimulation` (985 l) | ripple solver + foam + obstacle + sleep readback + scroll + injection queues | GPU kernel driver AND injection queue/sleep policy | `WaterInjectionQueue` (AddDrop/AddSphere/Flush/latch, :682-777) and `WaterSleepMonitor` (:909-960) out of the kernel wrapper. |
| `WaterSplashEmitter` (843 l) | #11 | runtime emission + Shuriken authoring | see #11 |
| `WaterSplashRange` (813 l) | demo: throwing, prop factory, HUD, containment physics | demo scene — fine as-is, but it is in Runtime, not Samples |
| `WaterSprayPump` (787 l) | cohesive (probe state machine); long but single purpose | none needed |
| `WaterOceanFft` (997 l) | FFT dispatch + CPU readback sampling + prediction | `OceanHeightReadback` (:706-882) is a separable CPU-side class |

**Duplicated logic (all CONFIRMED by reading each copy)**

- Surface height sampling: `WaterVolume.Facade.cs:167-231` ×3 + `WaterVolume.Query.cs:69-110` (#6); plus a fifth, different-answer height at `WaterVolume.Underwater.cs:623-660` (`SurfaceHeightAtWorldXZ`: rest plane + swell, ignores the sim readback — intentional for the gate, but undocumented as "differs from TrySampleHeight").
- Footprint/contains: `WorldToPoolXZ:188`, `QueryPoolXZ:233`, `ContainsPointXYZ:202`, `ContainsPointWithinMargin:213` (all `WaterVolume.Frames.cs`) + inline copy `WaterVolume.Facade.cs:225`. Four are legitimately different questions; the inline one is not.
- "Find body at point": `WaterVolume.Settings.Underwater.cs:220-236` (`ResolveContainingBody`, XZ footprint + nearest centre), `Query/WaterDomainResolver.cs:161-198` (`SelectContaining`, XYZ + specificity/volume), `Query/IWaterSurfaceProvider.cs:73-83` (`ExtraProviderContaining`, first hit, ribbons only), `WaterVolume.Resolve():108-118` (primary/any). Two parallel systems (legacy render-side vs gameplay) — documented at `:123-127`, but `BodyContaining` is still `public`.
- Spline projection lateral test: `WaterRiverSurfaceProvider.cs:184-197` ≡ `WaterRiverDisturbance.cs:338-349`; `ProjectAabbExtent` ×2 (#18).
- Mouth-outflow shader arrays cleared+filled in `WaterRiverSurface.cs:660-682` AND `WaterVolume.Currents.cs:211-240`.
- Exclusion uniform buffers allocated twice: `WaterUniformPublisher.cs:284-287` and `WaterFoamParticles.cs:197-199` (same MaxVolumes arrays, same `WriteVolumeUniforms` call).

---

## 3. PUBLIC API SURFACE

Scan: every `public` member in Runtime+Query, grep for the identifier across Runtime+Editor+Tests excluding the declaring file. 102 public members have zero references outside their own file (Samples~ excluded from the tree, so game-facing entry points are expected here — flagged as "documented API" where the doc-comment says so).

Top 20 candidates (all zero external refs, CONFIRMED by grep):

| Member | Declared | Own-file refs | Verdict |
|---|---|---|---|
| `WaterVolume.TryGetWaterHeight` | `WaterVolume.Facade.cs:167` | 1 (wrapper) | duplicate of `TrySampleHeight` → internal/delete (#6) |
| `WaterVolume.TrySampleSubmersion` | `Facade.cs:219` | 0 | zero callers anywhere; `IsSubmerged` uses it → fold into `TrySampleWorld` |
| `WaterVolume.IsSubmergedAt` | `Facade.cs:276` | 0 | documented gameplay façade; keep or move to Samples |
| `WaterVolume.SpawnRipple` | `Facade.cs:263` | 1 | façade; `TrySpawnRippleAt` has 3 refs — keep one |
| `WaterVolume.SampleHeightAcrossBodies` | `WaterVolume.Query.cs:58` | 0 | superseded by `WaterDomainResolver` → delete |
| `WaterVolume.SimWindowHalfExtent` | `Facade.cs:388` | 0 | forwards `internal SimHalfExtent` → delete |
| `WaterVolume.ApplyLookPreset` | `WaterVolume.LookPreset.cs:25` | 0 | documented runtime API; keep |
| `WaterVolume.SetFogQuality` / `ResetFogQualityToTier` | `WaterVolume.Quality.cs:213,221` | 0 | documented ("the game calls this"); keep |
| `WaterVolume.RebakeShoreDepth/ToggleShoreDepthDebug/ToggleShoreSdfDebug` | `WaterVolume.Bake.cs:25-35` | 0 | `[ContextMenu]` — make `internal` (ContextMenu works on non-public) |
| `WaterVolume.BodyContaining` | `Settings.Underwater.cs:128` | via internal callers | legacy resolve marked LEGACY → `internal` |
| `WaterConnection.DownstreamPort`, `OtherPortFor` | `WaterConnection.cs:47,52` | 0 | "FishingGame-style graph walks" — external consumer, keep but note untested |
| `WaterProbe.Emerged` | `WaterProbe.cs:30` | 0 | UnityEvent accessor; pair `Submerged` has refs → keep |
| `WaterRiverInteractor.EmitImpact` | `WaterRiverInteractor.cs:128` | 0 | public entry, no test → add test or internal |
| `WaterSurfaceDisturbance.TriggerFlick` | `:210` | 0 | same |
| `WaterBreachSplash.TriggerSplash/At` | `:174,177` | 2 | same |
| `WaterWaveGauge.CapturedCrestElevation/CapturedTroughElevation` | `:102-103` | 0 | unread telemetry → internal |
| `WaterStressScenarioController.CameraPathRunning` | `:81` | 0 | unread → delete |
| `WaterDemoOverlay.SetCaption` | `:127` | 0 | demo hook; keep or Samples |
| `OrbitCamera.SetView` | `:52` | 0 | demo camera; keep |
| `WaterSplashRange.Throw/ThrowCustom/ResetRange` | `:343,346,423` | 1-2 | demo public surface |
| Public mutable fields on `WaterExclusionVolume` (`wallScatterBoost:102`, `edgeColor:114`, `edgeIntensity:118`, `edgeSpread:124`, `affectParticles:146`, `particleFadeBand:151`, `particleDissolveSpeed:156`) | — | 1-3 | public fields violate "minimal surface / prefer immutability" → `[SerializeField] internal` + read-only props |

Also: struct fields on GPU-mirrored types (`WaterFoamParticles.cs:160-162`, `WaterSimulation.cs:198-204`, `LargeWaveField.cs:196-199,460-464`) are public because of layout mirroring — acceptable, but `internal` would also work.

---

## 4. DEAD CODE

- `#if false` / `#if FALSE`: **0**.
- `TODO|HACK|FIXME|XXX`: **0** in Runtime+Query.
- Commented-out code blocks: **0** found (pattern scan for `// <identifier>(…);` lines, then manual check).
- Unused private fields/consts (scan over class scope, partials merged): `WaterSimScheduler.InvalidFrame` (`:10`, declared, literal used instead — #17). `WaterExclusionVolume.MeshShapeId` (`:38`) has no C# reader but is machine-checked by `Editor/WaterWaveConstantsValidator.cs:273` — NOT dead (rule 3).
- Stale comments naming a state that no longer holds (these are the "real open issues"): `WaterUniformPublisher.cs:392-394` (#13); `WaterExclusionVolume.cs:398-400` (#2); `WaterWaveGauge.cs:157-159` (#15). Also `WaterVolume.Query.cs:46-47` "ownerHash is part of the seam for a future GPU cache … unused here" — a parameter carried through `IWaterHeightSampler.SampleHeights` for a feature that does not exist; either implement or drop.
- `WaterHeightQuery.RentResults` buffers are never trimmed; `Release` only on `OnDestroy` — fine.

---

## 5. STANDARDS VIOLATIONS

**Magic-number clusters**
- `WaterSplashRange.cs:225-499` (live logic, #20); `:670-736` prop geometry (data, acceptable); `:745-773` HUD `0.8f/0.7f` row spacing.
- `JerlovWaterTypes.cs` (38 literals): physical coefficient table — data, acceptable if the source is cited.
- `WaterSimScheduler.cs:11,14` (`-1` with an unused const beside it).
- Everything else uses named consts with WHY comments; `WaterWaveConstantsValidator` guards the HLSL pairs. Standard is met.

**Hardcoded strings**
- Shader property names: 479 `Shader.PropertyToID("…")` literals vs 142 through `WaterShaderProps`. 14 names declared in two files (drift risk, #16): `_WaveNormalStrength` (`WaterCausticsPass.cs:31`/`WaterUniformPublisher.cs:125`), `_SimSlopeToWorld` (`:30`/`:158`), `_PoolSlopeToWorld` (`:29`/`:157`), `_OceanFftActive` (`:32`/`:144`), `_LargeWaveAmplitude` (`:33`/`:152`), `_SimEdgeFadeTexels` (`WaterFoamParticles.cs:183`/`:142`), `_PatchPoolHalf`/`_PatchPoolCenter` (`WaterVolume.SimWindowPatch.cs:23-24`/`:162-163`), `_ParticleOpacity` (`WaterFoamParticles.cs:171`/`WaterFoamProfile.cs:25`), `_MouthOutflowCount/Origins/Directions/Parameters/FoamFrames` (`WaterRiverSurface.cs:57-61`/`:246-250`).
- Kernel names: `WaterFoamParticles.cs:21-26`, `WaterSimulation.cs` `RequiredKernels` — consts, good. `Shader.Find` only via `WaterShaderNames` consts (`WaterShaderNames.cs`) except `WaterWaveGauge.cs:336,356` (`DefaultLineShaderName`/`GaugeTextShaderName` — consts, Unity built-in shaders; fine). Layers via `WaterVolume.WaterLayerName` const. Menu paths via `AddComponentMenu` literals — accepted Unity idiom.

**Silent failure**
- `catch` blocks: 3 total. `AsyncReadbackChannel.cs:124` (intentional probe), `BoatController.cs:220` (logs), `WaterRiverSurface.cs:216` (#19 — loses stack).
- Boundary validation is strong: `SampleHeights`/`ResolveBatch`/`WriteVolumeUniforms`/`WaterSimulation` ctor/`WaterFoamParticles.OnEnable` all throw or `LogError`+disable. `if (x == null) return;` density is highest in demo files and OceanClipmap/Facade (`_sampler == null` "not initialized yet") — legitimate not-ready guards.

**Deep nesting (>4 control levels)**: none. Max = 5 at `WaterShoreDepthField.cs:245` (JFA inner loop, edit-time bake). Wrapped-argument indentation inflates line-indent scans but brace-depth confirms the standard is met.

**Composition/immutability**: `readonly` used consistently; public mutable fields only on `WaterExclusionVolume` (above) and the demo classes.

---

## 6. Query/ folder review (08-29, compiled, runtime-unverified)

**Correctness smells**
1. Double sampling on non-containing intents (#7) — cost, not correctness.
2. Hysteresis `SelectContaining` (`WaterDomainResolver.cs:161-198`): rule "incumbent keeps a tie" requires `previous.Specificity == best.Specificity && DomainVolume(previous) <= bestVolume` — since `RanksAbove` already chose the smaller volume, the `<=` reduces to `==`; correct but the condition reads as if a smaller previous could exist. When NOTHING contains the point, the widened previous wins (`ContainsPointWithin`) — but for an unbounded ocean `ContainsPointWithinMargin` widens only the vertical column (`WaterVolume.Frames.cs:213-228`), so a floater hopping out of an ocean's column keeps the ocean for 0.5 m — intended.
3. `SelectSurfaceInRange` (`:216-253`): previous wins when `previousGap - bestGap < 0.5` — a rival must beat the incumbent by the margin; tie on `BodyId asc`. Deterministic. Note `MaxVerticalDistance` uses `0` as "unset" → 10 m default (`:220-221`): a caller asking for exactly 0 m reach gets 10 m. Use `float.NaN`/negative sentinel or a nullable.
4. Static registries: `WaterSurfaceProviders._registered`, `WaterTopology._connections`, `WaterSimLeasePool._idle/_leases` are all cleared by `WaterVolume.ResetStaticState` (`Settings.Underwater.cs:351-354`) under `SubsystemRegistration` — CONFIRMED. Policy mismatch on id counters (#14). `WaterSimLeasePool.ResetStaticState` (`:111-116`) disposes idle contexts but NOT leased ones — relies on owners disposing first; fine as long as bodies always release (`WaterVolume.cs` module dispose order handles it).
5. Ordering assumptions: `WaterSurfaceProviders.Candidate(i)` enumerates `WaterVolume.Bodies` then `_registered` — registration order == `OnEnable` order, so `BodyId asc` tie-break (lazily assigned at `WaterVolume.Query.cs:153` on first `BodyId` read) depends on WHO first asked for an id, not on enable order. Deterministic per session, not across sessions. Acceptable for tie-breaks but worth documenting.
6. `WaterTopology.ApplySeamBlend` (`:94-155`): first matching connection wins ("one seam per sample"); with two connections on one body (source+mouth on a short river) inside both transition radii, the winner is registration order. Document or blend the nearer seam.
7. `WaterRiverSurfaceProvider.TryProject` memo (`:157-160`) is play-mode only and keyed on exact float equality of the point — fine for the "same point twice in one resolve" case it targets.
8. `ResolveExplicit` fake-null (#22) — correct, awkward.
9. `WaterDomainSample` carries both `Body` (WaterVolume, may be null for a standalone ribbon) and `Provider`; `GameplayBodyAt` returns `Body` → standalone ribbons resolve to null for Membership/Interactable/WaveGauge callers, silently. Documented in Membership only.

**API awkwardness for a future "create water system" tool**
- Two parallel resolve APIs (`WaterVolume.BodyContaining/Resolve/TrySampleHeightAt/IsSubmergedAt` statics vs `WaterDomainResolver`) — pick one public surface.
- `WaterDomainQueryOptions.BodyHint` is typed `WaterVolume`, so `ExplicitBody` cannot target a ribbon provider — type it `IWaterSurfaceProvider`.
- Ports/connections are wired by `WaterConnectionPort.body`/`river` serialized refs + a GUID `portId`; `WaterTopology.TryGetPort` is a linear string compare over connections. A system builder will want `WaterTopology.Connect(providerA, providerB, radius)` that creates ports+connection in one call and a `Dictionary<string, WaterConnectionPort>` index.
- `WaterSurfaceProviders` is `internal` while `IWaterSurfaceProvider` is `public` — a game cannot register its own provider (e.g. a lake mesh) despite the public interface.
- `WaterHeightQuery` (public class) is used only by `WaterBuoyancy` via a private static; either make it the documented batching handle for all callers or make it internal.
- Specificity is an int with two named constants (`VolumeSpecificity=0`, `RibbonSpecificity=1`) — an enum would let a tool reason about it.

---

## What to measure first (WaterCostProbe-friendly)
1. Profiler marker `WaterDomainResolver.Resolve` count/frame with a splash burst active (#1) and with a river + boat (#3).
2. `WaterVolume.SampleHeights` + `WaterDomainResolver.Resolve` self-time vs `WaterExclusionVolume` count (#2).
3. Worst-ms readout spike period ≈ 128 frames (#5).
4. `BodyPublicationMarker`/`GlobalPublicationMarker` self-time on an ocean (publisher cache overhead, §1b last bullet).
