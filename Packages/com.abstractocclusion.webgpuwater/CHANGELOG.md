# Changelog

All notable changes to this package are documented here.

## [Unreleased] - pre-release audit pass

### Added
- **Water Wizard Renderer Setup tool** installs or repairs all six WebGpuWater renderer
  features on the active URP asset's default Renderer Data. It assigns all seven required
  shaders, preserves existing custom assignments, avoids duplicates, maintains URP's local-ID
  feature map, and supports Undo. Camera renderer overrides remain explicit.

### Fixed
- **Large-body caustics no longer time out the D3D11 shader compiler on a cold package import**:
  the five-point projection now samples the generated ocean FFT through a dedicated compile-bounded
  path instead of expanding the complete shore/surf surface graph at every point.
- **Package manifest now declares the versions the code actually needs**: Unity `6000.3.9f1` and a
  URP `17.0.0` dependency. The runtime uses `Rigidbody.linearVelocity` and the URP 17 RenderGraph
  pass API with no version guards, so the previous `2022.2` / "URP 12+" claim meant a wall of
  compile errors for anyone who believed it.
- **Terrain bed texture is point-sampled again.** `_BedTex` is an `RFloat` created with a Bilinear
  filter, and Unity's inline sampler inherits that mode - WebGPU rejects a filtering sampler on an
  unfilterable `r32float` view and the whole bind group fails. The FFT cascade arrays get the same
  treatment on their float32 fallback format.
- **`WaterSurface.shader` is back inside the 16-sampler limit.** Both FFT cascades now share one
  sampler (they are always created from the same format pair, so their filterability agrees), taking
  the ForwardLit pass from 17 to 16 units. 17 is a hard d3d11 compile error and a WebGPU bind
  failure - and d3d12 has no such cap, so it never showed up in a default Windows editor session.
  The budget is now documented in `WaterSurfaceScreen.hlsl`; read it before adding a texture.

- **One Beer-Lambert transmittance, one clarity-density ramp.** `exp(-_WaterExtinction * density * dist)`
  was hand-written at nine sites across six shaders and the copies had already drifted on whether they
  honour `_WaterFogEnabled`. `WaterFog.hlsl` now owns `WaterTransmittance` / `WaterTransmittanceClarity`
  (gated) and `WaterTransmittanceAtDensity` / `WaterClarityDensity` (raw); every site routes through
  them. Output is unchanged everywhere - the previously ungated sites are ones where the flag is
  guaranteed on (the fog pass is CPU-gated; the chunk shell forces the flag; the particle path gates
  on `_UnderwaterFogArmed`), and the exclusion veil's own opacity deliberately keeps the raw form.
- **Shore-field UV divide is guarded everywhere.** `WaterSim.compute`'s copy of the shore fetch had
  lost the half-extent floor its sibling in `WaterParticleCommon.hlsl` carries, so a degenerate field
  publish produced inf/NaN UVs that passed the influence gate and reached the foam accumulator. The
  floor is now one shared `ShoreFieldHalfSafe` in `WaterShoreMath.hlsl`, applied by all three bindings
  of that field (sim, particles, surface).
- **New `WaterShaderProps` registry** - the property-name twin of `WaterShaderNames`. Nine property
  names were written out in two files each (`_VolumeRot` and friends across the publisher, the caustic
  pass and the chunk shell), so renaming one in HLSL broke one consumer silently.
- **The inspector now uses `WaterVolumePropertyPaths`** instead of retyping paths it already had
  consts for - 27 raw literals routed, 6 cross-file paths added to the registry. The registry header
  now states its scope: paths read from more than one place live there, single-use paths stay inline.

- **Foam particles no longer dispatch against a stopped volume.** `_densityPending` was cleared after
  five early-returns, so disabling the WaterVolume (or letting ambient foam lapse past the burst
  window) left the flag armed: the `OnBeginCameraRendering` density splat kept clearing and
  rasterising a full-pool dispatch every camera render, reading an already-released sim texture.
