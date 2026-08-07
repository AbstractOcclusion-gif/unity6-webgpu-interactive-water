#!/usr/bin/env python3
"""REVERT streak jets entirely (2026-08-07): inverse of patch_streaks_v2 + v3.
Restores the three files byte-exactly to their confirmed increment-1 state,
verified by md5 equality. Crown chunk cloud is untouched."""
import hashlib, sys, os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
PKG = os.path.join(ROOT, 'Packages', 'com.abstractocclusion.webgpuwater')

FILES = {
    'emitter':  (os.path.join(PKG, 'Runtime', 'WaterSplashEmitter.cs'),
                 '4342ac077e90169b240959e6f02f0694', '83d81bab9abf4a96d1700daaa0bb00d7'),
    'scenerig': (os.path.join(PKG, 'Editor', 'WaterBuildKit.SceneRig.cs'),
                 '5598975f0b6e72331992573d1e9d6ebb', 'ee1ccf8c5388b4dc279179eb3070ad9e'),
    'editor':   (os.path.join(PKG, 'Editor', 'WaterSplashEmitterEditor.cs'),
                 'c5a97b055233522baab6c6a454f0ca70', 'ff2ea4c8bbb5b8a1a5dd83b10d43dddc'),
}

def load(key):
    path, want, _ = FILES[key]
    raw = open(path, 'rb').read()
    got = hashlib.md5(raw).hexdigest()
    if got != want: sys.exit(f'MD5 MISMATCH {key}: {got} - ABORT')
    return path, raw.decode('utf-8').replace('\r\n', '\n'), b'\r\n' in raw

def save(key, path, text, crlf):
    data = text.replace('\n', '\r\n') if crlf else text
    raw = data.encode('utf-8')
    open(path, 'wb').write(raw)
    want = FILES[key][2]
    got = hashlib.md5(raw).hexdigest()
    if got != want: sys.exit(f'REVERT VERIFY FAIL {key}: {got} != {want}')
    print(f'reverted {os.path.basename(path)} -> md5 {got} (byte-exact)')

def rep(text, old, new, where):
    n = text.count(old)
    if n != 1: sys.exit(f'ANCHOR FAIL ({where}): {n} matches')
    return text.replace(old, new)

# ---- emitter: undo v3 first (newest), then v2 ----
path, t, crlf = load('emitter')

t = rep(t, '''        // KWS WaterSplashes.prefab layer A: a handful of velocity-stretched chunk sprites
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
'''        // KWS WaterSplashes.prefab layer A: a handful of velocity-stretched chunk sprites
        // thrown near-vertically with NO gravity, decelerated by drag - the splash's water
        // columns. The stretch itself lives on the renderer (builder): lengthScale 4.
        const int StreakBurstMinCount = 3;            // KWS bursts 4-6 regardless of strength
        const int StreakBurstMaxCount = 6;
        const float StreakUpSpeedMin = 0.6f;          // vertical throw at threshold strength...
        const float StreakUpSpeedMax = 2.0f;          // ...and at full strength (KWS 0.5-2)
        const float StreakConeTangent = 0.18f;        // horizontal/vertical ratio (KWS 10 deg cone)
        const float StreakSizeJitterMin = 0.7f;       // per-sprite size, relative to crown base
        const float StreakSizeJitterMax = 1.0f;''',
'revert streak consts')

t = rep(t, '''                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * StreakSpawnJitterScale);
                float up = Mathf.Lerp(StreakUpSpeedMin, StreakUpSpeedMax, strength)''',
'''                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * SpawnRingRadiusScale);
                float up = Mathf.Lerp(StreakUpSpeedMin, StreakUpSpeedMax, strength)''',
'revert streak spawn jitter')

t = rep(t, '''                ep.startLifetime = crownLifetime
                                   * Random.Range(StreakLifetimeFactorMin, StreakLifetimeFactorMax);
                ep.startColor = streakColor;''',
'''                ep.startLifetime = crownLifetime
                                   * Random.Range(CrownLifetimeJitterMin, CrownLifetimeJitterMax);
                ep.startColor = streakColor;''',
'revert streak lifetime')

t = rep(t, '''            main.gravityModifier = StreakGravityModifier; // spent columns sink back down''',
'''            main.gravityModifier = 0f;   // KWS layer A: the columns hang, drag bleeds them out''',
'revert streak gravity')

