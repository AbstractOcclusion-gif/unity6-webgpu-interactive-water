# PLAN — increment I2: hull slice, probe dots, Apply

**Design only. No code written.** Follows `PLAN_hull_petal_spray_v2.md` §2 and
`PLAN_hull_petal_spray_addendum_A.md` §7. I1 is confirmed and on disk.

Every `file:line` below was re-read this session, not carried from the earlier docs.

---

## 0. What changed from the v2 plan

Three corrections, each found by reading the source rather than the plan.

### 0.1 "Use current pose" is already the behaviour — drop it as a mode

v2 §1.4 lists three draft sources, the third being *"slice at the hull's transform as it sits, for the
heeled case"*. That assumed the slicer works in the hull's canonical local frame.

It does not. I1 already projects **world** vertices through `filter.transform.localToWorldMatrix`
(`WaterHullOutline2D.cs`, `AppendFilter`), and the slicer will do the same. **A heeled hull is
therefore already sliced heeled.** The mode would be a no-op switch.

⇒ Two sources, not three: **Solve from buoyancy** and **Rest plane**. The escape hatch v2 wanted is
free.

### 0.2 The outward normal must come from WINDING, not the centroid

v2 §2 step 6: *"outward normal = 2D loop normal, sign-checked against the loop centroid."*

The centroid test is wrong for exactly the shape it needs to be right for. A concave outline — a hull
with a pronounced tumblehome, or any banana-shaped loop — can have its centroid **outside the loop**,
which flips the sign for a whole run of probes and sprays them into the hull.

The robust sign is the loop's **signed area**: orient every kept loop counter-clockwise, then the
outward normal is a fixed rotation of the tangent. Correct for concave loops by construction, and it
deletes v2 §6.4's "drop points whose normal faces back into the hull" special case — with correct
winding, none do.

### 0.3 The buoyancy solve reads the COLLIDER, not the hull mesh

`WaterBuoyancy.BuildSamplePoints` (`:117-133`) lays its lattice across `GetLocalBox(_col, ...)`
(`:152-169`) — the collider's box, never a mesh. The wizard's Hull mesh field picks a *mesh*.

This is not a bug to fix. It means **the solved draft is where the boat actually floats**, which is
what we want, even when that differs from where the hull mesh's waterline "ought" to be. But it has to
be said out loud in the UI, or the first hull whose collider is the auto-fitted box from
`WaterBuildKit.Boat.cs:140` will read as a broken solve.

---

## 1. The equilibrium formula — re-derived from source, and it holds exactly

`ApplyPointForces` (`WaterBuoyancy.cs:219-236`):

```csharp
Vector3 lift = up * (gravity * buoyancy * weight);   // weight = fraction_i * (1 / N)
_rb.AddForceAtPosition(lift, world, ForceMode.Acceleration);
```

`ForceMode.Acceleration`, so the terms sum as accelerations:

```
a_lift = gravity * buoyancy * Σ fraction_i / N = gravity * buoyancy * mean(fraction)
```

Against gravity: **`mean(fraction) = 1 / buoyancy`**. Confirmed, exact.

Two conditions the derivation quietly needs, both true at rest:

- the drag term is `_rb.GetPointVelocity(world) * waterLinearDamping` (`:243-252`) → **zero at rest**
- the drift term is `sample.Velocity` (`:233`) → **zero on a flat rest plane**

So this is a *rest* equilibrium, which is exactly what a draft is.

### The bracket is guaranteed, so bisection needs no tuning

`SphereSubmergedFraction` (`:265-273`) is monotonic in depth, so `mean(fraction)` is monotonically
decreasing in the hull's vertical offset `dy`. With lattice points `p_i` and rest plane `restY`:

| Bound | Value | `mean(fraction)` |
|---|---|---|
| fully under | `dy_low  = restY - radius - max(p_i.y)` | 1 |
| fully clear | `dy_high = restY + radius - min(p_i.y)` | 0 |

Target `1 / buoyancy` lies in `(0, 1]` whenever `buoyancy >= 1`. Bisect to a metre tolerance, not an
iteration count.

### Move the plane, not the boat

The solve yields `dy`, the offset the hull would settle by. Rather than moving the hull, **slice the
hull where it sits at plane `Y = restY - dy`** — every point's depth is `restY - dy - p_i.y` either
way, so the two are identical. Nothing in the scene is touched.

