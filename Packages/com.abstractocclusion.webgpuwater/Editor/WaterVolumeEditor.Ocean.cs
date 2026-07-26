// WebGpuWater - WaterVolume inspector: large-water + ocean sections (camera-following sim window,
// open water, horizon clipmap, ocean god rays, whitecap foam). Greyed by body type: the sim window
// and open water apply to Lake + Ocean; the clipmap/god-ray/whitecap blocks are Ocean-only. Draws
// serialized properties by exact path. Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawWindowSection()
        {
            _showWindow = WaterEditorUI.SectionWithToggle(
                "Large-Water Sim Window", _showWindow, Prop(WaterVolumePropertyPaths.EnableLargeBodyWindow), () =>
                DrawFields(
                    "largeBodyThreshold",
                    "simWindowMeters",
                    "clampWindowToShore",
                    "simWindowFocus",
                    "simWindowOffset",
                    "simWindowEdgeFadeTexels"),
                contentEnabled: LakeOrOcean);
        }

        void DrawOceanOpenWaterSection()
        {
            _showOceanOpenWater = WaterEditorUI.SectionWithToggle(
                "Ocean · Open Water", _showOceanOpenWater, Prop(WaterVolumePropertyPaths.OpenWater), () =>
                {
                    EditorGUILayout.HelpBox(SwellHelp, MessageType.None);
                    DrawFields(
                        WaterVolumePropertyPaths.LargeWaveAmplitude,
                        WaterVolumePropertyPaths.LargeWaveChoppiness,
                        WaterVolumePropertyPaths.SwellHeight,
                        WaterVolumePropertyPaths.SwellWavelength,
                        WaterVolumePropertyPaths.UnboundedOcean,
                        WaterVolumePropertyPaths.EdgeFeatherMeters);
                },
                contentEnabled: LakeOrOcean);
        }

        void DrawOceanClipmapSection()
        {
            _showOceanClipmap = WaterEditorUI.Section("Ocean · Clipmap (horizon)", _showOceanClipmap, () =>
            {
                EditorGUILayout.HelpBox(OceanOnlyHelp, MessageType.None);
                DrawFields(
                    "ocean.clipmapGridResolution",
                    "ocean.clipmapOuterRadius",
                    "ocean.oceanDetailFalloff",
                    "ocean.horizonFadeDistance",
                    "ocean.horizonHazeColor",
                    WaterVolumePropertyPaths.HorizonHazeDensity);
            }, contentEnabled: IsOcean);
        }

        void DrawOceanGodRaysSection()
        {
            _showOceanGodRays = WaterEditorUI.Section("Ocean · God Rays", _showOceanGodRays, () =>
                DrawFields(
                    "ocean.largeGodRayColor",
                    WaterVolumePropertyPaths.LargeGodRayDensity,
                    "ocean.largeGodRaySteps",
                    "ocean.largeGodRayAnisotropy",
                    "ocean.largeGodRayExtinction",
                    "ocean.largeGodRayCausticStrength",
                    "ocean.largeGodRayCausticSmooth",
                    "ocean.largeGodRayCausticDepthSoften",
                    "ocean.largeCausticTimeScale",
                    "ocean.largeCausticRippleScale",
                    "ocean.largeCausticRippleStrength"),
                contentEnabled: IsOcean);
        }

        void DrawOceanFoamSection()
        {
            _showOceanFoam = WaterEditorUI.Section("Ocean · Foam (whitecaps)", _showOceanFoam, () =>
                DrawFields(
                    "ocean.oceanFoamWindThreshold",
                    "ocean.oceanFoamCoverage",
                    "ocean.oceanFoamStrength",
                    "ocean.oceanFoamFadeRate",
                    "ocean.oceanFoamColor",
                    "ocean.oceanFoamTileSize",
                    "ocean.oceanFoamFeather",
                    "ocean.oceanFoamDeposit",
                    "ocean.oceanFoamDrift",
                    "ocean.oceanFoamMaxBuildup"),
                contentEnabled: IsOcean);
        }

        const string OceanOnlyHelp = "Ocean-only. Requires Open Water on to take effect.";
        const string SwellHelp =
            "Large Wave Amplitude scales the wind-driven swell (steered by the Wind Waves section). " +
            "Swell Height adds an independent long-period roll on top. Unbounded Ocean extends the " +
            "surface to the horizon (an ocean, not a bounded lake). Edge Feather flattens the wave " +
            "field toward a BOUNDED body's border so the surface never ends as a wall of water.";
    }
}
#endif
