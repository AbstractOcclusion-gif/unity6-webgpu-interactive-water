// WebGpuWater - one CPU-owned wind-fetch bake shared by rendering and buoyancy.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSeaStateFetchField
    {
        internal const int Resolution = 256;
        internal const float FullyDevelopedFetchMeters = 10000f;
        internal const float FullyDevelopedWavelengthMeters = 100f;
        internal const float PeakWavelengthFetchExponent = 0.66f;
        internal const float SignificantHeightFetchExponent = 0.5f;

        static readonly int ID_Texture = Shader.PropertyToID("_SeaStateFetchTex");
        static readonly int ID_Frame = Shader.PropertyToID("_SeaStateFetchFrame");
        static readonly int ID_Params = Shader.PropertyToID("_SeaStateFetchParams");

        const float MinimumHalfExtentMeters = 0.01f;
        // 0.05 deg is invisible in the fetch weighting but stops micro-jitter rebakes; the real
        // drag protection is MinRebakeIntervalFrames below.
        const float WindDirectionChangeEpsilonDegrees = 0.05f;
        // A rebake is a 256^2 CPU shore raymarch; dragging the wind-direction slider used to run
        // it EVERY editor frame. While a change is pending inside the interval the stale bake
        // holds; EnsureBaked runs every frame, so the drag's final value always lands.
        const int MinRebakeIntervalFrames = 30;

        readonly WaterVolume _body;
        Texture2D _texture;
        float[] _fetch;
        Vector2 _center;
        Vector2 _halfSize;
        float _bakedWindFromDegrees = float.NaN;
        int _shoreBakeVersion = -1;
        bool _baked;
        int _lastRebakeFrame = -MinRebakeIntervalFrames;
        Color[] _pixels; // rebake scratch - Resolution is const, so both buffers are reusable

        internal WaterSeaStateFetchField(WaterVolume body)
            => _body = body ?? throw new System.ArgumentNullException(nameof(body));

        internal bool IsBaked => _baked;
        internal Texture Texture => _texture;
        internal Vector2 Center => _center;
        internal Vector2 HalfSize => _halfSize;

        internal void EnsureBaked()
        {
            if (!IsRequested)
            {
                _baked = false;
                return;
            }

            bool windChanged = float.IsNaN(_bakedWindFromDegrees)
                || Mathf.Abs(Mathf.DeltaAngle(_bakedWindFromDegrees, _body.windFromDegrees))
                   > WindDirectionChangeEpsilonDegrees;
            WaterShoreDepthField shore = _body.ShoreDepth;
            Vector3 volumeCenter = _body.VolumeCenter;
            Vector2 bodyHalfSize = _body.SeaStateFetchHalfSize;
            Vector2 halfSize = new Vector2(Mathf.Max(bodyHalfSize.x, MinimumHalfExtentMeters),
                                           Mathf.Max(bodyHalfSize.y, MinimumHalfExtentMeters));
            bool frameChanged = !Approximately(_center, new Vector2(volumeCenter.x, volumeCenter.z))
                             || !Approximately(_halfSize, halfSize);
            bool changed = windChanged || frameChanged || _shoreBakeVersion != shore.BakeVersion;
            if (_baked && !changed) return;
            // Throttle change-driven rebakes: a slider drag re-detects the change every frame, so
            // the stale bake holds until the interval passes. A never-baked field skips the
            // throttle - there is nothing stale to show while waiting.
            if (_baked && Time.frameCount - _lastRebakeFrame < MinRebakeIntervalFrames) return;
            Rebake();
        }

        internal void Rebake()
        {
            _baked = false;
            if (!IsRequested) return;

            WaterShoreDepthField shore = _body.ShoreDepth;
            shore.EnsureBaked();
            if (!shore.DepthBaked) return;

            Vector3 volumeCenter = _body.VolumeCenter;
            Vector2 volumeExtent = _body.SeaStateFetchHalfSize;
            _center = new Vector2(volumeCenter.x, volumeCenter.z);
            _halfSize = new Vector2(Mathf.Max(volumeExtent.x, MinimumHalfExtentMeters),
                                    Mathf.Max(volumeExtent.y, MinimumHalfExtentMeters));

            float windRadians = _body.LargeWaveHeadingRad;
            Vector2 upwindDirection = new Vector2(-Mathf.Cos(windRadians), -Mathf.Sin(windRadians));
            float texelSizeX = 2f * _halfSize.x / Resolution;
            float texelSizeZ = 2f * _halfSize.y / Resolution;
            float shoreTexelSizeX = 2f * shore.FieldHalfSize.x / shore.FieldResolution;
            float shoreTexelSizeZ = 2f * shore.FieldHalfSize.y / shore.FieldResolution;
            float marchStep = Mathf.Max(Mathf.Min(texelSizeX, texelSizeZ),
                                        Mathf.Min(shoreTexelSizeX, shoreTexelSizeZ),
                                        MinimumHalfExtentMeters);
            float maxMarchDistance = Mathf.Min(FullyDevelopedFetchMeters,
                                                2f * shore.FieldHalfSize.magnitude);
            int maxSteps = Mathf.CeilToInt(maxMarchDistance / marchStep);

            _fetch ??= new float[Resolution * Resolution];
            Color[] pixels = _pixels ??= new Color[Resolution * Resolution];
            for (int z = 0; z < Resolution; z++)
            {
                float worldZ = TexelToWorld(z, _center.y, _halfSize.y);
                for (int x = 0; x < Resolution; x++)
                {
                    float worldX = TexelToWorld(x, _center.x, _halfSize.x);
                    float normalizedFetch = BakePointFetch(shore, new Vector2(worldX, worldZ),
                                                           upwindDirection, marchStep, maxSteps);
                    int index = z * Resolution + x;
                    _fetch[index] = normalizedFetch;
                    pixels[index] = new Color(normalizedFetch, 0f, 0f, 0f);
                }
            }

            EnsureTexture();
            _texture.SetPixels(pixels);
            _texture.Apply(false, false);
            _bakedWindFromDegrees = _body.windFromDegrees;
            _shoreBakeVersion = shore.BakeVersion;
            _lastRebakeFrame = Time.frameCount;
            _baked = true;
        }

        float BakePointFetch(WaterShoreDepthField shore, Vector2 worldXZ, Vector2 upwindDirection,
                             float marchStep, int maxSteps)
        {
            if (!shore.TrySampleDepth(worldXZ.x, worldXZ.y, out float startDepth)) return 1f;
            if (startDepth <= 0f) return 0f;

            for (int step = 1; step <= maxSteps; step++)
            {
                float distance = step * marchStep;
                Vector2 sample = worldXZ + upwindDirection * distance;
                if (!shore.TrySampleDepth(sample.x, sample.y, out float depth)) return 1f;
                if (depth <= 0f)
                    return Mathf.Clamp01(distance / FullyDevelopedFetchMeters);
            }
            return 1f;
        }

        internal float Weight(float worldX, float worldZ, float wavelength)
        {
            if (!Live) return 1f;
            float u = (worldX - _center.x) / (2f * _halfSize.x) + 0.5f;
            float v = (worldZ - _center.y) / (2f * _halfSize.y) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return 1f;
            float normalizedFetch = WaterFieldSampling.SampleBilinear(_fetch, Resolution, u, v);
            float physicalWeight = PhysicalWeight(normalizedFetch, wavelength);
            return Mathf.Lerp(1f, physicalWeight, _body.seaStateFetchStrength);
        }

        internal static float PhysicalWeight(float normalizedFetch, float wavelength)
        {
            float wavelengthRatio = Mathf.Max(wavelength, MinimumHalfExtentMeters)
                                  / FullyDevelopedWavelengthMeters;
            float requiredFetch = FullyDevelopedFetchMeters
                                * Mathf.Pow(wavelengthRatio, 1f / PeakWavelengthFetchExponent);
            float fetchMeters = Mathf.Clamp01(normalizedFetch) * FullyDevelopedFetchMeters;
            return Mathf.Pow(Mathf.Clamp01(fetchMeters / Mathf.Max(requiredFetch, MinimumHalfExtentMeters)),
                             SignificantHeightFetchExponent);
        }

        internal void WriteUniforms(WaterUniformPublisher.IUniformSink sink)
        {
            if (sink == null) throw new System.ArgumentNullException(nameof(sink));
            sink.SetTexture(ID_Texture, Live ? (Texture)_texture : Texture2D.whiteTexture);
            sink.SetVector(ID_Frame, new Vector4(_center.x, _center.y, _halfSize.x, _halfSize.y));
            sink.SetVector(ID_Params, new Vector4(Live ? _body.seaStateFetchStrength : 0f,
                                                  FullyDevelopedFetchMeters,
                                                  FullyDevelopedWavelengthMeters,
                                                  Live ? 1f : 0f));
        }

        internal void BindTo(ComputeShader compute, int kernel)
        {
            if (compute == null) throw new System.ArgumentNullException(nameof(compute));
            compute.SetTexture(kernel, ID_Texture, Live ? (Texture)_texture : Texture2D.whiteTexture);
            compute.SetVector(ID_Frame, new Vector4(_center.x, _center.y, _halfSize.x, _halfSize.y));
            compute.SetVector(ID_Params, new Vector4(Live ? _body.seaStateFetchStrength : 0f,
                                                     FullyDevelopedFetchMeters,
                                                     FullyDevelopedWavelengthMeters,
                                                     Live ? 1f : 0f));
        }

        bool IsRequested => _body.openWater && !_body.unboundedOcean && _body.seaStateFetchEnabled;
        bool Live => IsRequested && _baked && _fetch != null && _texture != null;

        static float TexelToWorld(int texel, float center, float halfSize)
            => center + (((texel + 0.5f) / Resolution) * 2f - 1f) * halfSize;

        static bool Approximately(Vector2 first, Vector2 second)
            => Mathf.Approximately(first.x, second.x) && Mathf.Approximately(first.y, second.y);

        void EnsureTexture()
        {
            if (_texture != null && _texture.width == Resolution) return;
            DisposeTexture();
            _texture = new Texture2D(Resolution, Resolution, TextureFormat.RFloat, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                name = "SeaStateWindFetch",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        internal void Dispose()
        {
            DisposeTexture();
            _fetch = null;
            _baked = false;
            _bakedWindFromDegrees = float.NaN;
            _shoreBakeVersion = -1;
        }

        void DisposeTexture()
        {
            if (_texture == null) return;
            WaterObjects.DestroyRuntime(_texture);
            _texture = null;
        }
    }
}
