// WebGpuWater - dry-region exclusion volume (analytic OBB, Phase 1).
// Marks an oriented box (transform pose + Size, scaled by lossyScale) in which the
// water surface must NOT render: a boat's hull interior, a submarine room, a house
// below sea level. Registers into a static list exactly like WaterInteractable;
// WaterUniformPublisher publishes the active volumes as global uniforms each frame
// and WaterSurface.shader discards fragments inside any of them (WaterExclusion.hlsl).
// Purely visual + camera-state: buoyancy, physics and the ripple sim are untouched -
// the hull still floats and still carves a wake.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways] // edit-mode preview: the water walls draw while authoring, like the water itself
    public class WaterExclusionVolume : MonoBehaviour
    {
        // GPU pair: EXCLUSION_MAX_VOLUMES in Runtime/Shaders/WaterExclusion.hlsl.
        // WaterWaveConstantsValidator guards the pair, so a drift is a console error.
        internal const int MaxVolumes = 4;

        // Floor on a box edge so a zero Size (or a zero parent scale) can never produce a
        // singular world->box matrix; well under any visually meaningful volume.
        const float MinEdgeLength = 1e-4f;

        static readonly List<WaterExclusionVolume> _active = new List<WaterExclusionVolume>();

        /// <summary>All currently enabled exclusion volumes, for the uniform publisher.
        /// Read-only to callers; membership is managed by OnEnable/OnDisable.</summary>
        public static IReadOnlyList<WaterExclusionVolume> Active => _active;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState()
        {
            _active.Clear();
            _warnedOverLimit = false;
        }

        // The over-limit drop is warned ONCE (editor only, re-armed when the count drops back
        // under the cap) - a per-frame publisher log would flood the console, silence would
        // hide the truncation. Never a silent cap.
        static bool _warnedOverLimit;

        [Tooltip("Edge lengths of the dry box in local units (like BoxCollider Size); the " +
                 "transform's position, rotation and scale place it in the world. The water " +
                 "surface is never rendered inside this box.")]
        public Vector3 size = Vector3.one;

        [Tooltip("Draw the carve boundary as WALLS OF WATER (the fog's lit in-scatter colour, " +
                 "depth-darkened): a bare volume then shows standing water at its edges instead " +
                 "of the unlit void. Turn OFF for volumes covered by real geometry - a boat hull " +
                 "or a room with windows - or the wall paints over their openings.")]
        public bool drawWaterWalls = true;

        [Tooltip("Scatter density of the water walls relative to the open fog. Slightly above 1 " +
                 "makes the carve boundary read denser than the surrounding water (the Crest-style " +
                 "carved presence); 1 blends seamlessly.")]
        [Range(0.5f, 2f)] public float wallScatterBoost = 1.2f;

        [Tooltip("Water-wall shader. Leave empty to resolve the packaged shader by name (works in " +
                 "the editor; a BUILD needs it assigned here or in Always Included Shaders, or the " +
                 "walls silently skip).")]
        [SerializeField] Shader wallShader;

        // ---- carve-boundary edge look (consumed by the fog's pane shading AND the wall) ------

        [Tooltip("Colour the carve-boundary edges shade TOWARD. Black is pure occlusion (the " +
                 "classic look); a deep water tint keeps the edges coloured instead of grey.")]
        [ColorUsage(false)] public Color edgeColor = Color.black;

        [Tooltip("Strength of the edge/corner occlusion on the carve boundary: 0 = no visible " +
                 "edges, 1 = corners fully saturated toward Edge Color.")]
        [Range(0f, 1f)] public float edgeIntensity = DefaultEdgeIntensity;

        [Tooltip("How far the edge shading reaches in from the box edges (spread), as a fraction " +
                 "of the box half-extent.")]
        [Range(0.01f, 0.5f)] public float edgeSpread = DefaultEdgeSpread;

        // The pre-knob hard-coded look: lerp(0.45, 1, edge) over a 0.12 half-extent band =
        // black edges at intensity 0.55, spread 0.12. Named so the defaults stay honest.
        const float DefaultEdgeIntensity = 0.55f;
        const float DefaultEdgeSpread = 0.12f;

        /// <summary>GPU encoding of the edge look: rgb = tint target, a = intensity.</summary>
        internal Vector4 EdgeColorUniform =>
            new Vector4(edgeColor.r, edgeColor.g, edgeColor.b, edgeIntensity);

        /// <summary>GPU encoding of the edge shape: x = spread (yzw reserved).</summary>
        internal Vector4 EdgeParamsUniform => new Vector4(edgeSpread, 0f, 0f, 0f);

        void OnEnable()
        {
            if (!_active.Contains(this)) _active.Add(this);
        }

        void OnDisable()
        {
            _active.Remove(this);
        }

        // ---- water walls (the drawn carve boundary) --------------------------------------
        // One shared unit-cube mesh + material for every volume (per-volume state rides the
        // MaterialPropertyBlock); DrawMesh enqueues into the normal render passes, so the walls
        // write depth (fog and god rays occlude against them like any opaque geometry).
        static Mesh _wallMesh;
        static Material _wallMaterial;
        MaterialPropertyBlock _wallProps;
        static readonly int ID_WallScatterBoost = Shader.PropertyToID("_WallScatterBoost");
        static readonly int ID_WallEdgeColor = Shader.PropertyToID("_WallEdgeColor");   // rgb tint, a = intensity
        static readonly int ID_WallEdgeSpread = Shader.PropertyToID("_WallEdgeSpread");

        // LateUpdate so the frame's transform motion (a floating room, physics) has settled
        // before the draw matrix is captured - the same reason WaterMembership binds late.
        void LateUpdate()
        {
            if (!drawWaterWalls) return;
            // The wall colour reads the water globals (fog, scatter, sun); with no water body
            // alive there is nothing meaningful to draw (and nothing to carve).
            if (WaterVolume.Primary == null) return;
            Material material = ResolveWallMaterial();
            if (material == null) return;

            if (_wallMesh == null) _wallMesh = WaterMeshBuilder.BuildUnitCube();
            _wallProps ??= new MaterialPropertyBlock();
            _wallProps.SetFloat(ID_WallScatterBoost, wallScatterBoost);
            _wallProps.SetVector(ID_WallEdgeColor, EdgeColorUniform);
            _wallProps.SetFloat(ID_WallEdgeSpread, edgeSpread);
            Graphics.DrawMesh(_wallMesh, BoxToWorldMatrix(), material, gameObject.layer,
                              null, 0, _wallProps);
        }

        // Prefer the serialized slot (a build must assign it - Shader.Find only reaches shaders
        // that ship); fall back to the packaged name so existing scenes preview in the editor
        // without re-wiring. Null -> the walls just don't draw (the carve itself is unaffected).
        Material ResolveWallMaterial()
        {
            if (_wallMaterial != null) return _wallMaterial;
            Shader shader = wallShader != null ? wallShader : Shader.Find(WaterShaderNames.WaterExclusionWall);
            if (shader == null) return null;
            // HideAndDontSave: an edit-mode preview must never serialize this into the scene.
            _wallMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _wallMaterial;
        }

        /// <summary>Unit-box -> world matrix: centre + rotation + size in one transform. Built
        /// from position/rotation/lossyScale (the BoxCollider approximation: shear from
        /// non-uniformly scaled rotated parents is ignored). Also the water-wall draw matrix.</summary>
        internal Matrix4x4 BoxToWorldMatrix()
        {
            Vector3 edge = Vector3.Scale(size, transform.lossyScale);
            edge = new Vector3(Mathf.Max(Mathf.Abs(edge.x), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.y), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.z), MinEdgeLength));
            return Matrix4x4.TRS(transform.position, transform.rotation, edge);
        }

        /// <summary>World -> unit-box matrix for this volume: the shader's inside test is
        /// abs(local) &lt;= 0.5 per axis.</summary>
        internal Matrix4x4 WorldToBoxMatrix() => BoxToWorldMatrix().inverse;

        // MUST equal EXCLUSION_BOX_HALF_EXTENT (WaterExclusion.hlsl): the unit-box half-extent
        // the world->box matrices map into, shared by the shader inside test and the CPU mirror.
        const float BoxHalfExtent = 0.5f;

        /// <summary>True when the world point lies inside ANY active exclusion volume - the CPU
        /// mirror of the shader's InsideExclusion (same WorldToBoxMatrix frame, same half-extent).
        /// Used by CPU-side gates that must agree with the render carve, e.g. the input router
        /// refusing to ripple/splash a click that lands inside a dry room.</summary>
        internal static bool ContainsPoint(Vector3 worldPoint)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                Vector3 local = _active[i].WorldToBoxMatrix().MultiplyPoint3x4(worldPoint);
                if (Mathf.Abs(local.x) <= BoxHalfExtent
                    && Mathf.Abs(local.y) <= BoxHalfExtent
                    && Mathf.Abs(local.z) <= BoxHalfExtent)
                    return true;
            }
            return false;
        }

        /// <summary>Fill the uniform buffers (each length MaxVolumes exactly) with up to
        /// MaxVolumes active volumes and return the count used. <paramref name="matrices"/> is
        /// required; <paramref name="edgeColors"/>/<paramref name="edgeParams"/> (the per-volume
        /// edge-look uniforms) may be null for consumers that only need the boxes (foam compute).
        /// Over the limit, the volumes NEAREST <paramref name="referencePoint"/> (the target
        /// camera) win and the drop is logged once - never a silent cap. Allocation-free:
        /// nearest-selection runs in place over the small active list.</summary>
        internal static int WriteVolumeUniforms(Matrix4x4[] matrices, Vector4[] edgeColors,
                                                Vector4[] edgeParams, Vector3 referencePoint)
        {
            ValidateBufferLength(matrices, nameof(matrices));
            if (edgeColors != null) ValidateBufferLength(edgeColors, nameof(edgeColors));
            if (edgeParams != null) ValidateBufferLength(edgeParams, nameof(edgeParams));

            int activeCount = _active.Count;
            if (activeCount <= MaxVolumes)
            {
                _warnedOverLimit = false;
                for (int i = 0; i < activeCount; i++)
                    WriteSlot(matrices, edgeColors, edgeParams, i, _active[i]);
                return activeCount;
            }

            WarnOverLimitOnce(activeCount);
            SelectNearest(matrices, edgeColors, edgeParams, referencePoint);
            return MaxVolumes;
        }

        static void ValidateBufferLength(System.Array buffer, string name)
        {
            if (buffer == null || buffer.Length != MaxVolumes)
                throw new System.ArgumentException(
                    $"WriteVolumeUniforms needs persistent buffers of exactly {MaxVolumes} " +
                    "entries (Unity locks a global array's size at its first set).", name);
        }

        // One volume -> one uniform slot: matrix always, edge look only for consumers that
        // bound the optional buffers. Keeps every writer path (in-limit + nearest) identical.
        static void WriteSlot(Matrix4x4[] matrices, Vector4[] edgeColors, Vector4[] edgeParams,
                              int slot, WaterExclusionVolume volume)
        {
            matrices[slot] = volume.WorldToBoxMatrix();
            if (edgeColors != null) edgeColors[slot] = volume.EdgeColorUniform;
            if (edgeParams != null) edgeParams[slot] = volume.EdgeParamsUniform;
        }

        // Selection-sort the MaxVolumes nearest volumes into the buffers without allocating:
        // the active list is tiny (a handful of rooms), so O(count * MaxVolumes) is nothing.
        static void SelectNearest(Matrix4x4[] matrices, Vector4[] edgeColors, Vector4[] edgeParams,
                                  Vector3 referencePoint)
        {
            for (int slot = 0; slot < MaxVolumes; slot++)
            {
                int best = -1;
                float bestSqr = float.MaxValue;
                for (int i = 0; i < _active.Count; i++)
                {
                    if (AlreadySelected(i, slot)) continue;
                    float sqr = (_active[i].transform.position - referencePoint).sqrMagnitude;
                    if (sqr >= bestSqr) continue;
                    bestSqr = sqr;
                    best = i;
                }
                _selected[slot] = best;
                WriteSlot(matrices, edgeColors, edgeParams, slot, _active[best]);
            }
        }

        // Scratch indices for SelectNearest (static: the publisher runs on the main thread).
        static readonly int[] _selected = new int[MaxVolumes];

        static bool AlreadySelected(int index, int slotsFilled)
        {
            for (int s = 0; s < slotsFilled; s++)
                if (_selected[s] == index) return true;
            return false;
        }

        static void WarnOverLimitOnce(int activeCount)
        {
            if (_warnedOverLimit) return;
            _warnedOverLimit = true;
#if UNITY_EDITOR
            Debug.LogWarning($"WaterExclusionVolume: {activeCount} volumes are enabled but the " +
                             $"shader supports {MaxVolumes}; only the {MaxVolumes} nearest the " +
                             "camera are excluded this frame. Disable some volumes, or raise " +
                             "MaxVolumes together with EXCLUSION_MAX_VOLUMES (validator-paired).");
#endif
        }

#if UNITY_EDITOR
        // Editor-only wire box so the dry region is visible while authoring.
        static readonly Color GizmoColor = new Color(0f, 0.85f, 0.9f, 0.9f); // package cyan

        void OnDrawGizmos()
        {
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation,
                                          Vector3.Scale(size, transform.lossyScale));
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
