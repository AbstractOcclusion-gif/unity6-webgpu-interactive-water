// WebGL Water - buoyancy (Unity 6 / URP port)
// The "water -> object" half of the two-way coupling. Instead of one force at the
// centre of mass (which can only bob, never tilt), this samples the water surface at a
// lattice of points spread through the body and applies a partial buoyant force AT EACH
// POINT. The summed forces produce torque, so the object leans into the local wave slope,
// rocks, and self-rights. Each submerged point also gets drag (settling) and a push along
// the surface flow (waves carry it).
//
// The surface is read through the batched IWaterHeightSampler seam: one SampleHeights call
// per FixedUpdate fills height/normal/velocity for every point, replacing the old per-point
// call into WaterVolume. With every added field at its default the forces are unchanged from
// the original single-point model; the new drag/velocity/width behaviours are opt-in.
//
// The "object -> water" half is handled by WaterInteractable + the obstacle pass.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Rigidbody))]
    public partial class WaterBuoyancy : MonoBehaviour
    {
        const int MinSamplesPerAxis = 1;
        const int MaxSamplesPerAxis = 3;
        const float FallbackHalfExtent = 0.15f; // used when there is no collider to size from
        const float MinSphereRadius = 0.01f;

        [Tooltip("Float strength. Net buoyancy cancels gravity when " +
                 "buoyancy * submergedFraction = 1, so ~2.5 floats with the top out.")]
        [SerializeField] internal float buoyancy = 2.5f;

        [Tooltip("Linear damping applied per submerged point (kills bobbing; also damps rotation).")]
        [SerializeField] internal float waterLinearDamping = 2.0f;

        [Tooltip("Extra angular damping while submerged, for rotational stability.")]
        [SerializeField] internal float waterAngularDamping = 1.0f;

        [Tooltip("Float sample points per axis. 2 = 8 corner points (good torque); 3 = 27 (smoother, costlier).")]
        [Range(MinSamplesPerAxis, MaxSamplesPerAxis)] [SerializeField] internal int samplesPerAxis = 2;

        [Tooltip("Sphere radius per point as a fraction of the vertical point spacing. " +
                 "Only softens the submersion curve; the float level is unaffected.")]
        [Range(0.5f, 3f)] [SerializeField] internal float floatRadiusScale = 1.5f;

        [Tooltip("How hard the local surface flow pushes submerged points (waves carry the object).")]
        [Range(0f, 4f)] [SerializeField] internal float waveDriftStrength = 1.0f;

        [Tooltip("Extra damping on vertical velocity only, to quiet the residual bob " +
                 "(which otherwise feeds displacement jitter). Doesn't slow drift/tilt.")]
        [Range(0f, 4f)] [SerializeField] internal float verticalSettleDamping = 1.0f;

        // ---- opt-in extensions (defaults preserve the original single-point behaviour) ----

        [Tooltip("Object width (m) for ripple LOD: wind-wave ripples shorter than this are ignored, so a " +
                 "large float rides the swell without buzzing on fine chop. 0 = sample every wave (default).")]
        [SerializeField] internal float objectWidth = 0f;

        [Tooltip("Cap on the per-point buoyant acceleration, so a deeply plunged float doesn't erupt. " +
                 "0 = uncapped (original behaviour).")]
        [SerializeField] internal float maxBuoyancyForce = 0f;

        [Tooltip("Drag against the WATER'S surface velocity (waves carry the object) instead of braking " +
                 "toward world rest. Off = the original settle-to-rest damping.")]
        [SerializeField] internal bool surfaceRelativeDrag = false;

        [Tooltip("Per-axis (object-local) drag scale, used only when Surface Relative Drag is on.")]
        [SerializeField] internal Vector3 dragCoefficients = Vector3.one;

        [Tooltip("Sample the analytic surface (rest + wind + swell) only, IGNORING interactive ripples. Turn " +
                 "ON for a body that emits its OWN ripples (e.g. a boat with a WaterInteractable wake) so it " +
                 "isn't carried by them. Leave OFF so pool floaters still bob on ripples, splashes and drops.")]
        [SerializeField] internal bool ignoreInteractiveRipples = false;

        [Tooltip("Draw the probe grid, per-point submersion, sampled surface points and lift arrows when " +
                 "this object is selected (editor only).")]
        [SerializeField] internal bool drawDebugGizmos = true;

        Rigidbody _rb;
        Collider _col;
        WaterVolume _body;

        // Float points in the body's local space, plus the world-space sphere radius
        // used for the per-point submerged-volume (sphere-cap) calculation.
        Vector3[] _localPoints;
        float _sphereRadius;

        // Reused per-FixedUpdate scratch for the single batched surface query (no per-frame allocation).
        // The results buffer is rented from a shared owner-keyed cache so floaters and (later) hulls share
        // one reuse mechanism; _results caches this frame's rented buffer for the solver and the gizmos.
        static readonly WaterHeightQuery SharedQuery = new WaterHeightQuery();
        Vector3[] _worldPoints;
        WaterSample[] _results;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _col = GetComponent<Collider>();
        }

        void Start()
        {
            BuildSamplePoints();
            if (WaterVolume.Resolve() == null)
                Debug.LogWarning("WaterBuoyancy: no WaterVolume in the scene; object will not float.");
        }

        void OnDestroy() => SharedQuery.Release(GetInstanceID());

        // Lay out a lattice of float points across the collider's local bounding box and
        // pick a per-point sphere radius from the vertical spacing.
        void BuildSamplePoints()
        {
            GetLocalBox(_col, out Vector3 localCenter, out Vector3 localSize);

            int n = Mathf.Clamp(samplesPerAxis, MinSamplesPerAxis, MaxSamplesPerAxis);
            var points = new List<Vector3>(n * n * n);
            AppendLatticePoints(localCenter, localSize, n, points);
            _localPoints = points.ToArray();
            _worldPoints = new Vector3[_localPoints.Length];

            // Radius ~ half the world vertical spacing, scaled, so the spherical cap
            // transitions smoothly over each layer. Force is normalised by point count,
            // so radius changes the softness of the curve, not the equilibrium depth.
            Vector3 worldSize = Vector3.Scale(localSize, AbsScale(transform.lossyScale));
            float spacingY = worldSize.y / n;
            _sphereRadius = Mathf.Max(MinSphereRadius, 0.5f * spacingY * floatRadiusScale);
        }

        // Even lattice of n^3 points across a local box (fractional offsets in [-0.5, 0.5]). Shared by the
        // runtime sample-point build and the editor gizmo preview so both show the exact same probe layout.
        static void AppendLatticePoints(Vector3 center, Vector3 size, int n, List<Vector3> into)
        {
            for (int ix = 0; ix < n; ix++)
                for (int iy = 0; iy < n; iy++)
                    for (int iz = 0; iz < n; iz++)
                    {
                        Vector3 frac = new Vector3(
                            (ix + 0.5f) / n - 0.5f,
                            (iy + 0.5f) / n - 0.5f,
                            (iz + 0.5f) / n - 0.5f);
                        into.Add(center + Vector3.Scale(frac, size));
                    }
        }

        // Local-space (unscaled) box of the collider; TransformPoint reapplies scale.
        void GetLocalBox(Collider col, out Vector3 center, out Vector3 size)
        {
            if (col is BoxCollider box)
            {
                center = box.center;
                size = box.size;
                return;
            }
            if (col != null)
            {
                center = transform.InverseTransformPoint(col.bounds.center);
                Vector3 local = transform.InverseTransformVector(col.bounds.size);
                size = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                return;
            }
            center = Vector3.zero;
            size = Vector3.one * (FallbackHalfExtent * 2f);
        }

        void FixedUpdate()
        {
            // Re-resolve every step so an object that drifts between lakes floats on the one it is
            // currently in (cheap: a handful of bodies).
            _body = WaterVolume.BodyContaining(transform.position);
            if (_body == null || _localPoints == null || _localPoints.Length == 0) return;

            BuildWorldPoints();
            // ONE batched surface query for every point (height + normal + velocity), through the water
            // height seam. objectWidth becomes the ripple LOD cut-off (0 = full spectrum). The results buffer
            // is rented per-owner (GetInstanceID is stable + unique) so there is no per-frame allocation.
            int ownerId = GetInstanceID();
            _results = SharedQuery.RentResults(ownerId, _worldPoints.Length);
            _body.SampleHeights(ownerId, objectWidth, _worldPoints, _results,
                                WaterQueryFields.HeightNormalVelocity, ignoreInteractiveRipples);

            float gravity = Physics.gravity.magnitude;
            float invCount = 1f / _localPoints.Length;
            float submergedSum = 0f;
            // One constant lift/settle direction for this body (the volume up), as in the original model.
            Vector3 up = _body.VolumeUp;

            for (int i = 0; i < _worldPoints.Length; i++)
            {
                WaterSample sample = _results[i];
                if (!sample.Valid) continue;

                Vector3 world = _worldPoints[i];
                // Depth below the surface along up. For a level body this is the vertical gap, identical to
                // the original pool-space submersion; a tilted body reads it along the volume up.
                float depth = Vector3.Dot(new Vector3(0f, sample.Height - world.y, 0f), up);
                float fraction = SphereSubmergedFraction(depth, _sphereRadius);
                if (fraction <= 0f) continue;

                submergedSum += fraction;
                ApplyPointForces(world, sample, up, gravity, fraction * invCount);
            }

            if (submergedSum <= 0f) return;
            ApplyBodyDamping(up, submergedSum * invCount);
        }

        void BuildWorldPoints()
        {
            for (int i = 0; i < _localPoints.Length; i++)
                _worldPoints[i] = transform.TransformPoint(_localPoints[i]);
        }

        // Lift + drag + wave-drift at one submerged point. With every added field at its default this is
        // byte-for-byte the original: lift along up, drag toward world rest, horizontal wave-drift push.
        void ApplyPointForces(Vector3 world, WaterSample sample, Vector3 up, float gravity, float weight)
        {
            // Archimedes lift along up -> net force + emergent righting torque.
            Vector3 lift = up * (gravity * buoyancy * weight);
            if (maxBuoyancyForce > 0f)
                lift = Vector3.ClampMagnitude(lift, maxBuoyancyForce * weight);
            _rb.AddForceAtPosition(lift, world, ForceMode.Acceleration);

            // Per-point drag so the object settles (damps translation and rotation).
            _rb.AddForceAtPosition(-SurfaceDrag(world, sample) * weight, world, ForceMode.Acceleration);

            // Push along the surface so waves carry and lean the object. Default uses the horizontal wave
            // drift (the original flow push); surface-relative drag also folds in the vertical orbital rise.
            Vector3 drift = surfaceRelativeDrag ? sample.Velocity : HorizontalOnly(sample.Velocity);
            _rb.AddForceAtPosition(drift * (waveDriftStrength * weight), world, ForceMode.Acceleration);
        }

        // Drag force (per unit weight) at a point. Default: brake toward world rest (original). Surface-
        // relative: brake toward the water's own velocity, anisotropically in the object's local frame so
        // a hull can slip forward yet resist sideways/heave.
        Vector3 SurfaceDrag(Vector3 world, WaterSample sample)
        {
            Vector3 pointVelocity = _rb.GetPointVelocity(world);
            if (!surfaceRelativeDrag)
                return pointVelocity * waterLinearDamping;

            Vector3 relative = pointVelocity - sample.Velocity;
            Vector3 local = transform.InverseTransformDirection(relative);
            local.Scale(dragCoefficients);
            return transform.TransformDirection(local) * waterLinearDamping;
        }

        // Rotational + vertical-settle damping, scaled by the average submersion. Unchanged from the original:
        // the vertical settle quiets the residual bob without slowing the horizontal drift/tilt we keep.
        void ApplyBodyDamping(Vector3 up, float averageFraction)
        {
            _rb.AddTorque(-_rb.angularVelocity * (waterAngularDamping * averageFraction), ForceMode.Acceleration);
            float verticalSpeed = Vector3.Dot(_rb.linearVelocity, up);
            _rb.AddForce(up * (-verticalSpeed * verticalSettleDamping * averageFraction), ForceMode.Acceleration);
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

        static Vector3 HorizontalOnly(Vector3 v) => new Vector3(v.x, 0f, v.z);

        static Vector3 AbsScale(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
    }
}
