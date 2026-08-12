// WebGpuWater - the ONE mapping between a WaterVolume and a WaterLookPreset.
//
// Both objects serialize the SAME nested Settings classes under the SAME field names, so
// capture and apply are generic subtree copies via SerializedObject.CopyFromSerializedProperty:
// no per-field code, and a field added to any Settings block is preset-ready automatically.
// The preset's include flags gate APPLY per domain; CAPTURE always stores everything.
// Apply also restores the handful of non-look subfields that ride inside copied blocks
// (topology / budget / project masks), so a preset can never flip a pond into an ocean or drag
// a cost knob along.
#if UNITY_EDITOR
using UnityEditor;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterLookPresetSync
    {
        internal readonly struct LookDomain
        {
            internal readonly string DisplayName;
            internal readonly string IncludeFlagPath; // serialized bool on the preset (inspector toggle)
            internal readonly System.Func<WaterLookPreset, bool> Included;
            internal readonly string[] Paths;

            internal LookDomain(string displayName, string includeFlagPath,
                                System.Func<WaterLookPreset, bool> included,
                                params string[] paths)
            {
                DisplayName = displayName;
                IncludeFlagPath = includeFlagPath;
                Included = included;
                Paths = paths;
            }
        }

        // The six look domains. Paths are identical on WaterVolume and WaterLookPreset - the
        // preset mirrors the volume's field names by design.
        internal static readonly LookDomain[] Domains =
        {
            new LookDomain("Waves", "includeWaves", preset => preset.includeWaves,
                "ocean", "windWaveSettings"),
            new LookDomain("Appearance", "includeAppearance", preset => preset.includeAppearance,
                "jerlovWaterType", "waterFogSettings", "volumeScatterSettings", "depthAttenuation",
                "refractShadows", "refractShadowSoftness"),
            new LookDomain("Surface", "includeSurface", preset => preset.includeSurface,
                "reflectionSettings", "detailNormalSettings",
                "foamPatternTexture", "foamPatternGrid", "foamPatternFps", "foamReliefStrength",
                "oceanWhitecapTexture", "oceanWhitecapGrid", "oceanWhitecapFps"),
            new LookDomain("Foam", "includeFoam", preset => preset.includeFoam,
                "foamSettings"),
            new LookDomain("Underwater", "includeUnderwater", preset => preset.includeUnderwater,
                "underwaterSurfaceSettings"),
            new LookDomain("Ripples", "includeRipples", preset => preset.includeRipples,
                "rippleSettings"),
        };

        // Non-look subfields that live INSIDE copied blocks: the body's own values win on apply.
        // openWater/unboundedOcean are topology, the resolution/steps are budget, the layer mask
        // is project wiring.
        static readonly string[] PreservedOnApply =
        {
            "ocean.openWater",
            "ocean.unboundedOcean",
            "ocean.clipmapGridResolution",
            "ocean.largeGodRaySteps",
            "reflectionSettings.planarExcludeLayers",
            "reflectionSettings.planarResolutionScale",
            "reflectionSettings.planarUpdateInterval",
            "reflectionSettings.planarRenderShadows",
            "reflectionSettings.planarFarClipDistance",
        };

        // Volume -> preset, every domain. The caller commits the preset's SerializedObject.
        internal static void CaptureAll(SerializedObject volume, SerializedObject preset)
        {
            foreach (LookDomain domain in Domains)
                foreach (string path in domain.Paths)
                    CopyPath(volume, preset, path);
        }

        // Preset -> volume, included domains only; returns how many domains were applied. The
        // caller commits the volume's SerializedObject (the inspector's OnInspectorGUI does).
        internal static int ApplyIncluded(SerializedObject preset, SerializedObject volume,
                                          WaterLookPreset asset)
        {
            object[] preserved = SnapshotPreserved(volume);

            int applied = 0;
            foreach (LookDomain domain in Domains)
            {
                if (!domain.Included(asset))
                    continue;
                foreach (string path in domain.Paths)
                    CopyPath(preset, volume, path);
                applied++;
            }

            RestorePreserved(volume, preserved);
            return applied;
        }

        static object[] SnapshotPreserved(SerializedObject volume)
        {
            var values = new object[PreservedOnApply.Length];
            for (int i = 0; i < PreservedOnApply.Length; i++)
                values[i] = Require(volume, PreservedOnApply[i], "WaterVolume").boxedValue;
            return values;
        }

        static void RestorePreserved(SerializedObject volume, object[] values)
        {
            for (int i = 0; i < PreservedOnApply.Length; i++)
                Require(volume, PreservedOnApply[i], "WaterVolume").boxedValue = values[i];
        }

        static void CopyPath(SerializedObject source, SerializedObject target, string path)
        {
            SerializedProperty sourceProperty = Require(source, path, "source");
            Require(target, path, "target"); // fail fast BEFORE the copy if the mirror drifted
            target.CopyFromSerializedProperty(sourceProperty);
        }

        // A rename that reaches only one side must fail loudly at the button press, naming the
        // path and the side - not half-apply silently.
        static SerializedProperty Require(SerializedObject serialized, string path, string side)
        {
            SerializedProperty property = serialized.FindProperty(path);
            if (property == null)
                throw new System.InvalidOperationException(
                    "[WebGpuWater] Look preset: serialized path '" + path + "' not found on " + side +
                    " ('" + serialized.targetObject.GetType().Name + "') - field renamed on one side only?");
            return property;
        }
    }
}
#endif
