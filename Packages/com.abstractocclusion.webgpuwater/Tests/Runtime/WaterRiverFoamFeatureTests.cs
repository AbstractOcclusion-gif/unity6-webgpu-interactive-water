using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverFluidFeatureTests
    {
        const int Width = 16;
        const int Height = 32;
        const int Iterations = 80;
        const int ObstacleHalfWidth = 1;
        const int ObstacleHalfHeight = 2;
        const int FarReachDivisor = 4;
        const int ShaderOutflowIndex = 0;
        const float DownstreamSpeed = 3f;
        const float MaximumPackedSpeed = 4f;
        const float FloatTolerance = 0.02f;
        const float RiverLength = 12f;
        const float RiverWidth = 6f;
        const float ObstacleFoamInfluenceMeters = 0.5f;
        const float ObstacleFoamTrailLengthMeters = 3f;
        const float SampleLateralU = 0.5f;
        const float SampleNormalizedT = 0.5f;
        const float ShortCascadePersistenceMeters = 0.01f;
        const float LongCascadePersistenceMeters = 100f;
        const float CascadeDropHeight = 6f;
        const float CascadeEndMeters = 6f;
        const float FlatReachEndMeters = 18f;
        const float OverallFoamTestStrength = 0.4f;
        const float MouthFoamPatternSizeMeters = 2.75f;
        const float MouthFoamEdgeFeather = 0.3f;
        const float MouthFoamCoreCut = 0.65f;
        const string HostName = "River Fluid Test";
        const string CascadeReceivingBodyName = "Cascade Foam Receiving Body";
        const string TextureName = "River Fluid Test Texture";

        [Test]
        public void Solve_IsDeterministicFiniteAndPreservesGridDimensions()
        {
            bool[] mask = CreateFluidMask(false);
            float[] speeds = CreateDownstreamSpeeds();
            WaterRiverFluidSolveSettings settings = CreateSettings();

            WaterRiverFluidSolveResult first = WaterRiverFluidSolver.Solve(
                Width, Height, mask, speeds, CreateLateralCellSizes(),
                RiverLength / Height, settings);
            WaterRiverFluidSolveResult second = WaterRiverFluidSolver.Solve(
                Width, Height, mask, speeds, CreateLateralCellSizes(),
                RiverLength / Height, settings);

            Assert.That(first.Width, Is.EqualTo(Width));
            Assert.That(first.Height, Is.EqualTo(Height));
            Assert.That(first.Velocity.Length, Is.EqualTo(Width * Height));
            Assert.That(first.Foam.Length, Is.EqualTo(Width * Height));
            Assert.That(first.FluidMask, Is.EqualTo(mask));
            Assert.That(second.Velocity, Is.EqualTo(first.Velocity));
            Assert.That(second.Foam, Is.EqualTo(first.Foam));
            for (int index = 0; index < first.Velocity.Length; index++)
            {
                Assert.That(float.IsFinite(first.Velocity[index].x), Is.True);
                Assert.That(float.IsFinite(first.Velocity[index].y), Is.True);
                Assert.That(float.IsFinite(first.Foam[index]), Is.True);
                Assert.That(first.Foam[index], Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void Solve_ClearChannelFlowsDownstreamWithoutLateralDrift()
        {
            WaterRiverFluidSolveResult result = WaterRiverFluidSolver.Solve(
                Width, Height, CreateFluidMask(false), CreateDownstreamSpeeds(),
                CreateLateralCellSizes(), RiverLength / Height, CreateSettings());
            Vector2 centre = result.Velocity[CellIndex(Width / 2, Height / 2)];

            Assert.That(Mathf.Abs(centre.x), Is.LessThan(FloatTolerance));
            Assert.That(centre.y, Is.EqualTo(DownstreamSpeed).Within(FloatTolerance));
        }

        [Test]
        public void Solve_DisabledBankFoamLeavesClearChannelFoamFree()
        {
            WaterRiverFluidSolveResult result = WaterRiverFluidSolver.Solve(
                Width, Height, CreateFluidMask(false), CreateDownstreamSpeeds(),
                CreateLateralCellSizes(), RiverLength / Height, CreateSettings());

            float maximumFoam = 0f;
            for (int index = 0; index < result.Foam.Length; index++)
                maximumFoam = Mathf.Max(maximumFoam, result.Foam[index]);

            Assert.That(maximumFoam, Is.Zero.Within(FloatTolerance));
        }

        [Test]
        public void Solve_SolidObstacleStopsItsCellsAndDeflectsSurroundingFlow()
        {
            WaterRiverFluidSolveResult result = WaterRiverFluidSolver.Solve(
                Width, Height, CreateFluidMask(true), CreateDownstreamSpeeds(),
                CreateLateralCellSizes(), RiverLength / Height, CreateSettings());
            int obstacleColumn = Width / 2;
            int obstacleRow = Height / 2;
            Vector2 solidVelocity = result.Velocity[CellIndex(obstacleColumn, obstacleRow)];
            float maximumLateralVelocity = 0f;
            float maximumFoam = 0f;
            for (int row = obstacleRow - ObstacleHalfHeight - 2;
                 row <= obstacleRow + ObstacleHalfHeight + 2; row++)
            {
                for (int column = obstacleColumn - ObstacleHalfWidth - 2;
                     column <= obstacleColumn + ObstacleHalfWidth + 2; column++)
                {
                    int index = CellIndex(column, row);
                    if (!result.FluidMask[index]) continue;
                    maximumLateralVelocity = Mathf.Max(
                        maximumLateralVelocity, Mathf.Abs(result.Velocity[index].x));
                    maximumFoam = Mathf.Max(maximumFoam, result.Foam[index]);
                }
            }

            Assert.That(solidVelocity, Is.EqualTo(Vector2.zero));
            Assert.That(maximumLateralVelocity, Is.GreaterThan(FloatTolerance));
            Assert.That(maximumFoam, Is.GreaterThan(0f));
        }

        [Test]
        public void Solve_ObstacleFoamDriftsDownstreamAndStopsAtTrailLength()
        {
            WaterRiverFluidSolveResult result = WaterRiverFluidSolver.Solve(
                Width, Height, CreateFluidMask(true), CreateDownstreamSpeeds(),
                CreateLateralCellSizes(), RiverLength / Height, CreateSettings());
            int upstreamEndRow = Height / FarReachDivisor;
            int downstreamStartRow = Height - upstreamEndRow;
            int obstacleStartRow = Height / 2 - ObstacleHalfHeight;
            int obstacleEndRow = Height / 2 + ObstacleHalfHeight + 1;
            float longitudinalCellSize = RiverLength / Height;
            int transportedStartRow = obstacleEndRow + Mathf.CeilToInt(
                ObstacleFoamInfluenceMeters / longitudinalCellSize);
            int transportedEndRow = transportedStartRow + FarReachDivisor;
            int trailEndRow = obstacleEndRow + Mathf.CeilToInt(
                ObstacleFoamTrailLengthMeters / longitudinalCellSize) + 1;

            float obstacleFoam = MaximumFoamInRows(
                result, obstacleStartRow, obstacleEndRow);
            float transportedFoam = MaximumFoamInRows(
                result, transportedStartRow, transportedEndRow);
            float farUpstreamFoam = MaximumFoamInRows(result, 0, upstreamEndRow);
            float farDownstreamFoam = MaximumFoamInRows(
                result, Mathf.Max(downstreamStartRow, trailEndRow), Height);

            Assert.That(obstacleFoam, Is.GreaterThan(0f));
            Assert.That(transportedFoam, Is.GreaterThan(0f));
            Assert.That(farUpstreamFoam, Is.Zero.Within(FloatTolerance));
            Assert.That(farDownstreamFoam, Is.Zero.Within(FloatTolerance));
        }

        [Test]
        public void BakeData_DecodesPackedVelocityFoamAndSolidMask()
        {
            Texture2D texture = CreatePackedTexture(
                new Color(0.75f, 1f, 0.6f, 1f));
            WaterRiverFluidBakeData data = CreateBakeData(texture);
            try
            {
                bool sampled = data.TrySample(
                    SampleLateralU, SampleNormalizedT,
                    out Vector2 velocity, out float foam, out float fluidMask);

                Assert.That(sampled, Is.True);
                Assert.That(velocity.x, Is.EqualTo(MaximumPackedSpeed * 0.5f)
                    .Within(FloatTolerance));
                Assert.That(velocity.y, Is.EqualTo(MaximumPackedSpeed)
                    .Within(FloatTolerance));
                Assert.That(foam, Is.EqualTo(0.6f).Within(FloatTolerance));
                Assert.That(fluidMask, Is.EqualTo(1f).Within(FloatTolerance));

                texture.SetPixels(CreateSolidPixels(texture.width * texture.height));
                texture.Apply(false, false);
                Assert.That(data.TrySample(
                    SampleLateralU, SampleNormalizedT, out _, out _, out fluidMask), Is.False);
                Assert.That(fluidMask, Is.EqualTo(0f).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void CurrentField_UsesBakedObstacleDeflectedVelocity()
        {
            GameObject host = CreateStraightSpline(out WaterRiverSpline spline);
            Texture2D texture = CreatePackedTexture(
                new Color(0.75f, 0.75f, 0f, 1f));
            WaterRiverFluidBakeData data = CreateBakeData(texture);
            try
            {
                WaterRiverSurface surface = host.AddComponent<WaterRiverSurface>();
                surface.spline = spline;
                WaterRiverFluid fluid = host.AddComponent<WaterRiverFluid>();
                fluid.AssignBakeData(data);
                WaterRiverCurrentField field = host.AddComponent<WaterRiverCurrentField>();
                field.Configure(spline, fluid);
                spline.TryEvaluate(SampleNormalizedT, out WaterRiverSplineSample sample);

                Assert.That(field.SampleCurrent(sample.Position, out Vector3 velocity), Is.True);
                Vector3 expected = (sample.Right + sample.Tangent) *
                                   (MaximumPackedSpeed * 0.5f);
                Assert.That(velocity.x, Is.EqualTo(expected.x).Within(FloatTolerance));
                Assert.That(velocity.y, Is.EqualTo(expected.y).Within(FloatTolerance));
                Assert.That(velocity.z, Is.EqualTo(expected.z).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FluidAndFoam_PublishPackedBakeAndCleanUpOverlayRegistration()
        {
            GameObject volumeHost = new GameObject("River Fluid Volume Test");
            volumeHost.SetActive(false);
            WaterVolume volume = volumeHost.AddComponent<WaterVolume>();
            GameObject host = CreateStraightSpline(out WaterRiverSpline spline);
            Texture2D texture = CreatePackedTexture(
                new Color(0.5f, 0.75f, 0.5f, 1f));
            WaterRiverFluidBakeData data = CreateBakeData(texture);
            try
            {
                WaterRiverSurface surface = host.AddComponent<WaterRiverSurface>();
                surface.spline = spline;
                surface.waterVolume = volume;
                surface.RequestRebuild();
                WaterRiverFluid fluid = host.AddComponent<WaterRiverFluid>();
                fluid.AssignBakeData(data);
                WaterRiverFoam foam = host.AddComponent<WaterRiverFoam>();
                foam.RequestRebuild();

                var properties = new MaterialPropertyBlock();
                host.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Assert.That(properties.GetFloat(WaterShaderProps.RiverFluidActive), Is.EqualTo(1f));
                Assert.That(properties.GetFloat(WaterShaderProps.RiverFoamActive), Is.EqualTo(1f));
                Assert.That(
                    properties.GetFloat(WaterShaderProps.RiverFoamOverallStrength),
                    Is.EqualTo(1f));
                Assert.That(properties.GetFloat(WaterShaderProps.RiverContactFoamStrength),
                            Is.GreaterThan(0f));
                Assert.That(properties.GetFloat(WaterShaderProps.FoamContactDepth),
                            Is.GreaterThan(0f));
                Assert.That(properties.GetFloat(WaterShaderProps.RiverCascadeFoamStrength),
                            Is.GreaterThan(0f));
                Assert.That(properties.GetFloat(WaterShaderProps.RiverCascadeStartCosine),
                            Is.GreaterThan(
                                properties.GetFloat(WaterShaderProps.RiverCascadeFullCosine)));
                Assert.That(
                    properties.GetFloat(WaterShaderProps.RiverCascadeTransportActive),
                    Is.EqualTo(1f));
                Assert.That(
                    properties.GetFloat(WaterShaderProps.RiverCascadeTransportInverseLength),
                    Is.GreaterThan(0f));
                Assert.That(properties.GetTexture(WaterShaderProps.FoamMask), Is.SameAs(texture));
                Assert.That(volume.HasLiveExternalFoamRenderer, Is.True);

                foam.enabled = false;
                Assert.That(volume.HasLiveExternalFoamRenderer, Is.False);
                fluid.enabled = false;
                host.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Assert.That(properties.GetFloat(WaterShaderProps.RiverFluidActive), Is.EqualTo(0f));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(volumeHost);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void CascadeFoam_PersistsAlongFlatReachAndPublishesTerminalCoverage()
        {
            GameObject host = CreateCascadeThenFlatSpline(out WaterRiverSpline spline);
            var volumeHost = new GameObject(CascadeReceivingBodyName);
            volumeHost.SetActive(false);
            WaterVolume receivingBody = volumeHost.AddComponent<WaterVolume>();
            try
            {
                WaterRiverSurface surface = host.AddComponent<WaterRiverSurface>();
                surface.spline = spline;
                surface.RequestRebuild();
                host.AddComponent<WaterRiverFluid>();
                WaterRiverFoam foam = host.AddComponent<WaterRiverFoam>();

                foam.cascadePersistenceMeters = ShortCascadePersistenceMeters;
                foam.RequestRebuild();
                float shortPersistenceCoverage = foam.TransportedCascadeAtMouth;

                foam.cascadePersistenceMeters = LongCascadePersistenceMeters;
                foam.RequestRebuild();
                float longPersistenceCoverage = foam.TransportedCascadeAtMouth;

                Assert.That(longPersistenceCoverage, Is.GreaterThan(0f));
                Assert.That(
                    longPersistenceCoverage,
                    Is.GreaterThan(shortPersistenceCoverage));

                WaterRiver river = host.AddComponent<WaterRiver>();
                foam.overallStrength = OverallFoamTestStrength;
                foam.patternSize = MouthFoamPatternSizeMeters;
                foam.edgeFeather = MouthFoamEdgeFeather;
                foam.coreCut = MouthFoamCoreCut;
                foam.RequestRebuild();
                river.mouthEnd.body = receivingBody;
                river.RegenerateConnection(WaterRiverEndKind.Mouth);

                Assert.That(
                    river.TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow),
                    Is.True);
                Assert.That(
                    outflow.FoamCoverage,
                    Is.EqualTo(longPersistenceCoverage * OverallFoamTestStrength)
                        .Within(FloatTolerance));
                Assert.That(
                    outflow.FoamLongitudinalMeters,
                    Is.EqualTo(surface.MouthLongitudinalMeters).Within(FloatTolerance));
                Assert.That(
                    Vector3.Dot(outflow.Right, surface.MouthRight.normalized),
                    Is.EqualTo(1f).Within(FloatTolerance));

                var origins = new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
                var directions = new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
                var parameters = new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
                var foamFrames = new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
                var foamAppearances =
                    new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
                outflow.WriteShaderData(
                    origins, directions, parameters, foamFrames, foamAppearances,
                    ShaderOutflowIndex);
                Assert.That(
                    foamFrames[ShaderOutflowIndex].z,
                    Is.EqualTo(surface.MouthLongitudinalMeters).Within(FloatTolerance));
                Assert.That(
                    foamFrames[ShaderOutflowIndex].x,
                    Is.EqualTo(outflow.Right.x).Within(FloatTolerance));
                Assert.That(
                    foamFrames[ShaderOutflowIndex].y,
                    Is.EqualTo(outflow.Right.z).Within(FloatTolerance));
                Assert.That(
                    foamFrames[ShaderOutflowIndex].w,
                    Is.EqualTo(outflow.FoamLateralSpeedMetersPerSecond)
                        .Within(FloatTolerance));
                Assert.That(
                    foamAppearances[ShaderOutflowIndex].x,
                    Is.EqualTo(MouthFoamPatternSizeMeters).Within(FloatTolerance));
                Assert.That(
                    foamAppearances[ShaderOutflowIndex].y,
                    Is.EqualTo(MouthFoamEdgeFeather).Within(FloatTolerance));
                Assert.That(
                    foamAppearances[ShaderOutflowIndex].z,
                    Is.EqualTo(MouthFoamCoreCut).Within(FloatTolerance));
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(volumeHost);
            }
        }

        [Test]
        public void Foam_EnablesProceduralSourcesWithoutFluidBake()
        {
            GameObject volumeHost = new GameObject("River Procedural Foam Volume Test");
            volumeHost.SetActive(false);
            WaterVolume volume = volumeHost.AddComponent<WaterVolume>();
            GameObject host = CreateStraightSpline(out WaterRiverSpline spline);
            try
            {
                WaterRiverSurface surface = host.AddComponent<WaterRiverSurface>();
                surface.spline = spline;
                surface.waterVolume = volume;
                surface.RequestRebuild();
                host.AddComponent<WaterRiverFluid>();
                WaterRiverFoam foam = host.AddComponent<WaterRiverFoam>();
                foam.RequestRebuild();

                var properties = new MaterialPropertyBlock();
                host.GetComponent<MeshRenderer>().GetPropertyBlock(properties);
                Assert.That(properties.GetFloat(WaterShaderProps.RiverFluidActive), Is.EqualTo(0f));
                Assert.That(properties.GetFloat(WaterShaderProps.RiverFoamActive), Is.EqualTo(1f));
                Assert.That(properties.GetFloat(WaterShaderProps.FoamEnabled), Is.EqualTo(1f));
                Assert.That(volume.HasLiveExternalFoamRenderer, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(volumeHost);
            }
        }

        static WaterRiverFluidSolveSettings CreateSettings()
            => new WaterRiverFluidSolveSettings(
                Iterations, 0.1f, 0.08f, 0.2f, 0.5f,
                0.999f, 0.1f, 0.25f, 0.65f,
                ObstacleFoamTrailLengthMeters, 0f);

        static float MaximumFoamInRows(WaterRiverFluidSolveResult result,
                                       int startRow, int endRow)
        {
            float maximumFoam = 0f;
            for (int row = startRow; row < endRow; row++)
            {
                for (int column = 0; column < Width; column++)
                {
                    int index = CellIndex(column, row);
                    if (!result.FluidMask[index]) continue;
                    maximumFoam = Mathf.Max(maximumFoam, result.Foam[index]);
                }
            }
            return maximumFoam;
        }

        static float[] CreateLateralCellSizes()
        {
            var sizes = new float[Height];
            float cellSize = RiverWidth / Width;
            for (int row = 0; row < Height; row++) sizes[row] = cellSize;
            return sizes;
        }

        static bool[] CreateFluidMask(bool addObstacle)
        {
            var mask = new bool[Width * Height];
            for (int row = 0; row < Height; row++)
            {
                for (int column = 0; column < Width; column++)
                    mask[CellIndex(column, row)] = true;
            }
            if (!addObstacle) return mask;

            int centreColumn = Width / 2;
            int centreRow = Height / 2;
            for (int row = centreRow - ObstacleHalfHeight;
                 row <= centreRow + ObstacleHalfHeight; row++)
            {
                for (int column = centreColumn - ObstacleHalfWidth;
                     column <= centreColumn + ObstacleHalfWidth; column++)
                    mask[CellIndex(column, row)] = false;
            }
            return mask;
        }

        static float[] CreateDownstreamSpeeds()
        {
            var speeds = new float[Height];
            for (int row = 0; row < Height; row++) speeds[row] = DownstreamSpeed;
            return speeds;
        }

        static int CellIndex(int column, int row) => row * Width + column;

        static Texture2D CreatePackedTexture(Color value)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true)
            {
                name = TextureName,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color[Width * Height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = value;
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        static Color[] CreateSolidPixels(int count)
        {
            var pixels = new Color[count];
            for (int index = 0; index < count; index++)
                pixels[index] = new Color(0.5f, 0.5f, 0f, 0f);
            return pixels;
        }

        static WaterRiverFluidBakeData CreateBakeData(Texture2D texture)
        {
            var data = ScriptableObject.CreateInstance<WaterRiverFluidBakeData>();
            data.Configure(texture, RiverLength, MaximumPackedSpeed,
                           new[] { 0f, 0.25f, 1f });
            return data;
        }

        static GameObject CreateStraightSpline(out WaterRiverSpline spline)
        {
            var host = new GameObject(HostName);
            spline = host.AddComponent<WaterRiverSpline>();
            Vector3 end = Vector3.forward * RiverLength;
            Vector3 tangent = end * WaterRiverSpline.BezierHandleLengthFraction;
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(Vector3.zero, tangent, RiverWidth, DownstreamSpeed),
                new WaterRiverKnot(end, tangent, RiverWidth, DownstreamSpeed),
            };
            return host;
        }

        static GameObject CreateCascadeThenFlatSpline(out WaterRiverSpline spline)
        {
            var host = new GameObject(HostName);
            spline = host.AddComponent<WaterRiverSpline>();
            Vector3 cascadeEnd = new Vector3(0f, 0f, CascadeEndMeters);
            Vector3 flatReachEnd = new Vector3(0f, 0f, FlatReachEndMeters);
            Vector3 cascadeTangent = cascadeEnd * WaterRiverSpline.BezierHandleLengthFraction;
            Vector3 flatTangent = (flatReachEnd - cascadeEnd) *
                                  WaterRiverSpline.BezierHandleLengthFraction;
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(
                    new Vector3(0f, CascadeDropHeight, 0f),
                    new Vector3(0f, -CascadeDropHeight, CascadeEndMeters) *
                    WaterRiverSpline.BezierHandleLengthFraction,
                    RiverWidth, DownstreamSpeed),
                new WaterRiverKnot(cascadeEnd, cascadeTangent, RiverWidth, DownstreamSpeed),
                new WaterRiverKnot(flatReachEnd, flatTangent, RiverWidth, DownstreamSpeed),
            };
            return host;
        }
    }
}
