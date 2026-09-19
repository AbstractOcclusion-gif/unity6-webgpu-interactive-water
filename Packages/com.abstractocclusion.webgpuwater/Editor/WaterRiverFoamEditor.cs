// WebGpuWater - River Foam inspector stub. Every foam control lives on the consolidated Water
// River inspector (Foam tab); this keeps the component from falling back to the default
// inspector and points the user at the right tab.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverFoam))]
    internal sealed class WaterRiverFoamEditor : UnityEditor.Editor
    {
        const string ComponentLabel = "River Foam";

        public override void OnInspectorGUI()
            => WaterRiverEditor.DrawSubComponentStub(
                (Component)target, WaterRiverEditor.InspectorTab.Foam, ComponentLabel);
    }
}
#endif
