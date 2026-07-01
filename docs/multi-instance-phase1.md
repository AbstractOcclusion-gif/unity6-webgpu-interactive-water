# Phase 1 — de-globalize into a `WaterVolume` component

> **Status: IMPLEMENTED (2026-06-30), pending editor compile/test.** Class kept as `WaterController`
> this pass (cosmetic rename to `WaterVolume` deferred to reduce blind-refactor risk). Frame is now
> transform-driven (`volumeCenter`/`volumeEuler` fields removed — position/rotation come from the
> component's Transform; `volumeExtent` stays a field). Per-body uniforms go through a
> `MaterialPropertyBlock` on the body's `surfaceAbove/Under/poolRenderer/godRayRenderer` refs; the
> `isPrimary` body also mirrors to globals so objects work (`WaterController.Primary` / `.Resolve()`).
> Shared globals (sun, sky, tiles, `_WaveTime`) still `SetGlobal`. `_WaveCount` became a `float` so it
> binds via MPB. Caustic material is fed this body's `_WaterTex`. Builder wires the renderer refs.
> Objects/buoyancy/splash/interactable resolve via `WaterController.Resolve()` (Phase 2 TODOs marked).


Branch: `experiment/multi-instance-water`. Goal: one body becomes a self-contained, instantiable
component fed through `MaterialPropertyBlock` instead of `Shader.SetGlobal`, so N bodies coexist.
Bounded scope: surfaces/pool/god-rays go per-instance; floating objects still follow ONE body this
phase (proper per-object association is Phase 2).

## The mechanic

`WaterController` -> `WaterVolume` (a component you can have many of). It already owns its sim,
obstacle, caustic pass, wave bank and volume frame. The only real change is **how it publishes**:

- Per-body data moves onto a `MaterialPropertyBlock` applied to THIS body's own renderers
  (surface above, surface under, pool, god rays — serialized references):
  `_WaterTex`, `_FoamMask`, `_CausticTex`, `_VolumeCenter/_VolumeExtent/_VolumeRot`,
  `_WaveA/_WaveB/_WaveCount/_WaveMetersPerUnit/_WaveNormalStrength`, fog params, foam params, `_Tiles`.
- Truly shared data stays `Shader.SetGlobal`: `_LightDir`, `_SunColor`, `_Sky`, `_Eye`,
  and a single shared `_WaveTime` clock.
- The caustic material becomes a per-body material instance (it samples this body's `_WaterTex`),
  writing into this body's caustic RT; that RT is handed to the body's renderers via the MPB.

No shader HLSL changes are required — the shaders already read these as named uniforms; they simply
receive them per-renderer (MPB) instead of globally. That's the nice part: Phase 1 is almost entirely
C# plumbing.

## The "objects still work" bridge

`WaterReceiver` (objects), and the buoyancy/splash components that do
`FindFirstObjectByType<WaterController>()`, currently rely on the globals. To avoid breaking floating
this phase, one body is marked **Primary** and ALSO publishes its per-body data as globals (the legacy
path). So: multiple surfaces render independently via MPB, while objects + analytic receivers follow
the primary body. Phase 2 replaces this with per-object body resolution.

## Frame from the transform (UX decision)

Natural prefab UX: derive `_VolumeCenter` from `WaterVolume.transform.position` and the rotation from
`transform.rotation`, keeping only `extent` (Vector3 half-size) as a field. You place/orient the water
by moving the GameObject and set its size with one vector. (Objects float in world space and are NOT
children, so using the transform here is safe — unlike the demo root.) Alternative: keep explicit
`volumeCenter/volumeEuler` fields. Recommend transform-driven.

## Work items

1. Rename `WaterController` -> `WaterVolume`; add serialized refs to its 4 renderers + caustic mesh.
2. Replace the per-body `Shader.SetGlobal*` calls with an MPB built once per frame and applied to
   those renderers. Keep shared globals as-is.
3. Make the caustic material + RT per-instance; feed the body's `_WaterTex` to it directly.
4. Add a `Primary` toggle that additionally mirrors this body's data to globals (objects bridge).
5. Update `WaterSceneBuilder` to assemble a `WaterVolume` (one body) and wire the renderer refs, so
   the existing menu still produces a working scene.
6. Make buoyancy/splash resolve the primary `WaterVolume` (unchanged behaviour for one body).

## Verification

- Default scene renders identically to now (one primary body).
- Drop a SECOND `WaterVolume` (different center/extent/wind) into the scene: both surfaces, pools and
  god-ray volumes render independently with no cross-talk (body A's waves must not appear on body B —
  the classic leftover-global bug to watch for).
- Floating crate still works against the primary body.
- WebGPU build still runs.

## Decisions to confirm before coding

1. Transform-driven frame (recommended) vs keep explicit center/euler fields?
2. Primary-body bridge for objects in Phase 1 (recommended) — OK to defer true per-object association
   to Phase 2?
