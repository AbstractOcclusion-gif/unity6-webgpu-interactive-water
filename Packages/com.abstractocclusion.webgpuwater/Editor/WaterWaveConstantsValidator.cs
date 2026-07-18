// Editor guard against silent drift between the hand-authored copies of the
// open-water swell + surf-front constants.
//
// WHY: the wave fields are authored twice - as LBW_* #defines in
// Runtime/Shaders/WaterLargeWaves.hlsl and SURF_* #defines in WaterSurfWaves.hlsl
// (drive the rendered surface) and as consts in Runtime/LargeWaveField.cs (the CPU
// buoyancy mirror). Nothing links them, so if one side is retuned and the other is
// forgotten, floating objects silently desync from the visible crests. This validator
// reads the source files on editor load and reports any drifted constant loudly,
// replacing the old "remember to edit both files" discipline. It is a read-only
// watcher: it changes no runtime behaviour and no files.
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
        const string LargeWavesHlslAssetName = "WaterLargeWaves";
        const string SurfWavesHlslAssetName = "WaterSurfWaves";
        const string HlslExtension = ".hlsl";
        const string CSharpAssetName = "LargeWaveField";
        const string CSharpExtension = ".cs";
        const string FoamComputeAssetName = "WaterFoamParticles";
        const string ComputeExtension = ".compute";
        const string SplashEmitterAssetName = "WaterSplashEmitter";

        // Relative tolerance for a matching value. The constants are authored to a few
        // decimal places; anything closer than this is the same number written two ways.
        const float MatchTolerance = 1e-5f;

        const string LogPrefix = "[WaterWaveConstants] ";
        const string MenuPath = "Window/AbstractOcclusion/WebGpuWater/Validate Wave Constants";

        // hlslDefine -> csharpConst. Every LBW_* #define in WaterLargeWaves.hlsl that has a
        // const counterpart in LargeWaveField.cs. SwellBaseAmplitude is intentionally absent:
        // its shader side is a positional literal (1.0) in EvaluateLargeBodyWave, not a #define,
        // so there is nothing stable to parse. LbwHash's inline magic numbers are likewise
        // inline literals in both files, not consts, and are out of scope for this guard.
        static readonly (string Hlsl, string CSharp)[] LargeWavesConstantPairs =
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

        // Height-affecting SURF_* #defines in WaterSurfWaves.hlsl mirrored as consts in
        // LargeWaveField.cs (the surf fronts move the surface, so buoyancy mirrors them).
        // Foam/swash-only constants (whitewash shaping, Hunt run-up) have no CPU side and are
        // intentionally absent.
        static readonly (string Hlsl, string CSharp)[] SurfWavesConstantPairs =
        {
            ("SURF_MIN_DEPTH",              "SurfMinDepth"),
            // Master-beat wrap + the two beat-periodic segmentation drifts (BEAT-1: the old
            // single SURF_CREST_SEED_DRIFT split into per-octave drifts, each an exact multiple
            // of 2pi/SURF_BEAT_WRAP_FRONTS). WaterVolume.SurfBeatWrapFronts carries a third copy
            // of the wrap for the clock itself - not parsed here; keep it lockstep by hand.
            ("SURF_BEAT_WRAP_FRONTS",       "SurfBeatWrapFronts"),
            ("SURF_CREST_SEED_DRIFT_A",     "SurfCrestSeedDriftA"),
            ("SURF_CREST_SEED_DRIFT_B",     "SurfCrestSeedDriftB"),
            ("SURF_FACE_FRACTION",          "SurfFaceFraction"),
            ("SURF_BACK_FRACTION",          "SurfBackFraction"),
            ("SURF_SET_WAVES",              "SurfSetWaves"),
            ("SURF_EDGE_BLEND_START",       "SurfEdgeBlendStart"),
            ("SURF_NEAR_FADE",              "SurfNearFade"),
            ("SURF_SECH_ARG_MAX",           "SurfSechArgMax"),
            ("SURF_SLOPE_EPSILON",          "SurfSlopeEpsilon"),
            ("SURF_XI_SPILL_END_LO",        "SurfXiSpillEndLo"),
            ("SURF_XI_SPILL_END_HI",        "SurfXiSpillEndHi"),
            ("SURF_XI_SURGE_START_LO",      "SurfXiSurgeStartLo"),
            ("SURF_XI_SURGE_START_HI",      "SurfXiSurgeStartHi"),
            ("SURF_DEEPWATER_LENGTH_COEF",  "SurfDeepwaterLengthCoef"),
            ("SURF_XI_HEIGHT_EPSILON",      "SurfXiHeightEpsilon"),
            ("SURF_GAMMA_BASE",             "SurfGammaBase"),
            ("SURF_GAMMA_SLOPE_GAIN",       "SurfGammaSlopeGain"),
            ("SURF_GAMMA_MAX",              "SurfGammaMax"),
            ("SURF_BORE_STABLE_GAMMA",      "SurfBoreStableGamma"),
            ("SURF_PLUNGE_FACE_SHARPEN",    "SurfPlungeFaceSharpen"),
        };

        // Splash-burst shaping: authored twice as BURST_* static consts in
        // WaterFoamParticles.compute (the GPU spray path) and as consts in
        // WaterSplashEmitter.cs (the legacy Shuriken fallback). The two paths must keep the
        // same feel or the look silently forks depending on whether a body has a GPU pool.
        static readonly (string Hlsl, string CSharp)[] SplashBurstConstantPairs =
        {
            ("BURST_OUT_JITTER_MIN",    "OutwardJitterMin"),
            ("BURST_OUT_JITTER_MAX",    "OutwardJitterMax"),
            ("BURST_UP_JITTER_MIN",     "UpwardJitterMin"),
            ("BURST_UP_JITTER_MAX",     "UpwardJitterMax"),
            ("BURST_RING_RADIUS_SCALE", "SpawnRingRadiusScale"),
            ("BURST_SPAWN_HEIGHT",      "SpawnHeightAboveSurface"),
        };

        // Captures the numeric literal, tolerating scientific notation and a trailing C# 'f'.
        const string NumberPattern = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";

        static WaterWaveConstantsValidator()
        {
            // Defer past the import/compile pass so the asset database is queryable.
            EditorApplication.delayCall += Validate;
        }

        // Menu entry removed; still runs automatically on script reload (see the static ctor above).
        static void Validate()
        {
            if (!TryReadPackageAsset(LargeWavesHlslAssetName, HlslExtension, out string largeWavesSource, out string readError) ||
                !TryReadPackageAsset(SurfWavesHlslAssetName, HlslExtension, out string surfWavesSource, out readError) ||
                !TryReadPackageAsset(CSharpAssetName, CSharpExtension, out string cSharpSource, out readError) ||
                !TryReadPackageAsset(FoamComputeAssetName, ComputeExtension, out string foamComputeSource, out readError) ||
                !TryReadPackageAsset(SplashEmitterAssetName, CSharpExtension, out string splashEmitterSource, out readError))
            {
                Debug.LogWarning(LogPrefix + "validation skipped - " + readError);
                return;
            }

            var problems = new List<string>();
            CollectProblems(problems, LargeWavesHlslAssetName, HlslExtension, largeWavesSource,
                            CSharpAssetName, cSharpSource, LargeWavesConstantPairs);
            CollectProblems(problems, SurfWavesHlslAssetName, HlslExtension, surfWavesSource,
                            CSharpAssetName, cSharpSource, SurfWavesConstantPairs);
            CollectProblems(problems, FoamComputeAssetName, ComputeExtension, foamComputeSource,
                            SplashEmitterAssetName, splashEmitterSource, SplashBurstConstantPairs);
            if (problems.Count == 0) return;

            Debug.LogError(BuildReport(problems));
        }

        static void CollectProblems(List<string> problems, string hlslAssetName, string hlslExtension,
                                    string hlslSource, string cSharpAssetName, string cSharpSource,
                                    (string Hlsl, string CSharp)[] constantPairs)
        {
            foreach ((string hlslName, string cSharpName) in constantPairs)
            {
                if (!TryParseHlslConstant(hlslSource, hlslName, out double hlslValue))
                {
                    problems.Add($"{hlslName}: not found in {hlslAssetName}{hlslExtension} (renamed or removed?)");
                    continue;
                }
                if (!TryParseCSharpConst(cSharpSource, cSharpName, out double cSharpValue))
                {
                    problems.Add($"{cSharpName}: not found in {cSharpAssetName}{CSharpExtension} (renamed or removed?)");
                    continue;
                }
                if (!ValuesMatch(hlslValue, cSharpValue))
                {
                    problems.Add($"{hlslName} = {Format(hlslValue)} (hlsl) vs {cSharpName} = {Format(cSharpValue)} (c#)");
                }
            }
        }

        static bool ValuesMatch(double a, double b)
        {
            double scale = System.Math.Max(1.0, System.Math.Abs(a));
            return System.Math.Abs(a - b) <= MatchTolerance * scale;
        }

        // HLSL constants are authored either as #defines (the wave headers) or as
        // `static const <type> NAME = value;` (the particle computes) - accept both.
        static bool TryParseHlslConstant(string source, string name, out double value)
        {
            string definePattern = $@"#define\s+{Regex.Escape(name)}\s+{NumberPattern}";
            if (TryMatchNumber(source, definePattern, out value)) return true;
            string staticConstPattern = $@"static\s+const\s+\w+\s+{Regex.Escape(name)}\s*=\s*{NumberPattern}";
            return TryMatchNumber(source, staticConstPattern, out value);
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
            report.AppendLine($"wave constants have drifted between the HLSL sources ({LargeWavesHlslAssetName}/{SurfWavesHlslAssetName}{HlslExtension}) " +
                              $"and {CSharpAssetName}{CSharpExtension}. " +
                              "The rendered surface and CPU buoyancy will disagree until these match:");
            foreach (string problem in problems)
                report.AppendLine("  - " + problem);
            return report.ToString();
        }

        static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
