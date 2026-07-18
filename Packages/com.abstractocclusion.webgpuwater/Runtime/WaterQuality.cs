// WebGL Water - quality tiers (Unity 6 / URP port)
// Scales the GPU-cost knobs (sim grid resolution, caustic resolution, god-ray steps)
// so the same water fits both a PC and the tighter WebGPU/mobile budget. Assign one
// asset to every WaterVolume; each body reads it at startup. With no asset a body uses
// Tier.Default (the original 256/1024/24 look), so existing scenes are unaffected.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [CreateAssetMenu(fileName = "WaterQuality", menuName = "AbstractOcclusion/WebGpuWater/Water Quality")]
    public class WaterQuality : ScriptableObject
    {
        public enum Selection { Auto, ForceLow, ForceMedium, ForceHigh }

        /// <summary>Underwater fog cost mode. Full = per-pixel wavy waterline (marches the displaced
        /// surface, desktop look). Simple = flat waterline at the camera's published surface height
        /// (closed form, no march) - same absorption/inscatter/darkening at a fraction of the cost,
        /// sized for the WebGPU/mobile budget. Off = the fullscreen fog pass never runs.</summary>
        public enum UnderwaterMode { Off, Simple, Full }

        // Grid resolution must be a positive multiple of the sim's thread-group size; derive
        // from the single source of truth so the two can't drift.
        const int ThreadGroupSize = WaterSimulation.ThreadGroupSize;
        const int MinCausticResolution = 64;
        const int MidGraphicsMemoryMB = 2048; // below this, Auto steps down from High to Medium

        // The refine loop cost is per-pixel dependent texture fetches, so cap it hard.
        const int MaxRefineSteps = 8;

        // Default (no-asset) tier knobs - the original look. Named once so the Default tier
        // and the High-tier inspector defaults below can't drift apart.
        const int DefaultSimResolution = 256;
        const int DefaultCausticResolution = 1024;
        const int DefaultGodRaySteps = 24;
        const int DefaultMaxWaveCount = WaterWaveBank.MaxWaves;
        const int DefaultRefineSteps = 5; // matches the surface shader's original fixed loop

        // Low-end knobs at their "do nothing" values (the High/Default behaviour).
        const float DefaultRenderScale = 1f;
        const int DefaultMeshDetail = 0;            // 0 = keep the authored grid mesh
        const int DefaultCausticInterval = 1;       // render caustics every simulated frame
        const int DefaultReadbackInterval = 1;      // request the height readback every frame
        const int DefaultMaxFoamParticles = 65536;  // effectively "no cap" (the component max)
        const UnderwaterMode DefaultUnderwaterMode = UnderwaterMode.Full; // the original wavy-waterline fog

        // Sanitisation bounds for the low-end knobs.
        const float MinRenderScale = 0.25f;
        const int MaxMeshDetail = 400;
        const int MaxUpdateInterval = 8;   // beyond this, caustics/buoyancy visibly lag
        const int MinFoamParticleCap = 64; // one update thread-group

        /// <summary>An immutable snapshot of the cost knobs a tier scales, handed to a body.
        /// Values are sanitised on construction so a mistyped inspector field still runs.</summary>
        public readonly struct Tier
        {
            public readonly int SimResolution;     // heightfield RT size (multiple of ThreadGroupSize)
            public readonly int CausticResolution; // caustic RT size
            public readonly int GodRaySteps;       // raymarch samples for the god-ray shader
            public readonly bool GodRays;          // god-ray pass on/off
            public readonly bool RichReflections;  // SSR/Planar allowed; when off, bodies cap to SkyOnly
            public readonly int MaxWaveCount;      // cap on summed wind-wave sinusoids (vertex+pixel+CPU cost)
            public readonly int RefineSteps;       // surface peaked-refine loop steps (dependent fetches per pixel)
            public readonly float RenderScale;     // URP render scale the primary body applies (<1 = upscaled)
            public readonly bool RealRefraction;   // screen-space refraction allowed (needs the opaque-texture copy)
            public readonly int MeshDetail;        // >0 = rebuild the surface grid at this detail (0 = authored mesh)
            public readonly int CausticInterval;   // render caustics every Nth simulated frame
            public readonly int ReadbackInterval;  // request the buoyancy height readback every Nth frame
            public readonly int MaxFoamParticles;  // cap on the GPU foam-particle pool
            public readonly UnderwaterMode UnderwaterFog; // fullscreen underwater fog cost mode

            public Tier(int simResolution, int causticResolution, int godRaySteps, bool godRays,
                        bool richReflections, int maxWaveCount, int refineSteps,
                        float renderScale, bool realRefraction, int meshDetail,
                        int causticInterval, int readbackInterval, int maxFoamParticles,
                        UnderwaterMode underwaterFog)
            {
                SimResolution = SanitizeResolution(simResolution);
                CausticResolution = Mathf.Max(MinCausticResolution, causticResolution);
                GodRaySteps = Mathf.Max(0, godRaySteps);
                GodRays = godRays;
                RichReflections = richReflections;
                MaxWaveCount = Mathf.Clamp(maxWaveCount, 1, WaterWaveBank.MaxWaves);
                RefineSteps = Mathf.Clamp(refineSteps, 0, MaxRefineSteps);
                RenderScale = Mathf.Clamp(renderScale, MinRenderScale, 1f);
                RealRefraction = realRefraction;
                MeshDetail = Mathf.Clamp(meshDetail, 0, MaxMeshDetail);
                CausticInterval = Mathf.Clamp(causticInterval, 1, MaxUpdateInterval);
                ReadbackInterval = Mathf.Clamp(readbackInterval, 1, MaxUpdateInterval);
                MaxFoamParticles = Mathf.Max(MinFoamParticleCap, maxFoamParticles);
                UnderwaterFog = underwaterFog;
            }

            // Round to the nearest valid grid size rather than fail, keeping a floor of one group.
            static int SanitizeResolution(int resolution)
            {
                int rounded = Mathf.RoundToInt(resolution / (float)ThreadGroupSize) * ThreadGroupSize;
                return Mathf.Max(ThreadGroupSize, rounded);
            }
        }

        /// <summary>Fallback tier when no quality asset is assigned - the original look.</summary>
        public static Tier Default => new Tier(DefaultSimResolution, DefaultCausticResolution,
                                               DefaultGodRaySteps, true, true,
                                               DefaultMaxWaveCount, DefaultRefineSteps,
                                               DefaultRenderScale, true, DefaultMeshDetail,
                                               DefaultCausticInterval, DefaultReadbackInterval,
                                               DefaultMaxFoamParticles, DefaultUnderwaterMode);

        [Tooltip("Auto picks a tier from a capability probe (WebGPU/mobile -> Low). The Force* " +
                 "options pin a specific tier, e.g. to preview Low in a desktop editor.")]
        public Selection selection = Selection.Auto;

        [Header("Tier: High (desktop)")]
        [Min(ThreadGroupSize)] [SerializeField] int highSimResolution = DefaultSimResolution;
        [Min(MinCausticResolution)] [SerializeField] int highCausticResolution = DefaultCausticResolution;
        [Range(8, 64)] [SerializeField] int highGodRaySteps = DefaultGodRaySteps;
        [SerializeField] bool highGodRays = true;
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        [SerializeField] bool highRichReflections = true;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] [SerializeField] int highMaxWaveCount = DefaultMaxWaveCount;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] [SerializeField] int highRefineSteps = DefaultRefineSteps;
        [Tooltip("URP render scale applied by the primary body (restored on exit). <1 renders " +
                 "fewer pixels and upscales - the single biggest lever on fillrate-bound devices.")]
        [Range(MinRenderScale, 1f)] [SerializeField] float highRenderScale = DefaultRenderScale;
        [Tooltip("Allow screen-space (real) refraction. Off also releases the URP opaque-texture " +
                 "copy, a large bandwidth cost on mobile tile GPUs; water falls back to the analytic pool look.")]
        [SerializeField] bool highRealRefraction = true;
        [Tooltip("Rebuild the surface grid at this vertex detail per side (0 = keep the authored " +
                 "mesh). The vertex shader runs 4 fetches + the wave sines per vertex.")]
        [Range(0, MaxMeshDetail)] [SerializeField] int highMeshDetail = DefaultMeshDetail;
        [Tooltip("Render caustics every Nth simulated frame (2 = half rate, rarely visible).")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int highCausticInterval = DefaultCausticInterval;
        [Tooltip("Request the buoyancy height readback every Nth frame (readback bandwidth).")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int highReadbackInterval = DefaultReadbackInterval;
        [Tooltip("Cap on the GPU foam-particle pool (all capacity is drawn every frame).")]
        [SerializeField] int highMaxFoamParticles = DefaultMaxFoamParticles;
        [Tooltip("Underwater fog: Full = wavy waterline (per-pixel surface march), Simple = flat " +
                 "waterline (closed form, near-free), Off = no fullscreen fog pass.")]
        [SerializeField] UnderwaterMode highUnderwaterFog = UnderwaterMode.Full;

        [Header("Tier: Medium")]
        [Min(ThreadGroupSize)] [SerializeField] int mediumSimResolution = 128;
        [Min(MinCausticResolution)] [SerializeField] int mediumCausticResolution = 512;
        [Range(8, 64)] [SerializeField] int mediumGodRaySteps = 16;
        [SerializeField] bool mediumGodRays = true;
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        [SerializeField] bool mediumRichReflections = true;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] [SerializeField] int mediumMaxWaveCount = 12;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] [SerializeField] int mediumRefineSteps = 3;
        [Tooltip("URP render scale applied by the primary body (restored on exit).")]
        [Range(MinRenderScale, 1f)] [SerializeField] float mediumRenderScale = DefaultRenderScale;
        [Tooltip("Allow screen-space (real) refraction (needs the URP opaque-texture copy).")]
        [SerializeField] bool mediumRealRefraction = true;
        [Tooltip("Rebuild the surface grid at this vertex detail per side (0 = authored mesh).")]
        [Range(0, MaxMeshDetail)] [SerializeField] int mediumMeshDetail = DefaultMeshDetail;
        [Tooltip("Render caustics every Nth simulated frame.")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int mediumCausticInterval = DefaultCausticInterval;
        [Tooltip("Request the buoyancy height readback every Nth frame.")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int mediumReadbackInterval = DefaultReadbackInterval;
        [Tooltip("Cap on the GPU foam-particle pool.")]
        [SerializeField] int mediumMaxFoamParticles = DefaultMaxFoamParticles;
        [Tooltip("Underwater fog: Full = wavy waterline (per-pixel surface march), Simple = flat " +
                 "waterline (closed form, near-free), Off = no fullscreen fog pass.")]
        [SerializeField] UnderwaterMode mediumUnderwaterFog = UnderwaterMode.Full;

        [Header("Tier: Low (WebGPU / mobile)")]
        [Min(ThreadGroupSize)] [SerializeField] int lowSimResolution = 128;
        [Min(MinCausticResolution)] [SerializeField] int lowCausticResolution = 256;
        // God rays kept ON at reduced steps: cheap enough for the WebGPU/mobile budget, and the
        // scene reads wrong without them (they were the main thing lost on the constrained build).
        [Range(0, 64)] [SerializeField] int lowGodRaySteps = 12;
        [SerializeField] bool lowGodRays = true;
        // SSR (needs Depth+Opaque) and Planar (a full extra scene render) are the priciest paths;
        // off by default on the constrained budget so Low falls back to the cheap procedural sky.
        [Tooltip("Allow SSR/Planar reflections. When off, every body caps to SkyOnly.")]
        [SerializeField] bool lowRichReflections = false;
        [Tooltip("Cap on the wind-wave sinusoid count (vertex + pixel + buoyancy cost each).")]
        [Range(1, WaterWaveBank.MaxWaves)] [SerializeField] int lowMaxWaveCount = 8;
        [Tooltip("Surface peaked-refine loop steps; each is a dependent texture fetch per pixel.")]
        [Range(0, MaxRefineSteps)] [SerializeField] int lowRefineSteps = 2;
        [Tooltip("URP render scale applied by the primary body (restored on exit). The single " +
                 "biggest lever on fillrate-bound tablets; the water's soft look upscales well.")]
        [Range(MinRenderScale, 1f)] [SerializeField] float lowRenderScale = 0.7f;
        // Real refraction needs URP's opaque-texture copy - a large bandwidth cost on mobile
        // tile GPUs. Off: the copy is released and the surface uses the analytic pool look.
        [Tooltip("Allow screen-space (real) refraction (needs the URP opaque-texture copy).")]
        [SerializeField] bool lowRealRefraction = false;
        [Tooltip("Rebuild the surface grid at this vertex detail per side (0 = authored mesh). " +
                 "100 = quarter the vertex cost of the authored 200 grid; a 128 sim doesn't need more.")]
        [Range(0, MaxMeshDetail)] [SerializeField] int lowMeshDetail = 100;
        [Tooltip("Render caustics every Nth simulated frame (2 = half rate, rarely visible).")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int lowCausticInterval = 2;
        [Tooltip("Request the buoyancy height readback every Nth frame (readback bandwidth; " +
                 "buoyancy already tolerates async latency).")]
        [Range(1, MaxUpdateInterval)] [SerializeField] int lowReadbackInterval = 3;
        [Tooltip("Cap on the GPU foam-particle pool (all capacity is drawn every frame).")]
        [SerializeField] int lowMaxFoamParticles = 1024;
        // Simple by default: the Full mode's per-pixel waterline march (up to 40 surface evaluations
        // per pixel, twice, fullscreen) is exactly the cost that pushed constrained devices under
        // budget; the flat-waterline fog keeps the underwater look for a couple of ALU ops.
        [Tooltip("Underwater fog: Full = wavy waterline (per-pixel surface march), Simple = flat " +
                 "waterline (closed form, near-free), Off = no fullscreen fog pass.")]
        [SerializeField] UnderwaterMode lowUnderwaterFog = UnderwaterMode.Simple;

        /// <summary>The active tier: the forced one, or the capability-probed one under Auto.</summary>
        public Tier Resolve()
        {
            switch (selection)
            {
                case Selection.ForceLow: return Low;
                case Selection.ForceMedium: return Medium;
                case Selection.ForceHigh: return High;
                default: return Probe();
            }
        }

        Tier High => new Tier(highSimResolution, highCausticResolution, highGodRaySteps, highGodRays,
                              highRichReflections, highMaxWaveCount, highRefineSteps,
                              highRenderScale, highRealRefraction, highMeshDetail,
                              highCausticInterval, highReadbackInterval, highMaxFoamParticles,
                              highUnderwaterFog);
        Tier Medium => new Tier(mediumSimResolution, mediumCausticResolution, mediumGodRaySteps, mediumGodRays,
                                mediumRichReflections, mediumMaxWaveCount, mediumRefineSteps,
                                mediumRenderScale, mediumRealRefraction, mediumMeshDetail,
                                mediumCausticInterval, mediumReadbackInterval, mediumMaxFoamParticles,
                                mediumUnderwaterFog);
        Tier Low => new Tier(lowSimResolution, lowCausticResolution, lowGodRaySteps, lowGodRays,
                             lowRichReflections, lowMaxWaveCount, lowRefineSteps,
                             lowRenderScale, lowRealRefraction, lowMeshDetail,
                             lowCausticInterval, lowReadbackInterval, lowMaxFoamParticles,
                             lowUnderwaterFog);

        // Pick a tier from the running hardware. The web player is how Unity ships WebGPU
        // builds, and async readback (buoyancy) is often unavailable there - both force Low.
        Tier Probe()
        {
            bool constrained = Application.platform == RuntimePlatform.WebGLPlayer
                               || Application.isMobilePlatform
                               || !SystemInfo.supportsAsyncGPUReadback;
            if (constrained) return Low;
            if (SystemInfo.graphicsMemorySize < MidGraphicsMemoryMB) return Medium;
            return High;
        }
    }
}
