# RESEARCH — River Harmonization: full first-class rivers + connect-to-body — 2026-08-29

**Study only. No code was written or modified. Unity was not opened. Every claim below was
re-verified against the working tree this session (branch codex/river, the post-domains state);
claims I could NOT verify are listed in §8, not silently assumed.**

Builds directly on `docs/RESEARCH_3D_WATER_DOMAINS_AND_SIMULATION_POOLING_2026-08-29.md` §10
(the shipped domain/provider/topology layer — compiled clean per Bert 08-29 evening, runtime
still unverified) and on the fog sagas (fps-cliff, godray-fog-surface-sync, single-solve,
half-res, F3 v2 doctrine).

---

## 1. VERIFIED CURRENT BEHAVIOR (file/line evidence)

### 1.1 A river today is 5+ hand-wired scripts — CONFIRMED

The full authored stack, as the demo rig documents it
(`Editor/WaterBuildKit.ConnectedWatersDemo.cs`, `CreateDemoRiver` / `CreateDemoSeam`):

| piece | file | role | wiring step |
|---|---|---|---|
| `WaterRiverSpline` | `Runtime/WaterRiverSpline.cs` | knots (localPosition/tangent/width/speed) | `spline.knots = new List<WaterRiverKnot>{...}` |
| `WaterRiverCurrentField` | `Runtime/WaterRiverCurrentField.cs:18-21` | spline→velocity; folds fluid bake | `currentField.Configure(spline)` **+ `lake.currentFields = new[]{currentField}`** |
| `WaterRiverSurface` | `Runtime/WaterRiverSurface.cs:34-43` | ribbon mesh + renderer + provider registration | `surface.Configure(spline, lake, ctx.MatAbove, samples)` |
| `WaterRiverFluid` (optional) | `Runtime/WaterRiverFluid.cs:9-48` | `[RequireComponent(WaterRiverSurface)]`, bake asset + ~12 solver knobs | add + assign `bakeData`, bake via `Editor/WaterRiverFluidBaker.cs` |
| `WaterRiverFoam` (optional) | `Runtime/WaterRiverFoam.cs:9-29` | `[RequireComponent(Surface, Fluid)]`, 4 shading knobs | add + `Configure(maskStrength)` |
| parent `WaterVolume` | `WaterRiverSurface.cs:36` `waterVolume` field | animated uniforms source (`LateUpdate → PublishRendererProperties`), fog medium | assigned in `Configure` |
| ports + connection (optional) | `CreateDemoSeam` | seam blend | 2 GameObjects + port refs + `WaterConnection` wiring |

⚠️ **Trap found in the recipe itself**: `CreateDemoRiver` does
`lake.currentFields = new WaterCurrentField[] { currentField };` — it **REPLACES** the lake's
current-field array. Fine in a fresh rig; a facade doing this on a user's lake would silently
delete their other current fields. The facade must APPEND (and remove only its own entry).

### 1.2 The river IS a first-class query surface since 08-29 — CONFIRMED

`Runtime/WaterRiverSurfaceProvider.cs`:
- `TrySampleSurface` → `sample.Height = spline.Position.y` (real spline elevation),
  normal = frame `Up`, velocity = current field, falling back to `Tangent * Speed` (lines 60-77, 118-126).
- Containment = lateral half-width (`sample.Width * 0.5 + tolerance`) × vertical column
  `[surface − gameplayDepthMeters, surface]` (lines 47-57).
- `Specificity => RibbonSpecificity` (=1) beats `VolumeSpecificity` (=0)
  (`IWaterSurfaceProvider.cs:59-60`) — inside its footprint the ribbon outranks the parent plane.
- Registered/unregistered by `WaterRiverSurface.OnEnable/OnDisable` (`WaterRiverSurface.cs:67-85`).
- `Body => _surface.WaterVolume` — **the parent volume, null allowed for a standalone ribbon**
  (lines 35-38).

### 1.3 …but every gameplay caller collapses back to the parent VOLUME — CONFIRMED

`WaterDomainResolver.GameplayBodyAt` (Resolver:72-91) returns `sample.Body` — a `WaterVolume`.
When the river provider wins, `Body` is the parent lake (or null standalone). Then:

- **Buoyancy** (`WaterBuoyancy.cs:200-211`): `_body = GameplayBodyAt(...)` then
  `_body.SampleHeights(ownerId, ...)` — the parent volume's batch path. A crate mid-slope
  floats at the LAKE plane. On a standalone ribbon (`Body == null`) it does not float at all.
  This is exactly the §10 honesty note 4, confirmed at source.
