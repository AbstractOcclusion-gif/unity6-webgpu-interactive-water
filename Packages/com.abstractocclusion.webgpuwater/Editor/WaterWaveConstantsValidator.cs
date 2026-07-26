// Editor guard against silent drift between the hand-authored copies of the
// open-water swell + surf-front constants.
//
// WHY: the wave fields are authored twice - as LBW_* #defines in
// Runtime/Shaders/WaterLargeWaves.hlsl, SURF_* #defines in WaterSurfWaves.hlsl and SHORE_*
// #defines in WaterShore.hlsl (drive the rendered surface) and as consts in Runtime/LargeWaveField.cs (the CPU
// buoyancy mirror). Nothing links them, so if one side is retuned and the other is
// forgotten, floating objects silently desync from the visible crests. This validator
// reads the source files on editor load and reports any drifted constant loudly,
// replacing the old "remember to edit both files" discipline. It is a read-only
// watcher: it changes no runtime behaviour and no files.
//
// AUTHOR-ONLY: this catches OUR editing mistake, and the only person who can act on it is whoever
// edits the package source. It therefore runs solely when the package is EMBEDDED (living in the
// project's Packages/ folder, i.e. the development project) - see IsEmbeddedPackage. A customer
// consuming the package from a registry, a tarball or an Asset Store import never pays the
// multi-file read + ~40 regex passes per domain reload, and can never be shown a console error
// about an internal invariant they cannot fix.
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [InitializeOnLoad]
    internal static class WaterWaveConstantsValidator
    {
        const string LargeWavesHlslAssetName = "WaterLargeWaves";
        const string SurfWavesHlslAssetName = "WaterSurfWaves";
        const string ShoreHlslAssetName = "WaterShoreMath"; // SHORE_* defines moved to the sampler-free header
        const string HlslExtension = ".hlsl";
        const string CSharpAssetName = "LargeWaveField";
        const string CSharpExtension = ".cs";
        const string FoamComputeAssetName = "WaterFoamParticles";
        const string ComputeExtension = ".compute";
        const string SplashEmitterAssetName = "WaterSplashEmitter";
        const string ExclusionHlslAssetName = "WaterExclusion";
        const string ExclusionVolumeAssetName = "WaterExclusionVolume";
        const string PrimitiveShapeHlslAssetName = "WaterPrimitiveShape";

        // Relative tolerance for a matching value. The constants are authored to a few
        // decimal places; anything closer than this is the same number written two ways.
        const float MatchTolerance = 1e-5f;

        const string LogPrefix = "[WaterWaveConstants] ";

        // hlslDefine -> csharpConst. Every LBW_* #define in WaterLargeWaves.hlsl that has a
        // const counterpart in LargeWaveField.cs. SwellBaseAmplitude is intentionally absent:
        // its shader side is a positional literal (1.0) in EvaluateLargeBodyWaveShore, not a #define,
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
            // Formerly-inline mirrored literals, hoisted so drift (like the bore width factor's
            // shader-1.4 / mirror-2.0 incident) is a console error instead of hand-discipline.
            ("SURF_MIN_PERIOD",             "SurfMinPeriod"),
            ("SURF_MIN_WAVELENGTH",         "SurfMinWavelength"),
            ("SURF_MIN_GREENS",             "SurfMinGreens"),
            ("SURF_SETAMP_HASH_PHASE",      "SurfSetAmpHashPhase"),
            ("SURF_SETAMP_FLOOR",           "SurfSetAmpFloor"),
            ("SURF_SETAMP_JITTER_MIN",      "SurfSetAmpJitterMin"),
            // NAME CONTRACT: WaterUnderwaterFog's UNDERWATER_SURF_SETAMP_MAX copy re-points at
            // SURF_SETAMP_JITTER_MAX - renaming it breaks that link, not just this table.
            ("SURF_SETAMP_JITTER_MAX",      "SurfSetAmpJitterMax"),
            ("SURF_WARP_REACH_SPACINGS",    "SurfWarpReachSpacings"),
            ("SURF_CREST_MIN_LENGTH",       "SurfCrestMinLength"),
            ("SURF_CREST_SEED_FRESH_SCALE", "SurfCrestSeedFreshScale"),
            ("SURF_CREST_FRESH_OCTAVE_RATIO", "SurfCrestFreshOctaveRatio"),
            ("SURF_CREST_DIR_A_Z",          "SurfCrestDirAZ"),
            ("SURF_CREST_DIR_B_X",          "SurfCrestDirBX"),
            ("SURF_CREST_FREQ_RATIO",       "SurfCrestFreqRatio"),
            ("SURF_CREST_OCTAVE_B_WEIGHT",  "SurfCrestOctaveBWeight"),
            ("SURF_CREST_NOISE_NORM",       "SurfCrestNoiseNorm"),
            ("SURF_EXPOSURE_FACING_LO",     "SurfExposureFacingLo"),
            ("SURF_EXPOSURE_FACING_HI",     "SurfExposureFacingHi"),
            ("SURF_XI_LENGTH_EPSILON",      "SurfXiLengthEpsilon"),
            ("SURF_GREEN_EXPONENT",         "SurfGreenExponent"),
            ("SURF_CAP_EPSILON",            "SurfCapEpsilon"),
            ("SURF_CRESTING_START",         "SurfCrestingStart"),
            ("SURF_CRESTING_END",           "SurfCrestingEnd"),
            ("SURF_BROKEN_START",           "SurfBrokenStart"),
            ("SURF_BROKEN_END",             "SurfBrokenEnd"),
            ("SURF_LEAN_REACH_FRACTION",    "SurfLeanReachFraction"),
            ("SURF_BORE_WIDTH_FACTOR",      "SurfBoreWidthFactor"),
            ("SURF_MIN_INFLUENCE",          "SurfMinInfluence"),
            ("SURF_MIN_BAND_DEPTH",         "SurfMinBandDepth"),
            ("SURF_WET_FADE_LO",            "SurfWetFadeLo"),
            ("SURF_WET_FADE_HI",            "SurfWetFadeHi"),
        };

        // SHORE_* #defines in WaterShore.hlsl (the Layer A shoal transform) mirrored as consts in
        // LargeWaveField.cs: ShoalWeight/GreenGain/WarpExtra move the CPU buoyancy surface exactly
        // like the shader's ShoalWeight/ShoreGreenGain/ShoreWarpExtra move the render.
        // SHORE_DEEP_SENTINEL / SHORE_BORDER_FEATHER are field-sampling plumbing with no C# math
        // twin (the CPU samples via WaterShoreDepthField), so they are intentionally absent.
        static readonly (string Hlsl, string CSharp)[] ShoreConstantPairs =
        {
            ("SHORE_SHOAL_WAVELENGTH_FACTOR", "ShoreShoalWavelengthFactor"),
            ("SHORE_WAVELENGTH_EPSILON",      "ShoreWavelengthEpsilon"),
            ("SHORE_BAND_EPSILON",            "ShoreBandEpsilon"),
            ("SHORE_BAND_INNER_FRACTION",     "ShoreBandInnerFraction"),
            ("SHORE_GREEN_MIN_DEPTH",         "ShoreGreenMinDepth"),
            ("SHORE_GREEN_EXPONENT",          "ShoreGreenExponent"),
            ("SHORE_WARP_REACH_MIN",          "ShoreWarpReachMin"),
            ("SHORE_MIN_GREENS",              "ShoreMinGreens"),
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

        // The exclusion-volume cap is authored twice: EXCLUSION_MAX_VOLUMES sizes the shader's
        // uniform array (WaterExclusion.hlsl) and WaterExclusionVolume.MaxVolumes sizes the C#
        // publish buffer. A drift would truncate or over-read the array silently.
        static readonly (string Hlsl, string CSharp)[] ExclusionConstantPairs =
        {
            ("EXCLUSION_MAX_VOLUMES", "MaxVolumes"),
            ("EXCLUSION_SHAPE_MESH", "MeshShapeId"),
        };

        // The shape selector is authored twice as well: PRIMITIVE_SHAPE_SPHERE picks the sphere
        // kernels in the shader (WaterPrimitiveShape.hlsl) and WaterExclusionVolume.Shape.Sphere's
        // ORDINAL is what the publisher sends as that selector. A drift would silently carve every
        // sphere volume as a box - visible, but with no error to point at it.
        static readonly (string Hlsl, string CSharp)[] PrimitiveShapeConstantPairs =
        {
            ("PRIMITIVE_SHAPE_SPHERE", "SphereShapeId"),
        };

        // Captures the numeric literal, tolerating scientific notation and a trailing C# 'f'.
        const string NumberPattern = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";

        // Package-relative folder every validated source lives under. Scoping the asset search to it
        // is what stops a consumer's own LargeWaveField.cs / WaterExclusionVolume.cs (same filename,
        // different file) from being parsed instead of ours and reported as drift.
        const string PackageSourceFolder = "/Runtime";

        static WaterWaveConstantsValidator()
        {
            if (!IsEmbeddedPackage()) return; // author-only; see the file header
            // Defer past the import/compile pass so the asset database is queryable.
            EditorApplication.delayCall += Validate;
        }

        // True only in the package's own development project. FindForAssembly returns null when the
        // package was imported into Assets/ (the Asset Store path), and a non-Embedded source for a
        // registry/tarball install - neither of which is us.
        static bool IsEmbeddedPackage()
        {
            PackageInfo package = PackageInfo.FindForAssembly(
                typeof(WaterWaveConstantsValidator).Assembly);
            return package != null && package.source == PackageSource.Embedded;
        }

        // Project-relative search root for FindAssets, e.g. "Packages/com.abstract.../Runtime".
        static string SearchFolder()
        {
            PackageInfo package = PackageInfo.FindForAssembly(
                typeof(WaterWaveConstantsValidator).Assembly);
            return package == null ? null : package.assetPath + PackageSourceFolder;
        }

        // Runs automatically on script reload (see the static ctor above).
        static void Validate()
        {
            if (!TryReadPackageAsset(LargeWavesHlslAssetName, HlslExtension, out string largeWavesSource, out string readError) ||
                !TryReadPackageAsset(SurfWavesHlslAssetName, HlslExtension, out string surfWavesSource, out readError) ||
                !TryReadPackageAsset(ShoreHlslAssetName, HlslExtension, out string shoreSource, out readError) ||
                !TryReadPackageAsset(CSharpAssetName, CSharpExtension, out string cSharpSource, out readError) ||
                !TryReadPackageAsset(FoamComputeAssetName, ComputeExtension, out string foamComputeSource, out readError) ||
                !TryReadPackageAsset(SplashEmitterAssetName, CSharpExtension, out string splashEmitterSource, out readError) ||
                !TryReadPackageAsset(ExclusionHlslAssetName, HlslExtension, out string exclusionHlslSource, out readError) ||
                !TryReadPackageAsset(ExclusionVolumeAssetName, CSharpExtension, out string exclusionVolumeSource, out readError) ||
                !TryReadPackageAsset(PrimitiveShapeHlslAssetName, HlslExtension, out string primitiveShapeSource, out readError))
            {
                Debug.LogWarning(LogPrefix + "validation skipped - " + readError);
                return;
            }

            var problems = new List<string>();
            CollectProblems(problems, LargeWavesHlslAssetName, HlslExtension, largeWavesSource,
                            CSharpAssetName, cSharpSource, LargeWavesConstantPairs);
            CollectProblems(problems, SurfWavesHlslAssetName, HlslExtension, surfWavesSource,
                            CSharpAssetName, cSharpSource, SurfWavesConstantPairs);
            CollectProblems(problems, ShoreHlslAssetName, HlslExtension, shoreSource,
                            CSharpAssetName, cSharpSource, ShoreConstantPairs);
            CollectProblems(problems, FoamComputeAssetName, ComputeExtension, foamComputeSource,
                            SplashEmitterAssetName, splashEmitterSource, SplashBurstConstantPairs);
            CollectProblems(problems, ExclusionHlslAssetName, HlslExtension, exclusionHlslSource,
                            ExclusionVolumeAssetName, exclusionVolumeSource, ExclusionConstantPairs);
            CollectProblems(problems, PrimitiveShapeHlslAssetName, HlslExtension, primitiveShapeSource,
                            ExclusionVolumeAssetName, exclusionVolumeSource, PrimitiveShapeConstantPairs);
            if (problems.Count == 0) return;

            // Warning, not error: drift is a real authoring bug but never blocks the editor, and a
            // red error trains you to ignore the console. This only ever fires in the dev project.
            Debug.LogWarning(BuildReport(problems));
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
        // File.ReadAllText resolves for an embedded package. The search is SCOPED to this package's
        // Runtime folder and then matched on the exact filename: an unscoped FindAssets would happily
        // return a consumer's own file of the same name and report phantom drift against it.
        static bool TryReadPackageAsset(string assetName, string extension, out string source, out string error)
        {
            source = null;
            error = null;

            string searchFolder = SearchFolder();
            if (searchFolder == null)
            {
                error = "could not resolve the package location";
                return false;
            }

            foreach (string guid in AssetDatabase.FindAssets(assetName, new[] { searchFolder }))
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
            report.AppendLine($"wave constants have drifted between the HLSL sources ({LargeWavesHlslAssetName}/{SurfWavesHlslAssetName}/{ShoreHlslAssetName}{HlslExtension}) " +
                              $"and {CSharpAssetName}{CSharpExtension}. " +
                              "The rendered surface and CPU buoyancy will disagree until these match:");
            foreach (string problem in problems)
                report.AppendLine("  - " + problem);
            return report.ToString();
        }

        static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
