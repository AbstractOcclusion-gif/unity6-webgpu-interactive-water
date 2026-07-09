// WebGpuWater - standalone planar reflection component (Unity 6 / URP).
//
// Renders the scene mirrored across a single water plane and publishes it as the GLOBAL
// _PlanarReflectionTex. This is the camera-level, whole-scene planar path (one plane, one mirror).
// WaterVolume now drives planar PER BODY (each pool renders its own mirror into its own property
// block), so this component is optional and only useful for a single global mirror; both paths reuse
// the same PlanarMirror renderer, so the matrix math lives in exactly one place.
//
// URP-only: the render path uses URP's single-camera render request, so its body compiles only when
// the Universal Render Pipeline is present (WEBGPUWATER_URP). The class and its inspector fields
// always exist, so callers that toggle it still compile on projects without URP - it simply does nothing.
using UnityEngine;
#if WEBGPUWATER_URP
using UnityEngine.Rendering;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    public class PlanarReflection : MonoBehaviour
    {
        [Tooltip("Camera the reflection is rendered for. Defaults to Camera.main.")]
        [SerializeField] internal Camera sourceCamera;

        [Tooltip("World-space height of the water surface (the demo's pool surface is y = 0).")]
        [SerializeField] internal float waterHeight = 0f;

        [Tooltip("Layers included in the reflection. Exclude the water layer itself.")]
        [SerializeField] internal LayerMask reflectLayers = ~0;

        [Range(0.25f, 1f)]
        [Tooltip("Reflection RT size as a fraction of the screen. Lower = faster, blurrier.")]
        [SerializeField] internal float resolutionScale = 0.5f;

        [Tooltip("Push the clip plane slightly below the surface to avoid seam artifacts.")]
        [SerializeField] internal float clipPlaneOffset = 0.02f;

        [Tooltip("Render the reflection at all. Turn off to disable planar reflections cheaply.")]
        [SerializeField] internal bool enableReflection = true;

#if WEBGPUWATER_URP
        static readonly int ID_PlanarTex = Shader.PropertyToID("_PlanarReflectionTex");

        PlanarMirror _mirror;

        void OnEnable() { RenderPipelineManager.beginCameraRendering += OnBeginCamera; }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            _mirror?.Dispose();
            _mirror = null;
            Shader.SetGlobalTexture(ID_PlanarTex, Texture2D.blackTexture);
        }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!enableReflection) return;

            var src = sourceCamera != null ? sourceCamera : Camera.main;
            if (src == null || cam != src) return; // only mirror the main camera

            _mirror ??= new PlanarMirror("PlanarReflectionTex");
            _mirror.Render(src, waterHeight, resolutionScale, clipPlaneOffset, reflectLayers);
            if (_mirror.Texture != null) Shader.SetGlobalTexture(ID_PlanarTex, _mirror.Texture);
        }
#endif
    }
}
