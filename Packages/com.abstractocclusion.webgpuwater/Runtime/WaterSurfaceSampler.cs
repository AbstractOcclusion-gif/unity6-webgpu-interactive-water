// WebGpuWater - CPU-side surface sampling for buoyancy and surface queries.
// Extracted from WaterVolume: owns the async height readback (throttling/error-streak state on
// the shared AsyncReadbackChannel, reused CPU buffer here) and the bilinear CPU sample of the
// ripple field, composited with the analytic wind waves. Created per enable; the volume's
// public TryGet* facade delegates here.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSurfaceSampler
    {
        readonly WaterVolume _body;
        readonly System.Action<AsyncGPUReadbackRequest> _onHeightReadback; // cached: a per-request method group would allocate every frame
        // Single-in-flight throttle + error-streak give-up. _readback.Unsupported is true on
        // backends without AsyncGPUReadback, or after persistent readback errors. NOTE: WebGPU is
        // NOT such a backend - Unity documents AsyncGPUReadback as the SUPPORTED path there (it is
        // synchronous readback that WebGPU lacks), so the fallback below is not a web special case.
        // Buoyancy and surface queries fall back to the analytic waterline (flat rest + wind waves)
        // whenever the ripple field is unavailable, so objects still float.
        readonly AsyncReadbackChannel _readback;

        // CPU copy of the height field for buoyancy queries
        Color[] _heightCpu;
        bool _heightReady;

        // Demand gate. The readback is 1.0-1.56 MiB of GPU->CPU traffic per request (the full
        // ARGBFloat sim RT) plus a same-size managed memcpy on landing, and nothing checked that
        // anyone consumed it: a scene with decorative water and no floaters paid in full, every
        // frame, per body. TrySamplePoolSurface stamps this; RequestReadback skips when it is cold.
        // NOT int.MinValue: Time.frameCount - int.MinValue overflows negative on frame 0, which
        // would leave the gate OPEN - the exact opposite of the state we want at startup.
        int _lastDemandFrame = -1000;

        // How long to keep requesting after the last query. Covers, in ONE window: the ceiling on
        // _readbackInterval (WaterQuality.MaxUpdateInterval = 8), so the gate can never close
        // BETWEEN two request-eligible frames; the worst FixedUpdate gap, since buoyancy queries on
        // the physics clock and WaterVolume.Update runs at execution order -50 (a LateUpdate
        // consumer's stamp is first seen next frame); and the 1-2 frame landing latency.
        const int DemandWindowFrames = 12;

        internal WaterSurfaceSampler(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
            _onHeightReadback = OnHeightReadback;
            // The channel probes SystemInfo.supportsAsyncGPUReadback itself.
            _readback = new AsyncReadbackChannel(OnReadbackGaveUp);
        }

        internal void RequestReadback()
        {
            if (_body.Simulation == null) return;
            // Nobody has read the ripple field inside the window: skip the whole transfer.
            if (Time.frameCount - _lastDemandFrame > DemandWindowFrames) return;
            // The channel refuses while a request is in flight, on "unsupported" (probed in its
            // ctor) and after "errored out" (OnReadbackGaveUp below); TrySamplePoolSurface serves
            // queries from the analytic waterline in the latter two cases.
            _readback.Request(_body.Simulation.Texture, TextureFormat.RGBAFloat, _onHeightReadback);
        }

        // Successful landings only: the channel absorbs errors and fires OnReadbackGaveUp once
        // the streak crosses AsyncReadbackChannel.MaxConsecutiveErrors.
        void OnHeightReadback(AsyncGPUReadbackRequest req)
        {
            var data = req.GetData<Color>();
            if (_heightCpu == null || _heightCpu.Length != data.Length)
                _heightCpu = new Color[data.Length];
            data.CopyTo(_heightCpu);
            _heightReady = true;
        }

        // Persistent errors (e.g. a backend that can't convert the format) would otherwise retry
        // silently forever with buoyancy never activating; the channel has latched Unsupported.
        void OnReadbackGaveUp()
        {
            _heightReady = false; // don't keep floating objects on a stale field
            Debug.LogWarning($"WaterVolume: height readback failed {AsyncReadbackChannel.MaxConsecutiveErrors} " +
                             "times in a row; falling back to the analytic waterline for buoyancy.", _body);
        }

        // Pool-space surface height + horizontal normal tilt at a world point (pool xz in [-1,1]).
        // Uses the GPU readback ripple field when available; when it is not - no AsyncGPUReadback on
        // the backend, a landing not yet arrived, or the demand gate having been shut while nothing
        // queried - it falls back to the analytic surface (flat rest + wind waves) so buoyancy and
        // surface queries keep working (interactive ripples / obstacle displacement are simply
        // absent). Always true for a point inside the body: valid from frame 0, which is the
        // contract IWaterHeightSampler documents.
        internal bool TrySamplePoolSurface(Vector3 world, float poolX, float poolZ,
                                           out float surfaceH, out Vector2 poolSurfaceTilt,
                                           float minWavelengthMeters = 0f, bool excludeRipples = false)
        {
            surfaceH = 0f;
            poolSurfaceTilt = Vector2.zero;

            // A self-emitting floater (one with a WaterInteractable wake) reads its OWN ripples back and
            // gets pushed by them - it self-propels. excludeRipples serves such a body the analytic surface
            // (rest + wind + swell) only, breaking the feedback loop; it stays valid from frame 0.

            // THE demand stamp. Buoyancy, probes, the spray pump, splash drift and the whole public
            // TryGet* facade reach the ripple field only through this method, so one mark here catches
            // every consumer. excludeRipples callers never touch _heightCpu, so they must NOT hold the
            // gate open on a body whose only queries are analytic.
            if (!excludeRipples) _lastDemandFrame = Time.frameCount;

            bool haveReadback = !excludeRipples && _heightReady && _heightCpu != null;
            if (haveReadback)
            {
                Color sample = SampleRipple(world, poolX, poolZ);
                surfaceH = sample.r;
                poolSurfaceTilt = new Vector2(sample.b, sample.a); // stored (normal.x, normal.z)
            }
            // No early-out when the readback has not landed. Serving the ANALYTIC surface here is
            // exactly what a backend with Unsupported latched already did, so this is an existing
            // path reached in one more case, not a new one. The old "return false" became
            // WaterBuoyancy's "if (!sample.Valid) continue;" - i.e. NO lift at all - so an object
            // spawned before the first landing free-fell through the surface. The demand gate above
            // makes that state reachable whenever a body has been idle, not just on the first
            // frames, so the fallback is what keeps the gate from costing gameplay.
            // else: analytic surface -> rest (0) + wind waves added below

            // Small wind-wave detail. Open water keeps this layer AND adds the big swell in world
            // space (in the WaterVolume callers), so both wind-wave scales are present.
            if (_body.WindWaves)
            {
                // Oceans sample the wind-wave layer in WORLD metres (extent-independent) to match the
                // shader's WindWaveSampleXZ; bounded bodies stay in pool xz. m = (world/mpu) * mpu = world.
                float mpu = _body.WaveMetersPerUnit;
                float waveX = _body.IsOceanClipmap ? world.x / mpu : poolX;
                float waveZ = _body.IsOceanClipmap ? world.z / mpu : poolZ;
                surfaceH += _body.WaveBank.SampleHeight(waveX, waveZ, _body.WaveTime, mpu, minWavelengthMeters);
                Vector2 waveSlope = _body.WaveBank.SampleSlope(
                    waveX, waveZ, _body.WaveTime, mpu, minWavelengthMeters);
                poolSurfaceTilt -= waveSlope * _body.waveNormalStrength;
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

            // Shared filter (WaterFieldSampling): identical clamp/half-texel semantics to the
            // maths this method used to inline.
            return WaterFieldSampling.SampleBilinear(_heightCpu, _body.SimResolution, u, v);
        }
    }
}
