// WebGpuWater - the ONE registry of shader PROPERTY names that cross a C# file boundary.
//
// WHY: this is the twin of WaterShaderNames, which already solved the same problem for shader
// DECLARATION names ("these names were inlined in up to three places each ... renaming a shader
// silently broke whichever copy was forgotten"). Renaming _VolumeRot in the HLSL once broke the
// caustic occluder's projection while the surface kept working - a silent, one-sided failure.
//
// A property name is a STRING, so a typo or a half-finished rename is invisible: PropertyToID
// happily returns an id for a name nothing declares, SetFloat on it is a no-op, and the feature
// simply does nothing with no error anywhere. Naming them here turns that whole class of silent
// failure into a compile error.
//
// SCOPE, deliberately narrow: only properties consumed from more than one C# file live here. A
// property used by exactly one file stays a private ID in that file, where it is already
// single-sourced and closer to the code that reads it. The HLSL declaration remains the source of
// truth; a rename is: change the shader + the one const here.
//
// ONE carve-out: property names UNITY owns (_BaseColor) are deliberately NOT here. The registry
// protects against OUR renames, and we will never rename a URP standard property - so an entry for
// it would be a line that guards nothing. Do not "complete" the list with it.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterShaderProps
    {
        // ---- WaterUniformPublisher + WaterCausticsPass / WaterVolume.Chunk (the original nine) ----
        internal const string WaterTexName = "_WaterTex";
        internal const string SimCenterName = "_SimCenter";
        internal const string SimExtentName = "_SimExtent";
        internal const string LightDirName = "_LightDir";
        internal const string VolumeCenterName = "_VolumeCenter";
        internal const string VolumeExtentName = "_VolumeExtent";
        internal const string VolumeRotName = "_VolumeRot";
        internal const string WaterFogEnabledName = "_WaterFogEnabled";
        internal const string WaterFogDensityName = "_WaterFogDensity";

        internal static readonly int WaterTex = Shader.PropertyToID(WaterTexName);
        internal static readonly int SimCenter = Shader.PropertyToID(SimCenterName);
        internal static readonly int SimExtent = Shader.PropertyToID(SimExtentName);
        internal static readonly int LightDir = Shader.PropertyToID(LightDirName);
        internal static readonly int VolumeCenter = Shader.PropertyToID(VolumeCenterName);
        internal static readonly int VolumeExtent = Shader.PropertyToID(VolumeExtentName);
        internal static readonly int VolumeRot = Shader.PropertyToID(VolumeRotName);
        internal static readonly int WaterFogEnabled = Shader.PropertyToID(WaterFogEnabledName);
        internal static readonly int WaterFogDensity = Shader.PropertyToID(WaterFogDensityName);

        // ---- WaterUniformPublisher <-> WaterRiverSurface ----
        internal const string PatchCoverActiveName = "_PatchCoverActive";
        internal static readonly int PatchCoverActive = Shader.PropertyToID(PatchCoverActiveName);

        // ---- WaterUniformPublisher / WaterRiverFoam / WaterRiverSurface ----
        internal const string FoamEnabledName = "_FoamEnabled";
        internal const string FoamMaskName = "_FoamMask";
        internal const string FoamTileSizeName = "_FoamTileSize";
        internal const string FoamFeatherName = "_FoamFeather";
        internal const string FoamCoreCutName = "_FoamCoreCut";
        internal const string RiverFoamActiveName = "_RiverFoamActive";
        internal const string RiverFoamStrengthName = "_RiverFoamStrength";
        internal const string RiverFluidActiveName = "_RiverFluidActive";
        internal const string RiverFluidInverseLengthName = "_RiverFluidInvLength";
        internal const string RiverFluidMaximumSpeedName = "_RiverFluidMaxSpeed";
        internal static readonly int FoamEnabled = Shader.PropertyToID(FoamEnabledName);
        internal static readonly int FoamMask = Shader.PropertyToID(FoamMaskName);
        internal static readonly int FoamTileSize = Shader.PropertyToID(FoamTileSizeName);
        internal static readonly int FoamFeather = Shader.PropertyToID(FoamFeatherName);
        internal static readonly int FoamCoreCut = Shader.PropertyToID(FoamCoreCutName);
        internal static readonly int RiverFoamActive = Shader.PropertyToID(RiverFoamActiveName);
        internal static readonly int RiverFoamStrength = Shader.PropertyToID(RiverFoamStrengthName);
        internal static readonly int RiverFluidActive = Shader.PropertyToID(RiverFluidActiveName);
        internal static readonly int RiverFluidInverseLength =
            Shader.PropertyToID(RiverFluidInverseLengthName);
        internal static readonly int RiverFluidMaximumSpeed =
            Shader.PropertyToID(RiverFluidMaximumSpeedName);

        // ---- WaterShoreDepthField.cs <-> WaterSimulation.cs ----
        internal const string ShoreShoalDepthName = "_ShoreShoalDepth";
        internal const string ShoreGreenBandDepthName = "_ShoreGreenBandDepth";
        internal const string ShoreSwashDepositGainName = "_ShoreSwashDepositGain";
        internal const string SurfActiveName = "_SurfActive";
        internal const string SurfAmbientFadeName = "_SurfAmbientFade";
        internal const string SurfAmplitudeName = "_SurfAmplitude";
        internal const string SurfBandDepthName = "_SurfBandDepth";
        internal const string SurfCompressionName = "_SurfCompression";
        internal const string SurfCrestLengthName = "_SurfCrestLength";
        internal const string SurfCrestPersistenceName = "_SurfCrestPersistence";
        internal const string SurfCrestVariationName = "_SurfCrestVariation";
        internal const string SurfDirectionalityName = "_SurfDirectionality";
        internal const string SurfFoamBoreGainName = "_SurfFoamBoreGain";
        internal const string SurfFoamRepartActiveName = "_SurfFoamRepartActive";
        internal const string SurfFoamTrailGainName = "_SurfFoamTrailGain";
        internal const string SurfFoamTrailLengthName = "_SurfFoamTrailLength";
        internal const string SurfGreensName = "_SurfGreens";
        internal const string SurfLeanName = "_SurfLean";
        internal const string SurfPeriodName = "_SurfPeriod";
        internal const string SurfSetStrengthName = "_SurfSetStrength";
        internal const string SurfSwashAmplitudeName = "_SurfSwashAmplitude";
        internal const string SurfSwashMaxSlopeTanName = "_SurfSwashMaxSlopeTan";
        internal const string SurfWaterlineFoamName = "_SurfWaterlineFoam";
        internal const string SurfWavelengthName = "_SurfWavelength";
        internal const string SurfWindDirXZName = "_SurfWindDirXZ";

        internal static readonly int ShoreShoalDepth = Shader.PropertyToID(ShoreShoalDepthName);
        internal static readonly int ShoreGreenBandDepth = Shader.PropertyToID(ShoreGreenBandDepthName);
        internal static readonly int ShoreSwashDepositGain = Shader.PropertyToID(ShoreSwashDepositGainName);
        internal static readonly int SurfActive = Shader.PropertyToID(SurfActiveName);
        internal static readonly int SurfAmbientFade = Shader.PropertyToID(SurfAmbientFadeName);
        internal static readonly int SurfAmplitude = Shader.PropertyToID(SurfAmplitudeName);
        internal static readonly int SurfBandDepth = Shader.PropertyToID(SurfBandDepthName);
        internal static readonly int SurfCompression = Shader.PropertyToID(SurfCompressionName);
        internal static readonly int SurfCrestLength = Shader.PropertyToID(SurfCrestLengthName);
        internal static readonly int SurfCrestPersistence = Shader.PropertyToID(SurfCrestPersistenceName);
        internal static readonly int SurfCrestVariation = Shader.PropertyToID(SurfCrestVariationName);
        internal static readonly int SurfDirectionality = Shader.PropertyToID(SurfDirectionalityName);
        internal static readonly int SurfFoamBoreGain = Shader.PropertyToID(SurfFoamBoreGainName);
        internal static readonly int SurfFoamRepartActive = Shader.PropertyToID(SurfFoamRepartActiveName);
        internal static readonly int SurfFoamTrailGain = Shader.PropertyToID(SurfFoamTrailGainName);
        internal static readonly int SurfFoamTrailLength = Shader.PropertyToID(SurfFoamTrailLengthName);
        internal static readonly int SurfGreens = Shader.PropertyToID(SurfGreensName);
        internal static readonly int SurfLean = Shader.PropertyToID(SurfLeanName);
        internal static readonly int SurfPeriod = Shader.PropertyToID(SurfPeriodName);
        internal static readonly int SurfSetStrength = Shader.PropertyToID(SurfSetStrengthName);
        internal static readonly int SurfSwashAmplitude = Shader.PropertyToID(SurfSwashAmplitudeName);
        internal static readonly int SurfSwashMaxSlopeTan = Shader.PropertyToID(SurfSwashMaxSlopeTanName);
        internal static readonly int SurfWaterlineFoam = Shader.PropertyToID(SurfWaterlineFoamName);
        internal static readonly int SurfWavelength = Shader.PropertyToID(SurfWavelengthName);
        internal static readonly int SurfWindDirXZ = Shader.PropertyToID(SurfWindDirXZName);

        // ---- WaterFoamParticles.cs <-> WaterUniformPublisher.cs ----
        internal const string ExclusionCountName = "_ExclusionCount";
        internal const string ExclusionEdgeParamsName = "_ExclusionEdgeParams";
        internal const string ExclusionShapeName = "_ExclusionShape";
        internal const string ExclusionWorldToLocalName = "_ExclusionWorldToLocal";

        internal static readonly int ExclusionCount = Shader.PropertyToID(ExclusionCountName);
        internal static readonly int ExclusionEdgeParams = Shader.PropertyToID(ExclusionEdgeParamsName);
        internal static readonly int ExclusionShape = Shader.PropertyToID(ExclusionShapeName);
        internal static readonly int ExclusionWorldToLocal = Shader.PropertyToID(ExclusionWorldToLocalName);

        // ---- WaterBuildKit.cs <-> WaterUniformPublisher.cs ----
        internal const string FoamTexName = "_FoamTex";
        internal const string FoamTexFramesName = "_FoamTexFrames";
        internal const string RealRefractionName = "_RealRefraction";

        internal static readonly int FoamTex = Shader.PropertyToID(FoamTexName);
        internal static readonly int FoamTexFrames = Shader.PropertyToID(FoamTexFramesName);
        internal static readonly int RealRefraction = Shader.PropertyToID(RealRefractionName);

        // ---- WaterFoamParticles.cs <-> WaterOceanFft.cs ----
        internal const string OceanFftCascadeCountName = "_OceanFftCascadeCount";
        internal const string OceanFftDomainSizesName = "_OceanFftDomainSizes";
        internal const string OceanFftNormalName = "_OceanFftNormal";

        internal static readonly int OceanFftCascadeCount = Shader.PropertyToID(OceanFftCascadeCountName);
        internal static readonly int OceanFftDomainSizes = Shader.PropertyToID(OceanFftDomainSizesName);
        internal static readonly int OceanFftNormal = Shader.PropertyToID(OceanFftNormalName);

        // ---- P4 aperiodic ocean: surface publisher + FFT bake + foam-particle glue ----
        internal const string OceanDirectionMapName = "_OceanDirectionMap";
        internal const string OceanAperiodicParamsName = "_OceanAperiodicParams";
        internal const string OceanDirectionMapFrameName = "_OceanDirectionMapFrame";

        internal static readonly int OceanDirectionMap = Shader.PropertyToID(OceanDirectionMapName);
        internal static readonly int OceanAperiodicParams = Shader.PropertyToID(OceanAperiodicParamsName);
        internal static readonly int OceanDirectionMapFrame = Shader.PropertyToID(OceanDirectionMapFrameName);

        // ---- WaterSimulation.cs <-> WaterUniformPublisher.cs ----
        internal const string BedTexName = "_BedTex";
        internal const string UseBedDepthName = "_UseBedDepth";

        internal static readonly int BedTex = Shader.PropertyToID(BedTexName);
        internal static readonly int UseBedDepth = Shader.PropertyToID(UseBedDepthName);

        // ---- WaterBuildKit.cs <-> WaterFoamProfile.cs ----
        internal const string ParticleTexName = "_ParticleTex";
        internal const string BreakupTexName = "_BreakupTex";

        internal static readonly int ParticleTex = Shader.PropertyToID(ParticleTexName);
        internal static readonly int BreakupTex = Shader.PropertyToID(BreakupTexName);

        // ---- PlanarReflection.cs <-> WaterUniformPublisher.cs ----
        internal const string PlanarReflectionTexName = "_PlanarReflectionTex";

        internal static readonly int PlanarReflectionTex = Shader.PropertyToID(PlanarReflectionTexName);

        // ---- WaterFoamParticles.cs <-> WaterParticlePool.cs ----
        internal const string ParticlesName = "_Particles";

        internal static readonly int Particles = Shader.PropertyToID(ParticlesName);

        // ---- WaterFoamParticles.cs <-> WaterSimulation.cs ----
        internal const string SizeName = "_Size";

        internal static readonly int Size = Shader.PropertyToID(SizeName);
    }
}
