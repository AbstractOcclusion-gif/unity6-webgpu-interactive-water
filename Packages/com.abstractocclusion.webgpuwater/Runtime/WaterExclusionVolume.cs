// WebGpuWater - dry-region exclusion volume (analytic primitive: box or sphere/ellipsoid).
// Marks a region (transform pose + Size, scaled by lossyScale) in which the water surface
// must NOT render: a boat's hull interior, a submarine room, a house below sea level, a
// diving bell. Registers into a static list exactly like WaterInteractable;
// WaterUniformPublisher publishes the active volumes as global uniforms each frame
// and WaterSurface.shader discards fragments inside any of them (WaterExclusion.hlsl).
// Purely visual + camera-state: buoyancy, physics and the ripple sim are untouched -
// the hull still floats and still carves a wake.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering; // RenderQueue: the wall's explicit sort offset (see WallRenderQueueOffset)

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways] // edit-mode preview: the water walls draw while authoring, like the water itself
    public class WaterExclusionVolume : MonoBehaviour
    {
        /// <summary>The shape a volume carves with. Box and Sphere are ANALYTIC and their ordinals
        /// ARE the shader's PRIMITIVE_SHAPE_* selector values (WaterPrimitiveShape.hlsl) - the
        /// publisher sends the ordinal straight through as a float, so the two must never drift
        /// apart. Mesh is not a primitive at all: it carves from a depth prepass of its real
        /// silhouette (WaterExclusionMesh.hlsl) and falls back to <see cref="meshProxy"/> for the
        /// queries a camera-space prepass cannot answer.</summary>
        public enum Shape
        {
            Box = 0,
            Sphere = 1,
            Mesh = 2,
        }

        // GPU pair: PRIMITIVE_SHAPE_SPHERE in Runtime/Shaders/WaterPrimitiveShape.hlsl. Named as
        // a const (rather than left implicit in the enum) so WaterWaveConstantsValidator can guard
        // the ordinal against the shader's selector the same way it guards MaxVolumes.
        const int SphereShapeId = 1;

        // GPU pair: EXCLUSION_SHAPE_MESH in Runtime/Shaders/WaterExclusion.hlsl - the selector the
        // wall carries for a mesh volume. Same reason as SphereShapeId: validator-guarded.
        const int MeshShapeId = 2;

        // GPU pair: EXCLUSION_MAX_VOLUMES in Runtime/Shaders/WaterExclusion.hlsl.
        // WaterWaveConstantsValidator guards the pair, so a drift is a console error.
        internal const int MaxVolumes = 4;

        // Floor on an edge so a zero Size (or a zero parent scale) can never produce a
        // singular world->local matrix; well under any visually meaningful volume.
        const float MinEdgeLength = 1e-4f;

        // Half-extent of the shader's unit local space: EXCLUSION_LOCAL_HALF_EXTENT in
        // WaterExclusion.hlsl (WaterWaveConstantsValidator guards the pair). The CPU point test
        // below must use the SAME convention as the shader's, or a click could ripple water the
        // GPU has carved away.
        const float LocalHalfExtent = 0.5f;

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

        [Tooltip("Shape the dry region is carved with. Box is an oriented box; Sphere is that " +
                 "box's INSCRIBED ball, so a non-uniform Size (or parent scale) makes it an " +
                 "ellipsoid; Mesh carves an arbitrary closed mesh. Box and Sphere are fully " +
                 "analytic - walls, fog carve, sun shadow column and particle culling all follow " +
                 "them exactly. Mesh needs the WaterExclusionDepth render feature on your URP " +
                 "renderer, and falls back to Mesh Proxy for the sun shadow column, particle " +
                 "culling and the CPU point test.")]
        public Shape shape = Shape.Box;

        [Tooltip("Closed mesh a Mesh-shape volume carves. Authored in the volume's LOCAL space " +
                 "spanning -0.5..0.5 (like the unit cube a Box carves), then placed and scaled by " +
                 "the transform and Size. Convex meshes are exact; a concave mesh's internal " +
                 "cavity biases the exit face.")]
        public Mesh carveMesh;

        [Tooltip("Analytic stand-in for a Mesh volume in the queries a camera-space prepass cannot " +
                 "answer: the sun shadow column, particle culling, and the CPU point test used by " +
                 "input routing. Pick whichever of Box or Sphere better matches the mesh's bulk.")]
        public Shape meshProxy = Shape.Box;

        [Tooltip("Extents of the dry region in local units (like BoxCollider Size): edge lengths " +
                 "for a Box, DIAMETERS for a Sphere. The transform's position, rotation and scale " +
                 "place it in the world. The water surface is never rendered inside it.")]
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

        [Tooltip("Strength of the boundary occlusion on the carve: 0 = no visible outline, " +
                 "1 = the outline fully saturated toward Edge Color. A Box shades its edges and " +
                 "corners; a Sphere has none, so it shades its silhouette RIM instead - both are " +
                 "the shape's visible outline.")]
        [Range(0f, 1f)] public float edgeIntensity = DefaultEdgeIntensity;

        [Tooltip("How far the boundary shading reaches in from the outline (spread), as a fraction " +
                 "of the half-extent. One value covers both shapes: a Box measures it across its " +
                 "faces, a Sphere across its silhouette.")]
        [Range(0.01f, 0.5f)] public float edgeSpread = DefaultEdgeSpread;

        // The pre-knob hard-coded look: lerp(0.45, 1, edge) over a 0.12 half-extent band =
        // black edges at intensity 0.55, spread 0.12. Named so the defaults stay honest.
        const float DefaultEdgeIntensity = 0.55f;
        const float DefaultEdgeSpread = 0.12f;

        // ---- sun shadow (this volume's own occlusion of the sunlight) --------------------

        [Tooltip("Let this volume BLOCK the sun, so the water beyond it reads shadowed: god-ray " +
                 "shafts stop at it and the fog's in-scatter darkens along its shadow column. ON " +
                 "suits a SEALED carve that really does block the light - a hull, a diving bell, a " +
                 "walled room. Turn OFF for a carve the light passes straight through - parted " +
                 "water, an open trench, a roofless room - where that shaft reads as an artifact. " +
                 "Does not change the carve itself: the water stays cut either way.")]
        public bool castsSunShadow = true;

        // ---- particle handling (foam/spray sprites, splash crown + droplets) -------------

        [Tooltip("Cull foam, spray and splash particles inside this volume. Turn OFF for a " +
                 "volume that only carves the surface - a room with open windows can let " +
                 "spray blow through its dry interior.")]
        public bool affectParticles = true;

        [Tooltip("Softness of the particle cut at the volume boundary, as a fraction of the " +
                 "half-extent: sprites dissolve over this shell just inside the surface " +
                 "instead of clipping on a razor edge. 0 = hard clip exactly on the surface.")]
        [Range(0f, 0.5f)] public float particleFadeBand = DefaultParticleFadeBand;

        [Tooltip("How fast simulated foam/spray already inside dies when this volume sweeps " +
                 "over it (a moving hull plowing through its own bow plume). 1 = the stock " +
                 "dissolve; higher snuffs a swept plume quicker, lower lets it linger.")]
        [Range(0.25f, 4f)] public float particleDissolveSpeed = 1f;

        // Thin enough that the dissolve reads as a soft edge, not a hollow shell.
        const float DefaultParticleFadeBand = 0.06f;

        /// <summary>The analytic shape the shader's closed-form kernels see for this volume: the
        /// authored shape, or - for a Mesh volume, which those kernels cannot evaluate - its
        /// proxy. A Mesh Proxy left on Mesh would recurse into nothing, so it degrades to Box.</summary>
        Shape AnalyticShape
        {
            get
            {
                if (shape != Shape.Mesh) return shape;
                return meshProxy == Shape.Sphere ? Shape.Sphere : Shape.Box;
            }
        }

        /// <summary>GPU encoding of the shape: x = the PRIMITIVE_SHAPE_* selector the analytic
        /// kernels use (a mesh volume sends its PROXY here), y = 1 for a Mesh volume so the
        /// camera-ray consumers know to carve from the depth prepass instead, z = 1 when the volume
        /// does NOT block the sun (<see cref="castsSunShadow"/>), w reserved for a future per-shape
        /// parameter (a capsule's radius, a wedge's angle). The sun flag is stored INVERTED so a
        /// zero slot still casts the shadow every pre-flag scene authored - the same polarity rule
        /// x and y already follow.</summary>
        internal Vector4 ShapeUniform => new Vector4(
            (float)AnalyticShape, shape == Shape.Mesh ? 1f : 0f, castsSunShadow ? 0f : 1f, 0f);

        /// <summary>The closed mesh a Mesh-shape volume carves, or null for any other shape (and
        /// for a Mesh volume with no mesh assigned - which is warned about, never silent).</summary>
        internal Mesh CarveMesh => shape == Shape.Mesh ? carveMesh : null;

        /// <summary>True when this volume can rasterise a prepass silhouette WITHOUT building
        /// anything: every analytic shape owns a shared unit mesh, so only a Mesh volume with
        /// nothing assigned answers false. Kept side-effect-free because the render feature calls
        /// it per camera - the lazy unit-mesh build belongs to PrepassMesh, on the main thread.</summary>
        bool HasPrepassGeometry => shape != Shape.Mesh || carveMesh != null;

        /// <summary>The mesh this volume rasterises into the exclusion depth prepass: its own carve
        /// mesh for a Mesh volume, the shared unit sphere/cube for the analytic shapes. Box and
        /// Sphere are NOT mesh-less - the wall already draws exactly this mesh every frame
        /// (ResolveWallMesh) - so covering every shape costs a draw, not a new geometry tier.
        /// Null only for a Mesh volume with nothing assigned.</summary>
        internal Mesh PrepassMesh => ResolveWallMesh();

        /// <summary>True when any enabled volume can rasterise a prepass silhouette - the render
        /// feature's self-gate. Wider than the old mesh-only gate on purpose: a consumer that takes
        /// the carve boundary from raster must not have to ask which tier the volume came from.</summary>
        internal static bool AnyPrepassVolumeActive()
        {
            for (int i = 0; i < _active.Count; i++)
                if (_active[i].HasPrepassGeometry) return true;
            return false;
        }

        /// <summary>Fill <paramref name="destination"/> with every enabled volume that has a prepass
        /// mesh. Clears first; a Mesh volume with nothing assigned is skipped (and warned about).
        /// Reading PrepassMesh HERE - from the pass's RecordRenderGraph, on the main thread - is
        /// deliberate: it forces the lazy unit-mesh build now, so the render function that runs
        /// later only ever hits an already-built cache.</summary>
        internal static void CollectPrepassVolumes(List<WaterExclusionVolume> destination)
        {
            destination.Clear();
            for (int i = 0; i < _active.Count; i++)
            {
                WaterExclusionVolume volume = _active[i];
                if (volume.PrepassMesh != null) destination.Add(volume);
                else volume.WarnMissingMeshOnce(); // only a Mesh volume can be mesh-less
            }
        }

        /// <summary>How many enabled volumes carve from a mesh, for the publisher's
        /// _ExclusionMeshCount gate (0 = the consumers skip the prepass reads entirely). Counts MESH
        /// volumes only, deliberately: the prepass now rasterises every shape, but the CONSUMERS
        /// still take Box/Sphere from the analytic kernels, so widening this gate would make an
        /// existing box scene depend on a render feature the user may never have installed.</summary>
        internal static int MeshVolumeCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _active.Count; i++)
                    if (_active[i].CarveMesh != null) count++;
                return count;
            }
        }

        // A Mesh volume with no mesh carves nothing at all, which on screen is indistinguishable
        // from the volume being disabled - so say so, once per volume, instead of leaving the
        // author to guess. Editor-only: a shipped build cannot fix the assignment anyway.
        bool _warnedMissingMesh;

        void WarnMissingMeshOnce()
        {
#if UNITY_EDITOR
            if (_warnedMissingMesh) return;
            _warnedMissingMesh = true;
            Debug.LogWarning($"WaterExclusionVolume '{name}': Shape is Mesh but no Carve Mesh is " +
                             "assigned, so this volume carves nothing. Assign a closed mesh, or " +
                             "switch Shape to Box or Sphere.", this);
#endif
        }

        /// <summary>GPU encoding of the edge look: rgb = tint target, a = intensity.</summary>
        internal Vector4 EdgeColorUniform =>
            new Vector4(edgeColor.r, edgeColor.g, edgeColor.b, edgeIntensity);

        /// <summary>GPU encoding of the edge shape + particle handling: x = edge spread,
        /// y = affect-particles flag, z = particle fade band, w = dissolve speed.</summary>
        internal Vector4 EdgeParamsUniform => new Vector4(
            edgeSpread, affectParticles ? 1f : 0f, particleFadeBand, particleDissolveSpeed);

        void OnEnable()
        {
            if (!_active.Contains(this)) _active.Add(this);
        }

        void OnDisable()
        {
            _active.Remove(this);
        }

        // ---- water walls (the drawn carve boundary) --------------------------------------
        // One shared mesh PER SHAPE + one shared material PER WALL SHADER (per-volume state
        // rides the MaterialPropertyBlock); DrawMesh enqueues into the normal render passes.
        // The wall does NOT write depth (WaterExclusionWall.shader is ZWrite Off and ships no
        // depth pass, on purpose - see its header): the fullscreen fog and the god rays must
        // integrate to the REAL scene through the carve, and the transparent veil tints on top.
        //
        // The wall shares the Transparent queue with the water surface, and inside a volume its
        // bounds centre sits at or behind the eye - so URP's back-to-front CommonTransparent sort
        // key is degenerate there and surface-vs-wall draw order FLIPPED as the camera moved. The
        // surface is ZWrite On and the wall ZWrite Off / ZTest LEqual, so the flip was visible:
        // wall-then-surface hid the wall, surface-then-wall let it tint over. An explicit offset
        // makes the order a fact instead of a distance comparison. Same value, same reason, as the
        // chunk shell's ChunkShellRenderQueueOffset (WaterVolume.Chunk.cs) - the two boundaries are
        // the same kind of draw and must not disagree about where they sit.
        const int WallRenderQueueOffset = 10;

        static Mesh _wallCubeMesh;
        static Mesh _wallSphereMesh;
        // Keyed by the RESOLVED shader. A single shared material was wrong: wallShader is a
        // per-instance [SerializeField], so the first volume to draw handed ITS shader to every
        // other volume in the scene. Volumes that agree on a shader still share one material.
        static readonly Dictionary<Shader, Material> _wallMaterials = new Dictionary<Shader, Material>();

        // Shader.Find is not free and LateUpdate runs every frame, so the packaged fallback is
        // resolved once rather than on every draw of a volume with an empty slot.
        static Shader _packagedWallShader;
        MaterialPropertyBlock _wallProps;
        static readonly int ID_WallShape = Shader.PropertyToID("_WallShape");
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
            Mesh wallMesh = ResolveWallMesh();
            if (wallMesh == null) return; // a Mesh volume with nothing assigned; warned elsewhere

            _wallProps ??= new MaterialPropertyBlock();
            _wallProps.SetFloat(ID_WallShape, (float)shape);
            _wallProps.SetFloat(ID_WallScatterBoost, wallScatterBoost);
            _wallProps.SetVector(ID_WallEdgeColor, EdgeColorUniform);
            _wallProps.SetFloat(ID_WallEdgeSpread, edgeSpread);
            Graphics.DrawMesh(wallMesh, ShapeToWorldMatrix(), material, gameObject.layer,
                              null, 0, _wallProps);
        }

        // The wall mesh IS the carve boundary, so it must be the authored shape itself: the
        // shader shades the fragment where it sits, and every term downstream (waterline clip,
        // veil span, downwelling, reconstruction) assumes that point lies ON the boundary.
        Mesh ResolveWallMesh()
        {
            // A mesh volume's boundary IS its mesh - drawing the proxy would paint a box where the
            // depth prepass carved a silhouette.
            if (shape == Shape.Mesh) return carveMesh;
            if (shape == Shape.Sphere)
                return _wallSphereMesh != null
                     ? _wallSphereMesh
                     : _wallSphereMesh = WaterMeshBuilder.BuildUnitSphere();
            return _wallCubeMesh != null
                 ? _wallCubeMesh
                 : _wallCubeMesh = WaterMeshBuilder.BuildUnitCube();
        }

        // Prefer the serialized slot (a build must assign it - Shader.Find only reaches shaders
        // that ship); fall back to the packaged name so existing scenes preview in the editor
        // without re-wiring. Null -> the walls just don't draw (the carve itself is unaffected).
        Material ResolveWallMaterial()
        {
            Shader shader = ResolveWallShader();
            if (shader == null) return null;
            // The Unity-null check covers a material destroyed under us (domain reload, Fast Enter
            // Play Mode): a stale entry rebuilds instead of returning a dead object.
            if (_wallMaterials.TryGetValue(shader, out Material cached) && cached != null) return cached;

            // HideAndDontSave: an edit-mode preview must never serialize this into the scene.
            Material material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.renderQueue = (int)RenderQueue.Transparent + WallRenderQueueOffset;
            _wallMaterials[shader] = material;
            return material;
        }

        Shader ResolveWallShader()
        {
            if (wallShader != null) return wallShader;
            if (_packagedWallShader != null) return _packagedWallShader;
            return _packagedWallShader = Shader.Find(WaterShaderNames.WaterExclusionWall);
        }

        /// <summary>Unit-local -> world matrix: centre + rotation + size in one transform. Built
        /// from position/rotation/lossyScale (the BoxCollider approximation: shear from
        /// non-uniformly scaled rotated parents is ignored). Also the water-wall draw matrix.
        /// The unit local space spans +-LocalHalfExtent, so Size reads as edge lengths for a box
        /// and as diameters for a sphere.</summary>
        internal Matrix4x4 ShapeToWorldMatrix()
        {
            Vector3 edge = Vector3.Scale(size, transform.lossyScale);
            edge = new Vector3(Mathf.Max(Mathf.Abs(edge.x), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.y), MinEdgeLength),
                               Mathf.Max(Mathf.Abs(edge.z), MinEdgeLength));
            return Matrix4x4.TRS(transform.position, transform.rotation, edge);
        }

        /// <summary>World -> unit-local matrix for this volume: the shader's inside test is
        /// the primitive kernel at LocalHalfExtent (WaterPrimitiveShape.hlsl).</summary>
        internal Matrix4x4 WorldToShapeMatrix() => ShapeToWorldMatrix().inverse;

        /// <summary>True when <paramref name="worldPoint"/> lies inside any active volume - the
        /// CPU twin of the shader's InsideExclusion (WaterExclusion.hlsl). Input routing uses it
        /// so clicks and drags never ripple or splash the carved-dry surface. The active list is
        /// tiny (a handful of rooms), so the per-call matrix inversions are nothing next to the
        /// raycast that precedes every call.</summary>
        internal static bool ContainsPoint(Vector3 worldPoint)
        {
            for (int i = 0; i < _active.Count; i++)
                if (_active[i].ContainsPointLocal(worldPoint)) return true;
            return false;
        }

        // The per-shape half of ContainsPoint, mirroring the shader's PrimitiveContains one for one.
        // A mesh volume answers through its PROXY here: the exact silhouette lives in a camera-space
        // depth prepass, and a click ray is not the camera ray the prepass was rendered from.
        bool ContainsPointLocal(Vector3 worldPoint)
        {
            Vector3 local = WorldToShapeMatrix().MultiplyPoint3x4(worldPoint);
            if (AnalyticShape == Shape.Sphere)
                return local.sqrMagnitude <= LocalHalfExtent * LocalHalfExtent;
            return Mathf.Abs(local.x) <= LocalHalfExtent
                && Mathf.Abs(local.y) <= LocalHalfExtent
                && Mathf.Abs(local.z) <= LocalHalfExtent;
        }

        /// <summary>Fill the uniform buffers (each length MaxVolumes exactly) with up to
        /// MaxVolumes active volumes and return the count used. <paramref name="matrices"/> and
        /// <paramref name="shapes"/> are required - a shape-less publish would silently carve
        /// every volume as a box. <paramref name="edgeColors"/>/<paramref name="edgeParams"/>
        /// (the per-volume edge-look uniforms) may be null for consumers that only need the
        /// geometry. Over the limit, the volumes NEAREST <paramref name="referencePoint"/> (the
        /// target camera) win and the drop is logged once - never a silent cap. Allocation-free:
        /// nearest-selection runs in place over the small active list.</summary>
        internal static int WriteVolumeUniforms(Matrix4x4[] matrices, Vector4[] shapes,
                                                Vector4[] edgeColors, Vector4[] edgeParams,
                                                Vector3 referencePoint)
        {
            ValidateBufferLength(matrices, nameof(matrices));
            ValidateBufferLength(shapes, nameof(shapes));
            if (edgeColors != null) ValidateBufferLength(edgeColors, nameof(edgeColors));
            if (edgeParams != null) ValidateBufferLength(edgeParams, nameof(edgeParams));

            int activeCount = _active.Count;
            if (activeCount <= MaxVolumes)
            {
                _warnedOverLimit = false;
                for (int i = 0; i < activeCount; i++)
                    WriteSlot(matrices, shapes, edgeColors, edgeParams, i, _active[i]);
                return activeCount;
            }

            WarnOverLimitOnce(activeCount);
            SelectNearest(matrices, shapes, edgeColors, edgeParams, referencePoint);
            return MaxVolumes;
        }

        static void ValidateBufferLength(System.Array buffer, string name)
        {
            if (buffer == null || buffer.Length != MaxVolumes)
                throw new System.ArgumentException(
                    $"WriteVolumeUniforms needs persistent buffers of exactly {MaxVolumes} " +
                    "entries (Unity locks a global array's size at its first set).", name);
        }

        // One volume -> one uniform slot: geometry always, edge look only for consumers that
        // bound the optional buffers. Keeps every writer path (in-limit + nearest) identical.
        static void WriteSlot(Matrix4x4[] matrices, Vector4[] shapes, Vector4[] edgeColors,
                              Vector4[] edgeParams, int slot, WaterExclusionVolume volume)
        {
            matrices[slot] = volume.WorldToShapeMatrix();
            shapes[slot] = volume.ShapeUniform;
            if (edgeColors != null) edgeColors[slot] = volume.EdgeColorUniform;
            if (edgeParams != null) edgeParams[slot] = volume.EdgeParamsUniform;
        }

        // Selection-sort the MaxVolumes nearest volumes into the buffers without allocating:
        // the active list is tiny (a handful of rooms), so O(count * MaxVolumes) is nothing.
        static void SelectNearest(Matrix4x4[] matrices, Vector4[] shapes, Vector4[] edgeColors,
                                  Vector4[] edgeParams, Vector3 referencePoint)
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
                WriteSlot(matrices, shapes, edgeColors, edgeParams, slot, _active[best]);
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
        // Editor-only wire shape so the dry region is visible while authoring.
        static readonly Color GizmoColor = new Color(0f, 0.85f, 0.9f, 0.9f); // package cyan

        void OnDrawGizmos()
        {
            Gizmos.color = GizmoColor;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation,
                                          Vector3.Scale(size, transform.lossyScale));
            // All three are drawn in the SAME unit local space, so the sphere shows as the box's
            // inscribed ball and the mesh sits where it will actually carve.
            if (shape == Shape.Mesh && carveMesh != null)
                Gizmos.DrawWireMesh(carveMesh);
            else if (AnalyticShape == Shape.Sphere)
                Gizmos.DrawWireSphere(Vector3.zero, LocalHalfExtent);
            else
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = Matrix4x4.identity;
        }
#endif
    }
}
