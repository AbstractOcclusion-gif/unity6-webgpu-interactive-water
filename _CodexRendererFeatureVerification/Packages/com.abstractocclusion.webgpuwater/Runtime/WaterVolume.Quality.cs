// WebGpuWater - WaterVolume partial: applying the quality tier to this body.
//
// One place for every tier-driven downgrade, so "what does a low tier actually change?" is
// answered by reading one file: the coarse surface grid swap, the pipeline-wide URP knobs the
// primary body owns, the tier cost knobs themselves, and the ripple-density resolution that
// scales the sim grid to the body's footprint. All of it runs before the RTs exist (see
// TryInitialize in WaterVolume.cs), so the resolutions are fixed for the session.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // ---- Low-tier surface grid swap ----------------------------------------
        // The authored grid is 200x200 and the vertex shader runs 4 fetches + the wave sines
        // per vertex; a 128 sim doesn't need that tessellation. Play mode only (an edit-mode
        // swap could serialize the runtime mesh reference into the scene), mirroring the
        // material-instance pattern: originals restored on disable.
        void ApplyMeshDetail()
        {
            if (!Application.isPlaying) return;

            int detail = SurfaceMeshDetail();
            if (detail <= 0) return; // keep the authored mesh

            _lowDetailGrid = discSurface
                ? WaterMeshBuilder.BuildDisc(detail, Mathf.Max(detail, DiscSurfaceMinSegments))
                : WaterMeshBuilder.BuildGrid(detail);
            _lowDetailGrid.hideFlags = HideFlags.HideAndDontSave;
            SwapRendererMesh(surfaceAbove, _lowDetailGrid, ref _surfaceAboveOriginalMesh);
            SwapRendererMesh(surfaceUnder, _lowDetailGrid, ref _surfaceUnderOriginalMesh);
        }

        // Bounded bodies match the surface grid to the sim grid (one vertex per texel) so displaced
        // ripples are round rather than faceted triangles; the vertex count follows the ripple quality.
        // Windowed bodies keep the tier's mesh-detail override (their dense near-field is the separate
        // sim-window patch, so their main plane needs no matching).
        int SurfaceMeshDetail() => _windowed ? _meshDetail : _simRes;

        void RestoreMeshDetail()
        {
            RestoreRendererMesh(surfaceAbove, ref _surfaceAboveOriginalMesh);
            RestoreRendererMesh(surfaceUnder, ref _surfaceUnderOriginalMesh);
            if (_lowDetailGrid != null) { WaterObjects.DestroyRuntime(_lowDetailGrid); _lowDetailGrid = null; }
        }

        // The caustic pass shares whichever grid the surface uses this session.
        Mesh EffectiveWaterMesh => _lowDetailGrid != null ? _lowDetailGrid : waterMesh;

        static void SwapRendererMesh(Renderer r, Mesh replacement, ref Mesh original)
        {
            original = null;
            if (r == null) return;
            var filter = r.GetComponent<MeshFilter>();
            if (filter == null) return;
            original = filter.sharedMesh;
            filter.sharedMesh = replacement;
        }

        static void RestoreRendererMesh(Renderer r, ref Mesh original)
        {
            if (original == null) return;
            var filter = r != null ? r.GetComponent<MeshFilter>() : null;
            if (filter != null) filter.sharedMesh = original;
            original = null;
        }

        // ---- Low-tier global URP knobs ------------------------------------------
        // Render scale and the opaque-texture copy are PIPELINE-wide, so the primary body
        // applies them once (play mode only) and restores the authored values on disable -
        // the asset never keeps a tier's values.
#if WEBGPUWATER_URP
        static WaterVolume _pipelineOwner; // the body that applied the tweaks (and must restore them)
        // The asset the values were SAVED FROM. Restore used to re-read
        // UniversalRenderPipeline.asset at teardown, which is whatever is active THEN: switch quality
        // level during play and the other tier's asset was permanently stamped with this one's
        // render scale. A saved reference cannot be fooled by the switch.
        static UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset _pipelineAsset;
        static float _savedRenderScale;
        static bool _savedOpaqueTexture;
