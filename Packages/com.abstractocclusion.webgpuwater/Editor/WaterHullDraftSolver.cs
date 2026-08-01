// WebGpuWater - editor-only: where a hull actually floats, solved rather than guessed.
//
// WaterBuoyancy applies its lift as ForceMode.ACCELERATION:
//
//     lift_i = up * (gravity * buoyancy * fraction_i / N)
//
// so the terms sum as accelerations and setting them against gravity gives the equilibrium outright:
//
//     mean(fraction_i) = 1 / buoyancy
//
// That is exact, not a fit. It holds at REST, which is what a draft is: the drag term is
// GetPointVelocity * damping (zero at rest) and the drift term is the sampled water velocity (zero on
// a flat rest plane), so both vanish and only lift and gravity remain.
//
// SphereSubmergedFraction is monotonic in depth, so mean(fraction) is monotonic in the hull's vertical
// offset and the solve is a BISECTION ON A GUARANTEED BRACKET between "fully clear" and "fully under".
// No fitting, no iteration count to tune.
//
// And rather than moving the boat by the solved offset, the DRAFT PLANE is moved by its negative: every
// point's depth is identical either way, and nothing in the user's scene is touched.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    /// <summary>Solves the waterline a hull settles at from its own <see cref="WaterBuoyancy"/>.</summary>
    internal static class WaterHullDraftSolver
    {
        // Below this the hull cannot float at all: mean(fraction) would have to exceed 1.
        const float MinFloatingBuoyancy = 1f;
        // Bracket resolution. A tenth of a millimetre is far under any probe spacing, and bisection
        // reaches it from a metre-scale bracket in about fourteen steps.
        const float BracketToleranceMeters = 1e-4f;
        // Hard stop, so a pathological bracket cannot spin the editor. Bisection halves each step, so
        // this covers a starting bracket of ~1600 m at the tolerance above.
        const int MaxBisectionSteps = 64;

        /// <summary>The solved waterline, or why there isn't one.</summary>
        internal readonly struct Solution
        {
            /// <summary>World height to slice at. Always usable: it falls back to the rest plane.</summary>
            public readonly float DraftWorldY;

            /// <summary>True when a WaterBuoyancy was solved; false means <see cref="DraftWorldY"/> is the rest plane.</summary>
            public readonly bool Solved;

            /// <summary>Why the solve was refused, when it was; null otherwise.</summary>
            public readonly string Error;

            /// <summary>Solved, but with a caveat the user should see; null when clean.</summary>
            public readonly string Warning;

            Solution(float draftWorldY, bool solved, string error, string warning)
            {
                DraftWorldY = draftWorldY;
                Solved = solved;
                Error = error;
                Warning = warning;
            }

            internal static Solution RestPlane(float restWorldY, string error)
                => new Solution(restWorldY, false, error, null);

            internal static Solution At(float draftWorldY, string warning)
                => new Solution(draftWorldY, true, null, warning);
        }

        /// <summary>
        /// The plane <paramref name="hullObject"/> settles to on water whose rest surface is at
        /// <paramref name="restWorldY"/>. With no <see cref="WaterBuoyancy"/> anywhere on the object this
        /// returns the rest plane and no error - that is the normal case for a kinematic boat.
        /// </summary>
        internal static Solution Solve(GameObject hullObject, float restWorldY)
        {
            if (hullObject == null) return Solution.RestPlane(restWorldY, null);

            var floater = hullObject.GetComponentInChildren<WaterBuoyancy>();
            if (floater == null) return Solution.RestPlane(restWorldY, null);

            if (floater.buoyancy < MinFloatingBuoyancy)
                return Solution.RestPlane(restWorldY,
                    $"Buoyancy is {floater.buoyancy:0.##}. Equilibrium needs a mean submersion of " +
                    "1 / buoyancy, and anything at or below 1 asks for a fraction of 1 or more - so this " +
                    "hull SINKS and has no floating waterline. Raise Buoyancy above 1, or switch to the " +
                    "rest plane and set the draft by hand.");

            floater.BuildProbeLayout(out Vector3[] localPoints, out float sphereRadius);
            if (localPoints.Length == 0)
                return Solution.RestPlane(restWorldY,
                    "The WaterBuoyancy lattice is empty, so there is nothing to solve against.");

            Vector3[] worldPoints = ToWorld(floater.transform, localPoints);
            float offset = SolveOffset(worldPoints, sphereRadius, restWorldY, 1f / floater.buoyancy);

            // Move the plane, not the boat: depth = restY - offset - point.y either way.
            return Solution.At(restWorldY - offset, DescribeCaveat(floater));
        }

        static string DescribeCaveat(WaterBuoyancy floater)
            => floater.maxBuoyancyForce > 0f
                ? $"Max Buoyancy Force is {floater.maxBuoyancyForce:0.##}, which clamps the lift and therefore " +
                  "moves the real equilibrium. This solve ignores the clamp, so the hull may float lower than " +
                  "the line shown."
                : null;

        static Vector3[] ToWorld(Transform owner, Vector3[] localPoints)
        {
            var world = new Vector3[localPoints.Length];
            for (int i = 0; i < localPoints.Length; i++)
                world[i] = owner.TransformPoint(localPoints[i]);
            return world;
        }

        // Bisection on the vertical offset the hull would settle by. mean(fraction) is 1 with every point
        // a full radius under the surface and 0 with every point a full radius clear of it, so the bracket
        // below is guaranteed to contain any target in (0, 1] - which every buoyancy >= 1 produces.
        static float SolveOffset(Vector3[] worldPoints, float sphereRadius, float restWorldY, float targetMean)
        {
            float lowestPoint = float.MaxValue;
            float highestPoint = float.MinValue;
            for (int i = 0; i < worldPoints.Length; i++)
            {
                lowestPoint = Mathf.Min(lowestPoint, worldPoints[i].y);
                highestPoint = Mathf.Max(highestPoint, worldPoints[i].y);
            }

            float fullySubmerged = restWorldY - sphereRadius - highestPoint; // mean(fraction) == 1
            float fullyClear = restWorldY + sphereRadius - lowestPoint;      // mean(fraction) == 0

            for (int step = 0; step < MaxBisectionSteps; step++)
            {
                if (fullyClear - fullySubmerged <= BracketToleranceMeters) break;

                float middle = 0.5f * (fullySubmerged + fullyClear);
                if (MeanSubmergedFraction(worldPoints, sphereRadius, restWorldY, middle) > targetMean)
                    fullySubmerged = middle; // still too deep: the hull can rise further
                else
                    fullyClear = middle;
            }
            return 0.5f * (fullySubmerged + fullyClear);
        }

        static float MeanSubmergedFraction(Vector3[] worldPoints, float sphereRadius, float restWorldY, float offset)
        {
            float sum = 0f;
            for (int i = 0; i < worldPoints.Length; i++)
            {
                float depth = restWorldY - (worldPoints[i].y + offset);
                sum += WaterBuoyancy.SphereSubmergedFraction(depth, sphereRadius);
            }
            return sum / worldPoints.Length;
        }
    }
}
