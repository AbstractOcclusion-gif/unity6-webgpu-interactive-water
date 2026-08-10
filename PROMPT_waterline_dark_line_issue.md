# PROMPT — the far-waterline DARK LINE: issue dossier (verify, instrument, THEN patch)

Unity senior dev, WebGpuWater (`Packages/com.abstractocclusion.webgpuwater`). Rules:
ask before code, md5-gate patches with FRESH measurements, line endings PER FILE
(one file is MIXED within itself), NEVER run git through the bridge, KEEP REPLIES
SHORT — Bert's patience is spent after a long day. This prompt describes an ISSUE,
not a fix. Two prescriptive attempts failed; earn the diagnosis before touching code.

## THE SYMPTOM
Open water (unbounded ocean, FFT), raging sea, Full tier (ForceHigh). A thin DARK
line / dashes along the water junction at DISTANCE (and along the drawn sheet's far
raster edge from underwater) in the beauty view. Stable, not a 1-frame pop.
CONFIRMED by A/B: WaterCostProbe F-toggle fog OFF -> line GONE (the fog feature's
output — or something composited through it — paints it).
⚠️ God rays were NEVER cleanly A/B'd (their composite is masked by the fog's
coverage, so the F-toggle may gate them too). G-toggle with fog ON is owed.

## HISTORY — what already failed (do not repeat)
1. Five consumer-side classification fixes (2026-08-10, all in WaterUnderwaterFog.shader,
   ALL STILL IN THE TREE as band-aids): camSurf fallback (DID fix the red mask-vs-span
   dashes — confirmed), wet-median, AND-corroboration, endpointWet (made it WORSE —
   lesson: a march against the VERTICAL field at grazing speckles by construction),
   far-sheet no-march. The dark line survived all five.
2. F3 V1 — a 512 m / 256² / R16F top-down displaced-height RT as single classification
   authority — WAS BUILT (8 files, unattended session, same day) and the FIRST TEST
   STILL SHOWED THE LINE. **BUT: F3 fails SOFT.** If the height shader didn't resolve
   (serialized slot unassigned + Shader.Find fallback), the pass didn't record, or ANY
   C# compile error disabled the assembly, every consumer silently falls back to
   pre-F3 behaviour and the frame is pixel-identical. The surviving line is therefore
   NOT yet evidence against F3. Bert was mid Library-rebuild + Unity restart at close.

## STEP 0 — FIVE-MINUTE VERIFICATION (do this before ANY reasoning)
1. Console: any C# compile errors? Three APIs were shipped unverified and would kill
   the whole feature silently: RasterCommandBuffer.SetViewProjectionMatrices;
   TextureDesc(int,int)+filterMode/wrapMode fields; UniversalCameraData
   .worldSpaceCameraPos/GetViewMatrix/GetGPUProjectionMatrix (WaterUnderwaterFogPass.cs).
2. Console: "WaterHeightRT ... not found" warning? -> assign the serialized
   heightRtShader slot on WaterUnderwaterFogFeature.
3. Frame Debugger: a 256×256 height pass recorded BEFORE the two fog passes? Inspect
   the RT — does it look like the wave field, window following the camera?
4. FOG_GATES flat-colour view: B channel on = Simple tier (the F-toggle is
   SESSION-STICKY; this faked a repro once already).
5. Did the fog actually recompile after the F3 edits (3 shared includes changed)?
   Logs/shadercompiler-*.log per-variant entries are the proof.
If ANY of 1-5 fails: fix THAT, retest the line, and you may be done.

## STEP 1 — IF THE RT PROVABLY RAN AND THE LINE PERSISTS
The painter is NOT (only) classification. Instrument, numerically:
1. G-toggle A/B (fog ON): line dies -> hunt moves to LargeBodyGodRays.shader
   (still carries rest-plane-era fallbacks; none of the 08-10 fixes ported).
2. Pass isolation: ABSORB only, then INSCATTER only (skip the other submission in
   WaterUnderwaterFogPass or a temp keyword). Dark line in absorb-only = a DARKENING
   term; line as missing-inscatter = span deficit.
3. THE UNEXPLORED LEAD: every prior fix chased pathLen, but the artifact is DARK and
   pass 0 multiplies by `pathTrans * depthAtten` where depthAtten (downwelling) is
   driven by `deepestY` and `surfaceRefY` — inputs that MIX authorities per branch
   (camSurf vs sceneSurf vs prepass hit heights). A correct span with a corrupted
   deepestY/surfaceRefY darkens with no matching in-scatter = a dark line.
4. Add numeric debug views (extend WaterFogDebug.hlsl, modes 7-13 pattern): heatmaps
   of pathLen, deepestY - cam.y, surfaceRefY - camSurf, depthAtten, pathTrans. The
   guilty term shows a junction-shaped discontinuity. Convict, THEN fix its inputs.
5. After a confirmed root fix: propose deleting the obsoleted band-aids (the five in
   HISTORY-1 + F3's kill list), one confirmation per deletion.

## GROUND TRUTH (F3 tree, RE-MEASURE — Bert hand-edits, sessions overlap)
- WaterUnderwaterFog.shader b8ffa98e7f4b74484d3a383a68aafb70 (fog consumers -> RT;
  ChopInverted has ZERO fog callers now; kill list still in)
- WaterHeightRT.shader 2c4f8bd26986d238cdd61415b20d0917 (+.meta guid a6e53f7c...) NEW
- WaterSurfaceVertStage.hlsl 43206563 (displacement chain extracted verbatim into
  DisplaceSurfaceVertex — verified code-identical) | WaterWaterline.hlsl 91026eba
  (WATER_HEIGHT_RT_* constants, SampleHeightRTWorldY / SurfaceSignedGapRT / feather)
- WaterUnderwaterFogPass.cs a6a1603e (MIXED CRLF/LF — byte-level patches only) |
  WaterUnderwaterFogFeature.cs 40986ef8 | WaterWaveConstantsValidator.cs c346017d
- Unrelated but nearby, CONFIRMED GOOD, do not disturb: WATER_STRIP_SHORE fork
  (WaterShore.hlsl 3f8c3921 / WaterSurfWaves.hlsl dc409b42 / WaterUniformPublisher.cs
  e9b9d749), [loop] chop inversion, camSurf marcher fallback.
- LargeBodyGodRays.shader b6f828d5 UNVERIFIED (re-measure).

## DEBUG DECODER
FogUnpainted: MAGENTA = span exists/mask killed (HARMLESS — by design at the near
crossing and the from-above junction); RED = mask wants fog/no span (real hole).
Branch: yellow ANALYTIC, blue PREPASS_WET, green PREPASS_AIR, cyan WAVY_MARCH,
magenta CARVE_MARCH, grey FLAT_SIMPLE. FOG_GATES: R underwater, G dry volume, B Simple.
Known-harmless artifacts to NOT chase: near-plane magenta band; from-above magenta
junction line + masked blue edge-row pixels.

## PROCESS
Quote expected duration first. Check in every ~10 min. Frame Debugger + console +
logs BEFORE shader edits. Show a file/line plan before code. One change per test.
Bert runs git himself and does a human pass on everything.
