# Large-Body Water — Design Doc (Draft 01)

**Date:** 2026-07-05
**Branch:** `feature/large-body-water`
**Status:** DRAFT — awaiting Bert's approval before any code.

## 0. Purpose

Make the existing WebGPU water port render believable **large bodies (lakes and
oceans)** without touching the near-field look you already like (interactive
ripples, caustics, overall surface shading). We reuse the *techniques* proven in
Crest (`Packages/com.waveharmonic.crest`) and KWS Water 2
(`Assets/KriptoFX/WaterSystem2`) — both living in the sibling `KWSWater` project —
and write fresh code in our `AbstractOcclusion.WebGpuWater` namespace. We never
copy their source and never reinvent what they already solved.

### Decisions locked with Bert (2026-07-05)
- **Pool:** keep the analytic pool (it only exists now for the "3-wall" pool
  trick). Add a *gated open-water mode* that never assumes a pool. No teardown —
  the 2026-07-03 removal attempt was reverted; we do not repeat it.
- **Fog + god rays:** yes to a **URP `ScriptableRendererFeature` (RenderGraph)**,
  used by the **large-body path only**. Small/pool bodies keep the analytic
  per-material path unchanged. (This intentionally reverses the earlier
  "no fullscreen pass" call, for the large-body path only.)
- **Waves:** follow the KWS/Crest approach — **FFT** for open water, blended with
  the existing near-field ripple sim.
- **Process:** new branch, this design doc first, approval before code.

### Non-goals
- No change to the shipped small/mid-body look or the pool demos.
- No rewrite of working port code — new features attach as thin, gated modules.
- Not chasing full Crest/KWS feature parity; we adopt the minimum that makes
  lakes and oceans read correctly.

---

## 1. Where the port stands today (verified against current source)

**Surface geometry.** A single finite grid plane (`Runtime/WaterMeshBuilder.cs`,
XY grid normalized to [-1,1]). For bodies past ~20 m a *ripple-sim window*
scrolls with the camera (`WaterSimWindow.cs`), but the **geometry never becomes
infinite or LOD'd** — it stays one plane sized to the body. This is the core
gap for oceans.

**Pool coupling (precise).** Reflection is already pool-free. The pool is still
referenced in `WaterSurface.shader`, but *only in the analytic branch*:
- `getSurfaceRayColor` down-ray → `GetWallColor(...)` over `POOL_BOX` (line ~399).
- Analytic fog chord → `IntersectCube(..., POOL_BOX_...)` (line ~545).
- Both are inside `#if !defined(_REAL_REFRACTION)`. In the **`_REAL_REFRACTION`
  variant the surface samples the real scene** (`_CameraOpaqueTexture` + scene
  depth, lines ~526/532) and never touches `POOL_BOX`.

Still hard-wired to `POOL_BOX`: **`Caustics.shader`** (lines ~41/88) and
**`GodRays.shader`** (box-marched, flat `surfaceLevel = _VolumeCenter.y`). These
are the genuine decoupling work — not the surface.

**Waves.** Analytic 16-component sum-of-sines (JONSWAP), evaluated identically on
CPU (`WaterWaveBank.cs`) and GPU (`WaterWaves.hlsl`) so buoyancy stays in sync.
Plus the interactive ripple sim. No FFT. Analytic waves stop reading as real open
water past ~20 m (documented limitation).

**Fog / transparency.** Analytic Beer-Lambert, per-material, distance-based
(`WaterFog.hlsl`). Scales predictably; no pool assumption in the fog math itself.

**God rays.** Mesh box, ray-marched, projected caustics, flat surface plane —
pool-frame bound.

**Pipeline.** No render feature today; standard URP forward passes.

---

## 2. Reference techniques, distilled (reuse targets)

**Crest** — clipmap ring geometry (`Surface/WaterChunkRenderer.cs`,
`Shaders/Surface/Geometry.hlsl` — taxicab-distance LOD morph, snap-to-camera);
FFT spectral cascades (`Waves/FFTCompute.cs`, `Shaders/Waves/FFT/*.compute`) with
displacement + derivative-normals sampled per-LOD; a **fullscreen underwater
pass** (`Volume/UnderwaterRenderer.cs`, `Shaders/Volume/UnderwaterShared.hlsl` —
`exp(-absorption*depth)` extinction + scattering + meniscus); screen-space
refraction with IOR (`Shaders/Surface/Refraction.hlsl`).

**KWS Water 2** — infinite quadtree, wind-relative LOD
(`FeaturesHD/Runtime/Ocean/MeshQuadTree.cs`); Phillips spectrum + 2D FFT, 4
cascades (`OceanFftWavesPass.cs`, `KWS_WavesSpectrum.compute`, `KWS_WavesFFT.compute`);
screen-space **volumetric lighting** with temporal reprojection + Mie scattering
(`VolumetricLightingPass.cs`); stencil-masked screen-space refraction/dispersion
(`KWS_WaterFragPass.cginc`).

**Choice for geometry: Crest-style clipmap, not KWS quadtree.** Rationale — the
port already renders *one* camera-relative surface mesh; a concentric clipmap ring
is a smaller, lower-risk evolution of that (swap the mesh, add LOD morph in the
vertex stage) than standing up a per-camera quadtree with seam-skirt bookkeeping.
Clipmap also composes cleanly with the existing scrolling sim window (both are
camera-centered lattices). We keep KWS as the reference for the FFT spectrum and
the volumetric fog pass.

