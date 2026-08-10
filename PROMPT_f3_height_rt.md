# PROMPT — F3: top-down displaced-height RT, ONE waterline classification authority

You are a Unity senior dev on the WebGpuWater package
(`Packages/com.abstractocclusion.webgpuwater`). Follow the project coding standards
(no magic numbers, comments explain WHY, reuse-never-rewrite, ask before code, fail
fast). Design is LOCKED in `docs/PLAN_F3_height_rt_2026-08-10.md` — read it FIRST,
then this prompt, then the code. Do not re-litigate closed decisions.

## WHY THIS EXISTS (one paragraph of history)
2026-08-10: after P1 stochastic wave sets shipped, the far waterline junction showed
artifacts (red dashes, white specks, dark line). FIVE targeted fog fixes shipped —
camSurf fallback (red: CONFIRMED fixed), wet-median, AND-corroboration, endpointWet,
far-sheet no-march — and the dark line SURVIVED, because every fix patched one
CONSUMER of a corrupted/absent authority: the fog classifies against four different
approximations of "where is the displaced surface" (vertical analytic read,
3x chop-inverted read, reduced-res signed prepass, per-path fallback lines). A/B
proof: WaterCostProbe F-toggle fog OFF -> artifact gone (fog is the painter).
F3 replaces the approximations with ONE sampled ground truth.

## GROUND TRUTH — verify before touching (md5, re-measure; line endings PER FILE)
- WaterUnderwaterFog.shader 194c42ec2c817d34b653fd079a56cb0b (LF) — carries ALL five
  2026-08-10 fixes; the kill list below refers to hunks inside it.
- WaterWaterline.hlsl cced4ea5468808c2ef9c9e2a6b9666c7 (LF) — SurfaceHeightAtXZ /
  SurfaceSignedGap / SurfaceHeightAtXZChopInverted ([loop]) / SurfaceHeightBand /
  WindWaveSampleXZ (shared coordinate rule — keep using it).
- WaterShore.hlsl 3f8c3921... / WaterSurfWaves.hlsl dc409b42... — carry the
  WATER_STRIP_SHORE inert fences (CONFIRMED: compile time "way better"). The height
  pass must NOT define WATER_STRIP_SHORE (it needs real shore/surf where present).
- WaterUniformPublisher.cs e9b9d749... (LF-dominant, ASCII) — publishes the strip
  keyword in PublishUnderwater; the fog's per-frame globals live here.
- LargeBodyGodRays.shader b6f828d5... UNVERIFIED (Bert hand-edits; re-measure).
- WaterVolume.Underwater.cs 30b4208c... — MIXED CRLF/LF WITHIN the file: byte-level
  single-line patches only (see never-write-via-shell-mount memory for the
  normalized-index -> raw-offset patcher).
- Bert runs git HIMSELF. NEVER run git through the bridge, not even status.

## WHAT TO BUILD (V1 = fog only; the PLAN's P0 defaults unless Bert overrode them)
1. **Height pass (raster, P0-1A):** ortho top-down draw of a dedicated grid mesh
   displaced by the SAME vertex path the surface uses (same includes: FFT cascades +
   analytic bands + surf fronts + shore transforms + LbwEdgeWeight). Output = surface
   world Y minus rest plane, R16Float, bilinear. Chop is handled BY rasterization —
   the horizontal displacement lands where it lands, which IS the inverted answer.
   - Window: 512 m square centered on camera XZ, 256x256 (P0-2A). Snap the window
     origin to whole texels (like the sim window) so it does not shimmer.
   - Pass owner: the fog feature's setup, alongside the existing OceanSurfaceEyeDepth
     prepass, BEFORE both fog passes. Publish `_WaterHeightRT`, window center/extent,
     and `_WaterHeightRTValid` as globals (black + valid 0 on teardown — the
     stale-global trap, see LargeBodyAtmospherePass precedent).
   - A vertex-displacing draw: no fragment cost worth stripping; the grid is its own
     mesh (do NOT reuse the clipmap - its LODs are camera-projective).
