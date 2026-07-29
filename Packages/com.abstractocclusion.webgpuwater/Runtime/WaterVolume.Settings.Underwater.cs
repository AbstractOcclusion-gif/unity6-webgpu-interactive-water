// WaterVolume settings - the view from BELOW the surface: the Snell window, the underside sheen
// and the screen-space waterline meniscus. Distinct from the fog, which is the medium itself.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        /// <summary>How the surface reads seen FROM BELOW (the _Underwater = 1 sheet), plus the
        /// screen-space waterline drawn while the camera crosses it. The above-water look lives in
        /// <see cref="ReflectionSettings"/>; this block owns the underside, which previously ran on
        /// hard-coded legacy constants (a 0.5 minimum mirror that buried the transparency).</summary>
        [System.Serializable]
        public sealed class UnderwaterSurfaceSettings
        {
            [Tooltip("Physical below-water Fresnel: a transparent Snell window overhead (~2% mirror " +
                     "straight up, like the above-water side) turning into a true total-internal-" +
                     "reflection mirror past the ~48.6° critical angle. Off = the legacy curve, " +
                     "a uniform half-mirror sheen that overrides the transparency everywhere.")]
            public bool physicalFresnel = true;
            [Tooltip("Width of the blend into the total-internal-reflection mirror at the Snell " +
                     "window's edge. 0 = the near-physical hard edge (can shimmer on waves); " +
                     "higher = a softer, wider ring.")]
            [Range(0f, 0.5f)] public float tirEdgeSoftness = 0.08f;
            [Tooltip("Minimum underside reflectance regardless of angle (physical mode only). " +
                     "0 = physical. The legacy curve behaved like 0.5.")]
            [Range(0f, 1f)] public float fresnelFloor = 0f;
            [Tooltip("Strength of the reflected term seen from below (the TIR mirror). Independent " +
                     "of the above-water Reflection Strength. 0 = a glass-clear ceiling.")]
            [Range(0f, 1f)] public float reflectionStrength = 1f;
            [Tooltip("What the mirror outside the Snell window shows: 0 = the sky environment " +
                     "tinted by the water (legacy), 1 = the water body's own in-scatter colour " +
                     "(reads as the depths mirrored on the surface). Blendable.")]
            [Range(0f, 1f)] public float mirrorWaterBlend = 0.5f;
            [Tooltip("How strongly dense foam patches darken the surface seen from below (the " +
                     "silhouette blocking the sky).")]
            [Range(0f, 1f)] public float foamSilhouetteDarken = 0.6f;
            [Tooltip("Sunlit glow scattered through thin foam lace seen from below.")]
            [Range(0f, 1f)] public float foamSunGlow = 0.4f;
            [Tooltip("Detail-normal tilt on the underside (uses the Textures section's detail " +
                     "normal map; needs one assigned). 0 = off, the historical ceiling look; " +
                     "raise so the underside carries the same micro-ripple as the top.")]
            [Range(0f, 2f)] public float detailNormalStrength = 0f;
            [Tooltip("Waterline meniscus: a thin darkened band along the on-screen waterline while " +
                     "the camera crosses the surface (partial submersion), so entering/leaving the " +
                     "water shows a line instead of a hard pop.")]
            public bool meniscus = true;
            [Tooltip("Meniscus band thickness, screen pixels.")]
            [Range(1f, 16f)] public float meniscusWidthPixels = 5f;
            [Tooltip("Meniscus opacity at the crossing (how hard the line darkens).")]
            [Range(0f, 1f)] public float meniscusStrength = 0.7f;
            [Tooltip("Waterline lens tension: warps the image in a band around the " +
                     "line so the water appears to grip and climb the lens while crossing the " +
                     "surface. 0 = plain darkened line only.")]
            [Range(0f, 1f)] public float meniscusWarp = 0.35f;
        }

        [SerializeField] UnderwaterSurfaceSettings underwaterSurfaceSettings = new UnderwaterSurfaceSettings();

        internal bool UnderwaterPhysicalFresnel => underwaterSurfaceSettings.physicalFresnel;
        internal float UnderwaterTirEdgeSoftness => underwaterSurfaceSettings.tirEdgeSoftness;
        internal float UnderwaterFresnelFloor => underwaterSurfaceSettings.fresnelFloor;
        internal float UnderwaterReflectionStrength => underwaterSurfaceSettings.reflectionStrength;
        internal float UnderwaterMirrorWaterBlend => underwaterSurfaceSettings.mirrorWaterBlend;
        internal float FoamUndersideDarken => underwaterSurfaceSettings.foamSilhouetteDarken;
        internal float FoamUndersideGlow => underwaterSurfaceSettings.foamSunGlow;
        // No texture -> strength 0, same convention as DetailNormalStrength above: the shader's
        // uniform gate then skips the detail taps on the underside too.
        internal float UnderwaterDetailNormalStrength
            => detailNormalSettings.texture != null
                 ? underwaterSurfaceSettings.detailNormalStrength * DetailNormalWindFactor : 0f;
        internal bool MeniscusEnabled => underwaterSurfaceSettings.meniscus;
        internal float MeniscusWidthPixels => underwaterSurfaceSettings.meniscusWidthPixels;
        internal float MeniscusStrength => underwaterSurfaceSettings.meniscusStrength;
        internal float MeniscusWarp => underwaterSurfaceSettings.meniscusWarp;

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateReflectionsV7. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("reflectionMode")] ReflectionMode _legacyReflectionMode = ReflectionMode.SSR;
        [SerializeField, HideInInspector, FormerlySerializedAs("environmentSource")] EnvironmentSource _legacyEnvironmentSource = EnvironmentSource.ProceduralSky;

        /// <summary>The primary water body: the global fallback for objects without a
        /// <see cref="WaterMembership"/>. Per-object association goes through
        /// <see cref="BodyContaining"/>.</summary>
        public static WaterVolume Primary { get; private set; }

        /// <summary>Resolve the body an object should use when it isn't inside any specific
        /// one: the primary body, or any found body as a fallback. Prefer
        /// <see cref="BodyContaining"/> for objects that have a world position.</summary>
        public static WaterVolume Resolve()
        {
            if (Primary != null) return Primary;
            // Frame-cache the scene search: per-particle callers (splash drift) would
            // otherwise degrade to a whole-scene FindFirstObjectByType per particle per frame.
            if (_fallbackBodyFrame != Time.frameCount || _fallbackBody == null)
            {
                _fallbackBodyFrame = Time.frameCount;
                _fallbackBody = FindFirstObjectByType<WaterVolume>();
            }
            return _fallbackBody;
        }
        static WaterVolume _fallbackBody;
        static int _fallbackBodyFrame = -1;

        /// <summary>The water body a world point belongs to: the body whose horizontal
        /// footprint contains the point, nearest-centre wins when several overlap, and the
        /// primary body as a fallback when the point is outside every footprint. Objects call
        /// this each frame so they float on, and are lit by, the lake they are actually in.</summary>
        public static WaterVolume BodyContaining(Vector3 worldPoint)
        {
            WaterVolume best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (!body.WorldToPoolXZ(worldPoint, out _, out _)) continue;

                // Tiebreak on HORIZONTAL distance to centre; the footprint ignores height,
                // so a vertical gap between the point and a body must not sway the choice.
                Vector3 toCenter = body.VolumeCenter - worldPoint;
                float sqr = toCenter.x * toCenter.x + toCenter.z * toCenter.z;
                if (sqr < bestSqr) { bestSqr = sqr; best = body; }
            }
            return best != null ? best : Resolve();
        }

        /// <summary>All live water bodies. Used by the input router to send a click to
        /// whichever body's surface the ray hits, by the sim scheduler, and by
        /// <see cref="BodyContaining"/>.</summary>
        internal static readonly List<WaterVolume> Bodies = new List<WaterVolume>();

        // Set true after the primary body's one-time autolink scan (reset per play session).
        static bool _receiversAutoLinked;

        // Water shaders whose user renderers should be per-body. Named here so the autolink
        // scan can spot a loose crate/pool that uses one and give it a WaterMembership.
        static readonly string[] WaterMaterialShaderNames =
        {
            WaterShaderNames.WaterReceiver,
            WaterShaderNames.AnalyticPool,
        };

        /// <summary>One-time play-mode scan (primary body only): give every scene renderer that
        /// uses a water material - and isn't already driven by a body - a WaterMembership, so it
        /// is lit and fogged by the body it sits in without manual wiring. Idempotent: skips
        /// renderers that already carry the component or belong to a body's own surface/pool.</summary>
        static void AutoLinkReceivers()
        {
            Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r.GetComponent<WaterMembership>() != null) continue;
                if (IsBodyOwnedRenderer(r)) continue;   // driven by ApplyBodyBlock already
                if (!UsesWaterMaterial(r)) continue;
                r.gameObject.AddComponent<WaterMembership>();
            }
        }

        // True when a renderer is one this-or-another body drives directly (surface/pool/god
        // rays), so the autolink scan must not also attach a membership and double-write its MPB.
        static bool IsBodyOwnedRenderer(Renderer r)
        {
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume b = Bodies[i];
                if (r == b.surfaceAbove || r == b.surfaceUnder || r == b.poolRenderer || r == b.godRayRenderer)
                    return true;
            }
            return false;
        }

        static bool UsesWaterMaterial(Renderer r)
        {
            Material[] mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null || m.shader == null) continue;
                for (int s = 0; s < WaterMaterialShaderNames.Length; s++)
                    if (m.shader.name == WaterMaterialShaderNames[s]) return true;
            }
            return false;
        }

        // Fast Enter Play Mode (the Unity 6.6 default) skips the domain reload, so statics
        // survive between play sessions. Reset every piece of scene-lifetime static state
        // before each session; OnEnable/OnDisable rebuild it for the new one.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStaticState()
        {
            Primary = null;
            Bodies.Clear();
            _fallbackBody = null;
            _fallbackBodyFrame = -1;
            _receiversAutoLinked = false;
#if WEBGPUWATER_URP
            _pipelineOwner = null;
            _savedRenderScale = 0f;
            _savedOpaqueTexture = false;
#endif
            // The static gates the URP features read live in the Underwater partial. They are only
            // cleared opportunistically by the LAST body out of OnDisable, which never runs if play
            // mode ends with bodies still alive - so left set, the fullscreen fog + meniscus features
            // (which sit on the renderer asset and are polled in EVERY scene) enter the next session
            // already armed. Same "reset every piece of scene-lifetime static state" contract as above.
            UnderwaterFogActive = false;
            WaterlineActive = false;
            CameraSubmerged = false;
            WaterSimScheduler.ResetStaticState();
            WaterInteractable.ResetStaticState();
            WaterDebugView.ResetStaticState();
            WaterExclusionVolume.ResetStaticState();
            WaterBuoyancy.ResetStaticState();
        }

        [Header("Simulation")]
        [Tooltip("Master animation speed for THIS body's surface: multiplies the wave clock and the " +
                 "ripple solver timestep. 1 = real time, 0 = frozen, 2 = double speed. Foam and splash " +
                 "particles keep real time (surface only).")]
        [Range(0f, MaxTimeScale)] [SerializeField] float timeScale = 1f;

        // Upper bound for timeScale + the inspector slider max. Kept modest so the CFL-bounded ripple
        // solver (waveSpeed is stable only to ~2) still integrates sanely when time is sped up.
        const float MaxTimeScale = 8f;

        /// <summary>Per-body master animation speed (wave clock + ripple timestep). Clamped to [0, MaxTimeScale].</summary>
        public float TimeScale { get => timeScale; set => timeScale = Mathf.Clamp(value, 0f, MaxTimeScale); }

        [Tooltip("Direction TOWARD the light. Used when no 'sun' is assigned (a sun overrides it).")]
        [SerializeField] internal Vector3 lightDir = new Vector3(2f, 2f, -1f);
        [Tooltip("Caustic map size. Detail is capped by the SIM resolution, not by this: the generator " +
                 "writes ONE focus value per sim grid cell, so above the sim resolution this only smooths " +
                 "the sampling and never adds a finer pattern. For finer caustics raise Ripple Quality " +
                 "(sim resolution), or narrow the sim window on an ocean.")]
        [SerializeField] internal int causticResolution = 1024;
        // Tier override for the caustic RT resolution; 0 = no tier applied -> the authored
        // causticResolution above (see ApplyQuality for why the serialized field is never written).
        [System.NonSerialized] int _causticRes;
        internal int EffectiveCausticResolution => _causticRes > 0 ? _causticRes : causticResolution;

        // Direction TOWARD the light: the assigned sun wins, the serialized vector is the manual
        // fallback. Derived (not written back to the field): the old per-frame write-back silently
        // dirtied the authored value under [ExecuteAlways] in edit mode.
        internal Vector3 EffectiveLightDir => sun != null ? -sun.transform.forward : lightDir;
    }
}
