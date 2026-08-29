# RESEARCH — True 3D Water Domains, Surface Providers, Exclusion Consistency, Simulation Pooling, Connected Topology, Independent Fog — 2026-08-29

**Status: ANALYSIS ONLY. No code has been written. Implementation awaits explicit GO.**

Scope: `Packages/com.abstractocclusion.webgpuwater` in ThreeJSWaterPort, plus the gameplay
constraints read from FishingAddon011025 (`Assets/NewTest/Scripts`) and FishingGameScafold.
KWS study was explicitly dropped by Bert ("we do better"). All claims below were verified
against current source on branch `codex/river` (HEAD `45a4aac`); file/line references are
from this session's reads. The working tree carries many unrelated modified files (foam,
FFT, fog, demo assets…) — none were touched and none will be.

---

## 1. VERIFIED CURRENT BEHAVIOR

### 1.1 Body resolution is XZ-only, with an unconditional Primary fallback — CONFIRMED

`WaterVolume.Settings.Underwater.cs`:

- `BodyContaining(worldPoint)` → `ResolveContainingBody(...)`: iterates `Bodies`, keeps any
  body whose `WorldToPoolXZ(worldPoint)` passes, tiebreaks by **horizontal** distance to
  centre (the comment says explicitly: "the footprint ignores height, so a vertical gap …
  must not sway the choice").
- `WorldToPoolXZ` (`WaterVolume.Frames.cs:188`) tests only pool X/Z ∈ [-1,1]. **Y is never
  examined anywhere in resolution.**
- When no footprint contains the point: `Resolve()` — `Primary`, else a frame-cached
  `FindFirstObjectByType<WaterVolume>()`. So a point 200 m above a lake, or between a pond
  and the sewer below it, still "belongs" to some body. The prompt's core claim is correct.

Consequences for stacked water: a pond above a sewer sharing XZ resolves by
nearest-centre-XZ — effectively arbitrary — and a point in the air gap resolves to
*something* rather than nothing.

Callers of `BodyContaining` (all inherit the defect): `WaterBuoyancy` (re-resolves every
`FixedUpdate`, no hysteresis), `WaterMembership`, `WaterSplash`, `WaterSplashEmitter` (×2),
`WaterBreachSplash`, `WaterInteractable` (×2), `WaterProbe`, `WaterWaveGauge`,
`BoatController`, `WaterSprayPump`, `WaterCausticsPass`. `WaterSplashRange:209` does an
explicit `?? Primary`. `BodyContainingForUnderwaterEffects` adds only a
`fullscreenVolumeFog` filter — same XZ containment.

### 1.2 The query seam is per-body, height-only, identity-less — CONFIRMED

`Runtime/Query/IWaterHeightSampler.cs`: `WaterSample { Height, Normal, Velocity, Valid }`.
No body identity, no signed depth, no inside/excluded flags, no intent, no transition info.
`Valid=false` conflates "outside footprint" with "not ready".

`WaterVolume.Query.cs / TrySampleWorld`: probe is flattened to `VolumeCenter.y` before
`QueryPoolXZ` — the query itself is 2D; the caller's Y only matters via
`SampleLargeWaveField(worldPoint.x, worldPoint.z, …)` (also 2D). `QueryPoolXZ` accepts
*every* point on an ocean clipmap (unbounded), by design.

Good foundations to keep: single shared per-point evaluator (batch ≡ single-point),
`WaterHeightQuery` per-owner reusable buffers (no steady-state allocs), profiler marker,
CPU-analytic from frame 0 (the WebGPU constraint honored), `SampleHeightAcrossBodies`
already resolves per point.

### 1.3 Exclusions are visual + a 3-caller CPU test; gameplay ignores them — CONFIRMED

`WaterExclusionVolume.cs` header states it outright: "Purely visual + camera-state:
buoyancy, physics and the ripple sim are untouched." Verified by grep: the CPU
`ContainsPoint` (line 401) is called from exactly `WaterInputRouter` (×2, click routing)
and `WaterVolume.Underwater.cs:311` (eye-in-dry-volume). **No buoyancy, splash, membership,
or height-query path consults exclusions.** A crate floats and splashes inside a carved
hull interior.

What exists and is solid:
- Box/Sphere are analytic; CPU `ContainsPointLocal` mirrors the shader's
  `PrimitiveContains` one-for-one (same `LocalHalfExtent = 0.5` convention), validator-guarded.
- Mesh shape carves via a depth prepass (`WaterExclusionDepthFeature`) and **already has a
  documented proxy policy**: `meshProxy` (Box|Sphere) answers the CPU point test, sun
  shadow column, and particle culling. The tooltip is honest about it. Part 3's "documented
  proxy policy" largely exists — it just isn't wired into gameplay.
- `MaxVolumes = 4` is a GPU-side cap (shader array, validator-guarded), warned once when
  exceeded. The CPU list is the same `_active` list.

### 1.4 Rivers: real spline current, render-only ribbon, no spline height — CONFIRMED

- `WaterRiverSurface.cs` is ribbon mesh ownership + renderer wiring only. It does not
  implement `IWaterHeightSampler` or any query contract. It borrows the parent
  `WaterVolume`'s animated uniforms and flips `_IsRiver` on.
- `WaterRiverCurrentField : WaterCurrentField` **is** genuinely spline-derived:
  `TryProjectPoint` → lateral bounds by ribbon width → tangent×speed, or the settled
  fluid-bake velocity when a valid `WaterRiverFluid` bake exists. It participates in
  queries through `WaterVolume.currentFields` → `SampleCurrentFields` → `WaterSample.Velocity`.
- **Height/normal for a point on a river come from the parent volume's rectangular pool
  plane** (`TrySamplePoolSurface`), not the ribbon. A sloped river reports the flat parent
  plane's elevation. `WaterRiverSplineSample` already carries `Position` (with Y), `Right`,
  `Tangent`, `Width`, `Speed` — everything a spline height/normal needs is computed and
  then dropped.
- Bert's framing: rivers are *a start* — the provider abstraction below is designed so the
  ribbon grows into a first-class surface rather than staying a skin.

### 1.5 Scheduler: per-body pause exists; resource pooling does not — CONFIRMED

`WaterSimScheduler.cs` (frame-guarded, order-independent): frustum visibility + activation
distance + `const int ActiveSimBudget = 4` (hardcoded, not per-tier) → nearest-N with a
stable registry-index tiebreak → sets `_visible` / `_simulate` per body. Edit mode: all on.

What pausing does *not* do: free anything. GPU resources are **per-body owned**:
`WaterSimulation` ping-pong RTs (heightfield, foam, flow), `WaterOceanFft` cascade RTs,
caustic RT (`WaterCausticsPass`), `PlanarMirror` RT, obstacle RTs. A paused body keeps its
full allocation; `Dispose`/`ReleaseAndDestroy` runs only on module teardown. There is no
leasing, no reassignment, no cross-body sharing, no per-tier budget, no ownership debug
view. On the plus side: RTs are created once, not per frame; paused bodies keep their last
readback height so floaters still float (`WaterVolume.Update.cs:135`) — the "dormant body
answers analytic queries" acceptance case is already true today and must stay true.

### 1.6 Quality tiers: fog knobs ride the global tier — CONFIRMED (with good plumbing)

`WaterQuality.cs`: three authored tiers (High/Medium/Low) + Auto probe (WebGPU/mobile →
Low, <2 GB VRAM → Medium). `Tier` is an immutable ~18-knob snapshot including
`UnderwaterMode UnderwaterFog` and `float FogSolveScale` (the 08-29 half-res work).
`WaterVolume.Quality.cs` copies them into runtime fields `_underwaterFogMode` /
`_fogSolveScale` that are deliberately **probe-writable at runtime** (the R-key A/B).
There is no *authored* way to promote fog independently of the tier — Low forces Simple
fog — but the runtime seam means Part 6 is mostly an authoring/API exercise, not a
rendering one.

### 1.7 The fishing side's actual contract (FishingAddon011025)

`Assets/NewTest/Scripts/Core/FishingWaterVolume.cs` is the gameplay authority today:
- A **BoxCollider column**; `SurfaceY = bounds.max.y` (flat); depth = `SurfaceY - y`.
- `TryFindContaining(position)` — a true 3D containment test (collider bounds), plus
  trigger-based enter/exit (`FromCollider`) in `vBaitBase`.
- `static Primary => activeVolumes[0]` — the same Primary pattern, first-registered.
- Per-volume composition: `vFishingZone`, `SwimZone`, `SurfaceBoundary`, `FishManager`,
  spawner, `FishSystemHookedWorldQuery`; `TacticsLabWorldQuery` needs `IsValidWater`,
  `ClampToWater`, open-water heading, target depth.
- Currently self-provisions a KWS dynamic-waves zone (to be replaced by our package).
- `FishingGameScafold/Assets/FishingGame` is UI-only today — **no water adapter exists yet**;
  the adapter will be written against whatever contract we expose. That is a freedom, not a
  constraint: we should expose exactly the domain-sample shape below and let the adapter be thin.

Notable: the fishing side already does 3D containment (colliders) while the water package
does 2D — integrating today would mean two disagreeing authorities. That is the strongest
practical argument for Part 1.

---

## 2. ARCHITECTURAL GAPS (delta between §1 and the target)

1. Resolution ignores Y; stacked domains unresolvable; air gaps claim membership.
2. Unconditional Primary fallback baked into every gameplay caller.
3. One implicit "nearest water" policy for all callers; no intent, no hint, no vertical
   limits, no hysteresis (buoyancy re-resolves raw every physics step).
4. `WaterSample` lacks identity/depth/inside/excluded/transition; callers can't tell why
   a sample failed.
5. Exclusions absent from every gameplay query path.
6. River height/normal not spline-derived; river surface outside the query system.
7. No provider abstraction: `WaterVolume` is simultaneously the registry, the resolver,
   and the only surface type (ocean clipmap is a flag inside it, ribbon is a skin on it).
8. Sim budget hardcoded (4), not per-tier; pausing frees nothing; no leases, no debug
   ownership, no reassignment hygiene.
9. No connection/port/topology concept at all (nothing found; only splines + current fields).
10. Fog quality inseparable from tier at authoring level.

---

## 3. PROPOSED PUBLIC CONTRACTS

Design stance: **extend, don't replace**. `IWaterHeightSampler`, `WaterSample`,
`WaterHeightQuery`, `WaterSimScheduler`, the analytic samplers and the tier system all
survive. New API is additive and small; existing callers migrate incrementally; legacy
behavior stays available behind explicit options so nothing silently changes.

### 3.1 Domain sample and query options (`Runtime/Query/WaterDomain.cs`, new)

```csharp
public struct WaterDomainSample
{
    public int BodyId;                  // stable per registration (see 4.1)
    public IWaterSurfaceProvider Provider;
    public float SurfaceHeight;
    public Vector3 SurfaceNormal;
    public Vector3 Velocity;            // waves + authored current
    public float SignedDepth;           // surfaceHeight - point.y (positive = submerged)
    public bool InsideDomain;           // full XYZ containment
    public bool Excluded;               // inside an exclusion volume (CPU policy, §3.4)
    public WaterSampleValidity Validity;// Valid | NoContainingBody | Excluded | NotReady…
    public WaterConnectionInfo Connection; // default when not near a seam (§3.6)
}

public enum WaterQueryIntent
{
    ContainingVolume,       // strict XYZ; never falls back
    BuoyancySurface,        // containing body, else nearest surface within vertical range
    RayInteraction,         // casting/clicks: surface hit semantics
    ExplicitBody,           // options.BodyHint is authoritative
    NearestWithinVerticalLimits,
}

public struct WaterDomainQueryOptions   // struct: no per-query alloc
{
    public WaterQueryIntent Intent;
    public int BodyHint;                // previous BodyId; enables hysteresis
    public float MaxVerticalDistance;   // for the nearest/buoyancy intents
    public WaterFallbackPolicy Fallback;// None (default) | PrimaryBody — explicit only
    public bool IncludeExcludedSpace;   // rendering/debug callers may want raw
    public WaterQueryFields Fields;
}
```

### 3.2 Resolver (`Runtime/Query/WaterDomainResolver.cs`, new; static like the current registry)

`bool Resolve(Vector3 point, in WaterDomainQueryOptions, out WaterDomainSample)` plus a
batched variant reusing the `WaterHeightQuery` owner-buffer pattern.

**Resolution rules (deterministic, in order):**
1. `ExplicitBody` hint → validate hint body only (containment per intent), done.
2. Gather candidates: bodies whose **XYZ** containment passes. Full-3D test = existing
   `WorldToPool` with the Y component finally examined (pool Y ∈ [-1,1]); ocean clipmap:
   XZ unbounded (as today) and Y ∈ [bedDepth-slack, surfaceHeight] — the exact ocean
   vertical rule is one of the few genuinely new decisions; proposal: below analytic
   surface height at that XZ and above `VolumeCenter.y - VolumeExtent.y`.
3. Multiple candidates (nested/overlapping domains): smallest volume wins (a pond inside
   an ocean's box should win), then nearest surface above the point, then registration
   index — every step deterministic.
4. Hysteresis: if `BodyHint` is among candidates and the winner beats it by less than a
   named epsilon (`DomainSwitchMarginMeters`), keep the hint. Kills boundary chatter
   without any static per-caller state.
5. Exclusion: winner found but point inside an active exclusion (per §3.4) →
   `Excluded = true`; for gameplay intents `Validity = Excluded` and the resolve reports
   false. `IncludeExcludedSpace` callers still get the full sample.
6. No candidate: `NoContainingBody`. **Never Primary** — unless
   `Fallback = PrimaryBody` was explicitly requested, which reproduces today's behavior
   for the callers that genuinely want it (`WaterMembership`'s lighting fallback, demo UI).
7. `BuoyancySurface` / `NearestWithinVerticalLimits` with no containing body: nearest
   provider surface within `MaxVerticalDistance` **directly above** the point (a fish/lure
   thrown above water must find the surface below it), tiebreak as rule 3.

`BodyContaining` is then reimplemented as
`Resolve(point, {Intent=ContainingVolume, Fallback=PrimaryBody, legacy XZ-only flag})` —
one internal switch keeps the old 2D test for bit-identical behavior until callers
migrate, then the flag dies. (Same migration shape as fogSolveScale default-1.)

### 3.3 Surface providers (`Runtime/Query/IWaterSurfaceProvider.cs`, new)

```csharp
public interface IWaterSurfaceProvider
{
    int BodyId { get; }
    WaterVolume Body { get; }           // owning body; null allowed for future free-standing providers
    bool ContainsXZ(Vector3 world);     // footprint
    bool ContainsPoint(Vector3 world);  // full XYZ domain
    bool TrySampleSurface(Vector3 world, WaterQueryFields fields, float minWavelength,
                          bool excludeInteractiveRipples, out WaterSample sample);
    Bounds DomainBounds { get; }
}
```

Implementations, in order of delivery:
- **PoolSurfaceProvider** — wraps today's `TrySampleWorld` (flat/analytic-wave volumes).
  Zero behavior change; it *is* the current path behind an interface.
- **OceanSurfaceProvider** — today's `openWater/IsOceanClipmap` branch (large wave field on
  top, unbounded XZ). CPU FFT data note: today's ocean height on the query path is the
  analytic large-wave field, not an FFT readback (readback is unavailable on WebGPU by
  design) — the provider makes that explicit rather than pretending.
- **RiverSurfaceProvider** — the real Part 2 payload: `TryProjectPoint` →
  `sample.Position.y` as height, `normalize(cross(Tangent, Right))` as normal, ribbon
  width bounds as footprint, current from the existing field. Rivers finally answer the
  unified contract from the actual 3D ribbon. Registered by `WaterRiverSurface` and
  **taking precedence over the parent volume's plane inside the ribbon footprint**
  (candidate rule 3 gets "provider specificity" ahead of volume size).
- Future custom surfaces implement the same interface.

Dormant-body guarantee: providers are pure CPU-analytic (they already are); a body with no
simulation lease answers every provider call. This is a stated invariant, tested.

### 3.4 Exclusion policy for CPU/gameplay

- Box/Sphere: **exact analytic**, the existing shader-mirrored `ContainsPointLocal`. 
- Mesh: **proxy only** (existing `meshProxy` Box/Sphere), stated in API docs and in this
  report — we do not claim exact CPU mesh containment. If a game case ever needs better,
  the documented upgrade path is an optional PhysX `MeshCollider.ClosestPoint` proxy,
  *not* silent inclusion now.
- Overlap determinism: exclusion always beats water ("dry wins"); multiple exclusions OR
  together (as the shader does).
- Wiring: the resolver applies it (§3.2 rule 5); `WaterInputRouter` keeps its existing
  check; `WaterBuoyancy`, splash emitters, breach splash, ripple input
  (`WaterInteractable`), spray pump migrate to the resolver and therefore inherit it.
  Per-probe cost is `activeCount × cheap matrix transform` with `MaxVolumes = 4` in
  practice — negligible; still, exclusion tests short-circuit on a squared-radius broad
  phase per volume to keep many-probe hulls cheap.

### 3.5 Simulation lease pool (extends `WaterSimScheduler`, does not compete)

The scheduler's frame-guard, eligibility, and stable tiebreak stay the authority on *who
deserves* simulation. Two additions:

1. **Budgets move to the tier** (`WaterQuality.Tier`): `MaxRippleSims`,
   `MaxFoamParticleSims`, `MaxPlanarReflections`, `MaxCausticContexts` (the hardcoded
   `ActiveSimBudget = 4` becomes the ripple default). Low can say 1/0/0/1; High 4/2/1/4.
2. **`WaterSimLeasePool` (new file)** — owns the *resources* the scheduler's decisions
   imply:
   - Preallocates per-tier context sets at tier apply (a context = the RT set + wrapper
     object one body's module needs: ripple sim RTs, foam particle buffers, planar RT,
     caustic RT). No RT creation in steady state (already true; becomes an enforced
     invariant of the pool instead of a habit).
   - **Lease**: when the scheduler marks a body `_simulate`, its collaborator modules ask
     the pool for their context instead of owning one. Priority score =
     (explicit importance, gameplay-activity flag set by the game via a public setter,
     camera relevance/visibility, distance) — folded into one comparable int so ties break
     deterministically, final tiebreak registration index (the scheduler's existing rule).
   - **Release/reassign**: losing a lease returns the context; the pool **clears state on
     reassign** (ripple RTs zero-filled via the existing sim clear dispatch, foam particle
     count reset, caustic RT cleared) so no wake/foam ghost leaks between bodies —
     acceptance case 15.
   - Body without a lease: keeps last readback height (today's behavior), answers all
     analytic queries (§3.3 invariant), renders the analytic surface without dynamic
     ripples — exactly what paused bodies do today, now with reclaimed memory.
   - Lifecycle: pool resets in `ResetStaticState` (the established Fast-Enter-Play
     pattern), disposes on tier change (resize = dispose + preallocate new sizes), and
     tolerates scene streaming (a leased body disabling returns its lease in `OnDisable`).
   - **Debug**: pool exposes `LeaseInfo { bodyId, contextType, score, reason }` enumerated
     by `WaterMetricsOverlay`/`WaterDebugView` — "which body owns each context and why".
   - WebGPU: the pool moves *ownership* of existing resources; it introduces zero new
     shaders/kernels/keywords, so WebGPU compatibility is untouched by construction.

Honesty note (final-report requirement): FFT/ocean cascades are **not** proposed for
pooling in this pass — today exactly one unbounded ocean is the realistic case and its
cascades are per-resolution stamped kernels; sharing across oceans is deferred and stated
as such. Underwater fog buffers are camera-owned (fog pass RTs), not body-owned — they are
already effectively pooled per camera and are out of scope for the body pool.

### 3.6 Connected-water topology (minimum viable, game logic stays outside)

New, deliberately small:
- `WaterConnectionPort` (component on a body/river end): stable authored string id
  (serialized GUID default), owning provider, world anchor + direction, authored flow sign.
- `WaterConnection` (component/asset): portA ↔ portB, `TransitionZone` (a Bounds or a
  spline-range on the river side), authored flow rate.
- Static registry mirroring the `Bodies` pattern, `ResetStaticState`-cleared.
- **Query handoff**: inside a transition zone the resolver fills
  `WaterConnectionInfo { connectionId, otherBodyId, blend01 }` and blends
  height/normal/velocity between the two providers across the zone (linear, named
  constants). Outside zones, `Connection` is default. That's it — enumeration API
  (`WaterTopology.ConnectionsOf(bodyId)`) lets FishingGame walk the graph for migration/
  unlocks; no hydrological simulation exists and none is added.

### 3.7 Independent fog quality (`WaterQuality` addition)

Authored override next to the tier selection:
`FogQualitySource { FollowTier, Explicit }` + explicit `UnderwaterMode` +
`FogSolveScale` fields, resolved in `ApplyTier` after the tier snapshot (override wins).
Runtime: `WaterQuality.SetFogOverride(mode, solveScale)` public — the game promotes fog
when large waves can cross the camera / near a surface crossing / exclusion boundaries
visible; the *triggers* are game-side, the package only obeys. Plumbing already exists
(`_underwaterFogMode` / `_fogSolveScale` are runtime, probe-writable, read per frame by
the fog pass) — this is authoring + a resolve rule, no rendering change. Default Low
stays exactly as shipped (override defaults to FollowTier).

---

## 4. KEY DESIGN DECISIONS & RATIONALE

**4.1 Stable identity**: `BodyId` = a monotonically increasing registration counter held
by the registry (not `GetInstanceID`, which is negative/editor-variant and reused across
Fast-Enter-Play sessions only by accident). Survives frames; documented as *not* surviving
scene reload (streaming systems that need persistence key on the port string ids of §3.6).

**4.2 Why hysteresis via hint, not static caches**: per-caller static state is what made
`WaterHeightQuery.Clear()` necessary; passing the previous `BodyId` in options keeps the
resolver stateless, thread-agnostic, and Fast-Enter-Play-safe for free.

**4.3 Why the resolver is static**: everything in this package is a static registry
(`Bodies`, exclusions, scheduler) with `ResetStaticState` discipline; a DI service would
be foreign style. Composition happens at the provider layer instead.

**4.4 Allocation budget**: options and samples are structs; batched paths reuse the
existing owner-buffer registrar; candidate gathering iterates `Bodies` in place (no LINQ,
no temp lists — same discipline as `EnforceSimBudget`'s O(n²)-over-allocation choice).

**4.5 The ocean vertical containment rule** (§3.2 rule 2) is the one place a new policy is
invented rather than extended; it's called out so it gets explicit review in the GO.

---

## 5. MIGRATION STRATEGY

Phase 0 ships the new API dormant (nothing calls it) → each phase migrates callers with
its tests → the legacy XZ switch is deleted last. At every phase the previous behavior is
reproducible via options, so any regression bisects to one caller migration, not to the
architecture. Defaults are bit-identical at each step (the fogSolveScale=1 precedent).

Caller migration order (risk-ascending):
1. `WaterProbe`, `WaterWaveGauge` (read-only diagnostics) — intent `ContainingVolume`.
2. `WaterMembership` — `ContainingVolume` + explicit `PrimaryBody` fallback (lighting
   wants a fallback; now it *says so*).
3. Splash/spray/breach emitters, `WaterInteractable` — `RayInteraction`/`ContainingVolume`,
   exclusion-gated (behavior change is the *point*: no splashes in dry volumes).
4. `WaterBuoyancy` — `BuoyancySurface` + `BodyHint` hysteresis (behavior change: floats
   stop working in dry volumes and in the air gap between stacked bodies).
5. `WaterInputRouter` — keeps its exclusion check, gains explicit intent.
6. `WaterCausticsPass` — `ContainingVolume`, no fallback.

---

## 6. FILE-LEVEL IMPLEMENTATION PLAN (small cohesive stages, each compiling + tested)

**Stage A — domain resolution + exclusion wiring (Parts 1+3)**
- NEW `Runtime/Query/WaterDomain.cs` — sample/validity/options/intent/fallback types.
- NEW `Runtime/Query/WaterDomainResolver.cs` — rules of §3.2 (legacy-XZ switch included).
- `Runtime/WaterVolume.Settings.Underwater.cs` — `BodyContaining` delegates to resolver
  (legacy options); `ResetStaticState` additions.
- `Runtime/WaterVolume.Frames.cs` — add the XYZ containment helper beside `WorldToPoolXZ`.
- `Runtime/WaterExclusionVolume.cs` — expose the per-volume test the resolver needs
  (internal, no new public surface).
- NEW `Tests/Runtime/WaterDomainResolutionFeatureTests.cs` — acceptance 1–8, 18, 19.

**Stage B — surface providers + river elevation (Part 2)**
- NEW `Runtime/Query/IWaterSurfaceProvider.cs`.
- `Runtime/WaterVolume.Query.cs` — pool/ocean providers wrap `TrySampleWorld` paths.
- NEW `Runtime/WaterRiverSurfaceProvider.cs` (+ registration in `WaterRiverSurface.cs`,
  precedence rule in resolver).
- `Runtime/WaterRiverSplineEvaluator.cs` — only if a normal helper is missing (verify first).
- Tests: acceptance 9, 10, 12 (`WaterRiverQueryFeatureTests.cs`).

**Stage C — lease pool (Part 4)**
- `Runtime/WaterQuality.cs` — per-tier budget knobs (defaults reproduce today: 4/∞-as-today).
- NEW `Runtime/WaterSimLeasePool.cs`; `Runtime/WaterSimScheduler.cs` — lease integration.
- `Runtime/WaterCollaboratorModules.cs` — modules acquire/release contexts.
- `Runtime/WaterMetricsOverlay.cs` — lease debug lines.
- Tests: acceptance 13–16 (`WaterSimLeaseFeatureTests.cs`).

**Stage D — topology (Part 5)**
- NEW `Runtime/WaterConnectionPort.cs`, `Runtime/WaterConnection.cs`,
  `Runtime/Query/WaterTopology.cs`; resolver seam blending.
- Tests: acceptance 11 (`WaterTopologyFeatureTests.cs`).

**Stage E — independent fog (Part 6)**
- `Runtime/WaterQuality.cs` + `Runtime/WaterVolume.Quality.cs` — override source + resolve.
- Tests: acceptance 17 (extend `QualityAndInteractionFeatureTests.cs`).

**Stage F — caller migration (§5) + steady-state allocation test + final report.**

Estimated new public API: ~6 types, all in the existing namespace, everything else
internal. No new shaders, no new keywords, no Resources folder, no asset moves.

---

## 7. RISKS & WEBGPU CONSTRAINTS

- **All new queries stay CPU-analytic.** No GPU readback may enter the resolve path
  (WebGPU builds have none; the package's founding constraint). The river provider and
  topology blending are pure math — safe.
- **Zero new kernels/shader variants** in stages A–F. The lease pool must reuse the
  existing sim clear dispatches for state hygiene, not add compute. Anything that would
  add a WebGPU-stripped variant is out of scope by design (07/08 memory: no webgpu
  renderer target for new kernels; stamped-kernel discipline for sizes).
- **Behavior changes are deliberate and land with their caller migration**, not with the
  architecture: floats/splashes dying in exclusions and in stacked-body air gaps will be
  *visible* in the demo scenes — each caller migration needs a scene sanity pass, and any
  demo relying on the Primary fallback (e.g. `WaterSplashRange`) gets the explicit policy.
- **Tier-change pool resize** happens at a frame boundary (tier apply already is one);
  leases invalidated mid-frame would be the classic leak — the pool API forbids mid-frame
  reassign by construction (single scheduler entry point).
- **Ocean vertical rule** (§4.5) needs Bert's eyes before Stage A.
- **Compile debt**: three prior rounds on this tree ("SHIPPED UNTESTED": fog half-res, FFT
  size, GMB2) have never been compiled; Stage A's first compile will surface their errors
  too. Budget for triage that isn't ours before concluding Stage A broke something.
- The many unrelated dirty files in the working tree stay untouched; every edit md5-gated
  per session discipline.

---

## 8. ACCEPTANCE-TEST MAP

| # | Case | Stage |
|---|------|-------|
| 1–6 | stacked XZ bodies, pond/sewer, air gap = none, no accidental Primary, explicit fallback, boundary hysteresis | A |
| 7–8 | box/sphere exclusion suppresses gameplay; mesh follows proxy policy | A |
| 9–10 | sloped river spline height/normal; current along spline | B |
| 11 | seam handoff | D |
| 12 | dormant body answers analytic queries | B (invariant test) + C |
| 13–16 | deterministic slot assignment, release/reuse, no stale state, tier resize | C |
| 17 | fog independent of tier | E |
| 18 | no steady-state managed allocations (Unity `Assert.That(..., Is.Not.AllocatingGCMemory())` style) | A + F |
| 19 | existing query/exclusion tests keep passing | every stage |
| 20 | WebGPU bindings valid | trivially satisfied (no new kernels); validator suite re-run each stage |

---

## 9. FINAL-DECISION SNAPSHOT (state of truth today, before any implementation)

- 3D domain cases supported today: **none** (XZ only, Primary fallback everywhere).
- Exclusions: box/sphere exact **on the CPU test that only input routing uses**; mesh =
  proxy (honestly documented); gameplay integration: **absent**.
- Simulations actually pooled today: **none** (per-body ownership; budget-paused only).
- River queries using real spline elevation: **no** (current: yes; height/normal: parent plane).
- Connected-water functionality: **none exists**.
- Blockers before FishingGameScafold integration: Parts 1–3 minimum (the addon already
  does 3D containment; shipping our 2D resolver against it would fork authority), plus the
  adapter itself (game-side, thin, written against §3.1).
- Tests/compilation: **nothing compiled or run this session — analysis only.** Three prior
  uncompiled rounds are stacked on this tree (see §7).

**Awaiting GO (or amendments) before any code.** Suggested first increment on GO: Stage A
alone, compiled, with tests 1–8 green, before anything else is touched.

---

## 10. IMPLEMENTATION RECORD — 2026-08-29, same session (GO received, option 2: authored A–F back to back, SHIPPED UNTESTED)

Unity was closed the whole session: **nothing here has been compiled or run.** First compile
will also surface the three earlier uncompiled rounds (fog half-res, FFT size, GMB2).

### New runtime files (8, each + minimal .meta)
- `Runtime/Query/WaterDomain.cs` — intents, options, validity, `WaterDomainSample`, connection info.
- `Runtime/Query/IWaterSurfaceProvider.cs` — provider seam + registry (extra providers; BodyId counter — counter deliberately survives ResetStaticState).
- `Runtime/Query/WaterDomainResolver.cs` — rules of §3.2 + `GameplayBodyAt` migration shim + batch API + profiler marker.
- `Runtime/Query/WaterTopology.cs` — connection registry, `ConnectionsOf`, seam blend (other side sampled AT the seam plane — a bounded provider can't answer past its own edge).
- `Runtime/WaterRiverSurfaceProvider.cs` — ribbon as provider: spline height, frame-Up normal, current field/spline velocity, lateral + authored-depth containment.
- `Runtime/WaterConnectionPort.cs` — GUID-once persistent port id, provider binding, authored flow.
- `Runtime/WaterConnection.cs` — port pair + transition radius + authored flow rate.
- `Runtime/WaterSimLeasePool.cs` — pooled `WaterSimulation` contexts (see honesty notes).

### Edits (19 runtime/test files, md5s in the session memory)
- `WaterVolume.Frames.cs` — `ContainsPointXYZ` (rest column, pool y ∈ [-1,0]; ocean = column only) + margin form.
- `WaterVolume.Query.cs` — WaterVolume implements `IWaterSurfaceProvider` (explicit members; public `BodyId` only).
- `WaterVolume.Settings.Underwater.cs` — `BodyContaining` re-documented as LEGACY; 3 new registries reset.
- `WaterSimulation.cs` — `ResetSimulationState()` (RT clears, queue reset, wake latch, readback-generation bump).
- `WaterCollaboratorModules.cs` — `SimulationModule` acquires/releases through the pool.
- `WaterQuality.cs` — `Tier.MaxSimulatedBodies` (default 4 = bit-identical) + per-tier fields; `FogQualitySource` override block + `ResolveFogMode/SolveScale`.
- `WaterVolume.State.cs` / `WaterVolume.Quality.cs` — budget runtime field; override-aware fog apply; public `SetFogQuality` / `ResetFogQualityToTier`.
- `WaterSimScheduler.cs` — budget = primary body's tier value (fallback 4).
- `WaterMetricsOverlay.cs` — lease debug lines (owner / resolution / reused, idle count).
- `WaterRiverSurface.cs` — `gameplayDepthMeters` (default 2), provider registration, current-field resolve.
- Callers migrated to the resolver (intent, hysteresis id, exclusion-aware, no implicit Primary):
  `WaterBuoyancy` (BuoyancySurface), `WaterSplash` (NearestWithinVerticalLimits),
  `WaterSplashEmitter` (drift: BuoyancySurface; burst: RayInteraction — bursts in carved space now emit NOTHING),
  `WaterInteractable` (BuoyancySurface + WaterlineY helper), `WaterSprayPump` (BuoyancySurface),
  `WaterBreachSplash` (RayInteraction), `WaterWaveGauge` (NearestWithinVerticalLimits),
  `WaterProbe` (NearestWithinVerticalLimits + EXPLICIT PrimaryBody fallback, per its documented contract).
- Left on the legacy resolve on purpose: `WaterMembership` (lighting wants a fallback),
  `WaterInputRouter` (already exclusion-aware), `WaterCausticsPass`, `BoatController`,
  `WaterSplashRange` (`?? Primary`), the `Facade` `*At` statics.

### New tests (5 files + 1 extended)
Domain rules 1–8 + batch validation + zero-alloc burst (fake providers); river height/normal/current/depth + dormant-analytic; topology identity/enumeration/seam continuity; lease retention policy + budget sanitisation; fog override independence; existing tier-sanitisation test extended for the new ctor arity.

### Honesty notes (deltas from the ideal)
1. **Pooling v1 = enable/disable reuse** (the streaming case), not live demand-leasing: a
   budget-paused body still keeps its RTs mid-session (unchanged behaviour). The acquire/release
   seam is where that increment lands. FFT cascades, caustics, planar mirrors: NOT pooled.
2. **Containment is rest-level**: a point inside a wave crest above rest is "not contained" —
   the buoyancy intent's below-surface search covers floaters; documented in code.
3. **GPU halves of acceptance 14/15** (real RT reuse across bodies, real state clears) and the
   fallback-to-a-live-Primary path need play mode; the tests pin the deterministic policy only.
4. **Buoyancy still samples via the parent WaterVolume batch path** — a river's spline elevation
   reaches single-point domain queries, not yet `WaterBuoyancy`'s batched probes.
5. `WaterExclusionVolume.MaxVolumes = 4` remains the GPU cap; the CPU test uses the full list.
