# ANALYSIS — Scaling to many connected water zones (visibility / proximity / relevance)
Date: 2026-08-31 · ANALYSIS ONLY, no code written, nothing touched.
**Rev 2** — corrected against Bert's review the same day. Correction log in §0.

Scope: what makes FPS fall when a scene holds many `WaterVolume` bodies + rivers + connections,
what the package already does about it, and where the remaining cost actually lives.
Every claim is quoted from a file read this session; guesses are marked **UNVERIFIED** and there
are no silent ones.

Files read: `WaterSimScheduler.cs`, `WaterSimLeasePool.cs`, `WaterCollaboratorModules.cs`,
`WaterReflections.cs`, `WaterVolume.Update.cs`, `WaterVolume.Quality.cs`, `WaterVolume.Waves.cs` (excerpt),
`WaterVolume.State.cs` (excerpt), `WaterVolume.Frames.cs` (excerpt), `WaterVolume.Underwater.cs` (excerpt),
`WaterQuality.cs` (excerpt), `WaterUniformPublisher.cs` (excerpt), `WaterExclusionVolume.cs` (excerpt),
`WaterSimulation.cs` (excerpt), `WaterCausticsPass.cs` (excerpt), `WaterFoamParticles.cs` (excerpt),
`WaterMembership.cs`, `WaterCostProbe.cs`, `WaterMetricsOverlay.cs`, `WaterRiverSurface.cs` (excerpt),
`WaterBedBaker.cs` (excerpt), `WaterShoreDepthField.cs` (excerpt), `WaterSeaStateFetchField.cs` (excerpt),
`Rendering/WaterCausticProjection{Feature,Pass}.cs`, `Rendering/WaterUnderwaterFog{Feature,Pass}.cs` (excerpts),
`Rendering/WaterExclusionDepthPass.cs` (excerpt),
`Query/{IWaterSurfaceProvider,WaterDomainResolver,WaterTopology}.cs`,
`WaterRiverSurfaceProvider.cs`, `WaterRiverSplineEvaluator.cs`,
`docs/RESEARCH_3D_WATER_DOMAINS_AND_SIMULATION_POOLING_2026-08-29.md` §3.5–3.6.

---

## 0. CORRECTION LOG (rev 1 → rev 2)

| # | Rev 1 said | Corrected |
|---|---|---|
| C1 | "Rivers have no underwater fog" (carried from the 2026-08-29 memory) | **Obsolete.** River fog + external fog-source handoff exist — `CanDriveExternalRiverFog` / `PrepareExternalRiverFogSource` in `WaterVolume.Underwater.cs`, consumed by `WaterUnderwaterFogFeature` via `WaterRiverSurface.TryFindExternalFogSource`. Trap removed. |
| C2 | Scheduler is "O(n² · containment-cost)" | **Wrong.** `IsSimEligible` is two bool flags + one `sqrMagnitude` compare — no containment call. It is plain O(n²) on cheap arithmetic; at 40 bodies it is very unlikely to be a bottleneck. Demoted from "leak" to "wrong shape, not a cost". |
| C3 | Foam overlay = fullscreen waste like caustic projection | **Different cost model.** `DrawFoamOverlays` issues one `cmd.DrawMesh` per collected above-surface renderer through `WaterSurface`'s `PondFoamOverlay` pass (shader pass 2). Cost is per-surface geometry + overdraw, not fullscreen. It still lacks visibility filtering. |
| C4 | Uniform section covered bodies only | **Incomplete.** Rivers were missing — see L1b. Also the ungated Update preamble (wave bank + three bake-validity checks) was not accounted for. |
| C5 | "VRAM held forever" | **Overstated.** Held by every *enabled and initialized* body, paused or not. `SimulationModule.Dispose` routes through `WaterSimLeasePool.Release`, so a **disabled** body returns its context, and at most `IdleRetentionCap = 4` are parked (the rest disposed). The ~8 MB/body baseline stands; ocean bodies add a caustic **mip chain** (`useMipMap = owner.IsOceanClipmap`). |
| C6 | "Push multi-probe callers through the existing `ResolveBatch` seam" | **Wrong.** `ResolveBatch` is literally `for (i) Resolve(points[i], …)` — no shared projection work. Collapsing a hull to one river-neighbourhood projection needs a real cache or redesign, not the existing seam. |
| C7 | Half-res sim demotion via `Release` + `Acquire` framed as cheap | **Understated risk.** `_simRes` also drives `SurfaceMeshDetail()` (`_windowed ? _meshDetail : _simRes`) for bounded water, the readback path, and solver state; `Acquire` calls `ResetSimulationState`, which **clears** the heightfield — so a demotion would visibly pop, not just get coarser. |
| C8 | Relevance rank could use `cameraSubmerged` as rank-0 | **Timing conflict.** The submerged/fog gate is resolved in `OnBeginCameraRender` — deliberately, *after* `Update` (the comment in `WaterVolume.Update` explains why: an Update-time read lagged the fog a frame). A scheduler running in Update can only use a one-frame-old `FogSource`, or the timing has to change. |
| C9 | P0.1 = "add `IsVisibleToCamera`" with the footprint issue as a caveat | **Promoted to the rule.** A plain `_visible` gate is unsafe: an off-screen surface can legitimately project caustics onto visible terrain. The gate must be projected-footprint relevance or a conservative ranked budget. |

