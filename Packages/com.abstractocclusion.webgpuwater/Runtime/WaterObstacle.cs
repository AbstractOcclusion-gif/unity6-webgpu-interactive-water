// WebGL Water - obstacle footprint renderer (Unity 6 / URP port)
// Draws every WaterInteractable top-down into a ping-pong pair of RenderTextures
// (R = submerged amount per column). The compute sim reads (prev - curr) to push
// the surface, generalising the original analytic sphere displacement to any mesh.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterObstacle
    {
        public RenderTexture Prev => _prev;
        public RenderTexture Curr => _curr;
        // Submerged footprint of REFLECTOR-flagged objects only (passive reflection solid mask).
        public RenderTexture Solid => _solid;

#if UNITY_EDITOR
        // Editor-only taps for the footprint inspector: the direct draw target (before any blit) and
        // the box-filtered footprint (before temporal smoothing), to isolate a draw/projection fault
        // from a downsample/smooth one. Not part of the runtime API.
        internal RenderTexture DebugHiRes => _hiRes;
        internal RenderTexture DebugRaw => _raw;
        internal RenderTexture DebugSolid => _solid;
#endif

        const float MinExtent = 1e-4f;        // floor so a zero extent can't collapse the frustum
        const float EyeHeightInExtents = 2f;  // ortho camera sits this many depth-extents above the surface
        const float FarPlaneInExtents = 4f;   // far plane spans this many depth-extents below the eye
        const float OrthoNearPlane = 0.01f;
        const float OrthoFarPad = 0.02f;       // keep the floor just inside the far plane

        // The footprint is rasterized at this multiple of the sim resolution, then
        // box-filtered down (two successive 2x bilinear blits = exact 4x4 box). At 1x, a
        // silhouette edge sweeping a sim texel flipped that column's full submerged
        // thickness on/off between frames - deltas the size of a mouse drop, stamped as
        // micro-ripples whenever an object drifted or slowly ROTATED. Supersampled, the
        // same crossing becomes a smooth 1/16-step coverage ramp.
        const int SupersampleFactor = 4;

        // Footprint pass in ObstacleDepth.shader. (Pass 1, the temporal-EMA blit, is disabled -
        // it doesn't run on this URP/WebGPU backend; see Render's plain-copy comment.)
        const int PassFootprint = 0;

        readonly Material _mat;
        readonly CommandBuffer _cb;
        readonly MaterialPropertyBlock _mpb;
        readonly int _resolution;
        RenderTexture _prev, _curr;    // consecutive-frame footprints (what the sim diffs)
        RenderTexture _hiRes, _midRes; // supersample chain: hi (4x) -> mid (2x) -> raw (1x)
        RenderTexture _raw;            // this frame's box-filtered footprint
        RenderTexture _solid;          // footprint of reflector-flagged objects only (reflection mask)
        Matrix4x4 _view, _gpuProj;

        static readonly int ID_Waterline = Shader.PropertyToID("_WaterlineY");
        static readonly int ID_DisplaceScale = Shader.PropertyToID("_DisplaceScale");

        public WaterObstacle(Shader obstacleShader, int resolution, Vector3 volumeCenter,
                             Quaternion volumeRotation, Vector3 volumeExtent)
        {
            if (obstacleShader == null) throw new System.ArgumentNullException(nameof(obstacleShader));
            if (resolution <= 0)
                throw new System.ArgumentException($"WaterObstacle resolution must be positive, got {resolution}.",
                                                   nameof(resolution));

            _resolution = resolution;
            _mat = new Material(obstacleShader) { hideFlags = HideFlags.HideAndDontSave };
            _cb = new CommandBuffer { name = "WebGLWater.Obstacle" };
            _mpb = new MaterialPropertyBlock();
            _prev = Create(_resolution);
            _curr = Create(_resolution);
            _raw = Create(_resolution);
            _solid = Create(_resolution);
            _hiRes = Create(_resolution * SupersampleFactor);
            _midRes = Create(_resolution * SupersampleFactor / 2);

            SetFrame(volumeCenter, volumeRotation, volumeExtent);
        }

        /// <summary>Rebuild the top-down orthographic view/projection for a frame given its
        /// centre, rotation and half-extent. Whole-body bodies set this once (the volume
        /// frame); a windowed large body calls it each frame so the footprint tracks the
        /// scrolling sim window.</summary>
        public void SetFrame(Vector3 center, Quaternion rotation, Vector3 extent)
        {
            // Orthographic view looking DOWN the frame's up axis, so the submerged
            // footprint maps into the RT along the same axis the surface is displaced.
            // Extents (X half-width, Z half-length) set the ortho size; up = frame forward
            // so the RT's u<->x and v<->z (the sim's coordinate convention).
            float ex = Mathf.Max(extent.x, MinExtent);
            float ez = Mathf.Max(extent.z, MinExtent);
            float ey = Mathf.Max(extent.y, MinExtent);
            Vector3 up = rotation * Vector3.up;
            Vector3 eye = center + up * (EyeHeightInExtents * ey);
            Quaternion rot = Quaternion.LookRotation(-up, rotation * Vector3.forward);
            Matrix4x4 camToWorld = Matrix4x4.TRS(eye, rot, Vector3.one);
            _view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * camToWorld.inverse;

            Matrix4x4 proj = Matrix4x4.Ortho(-ex, ex, -ez, ez, OrthoNearPlane, FarPlaneInExtents * ey + OrthoFarPad);
            _gpuProj = GL.GetGPUProjectionMatrix(proj, true); // renderIntoTexture = true
        }

        RenderTexture Create(int resolution)
        {
            // RHalf, not RFloat: the footprint pass blends additively (Blend One One) and base
            // WebGPU cannot blend into float32 targets (that needs the optional
            // 'float32-blendable' feature, absent on many mobile GPUs). Half precision is
            // ample for a submerged-depth coverage mask.
            var rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RHalf)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "WaterObstacle",
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            return rt;
        }

        /// <summary>Render the current submerged footprint of all interactables at the
        /// supersampled resolution, box-filter it down to the sim grid, and ping-pong so Prev
        /// holds last frame's footprint and Curr holds this frame's (the sim diffs the two).
        /// <paramref name="temporalBlend"/> is currently unused: the temporal-EMA smoothing pass
        /// is disabled because its custom-material blit doesn't run on this URP/WebGPU backend
        /// (see the plain-copy note below). Kept in the signature for the planned compute-based
        /// EMA re-add.</summary>
        public void Render(float waterY, float temporalBlend)
        {
            (_prev, _curr) = (_curr, _prev);

            _cb.Clear();
            _cb.SetRenderTarget(_hiRes);
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetViewProjectionMatrices(_view, _gpuProj);

            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || it.Renderer == null) continue;

                float waterlineY = it.WaterlineY(waterY);
                if (!it.IsSubmerged(waterlineY)) continue;

                // Merge into the renderer's EXISTING block instead of replacing it: a bare
                // SetPropertyBlock would wipe WaterMembership's per-body water uniforms (and
                // any user-set per-instance values) from the floater every simulated frame.
                it.Renderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(ID_Waterline, waterlineY);
                _mpb.SetFloat(ID_DisplaceScale, it.displaceScale);
                it.Renderer.SetPropertyBlock(_mpb);
                _cb.DrawRenderer(it.Renderer, _mat, 0, PassFootprint);
            }

            // Two successive 2x bilinear downsamples = an exact 4x4 box filter: each sim
            // texel becomes the average of 16 rasterized samples, so silhouette motion
            // (drift AND slow rotation) changes coverage smoothly instead of popping.
            _cb.Blit(_hiRes, _midRes);
            _cb.Blit(_midRes, _raw);

            // Copy the fresh footprint into _curr with a PLAIN blit. The previous temporal-EMA
            // used a custom-material blit (ObstacleDepth Pass 1, a CG vert_img/tex2D fullscreen
            // pass); on this URP/WebGPU backend that pass never runs, so _curr stayed frozen at
            // its initial value and every footprint read back as a uniform constant - the real
            // "FootprintDelta works weird" bug. A plain Blit (the same call the downsamples use,
            // proven to work here) actually lands _raw in _curr. Trade-off: temporal anti-flicker
            // smoothing is disabled; re-add it as a COMPUTE kernel (the codebase's WebGPU-proven
            // path) if moving-object flicker returns. 'temporalBlend' is unused until then.
            _cb.Blit(_raw, _curr);

            Graphics.ExecuteCommandBuffer(_cb);
        }

        /// <summary>Rasterize the submerged footprint of ONLY the reflector-flagged interactables into
        /// the solid mask (sim resolution, no supersample - a reflecting wall tolerates a hard edge).
        /// The Update kernel thresholds this to decide which cells bounce ripples. Separate from the
        /// emission footprint so ordinary floaters never become walls.</summary>
        public void RenderSolid(float waterY)
        {
            _cb.Clear();
            _cb.SetRenderTarget(_solid);
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetViewProjectionMatrices(_view, _gpuProj);

            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || it.Renderer == null || !it.reflectsWaves) continue;

                float waterlineY = it.WaterlineY(waterY);
                if (!it.IsSubmerged(waterlineY)) continue;

                it.Renderer.GetPropertyBlock(_mpb);
                _mpb.SetFloat(ID_Waterline, waterlineY);
                _mpb.SetFloat(ID_DisplaceScale, it.displaceScale);
                it.Renderer.SetPropertyBlock(_mpb);
                _cb.DrawRenderer(it.Renderer, _mat, 0, PassFootprint);
            }

            Graphics.ExecuteCommandBuffer(_cb);
        }

        public void Dispose()
        {
            ReleaseAndDestroy(ref _prev);
            ReleaseAndDestroy(ref _curr);
            ReleaseAndDestroy(ref _raw);
            ReleaseAndDestroy(ref _solid);
            ReleaseAndDestroy(ref _hiRes);
            ReleaseAndDestroy(ref _midRes);
            _cb?.Release();
            DestroyRuntimeObject(_mat); // the footprint material leaked once per enable cycle
        }

        static void ReleaseAndDestroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            DestroyRuntimeObject(rt);
            rt = null;
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
