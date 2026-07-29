// WaterVolume settings - what the surface reflects: the reflection mode and environment source,
// the detail-normal layer that breaks up the mirror, and the specular/fresnel family.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        public enum ReflectionMode { SkyOnly, SSR, Planar }

        // The reflection BASE (what SkyOnly shows and what SSR/Planar layer over): the built-in
        // procedural sky cubemap, or the scene's URP reflection probe / skybox (unity_SpecCube0).
        public enum EnvironmentSource { ProceduralSky, UrpProbe }

        [Header("Reflections (Phase 3c)")]
        [SerializeField] ReflectionSettings reflectionSettings = new ReflectionSettings();

        [SerializeField] DetailNormalSettings detailNormalSettings = new DetailNormalSettings();

        /// <summary>Crest-style crossing scrolling detail normals: micro-ripple detail finer than the
        /// FFT cascades resolve. Off (flat) until a tiling water-normal texture is assigned; the
        /// publisher forces the strength to 0 with no texture so the shader skips the taps.</summary>
        [System.Serializable]
        public sealed class DetailNormalSettings
        {
            [Tooltip("Tiling water-normal texture, sampled as two crossing scrolling layers at two " +
                     "world scales. None = feature off (surface unchanged).")]
            public Texture2D texture = null;
            [Tooltip("Tilt strength of the detail layer on the surface normal.")]
            [Range(0f, 2f)] public float strength = 0.6f;
            [Tooltip("World size of one texture tile, metres (the far layer runs at twice this).")]
            [Range(1f, 100f)] public float tileMeters = 18f;
            [Tooltip("Scroll speed of the crossing layers, metres per second.")]
            [Range(0f, 2f)] public float scrollSpeed = 0.25f;
        }

        internal Texture2D DetailNormalTexture => detailNormalSettings.texture;
        // No texture -> strength 0: the shader's uniform gate then skips all four detail taps.
        internal float DetailNormalStrength
            => detailNormalSettings.texture != null ? detailNormalSettings.strength : 0f;
        internal float DetailNormalScale => detailNormalSettings.tileMeters;
        internal float DetailNormalSpeed => detailNormalSettings.scrollSpeed;

        /// <summary>How this body reflects (mode) and what it reflects (base environment). Migrated off the
        /// flat WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// unchanged.</summary>
        [System.Serializable]
        public sealed class ReflectionSettings
        {
            [Tooltip("Screen-space reflection: reflect the on-screen scene. Scales to many bodies; needs " +
                     "Depth + Opaque Texture on the active URP asset. Mixable with Planar (layered).")]
            public bool useScreenSpaceReflection = true;
            [Tooltip("Planar reflection: a full extra scene render across this body's plane. Use for at " +
                     "most ONE 'hero' body. Mixable with SSR (planar layers under SSR).")]
            public bool usePlanarReflection = false;
            [Tooltip("Reflect the scene's active URP reflection probe / skybox instead of the built-in " +
                     "procedural sky. The reflection BASE that SSR and Planar layer over.")]
            public bool reflectUrpProbe = false;
            [Tooltip("Real (screen-space) refraction: see the actual scene through the water instead of " +
                     "the analytic approximation. Needs the URP opaque texture; a tier may force it off.")]
            public bool realRefraction = false;
            [Tooltip("Layers kept OUT of the planar mirror, on top of this body's own water layer " +
                     "(always excluded). USE IT FOR DYNAMIC FLOATERS. A plane cannot fit a displaced " +
                     "surface: an object floating h above the mirror plane has its image placed at -h " +
                     "while the wave it sits on is at +h, so the reflection lands low and swims as the " +
                     "swell lifts it. Excluding it here leaves planar owning the SKY, which it does " +
                     "well; turn SSR on to get that object's reflection back, since SSR marches the " +
                     "real reflected ray and sticks to it by construction. Affects PLANAR only - SSR " +
                     "and the environment base ignore this.")]
            public LayerMask planarExcludeLayers = 0;

            // Look (drives the above-water surface; the under-water surface uses the same strength /
            // distortion for its total-internal-reflection view). Ranges mirror the shader.
            [Tooltip("Overall strength of the reflected term (0 = none, 1 = full).")]
            [Range(0f, 1f)] public float reflectionStrength = 1f;
            [Tooltip("Brightness of the reflected environment - the procedural sky OR the URP reflection " +
                     "probe (whichever is active). Boost to make a dim baked probe / dark skybox read on " +
                     "the water; lower to calm a bright reflection. Does not affect the sun glint.")]
            [Range(0f, 4f)] public float envReflectionIntensity = 1f;
            [Tooltip("Minimum Fresnel reflectance regardless of view angle. 0 = physical (~2% looking " +
                     "straight down, full mirror at grazing). Raise toward the legacy uniformly-mirrored " +
                     "look (the old curve behaved like ~0.25).")]
            [Range(0f, 1f)] public float fresnelFloor = 0f;
            [Tooltip("OVERALL SHININESS: the Fresnel grazing exponent. 5 = physical water; LOWER makes " +
                     "reflectivity rise faster on tilted wave faces, so the whole surface reads " +
                     "glossier with contrast (unlike the floor, which mirrors uniformly).")]
            [Range(1f, 5f)] public float fresnelPower = 5f;
            [Tooltip("Surface roughness at the camera: width of the sun's specular lobe AND blur of the " +
                     "sky reflection. Low = tight glints on calm water; high = broad soft glitter.")]
            [Range(0.01f, 1f)] public float sunRoughness = 0.08f;
            [Tooltip("Roughness far away. RAISE THIS to calm shiny mid/long-range waves: the sun path " +
                     "widens and the sky mirror blurs toward the horizon.")]
            [Range(0.01f, 1f)] public float roughnessFar = 0.2f;
            [Tooltip("Distance (metres) over which roughness ramps from the near value to Far.")]
            [Range(50f, 5000f)] public float roughnessFarDistance = 1000f;
            [Tooltip("Curve of the near-to-far roughness ramp: 1 = linear, above 1 keeps the water " +
                     "sharp for longer, below 1 roughens sooner.")]
            [Range(0.25f, 4f)] public float roughnessFalloff = 1f;
            [Tooltip("Vertical stretching of the blurred sky reflection - rough water smears what it " +
                     "reflects vertically (the classic elongated ocean streaks). 0 = off.")]
            [Range(0f, 1f)] public float reflectionAnisoStretch = 0.5f;
            [Tooltip("Sun sheen: weight of a second, much broader specular lobe, so wave faces far " +
                     "outside the direct sun reflection still catch a soft highlight. 0 = off.")]
            [Range(0f, 1f)] public float sunSheen = 0f;
            [Tooltip("Breadth of the sheen lobe (its roughness). Higher = softer, wider sheen.")]
            [Range(0.2f, 1f)] public float sunSheenRoughness = 0.6f;
            [Tooltip("Keeps the sun glitter alive when the sun sits at/near the horizon (wrapped " +
                     "lighting on the sun lobes). 0 = physical; raise for stronger low-sun sparkle.")]
            [Range(0f, 1f)] public float sunGrazeBoost = 0f;
            [Tooltip("Wave-normal distortion of the reflection.")]
            [Range(0f, 0.2f)] public float reflectionDistortion = 0.05f;
            [Tooltip("Screen-space reflection strength (used when SSR is on).")]
            [Range(0f, 1f)] public float ssrStrength = 1f;
            [Tooltip("SSR ray-march step size, world units.")]
            [Range(0.005f, 0.2f)] public float ssrStepSize = 0.03f;
            [Tooltip("SSR maximum ray-march steps.")]
            [Range(8, 64)] public int ssrMaxSteps = 24;
            [Tooltip("SSR depth thickness tolerance for a hit.")]
            [Range(0.01f, 1f)] public float ssrThickness = 0.2f;
            [Tooltip("Wave-normal distortion of the screen-space refraction (Real Refraction). " +
                     "A screen-UV offset on the opaque texture, so it only exists on that path.")]
            [Range(0f, 0.2f)] public float refractionDistortion = 0.05f;
            [Tooltip("How far the view BENDS entering the water on the ANALYTIC path (Real " +
                     "Refraction OFF). 1 = the physical Snell ray for water; 0 = a flat window that " +
                     "looks straight through. Lower it to calm a busy pool floor. The two refraction " +
                     "knobs are mutually exclusive - Real Refraction picks which one is live.")]
            [Range(0f, 1f)] public float refractionStrength = 1f;
        }

        // Tier-capped effective reflection toggles + look, published per body every frame by
        // WaterUniformPublisher (uniform-driven, so they update live). SSR / Planar / real refraction
        // are the priciest paths, so a tier that disallows them (Low) forces them off; the URP-probe
        // base is never capped.
        internal bool EffectiveUseSSR => _richReflectionsAllowed && reflectionSettings.useScreenSpaceReflection;
        // Planar is split in two: WantsPlanar is the body's own opt-in (tier-capped); EffectiveUsePlanar
        // adds the per-frame budget grant (WaterReflections) so only the nearest few pools actually render
        // a mirror and the rest degrade to SSR / sky. Both the _UsePlanar publish and the mirror pass read
        // EffectiveUsePlanar, so they can never disagree within a frame.
        internal bool WantsPlanar => _richReflectionsAllowed && reflectionSettings.usePlanarReflection;
        internal bool EffectiveUsePlanar => WantsPlanar && WaterReflections.IsPlanarGranted(this);
        /// <summary>Layers the author wants kept out of this body's planar mirror (on top of the
        /// water layer, which <see cref="PlanarReflectLayers"/> always removes).</summary>
        internal LayerMask PlanarExcludeLayers => reflectionSettings.planarExcludeLayers;
        internal bool EffectiveRealRefraction => _realRefractionAllowed && reflectionSettings.realRefraction;
        internal bool ReflectUrpProbe => reflectionSettings.reflectUrpProbe;
        internal float ReflectionStrength => reflectionSettings.reflectionStrength;
        internal float EnvReflectionIntensity => reflectionSettings.envReflectionIntensity;
        internal float FresnelFloor => reflectionSettings.fresnelFloor;
        internal float FresnelPower => reflectionSettings.fresnelPower;
        internal float SunRoughness => reflectionSettings.sunRoughness;
        internal float RoughnessFar => reflectionSettings.roughnessFar;
        internal float RoughnessFarDistance => reflectionSettings.roughnessFarDistance;
        internal float RoughnessFalloff => reflectionSettings.roughnessFalloff;
        internal float ReflectionAnisoStretch => reflectionSettings.reflectionAnisoStretch;
        internal float SunSheen => reflectionSettings.sunSheen;
        internal float SunSheenRoughness => reflectionSettings.sunSheenRoughness;
        internal float SunGrazeBoost => reflectionSettings.sunGrazeBoost;
        internal float ReflectionDistortion => reflectionSettings.reflectionDistortion;
        internal float SSRStrength => reflectionSettings.ssrStrength;
        internal float SSRStepSize => reflectionSettings.ssrStepSize;
        internal float SSRMaxSteps => reflectionSettings.ssrMaxSteps;
        internal float SSRThickness => reflectionSettings.ssrThickness;
        internal float RefractionDistortion => reflectionSettings.refractionDistortion;
        internal float RefractionStrength => reflectionSettings.refractionStrength;
    }
}
