// WebGL Water - planar reflection (Unity 6 / URP port)
// Renders the scene mirrored across the water plane (y = 0) into a RenderTexture
// and publishes it as the global _PlanarReflectionTex, sampled by WaterSurface.
//
// This is the "planar" half of the hybrid reflection. It is fully optional: if
// disabled (or unsupported on the target backend) the surface shader falls back
// to SSR and then the sky cubemap, so nothing breaks.
//
// Approach: a hidden reflection Camera is positioned by mirroring the main
// camera across the plane, given an oblique near-clip aligned to the water
// surface (so geometry below the water never leaks into the reflection), and
// rendered to a RT each frame in beginCameraRendering.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WebGLWater
{
    [DisallowMultipleComponent]
    public class PlanarReflection : MonoBehaviour
    {
        [Tooltip("Camera the reflection is rendered for. Defaults to Camera.main.")]
        public Camera sourceCamera;

        [Tooltip("World-space height of the water surface (the demo's pool surface is y = 0).")]
        public float waterHeight = 0f;

        [Tooltip("Layers included in the reflection. Exclude the water layer itself.")]
        public LayerMask reflectLayers = ~0;

        [Range(0.25f, 1f)]
        [Tooltip("Reflection RT size as a fraction of the screen. Lower = faster, blurrier.")]
        public float resolutionScale = 0.5f;

        [Tooltip("Push the clip plane slightly below the surface to avoid seam artifacts.")]
        public float clipPlaneOffset = 0.02f;

        [Tooltip("Render the reflection at all. Turn off to disable planar reflections cheaply.")]
        public bool enableReflection = true;

        const int MinReflectionSize = 8;     // don't allocate a sub-8px reflection target
        const int ReflectionDepthBits = 24;  // depth buffer for the mirrored scene render

        static readonly int ID_PlanarTex = Shader.PropertyToID("_PlanarReflectionTex");

        Camera _reflectionCamera;
        RenderTexture _rt;
        Vector2Int _rtSize;
        bool _rendering; // re-entrancy guard

        void OnEnable()  { RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            Cleanup();
            Shader.SetGlobalTexture(ID_PlanarTex, Texture2D.blackTexture);
        }

        void Cleanup()
        {
            if (_reflectionCamera != null)
            {
                if (Application.isPlaying) Destroy(_reflectionCamera.gameObject);
                else DestroyImmediate(_reflectionCamera.gameObject);
                _reflectionCamera = null;
            }
            if (_rt != null) { _rt.Release(); _rt = null; }
        }

        void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (!enableReflection || _rendering) return;

            var src = sourceCamera != null ? sourceCamera : Camera.main;
            if (src == null || cam != src) return; // only mirror the main camera

            EnsureResources(src);

            // Mirror the camera transform across the plane y = waterHeight.
            Vector3 normal = Vector3.up;
            Vector3 pos = src.transform.position;
            Vector3 mirroredPos = pos;
            mirroredPos.y = 2f * waterHeight - pos.y;

            // Reflect the world-to-camera matrix through the plane.
            Matrix4x4 reflection = CalculateReflectionMatrix(new Vector4(normal.x, normal.y, normal.z, -waterHeight));
            _reflectionCamera.worldToCameraMatrix = src.worldToCameraMatrix * reflection;

            // Oblique projection so the near plane sits on the water surface.
            Vector4 clipPlane = CameraSpacePlane(_reflectionCamera, new Vector3(0, waterHeight, 0), normal, clipPlaneOffset);
            _reflectionCamera.projectionMatrix = src.CalculateObliqueMatrix(clipPlane);

            _reflectionCamera.transform.position = mirroredPos;

            // Reflections invert winding order.
            GL.invertCulling = true;
            _rendering = true;
#if UNITY_2022_1_OR_NEWER
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(
                _reflectionCamera,
                new UniversalRenderPipeline.SingleCameraRequest { destination = _rt });
#else
            _reflectionCamera.targetTexture = _rt;
            _reflectionCamera.Render();
#endif
            _rendering = false;
            GL.invertCulling = false;

            Shader.SetGlobalTexture(ID_PlanarTex, _rt);
        }

        void EnsureResources(Camera src)
        {
            int width = Mathf.Max(MinReflectionSize, Mathf.RoundToInt(src.pixelWidth * resolutionScale));
            int height = Mathf.Max(MinReflectionSize, Mathf.RoundToInt(src.pixelHeight * resolutionScale));
            if (_rt == null || _rtSize.x != width || _rtSize.y != height)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(width, height, ReflectionDepthBits, RenderTextureFormat.DefaultHDR)
                {
                    name = "PlanarReflectionTex",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                _rt.Create();
                _rtSize = new Vector2Int(width, height);
            }

            if (_reflectionCamera == null)
            {
                var go = new GameObject("PlanarReflectionCamera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _reflectionCamera = go.AddComponent<Camera>();
                _reflectionCamera.enabled = false; // we drive it manually
            }

            // Copy the important settings each frame so editor tweaks track live.
            _reflectionCamera.CopyFrom(src);
            _reflectionCamera.targetTexture = _rt;
            _reflectionCamera.cullingMask = reflectLayers & src.cullingMask;
            _reflectionCamera.enabled = false;
        }

        // Householder reflection matrix for the plane (n, d).
        static Matrix4x4 CalculateReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = 1f - 2f * plane.x * plane.x; m.m01 = -2f * plane.x * plane.y; m.m02 = -2f * plane.x * plane.z; m.m03 = -2f * plane.x * plane.w;
            m.m10 = -2f * plane.y * plane.x; m.m11 = 1f - 2f * plane.y * plane.y; m.m12 = -2f * plane.y * plane.z; m.m13 = -2f * plane.y * plane.w;
            m.m20 = -2f * plane.z * plane.x; m.m21 = -2f * plane.z * plane.y; m.m22 = 1f - 2f * plane.z * plane.z; m.m23 = -2f * plane.z * plane.w;
            m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
            return m;
        }

        // Plane in the reflection camera's space, for the oblique near clip.
        static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float offset)
        {
            Vector3 offsetPos = pos + normal * offset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cpos = m.MultiplyPoint(offsetPos);
            Vector3 cnormal = m.MultiplyVector(normal).normalized;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }
    }
}