### The three honest edges (v2 §1.3, all confirmed against source)

| Case | Source | Behaviour |
|---|---|---|
| `buoyancy < 1` | `:30` | mean fraction would have to exceed 1 — **this hull sinks.** Report it; do not clamp |
| `maxBuoyancyForce > 0` | `:225-226` clamps the lift → changes equilibrium | warn that the solve ignores it |
| no `WaterBuoyancy` | — | fall back to the rest plane **silently** — the normal case for a kinematic boat |

---

## 2. The slicer — `Editor/WaterHullSlice.cs` (new)

Plane/triangle walk. **Not** `WaterBuildKit.ConvexHull.cs` — that is 3D quickhull for the dry-interior
carve and would smooth away a transom or a twin hull.

1. **Collect triangles** in world space from the Hull mesh field, else every `MeshFilter` under the
   hull object. Same source resolution as I1's silhouette, so the drawing and the slice never disagree.
2. **Intersect each triangle** with `y = draftY` → 0 or 1 segment. Sign-of-vertex classification;
   a triangle with a vertex exactly on the plane is nudged by an epsilon rather than special-cased.
3. **Weld endpoints** on a tolerance grid — reuse `ConvexWeldGridMeters = 0.005f`
   (`WaterBuildKit.ConvexHull.cs:14`), widened to `internal`. One definition, not a second copy.
4. **Chain into loops** through an endpoint → segment map. An open chain closes across its gap and
   warns with the gap size.
5. **Keep loops by enclosed area**, discard slivers. **A catamaran legitimately yields two loops.**
   Then **drop any loop fully contained in another kept loop** (point-in-polygon on one vertex) — that
   is the cockpit rim, and without it the deck opening gets probes spraying inboard. ~15 lines, and it
   keeps catamarans. **DECIDED with Bert 2026-08-01**, overriding v2 §2 step 4's plain area filter.
6. **Orient every kept loop CCW** (signed area), so the outward normal is a fixed rotation of the
   tangent. See §0.2.
7. **Resample by arc length**, not by index, so a dense bow does not crowd probes. Probe budget split
   across loops in proportion to perimeter.
8. **Push out by `inset`** (default 0.03 m) along the outward normal, so probes sit just off the plating.

The normals are computed in I2 because the inset needs them — but **I2 does not write them**. They
reach the probe in I3.

### Failure is loud, never silent — matching the convex-hull file's existing rule

| Case | Behaviour |
|---|---|
| plane misses the mesh entirely | warn, fall back to today's AABB bow row, and the tool says which |
| fewer than 3 usable segments | same |
| open loop | close across the gap, warn with the gap size |
| no readable mesh | I1's existing message, unchanged |

---

## 3. Apply

Target is the **hull object** (a scene GameObject — decision 8; no prefab-variant handling yet).

- No `WaterSprayPump` on it → `Undo.AddComponent<WaterSprayPump>`. The build kit does not add one
  (`PARTICLE_FOAM_INVENTORY.md` §4: *"Not auto-added anywhere"*), so this is the common path.
- Probes written through **`SerializedProperty`** — that is what makes Undo and prefab overrides work
  (`WaterSprayPumpEditor.cs:112-124`). Mode stamped `Boat`, as the existing bow row does.
- One `Undo.CollapseUndoOperations` group, matching every other wizard action.

**Reuse, do not re-type.** `WaterSprayPumpEditor.WriteProbes` is an instance method bound to the
inspector's own `serializedObject`. Extract the loop into an `internal static` taking a
`SerializedObject`; the instance method becomes a one-line call. Same shape as I1's `ResolveWaterY`
widening — the alternative is an eight-line copy that drifts the first time the probe struct grows,
which is precisely what I3 does to it.

### The burst-budget warning ships with Apply, not after it

Trap #1. `MaxBurstsPerFrame = 16` (`WaterFoamParticles.cs:46`) and **the 17th request is dropped, not
deferred** (`:716`). A hull outline wants 20–40 probes. The drops are not random — they are always the
probes late in the array, i.e. **one side of the boat**.

The tool states the arithmetic live, next to the probe-count slider, quoting the real constant
(widened to `internal`, never copied):

