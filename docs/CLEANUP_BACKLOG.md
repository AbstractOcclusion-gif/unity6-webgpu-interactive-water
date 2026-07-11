# Cleanup / Reorg Backlog

Living list of issues deferred to a dedicated clean-up / reorganization session. Add entries with
enough diagnostic context that they can be picked up cold.

---

## God rays / water receiver shader vs. fog (opaque floaters)

**Reported:** 2026-07-07, during ocean foam work (digression).

**Symptom:** On small ponds, god rays and god-ray "fog" appear over opaque floating objects at
certain camera angles — looks like the shafts aren't sorted / occluded correctly against the floaters.

**Root cause (per Bert):** the issue originates in the **water receiver shader**. Now that fog is in,
the water receiver has become largely redundant / "a bit useless", and its interaction with the
god-ray volume is what produces the artifact.

**Investigation context (pool god-ray pass, `Runtime/Shaders/GodRays.shader`):**
- Additive volume: `Blend One One`, `ZWrite Off`, `ZTest Always`, `Cull Front`, drawn `Transparent+100`.
- Its ONLY occlusion against solids is the per-step break `if (pe > sceneEye) break;` against
  `_CameraDepthTexture`.
- Both pipelines have `m_RequireDepthTexture: 1`; `m_CopyDepthMode: 0` (AfterOpaque), so the depth
  texture holds opaque geometry (the floaters ARE opaque, so they are present in it).
- Because occlusion works, the residual glow is the shaft segment *in front* of the floater
  accumulating (valid volumetric scattering), which reads as haze on the object at grazing angles —
  compounded/overlapped by the now-redundant water receiver path.

**To tackle during cleanup:** review whether the water receiver shader is still needed post-fog;
if not, remove/retire it and re-check the god-ray layering. If kept, add a soft depth-proximity
fade near geometry to the god-ray march instead of the hard `break`, and/or damp density where a
sample is close to `sceneEye`.

---

## Ocean foam — multi-scale detail (Crest-style)

**Noted:** 2026-07-07, during ocean whitecap foam work. **Partly superseded 2026-07-10:** the
shipped whitecap path now blends a SECOND rotated/rescaled octave by distance
(`SampleOceanWhitecapPattern`, `OCEAN_WHITECAP_OCTAVE2_*` in `WaterSurface.shader`), which covers
the anti-tiling goal. Crest's per-LOD dual-scale sampling remains a possible upgrade if the near
field still reads repetitive — see `FOAM_SYSTEMS_AUDIT_2026-07-11.md` (C1.x).

Increment 2b adds a single world-tiled foam texture thresholded by coverage. Crest samples TWO
tiling scales and blends them by distance/LOD (`WhiteFoamTexture` + `d_Crest_FoamMultiScale`) so
foam has both fine near-camera lace and coarse far-field structure without one tile size looking
repetitive up close or mushy far away.

**Enhancement:** add an optional second foam-texture scale to `OceanFftFoam`/the surface blend,
blended by camera distance (reuse the same distance term the cascade fade already computes). Gate it
so single-scale stays the default. Only worth doing if the single-scale look reads too repetitive
near the camera or too soft toward the horizon.

---

## Ocean FFT debug leftovers

**Noted:** 2026-07-11, foam-systems Batch 1 cleanup.

`OceanFft.compute` still ships the TEMP `VisualizePreview` kernel + `OceanPreview` target +
`OceanPreviewGain`, and `OceanFftDebug.compute` is debug-only. Left in place (the C# side
FindKernel/dispatch wiring would need an editor pass to gate safely). To tackle during cleanup:
strip them or gate behind a `WEBGPUWATER_DEBUG` define together with their C# dispatch sites.
Also: `_HorizonFadeDistance` legacy path in `WaterSurface.shader` is retired-but-kept behind
`_HorizonHazeDensity == 0` — remove once scenes are migrated.

---

## Pond foam decay knobs — remap to (fadeRate, deposit)

**Noted:** 2026-07-11, foam-systems Batch 1 (audit C0.5).

Pond foam exposes THREE decay controls (`foamDecay` fresh-survival, `foamDecayResidual`,
`foamDecayRate`) while ocean foam exposes a (fadeRate, deposit) pair — same user intent, different
parameterization. Shader-side the blend is now shared (`FoamDecayBlend`, `WaterFoamCommon.hlsl`,
with deliberately OPPOSITE semantics documented there). Remapping the pond inspector to a
(fadeRate, deposit) pair needs a serialized-field migration + editor verification — do WITH the
editor open, same rule as the WaterVolume settings migration (Phase 2).
