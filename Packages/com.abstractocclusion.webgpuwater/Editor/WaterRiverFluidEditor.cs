// WebGpuWater - River Fluid inspector stub. Every fluid/bake control lives on the consolidated
// Water River inspector (Fluid tab); this keeps the component from falling back to the default
// inspector and points the user at the right tab.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterRiverFluid))]
    internal sealed class WaterRiverFluidEditor : UnityEditor.Editor
    {
        const string ComponentLabel = "River Fluid";

        public override void OnInspectorGUI()
            => WaterRiverEditor.DrawSubComponentStub(
                (Component)target, WaterRiverEditor.InspectorTab.Fluid, ComponentLabel);
    }
}
#endif
