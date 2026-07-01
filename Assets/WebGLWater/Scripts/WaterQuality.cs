// WebGL Water - quality tiers (Unity 6 / URP port)
// Scales the GPU-cost knobs (sim grid resolution, caustic resolution, god-ray steps)
// so the same water fits both a PC and the tighter WebGPU/mobile budget. Assign one
// asset to every WaterVolume; each body reads it at startup. With no asset a body uses
// Tier.Default (the original 256/1024/24 look), so existing scenes are unaffected.
using UnityEngine;

namespace WebGLWater
{
    [CreateAssetMenu(fileName = "WaterQuality", menuName = "WebGL Water/Water Quality")]
    public class WaterQuality : ScriptableObject
    {
        public enum Selection { Auto, ForceLow, ForceMedium, ForceHigh }

        // Grid resolution must be a positive multiple of the sim's thread-group size; derive
        // from the single source of truth so the two can't drift.
        const int ThreadGroupSize = WaterSimulation.ThreadGroupSize;
        const int MinCausticResolution = 64;
        const int MidGraphicsMemoryMB = 2048; // below this, Auto steps down from High to Medium

        /// <summary>An immutable snapshot of the cost knobs a tier scales, handed to a body.
        /// Values are sanitised on construction so a mistyped inspector field still runs.</summary>
        public readonly struct Tier
        {
            public readonly int SimResolution;     // heightfield RT size (multiple of ThreadGroupSize)
            public readonly int CausticResolution; // caustic RT size
            public readonly int GodRaySteps;       // raymarch samples for the god-ray shader
            public readonly bool GodRays;          // god-ray pass on/off

            public Tier(int simResolution, int causticResolution, int godRaySteps, bool godRays)
            {
                SimResolution = SanitizeResolution(simResolution);
                CausticResolution = Mathf.Max(MinCausticResolution, causticResolution);
                GodRaySteps = Mathf.Max(0, godRaySteps);
                GodRays = godRays;
            }

            // Round to the nearest valid grid size rather than fail, keeping a floor of one group.
            static int SanitizeResolution(int resolution)
            {
                int rounded = Mathf.RoundToInt(resolution / (float)ThreadGroupSize) * ThreadGroupSize;
                return Mathf.Max(ThreadGroupSize, rounded);
            }
        }

        /// <summary>Fallback tier when no quality asset is assigned - the original look.</summary>
        public static Tier Default => new Tier(256, 1024, 24, true);

        [Tooltip("Auto picks a tier from a capability probe (WebGPU/mobile -> Low). The Force* " +
                 "options pin a specific tier, e.g. to preview Low in a desktop editor.")]
        public Selection selection = Selection.Auto;

        [Header("Tier: High (desktop)")]
        [Min(ThreadGroupSize)] public int highSimResolution = 256;
        [Min(MinCausticResolution)] public int highCausticResolution = 1024;
        [Range(8, 64)] public int highGodRaySteps = 24;
        public bool highGodRays = true;

        [Header("Tier: Medium")]
        [Min(ThreadGroupSize)] public int mediumSimResolution = 128;
        [Min(MinCausticResolution)] public int mediumCausticResolution = 512;
        [Range(8, 64)] public int mediumGodRaySteps = 16;
        public bool mediumGodRays = true;

        [Header("Tier: Low (WebGPU / mobile)")]
        [Min(ThreadGroupSize)] public int lowSimResolution = 128;
        [Min(MinCausticResolution)] public int lowCausticResolution = 256;
        [Range(0, 64)] public int lowGodRaySteps = 0;
        public bool lowGodRays = false;

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

        Tier High => new Tier(highSimResolution, highCausticResolution, highGodRaySteps, highGodRays);
        Tier Medium => new Tier(mediumSimResolution, mediumCausticResolution, mediumGodRaySteps, mediumGodRays);
        Tier Low => new Tier(lowSimResolution, lowCausticResolution, lowGodRaySteps, lowGodRays);

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
