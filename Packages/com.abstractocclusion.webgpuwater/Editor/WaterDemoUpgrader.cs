// WebGpuWater - one-shot editor tool: give every demo scene the FULL particle system so the
// WaterFoamParticles "Use Particles" master switch can be toggled in any of them.
//
// For each WaterVolume in a scene it ensures a wired WaterFoamParticles (GPU foam + spray), ensures a
// shared WaterSplashEmitter (drift droplets + crown) with the body's splash gate on, and assigns the
// shared "WaterFoamProfile 1" to both. Pure reuse of WaterBuildKit's own build/wire helpers, so the
// result is identical to what Create Water / Wire-Repair produce. Idempotent: re-running only fills
// gaps (a body that already has foam particles keeps them) and re-asserts the profile. Reversible via
// git - it writes the demo scenes under Samples/Demos/Scenes and may create shared splash materials.
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterDemoUpgrader
    {
        const string PackageRoot = "Packages/com.abstractocclusion.webgpuwater";
        const string ScenesFolder = PackageRoot + "/Samples/Demos/Scenes";
        // Foam particle materials (FoamParticles/FoamDensityComposite/FoamDroplet.mat) are loaded/created
        // here, next to the demos' other shared assets, instead of the Generated/ fallback.
        const string CommonFolder = PackageRoot + "/Samples/Demos/Common";
        const string Profile1Path = CommonFolder + "/WaterFoamProfile 1.asset";

        const string MenuActive = MenuRoot + "Demos/Upgrade Active Scene (Full Particles + Profile 1)";
        const string MenuAll = MenuRoot + "Demos/Upgrade All Demo Scenes (Full Particles + Profile 1)";

        [MenuItem(MenuActive, priority = 400)]
        static void UpgradeActiveScene()
        {
            WaterFoamProfile profile = LoadProfileOrWarn();
            if (profile == null) return;

            Scene scene = SceneManager.GetActiveScene();
            int bodies = UpgradeOpenScene(profile, out int added);
            if (bodies == 0)
            {
                Debug.LogWarning("[WebGpuWater] No WaterVolume in the active scene - nothing to upgrade.");
                return;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] '{scene.name}' upgraded: {bodies} body(ies), {added} component(s) added, Profile 1 assigned.");
        }

        [MenuItem(MenuAll, priority = 401)]
        static void UpgradeAllDemoScenes()
        {
            WaterFoamProfile profile = LoadProfileOrWarn();
            if (profile == null) return;

            // Let the user save (or discard) any in-progress scene before we start opening scenes.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { ScenesFolder });
            if (guids.Length == 0)
            {
                Debug.LogError($"[WebGpuWater] No scenes found under {ScenesFolder}.");
                return;
            }

            int scenesUpgraded = 0, totalBodies = 0, totalAdded = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int bodies = UpgradeOpenScene(profile, out int added);
                if (bodies == 0) continue;

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                scenesUpgraded++;
                totalBodies += bodies;
                totalAdded += added;
                Debug.Log($"[WebGpuWater] {System.IO.Path.GetFileNameWithoutExtension(path)}: {bodies} body(ies), {added} component(s) added.");
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGpuWater] Done - {scenesUpgraded} scene(s), {totalBodies} body(ies), {totalAdded} component(s) added. WaterFoamProfile 1 assigned everywhere.");
        }

        static WaterFoamProfile LoadProfileOrWarn()
        {
            var profile = AssetDatabase.LoadAssetAtPath<WaterFoamProfile>(Profile1Path);
            if (profile == null)
                Debug.LogError($"[WebGpuWater] 'WaterFoamProfile 1' not found at {Profile1Path}.");
            return profile;
        }

        // Ensure every WaterVolume in the currently open scene has the full particle system + the profile.
        // Returns the body count; reports how many components were newly added via the out param.
        static int UpgradeOpenScene(WaterFoamProfile profile, out int added)
        {
            added = 0;
            WaterVolume[] bodies = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            foreach (WaterVolume body in bodies)
            {
                // 1) GPU foam particles (wired to the shared Common materials). Skip if already present.
                if (body.GetComponent<WaterFoamParticles>() == null)
                {
                    if (AddFoamParticles(body, CommonFolder) != null) added++;
                    // Ambient foam/spray is gated on the body's turbulence Foam flag - without it the
                    // retrofitted system would simulate nothing, so match Create Water and enable it.
                    if (!body.Foam) body.Foam = true;
                }

                // 2) Shared splash emitter (drift droplets + crown). Reuse the scene's own if any, so we
                // never stack duplicate emitters; create one under this body when the scene has none.
                WaterSplashEmitter emitter = body.splashEmitter != null
                    ? body.splashEmitter
                    : Object.FindFirstObjectByType<WaterSplashEmitter>();
                if (emitter == null)
                {
                    emitter = CreateSplashEmitter(body.transform);
                    added++;
                }
                body.splashEmitter = emitter;
                body.provideSplashEmitter = true; // the body's splash gate on, so it actually emits

                // 3) One profile for both foam + splash.
                AssignFoamProfileToBody(body, profile);
                EditorUtility.SetDirty(body);
            }
            return bodies.Length;
        }
    }
}
#endif