---

## 1. WHAT ALREADY EXISTS — do not re-propose it

| Mechanism | Where | Rule today |
|---|---|---|
| Frustum visibility | `WaterSimScheduler.EnsureSchedule` | one `TestPlanesAABB` per body vs `CullBounds()`, once per frame, frame-guarded. Drives `_visible`. |
| Distance gate | `IsSimEligible` | `(VolumeCenter - camPos).sqrMagnitude <= activationDistance²`. `activationDistance` defaults to `CameraFarClip = 100f`. Ocean clipmap exempt. |
| Sim budget | `EnforceSimBudget` | nearest **N** eligible bodies simulate; N = `Primary.MaxSimulatedBodies` (tier, default 4, cap 16). |
| Planar budget | `WaterReflections` | hardcoded `MaxActivePlanarBodies = 3`, visibility-gated, distance-ranked, one recompute/frame. |
| Fog | `WaterUnderwaterFogFeature` | ONE `WaterVolume.FogSource`, per-camera `FogVolumeVisibleTo` cull (batch 4), **plus** the external river fog-source handoff (C1). |
| Exclusions | `PublishExclusionVolumes` | global, capped at `MaxVolumes = 4`, over-limit tie-break = nearest to the target camera. |
| Frame amortisation | tier | `CausticInterval`, `ReadbackInterval`, `OceanFftInterval`, `FogSolveScale`. |
| Foam particles | `WaterFoamParticles.LateUpdate` | already reads `volume.IsSimulating` and `volume.IsVisibleToCamera`. |
| Native-write dedupe | `CachedUniformSink` | ~2,000 → ~200–300 native property calls/frame/body (batch 3). |
| RT reuse | `WaterSimLeasePool` | reuse across enable/disable cycles; idle cap 4; **paused-but-enabled bodies keep their RTs** (v1 by design). |

The skeleton you want exists. What is missing is (a) two subsystems that never learned about
relevance at all, (b) any notion of *degree* of relevance, and (c) any spatial index on the query side.

---

## 2. THE COST LEDGER

### 2a. Scales with TOTAL body/river count, visible or not

**L1a. Per-body uniform publish is not visibility-gated.**
`WaterVolume.Update` calls `ApplyBodyBlock()` unconditionally — no `_visible` test.
`ApplyBodyBlock` → `WriteBodyProps` → `WriteBodyUniforms`, which contains **174 `sink.Set*` calls**
plus `ShoreDepth.WriteUniforms`, `SeaStateFetch.WriteUniforms` and `WriteOceanAperiodicUniforms`,
then five `SetPropertyBlock` calls. `CachedUniformSink` suppresses the *native* call on unchanged
values — it does **not** suppress the C# derivation or the bitwise compare. Every body in the scene
pays that every frame, whether or not its renderers are `forceRenderingOff`.
*Note:* `MembershipBlock` is a **separate**, lazily-built block pulled by `WaterMembership.LateUpdate`,
so gating `ApplyBodyBlock` would not starve floating objects — but it is a *second* block with its
own `CachedUniformSink` shadow, so it pays its own full derivation pass whenever a member asks.

