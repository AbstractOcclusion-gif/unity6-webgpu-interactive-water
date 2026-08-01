# PLAN v2 — Hull-fitted spray pump with directional petals

**Status: DESIGN ONLY. No code written. Awaiting validation.**
Supersedes v1. Changes are marked **NEW in v2**.

---

## 0. Decisions locked

| # | Decision | Consequence |
|---|---|---|
| 1 | Hull outline from the **waterline cross-section** of the mesh | editor geometry pass + a draft plane (§1) |
| 2 | A petal is an **arc** — narrow the ring to a wedge | `angle` remap in `SpawnBurst`, not a velocity rotation |
| 3 | Direction from the **probe's own measured velocity** | free — the pump already diffs probe positions |
| 4 | Direction **reaches the GPU** | `BurstRequest` grows; HLSL twin + validator follow |
| 5 | **NEW** — tool lives in a **Scene view overlay**, water temporarily hidden | no second camera; but scene state is mutated (§6.0) |
| 6 | **NEW** — **Solve from buoyancy** ships in v1 | exact, not a heuristic (§1.3) |
| 7 | **NEW** — **nothing written until Apply** | preview held in editor state, one Undo step on commit |

Because an arc only throws outward, rotating the arc's *centre* from outward toward astern **is** the
rake. Three new floats, not four.

---

## 1. The draft

### 1.1 The split

> **Author time decides SHAPE. Runtime decides HEIGHT.**

The outline in XZ is a property of the mesh and a chosen draft — slice once in the editor. The
height relative to the water changes every frame, and `StepProbe` **already** holds `sample.Height`
per probe from a query it already pays for, so sliding each probe's Y onto the live surface costs
nothing new. Heave and moderate pitch handled free; `surfaceBand` keeps a lifted probe silent.

### 1.2 Where this is not enough

Heavy **roll** changes the outline in XZ, and no Y-tracking recovers it. Below ~15° of heel the error
is under probe spacing — ignore it in v1. Escape hatch is the **Use current pose** toggle: heel the
hull in the editor and slice the heeled outline. Two blended probe sets is a v2 idea, not v1.

### 1.3 Solve from buoyancy — **NEW in v2**, and it is exact

`WaterBuoyancy.ApplyPointForces` applies lift as `ForceMode.Acceleration`:

```
lift_i = up * (gravity * buoyancy * fraction_i * (1 / N))
```

Summing and setting it against gravity gives the equilibrium condition outright:

> **mean(fraction_i) = 1 / buoyancy**

`fraction_i` is `SphereSubmergedFraction(depth_i, sphereRadius)` — a monotonic S-curve in depth. So
the mean is monotonic in the hull's vertical offset, and the draft is a **bisection on a guaranteed
bracket** between "fully clear" and "fully under". No fitting, no iteration count to tune.

Three honest edges the solver must report rather than swallow:

| Case | Behaviour |
|---|---|
| `buoyancy < 1` | mean fraction would have to exceed 1 — **this hull sinks.** Report it; do not clamp to "almost submerged" |
| `maxBuoyancyForce > 0` | the clamp changes equilibrium. Warn that the solve ignores it, or refuse |
| no `WaterBuoyancy` on the object | fall back to the rest plane silently — that is the normal case for a kinematic boat |

**Blocker to note:** `_localPoints` and `_sphereRadius` are private and built in `OnEnable`, so they
do not exist at edit time. The solver needs the same lattice. Cleanest fix is a pure refactor —
extract the lattice construction into an `internal static` the editor can call. No behaviour change,
and it keeps one definition of the lattice rather than a second copy that drifts.

### 1.4 Draft sources, in priority order

1. **Solve from buoyancy** (when a `WaterBuoyancy` is present) — the default, usually untouched
2. **Rest plane + offset slider** — the manual path
3. **Use current pose** — slice at the hull's transform as it sits, for the heeled case

---

## 2. Part A — extracting the outline *(unchanged from v1)*

