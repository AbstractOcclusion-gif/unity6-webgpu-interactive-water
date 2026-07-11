// WebGL Water - WaterVolume inspector: large-water + ocean sections (camera-following sim window,
// open water, horizon clipmap, ocean god rays, whitecap foam). The clipmap/god-ray/foam blocks are
// ocean-only, so they grey out until Open Water is on. Draws serialized properties by exact path.
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
                "Large-Water Sim Window", _showWindow, Prop("enableLargeBodyWindow"), () =>
                DrawFields(
                    "largeBodyThreshold",
                    "simWindowMeters",
                    "clampWindowToShore",
                    "simWindowFocus",
                    "simWindowOffset",
                    "simWindowEdgeFadeTexels"));
        }

        void DrawOceanOpenWaterSection()
        {
            _showOceanOpenWater = WaterEditorUI.SectionWithToggle(
                "Ocean · Open Water", _showOceanOpenWater, Prop("ocean.openWater"), () =>
                DrawFields(
                    "ocean.largeWaveAmplitude",
                    "ocean.largeWaveChoppiness",
                    "ocean.swellHeight",
                    "ocean.swellWavelength",
                    "ocean.unboundedOcean"));
        }

        void DrawOceanClipmapSection()
        {
            _showOceanClipmap = WaterEditorUI.Section("Ocean · Clipmap (horizon)", _showOceanClipmap, () =>
                DrawOceanOnly(() =>
                {
                    EditorGUILayout.HelpBox(OceanOnlyHelp, MessageType.None);
                    DrawFields(
                        "ocean.clipmapRings",
                        "ocean.clipmapSegments",
                        "ocean.clipmapOuterRadius",
                        "ocean.oceanDetailFalloff",
                        "ocean.horizonFadeDistance",
                        "ocean.horizonHazeColor",
                        "ocean.horizonHazeDensity");
                }));
        }

        void DrawOceanGodRaysSection()
        {
            _showOceanGodRays = WaterEditorUI.Section("Ocean · God Rays", _showOceanGodRays, () =>
                DrawOceanOnly(() => DrawFields(
                    "ocean.largeGodRayColor",
                    "ocean.largeGodRayDensity",
                    "ocean.largeGodRaySteps",
                    "ocean.largeGodRayAnisotropy",
                    "ocean.largeGodRayExtinction",
                    "ocean.largeGodRayCausticStrength")));
        }

        void DrawOceanFoamSection()
        {
            _showOceanFoam = WaterEditorUI.Section("Ocean · Foam (whitecaps)", _showOceanFoam, () =>
                DrawOceanOnly(() => DrawFields(
                    "ocean.oceanFoamWindThreshold",
                    "ocean.oceanFoamCoverage",
                    "ocean.oceanFoamStrength",
                    "ocean.oceanFoamFadeRate",
                    "ocean.oceanFoamColor",
                    "ocean.oceanFoamTileSize",
                    "ocean.oceanFoamFeather",
                    "ocean.oceanFoamDeposit",
                    "ocean.oceanFoamDrift",
                    "ocean.oceanFoamMaxBuildup")));
        }

        // Ocean sub-features do nothing on a body that isn't open water; grey them to say so.
        void DrawOceanOnly(System.Action drawContent)
        {
            EditorGUI.BeginDisabledGroup(!Prop("ocean.openWater").boolValue);
            drawContent.Invoke();
            EditorGUI.EndDisabledGroup();
        }

        const string OceanOnlyHelp = "Ocean-only. Requires Open Water (above) to take effect.";
    }
}
#endif