# ---- undo v2 on emitter ----
t = rep(t, '''// with three children - "Droplet Spray (CPU Fallback)" (Shuriken droplets, only
// bursts on bodies WITHOUT an active GPU WaterFoamParticles), "Crown Ring" (a cloud
// of photographic chunk sprites, always plays) and "Streak Jets" (a few velocity-
// stretched chunk sprites, the splash's vertical water columns). Swap the droplet texture on the fallback's''',
'''// with two children - "Droplet Spray (CPU Fallback)" (Shuriken droplets, only
// bursts on bodies WITHOUT an active GPU WaterFoamParticles) and "Crown Ring"
// (a cloud of photographic chunk sprites, always plays). Swap the droplet texture on the fallback's''',
'revert emitter head comment')

t = rep(t, '''        const float CrownLifetimeJitterMin = 0.75f;   // per-sprite life spread (KWS 0.75..1.25)
        const float CrownLifetimeJitterMax = 1.25f;

        // ---- streak-jet defaults (ConfigureStreaks / EmitStreaks) ----
        // KWS WaterSplashes.prefab layer A: a handful of velocity-stretched chunk sprites
        // thrown near-vertically with NO gravity, decelerated by drag - the splash's water
        // columns. The stretch itself lives on the renderer (builder): lengthScale 4.
        const int StreakBurstMinCount = 3;            // KWS bursts 4-6 regardless of strength
        const int StreakBurstMaxCount = 6;
        const float StreakUpSpeedMin = 0.6f;          // vertical throw at threshold strength...
        const float StreakUpSpeedMax = 2.0f;          // ...and at full strength (KWS 0.5-2)
        const float StreakConeTangent = 0.18f;        // horizontal/vertical ratio (KWS 10 deg cone)
        const float StreakSizeJitterMin = 0.7f;       // per-sprite size, relative to crown base
        const float StreakSizeJitterMax = 1.0f;''',
'''        const float CrownLifetimeJitterMin = 0.75f;   // per-sprite life spread (KWS 0.75..1.25)
        const float CrownLifetimeJitterMax = 1.25f;''',
'revert emitter streak consts')

t = rep(t, '''        [Tooltip("Crown opacity multiplier on top of the tint's alpha.")]
        [Range(0f, 1f)] [SerializeField] internal float crownOpacity = 1f;

        [Header("Streak jets")]
        [Tooltip("Optional velocity-stretched chunk sprites thrown with the crown (the " +
                 "splash's vertical water columns). Leave empty to disable.")]
        [SerializeField] internal ParticleSystem streakParticles;''',
'''        [Tooltip("Crown opacity multiplier on top of the tint's alpha.")]
        [Range(0f, 1f)] [SerializeField] internal float crownOpacity = 1f;''',
'revert emitter streak field')

t = rep(t, '''        ParticleSystemRenderer _dropletRenderer; // lazy cache: the fallback droplets' renderer
        ParticleSystemRenderer _crownRenderer;   // lazy cache: the crown ring's renderer
        ParticleSystemRenderer _streakRenderer;  // lazy cache: the streak jets' renderer''',
'''        ParticleSystemRenderer _dropletRenderer; // lazy cache: the fallback droplets' renderer
        ParticleSystemRenderer _crownRenderer;   // lazy cache: the crown ring's renderer''',
'revert emitter renderer cache')

t = rep(t, '''            if (crownParticles != null)
            {
                if (_crownRenderer == null)
                    _crownRenderer = crownParticles.GetComponent<ParticleSystemRenderer>();
                if (_crownRenderer != null) _crownRenderer.forceRenderingOff = reroute;
            }
            if (streakParticles != null)
            {
                if (_streakRenderer == null)
                    _streakRenderer = streakParticles.GetComponent<ParticleSystemRenderer>();
                if (_streakRenderer != null) _streakRenderer.forceRenderingOff = reroute;
            }
        }''',
'''            if (crownParticles != null)
            {
                if (_crownRenderer == null)
                    _crownRenderer = crownParticles.GetComponent<ParticleSystemRenderer>();
                if (_crownRenderer != null) _crownRenderer.forceRenderingOff = reroute;
            }
        }''',
'revert emitter reroute')

t = rep(t, '''            if (_crownRenderer != null && crownParticles != null && crownParticles.particleCount > 0
                && _crownRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_crownRenderer, _crownRenderer.sharedMaterial, 0, 0);
            if (_streakRenderer != null && streakParticles != null && streakParticles.particleCount > 0
                && _streakRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_streakRenderer, _streakRenderer.sharedMaterial, 0, 0);
        }''',
'''            if (_crownRenderer != null && crownParticles != null && crownParticles.particleCount > 0
                && _crownRenderer.sharedMaterial != null)
                cmd.DrawRenderer(_crownRenderer, _crownRenderer.sharedMaterial, 0, 0);
        }''',
'revert emitter DrawAfterFog')

