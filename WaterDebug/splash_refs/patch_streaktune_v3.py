#!/usr/bin/env python3
"""Streak retune (2026-08-06 v3): jets rise from the impact heart, lead the splash,
die BEFORE the chunk cloud, and sink back instead of hovering. md5-gated."""
import hashlib, sys, os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
PATH = os.path.join(ROOT, 'Packages', 'com.abstractocclusion.webgpuwater',
                    'Runtime', 'WaterSplashEmitter.cs')
WANT = 'd48ff8287fa72a19285e29fed950d86d'

raw = open(PATH, 'rb').read()
got = hashlib.md5(raw).hexdigest()
if got != WANT: sys.exit(f'MD5 MISMATCH: {got} - ABORT')
crlf = b'\r\n' in raw
t = raw.decode('utf-8').replace('\r\n', '\n')

def rep(text, old, new, where):
    n = text.count(old)
    if n != 1: sys.exit(f'ANCHOR FAIL ({where}): {n} matches')
    return text.replace(old, new)

t = rep(t, '''        // KWS WaterSplashes.prefab layer A: a handful of velocity-stretched chunk sprites
        // thrown near-vertically with NO gravity, decelerated by drag - the splash's water
        // columns. The stretch itself lives on the renderer (builder): lengthScale 4.
        const int StreakBurstMinCount = 3;            // KWS bursts 4-6 regardless of strength
        const int StreakBurstMaxCount = 6;
        const float StreakUpSpeedMin = 0.6f;          // vertical throw at threshold strength...
        const float StreakUpSpeedMax = 2.0f;          // ...and at full strength (KWS 0.5-2)
        const float StreakConeTangent = 0.18f;        // horizontal/vertical ratio (KWS 10 deg cone)
        const float StreakSizeJitterMin = 0.7f;       // per-sprite size, relative to crown base
        const float StreakSizeJitterMax = 1.0f;''',
'''        // KWS WaterSplashes.prefab layer A: a handful of velocity-stretched chunk sprites
        // thrown near-vertically, decelerated by drag - the splash's water columns. The
        // stretch itself lives on the renderer (builder): lengthScale 4.
        const int StreakBurstMinCount = 3;            // KWS bursts 4-6 regardless of strength
        const int StreakBurstMaxCount = 6;
        const float StreakUpSpeedMin = 1.0f;          // vertical throw at threshold strength...
        const float StreakUpSpeedMax = 2.5f;          // ...at full strength - jets LEAD the
                                                      //    splash, they never trail it
        const float StreakConeTangent = 0.18f;        // horizontal/vertical ratio (KWS 10 deg cone)
        const float StreakSizeJitterMin = 0.7f;       // per-sprite size, relative to crown base
        const float StreakSizeJitterMax = 1.0f;
        // Jets rise from the impact HEART: a real splash column stands in the middle of
        // the crown, so the spawn jitter is much tighter than the crown's ring.
        const float StreakSpawnJitterScale = 0.15f;
        // Jets die BEFORE the chunk cloud (KWS: streaks 0.75-1.5s vs droplets up to 4s).
        // A column that outlives the crown reads as scraps hovering over a dead splash.
        const float StreakLifetimeFactorMin = 0.5f;
        const float StreakLifetimeFactorMax = 0.8f;
        // A touch of gravity so a spent column sinks back instead of hanging weightless
        // (KWS ships 0 at their world scale; at ours the hang is what read as fake).
        const float StreakGravityModifier = 0.35f;''',
'streak consts')

t = rep(t, '''                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * SpawnRingRadiusScale);
                float up = Mathf.Lerp(StreakUpSpeedMin, StreakUpSpeedMax, strength)''',
'''                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * StreakSpawnJitterScale);
                float up = Mathf.Lerp(StreakUpSpeedMin, StreakUpSpeedMax, strength)''',
'streak spawn jitter')

t = rep(t, '''                ep.startLifetime = crownLifetime
                                   * Random.Range(CrownLifetimeJitterMin, CrownLifetimeJitterMax);
                ep.startColor = streakColor;''',
'''                ep.startLifetime = crownLifetime
                                   * Random.Range(StreakLifetimeFactorMin, StreakLifetimeFactorMax);
                ep.startColor = streakColor;''',
'streak lifetime')

t = rep(t, '''            main.gravityModifier = 0f;   // KWS layer A: the columns hang, drag bleeds them out''',
'''            main.gravityModifier = StreakGravityModifier; // spent columns sink back down''',
'streak gravity')

data = t.replace('\n', '\r\n') if crlf else t
open(PATH, 'wb').write(data.encode('utf-8'))
print('patched WaterSplashEmitter.cs (streak retune v3)')
