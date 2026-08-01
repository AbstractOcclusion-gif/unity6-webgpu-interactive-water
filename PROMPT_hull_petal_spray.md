# PROMPT — Hull-fitted spray pump with directional petals

You are a senior Unity dev working in `com.abstractocclusion.webgpuwater`.
**Read `CLAUDE.md` / the project instructions first — especially "never guess, always check" and
"ask before touching code".** This prompt is DESIGN-GATED: the design below was validated with Bert
on 2026-08-01 and is not to be re-litigated. Implementation authorisation is still per-increment.

---

## 0. Read these first, in this order

```
docs/PLAN_hull_petal_spray_v2.md            the plan
docs/PLAN_hull_petal_spray_addendum_A.md    REPLACES v2 sections 6 and 7 — read both
docs/PLAN_hull_petal_spray_mockup.html      the user-POV mockup (open it, it is drawn)
docs/PARTICLE_FOAM_INVENTORY.md             how the particle systems actually work
Documentation~/ParticleSchemes.html         the same, drawn
```

Project memory: `session-status-2026-08-01` is the authoritative status of what compiles and what has
never run. Read it before assuming anything in the package is proven.

---

## 1. The goal, in one paragraph

A boat's `WaterSprayPump` currently gets its probes from an **axis-aligned bounding box** (a straight
bow row or an ellipse ring), and every burst it throws is a **perfectly isotropic ring** of droplets.
Both are wrong for a hull. This feature places probes on the hull's **real waterline outline**, gives
each probe an **outward direction**, and narrows each burst from a ring to an **arc** — so a hull is
ringed with spray petals that rake astern as the boat accelerates.

---

## 2. Decisions LOCKED with Bert — do not reopen

| # | Decision |
|---|---|
| 1 | Placement from the **waterline cross-section of the hull mesh** (a plane/triangle walk, not a convex hull) |
| 2 | A petal is an **ARC** — narrow the ring to a wedge. Not a tilted cone, not a weighted ring |
| 3 | Direction comes from **the probe's own measured velocity**, not a Rigidbody, not the transform axis |
| 4 | The direction **reaches the GPU** — `BurstRequest` grows, HLSL twin and validator follow |
| 5 | The tool lives in the **Water Wizard** with a **2D preview**. NOT a Scene view overlay, NOT `PreviewRenderUtility` |
| 6 | **Nothing is serialized until Apply** |
| 7 | **Hull mesh override field ships in v1** — a visual root carries masts/cabin/crew that must not be sliced |
| 8 | Target is a **scene GameObject**, not a prefab. Do not build variant-safe handling yet |
| 9 | **Keep the rake shear.** Astern is per-probe, so a hard turn shears the flower. This is wanted |
| 10 | **Keep every knob**, including `petalSpinDegrees`. Bert wants the tweaks |
| 11 | **Solve from buoyancy ships**, and may extract shared lattice code (§6) |

Because an arc only throws outward, rotating the arc's **centre** from outward toward astern IS the
rake. There is no tilt term. Three new floats, not four.

---

## 3. Build order — five increments, each authorised separately

| # | Increment | Definition of done |
|---|---|---|
| **I1** | Wizard section + 2D side preview + draggable draft line. **No slicing.** The line just reports a world Y | the silhouette looks like the boat; dragging reports a sane Y |
| **I2** | Hull slice + probe dots in the preview + Apply | probes land on the real outline; **zero runtime change** |
| **I3** | `outwardLocal` on the probe + top view with arrows + Scene-view arrows | arrows point out of the hull; **still zero behaviour change** |
| **I4** | Arc reaches the GPU, fixed width, no rake | arc 60° gives wedges; **legacy scenes byte-identical** |
| **I5** | Runtime rake from measured velocity + cooldown staggering | petals swing astern; a 32-probe hull stops dropping one side |

I1–I3 touch no rendering and cannot regress anything. **I4 is the one that needs care.**

---

## 4. Ground truth — verified file:line, do not re-derive

### The pump
- `Runtime/WaterSprayPump.cs` — `SprayProbe { localOffset, mode, amountBoost }` at `:66-80`;
  probe array `:85`; `LateUpdate` `:131`; `StepProbe` `:180`; **`TryEmit` `:200-231`**
- `TryEmit` already computes the horizontal velocity and **discards everything but the magnitude** —
  the direction is free. `ProbeState.PreviousProbePosition` already exists
- `normalisedTriggerSpeed` already exists too: it is `strength`, just before `EmitSplash`. **Reuse it.**
  A second speed normalisation will drift from `maxImpactSpeed`
