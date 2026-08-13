// WebGpuWater - edit-mode live preview driver.
//
// WaterVolume (and WaterMembership) are [ExecuteAlways], but in edit mode Unity only runs
// the player loop on demand (a repaint, an inspector tweak). This driver pumps it every
// editor tick while a water body is alive, so the ripple sim, waves, caustics and foam run
// live in the scene/game view without entering Play - and a freshly opened scene shows its
// OWN water instead of stale shader globals from the previous one.
//
// Toggleable (Window > AbstractOcclusion > WebGpuWater > Live Water Preview) and persisted in
// EditorPrefs: continuous player-loop pumping costs GPU while the editor idles, and the
// experimental WebGPU editor device has a history of hangs - the kill switch is one click.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [InitializeOnLoad]
    internal static class WaterEditorPreviewDriver
    {
        const string MenuPath = WaterBuildKit.MenuRoot + "Live Water Preview";
        const string EditorPrefsKey = "AbstractOcclusion.WebGpuWater.LivePreview";

        // The ripple sim advances a fixed amount per player-loop tick (stepsPerFrame), so the
        // preview must tick at play-like cadence: unthrottled EditorApplication.update can fire
        // far faster than 60 fps and the water would visibly fast-forward.
        const double TargetTickIntervalSeconds = 1.0 / 60.0;

        static double _lastTickTime;

        static WaterEditorPreviewDriver()
        {
            EditorApplication.update += Tick;
            // Delay: menu checkmarks can't be set from the static constructor (menus not built yet).
            EditorApplication.delayCall += () => Menu.SetChecked(MenuPath, Enabled);
        }

        static bool Enabled
        {
            get => EditorPrefs.GetBool(EditorPrefsKey, true);
            set => EditorPrefs.SetBool(EditorPrefsKey, value);
        }

        [MenuItem(MenuPath)]
        static void ToggleLivePreview()
        {
            Enabled = !Enabled;
            Menu.SetChecked(MenuPath, Enabled);
            SceneView.RepaintAll();
        }

        static void Tick()
        {
            if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (WaterVolume.ActiveBodyCount == 0) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastTickTime < TargetTickIntervalSeconds) return;
            _lastTickTime = now;

            // Run one player-loop pass (Update/LateUpdate on ExecuteAlways scripts) and
            // repaint the scene views so the result is visible even when nothing else
            // would trigger a redraw. The game view repaints via the player loop itself.
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
    }
}
