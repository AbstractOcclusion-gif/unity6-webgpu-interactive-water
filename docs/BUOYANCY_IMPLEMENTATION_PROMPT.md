# Advanced Buoyancy — Implementation Prompt

**Project:** WebGpuWater port (`Packages/com.abstractocclusion.webgpuwater`, namespace `AbstractOcclusion.WebGpuWater`).
**Goal:** A production-grade buoyancy system: a clean water-height query API, a batched multi-query path, two buoyancy fidelity tiers (probe + mesh-slicing), a stress-test harness, floater gizmos, and wizard support for large/complex objects.

Use this document as the working brief. Follow it top to bottom; each phase is independently shippable and testable.

---

## 0. Constraints & principles (read first)

- **WebGPU-first.** The deployed target is a WebGPU build. `SystemInfo.supportsAsyncGPUReadback` is **false** there, so any design that depends on GPU→CPU readback (as Crest's query system does) must have a first-class CPU path. The port already has byte-matched CPU mirrors of the wave math — lean on them (see §2).
- **Reuse, never rewrite; mine references, write fresh in our namespace.** Crest (`WaveHarmonic.Crest`) and NWH Dynamic Water Physics 2 (`NWH.DWP2`) live in the `KWSWater` reference project. Study them for the force models and batching ideas, then implement clean equivalents under `AbstractOcclusion.WebGpuWater`. Do **not** copy their code or ship their assemblies.
- **Ask before touching code.** This prompt is the plan; get Bert's go before each phase's implementation. Ship phases behind default-off/opt-in switches where they change existing behaviour.
- **House style.** No magic numbers (named consts/serialized fields), short single-responsibility methods, early returns, comments explain *why*, no dead code, minimal public surface, immutability where reasonable. Follow the existing quality-tier pattern (per-body tiers, forwarding accessors, publisher).
- **Two tiers, native.** Decision taken: ship **both** a light **probe** model and a heavy **mesh-slicing** model as selectable tiers, both reimplemented natively; plus an **optional** DWP adapter for users who own DWP2.

---

## 1. Current state (what exists today)

**Buoyancy**
- `Runtime/WaterBuoyancy.cs` — `WaterBuoyancy : MonoBehaviour` (`[RequireComponent(typeof(Rigidbody))]`). Multi-point lattice (`samplesPerAxis` 1–3 → up to 27 points) over the collider bounds. Each `FixedUpdate` resolves `WaterVolume.BodyContaining(transform.position)`, then per point calls the single-point query `TrySampleSubmersion(...)` and applies `AddForceAtPosition`: Archimedes lift (`ForceMode.Acceleration`), per-point linear drag, wave-drift push, plus angular + vertical settle damping. Submersion via a sphere-cap S-curve. Tunables: `buoyancy=2.5`, `waterLinearDamping=2`, `waterAngularDamping=1`, `samplesPerAxis=2`, `floatRadiusScale=1.5`, `waveDriftStrength=1`, `verticalSettleDamping=1`.
- `Runtime/WaterProbe.cs` — enter/exit-water `UnityEvent`s off `Body.IsSubmerged(point)`.
- `Runtime/WaterInteractable.cs` — object→water ripple emitter (separate concern; leave as-is).

**Water-height query API** — all on `WaterVolume` (partial), delegating to `internal WaterSurfaceSampler` (`Runtime/WaterSurfaceSampler.cs`). **Single-point only:**
- `bool TryGetWaterHeight(float x, float z, out float height)`
- `bool TryGetSurface(float x, float z, out float height, out Vector2 flow)`
- `bool TrySampleSubmersion(Vector3 p, out float depthWorld, out Vector3 up, out Vector3 worldFlow)` — the buoyancy query; pool-space so it's correct under rotation/tilt/non-uniform extent.
- `bool TrySampleHeight(Vector3 p, out float worldY)`, `bool IsSubmerged(Vector3 p)`
- `bool TryGetAnalyticWaterline(float x, float z, out float height)` — rest plane + wind waves, **valid from frame 0, no readback**.
- `bool TryRaycastSurface(Ray, out Vector3 hit)`
- Statics: `WaterVolume.TrySampleHeightAt`, `IsSubmergedAt`, `BodyContaining(worldPoint)`, `Primary`, `Resolve()`.

**Shared wave math (CPU ↔ GPU, kept in lockstep — this is the key asset for a CPU query path):**
- Wind waves: `Runtime/WaterWaveBank.cs` (`SampleHeight`, `SampleSlope`, `PackedA/PackedB`) ↔ `Runtime/Shaders/WaterWaves.hlsl` (`WaveHeight`, `WaveSlope`).
- Open-water swell: `Runtime/LargeWaveField.cs` (`EvaluateAtQuery`, `HeightAtQuery`, 4-iter `InvertToSource`) ↔ `Runtime/Shaders/WaterLargeWaves.hlsl`.
- FFT ocean height on CPU (desktop, via readback): `Runtime/WaterOceanFft.cs` `TrySampleField(x,z,out Vector3 heightSlope)` and `TrySampleHeightExtrapolated(...)`; gated by `OceanFftActive`. Analytic `LargeWaveField` is the always-valid fallback.

**Wizard** — `Editor/WaterWizardWindow.cs`. `MakeFloatable(go)` wires `Rigidbody + WaterInteractable + WaterBuoyancy(preset) + optional WaterSplash + WaterMembership`. Presets Light/Normal/Heavy set 3 fields only. **No per-object probe/hull authoring, no big-object support.**

**Gizmos** — only in `Editor/WaterVolumeEditor.cs` (volume box, sim-window wireframe, extent handles). **No floater/probe gizmo, no runtime `OnDrawGizmos` on buoyancy components.**

**Gaps this project fills:** (1) no batched multi-query, (2) no clean reusable height-provider seam, (3) probe model is ad-hoc (no surface velocity, no `minimumLength` LOD filtering, no mesh model), (4) no mesh-slicing buoyancy, (5) no stress test, (6) no floater gizmos, (7) wizard can't set up big/complex objects.

---

## 2. Reference approaches to mine (do NOT copy code)

**Crest 5.x query grid** (`KWSWater/Packages/com.waveharmonic.crest/Runtime/Scripts/Data/Query/`)
- `ICollisionProvider.Query(int hash, float minimumLength, Vector3[] points, float[] heights|Vector3[] displacements, Vector3[] normals, Vector3[] velocities, CollisionLayer, Vector3? center)` — one batched call per object, fill only the arrays you want.
- One shared `ComputeBuffer` (4096 pts) with **per-caller hash-keyed segments**; a **ring buffer of segment layouts** absorbs the 1–2 frame async-readback latency and carries registrations forward across skipped `FixedUpdate`s.
- Normals via 2 extra offset GPU samples (`dx=0.1`); **velocities via CPU finite-diff of the previous frame's results**.
- `minimumLength` → picks the coarsest cascade LOD so large objects ignore small ripples (stable boats). A 4-iteration displacement-field inversion finds the true surface point under a horizontally-displacing wave.
- `FloatingObject` force models: **Probes** = per-probe Archimedes spring `F = k·w·ρg·depth·up` applied `AddForceAtPosition` (emergent righting torque); **AlignNormal** = cubic single-point spring + explicit `up × waterNormal` torque. Anisotropic drag vs. water velocity + flow.

**Take from Crest:** the batched **hash-segment API shape**, `minimumLength`→LOD filtering, CPU finite-diff velocities, per-probe `AddForceAtPosition` for free torque. **Leave behind:** the async-readback dependency as the *only* path (WebGPU).

**NWH DWP2 mesh buoyancy** (`KWSWater/Packages/com.nwh.dynamicwaterphysics/Runtime/`)
- `WaterObject` slices each sim-mesh triangle at the waterline (per-vertex signed depth `d = P.y - waterHeight`; classify all-above / all-below / cut into 1–2 sub-triangles), then per submerged sub-triangle:
  - **Buoyancy (hydrostatic):** `F = ρ · A · depth · (n·û) · g` along the water normal (`fluidDensity=1030`).
  - **Dynamic pressure / slam:** `F = -ρ·A·(n·v̂)·|v| · n`, scaled by `slamForceCoefficient` when advancing, `suctionForceCoefficient` when retreating; optional `pow(|n·v̂|, velocityDotPower)` shaping.
  - **Skin/viscous drag:** `F = -(1-|n·v̂|)·skinDrag·ρ·A · v` (tangential).
- Sums per-triangle force + `cross(center-CoM, force)` torque. Performance: mesh **decimation** to `targetTriangleCount≈64`, convexify, vertex weld, serialized sim mesh, all in `FixedUpdate`, Burst.
- **Integration seam:** abstract `WaterDataProvider : MonoBehaviour` with `bool SupportsWaterHeightQueries()`, `void GetWaterHeights(WaterObject, ref Vector3[] points, ref float[] heights)` (+ normals/flow), auto-registered via a large trigger collider. `RaycastWaterDataProvider` and `FlatWaterDataProvider` ship as references.
- Editor: side-by-side sim-mesh preview, per-triangle gizmos coloured by submersion + force heat, an auto-setup wizard.

**Take from DWP2:** the **per-triangle slice + hydrostatic/dynamic/skin force decomposition**, mesh decimation pipeline, and the **provider-seam pattern**. Reimplement fresh.

---

## 3. Target architecture

### A. Water-height query facade (`IWaterHeightSampler`) — "the simple way to query water height"

Introduce a small public seam so gameplay code (and buoyancy, and a DWP adapter) never touches `WaterVolume` internals:

```csharp
namespace AbstractOcclusion.WebGpuWater
{
    public struct WaterSample          // one point result
    {
        public float  Height;          // world-space surface Y
        public Vector3 Normal;         // world surface normal
        public Vector3 Velocity;       // world surface velocity (orbital + flow)
        public bool    Valid;          // false = outside footprint / not ready
    }

    public interface IWaterHeightSampler
    {
        // Single point (thin wrapper over the batch path).
        bool SampleHeight(Vector3 worldPoint, out WaterSample sample, float minimumLength = 0f);

        // Batched: fill results[i] for each points[i]. minimumLength filters short ripples for big objects.
        void SampleHeights(int ownerHash, float minimumLength,
                           IReadOnlyList<Vector3> points, WaterSample[] results,
                           WaterQueryFields fields = WaterQueryFields.HeightNormalVelocity);
    }

    [System.Flags] public enum WaterQueryFields { Height=1, Normal=2, Velocity=4, HeightNormalVelocity=7 }
}
```

- `WaterVolume` implements `IWaterHeightSampler` (or exposes one via a module). Keep the existing single-point methods as thin adapters so nothing breaks.
- Resolve the body per query with the existing `WaterVolume.BodyContaining`; a batch may straddle bodies — resolve per-point or per-batch-centroid (start per-batch centroid for speed, document the limitation).

### B. Batched multi-query grid (§ "multiple buoyancy queries")

**Primary path is CPU-analytic** (works on WebGPU, zero readback latency, deterministic):
- Evaluate each point with the existing mirrors: rest plane + `WaterWaveBank.SampleHeight/SampleSlope` (wind waves) + `LargeWaveField`/`WaterOceanFft.TrySampleField` (swell/FFT). Compose exactly as `WaterSurfaceSampler.TrySamplePoolSurface` already does for one point — refactor that into a per-point function reused by the batch.
- **Velocity** = analytic time-derivative of the wave sum where available (wind-wave bank has closed-form `∂/∂t`), else CPU finite-diff of the previous frame's cached result (Crest's trick). Prefer analytic for wind waves; finite-diff for FFT.
- **Normal** from the slope the mirrors already return (`SampleSlope`), no extra samples needed (advantage over Crest's 2-extra-sample GPU normals).
- **`minimumLength` filtering:** skip/attenuate wave bands whose wavelength < `minimumLength/2` (mirror Crest's LOD-slice idea in CPU terms: clamp which `WaveBank` entries and which FFT cascade contribute).
- **Jobify:** implement the batch as a Burst `IJobParallelFor` over the points (the wave sum is embarrassingly parallel). Cache per-owner arrays keyed by `ownerHash` (mirror Crest's segment registrar, minus the GPU ring buffer). Provide a synchronous `Complete()` in `FixedUpdate` so buoyancy gets same-frame data.
- **Optional GPU acceleration (desktop only):** where `supportsAsyncGPUReadback`, reuse `WaterOceanFft`'s existing readback for the FFT term. Do **not** make this the only path.

Deliverable: `Runtime/Query/WaterHeightQuery.cs` (batch job + owner-segment cache) + `WaterSurfaceSampler` refactor to share the per-point evaluator.

### C. Buoyancy Tier 1 — Probe model (evolve `WaterBuoyancy`)

Refactor `WaterBuoyancy` into a proper probe model using the batch API:
- Author probes as a **weighted point set** (keep the auto-lattice as a default generator; allow explicit probe lists for tuned objects). Struct `{ Vector3 localPos; float weight; }`.
- Each `FixedUpdate`: build world probe points, one `SampleHeights(GetHashCode(), objectWidth, points, results)` call, then per probe: Archimedes spring `F = buoyancy · weight · ρg · depth · up` (clamp to a max), `AddForceAtPosition` for emergent torque. Add **surface-relative drag** (anisotropic, vs. `result.Velocity` + flow) and orbital push from `result.Velocity`.
- Add `objectWidth`→`minimumLength` so large probe objects ignore ripples.
- Keep existing tunables; add `maxBuoyancyForce`, `dragCoefficients (Vector3)`. Default-preserve current feel (gate new drag behind a sensible default; verify the existing demo floaters still behave).

### D. Buoyancy Tier 2 — Mesh-slicing model (native DWP-style), new component `WaterHull`

New `Runtime/WaterHull.cs` (`[RequireComponent(typeof(Rigidbody))]`) + a baked sim mesh:
- **Sim-mesh bake (editor):** decimate the source mesh to `targetTriangleCount` (start ~64, `[Range(8,256)]`), optional convex hull, vertex weld; serialize the result (mirror DWP's `SerializedMesh`). Write our own decimator or use Unity's built-in mesh simplification — **no third-party libs**.
- **Per-FixedUpdate:** transform sim verts to world; one batched `SampleHeights` over all sim vertices; per triangle compute signed depths, slice at waterline into submerged sub-triangles, and accumulate the three-term force (hydrostatic buoyancy + dynamic-pressure/slam + skin drag) exactly per §2's DWP decomposition, applied `AddForceAtPosition`.
- Named coefficients: `buoyancyCoefficient`, `fluidDensity=1030`, `hydrodynamicCoefficient`, `skinDragCoefficient`, `slamCoefficient`, `suctionCoefficient`, `velocityDotPower`. Expose read-out `submergedVolume`.
- Burst-jobify `CalcTri` over triangles; hand-expanded scalar math in the hot loop (no per-tri allocations).

### E. Optional DWP2 adapter (for users who own DWP)

Ship in an **optional assembly/folder gated by a define** (e.g. `WEBGPUWATER_DWP`) so the port has no hard dependency:
- `KWSWaterDataProvider : NWH.DWP2.WaterData.WaterDataProvider` — returns `true` for height (and normals/flow if cheap), overrides `GetWaterHeights(WaterObject, ref points, ref heights)` to call our batched `SampleHeights` and write world surface Y. Auto-registers via the trigger-collider pattern DWP expects.
- Document: users drop this on a trigger volume; their `WaterObject`s then float on our water. This is the fastest route to "DWP-grade behaviour" for owners while our native Tier 2 covers everyone else.

### F. Wizard enhancements (big / complex objects)

Extend `Editor/WaterWizardWindow.cs` Floating-Objects section:
- **Model picker:** Probe (Tier 1) vs. Hull (Tier 2), plus the existing presets.
- **Big/complex object flow:** for a Hull object, run the sim-mesh bake, preview original-vs-sim side by side (mirror DWP's preview), let the user set `targetTriangleCount`, convexify, and center-of-mass.
- **Probe authoring:** for Tier 1, expose `samplesPerAxis`/explicit probes and `objectWidth`.
- **Mass helpers:** optional "mass from volume/children" utilities (DWP has these) to get plausible densities.

### G. Floater gizmos (visualize floaters)

Runtime `OnDrawGizmos`/`OnDrawGizmosSelected` on both buoyancy components (follow the `WaterVolumeEditor` gizmo palette):
- **Probe model:** draw each probe as a sphere sized by its radius, coloured by submersion (above=grey, submerged=cyan), and a force vector (green→red by magnitude) at each probe.
- **Hull model:** draw the clipped submerged sub-triangles (cyan), the per-triangle force application points + normals, and the waterline slice — DWP's most useful debug view. Play-mode only for the sliced view; show the sim-mesh wireframe when selected in edit mode.
- Draw the sampled water surface points so the query grid is visible.

### H. Stress test harness + profiling

- **Scene builder** (editor menu, like the existing demo builders): spawn *N* floaters (both tiers) on an ocean body — parametrise count, probe/triangle budget, spawn area.
- **Metrics:** frame time, physics step time, query batch time (add lightweight `ProfilerMarker`s around the batch job and each tier's `FixedUpdate`). Log/overlay ms like the existing `DemoOverlay`.
- **Targets to validate:** e.g. 50 probe-floaters and 10 hull-floaters at the current ocean's 2.5–6 ms high-res budget without tanking physics; confirm WebGPU build (no async readback) stays correct via the CPU-analytic path.
- Correctness checks: a floater at rest settles at the waterline (no drift/jitter), rides wave slopes, self-rights; a hull object's slice follows the wavy surface (eyeball via gizmos).

---

## 4. Phased plan (each phase: get go-ahead, ship behind opt-in, Bert editor/WebGPU tests)

1. **Query facade + batched CPU-analytic grid** (§A, §B). Refactor `WaterSurfaceSampler` to a shared per-point evaluator; add `IWaterHeightSampler` + Burst batch job + owner-segment cache. *Deliverable:* `SampleHeights` returns correct height/normal/velocity for N points on pond + ocean, WebGPU-safe. No behaviour change to existing floaters yet.
2. **Probe model on the new API** (§C). Evolve `WaterBuoyancy`; preserve current feel by default; add drag/velocity/`minimumLength`. *Deliverable:* existing demo floaters unchanged or better; new drag opt-in.
3. **Floater gizmos — probe** (§G). Immediate debugging value for phases 1–2.
4. **Mesh-slicing Hull** (§D) + **sim-mesh bake** + **hull gizmos**. *Deliverable:* a boat/crate floats via per-triangle forces; slice visible in gizmos.
5. **Wizard** (§F) — model picker, hull bake/preview, big-object flow.
6. **Stress test harness** (§H) + profiling; tune tiers to budget.
7. **Optional DWP adapter** (§E) behind `WEBGPUWATER_DWP`.

---

## 5. Acceptance / verification

- A resting floater holds the waterline with no creep or buzz; disturbed, it bobs and self-rights.
- Floaters ride ocean wave slopes and the FFT crests; hull slice tracks the wavy surface (gizmo check).
- Batch query returns the same height as the single-point API for the same point (unit check), and runs on the WebGPU build (analytic path) without async readback.
- Stress scene hits the agreed floater counts within the frame budget; `ProfilerMarker`s show the batch job cost scaling linearly with point count.
- No regression to existing `WaterBuoyancy` demo scenes when new features are left at defaults.

---

## 6. Open questions for Bert

- Floater counts / triangle budgets to target for the stress test (mobile vs. desktop vs. WebGPU)?
- Do we need **surface flow** (rivers/currents) in the query now, or height+velocity first and flow later?
- Should Tier 2 hulls support **multiple water bodies** at once (a boat spanning a lake edge), or single-body per object to start?
- Is a **buoyancy quality tier on `WaterVolume`** (like the existing render tiers) desired, or purely per-floater component settings?

---

### Key file targets (new unless noted)
- `Runtime/Query/IWaterHeightSampler.cs`, `Runtime/Query/WaterHeightQuery.cs` (batch job + owner cache)
- `Runtime/WaterSurfaceSampler.cs` (refactor: shared per-point evaluator) — existing
- `Runtime/WaterBuoyancy.cs` (evolve to probe model) — existing
- `Runtime/WaterHull.cs` + `Runtime/WaterHullMesh.cs` (sim-mesh bake/serialize)
- `Editor/WaterHullEditor.cs`, `Editor/WaterBuoyancyGizmos.cs` (or runtime `OnDrawGizmos`)
- `Editor/WaterWizardWindow.cs` (extend) — existing
- `Editor/WaterStressTestBuilder.cs` (menu)
- `Runtime/Integrations/KWSWaterDataProvider.cs` (gated by `WEBGPUWATER_DWP`)