t = rep(t, '''                if (allowCrown)
                {
                    EmitCrown(surfacePos, strength, radius);
                    EmitStreaks(surfacePos, strength, radius);
                }
                return;''',
'''                if (allowCrown) EmitCrown(surfacePos, strength, radius);
                return;''',
'revert emitter GPU-path call site')

t = rep(t, '''            if (allowCrown)
            {
                EmitCrown(surfacePos, strength, radius);
                EmitStreaks(surfacePos, strength, radius);
            }
        }''',
'''            if (allowCrown) EmitCrown(surfacePos, strength, radius);
        }''',
'revert emitter fallback-path call site')

t = rep(t, '''                ep.startColor = crownColor;
                crownParticles.Emit(ep, 1);
            }
        }

        // The vertical-throw companion to the chunk cloud: a few stretched chunk sprites
        // fired near-vertically with no gravity (KWS WaterSplashes.prefab layer A). Shares
        // the crown's material, atlas, strength gate and tint; the builder owns the
        // stretched-renderer setup (ConfigureStreaks + lengthScale on the renderer).
        void EmitStreaks(Vector3 surfacePos, float strength, float radius)
        {
            if (streakParticles == null || strength < crownMinStrength) return;

            int count = Mathf.RoundToInt(
                Mathf.Lerp(StreakBurstMinCount, StreakBurstMaxCount, strength));
            float baseSize = crownBaseSize * Mathf.Lerp(CrownMinSizeFactor, CrownMaxSizeFactor, strength)
                           + radius * CrownRadiusContribution;
            Color streakColor = crownTint;
            streakColor.a *= crownOpacity;

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * SpawnRingRadiusScale);
                float up = Mathf.Lerp(StreakUpSpeedMin, StreakUpSpeedMax, strength)
                           * Random.Range(UpwardJitterMin, UpwardJitterMax);
                Vector2 cone = Random.insideUnitCircle * (up * StreakConeTangent);
                ep.velocity = new Vector3(cone.x, up, cone.y);
                ep.startSize = baseSize * Random.Range(StreakSizeJitterMin, StreakSizeJitterMax);
                ep.startLifetime = crownLifetime
                                   * Random.Range(CrownLifetimeJitterMin, CrownLifetimeJitterMax);
                ep.startColor = streakColor;
                streakParticles.Emit(ep, 1);
            }
        }''',
'''                ep.startColor = crownColor;
                crownParticles.Emit(ep, 1);
            }
        }''',
'revert emitter EmitStreaks')

t = rep(t, '''        // The chunk pop-then-grow size curve: (0,0) -> (PopTime, PopFraction) -> (1,1).
        static AnimationCurve PopCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(CrownPopTime, CrownPopFraction),
                new Keyframe(1f, 1f));
            return curve;
        }

        /// <summary>Configure a particle system as the streak-jet layer: no gravity, air
        /// drag, pop-then-grow size, and the chunk atlas stepped once over each particle's
        /// lifetime. The caller assigns the (shared crown) material, matching tile counts,
        /// and the stretched-billboard renderer settings.</summary>
        public static void ConfigureStreaks(ParticleSystem ps, int tilesX, int tilesY)
        {
            if (ps == null) throw new System.ArgumentNullException(nameof(ps));
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;   // KWS layer A: the columns hang, drag bleeds them out
            main.startSpeed = 0f;
            main.startLifetime = CrownStartLifetime;
            main.startSize = CrownStartSize;
            main.startColor = CrownStartColor;
            main.maxParticles = CrownMaxParticles;
            main.playOnAwake = true;

            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // drag bleeds the throw out near apex instead of gravity pulling it back
            var velocityLimit = ps.limitVelocityOverLifetime;
            velocityLimit.enabled = true;
            velocityLimit.dampen = CrownVelocityDampen;
            velocityLimit.multiplyDragByParticleSize = false;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, PopCurve());

            // step through the chunk atlas exactly once across each particle's life
            var sheetAnimation = ps.textureSheetAnimation;
            sheetAnimation.enabled = true;
            sheetAnimation.mode = ParticleSystemAnimationMode.Grid;
            sheetAnimation.numTilesX = tilesX;
            sheetAnimation.numTilesY = tilesY;
            sheetAnimation.animation = ParticleSystemAnimationType.WholeSheet;
            sheetAnimation.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            sheetAnimation.cycleCount = 1;
            sheetAnimation.startFrame = 0f;
            sheetAnimation.frameOverTime = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));

            // Linear alpha 1 -> 0: the same erosion clock the crown chunks run on.
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(0f);

            ps.Play();
        }''',
'''        // The chunk pop-then-grow size curve: (0,0) -> (PopTime, PopFraction) -> (1,1).
        static AnimationCurve PopCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(CrownPopTime, CrownPopFraction),
                new Keyframe(1f, 1f));
            return curve;
        }''',
'revert emitter ConfigureStreaks')

