// WebGL Water - the shared per-frame seam handed to the water modules (Unity 6 / URP port).
namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>
    /// The state the master shares with its <see cref="IWaterModule"/> set. It is the seam that keeps
    /// cross-feature flow explicit as the god-class is broken up: modules receive this instead of
    /// reaching into <see cref="WaterVolume"/> directly.
    ///
    /// Phase 1 carries only the owning body (modules still read most state through that facade). The
    /// per-frame fields the architecture calls for — camera, wave time, wind speed/heading, sim window,
    /// shared textures, quality tier — are lifted onto this context as module Tick and uniform
    /// publishing move onto <see cref="IWaterModule"/> in later increments.
    /// </summary>
    internal sealed class WaterContext
    {
        /// <summary>The body that owns these modules; the facade for state not yet lifted here.</summary>
        public WaterVolume Owner { get; }

        public WaterContext(WaterVolume owner) => Owner = owner;
    }
}
