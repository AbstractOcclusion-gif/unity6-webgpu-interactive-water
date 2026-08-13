// WebGpuWater - Multi Boat sample helper.
//
// Keeps exactly one active BoatController, retargets the chase camera, and optionally translates
// directional input through that camera. It discovers active scene boats once at startup so sample
// authors can add or remove ready-boat prefabs without maintaining a second serialized list.
using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Demo Boat Switcher")]
    [DisallowMultipleComponent]
    public sealed class WaterDemoBoatSwitcher : MonoBehaviour
    {
        const string LogPrefix = "[WebGpuWater] ";
        const float HullLengthCameraDistanceMultiplier = 1.25f;

        [SerializeField] SimpleFollowCamera followCamera;
        [Tooltip("When enabled, WASD chooses a direction relative to the current camera view.")]
        [SerializeField] bool cameraRelativeDrive = true;

        BoatController[] _boats = Array.Empty<BoatController>();
        int _activeIndex;

        void Awake()
        {
            _boats = FindSceneBoats();
            if (_boats.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix}Multi Boat switcher found no active boat controllers in '{gameObject.scene.path}'.", this);
                enabled = false;
                return;
            }

            if (followCamera == null) followCamera = FindSceneFollowCamera();
            _activeIndex = FindInitialBoatIndex();
            SelectBoat(_activeIndex, false);
        }

        void Update()
        {
            if (!SwitchPressed()) return;
            SelectBoat((_activeIndex + 1) % _boats.Length, true);
        }

        BoatController[] FindSceneBoats()
        {
            BoatController[] found = FindObjectsByType<BoatController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var sceneBoats = new List<BoatController>(found.Length);
            foreach (BoatController boat in found)
            {
                if (boat == null || !boat.gameObject.activeInHierarchy || boat.gameObject.scene != gameObject.scene) continue;
                sceneBoats.Add(boat);
            }

            sceneBoats.Sort(CompareBoatNames);
            return sceneBoats.ToArray();
        }

        SimpleFollowCamera FindSceneFollowCamera()
        {
            SimpleFollowCamera[] cameras = FindObjectsByType<SimpleFollowCamera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (SimpleFollowCamera candidate in cameras)
            {
                if (candidate != null && candidate.gameObject.scene == gameObject.scene) return candidate;
            }

            Debug.LogWarning($"{LogPrefix}Multi Boat switcher found no follow camera in '{gameObject.scene.path}'.", this);
            return null;
        }

        int FindInitialBoatIndex()
        {
            if (followCamera != null && followCamera.target != null)
            {
                for (int index = 0; index < _boats.Length; index++)
                {
                    if (_boats[index].transform == followCamera.target) return index;
                }
            }

            for (int index = 0; index < _boats.Length; index++)
            {
                if (_boats[index].enabled) return index;
            }

            return 0;
        }

        void SelectBoat(int index, bool announce)
        {
            _activeIndex = index;
            BoatController selectedBoat = _boats[_activeIndex];
            Transform driveReference = cameraRelativeDrive && followCamera != null ? followCamera.transform : null;

            foreach (BoatController boat in _boats)
            {
                bool selected = boat == selectedBoat;
                boat.SetDriveReference(selected ? driveReference : null);
                boat.enabled = selected;
            }

            if (followCamera != null)
                followCamera.SetTarget(selectedBoat.transform, CalculateFramingDistance(selectedBoat));
            if (announce) Debug.Log($"{LogPrefix}Driving '{selectedBoat.name}'.", selectedBoat);
        }

        static float CalculateFramingDistance(BoatController boat)
        {
            Collider hullCollider = boat.GetComponent<Collider>();
            if (hullCollider == null) return 0f;

            Vector3 hullSize = hullCollider.bounds.size;
            float hullLength = Mathf.Max(hullSize.x, hullSize.y, hullSize.z);
            return hullLength * HullLengthCameraDistanceMultiplier;
        }

        static int CompareBoatNames(BoatController left, BoatController right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.name, right.name);

        static bool SwitchPressed()
        {
#if ENABLE_INPUT_SYSTEM
            bool keyboardPressed = Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame;
            bool gamepadPressed = Gamepad.current != null && Gamepad.current.leftShoulder.wasPressedThisFrame;
            return keyboardPressed || gamepadPressed;
#else
            return Input.GetKeyDown(KeyCode.Tab);
#endif
        }
    }
}
