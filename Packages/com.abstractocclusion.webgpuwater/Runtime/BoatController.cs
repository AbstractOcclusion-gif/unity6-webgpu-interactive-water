// WebGpuWater - arcade boat controller.
//
// A fresh, single-Rigidbody boat drive model in our namespace. It supplies only what buoyancy can't:
// propulsion, steering, and the water resistance that makes a boat carve instead of skid. Float, roll,
// pitch and self-righting come from the WaterBuoyancy probes on the same object (add both).
//
// Model (mirrors the essentials of a mesh-hydrodynamics boat without the mesh solver):
//   - Thrust: a constant acceleration along forward applied AT THE STERN, linear in throttle. Top speed
//     emerges from the quadratic drag (no speed falloff). Eased by how submerged the propeller is.
//   - Steering: a yaw acceleration scaled by forward speed, so the rudder has no authority at rest -
//     the same "can't turn when stopped" feel real hull hydrodynamics produce.
//   - Resistance: quadratic drag along the keel plus a much stronger LATERAL drag (the keel effect that
//     turns sideways slip into a carved turn), plus yaw damping so a turn stops when the wheel centres.
// Forces use ForceMode.Acceleration so tuning is independent of the boat's mass.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Boat Controller")]
    public sealed class BoatController : MonoBehaviour
    {
        const string ThrottleAxis = "Vertical";   // W/S or up/down (default Input Manager axes)
        const string SteerAxis = "Horizontal";    // A/D or left/right
        const float MinDivisor = 0.01f;
        const float SternGizmoRadius = 0.15f;
        const float CameraRelativeFullLockAngle = 45f;
        const float DirectionMinSqrMagnitude = 0.0001f;

        [Header("Propulsion (acceleration, m/s^2)")]
        [Tooltip("Forward acceleration at full throttle. Top speed emerges where the quadratic drag below " +
                 "balances this, so thrust itself stays linear in throttle.")]
        [SerializeField] float enginePower = 9f;
        [SerializeField, Range(0f, 1f)] float reverseCoefficient = 0.35f;
        [Tooltip("Local point the propeller pushes from (near the stern, below the waterline).")]
        [SerializeField] internal Vector3 sternOffset = new Vector3(0f, -0.25f, -2.2f);

        [Header("Steering")]
        [SerializeField] float turnAcceleration = 2.5f;      // rad/s^2 at full lock and cruising speed
        [SerializeField] float fullAuthoritySpeed = 3f;      // forward speed (m/s) for full rudder authority
        [SerializeField] bool reverseSteerWhenBackward = true;

        [Header("Hydrodynamic resistance")]
        [SerializeField] float forwardDrag = 0.06f;          // quadratic drag along the keel (also sets top speed)
        [SerializeField] float lateralDrag = 0.8f;           // quadratic sideways drag (carves turns)
        [SerializeField] float yawDamping = 3f;              // stops the turn when steering centres

        [Header("Water contact")]
        [Tooltip("Height (m) the propeller can rise above the surface before thrust fully fades. Thrust is " +
                 "full while it's submerged and eases out as a wave lifts the stern clear - never cut dead.")]
        [SerializeField] internal float propellerDepth = 0.5f;
        [Tooltip("Height (m) the hull centre can rise above the surface before drag/steering fully fade " +
                 "(so an airborne boat isn't dragged by water it isn't touching).")]
        [SerializeField] internal float hullDepth = 0.6f;

        [Header("Input response")]
        [SerializeField] float throttleResponse = 2f;
        [SerializeField] float steerResponse = 3f;

        Rigidbody _rb;
        Transform _driveReference;
        float _rawThrottle, _rawSteer;   // polled each frame in Update
        float _throttle, _steer;         // smoothed toward the raw input
        // Camera-relative stick from BoatTouchDriver (x = lateral, y = forward), zero when no
        // touch drive is active. Summed with the keyboard axes then clamped, so either input
        // can drive without fighting the other; desktop behaviour is unchanged at (0,0).
        Vector2 _touchInput;

        internal void SetTouchInput(Vector2 stick) => _touchInput = stick;

        void Awake() => _rb = GetComponent<Rigidbody>();

        void Update()
        {
            float throttleInput = Mathf.Clamp(ReadAxis(ThrottleAxis) + _touchInput.y, -1f, 1f);
            float steerInput = Mathf.Clamp(ReadAxis(SteerAxis) + _touchInput.x, -1f, 1f);
            if (_driveReference == null)
            {
                _rawThrottle = throttleInput;
                _rawSteer = steerInput;
                return;
            }

            ReadCameraRelativeInput(throttleInput, steerInput);
        }

        // The demo switcher supplies the active camera at runtime. Keeping this opt-in avoids changing
        // the handling of existing boats and prefabs that use conventional hull-relative controls.
        internal void SetDriveReference(Transform driveReference) => _driveReference = driveReference;

        void ReadCameraRelativeInput(float forwardInput, float lateralInput)
        {
            Vector3 cameraForward = Vector3.ProjectOnPlane(_driveReference.forward, Vector3.up);
            if (cameraForward.sqrMagnitude <= DirectionMinSqrMagnitude)
                cameraForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);

            cameraForward.Normalize();
            Vector3 cameraRight = Vector3.Cross(Vector3.up, cameraForward);
            Vector3 desiredDirection = cameraForward * forwardInput + cameraRight * lateralInput;
            float inputAmount = Mathf.Clamp01(desiredDirection.magnitude);
            if (inputAmount <= 0f)
            {
                _rawThrottle = 0f;
                _rawSteer = 0f;
                return;
            }

            desiredDirection.Normalize();
            float headingError = Vector3.SignedAngle(transform.forward, desiredDirection, Vector3.up);
            _rawThrottle = inputAmount;
            _rawSteer = Mathf.Clamp(headingError / CameraRelativeFullLockAngle, -1f, 1f);
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _throttle = Mathf.MoveTowards(_throttle, _rawThrottle, throttleResponse * dt);
            _steer = Mathf.MoveTowards(_steer, _rawSteer, steerResponse * dt);

            // Thrust eases with how submerged the propeller is (no hard on/off gate, so wave bob doesn't
            // stall the boat). Steering + water drag engage while the hull is in the water.
            float sternWetness = Wetness(SternWorld, propellerDepth);
            if (sternWetness > 0f) ApplyThrust(sternWetness);

            float hullWetness = Wetness(_rb.worldCenterOfMass, hullDepth);
            if (hullWetness <= 0f) return; // fully airborne (jumped a wave): no steering or water drag

            float forwardSpeed = Vector3.Dot(_rb.linearVelocity, transform.forward);
            ApplySteering(forwardSpeed);
            ApplyHydrodynamicResistance(hullWetness);
        }

        Vector3 SternWorld => transform.TransformPoint(sternOffset);

        void ApplyThrust(float wetness)
        {
            // Constant thrust per throttle (linear response); the quadratic drag caps the top speed smoothly,
            // so there is no speed-dependent falloff to make the throttle feel uneven.
            float reverse = _throttle >= 0f ? 1f : reverseCoefficient;
            float acceleration = _throttle * enginePower * reverse * wetness;
            _rb.AddForceAtPosition(transform.forward * acceleration, SternWorld, ForceMode.Acceleration);
        }

        void ApplySteering(float forwardSpeed)
        {
            float authority = Mathf.Clamp01(Mathf.Abs(forwardSpeed) / Mathf.Max(fullAuthoritySpeed, MinDivisor));
            float direction = (reverseSteerWhenBackward && forwardSpeed < 0f) ? -1f : 1f;
            float yawAcceleration = _steer * turnAcceleration * authority * direction;
            _rb.AddTorque(transform.up * yawAcceleration, ForceMode.Acceleration);
        }

        void ApplyHydrodynamicResistance(float wetness)
        {
            Vector3 localVelocity = transform.InverseTransformDirection(_rb.linearVelocity);
            // Quadratic drag: gentle along the keel, strong sideways so the hull carves instead of skidding.
            Vector3 dragLocal = new Vector3(
                -localVelocity.x * Mathf.Abs(localVelocity.x) * lateralDrag,
                0f,
                -localVelocity.z * Mathf.Abs(localVelocity.z) * forwardDrag);
            _rb.AddForce(transform.TransformDirection(dragLocal) * wetness, ForceMode.Acceleration);

            float yawRate = Vector3.Dot(_rb.angularVelocity, transform.up);
            _rb.AddTorque(transform.up * (-yawRate * yawDamping * wetness), ForceMode.Acceleration);
        }

        // How wet a point is, 0..1, from the ANALYTIC waterline (rest + wind + swell, no interactive ripples
        // - so the boat's own wake doesn't make the reading flicker). Full (1) while the point is at or below
        // the surface, fading to 0 as it lifts up to depthScale ABOVE it. So a wave that lifts the stern/hull
        // eases the forces out smoothly instead of cutting them dead.
        float Wetness(Vector3 point, float depthScale)
        {
            WaterVolume body = WaterVolume.BodyContaining(point);
            if (body == null || !body.TryGetAnalyticWaterline(point.x, point.z, out float waterY)) return 0f;
            float heightAboveSurface = point.y - waterY;
            return Mathf.Clamp01(1f - heightAboveSurface / Mathf.Max(depthScale, MinDivisor));
        }

        // Throttle/steer axis, on either input backend. The Input System path reads WASD/arrows
        // directly (like FlyCamera) so an Input-System-only project gets a working boat instead of
        // an InvalidOperationException from the legacy API. The legacy path probes the axis ONCE
        // (Awake) instead of exception-driven flow control every frame.
        static float ReadAxis(string axis)
        {
#if ENABLE_INPUT_SYSTEM
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return 0f;
            float v = 0f;
            if (axis == ThrottleAxis)
            {
                if (k.wKey.isPressed || k.upArrowKey.isPressed) v += 1f;
                if (k.sKey.isPressed || k.downArrowKey.isPressed) v -= 1f;
            }
            else
            {
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) v += 1f;
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) v -= 1f;
            }
            return v;
#else
            if (!_axesProbed) ProbeAxes();
            if (!_axesValid) return 0f;
            return Input.GetAxis(axis);
#endif
        }

#if !ENABLE_INPUT_SYSTEM
        static bool _axesProbed, _axesValid;

        // One-time guard so a project missing the default axes degrades to a parked boat with a
        // single warning, instead of throwing per frame.
        static void ProbeAxes()
        {
            _axesProbed = true;
            try
            {
                Input.GetAxis(ThrottleAxis);
                Input.GetAxis(SteerAxis);
                _axesValid = true;
            }
            catch (System.ArgumentException)
            {
                _axesValid = false;
                Debug.LogWarning($"BoatController: Input Manager axes '{ThrottleAxis}'/'{SteerAxis}' are missing; boat input disabled.");
            }
        }
#endif

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(transform.TransformPoint(sternOffset), SternGizmoRadius);
        }
    }
}
