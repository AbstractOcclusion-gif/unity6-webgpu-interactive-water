# Luminex × WebGpuWater — Integration Plan (Phase 0 WebGPU port + Phase 1 MVP)

**2026-08-29 — PLAN ONLY, no code written.** Companion to
`WebGpuWater_Luminex_Compat_Analysis_2026-08-29.md`. All file/line claims below were
re-verified against both packages today (read-only, via grep/cat).

Decisions already taken (Bert, 2026-08-29):
- Browser (WebGPU) build IS the target → the port pass is **Phase 0**, first.
- Scope after that: **Increment 1 MVP** (Luminex as shaft-only layer).
- Increment 2 seam, when it comes: **global texture publish** (no asmdef reference).

---

## 0. Corrections to the analysis doc (from today's code verification)

1. **Pragma sweep is 28 files, not "all 32".** 46 runtime shader/compute files exist
   (Samples~ excluded); **28** carry `#pragma only_renderers` (3 variants: `d3d11 vulkan
   metal glcore`, `… gles3`, `d3d11 vulkan metal xboxseries playstation`); **18 carry no
   pragma at all** and are already unrestricted (all shadow passes, dust render,
   transparent-depth, etc.).
2. **The RenderGraph passes are already `AddUnsafePass`** (VolumetricFogCompositePass_RenderGraph.cs:87,
   VolumetricFogComputePass_RenderGraph.cs:67). `cmd.Blit` inside an unsafe pass is valid
   RG API — no Blitter rewrite is *required*; the remaining concern is only the bandwidth
   of the two fullscreen copies.
3. **Luminex is NOT currently imported in ThreeJSWaterPort.** `Packages/manifest.json`
   has no luminex entry; the `AbstractOcclusion.Luminex.*.csproj` files at the project
   root are stale leftovers. Phase 0 starts by re-adding it.
4. **The water's `BeforeRenderingPostProcessing+2` slot is taken.**
   Verified water ordering: fog at `+0` (WaterUnderwaterFogPass.cs:27), god rays /
   atmosphere at `+1` (LargeBodyAtmospherePass.cs:39), **`WaterParticlesAfterFogPass` at
   `+2`** (WaterUnderwaterFogFeature.cs:151 — sprites, pond-foam overlay and user
   transparents deliberately redrawn AFTER the fog so they are not double-fogged). The
   analysis doc's "+2 composite" recommendation collides with it → §3.3 below.
5. **Mobile path detail** (relevant to WebGPU): capability-gated behind `LUMINEX_MOBILE`
   (MobileCapabilityDetector.ShouldUseMobilePath), NOT platform-name-gated. Tier 2
   MicroFroxel = one combined kernel `numthreads(4,4,4)`=64 writing ARGBHalf only;
   Tier 1 Fragment = no compute at all. FogOnly composite already branches on
   `_UseMobileFogPath` (VolumetricFogComposite.shader).

---

## 1. Design: above-water vs underwater split (Bert's question)

Split by **medium ownership**, not by system:

| Region | Fog medium (extinction) | Volumetric light (shafts/beams) |
|---|---|---|
| Above water | **Luminex** — unchanged, full system (atmospheric volumes, height fog) | **Luminex** — unchanged |
| Underwater | **Water fog** — sole owner (per-channel Beer-Lambert; Luminex T is scalar and cannot do "red dies first") | **Luminex, additive only** — low-density volume, `T_L ≈ 1`, shafts recovered via light intensity |

Spatially this is separate `VolumetricFogVolume`s:
- Above-water volumes: whatever the scene already uses; untouched.
- Underwater: ONE volume per water body. Big pond → Box volume matched to the pool box.
  Ocean → height-fog ceiling at rest waterline (`heightFogMaxHeight = surface Y`) with a
  soft falloff band (flat-waterline approximation; crest pop-through is the known cost,
  fixed for real in Increment 2).

**One composite event serves both regions.** `compositePassEvent` is a single serialized
field (VolumetricFogRendererFeature.cs:67, default `AfterRenderingTransparents`). Moving
it into the before-post-processing range affects above-water frames too — and that is
harmless: above water nothing water-related runs, and the new slot is still after
transparents / before post. No submerged-dependent event switching, no dual feature setup.

---

## 2. Phase 0 — Luminex WebGPU compatibility port

Everything below is editor-provable except the final browser validation. Work happens in
the Luminex package (symlinked — edits hit all consumer projects at once; the 3-harness
caution applies).

### P0.1 Re-import Luminex into ThreeJSWaterPort (setup, no package code)
- Add `com.abstractocclusion.luminex` to WaterPort `Packages/manifest.json` as a local
  `file:` reference (or directory-junction symlink, same as the HDRP/BIRP harnesses).
