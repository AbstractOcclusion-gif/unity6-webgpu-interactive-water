// WebGpuWater - WaterVolume as a CHUNK: a self-contained finite body of water in dry space (the
// INVERT of the exclusion carve). The body already renders the real surface (foam, above/below,
// reflections); this partial adds the submerged fog SHELL as a body-owned renderer so ONE volume is
// the whole chunk - the shell reads THIS body's frame + waves + fog through the shared per-body
// block, so its waterline matches the disc surface with no seam and it needs no external primary.
//
// The shell is a pool-space box (BuildChunkShellBox) placed by the frame in the shader, exactly like
// the analytic pool renderer; the primitive (box / inscribed sphere) is resolved analytically in
// WaterChunkWall.shader. Created lazily, HideAndDontSave (never serialized), parented to the body so
// it is torn down with it.
//
// DEVELOPMENT STATUS: the current chunk path intentionally exposes a reduced authoring surface and
// is not feature-equivalent to the standard ocean renderer. Full wave, shading, fog and effects
// control parity is scheduled as a v1.1 feature. Keep additions compatible with that future shared
// water-settings architecture; do not grow a second independent ocean stack inside this partial.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        /// <summary>Chunk footprint. None = an ordinary (square-footprint) body. Box / Sphere turn the
        /// body into a floating chunk (analytic primitive). Mesh takes the water column's entry/exit
        /// from an ARBITRARY closed mesh via the depth prepass (WaterChunkDepthFeature).</summary>
        public enum ChunkFootprint { None, Box, Sphere, Mesh }

        /// <summary>How a box chunk joins its displaced surface to the vertical shell.</summary>
        public enum ChunkBoundaryMode { Vertical, Stabilized, CalmRim }

        [SerializeField, HideInInspector] internal ChunkFootprint chunkFootprint = ChunkFootprint.None;
        [SerializeField, HideInInspector] internal float chunkDensityBoost = 1f;
        [SerializeField, HideInInspector] internal float chunkRefraction = 0.5f;
        [SerializeField, HideInInspector] internal float chunkReflectivity = 0.6f;
        // Meniscus line strength (0 = off). A thin surface-tension darkening along the on-screen
        // waterline, drawn only on the near-plane "at 0" frames by WaterChunkWall.shader. Exposed as
        // a slider like the other chunk knobs (WaterVolumeEditor.Chunk.cs).
        [SerializeField, HideInInspector] internal float chunkMeniscus = 0.5f;
        // Whitecap foam strength for an OPEN-WATER chunk surface: published as the analytic
        // geometry-foam floor (_LbwGeomFoamFloor - see WaterUniformPublisher / LbwGeometryFoamGate).
        // 1 = physical crest pinch/steepness, >1 whitens milder crests, 0 = off.
        [SerializeField, HideInInspector] internal float chunkFoamStrength = 1f;
        // Volumetric god-ray shafts inside the chunk (0 = off). The shell wall marches the submerged
        // column and accumulates the body's caustic focusing, so the shafts are shaped to the chunk
        // primitive + fill level. Default off - opt-in look knob (WaterChunkWall.shader reads both).
        [SerializeField, HideInInspector] internal float chunkGodRayStrength = 0f;
        [SerializeField, HideInInspector] internal Color chunkGodRayColor = new Color(1f, 0.97f, 0.85f, 1f);
        // The closed mesh a Mesh-footprint chunk fills. Authored in POOL space [-1,1] (like the shell
        // box), placed by the volume frame; the depth prepass rasterises its front/back faces.
        [SerializeField, HideInInspector] internal Mesh chunkMesh;
        // Fill level 0..1: how full the chunk is. 0.5 = the rest plane (surface at the shape's centre,
        // the historical default); 1 = brim-full (surface at the top); 0 = empty. Maps to a pool-Y plane.
        [SerializeField, HideInInspector] internal float chunkFillLevel = 0.5f;
        [SerializeField, HideInInspector] internal ChunkBoundaryMode chunkBoundaryMode = ChunkBoundaryMode.Stabilized;
        [SerializeField, HideInInspector, Min(0f)] internal float chunkBoundaryWidth = 1f;

        internal bool IsChunk => chunkFootprint != ChunkFootprint.None;

        // Mesh-footprint chunk that actually has a mesh to prepass. The depth feature/pass gate on
        // this so sphere/box chunks (analytic) never trigger the prepass.
        internal bool IsMeshChunk => chunkFootprint == ChunkFootprint.Mesh && chunkMesh != null;
        internal Mesh ChunkDepthMesh => chunkMesh;

        // Scanned by WaterChunkDepthFeature (any-active gate) and WaterChunkDepthPass (draw list).
        // Bodies is the package-wide registry (declared in WaterVolume.Settings.Underwater.cs).
        internal static bool AnyMeshChunkActive()
        {
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (body != null && body.isActiveAndEnabled && body.IsMeshChunk) return true;
            }
            return false;
        }

        internal static void CollectMeshChunks(List<WaterVolume> into)
        {
            into.Clear();
            for (int i = 0; i < Bodies.Count; i++)
            {
                WaterVolume body = Bodies[i];
                if (body != null && body.isActiveAndEnabled && body.IsMeshChunk) into.Add(body);
            }
        }

        // GPU pair: CHUNK_SHAPE_* in WaterChunkPrimitive.hlsl.
        const float ChunkShapeBoxValue = 0f;
        const float ChunkShapeSphereValue = 1f;

        // Deterministic transparent order: the shell must composite AFTER the water surfaces -
        // same-queue transparents with huge mesh bounds sort arbitrarily, which flipped the
        // shell/disc order per view (underwater the disc drew over the fog). The shader's
        // ownership split (top entries discarded, camera-in-water veils the framebuffer)
        // relies on the shell being last.
        const int ChunkShellRenderQueueOffset = 10;

        // Camera-in-this-chunk's-water state, decided per FRAME on the CPU and published as
        // _ChunkCameraUnderwater: it flips the shell between the refracted-backdrop composite
        // (outside view) and the framebuffer VEIL (inside view - the backdrop texture holds no
        // transparents, so replacing erased the disc underside). Partial submersion: the LOWEST
        // near-plane corner decides (mirrors ComputeCameraSubmerged), with the same hysteresis,
        // so the veil engages the moment the view starts dipping under and a crest bobbing across
        // the waterline cannot toggle it every frame. A per-pixel ray test was tried and flickered.
        //
        // The footprint margin exists ONLY because the near PLANE can dip into the water before
        // the camera POINT does, so it is sized from the near-clip reach in WORLD metres - a
        // near-plane corner sits at most ~2x the near-clip distance from the camera at common
        // FOVs. The old margin was RELATIVE (10% of the chunk's own size): walking up to a chunk
        // at eye level flipped the veil while still standing in AIR, killing the reflection
        // sheen and the refracted backdrop in one frame - "the water darkens when I come close".
        const float ChunkCameraNearReachScale = 2f;
        bool _wasChunkCameraUnder;

        MeshRenderer _chunkShellRenderer;
        static Mesh _chunkShellMesh;
        static Material _chunkShellMaterial;
        static readonly int ID_ChunkShape = Shader.PropertyToID("_ChunkShape");
        static readonly int ID_ChunkRefraction = Shader.PropertyToID("_ChunkRefraction");
        static readonly int ID_ChunkReflectivity = Shader.PropertyToID("_ChunkReflectivity");
        static readonly int ID_ChunkSphereClip = Shader.PropertyToID("_ChunkSphereClip");
        static readonly int ID_ChunkBoxClip = Shader.PropertyToID("_ChunkBoxClip");
        static readonly int ID_ChunkFogClamp = Shader.PropertyToID("_ChunkFogClamp");
        // NOTE: the chunk fog gate + density boost live in WaterUniformPublisher.WriteBodyUniforms
        // (they alias the publisher-owned _WaterFogEnabled/_WaterFogDensity ids - writing them here
        // fought the cached sinks and killed through-surface fog on every NON-chunk body).
        static readonly int ID_ChunkCameraUnderwater = Shader.PropertyToID("_ChunkCameraUnderwater");
        static readonly int ID_ChunkMeniscus = Shader.PropertyToID("_ChunkMeniscus");
        static readonly int ID_ChunkUseMesh = Shader.PropertyToID("_ChunkUseMesh");
        static readonly int ID_ChunkSurfacePoolY = Shader.PropertyToID("_ChunkSurfacePoolY");
        static readonly int ID_ChunkBoundaryEnabled = Shader.PropertyToID("_ChunkBoundaryEnabled");
        static readonly int ID_ChunkBoundaryWidth = Shader.PropertyToID("_ChunkBoundaryWidth");
        static readonly int ID_ChunkEdgeWaveHeight = Shader.PropertyToID("_ChunkEdgeWaveHeight");
        static readonly int ID_ChunkEdgeChoppiness = Shader.PropertyToID("_ChunkEdgeChoppiness");
        static readonly int ID_ChunkGodRayStrength = Shader.PropertyToID("_ChunkGodRayStrength");
        static readonly int ID_ChunkGodRayColor = Shader.PropertyToID("_ChunkGodRayColor");

        // Build the shell renderer once (lazily). Null material (shader missing in a build without the
        // Always-Included registration) leaves the shell absent - the surface still renders.
        void EnsureChunkShell()
        {
            if (_chunkShellRenderer != null) return;
            Material material = ResolveChunkShellMaterial();
            if (material == null) return;

            // `== null`, NOT `??=`. This is a UnityEngine.Object: Unity overloads == so a DESTROYED
            // object compares equal to null, but `??=` tests the raw C# reference and so sees a
            // destroyed mesh as "already assigned" and skips the rebuild.
            //
            // That is exactly what made a chunk vanish in the EDITOR after stopping play. Exiting play
            // mode destroys the runtime-created mesh, but the STATIC reference to it survives (no
            // domain reload on exit) - so back in edit mode the shell was rebuilt with a destroyed
            // mesh and rendered nothing. ResolveChunkShellMaterial above already gets this right for
            // the material, which is why the material recovered and the mesh did not.
            if (_chunkShellMesh == null) _chunkShellMesh = WaterMeshBuilder.BuildChunkShellBox();
            var shellObject = new GameObject("Chunk Shell") { hideFlags = HideFlags.HideAndDontSave };
            shellObject.transform.SetParent(transform, false); // identity: the frame places it in-shader
            shellObject.layer = gameObject.layer;
            shellObject.AddComponent<MeshFilter>().sharedMesh = _chunkShellMesh;
            _chunkShellRenderer = shellObject.AddComponent<MeshRenderer>();
            _chunkShellRenderer.sharedMaterial = material;
            _chunkShellRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _chunkShellRenderer.receiveShadows = false;
        }

        // Tear the per-body shell down on disable. TWO reasons, both bugs before this existed:
        //
        //  1. _chunkShellRenderer was never nulled anywhere, and EnsureChunkShell short-circuits on
        //     it. Under Fast Enter Play Mode with Reload Scene off, ResetChunkStaticState destroys
        //     the SHARED material/mesh between sessions while the instance field survives - so the
        //     shell kept rendering with a destroyed material and never rebuilt.
        //  2. The shell GameObject/MeshFilter are HideAndDontSave, so in edit mode nothing collected
        //     them: disabling a chunk body left its fog shell drawing with frozen uniforms.
        //
        // The shared material and mesh are deliberately NOT destroyed here - other chunk bodies may
        // still be using them; ResetChunkStaticState owns their lifetime.
        void DestroyChunkShell()
        {
            if (_chunkShellRenderer == null) return;
            WaterObjects.DestroyRuntime(_chunkShellRenderer.gameObject);
            _chunkShellRenderer = null;
        }

        static Material ResolveChunkShellMaterial()
        {
            if (_chunkShellMaterial != null) return _chunkShellMaterial;
            Shader shader = Shader.Find(WaterShaderNames.WaterChunkWall);
            if (shader == null) return null;
            _chunkShellMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _chunkShellMaterial.renderQueue =
                (int)RenderQueue.Transparent + ChunkShellRenderQueueOffset;
            return _chunkShellMaterial;
        }

        // Set on the body block BEFORE the disc surface renderers receive it (in ApplyBodyBlock, right
        // after WriteBodyProps), so the surface clips its flat disc to the sphere AND caps its
        // refraction fog at the chunk primitive. Shape/clip/clamp are always written (0 for ordinary
        // bodies) so a body leaving chunk mode never reads a stale flag.
        void SetChunkSurfaceProps(MaterialPropertyBlock block)
        {
            bool isSphere = chunkFootprint == ChunkFootprint.Sphere;
            bool isBox = chunkFootprint == ChunkFootprint.Box;
            block.SetFloat(ID_ChunkSphereClip, isSphere ? 1f : 0f);
            block.SetFloat(ID_ChunkBoxClip, isBox ? 1f : 0f);
            block.SetFloat(ID_ChunkFogClamp, IsChunk ? 1f : 0f);
            block.SetFloat(ID_ChunkShape, isSphere ? ChunkShapeSphereValue : ChunkShapeBoxValue);
            // Fill level -> surface pool-Y plane (0 = rest). Always written (0 off-chunk) so a body
            // leaving chunk mode never keeps a stale level. The disc (WaterSurface) and the shell wall
            // read the same value, so their waterlines stay locked together.
            block.SetFloat(ID_ChunkSurfacePoolY, IsChunk ? (chunkFillLevel * 2f - 1f) : 0f);
            // Mesh footprint flag, needed by BOTH the disc (WaterSurface clips itself to the mesh) and
            // the shell wall (reads entry/exit from the depth prepass). Set here so the disc's block
            // carries it; always written (0 off-chunk) so a body leaving mesh mode resets.
            block.SetFloat(ID_ChunkUseMesh, chunkFootprint == ChunkFootprint.Mesh ? 1f : 0f);
            SetChunkBoundaryProps(block, isBox);

            // The chunk fog GATE and DENSITY BOOST are folded into the publisher's own
            // _WaterFogEnabled/_WaterFogDensity writes (WaterUniformPublisher.WriteBodyUniforms) -
            // see the note at the id declarations above. Only the genuinely chunk-own id remains,
            // always written (0 off-chunk) so a body leaving chunk mode never reads a stale flag.
            block.SetFloat(ID_ChunkCameraUnderwater, IsChunk && ComputeChunkCameraUnder() ? 1f : 0f);
        }

        void SetChunkBoundaryProps(MaterialPropertyBlock block, bool isBox)
        {
            bool boundaryEnabled = isBox && chunkBoundaryMode != ChunkBoundaryMode.Vertical
                                            && chunkBoundaryWidth > 0f;
            float edgeWaveHeight = chunkBoundaryMode == ChunkBoundaryMode.CalmRim ? 0f : 1f;
            block.SetFloat(ID_ChunkBoundaryEnabled, boundaryEnabled ? 1f : 0f);
            block.SetFloat(ID_ChunkBoundaryWidth, Mathf.Max(chunkBoundaryWidth, 0f));
            block.SetFloat(ID_ChunkEdgeWaveHeight, edgeWaveHeight);
            block.SetFloat(ID_ChunkEdgeChoppiness, boundaryEnabled ? 0f : 1f);
        }

        // See the field block above: lowest near-plane corner vs the wave-aware surface height,
        // with the shared submerge hysteresis, gated on the camera being inside the footprint.
        bool ComputeChunkCameraUnder()
        {
            Camera cam = targetCamera;
            if (cam == null) { _wasChunkCameraUnder = false; return false; }

            Vector3 cameraPos = cam.transform.position;
            float nearReach = cam.nearClipPlane * ChunkCameraNearReachScale;
            if (!ChunkCameraInsideFootprint(cameraPos, nearReach)) { _wasChunkCameraUnder = false; return false; }

            float near = cam.nearClipPlane;
            float referenceY = cameraPos.y;
            referenceY = Mathf.Min(referenceY, cam.ViewportToWorldPoint(new Vector3(0f, 0f, near)).y);
            referenceY = Mathf.Min(referenceY, cam.ViewportToWorldPoint(new Vector3(1f, 0f, near)).y);
            referenceY = Mathf.Min(referenceY, cam.ViewportToWorldPoint(new Vector3(0f, 1f, near)).y);
            referenceY = Mathf.Min(referenceY, cam.ViewportToWorldPoint(new Vector3(1f, 1f, near)).y);

            float surfaceY = SurfaceHeightAtCamera();
            float threshold = _wasChunkCameraUnder ? surfaceY + SubmergeHysteresis
                                                   : surfaceY - SubmergeHysteresis;
            _wasChunkCameraUnder = referenceY < threshold;
            return _wasChunkCameraUnder;
        }

        // Camera within the chunk primitive plus the near-plane reach (world metres, converted
        // per axis into pool units) - the tightest region where the near plane could already be
        // touching the water while the camera point is still outside it.
        bool ChunkCameraInsideFootprint(Vector3 cameraPos, float nearReachWorld)
        {
            Vector3 pool = WorldToPool(cameraPos);
            Vector3 extent = VolumeExtentSafe;
            if (chunkFootprint == ChunkFootprint.Sphere)
            {
                float radius = 1f + nearReachWorld / Mathf.Min(extent.x, Mathf.Min(extent.y, extent.z));
                return pool.sqrMagnitude <= radius * radius;
            }
            if (chunkFootprint == ChunkFootprint.Mesh && chunkMesh != null)
            {
                // Chunk meshes are authored IN pool space (the depth prepass draws them through
                // PoolToWorld with an identity object matrix - WaterChunkDepth.shader), so the
                // mesh's local bounds ARE its pool-space bounds: the tightest cheap containment.
                // The whole-VOLUME-box test stood in before, and a mesh smaller than its box (the
                // built-in primitives span only +-0.5 on xz) flipped the veil while the camera was
                // visibly in AIR beside the water below surface level - the fog colour/light
                // popped on approach (masked at fog density > ~3 only because the veil and the
                // refracted composite converge once transmittance is ~0).
                //
                // KNOWN LIMITATION: this is the mesh's AABB, not the mesh. In the gap between
                // the box and a round/concave shape (a cylinder's corners, a concave pocket)
                // the veil can still flip while the camera is in air below surface level -
                // visible as the same colour/light pop, ONLY at low fog density. Confirmed
                // reachable on concave/collider-wall meshes (Bert, 2026-07-26); accepted for
                // now as a big improvement over the volume box.
                // FIX TO RE-EXPLORE: choose the composite PER PIXEL in WaterChunkWall.shader
                // from data it already has - a ray with a front ENTRY face (meshHasEntryFace /
                // nearInAir) starts in air and can take the refracted-backdrop path even while
                // this CPU flag says "under"; only entry-less rays need the veil. That removes
                // the containment question entirely. A per-pixel camera-under DECISION was
                // tried once and flickered at the waterline - the re-attempt should keep this
                // CPU flag as the OUTER gate and only split the composite inside it, with the
                // near-plane hysteresis still owning the on/off.
                Bounds meshBounds = chunkMesh.bounds;
                Vector3 boundsMin = meshBounds.min;
                Vector3 boundsMax = meshBounds.max;
                return pool.x >= boundsMin.x - nearReachWorld / extent.x
                    && pool.x <= boundsMax.x + nearReachWorld / extent.x
                    && pool.y >= boundsMin.y - nearReachWorld / extent.y
                    && pool.y <= boundsMax.y + nearReachWorld / extent.y
                    && pool.z >= boundsMin.z - nearReachWorld / extent.z
                    && pool.z <= boundsMax.z + nearReachWorld / extent.z;
            }
            return Mathf.Abs(pool.x) <= 1f + nearReachWorld / extent.x
                && Mathf.Abs(pool.y) <= 1f + nearReachWorld / extent.y
                && Mathf.Abs(pool.z) <= 1f + nearReachWorld / extent.z;
        }

        // Feed the shell THIS body's block (frame + waves + fog: written by WriteBodyProps into the
        // shared block just before) plus the per-chunk knobs, then push it. Called from ApplyBodyBlock
        // AFTER the ordinary renderers, so mutating the block here can't leak chunk props onto them.
        // _ChunkShape is already on the block (SetChunkSurfaceProps - the surface needs it too).
        void ApplyChunkShellBlock(MaterialPropertyBlock bodyBlock)
        {
            if (!IsChunk || !renderBuiltInGeometry) { DisableChunkShell(); return; }
            EnsureChunkShell();
            if (_chunkShellRenderer == null) return;

            bodyBlock.SetFloat(ID_ChunkRefraction, chunkRefraction);
            bodyBlock.SetFloat(ID_ChunkReflectivity, chunkReflectivity);
            bodyBlock.SetFloat(ID_ChunkMeniscus, chunkMeniscus);
            bodyBlock.SetFloat(ID_ChunkGodRayStrength, chunkGodRayStrength);
            bodyBlock.SetColor(ID_ChunkGodRayColor, chunkGodRayColor);
            _chunkShellRenderer.SetPropertyBlock(bodyBlock);
        }

        // Culling gate, folded into SetRenderersEnabled so the shell follows the body on/off-screen.
        void SetChunkShellEnabled(bool on)
        {
            if (_chunkShellRenderer != null) SetRendererEnabled(_chunkShellRenderer, on && IsChunk);
        }

        void DisableChunkShell()
        {
            if (_chunkShellRenderer != null) SetRendererEnabled(_chunkShellRenderer, false);
        }

        // Fast play-mode enter (Domain Reload disabled) keeps these statics alive across sessions while
        // the shell GameObjects they fed are gone - reset so the first chunk rebuilds cleanly and a
        // destroyed material/mesh is never reused. Multi-chunk note: per-body state (frame, waves, fog,
        // camera-underwater) already flows through each volume's OWN MaterialPropertyBlock, so it is
        // per-instance; only this shared material/mesh needed resetting. Inter-chunk transparent SORT
        // order (two chunks near each other) is a render-queue limitation the depth-RT rearchitecture
        // addresses - a per-instance material would not fix it, so it is deliberately left shared.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetChunkStaticState()
        {
            if (_chunkShellMaterial != null) Destroy(_chunkShellMaterial);
            if (_chunkShellMesh != null) Destroy(_chunkShellMesh);
            _chunkShellMaterial = null;
            _chunkShellMesh = null;
        }
    }
}
