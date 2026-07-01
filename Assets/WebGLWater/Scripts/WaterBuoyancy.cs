// WebGL Water - buoyancy (Unity 6 / URP port)
// The "water -> object" half of the two-way coupling. Instead of one force at the
// centre of mass (which can only bob, never tilt), this samples the GPU height
// field at a lattice of points spread through the body and applies a partial
// buoyant force AT EACH POINT. The summed forces produce torque, so the object
// leans into the local wave slope, rocks, and self-rights. Each submerged point
// also gets drag (settling) and a push along the surface flow (waves carry it).
// The "object -> water" half is handled by WaterInteractable + the obstacle pass.
using System.Collections.Generic;
using UnityEngine;

namespace WebGLWater
{
    [RequireComponent(typeof(Rigidbody))]
    public class WaterBuoyancy : MonoBehaviour
    {
        const int MinSamplesPerAxis = 1;
        const int MaxSamplesPerAxis = 3;
        const float FallbackHalfExtent = 0.15f; // used when there is no collider to size from
        const float MinSphereRadius = 0.01f;

        [Tooltip("Float strength. Net buoyancy cancels gravity when " +
                 "buoyancy * submergedFraction = 1, so ~2.5 floats with the top out.")]
        public float buoyancy = 2.5f;

        [Tooltip("Linear damping applied per submerged point (kills bobbing; also damps rotation).")]
        public float waterLinearDamping = 2.0f;

        [Tooltip("Extra angular damping while submerged, for rotational stability.")]
        public float waterAngularDamping = 1.0f;

        [Tooltip("Float sample points per axis. 2 = 8 corner points (good torque); 3 = 27 (smoother, costlier).")]
        [Range(MinSamplesPerAxis, MaxSamplesPerAxis)] public int samplesPerAxis = 2;

        [Tooltip("Sphere radius per point as a fraction of the vertical point spacing. " +
                 "Only softens the submersion curve; the float level is unaffected.")]
        [Range(0.5f, 3f)] public float floatRadiusScale = 1.5f;

        [Tooltip("How hard the local surface flow pushes submerged points (waves carry the object).")]
        [Range(0f, 4f)] public float waveDriftStrength = 1.0f;

        [Tooltip("Extra damping on vertical velocity only, to quiet the residual bob " +
                 "(which otherwise feeds displacement jitter). Doesn't slow drift/tilt.")]
        [Range(0f, 4f)] public float verticalSettleDamping = 1.0f;

        Rigidbody _rb;
        Collider _col;
        WaterController _ctrl;

        // Float points in the body's local space, plus the world-space sphere radius
        // used for the per-point submerged-volume (sphere-cap) calculation.
        Vector3[] _localPoints;
        float _sphereRadius;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        void Start()
        {
            BuildSamplePoints();
            _ctrl = WaterController.Resolve(); // TODO(Phase 2): the body containing this object
            if (_ctrl == null)
                Debug.LogWarning("WaterBuoyancy: no WaterController in the scene; object will not float.");
        }

        // Lay out a lattice of float points across the collider's local bounding box and
        // pick a per-point sphere radius from the vertical spacing.
        void BuildSamplePoints()
        {
            GetLocalBox(out Vector3 localCenter, out Vector3 localSize);

            int n = Mathf.Clamp(samplesPerAxis, MinSamplesPerAxis, MaxSamplesPerAxis);
            var points = new List<Vector3>(n * n * n);
            for (int ix = 0; ix < n; ix++)
                for (int iy = 0; iy < n; iy++)
                    for (int iz = 0; iz < n; iz++)
                    {
                        Vector3 frac = new Vector3(
                            (ix + 0.5f) / n - 0.5f,
                            (iy + 0.5f) / n - 0.5f,
                            (iz + 0.5f) / n - 0.5f);
                        points.Add(localCenter + Vector3.Scale(frac, localSize));
                    }
            _localPoints = points.ToArray();

            // Radius ~ half the world vertical spacing, scaled, so the spherical cap
            // transitions smoothly over each layer. Force is normalised by point count,
            // so radius changes the softness of the curve, not the equilibrium depth.
            Vector3 worldSize = Vector3.Scale(localSize, AbsScale(transform.lossyScale));
            float spacingY = worldSize.y / n;
            _sphereRadius = Mathf.Max(MinSphereRadius, 0.5f * spacingY * floatRadiusScale);
        }

        // Local-space (unscaled) box of the collider; TransformPoint reapplies scale.
        void GetLocalBox(out Vector3 center, out Vector3 size)
        {
            if (_col is BoxCollider box)
            {
                center = box.center;
                size = box.size;
                return;
            }
            if (_col != null)
            {
                center = transform.InverseTransformPoint(_col.bounds.center);
                Vector3 local = transform.InverseTransformVector(_col.bounds.size);
                size = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                return;
            }
            center = Vector3.zero;
            size = Vector3.one * (FallbackHalfExtent * 2f);
        }

        void FixedUpdate()
        {
            if (_ctrl == null || _localPoints == null || _localPoints.Length == 0) return;

            float g = Physics.gravity.magnitude;
            float invCount = 1f / _localPoints.Length;
            float submergedSum = 0f;
            Vector3 up = -Physics.gravity.normalized; // replaced by the volume's up while submerged

            foreach (Vector3 local in _localPoints)
            {
                Vector3 world = transform.TransformPoint(local);
                // Evaluated in the volume's frame, so this is correct under rotation,
                // tilt and a non-uniform (rectangular / custom-depth) volume.
                if (!_ctrl.TrySampleSubmersion(world, out float depth, out Vector3 volumeUp, out Vector3 flow)) continue;
                up = volumeUp;

                float fraction = SphereSubmergedFraction(depth, _sphereRadius);
                if (fraction <= 0f) continue;
                submergedSum += fraction;

                float weight = fraction * invCount;

                // Archimedes lift along the volume up -> net force + righting torque.
                _rb.AddForceAtPosition(up * (g * buoyancy * weight), world, ForceMode.Acceleration);

                // Per-point drag so the object settles (damps translation and rotation).
                Vector3 pointVel = _rb.GetPointVelocity(world);
                _rb.AddForceAtPosition(-pointVel * (waterLinearDamping * weight), world, ForceMode.Acceleration);

                // Push along the surface flow so waves carry and lean the object.
                _rb.AddForceAtPosition(flow * (waveDriftStrength * weight), world, ForceMode.Acceleration);
            }

            if (submergedSum <= 0f) return;

            float avgFraction = submergedSum * invCount;
            _rb.AddTorque(-_rb.angularVelocity * (waterAngularDamping * avgFraction), ForceMode.Acceleration);

            // Quiet the residual vertical bob (the source of displacement jitter) without
            // damping the horizontal drift/tilt we want to keep.
            float verticalSpeed = Vector3.Dot(_rb.linearVelocity, up);
            _rb.AddForce(up * (-verticalSpeed * verticalSettleDamping * avgFraction), ForceMode.Acceleration);
        }

        // Submerged fraction of a sphere whose centre is 'depth' below the surface
        // (depth > 0 means below). Spherical-cap volume over full sphere volume -> a
        // smooth 0..1 S-curve that is exactly 0.5 when the centre sits on the surface.
        static float SphereSubmergedFraction(float depth, float radius)
        {
            if (depth >= radius) return 1f;
            if (depth <= -radius) return 0f;
            float capHeight = depth + radius; // measured up from the bottom of the sphere
            return (capHeight * capHeight * (3f * radius - capHeight)) / (4f * radius * radius * radius);
        }

        static Vector3 AbsScale(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }
}
