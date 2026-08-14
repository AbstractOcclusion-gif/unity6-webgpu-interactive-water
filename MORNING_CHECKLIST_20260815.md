# MORNING_CHECKLIST_20260815 — top to bottom, one section at a time

Overnight session (2026-08-14 → 15, you were asleep): 3 files edited + C1 retry scripts staged
but NOT applied. Unity was closed all night — **everything below is untested by definition.**
Full detail in memory `overnight-20260815`; per-file md5s in section 0.

## 0. What changed on disk overnight (all byte-exact backed up)

| file | before | after | change |
|---|---|---|---|
| Runtime/Shaders/WaterChunkWall.shader | 346a3b47 | 9f679b3b | Task 1: scene-fog strip (same disease + cure as the exclusion wall) |
| Runtime/WaterVolume.Underwater.cs | 141246b6 | 3e019212 | Task 2: batch 4 — FogVolumeVisibleTo(cam) |
| Runtime/Rendering/WaterUnderwaterFogFeature.cs | d5d10c61 | 84556a74 | Task 2: batch 4 — per-camera cull at enqueue (byte-identical to the originally shipped batch 4) |

NOT touched: WaterExclusionWall.shader (31d75a55) and LargeBodyGodRays.shader (3d409358 — your
confirmed fixes), all scenes, all .meta, your 4 discard-list .cs files, and the 4 C1 fog files
(verified still at pre-C1 bytes: 7a9cf150 / d4d76862 / d338ad55 / 39a9dc29).

Task 1 verdict for the record: WaterChunkWall.shader carried a2ed9c3's full fog kit (pragma,
fogFactor varying, air-gated unity_FogColor lerp on the veil, MixFog on the full tier, fogged
meniscus) while the chunk disc surface (WaterSurface.shader + its vert/frag hlsl) carries ZERO
scene fog today. Mismatched exactly like exclusion wall vs surface → stripped all 6 sites, same
in-shader comment style. Meniscus is back to a pure premultiplied darken.

## 1. Compile ONCE

Open Unity, let it compile everything. Expected: clean console.
On compile errors: identify the file, use its rollback below, recompile, continue the checklist
without that task, and leave me a note.

## 2. Exclusion demo — regression guard (yesterday's confirmed fixes must survive)

- [ ] wall from AIR at DISTANCE: scattering/color/density hold, no wash
- [ ] water/exclusion junction: no seam
- [ ] from-air god-ray pane: wavy, never a flat-clipped cube

These files were untouched tonight; any regression here means a cross-file side effect — STOP
and diagnose before going further down the list.

## 3. Chunk scenes (Task 1 changed WaterChunkWall.shader)

- [ ] chunk walls at DISTANCE from AIR: shell keeps scattering/color — the fix's whole point
- [ ] shell vs disc rim at distance: colors agree (no wall-only wash washing past the disc)
- [ ] box + sphere + MESH chunk, LOW fog density, near-approach below surface height from all
      sides: no color/light pop (the 07-26 containment fix must still hold)
- [ ] meniscus at waterline touch: thin dark line still there (now pure darken, no fog tint)
- [ ] if the scene has Unity fog enabled: the shell no longer receives it — DELIBERATE, the
      disc surface never did

## 4. Pond / Deep Dive scenes (Task 2, batch 4 per-camera fog cull — UNTESTED CODE)

- [ ] circle the pool, any angle/distance with the pool ON screen: murk unchanged
- [ ] look AWAY (pool fully off screen): WaterCostProbe fog ms drops to ~0; look back: fog is
      instantly there again
- [ ] submerge + waterline crossing: unchanged
- [ ] scene view keeps its fog while the game camera looks away (each camera runs its own test)
- [ ] exclusion-room demo: unchanged (oceans always pass the cull)

## 5. ONLY IF 1–4 ARE ALL GREEN — C1 retry (its own compile + test cycle)

The 2026-08-14 bisect proved the wall-above-water break predates C1 by five days, so C1's
conviction was likely wrongful. All 22 edits of the original patcher re-verified tonight against
the current tree (dry-run offline; nothing applied). Do NOT run this together with any other
change — C1 gets its own cycle so a failure is attributable.

Apply (from repo root):

```
python _stage_tmp/apply_c1_retry_20260815.py
```

It re-gates the 4 pre md5s, backs up to _stage_tmp/_base_c1retry_20260815/, runs the original
patcher, and asserts the post md5s (5f608414 / 25a9822e / 2c976c97 / 4f526752). Then recompile
and test per memory `fog-single-solve-2026-08-13`:

- [ ] FIRST: exclusion demo, camera ABOVE water — wall display intact (the symptom that
      convicted C1; also re-check a pond "murk from any angle" frame and a scene view)
- [ ] underwater fog ocean + pond, debug views, waterline straddle, first frame after play
- [ ] junction + god-ray pane still fine

One-line revert, kept next to it:

```
python _stage_tmp/revert_c1_retry_20260815.py
```

## Rollbacks (per task, independent; run from repo root; one command per line)

Task 1 — chunk wall fog strip:

```
git checkout -- Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterChunkWall.shader
```

Task 2 — batch 4 pond cull (both files):

```
git checkout -- Packages/com.abstractocclusion.webgpuwater/Runtime/WaterVolume.Underwater.cs
git checkout -- Packages/com.abstractocclusion.webgpuwater/Runtime/Rendering/WaterUnderwaterFogFeature.cs
```

C1 retry (only if you applied it):

```
python _stage_tmp/revert_c1_retry_20260815.py
```

Byte-exact pre-overnight copies (same bytes `git checkout --` should restore, kept in case the
index and disk disagree):

- _stage_tmp/_base_overnight_20260815/WaterChunkWall.shader (346a3b47)
- _stage_tmp/_base_overnight_20260815/WaterVolume.Underwater.cs (141246b6)
- _stage_tmp/_base_overnight_20260815/WaterUnderwaterFogFeature.cs (d5d10c61)
- C1 pre-retry copies land in _stage_tmp/_base_c1retry_20260815/ when the apply runs
  (_stage_tmp/_base_fogc1/ still holds the same bytes from the 08-14 revert)

If in doubt, copy the backup over the file instead of git checkout — the backups are the exact
bytes measured tonight.
