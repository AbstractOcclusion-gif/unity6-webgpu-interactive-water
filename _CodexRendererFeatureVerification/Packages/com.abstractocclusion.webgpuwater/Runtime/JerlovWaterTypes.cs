// WebGpuWater - Jerlov physical water-colour presets.
//
// Each preset carries a per-channel absorption (the water's Fog Extinction at density 1) and the
// single-scattering-albedo body colour (the deep-water / scatter colour). Selecting a Jerlov water
// type therefore drives the see-through tint AND the body glow from one consistent physical source,
// instead of three hand-picked constants.
//
// DERIVATION (see the Phase-1 sign-off table in docs/):
//   Coefficients are reconstructed from the fitted IOP parameters of
//     Solonenko & Mobley (2015), "Inherent optical properties of Jerlov water types," Appl. Opt. 54(17):5392,
//   built into full a(lambda)/b(lambda) spectra with published reference data
//     - pure-water absorption: Pope & Fry (1997)
//     - chlorophyll absorption: Bricaud et al. (1998)
//     - particle scattering shapes: Kopelevich / Haltrin (per the 2015 paper)
//     - backscatter fraction bb/b = 0.0183: Petzold average particle (Mobley 1994)
//   then integrated against the CIE 1931 colour-matching functions (Wyman et al. 2013) under an
//   equal-energy illuminant and converted to linear sRGB. Values are NOT hand-tuned.
//
// The numbers below are data, labelled by water type; they are not adjustable "magic" constants.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Jerlov optical water types: clearest open ocean (I) to most turbid coastal (9C).</summary>
    public enum JerlovWaterType
    {
        OceanI,
        OceanIA,
        OceanIB,
        OceanII,
        OceanIII,
        Coastal1C,
        Coastal3C,
        Coastal5C,
        Coastal7C,
        Coastal9C,
        // Reproduces the pre-Jerlov constant-tint look for existing scenes. Best-effort, NOT
        // byte-identical: the old constants brightened blue (>1), which physical absorption can't do.
        Legacy,
    }

    /// <summary>
    /// One physically-derived water-colour preset. <see cref="Extinction"/> is per-channel absorption
    /// in 1/m (feeds Fog Extinction with density = 1); <see cref="BodyColor"/> is the single-scattering
    /// albedo colour (feeds the Scatter / Fog colour). Both are linear-space colours.
    /// </summary>
    public readonly struct JerlovPreset
    {
        public readonly string DisplayName;
        public readonly Color Extinction;
        public readonly Color BodyColor;

        public JerlovPreset(string displayName, Color extinction, Color bodyColor)
        {
            DisplayName = displayName;
            Extinction = extinction;
            BodyColor = bodyColor;
        }
    }

    /// <summary>Lookup table of the validated Jerlov presets.</summary>
    public static class JerlovWaterTypes
    {
        /// <summary>Extinction already carries the physical a (1/m); density is a turbidity multiplier on top.</summary>
        public const float PhysicalDensity = 1f;

        // Ordered to match JerlovWaterType so Get() can index directly.
        static readonly JerlovPreset[] Presets =
        {
            new JerlovPreset("Open ocean I",   ExtColor(0.6189f, 0.0582f, 0.0331f), Body(0.0000f, 0.0323f, 0.1223f)),
            new JerlovPreset("Open ocean IA",  ExtColor(0.6189f, 0.0587f, 0.0373f), Body(0.0000f, 0.0348f, 0.1144f)),
            new JerlovPreset("Open ocean IB",  ExtColor(0.6189f, 0.0584f, 0.0384f), Body(0.0000f, 0.0721f, 0.1741f)),
            new JerlovPreset("Open ocean II",  ExtColor(0.6189f, 0.0583f, 0.0380f), Body(0.0000f, 0.2805f, 0.5495f)),
            new JerlovPreset("Open ocean III", ExtColor(0.7124f, 0.0612f, 0.0514f), Body(0.0335f, 0.5801f, 0.8500f)),
            new JerlovPreset("Coastal 1C",     ExtColor(0.8125f, 0.0667f, 0.1043f), Body(0.0132f, 0.2439f, 0.2391f)),
            new JerlovPreset("Coastal 3C",     ExtColor(0.6587f, 0.0712f, 0.1508f), Body(0.0775f, 0.5448f, 0.4060f)),
            new JerlovPreset("Coastal 5C",     ExtColor(0.3035f, 0.0849f, 0.2280f), Body(0.1295f, 0.5584f, 0.3404f)),
            new JerlovPreset("Coastal 7C",     ExtColor(0.2790f, 0.1154f, 0.3611f), Body(0.2564f, 0.6742f, 0.3732f)),
            new JerlovPreset("Coastal 9C",     ExtColor(0.2865f, 0.1779f, 0.6630f), Body(0.3349f, 0.6065f, 0.2968f)),
            // Legacy: extinction reproduces the OLD transmission exactly (old _WaterExtinction
            // 0.45/0.15/0.08 x fogDensity 2); body reproduces the OLD above-water deep colour exactly
            // (old _WaterFogColor 0.10/0.30/0.40 x the removed ABOVEWATER_COLOR 0.25/1.0/1.25).
            new JerlovPreset("Legacy (classic look)", ExtColor(0.9000f, 0.3000f, 0.1600f), Body(0.0250f, 0.3000f, 0.5000f)),
        };

        /// <summary>Preset for a water type. Fails fast on an out-of-range enum value.</summary>
        public static JerlovPreset Get(JerlovWaterType type)
        {
            int index = (int)type;
            if (index < 0 || index >= Presets.Length)
                throw new System.ArgumentOutOfRangeException(nameof(type), type, "Unknown Jerlov water type.");
            return Presets[index];
        }

        // Opaque linear colours; alpha is unused by the fog/scatter shaders but kept at 1 for tidiness.
        static Color ExtColor(float r, float g, float b) => new Color(r, g, b, 1f);
        static Color Body(float r, float g, float b) => new Color(r, g, b, 1f);
    }
}
