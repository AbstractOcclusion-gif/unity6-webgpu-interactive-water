// WebGpuWater - screen-space caustic projection render feature (URP, RenderGraph).
// Paints the projected caustic pattern onto ANY underwater surface (terrain, Standard Lit props, a bare
// ocean floor with no WaterReceiver) by reading the depth buffer and reusing the water's own pool-space
// projection. Add this feature once to the renderer used by the water camera and assign the
// WaterCausticProjection shader; it self-gates on WaterVolume.AnyCausticProjectionWork(), so it only
// enqueues when at least one body can contribute visible caustic light or a valid refracted shadow.
//
// WIRING / CAVEATS:
//  * Must be ADDED to the URP Renderer asset(s) the water camera uses, and the shader assigned - exactly
//    like WaterUnderwaterFogFeature (which had to be re-added to Mobile_RPAsset / Mobile_Renderer for
//    builds; do the same here if you target those).
//  * Double-caustics is avoided by stencil (Approach A): WaterReceiver / AnalyticPool write stencil bit 3
//    and this pass skips those pixels, so they are visually unchanged. If your project uses screen-space
//    decals or a Render Objects feature that also writes URP user stencil bit 3 (0x08) on submerged
//    geometry, those pixels would be skipped too - re-home the bit in both places if so.
//  * PER BODY: the pass draws one fullscreen projection per body that has Screen-Space Caustics on, each
//    framed on that body's own caustic RT + volume frame (via WriteBodyProps into a per-draw block). So a
//    SECONDARY chunk's foreign floors receive the CHUNK's caustics, not just the primary's.
//  * This adds the caustic PATTERN only. It does not fix the object SHADOW on foreign shaders (the
//    separate un-refracted-URP-shadow limitation); use WaterReceiver on submerged props for that.
//
// URP-only: ScriptableRendererFeature is a URP type, so the whole file compiles only when the Universal
// Render Pipeline is present (WEBGPUWATER_URP).
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterCausticProjectionFeature : ScriptableRendererFeature
    {
        // Defaults mirror WaterReceiver's / AnalyticPool's controls so foreign surfaces read the same.
        const float DefaultCausticStrength = 4f;
        const float DefaultRefractedShadowStrength = 0.6f;

        [Tooltip("The AbstractOcclusion/WebGpuWater/WaterCausticProjection shader. Assign the shader asset of that name.")]
        [SerializeField] Shader causticProjectionShader;

        [Tooltip("Brightness of the projected caustics on foreign surfaces. Matches WaterReceiver's Caustic Strength (default 4).")]
        [Range(0f, 8f)]
        [SerializeField] float causticStrength = DefaultCausticStrength;

        [Tooltip("Colour tint of the projected caustics, applied like WaterReceiver's Caustic Tint.")]
        [SerializeField] Color causticTint = Color.white;

        [Tooltip("Also project the REFRACTED object shadow onto foreign underwater surfaces (terrain, Standard " +
                 "Lit) - the shadow those shaders can't refract themselves. Owned surfaces (WaterReceiver / " +
                 "AnalyticPool) already do this in-shader and are skipped. NOTE: foreign surfaces still receive " +
                 "URP's own un-refracted shadow from submerged casters, so with a low sun you may see a second, " +
                 "offset shadow; drop those casters' URP shadow (or convert them to WaterReceiver) to avoid it.")]
        [SerializeField] bool projectRefractedShadows = true;

        [Tooltip("How dark the refracted object shadow is on foreign surfaces (0 = none, 1 = black). Matches AnalyticPool's Object Shadow Strength (default 0.6).")]
        [Range(0f, 1f)]
        [SerializeField] float refractedShadowStrength = DefaultRefractedShadowStrength;

        WaterCausticProjectionPass _pass;
        Material _material;

        static readonly int ID_CausticStrength = Shader.PropertyToID("_CausticStrength");
        static readonly int ID_CausticTint = Shader.PropertyToID("_CausticTint");
        static readonly int ID_RefractedShadowStrength = Shader.PropertyToID("_RefractedShadowStrength");

        public override void Create()
        {
        // Release BEFORE (re)creating. URP calls Create() on OnEnable, on OnValidate and on every
        // domain reload, but Dispose() only when the feature asset is destroyed - so allocating here
        // without releasing first leaked one engine Material (and, where the pass owns RTHandles, the
        // pass's history targets) per inspector tweak. Create and Dispose now share ONE teardown, so
        // they cannot drift.
            ReleaseResources();
            if (causticProjectionShader == null) { _pass = null; return; } // unassigned: feature is inert
            _material = CoreUtils.CreateEngineMaterial(causticProjectionShader);
            ApplyMaterialParameters();
            _pass = new WaterCausticProjectionPass(_material);
        }

        // Re-applied on every enqueue so inspector edits to strength/tint take effect live (Create already
        // seeds them; this keeps them current without forcing a full feature rebuild).
        void ApplyMaterialParameters()
        {
            if (_material == null) return;
            _material.SetFloat(ID_CausticStrength, causticStrength);
            _material.SetColor(ID_CausticTint, causticTint);
            _material.SetFloat(ID_RefractedShadowStrength, refractedShadowStrength);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Never for material/prefab thumbnails - see WaterPassCameraGate.
            // Fullscreen paint: also excluded from reflections. See WaterPassCameraGate.
            if (WaterPassCameraGate.SkipCameraFullscreen(renderingData.cameraData.cameraType)) return;
            if (_pass == null) return; // shader unassigned / not created
            bool renderCaustics = causticStrength > 0f;
            bool renderRefractedShadows = projectRefractedShadows && refractedShadowStrength > 0f;
            if (!WaterVolume.AnyCausticProjectionWork(renderCaustics, renderRefractedShadows)) return;
            ApplyMaterialParameters();
            _pass.renderCaustics = renderCaustics;
            _pass.renderRefractedShadow = renderRefractedShadows;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }
    }
}
#endif
