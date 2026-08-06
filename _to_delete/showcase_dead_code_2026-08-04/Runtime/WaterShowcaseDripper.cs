// WebGpuWater - timed drip source: calls Emit() on its WaterRippleEmitter at a fixed interval, so
// a showcase pond ripples on its own - and a slow-motion body visibly lags a normal-time one when
// two drippers share the same interval (the multi-body / time-scale station).
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("")] // showcase plumbing: created by the showcase builder, not hand-placed
    [RequireComponent(typeof(WaterRippleEmitter))]
    internal sealed class WaterShowcaseDripper : MonoBehaviour
    {
        [Tooltip("Seconds between drips.")]
        [Min(0.05f)] [SerializeField] internal float intervalSeconds = 1.5f;

        WaterRippleEmitter _emitter;
        float _nextEmitTime;

        void Awake() => _emitter = GetComponent<WaterRippleEmitter>();

        void OnEnable() => _nextEmitTime = Time.time + intervalSeconds;

        void Update()
        {
            if (Time.time < _nextEmitTime) return;
            _nextEmitTime = Time.time + intervalSeconds;
            _emitter.Emit();
        }
    }
}
