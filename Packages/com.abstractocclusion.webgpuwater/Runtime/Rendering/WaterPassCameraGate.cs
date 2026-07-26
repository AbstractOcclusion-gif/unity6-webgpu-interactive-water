// WebGpuWater - ONE definition of "should a water render pass run for this camera".
//
// WHY: none of the water features filtered by camera type, so the fullscreen fog, the caustic
// projection, the ocean god-ray march and both mesh-depth prepasses all recorded for cameras that
// have no business seeing water - most visibly Unity's material/prefab PREVIEW cameras, which render
// thumbnails in the Project and Inspector windows. That is not just wasted work: a preview draws the
// package's procedural shaders with none of the per-body buffers bound, which is the same class of
// bufferless-preview problem that produced the "_Particles SRV none provided" d3d12 error.
//
// SCOPE - deliberately narrow. Only Preview is skipped:
//   * SceneView must keep running: the fog, the waterline and the shell are exactly what you author
//     against in the scene view.
//   * Reflection (probe faces) is arguably also wrong to fog, but it is a LOOK change on any scene
//     that already baked probes with water passes active, and the depth prepasses feed wall shaders
//     that would then read a stale prepass target. Left running on purpose; revisit deliberately,
//     with a scene to compare against, rather than as part of a cleanup pass.
#if WEBGPUWATER_URP
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterPassCameraGate
    {
        /// <summary>True when a water pass should NOT be recorded for this camera.</summary>
        internal static bool SkipCamera(CameraType cameraType) => cameraType == CameraType.Preview;
    }
}
#endif
