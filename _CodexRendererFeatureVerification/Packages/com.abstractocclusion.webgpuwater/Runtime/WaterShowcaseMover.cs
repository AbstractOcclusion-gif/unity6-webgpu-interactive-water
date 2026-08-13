// WebGpuWater - constant-speed circular mover for showcase props (e.g. the wake sphere).
// Kinematic by design: it writes the transform directly and lets components that measure their own
// displacement (WaterSphereInteractor) react - no physics, no drift, deterministic path.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("")] // showcase plumbing: created by the showcase builder, not hand-placed
    internal sealed class WaterShowcaseMover : MonoBehaviour
    {
        const float FullTurnDegrees = 360f;

        [Tooltip("World-space centre of the circular path.")]
        [SerializeField] internal Vector3 pathCenter = Vector3.zero;

        [Tooltip("Circle radius in metres.")]
        [Min(0f)] [SerializeField] internal float pathRadius = 4f;

        [Tooltip("Seconds per full lap.")]
        [Min(0.1f)] [SerializeField] internal float lapSeconds = 12f;

        float _angleDegrees;

        void OnEnable() => Place();

        void Update()
        {
            _angleDegrees = Mathf.Repeat(
                _angleDegrees + FullTurnDegrees / lapSeconds * Time.deltaTime, FullTurnDegrees);
            Place();
        }

        void Place()
        {
            float radians = _angleDegrees * Mathf.Deg2Rad;
            var offset = new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * pathRadius;
            transform.position = pathCenter + offset;
            // Face the travel direction (circle tangent) so an oblong prop reads as "driving".
            var tangent = new Vector3(-Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            transform.rotation = Quaternion.LookRotation(tangent, Vector3.up);
        }
    }
}
