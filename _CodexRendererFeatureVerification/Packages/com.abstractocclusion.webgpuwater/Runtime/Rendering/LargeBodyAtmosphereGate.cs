// WebGpuWater - large-body atmosphere gate.
// Single definition of "should the fullscreen ocean god-ray pass run this frame". The pass is
// OCEAN-ONLY and reads the camera-selected body's property block. Bounded bodies report
// IsOceanClipmap == false and pools never set a god-ray density, so their look is untouched.
//
// URP-only: the pass is a URP ScriptableRendererFeature, so this gate only has a consumer when
// the Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
namespace AbstractOcclusion.WebGpuWater
{
    internal static class LargeBodyAtmosphereGate
    {
        // The source matches the fullscreen fog source, so both passes reconstruct one water volume.
        internal static WaterVolume SourceOcean
        {
            get
            {
                WaterVolume source = WaterVolume.FogSource ?? WaterVolume.Primary;
                return source != null && source.IsOceanClipmap && source.LargeGodRayDensity > 0f
                    ? source
                    : null;
            }
        }

        internal static bool HasActiveGodRayOcean => SourceOcean != null;
    }
}
#endif