**L1b. Every river republishes the parent body's whole uniform set — a THIRD block.** ← missed in rev 1
`WaterRiverSurface.LateUpdate() => PublishRendererProperties()`, unconditionally, which calls
`waterVolume.WriteBodyProps(_propertyBlock)` — the same 174-derivation pass, into a *different* MPB
and therefore a *different* cache shadow — then `PublishMouthOutflowProperties()` (five `Array.Clear`
+ an outflow rebuild), `ApplyRiverShaderOverrides()`, `PublishEndWaveAnchors()`, a loop over
`_rendererPropertySources`, and two `SetPropertyBlock` calls (above + under sheets). No visibility
gate. So a lake with three rivers pays the body publish **four** times per frame, plus the membership
block if anything floats in it.

**L1c. The Update preamble runs for every body regardless of visibility.**
Before the publish: `EnsureWaveBank()` recomputes ~5 effective wave values and compares a dirty set
every frame; `BedBaker.EnsureBaked()` and `ShoreDepth.EnsureBaked()` are two-flag early-outs (near
free); `SeaStateFetch.EnsureBaked()` does angle-delta and extent work *before* its own gate when
requested. Individually small — listed for completeness, not as a headline. The headline is L1a+L1b.

**L2. Screen-space caustic projection is per-body and has NO relevance gate. ← strongest finding**
`QualifiesForCausticProjection` = `isActiveAndEnabled && screenSpaceCaustics && CausticTexture != null`.
`WaterCausticProjectionPass` then draws **one fullscreen quad per qualifying body**, and a second
per body for refracted shadows. Worse: the caustic RT it samples is refreshed under
`if (_simulate && frameCount % _causticInterval == 0)`, so a budget-paused body contributes a
fullscreen pass sampling a **stale** texture. Cost is `O(bodies) × fullscreen`, unbounded by anything.
**But see C9:** the fix is not `_visible`. Caustics land on *terrain*, which can be on screen while
the water box is not, so the gate must be a projected-footprint relevance test or a conservative
ranked budget (the pattern `WaterReflections` already uses for planar).

