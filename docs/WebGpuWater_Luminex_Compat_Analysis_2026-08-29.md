# Luminex × WebGpuWater — Underwater Volumetric Lighting Compatibility Analysis

**2026-08-29 — ANALYSIS ONLY, no code touched in either package.**
Goal: real volumetric lighting for the ocean and big-pond underwater, using Luminex
(`luminexfromstrore/Packages/com.abstractocclusion.luminex`, v1.0.4) with WebGpuWater.

---

## 1. Executive verdict

The two systems are architecturally compatible — Luminex is a froxel-grid volumetric
(inject → raymarch → temporal → fullscreen composite `scene·T + S`) and the water fog is a
per-pixel analytic solve; they can be layered. But three hard facts shape everything:

1. **Luminex cannot run in your shipped web builds today.** Every one of its 32 shaders and
   computes carries `#pragma only_renderers d3d11 vulkan metal glcore` (a few add gles3 /
   playstation) — **`webgpu` is never listed**, so a WebGPU-only WebGL build strips every
   Luminex program. Editor (D3D11/D3D12) works; the browser does not, until a port pass.
2. **Luminex transmittance is SCALAR.** The composite reads `T = fog.a` (one float) and does
   `scene.rgb * T + S` (VolumetricFogComposite.shader, "FogOnly" pass). The water's signature
   look — per-channel Beer-Lambert where red dies first — cannot be reproduced by Luminex's
   integrator. **Luminex can never own the underwater MEDIUM; the water fog must keep
   absorption.** Luminex's colored `_ExtinctionGrid` (ARGBHalf) exists but is fed only by
   per-light translucent-shadow volumes (AbsorptionVolumeProcessor), not by the medium.
3. **Default pass ordering reproduces a bug you already fixed once.** Luminex composites at
   `AfterRenderingTransparents` (VolumetricFogRendererFeature.cs:67, serialized field); the
   water fog runs later, at `BeforeRenderingPostProcessing` (WaterUnderwaterFogPass.cs:27),
   god rays at `+1`. So shafts composited by Luminex get multiplied down by the water's
   absorb pass — the exact "god rays invisible at fog density > 0" double-attenuation killed
   on 2026-07-26. The god-ray precedent (inject AFTER the fog) is the template.

Recommended path: **two increments.** MVP (mostly configuration): Luminex as a shaft-only
layer composited after the water fog. Endgame ("real volume lighting"): the water fog solve
samples Luminex's froxel `_LightingGrid` inside its own integral — one medium, one integral,
per-channel extinction, wavy waterline and exclusion carves for free. Details in §5–6.

---

## 2. The two architectures, side by side

| | Luminex | WebGpuWater underwater fog |
|---|---|---|
| Model | Froxel grid (≤320×180×64-256 slices, ~2 slices/m near, log depth), compute inject density/lighting → raymarch accumulate → temporal reproject → fullscreen composite | Analytic per-pixel solve (C1 "WaterFogSolve" MRT), per-channel Beer-Lambert + downwelling, waterline classify + rendered-surface prepass, optional half-res (2026-08-29) |
| Output | 3D ARGBHalf buffer: rgb = in-scatter S, a = scalar transmittance T; composite `scene·T + S` at scene-depth froxel z | Two RTs: absorb (per-channel multiplier) + inscatter (additive), hardware-blended |
| Lights | Directional (URP main shadows), spot + point with OWN shadow atlases (VSM, cube arrays), tube beams, translucent colored shadows, dust | Sun only + flat analytic scene-lamp glow (`WaterSceneLightsInscatter`, no shadows, pure ALU) + screen-space god-ray march |
| Medium bounds | Box/Sphere `VolumetricFogVolume` + height fog (base/max height, falloff) — flat world-Y confinement | Ocean half-space below DISPLACED surface (classify/prepass), pond = pool box, exclusion carves subtracted |
| Injection (URP) | compute `AfterRenderingShadows`, composite `AfterRenderingTransparents` (both serialized → movable without code) | fog `BeforeRenderingPostProcessing`, god rays `+1` |
| Scene access | `cmd.Blit(camera→temp)` + `Blit(temp→camera, mat)` inside the RenderGraph pass (compatibility-mode API, touches `RTHandle.rt`) | Blend-only; never copies the scene (deliberate) |
| Temporal | Reprojection subsystem + cascade scheduler | None (fog is analytic, stable) |

