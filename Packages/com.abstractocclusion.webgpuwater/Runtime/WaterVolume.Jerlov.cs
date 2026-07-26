// WebGpuWater - WaterVolume: stored Jerlov water-type selection.
// The physical colour coefficients live in JerlovWaterTypes; this only remembers which type the
// body was set to, so the inspector's "Apply water colour" button has a source and later phases can
// re-derive from it. Applying the type writes the existing Fog Extinction / Scatter fields (editor).
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [SerializeField,
         Tooltip("Physical Jerlov water type. Use the \"Apply water colour\" button to write it into " +
                 "Fog Extinction and the body/scatter colour. Purely a stored reference on its own.")]
        JerlovWaterType jerlovWaterType = JerlovWaterType.OceanII;
    }
}
