// WaterVolume settings - how rigidbodies and interactors disturb the water.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        [Header("Object interaction")]
        [SerializeField] ObjectInteractionSettings objectInteractionSettings = new ObjectInteractionSettings();

        /// <summary>How floating objects disturb the surface (mouse-like drops vs rasterized footprint).
        /// Migrated off the flat WaterVolume fields into this block (Phase 2); the same-named accessors
        /// keep every reader unchanged.</summary>
        [System.Serializable]
        public sealed class ObjectInteractionSettings
        {
            [Tooltip("How floating objects disturb the water. MouseLikeDrops clones the mouse " +
                     "interaction: analytic cosine drops from bobbing and drift (uses Ripple " +
                     "Radius/Strength below; smooth, zero rasterization noise, slow rotation is " +
                     "silent). FootprintDelta displaces by the rasterized submerged footprint " +
                     "(shaped wakes for large hulls; costlier and noisier).")]
            public ObjectInteraction objectInteraction = ObjectInteraction.MouseLikeDrops;
            [Tooltip("FootprintDelta mode: MASTER strength for how strongly submerged objects " +
                     "displace the water. Multiplies the per-frame submerged-thickness DELTA " +
                     "(a much smaller quantity than a mouse drop's unit push), so it reads " +
                     "higher than Ripple Strength for a comparable wake. " +
                     "Per-object weighting is WaterInteractable.displaceScale.")]
            [Range(0f, 1f)] public float obstacleStrength = 0.25f;
            [Tooltip("FootprintDelta mode: soft dead-band (in submerged-thickness world units) " +
                     "that swallows tiny footprint deltas from drift/rotation rasterization " +
                     "noise. Raise to kill jitter; LOWER if a slowly moving float's wake is " +
                     "invisible (its genuine per-frame delta is sub-millimetre).")]
            [Range(0f, 0.005f)] public float obstacleDeadband = 0.0006f;
            [Tooltip("Temporal smoothing of the object footprint (0 = off). Low-pass filters " +
                     "the displacement a floater injects, so continuous bobbing/rotation emits " +
                     "a few long clean waves instead of a dense packet of tight rings. The " +
                     "total displaced volume is unchanged; higher = calmer but lazier response.")]
            [Range(0f, 0.95f)] public float obstacleSmoothing = 0.65f;
            [Tooltip("Flip the obstacle map in Z if object ripples appear mirrored.")]
            public bool obstacleFlipY = true;
        }

        // Same-named forwarding accessors keep every reader unchanged (objectInteraction is read by Step).
        internal ObjectInteraction objectInteraction => objectInteractionSettings.objectInteraction;
        internal float obstacleStrength => objectInteractionSettings.obstacleStrength;
        internal float obstacleDeadband => objectInteractionSettings.obstacleDeadband;
        internal float obstacleSmoothing => objectInteractionSettings.obstacleSmoothing;
        internal bool obstacleFlipY => objectInteractionSettings.obstacleFlipY;
    }
}
