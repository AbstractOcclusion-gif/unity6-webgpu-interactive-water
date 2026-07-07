// WebGL Water - the module seam (Unity 6 / URP port).
//
// WaterVolume is being broken out of a god-class into a thin master that orchestrates optional,
// self-contained modules. IWaterModule is the lifecycle contract those modules share. Phase 1
// formalises the collaborators WaterVolume already owns (sim, obstacle, caustics, surface sampler,
// ocean FFT, sim window): the master constructs and disposes them through a registry instead of by
// hand. Per-frame Tick and uniform publishing are added to this contract in later increments, once
// the render/sim schedule is restructured with the editor in the loop.
namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>
    /// One optional unit of water behaviour that <see cref="WaterVolume"/> owns and drives through a
    /// uniform lifecycle. Modules own their resources; the master owns their order and the shared
    /// <see cref="WaterContext"/>.
    /// </summary>
    internal interface IWaterModule
    {
        /// <summary>
        /// Whether this module runs for the owning body. Evaluated once, before <see cref="Initialize"/>,
        /// so a body that lacks the wiring or opt-in for a feature skips it entirely (e.g. the ocean FFT
        /// runs only on an unbounded-ocean body whose FFT compute is assigned).
        /// </summary>
        bool Enabled { get; }

        /// <summary>Create the module's resources for this enable. Called only when <see cref="Enabled"/>.</summary>
        void Initialize(WaterContext context);

        /// <summary>
        /// Release the module's resources for this disable. Must be safe to call when the module was
        /// disabled or never initialized.
        /// </summary>
        void Dispose();
    }
}
