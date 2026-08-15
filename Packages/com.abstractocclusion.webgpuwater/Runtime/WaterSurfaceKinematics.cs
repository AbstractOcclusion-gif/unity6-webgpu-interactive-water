// WebGpuWater - pure surface-kinematics composition shared by water queries.
//
// Surface tilt describes geometry and produces the normal. Water velocity describes motion and
// carries buoyancy, particles, and gameplay objects. Keeping those inputs separate is required for
// authored river currents: a fast current must move objects without tilting the rendered surface.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterSurfaceKinematics
    {
        const float NormalLengthEpsilon = 1e-6f;

        internal static Vector3 TiltToWorld(Quaternion surfaceRotation, Vector2 localSurfaceTilt)
            => surfaceRotation * new Vector3(localSurfaceTilt.x, 0f, localSurfaceTilt.y);

        internal static Vector3 NormalFromTilt(Vector3 surfaceUp, Vector3 worldSurfaceTilt)
        {
            Vector3 unnormalizedNormal = surfaceUp + worldSurfaceTilt;
            float normalLength = unnormalizedNormal.magnitude;
            return normalLength > NormalLengthEpsilon
                ? unnormalizedNormal / normalLength
                : surfaceUp;
        }

        // Existing buoyancy and splash behavior treats horizontal surface tilt as a unit-scale
        // wave-drift velocity. Centralizing that compatibility mapping lets the current system add
        // real velocity later without feeding it back into NormalFromTilt.
        internal static Vector3 WaveDriftVelocityFromTilt(Vector3 worldSurfaceTilt)
            => worldSurfaceTilt;

        internal static bool IsFinite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        internal static Vector3 ComposeVelocity(Vector3 waveDriftVelocity, Vector3 currentVelocity,
                                                float verticalWaveVelocity)
        {
            Vector3 velocity = waveDriftVelocity + currentVelocity;
            velocity.y += verticalWaveVelocity;
            return velocity;
        }
    }
}