New file: `Editor/WaterSprayPumpHullSlice.cs`. Plane/triangle walk, **not** a convex hull:

1. Collect triangles under the pump, or from an explicit **Hull Mesh** override (a visual root
   carrying masts and crew must not be sliced).
2. Intersect each triangle with `y = draftY` → 0 or 1 segment.
3. Weld endpoints on a tolerance grid (reuse `ConvexWeldGridMeters = 0.005f`), chain into loops.
4. Keep loops by enclosed area; discard slivers. **A catamaran legitimately yields two loops — keep
   both.**
5. **Resample by arc length**, not index, so a dense bow does not crowd probes.
6. Outward normal = 2D loop normal, sign-checked against the loop centroid.
7. Push outward by an **inset** knob (default 2–5 cm) so probes sit just off the mesh.

Failure is loud, never silent — the package's existing convex-hull rule:

| Case | Behaviour |
|---|---|
| plane misses the mesh | warn, fall back to today's AABB bow row, tool says so |
| < 3 usable segments | same |
| open loop | close across the gap, warn with the gap size |
| no `MeshFilter` | existing warning path, unchanged |

`BuildConvexHullMesh` is **not** the slicer — it is 3D quickhull for the dry-interior carve and would
smooth away a transom or a twin hull. It stays as the fallback shape only.

---

## 3. Part B — the probe gains a direction *(unchanged from v1)*

```csharp
[Tooltip("Outward horizontal direction this probe throws toward, in local space. Zero = legacy
          full-ring burst (what every probe placed before this feature does).")]
public Vector3 outwardLocal;
```

**Backward compatibility is a zero-vector sentinel, end to end.** Old scenes deserialise to zero,
which flows to the GPU as a zero direction, which hits the kernel's existing `r0 * 2π` line. Every
splash authored before this feature stays **byte-identical**, and `WaterSplash` /
`WaterInputRouter` need no changes at all.

Pump-wide knobs, defaults chosen so adding them changes nothing (`360 / 0 / 0 / 0`):

| Field | Range | Meaning |
|---|---|---|
| `petalArcDegrees` | 10 – 360 | wedge width. **360 = today's ring** |
| `petalRakeAtRest` | 0 – 1 | turn from outward toward astern when barely moving |
| `petalRakeAtSpeed` | 0 – 1 | the same at full trigger speed — the acceleration behaviour |
| `petalSpinDegrees` | -180 – 180 | flat extra rotation, for asymmetric looks |

---

## 4. Part C — the arc reaching the GPU *(unchanged from v1)*

### 4.1 `Runtime/WaterFoamParticles.cs`

```
now:  center(3) radius strength upSpeed outSpeed seed count lifeMin lifeMax size   = 12 floats
new:  ...as above... + dirX dirZ arcHalfRadians + reserved                         = 16 floats (64 B)
```

The 16th is a **named reserved slot**, earmarked for a future cone tilt — not anonymous padding. The
file already has precedent (*"Per-burst droplet life/size (was padding)"*).

### 4.2 `Runtime/Shaders/WaterFoamParticles.compute`

Struct twin, then one line in `SpawnBurst`:

```hlsl
float2 d = float2(burst.dirX, burst.dirZ);
float angle = (dot(d, d) < BURST_DIR_MIN_SQ)
    ? r0 * 6.2831853                                  // legacy ring, byte-identical
    : atan2(d.y, d.x) + (r0 * 2.0 - 1.0) * burst.arcHalfRadians;
```

Per-request value → uniform across the thread group → coherent branch, no divergence cost.
Everything downstream is untouched.

### 4.3 `Runtime/WaterSplashEmitter.cs`

```csharp
public void EmitSplash(Vector3 surfacePos, float strength, float radius,
                       float amountScale = BaseAmountScale,
                       Vector3 direction = default,          // zero = full ring
                       float arcDegrees = FullRingDegrees)
```

