// WebGpuWater - enter/exit-water events (Unity 6 / URP port)
// Watches a single point (this object, plus an optional local offset) and fires submerge /
// emerge events as it crosses the water surface of whichever body contains it. Wire the
// events in the Inspector (audio, VFX, swimming, oxygen) or subscribe from code, and poll
// IsSubmerged / Body for the current state.
//
// Submersion uses the async height readback (like WaterBuoyancy), so on a WebGPU build with
// no async readback it can't detect submersion and the events won't fire.
using UnityEngine;
using UnityEngine.Events;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterProbe : MonoBehaviour
    {
        [Tooltip("Local-space offset from this object's origin at which submersion is tested " +
                 "(e.g. a character's chest or waterline point).")]
        [SerializeField] Vector3 probePoint = Vector3.zero;

        [Tooltip("Fired the moment the probe point goes below the water surface.")]
        [SerializeField] UnityEvent submerged = new UnityEvent();

        [Tooltip("Fired the moment the probe point rises back above the water surface.")]
        [SerializeField] UnityEvent emerged = new UnityEvent();

        /// <summary>Fired when the probe point crosses below the surface.</summary>
        public UnityEvent Submerged => submerged;

        /// <summary>Fired when the probe point crosses back above the surface.</summary>
        public UnityEvent Emerged => emerged;

        /// <summary>True while the probe point is below the surface of its containing body.</summary>
        public bool IsSubmerged { get; private set; }

        /// <summary>The water body the probe resolves to: the one whose footprint contains the
        /// point, else the nearest/primary body as a fallback. Null only when the scene has no
        /// water. Use <see cref="IsSubmerged"/> for whether the point is actually under water.</summary>
        public WaterVolume Body { get; private set; }

        void Update()
        {
            Vector3 point = transform.TransformPoint(probePoint);
            Body = WaterVolume.BodyContaining(point);

            bool nowSubmerged = Body != null && Body.IsSubmerged(point);
            if (nowSubmerged == IsSubmerged) return;

            IsSubmerged = nowSubmerged;
            if (nowSubmerged) submerged.Invoke(); else emerged.Invoke();
        }
    }
}