**L3. Foam overlay has no visibility filter — but it is not fullscreen.** (corrected, C3)
`AnyFoamOverlayBody()` / `CollectFoamOverlayRenderers()` test `isActiveAndEnabled` + foam /
external-foam / river-mouth, never `_visible`. `DrawFoamOverlays` then issues one `cmd.DrawMesh`
per collected renderer through `WaterSurface`'s `PondFoamOverlay` pass. Cost = per-surface geometry
+ overdraw for surfaces that may be entirely off-screen. Smaller and more bounded than L2, and the
fix is simpler: filter the collected list, since redrawing an off-screen mesh has no defensible
purpose (unlike L2's terrain case).

**L4. VRAM is held by every enabled body, paused or not.** (corrected, C5)
Verified: `WaterSimulation` = 2 × `ARGBFloat` + 4 × `RGFloat` at `SimResolution` (High default 256)
= **4 MB**; `WaterCausticsPass` = 1 × `ARGB32` at `CausticResolution` (High default 1024) = **4 MB**,
**plus a mip chain on ocean bodies** (`useMipMap = owner.IsOceanClipmap`, explicit generation).
≈ **8 MB per enabled body** before foam buffers and planar RTs. Disabled bodies *do* release
(`SimulationModule.Dispose` → `Release`), with `IdleRetentionCap = 4` parked and the rest disposed.
So the exposure is "everything enabled", not "everything ever": 30 enabled zones ≈ 240 MB while only
4 simulate. Streaming already reclaims; a paused-but-enabled body does not.

**L5. `EnsureSchedule` is O(n²) — on cheap arithmetic.** (corrected, C2)
`EnforceSimBudget` ranks by counting, for each body, how many others are nearer, re-calling
`IsSimEligible` in the inner loop. That test is two bool flags plus one `sqrMagnitude` compare.
At n = 40 this is ~1,600 trivial iterations/frame. **Not a bottleneck.** It is listed only because
it is the wrong *shape* if a relevance rank (§3, Tier 1) replaces it anyway — a single sort is both
cheaper and more expressive. Do not spend a round on this alone.

### 2b. Scales with query points × bodies — the CPU cliff for gameplay

**Q1. No spatial index exists anywhere in Runtime.** Grepped octree / BVH / quadtree / spatial hash:
the only hit is a comment in `LargeWaterClipmap.cs` about the clipmap lattice.

**Q2.** `WaterDomainResolver.SelectContaining` walks `Bodies.Count + registeredProviders.Count`
per query point, calling `ContainsPoint` on each — for a `WaterVolume` that is `WorldToPool`
(a matrix transform) + 6 compares.

**Q3.** `WaterTopology.ApplySeamBlend` runs on **every valid sample**, linear over all connections,
calling `port.ResolveProvider()` twice per connection before it can reject. Its own comment says
"connections are authored and few" — true today, false in the scene being described.

**Q4. River containment is the expensive one.** `ContainsPointWithin` → `TryProject` →
`WaterRiverSplineEvaluator.TryProjectPoint` = `ProjectionSamplesPerSegment = 16` (+1) evaluations
**per spline segment**, then `ProjectionRefinementIterations = 6` golden-section refinements.
A 10-knot river ≈ 170 spline evaluations + 6 refinements **per query point**. There is a cheap XZ
bounds reject in front (good) and a memo — but a **single slot**, keyed on exact `worldPoint`
equality + frame stamp, so a hull with 20 buoyancy probes misses it 20 times per frame.
**And `ResolveBatch` does not help** (C6): it is a plain per-point loop over `Resolve`. Collapsing a
hull to one river-neighbourhood projection is a redesign or a real multi-entry cache, not free reuse
of an existing seam.

Composite: `probes × (bodies + providers) × containment` + `probes × connections`, per frame, main
thread. This is the term that explodes with zone count — **if** gameplay queries are hot at all,
which is exactly what §5 has to measure before anyone builds a grid.

### 2c. Structural limits

**S1. The tier is global and applied ONCE at startup.** `ApplyQuality`: "Called once at startup,
before the sim/caustic RTs are created, so the resolutions are fixed for the session". Every body
gets the same `SimResolution`, `CausticResolution`, `MaxWaveCount`, `RefineSteps`. No per-body LOD —
a pond at 95 m runs the same 256² solver as the one at your feet, until the budget kills it outright.

**S2. Scheduling is binary and un-hysteresised.** `_simulate` flips hard. Crossing a boundary at
60 fps can flip a body repeatedly, and re-arming costs `Wake()`, `FlushInjections`, a solver restart.
The domain *resolver* has hysteresis (`DomainSwitchMarginMeters = 0.5f`); the *scheduler* has none.

**S3. Three different distance metrics.** Scheduler: `VolumeCenter`. `WaterReflections.CameraDistanceSq`:
`body.transform.position`. Neither uses the nearest point of `CullBounds()`. A 200 m lake you stand in
ranks by its centroid, so a 3 m pond nearer that centroid can outrank the water under your feet — and
with `activationDistance` at 100 m, a large lake's centroid can fall out of range while its shore is
at the camera.

**S4. One scheduling camera.** `ScheduleCamera()` = primary → any body's camera → `Camera.main`.
Split-screen, a second gameplay camera and cutscene cameras all inherit the primary's decisions.

**S5. Ocean FFT is deliberately ungated.** `WaterVolume.Update`'s comment gives the reason (edit/play
parity; gating it would freeze the ocean surface). Do not "optimise" it.

**S6. The submerged/fog gate is a late-frame signal.** (C8) `FogSource` and the camera-submerged gate
are resolved in `OnBeginCameraRender`, after `LateUpdate` — deliberately, because an Update-time read
lagged fog by a frame on entry. Any Update-time scheduler can only consume it one frame stale.

---

## 3. PROPOSALS

### Tier 0 — plug the two real leaks

- **P0.1 — Give screen-space caustic projection a relevance budget** (L2, the strongest finding).
  Not a `_visible` gate (C9): the projection legitimately lands on visible terrain from an off-screen
  body. Two defensible options, decide before coding: (a) rank bodies and grant the nearest/most
  relevant K a projection, mirroring `WaterReflections`' planar budget exactly; (b) test the body's
  **projected caustic footprint** against the frustum rather than its water box. (a) is bounded and
  cheap; (b) is correct but needs a footprint bounds that does not exist yet. A body whose caustic RT
  is stale (not `_simulate`) is also a candidate to drop first, under either option.
