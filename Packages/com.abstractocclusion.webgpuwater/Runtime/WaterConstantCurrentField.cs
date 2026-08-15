// WebGpuWater - constant current source for simple streams and baseline current authoring.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/Constant Current Field")]
    [DisallowMultipleComponent]
    public sealed class WaterConstantCurrentField : WaterCurrentField
    {
        const float MinimumSpeed = 0f;
        const float DefaultSpeed = 1f;

        [Tooltip("Current speed in world metres per second. Direction follows this transform's blue axis.")]
        [Min(MinimumSpeed)]
        [SerializeField] internal float speed = DefaultSpeed;

        protected override bool TryEvaluateCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = transform.forward * Mathf.Max(MinimumSpeed, speed);
            return true;
        }
    }
}