> ⚠ 24 probes can trigger in the same frame; the burst cap is 16 and the surplus is **dropped** —
> always the probes late in the array, so one side of the hull goes quiet. Cooldown staggering lands
> in I5.

The fix is I5's. Meeting it while authoring is what stops it being discovered as mysterious one-sided
spray.

---

## 4. The one authorised refactor (prompt §6)

`WaterBuoyancy.Gizmos.cs` `DrawLayoutPreview()` (`:60-72`) already reconstructs the whole lattice at
edit time, before `Start` has run. It does the three things the solver needs.

**Extract them into one `internal` method on `WaterBuoyancy` that the gizmo, the runtime build and the
solver all call:**

```csharp
internal void BuildProbeLayout(out Vector3[] localPoints, out float sphereRadius)
```

Three call sites collapse to one formula:

| Call site | Today | After |
|---|---|---|
| `BuildSamplePoints()` `:117-133` | builds lattice + radius | calls it, assigns the fields |
| `DrawLayoutPreview()` `Gizmos:60-72` | rebuilds the same lattice | calls it |
| the new draft solver | would be a third copy | calls it |

And **`PreviewProbeRadius` (`Gizmos.cs:98-102`) is deleted.** Its own comment admits it is a hand-copy
of `BuildSamplePoints:130-132`. Adding a third copy for the solver is exactly the drift
`WaterShaderNames` exists to prevent.

**One semantic difference, and it is provably nil.** `BuildSamplePoints` reads the cached `_col`;
`DrawLayoutPreview` reads `GetComponent<Collider>()`. The shared method must use `GetComponent`, since
`_col` is null at edit time. `_col` is assigned in `Awake` (`:103`) and `BuildSamplePoints` runs from
`Start` (`:108`), so at runtime the two are the same object. Cost: one `GetComponent` per floater at
`Start`.

`SphereSubmergedFraction` (`:265`) goes `private static` → `internal static` so the solver evaluates
the *same* curve the physics does.

---

## 5. Files

| # | File | Change |
|---|---|---|
| 1 | `Editor/WaterHullSlice.cs` | **NEW** — the plane/triangle walk (§2) |
| 2 | `Editor/WaterHullDraftSolver.cs` | **NEW** — the bisection + its three reports (§1) |
| 3 | `Editor/WaterWizardWindow.HullSpray.cs` | draft source, probe count, inset, dots, budget warning, Apply |
| 4 | `Editor/WaterSprayPumpEditor.cs` | `WriteProbes` gains an `internal static` overload |
| 5 | `Editor/WaterBuildKit.ConvexHull.cs` | `ConvexWeldGridMeters` → `internal` |
| 6 | `Runtime/WaterFoamParticles.cs` | `MaxBurstsPerFrame` → `internal` |
| 7 | `Runtime/WaterBuoyancy.cs` | extract `BuildProbeLayout`; `SphereSubmergedFraction` → `internal` |
| 8 | `Runtime/WaterBuoyancy.Gizmos.cs` | call the extraction; **delete `PreviewProbeRadius`** |

**Runtime behaviour change: none.** Files 6–8 are widenings and a move; file 6 changes an access
modifier only.

### The split — I2a / I2b. **DECIDED with Bert 2026-08-01.**

Eight files is well past the addendum's "1–2 editor", because I2 absorbed the draft source. The seam
is clean and each half is testable alone:

| | Files | Testable by |
|---|---|---|
| **I2a** | 1, 3, 4, 5, 6 — slice + dots + Apply, draft = rest plane + offset | do probes land on the real outline? **No runtime file touched.** |
| **I2b** | 2, 3, 7, 8 — the buoyancy solve + the lattice extraction | does the solved draft match where the boat actually floats in play mode? |

I2a carries all the geometry risk and none of the runtime risk. I2b touches two runtime files and has
a crisp pass/fail: press Play, and the hull should settle onto the line the tool drew.

---

## 6. Deferred, deliberately

- `outwardLocal` on the probe, the top view and the Scene-view arrows — **I3**
- the arc reaching the GPU — **I4**
- the runtime rake and the cooldown staggering — **I5**
- roll changing the outline in XZ — v2 §1.2's honest gap; below ~15° of heel the error is under probe
  spacing, and the slice already follows the hull's live pose (§0.1)

Nothing gets written until you say go.