- **P0.2 — Visibility-filter the foam overlay submissions** (L3). Simpler than P0.1: an off-screen
  surface mesh redrawn through `PondFoamOverlay` has no visible effect. Filter `CollectFoamOverlayRenderers`.
- **P0.3 — One distance metric: nearest point of `CullBounds()`** (S3), in both `WaterSimScheduler`
  and `WaterReflections.CameraDistanceSq`.
- **P0.4 — Hysteresis band on `_simulate`** (S2). Reuse the resolver's idiom: an incumbent keeps its
  slot until a rival beats it by a margin.
- **P0.5 — Address the uniform publish** (L1a + L1b). Bigger than it looks because of the river path:
  a gate has to cover `ApplyBodyBlock`, `WaterRiverSurface.PublishRendererProperties`, and the
  `MembershipBlock` interaction, and the batch-3 alias-chain rule (§4) applies to all of them.
  Worth doing, but *after* P0.1–P0.2 and only if instrumentation says it is on the critical path.
- **P0.6 — Move `MaxActivePlanarBodies` onto the tier.** Scoped in the 2026-08-29 research doc §3.5
  as `MaxPlanarReflections`, never shipped; `MaxSimulatedBodies` from that same list did ship.

### Tier 1 — ONE relevance rank, read by every subsystem

Today four subsystems each answer "which bodies matter" differently: `EnsureSchedule` (frustum +
centroid distance + count), `WaterReflections` (frustum + transform distance + count 3),
`CollectCausticProjectionBodies` (no gate), `CollectFoamOverlayRenderers` (no gate). They disagree.

Proposal: `EnsureSchedule` computes one `Relevance` score per body per frame from:

1. `visible` — the frustum test it already does.
2. `distance` — to nearest point of `CullBounds()` (P0.3).
3. **screen coverage** — projected area of `CullBounds()`. The signal the package has never had, and
   the one that matters: a large lake at 80 m deserves more budget than a bathtub at 15 m, and today
   it gets less.
4. `gameplayPin` — a public setter, so the game can pin the pond the player is fishing in
   (research doc §3.5 already proposes exactly this).
5. `cameraSubmerged` — **one frame stale, and must be labelled as such** (S6/C8). Either accept the
   lag or move the gate; do not pretend it is current.
6. tiebreak: registration index — the scheduler's existing deterministic rule.

Then one budget table on the tier (`MaxRippleSims` exists; add planar / caustic / foam), and every
subsystem takes top-K of the *same* ordering. One sort replaces four scans, decisions stop
disagreeing, and `WaterMetricsOverlay` can print "body / rank / score / what it was granted" — the
debug view this problem has no answer for today.

Risk: low. Each subsystem's current behaviour is reproducible as a special case, so it can ship
behind a legacy-ordering comparison test.

### Tier 2 — graded LOD — HIGHER RISK, separate phase (C7)

Once a rank exists, `_simulate` could become a band (full / reduced intervals / released). The
interval knobs (`_causticInterval`, `_readbackInterval`, `_maxWaveCount`, `_peakedRefineSteps`) are
already per-body runtime fields and are the safe half.

**Changing `SimResolution` per body is the unsafe half**, and rev 1 understated it: `_simRes` drives
`SurfaceMeshDetail()` for bounded water (so the surface *mesh* rebuilds), the readback path, and the
solver state — and `WaterSimLeasePool.Acquire` calls `ResetSimulationState`, which clears the
heightfield. A naive `Release` + `Acquire` demotion therefore **resets the water and pops visibly**.
Live lease release for band 3 (research doc §3.5) is the same family of risk. Treat all of this as
its own phase with its own GO, not as a rider on Tier 1.

### Tier 3 — the 3D grid, honestly assessed — AFTER measurement

- *Render side:* worthless. Tens of bodies, one AABB test each; a grid costs more than it saves. Skip.
- *Query side:* this is where it could pay (Q1–Q4). A **coarse XZ hash grid over provider bounds**
  with a y-range per entry, rebuilt on register/unregister and transform change (bodies are mostly
  static), turns `SelectContaining` into "test the 1–3 in this cell". Full 3D cells only pay if you
  genuinely stack many bodies vertically; the package supports stacking (pond over sewer) but the
  counts are small, so **XZ cells + per-entry y-interval** is the honest cost/benefit. It only shrinks
  the candidate set — the resolver's rule order is untouched.
