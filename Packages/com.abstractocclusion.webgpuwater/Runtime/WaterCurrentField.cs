// WebGpuWater - allocation-free world-space current contract.
//
// Current sources are independent components so river splines, baked maps, procedural eddies, and
// optional ecosystem adapters can share one velocity meaning without coupling their implementations.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Samples physical water velocity in world metres per second.</summary>
    public interface IWaterCurrentSampler
    {
        /// <summary>Returns false when the point is outside this sampler's domain.</summary>
        bool SampleCurrent(Vector3 worldPoint, out Vector3 worldVelocity);
    }

    /// <summary>
    /// Authorable current source. Sources return full 3D world velocity: ordinary river fields can
    /// remain horizontal, while falls and plunging streams may include a vertical component.
    /// </summary>
    public abstract class WaterCurrentField : MonoBehaviour, IWaterCurrentSampler
    {
        const string NonFiniteVelocityMessage =
            "WaterCurrentField returned a non-finite velocity. The sample was ignored.";

        bool _reportedNonFiniteVelocity;

        /// <inheritdoc/>
        public bool SampleCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (!isActiveAndEnabled || !WaterSurfaceKinematics.IsFinite(worldPoint)) return false;
            if (!TryEvaluateCurrent(worldPoint, out Vector3 evaluatedVelocity)) return false;
            if (WaterSurfaceKinematics.IsFinite(evaluatedVelocity))
            {
                worldVelocity = evaluatedVelocity;
                return true;
            }

            ReportNonFiniteVelocity();
            return false;
        }

        /// <summary>Evaluate this source. Return false when it has no influence at the point.</summary>
        protected abstract bool TryEvaluateCurrent(Vector3 worldPoint, out Vector3 worldVelocity);

        void ReportNonFiniteVelocity()
        {
            if (_reportedNonFiniteVelocity) return;
            _reportedNonFiniteVelocity = true;
            Debug.LogError(NonFiniteVelocityMessage, this);
        }
    }
}
