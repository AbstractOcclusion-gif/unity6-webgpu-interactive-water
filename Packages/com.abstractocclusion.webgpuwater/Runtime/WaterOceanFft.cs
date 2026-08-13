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
        internal readonly struct AperiodicParams
        {
            internal readonly bool Enabled;
            internal readonly Texture2D DirectionMap;
            internal readonly Vector2 MapCenter;
            internal readonly float MapSize;
            internal readonly float DirectionStrength;
            internal readonly float TileScale;

            internal AperiodicParams(bool enabled, Texture2D directionMap, Vector2 mapCenter,
                                     float mapSize, float directionStrength, float tileScale)
            {
                Enabled = enabled;
                DirectionMap = directionMap;
                MapCenter = mapCenter;
                MapSize = Mathf.Max(1f, mapSize);
                DirectionStrength = Mathf.Clamp01(directionStrength);
                TileScale = Mathf.Clamp(tileScale, 0.5f, 2f);
            }
        }

        /// <summary>Per-body whitecap-foam accumulation knobs, authored on the ocean WaterVolume.</summary>
        internal readonly struct FoamParams : System.IEquatable<FoamParams>
        {
            internal readonly float WindThreshold; // m/s; no foam below this wind speed
            internal readonly float Coverage;      // fold threshold: fresh = saturate(coverage - jacobian)
            internal readonly float Strength;      // accumulation gain per unit fold
            internal readonly float FadeRate;      // exponential decay per second (lower = foam lingers)
            internal readonly float SlowFadeFraction; // dense-foam decay as a fraction of FadeRate (deposit persistence)
            internal readonly float DriftFraction;    // downwind roll speed as a fraction of wind speed
            internal readonly float Max;              // accumulation ceiling (how dense deposits can pile up)
            internal readonly float CrestAnisotropy;  // 0 = area fold (determinant), 1 = single-axis fold
            internal readonly float CrestGate;        // 0 = fold anywhere, 1 = only on the crest line
            internal readonly float FaceBias;         // 0 = both faces, 1 = leading (downwind) face only
            internal readonly float CascadeMix;       // 0 = damped per-cascade fold weights, 1 = all cascades
            internal FoamParams(float windThreshold, float coverage, float strength, float fadeRate,
                                float slowFadeFraction, float driftFraction, float max,
                                float crestAnisotropy, float crestGate, float faceBias, float cascadeMix)
            {
                WindThreshold = windThreshold; Coverage = coverage; Strength = strength; FadeRate = fadeRate;
                SlowFadeFraction = slowFadeFraction; DriftFraction = driftFraction; Max = max;
                CrestAnisotropy = crestAnisotropy; CrestGate = crestGate; FaceBias = faceBias;
                CascadeMix = cascadeMix;
            }

            public bool Equals(FoamParams other) =>
                WindThreshold == other.WindThreshold && Coverage == other.Coverage
                && Strength == other.Strength && FadeRate == other.FadeRate
                && SlowFadeFraction == other.SlowFadeFraction && DriftFraction == other.DriftFraction
                && Max == other.Max && CrestAnisotropy == other.CrestAnisotropy
                && CrestGate == other.CrestGate && FaceBias == other.FaceBias
                && CascadeMix == other.CascadeMix;
            public override bool Equals(object obj) => obj is FoamParams other && Equals(other);
            public override int GetHashCode() => System.HashCode.Combine(
                System.HashCode.Combine(WindThreshold, Coverage, Strength, FadeRate,
                                        SlowFadeFraction, DriftFraction, Max, CrestAnisotropy),
                CrestGate, FaceBias, CascadeMix);
        }

        /// <summary>The authored sea state. Any change rebuilds the cascade layout, the gains and H0.</summary>
        /// <remarks>
        /// Grouped rather than passed as ten arguments because they are ONE thing: the spectrum. They are
        /// also the exact set the rebuild edge tests, so bundling them makes "did the sea change" a single
        /// equality rather than a line of ORs that a new knob can silently be left out of.
        /// </remarks>
        internal readonly struct SeaParams : System.IEquatable<SeaParams>
        {
            internal readonly float WindSpeed;        // m/s; steers spreading, whitecaps and foam drift ONLY
            internal readonly float WindHeadingRad;
            internal readonly float WindTurbulence;   // 0 = ordered downwind march, 1 = isotropic
            internal readonly float SignificantHeight;// metres of Hs the wind sea is normalised to
            internal readonly float PeakWavelength;   // metres; where the wind sea's energy sits
            internal readonly float PeakSharpness;    // JONSWAP gamma
            internal readonly float SeaDepth;         // metres for TMA; <= 0 = deep water
            internal readonly float Choppiness;       // horizontal Gerstner displacement scale
            internal readonly float SwellWavelength;
            internal readonly float SwellHeight;      // metres of Hs for the swell ring
            internal readonly float SwellHeadingRad;  // absolute swell travel heading (wind + authored offset)
            internal readonly float CascadeReach;     // multiplier on how far each cascade stays drawn
            internal SeaParams(float windSpeed, float windHeadingRad, float windTurbulence,
                               float significantHeight, float peakWavelength, float peakSharpness,
                               float seaDepth, float choppiness, float swellWavelength, float swellHeight,
                               float swellHeadingRad, float cascadeReach)
            {
                CascadeReach = cascadeReach;
                WindSpeed = windSpeed; WindHeadingRad = windHeadingRad; WindTurbulence = windTurbulence;
                SignificantHeight = significantHeight; PeakWavelength = peakWavelength;
                PeakSharpness = peakSharpness; SeaDepth = seaDepth; Choppiness = choppiness;
                SwellWavelength = swellWavelength; SwellHeight = swellHeight;
                SwellHeadingRad = swellHeadingRad;
            }

            /// <summary>True when the SHAPE is unchanged - the only inputs the cascade layout and the
            /// normalisation gains depend on.</summary>
            /// <remarks>
            /// Split out from Equals because the two rebuilds cost wildly different amounts. Re-running
            /// SpectrumInit is one GPU dispatch; recomputing the gains is a resolution^2 * cascades CPU
            /// integral. Wind heading, wind speed, turbulence and choppiness all change the FIELD without
            /// changing its energy, so a gust curve or a turning heading must reach the first and never
            /// the second - otherwise animating the wind costs 65k transcendental evaluations a frame for
            /// an answer that is provably identical (see WaterOceanSpectrum.ComputeGains).
            /// </remarks>
            internal bool ShapeEquals(SeaParams other) =>
                SignificantHeight == other.SignificantHeight && PeakWavelength == other.PeakWavelength
                && PeakSharpness == other.PeakSharpness && SeaDepth == other.SeaDepth
                && SwellWavelength == other.SwellWavelength && SwellHeight == other.SwellHeight
                && CascadeReach == other.CascadeReach;

            public bool Equals(SeaParams other) =>
                WindSpeed == other.WindSpeed && WindHeadingRad == other.WindHeadingRad
                && WindTurbulence == other.WindTurbulence && SignificantHeight == other.SignificantHeight
                && PeakWavelength == other.PeakWavelength && PeakSharpness == other.PeakSharpness
                && SeaDepth == other.SeaDepth && Choppiness == other.Choppiness
                && SwellWavelength == other.SwellWavelength && SwellHeight == other.SwellHeight
                && SwellHeadingRad == other.SwellHeadingRad
                && CascadeReach == other.CascadeReach;
            public override bool Equals(object obj) => obj is SeaParams other && Equals(other);
            public override int GetHashCode() => System.HashCode.Combine(
                WindSpeed, WindHeadingRad, WindTurbulence, SignificantHeight,
                PeakWavelength, PeakSharpness, SeaDepth, Choppiness);
        }

        // Per-cascade FFT grid side. Fixed at 128: the compute sizes its groupshared butterfly buffers at
        // this compile-time constant (FFT_SIZE) and stays well under the WebGPU threadgroup limits.
        internal const int DefaultResolution = 128;
        internal const int DefaultCascadeCount = 4;
        // THE FFT TILE IS NOT THE BAND. A cascade whose tile equals its band top can only hold ONE period
        // of its longest wave, and a decaying spectrum puts most of the band's energy exactly there - so
        // the cascade degenerates into a handful of sinusoids repeating at the tile pitch (measured:
        // cascades 1-3 had 38/62/98 live spectral bins out of 16384, and 6/48/21 EFFECTIVE modes). Making
        // the tile several times longer refines the k-lattice inside the SAME band: ~16x more live bins at
        // the same resolution, same dispatch count, same cost. This is Crest's WAVE_SAMPLE_FACTOR rule (its
        // bands top out at a quarter of the patch, 4-8 periods per tile).
        //
        // The energy is held constant by OceanCascadeMeasure in the compute, so this is a variety knob,
        // NOT a height knob. Raising it costs the SHORT end: cascade 0's texel is tile/128.
        // HLSL pair: OCEAN_FFT_CASCADE_WAVELENGTH_FRACTION in WaterShared.hlsl is 0.25 DIVIDED BY THIS -
        // change one without the other and shore attenuation silently retunes.
        internal const float CascadeTileOversample = 4f;

        // HLSL pair: FFT_SIZE / FFT_STAGES in OceanFft.compute, both validator-guarded.
        const int FftSize = 128;
        const int FftStages = 7;   // log2(FftSize)
        const int ThreadGroupSize = 8;
        const int MaxCascades = 4; // HLSL pair: OCEAN_FFT_MAX_CASCADES (validator-guarded)
        const int SpectrumSeed = 1337;
        const float PreviewGain = 8f; // debug-view display gain (editor/dev builds only)
        // Ocean whitecap foam internal calibration (NOT art knobs - the coverage/strength/fade/threshold
        // sliders live on the ocean WaterVolume and arrive via FoamParams). Named so there are no magic numbers.
        const float MaxFoamDeltaTime = 0.1f; // clamp dt so a frame hitch or pause can't over-accumulate foam
        // Camera-centred buoyancy height field: a small readback covering the near ocean where floaters
        // live. 256 m / 128 texels = 2 m per texel, enough for swell + medium waves under a boat.
        const int HeightFieldRes = 128;
        const float HeightFieldSize = 256f;
        // Readback give-up threshold lives on AsyncReadbackChannel.MaxConsecutiveErrors (shared).

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
        static readonly int ID_BandMin = Shader.PropertyToID("OceanBandMin");
        static readonly int ID_BandMax = Shader.PropertyToID("OceanBandMax");
        static readonly int ID_VisibleAreas = Shader.PropertyToID("OceanVisibleAreas");
        static readonly int ID_WindDir = Shader.PropertyToID("OceanWindDir");
        static readonly int ID_SwellDir = Shader.PropertyToID("OceanSwellDir");
        static readonly int ID_WindSpeed = Shader.PropertyToID("OceanWindSpeed");
        static readonly int ID_WindTurbulence = Shader.PropertyToID("OceanWindTurbulence");
        static readonly int ID_PeakAngularFreq = Shader.PropertyToID("OceanPeakAngularFreq");
        static readonly int ID_PeakSharpness = Shader.PropertyToID("OceanPeakSharpness");
        static readonly int ID_SeaDepth = Shader.PropertyToID("OceanSeaDepth");
        static readonly int ID_SpectrumGain = Shader.PropertyToID("OceanSpectrumGain");
        static readonly int ID_SwellGain = Shader.PropertyToID("OceanSwellGain");
        static readonly int ID_Choppiness = Shader.PropertyToID("OceanChoppiness");
        static readonly int ID_FoamAnisotropy = Shader.PropertyToID("OceanFoamAnisotropy");
        static readonly int ID_FoamCrestGate = Shader.PropertyToID("OceanFoamCrestGate");
        static readonly int ID_FoamFaceBias = Shader.PropertyToID("OceanFoamFaceBias");
        static readonly int ID_FoamCascadeMix = Shader.PropertyToID("OceanFoamCascadeMix");
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
        static readonly int ID_GlobalNormal = WaterShaderProps.OceanFftNormal;
        static readonly int ID_GlobalDomainSizes = WaterShaderProps.OceanFftDomainSizes;
        static readonly int ID_GlobalCascadeCount = WaterShaderProps.OceanFftCascadeCount;
        static readonly int ID_GlobalVisibleAreas = Shader.PropertyToID("_OceanFftVisibleAreas");
        static readonly int ID_FoamPrev = Shader.PropertyToID("OceanFoamPrev");
        static readonly int ID_FoamNext = Shader.PropertyToID("OceanFoamNext");
        static readonly int ID_FoamDeltaTime = Shader.PropertyToID("OceanFoamDeltaTime");
        static readonly int ID_FoamHistoryValid = Shader.PropertyToID("OceanFoamHistoryValid");
        static readonly int ID_FoamMinWind = Shader.PropertyToID("OceanFoamMinWind");
        static readonly int ID_FoamCoverage = Shader.PropertyToID("OceanFoamCoverage");
        static readonly int ID_FoamStrength = Shader.PropertyToID("OceanFoamStrength");
        static readonly int ID_FoamFadeRate = Shader.PropertyToID("OceanFoamFadeRate");
        static readonly int ID_FoamMax = Shader.PropertyToID("OceanFoamMax");
        static readonly int ID_FoamSlowFade = Shader.PropertyToID("OceanFoamSlowFadeFraction");
        static readonly int ID_FoamDrift = Shader.PropertyToID("OceanFoamDriftFraction");
        static readonly int ID_OceanDirectionMap = WaterShaderProps.OceanDirectionMap;
        static readonly int ID_OceanAperiodicParams = WaterShaderProps.OceanAperiodicParams;
        static readonly int ID_OceanDirectionMapFrame = WaterShaderProps.OceanDirectionMapFrame;

        readonly ComputeShader _cs;
        readonly int _kInit, _kUpdate, _kFftH, _kFftV, _kNormal, _kBake, _kPreview;
        readonly System.Action<AsyncGPUReadbackRequest> _onHeightReadback;
        readonly int _resolution;
        readonly int _cascades;
        readonly int _groups;
        // The cascade layout is DERIVED from the authored peak wavelength, so it is state, not
        // configuration: a Gulliver sea and an open ocean run the same code with different bands.
        Vector4 _domainSizes;
        Vector4 _bandMin, _bandMax;
        Vector4 _visibleAreas;
        float _windSeaGain, _swellGain;

        RenderTexture _h0, _specX, _specY, _specZ, _displacement, _normal, _preview;
        RenderTexture _heightField;
        RenderTexture _foamHistA, _foamHistB; // ping-pong accumulated-foam history (one slice per cascade)
        float _lastDispatchTime;              // wave time at the previous dispatch, for the foam delta time
        bool _hasLastDispatchTime;            // false until the first dispatch runs (history not yet valid)
        Texture2D _butterfly;
        bool _ready;
        bool _spectrumBuilt;
        SeaParams _lastSea;
        bool _hasLastSea;
        // Shape-rebuild throttle (see Dispatch): the CPU gain re-integration runs at most once per
        // interval. ~0.25 s at 60 fps - fast enough to feel live under a slider drag, 15x cheaper
        // than the per-frame re-integration an exact-equality edge produced while dragging.
        const int MinShapeRebuildIntervalFrames = 15;
        int _lastShapeRebuildFrame = -MinShapeRebuildIntervalFrames;
        FoamParams _lastFoam;
        bool _hasLastFoam;

        // Async buoyancy readback: throttle/error-streak/unsupported state lives on the shared
        // channel (the same machinery WaterSurfaceSampler uses); the landed buffer stays here.
        readonly AsyncReadbackChannel _readback;
        Color[] _heightCpu; // (r = dispX, g = height, b = dispZ) - matches the bake's channel order
        bool _heightReady;
        Vector2 _bakedCenter, _pendingCenter, _sampledCenter; // region centre at bake / in-flight / landed
        float _bakedSize, _pendingSize, _sampledSize;
        // Wave-clock stamp travelling with the region through the same three stages, so a landed
        // field knows WHEN it was baked. Without it two landings cannot be differenced in time.
        float _bakedTime, _pendingTime, _sampledTime;

        // The landing BEFORE the current one, kept so TrySampleField can measure d(height)/dt on the
        // FFT surface itself instead of borrowing a rate from the analytic mirror - a field the FFT
        // branch does not render, whose phase is unrelated to this one's height. Its own centre/size
        // are kept beside it: the camera moves between bakes, so a shared UV would difference two
        // different PLACES and report their height gap as a velocity.
        Color[] _heightCpuPrev;
        Vector2 _sampledCenterPrev;
        float _sampledSizePrev, _sampledTimePrev;
        bool _hasPrevField;

        // The complete height field is only needed by CPU consumers (buoyancy, accurate
        // submersion and displaced-space interaction placement). Keeping the last request alive
        // briefly covers the physics/render cadence and async landing latency without moving a
        // 256 KiB field across the GPU/CPU boundary for decorative oceans.
        const int ReadbackDemandWindowFrames = 12;
        int _lastReadbackDemandFrame = -1000;

        // The debug view shows the readable preview, not the raw signed displacement.
        // Null in release builds: the preview array is a debug aid and is neither allocated nor
        // dispatched there (see TryAllocate / the gated preview dispatch).
        internal RenderTexture DisplacementTexture
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return _preview;
#else
                return null;
