// WebGpuWater - WaterVolume: camera-following sim-window PATCH renderers.
// Split out of WaterVolume.cs (final-clean E, verbatim move - any behavior change here is a bug):
// the dense near-field grid drawn over the scrolling sim window (above + under twins), its
// per-renderer property blocks, and its build / per-frame placement / teardown.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // Camera-following high-detail surface over the sim window (windowed bodies, play mode).
        // Its grid follows the SIM resolution up to MaxPatchGridResolution, so the near field is
        // sampled densely - the far plane's fixed grid stretched over a large volume samples the
        // ripple heightfield too sparsely and aliases into false, bobbing ripples. Above the cap
        // the vertex density stops following the sim: a bicubic-filtered read does not need one
        // vertex per texel, and an uncapped grid at the High tier (sim 512) cost 513^2 vertices
        // x 2 twins x ~22 fetches through the displacement vertex stage every frame - and the
        // eye-depth prepass and foam overlay re-draw the same mesh on fog-armed frames.
        Renderer _patchRenderer;
        Mesh _patchGrid;
        MaterialPropertyBlock _patchMpb;
        static readonly int ID_IsPatch = Shader.PropertyToID("_IsPatch");
        static readonly int ID_PatchPoolCenter = Shader.PropertyToID("_PatchPoolCenter");
        static readonly int ID_PatchPoolHalf = Shader.PropertyToID("_PatchPoolHalf");
        static readonly int ID_PatchDepthBias = Shader.PropertyToID("_PatchDepthBias");
        const float PatchDepthBiasMeters = 0.02f;   // view-space nudge toward the camera so the dense patch wins the
                                                    // overlap (beats the coplanar far plane AND the coarser ocean
                                                    // clipmap). World metres, so it can't draw over opaque at distance.
        const string PatchObjectName = "Sim Window Patch";
        // Vertex ceiling for the patch grid (see the class note above). Mid tier has always
        // shipped this density (257^2 vertices); higher tiers keep their full TEXEL detail in
        // the per-pixel normal/refine reads - only the geometric displacement mesh stops
        // scaling quadratically with the sim.
        const int MaxPatchGridResolution = 256;
        // Underside twin of the near-field patch: the SAME dense grid drawn with the under-water
        // material, so the submerged near field is sampled as finely as the above one and the two line
        // up vertex-for-vertex at the waterline (a coarse underside would show through the fine top).
        // Ocean-clipmap bodies only: it fills the under-clipmap's centre hole, and the bounded
        // under-plane it would otherwise fight is already switched off there.
        Renderer _patchUnderRenderer;
        MaterialPropertyBlock _patchUnderMpb;
        const string PatchUnderObjectName = "Sim Window Patch (under)";

        // Camera-following clipmap surface for unbounded open-water (ocean) bodies: a WORLD-LOCKED

        // ---- The patch's pool rect, and the hole the base sheet cuts for it ------------------
        // The patch and the base sheet are COINCIDENT over the window and tessellate the SAME ripple
        // field at very different densities, so the coarse sheet chords across waves the patch
        // resolves. After a real disturbance the two surfaces differ by far more than
        // PatchDepthBiasMeters can hold and the base sheet punches through in blobs - visible as
        // patches of the surface stepping, and as half the ripple field being drawn by a mesh that
        // cannot resolve it. The cure is ONE surface per pixel: the base sheet drops the region the
        // patch covers. Derived here rather than at each consumer so the patch's own vertex remap and
        // the hole can never disagree about where the patch is.
        internal bool PatchCoverActive => _patchRenderer != null;

        internal Vector4 PatchPoolCenter
        {
            get
            {
                Vector3 poolCenter = WorldToPool(SimWindowCenter);
                return new Vector4(poolCenter.x, poolCenter.z, 0f, 0f);
            }
        }

        internal Vector4 PatchPoolHalf => new Vector4(SimHorizontalExtent / VolumeExtentSafe.x,
                                                      SimHorizontalExtent / VolumeExtentSafe.z, 0f, 0f);

        // The hole is shrunk by this much so the patch OVERLAPS its rim instead of meeting it exactly:
        // two surfaces that end on the same line leave a rasterised seam showing the sky through the
        // water. Measured in base-sheet quads, because a quad is the width the seam would open by.
        const float PatchCoverMarginQuads = 2f;
        const float MinPatchCoverMarginPool = 0.01f; // floor for bodies that kept their authored mesh

        internal float PatchCoverMargin
        {
            get
            {
                int detail = SurfaceMeshDetail();
                float quadPool = detail > 0 ? 2f / detail : 0f;   // pool space spans [-1, 1]
                return Mathf.Max(PatchCoverMarginQuads * quadPool, MinPatchCoverMarginPool);
            }
        }

        // Refresh both near-field patches (the above one, and the under twin on ocean bodies).
        void ApplyPatchBlock()
        {
            PositionPatch(_patchRenderer, ref _patchMpb, isUnderTwin: false);
            PositionPatch(_patchUnderRenderer, ref _patchUnderMpb, isUnderTwin: true);
        }

        // Feed one patch renderer this body's per-body uniforms PLUS the window remap it needs, and park
        // it on the window centre so it culls with the window. The remap rides its own block so _IsPatch
        // never leaks onto the flat surface renderers. The transform is cosmetic (the shader places the
        // verts via PoolToWorld); it only sizes the culling bounds.
        void PositionPatch(Renderer patch, ref MaterialPropertyBlock block, bool isUnderTwin)
        {
            if (patch == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            WriteBodyProps(block);

            block.SetFloat(ID_IsPatch, 1f);
            // Patch bias plus the camera-medium tie-breaker shared with the clipmap twins
            // (WaterVolume.OceanClipmap.MediumMatchedTwinExtraBias - full rationale there):
            // the patch twin matching the eye's medium wins its coincident-depth pixels.
            block.SetFloat(ID_PatchDepthBias,
                           PatchDepthBiasMeters + MediumMatchedTwinExtraBias(isUnderTwin));
            block.SetVector(ID_PatchPoolCenter, PatchPoolCenter);
            block.SetVector(ID_PatchPoolHalf, PatchPoolHalf);
            patch.SetPropertyBlock(block);

            Transform t = patch.transform;
            t.position = SimWindowCenter;
            t.localScale = SimHalfExtent;
        }

        // Build the windowed near-field patch: a grid at the sim resolution (capped - see
        // MaxPatchGridResolution), remapped by the
        // shader into the window's pool sub-region. Reuses THIS body's surface material instance
        // (so it inherits reflections/fog) with _IsPatch riding its property block. Play mode
        // only - it depends on the per-body material instance created in ApplyReflections.
        void CreateSimWindowPatch()
        {
            if (!Application.isPlaying || !_windowed || !renderBuiltInGeometry) return;
            if (_patchRenderer != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            _patchGrid = WaterMeshBuilder.BuildGrid(Mathf.Clamp(_simRes, 1, MaxPatchGridResolution));
            _patchGrid.hideFlags = HideFlags.HideAndDontSave;
            _patchRenderer = CreateSurfaceRenderer(PatchObjectName, _patchGrid, surfaceAbove.sharedMaterial);

            // Underside twin (ocean clipmap only): the same dense grid drawn with the under-water
            // material fills the under-clipmap's centre hole and matches the top vertex-for-vertex, so
            // the two never show through each other at the waterline. Bounded and non-ocean windowed
            // bodies keep their single bounded under-plane (no twin), so they stay unchanged.
            if (IsOceanClipmap && surfaceUnder != null && surfaceUnder.sharedMaterial != null)
                _patchUnderRenderer = CreateSurfaceRenderer(PatchUnderObjectName, _patchGrid, surfaceUnder.sharedMaterial);
        }

        void DestroySimWindowPatch()
        {
            if (_patchRenderer != null)
            {
                WaterObjects.DestroyRuntime(_patchRenderer.gameObject);
                _patchRenderer = null;
            }
            if (_patchUnderRenderer != null)
            {
                WaterObjects.DestroyRuntime(_patchUnderRenderer.gameObject);
                _patchUnderRenderer = null;
            }
            WaterObjects.DestroyRuntime(_patchGrid);
            _patchGrid = null;
            _patchMpb = null;
            _patchUnderMpb = null;
        }
    }
}
