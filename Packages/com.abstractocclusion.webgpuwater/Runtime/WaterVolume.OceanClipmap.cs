// WebGpuWater - WaterVolume: horizon geometry-CLIPMAP driver (unbounded oceans).
// Split out of WaterVolume.cs (final-clean E, verbatim move - any behavior change here is a bug):
// the nested-LOD annulus levels (above + under twins), their world-lattice snapping and geomorph
// uniforms, and build / per-frame placement / teardown. The template mesh itself comes from
// LargeWaterClipmap; the level-count/reach derivations live with the Ocean settings.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        /// <summary>Every LIVE renderer currently drawing this body's ocean surface (the clipmap
        /// levels, the near-field window patches, and the base plane sheets when enabled), for the
        /// underwater fog's surface-depth prepass. The prepass re-draws each with its OWN mesh,
        /// matrix, material and property block, so it displaces exactly like the visible surface
        /// by construction.</summary>
        internal void CollectOceanSurfaceRenderers(List<Renderer> into)
        {
            AddLiveRenderer(into, surfaceAbove);
            AddLiveRenderer(into, surfaceUnder);
            AddLiveRenderer(into, _patchRenderer);
            AddLiveRenderer(into, _patchUnderRenderer);
            if (_clipmapLevels == null) return;
            for (int i = 0; i < _clipmapLevels.Length; i++)
            {
                AddLiveRenderer(into, _clipmapLevels[i].above);
                AddLiveRenderer(into, _clipmapLevels[i].under);
            }
        }

        /// <summary>Every LIVE renderer drawing this body's ABOVE-water surface (base sheet,
        /// near-field window patch, clipmap above levels) - the sheets whose pond foam the
        /// after-fog PondFoamOverlay pass re-draws. Under twins are deliberately absent: the
        /// underside draws its foam at queue time (the fog is in front of it there), so
        /// overlaying it would lay foam twice.</summary>
        internal void CollectAboveSurfaceRenderers(List<Renderer> into)
        {
            AddLiveRenderer(into, surfaceAbove);
            AddLiveRenderer(into, _patchRenderer);
            if (_clipmapLevels == null) return;
            for (int i = 0; i < _clipmapLevels.Length; i++)
                AddLiveRenderer(into, _clipmapLevels[i].above);
        }

        static void AddLiveRenderer(List<Renderer> into, Renderer renderer)
        {
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                into.Add(renderer);
        }

        // geometry clipmap (see LargeWaterClipmap). One shared uniform-grid template is drawn as N nested
        // LOD levels; each level scales the template to its cell size and SNAPS its centre to that level's
        // own world lattice, so its vertices never slide under the world-space waves as the camera follows
        // (the "swim" the old radial mesh suffered). The _IsClipmap flag + per-level morph uniforms ride
        // ONE shared block re-stamped per level (see _clipmapBlock), so nothing leaks onto the pool-grid
        // renderers. An underside twin
        // per level (opposite cull, same material family as the bounded under-surface) reaches the horizon
        // for the submerged view; its centre hole is filled by the near-field under-patch.
        struct ClipmapLevel
        {
            public MeshRenderer above;
            public MeshRenderer under;                 // null when the body has no under-surface material
            public float cellSize;                     // world metres per grid cell at this level
            public float depthBias;                    // view-space nudge toward the camera; finer levels win an overlap
            public float morphStart;                   // cheb cell distance where the edge geomorph begins (>= M/2 = off)
            public float morphScale;                   // 1 / morph-band width in cells
        }
        ClipmapLevel[] _clipmapLevels;
        Mesh _clipmapTemplate;                         // shared uniform square-annulus grid backing every level
        // ONE property block for every level. SetPropertyBlock COPIES into the renderer, so all 20
        // renderers of a default ocean (9 levels x above/under, plus the near patches) can share one
        // instance: the ~138 body uniforms go in ONCE per frame and only the four per-level floats are
        // re-stamped between draws. Writing them per level meant ~2,840 native property writes a frame
        // to publish 138 byte-identical values twenty times over.
        MaterialPropertyBlock _clipmapBlock;
        static readonly int ID_IsClipmap = Shader.PropertyToID("_IsClipmap");
        static readonly int ID_ClipmapMorphStart = Shader.PropertyToID("_ClipmapMorphStart");
        static readonly int ID_ClipmapMorphScale = Shader.PropertyToID("_ClipmapMorphScale");
        const string ClipmapObjectName = "Ocean Clipmap";
        const string ClipmapUnderObjectName = "Ocean Clipmap (under)";

        // Re-place every clipmap LOD level each frame (per-level world-lattice snap + per-level uniforms).
        void ApplyClipmapBlock()
        {
            if (_clipmapLevels == null) return;
            // Body uniforms ONCE for the whole clipmap. The per-level floats below are written
            // unconditionally for every level, so nothing stale survives even though the block is
            // persistent now (cached publisher sinks push only changed body values).
            _clipmapBlock ??= new MaterialPropertyBlock();
            WriteBodyProps(_clipmapBlock);
            for (int i = 0; i < _clipmapLevels.Length; i++)
                PositionClipmapLevel(_clipmapLevels[i]);
        }

        // Place one LOD level: snap its centre to the level's own world lattice, scale the shared template
        // to the level's cell size, and push its per-level uniforms (the _IsClipmap flag, the edge geomorph
        // band, and a small toward-camera depth bias so a finer level wins where it overlaps a coarser one).
        // The above and under twins share the centre + scale; only their material (and cull) differ.
        void PositionClipmapLevel(ClipmapLevel level)
        {
            Vector3 center = ClipmapLevelSnappedCenter(level.cellSize);
            Vector3 scale = new Vector3(level.cellSize, 1f, level.cellSize); // template verts are in cell units
            PlaceClipmapRenderer(level.above, center, scale, level, isUnderTwin: false);
            PlaceClipmapRenderer(level.under, center, scale, level, isUnderTwin: true);
        }

        // The above/under twins are the SAME lattice drawn twice with opposite culling, so
        // wherever both rasterize a pixel their depths are EXACTLY equal and the winner is a
        // per-pixel coin toss. Where the UNDER twin wins a tie seen from the air (or the above
        // twin seen from below), the pixel shades the WRONG MEDIUM - the dark specks along
        // crest silhouettes (2026-08-11). Resolve every tie DETERMINISTICALLY toward the twin
        // matching the camera's medium, through the same _PatchDepthBias plumbing the patch
        // already trusts (view-space metres; the eye-depth prepass stores PHYSICAL depth, so
        // this can only ever order the raster, never corrupt the fog's data).
        // LARGER THAN THE WHOLE LOD BIAS SPREAD (patch 0.02 m, level steps ~0.002 m) so a
        // matched COARSE level still beats a mismatched FINE one where LODs overlap; among
        // matched twins the per-level bias still ranks them, so the finest present keeps
        // winning overall.
        const float MediumMatchedTwinBiasMeters = 0.04f;

        // STOOD DOWN while the waterline straddles the near plane: a deterministic winner is
        // wrong for half of a straddling screen (the failed _Underwater-flip idea,
        // 2026-08-11), so those few frames keep the coin toss - the meniscus band covers
        // them. Shared by the clipmap twins here and the near-field patch twins
        // (WaterVolume.SimWindowPatch.PositionPatch).
        static float MediumMatchedTwinExtraBias(bool isUnderTwin)
        {
            if (WaterlineActive) return 0f;
            return (CameraSubmerged == isUnderTwin) ? MediumMatchedTwinBiasMeters : 0f;
        }

        // The body uniforms are already in _clipmapBlock (ApplyClipmapBlock wrote them once this frame);
        // only the four per-level floats are re-stamped here. SetPropertyBlock copies, so the next level
        // overwriting them cannot reach a renderer that has already been handed the block. ApplyClipmapBlock
        // is the sole path in, so _clipmapBlock is non-null by construction.
        void PlaceClipmapRenderer(MeshRenderer renderer, Vector3 center, Vector3 scale, ClipmapLevel level,
                                  bool isUnderTwin)
        {
            if (renderer == null) return;
            _clipmapBlock.SetFloat(ID_IsClipmap, 1f);
            // Per-level LOD ordering plus the camera-medium tie-breaker (see the constant's
            // header above): the twin matching the eye's medium wins every coincident-depth
            // pixel instead of coin-tossing it into the wrong-medium speck.
            _clipmapBlock.SetFloat(ID_PatchDepthBias,
                                   level.depthBias + MediumMatchedTwinExtraBias(isUnderTwin));
            _clipmapBlock.SetFloat(ID_ClipmapMorphStart, level.morphStart);
            _clipmapBlock.SetFloat(ID_ClipmapMorphScale, level.morphScale);
            renderer.SetPropertyBlock(_clipmapBlock);

            Transform t = renderer.transform;
            t.SetPositionAndRotation(center, VolumeRotation);
            t.localScale = scale;
        }

        // Snap the level's follow centre to its own world lattice (multiples of 2*cell in the volume-local
        // frame about VolumeCenter). Because the shared template's vertices sit at integer-cell offsets,
        // snapping to 2*cell keeps every vertex on the fixed world lattice VolumeCenter + cell*Z, so the
        // wave field (a pure function of world XZ) is sampled at stable points as the camera follows - which
        // is what removes the geometry swim. Follows the same target as the sim window (an explicit focus,
        // else the camera); falls back to the window centre when neither exists.
        Vector3 ClipmapLevelSnappedCenter(float cellSize)
        {
            Transform follow = simWindowFocus != null ? simWindowFocus
                             : (targetCamera != null ? targetCamera.transform : null);
            if (follow == null) return SimWindowCenter;

            Vector3 up = VolumeUp;
            Vector3 followPos = follow.position;
            Vector3 onPlane = followPos - Vector3.Dot(followPos - VolumeCenter, up) * up;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (onPlane - VolumeCenter);
            float snap = ClipmapSnapCellMultiple * cellSize;
            local.x = Mathf.Round(local.x / snap) * snap;
            local.z = Mathf.Round(local.z / snap) * snap;
            return VolumeCenter + VolumeRotation * new Vector3(local.x, 0f, local.z);
        }

        // Build the unbounded-ocean clipmap: a radial ring mesh in world metres, reusing THIS body's
        // surface material with _IsClipmap on its block. Play mode only, and only when the body is a
        // true ocean (open water + opt-in + sim window). Fails loudly if the sim window is missing,
        // because without it the near-field ripple fade can't keep the far field clean.
        void CreateOceanClipmap()
        {
            if (!Application.isPlaying || !renderBuiltInGeometry) return;
            if (openWater && unboundedOcean && !_windowed)
            {
                Debug.LogWarning("WaterVolume: Unbounded Ocean needs the large-body sim window " +
                                 "(Enable Large Body Window) for near-field ripples; the surface stays " +
                                 "the bounded plane until it is enabled.", this);
                return;
            }
            if (!IsOceanClipmap) return;
            if (_clipmapLevels != null || surfaceAbove == null || surfaceAbove.sharedMaterial == null) return;

            // One shared uniform square-annulus template (integer cell units); every LOD level scales and
            // snaps it independently. The central hole sits just inside the near-field patch so the dense
            // patch owns the near field (its depth bias covers the overlap ring), and each level's hole is
            // shrunk by the overlap margin so consecutive levels overlap rather than crack at the seam.
            _clipmapTemplate = LargeWaterClipmap.BuildAnnulusTemplate(ClipmapGridRes, ClipmapHoleHalfCells);
            _clipmapTemplate.hideFlags = HideFlags.HideAndDontSave;

            int levelCount = ClipmapLevelCount;
            float baseCell = ClipmapBaseCell;
            float morphBandCells = Mathf.Max(1f, Mathf.Round((ClipmapGridRes / 4f) * ClipmapMorphBandFraction));
            float biasStep = PatchDepthBiasMeters / (levelCount + 1);   // every level stays under the patch's bias
            bool buildUnder = surfaceUnder != null && surfaceUnder.sharedMaterial != null;

            _clipmapLevels = new ClipmapLevel[levelCount];
            for (int level = 0; level < levelCount; level++)
            {
                bool outermost = level == levelCount - 1;
                var entry = new ClipmapLevel
                {
                    cellSize = baseCell * Mathf.Pow(2f, level),
                    // Finer levels get a larger toward-camera nudge so they win where they overlap a coarser
                    // one; all stay below the patch bias so the patch still owns the innermost overlap.
                    depthBias = biasStep * (levelCount - 1 - level),
                    // Outermost level has no coarser neighbour: disable its edge morph by pushing the start
                    // past the outer edge.
                    morphStart = outermost ? ClipmapGridRes : (ClipmapGridRes / 2f - morphBandCells),
                    morphScale = 1f / morphBandCells,
                    above = CreateSurfaceRenderer(ClipmapObjectName, _clipmapTemplate, surfaceAbove.sharedMaterial),
                };
                if (buildUnder)
                    entry.under = CreateSurfaceRenderer(ClipmapUnderObjectName, _clipmapTemplate, surfaceUnder.sharedMaterial);
                _clipmapLevels[level] = entry;
            }
        }

        // Enable/disable every LOD level's above + under renderer together.
        void SetClipmapRenderersEnabled(bool on)
        {
            if (_clipmapLevels == null) return;
            for (int i = 0; i < _clipmapLevels.Length; i++)
            {
                SetRendererEnabled(_clipmapLevels[i].above, on);
                SetRendererEnabled(_clipmapLevels[i].under, on);
            }
        }

        void DestroyOceanClipmap()
        {
            if (_clipmapLevels != null)
            {
                for (int i = 0; i < _clipmapLevels.Length; i++)
                {
                    if (_clipmapLevels[i].above != null) WaterObjects.DestroyRuntime(_clipmapLevels[i].above.gameObject);
                    if (_clipmapLevels[i].under != null) WaterObjects.DestroyRuntime(_clipmapLevels[i].under.gameObject);
                }
                _clipmapLevels = null;
            }
            WaterObjects.DestroyRuntime(_clipmapTemplate);
            _clipmapTemplate = null;
        }
    }
}