- **Splash** (`WaterSplash.cs:48-66`): resolves body, then
  `body.TryGetAnalyticWaterline(x,z, out surfaceY)` — parent analytic surface. Entry detection
  and the emit height are both wrong on a sloped river (fires at the lake plane's height).
- **Membership** (`WaterMembership.cs:52`): still legacy `WaterVolume.BodyContaining` (XZ-only,
  deliberate per §10 — lighting wants a fallback). A crate in a ribbon outside the parent's
  footprint gets `SetPropertyBlock(null)` → primary globals.

`WaterDomainSample.Provider` EXISTS (`Query/WaterDomain.cs:119`) — the resolver already hands
back the winning provider; the callers just don't use it yet. **Gameplay parity is therefore a
caller-side change, no new resolver API needed.**

### 1.4 Underwater fog is 100% volume-driven; a river can never be the fog source — CONFIRMED

The gate chain, all in `Runtime/WaterVolume.Underwater.cs` + `WaterVolume.Settings.Underwater.cs`:

1. `OnBeginCameraRender` (Underwater.cs:143-158, primary body only, target camera only) →
   `fogSource = BodyContainingForUnderwaterEffects(cam.position)`.
2. `BodyContainingForUnderwaterEffects` (Settings.Underwater.cs:131-165) iterates `Bodies`
   (WaterVolumes ONLY), requires `body.fullscreenVolumeFog` and `WorldToPoolXZ` footprint,
   tiebreaks on horizontal centre distance, then falls back Primary-first among fog-eligible
   bodies. **Rivers are not `WaterVolume`s → never candidates.**
3. `fogSource.UpdateUnderwaterState(cam)` (Underwater.cs:~286-370): republishes that BODY's
   globals, `surfaceY = SurfaceHeightAtCamera` = `SurfaceHeightAtWorldXZ(cam.x, cam.z)`
   (Underwater.cs:569-600: `VolumeCenter.y` + FFT-predicted / analytic wave field — the parent
   plane), computes submerge/straddle/arm gates from `VolumeCenter.y + envelope + FogArmBandMeters`
   (Underwater.cs:460), then
   `Publisher.PublishUnderwater(eyeInWater, surfaceY, unbounded, fogSimple, fogArmed, dry)`
   (Underwater.cs:359-361).
4. `WaterUnderwaterFogFeature.AddRenderPasses` self-gates on `WaterVolume.UnderwaterFogActive`
   / `WaterlineActive` (Feature:51-103) and reads `WaterVolume.FogSource`.

Net effect for a camera under a river ribbon: if the parent lake's footprint reaches there,
it gets the LAKE's fog at the LAKE's surface height (wrong under a sloped ribbon, and
`eyeInWater` itself flips at the wrong height); if not, whatever other fog-eligible body wins
the fallback — or nothing. Matches Bert's "actually no underwater fog" observation.

The Simple/Full fork: `fogSimple` published from `_underwaterFogMode == Simple`
(Underwater.cs:346-352) rides the `WATER_FOG_SIMPLE` multi_compile (the 07-29 keyword fix);
Simple = closed-form FLAT waterline at `surfaceY`, "wave-aware at the camera's xz" per the
comment at the publish site. **This is the exact vehicle axis C needs — it already exists and
costs zero new variants.**

Fog quality override plumbing also confirmed: `WaterQuality.cs:310-333`
(`FogQualitySource {FollowTier, Override}`, `ResolveFogMode`, `ConfigureFogOverride`).

### 1.5 Ripples on rivers are OFF by design, in the shader — CONFIRMED (new finding)

`Runtime/Shaders/WaterSurfaceFragStages.hlsl:90-95`:
```
float interactiveRippleWeight = 1.0 - saturate(_IsRiver);
// A river has no valid coordinates in the rectangular interactive-ripple simulation. The
// vertex stage therefore excludes this field too; matching that gate here keeps its shading
// normal attached to the analytic wind-wave geometry instead of an unrelated volume texture.
info *= interactiveRippleWeight;
```
So even a perfectly-routed `AddRipple` into the parent's sim window can never SHOW on a ribbon —
both vertex displacement and fragment shading are gated. Injection side
(`WaterVolume.Facade.cs:35-56`): world→sim-window frame, out-of-window drops silently. River
ripples are a **shader + UV-domain feature, not a wiring fix** — axis D must say so honestly.

### 1.6 Topology: exists, blends queries, is entirely hand-authored — CONFIRMED

- `WaterConnectionPort.cs`: GUID-once `portId` (`EnsurePortId`, Reset/OnValidate, never
  regenerated), `ResolveProvider()` = override → river → body; `FlowDirection = transform.forward`.
- `WaterConnection.cs`: portA/B + `transitionRadiusMeters` (min 0.1, default 4) + authored flow;
  registers in `OnEnable`, warns loud when half-wired.
- `WaterTopology.cs`: static registry + `ConnectionsOf(bodyId, results[])` (allocation-free) +
  `ApplySeamBlend` — seam PLANE at the port midpoint, slab falloff, other side sampled AT the
  plane, 50/50 at the plane, **first registered connection wins overlaps**, applied by the
  resolver on every valid sample.
- Nothing generates ports, nothing cleans them, rendering knows nothing about seams. All
  authoring is the 3-GameObject dance in `CreateDemoSeam`.

### 1.7 Spline projection cost — measured from source

`WaterRiverSplineEvaluator.cs:9-10,86-121`: `TryProjectPoint` = coarse scan of
**17 samples × EVERY segment** + 6 bisection refinements ≈ `17·S + 7` Bezier evaluations per
call (S = segment count; 41 evals for the 2-segment demo river). No AABB pre-reject inside
`TryProject`; `WaterRiverSurfaceProvider.DomainBounds` recomputes over all knots on every
property read. Numbers in axis E.

---

## 2. GAPS (delta to "rivers as harmonized as lakes")

1. Authoring: 5+ components, ~7 wiring steps, one overwrite trap; no wizard/menu entry for a
   river; no validation ("spline but no surface", "fluid without bake", half-wired seam).
2. Connections: fully manual (3 GameObjects, 6 field assignments); no derivation from spline
   ends; no regeneration/cleanup story; no render-side seam blend.
3. Gameplay: resolver knows the river; buoyancy/splash/membership discard the provider and
   re-query the parent volume (§1.3). Standalone ribbons (no parent) get NO gameplay at all.
4. Fog: rivers cannot be a fog source; surfaceY and every arm gate are parent-volume math (§1.4).
5. Ripples: shader-gated off for rivers (§1.5).
6. Perf hygiene: unbounded coarse projection over all segments, per-read bounds recompute.

---

## 3. DESIGN PER AXIS

### AXIS A — Authoring: the `WaterRiver` facade

**One component, on one GameObject, orchestrating the existing pieces. It owns WIRING and
VALIDATION only — zero duplicated knobs (reuse-never-rewrite: every setting keeps living on the
component that consumes it; the facade's inspector surfaces the siblings' editors, it does not
copy their fields).**

Minimal serialized surface (proposed `Runtime/WaterRiver.cs`):
```
[SerializeField] WaterVolume parentVolume;          // animated uniforms + fog medium; null = standalone (validated warning)
[SerializeField] Material surfaceMaterial;          // must be WaterSurface shader (Surface.Configure already validates)
[SerializeField] bool fluidBake;                    // adds/keeps WaterRiverFluid (+Foam gated below)
[SerializeField] bool foam;                         // adds/keeps WaterRiverFoam (requires fluidBake — RequireComponent chain)
[SerializeField] WaterRiverEndConnection sourceEnd; // axis B (per-end connect struct)
[SerializeField] WaterRiverEndConnection mouthEnd;  // axis B
```
Everything else (knots, samplesPerSegment, gameplayDepthMeters, solver knobs, foam knobs)
stays where it is. The facade:
- `Reset()`/`OnValidate()` ensures the sibling `WaterRiverSpline` + `WaterRiverCurrentField` +
  `WaterRiverSurface` exist and are cross-wired via the EXISTING internal `Configure` seams
  (`Surface.Configure` WaterRiverSurface.cs:134, `CurrentField.Configure` :67/:74,
  `Foam.Configure` :71). Component ADDS happen from an editor button / `ObjectFactory`, never
  from `OnValidate` (Unity forbids Add/DestroyImmediate there — see Risks).
- Parent wiring **appends** its current field to `parentVolume.currentFields` if absent, and
  removes exactly its own entry when unlinked/destroyed (§1.1 trap).
- Validation warnings, fail-loud per AGENTS.md: no spline knots, material wrong shader,
  standalone ribbon ("no parent volume: no animated waves, no fog medium, gameplay via provider
  only"), foam without fluid, half-wired ends.

**Wizard**: new `Editor/WaterBuildKit.River.cs` partial + GameObject-menu creator
("GameObject/AbstractOcclusion/River", priority beside the existing creators — same policy as
`ConnectedDemoMenuPriority`). It re-hosts the `CreateDemoRiver` recipe: default 3-knot descending
spline, facade component, parent picked from selection or scene Primary, material from the
BuildKit material context. `ConnectedWatersDemo.CreateDemoRiver` then DELEGATES to the same
builder (one recipe implementation, per reuse-never-rewrite).

**Migration for hand-wired rivers**: additive adoption. Dropping `WaterRiver` onto an existing
river GameObject finds the existing siblings (`GetComponent`), adopts them (no field moves, no
data loss — their serialized state is untouched), reports what it adopted, and only creates what
is missing. Existing scenes without the facade keep working unchanged — the facade adds no
runtime behaviour the siblings don't already have; it is optional forever.

### AXIS B — Connect-to-body

**Per-end serialized struct on the facade:**
```
enum WaterRiverEndTarget { None, WaterBody }
struct WaterRiverEndConnection {
    WaterRiverEndTarget target;
    WaterVolume body;                     // the lake/ocean/reservoir to connect to
    float transitionRadiusMeters;         // default = WaterConnection's default (4)
    // generated, serialized, owned by the facade:
    WaterConnectionPort riverPort;        // child GameObject
    WaterConnectionPort bodyPort;         // child GameObject
    WaterConnection connection;           // child GameObject
}
```

**Port derivation from the spline ends** (all data already exists):
- River port: `TryEvaluateSegment(0, 0)` for the source, `(lastSegment, 1)` for the mouth
  (`WaterRiverSpline.cs:TryEvaluateSegment`) → position = `sample.Position`, forward = the
  authored downstream direction (`+Tangent` at the mouth, `−Tangent` at the source — matching
  the demo's "forward points downstream" convention).
  `authoredFlowRate = sample.Width * sample.Speed` (the demo's own formula).
- Body port: the river-end position projected toward the body's footprint — clamp the
  river-port anchor to the volume's pool rect (`WorldToPoolXZ` inverse), at the body's rest
  level. The demo used ~1.5 m separation; derived form: walk from the river port along the
  port forward until entering the footprint, capped at `transitionRadiusMeters`.

**When to (re)generate — BOTH, but with different verbs:**
- Explicit "Generate / Update Connection" button (editor) = creates or moves ports, with Undo.
- `OnValidate` and spline-change events (`WaterRiverSurface.ConfigurationChanged`,
  `WaterRiverAuthoringChangeRouter` already routes Transform edits) only RE-POSITION already-
  generated ports (moving a Transform is safe there) and flag staleness; they never create or
  destroy objects (Unity lifecycle — see Risks). A helpbox "connection out of date — press
  Update" covers the flagged state.

**Stable ids / no duplicates**: the facade's serialized refs are the ownership record —
regeneration always reuses `riverPort`/`bodyPort`/`connection` when the refs are alive (their
GUID-once `portId` survives, streaming/save keys stay valid), creates only when null, and
"Remove Connection" destroys exactly the referenced objects. **Never search-and-destroy by name
or proximity** — a user's hand-authored ports must be untouchable. Switching `body` to another
volume keeps the same port objects (same portIds) and rewires `bodyPort.body` — id-stable
across retargeting.

**Render blending at the seam**: SEPARATE future increment, not R1. Honest reasons: the ribbon
and the lake surface are different draw calls with different vertex domains; blending them needs
either vertex-stage pull of the other surface's height (a new cross-surface data path) or an
opacity feather on the ribbon over the lake (cheap but only hides the seam from above, does
nothing for the waterline/fog). The transparent queue-3000 sorting trap (perf-audit memories)
applies to any feather. Recommendation: ship query-side seams (already live) first; scope a
"ribbon mouth fade" as its own small increment after R3, when it can be judged against the fog
behaviour at the seam.

**What FishingGame still misses from `WaterTopology`** (vs. the §1.7 fishing contract):
- Port lookup by persistent id (`WaterTopology.PortById(string)`) — saves key on portId, but
  nothing resolves one today.
- Global connection enumeration is index-based only (`ConnectionCount`/`GetConnection`); fine,
  but `ConnectionsOf` takes a session `bodyId` — a save/streaming system needs
  `ConnectionsOf(portId)` or bodyId↔portId translation to survive reloads.
- Flow semantics helper: "which end is downstream of body X" currently requires reading
  `AuthoredFlowRate` sign + port order; one convenience accessor
  (`connection.DownstreamPortFor(bodyId)`) keeps game code out of sign conventions.
All three are small, allocation-free additions to `WaterTopology`/`WaterConnection` — R1 scope
if Bert wants them, R4 otherwise.

### AXIS C — Underwater fog on rivers, v1 (FLAT waterline at the river's height)

**Design, honestly bounded**: v1 gives a camera under a river ribbon the EXISTING Simple-mode
(flat-waterline) fog at the RIVER's local surface height, with the medium (Jerlov/fog colors,
density, downwelling) coming from the parent volume. The WAVY river waterline is explicitly
future work (sketch below). No volume-fog default or behaviour changes; scenes without rivers
are bit-identical by construction (every new branch is gated on "a river claimed the camera").

Mechanism (all CPU/gate-level, ZERO new shader variants — it rides the existing
`WATER_FOG_SIMPLE` fork):
1. **Detection**: in the primary's `OnBeginCameraRender`, BEFORE
   `BodyContainingForUnderwaterEffects`, ask the registered river providers whether one
   contains the camera (`WaterRiverSurfaceProvider.ContainsPointWithin(camPos, margin)`), via a
   small `WaterSurfaceProviders` helper — resolver-consistent (same containment math the
   gameplay domain uses) and cheap (rivers are few; AABB reject from axis E applies). A river
   wins only when a ribbon column actually contains the camera — a camera in the LAKE next to
   the mouth keeps today's path even though the ribbon's XZ footprint may overlap.
2. **Fog source**: the river's PARENT volume (`WaterRiverSurface.WaterVolume`) — it must be
   fog-eligible (`fullscreenVolumeFog`); a standalone ribbon (no parent) gets NO fog in v1,
   validated + documented (the facade warns, §A). The parent publishes its own globals exactly
   as today (`UpdateUnderwaterState` → `PublishBodyGlobalsTracked`) so Jerlov/medium/absorption
   all come from the body — no new uniform.
3. **Height + gates override**: the frame's `surfaceY` becomes the RIVER surface at camera XZ
   (`provider.TrySampleSurface(camPos, Height, ...)`); the submerge test, the fog-arm ceiling
   and the waterline straddle band for that frame use river-local heights
   (`riverSurfaceY ± FogArmBandMeters` / `WaterlineArmPad` — the river has no FFT envelope,
   so `envelope = 0`, which reduces the bands to exactly the pond form they were derived from,
   Underwater.cs:455-470). Implementation shape: `UpdateUnderwaterState` gains an optional
   "external surface override" (height + flatness) parameter/struct rather than a fork —
   one method, one new argument, defaults preserving today's byte-for-byte behaviour.
4. **Force Simple for river-sourced frames**: publish `fogSimple = 1` regardless of tier mode
   (`Publisher.PublishUnderwater(..., fogSimple: 1, ...)`) so the shader takes the closed-form
   flat waterline at the published `surfaceY`. Full-mode marches would march the PARENT's wavy
   surface — actively wrong under a ribbon. This piggybacks the existing keyword — the fps-cliff
   memory's whole point is that this fork is a real compile-time fence, so river fog inherits
   the CHEAP path by construction.
5. **Bounded fog**: rivers publish as bounded (`unbounded = 0`) so the fog clips to the body
   box… **CAVEAT verified**: the box is the PARENT volume's; a ribbon climbing above the
   parent's extent would have its fog clipped at the parent's box top. v1 accepts this
   (documented); if it shows in practice the parent volume simply needs to enclose the river —
   an authoring rule the facade can validate (warn when the spline leaves the parent box).

**Touched globals, complete list** (all existing): `_CameraUnderwater` (eyeInWater at river
height), `_UnderwaterSurfaceY` (river height at cam XZ), `_UnderwaterUnbounded`,
`_UnderwaterFogSimple`, `_UnderwaterFogArmed`, `_CameraDryVolume` — i.e. exactly the
`PublishUnderwater` six, plus the parent's body globals it already publishes. Nothing new.

**What breaks when the camera crosses a seam underwater** — stated plainly: `surfaceY` and the
gates flip from river-local to lake math on the frame the camera leaves the ribbon column
(hysteresis on the containment margin softens the flip point but not the value jump). Near the
mouth the two heights converge by construction (the river descends to the lake level — demo:
mouth knot at y=0.1 vs lake 0), so the pop is small in sane authoring; a river mouth
significantly above its lake will pop. Mitigation available cheaply if needed: lerp the
published `surfaceY` across the connection's transition slab using the SAME
`WaterTopology.ApplySeamBlend` math (CPU-side, camera only). Recommend shipping v1 WITHOUT it
and testing — one more moving part in the most regression-scarred area needs a sighting first.

**Regression fences (from the fog memories — the traps that WILL bite):**
- Do NOT touch the Simple/Full defaults, the arm-gate formulas for volume-sourced frames, the
  debug-view color contracts, or the C1/half-res alpha contract. River frames only OVERRIDE
  inputs (surfaceY, envelope, fogSimple) — never the formulas.
- `WaterCostProbe`'s F-toggle is session-sticky and forks god rays too — any river-fog repro
  session must start with the FOG_GATES debug view (flat color, B = Simple).
- The half-res solve (`fogSolveScale`) and debug-view force-full-res interplay is untouched —
  river frames change no RT logic.
- Test the exclusion-wall-above-water + straddle suite (the C1 regression suite) with a river
  in the scene and the camera NOT in it — must be bit-identical.

**Wavy river waterline — future, cost sketch**: two candidate routes. (a) Shader-side spline
eval: the fragment/vertex stages would need the knot array + projection ≈ 17·S+7 Bezier evals
per classification point — prohibitive per-pixel, and the F3 v2 doctrine says the NEAR-field
waterline must stay effectively analytic at sub-texel precision, which a coarse approximation
breaks (the v1 F3 pop). (b) F3-style height RT: rasterize the ribbon strip into
`_WaterHeightRT` so the existing far-field march sees the river surface; near field then needs
a river-analytic flat fallback (this v1!) — so v1 is a prerequisite, not throwaway. Route (b)
is the honest path; estimated as its own R-sized increment (RT authoring + march windowing +
the F3 rebase warnings), do not bundle.

### AXIS D — Gameplay parity

**Buoyancy (the core of R2)**: switch `WaterBuoyancy` from `GameplayBodyAt` + volume batch to
`WaterDomainResolver.Resolve` keeping `sample.Provider`:
- Winner is a `WaterVolume` (BodyId == body's own) → EXACTLY today's `_body.SampleHeights`
  batch path, untouched, bit-identical (FFT/ripples/readback all preserved).
- Winner is a river provider → per-probe `provider.TrySampleSurface(worldPoint,
  HeightNormalVelocity, objectWidth, ignoreInteractiveRipples, out sample)` loop into the SAME
  rented results buffer (`SharedQuery.RentResults` — no new allocations; the provider is
  CPU-analytic, no readback, valid from frame 0). One projection per probe; cost in axis E.
  `up` stays `Vector3.up`-equivalent for rivers (frame Up is the sample normal; lift along the
  volume-up convention needs a decision — recommend: keep `_body.VolumeUp` when parent exists,
  world up when standalone, and let the sample normal drive only the surface-relative terms —
  flag for Bert, this is a feel choice).
- Standalone ribbon (Body null) now floats — resolve keeps the provider even with no volume.
  This also fixes the falling-lure story on rivers via the existing BuoyancySurface
  below-surface search (the surfaceBelowPointOnly rule from the domains round).

**Membership**: migrate `WaterMembership` to
`GameplayBodyAt(pos, ContainingBody-intent, PrimaryBody fallback, ref id)` — the fallback
preserves the lighting-wants-a-fallback doctrine (§10 kept it legacy for exactly that reason;
the resolver's `WaterFallbackPolicy.PrimaryBody` reproduces it). A crate in a river then
resolves the RIVER domain whose `Body` is the parent → parent's `MembershipBlock` — the correct
lighting/caustics/fog medium for river water. Standalone ribbons: fallback → primary (today's
implicit behaviour, now explicit).

**Splash**: `WaterSplash` keeps its resolver intent but must take the surface height from the
DOMAIN, not `body.TryGetAnalyticWaterline`: resolve with `Fields = Height` and use
`sample.SurfaceHeight` for the under/over test and the emit position. On volumes this is the
same analytic value through the provider seam (bit-identical goal — verify in test); on rivers
it is the spline height, so splashes fire AT the ribbon. `body.ResolveSplashEmitter()` keeps
working (parent supplies the emitter; standalone ribbons need the explicit `emitter` override —
validated warning).

**Ripples — the honest statement**: injection routes exist (`AddRipple` → parent sim window)
but the surface shader hard-gates interactive ripples off for rivers (§1.5), for the stated
reason that the ribbon has no valid coordinates in the rectangular sim domain. Making river
ripples real requires: valid sim-window UVs for ribbon fragments (or a river-space ripple
domain), the vertex-stage displacement ungate, and shading normal reattachment — a
shader-and-sim increment of its own, NOT part of R2. R2 scope for rivers: splash particles +
foam react (world-space systems, already fine) + the impact ripple lands in the PARENT's water
around the ribbon (visible in the lake at a mouth splash — correct and free). Recommend
logging river-ripple as a roadmap item with route (river-space ripple window pinned to the
spline) rather than half-shipping.

### AXIS E — Perf / WebGPU

- **Everything above is CPU-analytic on the query side**: spline projection + authored current;
  no readback anywhere (river provider never touches sim RTs — confirmed §1.2); fog v1 changes
  gates/uniform values only. WebGPU-safe by construction.
- **Zero new shader variants**: axis C rides the existing `WATER_FOG_SIMPLE` multi_compile;
  axes A/B/D are C#-only. (Axis C explicitly does NOT justify a new variant in v1.)
- **Projection cost, measured from source** (§1.7: `17·S + 7` Bezier evals/projection, each
  eval a cubic Bezier + frame build):
  - Demo river (S=2): ≈41 evals ≈ low single-digit µs. One 8-probe floater ≈ 9 projections
    (1 resolve + 8 samples) ≈ 370 evals per FixedUpdate — negligible.
  - Stress case (S=10, 20 floaters): 20 × 9 × 177 ≈ 32k evals per FixedUpdate ≈ 1.6M evals/s
    at 50 Hz — likely ~1-2 ms of main-thread CPU. Worth the two cheap fixes below BEFORE R2
    ships, not after a complaint:
  1. **Coarse AABB reject** in `WaterRiverSurfaceProvider.TryProject`: cache `DomainBounds`
     (invalidate on `ConfigurationChanged` / spline `NotifyChanged` — the events exist) and
     early-out before any projection. Kills the cost for every probe NOT near the river
     (the common case: resolver candidate scans).
  2. **Per-call projection reuse**: `ContainsPoint` → `TryProject` and `TrySampleSurface` →
     `TryProject` each project independently; the resolver may hit both for one sample. A
     one-entry memo (last worldPoint + result, frame-stamped) halves it without any cache
     policy risk. (Segment-level AABB rejection inside the evaluator is the next lever if a
     100-knot river ever exists; not needed now.)
- **No steady-state allocations**: provider loop reuses the rented buffer; port/connection
  regeneration is editor-time; `ConnectionsOf` is caller-array. The one watch item:
  `DomainBounds` per-read recompute is also an alloc-free but O(knots) cost — the cache above
  fixes both.
- Fog v1 adds per-frame: one river containment test per registered ribbon (AABB-rejected) + one
  projection when a river claims the camera. Camera-only, not per-pixel. Negligible.

---

## 4. RISKS

1. **Fog area = highest regression risk in the package** (5 memory files of scars). Fences in
   axis C; R3 is its own increment, defaults-off-safe, after R1/R2 are green.
2. **Unity object lifecycle in authoring**: AddComponent/DestroyImmediate is illegal in
   `OnValidate`; the facade creates/destroys only from editor buttons/menus (with Undo), and
   `OnValidate` only re-positions + flags. The `WaterRiverAuthoringChangeRouter` precedent
   shows the event-driven pattern to copy.
3. **`lake.currentFields` overwrite trap** (§1.1) — facade must append/remove-own; also fix the
   demo rig to use the same helper once it exists (one recipe).
4. **Port id stability**: regeneration MUST reuse generated objects (facade-serialized refs);
   `portId` is GUID-once by design — any code path that recreates ports mints new ids and
   orphans saves. Acceptance test 3 pins this.
5. **Standalone rivers** (no parent volume): no animated uniforms, no fog, gameplay only via
   provider. v1 supports them for buoyancy/splash (R2) and validates loudly everywhere else.
6. **`VolumeUp` vs river frame for buoyancy lift** — a feel decision flagged in axis D; needs
   Bert's call before R2 code.
7. **Uncompiled stack**: this study sits on §10 code that compiled clean but has NEVER run.
   R1 should start only after the domains test pass (the "NEXT SESSION" checklist in the
   domains memory) is green — otherwise river bugs and domain bugs will be indistinguishable.
8. Line endings / md5 discipline: every file in the R1-R4 touch lists gets md5-gated and
   endings-measured per session (never-write-via-shell-mount rules apply to any patcher).

---

## 5. INCREMENT PLAN (each needs its own GO)

### R1 — Authoring facade + connect-to-body (~60-75 min, low risk, editor-heavy)
Files: NEW `Runtime/WaterRiver.cs` (+meta), NEW `Editor/WaterRiverEditor.cs` (+meta),
NEW `Editor/WaterBuildKit.River.cs` (+meta); EDIT `Editor/WaterBuildKit.ConnectedWatersDemo.cs`
(delegate to the shared builder), possibly `WaterTopology.cs`/`WaterConnection.cs` (the three
small fishing accessors, if GO'd). Tests: facade wiring idempotence, adopt-existing, append-not-
replace currentFields, port derivation from spline ends, regenerate-reuses-ids, remove-cleans-own.
### R2 — Gameplay parity (~45-60 min, medium risk, physics-visible)
Files: EDIT `Runtime/WaterBuoyancy.cs`, `Runtime/WaterSplash.cs`, `Runtime/WaterMembership.cs`,
`Runtime/WaterRiverSurfaceProvider.cs` (AABB cache + projection memo), possibly
`Runtime/Query/WaterDomainResolver.cs` (nothing expected — Provider already exposed).
Tests: sloped-river buoyancy rides spline height; volume-winner path bit-identical (fake
provider assert on call counts); standalone-ribbon float; splash height on river; membership
resolves parent; zero-alloc steady state.
Pre-req: Bert's VolumeUp decision (Risk 6).
### R3 — River underwater fog v1 (~60-90 min, ⚠️ highest risk — separate GO, separate test day)
Files: EDIT `Runtime/WaterVolume.Underwater.cs` (surface-override plumb),
`Runtime/WaterVolume.Settings.Underwater.cs` (river-aware source resolve),
`Runtime/Query/IWaterSurfaceProvider.cs` or a small helper for camera-in-river, NO shader files.
Tests: camera under sloped ribbon gets flat fog at river height; camera in parent lake
unchanged (bit-identical); no-river scenes bit-identical; seam crossing documented behaviour;
C1 regression suite green with a river present.
### R4 — Polish (~30 min): port/connection/river gizmos, validation helpboxes, docs page,
demo-rig delegation cleanup if not done in R1.

Re-scope note vs the memory's R1-R4: unchanged in shape; R1 grew the optional topology
accessors, R2 grew the two perf guards (cheap, and they de-risk the stress case up front).

## 6. ACCEPTANCE TESTS (minimum, per the prompt)

1. One-click river creation (menu) → playable river, zero console errors, all wiring green.
2. Connect-to-body generates ports + connection with stable ids; blend live at the seam.
3. Regenerating (button, retarget, spline edit) does NOT duplicate objects or change portIds.
4. Buoyancy on a sloped river rides the spline height (crate at mid-slope elevation).
5. Membership in a river resolves the parent body's uniforms.
6. Camera under a river gets fog at the river's height (flat waterline v1).
7. Volume-fog scenes with NO river present: bit-identical (fog + gates + globals).
8. No steady-state allocations in buoyancy/splash/membership/topology paths.
9. (added) Standalone ribbon: floats + splashes, warns about missing parent, no fog.
10. (added) C1/half-res fog regression suite green with a river in the scene, camera outside it.

## 7. EXPECTED DURATIONS SUMMARY
R1 ~60-75 min · R2 ~45-60 min · R3 ~60-90 min · R4 ~30 min. All AFTER the domains-round test
pass is green in Unity (Risk 7).

## 8. CLAIMS I COULD NOT VERIFY (stated, not assumed)

- **Nothing was compiled or run** — every "works" above means "reads correct at source"; the
  entire §10 layer this builds on is itself untested at runtime.
- `WaterVolume.SampleHeights` internals (readback/ripple composition) were not re-read this
  session — the "volume path bit-identical" claim in R2 rests on NOT touching that path, which
  the design guarantees structurally, not on having re-verified its contents.
- The fishing-side contract (§1.7 facts) is quoted from the domains doc §1.7, not re-verified
  against `FishingAddon011025` sources this session.
- CPU timing figures in axis E are operation-count estimates, not measurements — the projection
  eval count IS verified (evaluator source), the µs/ms translations are engineering estimates.
- `fullscreenVolumeFog` default value on freshly-created bodies (wizard path) was not checked —
  axis C assumes typical bodies are fog-eligible; verify before R3.
- Whether `TryGetAnalyticWaterline` and the provider's height agree bit-for-bit on plain
  volumes (the R2 splash bit-identical goal) needs the test to prove it, not this study.

---

## 9. IMPLEMENTATION RECORD — same session (Bert's GO: "full pass, git is clean"). SHIPPED UNTESTED

Unity was closed the whole session: **nothing here has been compiled or run.** The first compile
also lands on top of the same-day domains/fog/FFT rounds.

### New files (5 + minimal .metas, GUID uuid4)
- `Runtime/WaterRiver.cs` — the facade: RequireComponent(Spline, CurrentField, Surface) trio;
  fill-null wiring + facade-authoritative parent link; APPEND/remove-own parent current-field
  link; per-end `WaterRiverEndConnection` (body + transition radius + serialized refs to the
  generated port pair/connection); `TryGetEndFrame` (terminal spline frame, downstream-facing
  ports, flow = width×speed), `DeriveBodyPortPosition` (body rest level, footprint-or-stepped,
  radius-capped), id-stable `RegenerateConnection` (reuses generated objects; runtime-created
  ports get `EnsurePortId()` explicitly), transform-only `SyncGeneratedConnectionAnchors` for
  OnValidate, loud validation.
- `Editor/WaterRiverEditor.cs` — inspector: parent + per-end body/radius, Generate/Update/Remove
  with full Undo (record-reused, register-created), optional Fluid/Foam add buttons.
- `Editor/WaterBuildKit.River.cs` — `CreateRiverRig` = THE one river recipe (components in
  dependency order, facade last, serialized current-field APPEND helper) + GameObject menu
  "AbstractOcclusion/River" (asset-half context only — no camera/sun rigged into an existing
  scene; parent from selection else Primary; auto-connects the mouth when parented).
- `Tests/Runtime/WaterRiverFacadeFeatureTests.cs` — trio completion, terminal frames, id-stable
  non-duplicating regeneration, upstream/downstream port order per end, append-not-replace
  current-field link, TryGetPort lookup.
- `Tests/Runtime/WaterRiverGameplayFeatureTests.cs` — resolver hands the PROVIDER (standalone
  ribbon, Body null), fog scan honours the pre-arm margin, memo/bounds caches answer-invariant,
  buoyancy fall-through from far above still finds the ribbon (the XZ-only reject case).

### Edits (12)
- `Editor/WaterBuildKit.ConnectedWatersDemo.cs` — demo river now built THROUGH `CreateRiverRig`
  + facade mouth connection (hand-wired seam and the `currentFields` array overwrite deleted).
- `Runtime/WaterBuoyancy.cs` — Resolve keeps `sample.Provider`; volume winners run the EXACT old
  `SampleHeights` batch line; river winners per-probe `SampleProviderHeights` into the same
  rented buffer; `up = _body != null ? _body.VolumeUp : Vector3.up` (the axis-D feel call).
- `Runtime/WaterSplash.cs` — surface height from the resolved domain (`ExcludeInteractiveRipples
  = true` keeps the analytic-only doctrine); body-null guards for emitter/ripple/cap.
- `Runtime/WaterMembership.cs` — resolver (ContainingVolume + PrimaryBody fallback + hysteresis
  id); crates inside an exclusion carve now drop the block ("dry wins").
- `Runtime/WaterRiverSurfaceProvider.cs` — XZ-ONLY coarse bounds reject (vertical must not gate
  the buoyancy fall-through), frame-stamped bounds cache + (point, margin) projection memo, both
  bypassed outside play mode.
- `Runtime/Query/IWaterSurfaceProvider.cs` — `ExtraProviderContaining(point, margin)` scan.
- `Runtime/Query/WaterTopology.cs` — `TryGetPort(portId)` persistent-id lookup.
- `Runtime/WaterConnection.cs` — consts internal; `DownstreamPort`, `OtherPortFor(bodyId)`;
  selected-gizmo (port axis + transition slab).
- `Runtime/WaterConnectionPort.cs` — `PortId` ensures lazily (runtime-generated ports in builds
  never see Reset); `EnsurePortId` internal; selected-gizmo (anchor + downstream arrow).
- `Runtime/WaterVolume.Underwater.cs` — river override plumb: overload
  `UpdateUnderwaterState(cam, externalFlatSurfaceY)` with try/finally-scoped
  `_externalSurfaceOverride`; surfaceY/envelope(0)/band anchors/footprint/fogSimple honor it;
  gate chain consults `TryResolveRiverFogOverride` BEFORE the legacy source resolve.
- `Runtime/WaterVolume.Settings.Underwater.cs` — `TryResolveRiverFogOverride` (ribbon at eye
  within the arm band → parent must be live + fog-eligible → river height at eye XZ).
- `Runtime/WaterVolume.Settings.Volume.cs` — `fullscreenVolumeFog` internal (facade validation).

### md5 manifest (all pure LF this session)
```
48316a99 Runtime/WaterRiver.cs                    (NEW)
238cf9a1 Editor/WaterRiverEditor.cs               (NEW)
e6cb9849 Editor/WaterBuildKit.River.cs            (NEW)
be5d3f51 Tests/Runtime/WaterRiverFacadeFeatureTests.cs   (NEW)
a57f9a72 Tests/Runtime/WaterRiverGameplayFeatureTests.cs (NEW)
ecbd745d Editor/WaterBuildKit.ConnectedWatersDemo.cs  (was 17bf2a6a)
3eb06018 Runtime/WaterBuoyancy.cs                     (was cbc9354a)
862efa97 Runtime/WaterSplash.cs                       (was b1edfc27)
6f2eb6eb Runtime/WaterMembership.cs                   (was 0a9abed1)
85490945 Runtime/WaterRiverSurfaceProvider.cs         (was cbe15c34)
ce0e0c8b Runtime/Query/IWaterSurfaceProvider.cs       (was 8f2de40b)
0aa7a43e Runtime/Query/WaterTopology.cs               (was f3af9ffc)
1e93ccc0 Runtime/WaterConnection.cs                   (was 39faedbe)
5a8df504 Runtime/WaterConnectionPort.cs               (was 9a370789)
96de08f5 Runtime/WaterVolume.Underwater.cs            (was 426fea25)
215bcf2a Runtime/WaterVolume.Settings.Underwater.cs   (was eb72fe2f)
6ab1783c Runtime/WaterVolume.Settings.Volume.cs       (was e2e551c5)
```

### Honesty notes (deltas from the study's ideal)
1. **Splash height parity is doctrine-equal, not proven bit-equal**: the domain read
   (`TrySampleWorld` + ExcludeInteractiveRipples) replaces `TryGetAnalyticWaterline`; both are
   analytic-only but travel different code paths — the acceptance test run must confirm entry
   heights on plain volumes feel unchanged.
2. **Membership semantics moved twice on purpose**: exclusion carves now drop the block ("dry
   wins"), and the out-of-water fallback is the primary's MembershipBlock rather than a cleared
   block (visually the primary either way).
3. Provider caches are frame-stamped and bypassed outside play — an edit-mode tool hammering one
   provider still pays full projections (correct, just not cached).
4. Seam RENDER blending, wavy river waterline (F3-style ribbon height RT), and river-rendered
   interactive ripples remain future increments, exactly as scoped in §3.
5. The R3 seam-crossing pop (surfaceY jump at the ribbon boundary underwater) ships as designed
   in §3C — mitigation (CPU seam-blended surfaceY) is pre-scoped, awaiting a sighting.

### §9 addendum — post-compile seam follow-up (same day)

Bert: "river are not seamlessly connected to water pond." Two causes, both fixed:
1. **Rig geometry**: the rig's terminal knots stopped SHORT of the body footprints. New
   authoring rule, applied to all four ends: a terminal knot OVERLAPS into its body's footprint
   and sits ~5 cm BELOW that body's rest surface, so the ribbon slides under the standing water.
   (`ConnectedWatersDemo.cs` b93de113 → d480162e.)
2. **Port-axis bug the rule exposed**: `WaterRiver.DeriveBodyPortPosition`'s inside-footprint
   branch dropped the body port straight onto the anchor → a ~5 cm near-VERTICAL port axis →
   horizontal seam plane → the transition slab blended every near-surface sample. The derivation
   is now ALWAYS horizontal (step toward the body centre, capped by the transition radius;
   degenerate fallback only at the exact centre). (`WaterRiver.cs` 48316a99 → 797ae44e.)

Render-side seam fading (ribbon alpha into the receiving surface) remains the scoped future
increment; the overlap-and-submerge rule is the v1 answer. Watch item: with waves on the
receiving body, crests can momentarily dip below the submerged ribbon tail and sparkle — raise
the submerge depth past the wave envelope if it shows.

### §9 addendum 2 — V2a: the mesh stitch + the river under-sheet (same day, Bert's GO "ok v2a")

Bert: "we should stitch surface meshes" + "river have no underwater surface mesh". Both shipped,
UNCOMPILED:

**The stitch (apron + per-vertex seam weight).** The vertex stage already blends every
river-specific term through ONE `riverWeight = saturate(_IsRiver)` lerp — v2a makes it
per-vertex. A connected end now grows an APRON (`WaterRiverRibbonMeshGenerator`:
`ApronCrossSections` = 4 flaring rows, reach = the seam's transition radius, outer edge landing
`ApronSubmergeMeters` = 5 cm UNDER the receiving body's rest surface) carrying a seam weight
1→0 in the new UV1.w (channel went float3→float4; unconnected ribbons carry w = 1 everywhere).
`vertexRiverWeight = saturate(_IsRiver) × UV1.w` now drives the MOTION terms only — ripple gate
(the apron sits inside the receiving body's sim window, so it re-admits the body's interactive
ripples), the wind-wave coordinate lerp (vertex displacement AND the matching fragment slope),
current visuals, river/baked foam, foam frames — while the geometric position/frame lerps stay
on the uniform flag (a ribbon vertex fed to the grid path would be garbage). Deliberately kept
UNIFORM (regression fences): the peaked-refine loop trip count, the detail-normal branch (the
D3D worker-kill comment), and the underwater foam ternary (WGSL derivative rule) — so an apron
keeps river detail normals, skips peaked refinement, and shows no sim foam. Zero new samplers,
zero new branches, zero new variants.

**The under-sheet.** Bodies render coincident twins (MatAbove cull-back `_Underwater=0` +
MatUnder cull-front `_Underwater=1`); the ribbon had only the above sheet — a submerged camera
looking up saw nothing. `WaterRiverSurface` now owns a runtime-created DontSave child ("River
Surface Under (generated)") sharing the generated mesh with a serialized `underSurfaceMaterial`
(the recipe passes `ctx.MatUnder`); same property block, same forceRenderingOff. Play-mode-only
by design (the mesh never serializes; edit mode keeps only the above sheet).

**Facade/wizard plumbing**: `WaterRiver.SyncSeamAprons` pushes per-end apron requests
(body rest Y + transition radius) into the surface on enable/validate/regenerate/remove;
`CreateRiverRig` gained the under material; a near-vertical terminal (waterfall) silently skips
its apron. New tests: `WaterRiverApronFeatureTests` (no-apron invariance, monotone fade,
submerged outer edge, waterfall skip).

### V2a md5 manifest (all pure LF; patcher _stage_tmp/patch_v2a_*_20260829.py)
```
86a962ab Runtime/WaterRiverRibbonMeshGenerator.cs      (was 8e5edb9e)
2c0543f7 Runtime/Shaders/WaterSurfaceVertStage.hlsl    (was b74d43ff)
b412eae2 Runtime/Shaders/WaterSurfaceFragStages.hlsl   (was 06e64542)
a5251ade Runtime/WaterRiverSurface.cs                  (was 29638866)
b5128662 Runtime/WaterRiver.cs                         (was 797ae44e seam fix, 48316a99 R1)
199fc667 Editor/WaterBuildKit.River.cs                 (was e6cb9849)
d490ed68 Editor/WaterBuildKit.ConnectedWatersDemo.cs   (was d480162e seam fix, b93de113 v2)
32439cc2 Tests/Runtime/WaterRiverApronFeatureTests.cs  (NEW)
```

**V2a honesty notes**: the WaterSurface shader recompiles (the slow one — 66% stall is
known-normal); pool/grid rendering should be bit-identical (their river weight is 0 through the
same expressions — verify, don't assume); apron pixels lose peaked ripple refinement + sim foam
+ keep river-oriented detail normals (all deliberate uniformity trades); the under-sheet adds
one draw per river and exists only in play mode; V2b (body-side mouth-capsule discard) remains
scoped, wanted only if a double-fog band shows in the overlap ring.

### §9 addendum 3 — Demo v3 (real gaps) + F1 fog-ribbon (same day, Bert: "a real full connection", "ok for me")

**Demo v3** (`ConnectedWatersDemo.cs` d490ed68 → 7c985847): pond/sewer moved to x=22, reservoir
to z=-32 — the spillway now bridges a ~10 m air gap, the main river ~19 m; crates and floor
follow; Play check (8) added: dive the mid-gap river, fog must ride the river.

**F1 fog-ribbon** — the underwater connection between bodies, C#-only, zero shader edits:
1. River-override frames publish `_UnderwaterUnbounded = 1` (`Underwater.cs` 96de08f5 →
   1c9e19eb): no single body's box clips the fog mid-gap; the flat Simple waterline at the
   river's height owns the reach — the shipped Low-tier ocean combination. The unbounded shader
   branches that assume a real ocean also gate on `_OceanSurfaceDepthValid`, which a pond medium
   never publishes (verified in WaterUnderwaterFog.shader:702/981/989 + Waterline.shader:85/131).
2. Medium handoff (`Settings.Underwater.cs` 215bcf2a → 33a685a7): the fog SOURCE is the nearer
   CONNECTED end's body — `SelectRiverFogMediumBody` walks ribbon → surface → facade ends,
   projects the eye onto the spline (`NormalizedT` vs midpoint 0.5, ±0.1 hysteresis band,
   `_riverFogLastBodyId` incumbent rule, reset in `ResetStaticState` and on leaving every
   river), preference order near-end → far-end → parent, each `IsFogEligible`-checked.
   (`WaterRiverSurfaceProvider.cs` 85490945 → 9f720798: internal `Surface` accessor.)

**F1 honesty**: the handoff is a SWITCH, not a blend — two ponds with different mediums pop once
per crossing, mid-gap (F2 = true per-uniform lerp, if wanted); with unbounded published, the fog
and the meniscus line extend laterally past the banks while the eye is in the river (ocean-style
reach — visible only where open air sits below river level beside the ribbon); the spillway-top
fog-clip caveat from R3 is GONE (that was the bounded parent box). UNCOMPILED, like everything
since the seam fix.

### §9 addendum 4 — V2b + C1: the perfect sewing (same day, Bert: "smooth the link zone like the swash", "do the max")

The apron's remaining sins — the double-layer tongue, the flicker, the poke-through, and the
ANGLE at the knot row — all fixed in one pass. UNCOMPILED.

1. **C1 Hermite profile** (`WaterRiverRibbonMeshGenerator` 86a962ab → 0c9a9f10): the apron now
   ARRIVES with the ribbon's own slope (slope-matched at the knot row, clamped ±1.5) and eases
   to slope-zero at rest−3 cm — the surf-film SmoothMin/SmoothMax lesson ("both joins were hard
   clamps, and each printed its own crease") applied to the mouth. Hard-min overshoot cap at
   rest−2 cm (engages only on steep-chute arcs, mid-tongue, underwater). Seam weight now reaches
   0 at 60% of the reach, so the outer apron displaces exactly like the body.
2. **V2b mouth carve — one surface per pixel**: the receiving body's sheets DISCARD inside a
   capsule derived by the generator from the SAME terminal frame as the apron
   (`TryComputeMouthCarve`: 80% of the reach, 90% of the flared width — strictly inside, hole
   always covered from below). Registered by the facade in `WaterTopology`
   (`AddMouthCarve/RemoveMouthCarves/CollectMouthCarves`, max 4/body, cleared on river disable +
   ResetStaticState; WaterTopology be85c166, WaterRiver 651864c1). Published per body in
   `WriteBodyUniforms` as six fixed cached properties (`_MouthCarveCount/Seg0..3/Radii` —
   publisher af434ac7; unconditional writes, so the cache's missing-id rebuild never triggers).
   Classifier `InsideMouthCarve` lives beside `PatchCoversBaseSheet` (VertStage e1dd7f1c;
   `MOUTH_CARVE_MAX 4` mirrored to `WaterTopology.MaxMouthCarves`; `_IsRiver` gate keeps the
   ribbon from carving itself — its block carries the PARENT's carves). Discard added to ALL
   THREE body passes (surface, ocean eye-depth prepass, foam overlay — WaterSurface.shader
   d62762b8 → ddf596f2) so fog classification and foam agree with the carved surface; tested at
   the undisplaced `largeWaveSourceXZ` so chop cannot wiggle the carve against its apron.
3. Tests updated (ApronTests → c86782ca): non-increasing fade reaching 0, outer edge at
   rest−ApronEdgeDipMeters, capsule strictly inside the apron.

Residual honesty: ~2–3 cm step at the carve edge (apron dip vs body rest — hidden by waves and
refraction; true flush = co-locating carve edge and apron edge, later polish); the carve is a
hard edge under the apron by design (silhouette agreement not required — the apron covers it).

### §9 addendum 5 — carve-hole fix (Bert's screenshot) + first stitch confirmation

**Confirmed by Bert in play: ripples transfer river → pond across the seam** — the V2a motion
stitch works. **Regression he screenshotted: the mouth carve punched HOLES** past the apron.
Root cause was mine: `TryComputeMouthCarve` v1 used ONE constant radius sized off the FLARED
apron width and checked coverage at a single point — along most of its length the capsule was
wider than the still-young apron, and its end cap reached past the apron tip. Fix: the carve is
now a **tapered capsule that provably hugs its cover along the whole length** — round start cap
at 90% of the UNFLARED half-width (the ribbon body covers behind the knot), sides widening with
the apron's own flare, FLAT end at 80% reach (no end bulge). Struct/publisher/shader updated
(`WaterMouthCarve` carries Start/Direction/Length/R0/R1; lanes `_MouthCarveLength/Radius0/
Radius1`); tests now assert carve ≤ cover at 8 stations plus the start-cap-under-ribbon rule.

```
fe7df271 RibbonMeshGenerator (was 0c9a9f10) / 2a713102 Topology (be85c166)
fcb2c294 WaterRiver (651864c1) / 32586912 UniformPublisher (af434ac7)
ce3b7925 WaterSurfaceVertStage.hlsl (e1dd7f1c) / 4ca7c966 ApronTests (c86782ca)
WaterSurface.shader unchanged at ddf596f2 (call site identical).
```

### §9 addendum 6 — W1: THE BORDER CONTRACT + flush seam (Bert's design call: "shouldn't we only use the borders of the water surfaces? Any other water link can be replaced by a waterfall")

Bert confirmed in play: **holes gone**; remaining defect a visible stitch band, angle-dependent.
Diagnosis: (a) the deliberate 3 cm apron dip printing a step at the carve edge, (b) the fade
band's damped motion vs open rippled water, (c) the hard carve edge crossing OPEN water —
because the mouth knot sat 2 m INSIDE the footprint. His borders-only question is the right
architecture and was adopted as **THE BORDER CONTRACT**:

> Surface-to-surface connections meet ONLY at the receiving body's footprint BORDER. Every link
> that cannot meet a border (steep drop, mid-body entry, arrival above the surface) is a
> WATERFALL link — a separate connection kind, scoped below.

Why it kills the band at the root: the seam moves to the bank line, where the body's own edge
feather (`LargeWaveEdgeWeight` / `LbwEdgeWeight` — CPU and GPU agree) already calms the wave
field to rest. Both sheets are near-rest there, so the carve edge has no rippled open water to
cut across, and the motion mismatch of the fade band dissolves into the feather. Found while
implementing — the contract gets its matching FOR FREE from machinery that predates rivers.

1. **Border snap** (`WaterRiver` fcb2c294 → db327d9f): `DeriveBodyPortPosition` reworked — the
   body port is now the closest point ON the footprint border (pool space = unit square under
   any body rotation; per-axis distances compared in WORLD metres because the pool frame is
   anisotropic), rest level, always horizontal. The step-toward-centre logic and
   `MinPortSeparationMeters` are GONE (signature lost its radius arg; single call site).
   `WarnIfAnchorDeepInsideFootprint` fires on explicit (re)generation when a terminal knot sits
   > `MaxKnotInsetWarnMeters` (0.5 m) inside a footprint — names the contract, suggests a
   waterfall; OnValidate-path syncs stay silent (no console spam).
2. **Flush seam** (`RibbonMeshGenerator` fe7df271 → 331f7aa7): `ApronEdgeDipMeters` 0.03 →
   **0.005** (anti-z-fight margin only — the razor dip IS now the whole visible step),
   `ApronMinSubmergeMeters` 0.02 → 0.004 (must stay < dip or the cap flattens the tip row),
   `ApronCarveCoverage` 0.8 → **0.95** (carve edge co-located with the apron tip, where seam
   weight is already 0; never 1.0 — float error past the tip row would re-open a hole). The
   8-station coverage asserts are symbolic against these constants — tests unchanged
   (ApronTests still 4ca7c966).
3. **Rig v4** (`ConnectedWatersDemo` 11063ce3 → 35fc9fee): all four terminal knots moved ONTO
   their borders, heights unchanged (−0.05 under each rest surface): river source
   (−3,4.15,−28)→(−3,4.15,**−27**) (reservoir z border), river mouth (0,−0.05,−6)→(0,−0.05,**−8**)
   (lake z border), spillway top (19,5.95,1)→(**18**,5.95,1) (pond x border), spillway mouth
   (7,−0.05,7)→(**8**,−0.05,**6.5**) (lake x border, on the approach line). Authoring-rule
   comments rewritten to the border rule (v4).

**W2 scope — waterfall link type (NOT built, next session's fork):** `WaterRiverEndConnection`
gains a link kind (Mouth | Waterfall). A waterfall end: no apron, no carve (the generator
already skips aprons on near-vertical terminals — that silent skip becomes the explicit kind);
a vertical sheet mesh from the lip to the receiving surface; splash/impact foam ring at the
plunge point (reuse the splash/foam machinery); current field impulse at the base
(`WaterRiverCurrentField` already reserves waterfall spans); fog: the fall publishes nothing —
the medium handoff happens at the plunge pool, which the F1 nearest-end logic already handles
if the mouth knot sits at the base. Authoring: the warning above routes users here.

Patcher `_stage_tmp/patch_border_contract_20260829.py` (verify-all-then-write, 9 edits, 3
files, balance-checked, LF preserved). UNCOMPILED like everything since the seam fix — compile
+ **rebuild the rig** (knots changed) to see it.

```
db327d9f WaterRiver (was fcb2c294) / 331f7aa7 RibbonMeshGenerator (fe7df271)
35fc9fee ConnectedWatersDemo (11063ce3)
unchanged: 2a713102 Topology, 32586912 Publisher, ce3b7925 VertStage,
ddf596f2 WaterSurface.shader, b412eae2 FragStages, a5251ade RiverSurface, 4ca7c966 ApronTests
```

---

## Addendum 7 (2026-08-30) — V2c: the KWS/RAM study + seam wave anchor + per-pixel detail handoff

Bert's report after compiling W1: "the texture is not seamless and I see an angle at junction."
He added RAM (River Auto Material 3, NatureManufacture) to the KWSWater study project; this
addendum records how RAM joins a river to a lake/sea, and what of it we ported.

### How RAM does it (read from `Spline System/Scripts/River/*`, `Water Shaders/Water River Offset.shader`)

1. **No geometric weld to a water body — overlap + dissolve.** `RamSplineConnection` has two
   connection kinds: `Split` (geometric weld, river-to-river only) and `Alpha` (river-to-lake and
   river-to-river). For a lake, the river's end control point is pushed ~5 m PAST the lake edge
   (`BlendOffset`, along the lake spline's binormal), floated 3 cm ABOVE the lake surface
   (`YOffset`), and the last ~4–8 m of ribbon fade out by VERTEX ALPHA (`BlendDistance ×
   BlendStrength`, `blendCurve` along the length, `sideBlendCurve` 0→1→1→0 across the width so
   the overlap has no hard side edges). The lake mesh underneath stays intact. Final opacity =
   depth-edge falloff × vertex alpha (`Blend SrcAlpha OneMinusSrcAlpha`).
2. **Height-contour fade, no angle.** `RamVertexColors.GenerateBlendWithLakePolygon` raycasts each
   river vertex down onto the lake and sets `alpha = clamp01(heightAboveLake)`: the fade follows
   the actual intersection CONTOUR, so no straight seam line exists at ANY approach angle.
3. **Everything cross-fades, not just motion.** Vertex color G/B lerp the river's vertex
   displacement, normals, foam, specular AND smoothness toward the SEA cascade systems in the
   blend zone (shader lines: `lerp(riverThing, seaThing, v.color.g/b)` at every output).
4. **The receiving field is GLOBAL.** RAM's sea cascades are world-anchored global wave systems;
   the river and the sea sample the SAME field, so the cross-fade target is exact by construction.

### Why our seam still printed

- **Micro-detail never faded (both ends).** The detail-normal path picked
  `RiverDetailNormalTilt` (ribbon-anchored, travels downstream) vs `DetailNormalTilt`
  (world-anchored) by the UNIFORM `saturate(_IsRiver)`, not the per-pixel V2a apron weight — the
  apron kept full river micro-texture up to the carve edge. RAM lesson 3.
- **Wind-wave anchor wrong at non-parent ends (the SOURCE seams).** The ribbon's property block is
  written by the PARENT volume (`WriteBodyProps`), so the apron's grid target
  (`WindWaveSampleXZ`) anchors in the PARENT's pool frame. Parent = mouth body (facade doctrine),
  so the LAKE mouth matched — but the reservoir/pond SOURCE seam blended toward the lake's
  anchor while the reservoir renders its own: same metric bank (seed 9173 is shared), two
  anchors → phase jump → a moving texture line + the visible "angle" at the carve edge. This is
  §"known asymmetry" from the W1 wave. RAM lesson 4: the cross-fade target must be the
  RECEIVING body's own field.

### V2c (this wave, UNCOMPILED)

1. **Per-pixel detail handoff** (`WaterSurfaceFragStages.hlsl`): inside the uniform `_IsRiver`
   branch, compute BOTH `RiverDetailNormalTilt` and the body's world-anchored
   `DetailNormalTilt`, lerp by per-pixel `riverWeight` (apron weight). Pools byte-identical
   (their branch untouched); ribbon interior byte-identical (weight 1).
2. **Seam wave anchor** (`WaterWaves.hlsl` + both stages + `WaterRiverSurface.cs`): new per-end
   uniforms `_RiverEndWaveFrame0/1`, `_RiverEndWaveAnchor0/1` = the RECEIVING body's world→pool
   XZ rows, center, metersPerUnit ratio, mode gate. `RiverApronWindWaveSampleXZ(poolXZ, worldXZ,
   longitudinalMetres)` replaces `WindWaveSampleXZ` at BOTH grid-sample call sites (vertex
   height + fragment slope — they must agree or height/normal desync); source vs mouth end
   picked by the SIGN of the cumulative longitudinal metres (source aprons are negative — mesh
   channel contract). Mode 0 (zero vector, the default for pools, unconnected ends, end==parent,
   ocean-clipmap ends) is today's path bit-for-bit. Published from
   `WaterRiverSurface.PublishEndWaveAnchors()` each LateUpdate via the facade's end bodies.
   Amplitude note: the apron still uses the PARENT's bank amplitudes — with equal authored wave
   settings the metric field matches exactly (per-body normalization cancels); differing
   settings leave a residual amplitude step (author's choice, not fixed here).
3. **NOT ported (documented options):** RAM's alpha dissolve needs transparent blending — our
   doctrine is one opaque surface per pixel (V2b carve). If a band persists after V2c, the next
   RAM-shaped lever is a stochastic (dithered) carve edge: per-pixel hash vs seam weight decides
   river-or-body over a widened band — keeps one surface per pixel, dissolves any residual
   mismatch like RAM's alpha, at the cost of edge noise. Scoped only.

Files (md5 first 8): 61a7b278 WaterWaves.hlsl / 61b61921 WaterSurfaceVertStage.hlsl /
0e833878 WaterSurfaceFragStages.hlsl / ec5d091d WaterRiverSurface.cs
(patcher `_stage_tmp/patch_v2c_seam_anchor_20260830.py`, 8 edits / 4 files, exact-match).

### The checks (after ONE compile)
(a) Source seams (reservoir→river, pond→spillway): the moving texture line at the carve edge
should be GONE — ripple pattern flows continuously across the bank line at both river angles.
(b) Mouth seam (lake): micro-detail no longer prints the apron outline.
(c) Pools + ribbon interiors: bit-identical (all new uniforms default 0 / weight 1).
(d) Buoyancy: unchanged by design — the CPU provider is analytic (no wind-wave detail), so no
CPU mirror obligation exists for these terms.
(e) STILL REQUIRED from W1 if not done: terminal knots ON the receiving body's borders +
regenerate both ends (the border contract is an AUTHORING rule; the warning only fires on
explicit regeneration).

---

## Addendum 8 (2026-08-30) — V2d: mix the waves, not the coordinates (the washboard fix)

Bert's screenshot after the V2c compile: a band of tight parallel stripes (a washboard) across
the river→lake mouth, plus flat-looking apron flanks with hard silhouette edges. Bert's design
statement, adopted verbatim: **"we sew borders, then a fade to mix waves, then we connect the
fog volume."** That is exactly W1 (sewn borders) + the apron (fade) + F1 (fog handoff) — the
fade was just mixing the WRONG thing.

**Root cause of the washboard:** both stages blended the wind-wave layer as
`WaveHeight(lerp(gridSample, riverSample, w))` — a lerp of SAMPLE COORDINATES. The grid sample
(pool anchor) and the river sample (ribbon metres) are far apart in the field's domain, so as w
goes 1→0 across a few metres of apron, the interpolated coordinate travels a long way through
the wave field — the phase sweeps through many cycles → a compressed interference stripe band.
It was always there; V2a's play test just never lit it up. RAM never does this: it lerps
OUTPUTS (displacement, normals, foam, specular), never inputs.

**Fix (2 exact-match edits):** `position.y += lerp(WaveHeight(grid), WaveHeight(river), w)` in
the vertex stage, `windSlope = lerp(WaveSlope(grid), WaveSlope(river), w)` in the fragment —
each inside a uniform `_IsRiver` branch so pools stay single-evaluation and byte-identical.
Endpoints are unchanged (w=0 and w=1 identical to before); only the transition is repaired, and
at w→0 the apron now equals the receiving body's own height/slope EXACTLY (same anchor: parent
at the mouth, V2c end anchor at a source). Cost: one extra ALU wave loop on river pixels only.

**Exclusion walls "don't show":** they DO draw (drawWaterWalls defaults true, packaged shader
resolves, edit + play). Their brightness rides the fog in-scatter globals, and the rig authors
fogDensity 0.2 (vs the class-default 2 that produced the July "Moses" walls) — at 0.2 the wall
is ~invisible from outside. To see them: raise the lake's fog density (or the carve's
wallScatterBoost, capped 2). Not a bug; recorded so the rig's look isn't mistaken for a
regression. The reason the exclusion hole's edge is always clean is that ONE surface discards
against itself — which is also why the mouth carve edge is clean on the lake side and every
remaining artifact lives on the apron.

Files (md5 first 8): 54ffe0c0 WaterSurfaceVertStage.hlsl / 630e811b WaterSurfaceFragStages.hlsl
(patcher `_stage_tmp/patch_v2d_value_blend_20260830.py`; V2c md5s for the other 2 files stand).

### Checks (after ONE compile)
(a) The stripe band at the mouth is GONE — smooth cross-fade of ripples into the lake's own.
(b) The apron flanks stop reading as flat wedges (they now carry the lake's real waves at w→0).
(c) Pools, ribbon interiors bit-identical. (d) Source seams keep the V2c anchor behaviour.
