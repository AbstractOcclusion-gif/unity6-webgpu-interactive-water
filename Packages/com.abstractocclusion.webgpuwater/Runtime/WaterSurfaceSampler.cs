// WebGpuWater - CPU-side surface sampling for buoyancy and surface queries.
// Extracted from WaterVolume: owns the async height readback (single in-flight request,
// reused CPU buffer, error-streak fallback) and the bilinear CPU sample of the ripple
// field, composited with the analytic wind waves. Created per enable; the volume's
// public TryGet* facade delegates here.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSurfaceSampler
    {
        // Give up on async readback after this many consecutive errored requests and fall
        // back to the analytic waterline (same path as backends without readback support).
        const int MaxConsecutiveReadbackErrors = 8;

        readonly WaterVolume _body;
        readonly System.Action<AsyncGPUReadbackRequest> _onHeightReadback; // cached: a per-request method group would allocate every frame

        // CPU copy of the height field for buoyancy queries
        Color[] _heightCpu;
        bool _heightReady, _readbackInFlight;
        int _readbackErrorStreak;
        // True on backends without AsyncGPUReadback (e.g. WebGPU) or after persistent readback
        // errors: buoyancy and surface queries fall back to the analytic waterline (flat rest
        // + wind waves) so objects still float.
        bool _analyticFallback;

        internal WaterSurfaceSampler(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
            _onHeightReadback = OnHeightReadback;
            _analyticFallback = !SystemInfo.supportsAsyncGPUReadback;
        }

        internal void RequestReadback()
        {
            if (_readbackInFlight || _body.Simulation == null) return;
            // Covers both "unsupported" (probed in the ctor) and "errored out" (set below);
            // TrySamplePoolSurface serves queries from the analytic waterline either way.
            if (_analyticFallback) return;
            _readbackInFlight = true;
            AsyncGPUReadback.Request(_body.Simulation.Texture, 0, TextureFormat.RGBAFloat, _onHeightReadback);
        }

        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            _readbackInFlight = false;
            if (req.hasError)
            {
                // Persistent errors (e.g. a backend that can't convert the format) would
                // otherwise retry silently forever with buoyancy never activating.
                if (++_readbackErrorStreak >= MaxConsecutiveReadbackErrors && !_analyticFallback)
                {
                    _analyticFallback = true;
                    _heightReady = false; // don't keep floating objects on a stale field
                    Debug.LogWarning($"WaterVolume: height readback failed {MaxConsecutiveReadbackErrors} " +
                                     "times in a row; falling back to the analytic waterline for buoyancy.", _body);
                }
                return;
            }
            _readbackErrorStreak = 0;
            var data = req.GetData<Color>();
            if (_heightCpu == null || _heightCpu.Length != data.Length)
                _heightCpu = new Color[data.Length];
            data.CopyTo(_heightCpu);
            _heightReady = true;
        }

        // Pool-space surface height + flow (normal.xz) at a world point (pool xz in [-1,1]).
        // Uses the GPU readback ripple field when available; on backends without AsyncGPUReadback
        // it falls back to the analytic surface (flat rest + wind waves) so buoyancy and surface
        // queries keep working (interactive ripples / obstacle displacement are simply absent there).
        // Returns false only when readback is supported but hasn't landed yet (first frames).
        internal bool TrySamplePoolSurface(Vector3 world, float poolX, float poolZ,
                                           out float surfaceH, out Vector2 poolFlow)
        {
            surfaceH = 0f;
            poolFlow = Vector2.zero;

            bool haveReadback = _heightReady && _heightCpu != null;
            if (haveReadback)
            {
                Color sample = SampleRipple(world, poolX, poolZ);
                surfaceH = sample.r;
                poolFlow = new Vector2(sample.b, sample.a); // (normal.x, normal.z)
            }
            else if (!_analyticFallback)
            {
                return false; // readback supported but not ready yet
            }
            // else: analytic fallback -> rest surface (0) + wind waves added below

            // Small wind-wave detail. Open water keeps this layer AND adds the big swell in world
            // space (in the WaterVolume callers), so both wind-wave scales are present.
            if (_body.WindWaves)
            {
                // Oceans sample the wind-wave layer in WORLD metres (extent-independent) to match the
                // shader's WindWaveSampleXZ; bounded bodies stay in pool xz. m = (world/mpu) * mpu = world.
                float mpu = _body.WaveMetersPerUnit;
                float waveX = _body.IsOceanClipmap ? world.x / mpu : poolX;
                float waveZ = _body.IsOceanClipmap ? world.z / mpu : poolZ;
                surfaceH += _body.WaveBank.SampleHeight(waveX, waveZ, _body.WaveTime, mpu);
                poolFlow -= _body.WaveBank.SampleSlope(waveX, waveZ, _body.WaveTime, mpu)
                            * _body.waveNormalStrength;
            }
            return true;
        }

        // Interactive ripple sample (r = height, b/a = normal.xz) at a world point. Windowed
        // bodies read the camera-following window by world position (rest outside it); whole-body
        // bodies read the fixed grid at pool UV. Mirrors the shader's SampleRipple.
        // BILINEAR across the four surrounding texels: the old nearest-texel read made every
        // CPU consumer (buoyancy, splash drift, waterline queries) jump in a step whenever a
        // mover crossed a texel boundary - one visible micro-pulse per crossing.
        Color SampleRipple(Vector3 world, float poolX, float poolZ)
        {
            float u, v;
            if (_body.IsWindowed)
            {
                Vector3 sim = _body.WorldToSim(new Vector3(world.x, _body.SimWindowCenter.y, world.z));
                if (sim.x < -1f || sim.x > 1f || sim.z < -1f || sim.z > 1f)
                    return new Color(0f, 0f, 0f, 0f); // outside the window: flat rest
                u = sim.x * 0.5f + 0.5f; v = sim.z * 0.5f + 0.5f;
            }
            else
            {
                u = poolX * 0.5f + 0.5f; v = poolZ * 0.5f + 0.5f;
            }

            int res = _body.SimResolution;
            float sx = Mathf.Clamp(u * res - 0.5f, 0f, res - 1f);
            float sz = Mathf.Clamp(v * res - 0.5f, 0f, res - 1f);
            int x0 = (int)sx, z0 = (int)sz;
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int z1 = Mathf.Min(z0 + 1, res - 1);
            float tx = sx - x0, tz = sz - z0;

            Color bottom = Color.Lerp(_heightCpu[z0 * res + x0], _heightCpu[z0 * res + x1], tx);
            Color top    = Color.Lerp(_heightCpu[z1 * res + x0], _heightCpu[z1 * res + x1], tx);
            return Color.Lerp(bottom, top, tz);
        }
    }
}
