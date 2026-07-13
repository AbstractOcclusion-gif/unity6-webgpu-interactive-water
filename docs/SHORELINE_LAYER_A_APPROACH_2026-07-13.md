# Layer A — Depth + SDF Substrate: concrete approach

Date: 2026-07-13. Grounded by Phase-0 recon of our package + KWS2/Crest. Companion to `docs/SHORELINE_PLAN_2026-07-13.md`. Status: **approach for approval — no code until Bert says go; then A1 first.**

## Decision that shaped this: new passes, not an extended bed baker
`WaterBedBaker.cs` is CPU `Terrain.SampleHeight`, single-body pool-frame, `Texture2D` (not a compute UAV). It cannot become a camera-following world-frame GPU field. So Layer A is **two new GPU components**, mirroring patterns the package already proves, and the existing bed baker stays untouched for its bounded-pool tint role.

## What we build
1. **`ShoreDepthCapture`** — a top-down orthographic capture of the seabed into a persistent world-frame RT, camera-following and texel-snapped.
2. **`ShoreSdfField`** — a jump-flood compute turning that depth into a signed distance + direction-to-shore field.

Both publish global textures (persistent RTs → plain `Shader.SetGlobalTexture`, no RenderGraph handle juggling) plus a per-body active flag, exactly like `_OceanFft*`.

## Formats (WebGPU-safe, from the recon)
- Seabed height: **`R16_SFloat`**, linear world height reconstructed from an ortho depth render (Crest `Copy` pattern). Not a depth attachment (can't be RW-sampled cheaply).
- JFA ping-pong payload: nearest-seed **UV in [0,1]** (KWS approach — R16-safe, unlike Crest's world-pos payload). Storage format `rg32float` or `rgba16float` (WebGPU storage needs r32/rgba16; **not** `rg16float`).
- Final SDF: **`rgba16float`** = (shoreDir.xy*0.5+0.5, signedDistance, mask). Half-float so it's hardware-filterable on WebGPU. If ever sampled as float32, add a `_ShoreSDFTexel` uniform + manual-bilinear helper cloned from `WaterCommon.hlsl:29-40`.
- Always bind a fallback texture + gate in-shader on a float flag (never leave a sampler unbound on WebGPU) — `WaterSimulation.cs:278-282` pattern.

## Frame & camera-follow
Reuse the `WaterSimWindow` model: texel-snapped world centre + integer scroll so texels stay pinned and don't swim (`WaterSimWindow.cs:75,95-96`; snap idiom `Crest Lod.cs:392`). Publish our own `_ShoreDepthCenter`/`_ShoreDepthSize` (analogous to `OceanFieldCenter/Size`). Follow the main camera XZ like the ocean clipmap (`WaterVolume.cs:2043-2045`).

## Naming (confirmed free — `_Shoreline*` is taken by the bed tint)
`_ShoreDepthTex`, `_ShoreDepthCenter`, `_ShoreDepthSize`, `_ShoreDepthTexel`, `_ShoreSDFTex`, `_ShoreSDFActive`. Grep confirms none exist today.

## Integration points (mirror existing wiring)
- Assets: add `[SerializeField] internal ComputeShader shoreSdfCompute;` + capture shader/camera fields next to `simCompute`/`oceanFftCompute` (`WaterVolume.cs:53-60`).
- Lifecycle: `ShoreDepthModule` + `ShoreSdfModule : IWaterModule` mirroring `OceanFftModule` (`WaterCollaboratorModules.cs:92-112`), instantiated in `BuildAndInitializeModules` (`WaterVolume.cs:1674-1690`), `Enabled` gated on the assets being wired.
- Dispatch: per-frame from `WaterVolume` where `_oceanFft?.Dispatch(...)` / caustics run (`WaterVolume.cs:2041-2055`), passing the sim-window frame.
- Publish: IDs in `WaterUniformPublisher.cs:44-49`; global textures via `Shader.SetGlobalTexture` in the dispatch driver (like `WaterOceanFft.cs:475`); per-body `_ShoreSDFActive` via `WriteBodyUniforms` (mirror `_OceanFftActive` `:223`).
- Consume: new `Runtime/Shaders/WaterShore.hlsl` (declares uniforms + sampler + a `SampleShore(worldXZ)` returning depth/dist/dir); a debug-viz branch in `WaterSurface.shader` near the existing shoreline tint (`:1091`).

## JFA algorithm (faithful port, KWS math + Crest snap)
1. Seed: `terrainDepth = seabedHeight - waterLevel`; seed where `abs(terrainDepth) < threshold`; store the texel's own UV. `(1,1)` = no seed.
2. `N = ceil(log2(res))` passes; step starts at res/2, halved each pass; 3×3 neighborhood adopts the neighbor whose stored UV is nearest (distance in texels). Optional few fixed small-step cleanup rounds.
3. Finalize: `unsignedDist = distance(nearestUV, uv)`; sign = seabed-below-water? +1 : -1; shoreDir = SDF gradient (4 taps), negated toward shore; pack `rgba16float`.
4. (Optional later) edge-aware direction blur (KWS pass 3).

## Seabed source (one small config choice, not a blocker)
The ortho capture renders a **culling mask** (like KWS `IntersectionLayerMask` / Crest `_Layers`). Default: terrain + default layers. Configurable per body/globally. Confirm the default mask when we wire it.

## Sub-phases (each compiles, is revertible, has a gate)
- **A1 — Ortho seabed-depth capture.** `ShoreDepthCapture` + copy-to-`R16_SFloat` + camera-follow/texel-snap frame + publish `_ShoreDepthTex`/`_ShoreDepthCenter`/`_ShoreDepthSize` + a debug-viz that colours the surface by seabed depth. **Gate:** depth field tracks irregular terrain, follows the camera with no swim, lights up on a bounded *and* an ocean body (where the old bed was absent). *Start here.*
- **A2 — Jump-flood SDF.** `ShoreSdfField` compute (seed → N flood passes → pack) + publish `_ShoreSDFTex`/`_ShoreSDFActive` + debug-viz of distance and direction. **Gate:** distance ramps correctly from an irregular waterline; direction points to shore; stable while the camera moves.
- **A3 — Verify.** Both body types; WebGPU format sanity (half-float filterable, storage r32/rgba16, no unbound samplers, no readback); a perf glance on the extra capture + JFA passes; confirm no collision with `_Shoreline*` bed tint.

## WebGPU / buoyancy notes
Entire layer is GPU compute + textures, **readback-free** → portable. Nothing here reaches CPU buoyancy (render substrate only). Half-float for filtered fields; r32/rgba16 for storage; fallback-bind + gate every sampler.
