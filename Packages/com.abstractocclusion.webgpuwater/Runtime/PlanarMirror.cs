// WebGpuWater - reusable planar-mirror renderer (Unity 6 / URP).
//
// Renders the scene mirrored across a horizontal water plane (y = waterHeight) into an OWNED
// RenderTexture and exposes it. It publishes NO globals: the caller decides where the texture goes
// (a per-body MaterialPropertyBlock for WaterVolume, or the shared global for the standalone
// PlanarReflection component). Extracted from PlanarReflection so both paths share ONE proven
// mirror-render implementation instead of duplicating the matrix math.
//
// URP-only: the render path uses URP's single-camera render request, so the body compiles only when
// the Universal Render Pipeline is present (WEBGPUWATER_URP). Off URP the class is an inert stub so
// callers still compile.
using UnityEngine;
#if WEBGPUWATER_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Renders one mirrored view of the scene across a horizontal water plane into an owned RT.</summary>
    internal sealed class PlanarMirror
    {
        const int MinReflectionSize = 8;    // don't allocate a sub-8px reflection target
        const int ReflectionDepthBits = 24; // depth buffer for the mirrored scene render

        readonly string _rtName;

        internal PlanarMirror(string renderTextureName)
        {
            _rtName = string.IsNullOrEmpty(renderTextureName) ? "PlanarMirror" : renderTextureName;
        }

#if WEBGPUWATER_URP
        Camera _reflectionCamera;
        RenderTexture _rt;
        Vector2Int _rtSize;
        bool _rendering; // re-entrancy guard

        /// <summary>The most recently rendered mirror, or null before the first render.</summary>
        internal RenderTexture Texture => _rt;

        /// <summary>
        /// Render the scene mirrored across y = <paramref name="waterHeight"/> for <paramref name="src"/>.
        /// Safe to call every frame; the RT persists between calls so consumers can sample last frame's
        /// mirror while this frame's is in flight.
        /// </summary>
        internal void Render(Camera src, float waterHeight, float resolutionScale, float clipPlaneOffset, LayerMask reflectLayers)
        {
            if (src == null || _rendering) return;

            EnsureResources(src, resolutionScale, reflectLayers);

            Vector3 normal = Vector3.up;
            Vector3 pos = src.transform.position;
            Vector3 mirroredPos = pos;
            mirroredPos.y = 2f * waterHeight - pos.y;

            Matrix4x4 reflection = CalculateReflectionMatrix(new Vector4(normal.x, normal.y, normal.z, -waterHeight));
            _reflectionCamera.worldToCameraMatrix = src.worldToCameraMatrix * reflection;

            Vector4 clipPlane = CameraSpacePlane(_reflectionCamera, new Vector3(0f, waterHeight, 0f), normal, clipPlaneOffset);
            _reflectionCamera.projectionMatrix = src.CalculateObliqueMatrix(clipPlane);

            // CULL WITH THE NON-OBLIQUE PROJECTION. Unity's default culling frustum is
            // projectionMatrix * worldToCameraMatrix, and the oblique matrix above replaces the
            // near plane with the water plane - a severely skewed frustum that culls geometry
            // sitting ON that plane. A floating boat sits exactly there, so it was culled out of
            // its own reflection: the mirror held sky (never culled) and a BOAT-SHAPED HOLE.
            // Downstream that hole read as a dark smear under the hull which drifted with the
            // wave-nudged sample UV - the "reflection detaching from the boat". Overriding
            // cullingMatrix decouples what is CULLED from what is CLIPPED, which is exactly what
            // this property exists for; the oblique matrix still does the clipping, so submerged
            // geometry stays out of the mirror.
            // Must come after worldToCameraMatrix above, and after CopyFrom (which resets it).
            _reflectionCamera.cullingMatrix = src.projectionMatrix * _reflectionCamera.worldToCameraMatrix;

            _reflectionCamera.transform.position = mirroredPos;

            // Reflections invert winding order. try/finally: if the render request throws (e.g. device
            // loss on the experimental WebGPU editor backend), leaked state would otherwise render the
            // whole scene inside-out and permanently disable this mirror via the stuck re-entrancy guard.
            GL.invertCulling = true;
            _rendering = true;
            try
            {
                RenderPipeline.SubmitRenderRequest(
                    _reflectionCamera,
                    new UniversalRenderPipeline.SingleCameraRequest { destination = _rt });
            }
            finally
            {
                _rendering = false;
                GL.invertCulling = false;
            }
        }

        internal void Dispose()
        {
            if (_reflectionCamera != null)
            {
                WaterObjects.DestroyRuntime(_reflectionCamera.gameObject);
                _reflectionCamera = null;
            }
            ReleaseAndDestroy(ref _rt);
            _rtSize = Vector2Int.zero;
        }

        void EnsureResources(Camera src, float resolutionScale, LayerMask reflectLayers)
        {
            int width = Mathf.Max(MinReflectionSize, Mathf.RoundToInt(src.pixelWidth * resolutionScale));
            int height = Mathf.Max(MinReflectionSize, Mathf.RoundToInt(src.pixelHeight * resolutionScale));
            if (_rt == null || _rtSize.x != width || _rtSize.y != height)
            {
                ReleaseAndDestroy(ref _rt); // a resolution change must not leak the old wrapper
                _rt = new RenderTexture(width, height, ReflectionDepthBits, RenderTextureFormat.DefaultHDR)
                {
                    name = _rtName,
                    // MIRROR, not Clamp. The water shader nudges its sample by the surface normal,
                    // so near the screen border that sample legitimately lands outside the RT.
                    // Clamp answered with the border ROW repeated, which smeared a band along the
                    // edge; shader-side attempts to avoid it were worse (fading to the sky drew a
                    // seam, scaling the nudge to fit collapsed the wobble into a visible strip at
                    // each side). Mirror wrap answers with the neighbouring pixels reflected back
                    // in - continuous across the border, plausible content, and it costs nothing.
                    wrapMode = TextureWrapMode.Mirror,
                    // Trilinear + a mip chain: the water surface samples this mirror at a
                    // roughness-driven mip (tex2Dlod in SamplePlanarReflection), so rough/far
                    // water blurs its planar reflection exactly like the sky path - without
                    // this the planar mirror stayed razor sharp and the roughness knobs had
                    // no visible effect on planar bodies. Mips regenerate after each mirror
                    // render; if a backend can't (some WebGPU cases), the lod clamps to the
                    // sharp top mip, which is the old look.
                    filterMode = FilterMode.Trilinear,
                    useMipMap = true,
                    autoGenerateMips = true,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _rt.Create();
                _rtSize = new Vector2Int(width, height);
            }

            if (_reflectionCamera == null)
            {
                var go = new GameObject(_rtName + "Camera") { hideFlags = HideFlags.HideAndDontSave };
                _reflectionCamera = go.AddComponent<Camera>();
                _reflectionCamera.enabled = false; // driven manually
            }

            // Copy the important settings each frame so editor tweaks track live.
            _reflectionCamera.CopyFrom(src);
            // AFTER CopyFrom, which overwrites it with the SOURCE camera's type (Game). Without
            // this the mirror camera was indistinguishable from the player's camera, so every
            // fullscreen water pass ran on it and painted the water's own fog and god rays INTO
            // the mirror - a black boat reflected back teal, carrying a wave pattern evaluated at
            // the mirrored position. WaterPassCameraGate.SkipCameraFullscreen keys on this.
            _reflectionCamera.cameraType = CameraType.Reflection;
            _reflectionCamera.targetTexture = _rt;
            _reflectionCamera.cullingMask = reflectLayers & src.cullingMask;
            _reflectionCamera.enabled = false;
        }

        // Release frees the GPU surface; Destroy frees the wrapper object, which otherwise accumulates
        // across disable cycles and resolution changes until scene unload.
        static void ReleaseAndDestroy(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            WaterObjects.DestroyRuntime(rt);
            rt = null;
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
#else
        /// <summary>Off URP there is no mirror; consumers fall back to SSR / sky.</summary>
        internal RenderTexture Texture => null;
        internal void Render(Camera src, float waterHeight, float resolutionScale, float clipPlaneOffset, LayerMask reflectLayers) { }
        internal void Dispose() { }
#endif
    }
}
