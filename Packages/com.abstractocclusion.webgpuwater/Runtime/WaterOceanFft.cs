// WebGpuWater - FFT-cascade ocean wave pass (increment 1).
//
// Owns the compute pipeline that produces the Tessendorf FFT displacement cascade behind the stable
// WaterLargeWaves.hlsl interface. Increment 1c completes the spatial pipeline: a static random spectrum
// H0 (rebuilt only on wind change) is evolved per frame into three complex displacement spectra, then a
// precomputed-butterfly inverse FFT (horizontal then vertical passes) turns them into a spatial
// displacement cascade. A throwaway preview kernel remaps that for the debug view.
//
// Ocean-only: constructed by WaterVolume solely when IsOceanClipmap and the compute is wired, so pools
// and bounded bodies stay byte-for-byte unaffected. Mirrors WaterSimulation's ownership/dispose pattern.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>GPU compute pass owning the ocean FFT cascade textures. Ocean-gated, default-off elsewhere.</summary>
    internal sealed class WaterOceanFft : System.IDisposable
    {
        // Per-cascade FFT grid side. Fixed at 128: the compute sizes its groupshared butterfly buffers at
        // this compile-time constant (FFT_SIZE) and stays well under the WebGPU threadgroup limits.
        internal const int DefaultResolution = 128;
        internal const int DefaultCascadeCount = 4;
        // Per-cascade domain size in metres, ASCENDING (KWS uses 5/20/100/600). Ascending order lets each
        // cascade own the disjoint wavelength band (prevDomain, thisDomain], so summing never double-counts.
        internal static readonly float[] DefaultDomainSizes = { 5f, 20f, 100f, 600f };
        // Per-cascade height multiplier (KWS 0.5/0.5/0.6/0.9): larger scales carry more of the swell energy.
        static readonly float[] DefaultHeightScales = { 0.5f, 0.5f, 0.6f, 0.9f };
        // Per-cascade view distance (metres) beyond which its detail fades out (KWS 40/160/800/4800): the
        // finest cascade fades first, so distant water keeps only the swell and fine ripples never alias.
        static readonly float[] DefaultVisibleAreas = { 40f, 160f, 800f, 4800f };

        const int FftSize = 128;   // must equal FFT_SIZE in OceanFft.compute
        const int FftStages = 7;   // log2(FftSize)
        const int ThreadGroupSize = 8;
        const int MaxCascades = 4;
        const int SpectrumSeed = 1337;
        const float PreviewGain = 8f; // TEMP (1c) debug-view display gain
        // Camera-centred buoyancy height field: a small readback covering the near ocean where floaters
        // live. 256 m / 128 texels = 2 m per texel, enough for swell + medium waves under a boat.
        const int HeightFieldRes = 128;
        const float HeightFieldSize = 256f;
        const int MaxReadbackErrors = 8; // give up on async readback after this many consecutive errors

        const string KernelSpectrumInit = "SpectrumInit";
        const string KernelSpectrumUpdate = "SpectrumUpdate";
        const string KernelFftHorizontal = "FftHorizontal";
        const string KernelFftVertical = "FftVertical";
        const string KernelComputeNormal = "ComputeNormal";
        const string KernelBakeHeightField = "BakeHeightField";
        const string KernelVisualizePreview = "VisualizePreview";

        static readonly int ID_H0 = Shader.PropertyToID("OceanH0");
        static readonly int ID_SpecX = Shader.PropertyToID("OceanSpecX");
        static readonly int ID_SpecY = Shader.PropertyToID("OceanSpecY");
        static readonly int ID_SpecZ = Shader.PropertyToID("OceanSpecZ");
        static readonly int ID_Displacement = Shader.PropertyToID("OceanDisplacement");
        static readonly int ID_Normal = Shader.PropertyToID("OceanNormal");
        static readonly int ID_Preview = Shader.PropertyToID("OceanPreview");
        static readonly int ID_Butterfly = Shader.PropertyToID("OceanButterfly");
        static readonly int ID_Resolution = Shader.PropertyToID("OceanFftResolution");
        static readonly int ID_Cascades = Shader.PropertyToID("OceanFftCascades");
        static readonly int ID_DomainSizes = Shader.PropertyToID("OceanDomainSizes");
        static readonly int ID_HeightScales = Shader.PropertyToID("OceanHeightScales");
        static readonly int ID_BandMin = Shader.PropertyToID("OceanBandMin");
        static readonly int ID_BandMax = Shader.PropertyToID("OceanBandMax");
        static readonly int ID_WindDir = Shader.PropertyToID("OceanWindDir");
        static readonly int ID_WindSpeed = Shader.PropertyToID("OceanWindSpeed");
        static readonly int ID_SwellWavelength = Shader.PropertyToID("OceanSwellWavelength");
        static readonly int ID_SwellHeight = Shader.PropertyToID("OceanSwellHeight");
        static readonly int ID_Time = Shader.PropertyToID("OceanFftTime");
        static readonly int ID_Seed = Shader.PropertyToID("OceanSpectrumSeed");
        static readonly int ID_PreviewGain = Shader.PropertyToID("OceanPreviewGain");
        static readonly int ID_HeightField = Shader.PropertyToID("OceanHeightField");
        static readonly int ID_FieldCenter = Shader.PropertyToID("OceanFieldCenter");
        static readonly int ID_FieldSize = Shader.PropertyToID("OceanFieldSize");
        static readonly int ID_FieldRes = Shader.PropertyToID("OceanFieldRes");
        static readonly int ID_FieldAmplitude = Shader.PropertyToID("OceanFieldAmplitude");
        static readonly int ID_GlobalDisplacement = Shader.PropertyToID("_OceanFftDisplacement");
        static readonly int ID_GlobalNormal = Shader.PropertyToID("_OceanFftNormal");
        static readonly int ID_GlobalDomainSizes = Shader.PropertyToID("_OceanFftDomainSizes");
        static readonly int ID_GlobalCascadeCount = Shader.PropertyToID("_OceanFftCascadeCount");
        static readonly int ID_GlobalVisibleAreas = Shader.PropertyToID("_OceanFftVisibleAreas");

        readonly ComputeShader _cs;
        readonly int _kInit, _kUpdate, _kFftH, _kFftV, _kNormal, _kBake, _kPreview;
        readonly System.Action<AsyncGPUReadbackRequest> _onHeightReadback;
        readonly int _resolution;
        readonly int _cascades;
        readonly int _groups;
        readonly Vector4 _domainSizes;
        readonly Vector4 _heightScales;
        readonly Vector4 _bandMin, _bandMax;
        readonly Vector4 _visibleAreas;

        RenderTexture _h0, _specX, _specY, _specZ, _displacement, _normal, _preview;
        RenderTexture _heightField;
        Texture2D _butterfly;
        bool _ready;
        bool _spectrumBuilt;
        float _lastWindSpeed, _lastWindHeading, _lastSwellWavelength, _lastSwellHeight;

        // Async buoyancy readback state (mirrors WaterSurfaceSampler's pattern).
        float[] _heightCpu;
        bool _heightReady, _readbackInFlight, _readbackUnsupported;
        int _readbackErrorStreak;
        Vector2 _bakedCenter, _pendingCenter, _sampledCenter; // region centre at bake / in-flight / landed
        float _bakedSize, _pendingSize, _sampledSize;

        // The debug view shows the readable preview, not the raw signed displacement.
        internal RenderTexture DisplacementTexture => _preview;
        internal bool Ready => _ready;

        internal WaterOceanFft(ComputeShader compute, int resolution, int cascades, float[] domainSizes)
        {
            _cs = compute ? compute : throw new System.ArgumentNullException(nameof(compute));
            _resolution = Mathf.Max(ThreadGroupSize, resolution);
            _cascades = Mathf.Clamp(cascades, 1, MaxCascades);
            _groups = Mathf.CeilToInt(_resolution / (float)ThreadGroupSize);
            _domainSizes = DomainSizesToVector(domainSizes);
            _heightScales = ArrayToVector(DefaultHeightScales, 1f);
            // Each cascade owns wavelengths (prevDomain, thisDomain]; assumes ascending domain sizes.
            _bandMin = new Vector4(0f, _domainSizes.x, _domainSizes.y, _domainSizes.z);
            _bandMax = _domainSizes;
            _visibleAreas = ArrayToVector(DefaultVisibleAreas, 1f);

            // Fail cleanly (not by throwing) on wrong/old compute or a size mismatch: disable only the FFT
            // and keep the ocean body on the analytic large-wave path.
            if (!HasAllKernels())
            {
                Debug.LogWarning($"WaterOceanFft: compute '{_cs.name}' is missing FFT kernels - assign the OceanFft " +
                                 "compute (not the old OceanFftDebug stub). FFT ocean disabled.");
                return;
            }
            if (_resolution != FftSize)
            {
                Debug.LogWarning($"WaterOceanFft: resolution {_resolution} must equal the compute's FFT_SIZE ({FftSize}); FFT ocean disabled.");
                return;
            }

            _kInit = _cs.FindKernel(KernelSpectrumInit);
            _kUpdate = _cs.FindKernel(KernelSpectrumUpdate);
            _kFftH = _cs.FindKernel(KernelFftHorizontal);
            _kFftV = _cs.FindKernel(KernelFftVertical);
            _kNormal = _cs.FindKernel(KernelComputeNormal);
            _kBake = _cs.FindKernel(KernelBakeHeightField);
            _kPreview = _cs.FindKernel(KernelVisualizePreview);
            _onHeightReadback = OnHeightReadback;
            _readbackUnsupported = !SystemInfo.supportsAsyncGPUReadback; // buoyancy falls back to analytic
            _ready = TryAllocate();
        }

        bool HasAllKernels() =>
            _cs.HasKernel(KernelSpectrumInit) && _cs.HasKernel(KernelSpectrumUpdate)
            && _cs.HasKernel(KernelFftHorizontal) && _cs.HasKernel(KernelFftVertical)
            && _cs.HasKernel(KernelComputeNormal) && _cs.HasKernel(KernelBakeHeightField)
            && _cs.HasKernel(KernelVisualizePreview);

        static Vector4 DomainSizesToVector(float[] sizes) => ArrayToVector(sizes, 1f);

        static Vector4 ArrayToVector(float[] values, float fallback)
        {
            var v = new Vector4();
            for (int i = 0; i < MaxCascades; i++)
                v[i] = (values != null && i < values.Length && values[i] > 0f) ? values[i] : fallback;
            return v;
        }

        bool TryAllocate()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures)
            {
                Debug.LogWarning("WaterOceanFft: device lacks compute shaders or 2D texture arrays; FFT ocean disabled.");
                return false;
            }

            _h0 = CreateArray("OceanFftH0", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
            _specX = CreateArray("OceanFftSpecX", RenderTextureFormat.RGHalf, RenderTextureFormat.RGFloat);
            _specY = CreateArray("OceanFftSpecY", RenderTextureFormat.RGHalf, RenderTextureFormat.RGFloat);
            _specZ = CreateArray("OceanFftSpecZ", RenderTextureFormat.RGHalf, RenderTextureFormat.RGFloat);
            _displacement = CreateArray("OceanFftDisplacement", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
            // Mipped + trilinear: the fragment samples this per pixel, so mips give distance anti-aliasing.
            _normal = CreateArray("OceanFftNormal", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat, mips: true);
            _preview = CreateArray("OceanFftPreview", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);

            _heightField = CreateHeightField();

            if (_h0 == null || _specX == null || _specY == null || _specZ == null
                || _displacement == null || _normal == null || _preview == null || _heightField == null)
            {
                Debug.LogWarning("WaterOceanFft: could not allocate the random-write float texture arrays; FFT ocean disabled.");
                return false;
            }

            _butterfly = BuildButterfly(FftSize, FftStages);
            Debug.Log($"WaterOceanFft: allocated {_resolution}x{_resolution}x{_cascades} FFT cascades ({_displacement.format}).");
            return true;
        }

        RenderTexture CreateArray(string name, RenderTextureFormat preferred, RenderTextureFormat fallback, bool mips = false)
        {
            if (TryCreateArray(name, preferred, mips, out RenderTexture rt)) return rt;
            if (TryCreateArray(name, fallback, mips, out rt)) return rt;
            return null;
        }

        bool TryCreateArray(string name, RenderTextureFormat format, bool mips, out RenderTexture rt)
        {
            rt = new RenderTexture(_resolution, _resolution, 0, format)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = _cascades,
                enableRandomWrite = true,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                useMipMap = mips,
                autoGenerateMips = false, // generated manually after the normal kernel writes mip 0
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            if (rt.IsCreated()) return true;
            rt.Release();
            Object.Destroy(rt);
            rt = null;
            return false;
        }

        // Precompute the butterfly (twiddle + input index pair) per (stage, element). Decimation-in-time:
        // stage 0 reads bit-reversed inputs; the twiddle exponent per element encodes the wing sign, so the
        // kernel is a uniform out = in[a] + w*in[b]. RGBAFloat is unclamped, so indices survive as floats.
        static Texture2D BuildButterfly(int size, int stages)
        {
            var tex = new Texture2D(stages, size, TextureFormat.RGBAFloat, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "OceanFftButterfly",
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color[stages * size];
            for (int stage = 0; stage < stages; stage++)
            {
                int span = 1 << stage;
                int block = 1 << (stage + 1);
                for (int y = 0; y < size; y++)
                {
                    int k = (y * (size >> (stage + 1))) % size;
                    float ang = 2f * Mathf.PI * k / size;
                    var tw = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    bool top = (y % block) < span;
                    int a, b;
                    if (stage == 0)
                    {
                        a = top ? BitReverse(y, stages) : BitReverse(y - span, stages);
                        b = top ? BitReverse(y + span, stages) : BitReverse(y, stages);
                    }
                    else
                    {
                        a = top ? y : y - span;
                        b = top ? y + span : y;
                    }
                    px[y * stages + stage] = new Color(tw.x, tw.y, a, b);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false);
            return tex;
        }

        // Single-channel camera-centred buoyancy field (2D, not an array). RFloat -> readable as R32 on CPU.
        RenderTexture CreateHeightField()
        {
            var rt = new RenderTexture(HeightFieldRes, HeightFieldRes, 0, RenderTextureFormat.RFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = "OceanFftHeightField",
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            if (rt.IsCreated()) return rt;
            rt.Release();
            Object.Destroy(rt);
            return null;
        }

        static int BitReverse(int x, int bits)
        {
            int r = 0;
            for (int i = 0; i < bits; i++) { r = (r << 1) | (x & 1); x >>= 1; }
            return r;
        }

        // Per-frame: (re)build H0 on a wind change, evolve, inverse-FFT to a spatial displacement cascade,
        // preview, and publish the displacement array as a global for the surface shader (from increment 2).
        internal void Dispatch(float waveTime, float windSpeed, float windHeadingRad, float amplitude,
                               float swellWavelength, float swellHeight, Vector2 cameraXZ)
        {
            if (!_ready) return;
            SetSharedUniforms(windSpeed, windHeadingRad, swellWavelength, swellHeight);

            // H0 is static: rebuild only when a spectrum input (wind or swell) actually changes.
            if (!_spectrumBuilt || windSpeed != _lastWindSpeed || windHeadingRad != _lastWindHeading
                || swellWavelength != _lastSwellWavelength || swellHeight != _lastSwellHeight)
            {
                _cs.SetTexture(_kInit, ID_H0, _h0);
                _cs.Dispatch(_kInit, _groups, _groups, _cascades);
                _spectrumBuilt = true;
                _lastWindSpeed = windSpeed;
                _lastWindHeading = windHeadingRad;
                _lastSwellWavelength = swellWavelength;
                _lastSwellHeight = swellHeight;
            }

            _cs.SetFloat(ID_Time, waveTime);
            BindSpectra(_kUpdate, bindH0: true);
            _cs.Dispatch(_kUpdate, _groups, _groups, _cascades);

            // Row FFT then column FFT (one threadgroup per row / per column of length FftSize).
            BindFft(_kFftH);
            _cs.Dispatch(_kFftH, 1, _resolution, _cascades);
            BindFft(_kFftV);
            _cs.Dispatch(_kFftV, _resolution, 1, _cascades);

            // Normal + foam cascade from the finished displacement, then mips for per-pixel trilinear
            // sampling. GenerateMips on an array RT may no-op on some WebGPU backends; the fragment then
            // just samples mip 0 (still correct, less distance anti-aliasing) - it never hard-fails.
            _cs.SetTexture(_kNormal, ID_Displacement, _displacement);
            _cs.SetTexture(_kNormal, ID_Normal, _normal);
            _cs.Dispatch(_kNormal, _groups, _groups, _cascades);
            if (_normal.useMipMap) _normal.GenerateMips();

            // Bake the camera-centred height field for CPU buoyancy readback.
            _bakedCenter = cameraXZ;
            _bakedSize = HeightFieldSize;
            _cs.SetVector(ID_FieldCenter, new Vector4(cameraXZ.x, cameraXZ.y, 0f, 0f));
            _cs.SetFloat(ID_FieldSize, HeightFieldSize);
            _cs.SetInt(ID_FieldRes, HeightFieldRes);
            _cs.SetFloat(ID_FieldAmplitude, amplitude);
            _cs.SetTexture(_kBake, ID_Displacement, _displacement);
            _cs.SetTexture(_kBake, ID_HeightField, _heightField);
            int bakeGroups = Mathf.CeilToInt(HeightFieldRes / (float)ThreadGroupSize);
            _cs.Dispatch(_kBake, bakeGroups, bakeGroups, 1);

            _cs.SetFloat(ID_PreviewGain, PreviewGain);
            _cs.SetTexture(_kPreview, ID_Displacement, _displacement);
            _cs.SetTexture(_kPreview, ID_Preview, _preview);
            _cs.Dispatch(_kPreview, _groups, _groups, _cascades);

            // Cascade textures + layout are global (only the ocean body samples them); the per-body
            // _OceanFftActive flag (published in WaterUniformPublisher.WriteBodyProps) decides who does.
            Shader.SetGlobalTexture(ID_GlobalDisplacement, _displacement);
            Shader.SetGlobalTexture(ID_GlobalNormal, _normal);
            Shader.SetGlobalVector(ID_GlobalDomainSizes, _domainSizes);
            Shader.SetGlobalFloat(ID_GlobalCascadeCount, _cascades);
            Shader.SetGlobalVector(ID_GlobalVisibleAreas, _visibleAreas);
        }

        // Throttled by the caller (like WaterSurfaceSampler): one request in flight, stored region centre so
        // the landed data is sampled against the centre it was baked at (the camera moved since).
        internal void RequestHeightReadback()
        {
            if (!_ready || _readbackInFlight || _readbackUnsupported) return;
            _readbackInFlight = true;
            _pendingCenter = _bakedCenter;
            _pendingSize = _bakedSize;
            AsyncGPUReadback.Request(_heightField, 0, TextureFormat.RFloat, _onHeightReadback);
        }

        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            _readbackInFlight = false;
            if (req.hasError)
            {
                if (++_readbackErrorStreak >= MaxReadbackErrors) { _readbackUnsupported = true; _heightReady = false; }
                return;
            }
            _readbackErrorStreak = 0;
            var data = req.GetData<float>();
            if (_heightCpu == null || _heightCpu.Length != data.Length) _heightCpu = new float[data.Length];
            data.CopyTo(_heightCpu);
            _sampledCenter = _pendingCenter;
            _sampledSize = _pendingSize;
            _heightReady = true;
        }

        // DEFERRED IMPROVEMENTS (tracked, intentionally not done yet):
        //  1. Choppiness inversion - we sample the base world xz, so under strong chop the buoyancy height
        //     lags the horizontally-folded crest. Add Crest-style iterative displacement inversion (needs a
        //     displacement readback too), matching LargeWaveField.InvertToSource.
        //  2. Region size - the readback covers a 256 m camera-centred square; widen it (or add a coarse
        //     outer ring) so far-flung floaters don't fall back to the analytic field.
        //  3. Async lag - the height is 1-2 frames stale; fine for buoyancy, revisit if fast boats need it.
        //  4. Batched multi-queries - one shared field serves every query; a per-point GPU query buffer
        //     (KWS BuoyancyPass) would be more accurate for sparse, far-apart query points.
        //
        // World-space (height, dHeight/dx, dHeight/dz) at a world xz, from the last readback. False before
        // the first readback lands or when the point is outside the baked camera-centred region.
        internal bool TrySampleField(float worldX, float worldZ, out Vector3 heightSlope)
        {
            heightSlope = Vector3.zero;
            if (!_heightReady || _heightCpu == null || _sampledSize <= 0f) return false;
            float u = (worldX - _sampledCenter.x) / _sampledSize + 0.5f;
            float v = (worldZ - _sampledCenter.y) / _sampledSize + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

            float texel = _sampledSize / HeightFieldRes; // metres per texel
            float du = 1f / HeightFieldRes;
            float h = SampleFieldBilinear(u, v);
            float slopeX = (SampleFieldBilinear(Mathf.Clamp01(u + du), v) - SampleFieldBilinear(Mathf.Clamp01(u - du), v)) / (2f * texel);
            float slopeZ = (SampleFieldBilinear(u, Mathf.Clamp01(v + du)) - SampleFieldBilinear(u, Mathf.Clamp01(v - du))) / (2f * texel);
            heightSlope = new Vector3(h, slopeX, slopeZ);
            return true;
        }

        float SampleFieldBilinear(float u, float v)
        {
            int res = HeightFieldRes;
            float sx = Mathf.Clamp(u * res - 0.5f, 0f, res - 1f);
            float sz = Mathf.Clamp(v * res - 0.5f, 0f, res - 1f);
            int x0 = (int)sx, z0 = (int)sz;
            int x1 = Mathf.Min(x0 + 1, res - 1), z1 = Mathf.Min(z0 + 1, res - 1);
            float tx = sx - x0, tz = sz - z0;
            float b = Mathf.Lerp(_heightCpu[z0 * res + x0], _heightCpu[z0 * res + x1], tx);
            float t = Mathf.Lerp(_heightCpu[z1 * res + x0], _heightCpu[z1 * res + x1], tx);
            return Mathf.Lerp(b, t, tz);
        }

        void SetSharedUniforms(float windSpeed, float windHeadingRad, float swellWavelength, float swellHeight)
        {
            _cs.SetInt(ID_Resolution, _resolution);
            _cs.SetInt(ID_Cascades, _cascades);
            _cs.SetInt(ID_Seed, SpectrumSeed);
            _cs.SetVector(ID_DomainSizes, _domainSizes);
            _cs.SetVector(ID_HeightScales, _heightScales);
            _cs.SetVector(ID_BandMin, _bandMin);
            _cs.SetVector(ID_BandMax, _bandMax);
            _cs.SetVector(ID_WindDir, new Vector4(Mathf.Cos(windHeadingRad), Mathf.Sin(windHeadingRad), 0f, 0f));
            _cs.SetFloat(ID_WindSpeed, Mathf.Max(0f, windSpeed));
            _cs.SetFloat(ID_SwellWavelength, Mathf.Max(1e-3f, swellWavelength));
            _cs.SetFloat(ID_SwellHeight, Mathf.Max(0f, swellHeight));
        }

        void BindSpectra(int kernel, bool bindH0)
        {
            if (bindH0) _cs.SetTexture(kernel, ID_H0, _h0);
            _cs.SetTexture(kernel, ID_SpecX, _specX);
            _cs.SetTexture(kernel, ID_SpecY, _specY);
            _cs.SetTexture(kernel, ID_SpecZ, _specZ);
        }

        void BindFft(int kernel)
        {
            BindSpectra(kernel, bindH0: false);
            _cs.SetTexture(kernel, ID_Butterfly, _butterfly);
            _cs.SetTexture(kernel, ID_Displacement, _displacement);
        }

        public void Dispose()
        {
            Release(ref _h0);
            Release(ref _specX);
            Release(ref _specY);
            Release(ref _specZ);
            Release(ref _displacement);
            Release(ref _normal);
            Release(ref _preview);
            Release(ref _heightField);
            _heightReady = false;
            if (_butterfly != null)
            {
                if (Application.isPlaying) Object.Destroy(_butterfly); else Object.DestroyImmediate(_butterfly);
                _butterfly = null;
            }
            _ready = false;
            _spectrumBuilt = false;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Object.Destroy(rt); else Object.DestroyImmediate(rt);
            rt = null;
        }
    }
}
