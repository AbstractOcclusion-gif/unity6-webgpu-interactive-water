// WebGpuWater - WaterVolume inspector: CHUNK section (floating volumetric body).
// Turns the body into a self-contained chunk of water - the invert of an exclusion carve. Footprint
// None keeps it an ordinary body; Box / Sphere add the submerged fog shell (analytic primitive), Mesh
// takes the shell's entry/exit from an arbitrary closed mesh via the depth prepass (WaterVolume.Chunk.cs).
// The chunk fields are serialized-but-hidden on the runtime, so this custom section is their only
// inspector; the look knobs grey out until a footprint is chosen. Editor-only.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawChunkSection()
        {
            _showChunk = WaterEditorUI.Section("Chunk (Floating Body)", _showChunk, () =>
            {
                EditorGUILayout.HelpBox(ChunkHelp, MessageType.None);

                SerializedProperty footprint = Prop("chunkFootprint");
                if (footprint == null) return; // chunk runtime absent (shouldn't happen); nothing to draw
                EditorGUILayout.PropertyField(footprint, ChunkFootprintLabel);

                bool isChunk = footprint.enumValueIndex != (int)WaterVolume.ChunkFootprint.None;
                EditorGUI.BeginDisabledGroup(!isChunk);

                SerializedProperty disc = Prop("discSurface");
                if (disc != null) EditorGUILayout.PropertyField(disc, ChunkRoundSurfaceLabel);

                // Mesh footprint: the closed mesh the chunk fills. Its front/back faces feed the depth
                // prepass (WaterChunkDepthFeature); authored in pool space [-1,1], sized by extent.
                bool isMesh = footprint.enumValueIndex == (int)WaterVolume.ChunkFootprint.Mesh;
                if (isMesh)
                {
                    SerializedProperty mesh = Prop("chunkMesh");
                    if (mesh != null) EditorGUILayout.PropertyField(mesh, ChunkMeshLabel);
                }

                ChunkSlider("chunkFillLevel", ChunkFillLabel, ChunkUnitMin, ChunkUnitMax);
                ChunkSlider("chunkDensityBoost", ChunkDensityLabel, ChunkDensityMin, ChunkDensityMax);
                ChunkSlider("chunkRefraction", ChunkRefractionLabel, ChunkUnitMin, ChunkUnitMax);
                ChunkSlider("chunkReflectivity", ChunkReflectivityLabel, ChunkUnitMin, ChunkUnitMax);
                ChunkSlider("chunkMeniscus", ChunkMeniscusLabel, ChunkUnitMin, ChunkUnitMax);

                EditorGUI.EndDisabledGroup();
            });
        }

        // Slider bound to a serialized float (the runtime fields carry no [Range], so the range lives
        // here). Change-checked so undo/multi-edit behave like any other inspector field.
        void ChunkSlider(string path, GUIContent label, float min, float max)
        {
            SerializedProperty property = Prop(path);
            if (property == null) return;
            EditorGUI.BeginChangeCheck();
            float value = EditorGUILayout.Slider(label, property.floatValue, min, max);
            if (EditorGUI.EndChangeCheck()) property.floatValue = value;
        }

        const float ChunkDensityMin = 0.5f;
        const float ChunkDensityMax = 2f;
        const float ChunkUnitMin = 0f;
        const float ChunkUnitMax = 1f;

        static readonly GUIContent ChunkFootprintLabel = new GUIContent(
            "Footprint", "None = an ordinary body. Box / Sphere turn this body into a floating chunk of " +
            "water (analytic primitive). Mesh fills an arbitrary closed mesh via the depth prepass.");
        static readonly GUIContent ChunkRoundSurfaceLabel = new GUIContent(
            "Round Surface", "Build the water surface as a disc instead of a square grid (keep on for a " +
            "Sphere chunk so the surface reads round).");
        static readonly GUIContent ChunkMeshLabel = new GUIContent(
            "Mesh", "The closed mesh a Mesh-footprint chunk fills. Authored in pool space [-1,1] (like the " +
            "shell box) and sized by the body's extent; its front/back faces drive the depth prepass.");
        static readonly GUIContent ChunkFillLabel = new GUIContent(
            "Fill Level", "How full the chunk is: 0.5 = half (surface at the shape's centre, the default), " +
            "1 = brim-full (surface at the top), 0 = empty.");
        static readonly GUIContent ChunkDensityLabel = new GUIContent(
            "Density Boost", "Optical density of the chunk relative to the open water (baked into the " +
            "body's fog density).");
        static readonly GUIContent ChunkRefractionLabel = new GUIContent(
            "Refraction", "How strongly the body bends the scene behind it - a lens, strongest at a " +
            "sphere's rim. 0 = a flat window.");
        static readonly GUIContent ChunkReflectivityLabel = new GUIContent(
            "Reflectivity", "Fresnel sheen: how much the shell reflects the sky + sun toward grazing angles.");
        static readonly GUIContent ChunkMeniscusLabel = new GUIContent(
            "Meniscus", "Strength of the thin surface-tension line drawn along the waterline on the " +
            "near-plane 'at 0' frames. 0 = off.");
        const string ChunkHelp =
            "Turns this body into a self-contained floating CHUNK of water (the invert of an exclusion " +
            "carve). For a Sphere, keep Round Surface on and Water Fog (Look tab) off - the chunk renders " +
            "its own fog volume through the shell.";
    }
}
#endif
