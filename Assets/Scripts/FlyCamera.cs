// FlyCamera - a minimal free-fly camera for testing scenes (e.g. flying out over the ocean clipmap).
//
// Hold the RIGHT mouse button to look; WASD moves on the view plane, Q/E move down/up, Shift sprints,
// and the mouse wheel changes the base speed. Look is gated behind the right button so the pointer stays
// free for the editor/Game view when you are not actively flying.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Demo
{
    [AddComponentMenu("AbstractOcclusion/Demo/Fly Camera")]
    [DisallowMultipleComponent]
    public sealed class FlyCamera : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Base move speed in metres/second.")]
        [Min(0f)] [SerializeField] float moveSpeed = 12f;
        [Tooltip("Multiplier applied while Sprint is held.")]
        [Min(1f)] [SerializeField] float sprintMultiplier = 4f;

        [Header("Look")]
        [Tooltip("Mouse look sensitivity (degrees per mouse unit).")]
        [Min(0f)] [SerializeField] float lookSensitivity = 2f;
        [Tooltip("How far the pitch can tilt up/down from level, in degrees.")]
        [Range(0f, 90f)] [SerializeField] float maxPitchDegrees = 89f;

        [Header("Speed wheel")]
        [Tooltip("How fast the mouse wheel scales the base move speed.")]
        [Min(0f)] [SerializeField] float wheelSpeedStep = 2f;
        [Min(0f)] [SerializeField] float minMoveSpeed = 1f;
        [Min(0f)] [SerializeField] float maxMoveSpeed = 500f;

        const KeyCode UpKey = KeyCode.E;
        const KeyCode DownKey = KeyCode.Q;
        const int LookMouseButton = 1; // right button

        float _yawDegrees;
        float _pitchDegrees;

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _yawDegrees = euler.y;
            _pitchDegrees = NormalizePitch(euler.x);
        }

        void Update()
        {
            AdjustSpeedFromWheel();
            if (Input.GetMouseButton(LookMouseButton)) ApplyLook();
            ApplyMove();
        }

        void AdjustSpeedFromWheel()
        {
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Approximately(wheel, 0f)) return;
            moveSpeed = Mathf.Clamp(moveSpeed + wheel * wheelSpeedStep * moveSpeed, minMoveSpeed, maxMoveSpeed);
        }

        void ApplyLook()
        {
            _yawDegrees += Input.GetAxis("Mouse X") * lookSensitivity;
            _pitchDegrees = Mathf.Clamp(_pitchDegrees - Input.GetAxis("Mouse Y") * lookSensitivity,
                                        -maxPitchDegrees, maxPitchDegrees);
            transform.rotation = Quaternion.Euler(_pitchDegrees, _yawDegrees, 0f);
        }

        void ApplyMove()
        {
            Vector3 direction = ReadMoveInput();
            if (direction.sqrMagnitude <= 0f) return;

            float speed = moveSpeed * (IsSprinting() ? sprintMultiplier : 1f);
            transform.position += transform.TransformDirection(direction.normalized) * (speed * Time.deltaTime);
        }

        // Local-space desired direction from the keys (x = right, y = up, z = forward).
        static Vector3 ReadMoveInput()
        {
            float vertical = 0f;
            if (Input.GetKey(UpKey)) vertical += 1f;
            if (Input.GetKey(DownKey)) vertical -= 1f;
            return new Vector3(Input.GetAxisRaw("Horizontal"), vertical, Input.GetAxisRaw("Vertical"));
        }

        static bool IsSprinting() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // Map Unity's 0..360 euler pitch into a signed -180..180 range so the clamp behaves.
        static float NormalizePitch(float pitchDegrees) => pitchDegrees > 180f ? pitchDegrees - 360f : pitchDegrees;
    }
}
