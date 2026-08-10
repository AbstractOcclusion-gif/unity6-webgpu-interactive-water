# PLAN F3 — Top-down displaced-height RT: ONE classification authority (2026-08-10)

## Why (the day's evidence)
Every remaining waterline artifact traces to the same disease: consumers classifying
against DIFFERENT approximations of "where is the displaced surface" — the vertical
analytic read (chop-blind, weaves under grazing rays), the 3x chop-inverted read
(expensive, derivative-fizzy), the screen-space prepass (exact per pixel but signed-twin
coin tosses + reduced-res quantization at silhouettes), and per-path fallback lines
(rest plane, camSurf). Five targeted fixes each patched ONE consumer; the dark line
survived because the mechanisms compound (a quantized-short prepass depth defeats the
far-sheet threshold AND mis-anchors the marcher). F3 replaces the approximations with
ONE ground truth every consumer samples.

## What
A small camera-following TOP-DOWN height RT of the true DISPLACED surface:
`HeightRT(world.xz) -> surface world Y`. Then
`SurfaceSignedGapRT(p) = p.y - SampleHeightRT(p.xz)` — chop-correct by construction,
C0-smooth via bilinear filtering, identical for every consumer.

## P0 DECISIONS — Bert locks these BEFORE code
1. **Source: raster vs compute.**
   - (A, recommended) RASTER: dedicated ortho draw of a grid displaced by the EXISTING
     vertex path (same includes as WaterSurface: FFT cascades + analytic bands + surf
     fronts + shore transforms + edge feather). Chop is handled BY rasterization —
     horizontal displacement lands where it lands, which IS the chop-inverted answer.
     Zero math drift possible (reuse-never-rewrite applied to the authority itself).
   - (B) COMPUTE: evaluate LargeBodyWaveHeightDispShore per texel + 2-iter inversion.
     No new camera/pass, but re-pays the inversion cost and can drift from the vertex path.
2. **Window extent + resolution.**
   - (A, recommended) 512 m square centered on camera XZ, 256x256 -> 2 m texels.
     Far junction at 250 m reads waves >= ~8 m wavelength fine; BEYOND the window the
     flat-fallback premise ("waves are sub-pixel") is finally actually true.
   - (B) 1 km / 256 -> 4 m texels (reaches further, coarser near-mid).
   - (C) two cascaded windows 256 m + 1 km (best, most machinery).
3. **Format:** R16Float, height relative to the rest plane (range +-65 k, ~1 cm precision
   at 10 m amplitude; float16 IS filterable on WebGPU — the format doctrine holds).
   Bilinear sampler. ~128 KB.
4. **V1 consumer set (recommended: fog only, one confirmable step):**
   - fog mask (ArmWeight): RT gap replaces SurfaceHeightAtXZChopInverted
   - fog span classification incl. sceneUnder tests: RT gap replaces vertical reads
   - the crossing MARCH + third exception: sample the RT along the ray (cheap texture
     taps, NO field evaluation) — the marcher's field math leaves the fog entirely
   - out-of-window: blend to the existing camSurf/flat behaviour over a feather band
   V2 (after V1 confirmed): god-ray bisections/composite, meniscus, exclusion wall.
5. **Kill list — ONLY after V1 is confirmed good** (each currently shipped hack becomes
   deletable): wet-median, AND-corroboration special-casing, endpointWet + far-sheet
   no-march, the camSurf fallback line (the RT covers its window; beyond it camSurf
   remains the asymptote). The chop-inversion (WaterWaterline) stays for NON-fog
   consumers until V2 migrates them.
6. **Update + tiers:** re-rendered every frame (waves move); Full tier only —
   WATER_FOG_SIMPLE never touches it; strip keyword unaffected. Editor + play mode.
7. **Ownership/pass placement:** rendered by the fog feature's setup (same place that
   runs the existing eye-depth prepass), BEFORE both fog passes; global texture +
   window-frame uniforms (center, extent, validity), validator-paired constants for
   any shared sizes.

## Expected side wins
- COMPILE: the fog's remaining bulk is inlined field math at ~10 classification sites;
  RT sampling removes most of it -> the ~600 KB Full variants shrink again, on top of
  the strip fork.
- RUNTIME: marches become texture taps; the 3x inversion leaves the fog's mask.
- P2 synergy: the sea-state displacement field (P2) gets a ground-truth surface to
  bake its spatial amplitude authority against.

## Test protocol (V1)
1. The dark line / specks at the far junction, above + below, raging sea (the exact
   repro that survived today) — expect gone inside the window.
2. Partial submersion transition (R2/R3 regression check — must stay seamless).
3. Carve/exclusion stitch (the prepass carve handoffs are untouched in V1 but re-verify).
4. Compile time + fps before/after.

## NOT in scope
Buoyancy/CPU mirror (render-only authority), Simple tier, pond path (IntersectCube lid
already correct), god rays (V2), any look tuning.
