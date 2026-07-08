// WebGL Water - GPU heightfield simulation driver (Unity 6 / URP port)
// Owns two RGBAFloat ping-pong RenderTextures and dispatches the compute kernels.
// Port of water.js by Evan Wallace (MIT).
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterSimulation
    {
        // The compute shader dispatches in 8x8 thread groups, so the grid must be a positive
        // multiple of this. Must match [numthreads(...)] in WaterSim.compute.
        public const int ThreadGroupSize = 8;

        // Interactive ripples are authored in WORLD radius, converted to a grid fraction by the caller.
        // On a large plane that fraction can fall below one texel and inject an aliased spike, so floor
        // it to a few texels: every drop stays a smooth bump regardless of body size. _Radius is a
        // fraction of the grid side, so N texels correspond to N / Resolution.
        const float MinDropTexelRadius = 2.5f;

        // Compute kernel names (must match WaterSim.compute).
        const string KernelDrop = "Drop";
        const string KernelUpdate = "Update";
        const string KernelNormal = "Normal";
        const string KernelObstacle = "Obstacle";
        const string KernelFoam = "Foam";
        const string KernelReduceMean = "ReduceMean";
        const string KernelReduceMeanFinal = "ReduceMeanFinal";
        const string KernelConserve = "Conserve";
        const string KernelScroll = "Scroll";
        const string KernelScrollFoam = "ScrollFoam";

        // Compute property ids, cached once instead of re-hashing strings every dispatch.
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_Delta = Shader.PropertyToID("_Delta");
        static readonly int ID_Src = Shader.PropertyToID("Src");
        static readonly int ID_Dst = Shader.PropertyToID("Dst");
        static readonly int ID_Center = Shader.PropertyToID("_Center");
        static readonly int ID_Radius = Shader.PropertyToID("_Radius");
        static readonly int ID_Strength = Shader.PropertyToID("_Strength");
        static readonly int ID_DropAxisScale = Shader.PropertyToID("_DropAxisScale");
        static readonly int ID_WaveAxisWeight = Shader.PropertyToID("_WaveAxisWeight");
        static readonly int ID_ObstaclePrev = Shader.PropertyToID("ObstaclePrev");
        static readonly int ID_ObstacleCurr = Shader.PropertyToID("ObstacleCurr");
        static readonly int ID_ObstacleStrength = Shader.PropertyToID("_ObstacleStrength");
        static readonly int ID_ObstacleFlipY = Shader.PropertyToID("_ObstacleFlipY");
        static readonly int ID_ObstacleDeadband = Shader.PropertyToID("_ObstacleDeadband");
        static readonly int ID_WaveSpeed = Shader.PropertyToID("_WaveSpeed");
        static readonly int ID_Damping = Shader.PropertyToID("_Damping");
        static readonly int ID_FoamGenRate = Shader.PropertyToID("_FoamGenRate");
        static readonly int ID_FoamDecayFresh = Shader.PropertyToID("_FoamDecayFresh");
        static readonly int ID_FoamDecayResidual = Shader.PropertyToID("_FoamDecayResidual");
        static readonly int ID_FoamDtSteps = Shader.PropertyToID("_FoamDtSteps");
        static readonly int ID_FoamDecayRate = Shader.PropertyToID("_FoamDecayRate");
        static readonly int ID_FoamSpread = Shader.PropertyToID("_FoamSpread");
        static readonly int ID_FoamFromSpeed = Shader.PropertyToID("_FoamFromSpeed");
        static readonly int ID_FoamFromCurv = Shader.PropertyToID("_FoamFromCurv");
        static readonly int ID_FoamAdvect = Shader.PropertyToID("_FoamAdvect");
        static readonly int ID_FoamSrc = Shader.PropertyToID("FoamSrc");
        static readonly int ID_FoamDst = Shader.PropertyToID("FoamDst");
        static readonly int ID_PartialSums = Shader.PropertyToID("PartialSums");
        static readonly int ID_MeanResult = Shader.PropertyToID("MeanResult");
        static readonly int ID_MeanCorrectionMax = Shader.PropertyToID("_MeanCorrectionMax");
        static readonly int ID_ScrollOffset = Shader.PropertyToID("_ScrollOffset");
        static readonly int ID_BedTex = Shader.PropertyToID("_BedTex");
        static readonly int ID_UseBedDepth = Shader.PropertyToID("_UseBedDepth");
        static readonly int ID_ShoalDepthPool = Shader.PropertyToID("_ShoalDepthPool");
        static readonly int ID_ShoreFoamDepthPool = Shader.PropertyToID("_ShoreFoamDepthPool");
        static readonly int ID_ShoreFoamStrength = Shader.PropertyToID("_ShoreFoamStrength");
        static readonly int ID_BreakFoamStrength = Shader.PropertyToID("_BreakFoamStrength");

        /// <summary>Grid resolution of the heightfield RTs (per side). Set per quality tier.</summary>
        public int Resolution { get; }

        readonly ComputeShader _cs;
        readonly int _kDrop, _kUpdate, _kNormal, _kObstacle, _kFoam, _kConserve, _kScroll, _kScrollFoam;
        readonly int _kReduceMean, _kReduceMeanFinal;
        readonly int _groups;
        readonly Vector4 _delta; // (1/Resolution, 1/Resolution, 0, 0), precomputed once

        // Per-axis anisotropy so ripples stay round in WORLD on a rectangular (non-square) pool.
        // Defaults are the isotropic square case, so a body that never calls SetAnisotropy is
        // identical to before. (0.25,0.25) reproduces the old 4-neighbour average Laplacian.
        Vector4 _waveAxisWeight = new Vector4(0.25f, 0.25f, 0f, 0f);
        Vector4 _dropAxisScale = new Vector4(1f, 1f, 0f, 0f);

        // Bed-depth coupling (Phase 1 shoreline). Inactive by default so a body without a baked bed
        // behaves exactly as a bottomless pool. Bound onto the Update and Foam kernels each frame.
        Texture _bedTex;
        float _useBedDepth;         // 1 = active
        float _shoalDepthPool;      // pool-unit depth below which ripples shoal
        float _shoreFoamDepthPool;  // pool-unit depth band for standing shore foam
        float _shoreFoamStrength;
        float _breakFoamStrength;   // dynamic breaking-foam strength (Phase 2a)

        RenderTexture _a; // current state (height, velocity, normal.x, normal.z)
        RenderTexture _b; // scratch
        RenderTexture _foamA, _foamB; // foam amount ping-pong (R)
        // Exact mean-height reduction for Conserve (see the WaterSim.compute rationale:
        // the old float-mip mean silently point-sampled in WebGPU builds).
        GraphicsBuffer _partialSums; // one float per 8x8 thread group
        GraphicsBuffer _meanResult;  // single float: the exact mean

        /// <summary>The texture holding the current simulation state.</summary>
        public RenderTexture Texture => _a;

        /// <summary>The current foam amount texture (R channel).</summary>
        public RenderTexture FoamTexture => _foamA;

        public WaterSimulation(ComputeShader cs, int resolution)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            if (resolution < ThreadGroupSize || resolution % ThreadGroupSize != 0)
                throw new System.ArgumentException(
                    $"WaterSimulation resolution must be a positive multiple of {ThreadGroupSize}, got {resolution}.",
                    nameof(resolution));

            Resolution = resolution;
            _delta = new Vector4(1f / Resolution, 1f / Resolution, 0f, 0f);
            _cs = cs;
            _kDrop = cs.FindKernel(KernelDrop);
            _kUpdate = cs.FindKernel(KernelUpdate);
            _kNormal = cs.FindKernel(KernelNormal);
            _kObstacle = cs.FindKernel(KernelObstacle);
            _kFoam = cs.FindKernel(KernelFoam);
            _kReduceMean = cs.FindKernel(KernelReduceMean);
            _kReduceMeanFinal = cs.FindKernel(KernelReduceMeanFinal);
            _kConserve = cs.FindKernel(KernelConserve);
            _kScroll = cs.FindKernel(KernelScroll);
            _kScrollFoam = cs.FindKernel(KernelScrollFoam);
            _groups = Resolution / ThreadGroupSize;

            _a = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _b = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _foamA = Create(RenderTextureFormat.RFloat, "WaterFoam");
            _foamB = Create(RenderTextureFormat.RFloat, "WaterFoam");
            Clear(_a); Clear(_b); Clear(_foamA); Clear(_foamB);

            _partialSums = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _groups * _groups, sizeof(float));
            _meanResult = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            _meanResult.SetData(new float[1]); // mean = 0 until the first reduction
        }

        RenderTexture Create(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(Resolution, Resolution, 0, format)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = name,
                hideFlags = HideFlags.HideAndDontSave // never serialized by an edit-mode preview
            };
            rt.Create();
            return rt;
        }

        static void Clear(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        public void Dispose()
        {
            ReleaseAndDestroy(ref _a);
            ReleaseAndDestroy(ref _b);
            ReleaseAndDestroy(ref _foamA);
            ReleaseAndDestroy(ref _foamB);
            _partialSums?.Dispose(); _partialSums = null;
            _meanResult?.Dispose(); _meanResult = null;
        }

        // Release frees the GPU surface immediately; Destroy frees the wrapper object, which
        // otherwise accumulates across enable/disable cycles until scene unload.
        static void ReleaseAndDestroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            if (Application.isPlaying) Object.Destroy(rt); else Object.DestroyImmediate(rt);
            rt = null;
        }

        // Grid size + texel step, shared by every kernel dispatch.
        void SetGridUniforms()
        {
            _cs.SetFloat(ID_Size, Resolution);
            _cs.SetVector(ID_Delta, _delta);
        }

        void Dispatch(int kernel)
        {
            SetGridUniforms();
            _cs.SetTexture(kernel, ID_Src, _a);
            _cs.SetTexture(kernel, ID_Dst, _b);
            _cs.Dispatch(kernel, _groups, _groups, 1);
            (_a, _b) = (_b, _a); // ping-pong: _a is always the latest state
        }

        /// <summary>Set the per-axis anisotropy for a rectangular pool so ripples read ROUND in
        /// world. <paramref name="laplacianWeight"/> weights the wave-propagation neighbours per
        /// axis (default 0.25,0.25 = isotropic square); <paramref name="dropScale"/> squashes the
        /// drop stamp per axis (default 1,1). Computed by WaterVolume from the body's extent;
        /// windowed bodies pass the defaults (their sim window is already square in world).</summary>
        public void SetAnisotropy(Vector2 laplacianWeight, Vector2 dropScale)
        {
            _waveAxisWeight = new Vector4(laplacianWeight.x, laplacianWeight.y, 0f, 0f);
            _dropAxisScale = new Vector4(dropScale.x, dropScale.y, 0f, 0f);
        }

        /// <summary>Bed-depth shoreline coupling (Phase 1): the pool-space bed-height map plus the
        /// shoal and shore-foam depths (pool units). With <paramref name="enabled"/> false (or a null
        /// map) the sim runs as a bottomless pool, unchanged. Bound on the Update + Foam kernels.</summary>
        public void SetBedDepth(Texture bed, bool enabled, float shoalDepthPool, float shoreFoamDepthPool,
                                float shoreFoamStrength, float breakFoamStrength)
        {
            _bedTex = bed;
            _useBedDepth = (enabled && bed != null) ? 1f : 0f;
            _shoalDepthPool = shoalDepthPool;
            _shoreFoamDepthPool = shoreFoamDepthPool;
            _shoreFoamStrength = shoreFoamStrength;
            _breakFoamStrength = breakFoamStrength;
        }

        // Bind the bed map + shoreline uniforms onto a kernel. A texture is always bound (black when
        // inactive) so the backend never sees an unbound sampler; the shader early-outs on _UseBedDepth.
        void BindBed(int kernel)
        {
            _cs.SetFloat(ID_UseBedDepth, _useBedDepth);
            _cs.SetFloat(ID_ShoalDepthPool, _shoalDepthPool);
            _cs.SetFloat(ID_ShoreFoamDepthPool, _shoreFoamDepthPool);
            _cs.SetFloat(ID_ShoreFoamStrength, _shoreFoamStrength);
            _cs.SetFloat(ID_BreakFoamStrength, _breakFoamStrength);
            _cs.SetTexture(kernel, ID_BedTex, _bedTex != null ? _bedTex : Texture2D.blackTexture);
        }

        public void AddDrop(float x, float y, float radius, float strength)
        {
            radius = Mathf.Max(radius, MinDropTexelRadius / Resolution);
            _cs.SetVector(ID_Center, new Vector4(x, y, 0, 0));
            _cs.SetFloat(ID_Radius, radius);
            _cs.SetFloat(ID_Strength, strength);
            _cs.SetVector(ID_DropAxisScale, _dropAxisScale);
            Dispatch(_kDrop);
        }

        /// <summary>Forces the surface by the change in submerged footprint
        /// (prev - curr), generalising the old sphere displacement to any meshes.</summary>
        public void ApplyObstacle(Texture prev, Texture curr, float strength, bool flipY, float deadband)
        {
            _cs.SetTexture(_kObstacle, ID_ObstaclePrev, prev);
            _cs.SetTexture(_kObstacle, ID_ObstacleCurr, curr);
            _cs.SetFloat(ID_ObstacleStrength, strength);
            _cs.SetFloat(ID_ObstacleFlipY, flipY ? 1f : 0f);
            _cs.SetFloat(ID_ObstacleDeadband, deadband);
            Dispatch(_kObstacle);
        }

        public void StepSimulation(float waveSpeed, float damping)
        {
            _cs.SetFloat(ID_WaveSpeed, waveSpeed);
            _cs.SetFloat(ID_Damping, damping);
            _cs.SetVector(ID_WaveAxisWeight, _waveAxisWeight);
            BindBed(_kUpdate);
            Dispatch(_kUpdate);
        }

        public void UpdateNormals() => Dispatch(_kNormal);

        /// <summary>Advance the foam buffer: advect along the surface flow, diffuse,
        /// generate from turbulence, decay. Decay is bi-exponential: thick fresh foam
        /// survives at <paramref name="decayFresh"/> per reference step, thin residual
        /// lace at the (slower, closer to 1) <paramref name="decayResidual"/>. Generation
        /// and decay scale by <paramref name="dtSteps"/> (elapsed time in reference steps,
        /// 1 = 1/60 s) so foam evolves frame-rate independently; <paramref name="decayRate"/>
        /// is a user time-scale on decay only (1 = authored speed, 2 = twice as fast).
        /// Reads the current height/normal state; ping-pongs the foam textures.</summary>
        public void StepFoam(float genRate, float decayFresh, float decayResidual,
                             float spread, float fromSpeed, float fromCurv, float advect,
                             float dtSteps, float decayRate)
        {
            SetGridUniforms();
            _cs.SetFloat(ID_FoamGenRate, genRate);
            _cs.SetFloat(ID_FoamDecayFresh, decayFresh);
            _cs.SetFloat(ID_FoamDecayResidual, decayResidual);
            _cs.SetFloat(ID_FoamDtSteps, dtSteps);
            _cs.SetFloat(ID_FoamDecayRate, decayRate);
            _cs.SetFloat(ID_FoamSpread, spread);
            _cs.SetFloat(ID_FoamFromSpeed, fromSpeed);
            _cs.SetFloat(ID_FoamFromCurv, fromCurv);
            _cs.SetFloat(ID_FoamAdvect, advect);
            _cs.SetTexture(_kFoam, ID_Src, _a);        // height state (read)
            _cs.SetTexture(_kFoam, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kFoam, ID_FoamDst, _foamB);
            BindBed(_kFoam);
            _cs.Dispatch(_kFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);
        }

        /// <summary>Subtracts the mean height to conserve volume. The mean is computed EXACTLY
        /// by a two-pass compute reduction (the old Blit + GenerateMips top-mip read silently
        /// point-sampled in WebGPU builds - float32 isn't filterable there - making the "mean"
        /// one arbitrary texel and popping the whole plane). The subtracted mean stays clamped
        /// to +/- <paramref name="maxCorrection"/> (pool units) as a pure safety bound.</summary>
        public void ConserveVolume(float maxCorrection)
        {
            SetGridUniforms();
            _cs.SetFloat(ID_MeanCorrectionMax, maxCorrection);

            _cs.SetTexture(_kReduceMean, ID_Src, _a);
            _cs.SetBuffer(_kReduceMean, ID_PartialSums, _partialSums);
            _cs.Dispatch(_kReduceMean, _groups, _groups, 1);

            _cs.SetBuffer(_kReduceMeanFinal, ID_PartialSums, _partialSums);
            _cs.SetBuffer(_kReduceMeanFinal, ID_MeanResult, _meanResult);
            _cs.Dispatch(_kReduceMeanFinal, 1, 1, 1);

            _cs.SetBuffer(_kConserve, ID_MeanResult, _meanResult);
            Dispatch(_kConserve);
        }

        /// <summary>
        /// Shift the whole sim state (height/velocity/normal and foam) by an integer
        /// texel offset so ripples stay world-anchored while a windowed body's sim
        /// follows the camera. The offset is the raw kernel shift: <c>Dst[p] = Src[p - offset]</c>,
        /// so cells exposed at the trailing edge reset to rest. The caller (WaterVolume)
        /// computes the grid-space offset from the window-centre movement. No-op at (0,0).
        /// </summary>
        public void Scroll(int offsetX, int offsetY)
        {
            if (offsetX == 0 && offsetY == 0) return;

            SetGridUniforms();
            _cs.SetInts(ID_ScrollOffset, offsetX, offsetY);

            _cs.SetTexture(_kScroll, ID_Src, _a);
            _cs.SetTexture(_kScroll, ID_Dst, _b);
            _cs.Dispatch(_kScroll, _groups, _groups, 1);
            (_a, _b) = (_b, _a);

            _cs.SetTexture(_kScrollFoam, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kScrollFoam, ID_FoamDst, _foamB);
            _cs.Dispatch(_kScrollFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);
        }
    }
}
