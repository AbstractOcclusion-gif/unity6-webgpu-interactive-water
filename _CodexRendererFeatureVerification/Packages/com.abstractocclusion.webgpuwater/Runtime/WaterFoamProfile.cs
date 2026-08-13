// WebGpuWater - ONE master foam profile (the "foam -> one master" decision).
//
// A body's whole foam story in one asset: a shared look block (tint, sprite atlas,
// flipbook, hero-size bias, opacity) plus one section per foam element (ambient
// foam/spray, screen-space veil, splash, bubbles). Every foam component takes an
// OPTIONAL profile reference:
//   - null profile          -> the component behaves exactly as before (zero migration);
//   - section 'drive' off   -> that section keeps the component's own inspector values;
//   - section 'drive' on    -> the profile's values are copied onto the component on
//                              enable/validate, so ONE asset retunes the whole body.
// The shared look is additionally pushed over the materials at draw time via the
// MaterialPropertyBlock - material assets are never written, which ends the "four
// divergent copies of FoamParticles.mat" class of drift.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [CreateAssetMenu(fileName = "WaterFoamProfile",
                     menuName = "AbstractOcclusion/WebGpuWater/Water Foam Profile")]
    public sealed class WaterFoamProfile : ScriptableObject
    {
        // Shader property ids for the look/veil overrides (same names on FoamParticles
        // and FoamDensityComposite).
        static readonly int ID_Tint = Shader.PropertyToID("_Tint");
        static readonly int ID_ParticleOpacity = Shader.PropertyToID("_ParticleOpacity");
        static readonly int ID_ParticleTex = WaterShaderProps.ParticleTex;
        static readonly int ID_DensityLowGain = Shader.PropertyToID("_DensityLowGain");
        static readonly int ID_DensityHighGain = Shader.PropertyToID("_DensityHighGain");
        static readonly int ID_BreakupTex = WaterShaderProps.BreakupTex;
        static readonly int ID_BreakupTiling = Shader.PropertyToID("_BreakupTiling");
        static readonly int ID_BreakupStrength = Shader.PropertyToID("_BreakupStrength");

        [System.Serializable]
        public sealed class SharedLook
        {
            [Tooltip("Drive the shared look from this profile (tint/opacity/atlas pushed over " +
                     "the materials at draw time; flipbook + hero bias copied onto components).")]
            public bool drive = true;
            public Color tint = new Color(0.95f, 0.98f, 1f, 1f);
            [Range(0f, 1f)] public float opacity = 0.85f;
            [Tooltip("Sprite atlas for foam + roller quads. None = keep each material's own.")]
            public Texture2D particleAtlas;
            public Vector2Int flipbookGrid = new Vector2Int(2, 2);
            // Defaults MATCH WaterFoamParticles' own field defaults, so assigning a fresh
            // profile changes nothing until the user actually tweaks it (no silent drift).
            [Range(0f, 30f)] public float flipbookFps = 0f;
            [Range(1f, 6f)] public float sizeHeroPower = 1f;
        }

        [System.Serializable]
        public sealed class AmbientSection
        {
            [Tooltip("When enabled, this profile overwrites the matching WaterFoamParticles component values every frame.")]
            public bool drive = true;
            [Range(0f, 1f)] public float spawnThreshold = 0.25f;
            [Range(0f, 200f)] public float spawnRate = 30f;
            [Range(16, 4096)] public int maxSpawnPerFrame = 256;
            [Range(0f, 1f)] public float sprayChance = 0.15f;
            [Range(0f, 5f)] public float sprayLaunchSpeed = 0.6f;
            [Header("Ripple crest flecks")]
            [Tooltip("Emit small floating flecks from moving ripple crests.")]
            public bool rippleCrestFlecksEnabled;
            [Range(0f, 4f)] public float rippleCrestFleckAmount = 1f;
            [Range(16, 1024)] public int rippleCrestFleckMaxPerFrame = 256;
            public Vector2 rippleCrestFleckLifetimeRange = new Vector2(0.4f, 0.8f);
            public Vector2 rippleCrestFleckSizeRange = new Vector2(0.01f, 0.025f);
            [Range(0f, 1f)] public float rippleCrestFleckMotion = 0.6f;
            [Header("Layer opacity")]
            [Tooltip("Opacity of floating surface foam before the global Shared Look opacity is applied.")]
            [Range(0f, 1f)] public float surfaceFoamOpacity = 1f;
            [Tooltip("Opacity of airborne GPU droplets before the global Shared Look opacity is applied.")]
            [Range(0f, 1f)] public float sprayOpacity = 1f;
            public Vector2 lifeRange = new Vector2(1.5f, 4f);
            public Vector2 sizeRange = new Vector2(0.02f, 0.06f);
            [Range(0f, 400f)] public float spawnMaxDistance = 120f;
            [Tooltip("Airborne spray droplet lifetime range (seconds) - separate from foam lifeRange.")]
            public Vector2 sprayLifeRange = new Vector2(0.5f, 1.2f);
            [Tooltip("Airborne spray droplet size range (world half-size) - separate from foam sizeRange.")]
            public Vector2 spraySizeRange = new Vector2(0.02f, 0.05f);
            // Deposited foam (landed droplets). Defaults match the component - zero drift.
            [Tooltip("Lifetime range (seconds) of the foam patch a landed droplet deposits.")]
            public Vector2 depositLifeRange = new Vector2(0.5f, 1f);
            [Tooltip("World half-size range of the deposited foam patch.")]
            public Vector2 depositSizeRange = new Vector2(0.02f, 0.05f);
        }

        // Motion was the ONE WaterFoamParticles block the profile could not reach (gravity /
        // flow drift / wind drift / drag): a profile-driven body still needed a hand edit on
        // every component to retune how foam rides the water. It carries its OWN 'drive',
        // default OFF - unlike every other section - because existing profiles predate it:
        // ambient.drive is already ticked in the field, and folding motion under it would
        // stomp hand-tuned component values the moment those assets reapplied. Defaults
        // MATCH the component's field defaults, so ticking it changes nothing until tuned.
        [System.Serializable]
        public sealed class MotionSection
        {
            [Tooltip("Drive the Motion block on WaterFoamParticles (gravity, flow drift, wind " +
                     "drift, drag) from this profile. Off = the component keeps its own values.")]
            public bool drive;
            [Tooltip("Gravity on spray droplets (world units/sec^2).")]
            [Range(0f, 20f)] public float gravity = 1f;
            [Tooltip("Drift speed along the surface flow, per unit of surface slope (world units/sec).")]
            [Range(0f, 2f)] public float flowDrift = 0.25f;
            [Tooltip("Constant downwind drift of floating foam (world units/sec).")]
            [Range(0f, 0.5f)] public float windDriftSpeed = 0.02f;
            [Tooltip("How quickly foam velocity relaxes to the driven flow (1/sec).")]
            [Range(0f, 10f)] public float drag = 2f;
        }

        [System.Serializable]
        public sealed class VeilSection
        {
            [Tooltip("Drive the screen-space density veil's material values from this profile.")]
            public bool drive = true;
            [Tooltip("Live render-time size multiplier for landed screen-space foam.")]
            [Range(WaterFoamParticles.MinimumDensitySurfaceSizeScale,
                   WaterFoamParticles.MaximumDensitySurfaceSizeScale)]
            public float surfaceSizeScale = WaterFoamParticles.DefaultDensitySurfaceSizeScale;
            [Range(0f, 1f)] public float opacity = 0.5f;
            [Range(0f, 4f)] public float densityLowGain = 0.6f;
            [Range(0f, 1f)] public float densityHighGain = 0.15f;
            [Tooltip("World-tiled breakup lace pattern. None = keep the material's own.")]
            public Texture2D breakupTexture;
            [Range(0.5f, 20f)] public float breakupTiling = 4f;
            [Range(0f, 1f)] public float breakupStrength = 0.3f;
        }

        [System.Serializable]
        public sealed class SplashSection
        {
            [Tooltip("When enabled, this profile overwrites the matching WaterSplashEmitter component values. These controls shape impact and pump bursts, not ambient airborne droplets or ripple flecks.")]
            public bool drive = true;
            [Tooltip("Maximum number of ballistic impact/pump droplets requested by one burst. This does not control ripple flecks or foam-mask clumps.")]
            [Range(1, 128)] public int maxParticlesPerBurst = 48;
            [Tooltip("Upward velocity multiplier for ballistic impact/pump droplets.")]
            [Range(0f, 3f)] public float upwardBias = 1f;
            [Tooltip("Horizontal velocity multiplier for ballistic impact/pump droplets.")]
            [Range(0f, 3f)] public float outwardSpread = 1.3f;
            [Tooltip("World-space half-size of impact/pump droplets; approximate visible width is twice this value. These are ballistic droplets, not mist or ripple flecks.")]
            public float dropletSize = 0.02f;
            [Tooltip("Lifetime range in seconds of ballistic impact/pump droplets.")]
            public Vector2 lifetime = new Vector2(0.6f, 1.3f);
            [Header("Crown")]
            [Range(0f, 1f)] public float crownMinStrength = 0.25f;
            [Tooltip("Base world-space size of the splash crown emitted at a sufficiently strong impact.")]
            public float crownBaseSize = 0.4f;
            [Tooltip("Lifetime in seconds of the splash crown.")]
            public float crownLifetime = 0.5f;
            [Tooltip("Vertical launch multiplier for the crown cloud. Lower values keep it close to the surface.")]
            [Range(0f, 3f)] public float crownLaunchHeight = 1f;
            [Tooltip("Horizontal launch multiplier for the crown cloud. Lower values reduce projected spread.")]
            [Range(0f, 3f)] public float crownLaunchSpread = 1f;
            // Crown LOOK lives here too (it used to be unreachable from the profile: only
            // sizing was mirrored, so the profile tint silently ignored the crown).
            // Defaults match WaterSplashEmitter's crown defaults - zero drift on assign.
            [Tooltip("Crown flipbook tint, applied per emit as the particle start color.")]
            public Color crownTint = new Color(0.95f, 0.98f, 1f, 1f);
            [Range(0f, 1f)] public float crownOpacity = 1f;
            [Tooltip("Opacity of impact droplets before the global Shared Look opacity is applied. " +
                     "Controls both GPU-routed and CPU-fallback splash droplets; ambient sea spray is separate.")]
            [Range(0f, 1f)]
            [UnityEngine.Serialization.FormerlySerializedAs("cpuFallbackOpacity")]
            public float dropletOpacity = 1f;
            [Header("Entry streaks")]
            [Tooltip("Enable narrow ballistic water-entry streaks emitted before the crown. These are neither mist nor ripple flecks.")]
            public bool entryStreaksEnabled = true;
            [Tooltip("Multiplier for the number of entry streaks emitted by an impact.")]
            [Range(0f, 2f)] public float entryStreakAmount = 1f;
            [Tooltip("Vertical size multiplier for entry streaks.")]
            [Range(0f, 3f)] public float entryStreakHeight = 1f;
            [Tooltip("Width multiplier for entry streaks.")]
            [Range(0.1f, 3f)] public float entryStreakWidth = 1f;
            [Range(0f, 2f)] public float entryStreakGravity = 1f;
            [Tooltip("Opacity of entry streaks before the global Shared Look opacity is applied.")]
            [Range(0f, 1f)] public float entryStreakOpacity = 1f;
            [Range(0f, 1f)] public float entryStreakMinStrength = 0.2f;
            [Tooltip("Lifetime range in seconds of water-entry streaks.")]
            public Vector2 entryStreakLifetimeRange = new Vector2(0.75f, 1.5f);
            [Tooltip("Base world-space size range of entry streak sprites before the Height and Width multipliers.")]
            public Vector2 entryStreakSizeRange = new Vector2(0.35f, 0.5f);
            public Color entryStreakTint = new Color(0.95f, 0.98f, 1f, 1f);
        }

        [System.Serializable]
        public sealed class BubbleSection
        {
            [Tooltip("Drive the bubble-plume fields on WaterFoamParticles from this profile.")]
            public bool drive = true;
            // Defaults MATCH WaterFoamParticles' own field defaults - zero drift on assign.
            [Tooltip("Bubbles injected DOWNWARD per droplet a splash burst throws (0 = none).")]
            [Range(0f, 1f)] public float bubbleAmount = 0.5f;
            [Tooltip("Terminal rise speed of the LARGEST bubbles (world units/sec).")]
            [Range(0.05f, 0.6f)] public float bubbleRiseSpeed = 0.25f;
            [Tooltip("Bubble lifetime range (seconds); surfacing pops one first.")]
            public Vector2 bubbleLifeRange = new Vector2(2f, 4f);
            [Tooltip("Bubble sprite half-size range (world units), skewed small on spawn.")]
            public Vector2 bubbleSizeRange = new Vector2(0.015f, 0.05f);
            [Tooltip("Sideways wobble while rising; amplitude scales with bubble size.")]
            [Range(0f, 2f)] public float bubbleWobble = 1f;
            [Header("Layer opacity")]
            [Tooltip("Opacity of underwater bubbles before the global Shared Look opacity is applied.")]
            [Range(0f, 1f)] public float opacity = 1f;
        }

        [Tooltip("Shared look for every foam element under the body.")]
        public SharedLook look = new SharedLook();
        [Tooltip("Ambient floating foam + ballistic spray (WaterFoamParticles).")]
        public AmbientSection ambient = new AmbientSection();
        [Tooltip("Foam/spray motion on WaterFoamParticles: gravity, flow drift, wind drift, drag.")]
        public MotionSection motion = new MotionSection();
        [Tooltip("Screen-space density veil (FoamDensityComposite material values).")]
        public VeilSection veil = new VeilSection();
        [Tooltip("Impact splashes: crown + droplet burst shaping (WaterSplashEmitter).")]
        public SplashSection splash = new SplashSection();
        [Tooltip("Underwater bubble plumes injected by splash bursts (WaterFoamParticles).")]
        public BubbleSection bubbles = new BubbleSection();

        // ---- Field application (enable/validate time) --------------------------------

        internal void ApplyTo(WaterFoamParticles foam)
        {
            if (foam == null) return;
            if (ambient.drive)
            {
                foam.spawnThreshold = ambient.spawnThreshold;
                foam.spawnRate = ambient.spawnRate;
                foam.maxSpawnPerFrame = ambient.maxSpawnPerFrame;
                foam.sprayChance = ambient.sprayChance;
                foam.sprayLaunchSpeed = ambient.sprayLaunchSpeed;
                foam.rippleCrestFlecksEnabled = ambient.rippleCrestFlecksEnabled;
                foam.rippleCrestFleckAmount = ambient.rippleCrestFleckAmount;
                foam.rippleCrestFleckMaxPerFrame = ambient.rippleCrestFleckMaxPerFrame;
                foam.rippleCrestFleckLifetimeRange = ambient.rippleCrestFleckLifetimeRange;
                foam.rippleCrestFleckSizeRange = ambient.rippleCrestFleckSizeRange;
                foam.rippleCrestFleckMotion = ambient.rippleCrestFleckMotion;
                foam.surfaceFoamOpacity = ambient.surfaceFoamOpacity;
                foam.sprayOpacity = ambient.sprayOpacity;
                foam.lifeRange = ambient.lifeRange;
                foam.sizeRange = ambient.sizeRange;
                foam.spawnMaxDistance = ambient.spawnMaxDistance;
                foam.sprayLifeRange = ambient.sprayLifeRange;
                foam.spraySizeRange = ambient.spraySizeRange;
                foam.depositLifeRange = ambient.depositLifeRange;
                foam.depositSizeRange = ambient.depositSizeRange;
            }
            if (motion.drive)
            {
                foam.gravity = motion.gravity;
                foam.flowDrift = motion.flowDrift;
                foam.windDriftSpeed = motion.windDriftSpeed;
                foam.drag = motion.drag;
            }
            if (veil.drive)
                foam.densitySurfaceSizeScale = veil.surfaceSizeScale;
            if (bubbles.drive)
            {
                foam.bubbleAmount = bubbles.bubbleAmount;
                foam.bubbleRiseSpeed = bubbles.bubbleRiseSpeed;
                foam.bubbleLifeRange = bubbles.bubbleLifeRange;
                foam.bubbleSizeRange = bubbles.bubbleSizeRange;
                foam.bubbleWobble = bubbles.bubbleWobble;
                foam.bubbleOpacity = bubbles.opacity;
            }
            if (look.drive)
            {
                foam.flipbookGrid = look.flipbookGrid;
                foam.flipbookFps = look.flipbookFps;
                foam.sizeHeroPower = look.sizeHeroPower;
            }
        }

        internal void ApplyTo(WaterSplashEmitter emitter)
        {
            if (emitter == null || !splash.drive) return;
            emitter.maxParticlesPerBurst = splash.maxParticlesPerBurst;
            emitter.upwardBias = splash.upwardBias;
            emitter.outwardSpread = splash.outwardSpread;
            emitter.dropletSize = splash.dropletSize;
            emitter.lifetime = splash.lifetime;
            emitter.crownMinStrength = splash.crownMinStrength;
            emitter.crownBaseSize = splash.crownBaseSize;
            emitter.crownLifetime = splash.crownLifetime;
            emitter.crownLaunchHeight = splash.crownLaunchHeight;
            emitter.crownLaunchSpread = splash.crownLaunchSpread;
            emitter.crownTint = splash.crownTint;
            emitter.crownOpacity = splash.crownOpacity;
            emitter.dropletOpacity = splash.dropletOpacity;
            emitter.entryStreaksEnabled = splash.entryStreaksEnabled;
            emitter.entryStreakAmount = splash.entryStreakAmount;
            emitter.entryStreakHeight = splash.entryStreakHeight;
            emitter.entryStreakWidth = splash.entryStreakWidth;
            emitter.entryStreakGravity = splash.entryStreakGravity;
            emitter.entryStreakOpacity = splash.entryStreakOpacity;
            emitter.entryStreakMinStrength = splash.entryStreakMinStrength;
            emitter.entryStreakLifetimeRange = splash.entryStreakLifetimeRange;
            emitter.entryStreakSizeRange = splash.entryStreakSizeRange;
            emitter.entryStreakTint = splash.entryStreakTint;
        }

        // ---- Draw-time material overrides (property blocks; assets never written) -----

        /// <summary>Shared look over the foam-quad draw: tint, opacity, and the shared atlas.</summary>
        internal void WriteLook(MaterialPropertyBlock mpb, float layerOpacity = 1f)
        {
            if (!look.drive) return;
            mpb.SetColor(ID_Tint, look.tint);
            mpb.SetFloat(ID_ParticleOpacity, look.opacity * Mathf.Clamp01(layerOpacity));
            if (look.particleAtlas != null) mpb.SetTexture(ID_ParticleTex, look.particleAtlas);
        }

        /// <summary>Shared look over the spray-droplet draw: tint + opacity only. The atlas is left to
        /// the spray's own material because the spray runs a separate flipbook grid (sprayFlipbookGrid);
        /// forcing the shared sheet, authored for the foam grid, would misplay the spray flipbook.</summary>
        internal void WriteSprayLook(MaterialPropertyBlock mpb, float layerOpacity = 1f)
        {
            if (!look.drive) return;
            mpb.SetColor(ID_Tint, look.tint);
            mpb.SetFloat(ID_ParticleOpacity, look.opacity * Mathf.Clamp01(layerOpacity));
        }

        /// <summary>Veil values over the density composite draw.</summary>
        internal void WriteVeil(MaterialPropertyBlock mpb)
        {
            if (look.drive) mpb.SetColor(ID_Tint, look.tint);
            if (!veil.drive) return;
            mpb.SetFloat(ID_ParticleOpacity, veil.opacity);
            mpb.SetFloat(ID_DensityLowGain, veil.densityLowGain);
            mpb.SetFloat(ID_DensityHighGain, veil.densityHighGain);
            mpb.SetFloat(ID_BreakupTiling, veil.breakupTiling);
            mpb.SetFloat(ID_BreakupStrength, veil.breakupStrength);
            if (veil.breakupTexture != null) mpb.SetTexture(ID_BreakupTex, veil.breakupTexture);
        }
    }
}
