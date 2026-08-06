// WebGpuWater - feature-showcase station cycler (Crest-Examples-style).
// One scene, many stations: each station is an INACTIVE template under the showcase root; exactly
// one is alive at a time as an instantiated clone, so every visit starts from fresh sim state and
// tearing a station down is a single destroy (WaterVolume's OnDisable releases its GPU resources
// and re-elects the primary). N / M or the arrow keys cycle; the custom inspector adds
// Previous / Next buttons that also work in edit mode (edit-mode clones are never saved).
// Supports both input backends, mirroring FlyCamera's input abstraction.
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("")] // showcase plumbing: created by the showcase builder, not hand-placed
    internal sealed class WaterShowcaseController : MonoBehaviour
    {
        // Overlay layout: a caption box pinned to the bottom-left corner.
        const float PanelMargin = 12f;
        const float PanelPadding = 10f;
        const float PanelWidth = 480f;
        const float PanelHeight = 96f;
        const string CycleHint = "N / M  (or Left / Right arrow)  -  previous / next station";

        [Tooltip("Inactive station templates, in showcase order (wired by the showcase builder).")]
        [SerializeField] internal List<WaterShowcaseStation> stations = new List<WaterShowcaseStation>();

        [Tooltip("Scene orbit camera the stations frame (wired by the showcase builder).")]
        [SerializeField] internal OrbitCamera orbitCamera;

        [Tooltip("Scene sun; stations may override its orientation (wired by the showcase builder).")]
        [SerializeField] internal Light sun;

        int _index;
        GameObject _current;
        Quaternion _sunHomeRotation;
        bool _sunHomeCached;

        static GUIStyle _overlayStyle; // built lazily in OnGUI (GUI.skin only exists there)

        internal int StationCount => stations.Count;
        internal int CurrentIndex => _index;

        void OnEnable()
        {
            if (stations.Count == 0)
            {
                Debug.LogWarning("WaterShowcaseController: no stations wired; disabling.", this);
                enabled = false;
                return;
            }
            ShowStation(_index);
        }

        void OnDisable() => DestroyCurrent();

        void Update()
        {
            if (CyclePressed(forward: false)) Cycle(reverse: true);
            else if (CyclePressed(forward: true)) Cycle(reverse: false);
        }

        internal void Cycle(bool reverse)
        {
            if (stations.Count == 0) return;
            int next = _index + (reverse ? -1 : 1);
            if (next < 0) next = stations.Count - 1;
            if (next >= stations.Count) next = 0;
            ShowStation(next);
        }

        // Shared by play-mode cycling and the inspector's edit-mode preview buttons.
        internal void ShowStation(int index)
        {
            if (stations.Count == 0) return;
            _index = Mathf.Clamp(index, 0, stations.Count - 1);
            WaterShowcaseStation station = stations[_index];
            if (station == null)
            {
                Debug.LogError($"WaterShowcaseController: station {_index} reference is missing.", this);
                return;
            }

            DestroyCurrent(); // tear the old body down BEFORE the new one enables (clean primary handoff)
            CacheSunHome();

            _current = Instantiate(station.gameObject, transform, true);
            _current.name = station.displayName;
            // Edit-mode previews must never be serialized into the scene alongside their template.
            if (!Application.isPlaying) _current.hideFlags = HideFlags.DontSave;
            _current.SetActive(true);

            ApplyView(station);
            ApplySun(station);
        }

        void DestroyCurrent()
        {
            if (_current == null) return;
            _current.SetActive(false); // run OnDisable teardown NOW; the destroy may land end-of-frame
            if (Application.isPlaying) Destroy(_current);
            else DestroyImmediate(_current);
            _current = null;
        }

        void ApplyView(WaterShowcaseStation station)
        {
            if (orbitCamera == null) return;
            orbitCamera.pivot = station.orbitPivot;
            orbitCamera.pivotTarget = null;
            orbitCamera.minDistance = station.orbitMinDistance;
            orbitCamera.maxDistance = station.orbitMaxDistance;
            orbitCamera.SetView(station.orbitPitch, station.orbitYaw, station.orbitDistance);
        }

        void ApplySun(WaterShowcaseStation station)
        {
            if (sun == null) return;
            sun.transform.rotation = station.overrideSun ? Quaternion.Euler(station.sunEuler)
                                                         : _sunHomeRotation;
        }

        // The shared sun pose is captured once, before any station override touches it, so
        // stations without an override always restore the scene's authored sun.
        void CacheSunHome()
        {
            if (_sunHomeCached || sun == null) return;
            _sunHomeRotation = sun.transform.rotation;
            _sunHomeCached = true;
        }

        static bool CyclePressed(bool forward)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard == null) return false;
            return forward
                ? keyboard.mKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame
                : keyboard.nKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame;
#else
            return forward
                ? Input.GetKeyDown(KeyCode.M) || Input.GetKeyDown(KeyCode.RightArrow)
                : Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.LeftArrow);
#endif
        }

        void OnGUI()
        {
            if (stations.Count == 0 || _index >= stations.Count) return;
            WaterShowcaseStation station = stations[_index];
            if (station == null) return;

            _overlayStyle ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };

            var panel = new Rect(PanelMargin, Screen.height - PanelHeight - PanelMargin,
                                 PanelWidth, PanelHeight);
            GUI.Box(panel, string.Empty);
            var text = new Rect(panel.x + PanelPadding, panel.y + PanelPadding,
                                panel.width - 2f * PanelPadding, panel.height - 2f * PanelPadding);
            GUI.Label(text,
                $"<b>{_index + 1}/{stations.Count}  {station.displayName}</b>\n" +
                $"{station.description}\n<i>{CycleHint}</i>",
                _overlayStyle);
        }
    }
}
