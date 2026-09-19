// WebGpuWater - applies Unity's built-in scene fog to the skybox in URP.
//
// URP does not fog a skybox because it is background, not geometry with a view distance. This pass
// runs immediately after the skybox and before opaque geometry, so it fogs only the background; the
// regular opaque and water passes then use the same RenderSettings fog independently. No water-body
// setting is involved: enabling Unity Fog is the sole opt-in.
#if WEBGPUWATER_URP
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    public sealed class WaterSkyFogFeature : ScriptableRendererFeature
    {
        const string ShaderName = "AbstractOcclusion/WebGpuWater/WaterSkyFog";

        [SerializeField, HideInInspector] Shader skyFogShader;

        WaterSkyFogPass _pass;
        Material _material;

        public override void Create()
        {
            ReleaseResources();
            skyFogShader ??= Shader.Find(ShaderName);
            if (skyFogShader == null) return;
            _material = CoreUtils.CreateEngineMaterial(skyFogShader);
            _pass = new WaterSkyFogPass(_material);
        }

        // Enviro 3 publishes this global (> 0) whenever it is driving the sky; it then owns the sky's
        // fog itself, so this Unity-fog paint over the skybox would double it. Gated on the global,
        // not on the Water Wizard's third-party hook: the double-paint happens whether or not the
        // water opted into Enviro's fog. Reads 0 in any project without Enviro (never-set global).
        static readonly int ID_EnviroActive = Shader.PropertyToID("_EnviroActive");

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || !RenderSettings.fog) return;
            if (Shader.GetGlobalFloat(ID_EnviroActive) > 0f) return;
            // Preview thumbnails never need a fogged skybox - this was the ONE water feature with
            // no camera gate, so it recorded a fullscreen pass for material/prefab previews too.
            // Reflection cameras DELIBERATELY keep the pass: this is scene fog, not a water-volume
            // paint, and the reflected horizon must stay as fogged as the directly-viewed one - so
            // the SkipCameraFullscreen doctrine does not apply here (see WaterPassCameraGate).
            if (WaterPassCameraGate.SkipCamera(renderingData.cameraData.cameraType)) return;
            Camera camera = renderingData.cameraData.camera;
            if (camera == null) return;

            _pass.skyFogOpacity = CalculateSkyFogOpacity(camera.farClipPlane);
            if (_pass.skyFogOpacity <= 0f) return;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing) => ReleaseResources();

        void ReleaseResources()
        {
            CoreUtils.Destroy(_material);
            _material = null;
            _pass = null;
        }

        static float CalculateSkyFogOpacity(float farClipDistance)
        {
            float distance = Mathf.Max(0f, farClipDistance);
            float transmittance = RenderSettings.fogMode switch
            {
                FogMode.Linear => LinearFogTransmittance(distance),
                FogMode.Exponential => Mathf.Exp(-RenderSettings.fogDensity * distance),
                FogMode.ExponentialSquared => Mathf.Exp(-Mathf.Pow(RenderSettings.fogDensity * distance, 2f)),
                _ => 1f,
            };
            return 1f - Mathf.Clamp01(transmittance);
        }

        static float LinearFogTransmittance(float distance)
        {
            float range = RenderSettings.fogEndDistance - RenderSettings.fogStartDistance;
            if (range <= 0f) return distance >= RenderSettings.fogEndDistance ? 0f : 1f;
            return Mathf.Clamp01((RenderSettings.fogEndDistance - distance) / range);
        }
    }
}
#endif
