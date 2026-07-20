// WebGpuWater - volumetric water CHUNK (the INVERT of WaterExclusionVolume).
// Where the exclusion volume carves water OUT of the surface, a chunk FILLS a primitive with
// standing water in otherwise-dry space: a detached body ("piece of cake") whose faces show the
// water column, tinting the scene behind by the water's own optical depth. Colour and waves are
// INHERITED from the primary WaterVolume's published globals (fog / extinction / scatter / sun +
// the wavy waterline field), so a chunk is a floating sample of the same sea.
//
// Phase 1-2: a BOX or an inscribed SPHERE primitive, both drawn with the same unit-cube proxy mesh
// and box-to-world matrix (the shape is resolved analytically in the wall shader). The water fills
// the body up to a flat cut plane (Fill Level) and refracts the backdrop (Refraction); cylinder /
// wedge / baked-mesh primitives follow.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways] // edit-mode preview: the chunk draws while authoring, like the water itself
    public class WaterChunkVolume : MonoBehaviour
    {
        // GPU pair: CHUNK_SHAPE_* in Runtime/Shaders/WaterChunkPrimitive.hlsl (published as _ChunkShape).
        public enum ChunkShape { Box, Sphere }

        // Floor on a box edge so a zero Size (or a zero parent scale) can never produce a singular
        // world->box matrix; well under any visually meaningful volume.
        const float MinEdgeLength = 1e-4f;

        // The unit shape spans [-0.5, 0.5] per axis: this is the centre-to-top offset the Fill Level
        // maps against, so fill 1 puts the cut plane at the top face and fill 0.5 at the centre.
        const float UnitCubeCenterOffset = 0.5f;

        const float DefaultDensityBoost = 1f;
        const float DefaultFillLevel = 1f;      // a full body (a whole orb / a brim-full box) by default
        const float DefaultRefraction = 0.5f;   // a visible lens without over-bending
        const float DefaultReflectivity = 0.6f; // a lit surface without a mirror-hard sheen

        [Tooltip("Primitive the water fills. Box = an oriented box; Sphere = the box's inscribed " +
                 "sphere (a floating orb; lower Fill Level for a cut bowl - the 'piece of cake').")]
        public ChunkShape shape = ChunkShape.Sphere;

        [Tooltip("Edge lengths of the bounding box in local units (like BoxCollider Size); the " +
                 "transform's position, rotation and scale place it in the world. A Sphere is inscribed.")]
        public Vector3 size = Vector3.one;

        [Tooltip("Where the flat water top sits within the body: 1 = brim-full, 0.5 = half-full " +
                 "(a hemisphere bowl for a Sphere). The top rides the inherited waves around this level.")]
        [Range(0.05f, 1f)] public float fillLevel = DefaultFillLevel;

        [Tooltip("Optical density of the chunk relative to the open water. Above 1 reads denser " +
                 "(murkier) than the surrounding sea; 1 matches it exactly.")]
        [Range(0.5f, 2f)] public float densityBoost = DefaultDensityBoost;

        [Tooltip("How strongly the body bends the scene behind it (a lens - strongest at a sphere's " +
                 "rim). 0 = no refraction (a flat window). Turn down on cheaper tiers.")]
        [Range(0f, 1f)] public float refraction = DefaultRefraction;

        [Tooltip("Fresnel sheen: how much the surface reflects the sky + sun toward grazing angles. " +
                 "0 = no reflection (pure refracted body). Cheap ALU, so it stays on every tier.")]
        [Range(0f, 1f)] public float reflectivity = DefaultReflectivity;

        [Tooltip("Chunk-wall shader. Leave empty to resolve the packaged shader by name (an editor " +
                 "auto-adds it to Always Included Shaders so builds work too). A wrong shader here is " +
                 "ignored with a warning - it must be " + WaterShaderNames.WaterChunkWall + ".")]
        [SerializeField] Shader wallShader;

        // One shared unit-cube proxy mesh + material for every chunk (per-chunk state rides the
        // MaterialPropertyBlock), matching WaterExclusionVolume's draw path.
        static Mesh _boxMesh;
        static Material _material;
        MaterialPropertyBlock _props;
        static readonly int ID_Shape = Shader.PropertyToID("_ChunkShape");
        static readonly int ID_DensityBoost = Shader.PropertyToID("_ChunkDensityBoost");
        static readonly int ID_Refraction = Shader.PropertyToID("_ChunkRefraction");
        static readonly int ID_Reflectivity = Shader.PropertyToID("_ChunkReflectivity");
        static readonly int ID_TopY = Shader.PropertyToID("_ChunkTopY");

        // LateUpdate so the frame's transform motion (a moving chunk, physics) has settled before the
        // draw matrix is captured - the same reason WaterExclusionVolume binds late. With no water body
        // alive there are no published globals (fog / sun / waterline) to shade the chunk with.
        void LateUpdate()
        {
            if (WaterVolume.Primary == null) return;
            Material material = ResolveMaterial();
            if (material == null) return;

            if (_boxMesh == null) _boxMesh = WaterMeshBuilder.BuildUnitCube();
            _props ??= new MaterialPropertyBlock();

            Matrix4x4 boxToWorld = BoxToWorldMatrix();
            _props.SetFloat(ID_Shape, (float)shape);
            _props.SetFloat(ID_DensityBoost, densityBoost);
            _props.SetFloat(ID_Refraction, refraction);
            _props.SetFloat(ID_Reflectivity, reflectivity);
            _props.SetFloat(ID_TopY, CutPlaneWorldY(boxToWorld));
            Graphics.DrawMesh(_boxMesh, boxToWorld, material, gameObject.layer, null, 0, _props);
        }

        // The flat water top as a world Y: the Fill Level maps to a box-local height in [-0.5, 0.5]
        // and rides through the volume transform, so a rotated/non-uniform chunk still caps level.
        // The shader adds the primary body's wave SHAPE around this Y (inherited waves).
        float CutPlaneWorldY(Matrix4x4 boxToWorld)
        {
            float boxLocalY = fillLevel - UnitCubeCenterOffset; // fill 1 -> +0.5 (top), 0.5 -> 0 (centre)
            return boxToWorld.MultiplyPoint3x4(new Vector3(0f, boxLocalY, 0f)).y;
        }

        Material ResolveMaterial()
        {
            if (_material != null) return _material;
            Shader shader = ResolveWallShader();
            if (shader == null) return null;
            // HideAndDontSave: an edit-mode preview must never serialize into the scene.
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _material;
        }

        // Accept ONLY the chunk wall shader. A wrong shader in the slot (e.g. WaterExclusionWall,
        // which integrates the DRY carve span and needs registered exclusion volumes) silently blanks
        // the chunk, so anything else warns and falls back to the packaged shader resolved by name.
        Shader ResolveWallShader()
        {
            if (wallShader != null)
            {
                if (wallShader.name == WaterShaderNames.WaterChunkWall) return wallShader;
#if UNITY_EDITOR
                Debug.LogWarning($"[WebGpuWater] WaterChunkVolume on '{name}' has the wrong wall shader " +
                                 $"assigned ('{wallShader.name}'); it must be " +
                                 $"'{WaterShaderNames.WaterChunkWall}'. Falling back to that shader - " +
                                 "clear the slot to silence this.");
#endif
            }
            return Shader.Find(WaterShaderNames.WaterChunkWall);
        }

        /// <summary>Unit-box -> world matrix: centre + rotation + size in one transform. Built from
        /// position/rotation/lossyScale (the BoxCollider approximation: shear from non-uniformly scaled
        /// rotated parents is ignored). Also the chunk-wall draw matrix and the sphere's placement.</summary>
        internal Matrix4x4 BoxToWorldMatrix()
        {
            Vector3 edge = Vector3.Scale(size, transform.lossyScale);
            edge = new Vector3(Mathf.Max(Mathf.Abs(edge.x), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.y), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.z), MinEdgeLength));
            return Matrix4x4.TRS(transform.position, transform.rotation, edge);
        }

#if UNITY_EDITOR
        // Editor-only wire shape so the water region is visible while authoring.
        static readonly Color GizmoColor = new Color(0.2f, 0.6f, 1f, 0.9f); // chunk blue

        void OnDrawGizmos()
        {
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation,
                                          Vector3.Scale(size, transform.lossyScale));
            if (shape == ChunkShape.Sphere) Gizmos.DrawWireSphere(Vector3.zero, UnitCubeCenterOffset);
            else Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
