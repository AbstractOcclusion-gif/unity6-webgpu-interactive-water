#!/usr/bin/env python3
"""Chunk-cloud wiring patch (2026-08-06). md5-gated exact-string patcher.
Converts the crown flipbook card into the KWS photographic chunk cloud.
Run from the repo root (mounted): python3 patch_chunkcloud_v1.py
"""
import hashlib, sys, os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
PKG = os.path.join(ROOT, 'Packages', 'com.abstractocclusion.webgpuwater')

FILES = {
    'buildkit': (os.path.join(PKG, 'Editor', 'WaterBuildKit.cs'),
                 'b7985e42e5d015faa93f80065b9b746b'),
    'scenerig': (os.path.join(PKG, 'Editor', 'WaterBuildKit.SceneRig.cs'),
                 'f04944e3e9c391513931e033b991e9a3'),
    'emitter':  (os.path.join(PKG, 'Runtime', 'WaterSplashEmitter.cs'),
                 'b68b4241c5a51a7fddc854a3b63af4a1'),
}

def load(key):
    path, want = FILES[key]
    raw = open(path, 'rb').read()
    got = hashlib.md5(raw).hexdigest()
    if got != want:
        sys.exit(f'MD5 MISMATCH {key}: {got} != {want} - ABORT, re-scope first')
    crlf = b'\r\n' in raw
    text = raw.decode('utf-8').replace('\r\n', '\n')
    return path, text, crlf

def save(path, text, crlf):
    data = text.replace('\n', '\r\n') if crlf else text
    open(path, 'wb').write(data.encode('utf-8'))

def rep(text, old, new, where):
    n = text.count(old)
    if n != 1:
        sys.exit(f'ANCHOR FAIL ({where}): {n} matches for:\n{old[:120]}...')
    return text.replace(old, new)

# ============================ 1. WaterBuildKit.cs ============================
path, t, crlf = load('buildkit')

t = rep(t, '''        internal const string SplashCrownSheetPath = Gen + "/SplashFlipbook_8x8.png";
        internal const string SplashCrownLightSheetAPath = Gen + "/SplashFlipbookLightA_8x8.png";
        internal const string SplashCrownLightSheetBPath = Gen + "/SplashFlipbookLightB_8x8.png";''',
'''        internal const string SplashCrownSheetPath = Gen + "/WaterSplashChunks_4x1.png";''',
'buildkit sheet paths')

t = rep(t, '''        // The crown flipbook (and its six-way light sheets) ship inside the package's Samples~
        // folder, which Unity never imports. These are their paths RELATIVE to the resolved
        // package root; the wizard copies them out to the Gen paths above on first build (see
        // LoadOrProvisionPackagedSheet) so the crown is textured even in projects that never
        // imported the demo samples.
        const string CrownSheetPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbook_8x8.png";
        const string CrownLightSheetAPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbookLightA_8x8.png";
        const string CrownLightSheetBPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/SplashFlipbookLightB_8x8.png";
        // Crown material upgrades applied when the six-way light sheets are provisioned:
        // directional flipbook lighting on, plus a default backlit-transmission glow.
        const string SixWayProperty = "_SixWay";
        const string LightSheetAProperty = "_LightSheetA";
        const string LightSheetBProperty = "_LightSheetB";
        const string TransmissionStrengthProperty = "_TransmissionStrength";
        const float DefaultCrownTransmission = 1.0f;''',
'''        // The chunk atlas ships inside the package's Samples~ folder, which Unity never
        // imports. This is its path RELATIVE to the resolved package root; the wizard copies
        // it out to the Gen path above on first build (see LoadOrProvisionPackagedSheet) so
        // the splash is textured even in projects that never imported the demo samples.
        const string CrownSheetPackageRelativePath =
            "Samples~/Demos/Common/Assets/Textures/WaterSplashChunks_4x1.png";
        // The chunk atlas has no baked six-way light sheets (those belonged to the old 8x8
        // procedural flipbook), so the upgrade switches the crown material to the scalar
        // foam lighting. Backlit transmission stays: it reads the atlas' thickness channel.
        const string SixWayProperty = "_SixWay";
        const string TransmissionStrengthProperty = "_TransmissionStrength";
        const float DefaultCrownTransmission = 1.0f;''',
'buildkit relative paths + six-way consts')

