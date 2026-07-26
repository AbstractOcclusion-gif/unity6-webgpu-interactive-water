// WebGpuWater - the ONE registry of shader PROPERTY names that cross a C# file boundary.
//
// WHY: this is the twin of WaterShaderNames, which already solved the same problem for shader
// DECLARATION names ("these names were inlined in up to three places each ... renaming a shader
// silently broke whichever copy was forgotten"). Property names got no such registry, so nine of
// them ended up written out twice in different files:
//
//   _WaterTex, _SimCenter, _SimExtent, _LightDir, _VolumeCenter, _VolumeExtent, _VolumeRot
//       -> WaterUniformPublisher + WaterCausticsPass
//   _WaterFogEnabled, _WaterFogDensity
//       -> WaterUniformPublisher + WaterVolume.Chunk
//
// Renaming _VolumeRot in the HLSL therefore broke the caustic occluder's projection while the
// surface kept working - a silent, one-sided failure. Every name below is written ONCE here.
//
// SCOPE, deliberately narrow: only properties consumed from more than one C# file live here. A
// property used by exactly one file stays a private ID in that file, where it is already
// single-sourced and closer to the code that reads it. The HLSL declaration remains the source of
// truth; a rename is: change the shader + the one const here.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterShaderProps
    {
        // ---- names (mirror of the HLSL declaration) ----
        internal const string WaterTexName = "_WaterTex";
        internal const string SimCenterName = "_SimCenter";
        internal const string SimExtentName = "_SimExtent";
        internal const string LightDirName = "_LightDir";
        internal const string VolumeCenterName = "_VolumeCenter";
        internal const string VolumeExtentName = "_VolumeExtent";
        internal const string VolumeRotName = "_VolumeRot";
        internal const string WaterFogEnabledName = "_WaterFogEnabled";
        internal const string WaterFogDensityName = "_WaterFogDensity";

        // ---- cached ids ----
        internal static readonly int WaterTex = Shader.PropertyToID(WaterTexName);
        internal static readonly int SimCenter = Shader.PropertyToID(SimCenterName);
        internal static readonly int SimExtent = Shader.PropertyToID(SimExtentName);
        internal static readonly int LightDir = Shader.PropertyToID(LightDirName);
        internal static readonly int VolumeCenter = Shader.PropertyToID(VolumeCenterName);
        internal static readonly int VolumeExtent = Shader.PropertyToID(VolumeExtentName);
        internal static readonly int VolumeRot = Shader.PropertyToID(VolumeRotName);
        internal static readonly int WaterFogEnabled = Shader.PropertyToID(WaterFogEnabledName);
        internal static readonly int WaterFogDensity = Shader.PropertyToID(WaterFogDensityName);
    }
}
