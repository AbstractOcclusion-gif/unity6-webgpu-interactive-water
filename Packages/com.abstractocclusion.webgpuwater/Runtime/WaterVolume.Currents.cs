// WebGpuWater - WaterVolume current composition and public current query.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [Tooltip("Additive current sources for this body. Empty preserves still-water behaviour. " +
                 "Each source returns full world-space velocity, so fields may also describe falls.")]
        [SerializeField] internal WaterCurrentField[] currentFields = Array.Empty<WaterCurrentField>();

        /// <summary>
        /// Samples this body's authored physical current in world metres per second. Returns true with
        /// zero velocity inside a body that has no active fields, and false outside its footprint.
        /// </summary>
        public bool SampleCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (!WaterSurfaceKinematics.IsFinite(worldPoint)) return false;

            Vector3 surfaceProbe = new Vector3(worldPoint.x, VolumeCenter.y, worldPoint.z);
            if (!QueryPoolXZ(surfaceProbe, out _, out _)) return false;

            worldVelocity = SampleCurrentFields(worldPoint);
            return true;
        }

        // The containing-body test has already run on surface-query paths. Keeping composition here
        // avoids a second transform/footprint calculation for every buoyancy probe.
        internal Vector3 SampleCurrentFields(Vector3 worldPoint)
        {
            Vector3 worldVelocity = Vector3.zero;
            if (currentFields == null) return worldVelocity;

            for (int i = 0; i < currentFields.Length; i++)
            {
                WaterCurrentField field = currentFields[i];
                if (field == null || !field.SampleCurrent(worldPoint, out Vector3 fieldVelocity))
                    continue;

                worldVelocity += fieldVelocity;
            }

            return worldVelocity;
        }
    }
}
