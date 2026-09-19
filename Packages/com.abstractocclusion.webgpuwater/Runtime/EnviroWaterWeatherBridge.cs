using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>
    /// Optional, dependency-free bridge from Enviro 3's live weather state to every WaterVolume.
    /// One instance is created automatically in play mode when the scene does not provide one.
    /// </summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Enviro 3 Weather Bridge")]
    [DefaultExecutionOrder(1000)]
    public sealed class EnviroWaterWeatherBridge : MonoBehaviour
    {
        const string AutoObjectName = "[WebGpuWater] Enviro 3 Weather Bridge (Auto)";
        static readonly int EnviroActiveId = Shader.PropertyToID("_EnviroActive");

        [Header("Wind")]
        [SerializeField] bool syncWind = true;
        [Tooltip("Water wind speed produced by Enviro's normalized wind speed of 1.")]
        [SerializeField, Min(0f)] float maximumWaterWindSpeed = 15f;

        [Header("Rain ripples")]
        [SerializeField] bool rainRipples = true;
        [Tooltip("Maximum ripple impacts per second at Enviro wetness target 1.")]
        [SerializeField, Range(0f, 60f)] float maximumDropsPerSecond = 18f;
        [SerializeField, Min(0.01f)] float rainAreaRadius = 24f;
        [SerializeField, Min(0.001f)] float dropRadius = 0.08f;
        [SerializeField, Min(0f)] float dropStrength = 0.015f;

        [Header("Lifecycle")]
        [Tooltip("Put each body's authored wind back when this bridge is disabled.")]
        [SerializeField] bool restoreAuthoredWindOnDisable = true;

        readonly Dictionary<WaterVolume, Vector2> authoredWind = new();
        EnviroAccessor enviro;
        float bindRetryAt;
        float dropDebt;

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
                return;

            if ((enviro == null || !enviro.TryRead(out _)) && Time.unscaledTime >= bindRetryAt)
            {
                enviro = EnviroAccessor.TryCreate();
                bindRetryAt = Time.unscaledTime + 1f;
            }
            if (enviro == null || !enviro.TryRead(out WeatherState weather))
                return;

            ApplyWind(weather);
            ApplyRain(weather.wetnessTarget);
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

        void ApplyRain(float wetnessTarget)
        {
            if (!rainRipples || maximumDropsPerSecond <= 0f) return;
            float rain = Mathf.Clamp01(wetnessTarget);
            if (rain <= 0.001f) { dropDebt = 0f; return; }

            dropDebt = Mathf.Min(dropDebt + rain * maximumDropsPerSecond * Time.deltaTime, 8f);
            int count = Mathf.Min(Mathf.FloorToInt(dropDebt), 4);
            if (count <= 0) return;
            dropDebt -= count;

            Camera camera = Camera.main;
            Vector3 centre = camera != null ? camera.transform.position : Vector3.zero;
            for (int drop = 0; drop < count; drop++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * rainAreaRadius;
                float strength = -dropStrength * Mathf.Lerp(0.35f, 1f, rain);
                for (int i = 0; i < WaterVolume.Bodies.Count; i++)
                {
                    WaterVolume body = WaterVolume.Bodies[i];
                    if (body != null && body.isActiveAndEnabled)
                        body.AddRipple(centre.x + offset.x, centre.z + offset.y, dropRadius, strength);
                }
            }
        }

        void OnDisable()
        {
            if (restoreAuthoredWindOnDisable)
            {
                foreach (var pair in authoredWind)
                    if (pair.Key != null)
                        pair.Key.SetWeatherWind(pair.Value.x, pair.Value.y);
            }
            authoredWind.Clear();
            dropDebt = 0f;
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

            internal static EnviroAccessor TryCreate()
            {
                Type managerType = Type.GetType("Enviro.EnviroManager, Enviro3.Runtime");
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
