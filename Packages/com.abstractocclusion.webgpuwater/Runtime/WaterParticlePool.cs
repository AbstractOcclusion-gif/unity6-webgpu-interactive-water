// WebGpuWater - shared GPU-particle pool plumbing.
//
// ONE home for the pool recipe WaterFoamParticles used to carry as a copy-pasted block:
// tier-capped pow2 pool allocation with dead-slot zeroing,
// zeroed counters, and the flipbook MaterialPropertyBlock plumbing both draws share.
// Pure helpers - each system keeps owning its buffers' lifetime (Dispose stays local).
using UnityEngine;
using System.Runtime.InteropServices;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterParticlePool
    {
        static readonly int ID_FlipbookGrid = Shader.PropertyToID("_ParticleFlipbookGrid");
        static readonly int ID_FlipbookFps = Shader.PropertyToID("_ParticleFlipbookFps");
        static readonly int ID_Particles = WaterShaderProps.Particles;

        // One dead particle for the global fallback binding below. Only the STRIDE matters: the slot is
        // all zeroes, and life = 0 marks it dead. Derived from the C# struct rather than restated here -
        // this used to be a hardcoded 48 with a "MUST match" comment, i.e. a fourth copy of the layout.
        static readonly int DeadParticleStrideBytes = WaterFoamParticles.ParticleStrideBytes;
        const int DeadParticleCount = 1;

        static GraphicsBuffer _deadFallback;

        // Bind a single dead particle GLOBALLY under _Particles. Any draw of the procedural
        // particle shader that carries no per-draw buffer - the material inspector's preview
        // sphere, asset-thumbnail renders, the material dropped onto a plain renderer by
        // mistake - then reads life = 0 (out-of-range structured loads return zero on every
        // supported backend), emits degenerate quads and stays silent, instead of D3D12
        // refusing the draw ("requires a buffer (SRV) _Particles ... but none provided") and
        // spamming the console. Real draws are unaffected: the per-draw
        // MaterialPropertyBlock binding always wins over the global.
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod] // previews happen outside play mode too
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void InstallGlobalDeadFallback()
        {
            if (_deadFallback != null && _deadFallback.IsValid()) return;
            _deadFallback = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                                               DeadParticleCount, DeadParticleStrideBytes);
            _deadFallback.SetData(
                new float[DeadParticleCount * DeadParticleStrideBytes / sizeof(float)]);
            Shader.SetGlobalBuffer(ID_Particles, _deadFallback);
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeGlobalDeadFallback;
#else
            Application.quitting += DisposeGlobalDeadFallback;
#endif
        }

        static void DisposeGlobalDeadFallback()
        {
            _deadFallback?.Dispose();
            _deadFallback = null;
        }

        /// <summary>Allocate the tier-capped, pow2-rounded particle pool plus its zeroed
        /// counters buffer, and return the pow2 capacity. The pool is zero-initialised
        /// (life = 0 marks every slot dead); the minimum keeps the pool a whole number of
        /// update thread groups. The whole pool is drawn every frame (dead slots emit
        /// degenerate quads), so weak devices pay for capacity whether particles are alive
        /// or not - hence the tier budget cap first.</summary>
        public static int Allocate<TParticle>(int requestedCapacity, int tierBudget,
                                              int minimumCapacity, int counterCount,
                                              out GraphicsBuffer particles,
                                              out GraphicsBuffer counters)
            where TParticle : struct
        {
            int budget = Mathf.Min(requestedCapacity, tierBudget);
            int capacityPow2 = Mathf.NextPowerOfTwo(Mathf.Max(minimumCapacity, budget));
            particles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacityPow2,
                                           Marshal.SizeOf<TParticle>());
            particles.SetData(new TParticle[capacityPow2]); // life = 0 -> every slot dead
            counters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, counterCount,
                                          sizeof(uint));
            counters.SetData(new uint[counterCount]);
            return capacityPow2;
        }

        /// <summary>Write the flipbook atlas layout + speed into a draw's property block.
        /// Driven from the owning component (the single control point), never material
        /// sliders - the same convention on every particle draw.</summary>
        public static void WriteFlipbook(MaterialPropertyBlock mpb, Vector2Int grid, float fps)
        {
            mpb.SetVector(ID_FlipbookGrid,
                          new Vector4(Mathf.Max(1, grid.x), Mathf.Max(1, grid.y), 0f, 0f));
            mpb.SetFloat(ID_FlipbookFps, fps); // 0 = static per-seed variant; >0 animates
        }
    }
}
