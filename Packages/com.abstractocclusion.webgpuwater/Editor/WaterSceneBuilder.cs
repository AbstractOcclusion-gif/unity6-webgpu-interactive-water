// WebGL Water - editor utilities backing the Water Wizard window.
//
// Menu entries were removed in favour of a single wizard (see WaterWizardWindow); these
// methods are the retrofit/one-off operations the wizard exposes as buttons. Scene CREATION
// lives in the wizard itself - this file only holds operations that act on an EXISTING scene
// selection or prefab. Thin layer over WaterBuildKit (which owns the reusable generators).
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;
using static AbstractOcclusion.WebGpuWater.Editor.WaterBuildKit;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterSceneBuilder
    {
        // World-space gap between the primary body's edge and a newly added secondary body,
        // so the two footprints never touch (touching footprints made BodyContaining ambiguous).
        const float SecondaryBodyGapMeters = 1f;
        const string WaterVolumePrefabPath = WaterBuildKit.Root + "/WaterVolume.prefab";
        const string WaterVolumeObjectName = "WaterVolume";
        const string DemoMaterialsRoot = WaterBuildKit.Root + "/Demos/Materials/";

        // A tidy, reusable single-body prefab: one WaterVolume + its two water renderers, with the
        // asset refs baked in. Scene refs (camera, sun) resolve at runtime, so it works when dropped
        // into a scene.
        internal static void CreateWaterVolumePrefab()
        {
            // Asset half only (TryBuildSharedAssets): a prefab build must not rig a camera/sun/
            // splash into the open scene the way CreateContext does.
            if (!TryBuildSharedAssets(Gen, buildPoolMaterial: false, out BuildContext ctx)) return;

            // The build is temporary scene state: the creators register undo entries, so reverting
            // the group below both destroys the temp objects AND leaves no stale undo steps behind.
            int undoGroup = Undo.GetCurrentGroup();
            var root = NewUndoableGameObject(WaterVolumeObjectName); // temp build object, reverted below
            var volume = root.AddComponent<WaterVolume>();
            var above = CreateRenderer(SurfaceAboveName, ctx.Grid, ctx.MatAbove, root.transform);
            var under = CreateRenderer(SurfaceUnderName, ctx.Grid, ctx.MatUnder, root.transform);

            // Same wiring block as the wizard body (one source; scene refs resolve at runtime).
            WireWaterVolumeAssets(volume, ctx.Shaders, ctx.Grid, ctx.Tiles, ctx.Sky, ctx.Quality);
            volume.surfaceAbove = above.GetComponent<Renderer>();
            volume.surfaceUnder = under.GetComponent<Renderer>();
            volume.IsPrimary = true;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, WaterVolumePrefabPath);
            Undo.RevertAllDownToGroup(undoGroup); // remove the temp build objects; only the prefab persists
            AssetDatabase.SaveAssets();

            if (prefab == null)
            {
                Debug.LogError($"[WebGL Water] Failed to save the WaterVolume prefab at {WaterVolumePrefabPath}.");
                return;
            }

            Selection.activeObject = prefab;
            Debug.Log($"[WebGL Water] WaterVolume prefab created at {WaterVolumePrefabPath}. " +
                      "Drop it into a scene with a camera - it resolves the camera and sun automatically.");
        }

        // Retrofit GPU foam particles onto an existing body. Demo scenes are create-once,
        // so they don't pick the feature up automatically; this wires the compute, the
        // procedural-quad material (shared, in Generated/) and the component in one click.
        internal static void AddFoamParticlesToSelection()
        {
            var selected = Selection.activeGameObject;
            var volume = selected != null ? selected.GetComponentInChildren<WaterVolume>() : null;
            if (volume == null)
            {
                Debug.LogError("[WebGL Water] Select a GameObject with a WaterVolume first.");
                return;
            }
            if (volume.GetComponent<WaterFoamParticles>() != null)
            {
                Debug.LogWarning("[WebGL Water] That body already has foam particles.");
                return;
            }
            EnsureGenFolder();
            Undo.SetCurrentGroupName("Add Foam Particles");
            AddFoamParticles(volume, MaterialFolderForActiveScene());

            // Ambient foam + spray are gated on the body's Foam flag (the turbulence gate), so the
            // retrofitted component would simulate NOTHING on a body whose Foam is off - the button
            // would look broken. Enable Foam so one click gives visible foam, matching Create Water.
            if (!volume.Foam)
            {
                Undo.RecordObject(volume, "Add Foam Particles");
                volume.Foam = true;
            }

            Selection.activeObject = volume.gameObject;
            Debug.Log($"[WebGL Water] Foam particles added to '{volume.name}' and Foam enabled.");
        }

        // Upgrade the shared splash materials (Generated/SplashDroplet.mat + SplashCrown.mat)
        // to the lit splash shader in place. They are shared by every demo scene, so one
        // click upgrades them all; hand-tuned values on matching properties are kept.
        internal static void UpgradeSplashMaterialsMenu()
        {
            UpgradeSplashMaterials();
            Debug.Log("[WebGL Water] Splash materials now use " + ShaderSplashParticles + ".");
        }

        // Assign the animated foam flipbook + relief normal map to every water surface
        // material in the open scene (above AND under). Demo materials are create-once,
        // so new foam textures don't reach them automatically; this is the retrofit.
        internal static void AssignFoamTexturesToSceneWater()
        {
            var volumes = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            if (volumes.Length == 0)
            {
                Debug.LogError("[WebGL Water] No WaterVolume in the open scene.");
                return;
            }

            int touched = 0;
            foreach (WaterVolume volume in volumes)
            {
                touched += AssignFoamTextures(volume.surfaceAbove);
                touched += AssignFoamTextures(volume.surfaceUnder);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[WebGL Water] Foam flipbook + normal map assigned to {touched} water material(s).");
        }

        static int AssignFoamTextures(Renderer surface)
        {
            if (surface == null || surface.sharedMaterial == null) return 0;
            AssignFoamFlipbook(surface.sharedMaterial);
            EditorUtility.SetDirty(surface.sharedMaterial);
            return 1;
        }

        // The per-demo material folder for the open scene ("3. Terrain Lake" ->
        // Demos/Materials/TerrainLake), so a retrofitted material lives (and is tweaked)
        // next to that demo's other materials. Falls back to Generated/ for custom scenes.
        static string MaterialFolderForActiveScene()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var compact = new System.Text.StringBuilder(sceneName.Length);
            foreach (char c in sceneName)
                if (char.IsLetter(c)) compact.Append(c);
            string candidate = DemoMaterialsRoot + compact;
            return AssetDatabase.IsValidFolder(candidate) ? candidate : Gen;
        }

        // Adds a SECOND (non-primary) water body next to the primary, sharing the sun, camera,
        // compute and shaders. The new body renders through its own MaterialPropertyBlock, so it
        // must look independent - proof the de-globalisation works.
        internal static void AddSecondaryBody()
        {
            var all = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            if (all == null || all.Length == 0)
            {
                Debug.LogError("[WebGL Water] Build the scene first (no WaterVolume found).");
                return;
            }
            WaterVolume primary = System.Array.Find(all, c => c.IsPrimary) ?? all[0];

            // One undo step for the whole added body.
            Undo.SetCurrentGroupName("Add Water Body (secondary)");
            int undoGroup = Undo.GetCurrentGroup();

            var bodyRoot = NewUndoableGameObject("Water Body (secondary)");

            var frameGO = NewUndoableGameObject(FrameObjectName);
            frameGO.transform.SetParent(bodyRoot.transform);
            float offsetX = 2f * primary.volumeExtent.x + SecondaryBodyGapMeters;
            frameGO.transform.position = primary.transform.position + new Vector3(offsetX, 0f, 0f);

            var body = frameGO.AddComponent<WaterVolume>();
            // One shared wiring block (asset refs + camera/sun) sourced from the live primary.
            WireWaterVolumeFrom(body, primary);
            body.volumeExtent = primary.volumeExtent;
            body.IsPrimary = false; // only ONE body mirrors to globals

            var rendGO = NewUndoableGameObject(RenderersObjectName);
            rendGO.transform.SetParent(bodyRoot.transform);
            body.surfaceAbove = CloneBodyRenderer(primary.surfaceAbove, rendGO.transform, SurfaceAboveName);
            body.surfaceUnder = CloneBodyRenderer(primary.surfaceUnder, rendGO.transform, SurfaceUnderName);
            body.poolRenderer = CloneBodyRenderer(primary.poolRenderer, rendGO.transform, AnalyticPoolName);
            body.godRayRenderer = CloneBodyRenderer(primary.godRayRenderer, rendGO.transform, GodRaysObjectName);

            Selection.activeObject = bodyRoot;
            EditorUtility.SetDirty(body);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(bodyRoot.scene);
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[WebGL Water] Secondary water body added. Move its 'Frame' child to reposition; " +
                      "edit that WaterVolume's Volume Extent for a different size/shape.");
        }

        // Copy a body renderer (same mesh + material + world transform, so its object->world maps to
        // the same pool space as the source); per-body data arrives via the MPB.
        static Renderer CloneBodyRenderer(Renderer src, Transform parent, string name)
        {
            if (src == null) return null;
            var go = NewUndoableGameObject(name);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
            go.transform.localScale = src.transform.lossyScale;

            var srcFilter = src.GetComponent<MeshFilter>();
            if (srcFilter != null) go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = src.sharedMaterial;
            mr.shadowCastingMode = src.shadowCastingMode;
            mr.receiveShadows = src.receiveShadows;
            return mr;
        }
    }
}
