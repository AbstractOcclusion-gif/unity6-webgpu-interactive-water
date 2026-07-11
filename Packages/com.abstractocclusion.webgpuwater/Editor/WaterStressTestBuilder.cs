// WebGpuWater - one-click "ocean buoyancy stress test" scene.
//
// Menu: Window > AbstractOcclusion > WebGpuWater > Ocean Buoyancy Stress Test
//
// Builds an open-water ocean and a Stress Rig carrying the parametric floater spawner + the metrics overlay,
// mirroring how WaterWizardWindow.CreateWater() assembles a scene. Press Play, then dial the spawner grid on
// the Stress Rig to push the probe-buoyancy budget while watching the overlay. Probe tier only for now.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterStressTestBuilder
    {
        const string MenuPath = "Window/AbstractOcclusion/WebGpuWater/Ocean Buoyancy Stress Test";
        const string RootObjectName = "Ocean Buoyancy Stress Test";
        const string OceanName = "Ocean";
        const string RigName = "Stress Rig";

        static readonly Vector3 OceanExtent = new Vector3(40f, 4f, 40f); // half-extents; big enough to read as open water

        // Orbit pose framing the whole grid (the default demo pose is tuned for a small pool).
        const float CameraDistance = 34f;
        const float CameraPitch = -22f;
        const float CameraYaw = -200f;
        static readonly Vector3 CameraPivot = new Vector3(0f, 0f, 0f);

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
                                               primary: true, withPool: false, withGodRays: false, withFoamParticles: false);
            body.rippleQuality = WaterVolume.RippleQuality.High;
            EnableOpenWater(body);
            EditorUtility.SetDirty(body);

            FrameGrid(ctx);
            BuildStressRig(root.transform);

            Selection.activeObject = root;
            EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Ocean buoyancy stress test built ({RootObjectName}). Press Play, then tune the " +
                      $"'{RigName}' spawner grid while watching the overlay.");
        }

        // openWater + horizon clipmap, set through SerializedObject like the wizard (private ocean block).
        static void EnableOpenWater(WaterVolume body)
        {
            var serialized = new SerializedObject(body);
            serialized.FindProperty("ocean.openWater").boolValue = true;
            serialized.FindProperty("ocean.unboundedOcean").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        static void FrameGrid(BuildContext ctx)
        {
            if (ctx.Orbit == null) return;
            ctx.Orbit.pivot = CameraPivot;
            ctx.Orbit.distance = CameraDistance;
            ctx.Orbit.pitch = CameraPitch;
            ctx.Orbit.yaw = CameraYaw;
        }

        static void BuildStressRig(Transform parent)
        {
            var rig = new GameObject(RigName);
            rig.transform.SetParent(parent, worldPositionStays: false);

            var overlay = rig.AddComponent<WaterMetricsOverlay>();
            var spawner = rig.AddComponent<WaterBuoyancyStressSpawner>();
            spawner.overlay = overlay; // internal field, reachable from the Editor asmdef (InternalsVisibleTo)
        }
    }
}
