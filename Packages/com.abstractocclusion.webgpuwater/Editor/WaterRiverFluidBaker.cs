// WebGpuWater - explicit Edit Mode bake from a 3D river spline and scene colliders.
#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterRiverFluidBaker
    {
        const string BakeRootFolder = "Assets/WebGpuWater";
        const string BakeFolder = BakeRootFolder + "/RiverFluidBakes";
        const string AssetSuffix = " River Fluid Bake.asset";
        const string TextureName = "Packed River Fluid";
        const string ProgressTitle = "Bake River Fluid";
        const string SamplingProgress = "Rasterizing river and obstacle colliders";
        const string SolvingProgress = "Settling obstacle-aware velocity and foam";
        const float HalfWidth = 0.5f;
        const float CellRadiusFraction = 0.45f;
        const float MinimumLength = 0.001f;
        const long MaximumSolveCellIterations = 20_000_000L;

        readonly struct ArcSample
        {
            internal readonly float NormalizedT;
            internal readonly float Distance;
            internal readonly WaterRiverSplineSample SplineSample;

            internal ArcSample(float normalizedT, float distance,
                               WaterRiverSplineSample splineSample)
            {
                NormalizedT = normalizedT;
                Distance = distance;
                SplineSample = splineSample;
            }
        }

        internal static WaterRiverFluidBakeData Bake(WaterRiverFluid fluid)
        {
            if (fluid == null) throw new ArgumentNullException(nameof(fluid));
            WaterRiverSpline spline = fluid.Spline;
            if (spline == null)
                throw new InvalidOperationException(
                    "River Fluid requires a spline assigned on River Surface before baking.");

            try
            {
                EditorUtility.DisplayProgressBar(ProgressTitle, SamplingProgress, 0f);
                ArcSample[] arcSamples = BuildArcSamples(spline, fluid.SamplesPerSegment);
                float riverLength = arcSamples[arcSamples.Length - 1].Distance;
                if (!float.IsFinite(riverLength) || riverLength < MinimumLength)
                    throw new InvalidOperationException(
                        "River spline is too short to bake a fluid field.");

                ValidateSolveWork(fluid);
                BuildSolveInputs(fluid, spline, arcSamples, riverLength,
                                 out bool[] fluidMask, out float[] downstreamSpeed,
                                 out float[] distanceLookup);
                EditorUtility.DisplayProgressBar(ProgressTitle, SolvingProgress, 0.5f);
                WaterRiverFluidSolveResult result = WaterRiverFluidSolver.Solve(
                    fluid.lateralResolution, fluid.longitudinalResolution,
                    fluidMask, downstreamSpeed, fluid.CreateSolveSettings());
                WaterRiverFluidBakeData data = SaveBake(fluid, result, riverLength,
                                                        distanceLookup);
                Undo.RecordObject(fluid, "Assign River Fluid Bake");
                fluid.AssignBakeData(data);
                EditorUtility.SetDirty(fluid);
                return data;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static ArcSample[] BuildArcSamples(WaterRiverSpline spline, int samplesPerSegment)
        {
            int intervalCount = checked(spline.SegmentCount * samplesPerSegment);
            if (intervalCount < 1)
                throw new InvalidOperationException(
                    "River spline requires at least one evaluable segment.");
            var samples = new ArcSample[intervalCount + 1];
            float distance = 0f;
            Vector3 previous = default;
            for (int index = 0; index <= intervalCount; index++)
            {
                float normalizedT = index / (float)intervalCount;
                if (!spline.TryEvaluate(normalizedT, out WaterRiverSplineSample sample))
                    throw new InvalidOperationException(
                        $"River spline evaluation failed at normalized parameter {normalizedT:0.###}.");
                if (index > 0) distance += Vector3.Distance(previous, sample.Position);
                samples[index] = new ArcSample(normalizedT, distance, sample);
                previous = sample.Position;
            }
            return samples;
        }

        static void BuildSolveInputs(WaterRiverFluid fluid, WaterRiverSpline spline,
                                     ArcSample[] arcSamples, float riverLength,
                                     out bool[] fluidMask, out float[] downstreamSpeed,
                                     out float[] distanceLookup)
        {
            int width = fluid.lateralResolution;
            int height = fluid.longitudinalResolution;
            fluidMask = new bool[checked(width * height)];
            downstreamSpeed = new float[height];
            distanceLookup = BuildDistanceLookup(arcSamples, riverLength);
            Physics.SyncTransforms();

            for (int row = 0; row < height; row++)
            {
                float normalizedDistance = (row + HalfWidth) / height;
                float normalizedT = DistanceToParameter(
                    arcSamples, normalizedDistance * riverLength);
                if (!spline.TryEvaluate(normalizedT, out WaterRiverSplineSample sample))
                    throw new InvalidOperationException(
                        $"River spline evaluation failed while rasterizing row {row}.");
                downstreamSpeed[row] = sample.Speed;
                float lateralCellSize = sample.Width / width;
                float longitudinalCellSize = riverLength / height;
                float contactRadius = Mathf.Max(
                    fluid.obstacleContactRadius,
                    Mathf.Min(lateralCellSize, longitudinalCellSize) * CellRadiusFraction);
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    float lateralU = (column + HalfWidth) / width;
                    Vector3 point = sample.Position + sample.Right *
                        ((lateralU - HalfWidth) * sample.Width);
                    bool blocked = Physics.CheckSphere(
                        point, contactRadius, fluid.obstacleLayers,
                        QueryTriggerInteraction.Ignore);
                    fluidMask[index] = !blocked;
                }
            }
        }

        static float[] BuildDistanceLookup(ArcSample[] samples, float riverLength)
        {
            var lookup = new float[samples.Length];
            for (int index = 0; index < samples.Length; index++)
                lookup[index] = samples[index].Distance / riverLength;
            return lookup;
        }

        static void ValidateSolveWork(WaterRiverFluid fluid)
        {
            long cellIterations = checked(
                (long)fluid.lateralResolution * fluid.longitudinalResolution * fluid.iterations);
            if (cellIterations <= MaximumSolveCellIterations) return;
            throw new InvalidOperationException(
                $"River fluid bake requests {cellIterations:N0} cell iterations; the safe editor " +
                $"limit is {MaximumSolveCellIterations:N0}. Reduce resolution or iterations.");
        }

        static float DistanceToParameter(ArcSample[] samples, float distance)
        {
            int upper = 1;
            while (upper < samples.Length && samples[upper].Distance < distance) upper++;
            upper = Mathf.Min(upper, samples.Length - 1);
            int lower = upper - 1;
            float span = samples[upper].Distance - samples[lower].Distance;
            if (span <= MinimumLength) return samples[lower].NormalizedT;
            float fraction = Mathf.Clamp01((distance - samples[lower].Distance) / span);
            return Mathf.Lerp(samples[lower].NormalizedT, samples[upper].NormalizedT, fraction);
        }

        static WaterRiverFluidBakeData SaveBake(WaterRiverFluid fluid,
                                                WaterRiverFluidSolveResult result,
                                                float riverLength,
                                                float[] distanceLookup)
        {
            EnsureAssetFolder();
            WaterRiverFluidBakeData data = fluid.BakeData;
            if (data == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(data)))
            {
                data = ScriptableObject.CreateInstance<WaterRiverFluidBakeData>();
                string fileName = MakeSafeFileName(fluid.gameObject.name) + AssetSuffix;
                string path = AssetDatabase.GenerateUniqueAssetPath(BakeFolder + "/" + fileName);
                AssetDatabase.CreateAsset(data, path);
            }

            Texture2D texture = PrepareTexture(data, result.Width, result.Height);
            texture.SetPixels(Pack(result));
            texture.Apply(false, false);
            data.Configure(texture, riverLength, result.MaximumSpeed, distanceLookup);
            EditorUtility.SetDirty(texture);
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return data;
        }

        static Texture2D PrepareTexture(WaterRiverFluidBakeData data, int width, int height)
        {
            Texture2D texture = data.PackedTexture;
            if (texture != null && texture.width == width && texture.height == height &&
                texture.format == TextureFormat.RGBAHalf)
                return texture;

            if (texture != null && AssetDatabase.IsSubAsset(texture))
                Object.DestroyImmediate(texture, true);
            texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
            {
                name = TextureName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            AssetDatabase.AddObjectToAsset(texture, data);
            return texture;
        }

        static Color[] Pack(WaterRiverFluidSolveResult result)
        {
            var pixels = new Color[result.Velocity.Length];
            float inverseSpeed = 1f / result.MaximumSpeed;
            for (int index = 0; index < pixels.Length; index++)
            {
                Vector2 encoded = result.Velocity[index] * inverseSpeed * HalfWidth +
                                  Vector2.one * HalfWidth;
                pixels[index] = new Color(
                    Mathf.Clamp01(encoded.x), Mathf.Clamp01(encoded.y),
                    Mathf.Clamp01(result.Foam[index]), result.FluidMask[index] ? 1f : 0f);
            }
            return pixels;
        }

        static void EnsureAssetFolder()
        {
            if (!AssetDatabase.IsValidFolder(BakeRootFolder))
                AssetDatabase.CreateFolder("Assets", "WebGpuWater");
            if (!AssetDatabase.IsValidFolder(BakeFolder))
                AssetDatabase.CreateFolder(BakeRootFolder, "RiverFluidBakes");
        }

        static string MakeSafeFileName(string name)
        {
            string safeName = string.IsNullOrWhiteSpace(name) ? nameof(WaterRiverFluid) : name;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalidCharacter, '_');
            return safeName;
        }
    }
}
#endif