- Confirm `UNITY_PIPELINE_URP` define state in WaterPort (memory: manual, WebGL-only
  there) — Standalone editor testing needs it too, or the RG files silently compile out
  (known blind spot: the 2022.3 Luminex dev project NEVER compiles the RG passes; the
  Unity 6 WaterPort import is the ONLY compile check for them).
- Gate: project compiles, Luminex renders in a WaterPort test scene on D3D/Vulkan editor.

### P0.2 Adapter-limits reconnaissance (before touching any kernel)
- One throwaway browser build (or WebGPU dev runtime) logging:
  `SystemInfo.maxComputeWorkGroupSize`, compute support, and the WebGPU adapter limits
  (esp. `maxComputeInvocationsPerWorkgroup`).
- Decides whether P0.5 (workgroup resize) is needed at all: base spec = 256, the 9
  Luminex kernels at `(8,8,8)` need 512. If Unity negotiates ≥512 on target browsers,
  P0.5 downgrades to "document the requirement".

### P0.3 Pragma sweep — add `webgpu` (28 files)
- Append `webgpu` to every `#pragma only_renderers` line; 3 variants exist, keep each
  variant's tail intact. The 18 pragma-less files need nothing.
- Mechanical sed-style edit + per-file verify (LF, no NUL, `only_renderers` count
  unchanged). **After the edit: full 3D-noise check** (non-negotiable rule).
- Decision inherited from the variant that omits glcore (`xboxseries playstation` file):
  verify that file's role before touching (it may be console-only by design).

### P0.4 `_DensityGrid` storage format (the one guaranteed-blocker format)
- `RWTexture3D<float2>` ↔ `RenderTextureFormat.RGHalf` (VolumetricBufferManager.cs:110
  and :179). `rg16float` is not a guaranteed WebGPU storage format.
- **Option A (recommended): widen to ARGBHalf** — smallest code delta: 2 creation sites,
  declarations in InjectDensity/InjectLighting (`_DensityGrid` writers/readers), any
  `.xy`-swizzle assumptions. Cost: +2 bytes/voxel on one grid.
- Option B: pack into `r32uint` (OceanPackC doctrine) — cheaper memory, wider blast
  radius (every read/write site rewritten). Only if profiling says A hurts.
- Same audit for the single-channel grids: `_PrecomputedNoiseGrid` /
  `_HDRPLightNoiseGrid` (`RWTexture3D<float>`) — confirm their REAL creation format
  (dummies are RFloat=r32float, fine; if any real one is RHalf, widen it too). Note
  r32float linear filtering is the optional `float32-filterable` feature — verify or
  sample with point filtering.
- Full 3D-noise check after every edit.

### P0.5 Workgroup sizes (conditional on P0.2)
- If needed: `(8,8,8)` → `(8,8,4)` (=256) in InjectAbsorption (1), InjectDensity (2),
  Reproject (3), SparseUpdate (3) + matching C# dispatch divisors. Keep a single named
  constant for the group size so C# and HLSL cannot drift (mirror-constant rule — C#
  side in VolumetricConstants, HLSL side stays a #define in its own file, NOT in
  VolumetricSharedConstants.hlsl).

### P0.6 CubeArray policy (point/VSM shadows)
- `TextureCubeArray` in 6+ files: WebGPU **core** OK, **compatibility mode** NOT.
- Plan: target core; add a startup capability log line; document "no point-light
  volumetric shadows on compat-mode browsers". A fallback gate (skip point shadows,
  keep directional/spot) is a later nicety, not Phase 0.

### P0.7 Composite bandwidth note (no action Phase 0)
- The unsafe-pass `cmd.Blit` pair (two fullscreen copies) is valid but mid-frame target
  switches were measured expensive in the water project on WebGPU. Log it as a perf
  follow-up; only revisit if the browser profile shows it hot.

### P0.8 Optional bridge: `LUMINEX_MOBILE` in the browser build
- Defining it lets capability detection pick MicroFroxel (64-invocation combined kernel,
  ARGBHalf-only storage — sidesteps P0.4/P0.5 entirely) or Fragment (no compute).
- Worth ONE build early: proves Luminex-on-WebGPU end-to-end before the desktop-path
  port lands, and gives an A/B reference. Not the endgame — desktop path stays the goal.
- Caveats: MobileFogCombined.compute needs the P0.3 pragma too; it uses CubeArray (core
  only); mobile tier clamps quality (48×27×24 grid).

### P0.9 Validation protocol (per increment of P0.3–P0.6)
- Editor D3D11 + Vulkan: demo scene renders identically (before/after screenshots).
- **3D noise verified after EVERY shader/compute edit** (memory rule, non-negotiable).
- WaterPort Unity 6 compile = the RG-file compile check.
- Browser build (18 min): adapter-limit log clean, no stripped-shader pink, fog visible,
  console free of WebGPU validation errors. Batch edits to amortize build time.
