// WebGpuWater - per-body caustics render pass.
// Extracted from WaterVolume: owns the caustic material, render target and command
// buffer, and renders the body's own sim into its own caustic RT - so caustics never
// come from whatever body last wrote the _WaterTex global. The RT reaches the body's
// renderers via the property block; the primary also mirrors it to the _CausticTex
// global for objects without a WaterMembership.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterCausticsPass
    {
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_SimCenter = Shader.PropertyToID("_SimCenter");
        static readonly int ID_SimExtent = Shader.PropertyToID("_SimExtent");

        readonly Material _material;
        readonly Material _largeBodyMaterial; // null when the large-body caustics shader isn't assigned (oceans only)
        readonly RenderTexture _target;
        readonly CommandBuffer _cb;

        internal RenderTexture Texture => _target;

        internal WaterCausticsPass(Shader causticsShader, Shader largeBodyCausticsShader, int resolution)
        {
            if (causticsShader == null) throw new System.ArgumentNullException(nameof(causticsShader));
            if (resolution <= 0)
                throw new System.ArgumentException($"Caustic resolution must be positive, got {resolution}.",
                                                   nameof(resolution));

            // HideAndDontSave: an edit-mode preview must never serialize these into the scene.
            _material = new Material(causticsShader) { hideFlags = HideFlags.HideAndDontSave };
            // Optional: only the windowed ocean uses it, so a project without the shader assigned simply
            // gets no large-body caustics (the shafts still read as plain shadow shafts).
            if (largeBodyCausticsShader != null)
                _largeBodyMaterial = new Material(largeBodyCausticsShader) { hideFlags = HideFlags.HideAndDontSave };
            _target = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex",
                hideFlags = HideFlags.HideAndDontSave
            };
            _target.Create();
            _cb = new CommandBuffer { name = "WebGLWater.Caustics" };
        }

        // Project the body's own sim state into its caustic RT (vertex shader outputs
        // clip space directly, so the mesh draws with an identity matrix).
        internal void Render(Mesh waterMesh, RenderTexture simTexture)
        {
            if (simTexture != null) _material.SetTexture(ID_Water, simTexture);

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _material, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        // Ocean version: project the near-field WINDOW sim into the caustic RT via the large-body
        // (world-frame) caustic. The window centre/extent are set on the material explicitly so the
        // projection frame is correct even on the first frame, before the body publishes those globals.
        // No-op when the large-body shader isn't assigned, so oceans just fall back to plain shafts.
        internal void RenderLargeBody(Mesh windowMesh, RenderTexture simTexture,
                                      Vector3 windowCenter, Vector3 windowHalfExtent)
        {
            if (_largeBodyMaterial == null || windowMesh == null) return;
            if (simTexture != null) _largeBodyMaterial.SetTexture(ID_Water, simTexture);
            _largeBodyMaterial.SetVector(ID_SimCenter, windowCenter);
            _largeBodyMaterial.SetVector(ID_SimExtent, windowHalfExtent);

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(windowMesh, Matrix4x4.identity, _largeBodyMaterial, 0, 0);
            Graphics.ExecuteCommandBuffer(_cb);
        }

        internal void Dispose()
        {
            _cb?.Release();
            // Release frees the GPU surface immediately; Destroy frees the wrapper objects,
            // which otherwise accumulate across enable/disable cycles until scene unload.
            if (_target != null)
            {
                _target.Release();
                DestroyRuntimeObject(_target);
            }
            DestroyRuntimeObject(_material);
            DestroyRuntimeObject(_largeBodyMaterial);
        }

        static void DestroyRuntimeObject(Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Object.Destroy(obj); else Object.DestroyImmediate(obj);
        }
    }
}