t = rep(t, '''        // Crown splash flipbook grid; must match the SplashFlipbook_8x8 sheet layout.
        const int CrownSheetCols = 8;
        const int CrownSheetRows = 8;''',
'''        // Splash chunk grid; must match the WaterSplashChunks_4x1 atlas layout: four packed
        // 512px photographic chunks side by side (the KWS WaterSplash construction). Each
        // sprite steps through all four chunks once over its life.
        const int CrownSheetCols = 4;
        const int CrownSheetRows = 1;''',
'buildkit grid consts')

save(path, t, crlf)
print('patched WaterBuildKit.cs')

# ======================== 2. WaterBuildKit.SceneRig.cs =======================
path, t, crlf = load('scenerig')

t = rep(t, '''            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);''',
'''            // Free-rotating billboards: each chunk sprite spawns at a random angle and
            // tumbles (KWS droplet cloud). The old vertical-billboard bottom pivot belonged
            // to the single crown card this system used to be.
            crownPSR.renderMode = ParticleSystemRenderMode.Billboard;
            crownPSR.pivot = Vector3.zero;''',
'scenerig renderer mode')

t = rep(t, '''        // The crown material: packed flipbook + the six-way light sheets and backlit
        // transmission (both baked by gen_splash_flipbook.py alongside the main sheet).
        // Doubles as the one-click upgrade for crown materials created before six-way
        // lighting existed. Missing light sheets (older package payloads) degrade
        // gracefully: the material stays on the scalar foam lighting.
        static Material CreateOrUpgradeCrownMaterial()
        {
            var material = LoadOrCreateSplashMaterial(SplashCrownMaterialPath,
                LoadOrProvisionPackagedSheet(SplashCrownSheetPath, CrownSheetPackageRelativePath));
            if (material == null) return null;

            var lightSheetA = LoadOrProvisionPackagedSheet(
                SplashCrownLightSheetAPath, CrownLightSheetAPackageRelativePath);
            var lightSheetB = LoadOrProvisionPackagedSheet(
                SplashCrownLightSheetBPath, CrownLightSheetBPackageRelativePath);
            bool sixWayReady = lightSheetA != null && lightSheetB != null
                && material.HasProperty(SixWayProperty);
            if (!sixWayReady) return material;

            material.SetTexture(LightSheetAProperty, lightSheetA);
            material.SetTexture(LightSheetBProperty, lightSheetB);
            material.SetFloat(SixWayProperty, 1f);
            if (material.HasProperty(TransmissionStrengthProperty) &&
                Mathf.Approximately(material.GetFloat(TransmissionStrengthProperty), 0f))
            {
                material.SetFloat(TransmissionStrengthProperty, DefaultCrownTransmission);
            }
            EditorUtility.SetDirty(material);
            return material;
        }''',
'''        // The crown material: the packed photographic chunk atlas (KWS WaterSplash
        // construction) + backlit transmission, which reads the atlas' thickness channel.
        // Doubles as the one-click upgrade for crown materials created on the old 8x8
        // procedural flipbook: the texture is swapped and six-way lighting is switched
        // OFF, because the baked light sheets match the old flipbook's frames, not the
        // chunk atlas - relighting chunks with them would shade garbage.
        static Material CreateOrUpgradeCrownMaterial()
        {
            var material = LoadOrCreateSplashMaterial(SplashCrownMaterialPath,
                LoadOrProvisionPackagedSheet(SplashCrownSheetPath, CrownSheetPackageRelativePath));
            if (material == null) return null;

            if (material.HasProperty(SixWayProperty))
                material.SetFloat(SixWayProperty, 0f);
            if (material.HasProperty(TransmissionStrengthProperty) &&
                Mathf.Approximately(material.GetFloat(TransmissionStrengthProperty), 0f))
            {
                material.SetFloat(TransmissionStrengthProperty, DefaultCrownTransmission);
            }
            EditorUtility.SetDirty(material);
            return material;
        }''',
'scenerig crown material')

