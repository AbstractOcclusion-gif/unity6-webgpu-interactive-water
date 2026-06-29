// WebGL Water - GPU heightfield simulation driver (Unity 6 / URP port)
// Owns two RGBAFloat ping-pong RenderTextures and dispatches the compute kernels.
// Port of water.js by Evan Wallace (MIT).
using UnityEngine;

namespace WebGLWater
{
    public class WaterSimulation
    {
        public const int Resolution = 256;

        readonly ComputeShader _cs;
        readonly int _kDrop, _kUpdate, _kNormal, _kSphere, _kConserve;
        readonly int _groups;

        RenderTexture _a; // current state (height, velocity, normal.x, normal.z)
        RenderTexture _b; // scratch

        /// <summary>The texture holding the current simulation state.</summary>
        public RenderTexture Texture => _a;

        public WaterSimulation(ComputeShader cs)
        {
            _cs = cs;
            _kDrop   = cs.FindKernel("Drop");
            _kUpdate = cs.FindKernel("Update");
            _kNormal = cs.FindKernel("Normal");
            _kSphere = cs.FindKernel("Sphere");
            _kConserve = cs.FindKernel("Conserve");
            _groups = Resolution / 8;

            _a = Create();
            _b = Create();
            Clear(_a);
            Clear(_b);
        }

        static RenderTexture Create()
        {
            var rt = new RenderTexture(Resolution, Resolution, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = "WaterSimState"
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
        }

        void Dispatch(int kernel)
        {
            _cs.SetFloat("_Size", Resolution);
            _cs.SetVector("_Delta", new Vector4(1f / Resolution, 1f / Resolution, 0, 0));
            _cs.SetTexture(kernel, "Src", _a);
            _cs.SetTexture(kernel, "Dst", _b);
            _cs.Dispatch(kernel, _groups, _groups, 1);
            (_a, _b) = (_b, _a); // ping-pong: _a is always the latest state
        }

        public void AddDrop(float x, float y, float radius, float strength)
        {
            _cs.SetVector("_Center", new Vector4(x, y, 0, 0));
            _cs.SetFloat("_Radius", radius);
            _cs.SetFloat("_Strength", strength);
            Dispatch(_kDrop);
        }

        public void MoveSphere(Vector3 oldCenter, Vector3 newCenter, float radius)
        {
            _cs.SetVector("_OldCenter", oldCenter);
            _cs.SetVector("_NewCenter", newCenter);
            _cs.SetFloat("_SphereRadius", radius);
            Dispatch(_kSphere);
        }

        public void StepSimulation(float waveSpeed = 2f, float damping = 0.995f)
        {
            _cs.SetFloat("_WaveSpeed", waveSpeed);
            _cs.SetFloat("_Damping", damping);
            Dispatch(_kUpdate);
        }

        public void UpdateNormals() => Dispatch(_kNormal);

        /// <summary>Subtracts the mean height (from heightMip's top mip) to conserve volume.</summary>
        public void ConserveVolume(RenderTexture heightMip)
        {
            _cs.SetTexture(_kConserve, "HeightMip", heightMip);
            Dispatch(_kConserve);
        }
    }
}