- *Two smaller siblings:* index connections by `bodyId` so `ApplySeamBlend` becomes a lookup (Q3);
  and give the river projector a real multi-entry cache keyed on quantised position (Q4) — **not**
  `ResolveBatch`, which shares no work (C6).

**This is step 5, not step 2.** Build it only if the sweep in §5 shows queries are the measured cliff.

### Tier 4 — connected network as the LOD unit — last

`WaterTopology.ConnectionsOf(bodyId)` exists and nothing consumes it for performance. Propagating
relevance one hop along the graph keeps the river feeding your lake warm, so **the seam you are about
to cross does not pop** — a quality win as much as a cost one. Cost: a depth-1 BFS over an authored
graph. Requires the Tier 1 relevance model to be stable first.

---

## 4. TRAPS

1. **Do not gate the ocean FFT on `_visible`/`_simulate`** (S5) — the comment in `WaterVolume.Update`
   states the reason and it is correct.
2. **The batch-3 alias-chain rule applies to every new skip-gate.** Session memory
   (`perf-audit-2026-08-13`): a "skip the write" optimisation killed through-surface fog because
   `Chunk.cs` aliased publisher-tracked ids via `PropertyToID(NameConst)` and the audit regex missed
   it. Any L1-style gate must resolve the FULL alias chain — and L1b means the river publish path
   is in scope too.
3. **Culled ≠ irrelevant.** `_simulate` also gates `_sampler.RequestReadback()`; pausing a body
   freezes floaters at their last readback height. Today's behaviour, but any new band must keep the
   analytic query path answering (research doc §3.3 invariant).
4. **Off-screen water can still light visible terrain** (C9). The caustic gate is not a visibility gate.
5. **`activationDistance` defaults to 100 m** (`CameraFarClip`). Scenes with a far clip beyond that
   already pause bodies at 100 m — check before blaming a new gate for a frozen pond.
6. **Fog is single-source** (`WaterVolume.FogSource`) and its gate is late-frame (S6). River fog now
   exists and routes through the same single source via the external handoff — nothing here should
   try to make fog per-body.

---

## 5. MEASURE FIRST — the instrumentation gap

Source cannot tell you whether the drop is main-thread CPU (L1a/L1b + Q2–Q4) or GPU (L2/L3 + fog +
planar). Today's tooling cannot either:

- `WaterCostProbe`: frame ms / worst ms, and fog / god-ray / fog-scale toggles. No per-bucket cost.
- `WaterMetricsOverlay`: a `ProfilerRecorder` on the buoyancy-query marker, plus lease lines. Nothing
  counts bodies live/visible/simulating, uniform-publish time, caustic-projection draw count, or
  resolver calls per frame.

Minimum additions (diagnostic only):

1. Overlay counters: `bodies live / visible / simulating / planar-granted / caustic-projected /
   foam-overlay-drawn`.
2. A `ProfilerMarker` around `ApplyBodyBlock` **and** around `WaterRiverSurface.PublishRendererProperties`
   — proves or kills L1a and L1b independently in one capture.
3. Calls-per-frame counter on `WaterDomainResolver.Resolve` (the existing `ResolveMarker` gives ms;
   call count is what tells you whether Q4 is the problem), and a memo hit/miss ratio on the river
   projector.
4. A body-count sweep on the **Connected Waters Test Rig** (`GameObject > AbstractOcclusion >
   Connected Waters Test Rig`): 1 / 5 / 15 / 30 bodies, same camera path, record frame ms.
   The shape of that curve decides everything below.

---

## 6. RECOMMENDED ORDER (Bert's, 2026-08-31)

1. **Instrumentation and counters** (§5).
2. **Budget/rank caustic projections; visibility-filter foam mesh submissions** (P0.1, P0.2).
3. **Standardise nearest-bounds distance; add hysteresis** (P0.3, P0.4).
4. **Address body and river uniform publishing** (P0.5 — L1a + L1b together).
5. **Profile gameplay queries**; add the XZ provider grid, topology index and river cache **only if**
   queries are the measured cliff (Tier 3).
6. **Graded simulation LOD / live lease release** as a separate, higher-risk phase (Tier 2).
7. **Connected-network relevance propagation** once the common relevance model is stable (Tier 4).

Nothing above is implemented. Awaiting GO per increment, as usual.