---

## 3. Target architecture (new, gated modules)

All new behavior is selected by a per-body **`WaterBodyKind { Pool, Small, Large }`**
(or an `isLargeBody` flag already implied by the windowing threshold). `Large`
turns on the modules below; `Pool`/`Small` are byte-for-byte unchanged.

1. **Open-water surface mode (pool decouple).**
   - Large bodies always compile/select the `_REAL_REFRACTION` surface variant, so
     the surface is already pool-free.
   - Where a refracted ray finds *no* scene geometry (open ocean, no floor), return
     a **seabed/deep-water color** from the existing depth/absorption model instead
     of `GetWallColor`. No `POOL_BOX` in the large path.
   - Move `Caustics.shader` and `GodRays.shader` off `POOL_BOX` onto an
     **infinite/volume-agnostic frame** (camera-centered, surface-relative) for the
     large path; keep the pool-box path for `Pool` bodies.

2. **Camera-following clipmap geometry.**
   - New `LargeWaterMesh` builder (concentric LOD rings) + vertex-stage LOD morph
     (Crest `Geometry.hlsl` technique, rewritten). Recentered/snapped to camera
     each frame. Existing single-plane path stays for `Pool`/`Small`.

3. **FFT spectral waves (far field) + ripple sim (near field).**
   - New `WaterFftProvider` (compute): ocean spectrum → cascades → displacement +
     normal textures (KWS/Crest technique). Surface samples FFT for far field,
     blends into the existing ripple-sim window near the camera so the interactive
     look is untouched.
   - **Buoyancy / CPU heights:** keep an **analytic band-limited approximation** of
     the spectrum on the CPU (a handful of dominant Gerstner components fitted to
     the FFT spectrum), reusing the existing `TryGetAnalyticWaterline` seam. No GPU
     readback (WebGPU readback latency risk). This preserves CPU=GPU-*enough* for
     floaters without a full FFT readback.

4. **Real fog + god rays (URP RenderGraph render feature, large-body only).**
   - New `LargeWaterRenderFeature`: a screen-space pass doing depth-based
     extinction/scattering (Crest `UnderwaterShared.hlsl` model) and volumetric
     light shafts (KWS volumetric technique, simplified). Replaces the mesh god
     rays and the fake fog *for large bodies*. Analytic path stays for the rest.

5. **Transparency / opacity at scale.**
   - Screen-space refraction + depth-based per-channel absorption for deep/large
     water (already partly present via `_REAL_REFRACTION` + `ApplyWaterFog`);
     extend the deep-water/horizon falloff so opacity holds at distance.

---

## 4. Phased plan (each phase independently verifiable)

Ordered to de-risk: prove the surface stands alone before building the big stuff.

- **Phase 0 — Scaffolding.** `WaterBodyKind` flag + wiring so `Large` selects new
  paths; no visual change yet. *Verify:* pools/small bodies pixel-identical.
- **Phase 1 — Pool decouple (open-water surface).** Force `_REAL_REFRACTION` +
  seabed fallback for `Large`; decouple `Caustics`/`GodRays` from `POOL_BOX` on the
  large path. *Verify:* a large body renders correctly with **no pool present**;
  pool demos unchanged. (This is the task that failed before — doing it as an
  additive gated mode, not a removal.)
- **Phase 2 — Clipmap geometry.** Camera-following LOD rings for `Large`. *Verify:*
  horizon-to-camera surface, no popping/cracks, near-field ripple still reads.
- **Phase 3 — FFT waves + buoyancy bridge.** FFT far field blended with ripple
  near field; analytic-spectrum buoyancy. *Verify:* believable open water; floaters
  still sit on the surface.
- **Phase 4 — Render feature: fog + god rays.** Screen-space extinction +
  volumetric shafts for `Large`. *Verify:* real depth fog and shafts at scale;
  small-body analytic look unchanged.
- **Phase 5 — Transparency polish.** Deep/large opacity + horizon falloff.
  *Verify:* holds up on deep and distant water; no over-transparency at grazing.

Each phase ends with an editor check (and a WebGPU build check where the pipeline
is touched — Phases 2/4 especially), plus a git commit on `feature/large-body-water`.

---

## 5. Risks & open questions

- **WebGPU compute + FFT:** the port targets WebGPU; FFT ping-pong on float
  textures must respect the known "WebGPU won't filter float32" gotcha (manual
  bilinear). Budget/validate early in Phase 3.
- **Render feature vs. RenderGraph (URP17):** Phase 4 must be RenderGraph-native
  and respect the earlier global-texture gotcha (`SetGlobalTextureAfterPass`).
- **Buoyancy fidelity:** analytic-spectrum approximation won't match FFT exactly.
  Acceptable? Or do you want a coarse GPU readback option later?
- **Two water looks in one scene:** a `Large` body and a `Pool` body coexisting —
  the render feature must not fog the pool. Footprint/kind gating required.
- **Scope creep:** foam whitecaps, Unity fog integration, and terrain shoreline
  blending (from the large-body backlog) are **out of scope for this doc** — flag
  as follow-ups after Phase 5.

---

## 6. What I need from you

1. Approve the phased plan (or reorder).
2. Confirm the **clipmap-over-quadtree** geometry choice.
3. Confirm the **analytic-spectrum buoyancy** approach (no GPU readback for now).
4. Say "go" on **Phase 0 + Phase 1 only** first — I stop for review before Phase 2.

No code will be written until you approve.
