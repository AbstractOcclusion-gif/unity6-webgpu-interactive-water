// WebGpuWater build kit - the splash range demo: a water body and the catapult controller,
// one menu click. The Unity twin of the three.js splash-range scene (Monty Python catapult:
// cannonball, cow, chicken and the trojan rabbit) - every throwable exercises breach splash,
// ripple rings, crest flecks, buoyancy and wakes at once, which is exactly what makes it a
// good splash-precision test bed.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string SplashRangeMenuPath = MenuRoot + "Build Splash Range Demo";
        const int SplashRangeMenuPriority = 430;
        const string SplashRangeRootName = "Splash Range Demo";
        const string SplashRangeWaterName = "Water";
        const string SplashRangeControllerName = "Splash Range";
        const string SplashRangeCatapultName = "Catapult Point";
        static readonly Vector3 SplashRangeCatapultOffset = new Vector3(-15f, 2.5f, 0f);
        static readonly Vector3 SplashRangeExtent = new Vector3(12f, 2f, 12f);
        static readonly Vector3 SplashRangeWaterPosition = Vector3.zero;

        [MenuItem(SplashRangeMenuPath, false, SplashRangeMenuPriority)]
        static void BuildSplashRangeDemo()
        {
            var root = NewUndoableGameObject(SplashRangeRootName);
            if (!CreateContext(root.transform, out BuildContext ctx, Gen, buildPoolMaterial: false))
            {
                Undo.DestroyObjectImmediate(root);
                return;
            }

            WaterVolume body = CreateWaterBody(ctx, root.transform, SplashRangeWaterName,
                                               SplashRangeWaterPosition, SplashRangeExtent,
                                               primary: true, withPool: false, withGodRays: false);

            var controllerGO = NewUndoableGameObject(SplashRangeControllerName);
            controllerGO.transform.SetParent(root.transform);
            var controller = controllerGO.AddComponent<WaterSplashRange>();
            controller.waterBody = body;
            // The body owns its splash emitter (CreateWaterBody rigs it under the body root);
            // wire it explicitly so every thrown object's breach splash uses the same look.
            Transform bodyRoot = body.transform.parent;
            controller.splashEmitter = bodyRoot != null
                ? bodyRoot.GetComponentInChildren<WaterSplashEmitter>()
                : null;

            // A movable catapult marker: the controller throws from EXACTLY here. Delete it
            // (or clear the field) to fall back to the computed edge point with spread.
            var catapultGO = NewUndoableGameObject(SplashRangeCatapultName);
            catapultGO.transform.SetParent(root.transform);
            catapultGO.transform.position = SplashRangeWaterPosition + SplashRangeCatapultOffset;
            controller.launchPoint = catapultGO.transform;

            Selection.activeGameObject = controllerGO;
            EditorGUIUtility.PingObject(controllerGO);
        }
    }
}
