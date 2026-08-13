// WebGpuWater - concise usage guidance for the three similarly named interaction components.
#if UNITY_EDITOR
using UnityEditor;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterSplash))]
    internal sealed class WaterSplashEditor : UnityEditor.Editor
    {
        const string Usage = "One-time Rigidbody entry splash. Uses the immediate analytic waterline " +
                             "(base level + wind/swell), so it does not require GPU height readback.";

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(Usage, MessageType.Info);
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(WaterBreachSplash))]
    internal sealed class WaterBreachSplashEditor : UnityEditor.Editor
    {
        const string Usage = "Optional repeated surface-crossing effect for projectiles, fish, or diving " +
                             "birds. It uses live GPU water-height readback, so it may wait for that data " +
                             "before triggering. It is not needed for a boat wake.";

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(Usage, MessageType.Info);
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(WaterSphereInteractor))]
    internal sealed class WaterSphereInteractorEditor : UnityEditor.Editor
    {
        const string Usage = "Continuous wake for boats and moving floaters. Horizontal movement makes the " +
                             "travelling wake; vertical heave/plunge makes a symmetric disturbance. Use " +
                             "Vertical Force Cap to limit only the latter.";

        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(Usage, MessageType.Info);
            DrawDefaultInspector();
        }
    }
}
#endif
