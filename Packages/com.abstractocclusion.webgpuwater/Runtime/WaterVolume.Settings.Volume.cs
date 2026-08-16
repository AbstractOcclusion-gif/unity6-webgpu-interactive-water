// WaterVolume settings - the medium itself: view-path fog, in-scattering, and the downwelling
// attenuation that darkens a point by its own depth rather than by how far the camera looks.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateInteractionAndRippleV6. Hidden.
        [SerializeField, HideInInspector, FormerlySerializedAs("objectInteraction")] ObjectInteraction _legacyObjectInteraction = ObjectInteraction.MouseLikeDrops;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleStrength")] float _legacyObstacleStrength = 0.25f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleDeadband")] float _legacyObstacleDeadband = 0.0006f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleSmoothing")] float _legacyObstacleSmoothing = 0.65f;
        [SerializeField, HideInInspector, FormerlySerializedAs("obstacleFlipY")] bool _legacyObstacleFlipY = true;

        [Header("Water fog (Beer-Lambert)")]
        [SerializeField] WaterFogSettings waterFogSettings = new WaterFogSettings();

        /// <summary>Beer-Lambert depth fog plus art-directed turbidity, shared by the surface, objects
        /// and pool. Migrated off the flat WaterVolume fields into this block (Phase 2); the same-named
        /// accessors keep every reader unchanged. (MaxFogDensity const stays on WaterVolume.)</summary>
        [System.Serializable]
        public sealed class WaterFogSettings
        {
            [Tooltip("Render the camera/scene through this WaterVolume's rectangular fullscreen fog " +
                     "and waterline passes. Disable when the volume only supplies waves and surface " +
                     "fog uniforms to external geometry such as a river ribbon.")]
            public bool fullscreenVolumeFog = true;
            [Tooltip("Global depth absorption, shared by the surface, objects and pool. The " +
                     "UNDERWATER view of it (the fullscreen fog you see with the camera below the " +
                     "surface) is drawn by the WaterUnderwaterFog renderer feature - add it to your " +
                     "URP renderer, or this ticks on and nothing happens below the waterline.")]
            public bool waterFog = false;
            public Color fogColor = new Color(0.10f, 0.30f, 0.40f);
            [Tooltip("Per-channel extinction; red highest so it absorbs first. HDR: push a channel " +
                     "above 1 for very heavy absorption (fully opaque water on short paths).")]
            [ColorUsage(false, true)] public Color fogExtinction = new Color(0.45f, 0.15f, 0.08f);
            [Tooltip("Overall fog multiplier. It MULTIPLIES the extinction above, so only the two " +
                     "together mean anything - their product is the per-metre absorption, which the " +
                     "readout under these fields turns into a distance. For pea-soup water push the " +
                     "extinction colour (HDR, unbounded) rather than this.")]
            [Range(0f, MaxFogDensity)] public float fogDensity = 2f;
            [Tooltip("Art-directed turbidity independent of depth: lerp the view THROUGH the surface " +
                     "toward the fog colour. 0 = clear, 1 = fully non-transparent water. Reflections " +
                     "still show on top (tune with the material's Reflection Strength).")]
            [Range(0f, 1f)] public float waterOpacity = 0f;
            [Tooltip("Scattering of Unity point/spot lights in the underwater fog (closed-form, no " +
                     "ray march): each additional light grows a glow volume in the murk instead of " +
                     "lighting geometry only. 0 = off, the legacy sun-only medium. Needs Water Fog " +
                     "on; Simple fog tiers skip it. Exclusion volumes do not shadow this scatter.")]
            [Range(0f, 1f)] public float lightScatter = 0f;
        }

        // Same-named forwarding accessors keep every reader unchanged. WaterFog stays a public get/set
        // (used by the sample scripting API) but now targets the settings; the rest are read-only.
        bool waterFog => waterFogSettings.waterFog;
        bool fullscreenVolumeFog => waterFogSettings.fullscreenVolumeFog;
        internal Color fogColor => waterFogSettings.fogColor;
        internal Color fogExtinction => waterFogSettings.fogExtinction;
        internal float fogDensity => waterFogSettings.fogDensity;
        internal float waterOpacity => waterFogSettings.waterOpacity;
        internal float UnderwaterLightScatter => waterFogSettings.lightScatter;

        /// <summary>Beer-Lambert depth fog, shared by the surface, objects and pool.</summary>
        public bool WaterFog { get => waterFogSettings.waterFog; set => waterFogSettings.waterFog = value; }

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateWaterFogV3. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("waterFog")] bool _legacyWaterFog = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("fogColor")] Color _legacyFogColor = new Color(0.10f, 0.30f, 0.40f);
        [SerializeField, HideInInspector, FormerlySerializedAs("fogExtinction")] Color _legacyFogExtinction = new Color(0.45f, 0.15f, 0.08f);
        [SerializeField, HideInInspector, FormerlySerializedAs("fogDensity")] float _legacyFogDensity = 2f;
        [SerializeField, HideInInspector, FormerlySerializedAs("waterOpacity")] float _legacyWaterOpacity = 0f;

        [Header("Volume scattering")]
        [SerializeField] VolumeScatterSettings volumeScatterSettings = new VolumeScatterSettings();

        /// <summary>Lit in-scatter colour layered on top of the Beer-Lambert fog. When off, the fog
        /// in-scatters the flat fog colour exactly as before, so this is opt-in per body. Absorption
        /// authoring converts a transmission colour to per-channel extinction; the crest SSS boosts sun
        /// scatter at steep wave peaks.</summary>
        [System.Serializable]
        public sealed class VolumeScatterSettings
        {
            [Tooltip("Light the water volume (a body colour scaled by intensity and lit by sun + ambient " +
                     "through a phase function) instead of in-scattering a flat picked colour. Makes the " +
                     "open ocean respond to the sun. Off = unchanged flat fog colour.")]
            public bool volumeScatter = false;
            [Tooltip("The water body colour, shown directly. HDR.")]
            [ColorUsage(false, true)] public Color scatterColor = new Color(0.05f, 0.22f, 0.32f);
            [Tooltip("Master brightness of the in-scattered colour. Raise this if the water reads too dark.")]
            [Range(0f, 8f)] public float scatterIntensity = 2f;
            [Tooltip("Phase anisotropy g: 0 scatters evenly, higher concentrates a forward glow toward the " +
                     "sun (Schlick/Henyey-Greenstein).")]
            [Range(0f, 0.95f)] public float scatterAnisotropy = 0.5f;
            [Tooltip("Weight of the ambient (sky) contribution to the in-scattered colour.")]
            [Range(0f, 4f)] public float scatterAmbientTerm = 1f;
            [Tooltip("Weight of the direct sun contribution to the in-scattered colour.")]
            [Range(0f, 4f)] public float scatterSunTerm = 1f;

            [Tooltip("Add a subsurface glow at steep wave crests, brightest when looking toward the sun. " +
                     "Ocean bodies only.")]
            public bool crestScatter = false;
            [Tooltip("Strength of the crest subsurface glow.")]
            [Range(0f, 8f)] public float sssIntensity = 3f;
            [Tooltip("How tightly the crest glow concentrates toward the sun (higher = tighter highlight).")]
            [Range(0.5f, 8f)] public float sssSunFalloff = 2f;
            [Tooltip("Crest fold amount (0 = flat water, 1 = breaking) where the glow starts to ramp in. " +
                     "Raise to keep the glow off the gentler swell and onto steeper crests.")]
            [Range(0f, 1f)] public float sssPinchMin = 0.1f;
            [Tooltip("Fold amount where the glow reaches full strength (folds seed the whitecaps, so keep " +
                     "this below full break to let foam take over the very tips).")]
            [Range(0f, 1f)] public float sssPinchMax = 0.6f;
            [Tooltip("Power curve on the fold ramp: >1 concentrates the glow onto the sharpest folds.")]
            [Range(0.5f, 6f)] public float sssPinchFalloff = 1.5f;
        }

        // Same-named forwarding accessors keep the publisher readable and every reader stable.
        internal bool volumeScatter => volumeScatterSettings.volumeScatter;
        internal Color scatterColor => volumeScatterSettings.scatterColor;
        internal float scatterIntensity => volumeScatterSettings.scatterIntensity;
        internal float scatterAnisotropy => volumeScatterSettings.scatterAnisotropy;
        internal float scatterAmbientTerm => volumeScatterSettings.scatterAmbientTerm;
        internal float scatterSunTerm => volumeScatterSettings.scatterSunTerm;
        internal bool crestScatter => volumeScatterSettings.crestScatter;
        internal float sssIntensity => volumeScatterSettings.sssIntensity;
        internal float sssSunFalloff => volumeScatterSettings.sssSunFalloff;
        internal float sssPinchMin => volumeScatterSettings.sssPinchMin;
        internal float sssPinchMax => volumeScatterSettings.sssPinchMax;
        internal float sssPinchFalloff => volumeScatterSettings.sssPinchFalloff;

        [Tooltip("On (default): underwater object shadows follow the REFRACTED light so they line up with " +
                 "the caustics - but only on shaders we own; put submerged props on the Water Receiver shader " +
                 "(other shaders like Standard Lit can't be intercepted and would show a second, straight " +
                 "shadow). Off: object shadows use URP's straight shadow map, so ANY material shows ONE " +
                 "consistent shadow - at the cost of the shadow and caustics drifting apart on a DEEP pool.")]
        [SerializeField] internal bool refractShadows = true;

        [Tooltip("How soft the refracted underwater shadow reads (with Refract Shadows ON). Widens the " +
                 "vertical fade below the occluder and grows a lateral penumbra with depth - like a real " +
                 "shadow softening away from its caster. 0 = the legacy hard silhouette. Overall shadow " +
                 "darkness follows the sun's Shadow Strength, matching URP's shadow-map path.")]
        [Range(0f, 1f)] [SerializeField] internal float refractShadowSoftness = 0.5f;

        [Tooltip("Which layers cast the refracted underwater shadow (with Refract Shadows ON). A " +
                 "submerged object on an excluded layer casts NO underwater shadow at all: once the " +
                 "refracted path is active the caustic channel is the only shadow source, so an " +
                 "excluded object does not fall back to URP's shadow map. Everything by default.")]
        [SerializeField] internal LayerMask refractShadowLayers = AllLayers;

        // A LayerMask serialises as an int bitfield, so ~0 = every layer = the pre-filter behaviour.
        const int AllLayers = ~0;

        [Header("Depth attenuation (downwelling)")]
        [SerializeField] DepthAttenuationSettings depthAttenuation = new DepthAttenuationSettings();

        /// <summary>Downwelling depth attenuation: darken submerged surfaces, caustics and god rays with
        /// depth, independent of the view-path water fog. First feature migrated off the flat WaterVolume
        /// fields into a nested Settings block (Phase 2).</summary>
        [System.Serializable]
        public sealed class DepthAttenuationSettings
        {
            [Tooltip("Darken submerged surfaces, caustics and god rays the DEEPER they sit, " +
                     "independent of view distance. Separate from the view-path fog above.")]
            public bool depthDarken = false;
            [Tooltip("Per-channel downwelling extinction (red highest so deep water shifts blue). " +
                     "Applied as exp(-extinction * strength * depth).")]
            public Color depthExtinction = new Color(0.45f, 0.15f, 0.08f);
            [Tooltip("Master multiplier on the depth term (acts like the fog density). The " +
                     "readout below the fields shows the half-brightness depth it implies - " +
                     "past ~3 every channel crushes black within a metre, which is why the " +
                     "slider stops there (the MaxFogDensity lesson: an exponential dial whose " +
                     "top half is all-black is uncontrollable).")]
            [Range(0f, 3f)] public float depthDarkenStrength = 1f;
            [Tooltip("Extra softening of projected caustics on objects, per world unit of depth.")]
            [Range(0f, 8f)] public float causticDepthFade = 0.5f;
            [Tooltip("Paint projected caustics onto ANY submerged surface (terrain, Standard Lit props, a " +
                     "bare floor with no WaterReceiver) via a fullscreen depth pass - not just the water's own " +
                     "receiver/pool shaders. Needs the WaterCausticProjection render feature on the camera's " +
                     "URP renderer. Off = only surfaces that sample the caustic map show caustics.")]
            public bool screenSpaceCaustics = false;
            [Tooltip("THIS body's intensity for the screen-space caustics above: multiplies the render " +
                     "feature's global Caustic Strength for this body's projection only. 1 = the feature's " +
                     "strength as-is, 0 = invisible. (The feature asset on the URP renderer also carries " +
                     "the global Caustic Strength + Tint shared by all bodies.)")]
            [Range(0f, 2f)] public float screenCausticIntensity = 1f;
            [Tooltip("How fast god-ray shafts fade with depth, per world unit of depth.")]
            [Range(0f, 8f)] public float godRayDepthFade = 0.5f;
            [Tooltip("How much the small wind-wave layer drives the caustic GENERATOR - and through " +
                     "it the god rays, which sample the caustic map. 1 = mirrors the surface's ripple " +
                     "normals exactly (unchanged), 0 = wind waves generate no caustics at all. The " +
                     "visible surface ripples are untouched either way; this only decouples the " +
                     "projected light pattern from them. (Oceans: use Large Caustic Ripple Strength.)")]
            [Range(0f, 1f)] public float causticWindWaveStrength = 1f;
            [Tooltip("Mirror the fog extinction into the depth extinction each frame, so one dial " +
                     "drives fog + depth darkening. Off = the depth colour is fully independent.")]
            public bool linkDepthToFog = false;
        }

        // Same-named forwarding accessors so every reader (WaterUniformPublisher, ...) is unchanged.
        internal bool depthDarken => depthAttenuation.depthDarken;
        internal Color depthExtinction => depthAttenuation.depthExtinction;
        internal float depthDarkenStrength => depthAttenuation.depthDarkenStrength;
        internal float causticDepthFade => depthAttenuation.causticDepthFade;
        internal bool screenSpaceCaustics => depthAttenuation.screenSpaceCaustics;
        internal float screenCausticIntensity => depthAttenuation.screenCausticIntensity;
        internal float godRayDepthFade => depthAttenuation.godRayDepthFade;
        internal float causticWindWaveStrength => depthAttenuation.causticWindWaveStrength;
        internal bool linkDepthToFog => depthAttenuation.linkDepthToFog;
    }
}