#endif

        void ApplyPipelineTier()
        {
#if WEBGPUWATER_URP
            if (!Application.isPlaying || !isPrimary || _pipelineOwner != null) return;
            var pipeline = UnityEngine.Rendering.Universal.UniversalRenderPipeline.asset;
            if (pipeline == null) return;

            bool wantScale = _renderScale < 1f;
            bool wantOpaqueOff = !_realRefractionAllowed; // nothing else in the package reads the opaque copy
            if (!wantScale && !wantOpaqueOff) return;

            _pipelineAsset = pipeline;
            _savedRenderScale = pipeline.renderScale;
            _savedOpaqueTexture = pipeline.supportsCameraOpaqueTexture;
            if (wantScale) pipeline.renderScale = _renderScale;
            if (wantOpaqueOff) pipeline.supportsCameraOpaqueTexture = false;
            _pipelineOwner = this;
#endif
        }

        void RestorePipelineTier()
        {
#if WEBGPUWATER_URP
            if (_pipelineOwner != this) return; // only the body that applied restores
            // The SAVED asset, never the currently-active one (see _pipelineAsset).
            if (_pipelineAsset != null)
            {
                _pipelineAsset.renderScale = _savedRenderScale;
                _pipelineAsset.supportsCameraOpaqueTexture = _savedOpaqueTexture;
            }
            _pipelineAsset = null;
            _pipelineOwner = null;
#endif
        }

        // Apply the quality tier's cost knobs. Called once at startup, before the sim/caustic
        // RTs are created, so the resolutions are fixed for the session (a tier change takes
        // effect on restart). With no asset assigned the inspector defaults are left untouched
        // (_simRes stays at its default), so existing scenes are unaffected.
        void ApplyQuality()
        {
            // No asset assigned no longer means "assume desktop". WaterQuality.Fallback probes the
            // device exactly as an Auto asset would; on an unconstrained desktop that resolves
            // field-for-field to Tier.Default, so nothing changes there - but a WebGPU / mobile /
            // no-async-readback build now gets Low instead of the full desktop configuration, which
            // is what makes every tier knob below actually reachable on the web target.
            // (A desktop under MidGraphicsMemoryMB now resolves to Medium rather than High. That is
            // the probe doing its job, but it IS a behaviour change on low-VRAM machines.)
            WaterQuality source = quality != null ? quality : WaterQuality.Fallback;

            WaterQuality.Tier tier = source.Resolve();
            _simRes = tier.SimResolution;
            // Runtime field, NOT the serialized causticResolution: ApplyQuality also runs in edit
            // mode (TryInitialize under [ExecuteAlways]), and writing the serialized field baked the
            // device-probed tier value into authored scene data on save. Every other tier knob
            // already uses a '_' runtime field; this one was the odd one out.
            _causticRes = tier.CausticResolution;
            _godRaysAllowed = tier.GodRays;
            _richReflectionsAllowed = tier.RichReflections;
            // Delivered per-body through WriteBodyUniforms (property block), never by writing
            // the shared god-ray material - which dirtied the asset in the editor and let
            // multiple bodies stomp each other's step count. Clamped >= 1 so a "god rays off"
            // tier (0 steps) can't bake a divide-by-zero; the renderer is disabled separately.
            _godRaySteps = Mathf.Max(1, tier.GodRaySteps);
            _maxWaveCount = tier.MaxWaveCount;
            _peakedRefineSteps = tier.RefineSteps;
            _renderScale = tier.RenderScale;
            _realRefractionAllowed = tier.RealRefraction;
            _meshDetail = tier.MeshDetail;
            _causticInterval = tier.CausticInterval;
            _readbackInterval = tier.ReadbackInterval;
            _oceanFftInterval = tier.OceanFftInterval;
            _maxFoamParticles = tier.MaxFoamParticles;
            _underwaterFogMode = tier.UnderwaterFog;

            // One line per enable so a DEVELOPMENT build's console shows exactly which knobs landed -
            // tier mismatches (stale build cache, wrong asset, missing serialized fields) are
            // otherwise near-impossible to diagnose on a device. Editor + development builds only:
            // under [ExecuteAlways] this also fires on every domain reload, and a shipped package has
            // no business writing to a customer's release-build console.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"WaterVolume '{name}': quality tier applied - sim {_simRes}, caustics {EffectiveCausticResolution}, " +
                      $"mesh {(_meshDetail > 0 ? _meshDetail.ToString() : "authored")}, renderScale {_renderScale:0.##}, " +
                      $"realRefraction {_realRefractionAllowed}, godRays {_godRaysAllowed} ({_godRaySteps} steps), " +
                      $"waves {_maxWaveCount}, refine {_peakedRefineSteps}, foamCap {_maxFoamParticles}, " +
                      $"underwaterFog {_underwaterFogMode}", this);