- Boat-mode probes sample the **analytic** surface (`excludeInteractiveRipples: true`, `:170-178`) so a
  hull's own wake cannot re-trigger them. Do not change this

### The editor
- `Editor/WaterSprayPumpEditor.cs` — `Layout { BowRow, RockRing }`, `Place()`, `WriteProbes()`,
  `ResolveWorldBounds()`, `OnSceneGUI` handles. **`WriteProbes` writes through `SerializedProperty` —
  that is what makes Undo and prefab overrides work. Keep it.**

### The wizard
- `Editor/WaterWizardWindow.cs` — `DrawBoatSection()` **`:562`** (already has a `Hull model (optional)`
  field), `DrawSplashSection()` **`:824`**. The new section goes between them
- Follow the wizard's conventions exactly: `EditorGUI.DisabledScope` + a `HelpBox` when the selection
  is invalid, and **name the target in the button** (`Add Splashes To "{body.name}"` → `Apply To "Boat"`)

### The burst chain — all three layers
- `Runtime/WaterSplashEmitter.cs` — `EmitSplash` `:256`; **the routing fork `:271-288`**;
  `EmitCrown` `:319`; the Shuriken fallback loop `:290-311`
- `Runtime/WaterFoamParticles.cs` — `BurstRequest` **`:50-57`, currently 12 floats**;
  `QueueSplashBurst` `:712-736`; drain `:617-628`; `MaxBurstsPerFrame = 16`, `MaxBurstDroplets = 64`
  at `:46-47`
- `Runtime/Shaders/WaterFoamParticles.compute` — `SpawnBurst` `:606-661`. **The line to change is
  `float angle = r0 * 6.2831853;`**
- `Editor/WaterWaveConstantsValidator.cs` — `SplashBurstConstantPairs` **`:209`**. **Extending this is
  not optional** — it is the machine check that catches C#/HLSL drift

### Buoyancy — everything the solver needs already exists at edit time
- `Runtime/WaterBuoyancy.cs` — `BuildSamplePoints` `:117-133`; **`AppendLatticePoints` `:137` is
  already `static`** and its comment already says it is *"Shared by the runtime sample-point build and
  the editor gizmo preview"*; `GetLocalBox` `:152`; `SphereSubmergedFraction` `:265`; `AbsScale` `:275`
- `Runtime/WaterBuoyancy.Gizmos.cs` — **`DrawLayoutPreview()` `:57-72` already reconstructs the whole
  lattice at edit time, before `Start` has run.** `PreviewProbeRadius` `:99-102`

### Convex hull (fallback only)
- `Editor/WaterBuildKit.ConvexHull.cs` — `BuildConvexHullMesh`, `ConvexWeldGridMeters = 0.005f`.
  **This is NOT the slicer** — it is 3D quickhull for the dry-interior carve and would smooth away a
  transom or a twin hull. Fallback shape only

---

## 5. The three formulas — exact, already derived, do not re-derive

**Equilibrium draft.** `WaterBuoyancy.ApplyPointForces` applies lift as `ForceMode.Acceleration`:
`lift_i = up * (gravity * buoyancy * fraction_i / N)`. Summing against gravity:

> **mean(fraction_i) = 1 / buoyancy**

`SphereSubmergedFraction` is monotonic in depth, so this is a **bisection on a guaranteed bracket**
between "fully clear" and "fully under". Not a fit, not an iteration count to tune.

Report rather than swallow: `buoyancy < 1` means **this hull sinks**; a non-zero `maxBuoyancyForce`
changes the equilibrium the solve ignores; no `WaterBuoyancy` at all falls back to the rest plane
silently (the normal case for a kinematic boat).

**The arc.** In `SpawnBurst`, with a **zero-direction sentinel** preserving legacy behaviour:

```hlsl
float2 d = float2(burst.dirX, burst.dirZ);
float angle = (dot(d, d) < BURST_DIR_MIN_SQ)
    ? r0 * 6.2831853                                  // legacy ring, BYTE-IDENTICAL
    : atan2(d.y, d.x) + (r0 * 2.0 - 1.0) * burst.arcHalfRadians;
```

`burst.*` is per-request, so the branch is uniform across the thread group — coherent, no divergence
cost. Everything downstream (`dir`, `ring`, `spawnXZ`, exclusion test, velocity) is untouched.

