# CONTINUE — WebGpuWater, big / infinite bodies

**Branch:** `main` is the source of truth. The old `feature/large-body-water` branch was **stale** (0 unique commits, behind main) and was deleted; all real work lives on `main`. I commit/push on my end — the sandbox git index is corrupt, so you (the agent) can read via `ls-remote`/`log`/`rev-list` but must not rely on the index, and must present git commands on **separate lines** (never chained with `&&`).

**Package:** `Packages/com.abstractocclusion.webgpuwater`, namespace `AbstractOcclusion.WebGpuWater`. Unity **6000.3.9f1**, URP **17.3.0** (RenderGraph-native; `WEBGPUWATER_URP` is defined when URP ≥ 12). Test scene: `Assets/ocean test.unity`. Two render features are on `Assets/Settings/PC_Renderer.asset`: **Large Body Atmosphere** and **Water Underwater Fog** (each needs its shader assigned).

---

## NEXT TASK (do this first): extend the UNDERWATER surface for big/infinite bodies

The ocean **clipmap** extends the *above-water* surface to the horizon (camera-following radial mesh, `_IsClipmap`). But the **under-water surface** seen from below is still the old bounded flat plane (`surfaceUnder`), so once you're submerged in the ocean the underside of the water doesn't reach out — it reads wrong. Also `_Underwater` is declared in `WaterSurface.shader` but never set from C#. Goal: make the submerged view of the surface reach the horizon like the above-water clipmap (reuse the clipmap, don't rewrite), with correct under-surface shading (sky/refraction from below, fog integration).

This is the prerequisite that makes the **milestone** — underwater god rays — read correctly.

## ROADMAP (my order, after water-under)
1. **Extend water-under** ← now
2. **Underwater god rays** ← the MILESTONE (caustic / surface-broken shafts living inside the underwater fog volume we just built; reuse the pool `GodRays.shader` caustic-projection technique, scaled + bounded like the fog)
3. **Ripples** (near-field on big bodies)
4. **Caustics + foam** (large-body)
5. **Shore + breaking waves**

---

## RULES (non-negotiable — how we work)
- **ASK BEFORE TOUCHING CODE.** Never code without my express OK. Explain first, find the *simplest* way, write a short plan, WAIT for approval, then go in small **compile-and-look** increments WITH me at the editor. You cannot compile Unity or see pixels — iterate with me.
- **Reuse, never rewrite** working code; wrap as thin, gated adapters. Reuse the port's existing systems.
- **Reference-first (HARD RULE):** before writing any technique, read how **KWS** and **Crest** do it, then write fresh code in our namespace. Never copy their code; never reinvent what they solved.
- **Everything gated / default-off** so pools and bounded bodies stay byte-for-byte unless opted in.
- **No magic numbers / no hardcoded strings** — named consts/enums. Short single-responsibility functions, early returns, comments say WHY not WHAT, no dead code, minimal public surface, prefer immutability.
- **Never guess — always check** the current source (memories/handoffs can be stale; verify a symbol still exists before relying on it). Verify RenderGraph/URP API against the installed package under `Library/PackageCache/com.unity.render-pipelines.*`.
- **Add a verification step** to every non-trivial change.

## REFERENCES (in the sibling `KWSWater` project, NOT in this repo)
- **Crest:** `KWSWater/Packages/com.waveharmonic.crest` — underwater renderer, `MaskHorizon.hlsl` / `Horizon.shader` (horizon mask plane), `Volume/IntegrateWaterVolume.hlsl`, `UnderwaterShared.hlsl` (absorption+scattering), meniscus.
- **KWS 2:** `KWSWater/Assets/KriptoFX/WaterSystem2` — `UnderwaterPass`, `KWS_Underwater.shader` (fullscreen, stencil mask, half-line tension meniscus), near-plane-corner submersion detection, volumetric light + caustics.
- Also **StylizedWater3** + **RAM** there for structure.

---

## STATE — shipped this session (on `main` working tree; verified live unless noted)

**Horizon haze (Increment 2, DONE + verified + committed):** `WaterSurface.shader` fades the far ocean into the sky with exponential `1 - exp(-density·dist)` toward `_HorizonHazeColor` (`.a` tints sky→fixed color; 0 = seamless). Supersedes the old `horizonFadeDistance` smoothstep (kept as an `else` fallback; retire later). Knobs on `WaterVolume` (Horizon Haze Color/Density), gated to `IsOceanClipmap`.

