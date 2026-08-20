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
        BoatRuntimeState[] _boatStates = Array.Empty<BoatRuntimeState>();
        int _activeIndex;
        BoatTouchDriver _touchDriver;

        void Awake()
        {
            _boats = FindSceneBoats();
            if (_boats.Length == 0)
            {
                Debug.LogWarning($"{LogPrefix}Multi Boat switcher found no active boat controllers in '{gameObject.scene.path}'.", this);
                enabled = false;
                return;
            }

            _boatStates = CreateBoatStates(_boats);
            if (followCamera == null) followCamera = FindSceneFollowCamera();
            _activeIndex = FindInitialBoatIndex();
            // Touch drive for phone/tablet/browser builds: spawned here so every boat demo
            // scene gains the on-screen stick without a scene edit. Inert without a touchscreen.
            _touchDriver = gameObject.AddComponent<BoatTouchDriver>();
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

            foreach (BoatRuntimeState boatState in _boatStates)
            {
                bool selected = boatState.Controller == selectedBoat;
                boatState.SetSelected(selected, selected ? driveReference : null);
            }

            if (followCamera != null)
                followCamera.SetTarget(selectedBoat.transform, CalculateFramingDistance(selectedBoat));
            if (_touchDriver != null) _touchDriver.SetTargets(selectedBoat, followCamera);
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

        static BoatRuntimeState[] CreateBoatStates(BoatController[] boats)
        {
            var states = new BoatRuntimeState[boats.Length];
            for (int index = 0; index < boats.Length; index++)
                states[index] = new BoatRuntimeState(boats[index]);
            return states;
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

        sealed class BoatRuntimeState
        {
            readonly Rigidbody _rigidbody;
            readonly bool _originalIsKinematic;
            readonly BehaviourState[] _simulationBehaviours;
            Vector3 _linearVelocity;
            Vector3 _angularVelocity;
            bool _simulationEnabled = true;

            public BoatController Controller { get; }

            public BoatRuntimeState(BoatController controller)
            {
                Controller = controller;
                _rigidbody = controller.GetComponent<Rigidbody>();
                _originalIsKinematic = _rigidbody != null && _rigidbody.isKinematic;
                _simulationBehaviours = FindSimulationBehaviours(controller);
            }

            public void SetSelected(bool selected, Transform driveReference)
            {
                if (selected) EnableSimulation();
                else DisableSimulation();

                Controller.SetDriveReference(driveReference);
                Controller.enabled = selected;
            }

            void EnableSimulation()
            {
                if (_simulationEnabled) return;

                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = _originalIsKinematic;
                    if (!_originalIsKinematic)
                    {
                        _rigidbody.linearVelocity = _linearVelocity;
                        _rigidbody.angularVelocity = _angularVelocity;
                        _rigidbody.WakeUp();
                    }
                }

                SetSimulationBehavioursEnabled(true);
                _simulationEnabled = true;
            }

            void DisableSimulation()
            {
                if (!_simulationEnabled) return;

                SetSimulationBehavioursEnabled(false);
                if (_rigidbody != null && !_rigidbody.isKinematic)
                {
                    _linearVelocity = _rigidbody.linearVelocity;
                    _angularVelocity = _rigidbody.angularVelocity;
                    _rigidbody.isKinematic = true;
                }

                _simulationEnabled = false;
            }

            void SetSimulationBehavioursEnabled(bool enabled)
            {
                foreach (BehaviourState behaviourState in _simulationBehaviours)
                    behaviourState.Behaviour.enabled = enabled && behaviourState.OriginallyEnabled;
            }

            static BehaviourState[] FindSimulationBehaviours(BoatController controller)
            {
                var behaviours = new List<Behaviour>();
                AddBehaviours(controller.GetComponentsInChildren<WaterBuoyancy>(true), behaviours);
                AddBehaviours(controller.GetComponentsInChildren<WaterInteractable>(true), behaviours);
                AddBehaviours(controller.GetComponentsInChildren<WaterSplash>(true), behaviours);
                AddBehaviours(controller.GetComponentsInChildren<WaterSphereInteractor>(true), behaviours);
                AddBehaviours(controller.GetComponentsInChildren<WaterSprayPump>(true), behaviours);

                var states = new BehaviourState[behaviours.Count];
                for (int index = 0; index < behaviours.Count; index++)
                    states[index] = new BehaviourState(behaviours[index]);
                return states;
            }

            static void AddBehaviours<T>(T[] source, List<Behaviour> destination) where T : Behaviour
            {
                foreach (T behaviour in source)
                    destination.Add(behaviour);
            }
        }

        readonly struct BehaviourState
        {
            public Behaviour Behaviour { get; }
            public bool OriginallyEnabled { get; }

            public BehaviourState(Behaviour behaviour)
            {
                Behaviour = behaviour;
                OriginallyEnabled = behaviour.enabled;
            }
        }
    }
}
