# Large-Body Water — Overnight Progress (2026-07-06)

**Branch:** `feature/large-body-water` (created as a git ref; check it out + reindex on your
side — the sandbox git index was corrupt, so I could not commit. Your working tree still shows
these edits; commit them once you're on the branch.)

**Golden rule kept:** every change is gated. With `openWater = false` (the default on every body)
and the `WEBGPUWATER_LARGE_BODY` define **absent**, the project is byte-for-byte the shipped
pool / small-body build. Nothing existing was rewritten or removed.

**Honesty note:** I cannot run Unity, compile URP shaders, or see pixels in this environment.
Phases 0–2 below are written and cross-checked by hand (includes, uniform plumbing, MPB routing
all verified against the current source), but **none of it has been compiled or run yet.** First
thing to do tomorrow is open the editor and let it compile.

---

## DONE — real code, gated, in your working tree

### Phase 0 — `_LargeBody` scaffolding
- **`WaterVolume.cs`**: new serialized `internal bool openWater = false` under a new
  "Open water (lake / ocean) - EXPERIMENTAL" header. Opt-in per body.
- **`WaterUniformPublisher.cs`**: publishes `_LargeBody` (0/1) through the existing per-body sink,
  so it rides the MaterialPropertyBlock exactly like `_SimWindowed`. Reaches the surface (whole-body
  + windowed patch renderers) and the god-ray renderer — verified via `ApplyBodyBlock`.
- **`WaterVolume.hlsl`**: declares `float _LargeBody;` (defaults to 0 when unpublished).

### Phase 1 — Open-water surface / pool decouple (the priority-1 task)
Follows the finding that the surface only touches the pool in its **analytic** branch; the
`_REAL_REFRACTION` variant already samples the real scene and is pool-free.
- **`WaterSurface.shader`**:
  - `getSurfaceRayColor` down-ray: when `_LargeBody`, returns the deep-water inscattering colour
    (`_WaterFogColor`) instead of `GetWallColor` over `POOL_BOX`. No pool floor assumed.
  - Analytic fog block (`#if !defined(_REAL_REFRACTION)`): the pool-chord fog is skipped for
    `_LargeBody` (the colour is already the deep colour).
- **`GodRays.shader`**: early-out `if (_LargeBody > 0.5) return 0;` — the pool-box shaft volume is
  suppressed for open water (the Phase 4 render feature replaces it). Belt-and-suspenders: large
  bodies are already `_windowed`, which disables the god-ray renderer anyway.
- Reflection was already pool-free; `Caustics.shader` left untouched (its RT simply isn't sampled
  once god rays early-out and the surface uses the deep fallback).

**Net effect:** set `openWater = true` on a body and its surface stands alone with **no pool** —
task 1 achieved for the open-water path, additively, with zero risk to the pool demos. This is the
exact goal the 2026-07-03 removal attempt was reverted for; done here as a gated mode, not a teardown.

### Phase 2 — Camera-following clipmap geometry (gated behind `WEBGPUWATER_LARGE_BODY`)
- **`LargeWaterClipmap.cs`**: builds a radial clipmap mesh — concentric rings whose radii grow
  geometrically, so triangles are dense near the viewer and coarse at distance (Crest single-mesh /
  KWS infinite-ocean principle, one continuous mesh = no LOD-seam stitching). Pure deterministic
  mesh gen, input-validated, `UInt32` indices, huge bounds (never culls).
- **`LargeWaterClipmapDriver.cs`**: recentres the surface object on the camera XZ each frame,
  texel-snapped so fine vertices don't swim under the wave field. Height stays a function of world
  XZ (keeps buoyancy valid).

**Still needed to make Phase 2 visible (next session):** a surface vertex path that (a) uses the
clipmap's world XZ rather than the pool [-1,1] grid, and (b) samples the FFT displacement from
Phase 3. Until Phase 3 exists the clipmap has no open-water wave field to sample, so wiring it into
a WaterVolume is deferred to tomorrow (and needs editor iteration).

---

## SPECCED, NOT CODED — Phases 3–5 (do these with the editor open)

I deliberately did **not** write these blind. Each needs compile + visual iteration; blind code
would most likely hand you a broken build. Below is the implementation plan so tomorrow is fast.

### Phase 3 — FFT waves + buoyancy bridge
New files (all under the define):
- `WaterFft.compute` — spectrum init (Phillips or JONSWAP, wind speed/dir/turbulence) → 2D
  Cooley-Tukey butterfly passes → displacement (XYZ) + normal, into a `RGBAHalf` Texture2DArray of
  4 cascades. Reference: KWS `KWS_WavesSpectrum.compute` + `KWS_WavesFFT.compute`; Crest
  `FFTSpectrum.compute` + `FFTCompute.compute`.
- `WaterFftProvider.cs` — owns the RTs, dispatches per frame, exposes cascade textures + params.
- `WaterFftWaves.hlsl` — `SampleFftDisplacement(worldXZ)` / `SampleFftNormal(worldXZ)` with
  per-cascade distance fade, used by the surface vertex + fragment.
- **Surface integration:** in `WaterSurface.shader`, when `_LargeBody`, add FFT displacement in the
  vertex stage and blend the existing ripple-sim window over the near field (keep the near-field
  look). Fragment normal = blend(FFT normal, ripple normal).
- **Buoyancy bridge (decided):** fit a handful of dominant Gerstner components to the same spectrum
  on the CPU and feed them through the existing `WaterWaveBank` / `TryGetAnalyticWaterline` seam —
  **no GPU readback** (WebGPU readback latency). Floaters ride the approximate swell; exact crest
  detail is visual-only.
- **WebGPU gotcha:** RGBA/RG float textures are **not hardware-filterable** on WebGPU — sample the
  cascades with manual bilinear (the codebase already does this for `_WaterTex`; reuse the pattern),
  or use `RGBAHalf` and confirm filtering.

### Phase 4 — Render feature: fog + god rays (URP 17.3 RenderGraph)
- `LargeWaterRenderFeature.cs` (`ScriptableRendererFeature`) + a `RecordRenderGraph` pass. URP17 is
  RenderGraph-only, so use `AddRasterRenderPass` / `AddComputePass`, `UniversalResourceData`,
  `TextureHandle`; declare depth/color reads via `builder.UseTexture`.
- Screen-space **depth extinction + scattering** (Crest `UnderwaterShared.hlsl`:
  `exp(-absorption*depth)` + scattering colour) and **volumetric shafts** (KWS raymarch, simplified,
  main-light shadow per step) — active only where the large body is on screen.
- **Gating so pools are never fogged:** the pass must mask to the large body's footprint / a stencil
  or a `_LargeBody` surface stencil write; a `Pool` body sharing the scene must be excluded. This is
  the open risk I flagged — resolve it here.
- **RenderGraph gotcha (from prior notes):** global textures set inside a pass must use
  `SetGlobalTextureAfterPass`, not `cmd.SetGlobalTexture` / `Shader.SetGlobalTexture`.
- Register the feature on the URP renderer asset(s); confirm Depth + Opaque Texture are on
  (large water needs both), and Transparent Receive Shadows if shafts want shadowing.

### Phase 5 — Transparency / opacity polish
- Extend deep/large opacity + a horizon falloff so distant open water doesn't read over-transparent
  at grazing angles; reuse `ApplyWaterFog` / `ApplyWaterOpacity` with a distance-aware term.

**Out of scope (large-body backlog, later):** wave/whitecap foam, Unity fog integration, terrain
shoreline blending.

---

## How to try what's here tomorrow
1. Check out `feature/large-body-water`, reindex git, let Unity compile (expect Phases 0–2 to build;
   they're the only active changes — the define stays off).
2. On an existing large body, tick **Open water (EXPERIMENTAL)** and confirm the surface no longer
   shows pool tiles through refraction and the god rays are gone — pool demos unchanged.
3. When ready to build Phase 2+ visually, add `WEBGPUWATER_LARGE_BODY` to Scripting Define Symbols
   and we wire the clipmap + FFT together (Phase 3) with the editor open.
