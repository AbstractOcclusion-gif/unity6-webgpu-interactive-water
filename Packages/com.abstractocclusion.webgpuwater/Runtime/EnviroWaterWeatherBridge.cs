using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>
    /// Optional, dependency-free bridge from Enviro 3's live weather state to every WaterVolume.
    /// One instance is created automatically in play mode when the scene does not provide one.
    /// It also keeps Enviro's fullscreen AIR fog out of the water column (Underwater fog section):
    /// Enviro fogs opaque geometry before the water draws and has no notion of a waterline.
    /// </summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Enviro 3 Weather Bridge")]
    [DefaultExecutionOrder(1000)]
    public sealed class EnviroWaterWeatherBridge : MonoBehaviour
    {
        const string AutoObjectName = "[WebGpuWater] Enviro 3 Weather Bridge (Auto)";
        const string FogZoneNamePrefix = "[WebGpuWater] Enviro Fog Removal - ";
        const string EnviroAssembly = "Enviro3.Runtime";
        // Enviro clamps its fog amount to [0, 10] BEFORE the zones subtract (FogIncludeHLSL.hlsl), so
        // -10 at full influence removes height/distance fog completely; the shader floors at 0.
        const float FogZoneDensity = -10f;
        // Enviro uploads (1 - feather) and its shader DIVIDES by that fraction of the box, per axis.
        // The floor keeps the division finite; the ceiling keeps a very shallow body's zone sane.
        const float MinZoneFeatherFraction = 0.0005f;
        const float MaxZoneFeatherFraction = 0.5f;

        /// <summary>How Enviro's fullscreen AIR fog is kept out of the water column.</summary>
        public enum UnderwaterFogRemoval
        {
            /// <summary>Leave Enviro's fog untouched.</summary>
            Off,
            /// <summary>Zones where the graphics API has them, the global switch everywhere else.</summary>
            Auto,
            /// <summary>One Enviro Effect Removal Zone per water body. Per pixel, so a view that
            /// straddles the waterline stays correct. Direct3D, Metal and Vulkan only.</summary>
            Zones,
            /// <summary>Switch Enviro's fog off while the camera is submerged. Every graphics API,
            /// but it acts on the whole frame and on every camera.</summary>
            GlobalSwitch,
        }
        static readonly int EnviroActiveId = Shader.PropertyToID("_EnviroActive");

        [Header("Wind")]
        [SerializeField] bool syncWind = true;
        [Tooltip("Water wind speed produced by Enviro's normalized wind speed of 1.")]
        [SerializeField, Min(0f)] float maximumWaterWindSpeed = 15f;

        [Header("Rain ripples")]
        [SerializeField] bool rainRipples = true;
        [Tooltip("Water rain intensity (the body's Rain Ripples scale, 0..1) produced by Enviro " +
                 "wetness target 1. The body's own slider still applies; the stronger one wins.")]
        [SerializeField, Range(0f, 1f)] float maximumRainRipples = 1f;

        [Header("Underwater fog")]
        [Tooltip("Enviro fogs opaque geometry with a fullscreen pass that knows nothing about water, so a " +
                 "submerged camera sees AIR fog on the bed. Auto removes it with Enviro Effect Removal " +
                 "Zones where the graphics API has them (Direct3D, Metal, Vulkan) and otherwise switches " +
                 "Enviro's fog off while the camera is submerged.")]
        [SerializeField] UnderwaterFogRemoval underwaterFogRemoval = UnderwaterFogRemoval.Auto;
        [Tooltip("Distance in metres over which a removal zone fades in BELOW the rest surface. The " +
                 "zone's top sits exactly on the rest surface, so nothing above the water loses its fog.")]
        [SerializeField, Range(0.01f, 1f)] float fogZoneFeatherMeters = 0.1f;

        [Header("Lifecycle")]
        [Tooltip("Put each body's authored wind back when this bridge is disabled.")]
        [SerializeField] bool restoreAuthoredWindOnDisable = true;

        readonly Dictionary<WaterVolume, Vector2> authoredWind = new();
        readonly Dictionary<WaterVolume, FogZone> fogZones = new();
        EnviroAccessor enviro;
        FogZoneAccessor zoneAccessor;
        FogSwitchAccessor fogSwitch;
        object zoneManager;
        float bindRetryAt;
        bool weatherRainApplied;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InstallAutomatically()
        {
            if (FindFirstObjectByType<EnviroWaterWeatherBridge>(FindObjectsInactive.Include) != null)
                return;

            var host = new GameObject(AutoObjectName);
            DontDestroyOnLoad(host);
            host.AddComponent<EnviroWaterWeatherBridge>();
        }

        void LateUpdate()
        {
            // This global is zero in projects without Enviro and while its manager is disabled.
            if (Shader.GetGlobalFloat(EnviroActiveId) <= 0f)
            {
                // Enviro stood down: never leave its fog switched off behind it. Zones are inert
                // without a manager and are re-registered when one returns (SyncFogZones).
                fogSwitch?.Restore();
                ReleaseWeatherRain();
                return;
            }

            if ((enviro == null || !enviro.TryRead(out _)) && Time.unscaledTime >= bindRetryAt)
            {
                enviro = EnviroAccessor.TryCreate();
                // ??= : a live fog switch holds the value it must restore, so it is never rebuilt.
                zoneAccessor ??= FogZoneAccessor.TryCreate();
                fogSwitch ??= FogSwitchAccessor.TryCreate();
                bindRetryAt = Time.unscaledTime + 1f;
            }
            if (enviro == null || !enviro.TryRead(out WeatherState weather))
                return;

            ApplyWind(weather);
            ApplyRain(weather.wetnessTarget);
            ApplyUnderwaterFogRemoval();
        }

        void ApplyWind(in WeatherState weather)
        {
            if (!syncWind) return;

            float speed = Mathf.Clamp01(weather.windSpeed) * maximumWaterWindSpeed;
            float heading = EnviroDirectionToWaterHeading(weather.windDirectionX, weather.windDirectionY);
            for (int i = 0; i < WaterVolume.Bodies.Count; i++)
            {
                WaterVolume body = WaterVolume.Bodies[i];
                if (body == null || !body.isActiveAndEnabled) continue;
                if (!authoredWind.ContainsKey(body))
                    authoredWind.Add(body, new Vector2(body.WindSpeed, body.WindFromDegrees));
                body.SetWeatherWind(speed, heading);
            }
            RemoveDestroyedBodies();
        }

        // The bridge used to throw its own drops inside a disc around the camera; on a small pond
        // nearly all of them missed the footprint. Each body now rains on ITSELF (WaterVolume's
        // Rain Ripples, per square metre over its simulated surface) and the bridge only supplies
        // the intensity, the same division of labour as the wind.
        void ApplyRain(float wetnessTarget)
        {
            if (!rainRipples) { ReleaseWeatherRain(); return; }
            float rain = Mathf.Clamp01(wetnessTarget) * maximumRainRipples;
            for (int i = 0; i < WaterVolume.Bodies.Count; i++)
            {
                WaterVolume body = WaterVolume.Bodies[i];
                if (body != null && body.isActiveAndEnabled) body.SetWeatherRain(rain);
            }
            weatherRainApplied = true;
        }

        void ReleaseWeatherRain()
        {
            if (!weatherRainApplied) return;
            for (int i = 0; i < WaterVolume.Bodies.Count; i++)
                if (WaterVolume.Bodies[i] != null) WaterVolume.Bodies[i].SetWeatherRain(0f);
            weatherRainApplied = false;
        }

        // ---- Underwater fog -------------------------------------------------------------------
        // Enviro's fog pass runs at BeforeRenderingTransparents - 1 and reconstructs each OPAQUE
        // pixel from _CameraDepthTexture. From above that is harmless: the water sheet is Blend Off
        // and refracts the opaque copy taken BEFORE Enviro ran, so it overwrites the fogged bed.
        // From below the bed is seen directly, carrying air fog under the water's own medium.
        void ApplyUnderwaterFogRemoval()
        {
            bool zonesAvailable = zoneAccessor != null
                               && GraphicsApiHasRemovalZones(SystemInfo.graphicsDeviceType);
            UnderwaterFogRemoval mode = ResolveFogRemovalMode(underwaterFogRemoval, zonesAvailable);

            if (mode == UnderwaterFogRemoval.Zones) SyncFogZones();
            else DestroyFogZones();

            if (mode == UnderwaterFogRemoval.GlobalSwitch && fogSwitch != null)
                fogSwitch.Apply(WaterVolume.CameraSubmerged, Camera.main);
            else
                fogSwitch?.Restore();
        }

        internal static UnderwaterFogRemoval ResolveFogRemovalMode(UnderwaterFogRemoval requested,
                                                                   bool zonesAvailable)
        {
            if (requested == UnderwaterFogRemoval.Auto)
                return zonesAvailable ? UnderwaterFogRemoval.Zones : UnderwaterFogRemoval.GlobalSwitch;
            // An explicit Zones request on an API without them does nothing rather than silently
            // becoming the whole-frame switch the author did not pick.
            if (requested == UnderwaterFogRemoval.Zones && !zonesAvailable)
                return UnderwaterFogRemoval.Off;
            return requested;
        }

        // Mirrors the #if around FogZones() in Enviro's FogInclude: SHADER_API_D3D11 (which Unity
        // also defines for Direct3D 12), Metal and Vulkan. Everywhere else - WebGPU above all - the
        // zone buffer is never read, so a zone would be dead weight.
        internal static bool GraphicsApiHasRemovalZones(GraphicsDeviceType api)
            => api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12
            || api == GraphicsDeviceType.Metal || api == GraphicsDeviceType.Vulkan;

        /// <summary>Enviro Cubical zone covering a body's REST water column. 'surfaceCentre' is the
        /// volume centre (it sits ON the rest surface), 'extent' the body's half-extents in x/z and
        /// its full column depth in y - the WorldToPool convention. The top face sits EXACTLY on the
        /// rest surface: lifting it (tried first, so the fade would finish at the surface) cleared a
        /// visible stripe of fog off everything just above the waterline, because a zone subtracts up
        /// to 10 from a fog amount that is rarely above 2-3 - even a sliver of influence removes it
        /// all, so the ramp is a cliff, not a fade. The same cliff means the feather band BELOW the
        /// surface clears almost at once too. Enviro's feather is a per-axis FRACTION of the box, so
        /// the same fraction also softens the side faces.</summary>
        internal static void ComputeFogZoneBox(Vector3 surfaceCentre, Quaternion rotation, Vector3 extent,
                                               float featherMeters, out Vector3 zoneCentre,
                                               out Vector3 zoneSize, out float enviroFeather)
        {
            float height = extent.y;
            zoneSize = new Vector3(2f * extent.x, height, 2f * extent.z);
            zoneCentre = surfaceCentre - rotation * Vector3.up * (0.5f * height);
            float fraction = Mathf.Clamp(featherMeters / height,
                                         MinZoneFeatherFraction, MaxZoneFeatherFraction);
            enviroFeather = 1f - fraction;
        }

        void SyncFogZones()
        {
            // A new EnviroManager (scene change) starts with an empty zone list, and a zone only
            // registers itself in ITS OnEnable - so re-run that. Its own add path de-duplicates;
            // the manager's public AddRemovalZone does not, which is why it is not called here.
            object manager = enviro.Manager;
            if (!ReferenceEquals(manager, zoneManager))
            {
                zoneManager = manager;
                foreach (var pair in fogZones)
                {
                    Behaviour component = pair.Value.component;
                    if (component == null) continue;
                    component.enabled = false;
                    component.enabled = true;
                }
            }

            for (int i = 0; i < WaterVolume.Bodies.Count; i++)
            {
                WaterVolume body = WaterVolume.Bodies[i];
                if (body == null || !body.isActiveAndEnabled) continue;

                Vector3 surfaceCentre = body.VolumeCenter;
                Vector3 extent = body.VolumeExtentSafe;
                if (body.IsOceanClipmap)
                {
                    // Unbounded: a box that follows the eye and reaches the far plane covers every
                    // point this camera can see. Oceans are level, so the eye's xz is used as is.
                    Camera eye = Camera.main;
                    if (eye == null) continue;
                    Vector3 eyePosition = eye.transform.position;
                    surfaceCentre = new Vector3(eyePosition.x, surfaceCentre.y, eyePosition.z);
                    extent = new Vector3(eye.farClipPlane, extent.y, eye.farClipPlane);
                }

                ComputeFogZoneBox(surfaceCentre, body.VolumeRotation, extent, fogZoneFeatherMeters,
                                  out Vector3 zoneCentre, out Vector3 zoneSize, out float feather);

                if (!fogZones.TryGetValue(body, out FogZone zone) || zone.component == null)
                {
                    Behaviour component = zoneAccessor.Create(body.name);
                    if (component == null) continue;
                    zone = new FogZone { component = component };
                    fogZones[body] = zone;
                }

                zone.component.transform.SetPositionAndRotation(zoneCentre, body.VolumeRotation);
                // Reflection writes box their values: only when something actually changed.
                if (zone.size != zoneSize || zone.feather != feather)
                {
                    zoneAccessor.Write(zone.component, zoneSize, feather);
                    zone.size = zoneSize;
                    zone.feather = feather;
                }
                // Activated LAST, so the zone's OnEnable registers an already-configured box.
                if (!zone.component.gameObject.activeSelf) zone.component.gameObject.SetActive(true);
            }
            RemoveStaleFogZones();
        }

        void RemoveStaleFogZones()
        {
            if (fogZones.Count == 0) return;
            List<WaterVolume> stale = null;
            foreach (var pair in fogZones)
            {
                if (pair.Key != null && pair.Key.isActiveAndEnabled) continue;
                stale ??= new List<WaterVolume>();
                stale.Add(pair.Key);
            }
            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++)
            {
                if (fogZones.TryGetValue(stale[i], out FogZone zone) && zone.component != null)
                    DestroyZoneObject(zone.component.gameObject);
                fogZones.Remove(stale[i]);
            }
        }

        void DestroyFogZones()
        {
            if (fogZones.Count == 0) return;
            foreach (var pair in fogZones)
                if (pair.Value.component != null)
                    DestroyZoneObject(pair.Value.component.gameObject);
            fogZones.Clear();
        }

        static void DestroyZoneObject(UnityEngine.Object target)
        {
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        void OnDisable()
        {
            DestroyFogZones();
            fogSwitch?.Restore();
            zoneManager = null;
            if (restoreAuthoredWindOnDisable)
            {
                foreach (var pair in authoredWind)
                    if (pair.Key != null)
                        pair.Key.SetWeatherWind(pair.Value.x, pair.Value.y);
            }
            authoredWind.Clear();
            ReleaseWeatherRain();
        }

        void RemoveDestroyedBodies()
        {
            if (authoredWind.Count == 0) return;
            List<WaterVolume> dead = null;
            foreach (var pair in authoredWind)
            {
                if (pair.Key != null) continue;
                dead ??= new List<WaterVolume>();
                dead.Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) authoredWind.Remove(dead[i]);
        }

        internal static float EnviroDirectionToWaterHeading(float x, float y)
        {
            // Enviro drives its WindZone forward with (-x, 0, -y). Water uses a heading TOWARD
            // the travel direction, where zero degrees is +X.
            if (x * x + y * y < 1e-6f) return 0f;
            return Mathf.Repeat(Mathf.Atan2(-y, -x) * Mathf.Rad2Deg, 360f);
        }

        readonly struct WeatherState
        {
            internal readonly float windDirectionX;
            internal readonly float windDirectionY;
            internal readonly float windSpeed;
            internal readonly float wetnessTarget;

            internal WeatherState(float directionX, float directionY, float speed, float wetness)
            {
                windDirectionX = directionX;
                windDirectionY = directionY;
                windSpeed = speed;
                wetnessTarget = wetness;
            }
        }

        sealed class FogZone
        {
            internal Behaviour component;
            internal Vector3 size;
            internal float feather;
        }

        // Enviro.EnviroEffectRemovalZone, bound by reflection like the weather state below. The
        // zone object is a scene ROOT on purpose: the component writes localScale = size every
        // Update, so any scaled parent would distort the box. It is an ordinary play-mode object
        // (no HideFlags): Unity removes it when play mode ends, and if its scene unloads the
        // bridge sees the dead component and builds a new zone on the next LateUpdate.
        sealed class FogZoneAccessor
        {
            readonly Type zoneType;
            readonly FieldInfo typeField;
            readonly FieldInfo densityField;
            readonly FieldInfo featherField;
            readonly FieldInfo sizeField;
            readonly object cubicalMode;

            FogZoneAccessor(Type zone, FieldInfo type, FieldInfo density, FieldInfo feather,
                            FieldInfo size, object cubical)
            {
                zoneType = zone;
                typeField = type;
                densityField = density;
                featherField = feather;
                sizeField = size;
                cubicalMode = cubical;
            }

            internal static FogZoneAccessor TryCreate()
            {
                Type zone = Type.GetType("Enviro.EnviroEffectRemovalZone, " + EnviroAssembly);
                if (zone == null || !typeof(Behaviour).IsAssignableFrom(zone)) return null;
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
                FieldInfo type = zone.GetField("type", Flags);
                FieldInfo density = zone.GetField("density", Flags);
                FieldInfo feather = zone.GetField("feather", Flags);
                FieldInfo size = zone.GetField("size", Flags);
                if (type == null || !type.FieldType.IsEnum || !Enum.IsDefined(type.FieldType, "Cubical")
                    || density?.FieldType != typeof(float) || feather?.FieldType != typeof(float)
                    || size?.FieldType != typeof(Vector3))
                    return null;
                return new FogZoneAccessor(zone, type, density, feather, size,
                                           Enum.Parse(type.FieldType, "Cubical"));
            }

            // Returned INACTIVE: the caller writes the box, then activates it.
            internal Behaviour Create(string bodyName)
            {
                var host = new GameObject(FogZoneNamePrefix + bodyName);
                host.SetActive(false);
                var component = host.AddComponent(zoneType) as Behaviour;
                if (component == null)
                {
                    DestroyZoneObject(host);
                    return null;
                }
                typeField.SetValue(component, cubicalMode);
                densityField.SetValue(component, FogZoneDensity);
                return component;
            }

            internal void Write(Behaviour component, Vector3 size, float feather)
            {
                sizeField.SetValue(component, size);
                featherField.SetValue(component, feather);
            }
        }

        // The whole-frame fallback. Enviro's render graph reads ONE flag to decide whether its fog
        // (height fog AND its volumetrics) renders: the camera's quality override when a Quality
        // module exists, else the Fog module's own setting - so this writes whichever is live.
        // Both owners are plain serializable classes held by ScriptableObjects: in the editor a
        // write survives until it is undone, which is why Restore runs on surfacing, on Enviro
        // standing down and in OnDisable. A change Enviro makes to the flag DURING a dive is
        // overwritten by that restore.
        sealed class FogSwitchAccessor
        {
            readonly PropertyInfo instanceProperty;
            readonly FieldInfo qualityModuleField;
            readonly MethodInfo qualityForCamera;
            readonly FieldInfo qualityFogOverrideField;
            readonly FieldInfo qualityFogFlag;
            readonly FieldInfo fogModuleField;
            readonly FieldInfo fogSettingsField;
            readonly FieldInfo fogFlag;
            readonly object[] cameraArgument = new object[1];

            object savedOwner;
            FieldInfo savedField;
            bool savedValue;
            bool switchedOff;

            FogSwitchAccessor(PropertyInfo instance, FieldInfo qualityModule, MethodInfo forCamera,
                              FieldInfo qualityOverride, FieldInfo qualityFlag, FieldInfo fogModule,
                              FieldInfo fogSettings, FieldInfo flag)
            {
                instanceProperty = instance;
                qualityModuleField = qualityModule;
                qualityForCamera = forCamera;
                qualityFogOverrideField = qualityOverride;
                qualityFogFlag = qualityFlag;
                fogModuleField = fogModule;
                fogSettingsField = fogSettings;
                fogFlag = flag;
            }

            internal static FogSwitchAccessor TryCreate()
            {
                Type managerType = Type.GetType("Enviro.EnviroManager, " + EnviroAssembly);
                if (managerType == null) return null;
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;
                PropertyInfo instance = managerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                FieldInfo fogModule = managerType.GetField("Fog", Flags);
                FieldInfo fogSettings = fogModule?.FieldType.GetField("Settings", Flags);
                FieldInfo flag = fogSettings?.FieldType.GetField("fog", Flags);
                if (instance == null || flag == null || flag.FieldType != typeof(bool)) return null;

                // The quality chain is optional: without it the Fog module's own flag is the live one.
                FieldInfo qualityModule = managerType.GetField("Quality", Flags);
                Type helperType = Type.GetType("Enviro.EnviroHelper, " + EnviroAssembly);
                MethodInfo forCamera = helperType?.GetMethod("GetQualityForCamera",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Camera) }, null);
                FieldInfo qualityOverride = forCamera?.ReturnType.GetField("fogOverride", Flags);
                FieldInfo qualityFlag = qualityOverride?.FieldType.GetField("fog", Flags);
                if (qualityModule == null || qualityFlag == null || qualityFlag.FieldType != typeof(bool))
                {
                    qualityModule = null;
                    forCamera = null;
                }
                return new FogSwitchAccessor(instance, qualityModule, forCamera, qualityOverride,
                                             qualityFlag, fogModule, fogSettings, flag);
            }

            internal void Apply(bool submerged, Camera camera)
            {
                if (!submerged) { Restore(); return; }
                if (switchedOff) return;
                if (!TryResolveLiveFlag(camera, out object owner, out FieldInfo field)) return;
                savedOwner = owner;
                savedField = field;
                savedValue = (bool)field.GetValue(owner);
                field.SetValue(owner, false);
                switchedOff = true;
            }

            internal void Restore()
            {
                if (!switchedOff) return;
                savedField.SetValue(savedOwner, savedValue);
                savedOwner = null;
                savedField = null;
                switchedOff = false;
            }

            // Same fork as EnviroURPRenderGraph: Quality module present -> the camera's quality
            // override, otherwise the Fog module's setting.
            bool TryResolveLiveFlag(Camera camera, out object owner, out FieldInfo field)
            {
                owner = null;
                field = null;
                object manager = instanceProperty.GetValue(null);
                if (IsMissing(manager)) return false;

                if (qualityModuleField != null && !IsMissing(qualityModuleField.GetValue(manager)))
                {
                    cameraArgument[0] = camera;
                    object quality = qualityForCamera.Invoke(null, cameraArgument);
                    cameraArgument[0] = null;
                    owner = IsMissing(quality) ? null : qualityFogOverrideField.GetValue(quality);
                    field = qualityFogFlag;
                    return owner != null;
                }

                object module = fogModuleField.GetValue(manager);
                owner = IsMissing(module) ? null : fogSettingsField.GetValue(module);
                field = fogFlag;
                return owner != null;
            }

            // Reflection hands back Unity objects as 'object', where a destroyed one is not null.
            static bool IsMissing(object value)
                => value == null || (value is UnityEngine.Object unityObject && unityObject == null);
        }

        // Reflection keeps the water package installable when Enviro is absent. Members are bound
        // once; the per-frame path is only four FieldInfo reads.
        sealed class EnviroAccessor
        {
            readonly PropertyInfo instanceProperty;
            readonly FieldInfo environmentField;
            readonly FieldInfo settingsField;
            readonly FieldInfo directionXField;
            readonly FieldInfo directionYField;
            readonly FieldInfo windSpeedField;
            readonly FieldInfo wetnessTargetField;

            EnviroAccessor(PropertyInfo instance, FieldInfo environment, FieldInfo settings,
                FieldInfo directionX, FieldInfo directionY, FieldInfo speed, FieldInfo wetness)
            {
                instanceProperty = instance;
                environmentField = environment;
                settingsField = settings;
                directionXField = directionX;
                directionYField = directionY;
                windSpeedField = speed;
                wetnessTargetField = wetness;
            }

            // The live EnviroManager (or null). Compared by reference to detect a replaced manager.
            internal object Manager => instanceProperty.GetValue(null);

            internal static EnviroAccessor TryCreate()
            {
                Type managerType = Type.GetType("Enviro.EnviroManager, " + EnviroAssembly);
                if (managerType == null) return null;
                PropertyInfo instance = managerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                FieldInfo environment = managerType.GetField("Environment", BindingFlags.Public | BindingFlags.Instance);
                if (instance == null || environment == null) return null;
                FieldInfo settings = environment.FieldType.GetField("Settings", BindingFlags.Public | BindingFlags.Instance);
                Type stateType = settings?.FieldType;
                FieldInfo x = stateType?.GetField("windDirectionX", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo y = stateType?.GetField("windDirectionY", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo speed = stateType?.GetField("windSpeed", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo wetness = stateType?.GetField("wetnessTarget", BindingFlags.Public | BindingFlags.Instance);
                return settings != null && x != null && y != null && speed != null && wetness != null
                    ? new EnviroAccessor(instance, environment, settings, x, y, speed, wetness)
                    : null;
            }

            internal bool TryRead(out WeatherState state)
            {
                state = default;
                object manager = instanceProperty.GetValue(null);
                object environment = manager != null ? environmentField.GetValue(manager) : null;
                object settings = environment != null ? settingsField.GetValue(environment) : null;
                if (settings == null) return false;
                state = new WeatherState(
                    (float)directionXField.GetValue(settings),
                    (float)directionYField.GetValue(settings),
                    (float)windSpeedField.GetValue(settings),
                    (float)wetnessTargetField.GetValue(settings));
                return true;
            }
        }
    }
}