2. **Sampling API (WaterWaterline.hlsl):**
   `float SampleHeightRTWorldY(float2 xz)` and
   `float SurfaceSignedGapRT(float3 p)` with an out-of-window FEATHER back to the
   existing behaviour (inside -> RT; outside -> current camSurf/flat asymptote).
   ONE home; every consumer calls these, nobody re-derives.
3. **Fog migration (WaterUnderwaterFog.shader):**
   - mask/ArmWeight: RT gap replaces the ChopInverted read (keep the derivative-split
     structure: position from RT gap; ddx/ddy of the SAME RT gap is now smooth — the
     bilinear surface is C0 and texel-stable, the fizz source is gone).
   - every SurfaceSignedGap classification (sceneUnder tests, clamps): RT gap.
   - the crossing march + refine: sample the RT along the ray (texture taps, no field
     evaluation). Keep step/refine counts; they become cheap.
   - third exception: keep the structure but let it read RT-consistent values; the
     expectation is that the special cases stop firing wrongly.
4. **Validator:** any constant shared between the pass C# and shader (window size,
   resolution) gets a WaterWaveConstantsValidator pair, same pattern as
   WATER_MAX_WAVES.

## KILL LIST — only AFTER Bert confirms V1 kills the dark line (each is a named hunk
in WaterUnderwaterFog.shader; delete, do not comment out):
wet-median block (PREPASS_WET), AND-corroboration special comment (revert to a plain
interior test against RT), endpointWet param + its 5 call-site args, far-sheet
no-march short-circuit, camSurf fallback line (RT covers its window; camSurf stays
the OUT-of-window asymptote only). SurfaceHeightAtXZChopInverted STAYS — meniscus /
god rays / wall still use it until V2.

## TRAPS (all cost a round before; do not rediscover)
- WaterCostProbe F-toggle is SESSION-STICKY: confirm Full tier via FOG_GATES (flat
  colour, B channel = Simple) before believing any repro.
- Debug decoder: FogUnpainted MAGENTA = harmless (span exists/mask killed);
  RED = real hole. Branch: yellow ANALYTIC, blue PREPASS_WET, green PREPASS_AIR,
  cyan WAVY_MARCH, magenta CARVE_MARCH, grey FLAT_SIMPLE.
- 16-sampler d3d11 cap on WaterSurface Pass 0 (the fog is a separate pass — but if a
  V2 consumer lives in WaterSurface, borrow sampler_CameraOpaqueTexture via
  UNITY_DECLARE_TEX2D_NOSAMPLER).
- WebGPU cannot filter float32 — R16Float + bilinear is the contract; do not "upgrade".
- The fog compiles per-variant at ~600 KB before this work; if you touch
  WaterLargeWaves/WaterWaterline you recompile the fog — batch your edits.
- Compiler-warning hygiene: the logs already show WaterLargeWaves(~548) potentially
  uninitialized height/disp and WaterExclusion(457) const div0 — do not add new ones;
  fix these two if touched en route.
- Editor logs: Logs/shadercompiler-*.log has per-variant outsize= — use it to verify
  the compile-size win instead of guessing.

## TEST PROTOCOL (Bert runs; quote before starting)
1. Raging sea, camera just under the surface, far junction, above AND below:
   dark line / specks GONE inside the 512 m window (the exact surviving repro).
2. Partial submersion crossing: still seamless (R2/R3 regression gate).
3. Exclusion/carve stitch: unchanged (V1 does not touch carve handoffs).
4. Compile time (should DROP again — field math leaves the fog) + fps.
5. FogUnpainted: magenta near-band only; no red anywhere.

## PROCESS
Quote expected duration first; check in every ~10 min; show a file/line plan before
generating code; md5-gate every patch with fresh measurements; narrate every tool
action; keep replies short. Bert does a human pass on everything.
