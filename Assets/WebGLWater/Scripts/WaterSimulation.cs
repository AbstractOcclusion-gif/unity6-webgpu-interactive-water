// WebGL Water - GPU heightfield simulation driver (Unity 6 / URP port)
// Owns two RGBAFloat ping-pong RenderTextures and dispatches the compute kernels.
// Port of water.js by Evan Wallace (MIT).
using UnityEngine;

namespace WebGLWater
{
    public class WaterSimulation
    {
        // The compute shader dispatches in 8x8 thread groups, so the grid must be a positive
        // multiple of this. Must match [numthreads(...)] in WaterSim.compute.
        public const int ThreadGroupSize = 8;

        // Compute kernel names (must match WaterSim.compute).
        const string KernelDrop = "Drop";
        const string KernelUpdate = "Update";
        const string KernelNormal = "Normal";
        const string KernelObstacle = "Obstacle";
        const string KernelFoam = "Foam";
        const string KernelConserve = "Conserve";

        // Compute property ids, cached once instead of re-hashing strings every dispatch.
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_Delta = Shader.PropertyToID("_Delta");
        static readonly int ID_Src = Shader.PropertyToID("Src");
        static readonly int ID_Dst = Shader.PropertyToID("Dst");
        static readonly int ID_Center = Shader.PropertyToID("_Center");
        static readonly int ID_Radius = Shader.PropertyToID("_Radius");
        static readonly int ID_Strength = Shader.PropertyToID("_Strength");
        static readonly int ID_ObstaclePrev = Shader.PropertyToID("ObstaclePrev");
        static readonly int ID_ObstacleCurr = Shader.PropertyToID("ObstacleCurr");
        static readonly int ID_ObstacleStrength = Shader.PropertyToID("_ObstacleStrength");
        static readonly int ID_ObstacleFlipY = Shader.PropertyToID("_ObstacleFlipY");
        static readonly int ID_WaveSpeed = Shader.PropertyToID("_WaveSpeed");
        static readonly int ID_Damping = Shader.PropertyToID("_Damping");
        static readonly int ID_FoamGenRate = Shader.PropertyToID("_FoamGenRate");
        static readonly int ID_FoamDecay = Shader.PropertyToID("_FoamDecay");
        static readonly int ID_FoamSpread = Shader.PropertyToID("_FoamSpread");
        static readonly int ID_FoamFromSpeed = Shader.PropertyToID("_FoamFromSpeed");
        static readonly int ID_FoamFromCurv = Shader.PropertyToID("_FoamFromCurv");
        static readonly int ID_FoamAdvect = Shader.PropertyToID("_FoamAdvect");
        static readonly int ID_FoamSrc = Shader.PropertyToID("FoamSrc");
        static readonly int ID_FoamDst = Shader.PropertyToID("FoamDst");
        static readonly int ID_HeightMip = Shader.PropertyToID("HeightMip");

        /// <summary>Grid resolution of the heightfield RTs (per side). Set per quality tier.</summary>
        public int Resolution { get; }

        readonly ComputeShader _cs;
        readonly int _kDrop, _kUpdate, _kNormal, _kObstacle, _kFoam, _kConserve;
        readonly int _groups;
        readonly Vector4 _delta; // (1/Resolution, 1/Resolution, 0, 0), precomputed once

        RenderTexture _a; // current state (height, velocity, normal.x, normal.z)
        RenderTexture _b; // scratch
        RenderTexture _foamA, _foamB; // foam amount ping-pong (R)

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
            _kConserve = cs.FindKernel(KernelConserve);
            _groups = Resolution / ThreadGroupSize;

            _a = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _b = Create(RenderTextureFormat.ARGBFloat, "WaterSimState");
            _foamA = Create(RenderTextureFormat.RFloat, "WaterFoam");
            _foamB = Create(RenderTextureFormat.RFloat, "WaterFoam");
            Clear(_a); Clear(_b); Clear(_foamA); Clear(_foamB);
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
                name = name
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
            if (_a != null) _a.Release();
            if (_b != null) _b.Release();
            if (_foamA != null) _foamA.Release();
            if (_foamB != null) _foamB.Release();
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

        public void AddDrop(float x, float y, float radius, float strength)
        {
            _cs.SetVector(ID_Center, new Vector4(x, y, 0, 0));
            _cs.SetFloat(ID_Radius, radius);
            _cs.SetFloat(ID_Strength, strength);
            Dispatch(_kDrop);
        }

        /// <summary>Forces the surface by the change in submerged footprint
        /// (prev - curr), generalising the old sphere displacement to any meshes.</summary>
        public void ApplyObstacle(Texture prev, Texture curr, float strength, bool flipY)
        {
            _cs.SetTexture(_kObstacle, ID_ObstaclePrev, prev);
            _cs.SetTexture(_kObstacle, ID_ObstacleCurr, curr);
            _cs.SetFloat(ID_ObstacleStrength, strength);
            _cs.SetFloat(ID_ObstacleFlipY, flipY ? 1f : 0f);
            Dispatch(_kObstacle);
        }

        public void StepSimulation(float waveSpeed, float damping)
        {
            _cs.SetFloat(ID_WaveSpeed, waveSpeed);
            _cs.SetFloat(ID_Damping, damping);
            Dispatch(_kUpdate);
        }

        public void UpdateNormals() => Dispatch(_kNormal);

        /// <summary>Advance the foam buffer: advect along the surface flow, diffuse,
        /// generate from turbulence, decay. Reads the current height/normal state;
        /// ping-pongs the foam textures.</summary>
        public void StepFoam(float genRate, float decay, float spread, float fromSpeed, float fromCurv, float advect)
        {
            SetGridUniforms();
            _cs.SetFloat(ID_FoamGenRate, genRate);
            _cs.SetFloat(ID_FoamDecay, decay);
            _cs.SetFloat(ID_FoamSpread, spread);
            _cs.SetFloat(ID_FoamFromSpeed, fromSpeed);
            _cs.SetFloat(ID_FoamFromCurv, fromCurv);
            _cs.SetFloat(ID_FoamAdvect, advect);
            _cs.SetTexture(_kFoam, ID_Src, _a);        // height state (read)
            _cs.SetTexture(_kFoam, ID_FoamSrc, _foamA);
            _cs.SetTexture(_kFoam, ID_FoamDst, _foamB);
            _cs.Dispatch(_kFoam, _groups, _groups, 1);
            (_foamA, _foamB) = (_foamB, _foamA);
        }

        /// <summary>Subtracts the mean height (from heightMip's top mip) to conserve volume.</summary>
        public void ConserveVolume(RenderTexture heightMip)
        {
            _cs.SetTexture(_kConserve, ID_HeightMip, heightMip);
            Dispatch(_kConserve);
        }
    }
}
