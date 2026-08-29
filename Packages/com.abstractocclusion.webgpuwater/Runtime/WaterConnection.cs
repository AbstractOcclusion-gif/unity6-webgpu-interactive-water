// WebGpuWater - an authored connection between two ports: the seam where two waters hand off.
//
// The minimum viable topology edge (research doc, section 3.6): two ports, a transition zone and
// an authored flow. No hydrological simulation - queries inside the transition zone blend the two
// providers' surfaces (WaterTopology.ApplySeamBlend) so fishing, fish migration and streaming get
// a continuous seam; everything else stays authored data the game reads through WaterTopology.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/Water Connection")]
    [DisallowMultipleComponent]
    public sealed class WaterConnection : MonoBehaviour
    {
        const float MinTransitionRadiusMeters = 0.1f;
        const float DefaultTransitionRadiusMeters = 4f;

        [Tooltip("Upstream-side port (by the authored flow sign convention).")]
        [SerializeField] internal WaterConnectionPort portA;

        [Tooltip("Downstream-side port.")]
        [SerializeField] internal WaterConnectionPort portB;

        [Tooltip("Half-width of the transition zone around the seam plane, metres. Queries " +
                 "inside it blend the two providers' surfaces for a continuous handoff.")]
        [Min(MinTransitionRadiusMeters)]
        [SerializeField] internal float transitionRadiusMeters = DefaultTransitionRadiusMeters;

        [Tooltip("Authored flow across the connection, cubic metres per second, positive from " +
                 "Port A toward Port B. Advisory data for gameplay; the renderer ignores it.")]
        [SerializeField] internal float authoredFlowRate;

        /// <summary>Session-stable connection identity (assigned at registration).</summary>
        public int ConnectionId { get; internal set; }

        public WaterConnectionPort PortA => portA;
        public WaterConnectionPort PortB => portB;
        public float TransitionRadiusMeters => transitionRadiusMeters;
        public float AuthoredFlowRate => authoredFlowRate;

        /// <summary>Both ports assigned and alive - the only state the seam blend consumes.</summary>
        public bool IsWired => portA != null && portB != null;

        void OnEnable()
        {
            if (!IsWired)
            {
                // Fail loud, once, at the authoring boundary - a silent half-wired connection
                // would just make the seam mysteriously not blend.
                Debug.LogWarning("WaterConnection: both ports must be assigned; this connection " +
                                 "is inert until they are.", this);
            }
            WaterTopology.Register(this);
        }

        void OnDisable() => WaterTopology.Unregister(this);
    }
}
