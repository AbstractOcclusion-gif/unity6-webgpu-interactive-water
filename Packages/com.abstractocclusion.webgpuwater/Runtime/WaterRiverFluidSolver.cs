// WebGpuWater - deterministic KWS1-style settled fluid solve in ribbon texture space.
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

        internal WaterRiverFluidSolveSettings(
            int iterations, float deltaTime, float viscosity, float pressure,
            float force, float velocityDecay, float vorticity,
            float foamThreshold, float foamStrength)
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
        const float VelocityDeadband = 0.0001f;
        const int PressureIterations = 12;
        // Settled-foam transport constants - mirror the live sim's foam contract
        // (WaterSim.compute: generate -> advect -> diffuse -> decay) so the bake reaches
        // the same look. Survival sets the streak length: advected foam survives about
        // 1/(1-survival) steps, so 0.97 leaves ~33-cell trails behind obstacles.
        const float FoamSurvivalPerStep = 0.97f;
        // Fraction of each cell's foam blended toward its neighbour average per step -
        // softens the single-cell speckle a raw shear term prints.
        const float FoamSpreadFraction = 0.15f;
        // Swirl adds foam where eddies spin even when net shear is small (behind obstacles).
        const float VorticityFoamWeight = 0.5f;
        // Converging flow packs surface foam together (hydraulic-jump style accumulation).
        const float ConvergenceFoamWeight = 1f;
        // Shear sampled this many cells apart (KWS1's _FoamTexelOffset idea): a multi-cell
        // radius reads the velocity CONTRAST across an obstacle's whole shadow instead of a
        // one-cell ring, so the foam response is a soft band rather than a thin halo.
        const int FoamShearRadiusCells = 2;

        struct Cell
        {
            internal Vector2 Velocity;
            internal float Density;
            internal float Vorticity;
            internal float Foam;
        }

        internal static WaterRiverFluidSolveResult Solve(
            int width, int height, bool[] fluidMask, float[] downstreamSpeed,
            WaterRiverFluidSolveSettings settings)
        {
            Validate(width, height, fluidMask, downstreamSpeed, settings);
            int count = checked(width * height);
            var source = new Cell[count];
            var target = new Cell[count];
            var foam = new float[count];
            var divergence = new float[count];
            var pressure = new float[count];
            var pressureTarget = new float[count];
            Initialize(source, width, height, fluidMask, downstreamSpeed);

            for (int iteration = 0; iteration < settings.Iterations; iteration++)
            {
                Step(source, target, width, height, fluidMask, downstreamSpeed, settings,
                     divergence, pressure, pressureTarget);
                (source, target) = (target, source);
            }

            var velocity = new Vector2[count];
            float maximumSpeed = MinimumPositiveValue;
            for (int index = 0; index < count; index++)
            {
                velocity[index] = fluidMask[index] ? source[index].Velocity : Vector2.zero;
                maximumSpeed = Mathf.Max(maximumSpeed, velocity[index].magnitude);
                foam[index] = fluidMask[index] ? Mathf.Clamp01(source[index].Foam) : 0f;
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
                         WaterRiverFluidSolveSettings settings, float[] divergence,
                         float[] pressure, float[] pressureTarget)
        {
            for (int row = 0; row < height; row++)
            {
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
                    Vector2 laplacian = left.Velocity + right.Velocity + down.Velocity +
                                        up.Velocity - centre.Velocity * 4f;

                    Vector2 backtrace = new Vector2(column, row) -
                                        centre.Velocity * (2f * settings.DeltaTime);
                    Cell advected = Sample(source, fluidMask, width, height, backtrace);
                    Vector2 desiredFlow = new Vector2(0f, downstreamSpeed[row]);
                    Vector2 velocity = advected.Velocity + settings.DeltaTime *
                        (settings.Viscosity * laplacian - settings.Pressure * densityGradient +
                         settings.Force * (desiredFlow - advected.Velocity));

                    float curl = (right.Velocity.y - left.Velocity.y -
                                  up.Velocity.x + down.Velocity.x) * 0.5f;
                    Vector2 curlGradient = new Vector2(
                        Mathf.Abs(up.Vorticity) - Mathf.Abs(down.Vorticity),
                        Mathf.Abs(left.Vorticity) - Mathf.Abs(right.Vorticity));
                    if (curlGradient.sqrMagnitude > MinimumPositiveValue)
                        velocity += curlGradient.normalized * curl * settings.Vorticity;

                    velocity *= settings.VelocityDecay;
                    velocity = ApplySolidBoundary(
                        velocity, fluidMask, width, height, column, row);
                    if (velocity.magnitude < VelocityDeadband) velocity = Vector2.zero;

                    float velocityDivergence = (right.Velocity.x - left.Velocity.x +
                                                up.Velocity.y - down.Velocity.y) * 0.5f;
                    target[index] = new Cell
                    {
                        Velocity = velocity,
                        Density = Mathf.Max(
                            MinimumDensity,
                            advected.Density - settings.DeltaTime *
                            Vector2.Dot(densityGradient, centre.Velocity) -
                            velocityDivergence * 0.1f),
                        Vorticity = curl,
                        Foam = StepFoam(source, fluidMask, width, height, column, row,
                                        centre, left, right, down, up, advected.Foam,
                                        curl, velocityDivergence, settings),
                    };
                }
            }

            ProjectVelocity(target, width, height, fluidMask,
                            divergence, pressure, pressureTarget);
            UpdateVorticity(target, width, height, fluidMask);
        }

        static void ProjectVelocity(Cell[] cells, int width, int height, bool[] fluidMask,
                                    float[] divergence, float[] pressure,
                                    float[] pressureTarget)
        {
            Array.Clear(pressure, 0, pressure.Length);
            Array.Clear(pressureTarget, 0, pressureTarget.Length);
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int index = row * width + column;
                    if (!fluidMask[index])
                    {
                        divergence[index] = 0f;
                        continue;
                    }

                    Vector2 centre = cells[index].Velocity;
                    Vector2 left = ReadVelocity(
                        cells, fluidMask, width, height, column - 1, row, centre);
                    Vector2 right = ReadVelocity(
                        cells, fluidMask, width, height, column + 1, row, centre);
                    Vector2 down = ReadVelocity(
                        cells, fluidMask, width, height, column, row - 1, centre);
                    Vector2 up = ReadVelocity(
                        cells, fluidMask, width, height, column, row + 1, centre);
                    divergence[index] = -0.5f *
                        (right.x - left.x + up.y - down.y);
                }
            }

            for (int iteration = 0; iteration < PressureIterations; iteration++)
            {
                for (int row = 0; row < height; row++)
                {
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
                            (divergence[index] + left + right + down + up) * 0.25f;
                    }
                }
                (pressure, pressureTarget) = (pressureTarget, pressure);
            }

            for (int row = 0; row < height; row++)
            {
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
                    Vector2 velocity = cells[index].Velocity -
                                       new Vector2(right - left, up - down) * 0.5f;
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

        // Per-step foam update: generation from local turbulence, transport by the SAME
        // backtrace the velocity advection already computed, neighbour diffusion, decay.
        // The old one-shot shear snapshot only lit cells TOUCHING an obstacle; transporting
        // foam through the settled field is what draws the downstream streaks, bank lines
        // and accumulation the live sim gets for free.
        static float StepFoam(Cell[] source, bool[] mask, int width, int height,
                              int column, int row, Cell centre, Cell left, Cell right,
                              Cell down, Cell up, float advectedFoam, float curl,
                              float velocityDivergence,
                              WaterRiverFluidSolveSettings settings)
        {
            Vector2 shearLeft = ReadShearVelocity(source, mask, width, height,
                                                  column - FoamShearRadiusCells, row, centre);
            Vector2 shearRight = ReadShearVelocity(source, mask, width, height,
                                                   column + FoamShearRadiusCells, row, centre);
            Vector2 shearDown = ReadShearVelocity(source, mask, width, height,
                                                  column, row - FoamShearRadiusCells, centre);
            Vector2 shearUp = ReadShearVelocity(source, mask, width, height,
                                                column, row + FoamShearRadiusCells, centre);
            float shear = Mathf.Max((shearRight - shearLeft).magnitude,
                                    (shearUp - shearDown).magnitude);
            float swirl = Mathf.Abs(curl) * VorticityFoamWeight;
            float convergence = Mathf.Max(0f, -velocityDivergence) * ConvergenceFoamWeight;
            float activity = shear + swirl + convergence;
            float generation =
                Mathf.Clamp01((activity - settings.FoamThreshold) * settings.FoamStrength);
            float transported = advectedFoam * FoamSurvivalPerStep +
                                generation * settings.DeltaTime;
            float neighbourFoam = (left.Foam + right.Foam + down.Foam + up.Foam) * 0.25f;
            return Mathf.Clamp01(
                Mathf.Lerp(transported, neighbourFoam, FoamSpreadFraction));
        }

        // Shear-specific neighbour read. Lateral out-of-bounds is the river BANK - a solid
        // wall, so it shears against the flow (bank foam). Longitudinal out-of-bounds is the
        // inlet/outlet - open flow, NOT a wall - so it mirrors the centre; treating it as
        // solid would print a foam bar across both river ends.
        static Vector2 ReadShearVelocity(Cell[] cells, bool[] mask, int width, int height,
                                         int column, int row, Cell centre)
        {
            if (column < 0 || column >= width) return Vector2.zero;
            if (row < 0 || row >= height) return centre.Velocity;
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
                Foam = Mathf.Lerp(first.Foam, second.Foam, value),
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
            for (int row = 0; row < downstreamSpeed.Length; row++)
                ValidateNonNegative(downstreamSpeed[row], nameof(downstreamSpeed));
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
