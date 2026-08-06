// Expose the runtime assembly's internals to the package's own editor assembly, so the
// build kit can share single-source-of-truth constants (e.g. WaterVolume's camera framing)
// without widening the public API surface.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AbstractOcclusion.WebGpuWater.Editor")]
[assembly: InternalsVisibleTo("AbstractOcclusion.WebGpuWater.Tests")]
