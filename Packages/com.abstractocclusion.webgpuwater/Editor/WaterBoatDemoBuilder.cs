// WebGpuWater - one-click "ocean boat demo" scene.
//
// Menu: Window > AbstractOcclusion > WebGpuWater > Ocean Boat Demo
//
// Builds an open-water ocean and a drivable boat (probe buoyancy for float/roll + BoatController for drive)
// with a yaw-only chase camera, mirroring how WaterWizardWindow.CreateWater() assembles a scene. Drive with
// W/S (throttle) and A/D (steer). The boat floats and self-rights on the WaterBuoyancy probes; the mesh-
// slicing hull tier is a later addition that would upgrade the hull response.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterBoatDemoBuilder
    {
        const string MenuPath = "Window/AbstractOcclusion/WebGpuWater/Ocean Boat Demo";
        const string RootObjectName = "Ocean Boat Demo";
        const string OceanName = "Ocean";
        const string BoatName = "Boat";
        const string CabinName = "Cabin";

        static readonly Vector3 OceanExtent = new Vector3(40f, 4f, 40f);
        static readonly Vector3 HullScale = new Vector3(2f, 0.6f, 5f);   // wide, low, long
        static readonly Vector3 CabinScale = new Vector3(1.2f, 0.5f, 1.8f);
        static readonly Vector3 CabinLocalPosition = new Vector3(0f, 0.55f, -0.4f);
        static readonly Vector3 BoatSpawn = new Vector3(0f, 1f, 0f);     // dropped just above the surface
        const float BoatMass = 200f;
        const float BoatBuoyancy = 2.6f;
        const int BoatSamplesPerAxis = 3;                                 // 27 probes -> good roll/pitch + length torque
        const float BoatObjectWidth = 5f;                                 // ignore ripples shorter than the hull

        [MenuItem(MenuPath)]
        static void Build()
        {
            var root = new GameObject(RootObjectName);
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildPoolMaterial: false))
            {
                Object.DestroyImmediate(root);
                return;
            }

            WaterVolume body = CreateWaterBody(ctx, root.transform, OceanName, Vector3.zero, OceanExtent,
                                               primary: true, withPool: false, withGodRays: false, withFoamParticles: true);
            body.rippleQuality = WaterVolume.RippleQuality.High;
            EnableOpenWater(body);
            EditorUtility.SetDirty(body);

            Transform boat = BuildBoat(root.transform);
            body.simWindowFocus = boat; // centre the ripple window on the boat, not the trailing camera
            AttachChaseCamera(ctx, boat);

            Selection.activeObject = root;
            EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Ocean boat demo built ({RootObjectName}). Press Play - drive with W/S (throttle) " +
                      "and A/D (steer).");
        }

        static void EnableOpenWater(WaterVolume body)
        {
            var serialized = new SerializedObject(body);
            serialized.FindProperty("ocean.openWater").boolValue = true;
            serialized.FindProperty("ocean.unboundedOcean").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static Transform BuildBoat(Transform parent)
        {
            var boat = GameObject.CreatePrimitive(PrimitiveType.Cube);
            boat.name = BoatName;
            boat.transform.SetParent(parent, worldPositionStays: false);
            boat.transform.position = BoatSpawn;
            boat.transform.localScale = HullScale;

            AddCabin(boat.transform);

            var rigidbody = boat.AddComponent<Rigidbody>();
            rigidbody.mass = BoatMass;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

            var buoyancy = boat.AddComponent<WaterBuoyancy>();
            buoyancy.buoyancy = BoatBuoyancy;
            buoyancy.samplesPerAxis = BoatSamplesPerAxis;
            buoyancy.objectWidth = BoatObjectWidth;
            buoyancy.surfaceRelativeDrag = true;
            buoyancy.ignoreInteractiveRipples = true; // don't let the boat's own wake ripples propel it

            boat.AddComponent<BoatController>();
            boat.AddComponent<WaterMembership>();
            boat.AddComponent<WaterInteractable>(); // wake ripples
            boat.AddComponent<WaterSplash>();
            return boat.transform;
        }

        // A visual-only cabin so the boat reads as a boat; its collider is removed so it never affects physics.
        static void AddCabin(Transform boat)
        {
            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = CabinName;
            cabin.transform.SetParent(boat, worldPositionStays: false);
            cabin.transform.localPosition = CabinLocalPosition;
            // Local scale is expressed in the hull's already-scaled space; divide out so the cabin reads at
            // world CabinScale rather than being stretched by the long hull.
            cabin.transform.localScale = new Vector3(
                CabinScale.x / boat.localScale.x,
                CabinScale.y / boat.localScale.y,
                CabinScale.z / boat.localScale.z);
            Object.DestroyImmediate(cabin.GetComponent<Collider>());
        }

        // Swap the build kit's default orbit rig for a yaw-only chase camera locked to the boat. The orbit is
        // disabled rather than destroyed because the WaterVolume holds a reference to it (volume.orbit).
        static void AttachChaseCamera(BuildContext ctx, Transform boat)
        {
            if (ctx.Camera == null) return;
            if (ctx.Orbit != null) ctx.Orbit.enabled = false;

            var follow = ctx.Camera.gameObject.AddComponent<SimpleFollowCamera>();
            follow.target = boat;
        }
    }
}
