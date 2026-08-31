// WebGpuWater - deterministic KWS1-style settled fluid solve in ribbon texture space.
// The authored spline current is the BASE FLOW and passes through the solve untouched:
// advection, viscosity, forcing, the deadband and the pressure projection all act on the
// PERTURBATION (velocity minus base flow). Solving the full field instead dragged slow
// upstream water into fast reaches and let the projection erase the authored row-to-row
// speed profile, so every varying-knot bake settled below its spline speeds - the knots
// are the source of truth; the solve only adds obstacle deflection, wakes and foam.
using System;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal readonly struct WaterRiverFluidSolveSettings
    {
        internal readonly int Iterations;
        internal readonly float DeltaTime;
        internal readonly float Viscosity;
        internal readonly float Pressure;
        internal readonly float Force;
        internal readonly float VelocityDecay;
        internal readonly float Vorticity;
        internal readonly float FoamThreshold;
        internal readonly float FoamStrength;
        internal readonly float ObstacleFoamTrailLengthMeters;
        internal readonly float BankFoamStrength;

        internal WaterRiverFluidSolveSettings(
            int iterations, float deltaTime, float viscosity, float pressure,
            float force, float velocityDecay, float vorticity,
            float foamThreshold, float foamStrength,
            float obstacleFoamTrailLengthMeters, float bankFoamStrength)
        {
            Iterations = iterations;
            DeltaTime = deltaTime;
            Viscosity = viscosity;
            Pressure = pressure;
            Force = force;
            VelocityDecay = velocityDecay;
            Vorticity = vorticity;
            FoamThreshold = foamThreshold;
            FoamStrength = foamStrength;
            ObstacleFoamTrailLengthMeters = obstacleFoamTrailLengthMeters;
            BankFoamStrength = bankFoamStrength;
        }
    }

    internal readonly struct WaterRiverFluidSolveResult
    {
        internal readonly Vector2[] Velocity;
        internal readonly float[] Foam;
        internal readonly bool[] FluidMask;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly float MaximumSpeed;

        internal WaterRiverFluidSolveResult(Vector2[] velocity, float[] foam, bool[] fluidMask,
                                            int width, int height, float maximumSpeed)
        {
            Velocity = velocity;
            Foam = foam;
            FluidMask = fluidMask;
            Width = width;
            Height = height;
            MaximumSpeed = maximumSpeed;
        }
    }

    internal static class WaterRiverFluidSolver
    {
        internal const int MinimumResolution = 8;
        internal const int MinimumIterations = 1;
        const float MinimumPositiveValue = 0.0001f;
        const float MinimumDensity = 0.5f;
        // Density is a soft pressure surrogate; without a ceiling it accumulated without
        // bound upstream of solid cells and the density-gradient force fed back into the
        // velocity until the solve overflowed to NaN. The ceiling keeps stalls bounded.
        const float MaximumDensity = 2f;
        const float VelocityDeadband = 0.0001f;
        const int PressureIterations = 12;
        // Swirl adds foam where eddies spin even when net shear is small (behind obstacles).
        const float VorticityFoamWeight = 0.5f;
        // Converging flow packs surface foam together (hydraulic-jump style accumulation).
        const float ConvergenceFoamWeight = 1f;
        // Shear sampled this many cells apart (KWS1's _FoamTexelOffset idea): a multi-cell
        // radius reads the velocity CONTRAST across an obstacle's whole shadow instead of a
        // one-cell ring, so the foam response is a soft band rather than a thin halo.
        const int FoamShearRadiusCells = 2;
        const float MaximumFoamTransportLateralCellsPerRow = 2f;
        // Obstacle-driven foam is generated only inside this physical neighbourhood. The
        // projected velocity remains global for current motion, but its long pressure wake
        // must not become a fresh foam source at every downstream cell.
        const float ObstacleFoamInfluenceMeters = 0.5f;

        struct Cell
        {
            internal Vector2 Velocity;
            internal float Density;
            internal float Vorticity;
        }

        internal static WaterRiverFluidSolveResult Solve(
            int width, int height, bool[] fluidMask, float[] downstreamSpeed,
            float[] lateralCellSize, float longitudinalCellSize,
            WaterRiverFluidSolveSettings settings)
        {
            Validate(width, height, fluidMask, downstreamSpeed, lateralCellSize,
                     longitudinalCellSize, settings);
            int count = checked(width * height);
            var source = new Cell[count];
            var target = new Cell[count];
            var foam = new float[count];
            var divergence = new float[count];
            var pressure = new float[count];
            var pressureTarget = new float[count];
            float[] obstacleFoamInfluence = BuildObstacleFoamInfluence(
                fluidMask, width, height, lateralCellSize, longitudinalCellSize);
            bool[] bowFaceMask = BuildBowFaceMask(fluidMask, width, height,
                                                  lateralCellSize, longitudinalCellSize);
            Initialize(source, width, height, fluidMask, downstreamSpeed);

            for (int iteration = 0; iteration < settings.Iterations; iteration++)
            {
                Step(source, target, width, height, fluidMask, downstreamSpeed,
                     lateralCellSize, longitudinalCellSize, settings,
                     divergence, pressure, pressureTarget);
                (source, target) = (target, source);
            }

            GenerateFoamSnapshot(
                source, foam, width, height, fluidMask, obstacleFoamInfluence,
                bowFaceMask, downstreamSpeed, lateralCellSize,
                longitudinalCellSize, settings);

            var velocity = new Vector2[count];
            float maximumSpeed = MinimumPositiveValue;
            for (int index = 0; index < count; index++)
            {
                velocity[index] = fluidMask[index] ? source[index].Velocity : Vector2.zero;
                maximumSpeed = Mathf.Max(maximumSpeed, velocity[index].magnitude);
            }
            return new WaterRiverFluidSolveResult(
                velocity, foam, (bool[])fluidMask.Clone(), width, height, maximumSpeed);
        }

        static void Initialize(Cell[] cells, int width, int height,
                               bool[] fluidMask, float[] downstreamSpeed)
        {
            for (int row = 0; row < height; row++)
            {
                float speed = downstreamSpeed[row];
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index]) continue;
                    cells[index].Velocity = new Vector2(0f, speed);
                    cells[index].Density = MinimumDensity;
                }
            }
        }

        static void Step(Cell[] source, Cell[] target, int width, int height,
                         bool[] fluidMask, float[] downstreamSpeed,
                         float[] lateralCellSize, float longitudinalCellSize,
                         WaterRiverFluidSolveSettings settings, float[] divergence,
                         float[] pressure, float[] pressureTarget)
        {
            for (int row = 0; row < height; row++)
            {
                float lateralStep = lateralCellSize[row];
                float inverseLateralStep = 1f / lateralStep;
                float inverseLongitudinalStep = 1f / longitudinalCellSize;
                Vector2 desiredFlow = new Vector2(0f, downstreamSpeed[row]);
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index])
                    {
                        target[index] = default;
                        continue;
                    }

                    Cell centre = source[index];
                    Cell left = ReadNeighbour(
                        source, fluidMask, width, height, column - 1, row, centre);
                    Cell right = ReadNeighbour(
                        source, fluidMask, width, height, column + 1, row, centre);
                    Cell down = ReadNeighbour(
                        source, fluidMask, width, height, column, row - 1, centre);
                    Cell up = ReadNeighbour(
                        source, fluidMask, width, height, column, row + 1, centre);
                    Vector2 densityGradient = new Vector2(
                        (right.Density - left.Density) * 0.5f,
                        (up.Density - down.Density) * 0.5f);
                    // Viscosity diffuses the PERTURBATION only: the base flow's row-to-row
                    // change is an authored speed profile, not shear to be smoothed away.
                    Vector2 centrePerturbation = centre.Velocity - desiredFlow;
                    Vector2 perturbationLeft = ReadPerturbation(
                        source, fluidMask, downstreamSpeed, width, height,
                        column - 1, row, centrePerturbation);
                    Vector2 perturbationRight = ReadPerturbation(
                        source, fluidMask, downstreamSpeed, width, height,
                        column + 1, row, centrePerturbation);
                    Vector2 perturbationDown = ReadPerturbation(
                        source, fluidMask, downstreamSpeed, width, height,
                        column, row - 1, centrePerturbation);
                    Vector2 perturbationUp = ReadPerturbation(
                        source, fluidMask, downstreamSpeed, width, height,
                        column, row + 1, centrePerturbation);
                    Vector2 laplacian = perturbationLeft + perturbationRight +
                                        perturbationDown + perturbationUp -
                                        centrePerturbation * 4f;

                    Vector2 gridVelocity = new Vector2(
                        centre.Velocity.x * inverseLateralStep,
                        centre.Velocity.y * inverseLongitudinalStep);
                    Vector2 backtrace = new Vector2(column, row) -
                                        gridVelocity * settings.DeltaTime;
                    Cell advected = Sample(source, fluidMask, width, height, backtrace);
                    // Advect the perturbation, not the full field: subtract the base flow
                    // AT THE SAMPLED ROW and re-anchor on this row's authored speed, so a
                    // fast reach never inherits its slower upstream neighbour's base speed.
                    Vector2 advectedPerturbation = advected.Velocity - new Vector2(
                        0f, SampleDownstreamSpeed(downstreamSpeed, height, backtrace.y));
                    Vector2 velocity = desiredFlow + advectedPerturbation +
                        settings.DeltaTime *
                        (settings.Viscosity * laplacian -
                         settings.Pressure * densityGradient -
                         settings.Force * advectedPerturbation);

                    float curl = (right.Velocity.y - left.Velocity.y -
                                  up.Velocity.x + down.Velocity.x) * 0.5f;
                    Vector2 curlGradient = new Vector2(
                        Mathf.Abs(up.Vorticity) - Mathf.Abs(down.Vorticity),
                        Mathf.Abs(left.Vorticity) - Mathf.Abs(right.Vorticity));
                    if (curlGradient.sqrMagnitude > MinimumPositiveValue)
                        velocity += curlGradient.normalized * curl * settings.Vorticity;

                    // Decay removes perturbation energy, not the authored river current. Damping
                    // the full velocity made every clear bake settle below its spline speed and
                    // desynchronised baked foam/current transport from the analytic river motion.
                    velocity = desiredFlow +
                               (velocity - desiredFlow) * settings.VelocityDecay;
                    velocity = ApplySolidBoundary(
                        velocity, fluidMask, width, height, column, row);
                    // The deadband stills tiny PERTURBATIONS so an undisturbed reach stays
                    // exactly on its authored speed (zeroing tiny full velocities instead
                    // would freeze a slow river outright).
                    if ((velocity - desiredFlow).magnitude < VelocityDeadband)
                        velocity = desiredFlow;

                    float velocityDivergence = (right.Velocity.x - left.Velocity.x +
                                                up.Velocity.y - down.Velocity.y) * 0.5f;
                    target[index] = new Cell
                    {
                        Velocity = velocity,
                        Density = Mathf.Clamp(
                            advected.Density - settings.DeltaTime *
                            Vector2.Dot(densityGradient, centre.Velocity) -
                            velocityDivergence * 0.1f,
                            MinimumDensity, MaximumDensity),
                        Vorticity = curl,
                    };
                }
            }

            ProjectVelocity(target, width, height, fluidMask, downstreamSpeed,
                            lateralCellSize, longitudinalCellSize,
                            divergence, pressure, pressureTarget);
            UpdateVorticity(target, width, height, fluidMask);
        }

        // Projection removes divergence from the PERTURBATION, in ribbon metres. Projecting
        // the full field treated the authored row-to-row speed profile as divergence to
        // erase, and the old unit-cell stencil ignored that a lateral cell is a different
        // physical size from a longitudinal one (and varies per row), which invented
        // lateral flow wherever the width or speed changed.
        static void ProjectVelocity(Cell[] cells, int width, int height, bool[] fluidMask,
                                    float[] downstreamSpeed, float[] lateralCellSize,
                                    float longitudinalCellSize,
                                    float[] divergence, float[] pressure,
                                    float[] pressureTarget)
        {
            Array.Clear(pressure, 0, pressure.Length);
            Array.Clear(pressureTarget, 0, pressureTarget.Length);
            float inverseLongitudinal = 1f / longitudinalCellSize;
            float longitudinalWeight = inverseLongitudinal * inverseLongitudinal;
            for (int row = 0; row < height; row++)
            {
                float inverseLateral = 1f / lateralCellSize[row];
                Vector2 desiredFlow = new Vector2(0f, downstreamSpeed[row]);
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index])
                    {
                        divergence[index] = 0f;
                        continue;
                    }

                    Vector2 centrePerturbation = cells[index].Velocity - desiredFlow;
                    Vector2 left = ReadPerturbation(
                        cells, fluidMask, downstreamSpeed, width, height,
                        column - 1, row, centrePerturbation);
                    Vector2 right = ReadPerturbation(
                        cells, fluidMask, downstreamSpeed, width, height,
                        column + 1, row, centrePerturbation);
                    Vector2 down = ReadPerturbation(
                        cells, fluidMask, downstreamSpeed, width, height,
                        column, row - 1, centrePerturbation);
                    Vector2 up = ReadPerturbation(
                        cells, fluidMask, downstreamSpeed, width, height,
                        column, row + 1, centrePerturbation);
                    divergence[index] =
                        (right.x - left.x) * 0.5f * inverseLateral +
                        (up.y - down.y) * 0.5f * inverseLongitudinal;
                }
            }

            for (int iteration = 0; iteration < PressureIterations; iteration++)
            {
                for (int row = 0; row < height; row++)
                {
                    float inverseLateral = 1f / lateralCellSize[row];
                    float lateralWeight = inverseLateral * inverseLateral;
                    // Row-to-row lateral-size change bends the grid slightly; the stencil
                    // uses each row's own weights, which is exact for the lateral pair and
                    // a first-order approximation across rows.
                    float inverseStencilSum = 1f / (2f * (lateralWeight + longitudinalWeight));
                    for (int column = 0; column < width; column++)
                    {
                        int index = row * width + column;
                        if (!fluidMask[index])
                        {
                            pressureTarget[index] = 0f;
                            continue;
                        }

                        float centre = pressure[index];
                        float left = ReadPressure(pressure, fluidMask, width, height,
                                                  column - 1, row, centre);
                        float right = ReadPressure(pressure, fluidMask, width, height,
                                                   column + 1, row, centre);
                        float down = ReadPressure(pressure, fluidMask, width, height,
                                                  column, row - 1, centre);
                        float up = ReadPressure(pressure, fluidMask, width, height,
                                                column, row + 1, centre);
                        pressureTarget[index] =
                            (lateralWeight * (left + right) +
                             longitudinalWeight * (down + up) -
                             divergence[index]) * inverseStencilSum;
                    }
                }
                (pressure, pressureTarget) = (pressureTarget, pressure);
            }

            for (int row = 0; row < height; row++)
            {
                float inverseLateral = 1f / lateralCellSize[row];
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index]) continue;
                    float centre = pressure[index];
                    float left = ReadPressure(pressure, fluidMask, width, height,
                                              column - 1, row, centre);
                    float right = ReadPressure(pressure, fluidMask, width, height,
                                               column + 1, row, centre);
                    float down = ReadPressure(pressure, fluidMask, width, height,
                                              column, row - 1, centre);
                    float up = ReadPressure(pressure, fluidMask, width, height,
                                            column, row + 1, centre);
                    Vector2 velocity = cells[index].Velocity - new Vector2(
                        (right - left) * 0.5f * inverseLateral,
                        (up - down) * 0.5f * inverseLongitudinal);
                    cells[index].Velocity = ApplySolidBoundary(
                        velocity, fluidMask, width, height, column, row);
                }
            }
        }

        static void UpdateVorticity(Cell[] cells, int width, int height, bool[] fluidMask)
        {
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index]) continue;
                    Vector2 centre = cells[index].Velocity;
                    Vector2 left = ReadVelocity(
                        cells, fluidMask, width, height, column - 1, row, centre);
                    Vector2 right = ReadVelocity(
                        cells, fluidMask, width, height, column + 1, row, centre);
                    Vector2 down = ReadVelocity(
                        cells, fluidMask, width, height, column, row - 1, centre);
                    Vector2 up = ReadVelocity(
                        cells, fluidMask, width, height, column, row + 1, centre);
                    cells[index].Vorticity =
                        (right.y - left.y - up.x + down.x) * 0.5f;
                }
            }
        }

        static Vector2 ReadVelocity(Cell[] cells, bool[] mask, int width, int height,
                                    int column, int row, Vector2 boundaryFallback)
        {
            if (column < 0 || column >= width || row < 0 || row >= height)
                return boundaryFallback;
            int index = row * width + column;
            return mask[index] ? cells[index].Velocity : Vector2.zero;
        }

        // Perturbation seen at a neighbour: a solid cell's real velocity is zero, so it
        // carries the full counter-flow against its row's base; grid edges mirror the
        // centre so open inlet/outlet rows and banks stay flux-free.
        static Vector2 ReadPerturbation(Cell[] cells, bool[] mask, float[] downstreamSpeed,
                                        int width, int height, int column, int row,
                                        Vector2 centrePerturbation)
        {
            if (column < 0 || column >= width || row < 0 || row >= height)
                return centrePerturbation;
            int index = row * width + column;
            Vector2 velocity = mask[index] ? cells[index].Velocity : Vector2.zero;
            return velocity - new Vector2(0f, downstreamSpeed[row]);
        }

        // Base-flow speed at a fractional backtrace row, clamped exactly like Sample's
        // bilinear so (velocity - base) advects as a pure perturbation at the grid ends too.
        static float SampleDownstreamSpeed(float[] downstreamSpeed, int height, float position)
        {
            float y = Mathf.Clamp(position, 0f, height - 1f);
            int lower = Mathf.FloorToInt(y);
            int upper = Mathf.Min(lower + 1, height - 1);
            return Mathf.Lerp(downstreamSpeed[lower], downstreamSpeed[upper], y - lower);
        }

        static float ReadPressure(float[] pressure, bool[] mask, int width, int height,
                                  int column, int row, float solidFallback)
        {
            if (column < 0 || column >= width || row < 0 || row >= height)
                return solidFallback;
            if (!mask[row * width + column]) return solidFallback;
            return pressure[row * width + column];
        }

        static Vector2 ApplySolidBoundary(Vector2 velocity, bool[] mask, int width, int height,
                                          int column, int row)
        {
            if (!IsFluid(mask, width, height, column - 1, row) && velocity.x < 0f) velocity.x = 0f;
            if (!IsFluid(mask, width, height, column + 1, row) && velocity.x > 0f) velocity.x = 0f;
            if (row > 0 && !IsFluid(mask, width, height, column, row - 1) && velocity.y < 0f)
                velocity.y = 0f;
            if (row < height - 1 && !IsFluid(mask, width, height, column, row + 1) &&
                velocity.y > 0f)
                velocity.y = 0f;
            return velocity;
        }

        // Velocity iterations are convergence work, not elapsed foam time. Foam is therefore
        // extracted once from the final settled field; carrying foam inside Step repeatedly
        // injected the same obstacle source and painted a steady band to the river outlet.
        static void GenerateFoamSnapshot(
            Cell[] cells, float[] foam, int width, int height, bool[] mask,
            float[] obstacleInfluence, bool[] bowFaceMask, float[] downstreamSpeed,
            float[] lateralCellSize, float longitudinalCellSize,
            WaterRiverFluidSolveSettings settings)
        {
            var obstacleSources = new float[foam.Length];
            for (int row = 0; row < height; row++)
            {
                float lateralStep = lateralCellSize[row];
                float inverseLateralStep = 1f / lateralStep;
                float inverseLongitudinalStep = 1f / longitudinalCellSize;
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!mask[index])
                    {
                        foam[index] = 0f;
                        continue;
                    }

                    Cell centre = cells[index];
                    Cell left = ReadNeighbour(
                        cells, mask, width, height, column - 1, row, centre);
                    Cell right = ReadNeighbour(
                        cells, mask, width, height, column + 1, row, centre);
                    Cell down = ReadNeighbour(
                        cells, mask, width, height, column, row - 1, centre);
                    Cell up = ReadNeighbour(
                        cells, mask, width, height, column, row + 1, centre);
                    float curl =
                        (right.Velocity.y - left.Velocity.y) *
                            0.5f * inverseLateralStep -
                        (up.Velocity.x - down.Velocity.x) *
                            0.5f * inverseLongitudinalStep;
                    float velocityDivergence =
                        (right.Velocity.x - left.Velocity.x) *
                            0.5f * inverseLateralStep +
                        (up.Velocity.y - down.Velocity.y) *
                            0.5f * inverseLongitudinalStep;
                    float obstacleSource = CalculateObstacleFoamGeneration(
                        cells, mask, width, height, column, row,
                        obstacleInfluence[index], bowFaceMask[index],
                        centre, curl, velocityDivergence,
                        lateralStep, longitudinalCellSize, settings);
                    obstacleSources[index] = obstacleSource;
                    float lateralSpan = 2f * FoamShearRadiusCells * lateralStep;
                    float bankActivity = CalculateBankActivity(
                        column, width, downstreamSpeed[row], lateralSpan,
                        settings.BankFoamStrength);
                    foam[index] = Mathf.Max(
                        obstacleSource, CalculateFoamCoverage(bankActivity, settings));
                }
            }

            TransportObstacleFoam(
                cells, foam, obstacleSources, width, height, mask, lateralCellSize,
                longitudinalCellSize, settings.ObstacleFoamTrailLengthMeters);
        }

        static float CalculateObstacleFoamGeneration(
            Cell[] cells, bool[] mask, int width, int height, int column, int row,
            float obstacleInfluence, bool bowFace, Cell centre, float curl,
            float velocityDivergence, float lateralCellSize, float longitudinalCellSize,
            WaterRiverFluidSolveSettings settings)
        {
            Vector2 shearLeft = ReadShearVelocity(cells, mask, width, height,
                                                  column - FoamShearRadiusCells, row, centre);
            Vector2 shearRight = ReadShearVelocity(cells, mask, width, height,
                                                   column + FoamShearRadiusCells, row, centre);
            Vector2 shearDown = ReadShearVelocity(cells, mask, width, height,
                                                  column, row - FoamShearRadiusCells, centre);
            Vector2 shearUp = ReadShearVelocity(cells, mask, width, height,
                                                column, row + FoamShearRadiusCells, centre);
            float lateralSpan = 2f * FoamShearRadiusCells * lateralCellSize;
            float longitudinalSpan = 2f * FoamShearRadiusCells * longitudinalCellSize;
            float lateralShear = (shearRight - shearLeft).magnitude / lateralSpan;
            float longitudinalShear = (shearUp - shearDown).magnitude / longitudinalSpan;
            // A solid just DOWNSTREAM makes this cell a bow face: water decelerating into
            // an obstruction piles up smoothly instead of churning, so the deceleration-
            // driven terms (longitudinal shear, convergence) must not print a foam bar
            // UPSTREAM of every rock - real whitewater lives beside and behind it. Ribbon
            // flow is always +row, so downstream needs no vector test. Cascade foam is
            // generated by its dedicated river-slope path, outside this fluid contributor.
            float shear = Mathf.Max(lateralShear, bowFace ? 0f : longitudinalShear);
            float swirl = Mathf.Abs(curl) * VorticityFoamWeight;
            float convergence = bowFace
                ? 0f
                : Mathf.Max(0f, -velocityDivergence) * ConvergenceFoamWeight;
            float obstacleActivity = (shear + swirl + convergence) * obstacleInfluence;
            return CalculateFoamCoverage(obstacleActivity, settings);
        }

        static float CalculateFoamCoverage(
            float activity, WaterRiverFluidSolveSettings settings)
            => Mathf.Clamp01(
                (activity - settings.FoamThreshold) * settings.FoamStrength);

        // The steady velocity solve has no meaningful elapsed foam time. Transport the final
        // obstacle source spatially, one downstream row at a time, and carry its travelled
        // distance separately. This follows the baked deflection while guaranteeing an exact
        // zero after the authored trail length, regardless of iteration count or river length.
        static void TransportObstacleFoam(
            Cell[] cells, float[] foam, float[] obstacleSources, int width, int height,
            bool[] mask, float[] lateralCellSize, float longitudinalCellSize,
            float trailLengthMeters)
        {
            if (trailLengthMeters <= 0f) return;

            var transportedCoverage = (float[])obstacleSources.Clone();
            var travelledDistance = new float[obstacleSources.Length];
            for (int index = 0; index < travelledDistance.Length; index++)
                travelledDistance[index] = obstacleSources[index] > 0f
                    ? 0f : float.PositiveInfinity;

            for (int row = 1; row < height; row++)
            {
                float lateralStep = lateralCellSize[row];
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!mask[index]) continue;

                    Vector2 velocity = cells[index].Velocity;
                    if (velocity.y <= MinimumPositiveValue) continue;
                    float lateralCells = velocity.x / velocity.y *
                        longitudinalCellSize / lateralStep;
                    lateralCells = Mathf.Clamp(
                        lateralCells, -MaximumFoamTransportLateralCellsPerRow,
                        MaximumFoamTransportLateralCellsPerRow);
                    float upstreamColumn = column - lateralCells;
                    if (!TrySampleTransportRow(
                            transportedCoverage, travelledDistance, mask, width,
                            row - 1, upstreamColumn, out float upstreamCoverage,
                            out float upstreamDistance))
                        continue;

                    float lateralDistance = lateralCells * lateralStep;
                    float stepDistance = Mathf.Sqrt(
                        longitudinalCellSize * longitudinalCellSize +
                        lateralDistance * lateralDistance);
                    float nextDistance = upstreamDistance + stepDistance;
                    if (nextDistance >= trailLengthMeters) continue;

                    float remainingBeforeStep = trailLengthMeters - upstreamDistance;
                    float remainingAfterStep = trailLengthMeters - nextDistance;
                    float advectedCoverage = upstreamCoverage *
                        (remainingAfterStep / remainingBeforeStep);
                    if (advectedCoverage <= transportedCoverage[index]) continue;

                    transportedCoverage[index] = advectedCoverage;
                    travelledDistance[index] = nextDistance;
                    foam[index] = Mathf.Max(foam[index], advectedCoverage);
                }
            }
        }

        static bool TrySampleTransportRow(
            float[] coverage, float[] travelledDistance, bool[] mask, int width,
            int row, float column, out float sampledCoverage, out float sampledDistance)
        {
            float clampedColumn = Mathf.Clamp(column, 0f, width - 1f);
            int firstColumn = Mathf.FloorToInt(clampedColumn);
            int secondColumn = Mathf.Min(firstColumn + 1, width - 1);
            float secondWeight = clampedColumn - firstColumn;
            float firstWeight = 1f - secondWeight;
            int firstIndex = row * width + firstColumn;
            int secondIndex = row * width + secondColumn;
            float firstContribution = mask[firstIndex]
                ? coverage[firstIndex] * firstWeight : 0f;
            float secondContribution = mask[secondIndex]
                ? coverage[secondIndex] * secondWeight : 0f;
            sampledCoverage = firstContribution + secondContribution;
            if (sampledCoverage <= 0f)
            {
                sampledDistance = 0f;
                return false;
            }

            float weightedDistance = 0f;
            if (firstContribution > 0f)
                weightedDistance += travelledDistance[firstIndex] * firstContribution;
            if (secondContribution > 0f)
                weightedDistance += travelledDistance[secondIndex] * secondContribution;
            sampledDistance = weightedDistance / sampledCoverage;
            return float.IsFinite(sampledDistance);
        }

        static float CalculateBankActivity(int column, int width, float downstreamSpeed,
                                           float lateralSpan, float bankFoamStrength)
        {
            if (bankFoamStrength <= 0f) return 0f;

            bool samplesLeftBank = column - FoamShearRadiusCells < 0;
            bool samplesRightBank = column + FoamShearRadiusCells >= width;
            if (!samplesLeftBank && !samplesRightBank) return 0f;

            Vector2 baseVelocity = new Vector2(0f, downstreamSpeed);
            Vector2 leftVelocity = samplesLeftBank
                ? Vector2.Lerp(baseVelocity, Vector2.zero, bankFoamStrength)
                : baseVelocity;
            Vector2 rightVelocity = samplesRightBank
                ? Vector2.Lerp(baseVelocity, Vector2.zero, bankFoamStrength)
                : baseVelocity;
            return (rightVelocity - leftVelocity).magnitude / lateralSpan;
        }

        static float[] BuildObstacleFoamInfluence(bool[] fluidMask, int width, int height,
                                                   float[] lateralCellSize,
                                                   float longitudinalCellSize)
        {
            var influence = new float[fluidMask.Length];
            int probeRows = Mathf.CeilToInt(
                ObstacleFoamInfluenceMeters / longitudinalCellSize);
            for (int row = 0; row < height; row++)
            {
                int probeColumns = Mathf.CeilToInt(
                    ObstacleFoamInfluenceMeters / lateralCellSize[row]);
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index]) continue;
                    influence[index] = FindObstacleFoamInfluence(
                        fluidMask, width, height, column, row, probeColumns, probeRows,
                        lateralCellSize[row], longitudinalCellSize);
                }
            }
            return influence;
        }

        static float FindObstacleFoamInfluence(bool[] mask, int width, int height,
                                                int column, int row, int probeColumns,
                                                int probeRows, float lateralCellSize,
                                                float longitudinalCellSize)
        {
            float nearestDistance = float.PositiveInfinity;
            for (int rowOffset = -probeRows; rowOffset <= probeRows; rowOffset++)
            {
                int probeRow = row + rowOffset;
                if (probeRow < 0 || probeRow >= height) continue;
                float longitudinalDistance = rowOffset * longitudinalCellSize;
                for (int columnOffset = -probeColumns;
                     columnOffset <= probeColumns; columnOffset++)
                {
                    int probeColumn = column + columnOffset;
                    if (probeColumn < 0 || probeColumn >= width) continue;
                    if (mask[probeRow * width + probeColumn]) continue;

                    float lateralDistance = columnOffset * lateralCellSize;
                    float distance = Mathf.Sqrt(
                        lateralDistance * lateralDistance +
                        longitudinalDistance * longitudinalDistance);
                    nearestDistance = Mathf.Min(nearestDistance, distance);
                }
            }

            return Mathf.InverseLerp(
                ObstacleFoamInfluenceMeters, 0f, nearestDistance);
        }

        // Marks every fluid cell holding a solid within ObstacleFoamInfluenceMeters downstream,
        // inside the same reach as a lateral window - the diagonal shoulders of a rounded
        // obstacle stagnate too, not just its centre column. Built ONCE per solve (the mask
        // never changes between iterations) so the per-cell probe cost never multiplies by
        // the iteration count. Rows past the grid are the open outlet and columns past the
        // banks are open, so neither counts as solid.
        static bool[] BuildBowFaceMask(bool[] fluidMask, int width, int height,
                                       float[] lateralCellSize, float longitudinalCellSize)
        {
            var bowFace = new bool[fluidMask.Length];
            int probeRows = Mathf.Max(FoamShearRadiusCells,
                Mathf.CeilToInt(ObstacleFoamInfluenceMeters / longitudinalCellSize));
            for (int row = 0; row < height; row++)
            {
                int probeColumns = Mathf.Max(FoamShearRadiusCells,
                    Mathf.CeilToInt(ObstacleFoamInfluenceMeters / lateralCellSize[row]));
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index]) continue;
                    bowFace[index] = HasSolidInProbe(fluidMask, width, height, column, row,
                                                     probeColumns, probeRows);
                }
            }
            return bowFace;
        }

        static bool HasSolidInProbe(bool[] mask, int width, int height, int column, int row,
                                    int probeColumns, int probeRows)
        {
            for (int rowOffset = 1; rowOffset <= probeRows; rowOffset++)
            {
                int probeRow = row + rowOffset;
                if (probeRow >= height) return false;
                for (int columnOffset = -probeColumns; columnOffset <= probeColumns;
                     columnOffset++)
                {
                    int probeColumn = column + columnOffset;
                    if (probeColumn < 0 || probeColumn >= width) continue;
                    if (!mask[probeRow * width + probeColumn]) return true;
                }
            }
            return false;
        }

        // Domain bounds mirror the channel velocity because bank generation is a separate,
        // author-controlled contributor. Interior solid cells remain zero so obstacle shear
        // is still measured inside the local influence field.
        static Vector2 ReadShearVelocity(Cell[] cells, bool[] mask, int width, int height,
                                         int column, int row, Cell centre)
        {
            if (column < 0 || column >= width || row < 0 || row >= height)
                return centre.Velocity;
            int index = row * width + column;
            return mask[index] ? cells[index].Velocity : Vector2.zero;
        }

        static Cell Sample(Cell[] cells, bool[] mask, int width, int height, Vector2 position)
        {
            float x = Mathf.Clamp(position.x, 0f, width - 1f);
            float y = Mathf.Clamp(position.y, 0f, height - 1f);
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int x1 = Mathf.Min(x0 + 1, width - 1);
            int y1 = Mathf.Min(y0 + 1, height - 1);
            Cell a = Lerp(Read(cells, mask, width, height, x0, y0),
                          Read(cells, mask, width, height, x1, y0), x - x0);
            Cell b = Lerp(Read(cells, mask, width, height, x0, y1),
                          Read(cells, mask, width, height, x1, y1), x - x0);
            return Lerp(a, b, y - y0);
        }

        static Cell Lerp(Cell first, Cell second, float value)
        {
            return new Cell
            {
                Velocity = Vector2.Lerp(first.Velocity, second.Velocity, value),
                Density = Mathf.Lerp(first.Density, second.Density, value),
                Vorticity = Mathf.Lerp(first.Vorticity, second.Vorticity, value),
            };
        }

        static Cell Read(Cell[] cells, bool[] mask, int width, int height, int column, int row)
        {
            if (column < 0 || column >= width || row < 0 || row >= height) return default;
            int index = row * width + column;
            return mask == null || mask[index] ? cells[index] : default;
        }

        static Cell ReadNeighbour(Cell[] cells, bool[] mask, int width, int height,
                                  int column, int row, Cell boundaryFallback)
        {
            if (column < 0 || column >= width || row < 0 || row >= height)
                return boundaryFallback;
            int index = row * width + column;
            return mask == null || mask[index] ? cells[index] : default;
        }

        static bool IsFluid(bool[] mask, int width, int height, int column, int row)
            => column >= 0 && column < width && row >= 0 && row < height &&
               mask[row * width + column];

        static void Validate(int width, int height, bool[] fluidMask, float[] downstreamSpeed,
                             float[] lateralCellSize, float longitudinalCellSize,
                             WaterRiverFluidSolveSettings settings)
        {
            if (width < MinimumResolution) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < MinimumResolution) throw new ArgumentOutOfRangeException(nameof(height));
            if (fluidMask == null || fluidMask.Length != checked(width * height))
                throw new ArgumentException("Fluid mask dimensions do not match the solve grid.",
                                            nameof(fluidMask));
            if (downstreamSpeed == null || downstreamSpeed.Length != height)
                throw new ArgumentException("Downstream speed must contain one value per row.",
                                            nameof(downstreamSpeed));
            if (lateralCellSize == null || lateralCellSize.Length != height)
                throw new ArgumentException("Lateral cell size must contain one value per row.",
                                            nameof(lateralCellSize));
            ValidatePositive(longitudinalCellSize, nameof(longitudinalCellSize));
            if (settings.Iterations < MinimumIterations)
                throw new ArgumentOutOfRangeException(nameof(settings.Iterations));
            ValidatePositive(settings.DeltaTime, nameof(settings.DeltaTime));
            ValidateNonNegative(settings.Viscosity, nameof(settings.Viscosity));
            ValidateNonNegative(settings.Pressure, nameof(settings.Pressure));
            ValidateNonNegative(settings.Force, nameof(settings.Force));
            if (!float.IsFinite(settings.VelocityDecay) ||
                settings.VelocityDecay < 0f || settings.VelocityDecay > 1f)
                throw new ArgumentOutOfRangeException(nameof(settings.VelocityDecay));
            ValidateNonNegative(settings.Vorticity, nameof(settings.Vorticity));
            ValidateNonNegative(settings.FoamThreshold, nameof(settings.FoamThreshold));
            ValidateNonNegative(settings.FoamStrength, nameof(settings.FoamStrength));
            ValidateNonNegative(
                settings.ObstacleFoamTrailLengthMeters,
                nameof(settings.ObstacleFoamTrailLengthMeters));
            if (!float.IsFinite(settings.BankFoamStrength) ||
                settings.BankFoamStrength < 0f || settings.BankFoamStrength > 1f)
                throw new ArgumentOutOfRangeException(nameof(settings.BankFoamStrength));
            for (int row = 0; row < downstreamSpeed.Length; row++)
            {
                ValidateNonNegative(downstreamSpeed[row], nameof(downstreamSpeed));
                ValidatePositive(lateralCellSize[row], nameof(lateralCellSize));
            }
        }

        static void ValidatePositive(float value, string name)
        {
            if (!float.IsFinite(value) || value <= 0f) throw new ArgumentOutOfRangeException(name);
        }

        static void ValidateNonNegative(float value, string name)
        {
            if (!float.IsFinite(value) || value < 0f) throw new ArgumentOutOfRangeException(name);
        }
    }
}
