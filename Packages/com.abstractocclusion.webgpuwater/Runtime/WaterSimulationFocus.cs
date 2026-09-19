using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Enable on the currently relevant boat/character to request its window profile.</summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Simulation Focus")]
    public sealed class WaterSimulationFocus : MonoBehaviour
    {
        [Tooltip("Optional explicit body. Otherwise resolves the water under the target.")]
        public WaterVolume water;
        [Tooltip("Defaults to this transform.")]
        public Transform target;
        [Tooltip("Higher priority wins. Fishing lure/bobber camera uses 100.")]
        public int priority = 10;
        public WaterSimulationWindowSettings window = new WaterSimulationWindowSettings();
        private WaterVolume requestedBody;
        private Transform requestedTarget;
        private WaterSimulationWindowSettings requestedSettings;
        private int requestedPriority;
        private System.IDisposable request;

        private void OnEnable() => Update();

        private void Update()
        {
            Transform focus = target ? target : transform;
            WaterVolume body = water ? water : WaterVolume.BodyContaining(focus.position);
            if (requestedBody == body && requestedTarget == focus
                && requestedSettings == window && requestedPriority == priority) return;
            Release();
            if (!body) return;
            requestedBody = body;
            requestedTarget = focus;
            requestedSettings = window;
            requestedPriority = priority;
            request = body.PushSimulationFocus(this, focus, window, priority);
        }

        private void Release()
        {
            request?.Dispose();
            request = null;
            requestedBody = null;
            requestedTarget = null;
            requestedSettings = null;
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();
    }
}
