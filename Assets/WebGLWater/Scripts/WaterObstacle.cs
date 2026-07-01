// WebGL Water - obstacle footprint renderer (Unity 6 / URP port)
// Draws every WaterInteractable top-down into a ping-pong pair of RenderTextures
// (R = submerged amount per column). The compute sim reads (prev - curr) to push
// the surface, generalising the original analytic sphere displacement to any mesh.
using UnityEngine;
using UnityEngine.Rendering;

namespace WebGLWater
{
    public class WaterObstacle
    {
        public RenderTexture Prev => _prev;
        public RenderTexture Curr => _curr;

        const float MinExtent = 1e-4f;        // floor so a zero extent can't collapse the frustum
        const float EyeHeightInExtents = 2f;  // ortho camera sits this many depth-extents above the surface
        const float FarPlaneInExtents = 4f;   // far plane spans this many depth-extents below the eye
        const float OrthoNearPlane = 0.01f;
        const float OrthoFarPad = 0.02f;       // keep the floor just inside the far plane

        readonly Material _mat;
        readonly CommandBuffer _cb;
        readonly MaterialPropertyBlock _mpb;
        readonly int _resolution;
        RenderTexture _prev, _curr;
        Matrix4x4 _view, _gpuProj;

        static readonly int ID_Waterline = Shader.PropertyToID("_WaterlineY");
        static readonly int ID_DisplaceScale = Shader.PropertyToID("_DisplaceScale");

        public WaterObstacle(Shader obstacleShader, int resolution, Vector3 volumeCenter,
                             Quaternion volumeRotation, Vector3 volumeExtent)
        {
            _resolution = resolution;
            _mat = new Material(obstacleShader);
            _cb = new CommandBuffer { name = "WebGLWater.Obstacle" };
            _mpb = new MaterialPropertyBlock();
            _prev = Create();
            _curr = Create();

            // Orthographic view looking DOWN the volume's up axis, so the submerged
            // footprint maps into the RT along the same axis the surface is displaced.
            // Extents (X half-width, Z half-length) set the ortho size; up = volume forward
            // so the RT's u<->pool x and v<->pool z (the sim's coordinate convention).
            float ex = Mathf.Max(volumeExtent.x, MinExtent);
            float ez = Mathf.Max(volumeExtent.z, MinExtent);
            float ey = Mathf.Max(volumeExtent.y, MinExtent);
            Vector3 up = volumeRotation * Vector3.up;
            Vector3 eye = volumeCenter + up * (EyeHeightInExtents * ey);
            Quaternion rot = Quaternion.LookRotation(-up, volumeRotation * Vector3.forward);
            Matrix4x4 camToWorld = Matrix4x4.TRS(eye, rot, Vector3.one);
            _view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * camToWorld.inverse;

            Matrix4x4 proj = Matrix4x4.Ortho(-ex, ex, -ez, ez, OrthoNearPlane, FarPlaneInExtents * ey + OrthoFarPad);
            _gpuProj = GL.GetGPUProjectionMatrix(proj, true); // renderIntoTexture = true
        }

        RenderTexture Create()
        {
            var rt = new RenderTexture(_resolution, _resolution, 0, RenderTextureFormat.RFloat)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "WaterObstacle"
            };
            rt.Create();
            return rt;
        }

        /// <summary>Render the current submerged footprint of all interactables.
        /// Ping-pongs so Prev holds last frame and Curr holds this frame.</summary>
        public void Render(float waterY)
        {
            (_prev, _curr) = (_curr, _prev);

            _cb.Clear();
            _cb.SetRenderTarget(_curr);
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetViewProjectionMatrices(_view, _gpuProj);

            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || it.Renderer == null) continue;

                float waterlineY = it.WaterlineY(waterY);
                if (!it.IsSubmerged(waterlineY)) continue;

                _mpb.SetFloat(ID_Waterline, waterlineY);
                _mpb.SetFloat(ID_DisplaceScale, it.displaceScale);
                it.Renderer.SetPropertyBlock(_mpb);
                _cb.DrawRenderer(it.Renderer, _mat, 0, 0);
            }

            Graphics.ExecuteCommandBuffer(_cb);
        }

        public void Dispose()
        {
            if (_prev != null) _prev.Release();
            if (_curr != null) _curr.Release();
            _cb?.Release();
        }
    }
}
