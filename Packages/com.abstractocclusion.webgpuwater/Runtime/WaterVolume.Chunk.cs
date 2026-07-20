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

        [SerializeField, HideInInspector] internal ChunkFootprint chunkFootprint = ChunkFootprint.None;
        [SerializeField, HideInInspector] internal float chunkDensityBoost = 1f;
        [SerializeField, HideInInspector] internal float chunkRefraction = 0.5f;
        [SerializeField, HideInInspector] internal float chunkReflectivity = 0.6f;
        // Meniscus line strength (0 = off). A thin surface-tension darkening along the on-screen
        // waterline, drawn only on the near-plane "at 0" frames by WaterChunkWall.shader. Look-tune
        // knob - wire an inspector slider in WaterVolumeEditor.Chunk.cs like the others if desired.
        [SerializeField, HideInInspector] internal float chunkMeniscus = 0.5f;
        // The closed mesh a Mesh-footprint chunk fills. Authored in POOL space [-1,1] (like the shell
        // box), placed by the volume frame; the depth prepass rasterises its front/back faces.
        [SerializeField, HideInInspector] internal Mesh chunkMesh;
        // Fill level 0..1: how full the chunk is. 0.5 = the rest plane (surface at the shape's centre,
        // the historical default); 1 = brim-full (surface at the top); 0 = empty. Maps to a pool-Y plane.
        [SerializeField, HideInInspector] internal float chunkFillLevel = 0.5f;

        internal bool IsChunk => chunkFootprint != ChunkFootprint.None;

        // Mesh-footprint chunk that actually has a mesh to prepass. The depth feature/pass gate on
        // this so sphere/box chunks (analytic) never trigger the prepass.
        internal bool IsMeshChunk => chunkFootprint == ChunkFootprint.Mesh && chunkMesh != null;
        internal Mesh ChunkDepthMesh => chunkMesh;

        // Scanned by WaterChunkDepthFeature (any-active gate) and WaterChunkDepthPass (draw list).
        // Bodies is the package-wide registry (WaterVolume.Settings.cs).
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
        const float ChunkCameraFootprintMargin = 1.1f;
        bool _wasChunkCameraUnder;

        MeshRenderer _chunkShellRenderer;
        static Mesh _chunkShellMesh;
        static Material _chunkShellMaterial;
        static readonly int ID_ChunkShape = Shader.PropertyToID("_ChunkShape");
        static readonly int ID_ChunkRefraction = Shader.PropertyToID("_ChunkRefraction");
        static readonly int ID_ChunkReflectivity = Shader.PropertyToID("_ChunkReflectivity");
        static readonly int ID_ChunkSphereClip = Shader.PropertyToID("_ChunkSphereClip");
        static readonly int ID_ChunkFogClamp = Shader.PropertyToID("_ChunkFogClamp");
        static readonly int ID_ChunkWaterFogEnabled = Shader.PropertyToID("_WaterFogEnabled");
        static readonly int ID_ChunkWaterFogDensity = Shader.PropertyToID("_WaterFogDensity");
        static readonly int ID_ChunkCameraUnderwater = Shader.PropertyToID("_ChunkCameraUnderwater");
        static readonly int ID_ChunkMeniscus = Shader.PropertyToID("_ChunkMeniscus");
        static readonly int ID_ChunkUseMesh = Shader.PropertyToID("_ChunkUseMesh");
        static readonly int ID_ChunkSurfacePoolY = Shader.PropertyToID("_ChunkSurfacePoolY");

        // Build the shell renderer once (lazily). Null material (shader missing in a build without the
        // Always-Included registration) leaves the shell absent - the surface still renders.
        void EnsureChunkShell()
        {
            if (_chunkShellRenderer != null) return;
            Material material = ResolveChunkShellMaterial();
            if (material == null) return;

            _chunkShellMesh ??= WaterMeshBuilder.BuildChunkShellBox();
            var shellObject = new GameObject("Chunk Shell") { hideFlags = HideFlags.HideAndDontSave };
            shellObject.transform.SetParent(transform, false); // identity: the frame places it in-shader
            shellObject.layer = gameObject.layer;
            shellObject.AddComponent<MeshFilter>().sharedMesh = _chunkShellMesh;
            _chunkShellRenderer = shellObject.AddComponent<MeshRenderer>();
            _chunkShellRenderer.sharedMaterial = material;
            _chunkShellRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _chunkShellRenderer.receiveShadows = false;
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
            block.SetFloat(ID_ChunkSphereClip, isSphere ? 1f : 0f);
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
            if (!IsChunk) return;

            // A chunk's fog comes from its OWN disc surface + shell, so the GPU fog gate is forced on
            // here while the C# WaterFog flag stays false - that flag must keep the fullscreen
            // underwater pass disarmed (it runs on the primary's globals and clips to the pool BOX,
            // not the chunk primitive, so it would fog the wrong volume).
            block.SetFloat(ID_ChunkWaterFogEnabled, 1f);
            // Density boost baked into the body's fog density ONCE, so the disc column, the shell and
            // any membership object all read the same (boosted) water - no per-consumer multiplier.
            block.SetFloat(ID_ChunkWaterFogDensity, fogDensity * chunkDensityBoost);
            block.SetFloat(ID_ChunkCameraUnderwater, ComputeChunkCameraUnder() ? 1f : 0f);
        }

        // See the field block above: lowest near-plane corner vs the wave-aware surface height,
        // with the shared submerge hysteresis, gated on the camera being inside the footprint.
        bool ComputeChunkCameraUnder()
        {
            Camera cam = targetCamera;
            if (cam == null) { _wasChunkCameraUnder = false; return false; }

            Vector3 cameraPos = cam.transform.position;
            if (!ChunkCameraInsideFootprint(cameraPos)) { _wasChunkCameraUnder = false; return false; }

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

        bool ChunkCameraInsideFootprint(Vector3 cameraPos)
        {
            Vector3 pool = WorldToPool(cameraPos);
            if (chunkFootprint == ChunkFootprint.Sphere)
                return pool.sqrMagnitude <= ChunkCameraFootprintMargin * ChunkCameraFootprintMargin;
            return Mathf.Max(Mathf.Abs(pool.x), Mathf.Max(Mathf.Abs(pool.y), Mathf.Abs(pool.z)))
                   <= ChunkCameraFootprintMargin;
        }

        // Feed the shell THIS body's block (frame + waves + fog: written by WriteBodyProps into the
        // shared block just before) plus the per-chunk knobs, then push it. Called from ApplyBodyBlock
        // AFTER the ordinary renderers, so mutating the block here can't leak chunk props onto them.
        // _ChunkShape is already on the block (SetChunkSurfaceProps - the surface needs it too).
        void ApplyChunkShellBlock(MaterialPropertyBlock bodyBlock)
        {
            if (!IsChunk) { DisableChunkShell(); return; }
            EnsureChunkShell();
            if (_chunkShellRenderer == null) return;

            bodyBlock.SetFloat(ID_ChunkRefraction, chunkRefraction);
            bodyBlock.SetFloat(ID_ChunkReflectivity, chunkReflectivity);
            bodyBlock.SetFloat(ID_ChunkMeniscus, chunkMeniscus);
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
