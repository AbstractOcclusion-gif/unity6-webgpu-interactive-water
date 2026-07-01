# Water system audit — Assets/WebGLWater

Read-only review of the full water system (Scripts + Editor + Shaders/HLSL/compute) against the
project coding standards (no magic numbers, no hardcoded shader strings, short single-responsibility
functions, fail-fast, no dead code, prefer immutability, minimal public API). Nothing here is changed —
this is a triage list to turn into commits.

**How to read it:** findings are grouped by severity. Line numbers are approximate (the quoted code is
the real anchor). Items marked **✓verified** were spot-checked directly against the file text.

## Resolved (2026-07-01)

Two commits landed against this list (compile/behavior reviewed by a second pass — no regressions):

- **All four blockers (1-4):** guarded `Caustics.shader` `sqrt`/area divides, guarded `GodRays.shader`
  `/steps` (+ C# `Mathf.Max(1, …)` clamp), and `WaterSplashEmitter` now resolves each droplet's body via
  `BodyContaining` instead of the primary.
- **Should-fix 5** (WaterSimulation kernel/property strings → consts + cached ids, precomputed `_delta`).
- **Should-fix 6 + 13** (builder shader/property names → consts; optional-shader validation warnings).
- **Should-fix 7 (partial)** (WaterQuality now derives `ThreadGroupSize` from `WaterSimulation`).
- **Should-fix 15** (`WaterInteractable.Active` → `IReadOnlyList`).
- **Magic numbers 16 (partial)** (WaterController camera consts + `activationDistance = CameraFarClip`
  coupling + seed-ripple consts). **Nit 23/24** (dropped `StepSimulation` default args; `TwoPi = 2·π`).

Second cleanup pass (2026-07-01):

- **Compute side of 7-8:** `#define THREAD_GROUP_SIZE 8` (all six `[numthreads]`) + named `MEAN_MIP_LOD`
  for the mean-height sample; stale `_Size` "(256)" comment updated.
- **Magic numbers 17, 20, 25:** `WaterObstacle` ortho-frustum consts + `_res`→`_resolution`; `WaterSplash`
  fallback/ripple consts (+ `_ctrl` field → local `body`, `c`→`center`); `PlanarReflection`
  depth-bits/min-size consts + `w`/`h`→`width`/`height`; `OrbitCamera` wheel/deadzone/pinch consts.

Third cleanup pass (2026-07-01) — **shared shader header**:

- New `Shaders/WaterShared.hlsl` (backend-agnostic: `IOR_*`, `POOL_HEIGHT`, `POOL_RIM_HEIGHT`,
  `CAUSTIC_PROJECTION_SCALE`, `RIM_SHADOW_*`, `IntersectCube`, `ProjectCausticUV`). Routed
  `WaterCommon`/`GodRays`/`WaterReceiver`/`Caustics`/`WaterSurface`/`PoolWall` through it and deleted the
  duplicated `IntersectCube`/IOR defines and the four-way caustic-projection copy. Named the remaining
  shared/flagged shader magic-numbers (rim sigmoid, sun-glint tint+exponent, underwater refraction tint,
  Fresnel bases, pool-rim height, `PoolWall` boost, `Caustics` focus). Covers audit items 9, 17, 18, 19,
  22, 24, 25 and shader-findings 7, 8 (rim), 10.

**Still open** (next passes): MPB-vs-global de-dup (10); per-frame body-resolution cache (11); `Build`
decomposition (12); `public`→`[SerializeField] private` (14, needs care — the builder writes many of
these, best done with the packaging asmdefs); a few remaining single-use shader nits (`WaterSurface`
peaked-refine `5`/`0.005` and foam-nudge `0.1`, the analytic box `float3(1,2,1)` y-max, the
`WaterCommon`/`Caustics` `length(p)`/`sqrt` guards — findings 9, 12, 13, 16); and deleting the deprecated
`WaterSphere.shader` (21 — do it from the Unity editor: it's still referenced by `Generated/Sphere.mat`,
so the editor will confirm nothing else uses that material before removal).

**Already clean (no findings):** `WaterQuality.cs`, `WaterFog.hlsl`, `WaterWaves.hlsl`,
`ObstacleDepth.shader` — named constants, immutable structs, guarded divides, WHY-comments. Good
baselines for the rest.

---

## Blockers — correctness risks that can corrupt output

These can inject NaN/Inf into the caustic RT (which *every* other shader samples) or produce wrong
gameplay results. Fix first.

1. **✓ `Caustics.shader:68` — unguarded area divide.** `float4 col = float4(oldArea / newArea * 0.2, ...)`.
   A degenerate/near-parallel projected triangle drives `newArea → 0` → Inf/NaN written into the caustic
   texture, then read back by the surface, pool, receiver and god-ray passes. **Fix:** `oldArea / max(newArea, 1e-6)`.

2. **✓ `Caustics.shader:50` — unguarded `sqrt`.** `sqrt(1.0 - dot(info.ba, info.ba))`. If the stored
   normal slope exceeds 1 the argument goes negative → NaN. Note the surface shader guards the *same*
   expression (`WaterSurface.shader:202`, `sqrt(max(1e-4, ...))`), so this is also an inconsistency.
   **Fix:** `sqrt(max(0.0, 1.0 - dot(info.ba, info.ba)))`.

3. **✓ `GodRays.shader:124` — divide-by-zero at 0 steps.** `int steps = (int)_GodRaySteps; float dt = (tExit - tEnter) / steps;`.
   Nothing clamps `steps`, and the **Low quality tier writes `_GodRaySteps = 0`** into the (shared)
   god-ray material — this is exactly the "0 step" hazard we noted during Phase 3b. It's currently masked
   only because Low also disables the renderer. **Fix:** `int steps = max(1, (int)_GodRaySteps);` and
   (C# side) don't push 0 to the shared material.

4. **`WaterSplashEmitter.cs:75` — splash drift resolves to the primary body.**
   `_controller = WaterController.Resolve(); // TODO(Phase 2): the body containing this emitter`. Splash
   particles always drift on the primary lake regardless of which body was actually splashed — a
   multi-instance correctness gap left open (the shared emitter was out of Phase 2's scope). **Fix:**
   resolve the emitting body per splash (`BodyContaining` at the splash point) or pass the body in.

---

## Should-fix — standards violations & drift hazards

### Hardcoded shader strings (explicit project-rule violation)

5. **`WaterSimulation.cs` — kernel & property names as raw strings.** `cs.FindKernel("Drop")` (…`"Update"`,
   `"Normal"`, `"Obstacle"`, `"Foam"`, `"Conserve"`), and `"_Size"`/`"_Delta"`/`"Src"`/`"Dst"`/`"_Center"`/
   `"_Radius"`/`"_Strength"`/`"ObstaclePrev"`/`"_FoamGenRate"`… repeated across methods (`"_Size"`/`"_Delta"`
   in three places). **Fix:** cache IDs in `static readonly int` via `Shader.PropertyToID` and hold kernel
   names in consts — exactly as `WaterController.cs` already does.

6. **`WaterSceneBuilder.cs` — material property & shader names hardcoded.** `m.SetFloat("_Underwater", 0f)`,
   `m.SetFloat("_Cull", 1f)` (×4), `m.SetColor("_BaseColor", …)`, and `Shader.Find("WebGLWater/WaterSurface")`
   / `"WebGLWater/PoolWall"` / `"WebGLWater/GodRays"` / `"Sprites/Default"` with no single source of truth.
   **Fix:** centralize property IDs and shader-name consts in one place.

### Cross-boundary / cross-file duplication (silent-drift hazards)

7. **✓ Thread-group size `8` duplicated 3 ways.** `WaterSim.compute` has `[numthreads(8, 8, 1)]` ×6, and
   C# owns the same value twice (`WaterSimulation.ThreadGroupSize = 8`, `WaterQuality` local
   `ThreadGroupSize = 8`). No shared definition ties them; they can silently diverge. **Fix:**
   `#define THREAD_GROUP_SIZE 8` in the compute + one C# const, with a "must match" comment.

8. **✓ `WaterSim.compute:225` — mean-height mip LOD hardcoded `20.0`.**
   `HeightMip.SampleLevel(..., 20.0)`. Works only because `SampleLevel` clamps to the top mip; it's
   decoupled from `_Size` (now tier-driven 128/256). **Fix:** derive from `log2(_Size)` or a named
   `TOP_MIP_LOD` with a comment.

9. **Cross-shader copy-paste.** `IntersectCube` is duplicated in `WaterCommon.hlsl` and `GodRays.shader`;
   `IOR_AIR`/`IOR_WATER` are re-`#define`d in `GodRays.shader` (and inlined as `1.0/1.333` in
   `WaterReceiver.shader:85`); the caustic projection `0.75 * (…) * 0.5 + 0.5` appears in four files
   (`WaterCommon`, `Caustics`, `GodRays`, `WaterReceiver`). **Fix:** one shared header — `IntersectCube`,
   the IOR defines, and a `ProjectCausticUV(poolPos, refractedLight)` helper — included everywhere.

10. **`WaterController.cs` — `WriteBodyProps` vs the `Publish*` globals duplicate every value.** Fog, foam,
    wave and volume uniforms are listed once for the MPB and again verbatim in `PublishFog`/`PublishFoam`/
    `PublishWaves`/`PublishVolume`. The MPB-vs-global split is *justified* (per-body renderers vs the
    global fallback for object shaders), but the value derivation shouldn't be written twice. **Fix:**
    funnel both through one writer that takes a "set color/set float" sink.

11. **Redundant per-frame body resolution (introduced in Phase 2).** `WaterBuoyancy` (FixedUpdate),
    `WaterMembership` (LateUpdate) and `WaterSplash` (FixedUpdate) each call `BodyContaining(...)` — up to
    3 spatial queries per object per frame with no sharing. **Fix:** cache the containing body per object
    per frame (or centralize resolution) — cheap now, matters as object count grows.

### Structure & error handling

12. **`WaterSceneBuilder.Build` — ~200-line method.** Does shaders, meshes, textures, materials, scene
    objects, demo crate, god rays, camera, lights, particles and controller wiring in one scope. **Fix:**
    decompose into `BuildAssets` / `CreateWaterRenderers` / `CreateDemoCrate` / `SetUpCamera` /
    `SetUpLighting` / `SetUpSplashParticles` / `WireController`.

13. **`WaterSceneBuilder.cs:40-46` — partial, silent shader validation.** Only `sfWater`/`sfCaust`/`compute`
    are validated up front; `sfReceiver`/`sfObstacle`/`sfPool`/`sfGodRays` degrade silently (e.g. a null
    receiver just skips the crate material with no warning). **Fix:** validate all required shaders up
    front; `Debug.LogWarning` each optional one that's skipped.

### API surface & immutability

14. **Public serialized fields → `[SerializeField] private`.** `WaterController` (nearly every field:
    `public Renderer surfaceAbove;`, `public float windSpeed`, …) and `OrbitCamera` (yaw/pitch/distance/
    speeds/limits) expose a wide mutable surface. Inspector visibility doesn't require `public`. **Fix:**
    `[SerializeField] private` unless another script genuinely needs the field.

15. **`WaterInteractable.cs:14` — mutable registry exposed.**
    `public static readonly List<WaterInteractable> Active = …`. Any caller can mutate it. **Fix:** expose
    `IReadOnlyList<WaterInteractable>` via a property over a private list.

### Magic-number clusters (meaningful literals worth naming)

16. **`WaterController.cs`** — seed ripples `20` / `0.03f` / `0.01f` (L314); camera `45f` / `0.01f` / `100f`
    where **`100f` silently duplicates the `activationDistance` default** its own tooltip says it "matches"
    (L318-320); splash `0.08f` / `0.1f` / `0.6f` / `4f` (L983). **Fix:** named consts; reference one
    far-clip const for both places.

17. **`WaterObstacle.cs:43,48`** — ortho frustum `2f * ey`, `4f * ey + 0.02f`, near `0.01f`; plus a second
    extent-clamp epsilon `1e-4f` that disagrees with `WaterController.VolumeExtentSafe`'s `1e-5f`. **Fix:**
    name the frustum consts; pass the already-safe extent in so the epsilon is defined once.

18. **`WaterSurface.shader`** — sun glint `float3(10,8,6)` + exponent `5000.0` (L178); peaked-refine loop
    `5` / step `0.005` (L191); refraction tint `float3(0.8,1.0,1.1)` (×2) and Fresnel bases `0.5` vs `0.25`
    (L216-235); pool rim `2.0/12.0` and box `float3(1,2,1)`. **Fix:** named `#define`s.

19. **Ported analytic constants recur bare across `WaterCommon`/`Caustics`/`GodRays`/`WaterReceiver`** —
    rim `2.0/12.0`, sigmoid `-200.0`/`10.0`, projection `0.75`, caustic brightness `0.2`. Faithful to Evan
    Wallace's renderer.js, but unnamed. **Fix:** one shared "original constants" header with named defines
    + a WHY comment resolves most shader magic-number findings at once.

20. **`WaterSplash.cs`** — fallback half-extent `0.15f` (dupes `WaterBuoyancy.FallbackHalfExtent`), ripple
    `Clamp(halfX, 0.02f, 0.2f)`, `speed * 0.02f`, divisor floor `0.01f`. **`WaterSplashEmitter.cs`** — burst
    min `3`, spread/upward ranges, strength floor. **Fix:** shared named consts.

---

## Nits — cleanups

21. **✓ Dead code: delete `WaterSphere.shader`.** Its own header says *"DEPRECATED … inert stub … can be
    safely deleted"*. Also remove `Generated/Sphere.mat` and `Generated/UnitSphere.asset` once no material
    references remain.

22. **Naming.** `WaterObstacle._res` → `_resolution` (sibling spells it out); single-letter locals off the
    allowed list — `g` (WaterBuoyancy gravity), `c` (WaterSplash center), `w`/`h` (PlanarReflection),
    `v`/`t`/`n`/`s`/`m` (builder mesh code); `KW_SSR`/`KW_PLANAR` and `MODE_*` use SCREAMING_CASE unlike the
    file's other PascalCase consts, and `MODE_NONE=-1, MODE_ADD_DROPS=0, MODE_ORBIT=2` skips `1`
    (removed mode). **Fix:** spell out names; make the three modes a private `enum`.

23. **Duplicated defaults.** `WaterSimulation.StepSimulation(waveSpeed = 2f, damping = 0.995f)` embeds the
    same defaults as the controller's inspector fields (two sources of truth); `WaterQuality.Default`'s
    `256/1024/24` duplicate the High-tier inspector defaults. **Fix:** drop the compute defaults / share
    named constants.

24. **`WaterWaveBank.cs`** — `const float TwoPi = 6.2831853f` literal alongside `Mathf.PI` usage (drift
    risk → make it `2f * Mathf.PI`); `SampleHeight`/`SampleSlope` recompute the wave phase independently
    (must stay consistent with each other *and* the shader) — extract a shared `Phase(...)` helper.

25. **Misc single literals** — `PlanarReflection` depth bits `24`, min RT size `8`, wheel divisor `120f`
    (OrbitCamera); `WaterSim.compute` neighbour-average `0.25` (×3), and a **stale comment** `_Size // texture
    resolution (256)` (resolution is now tier-driven). **Fix:** name them / update the comment.

26. **Latent divides worth a cheap guard** — `WaterCommon.hlsl:55` `scale /= length(p)` and `IntersectCube`
    `/ ray` (zero component → Inf). Low real-world risk, but `max(…, 1e-4)` is free insurance.

---

## Suggested order

1. **Blockers 1-3** (shader NaN/÷0 guards) — tiny, high-value, protect the shared caustic RT; ships with
   the Phase 3 reflection commit cleanly.
2. **Blocker 4** (splash-emitter body resolution) — closes the last multi-instance correctness gap.
3. **Should-fix 5-6** (hardcoded shader strings) — the clearest rule violations; mechanical.
4. **7-8** (thread-group + mip-LOD coupling) — a few lines, removes real drift hazards.
5. **9-11** (shared shader header, MPB/global consolidation, per-frame resolution cache) — the meatier
   refactors; do when you want to reduce duplication structurally.
6. Magic-number naming (16-20), API-surface tightening (14-15), and nits as background cleanup — the
   shared "original constants" header (19) knocks out most shader magic-numbers in one pass.

No active defects were found in the shipping render/sim path beyond the guarded-divide risks above; the
codebase is in good shape overall — most findings are hardening and standards polish, not bugs.