save(path, t, crlf)
print('patched WaterBuildKit.SceneRig.cs')

# ========================= 3. WaterSplashEmitter.cs ==========================
path, t, crlf = load('emitter')

t = rep(t, '''// with two children - "Droplet Spray (CPU Fallback)" (Shuriken droplets, only
// bursts on bodies WITHOUT an active GPU WaterFoamParticles) and "Crown Ring"
// (flipbook crown, always plays). Swap the droplet texture on the fallback's''',
'''// with two children - "Droplet Spray (CPU Fallback)" (Shuriken droplets, only
// bursts on bodies WITHOUT an active GPU WaterFoamParticles) and "Crown Ring"
// (a cloud of photographic chunk sprites, always plays). Swap the droplet texture on the fallback's''',
'emitter head comment')

t = rep(t, '''        // ---- crown particle-system defaults (ConfigureCrown) ----
        const float CrownStartLifetime = 0.5f;
        const float CrownStartSize = 0.4f;
        static readonly Color CrownStartColor = new Color(0.95f, 0.98f, 1.0f, 1.0f);
        const int CrownMaxParticles = 64;
        const float CrownFadeStartFraction = 0.7f;    // flipbook tail softening''',
'''        // ---- crown particle-system defaults (ConfigureCrown) ----
        // The crown is a CLOUD of photographic chunk sprites (KWS WaterSplashes.prefab
        // droplet layer), not a single flipbook card. Gravity/drag/tumble are the measured
        // KWS prefab values (docs/RESEARCH_kws_splash_definition_2026-08-06.md section 3).
        const float CrownStartLifetime = 0.5f;
        const float CrownStartSize = 0.4f;
        static readonly Color CrownStartColor = new Color(0.95f, 0.98f, 1.0f, 1.0f);
        const int CrownMaxParticles = 256;            // bursts are sprite CLOUDS now, not 1 card
        const float CrownGravityModifier = 1.2f;      // chunks arc over and fall (KWS 1..1.5)
        const float CrownVelocityDampen = 0.03f;      // air drag (KWS LimitVelocity dampen)
        const float CrownTumbleMaxDegrees = 30f;      // slow random spin, +/- deg per second
        // Size-over-lifetime pop: a chunk reaches CrownPopFraction of its size within
        // CrownPopTime of its life, then grows linearly to full size (KWS pop shape).
        const float CrownPopTime = 0.04f;
        const float CrownPopFraction = 0.5f;

        // ---- chunk-cloud burst shaping (EmitCrown) ----
        const int CrownBurstMinCount = 6;             // a threshold hit still reads as a cloud
        const int CrownBurstMaxCount = 16;            // full-strength slam
        const float CrownUpSpeedMin = 0.5f;           // vertical throw at threshold strength...
        const float CrownUpSpeedMax = 2.0f;           // ...and at full strength
        const float CrownOutSpeedMax = 0.8f;          // horizontal scatter at full strength
        const float CrownSizeJitterMin = 0.5f;        // per-sprite size randomisation...
        const float CrownSizeJitterMax = 1.1f;
        const float CrownHeroSizePower = 10f;         // ...pow-shaped so a RARE sprite lands
        const float CrownHeroSizeBonus = 1.5f;        //    near hero size (KWS pow10 distro)
        const float CrownLifetimeJitterMin = 0.75f;   // per-sprite life spread (KWS 0.75..1.25)
        const float CrownLifetimeJitterMax = 1.25f;''',
'emitter crown consts')

