// WaterVolume settings - open water: the ocean spectrum, the FFT cascades and the geometry
// clipmap, including the derived clipmap dimensions (pure functions of the two authored knobs).
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [Range(0f, 32f)] [SerializeField] internal float simWindowEdgeFadeTexels = 8f;

        [Header("Ocean (open water, clipmap, god rays, whitecaps)")]
        [SerializeField] OceanSettings ocean = new OceanSettings();

        /// <summary>Open-water / ocean look: the standalone surface, its horizon clipmap, large-body god
        /// rays and FFT whitecap foam. All ocean-only - inert on pools / bounded lakes. Migrated off the
        /// flat WaterVolume fields into this block (Phase 2); the same-named accessors keep every reader
        /// and the derived helpers below unchanged. (Consts and derived helpers stay on WaterVolume.)</summary>
        [System.Serializable]
        public sealed class OceanSettings
        {
            [Header("Open water (lake / ocean) - EXPERIMENTAL")]
            [Tooltip("Render this body as open water: the surface stands alone with NO analytic pool. " +
                     "The refracted view falls back to the deep-water colour where there is no scene " +
                     "geometry, and the mesh god rays are suppressed (the large-body render feature " +
                     "replaces them). OFF = the original pool / small-body look, byte-for-byte unchanged. " +
                     "Publishes the _LargeBody shader flag; the clipmap + FFT modules read the same flag.")]
            public bool openWater = false;
            [Tooltip("Artistic multiplier on the whole wave field, applied AFTER the sea state is " +
                     "normalised to its Significant Height. Leave at 1 to keep the authored heights " +
                     "honest in metres; push it for a stylised sea. 0 = flat water.")]
            [Min(0f)] public float largeWaveAmplitude = 1f;
            [Tooltip("CHOPPINESS: horizontal Gerstner displacement that sharpens crests and broadens " +
                     "troughs. 0 = round sine humps; 1 = a realistic sea; past ~1 the surface folds " +
                     "through itself, which is what breeds whitecaps. Buoyancy inverts it, so floaters " +
                     "still ride the visible crest.")]
            [Range(0f, LargeWaveChoppinessMax)] public float largeWaveChoppiness = DefaultLargeWaveChoppiness;

            [Header("Surface current (drifts the whole wave field)")]
            [Tooltip("SURFACE CURRENT HEADING (degrees): world-XZ direction the water drifts TOWARD. " +
                     "The entire sampled wave field - crests, whitecap deposits and the waterline - " +
                     "slides together, the way waves ride a real current. Inert while Current Speed " +
                     "is 0.")]
            [Range(0f, 360f)] public float currentHeadingDegrees = 0f;
            [Tooltip("SURFACE CURRENT SPEED (m/s). 0 = off (byte-identical). Typical values: 0.2 " +
                     "(gentle drift) to 2 (strong tidal race).")]
            [Min(0f)] public float currentSpeed = 0f;

            [Header("Ocean sea state (FFT spectrum)")]
            [Tooltip("Makes Wind Speed drive every ambient wind-made layer: the FFT wind sea, small wind " +
                     "waves and detail normals. At 0 m/s the local wind sea is flat; at Reference Wind " +
                     "Speed the authored height and wavelength are used. Remote Swell, wakes " +
                     "and impact ripples remain independent. Off keeps the manually authored sea state. " +
                     "For runtime weather, change wind gradually because the FFT spectrum must refresh.")]
            public bool windDrivesAmbientSeaState = false;
            [Tooltip("Wind speed (m/s) at which this ocean's authored Significant Wave Height and Peak " +
                     "Wavelength are used exactly. Set this to your storm maximum to author the raging " +
                     "state there; lower wind then scales it down. Used only when Wind Drives Ambient Sea " +
                     "State is enabled.")]
            [Min(AmbientWindReferenceSpeedMin)] public float ambientWindReferenceSpeed = DefaultAmbientWindReferenceSpeed;
            [Tooltip("SIGNIFICANT WAVE HEIGHT (metres): average CREST-TO-TROUGH height of the highest " +
                     "third of waves, not crest elevation above the mean surface. A 15 m setting " +
                     "typically places prominent crests about 7.5 m above mean water, with individual " +
                     "waves varying as the spectrum interferes. Independent of wavelength, so raising " +
                     "it makes the same waves steeper rather than longer.")]
            [Min(0f)] public float significantWaveHeight = DefaultSignificantWaveHeight;
            [Tooltip("PEAK WAVELENGTH (metres): the crest-to-crest distance of the dominant wave. This is " +
                     "the sea's SCALE - and, with Significant Height, its steepness. A short peak with a " +
                     "tall height is a small agitated chop; a long peak with the same height is a lazy " +
                     "ocean swell. The whole cascade layout is derived from it, so this is also what " +
                     "makes a pond-sized or a giant ocean.")]
            [Min(OceanPeakWavelengthMin)] public float peakWavelength = DefaultPeakWavelength;
            [Tooltip("PEAK SHARPNESS (JONSWAP gamma): how much of the energy is concentrated at the peak " +
                     "wavelength. 1 = a broad Pierson-Moskowitz spectrum - many scales at once, confused " +
                     "and choppy. 3.3 = the classic storm sea. 5-7 = one narrow band, long organised " +
                     "corduroy rollers. Does NOT change the wave height: the spectrum is renormalised.")]
            [Range(OceanPeakSharpnessMin, OceanPeakSharpnessMax)] public float peakSharpness = DefaultPeakSharpness;
            [Tooltip("WAVE SCALE: multiplies the Peak Wavelength (and therefore the whole cascade set) " +
                     "without touching the height, so it changes the sea's SIZE at constant steepness. " +
                     "Below 1 for a miniature, Gulliver-scale sea; above 1 for a giant one. 1 = the " +
                     "Peak Wavelength exactly as authored.")]
            [Min(OceanWaveScaleMin)] public float waveScale = 1f;
            [Tooltip("SEA DEPTH (metres) for the shallow-water (TMA) correction, which drains the " +
                     "long-wave end and makes a coastal sea read shorter and steeper than the same wind " +
                     "offshore. 0 = deep water, correction off. This is the OPEN-SEA depth, not the " +
                     "shoreline bathymetry - the shoreline has its own shoaling in Bed Depth.")]
            [Min(0f)] public float seaDepth = 0f;
            [Tooltip("WAVE REACH: how far out the wave detail keeps being drawn, as a multiple of " +
                     "the automatic per-cascade range. Past a cascade's range it fades to nothing, " +
                     "so too low a value leaves the far sea a flat mirror; too high makes distant " +
                     "waves finer than the mesh can carry and they crawl. 1 = the strict automatic " +
                     "rule; the default trades a little far-field shimmer for an ocean that reaches " +
                     "the horizon.")]
            [Range(OceanCascadeReachMin, OceanCascadeReachMax)] public float cascadeReach = DefaultCascadeReach;
            [Tooltip("Long-period SWELL height (metres, significant height like the sea state's): tall, " +
                     "slow rollers layered on top of the wind sea. 0 = no long swell.")]
            [Min(0f)] public float swellHeight = 0f;
            [Tooltip("Wavelength (metres) of the swell. Bigger = longer, slower rolls. Only the part of " +
                     "it that falls inside the cascade bands is rendered, so keep it below about twice " +
                     "the Peak Wavelength.")]
            [Min(1f)] public float swellWavelength = DefaultSwellWavelength;
            [Tooltip("Gust patches (\"cat's paws\"): local wind variation that roughens drifting patches " +
                     "of the surface and leaves glassy lulls between them. Shading only (roughness, " +
                     "whitecaps, micro-ripple) - wave heights and buoyancy are untouched. 0 = uniform sea.")]
            [Range(0f, 1f)] public float seaStateGusts = 0f;
            [Tooltip("Slicks / windrows: long glassy streaks aligned with the wind where the finest " +
                     "ripples are damped (surfactant films), while longer waves roll through untouched. " +
                     "Shading only - heights and buoyancy are untouched. 0 = none.")]
            [Range(0f, 1f)] public float seaStateSlicks = 0f;
            [Tooltip("Bounded open water: bake the upwind distance to land and attenuate wave height " +
                     "where fetch is short. Disabled by default; unbounded oceans remain inert.")]
            public bool seaStateFetchEnabled = false;
            [Tooltip("How strongly the physical wind-fetch attenuation affects displacement. 0 keeps " +
                     "the existing wave field; 1 applies the full baked response.")]
            [Range(0f, 1f)] public float seaStateFetchStrength = 1f;
            [Tooltip("Break the FFT tile repetition with covariance-preserving three-way hexagonal " +
                     "tiling and blending. Disabled by default: the historical direct cascade sample " +
                     "remains bit-identical.")]
            public bool oceanAperiodicEnabled = false;
            [Tooltip("Runtime-editable RG direction map. RG encodes a signed XY direction from [0,1] " +
                     "to [-1,1]. A missing map keeps the original wave heading.")]
            public Texture2D oceanDirectionMap;
            [Tooltip("World width and height covered by the direction map, centred on this water body.")]
            [Min(1f)] public float oceanDirectionMapSize = 1024f;
            [Tooltip("How strongly the direction map rotates each hexagonal wave tile.")]
            [Range(0f, 1f)] public float oceanDirectionMapStrength = 1f;
            [Tooltip("Hex tile size relative to one FFT exemplar. Larger values retain broader wave " +
                     "structures; smaller values increase variation.")]
            [Range(0.5f, 2f)] public float oceanAperiodicTileScale = 1f;
            [Tooltip("Swell travel direction OFFSET from the wind, in degrees. Real swell radiates from " +
                     "a distant storm, not the local wind - most of the open ocean carries swell crossing " +
                     "the wind sea at an angle, which is what breaks the single-direction look. 0 = " +
                     "aligned with the wind (the historical behaviour, bit-identical).")]
            [Range(-180f, 180f)] public float swellHeadingOffsetDegrees = 0f;
            [Tooltip("How much of the FFT ocean's energy travels ACROSS and AGAINST the wind instead of " +
                     "with it. 0 = a perfectly ordered sea marching downwind; 1 = fully isotropic, no net " +
                     "travel direction at all. Wave HEIGHT does not change with this - only the direction " +
                     "the energy is spread over.")]
            [Range(0f, 1f)] public float oceanWindTurbulence = DefaultOceanWindTurbulence;
            [Tooltip("Extend this open-water body's surface to the HORIZON with a camera-following clipmap " +
                     "mesh (an OCEAN, not a bounded lake). Requires Open Water ON and the large-body sim " +
                     "window (near-field ripples fade to flat past it). OFF = the surface stays the bounded " +
                     "footprint plane, unchanged. Drawing water past the shore would be wrong for a lake, so " +
                     "this is opt-in.")]
            public bool unboundedOcean = false;
            [Tooltip("BOUNDED open water only: metres over which the whole wave field (swell, chop, FFT, " +
                     "surf, whitecaps) feathers to the rest level toward the footprint border, so the " +
                     "surface never ends mid-wave as a standing wall of water. Ignored on an Unbounded " +
                     "ocean (its clipmap has no border). 0 = off.")]
            [Range(0f, EdgeFeatherMetersMax)] public float edgeFeatherMeters = DefaultEdgeFeatherMeters;

            [Header("Ocean clipmap (unbounded open water)")]
            [Tooltip("Cells per side of each geometry-clipmap LOD level (even). Higher = finer far-field " +
                     "tessellation and less wave 'swim' when the camera moves, at more vertices.")]
            [Min(ClipmapMinGridResolution)] public int clipmapGridResolution = DefaultClipmapGridResolution;
            [Tooltip("Target horizon reach (metres) of the outermost LOD level: the number of levels is " +
                     "derived so the ocean reaches at least this far. Drives the camera far plane too.")]
            [Min(ClipmapMinRadius)] public float clipmapOuterRadius = DefaultClipmapOuterRadius;
            [Tooltip("Far-field band-limit: how fast the shortest DRAWN wavelength grows with camera distance " +
                     "(metres of wavelength per metre of distance). Keeps the long rolling swell out to the " +
                     "horizon while dropping short chop the coarse far mesh can't resolve (which would crawl). " +
                     "Lower = waves reach further (needs denser Clipmap Rings); higher = calms sooner.")]
            [Min(0f)] public float oceanDetailFalloff = DefaultOceanDetailFalloff;
            [Tooltip("Distance (metres) at which the ocean surface fully dissolves into the horizon sky, so " +
                     "the far mesh edge has no hard line. 0 = off. A light stopgap - real horizon softening " +
                     "is the future fog pass. Set near the Clipmap Outer Radius to try it.")]
            [Min(0f)] public float horizonFadeDistance = 0f;
            [Tooltip("Atmosphere colour the far ocean dissolves toward at the horizon. Alpha controls how much " +
                     "it overrides the reflected sky: 0 = pure sky (seamless, the natural default), 1 = fully " +
                     "this colour (a coloured haze band). Only used when Horizon Haze Density > 0.")]
            public Color horizonHazeColor = DefaultHorizonHazeColor;
            [Tooltip("Horizon haze AMOUNT (0 = off, 1 = strongest) - the far ocean dissolves toward the " +
                     "horizon sky colour. Mapped internally to a gentle distance-haze so the whole 0..1 range " +
                     "is usable; ~0.3-0.5 reads as a light atmospheric haze. (Previously a raw per-metre " +
                     "density where anything over ~0.001 saturated instantly - re-enter as a 0..1 amount.)")]
            [Range(0f, 1f)] public float horizonHazeDensity = 0f;

            [Header("Ocean god rays (large-body light shafts)")]
            [Tooltip("Shaft colour, multiplied by the sun colour. Only used when God Ray Density > 0.")]
            public Color largeGodRayColor = DefaultLargeGodRayColor;
            [Tooltip("Master intensity of the ocean god-ray shafts. 0 = off (also the gate: the fullscreen " +
                     "shaft pass is skipped entirely). Raise for brighter volumetric beams.")]
            [Min(0f)] public float largeGodRayDensity = 0f;
            [Tooltip("Shafts seen from ABOVE the water, THROUGH AN EXCLUSION VOLUME'S WINDOW - a " +
                     "sunken room's pane, a hull opening. 0 = underwater only (the default, and the " +
                     "look this asset shipped with). Above water the shafts draw ONLY where the view " +
                     "ray crosses the waterline INSIDE a carve: over open sea the surface shader owns " +
                     "the view and beams there would be painted onto water the viewer is not inside, " +
                     "but looking through a pane genuinely IS looking into a lit water volume. Scales " +
                     "the shafts relative to the submerged view, which always renders full strength.")]
            [Range(0f, 1f)] public float largeGodRayFromAir = 0f;
            [Tooltip("Scene point/spot lights scattered INSIDE the volumetric shaft march: a sunk " +
                     "lamp grows a real halo in the beams, and the halo reaches the underside " +
                     "mirror shafts. This ADDS to the fog's own Light Scatter glow (Water Fog " +
                     "block) - the two are separate layers, balance them by ear exactly like sun " +
                     "in-scatter vs god rays. 0 = off, the shipped look. Simple fog tiers skip it.")]
            [Range(0f, 1f)] public float largeGodRayLightScatter = 0f;
            [Tooltip("Raymarch samples per pixel for the ocean shafts - SEPARATE from the pool god-ray steps. " +
                     "More = smoother beams, higher cost.")]
            [Range(LargeGodRayMinSteps, LargeGodRayMaxSteps)] public int largeGodRaySteps = DefaultLargeGodRaySteps;
            [Tooltip("Forward-scattering (Mie / Henyey-Greenstein g): 0 = even glow, higher = beams brighten " +
                     "sharply when looking toward the sun, like real shafts through haze.")]
            [Range(0f, LargeGodRayMaxAnisotropy)] public float largeGodRayAnisotropy = DefaultLargeGodRayAnisotropy;
            [Tooltip("Distance extinction (per metre) that thins the shafts as they recede, so the far ocean " +
                     "does not over-glow. 0 = no distance falloff.")]
            [Min(0f)] public float largeGodRayExtinction = 0f;
            [Tooltip("How strongly the near-field surface caustics brighten and flicker the shafts (the shimmer " +
                     "close to the camera, inside the sim window). 0 = plain shadow shafts. Needs the Large Body " +
                     "Caustics Shader assigned.")]
            [Min(0f)] public float largeGodRayCausticStrength = DefaultLargeGodRayCausticStrength;
            [Tooltip("Caustic smoothing radius (metres) for the SWELL the caustics focus through - both the " +
                     "shafts and the pattern on the seabed. They focus only through waves LONGER than about " +
                     "twice this, so the shimmer rides the slow swell instead of fast wind ripple; the rendered " +
                     "surface keeps its full detail. 0 = full spectrum, and NOT a saving - that path samples the " +
                     "shore and surf fields instead, so it is likely dearer; what changes is the content, and " +
                     "the FFT chop is back, flickering at a rate Ripple Speed cannot slow. Above 0 costs 4 height " +
                     "taps per caustic vertex - and the caustic pass evaluates every vertex 5 times.")]
            [Range(0f, 10f)] public float largeGodRayCausticSmooth = 2f;
            [Tooltip("How quickly the shaft shimmer blurs and calms with the sample's depth below the surface " +
                     "(softening per metre): deep beams read broad and slow instead of razor sharp, like real " +
                     "light losing focus. 0 = sharp at any depth.")]
            [Range(0f, 1f)] public float largeGodRayCausticDepthSoften = 0.25f;
            [Tooltip("Speed of the caustic's OWN ripple field (1 = physical wave speed for its wavelength). " +
                     "The caustic runs a dedicated small-wave layer decoupled from the surface (the surface's " +
                     "small content is FFT-driven and cannot be slowed), so beam/shimmer pace is a direct dial.")]
            [Range(0.05f, 1f)] public float largeCausticTimeScale = 0.5f;
            [Tooltip("Dominant wavelength (metres) of the caustic's own ripple field - the small waves that " +
                     "trigger the shafts. Smaller = finer, denser beams; larger = broad slow bands.")]
            [Range(0.5f, 10f)] public float largeCausticRippleScale = 3f;
            [Tooltip("Strength of the dedicated caustic ripple layer, which is ADDED ON TOP of the swell - " +
                     "0 = swell only, above 0 = swell plus this much fine dapple. It is not a mode switch: the " +
                     "caustics keep showing the actual wave shape at every value. The dapple has its own clock " +
                     "(Ripple Speed / Ripple Scale) because the surface's own small content is FFT-driven and " +
                     "cannot be slowed.")]
            [Range(0f, 2f)] public float largeCausticRippleStrength = 1f;
            [Tooltip("Softening (in mip levels) for the caustics painted on the seabed and on terrain - " +
                     "the light shafts are never affected, they keep reading the sharp map. 0 = the sharpest " +
                     "the generator can produce, and that is now the right default: the caustic field is " +
                     "smooth by construction, so there is no pixelation left for a blur to hide. Raise it " +
                     "only as a look choice.")]
            [Range(0f, ProjectionSoftenMax)] public float largeCausticProjectionSoften = 0f;

            [Header("Ocean foam (whitecaps)")]
            [Tooltip("Wind speed (m/s) below which the FFT ocean grows NO whitecaps (KWS foams above ~4). Tie " +
                     "to the same Wind Speed that drives the swell: calmer seas stay foam-free. Ocean-only.")]
            [Min(0f)] public float oceanFoamWindThreshold = DefaultOceanFoamWindThreshold;
            [Tooltip("How readily a folding wave crest turns to foam. 1 = only where the surface actually pinches " +
                     "(the natural default); higher spreads foam onto gentler folds; lower needs sharper breaks. " +
                     "Needs Large Wave Choppiness above 0 for crests to fold at all.")]
            [Range(0f, OceanFoamCoverageMax)] public float oceanFoamCoverage = DefaultOceanFoamCoverage;
            [Tooltip("How fast foam builds up on breaking crests. Higher = denser whitecaps sooner.")]
            [Range(0f, OceanFoamStrengthMax)] public float oceanFoamStrength = DefaultOceanFoamStrength;
            [Tooltip("How fast foam fades once a crest passes (per second). Lower = foam lingers and streaks; " +
                     "higher = it dies back quickly. This is what stops whitecaps flickering frame to frame.")]
            [Range(0f, OceanFoamFadeRateMax)] public float oceanFoamFadeRate = DefaultOceanFoamFadeRate;
            [Tooltip("Whitecap tint (RGB) and overall opacity (alpha) where foam sits on the surface. White is " +
                     "the natural default; alpha 0 hides the surface foam entirely (accumulation still runs).")]
            public Color oceanFoamColor = Color.white;
            [Tooltip("Metres per tile of the Foam Pattern texture on the ocean surface. Smaller = finer, more " +
                     "repeated lace; larger = broader foam shapes. Uses the material's Foam Pattern slot.")]
            [Min(OceanFoamTileSizeMin)] public float oceanFoamTileSize = DefaultOceanFoamTileSize;
            [Tooltip("How softly the foam texture dissolves in as coverage rises. 0 = hard edges; higher = a " +
                     "gentle feathered fade from water to foam.")]
            [Range(0f, 1f)] public float oceanFoamFeather = DefaultOceanFoamFeather;
            [Tooltip("Smears the foam TEXTURE along the drift (downwind) axis, so deposited foam reads " +
                     "as a trail instead of a patch. DETAIL ONLY - a texture frame cannot change which " +
                     "parts of the sea are foamy; for the overall SHAPE use Crest Gate / Face Bias / " +
                     "Crest Anisotropy. 1 = isotropic (unchanged), 3-4 = strongly drawn out.")]
            [Range(1f, OceanFoamStreakStretchMax)] public float oceanFoamStreakStretch = DefaultOceanFoamStreakStretch;
            [Tooltip("Who decides the SHAPE of a whitecap. At 1 the outline is the foam texture's own " +
                     "contours, so a cellular texture prints round blobs whatever the waves do. Lower " +
                     "it and the outline comes from the WAVE FIELD - pair with Crest Anisotropy for " +
                     "foam that runs in lines along the crests. 0 = the fold owns the shape entirely. " +
                     "This also fades out by itself with distance, because a tiled pattern loses its " +
                     "contrast as it mips and far-field foam would otherwise wash out bright and stop " +
                     "responding to these knobs.")]
            [Range(0f, 1f)] public float oceanFoamTextureInfluence = DefaultOceanFoamTextureInfluence;
            [Tooltip("How DIRECTIONAL wave breaking is. 0 reads the fold as an area change, which " +
                     "spreads foam into round patches and can miss a crest entirely when it stretches " +
                     "sideways as it compresses. 1 reads the strongest single-axis compression, so foam " +
                     "follows the crest LINE. Raising this finds more folds, so Coverage may want to " +
                     "come down a little to keep the same overall amount of foam.")]
            [Range(0f, 1f)] public float oceanFoamCrestAnisotropy = DefaultOceanFoamCrestAnisotropy;
            [Tooltip("Pushes foam GENERATION up onto the crest line. 0 = a cap can be born anywhere " +
                     "the surface folds, which scatters them through the wave field; 1 = only where " +
                     "the water is higher than everything around it. This is what turns whitecaps " +
                     "into lines running ALONG the waves instead of round patches.")]
            [Range(0f, 1f)] public float oceanFoamCrestGate = DefaultOceanFoamCrestGate;
            [Tooltip("Throws the foam FORWARD off the crest: 0 spreads it evenly over both faces, 1 " +
                     "puts it on the leading (downwind) face only. Breaks the symmetry that makes a " +
                     "whitecap look like a cap sitting on a bump.")]
            [Range(0f, 1f)] public float oceanFoamFaceBias = DefaultOceanFoamFaceBias;
            [Tooltip("Tints thin foam with the WATER'S own colour instead of painting it flat white, " +
                     "using the same extinction the fog and depth transmittance run on - so foam and " +
                     "sea keep agreeing when the water type is retuned. Dense foam still goes white. " +
                     "0 = flat tint (unchanged).")]
            [Range(0f, 1f)] public float oceanFoamDepthTint = DefaultOceanFoamDepthTint;
            [Tooltip("How many wave SCALES are allowed to make foam. 0 keeps the shipped damping, where " +
                     "the smallest ripples make none and two other scales are held back to stop the " +
                     "near water turning into foam soup; 1 lets every scale fold, the way Ceto sums all " +
                     "of its grids - more small-scale lace and filament inside each cap. Turn it down " +
                     "if close water starts reading as a uniform froth.")]
            [Range(0f, 1f)] public float oceanFoamCascadeMix = DefaultOceanFoamCascadeMix;
            [Tooltip("How much foam is left behind (deposited) after a crest passes. Higher = dense whitecaps " +
                     "linger and streak into trails; 0 = foam fades as fast as it forms. This is the main " +
                     "'deposit' control.")]
            [Range(0f, 1f)] public float oceanFoamDeposit = DefaultOceanFoamDeposit;
            [Tooltip("How fast deposited foam rolls downwind, streaking into windrows (as a fraction of wind " +
                     "speed). 0 = foam stays where it formed.")]
            [Range(0f, OceanFoamDriftMax)] public float oceanFoamDrift = DefaultOceanFoamDrift;
            [Tooltip("Ceiling on how dense foam can pile up before accumulation stops. Higher = thicker, " +
                     "longer-lasting deposits (1 = the original ceiling).")]
            [Range(OceanFoamMaxBuildupMin, OceanFoamMaxBuildupMax)] public float oceanFoamMaxBuildup = DefaultOceanFoamMaxBuildup;
        }

        // Premultiplied surface-current drift offset in METRES (current velocity * the SAME clock
        // published as _WaveTime), so the surface graph, the FFT reads and the caustic receiver
        // subtract one synchronized offset with no per-chain time uniform (shader pair:
        // OceanCurrentDrift, WaterWaves.hlsl).
        internal Vector4 OceanCurrentOffsetXZ
        {
            get
            {
                if (ocean.currentSpeed <= 0f) return Vector4.zero;
                float headingRadians = ocean.currentHeadingDegrees * Mathf.Deg2Rad;
                float driftMetres = ocean.currentSpeed * WaveTime;
                return new Vector4(Mathf.Cos(headingRadians) * driftMetres,
                                   Mathf.Sin(headingRadians) * driftMetres, 0f, 0f);
            }
        }

        // Same-named forwarding accessors so every reader (WaterUniformPublisher, the derived helpers
        // below, the clipmap/FFT setup, ShouldWindow/IsOceanClipmap) is unchanged. Names are the exact
        // former field names; the derived helpers (PascalCase, e.g. LargeWaveChoppiness) read these.
        internal bool openWater => ocean.openWater;
        internal float largeWaveAmplitude => ocean.largeWaveAmplitude;
        internal float largeWaveChoppiness => ocean.largeWaveChoppiness;
        internal float swellHeight => ocean.swellHeight;
        internal float swellWavelength => ocean.swellWavelength;
        internal float seaStateGusts => ocean.seaStateGusts;
        internal float seaStateSlicks => ocean.seaStateSlicks;
        internal bool seaStateFetchEnabled => ocean.seaStateFetchEnabled;
        internal float seaStateFetchStrength => ocean.seaStateFetchStrength;
        internal bool oceanAperiodicEnabled => ocean.oceanAperiodicEnabled;
        internal Texture2D oceanDirectionMap => ocean.oceanDirectionMap;
        internal float oceanDirectionMapSize => ocean.oceanDirectionMapSize;
        internal float oceanDirectionMapStrength => ocean.oceanDirectionMapStrength;
        internal float oceanAperiodicTileScale => ocean.oceanAperiodicTileScale;
        internal float swellHeadingOffsetDegrees => ocean.swellHeadingOffsetDegrees;
        internal float oceanWindTurbulence => ocean.oceanWindTurbulence;
        internal bool unboundedOcean => ocean.unboundedOcean;
        internal float edgeFeatherMeters => ocean.edgeFeatherMeters;
        internal int clipmapGridResolution => ocean.clipmapGridResolution;
        internal float clipmapOuterRadius => ocean.clipmapOuterRadius;
        internal float oceanDetailFalloff => ocean.oceanDetailFalloff;
        internal float horizonFadeDistance => ocean.horizonFadeDistance;
        internal Color horizonHazeColor => ocean.horizonHazeColor;
        internal float horizonHazeDensity => ocean.horizonHazeDensity;
        internal Color largeGodRayColor => ocean.largeGodRayColor;
        internal float largeGodRayDensity => ocean.largeGodRayDensity;
        internal int largeGodRaySteps => ocean.largeGodRaySteps;
        internal float largeGodRayAnisotropy => ocean.largeGodRayAnisotropy;
        internal float largeGodRayExtinction => ocean.largeGodRayExtinction;
        internal float largeGodRayCausticStrength => ocean.largeGodRayCausticStrength;
        internal float oceanFoamWindThreshold => ocean.oceanFoamWindThreshold;
        internal float oceanFoamCoverage => ocean.oceanFoamCoverage;
        internal float oceanFoamStrength => ocean.oceanFoamStrength;
        internal float oceanFoamFadeRate => ocean.oceanFoamFadeRate;
        internal Color oceanFoamColor => ocean.oceanFoamColor;
        internal float oceanFoamTileSize => ocean.oceanFoamTileSize;
        internal float oceanFoamFeather => ocean.oceanFoamFeather;
        internal float oceanFoamStreakStretch => ocean.oceanFoamStreakStretch;
        internal float oceanFoamTextureInfluence => ocean.oceanFoamTextureInfluence;
        internal float oceanFoamCrestAnisotropy => ocean.oceanFoamCrestAnisotropy;
        internal float oceanFoamCrestGate => ocean.oceanFoamCrestGate;
        internal float oceanFoamFaceBias => ocean.oceanFoamFaceBias;
        internal float oceanFoamDepthTint => ocean.oceanFoamDepthTint;
        internal float oceanFoamCascadeMix => ocean.oceanFoamCascadeMix;
        internal float oceanFoamDeposit => ocean.oceanFoamDeposit;
        internal float oceanFoamDrift => ocean.oceanFoamDrift;
        internal float oceanFoamMaxBuildup => ocean.oceanFoamMaxBuildup;

        // Legacy capture (scenes/prefabs from before this migration) -> copied once by MigrateOceanV2.
        // Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("openWater")] bool _legacyOpenWater = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveAmplitude")] float _legacyLargeWaveAmplitude = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeWaveChoppiness")] float _legacyLargeWaveChoppiness = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellHeight")] float _legacySwellHeight = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("swellWavelength")] float _legacySwellWavelength = DefaultSwellWavelength;
        [SerializeField, HideInInspector, FormerlySerializedAs("unboundedOcean")] bool _legacyUnboundedOcean = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("clipmapOuterRadius")] float _legacyClipmapOuterRadius = DefaultClipmapOuterRadius;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanDetailFalloff")] float _legacyOceanDetailFalloff = DefaultOceanDetailFalloff;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonFadeDistance")] float _legacyHorizonFadeDistance = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonHazeColor")] Color _legacyHorizonHazeColor = DefaultHorizonHazeColor;
        [SerializeField, HideInInspector, FormerlySerializedAs("horizonHazeDensity")] float _legacyHorizonHazeDensity = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayColor")] Color _legacyLargeGodRayColor = DefaultLargeGodRayColor;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayDensity")] float _legacyLargeGodRayDensity = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRaySteps")] int _legacyLargeGodRaySteps = DefaultLargeGodRaySteps;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayAnisotropy")] float _legacyLargeGodRayAnisotropy = DefaultLargeGodRayAnisotropy;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayExtinction")] float _legacyLargeGodRayExtinction = 0f;
        [SerializeField, HideInInspector, FormerlySerializedAs("largeGodRayCausticStrength")] float _legacyLargeGodRayCausticStrength = DefaultLargeGodRayCausticStrength;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamWindThreshold")] float _legacyOceanFoamWindThreshold = DefaultOceanFoamWindThreshold;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamCoverage")] float _legacyOceanFoamCoverage = DefaultOceanFoamCoverage;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamStrength")] float _legacyOceanFoamStrength = DefaultOceanFoamStrength;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamFadeRate")] float _legacyOceanFoamFadeRate = DefaultOceanFoamFadeRate;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamColor")] Color _legacyOceanFoamColor = Color.white;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamTileSize")] float _legacyOceanFoamTileSize = DefaultOceanFoamTileSize;
        [SerializeField, HideInInspector, FormerlySerializedAs("oceanFoamFeather")] float _legacyOceanFoamFeather = DefaultOceanFoamFeather;

        // Legacy reference for the analytic large-wave field and detail normals when ambient wind
        // coupling is off. The opt-in ambient mode uses the per-ocean reference speed below instead.
        const float LargeWaveReferenceWind = 3f;
        const float DefaultAmbientWindReferenceSpeed = 3f;
        const float AmbientWindReferenceSpeedMin = 0.01f;
        const float WindSeaHeightExponent = 1f;
        const float WindSeaLengthExponent = 2f / 3f;
        // Crest's _Chop range; beyond this the Gerstner surface self-intersects (pinch-through) and the
        // buoyancy inversion stops converging, so the knob is clamped here.
        const float LargeWaveChoppinessMax = 2f;
        // Edge guard defaults: 10 m rides out the default swell without visibly shrinking a lake;
        // the slider cap keeps the feather from eating a small bounded body whole.
        const float DefaultEdgeFeatherMeters = 10f;
        const float EdgeFeatherMetersMax = 50f;
        // Ocean whitecap foam defaults - subtle + wind-gated so the current look is unchanged until dialed.
        const float DefaultOceanFoamWindThreshold = 4f; // KWS FOAM_MIN_WIND: no whitecaps below ~4 m/s
        const float DefaultOceanFoamStrength = 1f;      // accumulation gain per unit fold
        const float DefaultOceanFoamFadeRate = 0.5f;    // exponential decay per second (lower = foam lingers)
        const float OceanFoamCoverageMax = 2f;          // beyond ~2 the whole surface foams; clamp the knob
        const float OceanFoamStrengthMax = 4f;          // sane upper bound for the build-up gain slider
        const float OceanFoamFadeRateMax = 4f;          // fastest useful decay; higher just flickers
        const float DefaultOceanFoamTileSize = 8f;      // metres per foam-pattern tile on the surface
        const float OceanFoamTileSizeMin = 0.5f;        // guard the divide + keep the pattern from collapsing
        const float DefaultOceanFoamFeather = 0.25f;    // dissolve softness of the foam texture black point
        // Both whitecap-SHAPE knobs default to the shipped look, so no authored ocean changes until
        // they are dialled up: stretch 1 = isotropic sampling, anisotropy 0 = the determinant fold.
        // WHITECAP SHAPE DEFAULTS. These used to default to the pre-2026-07-30 look (all off) purely
        // to avoid migrating authored scenes; with Bert re-tuning his three ocean demos by hand, they
        // now ship at the values that actually make a whitecap read as a breaking wave rather than a
        // round patch of texture. Each is still a plain 0..1 knob - dial any of them back to the
        // "off" value in the comment to recover the old behaviour exactly.
        const float DefaultOceanFoamStreakStretch = 3.5f;   // off = 1
        const float DefaultOceanFoamTextureInfluence = 0.35f;  // off = 1 (texture owns the outline)
        const float OceanFoamStreakStretchMax = 8f;     // past this the cells smear into unbroken lines
        const float DefaultOceanFoamCrestAnisotropy = 1f;   // off = 0 (area fold / determinant)
        const float DefaultOceanFoamCrestGate = 0.8f;   // off = 0 (foam anywhere the surface folds)
        const float DefaultOceanFoamFaceBias = 0.6f;    // off = 0 (symmetric about the crest)
        const float DefaultOceanFoamDepthTint = 0.45f;  // off = 0 (flat painted foam colour)
        // Ceto-like by default: every cascade's fold counts. off = 0 (the KWS/Crest per-cascade damping).
        const float DefaultOceanFoamCascadeMix = 1f;
        // Coverage comes DOWN with the anisotropy default: the smallest-eigenvalue fold FINDS crest
        // folds the determinant used to cancel out, so the same authored number now yields more foam.
        // Off = 1, which is the original saturate(1 - jacobian).
        const float DefaultOceanFoamCoverage = 0.75f;
        // Deposit knobs (promoted from OceanFft.compute #defines so they're art-tweakable). Defaults lean
        // toward MORE deposit than the old constants (slow-fade 0.25 -> deposit 0.85 = slow-fade 0.15).
        const float DefaultOceanFoamDeposit = 0.85f;    // dense-foam persistence; slowFadeFraction = 1 - this
        const float DefaultOceanFoamDrift = 0.08f;      // downwind roll speed as a fraction of wind speed
        const float OceanFoamDriftMax = 0.3f;           // fastest useful roll before foam smears across the tile
        const float DefaultOceanFoamMaxBuildup = 1f;    // accumulation ceiling (1 = the original FoamMax)
        const float OceanFoamMaxBuildupMin = 0.25f;
        const float OceanFoamMaxBuildupMax = 3f;
        internal float LargeWaveHeadingRad => windFromDegrees * Mathf.Deg2Rad;
        // The FFT sea state is normalised to an authored Significant Height in METRES, so scaling it by
        // the wind on top would make that number a lie. The wind coupling survives only on the ANALYTIC
        // generator, whose amplitude is a shape rather than a height and which has no spectrum to
        // normalise - i.e. on pools and bounded lakes, whose look is unchanged.
        internal float LargeWaveAmplitudeEffective => IsOceanClipmap
            ? largeWaveAmplitude
            : largeWaveAmplitude * (windSpeed / LargeWaveReferenceWind);
        internal bool WindDrivesAmbientSeaState => ocean.windDrivesAmbientSeaState;
        internal float AmbientWindReferenceSpeed => WindDrivesAmbientSeaState
            ? Mathf.Max(AmbientWindReferenceSpeedMin, ocean.ambientWindReferenceSpeed)
            : LargeWaveReferenceWind;
        float WindSeaGrowth(float exponent)
        {
            if (!WindDrivesAmbientSeaState) return 1f;
            float windRatio = Mathf.Max(0f, windSpeed) / AmbientWindReferenceSpeed;
            return Mathf.Pow(windRatio, exponent);
        }

        /// <summary>Wind sea height after optional ambient wind response. Swell remains independent.</summary>
        internal float SignificantWaveHeight => ocean.significantWaveHeight * WindSeaGrowth(WindSeaHeightExponent);
        /// <summary>Peak wavelength after optional wind response and the Gulliver/giant scale multiplier.</summary>
        internal float PeakWavelengthEffective => Mathf.Max(OceanPeakWavelengthMin,
                                                            ocean.peakWavelength * Mathf.Max(OceanWaveScaleMin, ocean.waveScale)
                                                            * WindSeaGrowth(WindSeaLengthExponent));
        internal float PeakSharpness => ocean.peakSharpness;
        internal float SeaDepth => ocean.seaDepth;
        internal float OceanCascadeReach => ocean.cascadeReach;

        /// <summary>Significant height (metres) of the whole open-water field, wind sea and swell together.</summary>
        /// <remarks>
        /// ONE definition of "how big is this sea", because more than one thing has to agree with it:
        /// the shoal band has to start outside it and the surf fronts cannot be smaller than it. The two
        /// trains have independent phases, so their variances add and the heights combine in quadrature -
        /// the same rule WaterOceanSpectrum applies when it normalises them.
        ///
        /// Only the FFT ocean carries an authored height in metres; on analytic bodies the long swell is
        /// still the only thing with a metre height, so it stands in unchanged there.
        /// </remarks>
        internal float OffshoreSignificantHeight => IsOceanClipmap
            ? Mathf.Sqrt(SignificantWaveHeight * SignificantWaveHeight + SwellHeight * SwellHeight)
            : SwellHeight;
        internal float LargeWaveChoppiness => largeWaveChoppiness;
        // Edge guard is a BOUNDED-body concept: an unbounded ocean's clipmap has no footprint border,
        // so the feather is forced off there (and pools never read it - _LargeBody gates the field).
        internal float LargeWaveEdgeFeatherEffective => (openWater && !unboundedOcean) ? edgeFeatherMeters : 0f;
        internal float SwellHeight => swellHeight;
        internal float SwellWavelength => swellWavelength;
        /// <summary>Packed gust/slick shading layer. HLSL pair: _SeaStateParams (WaterLargeWaves.hlsl).</summary>
        internal Vector4 SeaStateParams => new Vector4(seaStateGusts, seaStateSlicks,
                                                       SeaStateGustSpeedMps, SeaStateGustCellMeters);
        /// <summary>Absolute swell heading (radians): the wind heading plus the authored offset.
        /// Equals LargeWaveHeadingRad when the offset is 0, so undecoupled scenes are bit-identical.
        /// Consumers: _LargeSwellHeading (analytic band), OceanSwellDir (FFT spectrum), CPU mirror.</summary>
        internal float SwellHeadingRad => LargeWaveHeadingRad + swellHeadingOffsetDegrees * Mathf.Deg2Rad;
        internal float OceanWindTurbulence => oceanWindTurbulence;
        internal float OceanFoamWindThreshold => oceanFoamWindThreshold;
        internal float OceanFoamCoverage => oceanFoamCoverage;
        internal float OceanFoamStrength => oceanFoamStrength;
        internal float OceanFoamFadeRate => oceanFoamFadeRate;
        internal Color OceanFoamColor => oceanFoamColor;
        internal float OceanFoamTileSize => oceanFoamTileSize;
        internal float OceanFoamFeather => oceanFoamFeather;
        internal float OceanFoamStreakStretch => oceanFoamStreakStretch;
        internal float OceanFoamTextureInfluence => oceanFoamTextureInfluence;
        internal float OceanFoamCrestAnisotropy => oceanFoamCrestAnisotropy;
        internal float OceanFoamCrestGate => oceanFoamCrestGate;
        internal float OceanFoamFaceBias => oceanFoamFaceBias;
        internal float OceanFoamDepthTint => oceanFoamDepthTint;
        internal float OceanFoamCascadeMix => oceanFoamCascadeMix;
        internal float OceanFoamDeposit => oceanFoamDeposit;
        internal float OceanFoamDrift => oceanFoamDrift;
        internal float OceanFoamMaxBuildup => oceanFoamMaxBuildup;
        const float DefaultSwellWavelength = 140f;
        // Sea-state shading layer (gusts/slicks). The cell size sets both the gust patch scale and
        // the crosswind windrow spacing; the advection speed is a typical near-surface wind - gust
        // cells ride the wind (Dorman & Mollo-Christensen 1973), slicks drift slower (shader-side
        // fraction). Not exposed: patch scale/speed are physical character, not per-scene art.
        const float SeaStateGustCellMeters = 45f;
        const float SeaStateGustSpeedMps = 6f;
        // Sea-state defaults + guard rails. A 1.5 m / 60 m sea is a moderate open ocean (steepness ~1/40,
        // a plausible mid-fetch swell) and gamma 3.3 is JONSWAP's own nominal peak enhancement.
        const float DefaultSignificantWaveHeight = 1.5f;
        const float DefaultPeakWavelength = 60f;
        const float DefaultPeakSharpness = 3.3f;
        // The finest cascade's tile is the coarsest band times CascadeBandRatio^3 times the oversample;
        // below ~0.1 m of peak wavelength that tile is under a centimetre and the FFT is resolving
        // surface tension, which this dispersion relation does not model.
        const float OceanPeakWavelengthMin = 0.1f;
        // Gamma 1 IS Pierson-Moskowitz (no enhancement); the observed upper end in the literature is ~7.
        const float OceanPeakSharpnessMin = 1f;
        const float OceanPeakSharpnessMax = 7f;
        const float OceanWaveScaleMin = 0.001f;
        // 3 puts a 60 m sea's ranges at roughly 38 / 160 / 680 / 2880 m, which is within a hair of the
        // 40 / 160 / 800 / 4800 the hardcoded arrays gave before the cascades were derived - i.e. it
        // restores the reach the asset shipped with rather than inventing a new one.
        const float DefaultCascadeReach = 3f;
        const float OceanCascadeReachMin = 0.25f;
        const float OceanCascadeReachMax = 8f;
        // Chop now reaches the FFT, where it was previously hardwired to 1. Defaulting to 1 keeps a fresh
        // ocean looking like the one that shipped; MigrateSeaStateV10 lifts authored 0s for the same reason.
        const float DefaultLargeWaveChoppiness = 1f;
        // KWS's shipped WindTurbulence (KWS_Ocean.cs:16). At this value the downwind:upwind ENERGY ratio
        // is 6.7:1 - a sea that clearly marches while still crossing enough to read as natural.
        const float DefaultOceanWindTurbulence = 0.25f;
        // Default horizon haze target: pale sky-blue, but alpha 0 so out of the box the far ocean
        // dissolves into the REAL reflected sky (seamless). The rgb only matters once alpha is raised.
        static readonly Color DefaultHorizonHazeColor = new Color(0.7f, 0.8f, 0.9f, 0f);
        // Ocean god-ray defaults + guard rails. Density 0 keeps the whole shaft pass off out of the box.
        static readonly Color DefaultLargeGodRayColor = new Color(1f, 0.97f, 0.85f, 1f);
        const int LargeGodRayMinSteps = 8;
        const int LargeGodRayMaxSteps = 96;
        const int DefaultLargeGodRaySteps = 24;
        const float LargeGodRayMaxAnisotropy = 0.95f;
        const float DefaultLargeGodRayAnisotropy = 0.6f;
        const float DefaultLargeGodRayCausticStrength = 4f;

        // Geometry-clipmap authoring + guard rails. Grid resolution = cells per side of each LOD level;
        // the level count is derived so the outermost reaches clipmapOuterRadius (the horizon target).
        const int DefaultClipmapGridResolution = 64;
        // Beyond this the projected pattern is averaged away to a flat wash - the same "flattened to
        // near-DC" failure the shaft caustic term is documented against.
        internal const float ProjectionSoftenMax = 4f;
        const int ClipmapMinGridResolution = 8;
        const int ClipmapMaxLevels = 12;
        const int ClipmapMinLevels = 2;
        const int ClipmapSnapCellMultiple = 2;    // each level snaps to 2*cell so its even cells align with the coarser level
        const int ClipmapHoleMarginCells = 2;     // shrink each level's hole so it overlaps the finer level (no seam gap)
        const float ClipmapMorphBandFraction = 0.5f; // fraction of the annulus half-width used for the edge geomorph
        const float DefaultClipmapOuterRadius = 10000f;
        const float DefaultOceanDetailFalloff = 0.03f; // low: the clipmap resolves waves far out, so the
                                                       // swell rolls near to the horizon before band-limiting
        const float ClipmapMinRadius = 1e-3f;
        // The clipmap's central hole is set a little INSIDE the near-field patch so the patch (which
        // carries a depth bias) covers the seam; beyond the patch, only the clipmap draws.
        const float ClipmapPatchOverlap = 0.9f;
        // Frustum-cull AABB size for an ocean body: large enough to always intersect the frustum
        // (the ocean is everywhere), matching the clipmap mesh's own huge bounds.
        const float OceanCullBoundsSize = 1_000_000f;

        // True when this body renders its surface as an unbounded ocean clipmap: needs open water, the
        // opt-in flag, AND the sim window (its ripple fade is what keeps the far field clean). Bounded
        // lakes / pools are always false, so their render path is untouched.
        internal bool IsOceanClipmap => openWater && unboundedOcean && _windowed;

        // --- Derived geometry-clipmap dimensions (all pure functions of the two authored knobs:
        //     clipmapGridResolution and clipmapOuterRadius, plus the shared patch extent). ---
        // Cells per side, clamped and forced even (the annulus needs a symmetric hole).
        int ClipmapGridRes { get { int m = Mathf.Max(ClipmapMinGridResolution, clipmapGridResolution); return m + (m & 1); } }
        // Hole half-width in cells, shrunk by the overlap margin so each level overlaps the finer one.
        int ClipmapHoleHalfCells => Mathf.Max(1, ClipmapGridRes / 4 - ClipmapHoleMarginCells);
        // Finest cell size (metres) so the innermost level's hole sits just inside the near-field patch.
        float ClipmapBaseCell => (ClipmapPatchOverlap * SimHorizontalExtent) / ClipmapHoleHalfCells;
        // Level 0's outer reach (metres); each further level doubles it.
        float ClipmapLevel0Reach => (ClipmapGridRes / 2f) * ClipmapBaseCell;
        // Levels needed for the outermost to reach at least the horizon target.
        int ClipmapLevelCount
        {
            get
            {
                float ratio = Mathf.Max(1f, clipmapOuterRadius / Mathf.Max(ClipmapLevel0Reach, 1e-3f));
                int levels = 1 + Mathf.CeilToInt(Mathf.Log(ratio, 2f));
                return Mathf.Clamp(levels, ClipmapMinLevels, ClipmapMaxLevels);
            }
        }
        // World reach of the outermost level - drives the camera far plane so the horizon isn't clipped.
        float ClipmapOuterReach => ClipmapLevel0Reach * Mathf.Pow(2f, ClipmapLevelCount - 1);

        // Band-limit slope for the shader. 0 for non-ocean bodies -> no band-limit -> the bounded
        // open-water surface keeps its full spectrum everywhere (unchanged).
        internal float OceanDetailSlope => IsOceanClipmap ? oceanDetailFalloff : 0f;
        // Horizon fade distance for the shader. 0 for non-ocean bodies -> no fade (unchanged).
        internal float HorizonFadeDistance => IsOceanClipmap ? horizonFadeDistance : 0f;
        // Horizon haze for the shader: density gated to 0 for non-ocean bodies so pools/lakes are never
        // hazed; the colour passes through (inert while density is 0).
        internal float HorizonHazeDensity => IsOceanClipmap ? horizonHazeDensity : 0f;
        internal Color HorizonHazeColor => horizonHazeColor;
        // Ocean god rays for the shader: density gated to 0 for non-ocean bodies (pools/lakes never get
        // shafts from this pass); the rest pass through (inert while density is 0).
        internal Color LargeGodRayColor => largeGodRayColor;
        // THE TIER GATE, and it had been missing here entirely. _godRaysAllowed was read in exactly
        // ONE place (WaterVolume.Update.cs, for godRayRenderer) and only when !_windowed - but
        // IsOceanClipmap REQUIRES _windowed, so an ocean body never reached it and a tier that turns
        // god rays off could not switch the ocean shafts off at all. Folding it in HERE fixes both
        // halves at once, because this one property is what LargeBodyAtmosphereGate tests to decide
        // whether to enqueue the raymarch pass AND what WriteBodyUniforms publishes as the density -
        // so the pass stops being recorded and the uniform reads 0 from a single line.
        internal float LargeGodRayDensity
            => (IsOceanClipmap && _godRaysAllowed) ? largeGodRayDensity : 0f;
        // THE TIER IS A CEILING, NOT AN OVERRIDE. This used to return the authored field raw, so the
        // tier's step count never reached the ocean shader and Low marched the authored 24 exactly
        // like High (the pool shafts never had this bug - they read _godRaySteps directly, because
        // there is no authored per-body step count on that path). Min() keeps the author's intent
        // wherever the budget allows it: at High, authored 24 under a 32-step ceiling is still 24.
        internal float LargeGodRaySteps => Mathf.Min(largeGodRaySteps, _godRaySteps);
        internal float LargeGodRayAnisotropy => largeGodRayAnisotropy;
        internal float LargeGodRayExtinction => largeGodRayExtinction;
        internal float LargeGodRayCausticStrength => IsOceanClipmap ? largeGodRayCausticStrength : 0f;
        internal float LargeGodRayCausticSmooth => ocean.largeGodRayCausticSmooth;
        internal float LargeGodRayCausticDepthSoften => ocean.largeGodRayCausticDepthSoften;
        /// <summary>Strength of the from-air (through-a-carve-pane) shafts relative to the
        /// submerged view. Ocean-only, like every other shaft term.</summary>
        internal float LargeGodRayFromAir => IsOceanClipmap ? ocean.largeGodRayFromAir : 0f;
        /// <summary>Scene-light in-scatter inside the shaft march (the A2 lamp halos). Gated
        /// like the mirror shafts: only an active god-ray ocean can spend it, so the dedicated
        /// WATER_GODRAY_POINT_LIGHTS keyword this knob helps arm (WaterUniformPublisher) never
        /// turns on for a body whose march cannot run. LargeGodRayDensity already folds the
        /// tier's god-ray ceiling in, so a tier that suppresses shafts zeroes this too.</summary>
        internal float LargeGodRayLightScatter
            => (IsOceanClipmap && LargeGodRayDensity > 0f) ? ocean.largeGodRayLightScatter : 0f;
        internal float LargeCausticTimeScale => ocean.largeCausticTimeScale;
        internal float LargeCausticRippleScale => ocean.largeCausticRippleScale;
        internal float LargeCausticRippleStrength => ocean.largeCausticRippleStrength;

        /// <summary>Mip bias the caustic projection samples the caustic RT at - now the ARTIST
        /// term alone. The shafts do not use it; they keep their own LOD so the beam banding stays sharp.
        ///
        /// THERE USED TO BE AN AUTOMATIC log2(rt / grid) FLOOR HERE, AND REMOVING IT IS THE POINT.
        /// Its whole justification was that the generator flat-shaded ONE value per grid cell (the
        /// focus term was an area Jacobian read with ddx/ddy of a linearly interpolated attribute,
        /// which is constant over a triangle), so an RT larger than the grid stored each cell as a
        /// block of identical texels and the only cure was to sample back down to one texel per cell.
        /// The generators now measure that Jacobian PER VERTEX by central differences and pass it as
        /// a varying, so the stored field is C0-continuous: there are no blocks left to hide, and at
        /// 256 cells into a 1024 RT the field is already smoother than the sampling rate, so LOD 0
        /// cannot alias. Keeping the floor after that change did not preserve detail, it destroyed it -
        /// linear ramps average away where flat plateaus survived, which read as "blobby".
        ///
        /// If a future change ever makes the RT carry a discontinuous field again, the floor comes
        /// back WITH it - do not re-add one without that reason.</summary>
        internal float LargeCausticProjectionLod => ocean.largeCausticProjectionSoften;

        [Header("Water body (multi-instance)")]
        [Tooltip("Renderers driven by THIS body via a MaterialPropertyBlock (surface above/under, " +
                 "pool, god rays). Assigned by the scene builder.")]
        [SerializeField] internal Renderer surfaceAbove;
        [SerializeField] internal Renderer surfaceUnder;
        [SerializeField] internal Renderer poolRenderer;
        [SerializeField] internal Renderer godRayRenderer;

        // True when this body draws the analytic/procedural pool (tiles). Surface-only bodies have no
        // pool renderer, so the surface shader must not sample pool tiles in their refraction.
        internal bool HasProceduralPool => poolRenderer != null;
        [Tooltip("The primary body also mirrors its data to global shader state, the fallback " +
                 "for objects that don't carry a WaterMembership (which otherwise resolves each " +
                 "object's own containing body). Exactly one body should be primary.")]
        [SerializeField] private bool isPrimary = true;
        [Tooltip("On Play, automatically add a WaterMembership to any scene renderer that uses a " +
                 "water material (receiver / pool wall) and doesn't already have one, so a crate " +
                 "or custom pool is lit and fogged by the body it actually sits in - no manual " +
                 "wiring. Only the primary body runs the one-time scan.")]
        [SerializeField] private bool autoLinkReceivers = true;

        /// <summary>Whether this body is the primary one (mirrors its data to global shader
        /// state and acts as the fallback for objects without a WaterMembership).</summary>
        public bool IsPrimary { get => isPrimary; set => isPrimary = value; }

        [Header("Performance (Phase 3)")]
        [Tooltip("Quality tier asset scaling sim/caustic resolution and god-ray steps. Leave " +
                 "empty for the default (256/1024/24) look. Assigned by the scene builder.")]
        [SerializeField] private WaterQuality quality;
        [Tooltip("Pause a body's simulation, caustics and height readback - and stop drawing it - " +
                 "when it is off-screen OR beyond Activation Distance, and let only the nearest few " +
                 "bodies simulate at once. A single visible body is unaffected. Turn off to force " +
                 "this body to always simulate and render.")]
        [SerializeField] private bool enableCulling = true;
        [Tooltip("Bodies whose centre is farther than this from the camera pause their simulation " +
                 "(they hold their last state). Matches the camera far clip by default.")]
        [SerializeField] internal float activationDistance = CameraFarClip;

        /// <summary>Quality tier asset scaling sim/caustic resolution and god-ray steps.
        /// Read at startup; assign before the body enables.</summary>
        public WaterQuality Quality { get => quality; set => quality = value; }

        /// <summary>Pause this body's simulation and rendering when off-screen or beyond the
        /// activation distance.</summary>
        public bool EnableCulling { get => enableCulling; set => enableCulling = value; }
    }
}
