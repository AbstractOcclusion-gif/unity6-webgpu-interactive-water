// WaterVolume settings - the interactive ripple solver: wave speed, damping, substeps and the
// volume-conservation correction.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        // Legacy capture (pre-Phase-2 scenes) -> copied once by MigrateFoamV5. Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("foam")] bool _legacyFoam = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamGenRate")] float _legacyFoamGenRate = 0.6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamDecay")] float _legacyFoamDecay = 0.96f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamDecayRate")] float _legacyFoamDecayRate = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamSpread")] float _legacyFoamSpread = 0.2f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamAdvect")] float _legacyFoamAdvect = 3f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFromSpeed")] float _legacyFoamFromSpeed = 6f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFromCurvature")] float _legacyFoamFromCurvature = 30f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamColor")] Color _legacyFoamColor = Color.white;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamStrength")] float _legacyFoamStrength = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamFeather")] float _legacyFoamFeather = 0.15f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamCoreCut")] float _legacyFoamCoreCut = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamBorderWidth")] float _legacyFoamBorderWidth = 0.08f;
        [SerializeField, HideInInspector, FormerlySerializedAs("foamContactDepth")] float _legacyFoamContactDepth = 0.06f;

        [Header("Ripple tuning")]
        [SerializeField] RippleSettings rippleSettings = new RippleSettings();

        /// <summary>Interactive ripple solver + click/drag ripple tuning. Migrated off the flat
        /// WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// unchanged.</summary>
        [System.Serializable]
        public sealed class RippleSettings
        {
            [Tooltip("Allow mouse clicks, pointer drags, and touch taps that hit this water surface " +
                     "to inject ripples and splash effects. Camera orbit remains available when off.")]
            public bool pointerWaterInteraction = true;
            [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
            [Range(0.1f, 2.0f)] public float waveSpeed = 0.6f;
            [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
            [Range(0.90f, 1.0f)] public float damping = 0.99f;
            [Tooltip("Smooths the velocity field toward its neighbourhood each step - a viscosity. " +
                     "Targets the texel-to-texel chop the explicit solver's dispersion leaves behind " +
                     "and barely touches long waves, which is what Damping cannot do. 0 = off.")]
            [Range(0f, 1f)] public float rippleViscosity = 0f;
            [Tooltip("Solver steps per frame AT THE 60 FPS REFERENCE - the sim accumulates real " +
                     "time and runs this rate regardless of frame rate, so wave speed is identical " +
                     "in a 30 fps build and a 144 fps editor. More = faster, smoother propagation.")]
            [Range(1, 8)] public int stepsPerFrame = 2;
            [Tooltip("Height added by a click/drag ripple (world units; volume-scale independent).")]
            [Range(0.001f, 0.08f)] public float rippleStrength = 0.025f;
            [Tooltip("Radius of a click/drag ripple (world units; volume-scale independent).")]
            [Range(0.005f, 0.2f)] public float rippleRadius = 0.05f;
            [Tooltip("Horizontal choppiness of the interactive ripple + WAKE field (horizontal pinch): " +
                     "sharpens ripple/wake crests horizontally so a boat wake reads crisp instead of soft " +
                     "and round. 0 = off (height-only, unchanged). Raise for a sharp V-wake; also sharpens " +
                     "ambient interactive ripples. On the ocean the wake rides the camera-following sim window.")]
            [Range(0f, 1.5f)] public float rippleChoppiness = 0f;
            [Tooltip("Caps the per-step velocity a moving interactor (boat/sphere) injects, taming the " +
                     "too-tall crest a FRESH wake dumps over still water before the solver spreads it - " +
                     "without touching the developed wake SHAPE (built from many smaller pushes below the " +
                     "cap). 0 = off (no cap). Lower positive = softer onset; the sim velocity ceiling is 0.5.")]
            [Range(0f, 0.5f)] public float wakeStartForceCap = 0f;
            [Tooltip("Caps the height ripple stamped when a Rigidbody first enters the water, taming " +
                     "an over-tall impact ring without changing the splash particles. 0 = off (no cap). " +
                     "Lower positive = softer impact ripple.")]
            [Range(0f, 0.08f)] public float splashImpactRippleCap = 0f;
            [Tooltip("Seed the pool with random ripples on start.")]
            public bool seedRipplesOnStart = true;
            [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
            public bool conserveVolume = true;
            [Tooltip("Safety cap on how far Conserve Volume can shift the whole surface per step " +
                     "(pool units). The mean is computed exactly, so this only guards against a " +
                     "diverged transient moving the plane in one step.")]
            [Range(0.005f, 0.5f)] public float conserveMaxCorrection = 0.05f;
        }

        // Same-named forwarding accessors keep every reader unchanged. RippleStrength/RippleRadius stay
        // public get/set (sample scripting API) targeting the settings; the rest are read-only.
        internal float waveSpeed => rippleSettings.waveSpeed;
        internal bool PointerWaterInteraction => rippleSettings.pointerWaterInteraction;
        internal float damping => rippleSettings.damping;
        internal float rippleViscosity => rippleSettings.rippleViscosity;
        internal int stepsPerFrame => rippleSettings.stepsPerFrame;
        internal float rippleChoppiness => rippleSettings.rippleChoppiness;
        internal float wakeStartForceCap => rippleSettings.wakeStartForceCap;
        internal float splashImpactRippleCap => rippleSettings.splashImpactRippleCap;
        internal bool seedRipplesOnStart => rippleSettings.seedRipplesOnStart;
        internal bool conserveVolume => rippleSettings.conserveVolume;
        internal float conserveMaxCorrection => rippleSettings.conserveMaxCorrection;

        /// <summary>Height added by a click/drag ripple (world units).</summary>
        public float RippleStrength { get => rippleSettings.rippleStrength; set => rippleSettings.rippleStrength = value; }

        /// <summary>Radius of a click/drag ripple (world units).</summary>
        public float RippleRadius { get => rippleSettings.rippleRadius; set => rippleSettings.rippleRadius = value; }
    }
}