- **The open-water swell is evaluated once per query, not twice.** Height and vertical velocity each
  ran their own 4-iteration Gerstner chop inversion on the same point in the same call - 10
  `EvaluateBands` passes where 5 suffice. Buoyancy asks for both at every probe of every floater, so
  this scaled with the scene. `ShoreWaveCtx` (22 fields, two trig calls, rebuilt on every property
  read) is now hoisted per sample too.
- **Chunk shell is torn down with its body.** The shell renderer field was never nulled, so under Fast
  Enter Play Mode it survived while the shared material/mesh it points at were destroyed - rendering
  with a dead material and never rebuilding - and its `HideAndDontSave` GameObject leaked in edit mode.
- **In-flight readbacks are drained before their source is destroyed** (`WaitAllRequests` ahead of
  module disposal), removing the console error on ocean disable / scene change.
- **Ocean god rays clamp their step count in the shader.** That pass has no `Properties` block, so
  `_LargeGodRaySteps` had no `Range()` bound and only the publisher guarded it - an unbounded dynamic
  loop is a device-lost on WebGPU and mobile, not just a slow frame.
- **NaN guard on both wall shaders' derivative normals.** `cross(ddy, ddx)` degenerates on an edge-on
  triangle and across a mesh silhouette (where lanes without a front face hold a substitute position);
  the NaN flowed into `refract` and the fresnel term. Now one shared, guarded `SafeFacetNormal`.
- **Water render passes no longer record for material/prefab preview cameras** - wasted work, and
  previews draw the procedural shaders with none of the per-body buffers bound. Scene view and
  reflection probes are deliberately unaffected.
- **`ResetStaticState` covers what it claims**: the underwater-fog and waterline gates the URP features
  poll in every scene, plus the buoyancy query's owner-keyed buffer cache.
- **The receiver converter sanitises material names before using them as asset paths.** A material
  called `Wood/Oak` targeted a non-existent subfolder, so `CreateAsset` failed silently mid-loop and
  left the renderer on a null material. It also now takes its shader name from `WaterShaderNames`.
- **Render features release before they re-create.** URP calls `Create()` on enable, validate and
  domain reload but `Dispose()` only on destroy, so every inspector tweak leaked an engine material -
  and, for the atmosphere pass, its history RTHandles. `Create` and `Dispose` now share one teardown.
- `WaterWaterline.hlsl` no longer claims to be texture-free: it is ~6 fetches per call, and the
  fullscreen fog's crossing march makes that ~290 per pixel. Documented where it will be read.

### Changed
- **Demo sample containment pass.** Shared post-processing now ships with the sample, scene-specific
  generated assets live beside their demo materials, Catapult and City Night join the numbered scene
  catalog, and demo water textures resolve to the canonical `Runtime/Defaults/Textures` assets.
- **Chunk/exclusion shader registration is opt-in.** The package no longer silently appends four
  shaders to the project's Always Included Shaders on import (every entry there compiles into every
  build). Use **Window > AbstractOcclusion > WebGpuWater > Register Chunk Shaders For Builds**, or
  the prompt the Water Wizard's Utilities section shows while a shader is missing.
- **Pre-rebrand "WebGL Water" naming removed from the user-facing surface**: dialog titles, log
  prefixes, the wizard's scene root object, and the generated-asset folder, which moves from
  `Assets/WebGLWater` to `Assets/WebGpuWater`. Product name and log prefix are now single constants.
  Attribution to Evan Wallace's original WebGL Water is unchanged.
- The wave-constant drift validator is author-only: it runs solely when the package is embedded,
  scopes its asset search to the package, and warns instead of erroring.
- The per-body quality-tier log line and the obstacle-footprint PNG dumper no longer reach customers
  (development builds / `WEBGPUWATER_DEV` respectively).