t = rep(t, '''        // One flipbook crown splash at the impact, for strong-enough hits. The crown
        // is a separate particle system (set up by ConfigureCrown), so the drifting
        // droplets above are unaffected.
        void EmitCrown(Vector3 surfacePos, float strength, float radius)
        {
            if (crownParticles == null || strength < crownMinStrength) return;

            var ep = new ParticleSystem.EmitParams();
            ep.position = surfacePos;
            ep.velocity = Vector3.zero;
            ep.startLifetime = crownLifetime;
            ep.startSize = crownBaseSize * Mathf.Lerp(CrownMinSizeFactor, CrownMaxSizeFactor, strength)
                         + radius * CrownRadiusContribution;
            // Per-particle start color (same channel the droplets already use for their
            // velocity-proportional alpha) - the profile can retint the crown without
            // touching the shared material asset.
            Color crownColor = crownTint;
            crownColor.a *= crownOpacity;
            ep.startColor = crownColor;
            crownParticles.Emit(ep, 1);
        }''',
'''        // One chunk-cloud burst at the impact, for strong-enough hits. Overlapping
        // photographic chunk sprites, each eroding on its own clock, are what reads as
        // a defined splash (the KWS WaterSplashes.prefab construction). The crown is a
        // separate particle system (ConfigureCrown), so the drifting droplets above are
        // unaffected.
        void EmitCrown(Vector3 surfacePos, float strength, float radius)
        {
            if (crownParticles == null || strength < crownMinStrength) return;

            int count = Mathf.RoundToInt(
                Mathf.Lerp(CrownBurstMinCount, CrownBurstMaxCount, strength));
            float baseSize = crownBaseSize * Mathf.Lerp(CrownMinSizeFactor, CrownMaxSizeFactor, strength)
                           + radius * CrownRadiusContribution;
            // Per-particle start color (same channel the droplets already use for their
            // velocity-proportional alpha) - the profile can retint the crown without
            // touching the shared material asset.
            Color crownColor = crownTint;
            crownColor.a *= crownOpacity;

            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector2 ring = Random.insideUnitCircle;
                ep.position = surfacePos + new Vector3(ring.x, 0f, ring.y)
                              * (radius * SpawnRingRadiusScale);
                float up = Mathf.Lerp(CrownUpSpeedMin, CrownUpSpeedMax, strength)
                           * Random.Range(UpwardJitterMin, UpwardJitterMax);
                ep.velocity = new Vector3(ring.x * CrownOutSpeedMax * strength, up,
                                          ring.y * CrownOutSpeedMax * strength);
                // pow-shaped size distribution: most sprites modest, a rare one near hero size
                float hero = Mathf.Pow(Random.value, CrownHeroSizePower) * CrownHeroSizeBonus;
                ep.startSize = baseSize * (Random.Range(CrownSizeJitterMin, CrownSizeJitterMax) + hero);
                ep.rotation = Random.Range(0f, 360f);
                ep.startLifetime = crownLifetime
                                   * Random.Range(CrownLifetimeJitterMin, CrownLifetimeJitterMax);
                ep.startColor = crownColor;
                crownParticles.Emit(ep, 1);
            }
        }''',
'emitter EmitCrown')

t = rep(t, '''            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;     // the crown stays put on the surface
            main.startSpeed = 0f;''',
'''            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = CrownGravityModifier; // chunks arc over and fall
            main.startSpeed = 0f;''',
'emitter ConfigureCrown gravity')

t = rep(t, '''            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // play the whole sprite sheet exactly once across each particle's life
            var sheetAnimation = ps.textureSheetAnimation;''',
'''            var emission = ps.emission; emission.enabled = false; // manual Emit only
            var shape = ps.shape; shape.enabled = false;

            // air drag so thrown chunks decelerate and arc instead of flying ballistic
            var velocityLimit = ps.limitVelocityOverLifetime;
            velocityLimit.enabled = true;
            velocityLimit.dampen = CrownVelocityDampen;
            velocityLimit.multiplyDragByParticleSize = false;

            // slow random tumble, half the sprites spinning each way
            var rotationOverLifetime = ps.rotationOverLifetime;
            rotationOverLifetime.enabled = true;
            rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(
                -CrownTumbleMaxDegrees * Mathf.Deg2Rad, CrownTumbleMaxDegrees * Mathf.Deg2Rad);

            // pop to CrownPopFraction almost immediately, then grow to full size
            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, PopCurve());

            // step through the chunk atlas exactly once across each particle's life
            var sheetAnimation = ps.textureSheetAnimation;''',
'emitter ConfigureCrown modules')

