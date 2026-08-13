// WebGpuWater - the ONE writer for "apply a Jerlov water type" to a serialized WaterVolume.
// Shared by the inspector's "Apply water colour" button and the wizard's default ocean, so the
// tuned look numbers exist exactly once (reuse-never-rewrite: a second copy would drift).
// Writes are plain SerializedProperty sets; the CALLER commits them (ApplyModifiedProperties /
// OnInspectorGUI), so they join the caller's undo group. Editor-only.
#if UNITY_EDITOR
using UnityEditor;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterJerlovLookWriter
    {
        // Look defaults tuned by Bert (2026-08-08) - artistic calls layered on the physical
        // coefficients.
        // Scatter: the body colour carries the physical cross-type magnitude, but
        // VolumeSchlickPhase (WaterFog.hlsl) deliberately drops the 1/4pi normalisation, so the
        // sunward phase gain reaches ~5.4x at the default anisotropy 0.5 - a unit intensity blew
        // the derived colours out.
        internal const float ScatterIntensity = 0.1f;
        // Turbidity multiplier on the physical extinction (JerlovWaterTypes.PhysicalDensity = 1
        // is the neutral anchor), so applied water deliberately reads clearer than strictly
        // physical.
        internal const float FogDensityScale = 0.3f;
        // Slight art-directed haze through the surface (waterOpacity: 0 = clear, 1 = opaque).
        internal const float WaterOpacity = 0.2f;

        // Writes the preset into the appearance fields plus the stored water-type reference, and
        // enables Water Fog so the transmission tint is visible immediately.
        internal static void Write(SerializedObject serialized, JerlovWaterType type)
        {
            JerlovPreset preset = JerlovWaterTypes.Get(type);

            serialized.FindProperty(WaterVolumePropertyPaths.JerlovWaterType).enumValueIndex = (int)type;
            serialized.FindProperty(WaterVolumePropertyPaths.FogExtinction).colorValue = preset.Extinction;
            serialized.FindProperty(WaterVolumePropertyPaths.FogDensity).floatValue =
                FogDensityScale * JerlovWaterTypes.PhysicalDensity;
            serialized.FindProperty(WaterVolumePropertyPaths.FogColor).colorValue = preset.BodyColor;
            serialized.FindProperty(WaterVolumePropertyPaths.WaterFog).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.WaterOpacity).floatValue = WaterOpacity;
            serialized.FindProperty(WaterVolumePropertyPaths.ScatterColor).colorValue = preset.BodyColor;
            serialized.FindProperty(WaterVolumePropertyPaths.ScatterIntensity).floatValue = ScatterIntensity;
        }
    }
}
#endif