**Above-water god rays (Increment 3a, built + worked, but MISDIRECTED):** `LargeBodyGodRays.shader` + `LargeBodyAtmosphere{Feature,Pass,Gate}.cs` — fullscreen RenderGraph shadow-shaft raymarch, sun + knobs (`_LargeGodRay*` on `WaterVolume`). **These are ABOVE-water atmospheric shafts, which I did NOT want** — "god rays" here means UNDERWATER caustic shafts. This code is parked; the real god rays (milestone #2) are underwater and may supersede/remove the LargeBodyAtmosphere/LargeBodyGodRays files. Don't build on the above-water god rays.

**Real underwater fog (DONE + verified + this is the prerequisite for the milestone):**
- `WaterUnderwaterFog.shader` + `WaterUnderwaterFog{Feature,Pass}.cs` — fullscreen RenderGraph, gated on `WaterVolume.UnderwaterFogActive`.
- Fogs only the **in-water segment** of each camera→scene ray → a waterline falls out for free.
  - **Ocean (unbounded):** below-surface half-space (`WaterPathLength`) — a fullscreen screen effect, runs only when submerged.
  - **Pond (bounded):** ray clipped to the pool box (pool space `[-1,1]` xz, `[-1,0]` y) via `IntersectCube` → a **finite fog volume visible from any angle** (renders whenever Water Fog is on, not just when submerged).
- **Partial submersion** trigger: samples the 4 near-plane corners; runs as soon as the near plane dips below the surface (`ComputeCameraSubmerged` in `WaterVolume.cs`).
- **Per-channel Beer-Lambert** absorption + **downwelling depth darkening** (`DownwellingAttenuation`), done as two hardware-blend passes (`Blend Zero SrcColor` absorb, then `Blend One One` inscatter) so scene color never has to be copied.
- **Fused with Water Fog:** one `waterFog` toggle + `fogColor`/`fogExtinction`/`fogDensity` drive both looking-in and submerged fog (no separate underwater knobs). Depth darkening reuses the Depth Darken controls.
- Globals published by the primary body: `_CameraUnderwater`, `_UnderwaterSurfaceY` (= `VolumeCenter.y`, flat plane for now), `_UnderwaterUnbounded`. Surface-Y is flat (U1/U2); **wave-accurate waterline + pixel-perfect KWS meniscus are deferred** (later polish).
- KNOWN OVERLAP: while above water looking into a pond, the volume fog can **double** with the surface shader's own refraction fog — the cleanup (retire the per-object/refraction fog where the volume fog now covers it) is a later "U3" step.

## GOTCHAS (URP 17.3 RenderGraph — learned the hard way this session)
- To composite onto the camera color with a hardware blend, bind the attachment `AccessFlags.ReadWrite` (loads the scene). `AccessFlags.Write` alone **discards → black screen**.
- Keep depth/shadow/published globals alive in a custom raster pass with `builder.UseAllGlobalTextures(true)`; hand a produced texture to a later pass with `builder.SetGlobalTextureAfterPass(handle, id)`.
- `CoreUtils.DrawFullScreen(ctx.cmd, material, null, passIndex)` inside `SetRenderFunc`; features build the material with `CoreUtils.CreateEngineMaterial(shader)` (a serialized `Shader` field, assigned in the renderer inspector) and free it in `Dispose(bool)`.
- Scene depth (`_CameraDepthTexture`) is the **opaque** scene (the water surface is transparent), so a ray to it passes through the water box — perfect for looking-in fog. Requires **Depth Texture ON** in the URP asset.
- Reuse `IntersectCube` (`WaterShared.hlsl`) + `PoolToWorld`/`WorldToPool` (`WaterVolume.hlsl`); the pool water box is `[-1,-1,-1]..[1,0,1]`.
- Detection runs on the **primary** body only; multi-body simultaneous submersion is an edge case for later.
- Beware per-scene **renderer/quality overrides** (a Mobile-asset override silently ran the wrong renderer once — the feature "wasn't there"). Confirm the scene uses `PC_Renderer` with both features.
- Bash `grep` over the mount can be **stale right after an edit** — `Read` is authoritative.

## START
Confirm the current state compiles and looks right, then **read the Crest/KWS underwater-surface references**, propose a short plan for **extending the underwater surface to the horizon** (reusing the ocean clipmap, gated, with correct under-surface shading + fog integration), and **WAIT for my OK** before coding.