#endif
            }
        }
        internal bool Ready => _ready;
        // Cascade data for consumers outside the render globals (e.g. the foam-particle spawn compute,
        // which samples the whitecap .w to emit crest foam).
        internal RenderTexture NormalTexture => _normal;
        // Raw spatial displacement cascade (.y = height, per-cascade scale baked in). The foam-particle
        // density splat sums it to place foam on the real swell (mirrors BakeHeightField's math).
        internal RenderTexture SpatialTexture => _displacement;
        internal Vector4 DomainSizes => _domainSizes;
        internal int CascadeCount => _cascades;

        internal WaterOceanFft(ComputeShader compute, int resolution, int cascades)
        {
            _cs = compute ? compute : throw new System.ArgumentNullException(nameof(compute));
            _resolution = Mathf.Max(ThreadGroupSize, resolution);
            _cascades = Mathf.Clamp(cascades, 1, MaxCascades);
            _groups = Mathf.CeilToInt(_resolution / (float)ThreadGroupSize);
            // Placeholder layout until the first Dispatch derives the real one from the authored sea.
            // Ones, not zeros: every consumer divides by the domain size, and Ready goes true here - one
            // frame ahead of the first Dispatch - so a zero would be a divide by zero in the window
            // between construction and the first sea state arriving.
            _domainSizes = _bandMax = _visibleAreas = Vector4.one;

            // Fail cleanly (not by throwing) on wrong/old compute or a size mismatch: disable only the FFT
            // and keep the ocean body on the analytic large-wave path.
            if (!HasAllKernels())
            {
                Debug.LogWarning($"WaterOceanFft: compute '{_cs.name}' is missing FFT kernels - assign the OceanFft " +
                                 "compute. FFT ocean disabled.");
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
            // The channel probes SystemInfo.supportsAsyncGPUReadback itself; on give-up (backend
            // unsupported or persistent errors) buoyancy falls back to analytic, and the stale
            // field is dropped so nothing keeps floating on it.
            _readback = new AsyncReadbackChannel(onGaveUp: () => _heightReady = false);
            _ready = TryAllocate();
        }

        bool HasAllKernels() =>
            _cs.HasKernel(KernelSpectrumInit) && _cs.HasKernel(KernelSpectrumUpdate)
            && _cs.HasKernel(KernelFftHorizontal) && _cs.HasKernel(KernelFftVertical)
            && _cs.HasKernel(KernelComputeNormal) && _cs.HasKernel(KernelBakeHeightField)
            && _cs.HasKernel(KernelVisualizePreview);

        bool TryAllocate()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures)
            {
                Debug.LogWarning("WaterOceanFft: device lacks compute shaders or 2D texture arrays; FFT ocean disabled.");
                return false;
            }

            _h0 = CreateArray("OceanFftH0", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
            // Complex spectra packed as R32_UINT (two half-floats/texel): the in-place butterfly FFT reads
            // AND writes these, and WebGPU only allows read-write storage on the r32 formats (rg16float is
            // not even a storage format there). The compute packs/unpacks via OceanPackC/OceanUnpackC.
            _specX = CreateSpecArray("OceanFftSpecX");
            _specY = CreateSpecArray("OceanFftSpecY");
            _specZ = CreateSpecArray("OceanFftSpecZ");
            _displacement = CreateArray("OceanFftDisplacement", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
            // Mipped + trilinear: the fragment samples this per pixel, so mips give distance anti-aliasing.
            _normal = CreateArray("OceanFftNormal", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat, mips: true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Debug-view only (WaterOceanFftDebugView): never allocated or dispatched in release builds.
            _preview = CreateArray("OceanFftPreview", RenderTextureFormat.ARGBHalf, RenderTextureFormat.ARGBFloat);
            if (_preview == null)
            {
                Debug.LogWarning("WaterOceanFft: could not allocate the debug preview array; FFT ocean disabled.");
                return false;
            }
#endif

            // Foam history: single-channel, ping-ponged so ComputeNormal reads last frame's accumulated foam
            // (SRV) and writes this frame's (UAV) - WebGPU forbids read+write on one storage texture. RFloat
            // (r32f), not RHalf: r16f is NOT a WebGPU storage format, and this is point-read (no filtering)
            // so float32 costs nothing here (the filtered surface sample reads the half-float OceanNormal.w).
            _foamHistA = CreateArray("OceanFftFoamA", RenderTextureFormat.RFloat, RenderTextureFormat.RFloat);
            _foamHistB = CreateArray("OceanFftFoamB", RenderTextureFormat.RFloat, RenderTextureFormat.RFloat);

            _heightField = CreateHeightField();

            if (_h0 == null || _specX == null || _specY == null || _specZ == null
                || _displacement == null || _normal == null || _heightField == null
                || _foamHistA == null || _foamHistB == null)
            {
                Debug.LogWarning("WaterOceanFft: could not allocate the random-write float texture arrays; FFT ocean disabled.");
                return false;
            }

            _butterfly = BuildButterfly(FftSize, FftStages);
            return true;
        }

        RenderTexture CreateArray(string name, RenderTextureFormat preferred, RenderTextureFormat fallback, bool mips = false)
        {
            if (TryCreateArray(name, preferred, mips, out RenderTexture rt)) return rt;
            if (TryCreateArray(name, fallback, mips, out rt)) return rt;
            return null;
        }

        // Spectrum array target in R32_UINT (packed complex, read-write storage). Point-sampled: the FFT
        // only index-loads it, never filters. R32_UInt is universally random-write capable, so no fallback.
        RenderTexture CreateSpecArray(string name)
        {
            var desc = new RenderTextureDescriptor(_resolution, _resolution,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R32_UInt, 0)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = _cascades,
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false,
            };
            var rt = new RenderTexture(desc)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            if (rt.IsCreated()) return rt;
            rt.Release();
            Object.Destroy(rt);
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

        // Camera-centred buoyancy/inversion field (2D, not an array): (dispX, height, dispZ, 0).
        // ARGBFloat since 2026-08-03: the horizontal displacement lanes ride along so the CPU can
        // invert chop (wake injection; deferred-improvement #1 below). 128x128x16B = 256 KB/readback.
        RenderTexture CreateHeightField()
        {
            var rt = new RenderTexture(HeightFieldRes, HeightFieldRes, 0, RenderTextureFormat.ARGBFloat)
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
        internal void Dispatch(float waveTime, in SeaParams sea, float amplitude,
                               Vector2 cameraXZ, in FoamParams foam,
                               WaterSeaStateFetchField fetchField, in AperiodicParams aperiodic)
        {
            if (!_ready) return;

            // TWO edges, not one. The cascade LAYOUT and the normalisation GAINS depend only on the
            // spectrum's shape and cost a CPU integral over the whole lattice, so they move on the
            // narrow edge; H0 depends on everything (wind included) but is a single GPU dispatch, so it
            // moves on the wide one. Collapsing them would make a turning wind re-integrate the
            // spectrum every frame. Order matters: the layout defines the lattice the gains integrate.
            bool shapeChanged = !_hasLastSea || !sea.ShapeEquals(_lastSea);
            // THROTTLE the narrow edge: the shape rebuild is the resolution^2 * cascades CPU
            // integral (~65k transcendentals), and ShapeEquals is EXACT float equality, so an
            // inspector DRAG on any sea-shape slider used to re-integrate it on every mouse-move
            // frame. While inside the interval, hold the WHOLE previous sea (uniforms included) so
            // layout, gains and H0 can never desync; Dispatch keeps running, so the drag's final
            // value always lands within one interval of the mouse stopping. The wide edge (wind,
            // turbulence, choppiness - one GPU dispatch) is deliberately NOT throttled.
            SeaParams activeSea = sea;
            if (shapeChanged && _hasLastSea
                && Time.frameCount - _lastShapeRebuildFrame < MinShapeRebuildIntervalFrames)
            {
                activeSea = _lastSea;
                shapeChanged = false;
            }
            bool seaChanged = !_hasLastSea || !activeSea.Equals(_lastSea);
            bool foamChanged = !_hasLastFoam || !foam.Equals(_lastFoam);
            if (shapeChanged)
            {
                RebuildSpectrumInputs(activeSea);
                _lastShapeRebuildFrame = Time.frameCount;
            }
            SetSharedUniforms(activeSea);

            // H0 is static: rebuild only when a spectrum input actually changes.
            if (!_spectrumBuilt || seaChanged)
            {
                _cs.SetTexture(_kInit, ID_H0, _h0);
                _cs.Dispatch(_kInit, _groups, _groups, _cascades);
                _spectrumBuilt = true;
                _lastSea = activeSea;
                _hasLastSea = true;
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
            // A spectrum or foam-control change defines a new whitecap field. Reusing the previous
            // history would stamp each edited configuration over the last one, progressively widening
            // the expensive rendered foam coverage. Invalidating the read keeps both ping-pong targets
            // allocated: ComputeNormal overwrites the destination with the new state's settled prewarm,
            // and the following frame naturally reads that result.
            bool resetFoamHistory = seaChanged || foamChanged;
            float historyValid = _hasLastDispatchTime && !resetFoamHistory ? 1f : 0f;
            float foamDt = _hasLastDispatchTime ? Mathf.Clamp(waveTime - _lastDispatchTime, 0f, MaxFoamDeltaTime) : 0f;
            _lastDispatchTime = waveTime;
            _hasLastDispatchTime = true;
            _lastFoam = foam;
            _hasLastFoam = true;
            _cs.SetFloat(ID_FoamDeltaTime, foamDt);
            _cs.SetFloat(ID_FoamHistoryValid, historyValid);
            _cs.SetFloat(ID_FoamMinWind, foam.WindThreshold);
            _cs.SetFloat(ID_FoamCoverage, foam.Coverage);
            // Fold directionality rides with the coverage it recalibrates - see OceanCascadeTurbulence's
            // neighbour in the compute. 0 = the shipped determinant fold, bit-identical.
            _cs.SetFloat(ID_FoamAnisotropy, foam.CrestAnisotropy);
            // WHERE on the wave a cap is born, as opposed to WHETHER the surface folds there.
            _cs.SetFloat(ID_FoamCrestGate, foam.CrestGate);
            _cs.SetFloat(ID_FoamFaceBias, foam.FaceBias);
            _cs.SetFloat(ID_FoamCascadeMix, foam.CascadeMix);
            _cs.SetFloat(ID_FoamStrength, foam.Strength);
            _cs.SetFloat(ID_FoamFadeRate, foam.FadeRate);
            _cs.SetFloat(ID_FoamSlowFade, foam.SlowFadeFraction);
            _cs.SetFloat(ID_FoamDrift, foam.DriftFraction);
            _cs.SetFloat(ID_FoamMax, foam.Max);

            _cs.SetTexture(_kNormal, ID_Displacement, _displacement);
            _cs.SetTexture(_kNormal, ID_Normal, _normal);
            _cs.SetTexture(_kNormal, ID_FoamPrev, _foamHistA); // read last frame's accumulated foam
            _cs.SetTexture(_kNormal, ID_FoamNext, _foamHistB); // write this frame's accumulated foam
            _cs.Dispatch(_kNormal, _groups, _groups, _cascades);
            if (_normal.useMipMap) _normal.GenerateMips();
            (_foamHistA, _foamHistB) = (_foamHistB, _foamHistA); // ping-pong: this frame becomes next frame's prev

            // Bake the camera-centred height field for CPU buoyancy readback.
            _bakedCenter = cameraXZ;
            _bakedSize = HeightFieldSize;
            _bakedTime = waveTime;
            _cs.SetVector(ID_FieldCenter, new Vector4(cameraXZ.x, cameraXZ.y, 0f, 0f));
            _cs.SetFloat(ID_FieldSize, HeightFieldSize);
            _cs.SetInt(ID_FieldRes, HeightFieldRes);
            _cs.SetFloat(ID_FieldAmplitude, amplitude);
            _cs.SetTexture(_kBake, ID_Displacement, _displacement);
            _cs.SetTexture(_kBake, ID_HeightField, _heightField);
            _cs.SetTexture(_kBake, ID_OceanDirectionMap,
                aperiodic.DirectionMap ? aperiodic.DirectionMap : Texture2D.grayTexture);
            _cs.SetVector(ID_OceanAperiodicParams,
                new Vector4(aperiodic.Enabled ? 1f : 0f, aperiodic.TileScale, aperiodic.DirectionStrength, 0f));
            _cs.SetVector(ID_OceanDirectionMapFrame,
                new Vector4(aperiodic.MapCenter.x, aperiodic.MapCenter.y, 1f / aperiodic.MapSize, 0f));
            fetchField?.BindTo(_cs, _kBake);
            int bakeGroups = Mathf.CeilToInt(HeightFieldRes / (float)ThreadGroupSize);
            _cs.Dispatch(_kBake, bakeGroups, bakeGroups, 1);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Debug-view remap only - a per-frame kernel over all cascades, so it must never run
            // (nor its target exist) in release builds.
            _cs.SetFloat(ID_PreviewGain, PreviewGain);
            _cs.SetTexture(_kPreview, ID_Displacement, _displacement);
            _cs.SetTexture(_kPreview, ID_Normal, _normal); // preview overlays the accumulated foam (.w) as white
            _cs.SetTexture(_kPreview, ID_Preview, _preview);
            _cs.Dispatch(_kPreview, _groups, _groups, _cascades);
#endif

            // Cascade textures + layout are global (only the ocean body samples them); the per-body
            // _OceanFftActive flag (published in WaterUniformPublisher.WriteBodyProps) decides who does.
            Shader.SetGlobalTexture(ID_GlobalDisplacement, _displacement);
            Shader.SetGlobalTexture(ID_GlobalNormal, _normal);
            Shader.SetGlobalVector(ID_GlobalDomainSizes, _domainSizes);
            Shader.SetGlobalFloat(ID_GlobalCascadeCount, _cascades);
            Shader.SetGlobalVector(ID_GlobalVisibleAreas, _visibleAreas);
        }

        // Throttled by the shared channel (one request in flight, like WaterSurfaceSampler);
        // region centre stored BEFORE issue so the landed data is sampled against the centre it
        // was baked at (the camera moved since).
        internal void RequestHeightReadback()
        {
            if (!_ready || !_readback.CanRequest) return;
            if (Time.frameCount - _lastReadbackDemandFrame > ReadbackDemandWindowFrames) return;
            _pendingCenter = _bakedCenter;
            _pendingSize = _bakedSize;
            _pendingTime = _bakedTime;
            _readback.Request(_heightField, TextureFormat.RGBAFloat, _onHeightReadback);
        }

        // Successful landings only: the channel absorbs errors (and drops _heightReady via the
        // ctor's onGaveUp when the streak crosses its threshold).
        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            var data = req.GetData<Color>();
            RetireLandedFieldToPrevious();
            if (_heightCpu == null || _heightCpu.Length != data.Length) _heightCpu = new Color[data.Length];
            data.CopyTo(_heightCpu);
            _sampledCenter = _pendingCenter;
            _sampledSize = _pendingSize;
            _sampledTime = _pendingTime;
            _heightReady = true;
        }

        // Move the current landing into the "previous" slot before the new one overwrites it.
        // The buffers are SWAPPED rather than copied (the same ping-pong _foamHistA/_foamHistB uses):
        // a copy would be a full field memcpy per landing purely to keep a value we are about to
        // overwrite anyway. After the swap _heightCpu holds the older of the two arrays, which the
        // caller's length check then reuses or reallocates.
        void RetireLandedFieldToPrevious()
        {
            if (!_heightReady) return; // first landing: there is no previous field yet
            (_heightCpu, _heightCpuPrev) = (_heightCpuPrev, _heightCpu);
            _sampledCenterPrev = _sampledCenter;
            _sampledSizePrev = _sampledSize;
            _sampledTimePrev = _sampledTime;
            _hasPrevField = true;
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
            => TrySampleField(worldX, worldZ, out heightSlope, out _);

        /// <summary>As the three-argument overload, and additionally the surface's vertical rate
        /// d(height)/dt (m/s) MEASURED on this same field. <paramref name="verticalRate"/> is 0 until a
        /// second readback has landed, and wherever a rate cannot be measured (see VerticalRateAt).</summary>
        /// <remarks>
        /// The rate used to come from LargeWaveField.VerticalVelocityAtQuery - the ANALYTIC Gerstner
        /// mirror - while the height came from here. WaterLargeWaves.hlsl renders the two branches
        /// MUTUALLY EXCLUSIVELY, so on an FFT ocean that rate described a surface nothing draws, in a
        /// phase unrelated to this height. Buoyancy's surface-relative drag chases a velocity it can
        /// never arrive at (the height that would let it arrive is elsewhere), so the drag term stops
        /// being a damper and becomes a permanent energy source - it scaled with Swell Height and threw
        /// boats out of the water. Differencing two landings costs one extra buffer and one extra
        /// bilinear tap, and the answer describes the surface actually being floated on.
        /// </remarks>
        internal bool TrySampleField(float worldX, float worldZ, out Vector3 heightSlope,
                                     out float verticalRate)
        {
            StampReadbackDemand();
            heightSlope = Vector3.zero;
            verticalRate = 0f;
            if (!_heightReady || _heightCpu == null || _sampledSize <= 0f) return false;
            if (!TryFieldUV(_sampledCenter, _sampledSize, worldX, worldZ, out float u, out float v))
                return false;

            float texel = _sampledSize / HeightFieldRes; // metres per texel
            float du = 1f / HeightFieldRes;
            float h = SampleFieldBilinear(u, v).g;
            float slopeX = (SampleFieldBilinear(Mathf.Clamp01(u + du), v).g - SampleFieldBilinear(Mathf.Clamp01(u - du), v).g) / (2f * texel);
            float slopeZ = (SampleFieldBilinear(u, Mathf.Clamp01(v + du)).g - SampleFieldBilinear(u, Mathf.Clamp01(v - du)).g) / (2f * texel);
            heightSlope = new Vector3(h, slopeX, slopeZ);
            verticalRate = VerticalRateAt(worldX, worldZ, h);
            return true;
        }

        // d(height)/dt from the two most recent landings, each read against the centre and size IT was
        // baked at. Returns 0 - never a guess - when the rate cannot be measured: no second landing yet,
        // the point has left the previous region (camera moved, or a floater near the border), or a
        // non-positive dt (a paused body re-requesting, or a scrubbed wave clock).
        //
        // Sampling adequacy: the request interval tops out at WaterQuality.MaxUpdateInterval frames, so
        // dt stays well under the period of anything this field can carry - it is HeightFieldSize /
        // HeightFieldRes metres per texel, which band-limits it far below the readback rate.
        float VerticalRateAt(float worldX, float worldZ, float heightNow)
        {
            if (!_hasPrevField || _heightCpuPrev == null || _sampledSizePrev <= 0f) return 0f;
            float deltaTime = _sampledTime - _sampledTimePrev;
            if (deltaTime <= 0f) return 0f;
            if (!TryFieldUV(_sampledCenterPrev, _sampledSizePrev, worldX, worldZ,
                            out float u, out float v)) return 0f;
            return (heightNow - SampleFieldBilinearFrom(_heightCpuPrev, u, v).g) / deltaTime;
        }

        Color SampleFieldBilinear(float u, float v) => SampleFieldBilinearFrom(_heightCpu, u, v);

        static bool TryFieldUV(Vector2 center, float size, float worldX, float worldZ, out float u, out float v)
        {
            u = (worldX - center.x) / size + 0.5f;
            v = (worldZ - center.y) / size + 0.5f;
            return u >= 0f && u <= 1f && v >= 0f && v <= 1f;
        }

        // Shared filter (WaterFieldSampling) with exactly the clamp/half-texel semantics this
        // method used to inline; the wrapper just binds the fixed field resolution.
        static Color SampleFieldBilinearFrom(Color[] field, float u, float v)
            => WaterFieldSampling.SampleBilinear(field, HeightFieldRes, u, v);

        // Latest landed readback height at a world xz. ~1-2 frames stale (async readback); the fog gate
        // tolerates that because the fog waterline itself is per-pixel exact (live depth in
        // WaterUnderwaterFog) - the gate only arms the pass. Same FFT surface the shader renders, so no
        // analytic-vs-FFT mismatch. Fog-gate only; buoyancy keeps the plain TrySampleField.
        internal bool TrySampleHeightLatest(float worldX, float worldZ, out float height)
        {
            StampReadbackDemand();
            height = 0f;
            if (!_heightReady || _heightCpu == null || _sampledSize <= 0f) return false;
            if (!TryFieldUV(_sampledCenter, _sampledSize, worldX, worldZ, out float u, out float v)) return false;
            height = SampleFieldBilinearFrom(_heightCpu, u, v).g;
            return true;
        }

        // Ceiling (metres) on the dead-reckoning correction below. The measured rate is a
        // first-order fact about a surface whose curvature peaks at the crest turnover, so
        // a long extrapolation overshoots exactly where the flip timing matters most; one
        // metre covers the realistic age x rate product (a couple of frames x a heavy
        // sea's ~3-5 m/s) without letting a glitched landing throw the gate a swell away.
        const float HeightPredictClampMeters = 1f;

        /// <summary>As <see cref="TrySampleHeightLatest"/>, dead-reckoned to <paramref name="atTime"/>
        /// (the caller's wave clock): height + measured vertical rate x the landing's age, clamped
        /// to +-<see cref="HeightPredictClampMeters"/>. WHY: the landing is ~1-2 frames stale,
        /// which the fog PASS tolerates (its per-pixel waterline is live) but the camera SUBMERGE
        /// flip taken from this height does not - that flip drives screen-wide uniforms
        /// (_CameraUnderwater: the exclusion wall's reconstruction handoff, the foam overlay
        /// routing), so a mistimed flip pops the whole frame at the crossing. KWS ships the same
        /// correction as an authored knob (OceanWavesPredictionOffset); measuring the rate needs
        /// no knob. Identical to the un-predicted landing until a second landing has arrived
        /// (rate 0), and whenever the wave clock is paused or scrubbed (non-positive age).</summary>
        internal bool TrySampleHeightPredicted(float worldX, float worldZ, float atTime, out float height)
        {
            if (!TrySampleHeightLatest(worldX, worldZ, out height)) return false;
            float rate = VerticalRateAt(worldX, worldZ, height);
            float age = atTime - _sampledTime;
            if (rate == 0f || age <= 0f) return true;
            height += Mathf.Clamp(rate * age, -HeightPredictClampMeters, HeightPredictClampMeters);
            return true;
        }

        /// <summary>Latest landed horizontal Gerstner displacement (metres, world xz) at a world xz -
        /// the .xz lanes the bake carries beside the height. Same region/staleness caveats as
        /// <see cref="TrySampleHeightLatest"/>. Consumer: WaterVolume.InvertLargeWaveChopXZ, which
        /// fixed-point-inverts it so wake/ripple injections land where the DISPLACED surface will
        /// actually be drawn (deferred-improvement #1, now done for the injection path).</summary>
        internal bool TrySampleDisplacementLatest(float worldX, float worldZ, out Vector2 dispXZ)
        {
            StampReadbackDemand();
            dispXZ = Vector2.zero;
            if (!_heightReady || _heightCpu == null || _sampledSize <= 0f) return false;
            if (!TryFieldUV(_sampledCenter, _sampledSize, worldX, worldZ, out float u, out float v)) return false;
            Color c = SampleFieldBilinearFrom(_heightCpu, u, v);
            dispXZ = new Vector2(c.r, c.b);
            return true;
        }

        void StampReadbackDemand() => _lastReadbackDemandFrame = Time.frameCount;

        // Derive the cascade layout from the authored peak wavelength, then integrate the spectrum over
        // that exact lattice for the gains that make Significant Height and Swell Height read in metres.
        void RebuildSpectrumInputs(in SeaParams sea)
        {
            float[] bands = WaterOceanSpectrum.DeriveCascadeBands(sea.PeakWavelength, _cascades);
            _bandMax = Vector4.one;
            for (int i = 0; i < _cascades; i++) _bandMax[i] = bands[i];
            // Bands first, tiles derived: the band is the physics, the tile is only how finely the band is
            // sampled (see CascadeTileOversample). Each cascade owns the disjoint band (previous top, own
            // top], so summing cascades never double-counts a frequency.
            _bandMin = new Vector4(0f, _bandMax.x, _bandMax.y, _bandMax.z);
            _domainSizes = _bandMax * CascadeTileOversample;
            // Reach: how far each cascade stays drawn, as a multiple of the band-derived default.
            //
            // The multiple ALONE is not enough, and this is the trap. It reproduces the ratio the
            // shipped arrays had (4800 / 600 = 8) - but those arrays sat on a FIXED 600 m top band,
            // while the derived top band is two peak wavelengths. On a 60 m sea that is 120 m, so the
            // same ratio cut the ocean's drawn reach from 4800 m to 960 m and the far field went flat.
            // The band-relative rule is still right; it just needed the free multiplier the fixed
            // arrays were quietly carrying.
            _visibleAreas = _bandMax * (WaterOceanSpectrum.VisibleAreaBandMultiple
                                        * Mathf.Max(sea.CascadeReach, 0f));

            var layout = new WaterOceanSpectrum.Layout(_resolution, _cascades, _domainSizes, _bandMin, _bandMax);
            var state = new WaterOceanSpectrum.SeaState(
                sea.SignificantHeight, WaterOceanSpectrum.PeakAngularFrequency(sea.PeakWavelength),
                sea.PeakSharpness, sea.SeaDepth, sea.SwellHeight, sea.SwellWavelength);
            WaterOceanSpectrum.ComputeGains(layout, state, out _windSeaGain, out _swellGain);
        }

        static Vector2 WindDirection(float headingRad) => new Vector2(Mathf.Cos(headingRad), Mathf.Sin(headingRad));

        void SetSharedUniforms(in SeaParams sea)
        {
            _cs.SetInt(ID_Resolution, _resolution);
            _cs.SetInt(ID_Cascades, _cascades);
            _cs.SetInt(ID_Seed, SpectrumSeed);
            _cs.SetVector(ID_DomainSizes, _domainSizes);
            _cs.SetVector(ID_BandMin, _bandMin);
            _cs.SetVector(ID_BandMax, _bandMax);
            // The buoyancy bake fades cascades with distance exactly like the render does, so a floater
            // never rides a wave the surface has already faded out.
            _cs.SetVector(ID_VisibleAreas, _visibleAreas);
            Vector2 windDir = WindDirection(sea.WindHeadingRad);
            _cs.SetVector(ID_WindDir, new Vector4(windDir.x, windDir.y, 0f, 0f));
            Vector2 swellDir = WindDirection(sea.SwellHeadingRad);
            _cs.SetVector(ID_SwellDir, new Vector4(swellDir.x, swellDir.y, 0f, 0f));
            _cs.SetFloat(ID_WindSpeed, Mathf.Max(0f, sea.WindSpeed));
            _cs.SetFloat(ID_WindTurbulence, Mathf.Clamp01(sea.WindTurbulence));
            _cs.SetFloat(ID_PeakAngularFreq, WaterOceanSpectrum.PeakAngularFrequency(sea.PeakWavelength));
            _cs.SetFloat(ID_PeakSharpness, Mathf.Max(1f, sea.PeakSharpness));
            _cs.SetFloat(ID_SeaDepth, Mathf.Max(0f, sea.SeaDepth));
            _cs.SetFloat(ID_SpectrumGain, _windSeaGain);
            _cs.SetFloat(ID_SwellGain, _swellGain);
            _cs.SetFloat(ID_Choppiness, Mathf.Max(0f, sea.Choppiness));
            _cs.SetFloat(ID_SwellWavelength, Mathf.Max(1e-3f, sea.SwellWavelength));
            _cs.SetFloat(ID_SwellHeight, Mathf.Max(0f, sea.SwellHeight));
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
            Release(ref _foamHistA);
            Release(ref _foamHistB);
            _hasLastDispatchTime = false;
            _heightReady = false;
            _heightCpu = null;
            _heightCpuPrev = null;
            _hasPrevField = false;
            if (_butterfly != null)
            {
                WaterObjects.DestroyRuntime(_butterfly);
                _butterfly = null;
            }
            _ready = false;
            _spectrumBuilt = false;
            _hasLastSea = false;
            _hasLastFoam = false;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            WaterObjects.DestroyRuntime(rt);
            rt = null;
        }
    }
}