**The Shuriken fallback must honour them too**, or a body without a GPU pool silently loses the
petals and the two paths diverge. ~5 lines on the `Random.insideUnitCircle` angle.

### 4.4 `Editor/WaterWaveConstantsValidator.cs`

`SplashBurstConstantPairs` gains the new constants. Not optional — that validator is the mechanism
that catches exactly this class of C#/HLSL drift.

---

## 5. Part D — the runtime rake *(unchanged from v1)*

`TryEmit` already computes the horizontal velocity vector and throws away everything but its
magnitude. Keep the vector:

```
outward  = TransformDirection(probe.outwardLocal), flattened + normalised
astern   = -normalise(horizontalVelocity)
rake     = lerp(petalRakeAtRest, petalRakeAtSpeed, normalisedTriggerSpeed)
petalDir = rotate(outward toward astern, by rake), then by petalSpinDegrees
```

- **Rotate, do not lerp-and-normalise.** A linear blend collapses to zero when `outward` and `astern`
  are opposed (transom probe, backing up) → NaN direction. Use the signed angle and rotate by
  `rake × angle`.
- `normalisedTriggerSpeed` **already exists** — it is `strength` before it goes to `EmitSplash`.
  Reuse it; a second speed normalisation with its own range will drift from `maxImpactSpeed`.
- Velocity near zero → `astern` undefined → rake falls to 0 → pure outward. Correct: a stationary
  hull throws straight out.

---

## 6. Part E — the tool, and what will bite

### 6.0 **NEW in v2** — the Scene view overlay, and restoring what it hides

The tool suppresses the water while active. **This is the riskiest part of the whole feature**, not
the geometry, because it mutates state the user owns.

**Do NOT touch `Renderer.enabled`.** That is serialized component state; a leaked `false` gets saved
into the scene and the user's water is gone with no clue why. Use editor-only visibility:
`SceneVisibilityManager.instance.Hide(...)` / `Show(...)`.

> ⚠ **Must verify in Unity before building on it:** whether `SceneVisibilityManager` state persists
> into the `.unity` asset on save. If it does, the tool must force `Show` before any save, and hook
> `EditorSceneManager.sceneSaving`. I cannot confirm this from source alone — it is the first thing
> to test.

Restore has to survive five exits, not one:

| Exit | Hook |
|---|---|
| tool closed normally | `OnDisable` |
| script recompile / domain reload | `AssemblyReloadEvents.beforeAssemblyReload` |
| entering play mode | `EditorApplication.playModeStateChanged` — restore *before* the transition |
| scene saved while active | `EditorSceneManager.sceneSaving` (see the verify note above) |
| editor killed, reopened | the hidden set must survive the reload to be undone |

The last one is why the hidden set lives in a **`ScriptableSingleton` with `[FilePath]`**, not in the
`Editor` instance — editors do not survive domain reload, and a set held only in memory becomes an
un-restorable orphan the first time a script recompiles mid-drag.

**Belt and braces:** a "Restore hidden water" menu item that clears the singleton unconditionally.
Cheap, and it means a bug here is an annoyance rather than a support thread.

### 6.1 The tool surface

Scene view overlay, active only while the pump is selected and the tool is engaged:

- **Water hidden** chip (toggle, so it can be turned back on to check the result in place)
- **side / top / front** chips — snap the Scene camera; orbit and zoom stay yours
- **Draft handle** — a draggable line, live re-slice as it moves
- **Preview** — outline, probe dots, arc wedges in the existing mode colours
- **Apply to pump** / **Revert preview** — nothing is serialized before Apply (decision 7)

Inspector holds the numbers: Hull Mesh, Draft source, Draft offset, Solve from buoyancy, Probe
count, Outward inset, and the four petal knobs.

### 6.2 ⚠ The exclusion volume already argues for this feature

From `SpawnBurst`, verbatim:

> *"Never born inside a dry volume: **a pump probe sitting at a hull edge spawns half its ring inside
> the boat's exclusion box**, and each of those droplets flashed for exactly one frame before Update
> culled it — per-burst strobing at the bow."*

