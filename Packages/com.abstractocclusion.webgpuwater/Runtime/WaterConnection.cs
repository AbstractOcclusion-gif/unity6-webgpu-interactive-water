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
        internal const float MinTransitionRadiusMeters = 0.1f;
        internal const float DefaultTransitionRadiusMeters = 4f;

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

        /// <summary>The port the authored flow exits through (sign convention: positive flow
        /// runs A toward B). Zero flow reports B - "downstream" defaults to the B side so an
        /// unauthored connection still answers deterministically.</summary>
        public WaterConnectionPort DownstreamPort => authoredFlowRate >= 0f ? portB : portA;

        /// <summary>The far-side port from the given session body id, or null when the id
        /// touches neither side. Keeps FishingGame-style graph walks out of the port/flow sign
        /// conventions.</summary>
        public WaterConnectionPort OtherPortFor(int bodyId)
        {
            if (!IsWired) return null;
            IWaterSurfaceProvider providerA = portA.ResolveProvider();
            IWaterSurfaceProvider providerB = portB.ResolveProvider();
            if (providerA != null && providerA.BodyId == bodyId) return portB;
            if (providerB != null && providerB.BodyId == bodyId) return portA;
            return null;
        }

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

#if UNITY_EDITOR
        // The seam made visible: the port axis, and the transition SLAB the query blend actually
        // uses (WaterTopology.ApplySeamBlend measures distance to the plane at the midpoint) -
        // so an author sees where the handoff happens, not just where the ports sit.
        const float GizmoSlabDrawSizeMeters = 4f;
        static readonly Color GizmoAxisColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        static readonly Color GizmoSlabColor = new Color(1f, 0.85f, 0.2f, 0.25f);

        void OnDrawGizmosSelected()
        {
            if (!IsWired) return;
            Vector3 anchorA = portA.Anchor;
            Vector3 anchorB = portB.Anchor;
            Vector3 axis = anchorB - anchorA;
            if (axis.sqrMagnitude <= Mathf.Epsilon) return;

            Gizmos.color = GizmoAxisColor;
            Gizmos.DrawLine(anchorA, anchorB);

            Vector3 seamCenter = (anchorA + anchorB) * 0.5f;
            Gizmos.color = GizmoSlabColor;
            Gizmos.matrix = Matrix4x4.TRS(seamCenter,
                                          Quaternion.LookRotation(axis.normalized, Vector3.up),
                                          Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero,
                                new Vector3(GizmoSlabDrawSizeMeters, GizmoSlabDrawSizeMeters,
                                            transitionRadiusMeters * 2f));
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