- No git from any sandboxed session; Bert commits from PowerShell. LF + zero NUL bytes
  verified per touched file.

**Phase 0 exit criteria:** Luminex desktop path renders in a WebGPU browser build of a
WaterPort scene (or, worst case, documented limit blockers + the mobile-tier bridge
running instead).

---

## 3. Phase 1 — MVP: Luminex as a shaft-only layer over the water fog

Config-first; the only potential code is one ordering constant and one optional helper.

### 3.1 Scene setup (no code)
- Big-pond demo scene first (bounded Box volume = easiest confinement), ocean second.
- One `VolumetricFogVolume` per water body as per §1. Above-water volumes untouched.
- Lights: start with ONE directional `VolumetricLight` (sun shafts), then one spot lamp.

### 3.2 Tuning contract (no code)
- Underwater volume density low enough that `T_L ≥ ~0.9` at grid far range — Luminex
  stays effectively additive; residual scalar dimming is the accepted MVP approximation.
- Recover shaft brightness via per-light intensity, not medium density.
- `_FogIntensity` is the master trim (lerps T_L→1 while scaling S_L).

### 3.3 Ordering — the one real decision (small code, needs Bert's GO)
Water stack today: fog `+0`, god rays `+1`, particles-after-fog `+2`.
Luminex must composite after the water fog; options:
- **Option 1 (recommended): water-side one-liner.** Move `WaterParticlesAfterFogPass`
  to `InjectionPoint + 3` and put Luminex `compositePassEvent` at `+2`
  (inspector field — no Luminex code). Order becomes: fog → god rays → **Luminex
  shafts** → sprites/user transparents. Sprites stay unfogged AND undimmed by T_L, and
  spray reads as in front of the shafts — correct for near-camera spray.
- Option 2: Luminex also at `+2`, rely on enqueue order within the event. Rejected:
  fragile, order not contractually defined.
- Option 3: Luminex at `+3` after the particle pass. Rejected: sprites get scalar-dimmed
  by `T_L` and shafts draw over near spray.
- Note: while the water god-ray pass (`+1`) and a directional VolumetricLight are both
  active, the sun is double-shafted — for A/B only; §3.4 stands one down.

### 3.4 Stand-downs (config)
- Per lamp promoted to `VolumetricLight`: disable its `WaterSceneLightsInscatter`
  contribution (else counted twice).
- Once directional shafts come from Luminex: disable the screen-space god-ray march
  (A/B via the cost probe's G key; it measured ~2 fps and had 3 historical waterline
  bugs — retiring it is a win if Luminex covers the look).

### 3.5 Optional helper (defer until proven needed)
- A tiny water-side component copying a body's surface Y into its volume's ceiling
  (`heightFogMaxHeight`) so the ocean setup is one click. Pure convenience — do the
  MVP by hand first.

### 3.6 Accepted MVP rough edges (documented, not fixed here)
- Flat-waterline confinement: shafts can pop through big-swell crests (falloff band
  mitigates; Increment 2 fixes properly).
- No exclusion-carve awareness: a shaft glows inside a dry underwater room — mitigate
  by volume placement.
- Temporal reprojection may ghost briefly on fast submerge/emerge.
- Residual scalar dimming from `T_L < 1`.

### 3.7 MVP acceptance test
- Pond scene: submerged camera sees sun + one spot lamp as soft volumetric shafts,
  per-channel water hue intact (red still dies first), waterline/meniscus intact,
  sprites/foam neither fogged nor dimmed, no shaft visible above the surface.
- A/B captures: water-only vs water+Luminex, same camera path; fps delta recorded via
  the cost probe.
- Fast submerge/emerge ×10: no full-screen fog flicker (hysteresis intact), ghosting
  clears ≤ a few frames.

---

## 4. Increment 2 pointer (not in this plan's scope)
Chosen seam when we get there: Luminex publishes `_LightingGrid` + frustum/`_GridParams`
uniforms as GLOBALS (`SetGlobalTexture` convention, no asmdef link); the water's
`WaterFogSolve` samples it inside the wet-span integral behind a `WATER_FOG_LUMINEX`
multi_compile fence. Prerequisite: Phase 0 + MVP tuning experience.

---

## 5. Open decisions for Bert
1. GO on Phase 0 order as written (P0.1 setup → P0.2 recon build → P0.3 sweep → P0.4
   format → conditional P0.5)?
2. P0.4: Option A (widen to ARGBHalf) unless you veto for memory — confirm.
3. P0.8: spend one early build on the `LUMINEX_MOBILE` bridge, yes/no?
4. §3.3: approve the water-side one-liner (particles pass `+2`→`+3`) when Phase 1 starts.
5. The console-variant pragma file (`xboxseries playstation`): include in the webgpu
   sweep or leave console-only?

*No code was written or modified for this plan. Every edit listed above waits for GO.*