---

## 3. Blocker for the web target: the WebGPU port pass

Required regardless of which integration design is chosen, **only** for shipped browser
builds (editor/desktop testing works today):

- Add `webgpu` to every `#pragma only_renderers` (32 files). Mechanical but wide.
- **`RWTexture3D<float2>` DensityGrid is RGHalf** — `rg16float` is NOT a guaranteed WebGPU
  storage format (rgba16float is). The density grid needs widening to ARGBHalf or packing
  into r32uint (the water package's own OceanPackC doctrine). Lighting/extinction/final
  buffers are ARGBHalf — fine.
- Point-shadow CubeArray textures: supported in WebGPU core, NOT in compatibility mode —
  same caveat class as WaterSurface's ~22 textures note (audit F-VAR-11).
- The composite's `cmd.Blit` + `RTHandle.rt` access inside a RenderGraph pass is
  compatibility-mode usage; Unity 6 RG may demand an unsafe pass or a Blitter rewrite. The
  water package's no-scene-copy doctrine also flags the two fullscreen copies per frame as a
  WebGPU bandwidth cost (mid-frame target switches were measured expensive there).
- Known blind spot ([[luminex-rendergraph-blind-spot]]): the Luminex dev project (2022.3)
  NEVER compiles `VolumetricFogComputePass_RenderGraph.cs`; every RenderGraph edit must be
  compile-tested in the Unity 6 WaterPort import. `UNITY_PIPELINE_URP` is a manual define,
  WebGL-only, in WaterPort — on Standalone the RG file silently compiles out.

Estimate: the pragma sweep + density-grid format is a day-scale change plus a real browser
validation round (18-min builds). Everything else in this report can be developed and
proven in the editor first.

---

## 4. Why neither system can simply run "on top of" the other

Write `T_W`, `S_W` for the water fog's per-channel transmittance/in-scatter and `T_L`, `S_L`
for Luminex's scalar pair.

**Luminex before water (today's defaults):** `final = T_W·(T_L·scene + S_L) + S_W`.
The shafts `S_L` are multiplied by the water transmittance measured to SCENE depth — always
≥ the attenuation to the shaft's actual mean depth — so shafts over-darken and die exactly
as the 2026-07-26 god rays did. Rejected.

**Water before Luminex, Luminex models the water medium:** `final = T_L·(T_W·scene + S_W) + S_L`.
Now the scene pays `T_W·T_L` — the same water extinguished twice — and `T_L` is scalar, so
the double half is colourless: a grey veil over the water's hue. Also rejected as-is; but
note `T_L → 1` as Luminex's density → 0, which is what makes §5 workable.

**The structural conclusion:** exactly ONE system may own extinction (the water, for its
per-channel hue), and the other may only ADD light. Luminex's own knobs make an additive
role possible: `_FogIntensity` lerps `T_L` toward 1 while scaling `S_L`, and per-light
intensities can compensate a low medium density.

---

## 5. Increment 1 (MVP): Luminex as a shaft-only layer after the water fog

Mostly configuration; small, contained code.

- **Order:** set the serialized `compositePassEvent` to run AFTER the water stack —
  `BeforeRenderingPostProcessing + 2` (fog at +0, god rays at +1). No Luminex code change;
  it is an inspector field on the renderer feature.
- **Confinement:** one `VolumetricFogVolume` per water body. Big pond: Box volume matched to
  the pool box. Ocean: height-fog ceiling at the rest waterline (`heightFogMaxHeight = water
  surface Y`) with a soft falloff so shafts fade before the surface. This is the FLAT
  waterline approximation — the same one `WebGpuWaterFogAPI` and Simple fog accept — and its
  cost is visible exactly at the crossing on a big swell: shafts can pop through crests.
  The falloff band is the mitigation; the real fix is Increment 2.
- **Tuning contract:** keep the underwater volume's density low enough that `T_L ≥ ~0.9` at
  the grid far range (shafts stay additive; the residual scalar dimming on the scene is the
  accepted approximation), and recover shaft brightness through light intensity.
- **What stands down:** the water's own `WaterSceneLightsInscatter` lamp glow for any lamp
  that gets a `VolumetricLight` (else the lamp is counted twice); optionally the screen-space
  god-ray march once a directional `VolumetricLight` supplies shafts (A/B with the probe's
  G key). Fog absorb/inscatter/waterline/meniscus all stay untouched.
- **Known rough edges, accepted in the MVP:** no exclusion-carve awareness (a Luminex shaft
  will glow inside a dry underwater room — mitigate by volume placement); temporal
  reprojection may ghost for a few frames at fast submerge/emerge; scalar residual dimming.

**Effort:** scene/config work + one small water-side helper at most (e.g. a component that
copies a body's surface Y into the volume's ceiling). Testable same-day in the editor.

---

## 6. Increment 2 (endgame): the water solve samples Luminex's lighting grid

"Real volume lighting" with the water's own correctness machinery:

- Luminex's compute runs at `AfterRenderingShadows`; its `_LightingGrid` (froxel-lit
  scatter, ARGBHalf, with VSM/atlas shadows and translucent colored shadows already applied)
  exists long before the water fog pass. The water's `WaterFogSolve` — which already owns
  the wet-span integral, per-channel `T_W(t)`, armWeight waterline, exclusion carves and the
  downwelling mean-depth machinery — samples the grid along its wet span and accumulates
  `Σ L_froxel(t)·T_W(t)·Δt` as its lit in-scatter term (alongside or replacing
  `WaterSceneLightsInscatter`, behind a `WATER_FOG_LUMINEX` multi_compile fence per the
  fps-cliff rule).
- This gives: per-channel water extinction of every shaft, shafts clipped by the DISPLACED
  waterline (armWeight), shafts correctly dark inside exclusion carves, no double medium, no
  ordering hazard (Luminex's own composite is simply not enqueued underwater — or the
  underwater volume is left out of Luminex's world), and the fog debug views keep working.
- Cross-package seam: the water shader needs the grid + its frustum mapping as GLOBALS.
  Luminex binds `_VolumetricBuffer`/`_LightingGrid` as material/dispatch properties today, so
  this needs ONE small Luminex-side addition (publish grid texture + `_GridParams`/frustum
  uniforms globally, or a tiny `VolumetricSystemAccess` bridge component on the water side).
  No asmdef reference needed if the handoff is by global names — the same convention the
  water package uses internally (`SetGlobalTextureAfterPass`).
- Interaction with the half-res fog solve (2026-08-29): free — the solve is uv-driven, and
  froxel sampling is resolution-independent.
- Risks: froxel resolution (≤320×180 × ~2 slices/m near) is coarser than the water's
  analytic edges — expect soft shafts (that is the volumetric look) but verify banding
  against the blue-noise jitter; Luminex temporal reprojection happens in ITS raymarch pass,
  which increment 2 bypasses — sample the pre-raymarch lighting grid, so no history coupling.

**Effort:** medium — 1 Luminex file (globals publish), 2–3 water files (solve fence +
publisher keyword + tier/inspector toggle), plus tuning. Builds on the MVP's scene setup.

---

## 7. What Luminex replaces vs what stays

| Water feature | After integration |
|---|---|
| Fog absorb + inscatter + downwelling | **Stays** — sole owner of the medium (per-channel) |
| Waterline classify/prepass/meniscus | **Stays** — and clips shafts in Increment 2 |
| Screen-space god rays (KWS trio) | **Candidate to retire** once a directional VolumetricLight covers it (it measured ~2 fps and had 3 waterline bugs historically) |
| `WaterSceneLightsInscatter` lamp glow | **Retire per-lamp** as lamps become VolumetricLights (shadowed, textured cones vs flat analytic glow) |
| Exclusion carve shadowing of scatter | Stays; Increment 2 extends it to Luminex shafts |
| `WebGpuWaterFogAPI` for transparents | Unaffected (fragment-level API; Luminex is a fullscreen layer) |

## 8. Decisions needed before any code

1. GO/no-go on Increment 1 config trial in the editor (no web build needed).
2. Is the browser build a target for Luminex underwater? If yes, the §3 port pass is the
   long pole and should be scheduled first (or in parallel with the editor trial).
3. Increment 2's Luminex-side seam: global-texture publish (my recommendation) vs a C#
   bridge component — your call as Luminex's author, it touches your store package.
4. Which lamps/scenes first — big pond demo (bounded box, easiest) before ocean (height
   ceiling + swell crossing, hardest).
