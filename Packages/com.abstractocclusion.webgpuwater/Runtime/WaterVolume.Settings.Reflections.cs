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
        // cubemap, an explicitly assigned Unity reflection probe, or the scene skybox cubemap.
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
            [Tooltip("Tiling water-normal texture, sampled as two crossing scrolling layers on an " +
                     "OCTAVE LADDER: the world tile grows with view distance so the pattern keeps a " +
                     "steady size on screen instead of repeating into the horizon. None = feature " +
                     "off (surface unchanged).")]
            public Texture2D texture = null;
            [Tooltip("Tilt strength of the detail layer on the surface normal. This is the main dial " +
                     "for how much micro-ripple the water carries; Crest Boost below adds more on " +
                     "steep wave faces on top of it.")]
            [Range(0f, DetailNormalStrengthMax)] public float strength = 0.6f;
            [Tooltip("World size of ONE texture tile at the camera, in metres. Only the NEAR end - " +
                     "each octave further out multiplies it by about 2.6, so this sets how fine the " +
                     "detail is at your feet without deciding how big the tile is at the horizon.")]
            [Range(1f, 100f)] public float tileMeters = 18f;
            [Tooltip("World size the tile GROWS TO at distance, in metres. The ladder climbs from " +
                     "Tile Meters toward this and stops - past a few times the near size the texture " +
                     "reads as blotches rather than ripple. About 4x the near tile is a good start. " +
                     "Set it at or below Tile Meters to pin a single fixed tile (the old behaviour).")]
            [Range(1f, 400f)] public float farTileMeters = DefaultFarTileMeters;
            [Tooltip("View distance (metres) at which the tile has grown to Far Tile Meters. This is " +
                     "the climb RATE - shorter reaches the big tile sooner, which is what you want " +
                     "when the camera sits high or looks along the water. Far Tile Meters alone only " +
                     "TRIMS the climb, so on its own it appears to stop mattering past a point.")]
            [Range(50f, 3000f)] public float farTileDistance = DefaultFarTileDistance;
            [Tooltip("Scroll speed of the crossing layers at the near tile, metres per second. Each " +
                     "octave further out scrolls about 1.6x faster, which is the deep-water " +
                     "dispersion relation for its longer wavelength.")]
            [Range(0f, 2f)] public float scrollSpeed = 0.25f;
            [Tooltip("Scroll speed at the FAR tile, metres per second. Screen motion is speed over " +
                     "distance, so once the tile stops growing the far water keeps slowing and ends " +
                     "up looking frozen - this is the dial that keeps the horizon alive. The " +
                     "dispersion-correct value is printed below the field; going above it is a " +
                     "readability cheat and usually the right call.")]
            [Range(0f, FarScrollSpeedMax)] public float farScrollSpeed = DefaultFarScrollSpeed;
            [Tooltip("HEX TILING: resample the texture on a random hexagonal lattice so its own " +
                     "repeat stops existing - the last tiling you can see once the wave cascades and " +
                     "the octave ladder are doing their job. Costs THREE taps per layer instead of " +
                     "one, so it is off by default; turn it on for hero water and leave it off on " +
                     "mobile. A featureless, isotropic normal map needs it least.")]
            public bool hexTiling = false;
            [Tooltip("How much the wind drives this layer. The crossing directions ALWAYS rotate with " +
                     "Wind Heading; this scales the AMPLITUDE response to Wind Speed, so calm water " +
                     "flattens and a blow roughens it. 0 = amplitude ignores wind (legacy).")]
            [Range(0f, 1f)] public float windResponse = 1f;
            [Tooltip("Extra micro-ripple on the STEEP faces of the larger waves, where wind-driven " +
                     "capillary ripple actually concentrates, instead of an even film everywhere. " +
                     "0 = uniform over the whole surface (legacy).")]
            [Range(0f, 2f)] public float crestBoost = 0.5f;
            [Tooltip("Extra ripple strength FAR AWAY, past the point where the tile stops growing. " +
                     "Beyond there the normal map keeps mipping toward flat, so the detail washes " +
                     "out just where you are looking when you scan the horizon. This adds it back " +
                     "without touching the water at your feet. 0 = off.")]
            [Range(0f, DetailNormalDistanceBoostMax)] public float distanceBoost = DefaultDistanceBoost;
        }

        // Raised from 2 once the octave ladder made the layer usable at every distance rather than
        // fading out by 600 m. The old ceiling was set when pushing the strength up mostly bought
        // near-field noise; now it buys micro-ripple all the way to the horizon, so the useful range
        // genuinely extends further. Authored values are untouched - this only opens headroom.
        const float DetailNormalStrengthMax = 4f;
        // Four times the near tile's default - the ratio that reads well on a typical water normal
        // map, and the one the two-layer scheme was hand-tuned to before the ladder replaced it.
        const float DefaultFarTileMeters = 72f;
        // Roughly restores what one octave of mip coarsening takes off the tilt, so the far field
        // holds its strength instead of fading as you look out. Raise for a deliberately crisp horizon.
        // Chosen so the default near/far pair reproduces the pre-knob climb rate almost exactly.
        const float DefaultFarTileDistance = 200f;
        // Twice the near speed = sqrt(4), the dispersion-correct ratio for the default 4x tile step,
        // so the defaults reproduce the fixed golden-ratio-per-octave climb this replaced.
        const float DefaultFarScrollSpeed = 0.5f;
        // Deliberately far above the dispersion-correct ratio (2x the near speed at the default tile
        // step). Screen motion falls off as 1/distance past the tile cap, so the far layer has to
        // outrun physics by a wide margin before it reads as moving at all - and driving it well past
        // that turns the horizon into glitter, which is a look worth being able to reach on purpose.
        const float FarScrollSpeedMax = 10f;
        const float DefaultDistanceBoost = 0.35f;
        const float DetailNormalDistanceBoostMax = 2f;

        internal Texture2D DetailNormalTexture => detailNormalSettings.texture;
        // Amplitude response to wind speed, shared by the top and the underside so ONE wind drives
        // both. sqrt, not linear: the authored range reaches 10 m/s, where a linear law would more
        // than triple the ripple while sqrt lands at 1.8x - clearly windier, still readable. Measured
        // against the ambient sea state's reference wind when that mode is enabled. Otherwise the
        // legacy reference breeze keeps existing bodies unchanged.
        internal float DetailNormalWindFactor
            => Mathf.Lerp(1f, Mathf.Sqrt(windSpeed / AmbientWindReferenceSpeed),
                          WindDrivesAmbientSeaState ? 1f : detailNormalSettings.windResponse);
        // No texture -> strength 0: the shader's uniform gate then skips all four detail taps.
        internal float DetailNormalStrength
            => detailNormalSettings.texture != null
                 ? detailNormalSettings.strength * DetailNormalWindFactor : 0f;
        internal float DetailNormalScale => detailNormalSettings.tileMeters;
        internal float DetailNormalFarScale => detailNormalSettings.farTileMeters;
        internal float DetailNormalFarDistance => detailNormalSettings.farTileDistance;
        internal float DetailNormalFarSpeed => detailNormalSettings.farScrollSpeed;
        internal bool DetailNormalHexTiling => detailNormalSettings.hexTiling;
        /// <summary>Scroll speed the far tile would run at under deep-water dispersion, c ~ sqrt(lambda).</summary>
        /// <remarks>Shown in the inspector so a deliberate cheat can be told apart from a mistake.</remarks>
        internal float DetailNormalDispersionFarSpeed
            => DetailNormalSpeed * Mathf.Sqrt(Mathf.Max(DetailNormalFarScale, 1e-3f)
                                              / Mathf.Max(DetailNormalScale, 1e-3f));
        internal float DetailNormalDistanceBoost => detailNormalSettings.distanceBoost;
        internal float DetailNormalSpeed => detailNormalSettings.scrollSpeed;
        internal float DetailNormalCrestBoost => detailNormalSettings.crestBoost;
        // (cos, sin) of the wind heading in the XZ plane - the SAME convention
        // WaterWaveBank.Generate builds its component directions from (WaterWaveBank.cs:116-117),
        // so the micro-ripple layer and the wind-wave bank cannot drift onto two different winds.
        internal Vector4 WindDirectionXZ
        {
            get
            {
                float windRadians = windFromDegrees * Mathf.Deg2Rad;
                return new Vector4(Mathf.Cos(windRadians), Mathf.Sin(windRadians), 0f, 0f);
            }
        }

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
            [Tooltip("Use a Unity reflection probe or the scene skybox instead of the water's Sky " +
                     "cubemap. This is the reflection BASE that SSR and Planar layer over.")]
            public bool reflectUrpProbe = false;
            [Tooltip("Optional explicit Unity Reflection Probe for this water body. Supports realtime, " +
                     "baked and custom probes. When empty or not ready, the scene skybox cubemap is " +
                     "used, then the water's Sky cubemap.")]
            public ReflectionProbe reflectionProbe = null;
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
            [Tooltip("How deep BELOW this body's rest plane still reaches the planar mirror, metres. " +
                     "The mirror crops at the plane, so on a big sea the strip of shoreline a wave " +
                     "TROUGH exposes gets cropped out and the island's base reflects SKY through the " +
                     "gap. Raise this to about the body's wave height to close it; the cost is that " +
                     "geometry that far under the surface reaches the mirror too. 0 = crop at the " +
                     "plane. Affects PLANAR only.")]
            [Range(0f, PlanarClipDepthMaxMeters)] public float planarClipDepth = 0f;
            [Tooltip("Planar mirror resolution as a fraction of the water camera. Cost follows the " +
                     "SQUARE of this value: 0.5 renders one quarter of the camera pixels. Affects " +
                     "PLANAR only.")]
            [Range(PlanarResolutionScaleMin, PlanarResolutionScaleMax)]
            public float planarResolutionScale = PlanarMirrorResolutionScale;
            [Tooltip("Render the planar mirror every Nth frame and reuse its previous texture between " +
                     "updates. 1 = every frame. 2 usually halves the mirror camera cost at the expense " +
                     "of one frame of reflection latency. Affects PLANAR only.")]
            [Range(PlanarUpdateIntervalMin, PlanarUpdateIntervalMax)]
            public int planarUpdateInterval = PlanarUpdateIntervalMin;
            [Tooltip("Render shadows inside the planar mirror camera. Disable when reflected shadow " +
                     "detail is not worth repeating the shadow work. Affects PLANAR only.")]
            public bool planarRenderShadows = true;
            [Tooltip("Maximum planar mirror camera distance in metres. 0 keeps the source camera's far " +
                     "distance. A finite value prevents an ocean camera's multi-kilometre far clip from " +
                     "being inherited by the mirror. Affects PLANAR only.")]
            [Min(0f)] public float planarFarClipDistance = 0f;

            // Below this the mirror starts showing the seabed instead of the shoreline strip the band
            // exists to save - a worse artifact than the gap it closes.
            const float PlanarClipDepthMaxMeters = 10f;
            internal const float PlanarResolutionScaleMin = 0.1f;
            internal const float PlanarResolutionScaleMax = 1f;
            internal const int PlanarUpdateIntervalMin = 1;
            internal const int PlanarUpdateIntervalMax = 8;

            // Look (drives the above-water surface; the under-water surface uses the same strength /
            // distortion for its total-internal-reflection view). Ranges mirror the shader.
            [Tooltip("Overall strength of the reflected term (0 = none, 1 = full).")]
            [Range(0f, 1f)] public float reflectionStrength = 1f;
            [Tooltip("Brightness of the reflected environment - the procedural sky OR the URP reflection " +
                     "probe (whichever is active). Boost to make a dim baked probe / dark skybox read on " +
                     "the water; lower to calm a bright reflection. Does not affect the sun glint.")]
            [Range(0f, 4f)] public float envReflectionIntensity = 1f;
            [Tooltip("Include the sun's reflected highlight on the water. Turn this off to keep sky, " +
                     "probe, SSR, and planar reflections while removing the sun glint.")]
            public bool reflectSunlight = true;
            [Tooltip("Brightness of the reflected sun only. Lower values simulate sunlight softened " +
                     "by cloud without changing the scene light or environment reflection.")]
            [Range(0f, SunReflectionIntensityMax)] public float sunReflectionIntensity = 1f;
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

            const float SunReflectionIntensityMax = 4f;
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
        /// <summary>Metres below the rest plane the planar mirror keeps instead of cropping. Clamped
        /// non-negative: a negative value would crop ABOVE the plane and eat the very shoreline strip
        /// this exists to save. The slider cannot go there, a script or a migrated asset can.</summary>
        internal float PlanarClipDepth => Mathf.Max(0f, reflectionSettings.planarClipDepth);
        internal float PlanarResolutionScale => Mathf.Clamp(reflectionSettings.planarResolutionScale,
            ReflectionSettings.PlanarResolutionScaleMin, ReflectionSettings.PlanarResolutionScaleMax);
        internal int PlanarUpdateInterval => Mathf.Clamp(reflectionSettings.planarUpdateInterval,
            ReflectionSettings.PlanarUpdateIntervalMin, ReflectionSettings.PlanarUpdateIntervalMax);
        internal bool PlanarRenderShadows => reflectionSettings.planarRenderShadows;
        internal float PlanarFarClipDistance => Mathf.Max(0f, reflectionSettings.planarFarClipDistance);
        internal bool EffectiveRealRefraction => _realRefractionAllowed && reflectionSettings.realRefraction;
        internal bool ReflectUrpProbe => reflectionSettings.reflectUrpProbe;
        internal ReflectionProbe ReflectionProbe => reflectionSettings.reflectionProbe;
        internal float ReflectionStrength => reflectionSettings.reflectionStrength;
        internal float EnvReflectionIntensity => reflectionSettings.envReflectionIntensity;
        internal float SunReflectionIntensity
            => reflectionSettings.reflectSunlight ? reflectionSettings.sunReflectionIntensity : 0f;
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
