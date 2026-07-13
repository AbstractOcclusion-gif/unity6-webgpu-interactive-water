// Editor guard against silent drift between the two hand-authored copies of the
// open-water swell constants.
//
// WHY: the swell wave field is authored twice - as LBW_* #defines in
// Runtime/Shaders/WaterLargeWaves.hlsl (drives the rendered surface) and as consts
// in Runtime/LargeWaveField.cs (the CPU buoyancy mirror). Nothing links them, so if
// one side is retuned and the other is forgotten, floating objects silently desync
// from the visible crests. This validator reads both source files on editor load and
// reports any drifted constant loudly, replacing the old "remember to edit both files"
// discipline. It is a read-only watcher: it changes no runtime behaviour and no files.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.EditorTools
{
    [InitializeOnLoad]
    internal static class WaterWaveConstantsValidator
    {
        const string HlslAssetName = "WaterLargeWaves";
        const string HlslExtension = ".hlsl";
        const string CSharpAssetName = "LargeWaveField";
        const string CSharpExtension = ".cs";

        // Relative tolerance for a matching value. The constants are authored to a few
        // decimal places; anything closer than this is the same number written two ways.
        const float MatchTolerance = 1e-5f;

        const string LogPrefix = "[WaterWaveConstants] ";
        const string MenuPath = "Window/AbstractOcclusion/Water/Validate Wave Constants";

        // hlslDefine -> csharpConst. Every LBW_* #define in WaterLargeWaves.hlsl that has a
        // const counterpart in LargeWaveField.cs. SwellBaseAmplitude is intentionally absent:
        // its shader side is a positional literal (1.0) in EvaluateLargeBodyWave, not a #define,
        // so there is nothing stable to parse. LbwHash's inline magic numbers are likewise
        // inline literals in both files, not consts, and are out of scope for this guard.
        static readonly (string Hlsl, string CSharp)[] ConstantPairs =
        {
            ("LBW_WAVE_COUNT",               "WaveCount"),
            ("LBW_BASE_WAVELENGTH",          "BaseWavelength"),
            ("LBW_WAVELENGTH_FALLOFF",       "WavelengthFalloff"),
            ("LBW_BASE_AMPLITUDE",           "BaseAmplitude"),
            ("LBW_AMPLITUDE_FALLOFF",        "AmplitudeFalloff"),
            ("LBW_DIR_SPREAD",               "DirectionSpread"),
            ("LBW_CHOP_PHASE_SEED",          "ChopPhaseSeed"),
            ("LBW_SWELL_COUNT",              "SwellCount"),
            ("LBW_SWELL_WAVELENGTH_FALLOFF", "SwellWavelengthFalloff"),
            ("LBW_SWELL_AMPLITUDE_FALLOFF",  "SwellAmplitudeFalloff"),
            ("LBW_SWELL_DIR_SPREAD",         "SwellDirectionSpread"),
            ("LBW_SWELL_PHASE_SEED",         "SwellPhaseSeed"),
            ("LBW_GRAVITY",                  "Gravity"),
            ("LBW_TWO_PI",                   "TwoPi"),
            ("LBW_INVERSION_ITERATIONS",     "InversionIterations"),
        };

        // Captures the numeric literal, tolerating scientific notation and a trailing C# 'f'.
        const string NumberPattern = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";

        static WaterWaveConstantsValidator()
        {
            // Defer past the import/compile pass so the asset database is queryable.
            EditorApplication.delayCall += Validate;
        }

        [MenuItem(MenuPath)]
        static void Validate()
        {
            if (!TryReadPackageAsset(HlslAssetName, HlslExtension, out string hlslSource, out string readError) ||
                !TryReadPackageAsset(CSharpAssetName, CSharpExtension, out string cSharpSource, out readError))
            {
                Debug.LogWarning(LogPrefix + "validation skipped - " + readError);
                return;
            }

            List<string> problems = CollectProblems(hlslSource, cSharpSource);
            if (problems.Count == 0) return;

            Debug.LogError(BuildReport(problems));
        }

        static List<string> CollectProblems(string hlslSource, string cSharpSource)
        {
            var problems = new List<string>();
            foreach ((string hlslName, string cSharpName) in ConstantPairs)
            {
                if (!TryParseHlslDefine(hlslSource, hlslName, out double hlslValue))
                {
                    problems.Add($"{hlslName}: not found in {HlslAssetName}{HlslExtension} (renamed or removed?)");
                    continue;
                }
                if (!TryParseCSharpConst(cSharpSource, cSharpName, out double cSharpValue))
                {
                    problems.Add($"{cSharpName}: not found in {CSharpAssetName}{CSharpExtension} (renamed or removed?)");
                    continue;
                }
                if (!ValuesMatch(hlslValue, cSharpValue))
                {
                    problems.Add($"{hlslName} = {Format(hlslValue)} (hlsl) vs {cSharpName} = {Format(cSharpValue)} (c#)");
                }
            }
            return problems;
        }

        static bool ValuesMatch(double a, double b)
        {
            double scale = System.Math.Max(1.0, System.Math.Abs(a));
            return System.Math.Abs(a - b) <= MatchTolerance * scale;
        }

        static bool TryParseHlslDefine(string source, string name, out double value)
        {
            string pattern = $@"#define\s+{Regex.Escape(name)}\s+{NumberPattern}";
            return TryMatchNumber(source, pattern, out value);
        }

        static bool TryParseCSharpConst(string source, string name, out double value)
        {
            string pattern = $@"const\s+\w+\s+{Regex.Escape(name)}\s*=\s*{NumberPattern}";
            return TryMatchNumber(source, pattern, out value);
        }

        static bool TryMatchNumber(string source, string pattern, out double value)
        {
            value = 0.0;
            Match match = Regex.Match(source, pattern);
            if (!match.Success) return false;
            return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // AssetDatabase paths are project-relative (e.g. "Packages/<id>/Runtime/...") which
        // File.ReadAllText resolves for an embedded package. Matching on the exact filename
        // avoids picking up a similarly named asset from a fuzzy search.
        static bool TryReadPackageAsset(string assetName, string extension, out string source, out string error)
        {
            source = null;
            error = null;
            foreach (string guid in AssetDatabase.FindAssets(assetName))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsExactAsset(path, assetName, extension)) continue;

                try
                {
                    source = File.ReadAllText(path);
                    return true;
                }
                catch (IOException ioException)
                {
                    error = $"could not read {path}: {ioException.Message}";
                    return false;
                }
            }

            error = $"{assetName}{extension} not found in the asset database";
            return false;
        }

        static bool IsExactAsset(string path, string assetName, string extension)
        {
            return path.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileNameWithoutExtension(path) == assetName;
        }

        static string BuildReport(List<string> problems)
        {
            var report = new StringBuilder();
            report.Append(LogPrefix);
            report.AppendLine($"swell constants have drifted between {HlslAssetName}{HlslExtension} and {CSharpAssetName}{CSharpExtension}. " +
                              "The rendered surface and CPU buoyancy will disagree until these match:");
            foreach (string problem in problems)
                report.AppendLine("  - " + problem);
            return report.ToString();
        }

        static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
