using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class WaterDemoSubmarineController : MonoBehaviour
    {
        const float MinimumInputScale = 0.01f;

        [SerializeField] float forwardAcceleration = 8f;
        [SerializeField] float verticalAcceleration = 5f;
        [SerializeField] float turnAcceleration = 2f;
        [SerializeField] float linearDamping = 0.5f;
        [SerializeField] float angularDamping = 1.5f;

        Rigidbody _rigidbody;

        void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            _rigidbody.useGravity = false;
        }

        void FixedUpdate()
        {
            ReadInput(out float throttle, out float yaw, out float pitch, out float ballast);

            _rigidbody.AddForce(transform.forward * (throttle * forwardAcceleration), ForceMode.Acceleration);
            _rigidbody.AddForce(Vector3.up * (ballast * verticalAcceleration), ForceMode.Acceleration);
            _rigidbody.AddTorque(transform.up * (yaw * turnAcceleration), ForceMode.Acceleration);
            _rigidbody.AddTorque(transform.right * (pitch * turnAcceleration), ForceMode.Acceleration);
            _rigidbody.AddForce(-_rigidbody.linearVelocity * linearDamping, ForceMode.Acceleration);
            _rigidbody.AddTorque(-_rigidbody.angularVelocity * angularDamping, ForceMode.Acceleration);
        }

        static void ReadInput(out float throttle, out float yaw, out float pitch, out float ballast)
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
            {
                throttle = yaw = pitch = ballast = 0f;
                return;
            }
            throttle = Axis(keyboard.wKey.isPressed, keyboard.sKey.isPressed);
            yaw = Axis(keyboard.dKey.isPressed, keyboard.aKey.isPressed);
            pitch = Axis(keyboard.upArrowKey.isPressed, keyboard.downArrowKey.isPressed);
            ballast = Axis(keyboard.spaceKey.isPressed, keyboard.leftCtrlKey.isPressed);
#else
            throttle = Axis(Input.GetKey(KeyCode.W), Input.GetKey(KeyCode.S));
            yaw = Axis(Input.GetKey(KeyCode.D), Input.GetKey(KeyCode.A));
            pitch = Axis(Input.GetKey(KeyCode.UpArrow), Input.GetKey(KeyCode.DownArrow));
            ballast = Axis(Input.GetKey(KeyCode.Space), Input.GetKey(KeyCode.LeftControl));
#endif
        }

        static float Axis(bool positive, bool negative)
        {
            float value = (positive ? 1f : 0f) - (negative ? 1f : 0f);
            return Mathf.Abs(value) < MinimumInputScale ? 0f : value;
        }
    }
}
