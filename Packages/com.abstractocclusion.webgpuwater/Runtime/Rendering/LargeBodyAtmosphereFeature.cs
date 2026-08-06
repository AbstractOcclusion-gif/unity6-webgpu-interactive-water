// WebGpuWater - large-body atmosphere render feature (URP, RenderGraph).
// Adds the ocean-scale god-ray shafts to a URP renderer. Add this feature once to the renderer
// used by the ocean camera and assign the LargeBodyGodRays shader; it self-gates, so it costs
// nothing and changes nothing on scenes without an unbounded ocean with god rays enabled.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the
// Universal Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class LargeBodyAtmosphereFeature : ScriptableRendererFeature
    {
        [Tooltip("The AbstractOcclusion/WebGpuWater/LargeBodyGodRays shader. Assign the shader asset of that name.")]
        [SerializeField] Shader godRayShader;

        LargeBodyAtmospherePass _pass;
        Material _material;
        // Pad above the wave envelope for the provably-dry camera reject in AddRenderPasses -
        // covers camera travel between this record-time test and the frame actually rendering
        // (FogArmBandMeters plays the same role at the fog's arming site).
        const float DryCameraPadMeters = 0.5f;

        public override void Create()
        {
        // Release BEFORE (re)creating. URP calls Create() on OnEnable, on OnValidate and on every
        // domain reload, but Dispose() only when the feature asset is destroyed - so allocating here
        // without releasing first leaked one engine Material (and, where the pass owns RTHandles, the
        // pass's history targets) per inspector tweak. Create and Dispose now share ONE teardown, so
        // they cannot drift.
            ReleaseResources();
            if (godRayShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(godRayShader);
            _pass = new LargeBodyAtmospherePass(_material);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Never for material/prefab thumbnails - see WaterPassCameraGate.
            // Fullscreen paint: also excluded from reflections - god-ray shafts belong in the view,
            // never composited into the mirror the view reflects. See WaterPassCameraGate.
            if (WaterPassCameraGate.SkipCameraFullscreen(renderingData.cameraData.cameraType)) return;
            if (_pass == null) return;                                // shader unassigned / not created
            if (!LargeBodyAtmosphereGate.HasActiveGodRayOcean) return; // ocean-only, and only when shafts are on
            // CPU mirror of the raymarch's own conservative reject (LargeBodyGodRays.shader:
            // "above the highest point the displaced surface can reach ... nothing below can
            // draw"). With the camera provably dry and the from-air veil off, every march pixel
            // early-outs and the composite multiplies the scene by 1 - yet the frame still paid
            // the half-res march target, its history copy, and a full-res composite whose
            // coverage mask costs ~6 surface fetches per pixel, on every ABOVE-water ocean
            // frame. Skip enqueueing instead. The envelope is the SAME derivation the fog's arm
            // gate uses (SurfaceHeightEnvelope), so the two reject heights cannot drift.
            // Temporal history is safe: the skip zone sits strictly ABOVE the envelope, so a
            // descending camera re-runs the pass (re-warming its history) through the whole
            // envelope band before any shaft can become visible.
            WaterVolume sourceOcean = LargeBodyAtmosphereGate.SourceOcean;
            Camera atmosphereCamera = renderingData.cameraData.camera;
            if (sourceOcean != null && atmosphereCamera != null
                && sourceOcean.LargeGodRayFromAir <= 0f
                && atmosphereCamera.transform.position.y
                   > sourceOcean.VolumeCenter.y + sourceOcean.SurfaceHeightEnvelope() + DryCameraPadMeters)
                return;
            // A fullscreen-fog debug view owns the frame: these shafts inject one slot AFTER the
            // fog and add WATER-TINTED light concentrated near the waterline, which tinted every
            // false-colour view green exactly where the boundary under investigation sits. An
            // instrument the scene can write on is not an instrument. See WaterDebugView.
            if (WaterDebugView.FogViewActive) return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
        {
            _pass?.Dispose(); // releases the persistent temporal-history RTs
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