- README scope section reconciled with what actually ships - the open-water/ocean path is documented
  rather than described as out of scope.

## [1.0.0] - 2026-07-03

First Asset Store release.

### Added
- Interactive ripple simulation (compute-based heightfield) with frame-rate-independent
  stepping: identical wave speed at 30 fps on a tablet and 144 fps in the editor.
- Two-way object coupling: multi-point buoyancy with righting torque and wave drift
  (`WaterBuoyancy`), analytic drop / footprint-delta disturbance (`WaterInteractable`),
  entry splashes with drifting droplets and flipbook crown (`WaterSplash` + emitter).
- Ambient wind-wave layer (sum of sines) that floating objects ride, with wind speed,
  heading, and spread controls.
- Turbulence-driven surface foam (generation/decay/advection) plus fully GPU-resident
  foam/spray particles (compute spawn + procedural quads, no readback, WebGPU-safe).
- Caustics, hybrid god rays with real shadow shafts, and per-body reflections:
  SSR, planar, or sky, over a procedural sky or the scene's URP probe.
- Water fog (Beer-Lambert, HDR extinction), opacity dial, per-channel depth darkening,
  and terrain bed depth with a shoreline gradient.
- Multi-instance water bodies (per-body MaterialPropertyBlocks) with visibility/distance
  culling and a simulation budget; camera-following sim window for large bodies.
- Quality tiers (`WaterQuality` asset): High/Medium/Low with auto hardware probe —
  sim/caustic resolution, god-ray steps, wave count, render scale, refraction,
  mesh detail, update intervals, and foam-particle caps per tier.
- One-window authoring: **Window > AbstractOcclusion > WebGpuWater > Water Wizard**, plus
  8 ready-made demo scenes in `Samples~/Demos`.
- Scripting API: `WaterVolume` gameplay facade (`TryGetWaterHeight`, `TryGetSurface`,
  `TrySampleSubmersion`, `AddRipple`, `TryRaycastSurface`, `IsSubmerged`) and public
  properties for runtime look/behavior (fog, foam, wind waves, ripple strength/radius,
  reflections, quality, culling).

### Changed
- Public API surface minimized for release: inspector tuning and builder wiring are no
  longer public fields; runtime-scriptable settings are exposed as properties. All
  serialized names unchanged — existing scenes and prefabs upgrade untouched.
- All shaders now declare `"RenderPipeline" = "UniversalPipeline"` (SRP-compatibility,
  Unity 6.6 BIRP deprecation).
- Fast Enter Play Mode (Unity 6.6 default) fully supported: all scene-lifetime static
  state resets via `SubsystemRegistration` before each play session.

### Verified
- Mobile/WebGPU: 30 fps on Honor X6 and Redmi Pad SE, 30+ fps on Samsung Galaxy A17
  with foam, caustics, and god rays enabled (Low tier). Unsupported browsers/GPUs get
  a clear error message instead of a crash.

## [0.1.0] - 2026-07-02
### Added
- Initial extraction of the WebGpuWater system into a standalone UPM package
  (`com.abstractocclusion.webgpuwater`), split out of the host project's `Assets/WebGLWater`.
- Runtime and Editor assembly definitions (`AbstractOcclusion.WebGpuWater`,
  `AbstractOcclusion.WebGpuWater.Editor`).
- URP-specific planar reflection isolated behind the `WEBGPUWATER_URP` define so the base
  assembly compiles even when the Universal Render Pipeline is not installed.
- Namespaces rebranded `WebGLWater.*` -> `AbstractOcclusion.WebGpuWater.*`.
- Single authoring entry point: **AbstractOcclusion > WebGpuWater > Water Wizard**.

### Notes
- Compute shaders are loaded from the package via `PackageShadersRoot`; generated meshes,
  materials, textures and the sample prefab are still written into the consuming project's
  `Assets/` (the package stays read-only).
- URP 12+ is recommended for full visual fidelity (planar reflections, screen-space refraction).
