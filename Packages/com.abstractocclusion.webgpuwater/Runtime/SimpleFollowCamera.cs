// WebGpuWater - a minimal chase camera for the boat demo.
//
// Follows a target from a fixed local offset, but only around the target's YAW - so the view doesn't roll
// or pitch with the boat as it rocks on the waves (which is nauseating). Smoothed with a frame-rate
// independent lerp. Add it to the camera and assign a target.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Simple Follow Camera")]
    [DisallowMultipleComponent]
    public sealed class SimpleFollowCamera : MonoBehaviour
    {
        [SerializeField] internal Transform target;
        [SerializeField] Vector3 localOffset = new Vector3(0f, 4.5f, -11f); // behind and above, in target yaw space
        [SerializeField] float lookHeight = 1f;                             // aim a little above the target origin
        [SerializeField] float followSharpness = 4f;                        // higher = snappier

        void LateUpdate()
        {
            if (target == null) return;

            // Yaw-only frame: ignore the boat's roll/pitch so the camera stays level.
            Quaternion yaw = Quaternion.Euler(0f, target.eulerAngles.y, 0f);
            Vector3 desired = target.position + yaw * localOffset;

            float t = 1f - Mathf.Exp(-followSharpness * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, desired, t);
            transform.LookAt(target.position + Vector3.up * lookHeight);
        }
    }
}
