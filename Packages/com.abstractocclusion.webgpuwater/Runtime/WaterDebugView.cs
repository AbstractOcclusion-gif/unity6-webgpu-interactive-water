// WebGpuWater - false-colour debug views for the water surface AND the fullscreen underwater fog.
// Drop this on ANY object in the scene and pick a mode; it publishes _WaterDebugMode and the
// matching shader replaces its output with the view - the surface pass for modes 1-6
// (WaterSurfaceDebug.hlsl), the fullscreen fog for modes 7-13 (WaterFogDebug.hlsl). The two
// ranges are disjoint and each side declines the other's, so exactly one of them ever paints.
// Remove the component (or set Off) and both are back to one uniform compare per pixel.
//
// WHY THIS EXISTS: reading the C# gates is not the same as reading what the GPU received. A
// renderer that never gets a MaterialPropertyBlock silently falls back to the MATERIAL ASSET's
// values, and coincident sheets (the base surface, the near-field patch and every clipmap ring)
// are indistinguishable in a beauty shot. Both reference assets ship an equivalent - Crest's
// _DEBUG_VISUALIZE_MASK, KWS's debug modes - for exactly this reason.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Debug View")]
    public sealed class WaterDebugView : MonoBehaviour
    {
        /// <summary>Which false-colour view the water surface draws. Values MUST match the
        /// WATER_DEBUG_* ordinals in Runtime/Shaders/WaterDebugMode.hlsl (the shared ordinal
        /// header both WaterSurfaceDebug.hlsl and WaterFogDebug.hlsl include).</summary>
        public enum Mode
        {
            /// <summary>Normal shading.</summary>
            Off = 0,
            /// <summary>R = SSR, G = planar, B = real refraction, as the SHADER reads them. Water
            /// that stays red after unticking SSR is a renderer missing its property block.</summary>
            ReflectionGate = 1,
            /// <summary>A distinct colour per renderer: base sheet, near-field patch, and each
            /// clipmap ring. Two colours interleaved over the same water = coincident sheets both
            /// shading, which reads as doubled reflections and distance-banded artifacts.</summary>
            RendererId = 2,
            /// <summary>The UV the planar mirror is sampled at, tiled x8. A band or a
            /// discontinuity here IS the artifact; smooth means the sampler is not the cause.</summary>
            PlanarUV = 3,
            /// <summary>View-space surface normal, the source of the planar nudge.</summary>
            ViewNormal = 4,
            /// <summary>The planar mirror RT itself, undecorated - no wave nudge, no roughness mip,
            /// no aniso smear. The scene should read upside-down, filling the frame, horizon at the
            /// same screen height as the real one. Anything wrong HERE was rendered wrong by
            /// PlanarMirror and no sampler change can repair it.</summary>
            RawMirror = 5,
            /// <summary>The mirror again, with MAGENTA wherever it holds nothing. Large magenta
            /// areas mean the reflection camera's frustum, oblique clip plane or culling is
            /// dropping the scene.</summary>
            MirrorEmpty = 6,

            // ---- Fullscreen underwater-fog views (WaterFogDebug.hlsl) --------------------
            // These REPLACE THE WHOLE FRAME, not just the water surface, and they only draw
            // while the fullscreen fog pass is armed (WaterVolume.UnderwaterFogActive). That
            // is a reading, not a limitation: the view showing means the pass ran this frame,
            // the view vanishing back to normal shading means it did not - and the arm gate is
            // a CPU near-plane-corner test against a ~1-2 frame stale FFT readback, while every
            // mask these views draw is live and per-pixel. Post-processing still runs after the
            // fog, so turn post off before trusting a hue.

            /// <summary>Greyscale waterline mask: how much of this pixel the fog is allowed to
            /// paint. Black = the pass contributes nothing here, white = full strength.</summary>
            FogArmWeight = 7,

            /// <summary>The hole hunt. MAGENTA = the pass computed a real wet span for this
            /// pixel and then threw it away on the waterline mask - water in front of it, no fog
            /// on it. WHITE = painted at full strength, CYAN = the feather band, BLUE = the span
            /// was eaten by an exclusion carve (correct, not a hole), dark grey = no water on
            /// this ray. A magenta band hugging the waterline is a feather/derivative problem; a
            /// magenta sheet across the screen is the classification point being wrong for every
            /// pixel at once.</summary>
            FogUnpainted = 8,

            /// <summary>Where the mask classified this pixel against the waterline. BLUE = its
            /// own near-plane point (the open-water rule). GREEN = pushed out to an exclusion
            /// volume's exit face, the way Crest moves its mask onto portal geometry. RED = the
            /// eye is in a dry carve but the push moved nothing, so the classification fell back
            /// to a near-plane point sitting in dry air below sea level - which says nothing
            /// about the water being looked at.</summary>
            FogClassifySource = 9,

            /// <summary>Which of the span paths priced this pixel. Orange = bounded pond box,
            /// grey = Simple tier flat waterline, GREEN = rendered sheet seen from air (fog
            /// suppressed), BLUE = submerged, span ends at the rendered sheet, YELLOW = analytic
            /// early-out with no prepass sample, MAGENTA = carve pixel handed to the crossing
            /// march, CYAN = the no-prepass tier's own march. (RED, the flat rest-plane
            /// fallback, was retired 2026-08-13 with its unreachable span path.)
            /// Black means nothing ran, which is a wiring bug rather than a water one.</summary>
            FogPathBranch = 10,

            /// <summary>Flat screen colour of the CPU gate state AS THE GPU RECEIVED IT:
            /// R = eye in water, G = eye inside a dry carve, B = Simple tier. The armed flag
            /// needs no channel - the view only draws while the pass is armed.</summary>
            FogGates = 11,

            /// <summary>The two halves of "should this pixel be fogged", shown disagreeing.
            /// RED = the waterline mask says paint and the span is ZERO, so nothing is painted -
            /// an unfogged pixel the mask wanted fogged, whatever zeroed the span. MAGENTA = a
            /// span exists and the mask threw it away. Neutral greys where the two agree (dark =
            /// both off, mid = both on), so only the faults carry colour. CAVEAT: a bounded pond
            /// sets the mask to 1 unconditionally (it is a finite volume seen from outside), so
            /// red is EXPECTED wherever a pond ray misses the box - this view is for oceans.</summary>
            FogMaskVsSpan = 12,

            /// <summary>The RAW prepass sign this pixel's span rule was decided on
            /// (_OceanSurfaceEyeDepth): RED = the air-facing side, BLUE = the underwater-facing
            /// side, BLACK = no surface rasterised. It comes from one canonical two-sided surface,
            /// so opposite-colour islands identify displaced triangle/LOD continuity faults rather
            /// than coincident above/under twins fighting at equal depth.</summary>
            FogSheetSide = 13,

            // ---- Surface views added after the fog block ----
            // APPENDED, never renumbered: this enum is serialized as an int on the component, so
            // reusing an ordinal would silently repoint every saved scene's selection.

            /// <summary>How much headroom the ripple sim has left before its containment clamp
            /// fires (WaterSim.compute, Sanitize). BLACK = a healthy field. RED = height climbing
            /// toward the bound, GREEN = velocity climbing toward it, WHITE = at the bound and
            /// being clamped THIS FRAME, BLUE = a texel Sanitize reset to flat after it went
            /// non-finite. A white patch that blinks on when the surface is hit hard IS the pop;
            /// blue speckle means the integrator diverged and the containment is papering over it,
            /// which no amount of clamp tuning can fix. Bounds are POOL units, so their world value
            /// scales with the body's vertical extent - a shallow pool reaches white far sooner
            /// than a deep one for the same ripple in metres.</summary>
            SimHeadroom = 14,

            /// <summary>What the water actually reads out of the foam buffer, split so generation
            /// and delivery cannot be confused for each other. RED = the RAW buffer value with no
            /// window fade (is there foam here at all?). GREEN = what the surface really gets
            /// (raw x fade). BLUE = the window fade on its own - 1 well inside, ramping down over
            /// Sim Window Edge Fade Texels, 0 at the border. MAGENTA = outside the sim window, where
            /// no foam can exist by construction, so the window's rectangle is visible as a shape.
            /// BLACK water = the buffer is empty there: a generation problem, and the render side is
            /// innocent. Red with no green = the foam is there and the edge fade is eating it.
            /// Red AND green = it is present and delivered, so the fault is downstream of this
            /// read.</summary>
            FoamMask = 15,

            /// <summary>The ripple sim's window, drawn as a shape. GREEN = full-strength sim, RED =
            /// the edge fade band (the same fade the foam mask and the ripple sample use, so this is
            /// the band that really attenuates them), DARK = outside the window, where the water is
            /// analytic and no interaction can reach it. The CYAN cross marks the window centre -
            /// on a boat-focused window it should sit on the hull, and if it lags or leads, the
            /// follow target or its offset is what to look at. The faint checker is ONE SQUARE PER
            /// SIM TEXEL: the grid's real density, countable on screen, which is what decides how
            /// coarse a ripple can be and is invisible in every other view.</summary>
            SimWindow = 16,

            /// <summary>The ripple sim's own state, converted to WORLD units so it reads the same on
            /// a 1 m pond and a 100 m-deep sea. RED = crest (height above rest), BLUE = trough,
            /// GREEN = speed - how hard the water is moving, which is the wake's ENERGY and outlives
            /// its shape. Still water is BLACK, so anything visible is something the sim was told to
            /// do: a wake reads as red/blue bands with a green core, an interactor that is spraying
            /// ripples everywhere paints them where they are actually being injected, and
            /// grid-frequency noise reads as a red/blue checker at texel scale. Point-sampled at the
            /// texel centre on purpose - a filtered read hides exactly that checker. Full channel is
            /// 25 cm of displacement / 5 cm per step of motion.</summary>
            RippleField = 17,
        }

        /// <summary>True while a FULLSCREEN-FOG view (modes 7+) is selected. The passes that draw
        /// AFTER the fog read this and stand down, so the false colour the fog writes reaches the
        /// screen undisturbed. It is not cosmetic: the god-ray shafts inject one slot after the
        /// fog and add water-tinted light near the waterline, which tinted every view green there
        /// and read exactly like a finding.</summary>
        internal static bool FogViewActive { get; private set; }

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        // BOTH halves: the shader global survives a skipped reload exactly as the flag does, and a
        // flag left set would silently keep the god rays, the after-fog sprites and the meniscus
        // switched off across every later session with nothing on screen explaining why.
        internal static void ResetStaticState()
        {
            FogViewActive = false;
            LogFogGates = false;
            Shader.SetGlobalFloat(ID_WaterDebugMode, 0f);
        }

        [Tooltip("Which false-colour view to draw. Modes 1-6 replace the water surface's colour; " +
                 "the Fog modes replace the whole frame and only appear while the fullscreen " +
                 "underwater fog pass is armed. Off restores normal shading.")]
        [SerializeField] Mode mode = Mode.Off;

        [Tooltip("Log the CPU fog gates to the console: one line per FLIP of any gate (the " +
                 "transition pops are single-frame flips, unquotable from a screenshot) plus a " +
                 "heartbeat. Filter the console on [FogGates].")]
        [SerializeField] bool logFogGates;

        /// <summary>CPU mirror of the toggle above (read by WaterVolume's underwater gate).
        /// Static like FogViewActive, cleared on the same paths, for the same reason.</summary>
        internal static bool LogFogGates { get; private set; }

        static readonly int ID_WaterDebugMode = Shader.PropertyToID("_WaterDebugMode");

        /// <summary>The active view. Setting it publishes immediately, so tooling can drive it.</summary>
        public Mode View
        {
            get => mode;
            set { mode = value; Publish(); }
        }

        // Published every frame rather than on change: the global is shared state that a domain
        // reload, a scene load or another component can clear underneath us, and a stale value
        // would leave the water stuck in a debug view with no visible cause.
        void Update() => Publish();

        void OnEnable() => Publish();

        // Leaving the mode set after the component goes away would be a trap - the water would
        // stay false-coloured with nothing in the scene explaining it.
        void OnDisable()
        {
            Shader.SetGlobalFloat(ID_WaterDebugMode, 0f);
            FogViewActive = false;
            LogFogGates = false;
        }

        // ONE writer for both halves of the selection - the shader global the views read, and the
        // CPU flag the after-fog passes stand down on - so the two can never disagree about which
        // view is live. FirstFogMode pairs with WATER_DEBUG_FOG_FIRST in WaterDebugMode.hlsl.
        const Mode FirstFogMode = Mode.FogArmWeight;

        void Publish()
        {
            Shader.SetGlobalFloat(ID_WaterDebugMode, (float)mode);
            FogViewActive = mode >= FirstFogMode;
            LogFogGates = logFogGates;
        }
    }
}
