// WebGpuWater - deterministic runtime controller for the generated multi-body stress scene.
// Body-count changes only enable cumulative authoring groups; water settings and resources are
// never rewritten, so 1/5/15/30 comparisons exercise the same authored bodies and camera route.
using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Multi-Body Stress Scenario")]
    [DisallowMultipleComponent]
    public sealed class WaterStressScenarioController : MonoBehaviour
    {
        const int ScenarioCount = 4;
        const int FirstScenarioIndex = 0;
        const int DefaultScenarioIndex = ScenarioCount - 1;
        const int OneBodyScenarioIndex = 0;
        const int FiveBodyScenarioIndex = 1;
        const int FifteenBodyScenarioIndex = 2;
        const int ThirtyBodyScenarioIndex = 3;
        const int ScenarioNotFound = -1;
        const int OneBodyCount = 1;
        const int FiveBodyCount = 5;
        const int FifteenBodyCount = 15;
        const int ThirtyBodyCount = 30;
        const float MinimumPositiveValue = 0.001f;
        const float FullTurnRadians = Mathf.PI * 2f;
        const float VerticalOscillationsPerLoop = 2f;
        const float ControlsLeft = 12f;
        const float ControlsTop = 12f;
        const float ControlsWidth = 440f;
        const float ControlsHeight = 96f;
        const int ControlsFontSize = 16;
        const float DefaultRouteRadiusX = 70f;
        const float DefaultRouteRadiusZ = 58f;
        const float DefaultRouteHeight = 30f;
        const float DefaultRouteHeightVariation = 6f;
        const float DefaultRouteDurationSeconds = 24f;
        const string ConfigurationError =
            "Water stress scenario requires one camera and exactly four cumulative tier roots.";
        const string UnsupportedBodyCountError =
            "Supported stress body counts are 1, 5, 15 and 30.";
        const string ControlsPrefix = "Multi-Body Water Stress";
        const string ControlsBodySuffix = " bodies";
        const string ControlsKeys = "[1] 1   [2] 5   [3] 15   [4] 30";
        const string ControlsCameraRunning = "[Space] camera path: RUNNING   [R] restart   [H] hide";
        const string ControlsCameraPaused = "[Space] camera path: PAUSED    [R] restart   [H] hide";
        const string ControlsTitleSeparator = " — ";

        static readonly int[] BodyCounts =
        {
            OneBodyCount,
            FiveBodyCount,
            FifteenBodyCount,
            ThirtyBodyCount,
        };

        [Tooltip("Camera that follows the deterministic profiling route.")]
        [SerializeField] Camera targetCamera;
        [Tooltip("Cumulative roots: body 1, bodies 2-5, bodies 6-15 and bodies 16-30.")]
        [SerializeField] GameObject[] cumulativeTierRoots = new GameObject[ScenarioCount];
        [SerializeField, Range(FirstScenarioIndex, DefaultScenarioIndex)]
        int initialScenarioIndex = DefaultScenarioIndex;
        [SerializeField] bool animateCamera = true;
        [SerializeField] bool showControls = true;
        [SerializeField] Vector3 routeCenter;
        [SerializeField] Vector2 routeRadii =
            new Vector2(DefaultRouteRadiusX, DefaultRouteRadiusZ);
        [SerializeField] float routeHeight = DefaultRouteHeight;
        [SerializeField] float routeHeightVariation = DefaultRouteHeightVariation;
        [SerializeField] float routeDurationSeconds = DefaultRouteDurationSeconds;

        int _activeScenarioIndex = DefaultScenarioIndex;
        float _routeElapsedSeconds;
        string _controlsText = string.Empty;
        GUIStyle _controlsStyle;

        public int ActiveBodyCount => BodyCounts[_activeScenarioIndex];
        public bool CameraPathRunning => animateCamera;

        internal void Configure(Camera camera, GameObject[] tierRoots, Vector3 center,
                                Vector2 radii, float height, float heightVariation,
                                float durationSeconds, int scenarioIndex)
        {
            ValidateConfiguration(camera, tierRoots, radii, durationSeconds, scenarioIndex);
            targetCamera = camera;
            cumulativeTierRoots = (GameObject[])tierRoots.Clone();
            routeCenter = center;
            routeRadii = radii;
            routeHeight = height;
            routeHeightVariation = Mathf.Max(0f, heightVariation);
            routeDurationSeconds = durationSeconds;
            initialScenarioIndex = scenarioIndex;
            _activeScenarioIndex = scenarioIndex;
            ApplyScenarioRoots();
            ApplyCameraPose(0f);
            RefreshControlsText();
        }

        public void SetBodyCount(int bodyCount)
        {
            EnsureConfigured();
            int scenarioIndex = FindScenarioIndex(bodyCount);
            if (scenarioIndex < FirstScenarioIndex)
                throw new ArgumentOutOfRangeException(nameof(bodyCount), bodyCount,
                                                      UnsupportedBodyCountError);
            _activeScenarioIndex = scenarioIndex;
            ApplyScenarioRoots();
            RefreshControlsText();
        }

        void Start()
        {
            if (!Application.isPlaying) return;
            if (!IsConfigured())
            {
                Debug.LogError(ConfigurationError, this);
                enabled = false;
                return;
            }
            _activeScenarioIndex = Mathf.Clamp(initialScenarioIndex, FirstScenarioIndex,
                                               DefaultScenarioIndex);
            ApplyScenarioRoots();
            RestartCameraPath();
        }

        void Update()
        {
            ReadInput();
            if (!animateCamera || targetCamera == null) return;
            _routeElapsedSeconds += Time.unscaledDeltaTime;
            float normalizedTime = Mathf.Repeat(_routeElapsedSeconds, routeDurationSeconds)
                                 / routeDurationSeconds;
            ApplyCameraPose(normalizedTime);
        }

        void ReadInput()
        {
            if (PressedScenarioKey(out int scenarioIndex))
                SetBodyCount(BodyCounts[scenarioIndex]);
            if (PressedCameraToggle())
            {
                animateCamera = !animateCamera;
                RefreshControlsText();
            }
            if (PressedRestart()) RestartCameraPath();
            if (PressedVisibilityToggle()) showControls = !showControls;
        }

        void RestartCameraPath()
        {
            _routeElapsedSeconds = 0f;
            ApplyCameraPose(0f);
            RefreshControlsText();
        }

        void ApplyScenarioRoots()
        {
            for (int i = 0; i < cumulativeTierRoots.Length; i++)
            {
                GameObject tierRoot = cumulativeTierRoots[i];
                if (tierRoot != null) tierRoot.SetActive(i <= _activeScenarioIndex);
            }
        }

        void ApplyCameraPose(float normalizedTime)
        {
            if (targetCamera == null) return;
            float angle = normalizedTime * FullTurnRadians;
            float heightOffset = Mathf.Sin(angle * VerticalOscillationsPerLoop)
                               * routeHeightVariation;
            Vector3 position = routeCenter + new Vector3(
                Mathf.Cos(angle) * routeRadii.x,
                routeHeight + heightOffset,
                Mathf.Sin(angle) * routeRadii.y);
            targetCamera.transform.position = position;
            targetCamera.transform.LookAt(routeCenter, Vector3.up);
        }

        void RefreshControlsText()
        {
            _controlsText = ControlsPrefix + ControlsTitleSeparator + ActiveBodyCount
                          + ControlsBodySuffix + "\n"
                          + ControlsKeys + "\n"
                          + (animateCamera ? ControlsCameraRunning : ControlsCameraPaused);
        }

        void OnGUI()
        {
            if (!Application.isPlaying || !showControls || _controlsText.Length == 0) return;
            _controlsStyle ??= new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = ControlsFontSize,
                richText = false,
            };
            GUI.Box(new Rect(ControlsLeft, ControlsTop, ControlsWidth, ControlsHeight),
                    _controlsText, _controlsStyle);
        }

        bool IsConfigured()
        {
            if (targetCamera == null || cumulativeTierRoots == null ||
                cumulativeTierRoots.Length != ScenarioCount) return false;
            for (int i = 0; i < cumulativeTierRoots.Length; i++)
                if (cumulativeTierRoots[i] == null) return false;
            return routeRadii.x > MinimumPositiveValue && routeRadii.y > MinimumPositiveValue
                && routeDurationSeconds > MinimumPositiveValue;
        }

        void EnsureConfigured()
        {
            if (!IsConfigured()) throw new InvalidOperationException(ConfigurationError);
        }

        static void ValidateConfiguration(Camera camera, GameObject[] tierRoots, Vector2 radii,
                                          float durationSeconds, int scenarioIndex)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (tierRoots == null) throw new ArgumentNullException(nameof(tierRoots));
            if (tierRoots.Length != ScenarioCount)
                throw new ArgumentException(ConfigurationError, nameof(tierRoots));
            for (int i = 0; i < tierRoots.Length; i++)
                if (tierRoots[i] == null)
                    throw new ArgumentException(ConfigurationError, nameof(tierRoots));
            if (radii.x <= MinimumPositiveValue || radii.y <= MinimumPositiveValue)
                throw new ArgumentOutOfRangeException(nameof(radii));
            if (durationSeconds <= MinimumPositiveValue)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            if (scenarioIndex < FirstScenarioIndex || scenarioIndex > DefaultScenarioIndex)
                throw new ArgumentOutOfRangeException(nameof(scenarioIndex));
        }

        static int FindScenarioIndex(int bodyCount)
        {
            for (int i = 0; i < BodyCounts.Length; i++)
                if (BodyCounts[i] == bodyCount) return i;
            return ScenarioNotFound;
        }

        static bool PressedScenarioKey(out int scenarioIndex)
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.digit1Key.wasPressedThisFrame)
                { scenarioIndex = OneBodyScenarioIndex; return true; }
                if (keyboard.digit2Key.wasPressedThisFrame)
                { scenarioIndex = FiveBodyScenarioIndex; return true; }
                if (keyboard.digit3Key.wasPressedThisFrame)
                { scenarioIndex = FifteenBodyScenarioIndex; return true; }
                if (keyboard.digit4Key.wasPressedThisFrame)
                { scenarioIndex = ThirtyBodyScenarioIndex; return true; }
            }
#else
            if (Input.GetKeyDown(KeyCode.Alpha1))
            { scenarioIndex = OneBodyScenarioIndex; return true; }
            if (Input.GetKeyDown(KeyCode.Alpha2))
            { scenarioIndex = FiveBodyScenarioIndex; return true; }
            if (Input.GetKeyDown(KeyCode.Alpha3))
            { scenarioIndex = FifteenBodyScenarioIndex; return true; }
            if (Input.GetKeyDown(KeyCode.Alpha4))
            { scenarioIndex = ThirtyBodyScenarioIndex; return true; }
#endif
            scenarioIndex = ScenarioNotFound;
            return false;
        }

        static bool PressedCameraToggle()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }

        static bool PressedRestart()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.R);
#endif
        }

        static bool PressedVisibilityToggle()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.H);
#endif
        }
    }
}
