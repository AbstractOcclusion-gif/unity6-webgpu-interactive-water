// WebGpuWater - River Current Field inspector stub. The field's two references are wired by
// the Water River facade (Wiring tab) and its readouts live on the Flow tab; this keeps the
// component from falling back to the default inspector and points the user at the right tab.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverCurrentField))]
    internal sealed class WaterRiverCurrentFieldEditor : UnityEditor.Editor
    {
        const string ComponentLabel = "River Current Field";

        public override void OnInspectorGUI()
            => WaterRiverEditor.DrawSubComponentStub(
                (Component)target, WaterRiverEditor.InspectorTab.Flow, ComponentLabel);
    }
}
#endif
