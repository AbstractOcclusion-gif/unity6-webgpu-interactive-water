// WebGpuWater - a stable, authored attachment point where one water body can connect to another.
//
// Ports carry the PERSISTENT identity in the topology layer (a serialized GUID string): BodyIds
// are session-stable only, so streaming and save systems key on port ids. A port belongs to one
// provider - a WaterVolume, or a river ribbon - and its transform authors the seam: position =
// where the waters meet, forward = the authored downstream direction.
//
// Deliberately game-agnostic: the package exposes ports/connections/handoff; what a connection
// MEANS (fish migration, unlock gating) is the game's business.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/Water Connection Port")]
    [DisallowMultipleComponent]
    public sealed class WaterConnectionPort : MonoBehaviour
    {
        [Tooltip("Persistent port identity. Auto-generated once; keep it stable - streaming and " +
                 "gameplay systems key on it across sessions, unlike the session-only BodyId.")]
        [SerializeField] internal string portId;

        [Tooltip("The water body this port belongs to. Leave empty when River is set.")]
        [SerializeField] internal WaterVolume body;

        [Tooltip("The river ribbon this port belongs to. Takes precedence over Body when both are set.")]
        [SerializeField] internal WaterRiverSurface river;

        [Tooltip("Authored volumetric flow through this port, cubic metres per second. Sign " +
                 "convention: positive flows OUT of this port along the transform's forward.")]
        [SerializeField] internal float authoredFlowRate;

        // Test seam: lets topology tests wire a fake provider without scene water. Never set in
        // production code paths.
        internal IWaterSurfaceProvider providerOverride;

        /// <summary>GUID-once persistent id. Ensured lazily too: a port created AT RUNTIME
        /// (facade generation, builds) never sees Reset/OnValidate, and an id-less port would
        /// orphan everything keyed on it.</summary>
        public string PortId
        {
            get { EnsurePortId(); return portId; }
        }
        public Vector3 Anchor => transform.position;
        /// <summary>Authored downstream direction (the transform's forward, normalized).</summary>
        public Vector3 FlowDirection => transform.forward;
        public float AuthoredFlowRate => authoredFlowRate;

        /// <summary>The provider this port attaches to, or null while its water is not live.</summary>
        public IWaterSurfaceProvider ResolveProvider()
        {
            if (providerOverride != null) return providerOverride;
            if (river != null && river.isActiveAndEnabled) return river.SurfaceProvider;
            if (body != null && body.isActiveAndEnabled) return body;
            return null;
        }

        void Reset() => EnsurePortId();
        void OnValidate() => EnsurePortId();

        // GUID-once: a port that loses its id would orphan every system keyed on it, so the id is
        // only ever created when absent, never regenerated.
        internal void EnsurePortId()
        {
            if (!string.IsNullOrEmpty(portId)) return;
            portId = System.Guid.NewGuid().ToString("N");
        }

#if UNITY_EDITOR
        // Selected-only so a scene full of ports stays clean; the arrow is the authored
        // DOWNSTREAM direction - the part of a port you cannot read off its transform gizmo.
        const float GizmoAnchorRadiusMeters = 0.2f;
        const float GizmoArrowLengthMeters = 1.5f;
        static readonly Color GizmoPortColor = new Color(0.2f, 0.8f, 1f, 0.9f);

        void OnDrawGizmosSelected()
        {
            Gizmos.color = GizmoPortColor;
            Gizmos.DrawWireSphere(Anchor, GizmoAnchorRadiusMeters);
            Gizmos.DrawLine(Anchor, Anchor + FlowDirection * GizmoArrowLengthMeters);
        }
#endif
    }
}