save('emitter', path, t, crlf)

path, t, crlf = load('scenerig')
t = rep(t, '''        // Hierarchy names for the splash feature: ONE root GO holding the emitter, with
        // its particle systems as clearly-labelled children (the old flat siblings
        // "Splash Particles"/"Splash Crown" read as two unrelated features).
        internal const string SplashRootName = "Water Splash FX";
        internal const string SplashDropletChildName = "Droplet Spray (CPU Fallback)";
        internal const string SplashCrownChildName = "Crown Ring";
        internal const string SplashStreakChildName = "Streak Jets";
        // KWS WaterSplashes.prefab stretched-billboard value: the sprite elongates along
        // its throw into a water column.
        const float StreakRendererLengthScale = 4f;''',
'''        // Hierarchy names for the splash feature: ONE root GO holding the emitter, with
        // both particle systems as clearly-labelled children (the old flat siblings
        // "Splash Particles"/"Splash Crown" read as two unrelated features).
        internal const string SplashRootName = "Water Splash FX";
        internal const string SplashDropletChildName = "Droplet Spray (CPU Fallback)";
        internal const string SplashCrownChildName = "Crown Ring";''',
'revert scenerig child name + const')

t = rep(t, '''        // One root so the hierarchy reads as a single feature: the emitter on the root
        // drives all children. "Droplet Spray" is the CPU fallback - bodies with an
        // active GPU WaterFoamParticles route droplets there instead, so it only bursts
        // on non-GPU bodies. "Crown Ring" and "Streak Jets" always play on both paths.''',
'''        // One root so the hierarchy reads as a single feature: the emitter on the root
        // drives both children. "Droplet Spray" is the CPU fallback - bodies with an
        // active GPU WaterFoamParticles route droplets there instead, so it only bursts
        // on non-GPU bodies. "Crown Ring" always plays on both paths.''',
'revert scenerig create doc')

t = rep(t, '''            crownPSR.sharedMaterial = CreateOrUpgradeCrownMaterial();
            splashEmitter.crownParticles = crownPS;

            var streakGO = NewUndoableGameObject(SplashStreakChildName);
            streakGO.transform.SetParent(rootGO.transform);
            var streakPS = streakGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureStreaks(streakPS, CrownSheetCols, CrownSheetRows);
            var streakPSR = streakGO.GetComponent<ParticleSystemRenderer>();
            // Velocity-stretched chunk sprites (KWS layer A). The crown's chunk material is
            // shared, so the streaks erode with the same look and upgrade in one place.
            streakPSR.renderMode = ParticleSystemRenderMode.Stretch;
            streakPSR.lengthScale = StreakRendererLengthScale;
            streakPSR.velocityScale = 0f;
            streakPSR.cameraVelocityScale = 0f;
            streakPSR.sharedMaterial = crownPSR.sharedMaterial;
            splashEmitter.streakParticles = streakPS;
            return splashEmitter;''',
'''            crownPSR.sharedMaterial = CreateOrUpgradeCrownMaterial();
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;''',
'revert scenerig streak child')
save('scenerig', path, t, crlf)

path, t, crlf = load('editor')
t = rep(t, '''        SerializedProperty _crownTint, _crownOpacity;
        SerializedProperty _streakParticles;''',
'''        SerializedProperty _crownTint, _crownOpacity;''',
'revert editor decl')

t = rep(t, '''            _crownOpacity = serializedObject.FindProperty("crownOpacity");
            _streakParticles = serializedObject.FindProperty("streakParticles");''',
'''            _crownOpacity = serializedObject.FindProperty("crownOpacity");''',
'revert editor find')

t = rep(t, '''            EditorGUILayout.PropertyField(_crownParticles,
                new GUIContent("Crown System", "Chunk-cloud crown system. Leave empty to disable the crown."));
            EditorGUILayout.PropertyField(_streakParticles,
                new GUIContent("Streak System",
                    "Velocity-stretched chunk jets thrown with the crown. Leave empty to disable."));''',
'''            EditorGUILayout.PropertyField(_crownParticles,
                new GUIContent("Crown System", "Flipbook crown system. Leave empty to disable the crown."));''',
'revert editor wiring field')
save('editor', path, t, crlf)

print('STREAK REVERT COMPLETE - all three files byte-identical to increment-1 state')