t = rep(t, '''            // soften the tail so the splash dissolves instead of cutting off
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(CrownFadeStartFraction);

            ps.Play();
        }''',
'''            // Linear alpha 1 -> 0 across the WHOLE life: this is the erosion clock.
            // SplashParticles.shader burns the sprite through its noise channel as this
            // alpha falls, so the chunk disintegrates instead of ghost-fading (KWS).
            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = FadeTailGradient(0f);

            ps.Play();
        }

        // The chunk pop-then-grow size curve: (0,0) -> (PopTime, PopFraction) -> (1,1).
        static AnimationCurve PopCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(CrownPopTime, CrownPopFraction),
                new Keyframe(1f, 1f));
            return curve;
        }''',
'emitter ConfigureCrown fade + PopCurve')

t = rep(t, '''            // Stretched billboards: fast droplets read as streaks along their motion (KWS
            // splash look); settled drifters are slow, so they stay effectively round.
            // The crown system is left as plain billboards - its flipbook is directional.''',
'''            // Stretched billboards: fast droplets read as streaks along their motion (KWS
            // splash look); settled drifters are slow, so they stay effectively round.
            // The crown system stays on plain billboards - its chunk sprites tumble instead.''',
'emitter drift renderer comment')

t = rep(t, '''        /// airborne droplet shares the KIND_SPRAY tech + look); the Shuriken system here then
        /// only plays the crown flipbook. Bodies without a GPU system keep the legacy''',
'''        /// airborne droplet shares the KIND_SPRAY tech + look); the Shuriken system here then
        /// only throws the crown chunk cloud. Bodies without a GPU system keep the legacy''',
'emitter EmitSplash doc')

t = rep(t, '''        /// <param name="allowCrown">False suppresses the crown flipbook for THIS emit - a continuous
        /// stream plays the crown on its first emit only. Droplets are unaffected.</param>''',
'''        /// <param name="allowCrown">False suppresses the crown chunk cloud for THIS emit - a
        /// continuous stream plays the crown on its first emit only. Droplets are unaffected.</param>''',
'emitter allowCrown doc')

t = rep(t, '''        [Header("Crown splash (flipbook)")]
        [Tooltip("Optional flipbook splash emitted at the impact point. Leave empty to disable.")]''',
'''        [Header("Crown splash (chunk cloud)")]
        [Tooltip("Optional chunk-sprite cloud emitted at the impact point. Leave empty to disable.")]''',
'emitter header attr')

t = rep(t, '''        [Tooltip("Crown lifetime; the flipbook plays through once over this time.")]''',
'''        [Tooltip("Crown lifetime; each sprite steps through the chunk atlas once over this time.")]''',
'emitter lifetime tooltip')

t = rep(t, '''        /// <summary>Configure a particle system to play a splash flipbook once over each
        /// particle's lifetime (used by the scene builder for the crown splash). The
        /// caller assigns the sprite-sheet material and matching tile counts.</summary>''',
'''        /// <summary>Configure a particle system as the splash chunk cloud: gravity, drag,
        /// tumble, pop-then-grow size, and the chunk atlas stepped once over each
        /// particle's lifetime (used by the scene builder for the crown splash). The
        /// caller assigns the sprite-sheet material and matching tile counts.</summary>''',
'emitter ConfigureCrown doc')

save(path, t, crlf)
print('patched WaterSplashEmitter.cs')
print('ALL PATCHES APPLIED')
