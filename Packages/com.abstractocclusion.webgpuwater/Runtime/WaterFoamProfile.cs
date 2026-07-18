// WebGpuWater - ONE master foam profile (the "foam -> one master" decision).
//
// A body's whole foam story in one asset: a shared look block (tint, sprite atlas,
// flipbook, hero-size bias, opacity) plus one section per foam element (ambient
// foam/spray, screen-space veil, splash). Every foam component takes an
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
        static readonly int ID_ParticleTex = Shader.PropertyToID("_ParticleTex");
        static readonly int ID_DensityLowGain = Shader.PropertyToID("_DensityLowGain");
        static readonly int ID_DensityHighGain = Shader.PropertyToID("_DensityHighGain");
        static readonly int ID_BreakupTex = Shader.PropertyToID("_BreakupTex");
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
            [Range(0f, 30f)] public float flipbookFps = 4f;
            [Range(1f, 6f)] public float sizeHeroPower = 2f;
        }

        [System.Serializable]
        public sealed class AmbientSection
        {
            public bool drive = true;
            [Range(0f, 1f)] public float spawnThreshold = 0.25f;
            [Range(0f, 200f)] public float spawnRate = 30f;
            [Range(16, 4096)] public int maxSpawnPerFrame = 256;
            [Range(0f, 1f)] public float sprayChance = 0.08f;
            [Range(0f, 5f)] public float sprayLaunchSpeed = 0.6f;
            public Vector2 lifeRange = new Vector2(1.5f, 4f);
            public Vector2 sizeRange = new Vector2(0.02f, 0.06f);
            [Range(0f, 400f)] public float spawnMaxDistance = 120f;
        }

        [System.Serializable]
        public sealed class VeilSection
        {
            [Tooltip("Drive the screen-space density veil's material values from this profile.")]
            public bool drive = true;
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
            public bool drive = true;
            [Range(1, 128)] public int maxParticlesPerBurst = 48;
            [Range(0f, 3f)] public float upwardBias = 1f;
            [Range(0f, 3f)] public float outwardSpread = 1.3f;
            public float dropletSize = 0.02f;
            public Vector2 lifetime = new Vector2(0.6f, 1.3f);
            [Range(0f, 1f)] public float crownMinStrength = 0.25f;
            public float crownBaseSize = 0.4f;
            public float crownLifetime = 0.5f;
        }

        [Tooltip("Shared look for every foam element under the body.")]
        public SharedLook look = new SharedLook();
        [Tooltip("Ambient floating foam + ballistic spray (WaterFoamParticles).")]
        public AmbientSection ambient = new AmbientSection();
        [Tooltip("Screen-space density veil (FoamDensityComposite material values).")]
        public VeilSection veil = new VeilSection();
        [Tooltip("Impact splashes: crown + droplet burst shaping (WaterSplashEmitter).")]
        public SplashSection splash = new SplashSection();

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
                foam.lifeRange = ambient.lifeRange;
                foam.sizeRange = ambient.sizeRange;
                foam.spawnMaxDistance = ambient.spawnMaxDistance;
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
        }

        // ---- Draw-time material overrides (property blocks; assets never written) -----

        /// <summary>Shared look over a particle draw (foam quads/spray).</summary>
        internal void WriteLook(MaterialPropertyBlock mpb)
        {
            if (!look.drive) return;
            mpb.SetColor(ID_Tint, look.tint);
            mpb.SetFloat(ID_ParticleOpacity, look.opacity);
            if (look.particleAtlas != null) mpb.SetTexture(ID_ParticleTex, look.particleAtlas);
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