**The rake.**
```
outward  = TransformDirection(probe.outwardLocal), flattened + normalised
astern   = -normalise(horizontalVelocity)
rake     = lerp(petalRakeAtRest, petalRakeAtSpeed, normalisedTriggerSpeed)
petalDir = rotate(outward toward astern, by rake), then by petalSpinDegrees
```
**Rotate, do not lerp-and-normalise.** A linear blend collapses to zero when `outward` and `astern`
are opposed (transom probe, backing up) and yields a NaN direction. Use the signed angle and rotate by
`rake × angle`. Velocity near zero → `astern` undefined → rake falls to 0 → pure outward, which is
correct.

---

## 6. The one refactor Bert authorised

`DrawLayoutPreview()` (`WaterBuoyancy.Gizmos.cs:57-72`) already does the three things the solver
needs. **Extract that trio into one `internal` method the gizmo AND the solver call.** It is a move,
not a rewrite, and it follows the precedent `AppendLatticePoints` already set.

While doing it: `PreviewProbeRadius` (`Gizmos.cs:99-102`) is a **hand-copy** of `BuildSamplePoints`
lines `129-132` — its own comment admits it. The extraction should collapse **all three** call sites
to one formula. Adding a third copy for the solver is exactly the drift `WaterShaderNames` exists to
prevent.

**Behaviour must not change.** Verify by construction: same inputs, same outputs, no new maths.

---

## 7. Traps — each one has already cost someone

1. **`MaxBurstsPerFrame = 16` and the 17th request is DROPPED, not deferred.** A hull outline wants
   20–40 probes, all triggering together at planing speed. The drops are **not random** — they are
   always the probes late in the array, i.e. **one side of the boat**. Fix in I5 is cheap: stagger the
   cooldown phase `i / count × cooldown` per probe. **Also surface the arithmetic live in the tool**
   (*"24 probes × 0.06 s → peaks at 24/frame, cap is 16"*) so the user meets it while authoring.
2. **`_ShoreFoamActive` is `SurfLayerActive`, NOT `InjectionActive`** (`WaterVolume.Solver.cs:205-214`).
   Unrelated to this feature but adjacent: zeroing the surf foam gains does NOT stop lip spray.
3. **The exclusion volume already argues for petals.** From `SpawnBurst` verbatim: *"a pump probe
   sitting at a hull edge spawns half its ring inside the boat's exclusion box"* — those droplets flash
   for one frame and are culled. Petals should **recover roughly half the bow droplet budget.** Measure
   it; it is a perf win, not only a look.
4. **The Shuriken fallback must honour the arc too**, or a body without a GPU pool silently loses the
   petals and the two paths diverge. ~5 lines on the `Random.insideUnitCircle` angle.
5. **Roll is the honest gap.** Y-tracking handles heave and pitch free (`StepProbe` already has
   `sample.Height`), but heavy heel changes the outline in **XZ** and nothing recovers that. Below ~15°
   the error is under probe spacing. The escape hatch is the **Use current pose** toggle.
6. **Slice failure must be LOUD**, matching the convex-hull file's existing rule: warn and fall back to
   the AABB bow row, never silently ship a broken outline.
7. **`grep` over the device bridge times out at 45 s on this project.** Recipe: `find` source
   extensions into a list → `tar czf` into `Temp/claude_stage/` → stage the ONE tarball → extract in
   the sandbox → grep at full speed. ~238 files, 780 KB. Do this FIRST.

---

## 8. Backward compatibility contract

**A zero `outwardLocal` means "legacy full ring", end to end.** An existing scene deserialises to
zero → flows to the GPU as a zero direction → hits the kernel's existing `r0 * 2π` line. Every splash
authored before this feature stays **byte-identical**, and `WaterSplash` / `WaterInputRouter` need no
changes at all.

New pump knobs default to `petalArcDegrees 360 / petalRakeAtRest 0 / petalRakeAtSpeed 0 /
petalSpinDegrees 0` — so **adding the fields changes nothing**.

New `EmitSplash` parameters are optional and default to the legacy behaviour, so no existing call site
moves.

---

## 9. Before you start

**Unverified from the 2026-08-01 session** — check these compile/run before adding to them:
`Runtime/Shaders/WaterTransparent.shader` and the depth + Water Opacity terms in
`Runtime/Shaders/WebGpuWaterFogAPI.hlsl` have **never been exercised**. Shaders compile lazily, so a
clean C# build proves nothing. If anything in the transparent path misbehaves, it predates this work.

**Ask Bert before writing each increment.** He validates plans before code, every time.
