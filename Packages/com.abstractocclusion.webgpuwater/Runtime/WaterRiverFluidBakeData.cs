// WebGpuWater - persistent output and CPU sampling contract for a settled river-fluid bake.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    // Packed texture contract (linear RGBAHalf, ribbon UV): R/G encode signed lateral/downstream
    // velocity from -MaximumSpeed..+MaximumSpeed, B is settled foam coverage, and A is the fluid
    // mask (1 fluid, 0 solid). These are the only channels consumed by current and foam steps.
    [CreateAssetMenu(menuName = "Abstract Occlusion/WebGpuWater/River Fluid Bake")]
    public sealed class WaterRiverFluidBakeData : ScriptableObject
    {
        const float MinimumMaximumSpeed = 0.001f;
        const float MinimumFluidMask = 0.5f;
        const float LookupBoundaryTolerance = 0.001f;

        [SerializeField] Texture2D packedTexture;
        [SerializeField] int lateralResolution;
        [SerializeField] int longitudinalResolution;
        [SerializeField] float riverLength;
        [SerializeField] float maximumSpeed;
        [SerializeField] float[] normalizedDistanceByParameter;

        public Texture2D PackedTexture => packedTexture;
        public int LateralResolution => lateralResolution;
        public int LongitudinalResolution => longitudinalResolution;
        public float RiverLength => riverLength;
        public float MaximumSpeed => maximumSpeed;
        public bool IsValid => packedTexture != null && packedTexture.isReadable &&
                               lateralResolution > 1 && longitudinalResolution > 1 &&
                               packedTexture.width == lateralResolution &&
                               packedTexture.height == longitudinalResolution &&
                               float.IsFinite(riverLength) && riverLength > 0f &&
                               float.IsFinite(maximumSpeed) && maximumSpeed >= MinimumMaximumSpeed &&
                               normalizedDistanceByParameter != null &&
                               normalizedDistanceByParameter.Length >= 2;

        public bool TrySample(float lateralU, float splineNormalizedT,
                              out Vector2 ribbonVelocity, out float foam, out float fluidMask)
        {
            ribbonVelocity = Vector2.zero;
            foam = 0f;
            fluidMask = 0f;
            if (!IsValid || !float.IsFinite(lateralU) || !float.IsFinite(splineNormalizedT))
                return false;

            float longitudinalV = ParameterToNormalizedDistance(splineNormalizedT);
            Color packed = packedTexture.GetPixelBilinear(
                Mathf.Clamp01(lateralU), Mathf.Clamp01(longitudinalV));
            ribbonVelocity = new Vector2(packed.r * 2f - 1f, packed.g * 2f - 1f)
                           * maximumSpeed;
            foam = Mathf.Clamp01(packed.b);
            fluidMask = Mathf.Clamp01(packed.a);
            return fluidMask >= MinimumFluidMask;
        }

        internal void Configure(Texture2D texture, float length, float speed,
                                float[] distanceLookup)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (!texture.isReadable)
                throw new ArgumentException("River fluid bake texture must remain CPU-readable.",
                                            nameof(texture));
            if (!float.IsFinite(length) || length <= 0f)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (!float.IsFinite(speed) || speed < MinimumMaximumSpeed)
                throw new ArgumentOutOfRangeException(nameof(speed));
            if (distanceLookup == null || distanceLookup.Length < 2)
                throw new ArgumentException("River fluid bake requires an arc-length lookup.",
                                            nameof(distanceLookup));
            ValidateDistanceLookup(distanceLookup);

            packedTexture = texture;
            lateralResolution = texture.width;
            longitudinalResolution = texture.height;
            riverLength = length;
            maximumSpeed = speed;
            normalizedDistanceByParameter = (float[])distanceLookup.Clone();
        }

        static void ValidateDistanceLookup(float[] lookup)
        {
            if (!float.IsFinite(lookup[0]) ||
                Mathf.Abs(lookup[0]) > LookupBoundaryTolerance)
                throw new ArgumentException(
                    "River fluid arc-length lookup must start at zero.", nameof(lookup));
            for (int index = 1; index < lookup.Length; index++)
            {
                if (!float.IsFinite(lookup[index]) || lookup[index] < lookup[index - 1])
                    throw new ArgumentException(
                        "River fluid arc-length lookup must be finite and monotonic.",
                        nameof(lookup));
            }
            if (Mathf.Abs(lookup[lookup.Length - 1] - 1f) > LookupBoundaryTolerance)
                throw new ArgumentException(
                    "River fluid arc-length lookup must end at one.", nameof(lookup));
        }

        float ParameterToNormalizedDistance(float normalizedT)
        {
            float position = Mathf.Clamp01(normalizedT) *
                             (normalizedDistanceByParameter.Length - 1);
            int lower = Mathf.FloorToInt(position);
            int upper = Mathf.Min(lower + 1, normalizedDistanceByParameter.Length - 1);
            return Mathf.Lerp(normalizedDistanceByParameter[lower],
                              normalizedDistanceByParameter[upper], position - lower);
        }
    }
}