Half of every bow burst is currently thrown *into the hull* and discarded. Petals **recover roughly
half the droplet budget at the bow**. Worth measuring — I expect it to show.

### 6.3 ⚠ The burst budget is the real risk

`MaxBurstsPerFrame = 16`, and **the 17th request in a frame is dropped, not deferred.**

Today a pump has ~7 probes. A hull outline wants 20–40. At `emitCooldownSeconds = 0.06` with all
probes triggering together at planing speed, a 32-probe pump exceeds 16 regularly — and the drops
are **not random**: they are always the probes late in the array, i.e. one side of the boat.

1. **Stagger the cooldowns** — phase offset `i / count × cooldown` at first emit. Same total spray,
   spread across frames, one float per probe. **Recommended.**
2. Raise `MaxBurstsPerFrame` — one constant in two places; raises the ceiling for every caller
   rather than fixing the clumping.
3. Coalesce per pump — one request carrying N arcs. Real fix, real work, not v1.

**NEW in v2:** the tool should show the arithmetic live — *"24 probes × 0.06 s → peaks at 24/frame,
cap is 16"* — so the user meets this at authoring time, not as mysterious one-sided spray.

### 6.4 Smaller ones

- **Probe count vs cost:** each probe is one entry in a batched query. 40 is fine; §6.3 is the limit,
  not sampling.
- **Prefab overrides:** placement writes through `SerializedProperty` — that is what makes Undo and
  prefab overrides work. Keep it.
- **Concave notches:** an outline point whose normal faces back into the hull is dropped by the tool,
  never shipped as a probe that sprays inward.
- **Execution order:** pump and pool both use default order, so a burst may drain this frame or the
  next. Pre-existing and harmless; the staggering in 6.3 makes it marginally more visible. Comment,
  do not fix.

---

## 7. Build order — six increments **(NEW: I0)**

| # | Increment | Files | Testable by |
|---|---|---|---|
| **I0** | Overlay shell: hide/restore water across all five exits, view chips, no geometry | 1 editor + 1 singleton | toggle it, recompile mid-session, enter play mode, save — water always comes back |
| **I1** | Hull slice + placement + live preview + Apply | 2 editor | probes land on the real outline; zero runtime change |
| **I2** | `outwardLocal` on the probe + arrow gizmos | 1 runtime, 1 editor | arrows point out of the hull; still zero behaviour change |
| **I3** | Arc reaches the GPU, fixed width, no rake | 4 files (§4) | arc 60° → wedges; legacy scenes byte-identical |
| **I4** | Runtime rake from measured velocity + sliders | 1 runtime | petals swing astern under acceleration |
| **I5** | Cooldown staggering | 1 runtime | 32-probe hull stops dropping one side |

**I0 first, deliberately.** It is the only part that can damage a user's scene, it is independent of
everything else, and it is worth proving before a single triangle is sliced. I0–I2 touch no
rendering. I3 is the one that needs care; its safety net is the zero-direction sentinel (§3).

---

## 8. Still open — from v1, unanswered

1. **Which mesh is the hull** on your boat — whole visual root, or a specific child? Decides whether
   the Hull Mesh override is v1 or v2.
2. **Is the boat a prefab you instance**, or scene objects? Changes how Apply should write.
3. **Rake shear:** astern is per-probe, so on a hard turn inner and outer probes rake differently and
   the flower shears. I think that reads as alive. Say if you want it rigid off one hull-wide
   velocity instead.
4. **Anything to drop** to keep I0–I3 tight.

Plus one **NEW in v2**:

5. **`WaterBuoyancy` lattice refactor** — extracting the point lattice to an `internal static` is a
   pure refactor with no behaviour change, but it touches a file that is not otherwise in scope.
   Confirm you are happy for the solver to reach in there, or I keep the solver to the rest plane
   and drop decision 6.

Nothing gets written until you say go.
