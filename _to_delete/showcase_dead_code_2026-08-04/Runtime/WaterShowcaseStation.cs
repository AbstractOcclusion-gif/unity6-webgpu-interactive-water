// WebGpuWater - one feature-showcase station: metadata + camera framing for a self-contained
// demonstration template (see WaterShowcaseController). The template root carries this component;
// everything under it (bodies, props, helpers) is instantiated and destroyed as one unit, so every
// visit to a station starts from fresh sim state with zero restore bookkeeping.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("")] // showcase plumbing: created by the showcase builder, not hand-placed
    internal sealed class WaterShowcaseStation : MonoBehaviour
    {
        [Tooltip("Title shown in the showcase overlay.")]
        [SerializeField] internal string displayName = "Station";

        [Tooltip("One-line feature description shown under the title.")]
        [TextArea(2, 4)]
        [SerializeField] internal string description = "";

        [Header("Camera framing (applied to the scene OrbitCamera when this station is shown)")]
        [SerializeField] internal Vector3 orbitPivot = Vector3.zero;
        [SerializeField] internal float orbitPitch = -25f;
        [SerializeField] internal float orbitYaw = -200.5f;
        [SerializeField] internal float orbitDistance = 6f;
        [SerializeField] internal float orbitMinDistance = 1.5f;
        [SerializeField] internal float orbitMaxDistance = 20f;

        [Header("Optional sun override (world euler angles; off = keep the shared sun pose)")]
        [SerializeField] internal bool overrideSun = false;
        [SerializeField] internal Vector3 sunEuler = Vector3.zero;
    }
}