#endif
        }

        // ---- Runtime tier state, writable for diagnostics (WaterCostProbe) ----------------
        // Both wrap fields ApplyQuality already owns and NOTHING serialises, which is the whole
        // reason the probe is allowed to write them: no code path here can bake a value into saved
        // scene data (the trap ApplyQuality records for causticResolution). Every consumer reads
        // these per frame - UpdateUnderwaterState re-derives the fog gates and republishes
        // _UnderwaterFogSimple, and LargeGodRayDensity re-gates the ocean shafts - so a change lands
        // on the very next frame with no restart and no extra plumbing.

        /// <summary>The tier's underwater fog cost mode for this body. Off = the fullscreen pass
        /// never enqueues; Simple = closed-form flat waterline; Full = the per-pixel wavy march.</summary>
        internal WaterQuality.UnderwaterMode UnderwaterFogMode
        {
            get => _underwaterFogMode;
            set => _underwaterFogMode = value;
        }

        /// <summary>Whether the tier permits god-ray shafts on this body - pool box AND ocean
        /// clipmap. See <see cref="LargeGodRayDensity"/>: the ocean path had never consulted this.</summary>
        internal bool GodRaysAllowed
        {
            get => _godRaysAllowed;
            set => _godRaysAllowed = value;
        }

        // Scale the interactive-sim grid to the body's footprint at the chosen ripple quality so
        // world-metres-per-texel stays roughly constant, keeping ripples crisp on larger planes. Rounded
        // up to the compute thread-group size (the sim requires a multiple), then clamped to the
        // quality's floor/cap.
        int ResolveDensitySimResolution()
        {
            RippleQualitySetting setting = RippleQualityTable[rippleQuality];
            float fullWidth = 2f * Mathf.Max(VolumeExtentSafe.x, VolumeExtentSafe.z);
            int group = WaterSimulation.ThreadGroupSize;
            int target = Mathf.CeilToInt(fullWidth * setting.TexelsPerMeter);
            target = Mathf.CeilToInt(target / (float)group) * group;
            return Mathf.Clamp(target, setting.MinResolution, setting.MaxResolution);
        }

        // ---- Scale-invariant ripples on cap-limited grids --------------------------------------------
        // How coarse the sim grid actually is versus the tier's authored texels-per-metre: 1 while the
        // grid holds tier density (every body below the resolution cap - their look is untouched), < 1
        // once the cap forces metres-per-texel to grow (bounded bodies wider than cap/texelsPerMeter,
        // and windowed bodies whose window outgrows the tier resolution). Feeds three corrections that
        // are all identity at 1: wave-speed dispersion, damping-per-world-metre, and drop-floor energy.
        // Without them the integrator's fixed texel-space units make world propagation speed, energy
        // persistence and injected footprints all drift with extent - the "harsh above 5 m, intensity
        // needs re-tweaking per size" complaint.
        float _simDensityRatio = 1f;

        void ResolveSimDensityRatio()
        {
            RippleQualitySetting setting = RippleQualityTable[rippleQuality];
            float fullWidth = 2f * (_windowed ? SimHorizontalExtent
                                              : Mathf.Max(VolumeExtentSafe.x, VolumeExtentSafe.z));
            float actualTexelsPerMeter = _simRes / Mathf.Max(fullWidth, MinVolumeExtent);
            // Never > 1: a small body clamped UP to the tier's minimum resolution is denser than
            // authored, which needs no correction (and boosting wave speed there would break CFL).
            _simDensityRatio = Mathf.Min(1f, actualTexelsPerMeter / setting.TexelsPerMeter);
        }

        // NOTE on the drop footprint floor: the sim floors every drop to MinDropTexelRadius texels,
        // which is physically wider on a cap-limited grid. Strength compensation for that widening
        // was tried in two flavours (volume-conserving ratio^2, then linear width ratio) and BOTH
        // rejected: any peak reduction reads as "ripples are weaker on big ponds" - incoherent.
        // With the wave speed and damping corrections above keeping the DYNAMICS world-consistent,
        // an uncompensated equal world PEAK (guaranteed by the strength / extent.y division in
        // AddRipple) is what actually looks coherent across sizes; only the bump footprint widens.
    }
}
