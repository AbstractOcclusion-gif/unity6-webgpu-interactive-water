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
        // Its grid is built at the SIM resolution so the near field is sampled ~one vertex per
        // texel - the far plane's fixed grid stretched over a large volume samples the ripple
        // heightfield too sparsely and aliases into false, bobbing ripples.
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
        // Underside twin of the near-field patch: the SAME dense grid drawn with the under-water
        // material, so the submerged near field is sampled as finely as the above one and the two line
        // up vertex-for-vertex at the waterline (a coarse underside would show through the fine top).
        // Ocean-clipmap bodies only: it fills the under-clipmap's centre hole, and the bounded
        // under-plane it would otherwise fight is already switched off there.
        Renderer _patchUnderRenderer;
        MaterialPropertyBlock _patchUnderMpb;
        const string PatchUnderObjectName = "Sim Window Patch (under)";

        // Camera-following clipmap surface for unbounded open-water (ocean) bodies: a WORLD-LOCKED

        // Refresh both near-field patches (the above one, and the under twin on ocean bodies).
        void ApplyPatchBlock()
        {
            PositionPatch(_patchRenderer, ref _patchMpb);
            PositionPatch(_patchUnderRenderer, ref _patchUnderMpb);
        }

        // Feed one patch renderer this body's per-body uniforms PLUS the window remap it needs, and park
        // it on the window centre so it culls with the window. The remap rides its own block so _IsPatch
        // never leaks onto the flat surface renderers. The transform is cosmetic (the shader places the
        // verts via PoolToWorld); it only sizes the culling bounds.
        void PositionPatch(Renderer patch, ref MaterialPropertyBlock block)
        {
            if (patch == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            WriteBodyProps(block);

            Vector3 poolCenter = WorldToPool(SimWindowCenter);
            block.SetFloat(ID_IsPatch, 1f);
            block.SetFloat(ID_PatchDepthBias, PatchDepthBiasMeters);
            block.SetVector(ID_PatchPoolCenter, new Vector4(poolCenter.x, poolCenter.z, 0f, 0f));
            block.SetVector(ID_PatchPoolHalf, new Vector4(
                SimHorizontalExtent / VolumeExtentSafe.x, SimHorizontalExtent / VolumeExtentSafe.z, 0f, 0f));
            patch.SetPropertyBlock(block);

            Transform t = patch.transform;
            t.position = SimWindowCenter;
            t.localScale = SimHalfExtent;
        }

        // Build the windowed near-field patch: a grid at the sim resolution, remapped by the
        // shader into the window's pool sub-region. Reuses THIS body's surface material instance
        // (so it inherits reflections/fog) with _IsPatch riding its property block. Play mode
        // only - it depends on the per-body material instance created in ApplyReflections.
        void CreateSimWindowPatch()
        {
            if (!Application.isPlaying || !_windowed) return;
            if (_patchRenderer != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            _patchGrid = WaterMeshBuilder.BuildGrid(Mathf.Max(1, _simRes));
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
